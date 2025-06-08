// The client side of the server query (the Join Server screen): ping and info requests on the DNet info protocol (0x10,
// docs/net-protocol.md 6.2), the pacing queues and the reply handling. Written from SimNet_processQueryQueue 0054ee8c,
// SimNet_onInfoPacket 0054f608 and the info handler 0042c4cc of the 1.11 exe.
using System.Globalization;

namespace OpenTribes.Net;

// $pref::maxConcurrentPings .. requestRetryCount (SimNet_loadQueryPrefs 0054e61c; the defaults are the ones the exe passes).
public sealed class ServerQueryPrefs
{
    public int MaxPings = 20, PingTimeoutMs = 1500, PingRetries = 2, MaxRequests = 10, RequestTimeoutMs = 2000, RequestRetries = 2;
}

// The record of a type 8 reply (0042c4cc fills the 0x458 byte server record in this order).
public sealed class ServerInfoRecord
{
    public ushort Key;
    public string Address = "";
    public string GameName = "", Version = "", Hostname = "", Mission = "", ModList = "", MissionType = "", Info = "";
    public string TeamHeading = "", ClientHeading = "";
    public byte Players, MaxPlayers;
    public bool Dedicated, Password;
    public int Cpu;
    public ushort PacketVersion;
    public readonly List<string> Rows = new();
}

public readonly record struct PingReply(ushort Key, byte Players, ushort PacketVersion, string Hostname);

public static class QueryPackets
{
    // 0054ee8c (case 3 request): 10 03 b2 00 key(le) 00 00, b2 = the query index or 0xff.
    public static byte[] BuildPing(ushort key, byte index) => new byte[] { 0x10, 3, index == 0 ? (byte)0xff : index, 0, (byte)key, (byte)(key >> 8), 0, 0 };

    // 0054ee8c (request loop): 10 07 ff 00 key 00 00.
    public static byte[] BuildInfoRequest(ushort key) => new byte[] { 0x10, 7, 0xff, 0, (byte)key, (byte)(key >> 8), 0, 0 };

    // 0054f608 case 4: 40 bytes, byte 3 is 0xf0, byte 7 the player count, bytes 8-9 the packet version, 10..38 the host name.
    public static bool TryParsePingReply(ReadOnlySpan<byte> d, out PingReply reply)
    {
        reply = default;
        if (d.Length < 40 || d[0] != 0x10 || d[1] != 4 || d[3] != 0xf0)
            return false;
        int end = 10;
        while (end < 39 && d[end] != 0)
            ++end;
        reply = new PingReply((ushort)(d[4] | d[5] << 8), d[7], (ushort)(d[8] | d[9] << 8), System.Text.Encoding.Latin1.GetString(d.Slice(10, end - 10)));
        return true;
    }

    // 0042c4cc: after the 8 byte header a bit stream: packet version (2 raw bytes), game name, version, host name, players, maximum,
    // mission, dedicated, password, cpu speed (4 raw bytes), mod list, mission type, info, team heading, client heading, then rows.
    // The packet version and the game name checks are the caller's. Returns false when the stream is too short for the fixed part.
    public static bool TryParseInfoReply(ReadOnlySpan<byte> d, out ServerInfoRecord rec)
    {
        rec = new ServerInfoRecord();
        if (d.Length < 8 + 2 || d[0] != 0x10 || d[1] != 8)
            return false;
        rec.Key = (ushort)(d[4] | d[5] << 8);
        byte[] body = d[8..].ToArray();
        var bs = new BitStream(body, body.Length);
        Span<byte> two = stackalloc byte[2];
        bs.ReadBytes(two);
        rec.PacketVersion = (ushort)(two[0] | two[1] << 8);
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
        rec.Cpu = four[0] | four[1] << 8 | four[2] << 16 | four[3] << 24;
        rec.ModList = bs.ReadString();
        rec.MissionType = bs.ReadString();
        rec.Info = bs.ReadString();
        rec.TeamHeading = bs.ReadString();
        rec.ClientHeading = bs.ReadString();
        // The rows: the loop of 0042c4cc runs while bytes remain (it compares the stream position with the packet length).
        while (!bs.Error() && bs.BytePosition() < body.Length)
        {
            string row = bs.ReadString();
            if (bs.Error())
                break;
            rec.Rows.Add(row);
        }
        return !bs.Error();
    }
}

// The wire: a datagram to or from an address string ("IP:a.b.c.d:port"). Tests give a fake.
// The UDP side: one socket on a port the OS picks (the client's own 0058bfac binds port 0).
public sealed class UdpQueryTransport : IQueryTransport, IDisposable
{
    private readonly UdpSocket mSock = new();
    private readonly Func<long> mNow;
    private readonly Queue<(Address From, byte[] Data, long AtMs)> mQueue = new();
    private Thread? mThread;
    private volatile bool mStop;
    private bool mOpen;

    // `now` is the clock of the query; the receive thread stamps each datagram with it on arrival (the stock exe takes the time of
    // the ping reply when the packet comes in, 0054f608 case 4, not when the screen next looks).
    public UdpQueryTransport(Func<long>? now = null) => mNow = now ?? (() => Environment.TickCount64);

    public long LastReceiveMs { get; private set; } = -1;

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
            int n = mSock.Receive(out Address from, buf);
            long at = mNow();
            if (n <= 0)
                continue;
            lock (mQueue)
                mQueue.Enqueue((from, buf.AsSpan(0, n).ToArray(), at));
        }
    }

    public bool Receive(out Address from, out byte[] data)
    {
        from = default;
        data = Array.Empty<byte>();
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

    public void Dispose()
    {
        mStop = true;
        mThread?.Join(200);
        mSock.Dispose();
    }
}

public interface IQueryTransport
{
    // Sends to the resolved address; false when it cannot be sent.
    bool Send(Address to, byte[] data);
    // One pending datagram or false.
    bool Receive(out Address from, out byte[] data);
    // The arrival time (the query clock) of the datagram Receive gave last, or -1 when the transport does not stamp them.
    long LastReceiveMs => -1;
}

// Address strings of the query queues (the canonical text DNetSession_resolveAddress gives, docs/net-protocol.md 2).
public static class QueryAddress
{
    public const string BroadcastHost = "BROADCAST";

    // Canonical text and the target. IP:a.b.c.d:port, IP:BROADCAST:port (target 255.255.255.255), IPX:BROADCAST:port (cannot be
    // sent here), LOOPBACK:port. Returns false for text that is none of them.
    public static bool TryResolve(string text, uint broadcastIp, out string canonical, out Address target, out bool broadcast, out bool ipx)
    {
        canonical = "";
        target = default;
        broadcast = ipx = false;
        string t = text.Trim();
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
        string rest = t[3..];
        int colon = rest.LastIndexOf(':');
        if (colon < 0 || !ushort.TryParse(rest[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out ushort port))
            return false;
        string host = rest[..colon];
        if (host.Equals(BroadcastHost, StringComparison.OrdinalIgnoreCase))
        {
            broadcast = true;
            target = new Address(broadcastIp, port);
            canonical = "IP:Broadcast:" + port.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (!System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        byte[] b = ip.GetAddressBytes();
        target = new Address((uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3], port);
        canonical = target.ToString();
        return true;
    }
}

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
    private readonly List<PingEntry> mPings = new();
    private readonly List<RequestEntry> mRequests = new();
    private readonly List<string> mSeen = new();
    private int mActivePings, mActiveRequests;
    private ushort mNextKey;

    public ServerQueryPrefs Prefs = new();
    // 255.255.255.255 on a LAN; the client sends the loopback broadcast (127.255.255.255) unless remote hosts are allowed (R6).
    public uint BroadcastIp = 0xffffffff;
    // The version the replies must carry (the packet version of this build, 0054f608 compares it with net + 0xd8).
    public ushort PacketVersion = 1;
    public string GameName = "Tribes";
    // The R6 gate: a target outside 127.0.0.0/8 is dropped when this returns false.
    public Func<Address, bool> MayContact = _ => true;

    // 0042caf0: a ping answered (address, host name, round trip in ms, the packet version it reported).
    public Action<string, string, int, ushort>? OnPing;
    // 0042c4cc: the info of one server.
    public Action<ServerInfoRecord>? OnInfo;
    // vtable +0xac (0042ca00): a master list part arrived (master address, its name and message of the day when this is part 1).
    public Action<string, string, bool, string>? OnMaster;
    // vtable +0xb0 (0042cb88): a ping or request gave up, or the server speaks another version.
    public Action<string>? OnFail;
    // Something changed for the screen: the queues shrank or grew.
    public Action? OnChange;

    public ServerQuery(IQueryTransport net, Func<long> now)
    {
        mNet = net;
        mNow = now;
    }

    public int PendingPings => mPings.Count;
    public int PendingRequests => mRequests.Count;
    public int Active => mActivePings + mActiveRequests;
    public IReadOnlyList<string> Seen => mSeen;
    public IEnumerable<string> PendingPingAddresses => mPings.Select(e => e.Address);

    // TribesNet_queryServers 0042bb50, the part that clears: the queues stay (the stock code leaves them as they are), the list of
    // addresses already answered starts empty (DAT_006aad58 = 0).
    public void ClearSeen() => mSeen.Clear();

    // 0042cd68: an address counted as answered.
    public void AddSeen(string canonical) => mSeen.Add(canonical);

    // SimNet_queueServerQuery: an address text not yet queued gets an entry, then the queue runs.
    public void Queue(string address, int index = 0)
    {
        if (!QueryAddress.TryResolve(address, BroadcastIp, out string canon, out Address target, out bool bc, out bool ipx))
            return;
        foreach (PingEntry e in mPings)
            if (string.Equals(e.Address, canon, StringComparison.Ordinal) && e.Index == index)
                return;
        mPings.Add(new PingEntry { Index = (byte)index, Address = canon, Target = target, Broadcast = bc, Ipx = ipx });
        Process();
    }

    // 005500e8: an info request for a server that answered the ping.
    public void Request(string address, int ping)
    {
        if (!QueryAddress.TryResolve(address, BroadcastIp, out string canon, out Address target, out _, out _))
            return;
        mRequests.Add(new RequestEntry { Ping = ping, Address = canon, Target = target });
        Process();
    }

    // The stock cancel (0055017c, 00550190): both queues are emptied.
    public void CancelAll()
    {
        mPings.Clear();
        mRequests.Clear();
        mActivePings = mActiveRequests = 0;
    }

    private bool Send(Address to, byte[] data) => MayContact(to) && mNet.Send(to, data);

    // SimNet_processQueryQueue 0054ee8c.
    public void Process()
    {
        long now = mNow();
        int i = 0, n = 0;
        while (i < mPings.Count)
        {
            PingEntry e = mPings[i];
            bool send = false;
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
                RequestEntry r = mRequests[i];
                bool send = false;
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

    // One frame: the replies, then the queue (the exe runs the queue from a 30 ms sim event while anything is in flight).
    public void Tick()
    {
        while (mNet.Receive(out Address from, out byte[] data))
            OnDatagram(from, data, mNet.LastReceiveMs);
        if (mActivePings > 0 || mActiveRequests > 0 || mPings.Count > 0 || mRequests.Count > 0)
            Process();
    }

    private PingEntry? FindPing(string address, int index)
    {
        foreach (PingEntry e in mPings)
            if (string.Equals(e.Address, address, StringComparison.Ordinal) && e.Index == index)
                return e;
        return null;
    }

    // 0054f608 case 6: a part of a master's server list (b2 = part, b3 = part count, b4/b5 = the key of the request). The ping entry of
    // the request leaves the queue; a reply to the first request makes entries for the parts still to come. Part 1 carries the master's
    // name and message of the day. Every address of the list that has not answered yet is queued as a ping (0054e3c8: a 6 byte entry is
    // IP, then the port in little endian order).
    private void OnMasterReply(string addr, byte[] d)
    {
        if (d.Length < 10)
            return;
        ushort key = (ushort)(d[4] | d[5] << 8);
        PingEntry? e = FindPing(addr, 0);
        if (e == null)
        {
            PingEntry? part = FindPing(addr, d[2]);
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
            if (QueryAddress.TryResolve(addr, BroadcastIp, out string canon, out Address target, out _, out _))
                for (int n = 1; n <= d[3]; ++n)
                {
                    if (n == d[2])
                        continue;
                    ++mActivePings;
                    mPings.Add(new PingEntry { Key = key, Index = (byte)n, Count = 1, SentMs = mNow(), Address = canon, Target = target });
                }
        }
        string name = "(no name)", motd = "(no motd)";
        int at = 0;
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
        int count = d[8 + at] * 256 + d[9 + at];
        int p = 10 + at;
        while (count-- > 0 && p < d.Length)
        {
            int len = d[p];
            string text;
            if (len == 6 && p + 6 < d.Length)
                text = $"IP:{d[p + 1]}.{d[p + 2]}.{d[p + 3]}.{d[p + 4]}:{d[p + 5] | d[p + 6] << 8}";
            else
            {
                int end = Array.IndexOf(d, (byte)0, p);
                text = System.Text.Encoding.Latin1.GetString(d, p, (end < 0 ? d.Length : end) - p);
            }
            p += len + 1;
            if (QueryAddress.TryResolve(text, BroadcastIp, out string c, out _, out _, out _) && !mSeen.Contains(c))
                Queue(text);
        }
        OnMaster?.Invoke(addr, name, d[2] == 1, motd);
        OnChange?.Invoke();
    }

    // SimNet_onInfoPacket 0054f608, cases 4 and 8 (a client; the server's cases 3 and 7 are NetServer's).
    public void OnDatagram(Address from, byte[] data, long arrivedMs = -1)
    {
        if (data.Length < 2 || data[0] != 0x10)
            return;
        string addr = from.ToString();
        switch (data[1])
        {
            case 4:
                {
                    // A ping reply counts only while a ping is queued: with the broadcast gone from the queue and nothing else
                    // pending (a LAN-only query), the answer is dropped.
                    if (mPings.Count == 0 || !QueryPackets.TryParsePingReply(data, out PingReply pr))
                        return;
                    PingEntry? e = FindPing(addr, 0);
                    if (e == null)
                    {
                        if (mSeen.Contains(addr))
                            return;
                        Queue(addr);
                    }
                    else if (e.Key == pr.Key)
                    {
                        int ping = (int)((arrivedMs >= 0 ? arrivedMs : mNow()) - e.SentMs);
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
                    int at = mRequests.FindIndex(r => string.Equals(r.Address, addr, StringComparison.Ordinal));
                    if (at >= 0)
                    {
                        mRequests.RemoveAt(at);
                        --mActiveRequests;
                    }
                    if (!QueryPackets.TryParseInfoReply(data, out ServerInfoRecord rec))
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
