// Port of src/server/telnet_console.hpp and src/server/telnet_console.cpp: remote console over TCP ("SimEngine Telnet
// Provider"): listener 0x00553b34, per-frame service 0x00553bf8, console output consumer 0x00553ad4. docs/server-ops.md 3.
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using OpenTribes.Core;

namespace OpenTribes.Server;

// src/server/telnet_console.hpp class TelnetSession: protocol state of one client, without sockets (unit-testable).
public sealed class TelnetSession
{
    public const int kLineMax = 0x1ff; // characters kept per line
    public const int kMaxTries = 4;    // the fourth wrong password closes

    // src/server/telnet_console.hpp TelnetSession::Host.
    public sealed class Host
    {
        public Func<string> Password = () => "";     // $TelnetPassword
        public Func<string> Prompt = () => "";       // $Console::Prompt
        public Action<string> Evaluate = _ => { };   // console input line
        public Action<byte[]> Send = _ => { };       // bytes to this client
    }

    private const string kBanner = "SimEngine Telnet Provider\r\n\r\nEnter Password:";
    private const string kRetry = "Nope... try agian.\r\nEnter Password:";
    private const string kTooMany = "Too many tries... cya.";

    private string mLine = "";
    private bool mAuthenticated;
    private int mTries;

    // src/server/telnet_console.cpp TelnetSession::banner: sent with its NUL on accept.
    public static string Banner() => kBanner;

    public bool Authenticated() => mAuthenticated;

    // src/server/telnet_console.cpp sendText.
    private static void SendText(Host host, string s)
    {
        if (s.Length != 0)
            host.Send(ByteString.ToBytes(s));
    }

    private static string CStr(string s)
    {
        int nul = s.IndexOf('\0');
        return nul < 0 ? s : s[..nul];
    }

    // src/server/telnet_console.cpp TelnetSession::receive: processes one received chunk. Returns false when the client
    // must be closed.
    public bool Receive(ReadOnlySpan<byte> data, Host host)
    {
        // Echo collected for this chunk. It is sent before a line is evaluated and at the end
        // of the chunk; after a wrong password it stays queued behind the retry text.
        var echo = new System.Text.StringBuilder();
        for (int i = 0; i < data.Length; ++i)
        {
            char c = (char)data[i];
            if (c == '\r')
                continue;
            if (c == '\n')
            {
                echo.Append("\r\n");
                // The line is a C string: it ends at the first NUL.
                string line = CStr(mLine);
                mLine = "";
                if (mAuthenticated)
                {
                    SendText(host, echo.ToString());
                    echo.Clear();
                    host.Evaluate(line);
                    SendText(host, host.Prompt());
                }
                else
                {
                    ++mTries;
                    string password = host.Password();
                    if (line == CStr(password))
                    {
                        SendText(host, echo.ToString());
                        echo.Clear();
                        SendText(host, host.Prompt());
                        mAuthenticated = true;
                    }
                    else if (mTries < kMaxTries)
                    {
                        SendText(host, kRetry);
                    }
                    else
                    {
                        SendText(host, kTooMany);
                        return false; // closed before the queued echo goes out
                    }
                }
            }
            else if (c == '\b')
            {
                if (mLine.Length != 0)
                {
                    mLine = mLine[..^1];
                    if (mAuthenticated)
                        echo.Append("\b \b");
                }
            }
            else if (mLine.Length < kLineMax)
            {
                mLine += c;
                if (mAuthenticated)
                    echo.Append(c);
            }
        }
        SendText(host, echo.ToString());
        return true;
    }
}

// src/server/telnet_console.hpp class TelnetConsole: listener and clients. Poll() is called once per server frame.
public sealed class TelnetConsole : IDisposable
{
    // src/server/telnet_console.hpp TelnetConsole::Host.
    public sealed class Host
    {
        public Func<int> Port = () => 0;           // $TelnetPort as int; 0 and -1 mean no listener
        public Func<string> Password = () => "";
        public Func<string> Prompt = () => "";
        public Action<string> Evaluate = _ => { };
        public Action<string> Print = _ => { };    // console printf
    }

    private sealed class Client
    {
        public Socket Fd = null!;
        public readonly TelnetSession Session = new();
    }

    private readonly Host mHost;
    private Socket? mListenFd;
    private int mPort = -1;
    private readonly List<Client> mClients = new();

    // src/server/telnet_console.cpp TelnetConsole::TelnetConsole.
    public TelnetConsole(Host host) => mHost = host;

    // src/server/telnet_console.cpp TelnetConsole::~TelnetConsole.
    public void Dispose()
    {
        foreach (Client c in mClients)
            c.Fd.Dispose();
        mClients.Clear();
        mListenFd?.Dispose();
        mListenFd = null;
    }

    public int ClientCount() => mClients.Count;
    public int ListeningPort() => mPort;

    // src/server/telnet_console.cpp sendAll: best effort like the original, which ignores send results: what a full
    // socket buffer refuses is dropped.
    private static void SendAll(Socket fd, ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int n;
            try
            {
                n = fd.Send(data, SocketFlags.None, out SocketError err);
                if (err != SocketError.Success)
                    return;
            }
            catch (Exception)
            {
                return;
            }
            if (n <= 0)
                return;
            data = data[n..];
        }
    }

    // src/server/telnet_console.cpp TelnetConsole::listenOn.
    private void ListenOn(int port)
    {
        if (mListenFd != null)
        {
            mListenFd.Dispose();
            mListenFd = null;
        }
        mPort = port;
        if (port == -1 || port == 0)
            return;
        Socket s;
        try
        {
            s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
        catch (Exception)
        {
            return;
        }
        // Like the original, a failed bind or listen is not reported or retried.
        try
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.Bind(new IPEndPoint(IPAddress.Any, (ushort)port));
            s.Listen(4);
            s.Blocking = false;
            mListenFd = s;
        }
        catch (Exception)
        {
            s.Dispose();
        }
    }

    // src/server/telnet_console.cpp TelnetConsole::poll: reopens the listener when the port changed, accepts one
    // client, serves all clients.
    public void Poll()
    {
        int port = mHost.Port();
        if (port != mPort)
            ListenOn(port);

        if (mListenFd != null)
        {
            Socket? fd = null;
            try
            {
                if (mListenFd.Poll(0, SelectMode.SelectRead))
                    fd = mListenFd.Accept();
            }
            catch (Exception)
            {
                fd = null;
            }
            if (fd != null)
            {
                byte[] ip = fd.RemoteEndPoint is IPEndPoint ep ? ep.Address.MapToIPv4().GetAddressBytes() : new byte[4];
                mHost.Print("Telnet connection from " + ip[0].ToString(CultureInfo.InvariantCulture) + "." +
                            ip[1].ToString(CultureInfo.InvariantCulture) + "." + ip[2].ToString(CultureInfo.InvariantCulture) + "." +
                            ip[3].ToString(CultureInfo.InvariantCulture));
                fd.Blocking = false;
                byte[] banner = ByteString.ToBytes(TelnetSession.Banner() + "\0"); // with the NUL
                SendAll(fd, banner);
                mClients.Add(new Client { Fd = fd });
            }
        }

        // A line evaluated here may print (consoleLine) or change the port; clients are only
        // added in this function, so indices stay valid.
        var buf = new byte[256];
        for (int i = 0; i < mClients.Count;)
        {
            bool close = false;
            int n = 0;
            SocketError err = SocketError.Success;
            try
            {
                n = mClients[i].Fd.Receive(buf, 0, buf.Length, SocketFlags.None, out err);
            }
            catch (Exception)
            {
                err = SocketError.SocketError;
            }
            if (err == SocketError.WouldBlock || err == SocketError.Interrupted)
            {
                close = false;
            }
            else if (err != SocketError.Success)
            {
                close = true;
            }
            else if (n == 0)
            {
                close = true;
            }
            else
            {
                Socket fd = mClients[i].Fd;
                var host = new TelnetSession.Host
                {
                    Password = mHost.Password,
                    Prompt = mHost.Prompt,
                    Evaluate = mHost.Evaluate,
                    Send = d => SendAll(fd, d),
                };
                close = !mClients[i].Session.Receive(buf.AsSpan(0, n), host);
            }
            if (close)
            {
                mClients[i].Fd.Dispose();
                mClients.RemoveAt(i);
            }
            else
            {
                ++i;
            }
        }
    }

    // src/server/telnet_console.cpp TelnetConsole::consoleLine: console output line, sent with its NUL, then "\r\n",
    // to every authenticated client.
    public void ConsoleLine(string line)
    {
        foreach (Client c in mClients)
            if (c.Session.Authenticated())
            {
                int nul = line.IndexOf('\0');
                string text = nul < 0 ? line : line[..nul];
                SendAll(c.Fd, ByteString.ToBytes(text + "\0"));
                SendAll(c.Fd, "\r\n"u8);
            }
    }
}
