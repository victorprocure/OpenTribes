// Port of src/server/master.hpp and src/server/master.cpp: master server heartbeat (SimNet_sendMasterHeartbeat
// 0x0054f478) and the DNet master commands (0x00422ad8, 0x00422a74). ROADMAP 8.3, rule R6: nothing leaves the machine
// unless the operator opts in (--allow-masters); loopback masters always work (tests).
using System.Net;
using System.Net.Sockets;
using OpenTribes.Net;
using OpenTribes.Script;

namespace OpenTribes.Server;

public sealed class MasterHeartbeat
{
    public const float kPeriod = 120.0f; // seconds of sim time between heartbeats

    // schedule(delay, action): run action after delay seconds of sim time.
    public delegate void Schedule(float delay, Action action);

    private static readonly byte[] kHeartbeat = { 0x10, 0x05, 0xff, 0, 0, 0, 0, 0 };

    private readonly Interpreter mIn;
    private readonly NetServer mNet;
    private readonly bool mAllowRemote;
    private Schedule? mSchedule;
    private readonly List<string> mNames = new();    // resolveMasters arguments
    private readonly List<string> mResolved = new(); // "IP:a.b.c.d:port" or ""
    private bool mResolveDone;

    // src/server/master.cpp MasterHeartbeat::MasterHeartbeat.
    public MasterHeartbeat(Interpreter interp, NetServer net, bool allowRemote)
    {
        mIn = interp;
        mNet = net;
        mAllowRemote = allowRemote;
    }

    // src/server/master.cpp parseMasterAddress: "IP:a.b.c.d:port" (prefix optional, ASCII case-insensitive). A host
    // name instead of the dotted address is looked up only when resolveNames is set. False when malformed.
    public static bool ParseMasterAddress(string text, out Address result, bool resolveNames)
    {
        result = default;
        string s = text;
        if (s.Length >= 3 && (s[0] | 0x20) == 'i' && (s[1] | 0x20) == 'p' && s[2] == ':') // strncasecmp(s, "IP:", 3)
            s = s[3..];
        int colon = s.LastIndexOf(':');
        if (colon < 0 || colon == 0 || colon + 1 >= s.Length)
            return false;
        string host = s[..colon];
        if (!Strtol(s, colon + 1, out long port) || port <= 0 || port > 65535)
            return false;
        if (!InetPton4(host, out uint ip))
        {
            if (!resolveNames)
                return false;
            // getaddrinfo(host, AF_INET, SOCK_DGRAM): the first IPv4 address.
            IPAddress[] found;
            try
            {
                found = Dns.GetHostAddresses(host, AddressFamily.InterNetwork);
            }
            catch (Exception e) when (e is SocketException or ArgumentException)
            {
                return false;
            }
            if (found.Length == 0)
                return false;
            byte[] b = found[0].GetAddressBytes();
            ip = (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
        }
        result = new Address(ip, unchecked((ushort)port));
        return true;
    }

    // strtol(s + start, &end, 10) followed by the `*end == '\0'` check of parseMasterAddress: leading C white space, an
    // optional sign, decimal digits; an overflow saturates to the 64-bit long range.
    private static bool Strtol(string s, int start, out long value)
    {
        value = 0;
        int i = start;
        while (i < s.Length && (s[i] == ' ' || (s[i] >= '\t' && s[i] <= '\r')))
            ++i;
        bool neg = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            neg = s[i++] == '-';
        int digits = i;
        ulong mag = 0;
        bool overflow = false;
        for (; i < s.Length && s[i] >= '0' && s[i] <= '9'; ++i)
        {
            ulong next = unchecked(mag * 10 + (ulong)(s[i] - '0'));
            if (mag > (ulong.MaxValue - 9) / 10 || next > (neg ? (ulong)long.MaxValue + 1 : long.MaxValue))
                overflow = true;
            mag = next;
        }
        if (i == digits)
            return start == s.Length; // no conversion: end is the start of the text
        value = overflow ? (neg ? long.MinValue : long.MaxValue) : neg ? unchecked(-(long)mag) : (long)mag;
        return i == s.Length;
    }

    // inet_pton(AF_INET): exactly four dotted decimal parts of 0..255, no leading zero, nothing else.
    private static bool InetPton4(string src, out uint ip)
    {
        ip = 0;
        var tmp = new uint[4];
        int tp = 0, octets = 0;
        bool sawDigit = false;
        foreach (char ch in src)
        {
            if (ch >= '0' && ch <= '9')
            {
                uint next = tmp[tp] * 10 + (uint)(ch - '0');
                if (sawDigit && tmp[tp] == 0)
                    return false;
                if (next > 255)
                    return false;
                tmp[tp] = next;
                if (!sawDigit)
                {
                    if (++octets > 4)
                        return false;
                    sawDigit = true;
                }
            }
            else if (ch == '.' && sawDigit)
            {
                if (octets == 4)
                    return false;
                tmp[++tp] = 0;
                sawDigit = false;
            }
            else
                return false;
        }
        if (octets < 4)
            return false;
        ip = tmp[0] << 24 | tmp[1] << 16 | tmp[2] << 8 | tmp[3];
        return true;
    }

    // src/server/master.cpp MasterHeartbeat::registerCommands: DNet::resolveMasters, DNet::getResolvedMaster.
    public void RegisterCommands()
    {
        // 0x00422ad8: up to 10 names, stopping at the first empty one; the original resolves them on a thread and
        // returns the count at once. Here names resolve during the call, and only with --allow-masters (dotted
        // addresses always translate).
        mIn.AddCommand("DNet::resolveMasters", (i, a) =>
        {
            mNames.Clear();
            mResolved.Clear();
            mResolveDone = false;
            if (a.Length < 2)
                return null; // the original returns a null result
            while (mNames.Count < 10 && mNames.Count + 1 < a.Length && a[mNames.Count + 1].Length != 0)
            {
                string name = a[mNames.Count + 1];
                mNames.Add(name.Length > 255 ? name[..255] : name);
            }
            foreach (string name in mNames)
                mResolved.Add(ParseMasterAddress(name, out Address addr, mAllowRemote) ? addr.ToString() : "");
            mResolveDone = true;
            return Numbers.FormatInt(mNames.Count);
        });
        // 0x00422a74: "" until the resolve finished, for a wrong argument count and for an index outside 0..9.
        mIn.AddCommand("DNet::getResolvedMaster", (i, a) =>
        {
            if (!mResolveDone || a.Length != 2)
                return "";
            int idx = Numbers.BorlandAtoi(a[1]);
            if (idx >= 0 && idx < mResolved.Count)
                return mResolved[idx];
            return "";
        });
    }

    // SimNet_activate 0x0054e86c, its tail: once the transports of a FearCSDelegate have started, the first round runs
    // when the delegate is a server (first argument starts with t, T or 1), a transport started and one of the
    // (transport, port) pairs is named exactly "IP" (case-sensitive compare with 0x0063ead5), and $SinglePlayer is not
    // true. A dedicated server and a game hosted by a client go through this same code.
    public bool OnNetworkStart(IReadOnlyList<string> args, bool transportStarted, Schedule schedule)
    {
        bool isServer = args.Count > 0 && args[0].Length > 0 && args[0][0] is 't' or 'T' or '1';
        bool ip = false;
        for (int i = 1; i + 1 < args.Count; i += 2)
            ip |= string.Equals(args[i], "IP", StringComparison.Ordinal);
        if (!isServer || !transportStarted || !ip || Numbers.ParseBool(mIn.PeekVariable("$SinglePlayer")))
            return false;
        Start(schedule);
        return true;
    }

    // src/server/master.cpp MasterHeartbeat::start: server network start (not $SinglePlayer): first heartbeat, then
    // one every 120 s.
    public void Start(Schedule schedule)
    {
        mSchedule = schedule;
        Beat();
    }

    // src/server/master.cpp MasterHeartbeat::beat: one heartbeat round: default $Server::HostPublicGame, reschedule,
    // then if public run checkMasterTranslation() and send `10 05 ff 00 00 00 00 00` to $inet::Master1..N.
    public void Beat()
    {
        // An unset $Server::HostPublicGame becomes TRUE: a server is public unless told otherwise.
        if (mIn.GetVariable("$Server::HostPublicGame").Length == 0)
            mIn.SetVariable("$Server::HostPublicGame", "TRUE");
        mSchedule?.Invoke(kPeriod, Beat);
        if (!Numbers.ParseBool(mIn.GetVariable("$Server::HostPublicGame")))
            return;
        mIn.Evaluate("checkMasterTranslation();");
        for (int i = 1; i <= 64; ++i) // the original has no bound; the list ends at the first unset entry
        {
            string master = mIn.GetVariable("$inet::Master" + Numbers.FormatInt(i));
            if (master.Length == 0)
                break;
            // R6: only loopback masters without the operator's opt-in. Names resolve with it.
            if (ParseMasterAddress(master, out Address addr, mAllowRemote) && (mAllowRemote || (addr.Ip >> 24) == 127))
                mNet.SendConnectionless(addr, kHeartbeat);
            mIn.Print(2, "SimNET: Sending heartbeat to master " + master + ".");
        }
    }
}
