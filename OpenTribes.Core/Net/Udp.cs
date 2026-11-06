// Port of src/net/udp.hpp and src/net/udp.cpp: non-blocking UDP socket.
using System.Net;
using System.Net.Sockets;

namespace OpenTribes.Net;

public sealed class UdpSocket : IDisposable
{
    private Socket? mFd;
    private ushort mPort;
    // recvfrom hands the caller the first `capacity` bytes of a larger datagram and drops the rest; .NET reports
    // that as an error, so the datagram is taken whole here and cut to the caller's buffer.
    private readonly byte[] mScratch = new byte[0x10000];

    // src/net/udp.cpp UdpSocket::~UdpSocket.
    public void Dispose() => Close();

    // src/net/udp.cpp UdpSocket::close.
    public void Close()
    {
        mFd?.Dispose();
        mFd = null;
    }

    private static IPAddress ToIp(uint ip) =>
        new(new[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip });

    private static uint FromIp(IPAddress a)
    {
        byte[] b = a.MapToIPv4().GetAddressBytes();
        return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
    }

    // src/net/udp.cpp UdpSocket::open.
    // loopbackOnly: the in-process transport of a hosted single-player game (LOOPBACK, 0058d7d4) is a datagram socket that
    // only this machine can reach.
    public bool Open(ushort port, bool loopbackOnly = false)
    {
        Close();
        try
        {
            mFd = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        }
        catch (SocketException)
        {
            return false;
        }
        try
        {
            mFd.ReceiveBufferSize = 0x8000;
            mFd.SendBufferSize = 0x8000;
            mFd.EnableBroadcast = true;
        }
        catch (SocketException)
        {
            // setsockopt results are ignored by the C++.
        }
        try
        {
            mFd.Bind(new IPEndPoint(loopbackOnly ? IPAddress.Loopback : IPAddress.Any, port));
            mFd.Blocking = false;
        }
        catch (SocketException)
        {
            Close();
            return false;
        }
        mPort = (ushort)((IPEndPoint)mFd.LocalEndPoint!).Port;
        return true;
    }

    // src/net/udp.cpp UdpSocket::send.
    public bool Send(Address to, ReadOnlySpan<byte> data)
    {
        if (mFd == null)
            return false;
        try
        {
            return mFd.SendTo(data, SocketFlags.None, new IPEndPoint(ToIp(to.Ip), to.Port)) == data.Length;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // src/net/udp.cpp UdpSocket::receive: bytes received, 0 when nothing is pending, -1 on error.
    public int Receive(out Address from, Span<byte> buffer)
    {
        from = default;
        if (mFd == null)
            return -1;
        EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
        int n;
        try
        {
            // Avoids a WouldBlock exception per empty poll.
            if (!mFd.Poll(0, SelectMode.SelectRead))
                return 0;
            n = mFd.ReceiveFrom(mScratch, SocketFlags.None, ref ep);
        }
        catch (SocketException e)
        {
            return e.SocketErrorCode == SocketError.WouldBlock ? 0 : -1;
        }
        var ipep = (IPEndPoint)ep;
        from = new Address(FromIp(ipep.Address), (ushort)ipep.Port);
        n = Math.Min(n, buffer.Length);
        mScratch.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    // Waits up to `ms` for a datagram; false on timeout or when the socket is closed.
    public bool WaitRead(int ms)
    {
        Socket? fd = mFd;
        try
        {
            return fd != null && fd.Poll(ms * 1000, SelectMode.SelectRead);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    // src/net/udp.hpp port.
    public ushort Port() => mPort;
}
