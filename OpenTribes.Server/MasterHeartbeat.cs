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
    public const float Period = 120.0f;

    /// <summary>Runs an action after a delay of sim time.</summary>
    /// <param name="delay">The delay in seconds.</param>
    /// <param name="action">The action.</param>
    public delegate void Schedule(float delay, Action action);

    private static readonly byte[] Heartbeat = [0x10, 0x05, 0xff, 0, 0, 0, 0, 0];

    private readonly Interpreter _in;
    private readonly NetServer _net;
    private readonly bool _allowRemote;
    private Schedule? _schedule;
    private readonly List<string> _names = [];    // resolveMasters arguments
    private readonly List<string> _resolved = []; // "IP:a.b.c.d:port" or ""
    private bool _resolveDone;
    private Task<string[]>? _resolveTask;         // the host name lookups of the last resolveMasters
    private readonly Func<string, uint?> _lookup;
    private readonly Dictionary<string, HostEntry> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sim time in seconds between checks for a heartbeat that waits on a host name lookup.</summary>
    public const float LookupPoll = 0.1f;

    // The lookups of one $inet::Master host name. Only the frame thread touches the fields; the lookup runs in Pending.
    private sealed class HostEntry
    {
        public bool Done { get; set; }            // a lookup finished
        public uint? Ip { get; set; }             // the result of the last finished lookup, null when it failed
        public Task<uint?>? Pending { get; set; }
    }

    /// <summary>Creates the heartbeat.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="net">The server network that sends the heartbeat.</param>
    /// <param name="allowRemote">True when the operator allows masters outside loopback (R6).</param>
    /// <param name="lookup">Looks up the first IPv4 address of a host name on a worker thread, null when there is none. Null for the system resolver.</param>
    public MasterHeartbeat(Interpreter interp, NetServer net, bool allowRemote, Func<string, uint?>? lookup = null)
    {
        _in = interp;
        _net = net;
        _allowRemote = allowRemote;
        _lookup = lookup ?? LookupHost;
    }

    // getaddrinfo(host, AF_INET, SOCK_DGRAM): the first IPv4 address. Blocks for the resolver timeout.
    private static uint? LookupHost(string host)
    {
        IPAddress[] found;
        try
        {
            found = Dns.GetHostAddresses(host, AddressFamily.InterNetwork);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            return null;
        }
        if (found.Length == 0)
            return null;
        var b = found[0].GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    // Starts a lookup off the frame thread. A lookup that throws counts as failed.
    private Task<uint?> StartLookup(string host)
    {
        var lookup = _lookup;
        return Task.Run(() =>
        {
            try
            {
                return lookup(host);
            }
            catch (Exception e) when (e is SocketException or ArgumentException or IOException)
            {
                return null;
            }
        });
    }

    /// <summary>Parses <c>IP:a.b.c.d:port</c>. The prefix is optional and matched without case.</summary>
    /// <param name="text">The address text.</param>
    /// <param name="result">Receives the address.</param>
    /// <param name="resolveNames">True to look up a host name given instead of the dotted address.</param>
    /// <returns>False when malformed.</returns>
    /// <remarks>A host name lookup blocks for the resolver timeout. The heartbeat and <c>DNet::resolveMasters</c> look names up off the frame thread instead.</remarks>
    public static bool ParseMasterAddress(string text, out Address result, bool resolveNames)
    {
        result = default;
        if (!SplitMasterAddress(text, out var host, out var port))
            return false;
        if (!InetPton4(host, out var ip))
        {
            if (!resolveNames || LookupHost(host) is not { } found)
                return false;
            ip = found;
        }
        result = new Address(ip, port);
        return true;
    }

    // The text part of ParseMasterAddress: the host and the port, without a lookup.
    private static bool SplitMasterAddress(string text, out string host, out ushort port)
    {
        host = "";
        port = 0;
        var s = text;
        if (s.Length >= 3 && (s[0] | 0x20) == 'i' && (s[1] | 0x20) == 'p' && s[2] == ':') // strncasecmp(s, "IP:", 3)
            s = s[3..];
        var colon = s.LastIndexOf(':');
        if (colon < 0 || colon == 0 || colon + 1 >= s.Length)
            return false;
        if (!Strtol(s, colon + 1, out var p) || p <= 0 || p > 65535)
            return false;
        host = s[..colon];
        port = unchecked((ushort)p);
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
    /// <remarks>Follows 00422ad8: up to 10 names, stopping at the first empty one. The original resolves them on a thread and returns the count at once. Here host names resolve on a worker thread too, and only with <c>--allow-masters</c>. Dotted addresses always translate during the call. <c>getResolvedMaster</c> (00422a74) returns an empty string until the resolve finished, for a wrong argument count and for an index outside 0 to 9.</remarks>
    public void RegisterCommands()
    {
        // 0x00422ad8: up to 10 names, stopping at the first empty one; the original resolves them on a thread and
        // returns the count at once. Host names resolve on a worker thread too, and only with --allow-masters (dotted
        // addresses translate during the call).
        _in.AddCommand("DNet::resolveMasters", (i, a) =>
        {
            _names.Clear();
            _resolved.Clear();
            _resolveDone = false;
            _resolveTask = null;
            if (a.Length < 2)
                return null; // the original returns a null result
            while (_names.Count < 10 && _names.Count + 1 < a.Length && a[_names.Count + 1].Length != 0)
            {
                var name = a[_names.Count + 1];
                _names.Add(name.Length > 255 ? name[..255] : name);
            }
            var results = new string[_names.Count];
            var lookups = new List<(int Index, string Host, ushort Port)>();
            for (var k = 0; k < _names.Count; ++k)
            {
                if (!SplitMasterAddress(_names[k], out var host, out var port))
                    results[k] = "";
                else if (InetPton4(host, out var ip))
                    results[k] = new Address(ip, port).ToString();
                else if (_allowRemote)
                    lookups.Add((k, host, port));
                else
                    results[k] = "";
            }
            if (lookups.Count == 0)
            {
                _resolved.AddRange(results);
                _resolveDone = true;
            }
            else
            {
                // Like the original, the names resolve on a worker thread and getResolvedMaster returns "" until they finished.
                var lookup = _lookup;
                _resolveTask = Task.Run(() =>
                {
                    foreach (var (index, host, port) in lookups)
                    {
                        uint? ip;
                        try
                        {
                            ip = lookup(host);
                        }
                        catch (Exception e) when (e is SocketException or ArgumentException or IOException)
                        {
                            ip = null;
                        }
                        results[index] = ip is { } v ? new Address(v, port).ToString() : "";
                    }
                    return results;
                });
            }
            return Numbers.FormatInt(_names.Count);
        });
        // 0x00422a74: "" until the resolve finished, for a wrong argument count and for an index outside 0..9.
        _in.AddCommand("DNet::getResolvedMaster", (i, a) =>
        {
            if (!_resolveDone && _resolveTask is { IsCompleted: true } t)
            {
                _resolveTask = null;
                _resolved.AddRange(t.IsCompletedSuccessfully ? t.Result : new string[_names.Count]);
                for (var k = 0; k < _resolved.Count; ++k)
                    _resolved[k] ??= "";
                _resolveDone = true;
            }
            if (!_resolveDone || a.Length != 2)
                return "";
            var idx = Numbers.BorlandAtoi(a[1]);
            if (idx >= 0 && idx < _resolved.Count)
                return _resolved[idx];
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
        if (!isServer || !transportStarted || !ip || Numbers.ParseBool(_in.PeekVariable("$SinglePlayer")))
            return false;
        Start(schedule);
        return true;
    }

    /// <summary>Sends the first heartbeat and schedules one every 120 s. Used on a server network start that is not <c>$SinglePlayer</c>.</summary>
    /// <param name="schedule">Schedules the later heartbeats.</param>
    public void Start(Schedule schedule)
    {
        _schedule = schedule;
        Beat();
    }

    /// <summary>Runs one heartbeat round: defaults <c>$Server::HostPublicGame</c> to TRUE, schedules the next round, and for a public server runs <c>checkMasterTranslation()</c> and sends <c>10 05 ff 00 00 00 00 00</c> to <c>$inet::Master1</c> to <c>N</c>.</summary>
    /// <remarks>R6: without the operator's opt-in only loopback masters are contacted.</remarks>
    public void Beat()
    {
        // An unset $Server::HostPublicGame becomes TRUE: a server is public unless told otherwise.
        if (_in.GetVariable("$Server::HostPublicGame").Length == 0)
            _in.SetVariable("$Server::HostPublicGame", "TRUE");
        _schedule?.Invoke(Period, Beat);
        if (!Numbers.ParseBool(_in.GetVariable("$Server::HostPublicGame")))
            return;
        _in.Evaluate("checkMasterTranslation();");
        for (var i = 1; i <= 64; ++i) // the original has no bound; the list ends at the first unset entry
        {
            var master = _in.GetVariable("$inet::Master" + Numbers.FormatInt(i));
            if (master.Length == 0)
                break;
            // R6: only loopback masters without the operator's opt-in. Names resolve with it, off the frame thread.
            if (SplitMasterAddress(master, out var host, out var port))
            {
                if (InetPton4(host, out var ip))
                {
                    if (_allowRemote || (ip >> 24) == 127)
                        _net.SendConnectionless(new Address(ip, port), Heartbeat);
                }
                else if (_allowRemote)
                    BeatHost(host, port);
            }
            _in.Print(2, "SimNET: Sending heartbeat to master " + master + ".");
        }
    }

    // A master given by host name. The original sends to the text address and its transport looks the name up on the
    // frame thread. Here the lookup runs on a worker: the heartbeat goes to the last address found and a new lookup
    // starts for the next round. Before the first lookup finished, the heartbeat waits and goes out on the first sim
    // check after it (every LookupPoll seconds, for at most one period).
    private void BeatHost(string host, ushort port)
    {
        if (!_hosts.TryGetValue(host, out var e))
        {
            e = new HostEntry();
            _hosts[host] = e;
        }
        Collect(e);
        e.Pending ??= StartLookup(host);
        if (e.Done)
        {
            if (e.Ip is { } ip)
                _net.SendConnectionless(new Address(ip, port), Heartbeat);
            return;
        }
        WaitForLookup(e, port, (int)(Period / LookupPoll) - 1);
    }

    private void WaitForLookup(HostEntry e, ushort port, int checksLeft) =>
        _schedule?.Invoke(LookupPoll, () =>
        {
            Collect(e);
            if (e.Done)
            {
                if (e.Ip is { } ip)
                    _net.SendConnectionless(new Address(ip, port), Heartbeat);
            }
            else if (checksLeft > 1)
                WaitForLookup(e, port, checksLeft - 1);
        });

    // Takes the result of a finished lookup.
    private static void Collect(HostEntry e)
    {
        if (e.Pending is not { IsCompleted: true } t)
            return;
        e.Pending = null;
        e.Done = true;
        e.Ip = t.IsCompletedSuccessfully ? t.Result : null;
    }
}
