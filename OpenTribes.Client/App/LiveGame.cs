namespace OpenTribes.Client.App;

using System.Globalization;
using OpenTribes.Client.Audio;
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Client.World;
using OpenTribes.ClientNet;
using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;
using OpenTribes.Script;

/// <summary>One joined game or one playing demo of the <c>tribes</c> client: the connection or the demo reader, the client world, the scene built from the environment ghosts, the prediction, and the per frame steps F9 to F16.</summary>
/// <remarks>docs/client-net.md 1 to 5 and docs/client-app.md 2.2. The scripts do the talking (onConnection, dataFinished, remote evals). This class feeds them.</remarks>
public sealed class LiveGame : IDisposable
{
    private readonly ClientApp mApp;
    /// <summary>The network session, or null.</summary>
    public NetClientSession? Net;
    /// <summary>The demo that plays, or null.</summary>
    public DemoPlayback? Demo;
    /// <summary>The client world.</summary>
    public ClientWorld World = new();
    /// <summary>The mission name.</summary>
    public string Mission = "";
    /// <summary>The name of the demo.</summary>
    public string DemoName = "";
    /// <summary>True once the game is joined.</summary>
    public bool Joined;

    /// <summary>The scene, built once the environment ghosts exist (world-render.md 6.1).</summary>
    public WorldScene? Scene;
    /// <summary>The scene model.</summary>
    public SceneModel? Model;
    /// <summary>The shape loader.</summary>
    public ShapeAssets? Assets;
    private ShapeRenderer? mRenderer;
    private SceneAudio? mAudio;
    private bool mSceneTried;
    private uint mAlwaysSeenMs;
    private double mTickCarry;
    private int mChatSeen;
    private int mGuiModeSeen = -1;
    private uint mPrevMs;
    private bool mPrevSet;
    /// <summary>The error of the last scene build, or empty.</summary>
    public string SceneError = "";
    /// <summary>The notes of the scene build.</summary>
    public readonly List<string> Notes = [];

    /// <summary>Creates the game state.</summary>
    /// <param name="app">The client.</param>
    public LiveGame(ClientApp app) => mApp = app;

    /// <summary>True when a demo plays.</summary>
    public bool IsDemo => Demo != null;
    /// <summary>True when the scene exists.</summary>
    public bool HasScene => Scene != null && Model != null;
    /// <summary>The mission palette the play view draws with (the HUD bitmaps are paletted too), or null before the scene exists.</summary>
    public PaletteBank? WorldPalette => HasScene ? Scene!.Assets.Palette : null;

    // ---- start ----

    private void Hook(ClientWorld w)
    {
        World = w;
        w.DataBlocks.Resources = mApp.Res;
        w.DataBlocks.Print = line => mApp.Interp.Print(0, line);
        w.Players.OnScript = text => mApp.Interp.Evaluate(text);
        // 0040940c: an empty $pref::playVoices is set to TRUE first, then read as a boolean.
        w.Players.PlayVoices = () =>
        {
            if (mApp.Interp.PeekVariable("$pref::playVoices").Length == 0)
                mApp.Interp.SetVariable("$pref::playVoices", "TRUE");
            return Numbers.ParseBool(mApp.Interp.PeekVariable("$pref::playVoices"));
        };
        // client-net.md 2.5: remote<Name>(manager, args...).
        w.OnRemoteCall = (name, args) =>
        {
            if (name.Equals("MInfo", StringComparison.OrdinalIgnoreCase) && args.Length > 0)
                Mission = args[0];
            var list = new List<string> { w.ServerId.ToString(CultureInfo.InvariantCulture) };
            list.AddRange(args);
            var fn = "remote" + name;
            if (mApp.Interp.HasFunction(fn))
                mApp.Interp.CallFunction(fn, list);
        };
        w.DataBlocks.OnProgress = (name, pct) =>
            mApp.Interp.CallFunction("dataGotBlock", new[] { name, pct.ToString("0.######", CultureInfo.InvariantCulture) });
        // The mode change runs while the packet is read, so a chat event of a later packet already finds playGui (PlayerManager_message).
        w.Psc.OnGuiMode = mode =>
        {
            mGuiModeSeen = mode;
            mApp.Play.SetGuiMode(mode);
        };
        w.Players.ChatDisplayExists = () => mApp.GuiRt.FindChildOf("playGui", PlayDelegate.ChatDisplayId) != null;
        w.Psc.OnScript = null;   // the play delegate evaluates loadPlayGui() on mode changes (PlayDelegate.SetGuiMode)
    }

    /// <summary>Joins a game (<c>connect(address)</c>). R6 is checked by the command.</summary>
    /// <param name="hostPort">The address as host and port.</param>
    /// <param name="interp">The interpreter.</param>
    /// <returns>True when the connection started.</returns>
    public bool StartNet(string hostPort, Interpreter interp)
    {
        var dump = new PacketDump(null);
        var name = interp.PeekVariable("$PCFG::Name");
        Net = new NetClientSession(name.Length > 0 ? name : interp.PeekVariable("$pref::PlayerName") is { Length: > 0 } pn ? pn : "OpenTribes", dump)
        {
            External = true,
            PrefReader = interp.PeekVariable,
            ClockMs = () => mApp.NowMs,
            Log = s => mApp.Log("net: " + s),
            Password = interp.PeekVariable("$Server::JoinPassword"),
            Gender = Or(interp.PeekVariable("$PCFG::Gender"), "Male"),
            Voice = Or(interp.PeekVariable("$PCFG::Voice"), "Male1"),
            SkinBase = Or(interp.PeekVariable("$PCFG::SkinBase"), "base"),
        };
        Hook(Net.World);
        ClientWorld netWorld = Net.World;
        Net.Prediction.Turret.Resolve = ResolveTurretAim;
        Net.OnConnection = (msg, reason) =>
        {
            if (msg != "Accepted")
                interp.SetVariable("$errorString", reason);
            if (msg == "Accepted")
                Joined = true;
            else
                Joined = false;
            interp.CallFunction("onConnection", new[] { msg });
            if (msg != "Accepted")
                Stop();
        };
        Net.OnPacketError = text =>
        {
            Joined = false;
            interp.CallFunction("onConnectionError", new[] { netWorld.ServerId.ToString(CultureInfo.InvariantCulture), netWorld.PlayerId.ToString(CultureInfo.InvariantCulture), text });
            Stop();
        };
        Net.OnDataFinished = () => interp.CallFunction("dataFinished", Array.Empty<string>());
        Net.OnAlwaysDone = () =>
        {
            interp.SetVariable("$ClientGhostAlwaysDone", "1");
            interp.CallFunction("onClientGhostAlwaysDone", Array.Empty<string>());
        };
        Net.PacketHook = OnPacket;
        if (!mApp.Opt.NoPredict)
        {
            var root = mApp.Opt.Root;
            Net.PredictFactory = mission => new PredictionWorld(root, new[] { "-dedicated", mission }, s => mApp.Log("predict: " + s));
        }
        mApp.InitSecondaryHooks(this);
        if (!Net.Connect(hostPort))
        {
            mApp.Log("connect: cannot open a socket");
            return false;
        }
        return true;
    }

    private static string Or(string v, string def) => v.Length > 0 ? v : def;

    /// <summary>Plays a recording through the same receive path as the network (<c>playDemo(file)</c>, client-net.md 5.4).</summary>
    /// <param name="path">The demo file.</param>
    /// <param name="interp">The interpreter.</param>
    /// <returns>True when the demo started.</returns>
    public bool StartDemo(string path, Interpreter interp)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        Demo = new DemoPlayback(data)
        {
            OnPacket = OnPacket
        };
        DemoName = Path.GetFileNameWithoutExtension(path);
        Hook(Demo.World);
        Demo.World.PlayerId = (int)Demo.Demo.ClientId;
        interp.SetVariable("$playingDemo", "1");
        Joined = true;
        mApp.InitSecondaryHooks(this);
        interp.CallFunction("onConnection", new[] { "Accepted" });
        // The record reader runs from the first frame; the environment appears when the always phase ends.
        return true;
    }

    /// <summary>Disconnects and forgets the session and the demo.</summary>
    public void Stop()
    {
        Net?.Disconnect();
        Net = null;
        Demo = null;
        Joined = false;
    }

    /// <summary>Disconnects from the server.</summary>
    public void Disconnect()
    {
        Net?.Disconnect();
        Joined = false;
    }

    // ---- remote eval from the console ----

    /// <summary>Posts a remote eval from the console.</summary>
    /// <param name="argv">The arguments of the command.</param>
    /// <returns>False when there is no open connection.</returns>
    public bool PostRemoteEval(string[] argv)
    {
        if (Net == null || !Net.IsOpen)
            return false;
        Net.Game.PostRemoteEval(argv);
        return true;
    }

    // ---- per frame ----

    /// <summary>Runs the steps F9 to F14 (client-app.md 2.2): moves, receive, send, prediction, scene upkeep.</summary>
    /// <param name="now">The time in milliseconds.</param>
    /// <param name="moves">The move builder.</param>
    public void Frame(uint now, MoveBuilder moves)
    {
        var prev = mPrevSet ? mPrevMs : now;
        mPrevMs = now;
        mPrevSet = true;
        if (Demo != null)
        {
            if (Demo.Finished && Joined && HasScene)
            {
                // onPlaybackFinished() runs once through schedule (client-net.md 5.4).
                Joined = false;
                mApp.Interp.Evaluate("schedule(\"onPlaybackFinished();\", 0);");
            }
            else if (!Demo.Finished)
                Demo.StepTo(Demo.ClockMs + (now - prev));
        }
        else if (Net != null)
        {
            if (Net.IsAccepted)
                moves.Collect(World.Psc, prev, now);
            // GameConnection_loadPacketPrefs (00550c68): $pref::PacketFrame, default 32, clamped to 32..128 (1.11).
            Net.Game.PacketFrame = Math.Clamp(unchecked((uint)Numbers.BorlandAtoi(mApp.Interp.PeekVariable("$pref::PacketFrame") ?? "")), 32u, 128u);
            Net.Frame(now);
            // A script callback of the frame (onConnection, onConnectionError) may have stopped the session.
            if (Net != null && !Net.IsOpen && Joined && !Net.IsAccepted)
                Joined = false;
        }
        mApp.PumpChat(this, ref mChatSeen);
        PollGuiMode();
        mApp.Play.SetScoreVisible(World.Psc.ScoreFlag && World.Psc.GuiMode != 0);
        // The stock client answers the end of the ghost-always phase with CGADone in its next packet (about 170 ms after the server
        // finished it, stock trace) and builds nothing first. The scene build takes about 0.6 s here, so it waits until that reply
        // has been written (at most 1 s), or the server sees CGADone late: the PSC mode change and the join event then arrive
        // in one packet and the join line is dropped (docs/gui.md 4.2).
        if (World.AlwaysDone && mAlwaysSeenMs == 0)
            mAlwaysSeenMs = now;
        if (!HasScene && !mSceneTried && World.AlwaysDone && (Net == null || !Net.Game.OutgoingPending || now - mAlwaysSeenMs > 1000))
            BuildScene();
        if (HasScene)
        {
            FeedRemote();
            FeedControlVehicle();
            FeedControlTurret();
            FeedControlImages();
            mTickCarry += (now - prev) / 1000.0;
            while (mTickCarry >= 0.032)
            {
                mTickCarry -= 0.032;
                FeedControlPlayer();
                Model!.Tick(0.032);
                if (mTickCarry > 1)
                    mTickCarry = 0;
            }
        }
    }

    private readonly List<DecodedPacket> mQueued = [];

    // Every decoded packet reaches the scene model; before the scene exists the packets wait (the ghosts they update live in
    // the world's table, so the replay builds the visuals from the latest state).
    private void OnPacket(DecodedPacket pkt)
    {
        if (Model != null)
            Model.OnPacket(pkt);
        else if (pkt.Ghosts != null && mQueued.Count < 20000)
            mQueued.Add(pkt);
    }

    // The commander map picture (rebuildCommandMap [code 0041e3d4]): rendered from the terrain when the script asks, or at the first
    // draw of the map once the world exists, over the area of the MissionCenterPos ghost (the constructor values 1230 1230 1400 1400
    // without one [code 004ceb18]).
    private CommandMapPicture? mCommandMap;
    private bool mCommandMapDirty = true;

    /// <summary>Marks the command map picture as stale.</summary>
    public void RebuildCommandMap() => mCommandMapDirty = true;

    /// <summary>Builds the command map picture, or returns the last one while it is current.</summary>
    /// <returns>The picture, or null.</returns>
    public CommandMapPicture? CommandMapPicture()
    {
        if (!mCommandMapDirty)
            return mCommandMap;
        TerrainRenderer? terrain = Scene?.TerrainDraw;
        if (terrain == null)
            return mCommandMap;
        double[] area = [1230, 1230, 1400, 1400];
        if (World.Ghosts.All().FirstOrDefault(g => g.Class == "MissionCenterPos")?.State.GetValueOrDefault("rect") is double[] { Length: 4 } r)
            area = r;
        PaletteBank bank = Scene!.Assets.Palette;
        var picture = terrain.RenderCommandMap(area[0], area[1], area[2], area[3], (r8, g8, b8) => bank.NearestColor(r8, g8, b8));
        mCommandMap = new CommandMapPicture((float)area[0], (float)area[1], (float)area[2], (float)area[3], CommandMapBake.Tiles(picture));
        mCommandMapDirty = false;
        return mCommandMap;
    }

    private void PollGuiMode()
    {
        var mode = World.Psc.GuiMode;
        if (mode != mGuiModeSeen && (World.Psc.GuiModeChanges > 0 || mGuiModeSeen >= 0))
        {
            mGuiModeSeen = mode;
            mApp.Play.SetGuiMode(mode);
        }
    }

    // Builds the world renderer from the environment ghosts (volumes, palette, terrain, sky, interiors).
    private void BuildScene()
    {
        mSceneTried = true;
        if (mApp.Dev == null)
            return;
        List<MissionObject> env = DemoRender.EnvironmentObjects(World);
        foreach (MissionObject o in env.Where(o => o.Class == "SimVolume"))
            mApp.Res.AddVolume(o.Get("fileName"));
        var name = IsDemo ? DemoName : Mission;
        Scene = WorldScene.LoadFrom(mApp.Dev, mApp.Res, name, env, out var error, mApp.Gamma);
        SceneError = error;
        if (error.Length > 0)
            mApp.Log("scene: " + error);
        if (Scene.TerrainDraw != null && !mApp.TerrainVarsBound)
        {
            // The T2 renderer constructor registers its tuning variables (1.11; LastHope 1.30 stubbed this out, lasthope-1.30.md L7).
            EngineVariables.RegisterTerrain(mApp.Interp);
            mApp.TerrainVarsBound = true;
        }
        Assets = new ShapeAssets(mApp.Dev, Scene.Assets);
        mRenderer = new ShapeRenderer(Assets);
        Model = new SceneModel(World, Assets, mRenderer, Scene.TerrainModel);
        Scene.Interiors?.TeamSkin = Model.TeamSkin;
        Model.Probe = new LightProbe
        {
            Ground = Scene.TerrainModel,
            TerrainLight = Scene.TerrainDraw == null ? null : Scene.TerrainDraw.LightAt,
            Interior = (sx, sy, sz, dx, dy, dz, len) =>
            {
                double t = 0;
                (float r, float g, float b) light = default;
                var hit = Scene.Interiors != null && Scene.Interiors.Probe(sx, sy, sz, dx, dy, dz, len, out t, out light);
                return (hit, t, light);
            },
        };
        Model.DebrisWorld = (sx, sy, sz, dx, dy, dz, len) =>
            Scene.Interiors != null && Scene.Interiors.Probe(sx, sy, sz, dx, dy, dz, len, out var t, out _, out var n) ? (true, t, n.x, n.y, n.z) : (false, 0, 0, 0, 1);
        // Player_updateWeaponPullback 004198d4: the cast of mask 0xbfffffff in the prediction world, the player's own copy left out.
        Model.PullbackRay = (ghost, a, b) =>
        {
            if (Net?.PredictWorld is not { } pw)
                return null;
            object? self = ghost == World.Psc.ControlGhost ? Net.Prediction.Object
                           : Net.Remote.Players.TryGetValue(ghost, out RemotePlayer? rp) ? rp.P : null;
            object? obj = null;
            OpenTribes.World.Vec3 hit = default, normal = default;
            return pw.Game.World.CastRay(new OpenTribes.World.Vec3((float)a.Item1, (float)a.Item2, (float)a.Item3),
                                         new OpenTribes.World.Vec3((float)b.Item1, (float)b.Item2, (float)b.Item3), 0xbfffffffu, self,
                                         ref obj, ref hit, ref normal)
                ? (hit.X, hit.Y, hit.Z)
                : null;
        };
        Model.BaseLight.SetSun(Scene.SunAzimuth, Scene.SunIncidence);
        Model.BaseLight.AmbR = (float)Scene.SunAmbient[0];
        Model.BaseLight.AmbG = (float)Scene.SunAmbient[1];
        Model.BaseLight.AmbB = (float)Scene.SunAmbient[2];
        Model.BaseLight.SunR = (float)Scene.SunIntensity[0];
        Model.BaseLight.SunG = (float)Scene.SunIntensity[1];
        Model.BaseLight.SunB = (float)Scene.SunIntensity[2];
        if (mApp.Sound != null && mApp.Sound.DeviceOpen)
        {
            mApp.Sound.DataBlocks = World.DataBlocks;
            mAudio = new SceneAudio(Model, mApp.Sound);
            Model.Audio = mAudio;
            mAudio.SurfaceType = (x, y, z) => Net?.PredictWorld is { } pw ? FootSurface.TypeAt(pw.Game.World, x, y, z) : -1;
        }
        // Packets already applied before the scene existed (the always phase) are replayed into the model: the
        // ghosts it would have built live in the world's table.
        // The mission palette waits for the play view (ClientApp.Frame): the loading screen before it keeps the shell palette.
        if (mApp.PlayViewActive)
            mApp.SetPalette(Scene.Assets.Palette);
        mApp.Log($"scene: {name}, {env.Count} environment objects, terrain visible {Scene.Terrain.Visible}");
        foreach (DecodedPacket q in mQueued)
            Model.OnPacket(q);
        mQueued.Clear();
    }

    /// <summary>Gets the camera of the view.</summary>
    /// <returns>The pose, or null without a scene.</returns>
    public DemoRender.CameraPose? Camera()
    {
        if (Model == null)
            return null;
        if (mApp.Opt.FixedCamera is { } fc)
            return new DemoRender.CameraPose(fc[0], fc[1], fc[2], fc[3], fc[4], false, -1);
        DemoRender.CameraPose? pose = DemoRender.ControlCamera(Model, World);
        if (Net?.PredictWorld != null && Net.Prediction.Object is Player p && Net.Prediction.Active && pose is { FirstPerson: true } cp)
        {
            var d = Net.Prediction.DisplayPosition();
            if (p.MountId != 0 && DemoRender.RiderCamera(Model, cp.ControlGhost, p.Transform, d, p.Pitch) is { } rc)
            {
                pose = rc;
            }
            else
            {
                var (ex, ey, ez) = DemoRender.EyeOf(Model, cp.ControlGhost, d[0], d[1], d[2], p.Rotation[2], p.Pitch);
                pose = new DemoRender.CameraPose(ex, ey, ez, p.Rotation[2] * 180 / Math.PI, p.Pitch * 180 / Math.PI, true, cp.ControlGhost);
            }
        }
        // Player_getCameraTransform 00412324: with the view distance (PSC +0x9c) at 0.01 or more the camera starts at the chase camera
        // node (z only) of the body at the pitch and moves back along the view direction by GameBase_placeBehind 00449604: a ray of
        // 2.5 * distance through terrain and interiors (mask 0x20c00006) shortens the distance to 0.025 m in front of the hit
        // (docs/client-net.md 4.2). The control object is drawn as seen from outside. The footstep bob (z += sin(2 * +0xfc8) / 120)
        // is not tracked here.
        if (pose is { FirstPerson: true } fp && Net != null && World.Psc.ControlClass == "Player" && mApp.Play.Moves.ViewDist >= 0.01f)
        {
            var predicted = Net.PredictWorld != null && Net.Prediction.Object is Player && Net.Prediction.Active;
            Player? body = predicted ? (Player)Net.Prediction.Object! : null;
            var bp = World.Psc.ControlState.TryGetValue("pos", out var pv) ? Db.Vec(pv) : new double[3];
            var d = predicted ? Net.Prediction.DisplayPosition() : [(float)bp[0], (float)bp[1], (float)bp[2]];
            pose = DemoRender.ChaseCamera(Model, fp.ControlGhost, d[0], d[1], d[2], fp.YawDeg * Math.PI / 180, fp.PitchDeg * Math.PI / 180,
                                          mApp.Play.Moves.ViewDist, predicted ? Net.PredictWorld!.Game.World : null, body);
        }
        // Vehicle_getCameraTransform 00445d88 (Vehicle and Flier): the `dummy eye` node of the vehicle as drawn, moved back by twice the
        // view distance (placeBehind with the vehicle ignored) and with pitch and roll scaled while the distance is not 0.
        if (Model != null && World.Psc.ControlClass is "Vehicle" or "Flier" && World.Psc.ControlGhost >= 0 && Net != null)
        {
            Vehicle? self = Net.PredictWorld != null && Net.Remote.Vehicles.TryGetValue(World.Psc.ControlGhost, out RemoteVehicle? rv) ? rv.V : null;
            var dist = mApp.Play.Moves.ViewDist;
            pose = DemoRender.VehicleCamera(Model, World.Psc.ControlGhost, dist, self != null ? Net.PredictWorld!.Game.World : null, self) ?? pose;
        }
        // Turret_getCameraTransform 004564fc: the aim is the ghost's, replayed with the buffered moves (Turret_clientUpdate 00456428).
        if (Model != null && World.Psc.ControlClass == "Turret" && World.Psc.ControlGhost >= 0 && Net is { Prediction.Turret.Active: true } tn)
        {
            TurretControl tc = tn.Prediction.Turret;
            pose = DemoRender.TurretCamera(Model, World.Psc.ControlGhost, tc.Rotation, tc.Elevation, mApp.Play.Moves.ViewDist,
                                           Net.PredictWorld?.Game.World, null) ?? pose;
        }
        // The camera query struct of the PSC (004077a8, call [vtable +0x170] at 00407877) keeps its previous contents when the control
        // object writes nothing: Turret_getCameraTransform 004564fc writes nothing while the turret has no shape instance. The view then
        // stays at the last pose instead of the turret origin or no frame.
        if (World.Psc.ControlClass == "Turret" && World.Psc.ControlGhost >= 0 && pose == null)
            return mLastPose;
        mLastPose = pose ?? mLastPose;
        return pose;
    }

    private DemoRender.CameraPose? mLastPose;

    /// <summary>Gets the eye and muzzle transforms of the control Vehicle, Flier or Turret in world space for the aim markers.</summary>
    /// <param name="ghost">The control ghost index.</param>
    /// <returns>The transforms, or null.</returns>
    /// <remarks>FUN_004ada90 calls vtable +0x110 and +0x10c of the control object (004adacd, 004ae719). The vehicle fire counter (+0x948) is the one the ghost stream set: the owner only ghost of the pilot carries none, so it keeps the value of an earlier update or 0.</remarks>
    public HudControlAim? ControlAimInputs(int ghost)
    {
        if (Model == null || !Model.Visuals.TryGetValue(ghost, out Visual? v) || v is not ShapeVisual sv || World.Ghosts.Get(ghost) is not { } g)
            return null;
        var cls = World.Psc.ControlClass;
        Sim.Transform obj, eye, muzzle;
        bool haveMuzzle;
        if (cls is "Vehicle" or "Flier")
        {
            if (!sv.TryNodeInputs(s => s.FindNodeBase("dummy eye", 0), out obj, out Sim.Transform? eyeNode, out _))
                return null;
            eye = ControlAim.VehicleEye(obj, eyeNode);
            var fire = g.State.TryGetValue("f948", out var fv) ? Convert.ToInt32(fv, CultureInfo.InvariantCulture) : 0;
            sv.TryNodeInputs(s =>
            {
                var table = new List<int>();
                for (var i = 1; i < 256 && s.FindNode("dummy muzzle" + i.ToString(CultureInfo.InvariantCulture)) is int n and >= 0; ++i)
                    table.Add(n);
                return ControlAim.VehicleMuzzleNode(s.FindNode("dummy muzzle"), table, fire);
            }, out _, out Sim.Transform? muzzleNode, out _);
            haveMuzzle = ControlAim.VehicleMuzzle(obj, muzzleNode, out muzzle);
        }
        else if (cls == "Turret")
        {
            float rot, elev;
            if (Net is { Prediction.Turret.Active: true } tn)
                (rot, elev) = (tn.Prediction.Turret.Rotation, tn.Prediction.Turret.Elevation);
            else
                (rot, elev) = ((float)Db.ToDouble(g.State.GetValueOrDefault("aimRot"), 0), (float)Db.ToDouble(g.State.GetValueOrDefault("aimElev"), 0));
            if (!sv.TryNodeInputs(s => s.FindNodeBase("dummy eye", 0), out obj, out Sim.Transform? eyeNode, out _)
                || !sv.TryNodeInputs(s => s.FindNode("dummy muzzle"), out _, out Sim.Transform? muzzleNode, out var shape))
                return null;
            eye = ControlAim.TurretNode(obj, eyeNode, rot, elev);
            // Turret_getMuzzleTransform 004565e0: false without a shape instance, else the node transform (the object transform without the node).
            haveMuzzle = shape;
            muzzle = ControlAim.TurretNode(obj, muzzleNode, rot, elev);
        }
        else
            return null;
        return new HudControlAim(((ReadOnlySpan<float>)eye.P).ToArray(), haveMuzzle ? ((ReadOnlySpan<float>)muzzle.P).ToArray() : null,
                                 ((ReadOnlySpan<float>)obj.M).ToArray(), ((ReadOnlySpan<float>)obj.P).ToArray());
    }

    private long mImageSteps;

    // The image state of the control player runs once per prediction step, replays included (ImageStateMachine.Stepped).
    private void FeedControlImages()
    {
        ClientPsc psc = World.Psc;
        var total = Net?.Prediction.Steps ?? 0;
        var steps = (int)Math.Max(0, total - mImageSteps);
        mImageSteps = total;
        if (psc.ControlGhost < 0 || Model == null || !Model.Visuals.TryGetValue(psc.ControlGhost, out Visual? v) || v is not PlayerVisual pv)
            return;
        var predicted = Net?.PredictWorld != null && Net.Prediction.Object is Player && Net.Prediction.Active && psc.ControlClass == "Player";
        pv.SetImageSteps(predicted, steps);
    }

    /// <summary>Gets the compass heading of the control object.</summary>
    /// <param name="ghost">The control ghost index.</param>
    /// <returns>The heading, or null for the rotZ of the control state (a Player on foot, whose transform yaw is its rotZ).</returns>
    /// <remarks>CompassHud (004e7e5c) reads vtable +0x178 = Euler z of the transform of the object (00413850): a piloted Vehicle or Flier as drawn, a Player on a mount (passenger) the Euler z of its mount transform.</remarks>
    public double? ControlHeading(int ghost)
    {
        ClientPsc psc = World.Psc;
        if (ghost != psc.ControlGhost || Model == null)
            return null;
        if (psc.ControlClass is "Vehicle" or "Flier")
            return Model.Visuals.TryGetValue(ghost, out Visual? v) && v is ShapeVisual { Driven: true } sv ? sv.HeadingRad : null;
        // Turret vtable +0x178 (00452d28): the aim rotation (+0x8b4) plus the rotation z of the object (+0x24c).
        if (psc.ControlClass == "Turret" && Net is { Prediction.Turret.Active: true } tn)
            return Model.Visuals.TryGetValue(ghost, out Visual? tv) && tv is ShapeVisual tsv ? (float)(tn.Prediction.Turret.Rotation + tsv.HeadingRad) : null;
        if (psc.ControlClass == "Player" && Net?.PredictWorld != null && Net.Prediction.Object is Player { MountId: not 0 } p && Net.Prediction.Active)
        {
            var e = new float[3];
            Sim.MissionTransform.MatrixToEuler(p.Transform, e);
            return e[2];
        }
        return null;
    }

    // The control player's visual follows the prediction (else the control state), see ControlSample.
    private void FeedControlPlayer()
    {
        ClientPsc psc = World.Psc;
        if (psc.ControlGhost < 0 || Model == null || !Model.Visuals.TryGetValue(psc.ControlGhost, out Visual? v) || v is not PlayerVisual pv)
            return;
        ControlSample? s;
        if (Net?.PredictWorld != null && Net.Prediction.Object is Player p && Net.Prediction.Active && psc.ControlClass == "Player")
        {
            var d = Net.Prediction.DisplayPosition();
            var yaw = p.Rotation[2];
            if (p.MountId != 0)
            {
                // A rider is drawn with the full transform of the mount point (as FeedRemote).
                var e = new float[3];
                Sim.MissionTransform.MatrixToEuler(p.Transform, e);
                (pv.TiltX, pv.TiltY, yaw) = (e[0], e[1], e[2]);
            }
            s = new ControlSample(d[0], d[1], d[2], p.Velocity[0], p.Velocity[1], p.Velocity[2], p.ContactFlag, p.Jetting, p.Crouching, yaw);
        }
        else
            s = ControlSample.FromControlState(psc, psc.ControlGhost);
        if (s is { } c)
            pv.ApplyControl(c);
    }

    // The control Vehicle or Flier is its own ghost: the server sends it no state updates (owner-only ghost, vehicle-sim.md 6.1), so
    // the drawn shape follows the predicted copy (Flier_clientUpdate 0042e254), else the control state (rotation, position).
    private void FeedControlVehicle()
    {
        ClientPsc psc = World.Psc;
        if (psc.ControlClass is not ("Vehicle" or "Flier") || psc.ControlGhost < 0 || Model == null
            || !Model.Visuals.TryGetValue(psc.ControlGhost, out Visual? v) || v is not ShapeVisual sv)
            return;
        if (Net?.PredictWorld != null && Net.Prediction.Object is Vehicle o && Net.Prediction.Active)
        {
            sv.Driven = true;
            sv.SetSmoothed(o.Rotation[0], o.Rotation[1], o.Rotation[2], o.Transform.P[0], o.Transform.P[1], o.Transform.P[2]);
            FlierThreads(sv, o);
        }
        else if (psc.ControlState.TryGetValue("rot", out var r) && r is double[] rot && psc.ControlState.TryGetValue("pos", out var q) && q is double[] pos)
        {
            sv.Driven = true;
            sv.SetSmoothed(rot[0], rot[1], rot[2], pos[0], pos[1], pos[2]);
        }
    }

    // The values of the aim step for a Turret ghost: TurretData (group 19) speed, speedModifier and isSustained, and the elevation limits of
    // the shape (Turret_onNewDataBlock 00452970). Null until the scene has the visual.
    private TurretAimParams? ResolveTurretAim(ClientGhost g)
    {
        if (Model == null || !Model.Visuals.TryGetValue(g.Index, out Visual? v) || v is not ShapeVisual sv)
            return null;
        if (!g.State.TryGetValue("datablock", out var di) || di is not long idx
            || !World.DataBlocks.Blocks.TryGetValue((19, (int)idx), out ClientDataBlock? b))
            return null;
        double F(string name, double def)
        {
            foreach (DecodedField f in b.Fields)
                if (f.Name == name)
                    return Db.ToDouble(f.Value, def);
            return def;
        }
        sv.TryElevationLimits(out var min, out var max);
        return new TurretAimParams((float)F("speed", 1), (float)F("speedModifier", 0), F("isSustained", 0) != 0, min, max);
    }

    // The control Turret draws the aim the client keeps (TurretControl): its ghost is owner-only and carries no rotation or elevation.
    private void FeedControlTurret()
    {
        ClientPsc psc = World.Psc;
        if (psc.ControlClass != "Turret" || psc.ControlGhost < 0 || Model == null || Net is not { Prediction.Turret.Active: true } n
            || !Model.Visuals.TryGetValue(psc.ControlGhost, out Visual? v) || v is not ShapeVisual sv)
            return;
        TurretControl tc = n.Prediction.Turret;
        sv.TryElevationLimits(out var min, out var max);
        sv.SetTurretAim(tc.Rotation, tc.Elevation, min, max);
    }

    // The Flier client threads (Flier_clientUpdate 0042e254) read the state of the simulated copy.
    private static void FlierThreads(ShapeVisual sv, Vehicle o)
    {
        if (o is Flier f)
            sv.SetFlierState(f.Piloted, f.Braking, f.ActiveFlag, f.SpeedF, f.SpeedT, f.Data.MaxSpeed);
    }

    // The remote Player and Vehicle ghosts draw from their simulated copies (client-net.md 4.4.2); the others keep the stream state.
    private void FeedRemote()
    {
        if (Model == null)
            return;
        RemoteGhosts? rg = Net?.PredictWorld != null ? Net.Remote : null;
        foreach (Visual v in Model.Visuals.Values)
        {
            if (v is PlayerVisual pv)
            {
                if (rg != null && rg.Players.TryGetValue(v.Index, out RemotePlayer? rp))
                {
                    pv.Driven = true;
                    Player p = rp.P;
                    var yaw = p.Rotation[2];
                    (pv.TiltX, pv.TiltY) = (p.Rotation[0], p.Rotation[1]);
                    if (p.MountId != 0)
                    {
                        // A rider is drawn with the full transform: the mount point's with the yaw of the Player.
                        var e = new float[3];
                        Sim.MissionTransform.MatrixToEuler(p.Transform, e);
                        (pv.TiltX, pv.TiltY, yaw) = (e[0], e[1], e[2]);
                    }
                    pv.ApplyControl(new ControlSample(p.Transform.P[0], p.Transform.P[1], p.Transform.P[2], p.Velocity[0], p.Velocity[1],
                                                      p.Velocity[2], p.ContactFlag, p.Jetting, p.Crouching, yaw));
                    pv.ViewPitch = p.Pitch;
                }
                else
                    pv.Driven = false;
            }
            else if (v is ShapeVisual sv)
            {
                if (rg != null && v.Index != World.Psc.ControlGhost && rg.Vehicles.TryGetValue(v.Index, out RemoteVehicle? rv))
                {
                    sv.Driven = true;
                    Vehicle o = rv.V;
                    sv.SetSmoothed(o.Rotation[0], o.Rotation[1], o.Rotation[2], o.Transform.P[0], o.Transform.P[1], o.Transform.P[2]);
                    FlierThreads(sv, o);
                }
                else
                    sv.Driven = false;
            }
        }
    }

    /// <summary>Moves the listener to the camera (step F15).</summary>
    /// <param name="dt">The frame time in seconds.</param>
    /// <param name="now">The time in milliseconds.</param>
    public void UpdateSound(double dt, uint now)
    {
        if (mAudio == null)
            return;
        DemoRender.CameraPose? c = Camera();
        if (c is { } pose)
            mAudio.SetListener(pose.X, pose.Y, pose.Z, pose.YawDeg * Math.PI / 180);
        mAudio.Frame(dt);
    }

    /// <summary>Gets the near plane the PSC camera query answers for the current view (<c>WorldScene.ViewNear</c>, 004077a8).</summary>
    /// <returns>The near plane.</returns>
    public double ViewNear() => WorldScene.ViewNear(mApp.Play.Moves.FirstPersonNow, World.Psc.ControlClass == "ObserverCamera");

    /// <summary>Draws the 3D view and then the GUI (step F16, the world part).</summary>
    /// <param name="dev">The graphics device.</param>
    /// <param name="fov">The field of view.</param>
    /// <param name="overlay2D">Draws the GUI after the 3D view.</param>
    /// <param name="view">The rectangle of a TSControl that draws the world inside the GUI draw (the observer panel of the command screen), or null.</param>
    /// <returns>False when there is no scene yet.</returns>
    public bool Render(IGfxDevice dev, double fov, Action overlay2D, GfxRect? view = null)
    {
        if (Scene == null || Model == null)
            return false;
        DemoRender.CameraPose? pose = Camera();
        if (pose == null)
            return false;
        DemoRender.CameraPose c = pose.Value;
        var ctx = new FrameContext { HazeStart = Scene.Terrain.HazeStart, Visible = Scene.Terrain.Visible, FirstPersonOfControl = c.FirstPerson, ControlGhost = c.ControlGhost };
        var precip = mApp.Interp.PeekVariable("$pref::weatherPrecipitation");
        Model.WeatherPrecipitation = precip.Length == 0 || Numbers.ParseBool(precip);
        Scene.Near = ViewNear();
        Scene.DynamicLights = Model.CollectLights().Points;
        Scene.TerrainDraw?.Params = Scene.TerrainDraw.Params with { ScreenError = Numbers.BorlandAtof(mApp.Interp.PeekVariable("$screenError")) };
        ctx.ContainerOf = Scene.Interiors == null ? null : box => Scene.Interiors.ContainerOf(box, ctx.Camera);
        Scene.Prepare = cam =>
        {
            ctx.Camera = cam;
            Model.Build(ctx);
        };
        Scene.DrawContents = i => ctx.Lists.DrawContents(dev, i);
        Scene.Interiors?.SimSeconds = Model.Time;
        Scene.Render(c.X, c.Y, c.Z, c.YawDeg, c.PitchDeg, fov, cam =>
        {
            using (FramePerf.Scope("shapes-draw"))
                ctx.Lists.Draw(dev);
        }, overlay2D, c.RollDeg, view);
        return true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Net?.Disconnect();
        Scene?.Dispose();
        Assets?.Dispose();
    }
}
