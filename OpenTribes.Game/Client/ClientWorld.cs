// Client-side state the receive path writes (docs/client-net.md 2.3): the client PlayerManager tables, the chat HUD
// log, remote evals and script callbacks, the datablocks, the ghost table and the PSC state of one connection.
namespace OpenTribes.Game;

/// <summary>One 0x1e0 byte client record of the client PlayerManager (index id - 2048).</summary>
public sealed class ClientPlayerRecord
{
    /// <summary>The client id.</summary>
    public int Id;
    /// <summary>True for an AI player.</summary>
    public bool Ai;
    /// <summary>The player name.</summary>
    public string Name = "";
    /// <summary>The skin.</summary>
    public string Skin = "";
    /// <summary>The voice set.</summary>
    public string Voice = "";
    /// <summary>True for a male player.</summary>
    public bool Male;
    /// <summary>The id of the commander (+0x40), -1 for none.</summary>
    public int Commander = -1;
    /// <summary>The target index of the current command (+0x48), -1 for a waypoint command.</summary>
    public int CmdTarget = -1;
    /// <summary>The x of the waypoint of the current command, in 1/1024 of the mission area (+0x50).</summary>
    public int CmdWaypointX;
    /// <summary>The y of the waypoint of the current command, in 1/1024 of the mission area (+0x54).</summary>
    public int CmdWaypointY;
    /// <summary>The commander of the current command (+0x4c, PlayerManager_processEvent 0040d45a).</summary>
    public int CmdIssuer = -1;
    /// <summary>The team (+0x14).</summary>
    public int Team = -1;
    /// <summary>The command status (+0x58).</summary>
    public int CommandStatus;
    /// <summary>True when the command map selects this player (+0x68). TSCommander (004d2a98) writes it and adding the record clears it.</summary>
    public bool Selected;
    /// <summary>True when the command tree shows the children of this node (+0x69). It starts at 1 and the team list toggles it (004a8b00).</summary>
    public bool Expanded = true;
    /// <summary>The score text (+0xb0).</summary>
    public string ScoreText = "";  // +0xb0
    /// <summary>The sort value (+0x1b0).</summary>
    public int SortValue;
    /// <summary>The ping of a human record, x4 ms on the wire.</summary>
    public int PingMs;
    /// <summary>The packet loss.</summary>
    public double Loss;
    /// <summary>The ghost index of the control object (+0x94), set by the GameBase part.</summary>
    public int ControlGhost = -1;
    /// <summary>The ghost index of the owned object (+0x98).</summary>
    public int OwnedGhost = -1;
}

/// <summary>A team record of the client PlayerManager.</summary>
public sealed class ClientTeam
{
    /// <summary>The team index.</summary>
    public int Index;
    /// <summary>The team name.</summary>
    public string Name = "";
    /// <summary>The team skin.</summary>
    public string Skin = "";
    /// <summary>The score text.</summary>
    public string ScoreText = "";
    /// <summary>The sort value.</summary>
    public int SortValue;
}

/// <summary>The PlayerManager on a client (0040ce84): the tables, the counters and the lines it prints to the chat HUD.</summary>
public sealed class ClientPlayerManager
{
    /// <summary>The client records by id.</summary>
    public readonly SortedDictionary<int, ClientPlayerRecord> Clients = new();
    /// <summary>The team records by index.</summary>
    public readonly SortedDictionary<int, ClientTeam> Teams = new();
    /// <summary>The number of human players.</summary>
    public int HumanCount;
    /// <summary>The number of players.</summary>
    public int PlayerCount;
    /// <summary>The team count, -1 until set.</summary>
    public int TeamCount = -1;
    /// <summary>The client score heading.</summary>
    public string ClientHeading = "";
    /// <summary>The team score heading.</summary>
    public string TeamHeading = "";
    /// <summary>The chat lines.</summary>
    public readonly List<string> Chat = new();
    /// <summary>The chat display type of each chat line: 0 system, 1 and 4 red, 3 green, else yellow.</summary>
    /// <remarks>Follows PlayerManager_message (0040dbfc, argument 2) and the FearGuiChatDisplay font table (004a5124).</remarks>
    public readonly List<int> ChatTypes = new();
    /// <summary>Answers whether the play GUI and its chat display exist.</summary>
    /// <remarks>PlayerManager_message (0040dbfc) looks up <c>playGui</c> and its chat display when the message is made, while the packet is read. A line made before the PSC mode change that loads play.gui (loadPlayGui) is never handed to the chat ring (004a525c) and goes to the console only. <see cref="ChatDisplayed"/> holds the answer for each chat line.</remarks>
    public Func<bool> ChatDisplayExists = () => true;
    /// <summary>The answer of <see cref="ChatDisplayExists"/> for each chat line.</summary>
    public readonly List<bool> ChatDisplayed = new();
    /// <summary>Reads <c>$pref::playVoices</c> as the engine does (0040940c): an empty value counts as true. Set by the client app.</summary>
    public Func<bool> PlayVoices = () => true;
    /// <summary>Called with the client id and the raw text with the <c>~w</c> tag when a command text carries a voice, so the audio plays the wave.</summary>
    public Action<int, string>? ChatVoice;
    /// <summary>The text of the last command event (PlayerManager +0x24ed4), reused when a later one has none.</summary>
    public string LastCommandText = "";
    /// <summary>The objective texts.</summary>
    public readonly List<string> Objectives = new();
    /// <summary>The target of the compass waypoint (global 006a83e4), -1 for none.</summary>
    public int WaypointTarget = -1;
    /// <summary>The x of the compass waypoint (global 006a83e8).</summary>
    public double WaypointX;
    /// <summary>The y of the compass waypoint (global 006a83ec).</summary>
    public double WaypointY;
    /// <summary>Converts the 10 bit wire pair to world coordinates: <c>value * size / 1024 + origin</c> of the MissionCenterPos rectangle (WaypointToWorld). Set by the world.</summary>
    public Func<long, long, (double X, double Y)> WaypointToWorld = (x, y) => (x, y);
    /// <summary>Sets the compass waypoint from the wire pair.</summary>
    /// <param name="x">The wire x.</param>
    /// <param name="y">The wire y.</param>
    public void SetWaypointXY(long x, long y) => (WaypointX, WaypointY) = WaypointToWorld(x, y);
    /// <summary>The objective lines by index. TeamObjectiveEvent sets one (docs/gui.md 3.3.1), and the page of CmdObjectives.gui shows them.</summary>
    public readonly string?[] ObjectiveLines = new string?[32];
    /// <summary>The target names by index.</summary>
    public readonly Dictionary<int, string> TargetNames = new();
    /// <summary>Called for script callbacks evaluated in the global namespace (docs/client-net.md 2.4).</summary>
    public Action<string>? OnScript;
    /// <summary>The script callbacks evaluated so far.</summary>
    public readonly List<string> Callbacks = new();

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
        int i = text.IndexOf("~w", StringComparison.Ordinal);
        return i < 0 ? text : text[..i];
    }

    /// <summary>Returns a client record.</summary>
    /// <param name="id">The client id.</param>
    /// <returns>The record, or null.</returns>
    public ClientPlayerRecord? Get(int id) => Clients.TryGetValue(id, out ClientPlayerRecord? r) ? r : null;
}

/// <summary>The state of one client connection that the receive path updates.</summary>
/// <remarks>Covers the client PlayerManager tables, the chat HUD log, remote evals and script callbacks, the datablocks, the ghost table and the PSC state (docs/client-net.md 2.3).</remarks>
public sealed class ClientWorld
{
    /// <summary>The client datablocks.</summary>
    public readonly ClientDataBlocks DataBlocks = new();
    /// <summary>The client PlayerManager.</summary>
    public readonly ClientPlayerManager Players = new();
    /// <summary>The client PlayerPSC.</summary>
    public readonly ClientPsc Psc = new();
    /// <summary>The client ghost manager.</summary>
    public readonly ClientGhostManager Ghosts;

    /// <summary>The player id of the client, from the accept payload or the .rec header.</summary>
    public int PlayerId;
    /// <summary>The server manager id.</summary>
    public int ServerId = 2048;
    /// <summary>The number of times the always phase ended. A mission reload ghosts the client again.</summary>
    public int AlwaysDoneCount;
    /// <summary>True once onClientGhostAlwaysDone() was evaluated (docs/client-net.md 2.7).</summary>
    public bool AlwaysDone;
    /// <summary>The net time in ms (0063efb0).</summary>
    public uint NetTimeMs;

    /// <summary>The <c>remote&lt;Name&gt;(2048, args...)</c> calls the server sent (docs/client-net.md 2.5), with the name without the "remote" prefix.</summary>
    public readonly List<(string Name, string[] Args)> RemoteCalls = new();
    /// <summary>Called for every remote call.</summary>
    public Action<string, string[]>? OnRemoteCall;

    /// <summary>The receive path total of packets (docs/client-net.md 2.2). Unknown bits are counted, never skipped silently.</summary>
    public long Packets;
    /// <summary>The total of ghost updates.</summary>
    public long GhostUpdates;
    /// <summary>The total of ghost deletes.</summary>
    public long GhostDeletes;
    /// <summary>The total of events.</summary>
    public long Events;
    /// <summary>The total of unknown bits.</summary>
    public long UnknownBits;
    /// <summary>The total of padding bits.</summary>
    public long PaddingBits;
    /// <summary>The total of errors.</summary>
    public long Errors;
    /// <summary>The number of events by name.</summary>
    public readonly Dictionary<string, long> EventCounts = new();
    /// <summary>The number of ghost updates by class.</summary>
    public readonly Dictionary<string, long> GhostCounts = new();
    /// <summary>The reasons for unknown bits.</summary>
    public readonly List<string> UnknownReasons = new();

    /// <summary>Creates an empty client world.</summary>
    public ClientWorld()
    {
        Ghosts = new ClientGhostManager(this);
        Players.WaypointToWorld = (x, y) =>
        {
            double[]? r = Ghosts.All().FirstOrDefault(g => g.Class == "MissionCenterPos")?.State.GetValueOrDefault("rect") as double[];
            if (r is not { Length: 4 })
                return (x, y);
            return ((float)((float)x * (float)r[2] * 0.0009765625f + (float)r[0]), (float)((float)y * (float)r[3] * 0.0009765625f + (float)r[1]));
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
