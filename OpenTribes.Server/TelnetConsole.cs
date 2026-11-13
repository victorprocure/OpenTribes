namespace OpenTribes.Server;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using OpenTribes.Core;

/// <summary>The protocol state of one telnet client, without sockets, so it can be unit tested.</summary>
/// <remarks>Port of src/server/telnet_console.cpp TelnetSession.</remarks>
public sealed class TelnetSession
{
    /// <summary>The characters kept per line.</summary>
    public const int LineMax = 0x1ff;
    /// <summary>The number of password tries. The fourth wrong password closes the session.</summary>
    public const int MaxTries = 4;

    /// <summary>What a session needs from its host. Port of src/server/telnet_console.hpp TelnetSession::Host.</summary>
    public sealed class Host
    {
        /// <summary>Returns <c>$TelnetPassword</c>.</summary>
        public Func<string> Password { get; set; } = () => "";     // $TelnetPassword
        /// <summary>Returns <c>$Console::Prompt</c>.</summary>
        public Func<string> Prompt { get; set; } = () => "";       // $Console::Prompt
        /// <summary>Runs a console input line.</summary>
        public Action<string> Evaluate { get; set; } = _ => { };
        /// <summary>Sends bytes to this client.</summary>
        public Action<byte[]> Send { get; set; } = _ => { };
    }

    private const string kBanner = "SimEngine Telnet Provider\r\n\r\nEnter Password:";
    private const string Retry = "Nope... try agian.\r\nEnter Password:";
    private const string TooMany = "Too many tries... cya.";

    private string _line = "";
    private bool _authenticated;
    private int _tries;

    /// <summary>Gets the banner sent with its NUL when a client is accepted.</summary>
    /// <returns>The banner text.</returns>
    public static string Banner() => kBanner;

    /// <summary>Tells whether the client gave the right password.</summary>
    /// <returns>True once authenticated.</returns>
    public bool Authenticated() => _authenticated;

    // src/server/telnet_console.cpp sendText.
    private static void SendText(Host host, string s)
    {
        if (s.Length != 0)
            host.Send(ByteString.ToBytes(s));
    }

    private static string CStr(string s)
    {
        var nul = s.IndexOf('\0');
        return nul < 0 ? s : s[..nul];
    }

    /// <summary>Processes one received chunk: collects lines, checks the password and runs console lines.</summary>
    /// <param name="data">The received bytes.</param>
    /// <param name="host">The host services.</param>
    /// <returns>False when the client must be closed.</returns>
    /// <remarks>The echo collected for the chunk is sent before a line is evaluated and at the end of the chunk. After a wrong password it stays queued behind the retry text. A line is a C string and ends at its first NUL.</remarks>
    public bool Receive(ReadOnlySpan<byte> data, Host host)
    {
        // Echo collected for this chunk. It is sent before a line is evaluated and at the end
        // of the chunk; after a wrong password it stays queued behind the retry text.
        var echo = new System.Text.StringBuilder();
        for (var i = 0; i < data.Length; ++i)
        {
            var c = (char)data[i];
            if (c == '\r')
                continue;
            if (c == '\n')
            {
                echo.Append("\r\n");
                // The line is a C string: it ends at the first NUL.
                var line = CStr(_line);
                _line = "";
                if (_authenticated)
                {
                    SendText(host, echo.ToString());
                    echo.Clear();
                    host.Evaluate(line);
                    SendText(host, host.Prompt());
                }
                else
                {
                    ++_tries;
                    var password = host.Password();
                    if (line == CStr(password))
                    {
                        SendText(host, echo.ToString());
                        echo.Clear();
                        SendText(host, host.Prompt());
                        _authenticated = true;
                    }
                    else if (_tries < MaxTries)
                    {
                        SendText(host, Retry);
                    }
                    else
                    {
                        SendText(host, TooMany);
                        return false; // closed before the queued echo goes out
                    }
                }
            }
            else if (c == '\b')
            {
                if (_line.Length != 0)
                {
                    _line = _line[..^1];
                    if (_authenticated)
                        echo.Append("\b \b");
                }
            }
            else if (_line.Length < LineMax)
            {
                _line += c;
                if (_authenticated)
                    echo.Append(c);
            }
        }
        SendText(host, echo.ToString());
        return true;
    }
}

/// <summary>The remote console over TCP, the <c>SimEngine Telnet Provider</c>. Call <see cref="Poll"/> once per server frame.</summary>
/// <remarks>Port of src/server/telnet_console.cpp (docs/server-ops.md 3). The listener is at 00553b34, the per-frame service at 00553bf8 and the console output consumer at 00553ad4.</remarks>
public sealed class TelnetConsole : IDisposable
{
    /// <summary>What the console needs from its host. Port of src/server/telnet_console.hpp TelnetConsole::Host.</summary>
    public sealed class Host
    {
        /// <summary>Returns <c>$TelnetPort</c> as an integer. 0 and -1 mean no listener.</summary>
        public Func<int> Port { get; set; } = () => 0;
        /// <summary>Returns <c>$TelnetPassword</c>.</summary>
        public Func<string> Password { get; set; } = () => "";
        /// <summary>Returns <c>$Console::Prompt</c>.</summary>
        public Func<string> Prompt { get; set; } = () => "";
        /// <summary>Runs a console input line.</summary>
        public Action<string> Evaluate { get; set; } = _ => { };
        /// <summary>Prints a console line.</summary>
        public Action<string> Print { get; set; } = _ => { };
    }

    private sealed class Client
    {
        public Socket Fd { get; set; } = null!;
        public TelnetSession Session { get; } = new();
    }

    private readonly Host _host;
    private Socket? _listenFd;
    private int _port = -1;
    private readonly List<Client> _clients = [];

    /// <summary>Creates the console. No listener opens until <see cref="Poll"/> sees a port.</summary>
    /// <param name="host">The host services.</param>
    public TelnetConsole(Host host) => _host = host;

    /// <summary>Closes the clients and the listener.</summary>
    public void Dispose()
    {
        foreach (var c in _clients)
            c.Fd.Dispose();
        _clients.Clear();
        _listenFd?.Dispose();
        _listenFd = null;
    }

    /// <summary>Gets the number of connected clients.</summary>
    /// <returns>The count.</returns>
    public int ClientCount() => _clients.Count;
    /// <summary>Gets the port of the listener.</summary>
    /// <returns>The port, or -1 when none.</returns>
    public int ListeningPort() => _port;

    // src/server/telnet_console.cpp sendAll: best effort like the original, which ignores send results: what a full
    // socket buffer refuses is dropped.
    private static void SendAll(Socket fd, ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int n;
            try
            {
                n = fd.Send(data, SocketFlags.None, out var err);
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
        _listenFd?.Dispose();
        _listenFd = null;
        _port = port;
        if (port is (-1) or 0)
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
            _listenFd = s;
        }
        catch (Exception)
        {
            s.Dispose();
        }
    }

    /// <summary>Reopens the listener when the port changed, accepts one client and serves all clients.</summary>
    public void Poll()
    {
        var port = _host.Port();
        if (port != _port)
            ListenOn(port);

        if (_listenFd != null)
        {
            Socket? fd = null;
            try
            {
                if (_listenFd.Poll(0, SelectMode.SelectRead))
                    fd = _listenFd.Accept();
            }
            catch (Exception)
            {
                fd = null;
            }
            if (fd != null)
            {
                var ip = fd.RemoteEndPoint is IPEndPoint ep ? ep.Address.MapToIPv4().GetAddressBytes() : new byte[4];
                _host.Print("Telnet connection from " + ip[0].ToString(CultureInfo.InvariantCulture) + "." +
                            ip[1].ToString(CultureInfo.InvariantCulture) + "." + ip[2].ToString(CultureInfo.InvariantCulture) + "." +
                            ip[3].ToString(CultureInfo.InvariantCulture));
                fd.Blocking = false;
                var banner = ByteString.ToBytes(TelnetSession.Banner() + "\0"); // with the NUL
                SendAll(fd, banner);
                _clients.Add(new Client { Fd = fd });
            }
        }

        // A line evaluated here may print (consoleLine) or change the port; clients are only
        // added in this function, so indices stay valid.
        var buf = new byte[256];
        for (var i = 0; i < _clients.Count;)
        {
            var close = false;
            var n = 0;
            var err = SocketError.Success;
            try
            {
                n = _clients[i].Fd.Receive(buf, 0, buf.Length, SocketFlags.None, out err);
            }
            catch (Exception)
            {
                err = SocketError.SocketError;
            }
            if (err is SocketError.WouldBlock or SocketError.Interrupted)
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
                var fd = _clients[i].Fd;
                var host = new TelnetSession.Host
                {
                    Password = _host.Password,
                    Prompt = _host.Prompt,
                    Evaluate = _host.Evaluate,
                    Send = d => SendAll(fd, d),
                };
                close = !_clients[i].Session.Receive(buf.AsSpan(0, n), host);
            }
            if (close)
            {
                _clients[i].Fd.Dispose();
                _clients.RemoveAt(i);
            }
            else
            {
                ++i;
            }
        }
    }

    /// <summary>Sends a console output line to every authenticated client, with its NUL and then <c>\r\n</c>.</summary>
    /// <param name="line">The line.</param>
    public void ConsoleLine(string line)
    {
        foreach (var c in _clients)
            if (c.Session.Authenticated())
            {
                var nul = line.IndexOf('\0');
                var text = nul < 0 ? line : line[..nul];
                SendAll(c.Fd, ByteString.ToBytes(text + "\0"));
                SendAll(c.Fd, "\r\n"u8);
            }
    }
}
