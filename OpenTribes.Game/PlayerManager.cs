// Port of src/game/player_manager.hpp and src/game/player_manager.cpp: the PlayerManager (object 713): client records,
// team scores and objectives, and the game events they post (docs/player-manager.md).
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>The record of one client (docs/player-manager.md 2.1). Fields marked "kept" survive the reuse of the slot.</summary>
public sealed class ClientRecord
{
    /// <summary>True while the slot is in use.</summary>
    public bool Used;
    /// <summary>True for an AI player.</summary>
    public bool IsAI;
    /// <summary>The client id.</summary>
    public int Id;
    /// <summary>The player name.</summary>
    public string Name = "";
    /// <summary>The skin name.</summary>
    public string Skin = "base";
    /// <summary>The voice set.</summary>
    public string Voice = "";
    /// <summary>True for a male player.</summary>
    public bool Male = true;
    /// <summary>The team id, or -1 for none.</summary>
    public int Team = -1;
    /// <summary>The GUI mode mirror (+0x1c). Written by the GUI mode change only.</summary>
    public int GuiMirror;
    /// <summary>The GUI mode (PlayerPSC +0x3d8).</summary>
    public int GuiMode;
    /// <summary>The mark of the last objective update sent to this client.</summary>
    public uint ObjectiveMark;
    /// <summary>The mark of the last command update sent to this client.</summary>
    public uint CommandMark;
    /// <summary>The stamp of the last command.</summary>
    public uint CommandStamp;
    /// <summary>The command icon, or -1 for none.</summary>
    public int CommandIcon = -1;
    /// <summary>The command status.</summary>
    public int CommandStatus;
    /// <summary>The id of the client that issued the command (kept).</summary>
    public int CommandIssuer;
    /// <summary>The target of the command (kept).</summary>
    public int CommandTarget;
    /// <summary>The waypoint x of the command (kept).</summary>
    public int WaypointX;
    /// <summary>The waypoint y of the command (kept).</summary>
    public int WaypointY;
    /// <summary>The command text (+0x44), interned and kept.</summary>
    public string? CommandText;
    /// <summary>The command text last sent to this client (+0xac).</summary>
    public string? LastCommandText;
    /// <summary>The id of the commander the client follows, or -1.</summary>
    public int Commander = -1;
    /// <summary>The join order.</summary>
    public int JoinOrder;
    /// <summary>True while the player shows the score menu (+0x6a).</summary>
    public bool MenuScoreVis;
    /// <summary>True once the client finished loading its data (+0x1dc).</summary>
    public bool DataFinished;
    /// <summary>The id of the object the client owns, 0 for none (+0x98).</summary>
    public int OwnedObject;
    /// <summary>The id of the object the client controls, 0 for none (+0x94).</summary>
    public int ControlObject;
    /// <summary>The observer camera of the client (+0x9c).</summary>
    public ObserverCamera? Camera;
    /// <summary>The connection's event manager as a server manager object (Net::EventManager, pool id). Human clients only (0054ea34, 0042b1b8; docs/game-stream.md 1.1).</summary>
    public SimObject? EventManager;
    /// <summary>The connection's PlayerPSC as a server manager object (id 715).</summary>
    public SimObject? PlayerPsc;
    /// <summary>The connection's ghost manager as a server manager object (Net::GhostManager, id 37).</summary>
    public SimObject? GhostManager;
    /// <summary>The command limit flag (PlayerPSC +0x90).</summary>
    public bool LimitCommand;
    /// <summary>The score text.</summary>
    public string ScoreText = "";
    /// <summary>The score sort value (kept).</summary>
    public int ScoreSort;
    /// <summary>The stamp of the last score change.</summary>
    public uint ScoreStamp;
    /// <summary>The mark of the last score update sent to this client.</summary>
    public uint ScoreMark;
    /// <summary>The connection ping in milliseconds.</summary>
    public uint Ping;
    /// <summary>The packet loss, 0 to 1.</summary>
    public float Loss;
    /// <summary>The shopping item mask.</summary>
    public readonly uint[] Shopping = new uint[7];
    /// <summary>The buying item mask.</summary>
    public readonly uint[] Buying = new uint[7];
    /// <summary>The next record of the human list, newest first.</summary>
    public ClientRecord? NextHuman;
    /// <summary>The next record of the player list (humans and AI), newest first (+0x8).</summary>
    public ClientRecord? NextPlayer;
}

// src/game/player_manager.cpp EventManagerObject: Net::EventManager as a script-visible object (getObjectType); the event
// queue itself lives in the connection layer.
internal sealed class EventManagerObject : SimObject
{
    public override string ClassName() => "Net::EventManager";
}

// src/game/player_manager.cpp PlayerPscObject, GhostManagerObject: PlayerPSC and Net::GhostManager the same way. Their
// constructors set the id (PlayerPSC 0x00404210: 715, GhostManager 0x005533b4: 37), so every connection registers the
// same two ids: the newest connection's objects shadow the older ones (docs/sim.md ID6) and an older connection's come
// back when the newer one is deleted.
internal sealed class PlayerPscObject : SimObject
{
    public override string ClassName() => "PlayerPSC";
    public override int FixedId() => 715;
}

internal sealed class GhostManagerObject : SimObject
{
    public override string ClassName() => "Net::GhostManager";
    public override int FixedId() => 37;
}

/// <summary>The player manager (object 713): the client records, teams, scores and commands.</summary>
/// <remarks>Port of class PlayerManager in src/game/player_manager.hpp. See docs/player-manager.md.</remarks>
public sealed class PlayerManager
{
    private readonly GameState mGame;
    private readonly ClassContext mCameraContext = new(); // observer cameras register as GameBase objects
    private readonly ClientRecord[] mRecords = new ClientRecord[128];
    private readonly List<ClientRecord> mFree = []; // LIFO, back is next
    private ClientRecord? mHumans;
    private ClientRecord? mPlayers; // humans and AI, newest first
    private int mHumanCount, mPlayerCount, mJoinCounter;
    private uint mScoreSeq, mObjectiveCounter, mCommandCounter;

    /// <summary>Delivers an event to one connected client. Set by the network layer.</summary>
    public Action<int, OutEvent> Send = (_, _) => { };
    /// <summary>Returns the connection ping (ms) and loss (0 to 1) of a client for the 1.5 s update (docs/player-manager.md 8).</summary>
    public Func<int, (uint Ping, float Loss)>? NetStats;
    /// <summary>Called when GameBase mask bits changed (NetObject::setMaskBits). Set by the network layer.</summary>
    public Action<GameBase, uint>? SetMaskBitsHook;

    /// <summary>Creates the manager for a game.</summary>
    /// <param name="game">The game state.</param>
    public PlayerManager(GameState game)
    {
        mGame = game;
        mCameraContext.Game = game;
        for (var i = 0; i < 128; ++i)
            mRecords[i] = new ClientRecord { Id = 2048 + i };
        for (var i = 127; i >= 1; --i)
            mFree.Add(mRecords[i]);
    }

    /// <summary>Returns the number of human clients.</summary>
    /// <returns>The count.</returns>
    public int HumanCount() => mHumanCount;
    /// <summary>Returns the number of players, humans and AI (+0x5c of the manager).</summary>
    /// <returns>The count.</returns>
    public int PlayerCount() => mPlayerCount;
    /// <summary>Returns the id the next add takes, the head of the free list.</summary>
    /// <returns>The id, or 0 without a free record.</returns>
    public int NextFreeId() => mFree.Count == 0 ? 0 : mFree[^1].Id;
    /// <summary>Returns the command counter (+0x50 of the manager), advanced by the command map commands.</summary>
    /// <returns>A reference to the counter.</returns>
    public ref uint CommandCounter() => ref mCommandCounter;
    /// <summary>Posts an event to a connected client through the network layer.</summary>
    /// <param name="id">The client id.</param>
    /// <param name="e">The event.</param>
    public void SendEvent(int id, OutEvent e) => Send(id, e);
    private void Post(int id, OutEvent e) => Send(id, e);

    // src/game/player_manager.cpp makeTeamChange.
    private static OutEvent MakeTeamChange(int id, int team)
    {
        var w = new EventWriter();
        w.PutInt((uint)(id - 2048), 7);
        w.PutInt((uint)(team + 1), 4);
        return w.Finish(0x452);
    }

    // src/game/player_manager.cpp makePlayerRemove.
    private static OutEvent MakePlayerRemove(int id)
    {
        var w = new EventWriter();
        w.PutInt((uint)(id - 2048), 7);
        return w.Finish(0x44c);
    }

    // src/game/player_manager.cpp makeSay.
    private static OutEvent MakeSay(int source, int type, string text)
    {
        var w = new EventWriter();
        w.PutInt((uint)source, 12);
        w.PutInt((uint)type, 5);
        w.PutString(text);
        return w.Finish(0x453);
    }

    // src/game/player_manager.cpp makeSkin.
    private static OutEvent MakeSkin(int id, string skin)
    {
        var w = new EventWriter();
        w.PutInt((uint)(id - 2048), 7);
        w.PutString(skin);
        return w.Finish(0x462);
    }

    private static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    // src/game/player_manager.cpp makeObjective.
    private static OutEvent MakeObjective(int line, string text)
    {
        var w = new EventWriter();
        w.PutInt((uint)line, 32);
        w.PutString(Cut(text, 255));
        return w.Finish(0x459);
    }

    // src/game/player_manager.cpp makeCommand: PlayerCommandEvent describing commanded client t's current command (10,
    // no text).
    private static OutEvent MakeCommand(ClientRecord t)
    {
        var w = new EventWriter();
        w.PutInt((uint)(t.Id - 2048), 10);
        w.PutInt((uint)t.CommandStatus, 2);
        w.PutFlag(false); // ack
        w.PutFlag(t.CommandIcon != -1);
        if (t.CommandIcon != -1)
        {
            w.PutInt((uint)(t.CommandIssuer - 2048), 10);
            w.PutInt((uint)t.CommandIcon, 6);
            w.PutFlag(t.CommandTarget != -1);
            if (t.CommandTarget != -1)
                w.PutInt((uint)t.CommandTarget, 8);
            w.PutInt((uint)t.WaypointX, 10);
            w.PutInt((uint)t.WaypointY, 10);
        }
        w.PutFlag(false); // no text
        return w.Finish(0x455);
    }

    // src/game/player_manager.cpp validSkin.
    private static bool ValidSkin(string s)
    {
        if (s.Length is 0 or > 19)
            return false;
        foreach (var c in s)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')))
                return false;
        return true;
    }

    /// <summary>Finds any client record.</summary>
    /// <param name="id">The client id.</param>
    /// <returns>The record, or null.</returns>
    /// <remarks>Follows 0040de80.</remarks>
    public ClientRecord? Find(int id)
    {
        if (id < 2048 || id >= 2048 + 128 || !mRecords[id - 2048].Used)
            return null;
        return mRecords[id - 2048];
    }

    /// <summary>Finds a human client record.</summary>
    /// <param name="id">The client id.</param>
    /// <returns>The record, or null for an AI player or an unused slot.</returns>
    /// <remarks>Follows 0040de38.</remarks>
    public ClientRecord? FindHuman(int id)
    {
        var r = Find(id);
        return r != null && !r.IsAI ? r : null;
    }

    /// <summary>Lists the human client ids.</summary>
    /// <returns>The ids, newest first.</returns>
    public List<int> Humans()
    {
        var output = new List<int>();
        for (var r = mHumans; r != null; r = r.NextHuman)
            output.Add(r.Id);
        return output;
    }

    // src/game/player_manager.cpp PlayerManager::playerAdd.
    private static OutEvent PlayerAdd(ClientRecord r) =>
        GameEvents.MakePlayerAdd(r.Id, r.IsAI, r.Name, r.Skin, r.Voice, r.Male, r.Commander, r.Team);

    /// <summary>Lists the player ids, humans and AI.</summary>
    /// <returns>The ids, newest first.</returns>
    public List<int> PlayerIds()
    {
        var output = new List<int>();
        for (var r = mPlayers; r != null; r = r.NextPlayer)
            output.Add(r.Id);
        return output;
    }

    /// <summary>Adds a client record (docs/player-manager.md 4.1, 4.2).</summary>
    /// <param name="id">The client id. It comes from the connection layer's LIFO pool (net-protocol.md).</param>
    /// <param name="name">The player name.</param>
    /// <param name="voice">The voice set.</param>
    /// <param name="male">True for a male player.</param>
    /// <param name="ai">True for an AI player.</param>
    /// <returns>The record, or null when none is free.</returns>
    public ClientRecord? AddClient(int id, string name, string voice, bool male, bool ai = false)
    {
        var it = mFree.FindIndex(r => r.Id == id);
        if (it < 0)
            return null; // no free record: rejected (13.3)
        mGame.Console?.Print(1, "MSG: \"" + Builtins.ExportEscape("Server: Player \"" + name + "\" added - id = " + Numbers.FormatInt(id)) + "\"");
        var r = mFree[it];
        mFree.RemoveAt(it);
        r.Used = true;
        r.IsAI = ai;
        r.Name = name;
        r.Voice = StringTable.Global().Intern(voice); // the record keeps the interned string: the first spelling wins
        r.Male = male;
        r.Skin = "base";
        r.Team = -1;
        r.GuiMirror = r.GuiMode = 0;
        r.ObjectiveMark = r.CommandMark = r.CommandStamp = 0;
        r.CommandIcon = -1;
        r.CommandStatus = 0;
        r.LastCommandText = null;
        r.Commander = -1;
        r.MenuScoreVis = r.DataFinished = false;
        r.OwnedObject = r.ControlObject = 0;
        r.ScoreText = "";
        r.ScoreStamp = r.ScoreMark = 0;
        r.Ping = 0;
        r.Loss = 0;
        Array.Clear(r.Shopping);
        Array.Clear(r.Buying);
        r.JoinOrder = mJoinCounter++;

        if (!ai)
        {
            ++mHumanCount;
            if (mGame.Sim?.Server() is SimManager m)
            {
                var services = new ObserverCamera.CameraServices
                {
                    Evaluate = line => mGame.Console?.Evaluate(line),
                    MissionArea = MissionArea,
                    Container = mGame.World,
                };
                // The connection's members (0x0042b1b8, docs/game-stream.md 1.1): the EventManager registers through
                // the root and takes a pool id before the camera does (the reference shows EventManager n, camera
                // n + 1); the PlayerPSC and the GhostManager register with their fixed ids and are named in the group.
                // All three are members of the connection object (the client id), when there is one.
                var group = m.FindById(id)?.AsGroup();
                r.EventManager = new EventManagerObject();
                m.AddToRoot(r.EventManager);
                group?.AddObject(r.EventManager);
                r.PlayerPsc = new PlayerPscObject();
                r.GhostManager = new GhostManagerObject();
                foreach ((var obj, var objName) in new[] { (r.PlayerPsc, "PlayerPSC"), (r.GhostManager, "GhostManager") })
                {
                    m.RegisterObject(obj);
                    var into = group ?? m;
                    into.AddObject(obj);
                    into.AssignName(obj, StringTable.Global().Intern(objName));
                }
                r.Camera = new ObserverCamera(mCameraContext, id, services);
                m.AddToRoot(r.Camera);
            }
        }
        r.LimitCommand = false;
        var older = Humans();
        if (!ai)
        {
            r.NextHuman = mHumans;
            mHumans = r;
            for (var t = -1; t < mGame.TeamCount && t + 1 < mGame.Teams.Count; ++t)
                Post(r.Id, GameEvents.MakeTeamAdd(t, mGame.Teams[t + 1].Name, mGame.Teams[t + 1].Skin));
        }
        var aiOlder = new List<int>();
        for (var p = mPlayers; p != null; p = p.NextPlayer)
            if (p.IsAI)
                aiOlder.Add(p.Id);
        r.NextPlayer = mPlayers;
        mPlayers = r;
        ++mPlayerCount;
        if (ai)
        {
            foreach (var h in older)
                Post(h, PlayerAdd(r)); // docs/player-manager.md 4.1 step 5: an AI record goes to every human
        }
        else
        {
            foreach (var h in older)
            {
                Post(h, PlayerAdd(r));
                Post(r.Id, PlayerAdd(Find(h)!));
            }
            Post(r.Id, PlayerAdd(r));
            foreach (var a in aiOlder)
                Post(r.Id, PlayerAdd(Find(a)!));
        }
        return r;
    }

    /// <summary>Removes a client record.</summary>
    /// <param name="id">The client id.</param>
    public void RemoveClient(int id)
    {
        var r = Find(id);
        if (r == null)
            return;
        var ai = r.IsAI;
        if (mGame.Console != null && !ai)
        {
            mGame.Console.CallFunction("Server::onClientDisconnect", new[] { Numbers.FormatInt(id) });
            if (mGame.Sim?.Server() is SimManager m && r.OwnedObject != 0 && m.FindById(r.OwnedObject) is SimObject owned)
                m.DeleteObject(owned);
            mGame.Console.Print(1, "MSG: \"" + Builtins.ExportEscape("Server: Player \"" + r.Name + "\" dropped - id = " + Numbers.FormatInt(id)) + "\"");
        }
        if (r.Camera != null && mGame.Sim?.Server() is SimManager ms)
        {
            var cam = r.Camera;
            ObjectRemoved(cam); // also when no unregister observer is wired
            ms.DeleteObject(cam);
        }
        r.Camera = null;
        // The connection's members go newest first, as the group's removal unregisters them (RD2).
        if (mGame.Sim?.Server() is SimManager me)
        {
            foreach (var member in new[] { r.GhostManager, r.PlayerPsc, r.EventManager })
                if (member != null)
                    me.DeleteObject(member);
        }
        r.GhostManager = r.PlayerPsc = r.EventManager = null;
        r.OwnedObject = r.ControlObject = 0;
        r.Used = false;
        if (!ai)
        {
            ClientRecord? prev = null;
            var cur = mHumans;
            while (cur != null && cur != r)
            {
                prev = cur;
                cur = cur.NextHuman;
            }
            if (cur != null)
            {
                if (prev == null)
                    mHumans = r.NextHuman;
                else
                    prev.NextHuman = r.NextHuman;
                --mHumanCount;
            }
            r.NextHuman = null;
        }
        {
            ClientRecord? prev = null;
            var cur = mPlayers;
            while (cur != null && cur != r)
            {
                prev = cur;
                cur = cur.NextPlayer;
            }
            if (cur != null)
            {
                if (prev == null)
                    mPlayers = r.NextPlayer;
                else
                    prev.NextPlayer = r.NextPlayer;
            }
            r.NextPlayer = null;
        }
        mFree.Add(r);
        --mPlayerCount;
        for (var h = mHumans; h != null; h = h.NextHuman)
            if (h.Commander == id)
                h.Commander = r.Commander;
        for (var h = mHumans; h != null; h = h.NextHuman)
            Post(h.Id, MakePlayerRemove(id));
    }

    /// <summary>Sets the team of a client (docs/player-manager.md 5).</summary>
    /// <param name="id">The client id.</param>
    /// <param name="team">The team id.</param>
    public void SetTeam(int id, int team)
    {
        var r = Find(id);
        if (r == null || team < -1 || team >= mGame.TeamCount || team == r.Team)
            return;
        r.Team = team;
        if (team + 1 < mGame.Teams.Count)
            r.Skin = mGame.Teams[team + 1].Skin;
        r.ObjectiveMark = r.CommandMark = 0;
        if (!r.IsAI)
            GuiChange(id, r.GuiMirror);
        for (var h = mHumans; h != null; h = h.NextHuman)
        {
            Post(h.Id, MakeTeamChange(id, team));
            if (h.Commander == id)
                h.Commander = r.Commander;
        }
        if (r.OwnedObject != 0 && mGame.Sim?.Server()?.FindById(r.OwnedObject) is GameBase gb)
            gb.SetTeam(team);
        r.Commander = -1;
    }

    /// <summary>Changes the GUI mode of a client (docs/player-manager.md 6).</summary>
    /// <param name="id">The client id.</param>
    /// <param name="mode">The new mode.</param>
    public void GuiChange(int id, int mode)
    {
        var r = FindHuman(id);
        if (r == null || !r.DataFinished)
            return;
        var old = r.GuiMirror;
        if (mode == 2)
        {
            for (var t = mHumans; t != null; t = t.NextHuman)
                if (t != r && t.Team == r.Team && (r.CommandMark < t.CommandStamp || t.CommandTarget != -1))
                    Post(id, MakeCommand(t));
            r.CommandMark = mCommandCounter;
        }
        if (old != 3 && old != 5 && (mode == 3 || mode == 5))
        {
            if (r.Team < -1 || r.Team >= mGame.TeamCount || r.Team + 1 >= mGame.Teams.Count)
                return;
            var team = mGame.Teams[r.Team + 1];
            for (var k = 0; k < 32; ++k)
                if (team.ObjectiveStamp[k] > r.ObjectiveMark)
                    Post(id, MakeObjective(k, team.Objectives[k]));
            r.ObjectiveMark = mObjectiveCounter;
        }
        if (mode == 3 && !r.MenuScoreVis)
        {
            SetMenuScoreVis(id, true);
            SetMenuScoreVis(id, false);
        }
        else if (mode != 3 && old == 3 && r.MenuScoreVis)
        {
            r.GuiMirror = mode;
            SetMenuScoreVis(id, true);
        }
        r.GuiMirror = mode;
    }

    // src/game/player_manager.cpp PlayerManager::scoreChanged.
    private void ScoreChanged(ref uint stamp, bool isTeam, int who, int sort, string text)
    {
        ++mScoreSeq;
        stamp = mScoreSeq;
        for (var h = mHumans; h != null; h = h.NextHuman)
            if (h.MenuScoreVis && h.GuiMirror != 3)
            {
                Post(h.Id, GameEvents.MakeDeltaScore(isTeam, who, sort, text));
                h.ScoreMark = mScoreSeq;
            }
    }

    /// <summary>Sets the score of a client (docs/player-manager.md 7).</summary>
    /// <param name="id">The client id.</param>
    /// <param name="text">The score text.</param>
    /// <param name="sort">The sort value.</param>
    public void SetScore(int id, string text, int sort)
    {
        var r = FindHuman(id);
        var t = Cut(text, 255);
        if (r == null || (r.ScoreSort == sort && r.ScoreText == t))
            return;
        r.ScoreText = t;
        r.ScoreSort = sort;
        ScoreChanged(ref r.ScoreStamp, false, id, sort, t);
    }

    /// <summary>Sets the score of a team.</summary>
    /// <param name="team">The team id.</param>
    /// <param name="text">The score text.</param>
    /// <param name="sort">The sort value.</param>
    public void SetTeamScore(int team, string text, int sort)
    {
        if (team < -1 || team > mGame.TeamCount || team + 1 >= mGame.Teams.Count)
            return;
        var t = mGame.Teams[team + 1];
        var s = Cut(text, 255);
        if (t.SortValue == sort && t.Score == s)
            return;
        t.Score = s;
        t.SortValue = sort;
        ScoreChanged(ref t.ScoreStamp, true, team, sort, s);
    }

    // src/game/player_manager.cpp PlayerManager::flushScores.
    private void FlushScores(ClientRecord c)
    {
        for (var h = mHumans; h != null; h = h.NextHuman)
            if (h.ScoreStamp > c.ScoreMark)
                Post(c.Id, GameEvents.MakeDeltaScore(false, h.Id, h.ScoreSort, h.ScoreText));
        for (var t = 0; t < mGame.TeamCount && t + 1 < mGame.Teams.Count; ++t)
        {
            var team = mGame.Teams[t + 1];
            if (team.ScoreStamp > c.ScoreMark)
                Post(c.Id, GameEvents.MakeDeltaScore(true, t, team.SortValue, team.Score));
        }
        c.ScoreMark = mScoreSeq;
    }

    /// <summary>Sets whether a client shows the score menu.</summary>
    /// <param name="id">The client id.</param>
    /// <param name="visible">True to show it.</param>
    public void SetMenuScoreVis(int id, bool visible)
    {
        var r = FindHuman(id);
        if (r == null || !r.DataFinished)
            return;
        r.MenuScoreVis = visible;
        if (visible && r.GuiMirror != 3)
            FlushScores(r);
    }

    /// <summary>Sets the team or client score heading.</summary>
    /// <param name="team">True for the team heading, false for the client heading.</param>
    /// <param name="text">The heading text.</param>
    public void SetHeading(bool team, string text)
    {
        var t = Cut(text, 255);
        if (team)
            mGame.TeamScoreHeading = t;
        else
            mGame.ClientScoreHeading = t;
        for (var h = mHumans; h != null; h = h.NextHuman)
            Post(h.Id, GameEvents.MakeDeltaScore(team, team ? -1 : 2048, 0, t));
    }

    /// <summary>Sets an objective line of a team (docs/player-manager.md 9).</summary>
    /// <param name="team">The team id.</param>
    /// <param name="line">The objective line.</param>
    /// <param name="text">The objective text.</param>
    public void SetObjective(int team, int line, string text)
    {
        if (team < -1 || team >= mGame.TeamCount || line < 0 || line > 31 || team + 1 >= mGame.Teams.Count)
            return;
        var t = mGame.Teams[team + 1];
        var s = Cut(text, 511);
        if (t.Objectives[line] == s)
            return;
        t.ObjectiveStamp[line] = ++mObjectiveCounter;
        t.Objectives[line] = s;
        for (var h = mHumans; h != null; h = h.NextHuman)
            if (h.Team == team && h.GuiMirror == 5)
            {
                Post(h.Id, MakeObjective(line, s));
                h.ObjectiveMark = mObjectiveCounter;
            }
    }

    /// <summary>Clears the objectives of a team.</summary>
    /// <param name="team">The team id.</param>
    public void ClearObjectives(int team)
    {
        if (team < -1 || team >= mGame.TeamCount || team + 1 >= mGame.Teams.Count)
            return;
        var t = mGame.Teams[team + 1];
        ++mObjectiveCounter; // once per call; every cleared line gets this stamp (0x0040c60c)
        for (var k = 0; k < 32; ++k)
        {
            if (t.Objectives[k].Length == 0)
                continue;
            t.ObjectiveStamp[k] = mObjectiveCounter;
            t.Objectives[k] = "";
            for (var h = mHumans; h != null; h = h.NextHuman)
                if (h.Team == team && h.GuiMirror == 5)
                {
                    Post(h.Id, MakeObjective(k, ""));
                    h.ObjectiveMark = mObjectiveCounter;
                }
        }
    }

    /// <summary>Sets the skin of a client.</summary>
    /// <param name="id">The client id.</param>
    /// <param name="skin">The skin name.</param>
    /// <remarks>Skins are StringTable entries, which compare without case (0x0040be10, 0x00584760 with caseSens 0).</remarks>
    public void SetSkin(int id, string skin)
    {
        var r = FindHuman(id);
        if (r == null || !ValidSkin(skin) || GameUtil.EqualsNoCase(r.Skin, skin))
            return;
        r.Skin = skin;
        for (var h = mHumans; h != null; h = h.NextHuman)
            Post(h.Id, MakeSkin(id, skin));
    }

    /// <summary>Sends a message to a client.</summary>
    /// <param name="id">The client id.</param>
    /// <param name="type">The message type.</param>
    /// <param name="text">The message text.</param>
    /// <param name="source">The id of the source client.</param>
    public void SendMessage(int id, int type, string text, int source)
    {
        if (FindHuman(id) != null)
            Post(id, MakeSay(source == -1 ? 0 : source, type, Cut(text, 255)));
    }

    /// <summary>Sends the TeamAddEvent of a team to every human client (addTeam).</summary>
    /// <param name="team">The team id.</param>
    public void TeamAdded(int team)
    {
        var t = mGame.Teams[team + 1];
        for (var h = mHumans; h != null; h = h.NextHuman)
            Post(h.Id, GameEvents.MakeTeamAdd(team, t.Name, t.Skin));
    }

    /// <summary>Resets the records and sends the MissionResetEvent.</summary>
    public void Reset()
    {
        foreach (var r in mRecords)
        {
            if (!r.Used)
                continue;
            if (!r.IsAI)
                Post(r.Id, new EventWriter().Finish(0x450));
            r.CommandStamp = r.CommandMark = 0;
            r.ScoreStamp = 1;
            r.ScoreMark = 0;
            r.ScoreText = "";
            r.CommandIcon = -1;
            r.CommandStatus = 0;
            r.ObjectiveMark = 0;
        }
        mCommandCounter = 0;
    }

    /// <summary>Starts the ping timer.</summary>
    /// <param name="server">The server manager.</param>
    /// <remarks>The 0x461 timer event is posted to object 713 at sim time + 1.5 when the server is created. Each run does the ping and loss update and posts the next one (docs/player-manager.md 8).</remarks>
    public void StartPingTimer(SimManager server)
    {
        var dest = server.FindById(713);
        server.PostEvent(dest ?? server, AddF(server.Time(), 1.5f), () =>
        {
            UpdatePing();
            StartPingTimer(server);
        });
    }

    /// <summary>Updates the ping and loss of every client and sends them.</summary>
    public void UpdatePing()
    {
        for (var h = mHumans; h != null; h = h.NextHuman)
        {
            uint ping = 0;
            float loss = 0;
            if (NetStats != null)
                (ping, loss) = NetStats(h.Id);
            h.Ping = Math.Min(ping, 999u);
            h.Loss = loss;
        }
        for (var h = mHumans; h != null; h = h.NextHuman)
        {
            if (!h.MenuScoreVis)
                continue;
            var w = new EventWriter();
            w.PutInt((uint)mHumanCount, 7);
            for (var p = mHumans; p != null; p = p.NextHuman)
            {
                w.PutInt((uint)(p.Id - 2048), 7);
                w.PutInt(p.Ping >> 2, 8);
                w.PutInt((uint)Mul(p.Loss, 63), 6);
            }
            Post(h.Id, w.Finish(0x461, false, false));
        }
    }

    /// <summary>Registers the player manager script commands.</summary>
    /// <param name="interp">The script interpreter.</param>
    public void RegisterCommands(Interpreter interp)
    {
        static int IdArg(string[] a, int i) => i < a.Length ? Numbers.BorlandAtoi(a[i]) : 0;

        interp.AddCommand("getNumClients", (_, _) => Numbers.FormatInt(mHumanCount));
        interp.AddCommand("getClientByIndex", (_, a) =>
        {
            if (a.Length != 2)
                return "0";
            var i = Numbers.BorlandAtoi(a[1]);
            if (i < 0 || i >= mHumanCount)
                return "0";
            var c = mHumans!;
            while (i-- > 0)
                c = c.NextHuman!;
            return Numbers.FormatInt(c.Id);
        });
        interp.AddCommand("Client::getFirst", (inr, a) =>
        {
            if (a.Length != 1)
                inr.Print(1, "invalid call to " + a[0] + "()");
            else if (mHumans != null)
                return Numbers.FormatInt(mHumans.Id);
            return "-1";
        });
        interp.AddCommand("Client::getNext", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "invalid call to " + a[0] + "(client)");
                return "-1";
            }
            var c = FindHuman(IdArg(a, 1));
            return c?.NextHuman != null ? Numbers.FormatInt(c.NextHuman.Id) : "-1";
        });
        interp.AddCommand("Client::getName", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to getPlayerName(playerId);");
                return "";
            }
            return Find(IdArg(a, 1))?.Name ?? "";
        });
        interp.AddCommand("Client::getTeam", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to getPlayerName(playerId);");
                return "-1";
            }
            var c = Find(IdArg(a, 1));
            return c != null ? Numbers.FormatInt(c.Team) : "-1";
        });
        foreach (var name in new[] { "Client::getSkinBase", "Client::getVoiceBase", "Client::getGender" })
            interp.AddCommand(name, (inr, a) =>
            {
                if (a.Length != 2)
                {
                    inr.Print(1, "invalid call to " + a[0] + "(client)");
                    return "";
                }
                var c = Find(IdArg(a, 1));
                if (c == null)
                    return "";
                if (GameUtil.EqualsNoCase(a[0], "Client::getSkinBase"))
                    return c.Skin;
                if (GameUtil.EqualsNoCase(a[0], "Client::getVoiceBase"))
                    return c.Voice;
                return c.Male ? "Male" : "Female";
            });
        interp.AddCommand("Client::getGuiMode", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(playerId);");
                return "-1";
            }
            var c = FindHuman(IdArg(a, 1));
            return c != null ? Numbers.FormatInt(c.GuiMode) : "-1";
        });
        interp.AddCommand("Client::setGuiMode", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(playerId,guiId);");
                return "False";
            }
            var c = FindHuman(IdArg(a, 1));
            if (c == null || !c.DataFinished)
                return "False";
            var mode = Numbers.BorlandAtoi(a[2]);
            if (mode >= 0 && mode <= 6 && mode != c.GuiMode)
            {
                c.GuiMode = mode;
                GuiChange(c.Id, mode);
            }
            return "True";
        });
        interp.AddCommand("Client::setMenuScoreVis", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "invalid call to " + a[0] + "()");
                return "0";
            }
            SetMenuScoreVis(IdArg(a, 1), Numbers.ParseBool(a[2]));
            return "1";
        });
        interp.AddCommand("Client::setScore", (inr, a) =>
        {
            if (a.Length != 4)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(playerId, scoreString, sortValue);");
                return "False";
            }
            SetScore(IdArg(a, 1), a[2], Numbers.BorlandAtoi(a[3]));
            return "True";
        });
        interp.AddCommand("Team::setScore", (inr, a) =>
        {
            if (a.Length != 4)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(playerId, scoreString, sortValue);");
                return "False";
            }
            SetTeamScore(Numbers.BorlandAtoi(a[1]), a[2], Numbers.BorlandAtoi(a[3]));
            return "True";
        });
        foreach (var name in new[] { "setTeamScoreHeading", "setClientScoreHeading" })
            interp.AddCommand(name, (inr, a) =>
            {
                if (a.Length != 2)
                {
                    inr.Print(1, "wrong argcount to " + a[0] + "(headingString);");
                    return "False";
                }
                SetHeading(GameUtil.EqualsNoCase(a[0], "setTeamScoreHeading"), a[1]);
                return "True";
            });
        interp.AddCommand("Team::setObjective", (inr, a) =>
        {
            if (a.Length != 4)
            {
                inr.Print(1, "wrong argcount to Team::setObjective(teamId, objNum, text);");
                return "0";
            }
            SetObjective(Numbers.BorlandAtoi(a[1]), Numbers.BorlandAtoi(a[2]), a[3]);
            return "0";
        });
        interp.AddCommand("Team::clearObjectives", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to Team::setObjective(teamId);");
                return "0";
            }
            ClearObjectives(Numbers.BorlandAtoi(a[1]));
            return "0";
        });
        interp.AddCommand("Client::setSkin", (_, a) =>
        {
            if (a.Length != 3)
                return "False";
            SetSkin(IdArg(a, 1), a[2]);
            return "True";
        });
        interp.AddCommand("Client::setInitialTeam", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "wrong argcount to setInitialTeam(playerId, teamId);");
                return "False";
            }
            var team = Numbers.BorlandAtoi(a[2]);
            var c = Find(IdArg(a, 1));
            if (c == null || team < 0 || team >= mGame.TeamCount)
                return "False";
            c.Team = team;
            return "True";
        });
        interp.AddCommand("Client::sendMessage", (_, a) =>
        {
            if (a.Length is not 4 and not 5)
                return "false";
            SendMessage(IdArg(a, 1), Numbers.BorlandAtoi(a[2]), a[3], a.Length == 5 ? Numbers.BorlandAtoi(a[4]) : 0);
            return "True";
        });
        interp.AddCommand("Client::setDataFinished", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "invalid call to " + a[0] + "(client)");
                return "";
            }
            var c = Find(IdArg(a, 1));
            if (c == null)
                return "";
            c.DataFinished = true;
            return "True";
        });
        interp.AddCommand("Client::getObserverCamera", (_, a) =>
        {
            var c = a.Length == 2 ? FindHuman(IdArg(a, 1)) : null;
            return c?.Camera != null ? Numbers.FormatInt(c.Camera.Id()) : "-1";
        });
        interp.AddCommand("Observer::setFlyMode", (_, a) =>
        {
            var c = a.Length == 6 ? FindHuman(IdArg(a, 1)) : null;
            var cam = c?.Camera;
            if (cam == null)
                return "false";
            float[] pos = [0, 0, 0], rot = [0, 0, 0];
            CScan.Floats(a[2], pos);
            CScan.Floats(a[3], rot);
            cam.SetFlyMode(Numbers.ParseBool(a[4]), Numbers.ParseBool(a[5]), rot, pos);
            return "True";
        });
        // Observer::setOrbitObject(client, object, dist, min, max [, rot]) 0043a2fc and Observer::setOrbitPoint(client,
        // pos, dist, min, max [, rot]) 0043a404. More than 7 arguments are accepted; the rotation is read only with
        // exactly 7.
        foreach (var name in new[] { "Observer::setOrbitObject", "Observer::setOrbitPoint" })
            interp.AddCommand(name, (_, a) =>
            {
                if (a.Length < 6)
                    return "false";
                var c = FindHuman(IdArg(a, 1));
                if (c == null)
                    return "false";
                var point = GameUtil.EqualsNoCase(a[0], "Observer::setOrbitPoint");
                var target = point ? null : FindServerObject(a[2]);
                if (c.Camera == null || (!point && target == null))
                    return "false";
                float[] pos = [0, 0, 0], rot = [0, 0, 0];
                if (point)
                    CScan.Floats(a[2], pos);
                var cur = Store(Numbers.BorlandAtof(a[3]));
                var min = Store(Numbers.BorlandAtof(a[4]));
                var max = Store(Numbers.BorlandAtof(a[5]));
                if (a.Length == 7)
                    CScan.Floats(a[6], rot);
                c.Camera.SetOrbit(target, pos, cur, min, max, rot);
                return "True";
            });
        interp.AddCommand("Observer::setDamageObject", (_, a) =>
        {
            var c = a.Length == 3 ? FindHuman(IdArg(a, 1)) : null;
            if (c == null)
                return "false";
            var target = FindServerObject(a[2]);
            if (c.Camera == null || target == null)
                return "false";
            c.Camera.SetDamageObject(target);
            return "true";
        });
        // Client::toggleCMDObserver(bool) 0043a670 drives the local command-map GUI, which a dedicated server does not
        // have.
        interp.AddCommand("Client::toggleCMDObserver", (_, _) => "False");
        interp.AddCommand("Client::limitCommandBandwidth", (_, a) =>
        {
            var c = a.Length == 3 ? FindHuman(IdArg(a, 1)) : null;
            if (c == null)
                return "false";
            c.LimitCommand = Numbers.ParseBool(a[2]);
            return "true";
        });
        // GameBase::getOwnerClient / Player::getClient 0041d548, GameBase::getControlClient 0041d5b4.
        foreach (var name in new[] { "GameBase::getOwnerClient", "Player::getClient", "GameBase::getControlClient" })
            interp.AddCommand(name, (inr, a) =>
            {
                if (a.Length != 2)
                {
                    inr.Print(1, "invalid call to " + a[0] + "(object)");
                    return "-1";
                }
                var o = FindServerObject(a[1]);
                if (o == null)
                    return "-1";
                var control = GameUtil.EqualsNoCase(a[0], "GameBase::getControlClient");
                var client = control ? o.ControlClient : o.OwnerClient;
                return client != 0 ? Numbers.FormatInt(client) : "-1";
            });
        foreach (var name in new[] { "Client::getOwnedObject", "Client::getControlObject" })
            interp.AddCommand(name, (inr, a) =>
            {
                if (a.Length != 2)
                {
                    inr.Print(1, "invalid call to " + a[0] + "(client)");
                    return "-1";
                }
                var owned = GameUtil.EqualsNoCase(a[0], "Client::getOwnedObject");
                var c = owned ? Find(IdArg(a, 1)) : FindHuman(IdArg(a, 1));
                var o = c != null ? (owned ? c.OwnedObject : c.ControlObject) : 0;
                return o != 0 ? Numbers.FormatInt(o) : "-1";
            });
        foreach (var name in new[] { "Client::setOwnedObject", "Client::setControlObject" })
            interp.AddCommand(name, (inr, a) =>
            {
                if (a.Length != 3)
                {
                    inr.Print(1, "invalid call to " + a[0] + "(client, object)");
                    return "false";
                }
                var owned = GameUtil.EqualsNoCase(a[0], "Client::setOwnedObject");
                var c = owned ? Find(IdArg(a, 1)) : FindHuman(IdArg(a, 1));
                if (c == null)
                    return "false";
                // 0041d3b4, 0041d4b8: a server GameBase takes the client; anything else releases the client's current
                // object.
                var o = FindServerObject(a[2]);
                if (o == null)
                {
                    var cur = owned ? c.OwnedObject : c.ControlObject;
                    o = cur != 0 ? mGame.Sim?.Server()?.FindById(cur) as GameBase : null;
                    if (o != null)
                    {
                        if (owned)
                            SetOwnerClient(o, 0);
                        else
                            SetControlClient(o, 0);
                    }
                }
                else if (owned)
                {
                    SetOwnerClient(o, c.Id);
                }
                else
                {
                    SetControlClient(o, c.Id);
                }
                return "true";
            });
    }

    // ---- ownership and control links (12.5) ----

    /// <summary>Resolves a script object argument.</summary>
    /// <param name="arg">The argument: a server GameBase, or a client id 2049 to 2176 naming that client's owned object.</param>
    /// <returns>The object, or null.</returns>
    /// <remarks>Follows 00422d18.</remarks>
    public GameBase? FindServerObject(string arg)
    {
        var m = mGame.Sim?.Server();
        if (m == null)
            return null;
        if (m.Find(arg) is GameBase gb)
            return gb;
        var id = Numbers.BorlandAtoi(arg);
        var c = id is >= 0x801 and <= 0x880 ? Find(id) : null;
        return c != null && c.OwnedObject != 0 ? m.FindById(c.OwnedObject) as GameBase : null;
    }

    /// <summary>Returns the control object of a client.</summary>
    /// <param name="client">The client id.</param>
    /// <returns>The object, or null.</returns>
    public GameBase? ControlObjectOf(int client)
    {
        var r = FindHuman(client);
        var m = mGame.Sim?.Server();
        return r != null && r.ControlObject != 0 && m != null ? m.FindById(r.ControlObject) as GameBase : null;
    }

    /// <summary>Links an object to the client that owns it.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="client">The client id. 0 clears the link.</param>
    /// <remarks>Follows 00448c3c.</remarks>
    public void SetOwnerClient(GameBase obj, int client)
    {
        if (client != 0)
            obj.LastOwnerClient = client;
        if (client == obj.OwnerClient)
            return;
        var m = mGame.Sim?.Server();
        if (Find(obj.OwnerClient) is ClientRecord old && old.OwnedObject == obj.Id())
            old.OwnedObject = 0;
        var r = Find(client);
        if (r != null && r.OwnedObject != 0 && r.OwnedObject != obj.Id() && m?.FindById(r.OwnedObject) is GameBase prev)
            SetOwnerClient(prev, 0);
        r?.OwnedObject = obj.Id();
        obj.OwnerClient = client;
        SetMaskBitsHook?.Invoke(obj, 4);
    }

    /// <summary>Links an object to the client that controls it.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="client">The client id. 0 clears the link.</param>
    /// <remarks>Follows 00448cd4.</remarks>
    public void SetControlClient(GameBase obj, int client)
    {
        if (client == obj.ControlClient)
            return;
        var m = mGame.Sim?.Server();
        if (FindHuman(obj.ControlClient) is ClientRecord old && old.ControlObject == obj.Id())
            old.ControlObject = 0;
        var r = FindHuman(client);
        if (r != null && r.ControlObject != 0 && r.ControlObject != obj.Id() && m?.FindById(r.ControlObject) is GameBase prev)
            SetControlClient(prev, 0);
        r?.ControlObject = obj.Id();
        obj.ControlClient = client;
        SetMaskBitsHook?.Invoke(obj, 4);
    }

    /// <summary>Drops the links to an object that left the server manager.</summary>
    /// <param name="obj">The object.</param>
    /// <remarks>Follows GameBase::onRemove (00448180, delete notifies).</remarks>
    public void ObjectRemoved(SimObject? obj)
    {
        if (obj == null)
            return;
        foreach (var r in mRecords)
        {
            if (!r.Used)
                continue;
            if (r.ControlObject == obj.Id())
                r.ControlObject = 0;
            if (r.OwnedObject == obj.Id())
                r.OwnedObject = 0;
            if (r.Camera != null && r.Camera != obj)
                r.Camera.ObjectDeleted(obj);
        }
        for (var i = 0; i < mGame.Targets.Count; ++i)
            if (mGame.Targets[i] == obj)
                mGame.Targets[i] = null;
    }

    // ---- sensor visibility and the command map (docs/game-stream.md 6.3) ----

    /// <summary>Tells whether an object is visible to a team.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="team">The team id.</param>
    /// <returns>True when bit t of the visibility word (+0x200) is set (detected by the sensor network of team t), or bit t + 8 (always visible to team t, Marker_setTeam).</returns>
    /// <remarks>Follows 0x004495b4.</remarks>
    public static bool VisibleToTeam(GameBase obj, int team)
    {
        var t = (uint)team & 0xff;
        return (obj.SensorWord & ((1u << (int)((t + 8) & 31)) | (1u << (int)(t & 31)))) != 0;
    }

    /// <summary>Builds the 256-bit mask of owned objects and sensor targets visible to a team.</summary>
    /// <param name="team">The team id.</param>
    /// <param name="output">Receives the mask words.</param>
    public void VisibleMask(int team, uint[] output)
    {
        Array.Clear(output, 0, 8);
        var m = mGame.Sim?.Server();
        foreach (var r in mRecords)
        {
            if (!r.Used || r.OwnedObject == 0 || m == null)
                continue;
            if (m.FindById(r.OwnedObject) is GameBase gb && VisibleToTeam(gb, team))
                output[(r.Id - 2048) >> 5] |= 1u << ((r.Id - 2048) & 31);
        }
        for (var i = 0; i < mGame.Targets.Count && i < 128; ++i)
            if (mGame.Targets[i] is GameBase t && VisibleToTeam(t, team))
                output[4 + (i >> 5)] |= 1u << (i & 31);
    }

    /// <summary>Reads the mission area of MissionCenterPos (object 716).</summary>
    /// <param name="output">Receives x, y, w, h, zeroed when there is no mission area.</param>
    /// <returns>False without a mission area.</returns>
    public bool MissionArea(float[] output)
    {
        output[0] = output[1] = output[2] = output[3] = 0;
        var o = mGame.Sim?.Server()?.FindById(716);
        if (o is not PersistObject po || !GameUtil.EqualsNoCase(o.ClassName(), "MissionCenterPos"))
            return false;
        var f = po.Fields();
        output[0] = f.Real("x", 1230.0f);
        output[1] = f.Real("y", 1230.0f);
        output[2] = f.Real("w", 1400.0f);
        output[3] = f.Real("h", 1400.0f);
        return true;
    }

    // src/game/player_manager.cpp ftol of the waypoint code.
    private static uint WaypointFtol(double v) => v is > (-2147483649.0) and < 2147483648.0 ? unchecked((uint)(int)v) : 0x80000000u;

    /// <summary>Updates the waypoint of a targeted command: the target's position in 1/1023 of the mission area while the target is visible to the client's team.</summary>
    /// <param name="r">The client record.</param>
    /// <param name="x">Receives the x coordinate.</param>
    /// <param name="y">Receives the y coordinate.</param>
    /// <returns>True when both coordinates fit in 10 bits and differ from the record, which then takes them.</returns>
    /// <remarks>Follows 0040507c, before the mode blocks.</remarks>
    public bool UpdateWaypoint(ClientRecord r, out uint x, out uint y)
    {
        x = y = 0;
        if (r.CommandIcon == -1 || r.CommandTarget == -1)
            return false;
        var m = mGame.Sim?.Server();
        GameBase? obj;
        if (r.CommandTarget < 0x80)
        {
            var t = Find(r.CommandTarget + 2048);
            obj = t != null && t.OwnedObject != 0 && m != null ? m.FindById(t.OwnedObject) as GameBase : null;
        }
        else
        {
            var i = r.CommandTarget - 128;
            obj = i < mGame.Targets.Count && i < 128 ? mGame.Targets[i] : null;
        }
        if (obj == null || !VisibleToTeam(obj, r.Team))
            return false;
        var area = new float[4];
        MissionArea(area);
        var xy = new uint[2];
        for (var i = 0; i < 2; ++i)
        {
            var v = Div(Mul(Sub(obj.Transform.P[i], area[i]), 1023.0f), area[2 + i]);
            xy[i] = WaypointFtol(v);
        }
        if (((xy[0] | xy[1]) & 0xfffffc00u) != 0 || ((uint)r.WaypointX == xy[0] && (uint)r.WaypointY == xy[1]))
            return false;
        r.WaypointX = (int)xy[0];
        r.WaypointY = (int)xy[1];
        x = xy[0];
        y = xy[1];
        return true;
    }

    /// <summary>Fills the command map slot of a viewer (PSC command mode).</summary>
    /// <param name="viewer">The viewing client.</param>
    /// <param name="slot">The slot index.</param>
    /// <param name="output">Receives the slot.</param>
    /// <returns>False when the slot is absent.</returns>
    public bool CommandSlot(ClientRecord viewer, uint slot, CommandMapSlot output)
    {
        var m = mGame.Sim?.Server();
        GameBase? obj;
        if (slot < 128)
        {
            var r = Find((int)slot + 2048);
            obj = r != null && r.OwnedObject != 0 && m != null ? m.FindById(r.OwnedObject) as GameBase : null;
            if (obj == null || (!VisibleToTeam(obj, viewer.Team) && r != viewer))
                return false;
            output.CommandStatus = (uint)(r!.CommandStatus + 1);
        }
        else
        {
            var i = (int)slot - 128;
            obj = i < mGame.Targets.Count ? mGame.Targets[i] : null;
            if (obj == null || !VisibleToTeam(obj, viewer.Team))
                return false;
        }
        var area = new float[4];
        MissionArea(area);
        var xy = new uint[2];
        for (var i = 0; i < 2; ++i)
        {
            // ((pos - origin) + extent * 0.5) * 1023 / extent, truncated (0040507c).
            var v = Add(Sub(obj.Transform.P[i], area[i]), Mul(area[2 + i], 0.5f));
            v = Div(Mul(v, 1023.0f), area[2 + i]);
            xy[i] = WaypointFtol(v);
        }
        output.X = xy[0];
        output.Y = xy[1];
        output.InRange = ((xy[0] | xy[1]) & 0xfffff800u) == 0;
        output.Team = (uint)obj.Team;
        // vtable +0x128 (status, flag) and +0x148 (docs/game-stream.md 6.3).
        output.Status = 0; // GameBase 00449520: Marker, Trigger, InteriorShape
        output.Active = output.Controllable = false;
        if (GameUtil.EqualsNoCase(obj.ClassName(), "ObserverCamera"))
        {
            output.Status = 2; // 00434a5c
        }
        else if (obj.IsShapeBase() && obj.ShapeClass() != GameBase.ShapeKind.NoShape)
        {
            // StaticBase 0044fcac: damage state - 1 in two bits, the active byte.
            output.Status = (uint)(obj.DamageState - 1) & 3;
            output.Active = obj.Active;
            // Turret 00452ec0: no control client, Enabled and active.
            output.Controllable = obj.ShapeClass() == GameBase.ShapeKind.Turret && obj.ControlClient == 0 &&
                                  obj.DamageState == GameBase.kEnabled && obj.IsActiveFlag();
        }
        else if (obj.IsShapeBase())
        {
            // ShapeBase 0044b518 (Player): damage level, f32 ratio against double 0.7 and 0.3.
            var ratio = DivF(obj.Damage, obj.MaxDamage);
            output.Status = ratio > 0.7 ? 2u : ratio > 0.3 ? 1u : 0u;
            output.Active = true;
        }
        var group = obj.DataBlockGroup();
        output.DbGroup = (uint)group;
        output.DbIndex = output.DbIndexBits = 0;
        if (group >= 0 && group <= 30 && mGame.Sim != null)
        {
            var db = mGame.Sim.DataBlocks();
            var n = (uint)db.GroupSize(group) + 1;
            while (n != 0)
            {
                ++output.DbIndexBits;
                n >>= 1;
            }
            if (obj.DataBlockName != null && db.Find(obj.DataBlockName) is DataBlockManager.Block b)
                output.DbIndex = (uint)b.Index;
        }
        return true;
    }
}
