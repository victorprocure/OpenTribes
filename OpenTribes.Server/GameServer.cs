// Port of src/game/game_server.hpp and src/game/game_server.cpp: the server side of client connections: client objects,
// the join sequence (docs/game-stream.md 5.1) and remote evals in both directions. It lives in OpenTribes.Server because
// it drives the NetServer of this project (the C# project graph is acyclic; OpenTribes.Game does not reference the
// server).
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Net;
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Server;

// src/game/game_server.hpp class GameServer.
public sealed class GameServer
{
    // src/game/game_server.cpp ClientObject: the object a client id names in scripts. Its id is the connection's player
    // id.
    private sealed class ClientObject : SimGroup
    {
        private readonly int mId;
        public ClientObject(int id) => mId = id;
        public override string ClassName() => "Net::PacketStream";
        public override int FixedId() => mId;
    }

    // src/game/game_server.hpp GameServer::Client.
    private sealed class Client
    {
        public GameConnection? Stream;
        public SimObject? Object; // the client id object scripts use (%clientId.field)
        public string Name = "", Address = "";
        public Connection? Vc;
    }

    private readonly GameState mGame;
    private readonly Interpreter mIn;
    private readonly NetServer mNet;
    private readonly SortedDictionary<int, Client> mClients = new(); // join order by id is not guaranteed; mOrder keeps it
    private readonly List<int> mOrder = new();

    // src/game/game_server.cpp collectAlways: set 34: objects with net flags 0x40 and 0x100 (docs/game-stream-ghosts.md
    // 1.1).
    private static void CollectAlways(SimObject obj, List<SimObject> output)
    {
        uint f = GhostScope.NetFlags(obj);
        if ((f & GhostScope.kNetGhostable) != 0 && (f & GhostScope.kNetScopeAlways) != 0)
            output.Add(obj);
        if (obj.AsSet() is SimSet set)
            foreach (SimObject m in set.Members())
                CollectAlways(m, output);
    }

    // src/game/game_server.cpp GameServer::GameServer.
    // claimGlobals: the ghost priority container and the sound routes are process-wide hooks; the one world that talks to
    // clients owns them (a world that is only stepped, like the prediction world of a client, leaves them alone).
    public GameServer(GameState game, Interpreter interp, NetServer net, bool claimGlobals = true)
    {
        mGame = game;
        mIn = interp;
        mNet = net;
        GhostScope.RegisterPriorityVariables(mIn);
        if (claimGlobals)
            GhostScope.SetPriorityContainer(mGame.World);
        GhostScope.RegisterGhostPriorities();
        var hooks = new NetHooks
        {
            OnAccepted = Accepted,
            OnData = (p, d) =>
            {
                if (!mClients.TryGetValue((int)p.Id, out Client? c))
                    return;
                c.Stream!.Receive(d);
                // An invalid member block ends the connection (docs/game-stream.md 1.6).
                if (c.Stream.Invalid())
                    mNet.DropInvalid(p.Id);
            },
            OnNotify = (p, cookie, delivered) =>
            {
                if (mClients.TryGetValue((int)p.Id, out Client? c))
                    c.Stream!.Notify(cookie, delivered);
            },
            OnClosed = Closed,
            OnTick = now =>
            {
                foreach (int id in mOrder.ToList())
                {
                    if (!mClients.TryGetValue(id, out Client? c))
                        continue;
                    if (mGame.Players.FindHuman(id) is ClientRecord r)
                    {
                        c.Stream!.GuiMode = (uint)r.GuiMode;
                        c.Stream.Flag6a = r.MenuScoreVis;
                    }
                    // $pref::PacketRate 1-30 (unsigned compare) and $pref::PacketSize 100-450, defaults 10 and 200
                    // (GameConnection_loadPacketPrefs 0x00550c68, same clamps in the 1.11 exe; the 1.30 exe uses 10-30 and 200-450; docs/game-stream.md 1.3). A change is sent to the
                    // client as rateReq.
                    string rateText = mIn.GetVariable("$pref::PacketRate", out bool haveRate);
                    string sizeText = mIn.GetVariable("$pref::PacketSize", out bool haveSize);
                    uint rate = haveRate ? unchecked((uint)Numbers.BorlandAtoi(rateText)) : 10u;
                    int bytes = haveSize ? Numbers.BorlandAtoi(sizeText) : 200;
                    rate = rate > 30 ? 30u : (rate < 1 ? 1u : rate);
                    bytes = bytes > 450 ? 450 : (bytes < 100 ? 100 : bytes);
                    c.Stream!.SetOwnRate(1000u / rate, (uint)bytes);
                    c.Stream.Tick(now);
                }
            },
        };
        mNet.SetHooks(hooks);
        if (mGame.Sim?.Server() is SimManager m)
        {
            // NetObject_onAdd (0050a420): an object with net flags 0x40 and 0x100 joins set 34 and is marked in scope in
            // every ghost manager, so one created while clients are connected reaches them.
            m.OnRegister = obj =>
            {
                uint f = GhostScope.NetFlags(obj);
                if ((f & GhostScope.kNetGhostable) == 0 || (f & GhostScope.kNetScopeAlways) == 0)
                    return;
                foreach (var c in mClients)
                    if (c.Value.Stream != null && ((f & GhostScope.kNetLocalOnly) == 0 || c.Key == 2049))
                        c.Value.Stream.Ghosts().MarkInScope(obj, true);
            };
            m.OnUnregister = obj =>
            {
                // A SimPath leaving tells every client to drop its path (SimPathManager, 0052b8cc).
                if (GameUtil.EqualsNoCase(obj.ClassName(), "SimPath"))
                {
                    int index = mGame.PathIndex(obj);
                    if (index >= 0)
                        foreach (var c in mClients)
                            c.Value.Stream!.Post(GameEvents.MakePathRemoved((uint)index));
                }
                foreach (var c in mClients)
                    c.Value.Stream!.Ghosts().ObjectRemoved(obj);
                mGame.Players.ObjectRemoved(obj);
            };
        }
        mGame.Players.SetMaskBitsHook = (obj, bits) =>
        {
            foreach (var c in mClients)
                c.Value.Stream!.Ghosts().SetMaskBits(obj, bits);
        };
        mGame.MaskBits = (obj, bits) =>
        {
            foreach (var c in mClients)
                c.Value.Stream?.Ghosts().SetMaskBits(obj, bits);
        };
        // Sound routing (command_map.cpp): the ghost index of an object and the scope object of a client.
        if (claimGlobals)
        {
            SoundRoute.GhostIndex = (id, obj) =>
                mClients.TryGetValue(id, out Client? c) && c.Stream != null ? c.Stream.Ghosts().GhostIndex(obj) : -1;
            SoundRoute.ScopeObject = id => mClients.TryGetValue(id, out Client? c) && c.Stream != null ? c.Stream.Ghosts().ScopeObject() : null;
        }
        mGame.Players.Send = (id, e) =>
        {
            if (mClients.TryGetValue(id, out Client? c))
                c.Stream!.Post(e);
        };
        mGame.Players.NetStats = id =>
        {
            if (mClients.TryGetValue(id, out Client? c) && c.Vc != null)
                return (c.Vc.RttMs(), c.Vc.PacketLoss());
            return (0u, 0.0f);
        };
    }

    // src/game/game_server.cpp collectPaths.
    private static void CollectPaths(SimObject obj, List<SimObject> output)
    {
        if (GameUtil.EqualsNoCase(obj.ClassName(), "SimPath"))
            output.Add(obj);
        if (obj.AsSet() is SimSet set)
            foreach (SimObject m in set.Members())
                CollectPaths(m, output);
    }

    // src/game/game_server.cpp GameServer::pathEvents: SimPathManager (object 42) contents as SimPathEvents (0052b3ac,
    // 0052b8cc). Paths are in SimPath registration order; points come from the path's Markers.
    private List<OutEvent> PathEvents()
    {
        var paths = new List<SimObject>();
        if (mGame.Sim?.Server() is SimManager m)
            CollectPaths(m, paths);
        paths.Sort((a, b) => a.Sequence().CompareTo(b.Sequence()));
        var output = new List<OutEvent>();
        for (int i = 0; i < paths.Count; ++i)
        {
            var pts = new List<(float[] Delta, float[] Rot, float[] Pos)>();
            foreach (SimObject o in paths[i].AsSet()!.Members())
            {
                if (o is not GameBase gb || !GameUtil.EqualsNoCase(gb.ClassName(), "Marker"))
                    continue;
                float[] rot = new float[3], pos = new float[3];
                MissionTransform.MatrixToEuler(gb.Transform, rot); // the marker's rotation after its matrix round trip
                gb.PersistPoint("position", pos);
                pts.Add((new float[6], rot, pos));
            }
            bool moveable = false;
            if (paths[i].Group() is SimSet parent)
                foreach (SimObject o in parent.Members())
                    moveable = moveable || GameUtil.EqualsNoCase(o.ClassName(), "Moveable");
            if (moveable && pts.Count != 0)
            {
                var next = pts[pts.Count > 1 ? 1 : 0];
                for (int k = 0; k < 3; ++k)
                {
                    pts[0].Delta[k] = SubF(next.Rot[k], pts[0].Rot[k]);
                    pts[0].Delta[3 + k] = SubF(next.Pos[k], pts[0].Pos[k]);
                }
            }
            bool looping = false;
            if (paths[i].PersistValue("isLooping") is IReadOnlyList<string> v)
                looping = v.Count != 0 && Numbers.IsTrueString(v[0]);
            var w = new EventWriter();
            w.PutFlag(false); // destination: object id
            w.PutFlag(false);
            w.PutInt(42, 32);
            w.PutInt((uint)i, 32);
            w.PutFlag(true);
            w.PutFlag(looping);
            w.PutInt((uint)pts.Count, 32);
            foreach (var p in pts)
                foreach (float f in new[] { p.Delta[0], p.Delta[1], p.Delta[2], p.Delta[3], p.Delta[4], p.Delta[5], p.Rot[0], p.Rot[1],
                                            p.Rot[2], p.Pos[0], p.Pos[1], p.Pos[2] })
                    w.PutInt(BitConverter.SingleToUInt32Bits(f), 32);
            output.Add(w.Finish(0x425, false, false));
        }
        return output;
    }

    // src/game/game_server.cpp GameServer::alwaysObjects: set 34 (ghost-always objects) in registration order
    // (00553500).
    private List<SimObject> AlwaysObjects()
    {
        var output = new List<SimObject>();
        if (mGame.Sim?.Server() is SimManager m)
            CollectAlways(m, output);
        output.Sort((a, b) => a.Sequence().CompareTo(b.Sequence()));
        return output;
    }

    // src/game/game_server.cpp GameServer::traceGhost: OT_GHOST_TRACE=1: "ghost <index> <class> <object id> <x y z>" when
    // an index is taken.
    private void TraceGhost(int client, uint index, SimObject obj)
    {
        float[] p = { 0, 0, 0 };
        bool known = true;
        if (obj is GameBase gb)
        {
            for (int i = 0; i < 3; ++i)
                p[i] = gb.Transform.P[i];
        }
        else if (mGame.World.Find(obj) is Container.Entry e)
        {
            for (int i = 0; i < 3; ++i)
                p[i] = Store(Div(Add(e.Box[i], e.Box[i + 3]), 2));
        }
        else
        {
            known = false;
        }
        string s = "client " + client + " ghost " + index + " " + obj.ClassName() + " " + obj.Id();
        if (known)
            s += " " + CFormat.G(p[0], 6) + " " + CFormat.G(p[1], 6) + " " + CFormat.G(p[2], 6);
        // The container holding the object: its id, 7 for container 7, - for none.
        if (mGame.World.Find(obj) == null)
            s += " in -";
        else if (mGame.World.ParentOf(obj) is SimObject parent)
            s += " in " + parent.Id();
        else
            s += " in 7";
        Console.Error.WriteLine(s);
    }

    // src/game/game_server.cpp GameServer::accepted.
    private void Accepted(Player p, Connection vc)
    {
        int id = (int)p.Id;
        if (!mClients.TryGetValue(id, out Client? c))
        {
            c = new Client();
            mClients[id] = c;
        }
        c.Name = p.Name;
        c.Address = vc.Peer().ToString();
        c.Vc = vc;
        var hooks = new GameConnection.Hooks
        {
            RemoteCall = (fn, args) => mIn.CallFunction(fn, args),
            ControlObject = () => mGame.Players.ControlObjectOf(id),
            PscInputFn = input =>
            {
                ClientRecord? r = mGame.Players.FindHuman(id);
                if (r == null)
                    return;
                input.Team = r.Team;
                Array.Copy(r.Shopping, input.Shopping, 7);
                Array.Copy(r.Buying, input.Buying, 7);
                input.LimitCommand = r.LimitCommand;
                input.WaypointChanged = mGame.Players.UpdateWaypoint(r, out input.WaypointX, out input.WaypointY);
                mGame.Players.VisibleMask(r.Team, input.Visible);
                input.ItemCounts = GameEvents.InventoryCounts(mGame.Players.ControlObjectOf(id));
                input.CommandSlot = (slot, output) =>
                {
                    ClientRecord? viewer = mGame.Players.FindHuman(id);
                    return viewer != null && mGame.Players.CommandSlot(viewer, slot, output);
                };
            },
        };
        c.Stream = new GameConnection(id, vc, hooks);
        GhostManager gm = c.Stream.Ghosts();
        // Normal-phase scoping and priorities (docs/game-stream-ghosts.md 6).
        gm.ScopeQuery = g => GhostScope.ScopeGhosts(g, mGame.World, client => mGame.Players.FindHuman(client)?.Camera);
        gm.DefaultPriority = GhostScope.GhostPriority;
        if (Environment.GetEnvironmentVariable("OT_GHOST_TRACE") == "1")
            gm.OnNewGhost = (index, obj) => TraceGhost(id, index, obj);
        gm.AddAlwaysObjects(AlwaysObjects());
        gm.OnAlwaysDone = () =>
        {
            // Connection delegate slot +0x68 (0042b7ac): paths, then the script (docs/game-stream-ghosts.md 6.5).
            if (mClients.TryGetValue(id, out Client? cc))
                foreach (OutEvent e in PathEvents())
                    cc.Stream!.Post(e);
            mIn.CallFunction("onServerGhostAlwaysDone", new[] { Numbers.FormatInt(id) });
        };
        mOrder.Insert(0, id);

        if (mGame.Sim?.Server() is SimManager m)
        {
            c.Object = new ClientObject(id);
            m.AddToRoot(c.Object);
        }

        // Join: teams, players, then the script greeting, then datablocks (player-manager.md 4.1).
        mGame.Players.AddClient(id, p.Name, p.Voice, p.Male); // NetServer caps ids below 2176
        mIn.CallFunction("Server::onClientConnect", new[] { Numbers.FormatInt(id) });
        QueueDataBlocks(c.Stream);
    }

    // src/game/game_server.cpp GameServer::queueDataBlocks.
    private void QueueDataBlocks(GameConnection c)
    {
        DataBlockManager db = mGame.Sim!.DataBlocks();
        for (int g = 0; g <= 30; ++g)
        {
            int count = db.GroupSize(g);
            if (count == 0)
            {
                var w = new EventWriter();
                w.PutInt((uint)g, 6);
                w.PutInt(0, 8);
                w.PutInt(255, 8);
                c.Post(w.Finish(0x458));
                continue;
            }
            for (int i = 0; i < count; ++i)
            {
                var w = new EventWriter();
                w.PutInt((uint)g, 6);
                w.PutInt((uint)count, 8);
                w.PutInt((uint)i, 8);
                if (DatablockPack.PackDataBlock(db, db.At(g, i)!, w))
                    c.Post(w.Finish(0x458));
            }
        }
        // After group 30: sensor target names, then the score headings (5.1 step 4). A target without a name sends no
        // event.
        foreach (OutEvent e in GameEvents.MakeTargetNameEvents(mGame.TargetNames))
            c.Post(e);
        c.Post(GameEvents.MakeDeltaScore(true, -1, 0, mGame.TeamScoreHeading));
        c.Post(GameEvents.MakeDeltaScore(false, 2048, 0, mGame.ClientScoreHeading));
    }

    // src/game/game_server.cpp GameServer::closed.
    private void Closed(Player p)
    {
        int id = (int)p.Id;
        if (!mClients.TryGetValue(id, out Client? c))
            return;
        mGame.Players.RemoveClient(id);
        if (c.Object != null && mGame.Sim?.Server() is SimManager m)
            m.DeleteObject(c.Object);
        mClients.Remove(id);
        mOrder.RemoveAll(x => x == id);
    }

    // src/game/game_server.cpp GameServer::registerCommands.
    public void RegisterCommands()
    {
        // remoteEval(id, cmd, args...) posts a console event (docs/sim.md 8).
        mIn.AddCommand("remoteEval", (inr, a) =>
        {
            if (a.Length < 3)
            {
                inr.Print(1, "remoteEval: <managerId> cmd args...");
                return "True";
            }
            int id = Numbers.BorlandAtoi(a[1]);
            if (!mClients.TryGetValue(id, out Client? c))
                return "True";
            string[] argv = a[2..];
            int total = 0;
            foreach (string s in argv)
                total += s.Length;
            if (argv.Length > 31 || total > 512)
                return "True"; // dropped silently
            c.Stream!.Post(GameEvents.MakeRemoteEval(argv));
            return "True";
        });
        mIn.AddCommand("startGhosting", (_, a) =>
        {
            if (a.Length != 2 || !mClients.TryGetValue(Numbers.BorlandAtoi(a[1]), out Client? c))
                return "False";
            c.Stream!.Ghosts().StartGhosting();
            return "True";
        });
        mIn.AddCommand("resetGhostManagers", (_, _) =>
        {
            foreach (var c in mClients)
                c.Value.Stream!.Ghosts().Reset();
            return "True";
        });
        mIn.AddCommand("Client::getTransportAddress", (_, a) =>
            a.Length == 2 && mClients.TryGetValue(Numbers.BorlandAtoi(a[1]), out Client? c) ? c.Address : "");
    }
}
