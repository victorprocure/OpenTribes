// Client-side state the receive path writes (docs/client-net.md 2.3): the client PlayerManager tables, the chat HUD
// log, remote evals and script callbacks, the datablocks, the ghost table and the PSC state of one connection.
namespace OpenTribes.Game;

/// <summary>One 0x1e0 byte client record of the client PlayerManager (index id - 2048).</summary>
public sealed class ClientPlayerRecord
{
    /// <summary>The client id.</summary>
    public int Id { get; set; }
    /// <summary>True for an AI player.</summary>
    public bool Ai { get; set; }
    /// <summary>The player name.</summary>
    public string Name { get; set; } = "";
    /// <summary>The skin.</summary>
    public string Skin { get; set; } = "";
    /// <summary>The voice set.</summary>
    public string Voice { get; set; } = "";
    /// <summary>True for a male player.</summary>
    public bool Male { get; set; }
    /// <summary>The id of the commander (+0x40), -1 for none.</summary>
    public int Commander { get; set; } = -1;
    /// <summary>The target index of the current command (+0x48), -1 for a waypoint command.</summary>
    public int CmdTarget { get; set; } = -1;
    /// <summary>The x of the waypoint of the current command, in 1/1024 of the mission area (+0x50).</summary>
    public int CmdWaypointX { get; set; }
    /// <summary>The y of the waypoint of the current command, in 1/1024 of the mission area (+0x54).</summary>
    public int CmdWaypointY { get; set; }
    /// <summary>The commander of the current command (+0x4c, PlayerManager_processEvent 0040d45a).</summary>
    public int CmdIssuer { get; set; } = -1;
    /// <summary>The team (+0x14).</summary>
    public int Team { get; set; } = -1;
    /// <summary>The command status (+0x58).</summary>
    public int CommandStatus { get; set; }
    /// <summary>True when the command map selects this player (+0x68). TSCommander (004d2a98) writes it and adding the record clears it.</summary>
    public bool Selected { get; set; }
    /// <summary>True when the command tree shows the children of this node (+0x69). It starts at 1 and the team list toggles it (004a8b00).</summary>
    public bool Expanded { get; set; } = true;
    /// <summary>The score text (+0xb0).</summary>
    public string ScoreText { get; set; } = "";  // +0xb0
    /// <summary>The sort value (+0x1b0).</summary>
    public int SortValue { get; set; }
    /// <summary>The ping of a human record, x4 ms on the wire.</summary>
    public int PingMs { get; set; }
    /// <summary>The packet loss.</summary>
    public double Loss { get; set; }
    /// <summary>The ghost index of the control object (+0x94), set by the GameBase part.</summary>
    public int ControlGhost { get; set; } = -1;
    /// <summary>The ghost index of the owned object (+0x98).</summary>
    public int OwnedGhost { get; set; } = -1;
}

/// <summary>A team record of the client PlayerManager.</summary>
public sealed class ClientTeam
{
    /// <summary>The team index.</summary>
    public int Index { get; set; }
    /// <summary>The team name.</summary>
    public string Name { get; set; } = "";
    /// <summary>The team skin.</summary>
    public string Skin { get; set; } = "";
    /// <summary>The score text.</summary>
    public string ScoreText { get; set; } = "";
    /// <summary>The sort value.</summary>
    public int SortValue { get; set; }
}

/// <summary>The PlayerManager on a client (0040ce84): the tables, the counters and the lines it prints to the chat HUD.</summary>
public sealed class ClientPlayerManager
{
    /// <summary>The client records by id.</summary>
    public SortedDictionary<int, ClientPlayerRecord> Clients { get; } = [];
    /// <summary>The team records by index.</summary>
    public SortedDictionary<int, ClientTeam> Teams { get; } = [];
    /// <summary>The number of human players.</summary>
    public int HumanCount { get; set; }
    /// <summary>The number of players.</summary>
    public int PlayerCount { get; set; }
    /// <summary>The team count, -1 until set.</summary>
    public int TeamCount { get; set; } = -1;
    /// <summary>The client score heading.</summary>
    public string ClientHeading { get; set; } = "";
    /// <summary>The team score heading.</summary>
    public string TeamHeading { get; set; } = "";
    /// <summary>The chat lines.</summary>
    public List<string> Chat { get; } = [];
    /// <summary>The chat display type of each chat line: 0 system, 1 and 4 red, 3 green, else yellow.</summary>
    /// <remarks>Follows PlayerManager_message (0040dbfc, argument 2) and the FearGuiChatDisplay font table (004a5124).</remarks>
    public List<int> ChatTypes { get; } = [];
    /// <summary>Answers whether the play GUI and its chat display exist.</summary>
    /// <remarks>PlayerManager_message (0040dbfc) looks up <c>playGui</c> and its chat display when the message is made, while the packet is read. A line made before the PSC mode change that loads play.gui (loadPlayGui) is never handed to the chat ring (004a525c) and goes to the console only. <see cref="ChatDisplayed"/> holds the answer for each chat line.</remarks>
    public Func<bool> ChatDisplayExists { get; set; } = () => true;
    /// <summary>The answer of <see cref="ChatDisplayExists"/> for each chat line.</summary>
    public List<bool> ChatDisplayed { get; } = [];
    /// <summary>Reads <c>$pref::playVoices</c> as the engine does (0040940c): an empty value counts as true. Set by the client app.</summary>
    public Func<bool> PlayVoices { get; set; } = () => true;
    /// <summary>Called with the client id and the raw text with the <c>~w</c> tag when a command text carries a voice, so the audio plays the wave.</summary>
    public Action<int, string>? ChatVoice { get; set; }
    /// <summary>The text of the last command event (PlayerManager +0x24ed4), reused when a later one has none.</summary>
    public string LastCommandText { get; set; } = "";
    /// <summary>The objective texts.</summary>
    public List<string> Objectives { get; } = [];
    /// <summary>The target of the compass waypoint (global 006a83e4), -1 for none.</summary>
    public int WaypointTarget { get; set; } = -1;
    /// <summary>The x of the compass waypoint (global 006a83e8).</summary>
    public double WaypointX { get; set; }
    /// <summary>The y of the compass waypoint (global 006a83ec).</summary>
    public double WaypointY { get; set; }
    /// <summary>Converts the 10 bit wire pair to world coordinates: <c>value * size / 1024 + origin</c> of the MissionCenterPos rectangle (WaypointToWorld). Set by the world.</summary>
    public Func<long, long, (double X, double Y)> WaypointToWorld { get; set; } = (x, y) => (x, y);
    /// <summary>Sets the compass waypoint from the wire pair.</summary>
    /// <param name="x">The wire x.</param>
    /// <param name="y">The wire y.</param>
    public void SetWaypointXY(long x, long y) => (WaypointX, WaypointY) = WaypointToWorld(x, y);
    /// <summary>The objective lines by index. TeamObjectiveEvent sets one (docs/gui.md 3.3.1), and the page of CmdObjectives.gui shows them.</summary>
    public string?[] ObjectiveLines { get; } = new string?[32];
    /// <summary>The target names by index.</summary>
    public Dictionary<int, string> TargetNames { get; } = [];
    /// <summary>Called for script callbacks evaluated in the global namespace (docs/client-net.md 2.4).</summary>
    public Action<string>? OnScript { get; set; }
    /// <summary>The script callbacks evaluated so far.</summary>
    public List<string> Callbacks { get; } = [];

    /// <summary>Evaluates a script callback in the global namespace.</summary>
    /// <param name="text">The script text.</param>
    public void Script(string text)
    {
        Callbacks.Add(text);
        OnScript?.Invoke(text);
    }

    /// <summary>Clears the tables and counters.</summary>
    public void Clear()
    {
        Clients.Clear();
        Teams.Clear();
        HumanCount = PlayerCount = 0;
        TeamCount = -1;
        Chat.Clear();
        ChatTypes.Clear();
        ChatDisplayed.Clear();
    }

    /// <summary>Prints a message line to the chat HUD.</summary>
    /// <param name="text">The message text.</param>
    /// <param name="type">The chat display type.</param>
    public void Message(string text, int type = 0)
    {
        Chat.Add(text);
        ChatTypes.Add(type);
        ChatDisplayed.Add(ChatDisplayExists());
    }

    /// <summary>Cuts the text at a <c>~w&lt;sound&gt;</c> voice tag when <c>$pref::playVoices</c> is on. The sound is played from the tag.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text to show.</returns>
    /// <remarks>Follows 0040940c.</remarks>
    public string StripVoice(string text)
    {
        if (!PlayVoices())
            return text;
        var i = text.IndexOf("~w", StringComparison.Ordinal);
        return i < 0 ? text : text[..i];
    }

    /// <summary>Returns a client record.</summary>
    /// <param name="id">The client id.</param>
    /// <returns>The record, or null.</returns>
    public ClientPlayerRecord? Get(int id) => Clients.TryGetValue(id, out var r) ? r : null;
}

/// <summary>The state of one client connection that the receive path updates.</summary>
/// <remarks>Covers the client PlayerManager tables, the chat HUD log, remote evals and script callbacks, the datablocks, the ghost table and the PSC state (docs/client-net.md 2.3).</remarks>
public sealed class ClientWorld
{
    /// <summary>The client datablocks.</summary>
    public ClientDataBlocks DataBlocks { get; } = new();
    /// <summary>The client PlayerManager.</summary>
    public ClientPlayerManager Players { get; } = new();
    /// <summary>The client PlayerPSC.</summary>
    public ClientPsc Psc { get; } = new();
    /// <summary>The client ghost manager.</summary>
    public ClientGhostManager Ghosts { get; }

    /// <summary>The player id of the client, from the accept payload or the .rec header.</summary>
    public int PlayerId { get; set; }
    /// <summary>The server manager id.</summary>
    public int ServerId { get; set; } = 2048;
    /// <summary>The number of times the always phase ended. A mission reload ghosts the client again.</summary>
    public int AlwaysDoneCount { get; set; }
    /// <summary>True once onClientGhostAlwaysDone() was evaluated (docs/client-net.md 2.7).</summary>
    public bool AlwaysDone { get; set; }
    /// <summary>The net time in ms (0063efb0).</summary>
    public uint NetTimeMs { get; set; }

    /// <summary>The <c>remote&lt;Name&gt;(2048, args...)</c> calls the server sent (docs/client-net.md 2.5), with the name without the "remote" prefix.</summary>
    public List<(string Name, string[] Args)> RemoteCalls { get; } = [];
    /// <summary>Called for every remote call.</summary>
    public Action<string, string[]>? OnRemoteCall { get; set; }

    /// <summary>The receive path total of packets (docs/client-net.md 2.2). Unknown bits are counted, never skipped silently.</summary>
    public long Packets { get; set; }
    /// <summary>The total of ghost updates.</summary>
    public long GhostUpdates { get; set; }
    /// <summary>The total of ghost deletes.</summary>
    public long GhostDeletes { get; set; }
    /// <summary>The total of events.</summary>
    public long Events { get; set; }
    /// <summary>The total of unknown bits.</summary>
    public long UnknownBits { get; set; }
    /// <summary>The total of padding bits.</summary>
    public long PaddingBits { get; set; }
    /// <summary>The total of errors.</summary>
    public long Errors { get; set; }
    /// <summary>The number of events by name.</summary>
    public Dictionary<string, long> EventCounts { get; } = [];
    /// <summary>The number of ghost updates by class.</summary>
    public Dictionary<string, long> GhostCounts { get; } = [];
    /// <summary>The reasons for unknown bits.</summary>
    public List<string> UnknownReasons { get; } = [];

    /// <summary>Creates an empty client world.</summary>
    public ClientWorld()
    {
        Ghosts = new ClientGhostManager(this);
        Players.WaypointToWorld = (x, y) =>
        {
            var r = Ghosts.All().FirstOrDefault(g => g.Class == "MissionCenterPos")?.State.GetValueOrDefault("rect") as double[];
            if (r is not { Length: 4 })
                return (x, y);
            return ((float)((x * (float)r[2] * 0.0009765625f) + (float)r[0]), (float)((y * (float)r[3] * 0.0009765625f) + (float)r[1]));
        };
        Psc.OnWaypoint = Players.SetWaypointXY;
    }

    /// <summary>Clears the world for a new connection.</summary>
    public void Reset()
    {
        DataBlocks.Clear();
        Players.Clear();
        Players.WaypointTarget = -1;
        Psc.Reset();
        Ghosts.Clear();
        AlwaysDone = false;
        AlwaysDoneCount = 0;
        RemoteCalls.Clear();
    }
}
