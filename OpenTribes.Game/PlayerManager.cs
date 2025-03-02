// Port of src/game/player_manager.hpp and src/game/player_manager.cpp: the PlayerManager (object 713): client records,
// team scores and objectives, and the game events they post (docs/player-manager.md).
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

// src/game/player_manager.hpp struct ClientRecord: client record (2.1). Fields marked "kept" survive a slot's reuse.
public sealed class ClientRecord
{
    public bool Used;
    public bool IsAI;
    public int Id;
    public string Name = "", Skin = "base", Voice = "";
    public bool Male = true;
    public int Team = -1;
    public int GuiMirror;           // +0x1c, written by the GUI mode change only
    public int GuiMode;             // PlayerPSC +0x3d8
    public uint ObjectiveMark, CommandMark, CommandStamp;
    public int CommandIcon = -1, CommandStatus;
    public int CommandIssuer, CommandTarget, WaypointX, WaypointY; // kept
    public string? CommandText;     // +0x44, interned, kept
    public string? LastCommandText; // +0xac: text last sent to this client
    public int Commander = -1;
    public int JoinOrder;
    public bool MenuScoreVis;       // +0x6a
    public bool DataFinished;       // +0x1dc
    public int OwnedObject, ControlObject; // object ids (+0x98, +0x94), 0 none
    public ObserverCamera? Camera;  // +0x9c
    public SimObject? EventManager; // the connection's Net::EventManager (id 0054ea34), human clients
    public bool LimitCommand;       // PlayerPSC +0x90
    public string ScoreText = "";
    public int ScoreSort;           // kept
    public uint ScoreStamp, ScoreMark;
    public uint Ping;
    public float Loss;
    public readonly uint[] Shopping = new uint[7], Buying = new uint[7];
    public ClientRecord? NextHuman;  // human list, newest first
    public ClientRecord? NextPlayer; // player list (humans and AI), newest first (+0x8)
}

// src/game/player_manager.cpp EventManagerObject: Net::EventManager as a script-visible object (getObjectType); the event
// queue itself lives in the connection layer.
internal sealed class EventManagerObject : SimObject
{
    public override string ClassName() => "Net::EventManager";
}

// src/game/player_manager.hpp class PlayerManager.
public sealed class PlayerManager
{
    private readonly GameState mGame;
    private readonly ClassContext mCameraContext = new(); // observer cameras register as GameBase objects
    private readonly ClientRecord[] mRecords = new ClientRecord[128];
    private readonly List<ClientRecord> mFree = new(); // LIFO, back is next
    private ClientRecord? mHumans;
    private ClientRecord? mPlayers; // humans and AI, newest first
    private int mHumanCount, mPlayerCount, mJoinCounter;
    private uint mScoreSeq, mObjectiveCounter, mCommandCounter;

    // Event delivery to one connected client (set by the network layer).
    public Action<int, OutEvent> Send = (_, _) => { };
    // Connection ping (ms) and loss (0..1) for the 1.5 s update (8).
    public Func<int, (uint Ping, float Loss)>? NetStats;
    // GameBase mask bits changed (NetObject::setMaskBits); set by the network layer.
    public Action<GameBase, uint>? SetMaskBitsHook;

    // src/game/player_manager.cpp PlayerManager::PlayerManager.
    public PlayerManager(GameState game)
    {
        mGame = game;
        mCameraContext.Game = game;
        for (int i = 0; i < 128; ++i)
            mRecords[i] = new ClientRecord { Id = 2048 + i };
        for (int i = 127; i >= 1; --i)
            mFree.Add(mRecords[i]);
    }

    public int HumanCount() => mHumanCount;
    public int PlayerCount() => mPlayerCount; // humans and AI (+0x5c of the manager)
    // Id the next add takes (head of the free list), 0 without a free record.
    public int NextFreeId() => mFree.Count == 0 ? 0 : mFree[^1].Id;
    // Command counter (+0x50 of the manager), advanced by the command-map commands (command_map.cpp).
    public ref uint CommandCounter() => ref mCommandCounter;
    // Posts an event to a connected client (the network layer's send).
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
        if (s.Length == 0 || s.Length > 19)
            return false;
        foreach (char c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')))
                return false;
        return true;
    }

    // src/game/player_manager.cpp PlayerManager::find: any record (0040de80).
    public ClientRecord? Find(int id)
    {
        if (id < 2048 || id >= 2048 + 128 || !mRecords[id - 2048].Used)
            return null;
        return mRecords[id - 2048];
    }

    // src/game/player_manager.cpp PlayerManager::findHuman: humans only (0040de38).
    public ClientRecord? FindHuman(int id)
    {
        ClientRecord? r = Find(id);
        return r != null && !r.IsAI ? r : null;
    }

    // src/game/player_manager.cpp PlayerManager::humans: newest first.
    public List<int> Humans()
    {
        var output = new List<int>();
        for (ClientRecord? r = mHumans; r != null; r = r.NextHuman)
            output.Add(r.Id);
        return output;
    }

    // src/game/player_manager.cpp PlayerManager::playerAdd.
    private static OutEvent PlayerAdd(ClientRecord r) =>
        GameEvents.MakePlayerAdd(r.Id, r.IsAI, r.Name, r.Skin, r.Voice, r.Male, r.Commander, r.Team);

    // src/game/player_manager.cpp PlayerManager::playerIds: player list, humans and AI, newest first.
    public List<int> PlayerIds()
    {
        var output = new List<int>();
        for (ClientRecord? r = mPlayers; r != null; r = r.NextPlayer)
            output.Add(r.Id);
        return output;
    }

    // src/game/player_manager.cpp PlayerManager::addClient: 4.1 / 4.2. The id comes from the connection layer (same
    // LIFO pool, net-protocol.md).
    public ClientRecord? AddClient(int id, string name, string voice, bool male, bool ai = false)
    {
        int it = mFree.FindIndex(r => r.Id == id);
        if (it < 0)
            return null; // no free record: rejected (13.3)
        mGame.Console?.Print(1, "MSG: \"" + Builtins.ExportEscape("Server: Player \"" + name + "\" added - id = " + Numbers.FormatInt(id)) + "\"");
        ClientRecord r = mFree[it];
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
                    MissionArea = area => MissionArea(area),
                    Container = mGame.World,
                };
                // The connection's EventManager (0x00551a48) is a registered object: it takes a pool id before the
                // camera does (the reference shows EventManager n, camera n + 1).
                r.EventManager = new EventManagerObject();
                m.AddToRoot(r.EventManager);
                r.Camera = new ObserverCamera(mCameraContext, id, services);
                m.AddToRoot(r.Camera);
            }
        }
        r.LimitCommand = false;
        List<int> older = Humans();
        if (!ai)
        {
            r.NextHuman = mHumans;
            mHumans = r;
            for (int t = -1; t < mGame.TeamCount && t + 1 < mGame.Teams.Count; ++t)
                Post(r.Id, GameEvents.MakeTeamAdd(t, mGame.Teams[t + 1].Name, mGame.Teams[t + 1].Skin));
        }
        var aiOlder = new List<int>();
        for (ClientRecord? p = mPlayers; p != null; p = p.NextPlayer)
            if (p.IsAI)
                aiOlder.Add(p.Id);
        r.NextPlayer = mPlayers;
        mPlayers = r;
        ++mPlayerCount;
        if (ai)
        {
            foreach (int h in older)
                Post(h, PlayerAdd(r)); // docs/player-manager.md 4.1 step 5: an AI record goes to every human
        }
        else
        {
            foreach (int h in older)
            {
                Post(h, PlayerAdd(r));
                Post(r.Id, PlayerAdd(Find(h)!));
            }
            Post(r.Id, PlayerAdd(r));
            foreach (int a in aiOlder)
                Post(r.Id, PlayerAdd(Find(a)!));
        }
        return r;
    }

    // src/game/player_manager.cpp PlayerManager::removeClient.
    public void RemoveClient(int id)
    {
        ClientRecord? r = Find(id);
        if (r == null)
            return;
        bool ai = r.IsAI;
        if (mGame.Console != null && !ai)
        {
            mGame.Console.CallFunction("Server::onClientDisconnect", new[] { Numbers.FormatInt(id) });
            if (mGame.Sim?.Server() is SimManager m && r.OwnedObject != 0 && m.FindById(r.OwnedObject) is SimObject owned)
                m.DeleteObject(owned);
            mGame.Console.Print(1, "MSG: \"" + Builtins.ExportEscape("Server: Player \"" + r.Name + "\" dropped - id = " + Numbers.FormatInt(id)) + "\"");
        }
        if (r.Camera != null && mGame.Sim?.Server() is SimManager ms)
        {
            ObserverCamera cam = r.Camera;
            ObjectRemoved(cam); // also when no unregister observer is wired
            ms.DeleteObject(cam);
        }
        r.Camera = null;
        if (r.EventManager != null && mGame.Sim?.Server() is SimManager me)
            me.DeleteObject(r.EventManager);
        r.EventManager = null;
        r.OwnedObject = r.ControlObject = 0;
        r.Used = false;
        if (!ai)
        {
            ClientRecord? prev = null;
            ClientRecord? cur = mHumans;
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
            ClientRecord? cur = mPlayers;
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
        for (ClientRecord? h = mHumans; h != null; h = h.NextHuman)
            if (h.Commander == id)
                h.Commander = r.Commander;
        for (ClientRecord? h = mHumans; h != null; h = h.NextHuman)
            Post(h.Id, MakePlayerRemove(id));
    }

    // src/game/player_manager.cpp PlayerManager::setTeam: 5.
    public void SetTeam(int id, int team)
    {
        ClientRecord? r = Find(id);
        if (r == null || team < -1 || team >= mGame.TeamCount || team == r.Team)
            return;
        r.Team = team;
        if (team + 1 < mGame.Teams.Count)
            r.Skin = mGame.Teams[team + 1].Skin;
        r.ObjectiveMark = r.CommandMark = 0;
        if (!r.IsAI)
            GuiChange(id, r.GuiMirror);
        for (ClientRecord? h = mHumans; h != null; h = h.NextHuman)
        {
            Post(h.Id, MakeTeamChange(id, team));
            if (h.Commander == id)
                h.Commander = r.Commander;
        }
        if (r.OwnedObject != 0 && mGame.Sim?.Server()?.FindById(r.OwnedObject) is GameBase gb)
            gb.SetTeam(team);
        r.Commander = -1;
    }

    // src/game/player_manager.cpp PlayerManager::guiChange: 6.
    public void GuiChange(int id, int mode)
    {
        ClientRecord? r = FindHuman(id);
        if (r == null || !r.DataFinished)
            return;
        int old = r.GuiMirror;
        if (mode == 2)
        {
            for (ClientRecord? t = mHumans; t != null; t = t.NextHuman)
                if (t != r && t.Team == r.Team && (r.CommandMark < t.CommandStamp || t.CommandTarget != -1))
                    Post(id, MakeCommand(t));
            r.CommandMark = mCommandCounter;
        }
        if (old != 3 && old != 5 && (mode == 3 || mode == 5))
        {
            if (r.Team < -1 || r.Team >= mGame.TeamCount || r.Team + 1 >= mGame.Teams.Count)
                return;
            TeamRecord team = mGame.Teams[r.Team + 1];
            for (int k = 0; k < 32; ++k)
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
        for (ClientRecord? h = mHumans; h != null; h = h.NextHuman)
            if (h.MenuScoreVis && h.GuiMirror != 3)
            {
                Post(h.Id, GameEvents.MakeDeltaScore(isTeam, who, sort, text));
                h.ScoreMark = mScoreSeq;
            }
    }

    // src/game/player_manager.cpp PlayerManager::setScore: 7.
    public void SetScore(int id, string text, int sort)
    {
        ClientRecord? r = FindHuman(id);
        string t = Cut(text, 255);
        if (r == null || (r.ScoreSort == sort && r.ScoreText == t))
            return;
        r.ScoreText = t;
        r.ScoreSort = sort;
        ScoreChanged(ref r.ScoreStamp, false, id, sort, t);
    }

    // src/game/player_manager.cpp PlayerManager::setTeamScore.
    public void SetTeamScore(int team, string text, int sort)
    {
        if (team < -1 || team > mGame.TeamCount || team + 1 >= mGame.Teams.Count)
            return;
        TeamRecord t = mGame.Teams[team + 1];
        string s = Cut(text, 255);
        if (t.SortValue == sort && t.Score == s)
            return;
        t.Score = s;
        t.SortValue = sort;
        ScoreChanged(ref t.ScoreStamp, true, team, sort, s);
    }

    // src/game/player_manager.cpp PlayerManager::flushScores.
    private void FlushScores(ClientRecord c)
    {
        for (ClientRecord? h = mHumans; h != null; h = h.NextHuman)
            if (h.ScoreStamp > c.ScoreMark)
                Post(c.Id, GameEvents.MakeDeltaScore(false, h.Id, h.ScoreSort, h.ScoreText));
        for (int t = 0; t < mGame.TeamCount && t + 1 < mGame.Teams.Count; ++t)
        {
            TeamRecord team = mGame.Teams[t + 1];
            if (team.ScoreStamp > c.ScoreMark)
                Post(c.Id, GameEvents.MakeDeltaScore(true, t, team.SortValue, team.Score));
        }
        c.ScoreMark = mScoreSeq;
    }

    // src/game/player_manager.cpp PlayerManager::setMenuScoreVis.
    public void SetMenuScoreVis(int id, bool visible)
    {
        ClientRecord? r = FindHuman(id);
        if (r == null || !r.DataFinished)
            return;
        r.MenuScoreVis = visible;
        if (visible && r.GuiMirror != 3)
            FlushScores(r);
    }

    // src/game/player_manager.cpp PlayerManager::setHeading.
    public void SetHeading(bool team, string text)
    {
        string t = Cut(text, 255);
        if (team)
            mGame.TeamScoreHeading = t;
        else
            mGame.ClientScoreHeading = t;
        for (ClientRecord? h = mHumans; h != null; h = h.NextHuman)
            Post(h.Id, GameEvents.MakeDeltaScore(team, team ? -1 : 2048, 0, t));
    }

    // src/game/player_manager.cpp PlayerManager::setObjective: 9.
    public void SetObjective(int team, int line, string text)
    {
        if (team < -1 || team >= mGame.TeamCount || line < 0 || line > 31 || team + 1 >= mGame.Teams.Count)
            return;
        TeamRecord t = mGame.Teams[team + 1];
        string s = Cut(text, 511);
        if (t.Objectives[line] == s)
            return;
        t.ObjectiveStamp[line] = ++mObjectiveCounter;
        t.Objectives[line] = s;
        for (ClientRecord? h = mHumans; h != null; h = h.NextHuman)
            if (h.Team == team && h.GuiMirror == 5)
            {
                Post(h.Id, MakeObjective(line, s));
                h.ObjectiveMark = mObjectiveCounter;
            }
    }

    // src/game/player_manager.cpp PlayerManager::clearObjectives.
    public void ClearObjectives(int team)
    {
        if (team < -1 || team >= mGame.TeamCount || team + 1 >= mGame.Teams.Count)
            return;
        TeamRecord t = mGame.Teams[team + 1];
        ++mObjectiveCounter; // once per call; every cleared line gets this stamp (0x0040c60c)
        for (int k = 0; k < 32; ++k)
        {
            if (t.Objectives[k].Length == 0)
                continue;
            t.ObjectiveStamp[k] = mObjectiveCounter;
            t.Objectives[k] = "";
            for (ClientRecord? h = mHumans; h != null; h = h.NextHuman)
                if (h.Team == team && h.GuiMirror == 5)
                {
                    Post(h.Id, MakeObjective(k, ""));
                    h.ObjectiveMark = mObjectiveCounter;
                }
        }
    }

    // src/game/player_manager.cpp PlayerManager::setSkin: skins are StringTable entries, which compare without case
    // (0x0040be10, 0x00584760 caseSens 0).
    public void SetSkin(int id, string skin)
    {
        ClientRecord? r = FindHuman(id);
        if (r == null || !ValidSkin(skin) || GameUtil.EqualsNoCase(r.Skin, skin))
            return;
        r.Skin = skin;
        for (ClientRecord? h = mHumans; h != null; h = h.NextHuman)
            Post(h.Id, MakeSkin(id, skin));
    }

    // src/game/player_manager.cpp PlayerManager::sendMessage.
    public void SendMessage(int id, int type, string text, int source)
    {
        if (FindHuman(id) != null)
            Post(id, MakeSay(source == -1 ? 0 : source, type, Cut(text, 255)));
    }

    // src/game/player_manager.cpp PlayerManager::teamAdded: TeamAddEvent of team t to every human (addTeam).
    public void TeamAdded(int team)
    {
        TeamRecord t = mGame.Teams[team + 1];
        for (ClientRecord? h = mHumans; h != null; h = h.NextHuman)
            Post(h.Id, GameEvents.MakeTeamAdd(team, t.Name, t.Skin));
    }

    // src/game/player_manager.cpp PlayerManager::reset: MissionResetEvent and record reset.
    public void Reset()
    {
        foreach (ClientRecord r in mRecords)
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

    // src/game/player_manager.cpp PlayerManager::startPingTimer: 8: the 0x461 timer event. Posted to object 713 at sim
    // time + 1.5 when the server is created; each run does the ping/loss update and posts the next one.
    public void StartPingTimer(SimManager server)
    {
        SimObject? dest = server.FindById(713);
        server.PostEvent(dest ?? server, AddF(server.Time(), 1.5f), () =>
        {
            UpdatePing();
            StartPingTimer(server);
        });
    }

    // src/game/player_manager.cpp PlayerManager::updatePing.
    public void UpdatePing()
    {
        for (ClientRecord? h = mHumans; h != null; h = h.NextHuman)
        {
            uint ping = 0;
            float loss = 0;
            if (NetStats != null)
                (ping, loss) = NetStats(h.Id);
            h.Ping = Math.Min(ping, 999u);
            h.Loss = loss;
        }
        for (ClientRecord? h = mHumans; h != null; h = h.NextHuman)
        {
            if (!h.MenuScoreVis)
                continue;
            var w = new EventWriter();
            w.PutInt((uint)mHumanCount, 7);
            for (ClientRecord? p = mHumans; p != null; p = p.NextHuman)
            {
                w.PutInt((uint)(p.Id - 2048), 7);
                w.PutInt(p.Ping >> 2, 8);
                w.PutInt((uint)Mul(p.Loss, 63), 6);
            }
            Post(h.Id, w.Finish(0x461, false, false));
        }
    }

    // src/game/player_manager.cpp PlayerManager::registerCommands.
    public void RegisterCommands(Interpreter interp)
    {
        static int IdArg(string[] a, int i) => i < a.Length ? Numbers.BorlandAtoi(a[i]) : 0;

        interp.AddCommand("getNumClients", (_, _) => Numbers.FormatInt(mHumanCount));
        interp.AddCommand("getClientByIndex", (_, a) =>
        {
            if (a.Length != 2)
                return "0";
            int i = Numbers.BorlandAtoi(a[1]);
            if (i < 0 || i >= mHumanCount)
                return "0";
            ClientRecord c = mHumans!;
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
            ClientRecord? c = FindHuman(IdArg(a, 1));
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
            ClientRecord? c = Find(IdArg(a, 1));
            return c != null ? Numbers.FormatInt(c.Team) : "-1";
        });
        foreach (string name in new[] { "Client::getSkinBase", "Client::getVoiceBase", "Client::getGender" })
            interp.AddCommand(name, (inr, a) =>
            {
                if (a.Length != 2)
                {
                    inr.Print(1, "invalid call to " + a[0] + "(client)");
                    return "";
                }
                ClientRecord? c = Find(IdArg(a, 1));
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
            ClientRecord? c = FindHuman(IdArg(a, 1));
            return c != null ? Numbers.FormatInt(c.GuiMode) : "-1";
        });
        interp.AddCommand("Client::setGuiMode", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(playerId,guiId);");
                return "False";
            }
            ClientRecord? c = FindHuman(IdArg(a, 1));
            if (c == null || !c.DataFinished)
                return "False";
            int mode = Numbers.BorlandAtoi(a[2]);
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
        foreach (string name in new[] { "setTeamScoreHeading", "setClientScoreHeading" })
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
            int team = Numbers.BorlandAtoi(a[2]);
            ClientRecord? c = Find(IdArg(a, 1));
            if (c == null || team < 0 || team >= mGame.TeamCount)
                return "False";
            c.Team = team;
            return "True";
        });
        interp.AddCommand("Client::sendMessage", (_, a) =>
        {
            if (a.Length != 4 && a.Length != 5)
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
            ClientRecord? c = Find(IdArg(a, 1));
            if (c == null)
                return "";
            c.DataFinished = true;
            return "True";
        });
        interp.AddCommand("Client::getObserverCamera", (_, a) =>
        {
            ClientRecord? c = a.Length == 2 ? FindHuman(IdArg(a, 1)) : null;
            return c?.Camera != null ? Numbers.FormatInt(c.Camera.Id()) : "-1";
        });
        interp.AddCommand("Observer::setFlyMode", (_, a) =>
        {
            ClientRecord? c = a.Length == 6 ? FindHuman(IdArg(a, 1)) : null;
            ObserverCamera? cam = c?.Camera;
            if (cam == null)
                return "false";
            float[] pos = { 0, 0, 0 }, rot = { 0, 0, 0 };
            CScan.Floats(a[2], pos);
            CScan.Floats(a[3], rot);
            cam.SetFlyMode(Numbers.ParseBool(a[4]), Numbers.ParseBool(a[5]), rot, pos);
            return "True";
        });
        // Observer::setOrbitObject(client, object, dist, min, max [, rot]) 0043a2fc and Observer::setOrbitPoint(client,
        // pos, dist, min, max [, rot]) 0043a404. More than 7 arguments are accepted; the rotation is read only with
        // exactly 7.
        foreach (string name in new[] { "Observer::setOrbitObject", "Observer::setOrbitPoint" })
            interp.AddCommand(name, (_, a) =>
            {
                if (a.Length < 6)
                    return "false";
                ClientRecord? c = FindHuman(IdArg(a, 1));
                if (c == null)
                    return "false";
                bool point = GameUtil.EqualsNoCase(a[0], "Observer::setOrbitPoint");
                GameBase? target = point ? null : FindServerObject(a[2]);
                if (c.Camera == null || (!point && target == null))
                    return "false";
                float[] pos = { 0, 0, 0 }, rot = { 0, 0, 0 };
                if (point)
                    CScan.Floats(a[2], pos);
                float cur = Store(Numbers.BorlandAtof(a[3]));
                float min = Store(Numbers.BorlandAtof(a[4]));
                float max = Store(Numbers.BorlandAtof(a[5]));
                if (a.Length == 7)
                    CScan.Floats(a[6], rot);
                c.Camera.SetOrbit(target, pos, cur, min, max, rot);
                return "True";
            });
        interp.AddCommand("Observer::setDamageObject", (_, a) =>
        {
            ClientRecord? c = a.Length == 3 ? FindHuman(IdArg(a, 1)) : null;
            if (c == null)
                return "false";
            GameBase? target = FindServerObject(a[2]);
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
            ClientRecord? c = a.Length == 3 ? FindHuman(IdArg(a, 1)) : null;
            if (c == null)
                return "false";
            c.LimitCommand = Numbers.ParseBool(a[2]);
            return "true";
        });
        // GameBase::getOwnerClient / Player::getClient 0041d548, GameBase::getControlClient 0041d5b4.
        foreach (string name in new[] { "GameBase::getOwnerClient", "Player::getClient", "GameBase::getControlClient" })
            interp.AddCommand(name, (inr, a) =>
            {
                if (a.Length != 2)
                {
                    inr.Print(1, "invalid call to " + a[0] + "(object)");
                    return "-1";
                }
                GameBase? o = FindServerObject(a[1]);
                if (o == null)
                    return "-1";
                bool control = GameUtil.EqualsNoCase(a[0], "GameBase::getControlClient");
                int client = control ? o.ControlClient : o.OwnerClient;
                return client != 0 ? Numbers.FormatInt(client) : "-1";
            });
        foreach (string name in new[] { "Client::getOwnedObject", "Client::getControlObject" })
            interp.AddCommand(name, (inr, a) =>
            {
                if (a.Length != 2)
                {
                    inr.Print(1, "invalid call to " + a[0] + "(client)");
                    return "-1";
                }
                bool owned = GameUtil.EqualsNoCase(a[0], "Client::getOwnedObject");
                ClientRecord? c = owned ? Find(IdArg(a, 1)) : FindHuman(IdArg(a, 1));
                int o = c != null ? (owned ? c.OwnedObject : c.ControlObject) : 0;
                return o != 0 ? Numbers.FormatInt(o) : "-1";
            });
        foreach (string name in new[] { "Client::setOwnedObject", "Client::setControlObject" })
            interp.AddCommand(name, (inr, a) =>
            {
                if (a.Length != 3)
                {
                    inr.Print(1, "invalid call to " + a[0] + "(client, object)");
                    return "false";
                }
                bool owned = GameUtil.EqualsNoCase(a[0], "Client::setOwnedObject");
                ClientRecord? c = owned ? Find(IdArg(a, 1)) : FindHuman(IdArg(a, 1));
                if (c == null)
                    return "false";
                // 0041d3b4, 0041d4b8: a server GameBase takes the client; anything else releases the client's current
                // object.
                GameBase? o = FindServerObject(a[2]);
                if (o == null)
                {
                    int cur = owned ? c.OwnedObject : c.ControlObject;
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

    // src/game/player_manager.cpp PlayerManager::findServerObject: script object argument (00422d18): a server GameBase,
    // else a client id 2049..2176 naming that client's owned object.
    public GameBase? FindServerObject(string arg)
    {
        SimManager? m = mGame.Sim?.Server();
        if (m == null)
            return null;
        if (m.Find(arg) is GameBase gb)
            return gb;
        int id = Numbers.BorlandAtoi(arg);
        ClientRecord? c = id >= 0x801 && id <= 0x880 ? Find(id) : null;
        return c != null && c.OwnedObject != 0 ? m.FindById(c.OwnedObject) as GameBase : null;
    }

    // src/game/player_manager.cpp PlayerManager::controlObjectOf.
    public GameBase? ControlObjectOf(int client)
    {
        ClientRecord? r = FindHuman(client);
        SimManager? m = mGame.Sim?.Server();
        return r != null && r.ControlObject != 0 && m != null ? m.FindById(r.ControlObject) as GameBase : null;
    }

    // src/game/player_manager.cpp PlayerManager::setOwnerClient: 00448c3c. client 0 clears the link.
    public void SetOwnerClient(GameBase obj, int client)
    {
        if (client != 0)
            obj.LastOwnerClient = client;
        if (client == obj.OwnerClient)
            return;
        SimManager? m = mGame.Sim?.Server();
        if (Find(obj.OwnerClient) is ClientRecord old && old.OwnedObject == obj.Id())
            old.OwnedObject = 0;
        ClientRecord? r = Find(client);
        if (r != null && r.OwnedObject != 0 && r.OwnedObject != obj.Id() && m?.FindById(r.OwnedObject) is GameBase prev)
            SetOwnerClient(prev, 0);
        if (r != null)
            r.OwnedObject = obj.Id();
        obj.OwnerClient = client;
        SetMaskBitsHook?.Invoke(obj, 4);
    }

    // src/game/player_manager.cpp PlayerManager::setControlClient: 00448cd4.
    public void SetControlClient(GameBase obj, int client)
    {
        if (client == obj.ControlClient)
            return;
        SimManager? m = mGame.Sim?.Server();
        if (FindHuman(obj.ControlClient) is ClientRecord old && old.ControlObject == obj.Id())
            old.ControlObject = 0;
        ClientRecord? r = FindHuman(client);
        if (r != null && r.ControlObject != 0 && r.ControlObject != obj.Id() && m?.FindById(r.ControlObject) is GameBase prev)
            SetControlClient(prev, 0);
        if (r != null)
            r.ControlObject = obj.Id();
        obj.ControlClient = client;
        SetMaskBitsHook?.Invoke(obj, 4);
    }

    // src/game/player_manager.cpp PlayerManager::objectRemoved: an object leaves the server manager (GameBase::onRemove
    // 00448180, delete notifies).
    public void ObjectRemoved(SimObject? obj)
    {
        if (obj == null)
            return;
        foreach (ClientRecord r in mRecords)
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
        for (int i = 0; i < mGame.Targets.Count; ++i)
            if (mGame.Targets[i] == obj)
                mGame.Targets[i] = null;
    }

    // ---- sensor visibility and the command map (docs/game-stream.md 6.3) ----

    // src/game/player_manager.cpp PlayerManager::visibleToTeam: 0x004495b4: bit t (detected by team t's sensor network) or
    // bit t + 8 (always visible to team t, Marker_setTeam) of the visibility word (+0x200).
    public bool VisibleToTeam(GameBase obj, int team)
    {
        uint t = (uint)team & 0xff;
        return (obj.SensorWord & ((1u << (int)((t + 8) & 31)) | (1u << (int)(t & 31)))) != 0;
    }

    // src/game/player_manager.cpp PlayerManager::visibleMask: the 256-bit mask of owned objects and sensor targets.
    public void VisibleMask(int team, uint[] output)
    {
        Array.Clear(output, 0, 8);
        SimManager? m = mGame.Sim?.Server();
        foreach (ClientRecord r in mRecords)
        {
            if (!r.Used || r.OwnedObject == 0 || m == null)
                continue;
            if (m.FindById(r.OwnedObject) is GameBase gb && VisibleToTeam(gb, team))
                output[(r.Id - 2048) >> 5] |= 1u << ((r.Id - 2048) & 31);
        }
        for (int i = 0; i < mGame.Targets.Count && i < 128; ++i)
            if (mGame.Targets[i] is GameBase t && VisibleToTeam(t, team))
                output[4 + (i >> 5)] |= 1u << (i & 31);
    }

    // src/game/player_manager.cpp PlayerManager::missionArea: MissionCenterPos (object 716): x, y, w, h. False without one
    // (out is zeroed).
    public bool MissionArea(float[] output)
    {
        output[0] = output[1] = output[2] = output[3] = 0;
        SimObject? o = mGame.Sim?.Server()?.FindById(716);
        if (o is not PersistObject po || !GameUtil.EqualsNoCase(o.ClassName(), "MissionCenterPos"))
            return false;
        PersistFields f = po.Fields();
        output[0] = f.Real("x", 1230.0f);
        output[1] = f.Real("y", 1230.0f);
        output[2] = f.Real("w", 1400.0f);
        output[3] = f.Real("h", 1400.0f);
        return true;
    }

    // src/game/player_manager.cpp ftol of the waypoint code.
    private static uint WaypointFtol(double v) => v > -2147483649.0 && v < 2147483648.0 ? unchecked((uint)(int)v) : 0x80000000u;

    // src/game/player_manager.cpp PlayerManager::updateWaypoint: waypoint of a targeted command (0040507c, before the mode
    // blocks): the target's position in 1/1023 of the mission area while the target is visible to the client's team.
    // True when both coordinates fit in 10 bits and differ from the record, which then takes them.
    public bool UpdateWaypoint(ClientRecord r, out uint x, out uint y)
    {
        x = y = 0;
        if (r.CommandIcon == -1 || r.CommandTarget == -1)
            return false;
        SimManager? m = mGame.Sim?.Server();
        GameBase? obj;
        if (r.CommandTarget < 0x80)
        {
            ClientRecord? t = Find(r.CommandTarget + 2048);
            obj = t != null && t.OwnedObject != 0 && m != null ? m.FindById(t.OwnedObject) as GameBase : null;
        }
        else
        {
            int i = r.CommandTarget - 128;
            obj = i < mGame.Targets.Count && i < 128 ? mGame.Targets[i] : null;
        }
        if (obj == null || !VisibleToTeam(obj, r.Team))
            return false;
        float[] area = new float[4];
        MissionArea(area);
        uint[] xy = new uint[2];
        for (int i = 0; i < 2; ++i)
        {
            double v = Div(Mul(Sub(obj.Transform.P[i], area[i]), 1023.0f), area[2 + i]);
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

    // src/game/player_manager.cpp PlayerManager::commandSlot: command map slot of a viewer (PSC command mode); false when
    // the slot is absent.
    public bool CommandSlot(ClientRecord viewer, uint slot, CommandMapSlot output)
    {
        SimManager? m = mGame.Sim?.Server();
        GameBase? obj;
        if (slot < 128)
        {
            ClientRecord? r = Find((int)slot + 2048);
            obj = r != null && r.OwnedObject != 0 && m != null ? m.FindById(r.OwnedObject) as GameBase : null;
            if (obj == null || (!VisibleToTeam(obj, viewer.Team) && r != viewer))
                return false;
            output.CommandStatus = (uint)(r!.CommandStatus + 1);
        }
        else
        {
            int i = (int)slot - 128;
            obj = i < mGame.Targets.Count ? mGame.Targets[i] : null;
            if (obj == null || !VisibleToTeam(obj, viewer.Team))
                return false;
        }
        float[] area = new float[4];
        MissionArea(area);
        uint[] xy = new uint[2];
        for (int i = 0; i < 2; ++i)
        {
            // ((pos - origin) + extent * 0.5) * 1023 / extent, truncated (0040507c).
            double v = Add(Sub(obj.Transform.P[i], area[i]), Mul(area[2 + i], 0.5f));
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
            float ratio = DivF(obj.Damage, obj.MaxDamage);
            output.Status = ratio > 0.7 ? 2u : ratio > 0.3 ? 1u : 0u;
            output.Active = true;
        }
        int group = obj.DataBlockGroup();
        output.DbGroup = (uint)group;
        output.DbIndex = output.DbIndexBits = 0;
        if (group >= 0 && group <= 30 && mGame.Sim != null)
        {
            DataBlockManager db = mGame.Sim.DataBlocks();
            uint n = (uint)db.GroupSize(group) + 1;
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
