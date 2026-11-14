namespace OpenTribes.Server;

using OpenTribes.Game;
using OpenTribes.Net;
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

/// <summary>The server side of client connections: client objects, the join sequence (docs/game-stream.md 5.1) and remote evals in both directions.</summary>
/// <remarks>Port of src/game/game_server.cpp. It lives in OpenTribes.Server because it drives the <see cref="NetServer"/> of this project. The C# project graph is acyclic and OpenTribes.Game does not reference the server.</remarks>
public sealed class GameServer
{
    // src/game/game_server.cpp ClientObject: the object a client id names in scripts. Its id is the connection's player
    // id.
    private sealed class ClientObject : SimGroup
    {
        private readonly int _id;
        public ClientObject(int id) => _id = id;
        /// <inheritdoc/>
        public override string ClassName() => "Net::PacketStream";
        /// <inheritdoc/>
        public override int FixedId() => _id;
    }

    // src/game/game_server.hpp GameServer::Client.
    private sealed class Client
    {
        public GameConnection? Stream { get; set; }
        public SimObject? Object { get; set; } // the client id object scripts use (%clientId.field)
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        public Connection? Vc { get; set; }
    }

    private readonly GameState _game;
    private readonly Interpreter _in;
    private readonly NetServer _net;
    private readonly SortedDictionary<int, Client> _clients = []; // join order by id is not guaranteed; mOrder keeps it
    private readonly List<int> _order = [];

    // src/game/game_server.cpp collectAlways: set 34: objects with net flags 0x40 and 0x100 (docs/game-stream-ghosts.md
    // 1.1).
    private static void CollectAlways(SimObject obj, List<SimObject> output)
    {
        var f = GhostScope.NetFlags(obj);
        if ((f & GhostScope.NetGhostable) != 0 && (f & GhostScope.NetScopeAlways) != 0)
            output.Add(obj);
        if (obj.AsSet() is SimSet set)
            foreach (var m in set.Members())
                CollectAlways(m, output);
    }

    /// <summary>Creates the server side and hooks it to the network.</summary>
    /// <param name="game">The game state.</param>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="net">The server network.</param>
    /// <param name="claimGlobals">True when this world talks to clients and so owns the process-wide hooks: the ghost priority container and the sound routes. A world that is only stepped, like the prediction world of a client, leaves them alone.</param>
    public GameServer(GameState game, Interpreter interp, NetServer net, bool claimGlobals = true)
    {
        _game = game;
        _in = interp;
        _net = net;
        GhostScope.RegisterPriorityVariables(_in);
        if (claimGlobals)
            GhostScope.SetPriorityContainer(_game.World);
        GhostScope.RegisterGhostPriorities();
        var hooks = new NetHooks
        {
            OnAccepted = Accepted,
            OnData = (p, d) =>
            {
                if (!_clients.TryGetValue((int)p.Id, out var c))
                    return;
                c.Stream!.Receive(d);
                // An invalid member block ends the connection (docs/game-stream.md 1.6).
                if (c.Stream.Invalid())
                    _net.DropInvalid(p.Id);
            },
            OnNotify = (p, cookie, delivered) =>
            {
                if (_clients.TryGetValue((int)p.Id, out var c))
                    c.Stream!.Notify(cookie, delivered);
            },
            OnClosed = Closed,
            OnTick = now =>
            {
                foreach (var id in _order.ToList())
                {
                    if (!_clients.TryGetValue(id, out var c))
                        continue;
                    if (_game.Players.FindHuman(id) is ClientRecord r)
                    {
                        c.Stream!.GuiMode = (uint)r.GuiMode;
                        c.Stream.Flag6a = r.MenuScoreVis;
                    }
                    // $pref::PacketRate 1-30 (unsigned compare) and $pref::PacketSize 100-450, defaults 10 and 200
                    // (GameConnection_loadPacketPrefs 0x00550c68, same clamps in the 1.11 exe; the 1.30 exe uses 10-30 and 200-450; docs/game-stream.md 1.3). A change is sent to the
                    // client as rateReq.
                    var rateText = _in.GetVariable("$pref::PacketRate", out var haveRate);
                    var sizeText = _in.GetVariable("$pref::PacketSize", out var haveSize);
                    var rate = haveRate ? unchecked((uint)Numbers.BorlandAtoi(rateText)) : 10u;
                    var bytes = haveSize ? Numbers.BorlandAtoi(sizeText) : 200;
                    rate = rate > 30 ? 30u : (rate < 1 ? 1u : rate);
                    bytes = bytes > 450 ? 450 : (bytes < 100 ? 100 : bytes);
                    c.Stream!.SetOwnRate(1000u / rate, (uint)bytes);
                    c.Stream.Tick(now);
                }
            },
        };
        _net.SetHooks(hooks);
        if (_game.Sim?.Server() is SimManager m)
        {
            // NetObject_onAdd (0050a420): an object with net flags 0x40 and 0x100 joins set 34 and is marked in scope in
            // every ghost manager, so one created while clients are connected reaches them.
            m.OnRegister = obj =>
            {
                var f = GhostScope.NetFlags(obj);
                if ((f & GhostScope.NetGhostable) == 0 || (f & GhostScope.NetScopeAlways) == 0)
                    return;
                foreach (var c in _clients)
                    if (c.Value.Stream != null && ((f & GhostScope.NetLocalOnly) == 0 || c.Key == 2049))
                        c.Value.Stream.Ghosts().MarkInScope(obj, true);
            };
            m.OnUnregister = obj =>
            {
                // A SimPath leaving tells every client to drop its path (SimPathManager, 0052b8cc).
                if (GameUtil.EqualsNoCase(obj.ClassName(), "SimPath"))
                {
                    var index = _game.PathIndex(obj);
                    if (index >= 0)
                        foreach (var c in _clients)
                            c.Value.Stream!.Post(GameEvents.MakePathRemoved((uint)index));
                }
                foreach (var c in _clients)
                    c.Value.Stream!.Ghosts().ObjectRemoved(obj);
                _game.Players.ObjectRemoved(obj);
            };
        }
        _game.Players.SetMaskBitsHook = (obj, bits) =>
        {
            foreach (var c in _clients)
                c.Value.Stream!.Ghosts().SetMaskBits(obj, bits);
        };
        _game.MaskBits = (obj, bits) =>
        {
            foreach (var c in _clients)
                c.Value.Stream?.Ghosts().SetMaskBits(obj, bits);
        };
        // Sound routing (command_map.cpp): the ghost index of an object and the scope object of a client.
        if (claimGlobals)
        {
            SoundRoute.GhostIndex = (id, obj) =>
                _clients.TryGetValue(id, out var c) && c.Stream != null ? c.Stream.Ghosts().GhostIndex(obj) : -1;
            SoundRoute.ScopeObject = id => _clients.TryGetValue(id, out var c) && c.Stream != null ? c.Stream.Ghosts().ScopeObject() : null;
        }
        _game.Players.Send = (id, e) =>
        {
            if (_clients.TryGetValue(id, out var c))
                c.Stream!.Post(e);
        };
        _game.Players.NetStats = id =>
        {
            if (_clients.TryGetValue(id, out var c) && c.Vc != null)
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
            foreach (var m in set.Members())
                CollectPaths(m, output);
    }

    // src/game/game_server.cpp GameServer::pathEvents: SimPathManager (object 42) contents as SimPathEvents (0052b3ac,
    // 0052b8cc). Paths are in SimPath registration order; points come from the path's Markers.
    private List<OutEvent> PathEvents()
    {
        var paths = new List<SimObject>();
        if (_game.Sim?.Server() is SimManager m)
            CollectPaths(m, paths);
        paths.Sort((a, b) => a.Sequence().CompareTo(b.Sequence()));
        var output = new List<OutEvent>();
        for (var i = 0; i < paths.Count; ++i)
        {
            var pts = new List<(float[] Delta, float[] Rot, float[] Pos)>();
            foreach (var o in paths[i].AsSet()!.Members())
            {
                if (o is not GameBase gb || !GameUtil.EqualsNoCase(gb.ClassName(), "Marker"))
                    continue;
                float[] rot = new float[3], pos = new float[3];
                MissionTransform.MatrixToEuler(gb.Transform, rot); // the marker's rotation after its matrix round trip
                gb.PersistPoint("position", pos);
                pts.Add((new float[6], rot, pos));
            }
            var moveable = false;
            if (paths[i].Group() is SimSet parent)
                foreach (var o in parent.Members())
                    moveable = moveable || GameUtil.EqualsNoCase(o.ClassName(), "Moveable");
            if (moveable && pts.Count != 0)
            {
                var (delta2, rot3, pos3) = pts[pts.Count > 1 ? 1 : 0];
                for (var k = 0; k < 3; ++k)
                {
                    pts[0].Delta[k] = SubF(rot3[k], pts[0].Rot[k]);
                    pts[0].Delta[3 + k] = SubF(pos3[k], pts[0].Pos[k]);
                }
            }
            var looping = false;
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
            foreach (var (delta, rot2, pos2) in pts)
                foreach (var f in new[] { delta[0], delta[1], delta[2], delta[3], delta[4], delta[5], rot2[0], rot2[1],
                                            rot2[2], pos2[0], pos2[1], pos2[2] })
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
        if (_game.Sim?.Server() is SimManager m)
            CollectAlways(m, output);
        output.Sort((a, b) => a.Sequence().CompareTo(b.Sequence()));
        return output;
    }

    // src/game/game_server.cpp GameServer::traceGhost: OT_GHOST_TRACE=1: "ghost <index> <class> <object id> <x y z>" when
    // an index is taken.
    private void TraceGhost(int client, uint index, SimObject obj)
    {
        float[] p = [0, 0, 0];
        var known = true;
        if (obj is GameBase gb)
        {
            for (var i = 0; i < 3; ++i)
                p[i] = gb.Transform.P[i];
        }
        else if (_game.World.Find(obj) is Container.Entry e)
        {
            for (var i = 0; i < 3; ++i)
                p[i] = Store(Div(Add(e.Box[i], e.Box[i + 3]), 2));
        }
        else
        {
            known = false;
        }
        var s = "client " + client + " ghost " + index + " " + obj.ClassName() + " " + obj.Id();
        if (known)
            s += " " + CFormat.G(p[0], 6) + " " + CFormat.G(p[1], 6) + " " + CFormat.G(p[2], 6);
        // The container holding the object: its id, 7 for container 7, - for none.
        if (_game.World.Find(obj) == null)
            s += " in -";
        else if (_game.World.ParentOf(obj) is SimObject parent)
            s += " in " + parent.Id();
        else
            s += " in 7";
        Console.Error.WriteLine(s);
    }

    // src/game/game_server.cpp GameServer::accepted.
    private void Accepted(Player p, Connection vc)
    {
        var id = (int)p.Id;
        if (!_clients.TryGetValue(id, out var c))
        {
            c = new Client();
            _clients[id] = c;
        }
        c.Name = p.Name;
        c.Address = vc.Peer().ToString();
        c.Vc = vc;
        var hooks = new GameConnection.Hooks
        {
            RemoteCall = (fn, args) => _in.CallFunction(fn, args),
            ControlObject = () => _game.Players.ControlObjectOf(id),
            PscInputFn = input =>
            {
                var r = _game.Players.FindHuman(id);
                if (r == null)
                    return;
                input.Team = r.Team;
                Array.Copy(r.Shopping, input.Shopping, 7);
                Array.Copy(r.Buying, input.Buying, 7);
                input.LimitCommand = r.LimitCommand;
                input.WaypointChanged = _game.Players.UpdateWaypoint(r, out var waypointX, out var waypointY);
                input.WaypointX = waypointX;
                input.WaypointY = waypointY;
                _game.Players.VisibleMask(r.Team, input.Visible);
                input.ItemCounts = GameEvents.InventoryCounts(_game.Players.ControlObjectOf(id));
                input.CommandSlot = (slot, output) =>
                {
                    var viewer = _game.Players.FindHuman(id);
                    return viewer != null && _game.Players.CommandSlot(viewer, slot, output);
                };
            },
        };
        c.Stream = new GameConnection(id, vc, hooks);
        var gm = c.Stream.Ghosts();
        // Normal-phase scoping and priorities (docs/game-stream-ghosts.md 6).
        gm.ScopeQuery = g => GhostScope.ScopeGhosts(g, _game.World, client => _game.Players.FindHuman(client)?.Camera);
        gm.DefaultPriority = GhostScope.GhostPriority;
        if (Environment.GetEnvironmentVariable("OT_GHOST_TRACE") == "1")
            gm.OnNewGhost = (index, obj) => TraceGhost(id, index, obj);
        gm.AddAlwaysObjects(AlwaysObjects());
        gm.OnAlwaysDone = () =>
        {
            // Connection delegate slot +0x68 (0042b7ac): paths, then the script (docs/game-stream-ghosts.md 6.5).
            if (_clients.TryGetValue(id, out var cc))
                foreach (var e in PathEvents())
                    cc.Stream!.Post(e);
            _in.CallFunction("onServerGhostAlwaysDone", [Numbers.FormatInt(id)]);
        };
        _order.Insert(0, id);

        if (_game.Sim?.Server() is SimManager m)
        {
            c.Object = new ClientObject(id);
            m.AddToRoot(c.Object);
        }

        // Join: teams, players, then the script greeting, then datablocks (player-manager.md 4.1).
        _game.Players.AddClient(id, p.Name, p.Voice, p.Male); // NetServer caps ids below 2176
        _in.CallFunction("Server::onClientConnect", [Numbers.FormatInt(id)]);
        QueueDataBlocks(c.Stream);
    }

    // src/game/game_server.cpp GameServer::queueDataBlocks.
    private void QueueDataBlocks(GameConnection c)
    {
        var db = _game.Sim!.DataBlocks();
        for (var g = 0; g <= 30; ++g)
        {
            var count = db.GroupSize(g);
            if (count == 0)
            {
                var w = new EventWriter();
                w.PutInt((uint)g, 6);
                w.PutInt(0, 8);
                w.PutInt(255, 8);
                c.Post(w.Finish(0x458));
                continue;
            }
            for (var i = 0; i < count; ++i)
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
        foreach (var e in GameEvents.MakeTargetNameEvents(_game.TargetNames))
            c.Post(e);
        c.Post(GameEvents.MakeDeltaScore(true, -1, 0, _game.TeamScoreHeading));
        c.Post(GameEvents.MakeDeltaScore(false, 2048, 0, _game.ClientScoreHeading));
    }

    // src/game/game_server.cpp GameServer::closed.
    private void Closed(Player p)
    {
        var id = (int)p.Id;
        if (!_clients.TryGetValue(id, out var c))
            return;
        _game.Players.RemoveClient(id);
        if (c.Object != null && _game.Sim?.Server() is SimManager m)
            m.DeleteObject(c.Object);
        _clients.Remove(id);
        _order.RemoveAll(x => x == id);
    }

    /// <summary>Registers the client commands, such as <c>remoteEval</c> (docs/sim.md 8).</summary>
    public void RegisterCommands()
    {
        // remoteEval(id, cmd, args...) posts a console event (docs/sim.md 8).
        _in.AddCommand("remoteEval", (inr, a) =>
        {
            if (a.Length < 3)
            {
                inr.Print(1, "remoteEval: <managerId> cmd args...");
                return "True";
            }
            var id = Numbers.BorlandAtoi(a[1]);
            if (!_clients.TryGetValue(id, out var c))
                return "True";
            var argv = a[2..];
            var total = 0;
            foreach (var s in argv)
                total += s.Length;
            if (argv.Length > 31 || total > 512)
                return "True"; // dropped silently
            c.Stream!.Post(GameEvents.MakeRemoteEval(argv));
            return "True";
        });
        _in.AddCommand("startGhosting", (_, a) =>
        {
            if (a.Length != 2 || !_clients.TryGetValue(Numbers.BorlandAtoi(a[1]), out var c))
                return "False";
            c.Stream!.Ghosts().StartGhosting();
            return "True";
        });
        _in.AddCommand("resetGhostManagers", (_, _) =>
        {
            foreach (var c in _clients)
                c.Value.Stream!.Ghosts().Reset();
            return "True";
        });
        _in.AddCommand("Client::getTransportAddress", (_, a) =>
            a.Length == 2 && _clients.TryGetValue(Numbers.BorlandAtoi(a[1]), out var c) ? c.Address : "");
    }
}
