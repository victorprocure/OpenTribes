// Port of src/server/net_server.hpp and src/server/net_server.cpp: dedicated server networking, connectionless
// queries and the connect handshake. Original: query handler 0x0042a4cc, connect handler 0x0042ad6c, DNet info 0x0054f608.
using System.Globalization;
using OpenTribes.Core;
using OpenTribes.Net;

namespace OpenTribes.Server;

// src/server/net_server.hpp struct ServerConfig.
public sealed class ServerConfig
{
    public ushort Port = 28001;
    public string Hostname = "TRIBES Server";
    public string Password = "";
    public string MaxPlayers = "8";
    public string Info = "Default TRIBES server setup\nAdmin: Unknown\nEmail: Unknown";
    public string ModList = "base";
    public string MissionType = "";
    public string MissionName = "";
    public bool Dedicated = true;
    public bool LastHope; // 1.30 version string, threshold and upgrade message

    public ServerConfig Clone() => (ServerConfig)MemberwiseClone();
}

// src/server/net_server.hpp struct GameInfo: live values for query replies, read from the script state when a game is
// running (docs/net-protocol.md 6.1).
public sealed class GameInfo
{
    public string Hostname = "", Password = "", MaxPlayers = "", Info = "", ModList = "", MissionType = "", MissionName = "";
    public bool Dedicated = true;
    public int CpuSpeed;
    public string CpuText = "0"; // $CPU::estimatedSpeed as text; empty sends -1 in the 0x10 info
    public int TeamCount = -1;
    public string TeamHeading = "", ClientHeading = "";

    // src/server/net_server.hpp GameInfo::TeamRow.
    public sealed class TeamRow
    {
        public string Name, Score; // score text as set by Team::setScore
        public int Sort;
        public TeamRow(string name, string score, int sort = 0)
        {
            Name = name;
            Score = score;
            Sort = sort;
        }
    }
    public List<TeamRow> Teams = new(); // teams 0..n-1

    // src/server/net_server.hpp GameInfo::PlayerRow: client record details by player id (docs/player-manager.md 2.1).
    // Players without an entry report the values of a fresh record.
    public sealed class PlayerRow
    {
        public uint Id;
        public int Team = -1;
        public string Score = ""; // score text as set by Client::setScore
        public int Sort;
        public uint Ping;         // ms, as stored by the 1.5 s update
        public float Loss;
    }
    public List<PlayerRow> Players = new();
}

// src/server/net_server.hpp struct Player.
public sealed class Player
{
    public uint Id;
    public string Name = "";
    public string Voice = "";
    public bool Male;
    public Connection? Connection;
}

// src/server/net_server.hpp struct NetHooks: game-layer callbacks for accepted connections (type-0 data, notify, close, tick).
public sealed class NetHooks
{
    public Action<Player, Connection>? OnAccepted;
    public Action<Player, byte[]>? OnData;
    public Action<Player, uint, bool>? OnNotify;
    public Action<Player>? OnClosed;
    public Action<uint>? OnTick;
}

public sealed class NetServer : ConnectionEvents
{
    private const uint kFirstPlayerId = 2049;   // first player record id observed on the original
    private const uint kServerManagerId = 2048; // server SimManager id sent in the accept
    private const int kMaxNameLength = 16;
    private const string kGameType = "Tribes";

    private ServerConfig mConfig;
    private readonly string mVersion;
    private readonly int mMinMinorVersion;
    private readonly string mUpgradeMessage;
    private readonly ushort mPacketVersion;

    private Func<GameInfo>? mGameInfo;
    private NetHooks mHooks = new();
    private readonly UdpSocket mSocket = new();
    private SendFn? mSendOverride;
    private Action<string> mLog;
    private Action<int, string>? mLeveledLog;
    private readonly BanList mBans = new();
    private readonly SortedDictionary<Address, Connection> mConnections = new();
    private readonly List<Player> mPlayers = new();
    // Player record ids are reused last-freed-first; unused ids follow in order.
    private readonly List<uint> mFreePlayerIds = new();
    private uint mNextFreshPlayerId;
    private uint mNextConnectionId;
    private uint mNow;

    private static string Str(int v) => v.ToString(CultureInfo.InvariantCulture);

    // glibc atoi: strtol (white space, sign, decimal digits, saturating at the long range), cast to int.
    internal static int CAtoi(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || (s[i] >= '\t' && s[i] <= '\r')))
            ++i;
        bool negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            negative = s[i++] == '-';
        long v = 0;
        bool overflow = false;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            int d = s[i++] - '0';
            if (v > (long.MaxValue - d) / 10)
                overflow = true;
            else
                v = v * 10 + d;
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
        int n = Math.Min(s.Length, maxLen);
        output.Add((byte)n);
        for (int i = 0; i < n; ++i)
            output.Add((byte)s[i]);
    }

    // src/server/net_server.cpp NetServer::NetServer.
    public NetServer(ServerConfig config)
    {
        mConfig = config.Clone();
        mNextFreshPlayerId = kFirstPlayerId;
        if (config.LastHope)
        {
            mVersion = "1.30";
            mMinMinorVersion = 30;
            mUpgradeMessage =
                "You must upgrade your executable to play on this server (requires version 1.30). " +
                "Check www.lasthope.us for the most recent patches.";
        }
        else
        {
            mVersion = "1.11";
            mMinMinorVersion = 11;
            mUpgradeMessage =
                "You must upgrade your executable to play on this server (requires version 1.11 or higher).  " +
                "Check www.tribesplayers.com for the most recent patches.";
        }
        // Packet version: the version string's part before the first '.' (0x0042b73c).
        mPacketVersion = (ushort)CAtoi(mVersion);
        // Session connection ids are seeded from the tick count (0x0058b890).
        mNextConnectionId = unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        mLog = line => StdIo.Out(line);
    }

    // src/server/net_server.cpp NetServer::open.
    public bool Open() => mSocket.Open(mConfig.Port);

    public bool LoopbackOnly { get; private set; }

    public bool Open(ushort port, bool loopbackOnly = false)
    {
        LoopbackOnly = loopbackOnly;
        mConfig.Port = port;
        return loopbackOnly ? mSocket.OpenMemory(port) : mSocket.Open(port, false);
    }

    public void Close() => mSocket.Close();

    public bool IsOpen() => mSocket.Port() != 0;
    public void SetGameInfo(Func<GameInfo>? provider) => mGameInfo = provider;
    public void SetHooks(NetHooks hooks) => mHooks = hooks;
    public BanList Bans() => mBans;
    // Log with a print level: 1 = printf, n = dbprintf(n). Replaces setLog when set.
    public void SetLeveledLog(Action<int, string>? log) => mLeveledLog = log;
    // Test seam: replaces the socket for outgoing datagrams.
    public void SetSendFunction(SendFn send) => mSendOverride = send;
    // Console output (default: stdout).
    public void SetLog(Action<string> log) => mLog = log;
    // One datagram outside any connection (master heartbeat).
    public void SendConnectionless(Address to, ReadOnlySpan<byte> data) => Send(to, data);
    public IReadOnlyList<Player> Players() => mPlayers;
    public ushort Port() => mSocket.Port();

    // src/server/net_server.cpp NetServer::currentInfo.
    private GameInfo CurrentInfo()
    {
        if (mGameInfo != null)
            return mGameInfo();
        return new GameInfo
        {
            Hostname = mConfig.Hostname,
            Password = mConfig.Password,
            MaxPlayers = mConfig.MaxPlayers,
            Info = mConfig.Info,
            ModList = mConfig.ModList,
            MissionType = mConfig.MissionType,
            MissionName = mConfig.MissionName,
            Dedicated = mConfig.Dedicated,
        };
    }

    // src/server/net_server.cpp NetServer::maxPlayers.
    private int MaxPlayers()
    {
        string max = CurrentInfo().MaxPlayers;
        int n = max.Length == 0 ? 32 : CAtoi(max);
        return Math.Min(n, 128);
    }

    // src/server/net_server.cpp NetServer::poll: drains the socket, then runs connection timeouts. nowMs is a millisecond tick.
    public void Poll(uint nowMs)
    {
        Drain(nowMs);
        mHooks.OnTick?.Invoke(nowMs);
        TickConnections(nowMs);
    }

    // src/server/net_server.cpp NetServer::receivePackets: the two network steps of a server frame (docs/sim.md TK2):
    // receivePackets before the ticks (socket and connection timers, 0058bb24), sendPackets after them (the game layer
    // writes one packet per connection that is due, 00550d2c).
    public void ReceivePackets(uint nowMs)
    {
        Drain(nowMs);
        TickConnections(nowMs);
    }

    // src/server/net_server.cpp NetServer::sendPackets.
    public void SendPackets(uint nowMs)
    {
        mNow = nowMs;
        mHooks.OnTick?.Invoke(nowMs);
    }

    // src/server/net_server.cpp NetServer::drain.
    private void Drain(uint nowMs)
    {
        mNow = nowMs;
        var buf = new byte[0x5f4];
        int n;
        while ((n = mSocket.Receive(out Address from, buf)) > 0)
            Receive(from, buf.AsSpan(0, n), nowMs);
    }

    // src/server/net_server.cpp NetServer::tickConnections.
    private void TickConnections(uint nowMs)
    {
        foreach (Address key in mConnections.Keys.ToList())
        {
            if (!mConnections.TryGetValue(key, out Connection? c))
                continue;
            if (!c.Dead() && c.State() != ConnectionState.Idle)
                c.Tick(nowMs);
            if (c.Dead() || c.State() == ConnectionState.Idle)
                mConnections.Remove(key);
        }
    }

    // src/server/net_server.cpp NetServer::receive: handles one received datagram (poll calls it for each one). Public for tests.
    public void Receive(Address from, ReadOnlySpan<byte> data, uint nowMs)
    {
        mNow = nowMs;
        if (data.Length == 0)
            return;
        // Self-echo of our own broadcasts.
        if (from.Ip == 0x7f000001 && from.Port == mSocket.Port())
            return;
        if ((data[0] & 1) == 0)
        {
            HandleConnectionless(from, data);
            return;
        }
        if (!mConnections.TryGetValue(from, out Connection? slot))
        {
            slot = new Connection(from, mNextConnectionId++, this, (to, d) => Send(to, d), nowMs);
            mConnections[from] = slot;
        }
        slot.Receive(data, nowMs);
    }

    // src/server/net_server.cpp NetServer::send.
    private void Send(Address to, ReadOnlySpan<byte> data)
    {
        if (mSendOverride != null)
            mSendOverride(to, data);
        else
            mSocket.Send(to, data);
    }

    // src/server/net_server.cpp NetServer::log.
    private void Log(int level, string line)
    {
        if (mLeveledLog != null)
            mLeveledLog(level, line);
        else if (level <= 1)
            mLog(line);
    }

    // src/server/net_server.cpp NetServer::handleConnectionless.
    private void HandleConnectionless(Address from, ReadOnlySpan<byte> data)
    {
        // 0x0042a4cc: nothing is answered to a banned address (docs/server-ops.md 4.4).
        if (mBans.IsBanned(from.ToString()))
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
        for (int k = mPlayers.Count - 1; k >= 0; --k)
        {
            var row = new GameInfo.PlayerRow { Id = mPlayers[k].Id };
            foreach (GameInfo.PlayerRow r in g.Players)
                if (r.Id == mPlayers[k].Id)
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
        bool full = false;
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
        StrField(kGameType, 255);
        StrField(mVersion, 255);
        GameInfo g = CurrentInfo();
        StrField(g.Hostname, 32);
        Byte(g.Dedicated ? (byte)1 : (byte)0);
        Byte(g.Password.Length == 0 ? (byte)0 : (byte)1);
        Byte((byte)mPlayers.Count);
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
            for (int t = 0; t < g.TeamCount && t < g.Teams.Count; ++t)
            {
                StrField(g.Teams[t].Name, 32);
                StrField(g.Teams[t].Score, 32);
            }
            // Per player: ping/4, loss*100, team, name, raw score text (no % expansion here).
            List<GameInfo.PlayerRow> rows = PlayerRows(g);
            for (int i = 0; i < rows.Count; ++i)
            {
                Byte(unchecked((byte)(rows[i].Ping >> 2)));
                Byte(unchecked((byte)(int)(rows[i].Loss * 100.0f)));
                Byte(unchecked((byte)rows[i].Team));
                StrField(mPlayers[mPlayers.Count - 1 - i].Name, 18);
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
        byte[] head = { 0x10, 0x04, 0xff, 0xf0, data.Length > 4 ? data[4] : (byte)0, data.Length > 5 ? data[5] : (byte)0,
                        0x20, (byte)mPlayers.Count, (byte)mPacketVersion, (byte)(mPacketVersion >> 8) };
        head.CopyTo(output, 0);
        // strncpy of 29 characters, then the last byte is 0.
        string host = CurrentInfo().Hostname;
        for (int i = 0; i < 29 && i < host.Length && host[i] != '\0'; ++i)
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
        byte[] header = { 0x10, 0x08, 0xff, 0x00, data.Length > 4 ? data[4] : (byte)0, data.Length > 5 ? data[5] : (byte)0, 0, 0 };
        var bs = new BitStream(payload, kPayload);
        GameInfo g = CurrentInfo();
        List<GameInfo.PlayerRow> players = PlayerRows(g);
        var names = new List<string>();
        for (int k = mPlayers.Count - 1; k >= 0; --k)
            names.Add(mPlayers[k].Name);

        byte[] pv = { (byte)mPacketVersion, (byte)(mPacketVersion >> 8) };
        bs.WriteBytes(pv);
        bs.WriteString(kGameType);
        bs.WriteString(mVersion);
        bs.WriteString(g.Hostname.Length == 0 ? "N/A" : g.Hostname);
        byte b = (byte)mPlayers.Count;
        bs.WriteBytes(new[] { b });
        // Decimal parse in a byte with no digit check.
        b = 0;
        foreach (char c in g.MaxPlayers)
            b = unchecked((byte)(b * 10 + (byte)c - 0x30));
        bs.WriteBytes(new[] { b });
        bs.WriteString(g.MissionName.Length == 0 ? "N/A" : g.MissionName);
        bs.WriteBytes(new[] { g.Dedicated ? (byte)1 : (byte)0 });
        bs.WriteBytes(new[] { g.Password.Length == 0 ? (byte)0 : (byte)1 });
        int cpu = g.CpuText.Length == 0 ? -1 : g.CpuSpeed;
        bs.WriteBytes(new[] { (byte)cpu, (byte)(cpu >> 8), (byte)(cpu >> 16), (byte)(cpu >> 24) });
        bs.WriteString(g.ModList);
        bs.WriteString(g.MissionType);
        bs.WriteString(g.Info);
        int remaining = kPayload - bs.BytePosition();

        bool hasTeams = g.TeamHeading.Length != 0;
        int playerCount = players.Count;
        int rowCount = hasTeams ? g.TeamCount + playerCount + 2 : playerCount + 1;
        // Teams and players sorted by descending sort value with the C runtime qsort.
        var teams = new List<GameInfo.TeamRow>();
        if (hasTeams)
            for (int t = 0; t < g.TeamCount && t < g.Teams.Count; ++t)
                teams.Add(g.Teams[t]);
        GameInfo.TeamRow[] teamArray = teams.ToArray();
        if (teamArray.Length != 0)
            Borland.BorlandQsort<GameInfo.TeamRow>(teamArray, (x, c) => unchecked((int)((uint)c.Sort - (uint)x.Sort)));
        var order = new int[players.Count];
        for (int i = 0; i < order.Length; ++i)
            order[i] = i;
        if (order.Length != 0)
            Borland.BorlandQsort<int>(order, (x, c) => unchecked((int)((uint)players[c].Sort - (uint)players[x].Sort)));
        int teamRows = teamArray.Length;
        string TeamName(int t) => t >= 0 && t < g.Teams.Count ? g.Teams[t].Name : "";

        bs.WriteString(g.TeamHeading);
        bs.WriteString(g.ClientHeading);
        for (int i = 0; i < rowCount; ++i)
        {
            var row = new System.Text.StringBuilder();
            if (i == 0 && hasTeams)
            {
                row.Append("01" + g.TeamHeading);
            }
            else if (i < teamRows + 1 && hasTeams)
            {
                GameInfo.TeamRow t = teamArray[i - 1];
                row.Append("00");
                for (int k = 0; k < t.Score.Length && row.Length < kRowMax; ++k)
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
                int idx = i - teamRows - (hasTeams ? 2 : 1);
                if (idx < 0 || idx >= playerCount)
                    break; // only when the team count and the team list disagree
                GameInfo.PlayerRow p = players[order[idx]];
                string text = p.Score;
                row.Append("10");
                for (int k = 0; k < text.Length && row.Length < kRowMax; ++k)
                {
                    char next = text[k] == '%' && k + 1 < text.Length ? text[k + 1] : '\0';
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
            string rowText = row.ToString();
            int len = rowText.Length;
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

    // src/server/net_server.cpp NetServer::playerList: listPlayers (FearPlugin id 9): "Server: <n> clients", then one
    // line per player, newest first: "<id> (<address>): <name> - <score text>".
    public List<string> PlayerList()
    {
        var output = new List<string> { "Server: " + Str(mPlayers.Count) + " clients" };
        List<GameInfo.PlayerRow> rows = PlayerRows(CurrentInfo());
        for (int i = 0; i < rows.Count; ++i)
        {
            Player p = mPlayers[mPlayers.Count - 1 - i];
            output.Add(p.Id.ToString(CultureInfo.InvariantCulture) + " (" + (p.Connection != null ? p.Connection.Peer().ToString() : "") +
                       "): " + p.Name + " - " + rows[i].Score);
        }
        return output;
    }

    // src/server/net_server.cpp NetServer::playerById.
    public Player? PlayerById(uint id)
    {
        foreach (Player p in mPlayers)
            if (p.Id == id)
                return p;
        return null;
    }

    // src/server/net_server.cpp NetServer::kick: Net::kick (0x0042babc): closes the game side, sends a disconnect carrying
    // the reason (NUL included) and frees the player id. reason null: the default text. False when no player has that id.
    public bool Kick(uint playerId, string? reason)
    {
        Player? p = PlayerById(playerId);
        if (p == null)
            return false;
        Connection? c = p.Connection;
        mHooks.OnClosed?.Invoke(p);
        c?.Close(Connection.Disconnect, reason ?? "You were kicked from the game.", mNow);
        mFreePlayerIds.Add(playerId);
        mPlayers.RemoveAll(q => q.Id == playerId);
        return true;
    }

    // src/server/net_server.cpp NetServer::dropInvalid: the SimNet error delegate (0x0042b7f8) after an invalid client
    // packet (docs/game-stream.md 1.6): the game side closes (client record removed) and the connection is deleted, which
    // sends a disconnect without a payload.
    public bool DropInvalid(uint playerId)
    {
        Player? p = PlayerById(playerId);
        if (p == null)
            return false;
        Connection? c = p.Connection;
        mHooks.OnClosed?.Invoke(p);
        c?.Close(Connection.Disconnect, null, mNow);
        mFreePlayerIds.Add(playerId);
        mPlayers.RemoveAll(q => q.Id == playerId);
        return true;
    }

    // src/server/net_server.cpp NetServer::allocatePlayerId: player record ids (docs/player-manager.md 2), connections
    // and AI players share one pool.
    public uint AllocatePlayerId()
    {
        if (mFreePlayerIds.Count == 0)
            return mNextFreshPlayerId++;
        uint id = mFreePlayerIds[^1];
        mFreePlayerIds.RemoveAt(mFreePlayerIds.Count - 1);
        return id;
    }

    // src/server/net_server.hpp NetServer::releasePlayerId.
    public void ReleasePlayerId(uint id) => mFreePlayerIds.Add(id);

    // src/server/net_server.cpp NetServer::uniqueName.
    private string UniqueName(string name)
    {
        if (name.Length > kMaxNameLength)
            name = name[..kMaxNameLength];
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; ++i)
        {
            char c = chars[i];
            if (c < 0x20 || c > 0x7f || c == '\\' || c == '"' || c == '~')
                chars[i] = '*';
        }
        name = new string(chars);
        bool Taken(string n) => mPlayers.Any(p => p.Name == n);
        string candidate = name;
        for (int i = 1; Taken(candidate); ++i)
            candidate = name + "." + Str(i);
        return candidate;
    }

    private static bool StrCaseEq(string a, string b)
    {
        a = CStr(a);
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; ++i)
        {
            char x = a[i] >= 'A' && a[i] <= 'Z' ? (char)(a[i] + 0x20) : a[i];
            char y = b[i] >= 'A' && b[i] <= 'Z' ? (char)(b[i] + 0x20) : b[i];
            if (x != y)
                return false;
        }
        return true;
    }

    private static string CStr(string s)
    {
        int nul = s.IndexOf('\0');
        return nul < 0 ? s : s[..nul];
    }

    // src/server/net_server.cpp NetServer::onConnectRequest.
    public override void OnConnectRequest(Connection c, ReadOnlySpan<byte> payload)
    {
        // 0x0042ad6c: a banned address gets no answer; its VC is removed.
        if (mBans.IsBanned(c.Peer().ToString()))
        {
            Log(2, "SimNET: ignoring connect from banned address " + c.Peer().ToString());
            c.Drop();
            return;
        }
        byte[] buf = payload.ToArray();
        var input = new BitStream(buf, buf.Length);
        var pv = new byte[2];
        input.ReadBytes(pv);
        ushort packetVersion = (ushort)(pv[0] | pv[1] << 8);

        if (packetVersion != mPacketVersion)
        {
            c.Close(Connection.Reject, "Wrong packet version.", mNow);
            return;
        }
        if (input.ReadString() != kGameType)
        {
            c.Close(Connection.Reject, "Wrong game type.", mNow);
            return;
        }

        string clientVersion = input.ReadString();
        int dot = mVersion.IndexOf('.');
        int majorLen = dot < 0 || dot == 0 ? 0 : dot - 1; // 0 for "1.11"
        string clientMajor = clientVersion[..Math.Min(majorLen, clientVersion.Length)];
        string ourMajor = mVersion[..Math.Min(majorLen, mVersion.Length)];
        if (clientMajor != ourMajor)
        {
            c.Close(Connection.Reject, "Wrong game version.", mNow);
            return;
        }
        int minor = 0;
        int clientDot = clientVersion.IndexOf('.');
        if (clientDot >= 0)
        {
            string digits = clientVersion[(clientDot + 1)..];
            minor = CAtoi(CStr(digits.Length > 2 ? digits[..2] : digits));
        }
        if (minor < mMinMinorVersion)
        {
            c.Close(Connection.Reject, mUpgradeMessage, mNow);
            return;
        }

        string password = input.ReadString();
        string serverPassword = CurrentInfo().Password;
        if (serverPassword.Length != 0 && password != serverPassword)
        {
            c.Close(Connection.Reject, "Wrong password.", mNow);
            return;
        }
        if (mPlayers.Count >= MaxPlayers())
        {
            c.Close(Connection.Reject, "Server is full.", mNow);
            return;
        }

        string name = UniqueName(input.ReadString());
        string gender = input.ReadString();
        string voice = input.ReadString();
        bool male = StrCaseEq(gender, "male");

        uint id = AllocatePlayerId();
        var accept = new byte[8];
        var output = new BitStream(accept, accept.Length);
        output.WriteInt(id, 32);
        output.WriteInt(kServerManagerId, 32);
        c.Send(Connection.Accept, accept, mNow);
        mPlayers.Add(new Player { Id = id, Name = name, Voice = voice, Male = male, Connection = c });
    }

    // src/server/net_server.cpp NetServer::onAckRangeOverflow: 1.30 logs the oversized ack-range list
    // (docs/lasthope-1.30.md L2, DECISIONS D11).
    public override void OnAckRangeOverflow(Connection c)
    {
        if (mConfig.LastHope)
            mLog("Crash Attempt: " + c.Peer().ToString());
    }

    // src/server/net_server.cpp NetServer::playerFor.
    private Player? PlayerFor(Connection c)
    {
        foreach (Player p in mPlayers)
            if (p.Connection == c)
                return p;
        return null;
    }

    // src/server/net_server.cpp NetServer::onEstablished: the accept was acknowledged, the game stream starts
    // (docs/game-stream.md 5.1).
    public override void OnEstablished(Connection c)
    {
        Player? p = PlayerFor(c);
        if (p != null)
            mHooks.OnAccepted?.Invoke(p, c);
    }

    // src/server/net_server.cpp NetServer::onData.
    public override void OnData(Connection c, ReadOnlySpan<byte> data)
    {
        Player? p = PlayerFor(c);
        if (p != null)
            mHooks.OnData?.Invoke(p, data.ToArray());
    }

    // src/server/net_server.cpp NetServer::onNotify.
    public override void OnNotify(Connection c, uint cookie, bool delivered)
    {
        Player? p = PlayerFor(c);
        if (p != null)
            mHooks.OnNotify?.Invoke(p, cookie, delivered);
    }

    // src/server/net_server.cpp NetServer::onClosed.
    public override void OnClosed(Connection c, ReadOnlySpan<byte> reason, bool timedOut)
    {
        Player? p = PlayerFor(c);
        if (p != null)
            mHooks.OnClosed?.Invoke(p);
        foreach (Player q in mPlayers)
            if (q.Connection == c)
                mFreePlayerIds.Add(q.Id);
        mPlayers.RemoveAll(q => q.Connection == c);
    }
}
