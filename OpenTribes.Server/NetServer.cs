namespace OpenTribes.Server;

using System.Globalization;
using OpenTribes.Core;
using OpenTribes.Net;

/// <summary>The static settings of the server, used for query replies when no game is running.</summary>
/// <remarks>Port of src/server/net_server.hpp ServerConfig.</remarks>
public sealed class ServerConfig
{
    /// <summary>The UDP port.</summary>
    public ushort Port { get; set; } = 28001;
    /// <summary>The host name shown in the server list.</summary>
    public string Hostname { get; set; } = "TRIBES Server";
    /// <summary>The join password, empty for none.</summary>
    public string Password { get; set; } = "";
    /// <summary>The player limit as text.</summary>
    public string MaxPlayers { get; set; } = "8";
    /// <summary>The server description text.</summary>
    public string Info { get; set; } = "Default TRIBES server setup\nAdmin: Unknown\nEmail: Unknown";
    /// <summary>The loaded mods.</summary>
    public string ModList { get; set; } = "base";
    /// <summary>The mission type.</summary>
    public string MissionType { get; set; } = "";
    /// <summary>The mission name.</summary>
    public string MissionName { get; set; } = "";
    /// <summary>True for a dedicated server.</summary>
    /// <summary>True for a dedicated server.</summary>
    public bool Dedicated { get; set; } = true;
    /// <summary>True to use the LastHope 1.30 version string, threshold and upgrade message.</summary>
    public bool LastHope { get; set; }

    /// <summary>Copies the settings.</summary>
    /// <returns>The copy.</returns>
    public ServerConfig Clone() => (ServerConfig)MemberwiseClone();
}

/// <summary>The live values for query replies, read from the script state when a game is running (docs/net-protocol.md 6.1).</summary>
/// <remarks>Port of src/server/net_server.hpp GameInfo.</remarks>
public sealed class GameInfo
{
    /// <summary>The server variables the reply reports, as script text.</summary>
    public string Hostname { get; set; } = "";
    public string Password { get; set; } = "";
    public string MaxPlayers { get; set; } = "";
    public string Info { get; set; } = "";
    public string ModList { get; set; } = "";
    public string MissionType { get; set; } = "";
    public string MissionName { get; set; } = "";
    /// <summary>True for a dedicated server.</summary>
    public bool Dedicated { get; set; } = true;
    /// <summary>The CPU speed the info reply reports.</summary>
    public int CpuSpeed { get; set; }
    /// <summary>The text of <c>$CPU::estimatedSpeed</c>. Empty sends -1 in the 0x10 info.</summary>
    public string CpuText { get; set; } = "0"; // $CPU::estimatedSpeed as text; empty sends -1 in the 0x10 info
    /// <summary>The number of teams.</summary>
    public int TeamCount { get; set; } = -1;
    /// <summary>The headings of the team and client columns in the info reply.</summary>
    public string TeamHeading { get; set; } = "";
    public string ClientHeading { get; set; } = "";

    /// <summary>One team row of the info reply. Port of src/server/net_server.hpp GameInfo::TeamRow.</summary>
    public sealed class TeamRow
    {
        /// <summary>The team name and the score text as set by <c>Team::setScore</c>.</summary>
        public string Name { get; set; }
        public string Score { get; set; }
        /// <summary>The sort value. Rows sort by descending value.</summary>
        /// <summary>The sort value.</summary>
        public int Sort { get; set; }
        /// <summary>Creates a row.</summary>
        /// <param name="name">The team name.</param>
        /// <param name="score">The score text.</param>
        /// <param name="sort">The sort value.</param>
        public TeamRow(string name, string score, int sort = 0)
        {
            Name = name;
            Score = score;
            Sort = sort;
        }
    }
    /// <summary>The team rows, teams 0 to n-1.</summary>
    public List<TeamRow> Teams { get; set; } = [];

    /// <summary>The client record details of one player by player id (docs/player-manager.md 2.1). Players without an entry report the values of a fresh record.</summary>
    /// <remarks>Port of src/server/net_server.hpp GameInfo::PlayerRow.</remarks>
    public sealed class PlayerRow
    {
        /// <summary>The player id.</summary>
        /// <summary>The player record id.</summary>
        public uint Id { get; set; }
        /// <summary>The team.</summary>
        public int Team { get; set; } = -1;
        /// <summary>The score text as set by <c>Client::setScore</c>.</summary>
        public string Score { get; set; } = ""; // score text as set by Client::setScore
        /// <summary>The sort value.</summary>
        public int Sort { get; set; }
        /// <summary>The ping in milliseconds, as stored by the 1.5 s update.</summary>
        public uint Ping { get; set; }
        /// <summary>The packet loss.</summary>
        public float Loss { get; set; }
    }
    /// <summary>The player rows.</summary>
    public List<PlayerRow> Players { get; set; } = [];
}

/// <summary>A connected player. Port of src/server/net_server.hpp Player.</summary>
public sealed class Player
{
    /// <summary>The player record id.</summary>
    public uint Id { get; set; }
    /// <summary>The player name.</summary>
    public string Name { get; set; } = "";
    /// <summary>The voice set.</summary>
    public string Voice { get; set; } = "";
    /// <summary>True for a male player.</summary>
    public bool Male { get; set; }
    /// <summary>The player's connection.</summary>
    public Connection? Connection { get; set; }
}

/// <summary>The game-layer callbacks for accepted connections: type-0 data, notify, close and tick. Port of src/server/net_server.hpp NetHooks.</summary>
public sealed class NetHooks
{
    /// <summary>Called when a connection is established.</summary>
    public Action<Player, Connection>? OnAccepted { get; set; }
    /// <summary>Called with the payload of a data packet.</summary>
    public Action<Player, byte[]>? OnData { get; set; }
    /// <summary>Called with the cookie of a sent packet and whether it was delivered.</summary>
    public Action<Player, uint, bool>? OnNotify { get; set; }
    /// <summary>Called when a player's connection ends.</summary>
    public Action<Player>? OnClosed { get; set; }
    /// <summary>Called every network step with the clock in milliseconds.</summary>
    public Action<uint>? OnTick { get; set; }
}

/// <summary>The server networking: the socket, connectionless queries and the connect handshake.</summary>
/// <remarks>Port of src/server/net_server.cpp. The original has the query handler at 0042a4cc, the connect handler at 0042ad6c and the DNet info at 0054f608.</remarks>
public sealed class NetServer : ConnectionEvents, IDisposable
{
    private const uint FirstPlayerId = 2049;   // first player record id observed on the original
    private const uint ServerManagerId = 2048; // server SimManager id sent in the accept
    private const int MaxNameLength = 16;
    private const string GameType = "Tribes";

    private readonly ServerConfig _config;
    private readonly string _version;
    private readonly int _minMinorVersion;
    private readonly string _upgradeMessage;
    private readonly ushort _packetVersion;

    private Func<GameInfo>? _gameInfo;
    private NetHooks _hooks = new();
    private readonly UdpSocket _socket = new();
    private SendFn? _sendOverride;
    private Action<string> _log;
    private Action<int, string>? _leveledLog;
    private readonly BanList _bans = new();
    private readonly SortedDictionary<Address, Connection> _connections = [];
    private readonly List<Player> _players = [];
    // Player record ids are reused last-freed-first; unused ids follow in order.
    private readonly List<uint> _freePlayerIds = [];
    private uint _nextFreshPlayerId;
    private uint _nextConnectionId;
    private uint _now;

    private static string Str(int v) => v.ToString(CultureInfo.InvariantCulture);

    // glibc atoi: strtol (white space, sign, decimal digits, saturating at the long range), cast to int.
    internal static int CAtoi(string s)
    {
        var i = 0;
        while (i < s.Length && (s[i] == ' ' || (s[i] >= '\t' && s[i] <= '\r')))
            ++i;
        var negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            negative = s[i++] == '-';
        long v = 0;
        var overflow = false;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            var d = s[i++] - '0';
            if (v > (long.MaxValue - d) / 10)
                overflow = true;
            else
                v = (v * 10) + d;
        }
        if (overflow)
            v = negative ? long.MinValue : long.MaxValue;
        else if (negative)
            v = -v;
        return unchecked((int)v);
    }

    // Appends a length-prefixed string clipped to maxLen (0x00588d3c).
    private static void PutString(List<byte> output, string s, int maxLen)
    {
        var n = Math.Min(s.Length, maxLen);
        output.Add((byte)n);
        for (var i = 0; i < n; ++i)
            output.Add((byte)s[i]);
    }

    /// <summary>Creates the server. The LastHope setting selects the 1.30 or 1.11 version string and upgrade message.</summary>
    /// <param name="config">The settings, copied.</param>
    /// <remarks>The packet version is the version string's part before the first <c>.</c> (0042b73c). Session connection ids are seeded from the clock (0058b890).</remarks>
    public NetServer(ServerConfig config)
    {
        _config = config.Clone();
        _nextFreshPlayerId = FirstPlayerId;
        if (config.LastHope)
        {
            _version = "1.30";
            _minMinorVersion = 30;
            _upgradeMessage =
                "You must upgrade your executable to play on this server (requires version 1.30). " +
                "Check www.lasthope.us for the most recent patches.";
        }
        else
        {
            _version = "1.11";
            _minMinorVersion = 11;
            _upgradeMessage =
                "You must upgrade your executable to play on this server (requires version 1.11 or higher).  " +
                "Check www.tribesplayers.com for the most recent patches.";
        }
        // Packet version: the version string's part before the first '.' (0x0042b73c).
        _packetVersion = (ushort)CAtoi(_version);
        // Session connection ids are seeded from the tick count (0x0058b890).
        _nextConnectionId = unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _log = StdIo.Out;
    }

    /// <summary>Opens the socket on the configured port.</summary>
    /// <returns>True on success.</returns>
    public bool Open() => _socket.Open(_config.Port);

    /// <summary>True when the server is open on the in-process loopback transport only.</summary>
    public bool LoopbackOnly { get; private set; }

    /// <summary>Opens the socket on a port.</summary>
    /// <param name="port">The port, or 0 for any.</param>
    /// <param name="loopbackOnly">True to use the in-process loopback transport.</param>
    /// <returns>True on success.</returns>
    public bool Open(ushort port, bool loopbackOnly = false)
    {
        LoopbackOnly = loopbackOnly;
        _config.Port = port;
        return loopbackOnly ? _socket.OpenMemory(port) : _socket.Open(port, false);
    }

    /// <summary>Closes the socket.</summary>
    public void Close() => _socket.Close();

    /// <summary>Tells whether the socket is open.</summary>
    /// <returns>True when open.</returns>
    public bool IsOpen() => _socket.Port() != 0;
    /// <summary>Sets the source of live query reply values. Without one the static config answers.</summary>
    /// <param name="provider">The provider, or null.</param>
    public void SetGameInfo(Func<GameInfo>? provider) => _gameInfo = provider;
    /// <summary>Sets the game-layer callbacks.</summary>
    /// <param name="hooks">The callbacks.</param>
    public void SetHooks(NetHooks hooks) => _hooks = hooks;
    /// <summary>Gets the ban list.</summary>
    /// <returns>The list.</returns>
    public BanList Bans() => _bans;
    /// <summary>Sets a log with print levels, where 1 is <c>printf</c> and n is <c>dbprintf(n)</c>. It replaces <see cref="SetLog"/> when set.</summary>
    /// <param name="log">The log, or null.</param>
    public void SetLeveledLog(Action<int, string>? log) => _leveledLog = log;
    /// <summary>Replaces the socket for outgoing datagrams. A test seam.</summary>
    /// <param name="send">The send function.</param>
    public void SetSendFunction(SendFn send) => _sendOverride = send;
    /// <summary>Sets the console output. The default is stdout.</summary>
    /// <param name="log">The log.</param>
    public void SetLog(Action<string> log) => _log = log;
    /// <summary>Sends one datagram outside any connection, for the master heartbeat.</summary>
    /// <param name="to">The destination.</param>
    /// <param name="data">The payload.</param>
    public void SendConnectionless(Address to, ReadOnlySpan<byte> data) => Send(to, data);
    /// <summary>Gets the connected players.</summary>
    /// <returns>The players.</returns>
    public IReadOnlyList<Player> Players() => _players;
    /// <summary>Gets the bound port.</summary>
    /// <returns>The port.</returns>
    public ushort Port() => _socket.Port();

    // src/server/net_server.cpp NetServer::currentInfo.
    private GameInfo CurrentInfo()
    {
        if (_gameInfo != null)
            return _gameInfo();
        return new GameInfo
        {
            Hostname = _config.Hostname,
            Password = _config.Password,
            MaxPlayers = _config.MaxPlayers,
            Info = _config.Info,
            ModList = _config.ModList,
            MissionType = _config.MissionType,
            MissionName = _config.MissionName,
            Dedicated = _config.Dedicated,
        };
    }

    // src/server/net_server.cpp NetServer::maxPlayers.
    private int MaxPlayers()
    {
        var max = CurrentInfo().MaxPlayers;
        var n = max.Length == 0 ? 32 : CAtoi(max);
        return Math.Min(n, 128);
    }

    /// <summary>Drains the socket, then runs connection timeouts.</summary>
    /// <param name="nowMs">The clock in milliseconds.</param>
    public void Poll(uint nowMs)
    {
        Drain(nowMs);
        _hooks.OnTick?.Invoke(nowMs);
        TickConnections(nowMs);
    }

    /// <summary>Runs the first network step of a server frame: the socket and the connection timers (docs/sim.md TK2).</summary>
    /// <param name="nowMs">The clock in milliseconds.</param>
    /// <remarks>Follows 0058bb24. <see cref="SendPackets"/> runs after the ticks.</remarks>
    public void ReceivePackets(uint nowMs)
    {
        Drain(nowMs);
        TickConnections(nowMs);
    }

    /// <summary>Runs the last network step of a server frame: the game layer writes one packet per connection that is due.</summary>
    /// <param name="nowMs">The clock in milliseconds.</param>
    /// <remarks>Follows 00550d2c.</remarks>
    public void SendPackets(uint nowMs)
    {
        _now = nowMs;
        _hooks.OnTick?.Invoke(nowMs);
    }

    // src/server/net_server.cpp NetServer::drain.
    private void Drain(uint nowMs)
    {
        _now = nowMs;
        var buf = new byte[0x5f4];
        int n;
        while ((n = _socket.Receive(out var from, buf)) > 0)
            Receive(from, buf.AsSpan(0, n), nowMs);
    }

    // src/server/net_server.cpp NetServer::tickConnections.
    private void TickConnections(uint nowMs)
    {
        foreach (var key in _connections.Keys.ToList())
        {
            if (!_connections.TryGetValue(key, out var c))
                continue;
            if (!c.Dead() && c.State() != ConnectionState.Idle)
                c.Tick(nowMs);
            if (c.Dead() || c.State() == ConnectionState.Idle)
                _connections.Remove(key);
        }
    }

    /// <summary>Handles one received datagram. <see cref="Poll"/> calls it for each one. Public for tests.</summary>
    /// <param name="from">The sender.</param>
    /// <param name="data">The datagram.</param>
    /// <param name="nowMs">The clock in milliseconds.</param>
    public void Receive(Address from, ReadOnlySpan<byte> data, uint nowMs)
    {
        _now = nowMs;
        if (data.Length == 0)
            return;
        // Self-echo of our own broadcasts.
        if (from.Ip == 0x7f000001 && from.Port == _socket.Port())
            return;
        if ((data[0] & 1) == 0)
        {
            HandleConnectionless(from, data);
            return;
        }
        if (!_connections.TryGetValue(from, out var slot))
        {
            slot = new Connection(from, _nextConnectionId++, this, Send, nowMs);
            _connections[from] = slot;
        }
        slot.Receive(data, nowMs);
    }

    // src/server/net_server.cpp NetServer::send.
    private void Send(Address to, ReadOnlySpan<byte> data)
    {
        if (_sendOverride != null)
            _sendOverride(to, data);
        else
            _socket.Send(to, data);
    }

    // src/server/net_server.cpp NetServer::log.
    private void Log(int level, string line)
    {
        if (_leveledLog != null)
            _leveledLog(level, line);
        else if (level <= 1)
            _log(line);
    }

    // src/server/net_server.cpp NetServer::handleConnectionless.
    private void HandleConnectionless(Address from, ReadOnlySpan<byte> data)
    {
        // 0x0042a4cc: nothing is answered to a banned address (docs/server-ops.md 4.4).
        if (_bans.IsBanned(from.ToString()))
        {
            Log(2, "SimNET: ignoring GI from banned address " + from.ToString());
            return;
        }
        if (data[0] == 0x60)
        {
            ReplyTribesQuery(from, data, false);
        }
        else if (data[0] == 0x62 && data.Length == 3)
        {
            ReplyTribesQuery(from, data, true);
        }
        else if (data[0] == 0x10 && data.Length >= 2)
        {
            // 0x0054f608: a server answers types 3 and 7 only. Short packets are answered too.
            if (data[1] == 3)
                ReplyDnetPing(from, data);
            else if (data[1] == 7)
                ReplyDnetInfo(from, data);
        }
    }

    // src/server/net_server.cpp NetServer::playerRows: players newest first (the human list order,
    // docs/player-manager.md 2), each with its client record details.
    private List<GameInfo.PlayerRow> PlayerRows(GameInfo g)
    {
        var rows = new List<GameInfo.PlayerRow>();
        for (var k = _players.Count - 1; k >= 0; --k)
        {
            var row = new GameInfo.PlayerRow { Id = _players[k].Id };
            foreach (var r in g.Players)
                if (r.Id == _players[k].Id)
                    row = r;
            rows.Add(row);
        }
        return rows;
    }

    // src/server/net_server.cpp NetServer::replyTribesQuery.
    private void ReplyTribesQuery(Address from, ReadOnlySpan<byte> data, bool info)
    {
        // The reply stream holds 0x578 bytes (0x0042a4cc). A field that does not fit ends the
        // reply here (D11; the original's behavior past the end is not verified).
        const int kLimit = 0x578;
        var output = new List<byte>();
        var full = false;
        void Byte(byte b)
        {
            full = full || output.Count + 1 > kLimit;
            if (!full)
                output.Add(b);
        }
        void StrField(string s, int maxLen)
        {
            full = full || output.Count + 1 + Math.Min(s.Length, maxLen) > kLimit;
            if (!full)
                PutString(output, s, maxLen);
        }
        Byte(info ? (byte)0x63 : (byte)0x61);
        Byte(data.Length > 1 ? data[1] : (byte)0);
        Byte(data.Length > 2 ? data[2] : (byte)0);
        Byte(0x62);
        StrField(GameType, 255);
        StrField(_version, 255);
        var g = CurrentInfo();
        StrField(g.Hostname, 32);
        Byte(g.Dedicated ? (byte)1 : (byte)0);
        Byte(g.Password.Length == 0 ? (byte)0 : (byte)1);
        Byte((byte)_players.Count);
        Byte(unchecked((byte)CAtoi(g.MaxPlayers)));
        if (info)
        {
            Byte(unchecked((byte)g.CpuSpeed));
            Byte(unchecked((byte)(g.CpuSpeed >> 8)));
            StrField(g.ModList, 32);
            StrField(g.MissionType, 8);
            StrField(g.MissionName, 16);
            StrField(g.Info, 255);
            Byte(unchecked((byte)g.TeamCount));
            StrField(g.TeamHeading, 64);
            StrField(g.ClientHeading, 64);
            for (var t = 0; t < g.TeamCount && t < g.Teams.Count; ++t)
            {
                StrField(g.Teams[t].Name, 32);
                StrField(g.Teams[t].Score, 32);
            }
            // Per player: ping/4, loss*100, team, name, raw score text (no % expansion here).
            var rows = PlayerRows(g);
            for (var i = 0; i < rows.Count; ++i)
            {
                Byte(unchecked((byte)(rows[i].Ping >> 2)));
                Byte(unchecked((byte)(int)(rows[i].Loss * 100.0f)));
                Byte(unchecked((byte)rows[i].Team));
                StrField(_players[_players.Count - 1 - i].Name, 18);
                StrField(rows[i].Score, 32);
            }
        }
        Send(from, output.ToArray());
    }

    // src/server/net_server.cpp NetServer::replyDnetPing.
    private void ReplyDnetPing(Address from, ReadOnlySpan<byte> data)
    {
        // Key bytes missing from a short request read as zero.
        var output = new byte[40];
        byte[] head = [ 0x10, 0x04, 0xff, 0xf0, data.Length > 4 ? data[4] : (byte)0, data.Length > 5 ? data[5] : (byte)0,
                        0x20, (byte)_players.Count, (byte)_packetVersion, (byte)(_packetVersion >> 8) ];
        head.CopyTo(output, 0);
        // strncpy of 29 characters, then the last byte is 0.
        var host = CurrentInfo().Hostname;
        for (var i = 0; i < 29 && i < host.Length && host[i] != '\0'; ++i)
            output[10 + i] = (byte)host[i];
        output[39] = 0;
        Send(from, output);
        Log(3, "SimNET: Got ping request from " + from.ToString());
    }

    // src/server/net_server.cpp NetServer::replyDnetInfo: 0x10 type 8 game info (0x0054f608 case 7, payload 0x0042bcec).
    // docs/server-ops.md 2.
    private void ReplyDnetInfo(Address from, ReadOnlySpan<byte> data)
    {
        Log(3, "SimNET: Got server info request from " + from.ToString() + ".");
        const int kPayload = 0x5d4;
        const int kRowMax = 1023; // the original's row buffer is 1024 bytes, unchecked (D11)
        var payload = new byte[kPayload];
        byte[] header = [0x10, 0x08, 0xff, 0x00, data.Length > 4 ? data[4] : (byte)0, data.Length > 5 ? data[5] : (byte)0, 0, 0];
        var bs = new BitBuffer(payload, kPayload);
        var g = CurrentInfo();
        var players = PlayerRows(g);
        var names = new List<string>();
        for (var k = _players.Count - 1; k >= 0; --k)
            names.Add(_players[k].Name);

        byte[] pv = [(byte)_packetVersion, (byte)(_packetVersion >> 8)];
        bs.WriteBytes(pv);
        bs.WriteString(GameType);
        bs.WriteString(_version);
        bs.WriteString(g.Hostname.Length == 0 ? "N/A" : g.Hostname);
        var b = (byte)_players.Count;
        bs.WriteBytes([b]);
        // Decimal parse in a byte with no digit check.
        b = 0;
        foreach (var c in g.MaxPlayers)
            b = unchecked((byte)((b * 10) + (byte)c - 0x30));
        bs.WriteBytes([b]);
        bs.WriteString(g.MissionName.Length == 0 ? "N/A" : g.MissionName);
        bs.WriteBytes([g.Dedicated ? (byte)1 : (byte)0]);
        bs.WriteBytes([g.Password.Length == 0 ? (byte)0 : (byte)1]);
        var cpu = g.CpuText.Length == 0 ? -1 : g.CpuSpeed;
        bs.WriteBytes([(byte)cpu, (byte)(cpu >> 8), (byte)(cpu >> 16), (byte)(cpu >> 24)]);
        bs.WriteString(g.ModList);
        bs.WriteString(g.MissionType);
        bs.WriteString(g.Info);
        var remaining = kPayload - bs.BytePosition();

        var hasTeams = g.TeamHeading.Length != 0;
        var playerCount = players.Count;
        var rowCount = hasTeams ? g.TeamCount + playerCount + 2 : playerCount + 1;
        // Teams and players sorted by descending sort value with the C runtime qsort.
        var teams = new List<GameInfo.TeamRow>();
        if (hasTeams)
            for (var t = 0; t < g.TeamCount && t < g.Teams.Count; ++t)
                teams.Add(g.Teams[t]);
        GameInfo.TeamRow[] teamArray = [.. teams];
        if (teamArray.Length != 0)
            Borland.BorlandQsort(teamArray, (x, c) => unchecked((int)((uint)c.Sort - (uint)x.Sort)));
        var order = new int[players.Count];
        for (var i = 0; i < order.Length; ++i)
            order[i] = i;
        if (order.Length != 0)
            Borland.BorlandQsort(order, (x, c) => unchecked((int)((uint)players[c].Sort - (uint)players[x].Sort)));
        var teamRows = teamArray.Length;
        string TeamName(int t) => t >= 0 && t < g.Teams.Count ? g.Teams[t].Name : "";

        bs.WriteString(g.TeamHeading);
        bs.WriteString(g.ClientHeading);
        for (var i = 0; i < rowCount; ++i)
        {
            var row = new System.Text.StringBuilder();
            if (i == 0 && hasTeams)
            {
                row.Append("01" + g.TeamHeading);
            }
            else if (i < teamRows + 1 && hasTeams)
            {
                var t = teamArray[i - 1];
                row.Append("00");
                for (var k = 0; k < t.Score.Length && row.Length < kRowMax; ++k)
                    if (t.Score[k] == '%' && k + 1 < t.Score.Length && t.Score[k + 1] == 't')
                    {
                        row.Append(t.Name);
                        ++k;
                    }
                    else
                    {
                        row.Append(t.Score[k]);
                    }
            }
            else if (!(hasTeams && i == teamRows + 1) && !(!hasTeams && i == 0))
            {
                var idx = i - teamRows - (hasTeams ? 2 : 1);
                if (idx < 0 || idx >= playerCount)
                    break; // only when the team count and the team list disagree
                var p = players[order[idx]];
                var text = p.Score;
                row.Append("10");
                for (var k = 0; k < text.Length && row.Length < kRowMax; ++k)
                {
                    var next = text[k] == '%' && k + 1 < text.Length ? text[k + 1] : '\0';
                    if (next == 'n')
                        row.Append(names[order[idx]]);
                    else if (next == 't')
                        row.Append(p.Team == -1 ? "" : TeamName(p.Team));
                    else if (next == 'p')
                        row.Append(Str(unchecked((int)p.Ping)));
                    else if (next == 'l')
                        row.Append(Str((int)(p.Loss * 100.0f)));
                    else
                    {
                        row.Append(text[k]);
                        continue;
                    }
                    ++k;
                }
            }
            else
            {
                row.Append("11" + g.ClientHeading);
            }
            if (row.Length > kRowMax)
                row.Length = kRowMax;
            var rowText = row.ToString();
            var len = rowText.Length;
            if (remaining < len)
                break;
            if (rowText[1] == '1')
            {
                // A heading row is preceded by an empty team row.
                bs.WriteString("00");
                remaining -= 2;
            }
            bs.WriteString(rowText);
            remaining -= len;
        }
        var output = new byte[8 + bs.BytePosition()];
        header.CopyTo(output, 0);
        Array.Copy(payload, 0, output, 8, bs.BytePosition());
        Send(from, output);
    }

    /// <summary>Builds the <c>listPlayers</c> output (FearPlugin id 9): <c>Server: &lt;n&gt; clients</c>, then one line per player, newest first: <c>&lt;id&gt; (&lt;address&gt;): &lt;name&gt; - &lt;score text&gt;</c>.</summary>
    /// <returns>The lines.</returns>
    public List<string> PlayerList()
    {
        var output = new List<string> { "Server: " + Str(_players.Count) + " clients" };
        var rows = PlayerRows(CurrentInfo());
        for (var i = 0; i < rows.Count; ++i)
        {
            var p = _players[_players.Count - 1 - i];
            output.Add(p.Id.ToString(CultureInfo.InvariantCulture) + " (" + (p.Connection != null ? p.Connection.Peer().ToString() : "") +
                       "): " + p.Name + " - " + rows[i].Score);
        }
        return output;
    }

    /// <summary>Finds a player by id.</summary>
    /// <param name="id">The player id.</param>
    /// <returns>The player, or null.</returns>
    public Player? PlayerById(uint id)
    {
        foreach (var p in _players)
            if (p.Id == id)
                return p;
        return null;
    }

    /// <summary>Kicks a player: closes the game side, sends a disconnect that carries the reason (NUL included) and frees the player id.</summary>
    /// <param name="playerId">The player id.</param>
    /// <param name="reason">The reason, or null for the default text.</param>
    /// <returns>False when no player has that id.</returns>
    /// <remarks>Follows Net::kick (0042babc).</remarks>
    public bool Kick(uint playerId, string? reason)
    {
        var p = PlayerById(playerId);
        if (p == null)
            return false;
        var c = p.Connection;
        _hooks.OnClosed?.Invoke(p);
        c?.Close(Connection.Disconnect, reason ?? "You were kicked from the game.", _now);
        _freePlayerIds.Add(playerId);
        _players.RemoveAll(q => q.Id == playerId);
        return true;
    }

    /// <summary>Drops a player after an invalid client packet (docs/game-stream.md 1.6): the game side closes, the client record is removed and the connection is deleted, which sends a disconnect without a payload.</summary>
    /// <param name="playerId">The player id.</param>
    /// <returns>False when no player has that id.</returns>
    /// <remarks>Follows the SimNet error delegate (0042b7f8).</remarks>
    public bool DropInvalid(uint playerId)
    {
        var p = PlayerById(playerId);
        if (p == null)
            return false;
        var c = p.Connection;
        _hooks.OnClosed?.Invoke(p);
        c?.Close(Connection.Disconnect, null, _now);
        _freePlayerIds.Add(playerId);
        _players.RemoveAll(q => q.Id == playerId);
        return true;
    }

    /// <summary>Allocates a player record id (docs/player-manager.md 2). Connections and AI players share one pool. Ids are reused last-freed-first and unused ids follow in order.</summary>
    /// <returns>The id.</returns>
    public uint AllocatePlayerId()
    {
        if (_freePlayerIds.Count == 0)
            return _nextFreshPlayerId++;
        var id = _freePlayerIds[^1];
        _freePlayerIds.RemoveAt(_freePlayerIds.Count - 1);
        return id;
    }

    /// <summary>Returns a player record id to the pool.</summary>
    /// <param name="id">The id.</param>
    public void ReleasePlayerId(uint id) => _freePlayerIds.Add(id);

    // src/server/net_server.cpp NetServer::uniqueName.
    private string UniqueName(string name)
    {
        if (name.Length > MaxNameLength)
            name = name[..MaxNameLength];
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; ++i)
        {
            var c = chars[i];
            if (c is < (char)0x20 or > (char)0x7f or '\\' or '"' or '~')
                chars[i] = '*';
        }
        name = new string(chars);
        bool Taken(string n) => _players.Any(p => p.Name == n);
        var candidate = name;
        for (var i = 1; Taken(candidate); ++i)
            candidate = name + "." + Str(i);
        return candidate;
    }

    private static bool StrCaseEq(string a, string b)
    {
        a = CStr(a);
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; ++i)
        {
            var x = a[i] is >= 'A' and <= 'Z' ? (char)(a[i] + 0x20) : a[i];
            var y = b[i] is >= 'A' and <= 'Z' ? (char)(b[i] + 0x20) : b[i];
            if (x != y)
                return false;
        }
        return true;
    }

    private static string CStr(string s)
    {
        var nul = s.IndexOf('\0');
        return nul < 0 ? s : s[..nul];
    }

    /// <summary>Handles a connect request: checks the ban list, the packet version, the game type, the game version, the password and the player limit, then accepts or rejects.</summary>
    /// <param name="c">The connection.</param>
    /// <param name="payload">The request payload.</param>
    /// <remarks>Follows 0042ad6c. A banned address gets no answer and its connection is removed.</remarks>
    public override void OnConnectRequest(Connection c, ReadOnlySpan<byte> payload)
    {
        // 0x0042ad6c: a banned address gets no answer; its VC is removed.
        if (_bans.IsBanned(c.Peer().ToString()))
        {
            Log(2, "SimNET: ignoring connect from banned address " + c.Peer().ToString());
            c.Drop();
            return;
        }
        var buf = payload.ToArray();
        var input = new BitBuffer(buf, buf.Length);
        var pv = new byte[2];
        input.ReadBytes(pv);
        var packetVersion = (ushort)(pv[0] | (pv[1] << 8));

        if (packetVersion != _packetVersion)
        {
            c.Close(Connection.Reject, "Wrong packet version.", _now);
            return;
        }
        if (input.ReadString() != GameType)
        {
            c.Close(Connection.Reject, "Wrong game type.", _now);
            return;
        }

        var clientVersion = input.ReadString();
        var dot = _version.IndexOf('.');
        var majorLen = dot is < 0 or 0 ? 0 : dot - 1; // 0 for "1.11"
        var clientMajor = clientVersion[..Math.Min(majorLen, clientVersion.Length)];
        var ourMajor = _version[..Math.Min(majorLen, _version.Length)];
        if (clientMajor != ourMajor)
        {
            c.Close(Connection.Reject, "Wrong game version.", _now);
            return;
        }
        var minor = 0;
        var clientDot = clientVersion.IndexOf('.');
        if (clientDot >= 0)
        {
            var digits = clientVersion[(clientDot + 1)..];
            minor = CAtoi(CStr(digits.Length > 2 ? digits[..2] : digits));
        }
        if (minor < _minMinorVersion)
        {
            c.Close(Connection.Reject, _upgradeMessage, _now);
            return;
        }

        var password = input.ReadString();
        var serverPassword = CurrentInfo().Password;
        if (serverPassword.Length != 0 && password != serverPassword)
        {
            c.Close(Connection.Reject, "Wrong password.", _now);
            return;
        }
        if (_players.Count >= MaxPlayers())
        {
            c.Close(Connection.Reject, "Server is full.", _now);
            return;
        }

        var name = UniqueName(input.ReadString());
        var gender = input.ReadString();
        var voice = input.ReadString();
        var male = StrCaseEq(gender, "male");

        var id = AllocatePlayerId();
        var accept = new byte[8];
        var output = new BitBuffer(accept, accept.Length);
        output.WriteInt(id, 32);
        output.WriteInt(ServerManagerId, 32);
        c.Send(Connection.Accept, accept, _now);
        _players.Add(new Player { Id = id, Name = name, Voice = voice, Male = male, Connection = c });
    }

    /// <summary>Logs the oversized ack-range list on 1.30 (docs/lasthope-1.30.md L2, DECISIONS D11).</summary>
    /// <param name="c">The connection.</param>
    public override void OnAckRangeOverflow(Connection c)
    {
        if (_config.LastHope)
            _log("Crash Attempt: " + c.Peer().ToString());
    }

    // src/server/net_server.cpp NetServer::playerFor.
    private Player? PlayerFor(Connection c)
    {
        foreach (var p in _players)
            if (p.Connection == c)
                return p;
        return null;
    }

    /// <summary>Starts the game stream once the accept was acknowledged (docs/game-stream.md 5.1).</summary>
    /// <param name="c">The connection.</param>
    public override void OnEstablished(Connection c)
    {
        var p = PlayerFor(c);
        if (p != null)
            _hooks.OnAccepted?.Invoke(p, c);
    }

    /// <summary>Passes a data packet to the game layer.</summary>
    /// <param name="c">The connection.</param>
    /// <param name="payload">The payload.</param>
    public override void OnData(Connection c, ReadOnlySpan<byte> payload)
    {
        var p = PlayerFor(c);
        if (p != null)
            _hooks.OnData?.Invoke(p, payload.ToArray());
    }

    /// <summary>Passes a delivery notification to the game layer.</summary>
    /// <param name="c">The connection.</param>
    /// <param name="cookie">The cookie of the sent packet.</param>
    /// <param name="delivered">True when the peer acknowledged it.</param>
    public override void OnNotify(Connection c, uint cookie, bool delivered)
    {
        var p = PlayerFor(c);
        if (p != null)
            _hooks.OnNotify?.Invoke(p, cookie, delivered);
    }

    /// <summary>Tells the game layer, frees the player id and removes the player when a connection ends.</summary>
    /// <param name="c">The connection.</param>
    /// <param name="reason">The disconnect reason bytes.</param>
    /// <param name="timedOut">True on a timeout or replacement.</param>
    public override void OnClosed(Connection c, ReadOnlySpan<byte> reason, bool timedOut)
    {
        var p = PlayerFor(c);
        if (p != null)
            _hooks.OnClosed?.Invoke(p);
        foreach (var q in _players)
            if (q.Connection == c)
                _freePlayerIds.Add(q.Id);
        _players.RemoveAll(q => q.Connection == c);
    }

    /// <inheritdoc/>
    public void Dispose() => _socket.Dispose();
}
