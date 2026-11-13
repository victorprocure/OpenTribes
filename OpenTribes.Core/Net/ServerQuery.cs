namespace OpenTribes.Net;

using System.Globalization;

/// <summary>The query pacing preferences, <c>$pref::maxConcurrentPings</c> to <c>requestRetryCount</c>.</summary>
/// <remarks>Follows SimNet_loadQueryPrefs (0054e61c). The defaults are the ones the exe passes.</remarks>
public sealed class ServerQueryPrefs
{
    /// <summary>The most pings in flight at once.</summary>
    public int MaxPings = 20;
    /// <summary>The time in milliseconds before a ping is sent again.</summary>
    public int PingTimeoutMs = 1500;
    /// <summary>The number of resends before a ping fails.</summary>
    public int PingRetries = 2;
    /// <summary>The most info requests in flight at once.</summary>
    public int MaxRequests = 10;
    /// <summary>The time in milliseconds before an info request is sent again.</summary>
    public int RequestTimeoutMs = 2000;
    /// <summary>The number of resends before an info request fails.</summary>
    public int RequestRetries = 2;
}

/// <summary>The record of a type 8 info reply.</summary>
/// <remarks>The info handler (0042c4cc) fills the 0x458 byte server record in this order.</remarks>
public sealed class ServerInfoRecord
{
    /// <summary>The key of the request this reply answers.</summary>
    public ushort Key;
    /// <summary>The canonical address text of the server.</summary>
    public string Address = "";
    /// <summary>The game name, which the caller checks.</summary>
    public string GameName = "";
    /// <summary>The game version text.</summary>
    public string Version = "";
    /// <summary>The server's host name.</summary>
    public string Hostname = "";
    /// <summary>The current mission name.</summary>
    public string Mission = "";
    /// <summary>The loaded mods.</summary>
    public string ModList = "";
    /// <summary>The mission type.</summary>
    public string MissionType = "";
    /// <summary>The server description text.</summary>
    public string Info = "";
    /// <summary>The heading of the team columns in <see cref="Rows"/>.</summary>
    public string TeamHeading = "";
    /// <summary>The heading of the client columns in <see cref="Rows"/>.</summary>
    public string ClientHeading = "";
    /// <summary>The current player count.</summary>
    public byte Players;
    /// <summary>The player limit.</summary>
    public byte MaxPlayers;
    /// <summary>True for a dedicated server.</summary>
    public bool Dedicated;
    /// <summary>True when the server needs a password.</summary>
    public bool Password;
    /// <summary>The server's CPU speed.</summary>
    public int Cpu;
    /// <summary>The packet version the server reports.</summary>
    public ushort PacketVersion;
    /// <summary>The status rows that follow the fixed part of the reply.</summary>
    public readonly List<string> Rows = [];
}

/// <summary>A parsed type 4 ping reply.</summary>
/// <param name="Key">The key of the ping it answers.</param>
/// <param name="Players">The current player count.</param>
/// <param name="PacketVersion">The packet version the server reports.</param>
/// <param name="Hostname">The server's host name.</param>
public readonly record struct PingReply(ushort Key, byte Players, ushort PacketVersion, string Hostname);

/// <summary>Builds and parses the query packets of the DNet info protocol.</summary>
public static class QueryPackets
{
    /// <summary>Builds a ping request: <c>10 03 b2 00 key(le) 00 00</c>.</summary>
    /// <param name="key">The request key.</param>
    /// <param name="index">The query index, sent as <c>b2</c>. 0 is sent as 0xff.</param>
    /// <returns>The packet.</returns>
    /// <remarks>Follows SimNet_processQueryQueue (0054ee8c), request case 3.</remarks>
    public static byte[] BuildPing(ushort key, byte index) => [0x10, 3, index == 0 ? (byte)0xff : index, 0, (byte)key, (byte)(key >> 8), 0, 0];

    /// <summary>Builds an info request: <c>10 07 ff 00 key 00 00</c>.</summary>
    /// <param name="key">The request key.</param>
    /// <returns>The packet.</returns>
    /// <remarks>Follows the request loop of SimNet_processQueryQueue (0054ee8c).</remarks>
    public static byte[] BuildInfoRequest(ushort key) => [0x10, 7, 0xff, 0, (byte)key, (byte)(key >> 8), 0, 0];

    /// <summary>Parses a type 4 ping reply.</summary>
    /// <param name="d">The datagram.</param>
    /// <param name="reply">Receives the reply.</param>
    /// <returns>False when the datagram is not a valid ping reply.</returns>
    /// <remarks>Follows SimNet_onInfoPacket (0054f608) case 4. The packet is 40 bytes. Byte 3 is 0xf0, byte 7 the player count, bytes 8-9 the packet version and bytes 10-38 the host name.</remarks>
    public static bool TryParsePingReply(ReadOnlySpan<byte> d, out PingReply reply)
    {
        reply = default;
        if (d.Length < 40 || d[0] != 0x10 || d[1] != 4 || d[3] != 0xf0)
            return false;
        var end = 10;
        while (end < 39 && d[end] != 0)
            ++end;
        reply = new PingReply((ushort)(d[4] | (d[5] << 8)), d[7], (ushort)(d[8] | (d[9] << 8)), System.Text.Encoding.Latin1.GetString(d[10..end]));
        return true;
    }

    /// <summary>Parses a type 8 info reply. The packet version and game name checks are the caller's.</summary>
    /// <param name="d">The datagram.</param>
    /// <param name="rec">Receives the record.</param>
    /// <returns>False when the stream is too short for the fixed part.</returns>
    /// <remarks>Follows the info handler (0042c4cc). After the 8 byte header comes a bit stream: packet version (2 raw bytes), game name, version, host name, players, maximum, mission, dedicated, password, CPU speed (4 raw bytes), mod list, mission type, info, team heading, client heading, then the rows. The row loop runs while bytes remain, as the handler compares the stream position with the packet length.</remarks>
    public static bool TryParseInfoReply(ReadOnlySpan<byte> d, out ServerInfoRecord rec)
    {
        rec = new ServerInfoRecord();
        if (d.Length < 8 + 2 || d[0] != 0x10 || d[1] != 8)
            return false;
        rec.Key = (ushort)(d[4] | (d[5] << 8));
        var body = d[8..].ToArray();
        var bs = new BitStream(body, body.Length);
        Span<byte> two = stackalloc byte[2];
        bs.ReadBytes(two);
        rec.PacketVersion = (ushort)(two[0] | (two[1] << 8));
        rec.GameName = bs.ReadString();
        rec.Version = bs.ReadString();
        rec.Hostname = bs.ReadString();
        Span<byte> one = stackalloc byte[1];
        bs.ReadBytes(one);
        rec.Players = one[0];
        bs.ReadBytes(one);
        rec.MaxPlayers = one[0];
        rec.Mission = bs.ReadString();
        bs.ReadBytes(one);
        rec.Dedicated = one[0] != 0;
        bs.ReadBytes(one);
        rec.Password = one[0] != 0;
        Span<byte> four = stackalloc byte[4];
        bs.ReadBytes(four);
        rec.Cpu = four[0] | (four[1] << 8) | (four[2] << 16) | (four[3] << 24);
        rec.ModList = bs.ReadString();
        rec.MissionType = bs.ReadString();
        rec.Info = bs.ReadString();
        rec.TeamHeading = bs.ReadString();
        rec.ClientHeading = bs.ReadString();
        // The rows: the loop of 0042c4cc runs while bytes remain (it compares the stream position with the packet length).
        while (!bs.Error() && bs.BytePosition() < body.Length)
        {
            var row = bs.ReadString();
            if (bs.Error())
                break;
            rec.Rows.Add(row);
        }
        return !bs.Error();
    }
}

/// <summary>The UDP transport of the query: one socket on a port the OS picks, with a receive thread that stamps each datagram.</summary>
/// <remarks>The client's own socket (0058bfac) binds port 0.</remarks>
public sealed class UdpQueryTransport : IQueryTransport, IDisposable
{
    private readonly UdpSocket mSock = new();
    private readonly Func<long> mNow;
    private readonly Queue<(Address From, byte[] Data, long AtMs)> mQueue = new();
    private Thread? mThread;
    private volatile bool mStop;
    private bool mOpen;

    /// <summary>Creates the transport.</summary>
    /// <param name="now">The query clock. The receive thread stamps each datagram with it on arrival. Defaults to the system tick count.</param>
    /// <remarks>The stock exe takes the time of a ping reply when the packet comes in (0054f608 case 4), not when the screen next looks.</remarks>
    public UdpQueryTransport(Func<long>? now = null) => mNow = now ?? (() => Environment.TickCount64);

    /// <summary>The arrival time of the datagram <see cref="Receive"/> gave last, or -1 before any.</summary>
    public long LastReceiveMs { get; private set; } = -1;

    /// <summary>Sends a datagram, opening the socket and the receive thread on first use.</summary>
    /// <param name="to">The destination.</param>
    /// <param name="data">The payload.</param>
    /// <returns>False when the socket cannot open or the send fails.</returns>
    public bool Send(Address to, byte[] data)
    {
        if (!mOpen)
        {
            mOpen = mSock.Open(0);
            if (mOpen)
            {
                mThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "query-recv" };
                mThread.Start();
            }
        }
        return mOpen && mSock.Send(to, data);
    }

    private void ReceiveLoop()
    {
        var buf = new byte[0x800];
        while (!mStop)
        {
            if (!mSock.WaitRead(20))
                continue;
            var n = mSock.Receive(out var from, buf);
            var at = mNow();
            if (n <= 0)
                continue;
            lock (mQueue)
                mQueue.Enqueue((from, buf.AsSpan(0, n).ToArray(), at));
        }
    }

    /// <summary>Takes the oldest datagram the receive thread queued.</summary>
    /// <param name="from">Receives the sender.</param>
    /// <param name="data">Receives the payload.</param>
    /// <returns>False when none is pending.</returns>
    public bool Receive(out Address from, out byte[] data)
    {
        from = default;
        data = [];
        lock (mQueue)
        {
            if (mQueue.Count == 0)
                return false;
            long at;
            (from, data, at) = mQueue.Dequeue();
            LastReceiveMs = at;
        }
        return true;
    }

    /// <summary>Stops the receive thread and closes the socket.</summary>
    public void Dispose()
    {
        mStop = true;
        mThread?.Join(200);
        mSock.Dispose();
    }
}

/// <summary>The wire of the query: a datagram to or from an address. Tests supply a fake.</summary>
public interface IQueryTransport
{
    /// <summary>Sends to a resolved address.</summary>
    /// <param name="to">The destination.</param>
    /// <param name="data">The payload.</param>
    /// <returns>False when it cannot be sent.</returns>
    public bool Send(Address to, byte[] data);
    /// <summary>Takes one pending datagram.</summary>
    /// <param name="from">Receives the sender.</param>
    /// <param name="data">Receives the payload.</param>
    /// <returns>False when none is pending.</returns>
    public bool Receive(out Address from, out byte[] data);
    /// <summary>Gets the arrival time (the query clock) of the datagram <see cref="Receive"/> gave last.</summary>
    /// <returns>The time, or -1 when the transport does not stamp datagrams.</returns>
    public long LastReceiveMs => -1;
}

/// <summary>Address strings of the query queues. The canonical text is what DNetSession_resolveAddress gives (docs/net-protocol.md 2).</summary>
public static class QueryAddress
{
    /// <summary>The host part that means a broadcast.</summary>
    public const string BroadcastHost = "BROADCAST";

    /// <summary>Resolves an address text. Accepted forms are <c>IP:a.b.c.d:port</c>, <c>IP:BROADCAST:port</c>, <c>IPX:BROADCAST:port</c> (cannot be sent here) and <c>LOOPBACK:port</c>.</summary>
    /// <param name="text">The address text.</param>
    /// <param name="broadcastIp">The target of a broadcast.</param>
    /// <param name="canonical">Receives the canonical text.</param>
    /// <param name="target">Receives the target. Unset for IPX.</param>
    /// <param name="broadcast">Receives true for a broadcast address.</param>
    /// <param name="ipx">Receives true for an IPX address.</param>
    /// <returns>False for text that is none of the forms.</returns>
    public static bool TryResolve(string text, uint broadcastIp, out string canonical, out Address target, out bool broadcast, out bool ipx)
    {
        canonical = "";
        target = default;
        broadcast = ipx = false;
        var t = text.Trim();
        if (t.StartsWith("IPX:", StringComparison.OrdinalIgnoreCase))
        {
            ipx = true;
            if (!t[4..].StartsWith(BroadcastHost, StringComparison.OrdinalIgnoreCase))
                return false;
            broadcast = true;
            canonical = "IPX:Broadcast:" + t[(t.LastIndexOf(':') + 1)..];
            return true;
        }
        if (t.StartsWith("LOOPBACK", StringComparison.OrdinalIgnoreCase))
            t = "IP:127.0.0.1" + t[8..];
        if (!t.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = t[3..];
        var colon = rest.LastIndexOf(':');
        if (colon < 0 || !ushort.TryParse(rest[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            return false;
        var host = rest[..colon];
        if (host.Equals(BroadcastHost, StringComparison.OrdinalIgnoreCase))
        {
            broadcast = true;
            target = new Address(broadcastIp, port);
            canonical = "IP:Broadcast:" + port.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (!System.Net.IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        target = new Address(((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3], port);
        canonical = target.ToString();
        return true;
    }
}

/// <summary>The client side of the server query (the Join Server screen): ping and info requests on the DNet info protocol (0x10, docs/net-protocol.md 6.2), the pacing queues and the reply handling.</summary>
/// <remarks>Follows SimNet_processQueryQueue (0054ee8c), SimNet_onInfoPacket (0054f608) and the info handler (0042c4cc) of the 1.11 exe.</remarks>
public sealed class ServerQuery
{
    private sealed class PingEntry
    {
        public ushort Key;
        public byte Index;
        public int Count;
        public long SentMs;
        public string Address = "";
        public Address Target;
        public bool Broadcast, Ipx;
    }

    private sealed class RequestEntry
    {
        public int Ping;
        public int Count;
        public long SentMs;
        public string Address = "";
        public Address Target;
    }

    private readonly IQueryTransport mNet;
    private readonly Func<long> mNow;
    private readonly List<PingEntry> mPings = [];
    private readonly List<RequestEntry> mRequests = [];
    private readonly List<string> mSeen = [];
    private int mActivePings, mActiveRequests;
    private ushort mNextKey;

    /// <summary>The pacing preferences.</summary>
    public ServerQueryPrefs Prefs = new();
    /// <summary>The target of a broadcast query: 255.255.255.255 on a LAN. The client sends the loopback broadcast (127.255.255.255) unless remote hosts are allowed (R6).</summary>
    public uint BroadcastIp = 0xffffffff;
    /// <summary>The packet version replies must carry, the one of this build.</summary>
    /// <remarks>0054f608 compares it with net + 0xd8.</remarks>
    public ushort PacketVersion = 1;
    /// <summary>The game name replies must carry.</summary>
    public string GameName = "Tribes";
    /// <summary>The R6 gate. A target outside 127.0.0.0/8 is dropped when this returns false.</summary>
    public Func<Address, bool> MayContact = _ => true;

    /// <summary>Raised when a ping is answered, with the address, the host name, the round trip in ms and the packet version it reported (0042caf0).</summary>
    public Action<string, string, int, ushort>? OnPing;
    /// <summary>Raised with the info of one server (0042c4cc).</summary>
    public Action<ServerInfoRecord>? OnInfo;
    /// <summary>Raised when a part of a master list arrived, with the master address, its name, whether this is part 1 and its message of the day. The name and message are set only for part 1 (vtable +0xac, 0042ca00).</summary>
    public Action<string, string, bool, string>? OnMaster;
    /// <summary>Raised with the address when a ping or request gave up or the server speaks another version (vtable +0xb0, 0042cb88).</summary>
    public Action<string>? OnFail;
    /// <summary>Raised when the queues shrank or grew, so the screen refreshes.</summary>
    public Action? OnChange;

    /// <summary>Creates a query.</summary>
    /// <param name="net">The wire.</param>
    /// <param name="now">The query clock in milliseconds.</param>
    public ServerQuery(IQueryTransport net, Func<long> now)
    {
        mNet = net;
        mNow = now;
    }

    /// <summary>The number of queued pings.</summary>
    public int PendingPings => mPings.Count;
    /// <summary>The number of queued info requests.</summary>
    public int PendingRequests => mRequests.Count;
    /// <summary>The number of pings and requests in flight.</summary>
    public int Active => mActivePings + mActiveRequests;
    /// <summary>The addresses that already answered or failed.</summary>
    public IReadOnlyList<string> Seen => mSeen;
    /// <summary>The addresses of the queued pings.</summary>
    public IEnumerable<string> PendingPingAddresses => mPings.Select(e => e.Address);

    /// <summary>Empties the list of addresses already answered. The queues stay, as the stock code leaves them.</summary>
    /// <remarks>Follows the clearing part of TribesNet_queryServers (0042bb50): DAT_006aad58 = 0.</remarks>
    public void ClearSeen() => mSeen.Clear();

    /// <summary>Counts an address as answered (0042cd68).</summary>
    /// <param name="canonical">The canonical address text.</param>
    public void AddSeen(string canonical) => mSeen.Add(canonical);

    /// <summary>Queues a ping for an address text that is not queued yet, then runs the queue.</summary>
    /// <param name="address">The address text.</param>
    /// <param name="index">The query index, 0 for a plain ping or the part of a master list.</param>
    /// <remarks>Follows SimNet_queueServerQuery.</remarks>
    public void Queue(string address, int index = 0)
    {
        if (!QueryAddress.TryResolve(address, BroadcastIp, out var canon, out var target, out var bc, out var ipx))
            return;
        foreach (var e in mPings)
            if (string.Equals(e.Address, canon, StringComparison.Ordinal) && e.Index == index)
                return;
        mPings.Add(new PingEntry { Index = (byte)index, Address = canon, Target = target, Broadcast = bc, Ipx = ipx });
        Process();
    }

    /// <summary>Queues an info request for a server that answered the ping, then runs the queue (005500e8).</summary>
    /// <param name="address">The address text.</param>
    /// <param name="ping">The ping entry the request follows.</param>
    public void Request(string address, int ping)
    {
        if (!QueryAddress.TryResolve(address, BroadcastIp, out var canon, out var target, out _, out _))
            return;
        mRequests.Add(new RequestEntry { Ping = ping, Address = canon, Target = target });
        Process();
    }

    /// <summary>Empties both queues (stock cancel 0055017c, 00550190).</summary>
    public void CancelAll()
    {
        mPings.Clear();
        mRequests.Clear();
        mActivePings = mActiveRequests = 0;
    }

    private bool Send(Address to, byte[] data) => MayContact(to) && mNet.Send(to, data);

    /// <summary>Sends or resends the pings and requests that are due and drops the ones that ran out of retries.</summary>
    /// <remarks>Follows SimNet_processQueryQueue (0054ee8c). Requests run only while no ping is in flight. A broadcast ping is sent once and leaves the queue (0054e438), and whoever answers is queried by its own address.</remarks>
    public void Process()
    {
        var now = mNow();
        int i = 0, n = 0;
        while (i < mPings.Count)
        {
            var e = mPings[i];
            var send = false;
            if (e.Count == 0)
            {
                if (mActivePings < Prefs.MaxPings)
                {
                    ++mActivePings;
                    send = true;
                    ++n;
                }
            }
            else if (e.SentMs + Prefs.PingTimeoutMs < now)
            {
                if (Prefs.PingRetries < e.Count)
                {
                    mPings.RemoveAt(i);
                    --mActivePings;
                    mSeen.Add(e.Address);
                    OnFail?.Invoke(e.Address);
                    continue;
                }
                send = true;
                ++n;
            }
            else
                ++n;
            if (send)
            {
                e.Key = mNextKey++;
                if (!e.Ipx)
                    Send(e.Target, QueryPackets.BuildPing(e.Key, e.Index));
                e.SentMs = mNow();
                ++e.Count;
                // 0054e438: a broadcast is sent once and leaves the queue, whoever answers is queried by its own address.
                if (e.Broadcast)
                {
                    mPings.RemoveAt(i);
                    --mActivePings;
                    --n;
                    continue;
                }
            }
            ++i;
            if (n == Prefs.MaxPings)
                break;
        }
        if (mActivePings == 0)
        {
            i = 0;
            n = 0;
            while (i < mRequests.Count)
            {
                var r = mRequests[i];
                var send = false;
                if (r.Count == 0)
                {
                    if (mActiveRequests < Prefs.MaxRequests)
                    {
                        ++mActiveRequests;
                        send = true;
                        ++n;
                    }
                }
                else if (r.SentMs + Prefs.RequestTimeoutMs < now)
                {
                    if (Prefs.RequestRetries < r.Count)
                    {
                        mRequests.RemoveAt(i);
                        --mActiveRequests;
                        OnFail?.Invoke(r.Address);
                        continue;
                    }
                    send = true;
                    ++n;
                }
                else
                    ++n;
                if (send)
                {
                    Send(r.Target, QueryPackets.BuildInfoRequest(mNextKey++));
                    r.SentMs = mNow();
                    ++r.Count;
                }
                ++i;
                if (n == Prefs.MaxRequests)
                    break;
            }
        }
    }

    /// <summary>Handles the pending replies, then runs the queue.</summary>
    /// <remarks>The exe runs the queue from a 30 ms sim event while anything is in flight.</remarks>
    public void Tick()
    {
        while (mNet.Receive(out var from, out var data))
            OnDatagram(from, data, mNet.LastReceiveMs);
        if (mActivePings > 0 || mActiveRequests > 0 || mPings.Count > 0 || mRequests.Count > 0)
            Process();
    }

    private PingEntry? FindPing(string address, int index)
    {
        foreach (var e in mPings)
            if (string.Equals(e.Address, address, StringComparison.Ordinal) && e.Index == index)
                return e;
        return null;
    }

    /// <summary>Handles a master server list part (0054f608 case 6).</summary>
    /// <param name="addr">The master address text.</param>
    /// <param name="d">The datagram: byte 2 is the part, byte 3 the part count and bytes 4-5 the key of the request.</param>
    /// <remarks>The ping entry of the request leaves the queue. A reply to the first request makes entries for the parts still to come. Part 1 carries the master's name and message of the day. Every address of the list that has not answered yet is queued as a ping. A 6 byte entry is the IP, then the port in little endian order (0054e3c8).</remarks>
    private void OnMasterReply(string addr, byte[] d)
    {
        if (d.Length < 10)
            return;
        var key = (ushort)(d[4] | (d[5] << 8));
        var e = FindPing(addr, 0);
        if (e == null)
        {
            var part = FindPing(addr, d[2]);
            if (part == null || part.Key != key)
                return;
            mPings.Remove(part);
            if (mActivePings > 0)
                --mActivePings;
        }
        else
        {
            if (e.Key != key)
                return;
            mPings.Remove(e);
            if (mActivePings > 0)
                --mActivePings;
            if (QueryAddress.TryResolve(addr, BroadcastIp, out var canon, out var target, out _, out _))
                for (var n = 1; n <= d[3]; ++n)
                {
                    if (n == d[2])
                        continue;
                    ++mActivePings;
                    mPings.Add(new PingEntry { Key = key, Index = (byte)n, Count = 1, SentMs = mNow(), Address = canon, Target = target });
                }
        }
        string name = "(no name)", motd = "(no motd)";
        var at = 0;
        if (d[2] == 1)
        {
            int nl = d[8];
            if (9 + nl >= d.Length)
                return;
            name = nl == 0 ? "(no name)" : System.Text.Encoding.Latin1.GetString(d, 9, nl);
            int ml = d[9 + nl];
            if (10 + nl + ml > d.Length)
                return;
            motd = ml == 0 ? "(no motd)" : System.Text.Encoding.Latin1.GetString(d, 10 + nl, ml);
            at = nl + 2 + ml;
        }
        if (8 + at + 2 > d.Length)
            return;
        var count = (d[8 + at] * 256) + d[9 + at];
        var p = 10 + at;
        while (count-- > 0 && p < d.Length)
        {
            int len = d[p];
            string text;
            if (len == 6 && p + 6 < d.Length)
                text = $"IP:{d[p + 1]}.{d[p + 2]}.{d[p + 3]}.{d[p + 4]}:{d[p + 5] | (d[p + 6] << 8)}";
            else
            {
                var end = Array.IndexOf(d, (byte)0, p);
                text = System.Text.Encoding.Latin1.GetString(d, p, (end < 0 ? d.Length : end) - p);
            }
            p += len + 1;
            if (QueryAddress.TryResolve(text, BroadcastIp, out var c, out _, out _, out _) && !mSeen.Contains(c))
                Queue(text);
        }
        OnMaster?.Invoke(addr, name, d[2] == 1, motd);
        OnChange?.Invoke();
    }

    /// <summary>Handles one query reply.</summary>
    /// <param name="from">The sender.</param>
    /// <param name="data">The datagram.</param>
    /// <param name="arrivedMs">The arrival time on the query clock, or -1 to use the current time.</param>
    /// <remarks>Follows SimNet_onInfoPacket (0054f608) cases 4, 6 and 8. This is the client side. The server cases 3 and 7 are handled by <c>NetServer</c>.</remarks>
    public void OnDatagram(Address from, byte[] data, long arrivedMs = -1)
    {
        if (data.Length < 2 || data[0] != 0x10)
            return;
        var addr = from.ToString();
        switch (data[1])
        {
            case 4:
            {
                // A ping reply counts only while a ping is queued: with the broadcast gone from the queue and nothing else
                // pending (a LAN-only query), the answer is dropped.
                if (mPings.Count == 0 || !QueryPackets.TryParsePingReply(data, out var pr))
                    return;
                var e = FindPing(addr, 0);
                if (e == null)
                {
                    if (mSeen.Contains(addr))
                        return;
                    Queue(addr);
                }
                else if (e.Key == pr.Key)
                {
                    var ping = (int)((arrivedMs >= 0 ? arrivedMs : mNow()) - e.SentMs);
                    mPings.Remove(e);
                    mSeen.Add(addr);
                    --mActivePings;
                    OnPing?.Invoke(addr, pr.Hostname, ping, pr.PacketVersion);
                    OnChange?.Invoke();
                }
                break;
            }
            case 6:
                OnMasterReply(addr, data);
                break;
            case 8:
            {
                var at = mRequests.FindIndex(r => string.Equals(r.Address, addr, StringComparison.Ordinal));
                if (at >= 0)
                {
                    mRequests.RemoveAt(at);
                    --mActiveRequests;
                }
                if (!QueryPackets.TryParseInfoReply(data, out var rec))
                {
                    OnFail?.Invoke(addr);
                    break;
                }
                rec.Address = addr;
                // 0042c4cc: another packet version or another game is reported as a failure of that address.
                if (rec.PacketVersion != PacketVersion || !string.Equals(rec.GameName, GameName, StringComparison.Ordinal))
                {
                    OnFail?.Invoke(addr);
                    break;
                }
                OnInfo?.Invoke(rec);
                OnChange?.Invoke();
                break;
            }
        }
    }
}
