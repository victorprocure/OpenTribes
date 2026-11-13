namespace OpenTribes.Net;

using System.Net;
using System.Net.Sockets;

/// <summary>The in-process datagram network of the LOOPBACK transport. A memory socket owns a port on 127.0.0.1 and delivers to the other memory sockets of the process only.</summary>
/// <remarks>Follows the LOOPBACK transport (LOOPTransport 0058d744). Its connect (0058d7d4) pairs two virtual connections that hand each other packets by memory copy, with no socket.</remarks>
internal static class MemoryNet
{
    private static readonly Lock sLock = new();
    private static readonly Dictionary<ushort, Queue<(Address From, byte[] Data)>> sQueues = [];
    private static ushort sNext = 40000;

    public static bool Has(ushort port)
    {
        lock (sLock)
            return sQueues.ContainsKey(port);
    }

    /// <summary>Claims a port. Port 0 picks a free one.</summary>
    public static bool Register(ref ushort port)
    {
        lock (sLock)
        {
            if (port == 0)
            {
                for (var i = 0; i < 0x10000 && port == 0; ++i)
                {
                    var c = sNext == 0xffff ? (ushort)40000 : ++sNext;
                    if (!sQueues.ContainsKey(c))
                        port = c;
                }
                if (port == 0)
                    return false;
            }
            else if (sQueues.ContainsKey(port))
                return false;
            sQueues[port] = new Queue<(Address, byte[])>();
            return true;
        }
    }

    public static void Unregister(ushort port)
    {
        lock (sLock)
            sQueues.Remove(port);
    }

    public static bool Deliver(Address from, ushort to, ReadOnlySpan<byte> data)
    {
        lock (sLock)
        {
            if (!sQueues.TryGetValue(to, out var q))
                return false;
            q.Enqueue((from, data.ToArray()));
            return true;
        }
    }

    public static bool HasPending(ushort port)
    {
        lock (sLock)
            return sQueues.TryGetValue(port, out var q) && q.Count > 0;
    }

    public static bool TryTake(ushort port, out Address from, out byte[] data)
    {
        lock (sLock)
        {
            if (sQueues.TryGetValue(port, out var q) && q.Count > 0)
            {
                (from, data) = q.Dequeue();
                return true;
            }
        }
        from = default;
        data = [];
        return false;
    }
}

/// <summary>A non-blocking UDP socket, or an in-process memory socket for the LOOPBACK transport.</summary>
/// <remarks>Port of src/net/udp.cpp.</remarks>
public sealed class UdpSocket : IDisposable
{
    private const uint LoopbackIp = 0x7f000001;
    private Socket? mFd;
    private bool mMemory;
    private ushort mPort;
    // recvfrom hands the caller the first `capacity` bytes of a larger datagram and drops the rest; .NET reports
    // that as an error, so the datagram is taken whole here and cut to the caller's buffer.
    private readonly byte[] mScratch = new byte[0x10000];

    /// <summary>Closes the socket.</summary>
    public void Dispose() => Close();

    /// <summary>Closes the socket and releases a memory port.</summary>
    public void Close()
    {
        mFd?.Dispose();
        mFd = null;
        if (mMemory)
            MemoryNet.Unregister(mPort);
        mMemory = false;
    }

    /// <summary>Opens a memory socket that only other sockets of this process reach (LOOPTransport 0058d744).</summary>
    /// <param name="port">The port, or 0 to pick a free one.</param>
    /// <returns>False when the port is taken.</returns>
    public bool OpenMemory(ushort port)
    {
        Close();
        if (!MemoryNet.Register(ref port))
            return false;
        mMemory = true;
        mPort = port;
        return true;
    }

    /// <summary>Opens the socket of a client that joins <paramref name="target"/>: a memory socket when the target is a LOOPBACK server of this process, else an ordinary socket on any port.</summary>
    /// <param name="target">The server address.</param>
    /// <returns>True on success.</returns>
    public bool OpenToward(Address target) =>
        target.Ip == LoopbackIp && MemoryNet.Has(target.Port) ? OpenMemory(0) : Open(0);

    private static IPAddress ToIp(uint ip) =>
        new([(byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip]);

    private static uint FromIp(IPAddress a)
    {
        var b = a.MapToIPv4().GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    /// <summary>Opens a non-blocking UDP socket with broadcast enabled.</summary>
    /// <param name="port">The port to bind, or 0 for any.</param>
    /// <param name="loopbackOnly">True to bind 127.0.0.1 only.</param>
    /// <returns>True on success.</returns>
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

    /// <summary>Sends one datagram.</summary>
    /// <param name="to">The destination.</param>
    /// <param name="data">The payload.</param>
    /// <returns>True when the whole datagram was sent.</returns>
    public bool Send(Address to, ReadOnlySpan<byte> data)
    {
        if (mMemory)
            return to.Ip == LoopbackIp && MemoryNet.Deliver(new Address(LoopbackIp, mPort), to.Port, data);
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

    /// <summary>Receives one datagram if one is pending. A datagram larger than <paramref name="buffer"/> is cut to it.</summary>
    /// <param name="from">Receives the sender.</param>
    /// <param name="buffer">Receives the payload.</param>
    /// <returns>The bytes received, 0 when nothing is pending, or -1 on error.</returns>
    public int Receive(out Address from, Span<byte> buffer)
    {
        from = default;
        if (mMemory)
        {
            if (!MemoryNet.TryTake(mPort, out from, out var packet))
                return 0;
            var len = Math.Min(packet.Length, buffer.Length);
            packet.AsSpan(0, len).CopyTo(buffer);
            return len;
        }
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

    /// <summary>Waits for a datagram.</summary>
    /// <param name="ms">The longest wait in milliseconds.</param>
    /// <returns>False on timeout or when the socket is closed.</returns>
    public bool WaitRead(int ms)
    {
        if (mMemory)
        {
            for (var waited = 0; waited <= ms; ++waited)
            {
                if (MemoryNet.HasPending(mPort))
                    return true;
                if (waited < ms)
                    Thread.Sleep(1);
            }
            return false;
        }
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

    /// <summary>Gets the bound port.</summary>
    /// <returns>The port.</returns>
    public ushort Port() => mPort;
}
