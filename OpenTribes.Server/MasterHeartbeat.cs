namespace OpenTribes.Server;

using System.Net;
using System.Net.Sockets;
using OpenTribes.Net;
using OpenTribes.Script;

/// <summary>The master server heartbeat and the DNet master commands.</summary>
/// <remarks>Port of src/server/master.cpp. Follows SimNet_sendMasterHeartbeat (0054f478) and the DNet master commands (00422ad8, 00422a74), ROADMAP 8.3. Rule R6: nothing leaves the machine unless the operator opts in with <c>--allow-masters</c>. Loopback masters always work, which the tests use.</remarks>
public sealed class MasterHeartbeat
{
    /// <summary>The sim time in seconds between heartbeats.</summary>
    public const float kPeriod = 120.0f;

    /// <summary>Runs an action after a delay of sim time.</summary>
    /// <param name="delay">The delay in seconds.</param>
    /// <param name="action">The action.</param>
    public delegate void Schedule(float delay, Action action);

    private static readonly byte[] kHeartbeat = [0x10, 0x05, 0xff, 0, 0, 0, 0, 0];

    private readonly Interpreter mIn;
    private readonly NetServer mNet;
    private readonly bool mAllowRemote;
    private Schedule? mSchedule;
    private readonly List<string> mNames = [];    // resolveMasters arguments
    private readonly List<string> mResolved = []; // "IP:a.b.c.d:port" or ""
    private bool mResolveDone;

    /// <summary>Creates the heartbeat.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="net">The server network that sends the heartbeat.</param>
    /// <param name="allowRemote">True when the operator allows masters outside loopback (R6).</param>
    public MasterHeartbeat(Interpreter interp, NetServer net, bool allowRemote)
    {
        mIn = interp;
        mNet = net;
        mAllowRemote = allowRemote;
    }

    /// <summary>Parses <c>IP:a.b.c.d:port</c>. The prefix is optional and matched without case.</summary>
    /// <param name="text">The address text.</param>
    /// <param name="result">Receives the address.</param>
    /// <param name="resolveNames">True to look up a host name given instead of the dotted address.</param>
    /// <returns>False when malformed.</returns>
    public static bool ParseMasterAddress(string text, out Address result, bool resolveNames)
    {
        result = default;
        var s = text;
        if (s.Length >= 3 && (s[0] | 0x20) == 'i' && (s[1] | 0x20) == 'p' && s[2] == ':') // strncasecmp(s, "IP:", 3)
            s = s[3..];
        var colon = s.LastIndexOf(':');
        if (colon < 0 || colon == 0 || colon + 1 >= s.Length)
            return false;
        var host = s[..colon];
        if (!Strtol(s, colon + 1, out var port) || port <= 0 || port > 65535)
            return false;
        if (!InetPton4(host, out var ip))
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
            var b = found[0].GetAddressBytes();
            ip = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }
        result = new Address(ip, unchecked((ushort)port));
        return true;
    }

    // strtol(s + start, &end, 10) followed by the `*end == '\0'` check of parseMasterAddress: leading C white space, an
    // optional sign, decimal digits; an overflow saturates to the 64-bit long range.
    private static bool Strtol(string s, int start, out long value)
    {
        value = 0;
        var i = start;
        while (i < s.Length && (s[i] == ' ' || (s[i] >= '\t' && s[i] <= '\r')))
            ++i;
        var neg = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            neg = s[i++] == '-';
        var digits = i;
        ulong mag = 0;
        var overflow = false;
        for (; i < s.Length && s[i] >= '0' && s[i] <= '9'; ++i)
        {
            var next = unchecked((mag * 10) + (ulong)(s[i] - '0'));
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
        var sawDigit = false;
        foreach (var ch in src)
        {
            if (ch is >= '0' and <= '9')
            {
                var next = (tmp[tp] * 10) + (uint)(ch - '0');
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
        ip = (tmp[0] << 24) | (tmp[1] << 16) | (tmp[2] << 8) | tmp[3];
        return true;
    }

    /// <summary>Registers <c>DNet::resolveMasters</c> and <c>DNet::getResolvedMaster</c>.</summary>
    /// <remarks>Follows 00422ad8: up to 10 names, stopping at the first empty one. The original resolves them on a thread and returns the count at once. Here names resolve during the call, and only with <c>--allow-masters</c>. Dotted addresses always translate. <c>getResolvedMaster</c> (00422a74) returns an empty string until the resolve finished, for a wrong argument count and for an index outside 0 to 9.</remarks>
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
                var name = a[mNames.Count + 1];
                mNames.Add(name.Length > 255 ? name[..255] : name);
            }
            foreach (var name in mNames)
                mResolved.Add(ParseMasterAddress(name, out Address addr, mAllowRemote) ? addr.ToString() : "");
            mResolveDone = true;
            return Numbers.FormatInt(mNames.Count);
        });
        // 0x00422a74: "" until the resolve finished, for a wrong argument count and for an index outside 0..9.
        mIn.AddCommand("DNet::getResolvedMaster", (i, a) =>
        {
            if (!mResolveDone || a.Length != 2)
                return "";
            var idx = Numbers.BorlandAtoi(a[1]);
            if (idx >= 0 && idx < mResolved.Count)
                return mResolved[idx];
            return "";
        });
    }

    /// <summary>Starts the heartbeat when the network start of a FearCSDelegate makes the process a public server.</summary>
    /// <param name="args">The delegate arguments: the server flag, then (transport, port) pairs.</param>
    /// <param name="transportStarted">True when a transport started.</param>
    /// <param name="schedule">Schedules the later heartbeats.</param>
    /// <returns>True when the first round ran.</returns>
    /// <remarks>Follows the tail of SimNet_activate (0054e86c). The delegate must be a server (first argument starts with t, T or 1), a transport must have started, one of the pairs must be named exactly <c>IP</c> (case-sensitive compare with 0063ead5), and <c>$SinglePlayer</c> must not be true. A dedicated server and a game hosted by a client go through this same code.</remarks>
    public bool OnNetworkStart(IReadOnlyList<string> args, bool transportStarted, Schedule schedule)
    {
        var isServer = args.Count > 0 && args[0].Length > 0 && args[0][0] is 't' or 'T' or '1';
        var ip = false;
        for (var i = 1; i + 1 < args.Count; i += 2)
            ip |= string.Equals(args[i], "IP", StringComparison.Ordinal);
        if (!isServer || !transportStarted || !ip || Numbers.ParseBool(mIn.PeekVariable("$SinglePlayer")))
            return false;
        Start(schedule);
        return true;
    }

    /// <summary>Sends the first heartbeat and schedules one every 120 s. Used on a server network start that is not <c>$SinglePlayer</c>.</summary>
    /// <param name="schedule">Schedules the later heartbeats.</param>
    public void Start(Schedule schedule)
    {
        mSchedule = schedule;
        Beat();
    }

    /// <summary>Runs one heartbeat round: defaults <c>$Server::HostPublicGame</c> to TRUE, schedules the next round, and for a public server runs <c>checkMasterTranslation()</c> and sends <c>10 05 ff 00 00 00 00 00</c> to <c>$inet::Master1</c> to <c>N</c>.</summary>
    /// <remarks>R6: without the operator's opt-in only loopback masters are contacted.</remarks>
    public void Beat()
    {
        // An unset $Server::HostPublicGame becomes TRUE: a server is public unless told otherwise.
        if (mIn.GetVariable("$Server::HostPublicGame").Length == 0)
            mIn.SetVariable("$Server::HostPublicGame", "TRUE");
        mSchedule?.Invoke(kPeriod, Beat);
        if (!Numbers.ParseBool(mIn.GetVariable("$Server::HostPublicGame")))
            return;
        mIn.Evaluate("checkMasterTranslation();");
        for (var i = 1; i <= 64; ++i) // the original has no bound; the list ends at the first unset entry
        {
            var master = mIn.GetVariable("$inet::Master" + Numbers.FormatInt(i));
            if (master.Length == 0)
                break;
            // R6: only loopback masters without the operator's opt-in. Names resolve with it.
            if (ParseMasterAddress(master, out Address addr, mAllowRemote) && (mAllowRemote || (addr.Ip >> 24) == 127))
                mNet.SendConnectionless(addr, kHeartbeat);
            mIn.Print(2, "SimNET: Sending heartbeat to master " + master + ".");
        }
    }
}
