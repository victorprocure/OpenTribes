// Client-side state the receive path writes (docs/client-net.md 2.3): the client PlayerManager tables, the chat HUD
// log, remote evals and script callbacks, the datablocks, the ghost table and the PSC state of one connection.
namespace OpenTribes.Game;

// One 0x1e0 byte client record of the client PlayerManager (index id - 2048).
public sealed class ClientPlayerRecord
{
    public int Id;
    public bool Ai;
    public string Name = "", Skin = "", Voice = "";
    public bool Male;
    public int Commander = -1;     // +0x40
    public int CmdIssuer = -1;     // +0x4c: the commander of the current command (PlayerManager_processEvent 0040d45a)
    public int Team = -1;          // +0x14
    public int CommandStatus;      // +0x58
    public string ScoreText = "";  // +0xb0
    public int SortValue;          // +0x1b0
    public int PingMs;             // human record ping (x4 ms on the wire)
    public double Loss;
    public int ControlGhost = -1;  // +0x94: control object (set by the GameBase part)
    public int OwnedGhost = -1;    // +0x98: owned object
}

public sealed class ClientTeam
{
    public int Index;
    public string Name = "", Skin = "";
    public string ScoreText = "";
    public int SortValue;
}

// 0040ce84 PlayerManager on a client: tables, counters and the lines it prints to the chat HUD.
public sealed class ClientPlayerManager
{
    public readonly SortedDictionary<int, ClientPlayerRecord> Clients = new();
    public readonly SortedDictionary<int, ClientTeam> Teams = new();
    public int HumanCount, PlayerCount;
    public int TeamCount = -1;
    public string ClientHeading = "", TeamHeading = "";
    public readonly List<string> Chat = new();
    // The chat display type of each Chat line (PlayerManager_message 0040dbfc arg 2): 0 system, 1 and 4 red, 3 green, else yellow
    // (FearGuiChatDisplay font table 004a5124).
    public readonly List<int> ChatTypes = new();
    // $pref::playVoices as the engine reads it (0040940c): an empty value counts as true. Set by the client app.
    public Func<bool> PlayVoices = () => true;
    // Called with (client id, raw text with the ~w tag) when a command text carries a voice (the audio plays the wave).
    public Action<int, string>? ChatVoice;
    // PlayerManager +0x24ed4: the text of the last command event, reused when a later one has none.
    public string LastCommandText = "";
    public readonly List<string> Objectives = new();
    // The compass waypoint [globals 006a83e4 (target, -1 none), 006a83e8, 006a83ec]. The 10 bit wire pair becomes
    // value * size / 1024 + origin of the MissionCenterPos rectangle (WaypointToWorld, set by the world).
    public int WaypointTarget = -1;
    public double WaypointX, WaypointY;
    public Func<long, long, (double X, double Y)> WaypointToWorld = (x, y) => (x, y);
    public void SetWaypointXY(long x, long y) => (WaypointX, WaypointY) = WaypointToWorld(x, y);
    // The objective lines by index (TeamObjectiveEvent sets one, docs/gui.md 3.3.1): the page of CmdObjectives.gui shows them.
    public readonly string?[] ObjectiveLines = new string?[32];
    public readonly Dictionary<int, string> TargetNames = new();
    public Action<string>? OnScript;      // script callbacks evaluated in the global namespace (2.4)
    public readonly List<string> Callbacks = new();

    public void Script(string text)
    {
        Callbacks.Add(text);
        OnScript?.Invoke(text);
    }

    public void Clear()
    {
        Clients.Clear();
        Teams.Clear();
        HumanCount = PlayerCount = 0;
        TeamCount = -1;
        Chat.Clear();
        ChatTypes.Clear();
    }

    public void Message(string text, int type = 0)
    {
        Chat.Add(text);
        ChatTypes.Add(type);
    }

    // 0040940c: with $pref::playVoices on, a `~w<sound>` tag ends the shown text (the sound is played from it).
    public string StripVoice(string text)
    {
        if (!PlayVoices())
            return text;
        int i = text.IndexOf("~w", StringComparison.Ordinal);
        return i < 0 ? text : text[..i];
    }

    public ClientPlayerRecord? Get(int id) => Clients.TryGetValue(id, out ClientPlayerRecord? r) ? r : null;
}

// The state of one client connection that the receive path updates.
public sealed class ClientWorld
{
    public readonly ClientDataBlocks DataBlocks = new();
    public readonly ClientPlayerManager Players = new();
    public readonly ClientPsc Psc = new();
    public readonly ClientGhostManager Ghosts;

    public int PlayerId;            // the client's player id (accept payload, or the .rec header)
    public int ServerId = 2048;     // the server manager id
    public int AlwaysDoneCount;     // times the always phase ended: a mission reload ghosts the client again
    public bool AlwaysDone;         // onClientGhostAlwaysDone() was evaluated (2.7)
    public uint NetTimeMs;          // net time 0063efb0

    // remote<Name>(2048, args...) calls the server sent (2.5): name without the "remote" prefix.
    public readonly List<(string Name, string[] Args)> RemoteCalls = new();
    public Action<string, string[]>? OnRemoteCall;

    // Receive path totals (client-net.md 2.2): unknown bits are counted, never skipped silently.
    public long Packets, GhostUpdates, GhostDeletes, Events, UnknownBits, PaddingBits, Errors;
    public readonly Dictionary<string, long> EventCounts = new();
    public readonly Dictionary<string, long> GhostCounts = new();
    public readonly List<string> UnknownReasons = new();

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
