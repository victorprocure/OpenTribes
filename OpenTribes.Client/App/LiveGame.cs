// One joined game or one playing demo of the `tribes` client (docs/client-net.md 1 to 5, docs/client-app.md 2.2): the
// connection or the demo reader, the client world, the scene built from the environment ghosts, the prediction, and the
// per-frame steps F9 to F16. The scripts do the talking (onConnection, dataFinished, remote evals); this class feeds them.
using System.Globalization;
using OpenTribes.Client.Audio;
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Client.World;
using OpenTribes.ClientNet;
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;
using OpenTribes.Script;

namespace OpenTribes.Client.App;

public sealed class LiveGame : IDisposable
{
    private readonly ClientApp mApp;
    public NetClientSession? Net;
    public DemoPlayback? Demo;
    public ClientWorld World = new();
    public string Mission = "";
    public string DemoName = "";
    public bool Joined;

    // The scene, built once the environment ghosts exist (world-render.md 6.1).
    public WorldScene? Scene;
    public SceneModel? Model;
    public ShapeAssets? Assets;
    private ShapeRenderer? mRenderer;
    private SceneAudio? mAudio;
    private bool mSceneTried;
    private double mTickCarry;
    private int mChatSeen;
    private int mGuiModeSeen = -1;
    private uint mPrevMs;
    private bool mPrevSet;
    public string SceneError = "";
    public readonly List<string> Notes = new();

    public LiveGame(ClientApp app) => mApp = app;

    public bool IsDemo => Demo != null;
    public bool HasScene => Scene != null && Model != null;
    // The mission palette the play view draws with (the HUD bitmaps are paletted too); null before the scene exists.
    public PaletteBank? WorldPalette => HasScene ? Scene!.Assets.Palette : null;

    // ---- start ----

    private void Hook(ClientWorld w)
    {
        World = w;
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
            string fn = "remote" + name;
            if (mApp.Interp.HasFunction(fn))
                mApp.Interp.CallFunction(fn, list);
        };
        w.DataBlocks.OnProgress = (name, pct) =>
            mApp.Interp.CallFunction("dataGotBlock", new[] { name, pct.ToString("0.######", CultureInfo.InvariantCulture) });
        w.Psc.OnScript = null;   // the play delegate evaluates loadPlayGui() on mode changes (PlayDelegate.SetGuiMode)
    }

    // connect(address): the join. R6 is checked by the command.
    public bool StartNet(string hostPort, Interpreter interp)
    {
        var dump = new PacketDump(null);
        string name = interp.PeekVariable("$PCFG::Name");
        Net = new NetClientSession(name.Length > 0 ? name : interp.PeekVariable("$pref::PlayerName") is { Length: > 0 } pn ? pn : "OpenTribes", dump)
        {
            External = true,
            ClockMs = () => mApp.NowMs,
            Log = s => mApp.Log("net: " + s),
            Password = interp.PeekVariable("$Server::JoinPassword"),
            Gender = Or(interp.PeekVariable("$PCFG::Gender"), "Male"),
            Voice = Or(interp.PeekVariable("$PCFG::Voice"), "Male1"),
            SkinBase = Or(interp.PeekVariable("$PCFG::SkinBase"), "base"),
        };
        Hook(Net.World);
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
        Net.OnDataFinished = () => interp.CallFunction("dataFinished", Array.Empty<string>());
        Net.OnAlwaysDone = () =>
        {
            interp.SetVariable("$ClientGhostAlwaysDone", "1");
            interp.CallFunction("onClientGhostAlwaysDone", Array.Empty<string>());
        };
        Net.PacketHook = OnPacket;
        if (!mApp.Opt.NoPredict)
        {
            string root = mApp.Opt.Root;
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

    // playDemo(file): a recording through the same receive path (client-net.md 5.4).
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
        Demo = new DemoPlayback(data) { };
        Demo.OnPacket = OnPacket;
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

    public void Stop()
    {
        Net?.Disconnect();
        Net = null;
        Demo = null;
        Joined = false;
    }

    public void Disconnect()
    {
        Net?.Disconnect();
        Joined = false;
    }

    // ---- remote eval from the console ----

    public bool PostRemoteEval(string[] argv)
    {
        if (Net == null || !Net.IsOpen)
            return false;
        Net.Game.PostRemoteEval(argv);
        return true;
    }

    // ---- per frame ----

    // Steps F9 to F14 (client-app.md 2.2): moves, receive, send, prediction, scene upkeep.
    public void Frame(uint now, MoveBuilder moves)
    {
        uint prev = mPrevSet ? mPrevMs : now;
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
            Net.Frame(now);
            if (Net.IsOpen == false && Joined && !Net.IsAccepted)
                Joined = false;
        }
        mApp.PumpChat(this, ref mChatSeen);
        PollGuiMode();
        mApp.Play.SetScoreVisible(World.Psc.ScoreFlag && World.Psc.GuiMode != 0);
        if (!HasScene && !mSceneTried && World.AlwaysDone)
            BuildScene();
        if (HasScene)
        {
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

    private readonly List<DecodedPacket> mQueued = new();

    // Every decoded packet reaches the scene model; before the scene exists the packets wait (the ghosts they update live in
    // the world's table, so the replay builds the visuals from the latest state).
    private void OnPacket(DecodedPacket pkt)
    {
        if (Model != null)
            Model.OnPacket(pkt);
        else if (pkt.Ghosts != null && mQueued.Count < 20000)
            mQueued.Add(pkt);
    }

    private void PollGuiMode()
    {
        int mode = World.Psc.GuiMode;
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
        string name = IsDemo ? DemoName : Mission;
        Scene = WorldScene.LoadFrom(mApp.Dev, mApp.Res, name, env, out string error);
        SceneError = error;
        if (error.Length > 0)
            mApp.Log("scene: " + error);
        Assets = new ShapeAssets(mApp.Dev, Scene.Assets);
        mRenderer = new ShapeRenderer(Assets);
        Model = new SceneModel(World, Assets, mRenderer, Scene.TerrainModel);
        if (Scene.Interiors != null)
            Scene.Interiors.TeamSkin = Model.TeamSkin;
        Model.Probe = new LightProbe
        {
            Ground = Scene.TerrainModel,
            TerrainLight = Scene.TerrainDraw == null ? null : (x, y) => Scene.TerrainDraw.LightAt(x, y),
            Interior = (sx, sy, sz, dx, dy, dz, len) =>
            {
                double t = 0;
                (float r, float g, float b) light = default;
                bool hit = Scene.Interiors != null && Scene.Interiors.Probe(sx, sy, sz, dx, dy, dz, len, out t, out light);
                return (hit, t, light);
            },
        };
        Model.BaseLight.SetSun(Scene.SunAzimuth, Scene.SunIncidence);
        Model.BaseLight.AmbR = (float)Scene.SunAmbient[0]; Model.BaseLight.AmbG = (float)Scene.SunAmbient[1]; Model.BaseLight.AmbB = (float)Scene.SunAmbient[2];
        Model.BaseLight.SunR = (float)Scene.SunIntensity[0]; Model.BaseLight.SunG = (float)Scene.SunIntensity[1]; Model.BaseLight.SunB = (float)Scene.SunIntensity[2];
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

    public DemoRender.CameraPose? Camera()
    {
        if (Model == null)
            return null;
        if (mApp.Opt.FixedCamera is { } fc)
            return new DemoRender.CameraPose(fc[0], fc[1], fc[2], fc[3], fc[4], false, -1);
        DemoRender.CameraPose? pose = DemoRender.ControlCamera(Model, World);
        if (Net?.PredictWorld != null && Net.Prediction.Object is Player p && Net.Prediction.Active && pose is { FirstPerson: true } cp)
        {
            float[] d = Net.Prediction.DisplayPosition();
            var (ex, ey, ez) = DemoRender.EyeOf(Model, cp.ControlGhost, d[0], d[1], d[2], p.Rotation[2], p.Pitch);
            pose = new DemoRender.CameraPose(ex, ey, ez, p.Rotation[2] * 180 / Math.PI, p.Pitch * 180 / Math.PI, true, cp.ControlGhost);
        }
        // Player_getCameraTransform 00412324: with the view distance (PSC +0x9c) at 0.01 or more the camera is the eye moved back along
        // the view direction by that distance and the control object is drawn as seen from outside. The terrain and interior ray that
        // shortens the distance in the original is not done here.
        if (pose is { FirstPerson: true } fp && Net != null && World.Psc.ControlClass == "Player" && mApp.Play.Moves.ViewDist >= 0.01f)
            pose = DemoRender.Orbit(fp.X, fp.Y, fp.Z, fp.YawDeg * Math.PI / 180, fp.PitchDeg * Math.PI / 180, mApp.Play.Moves.ViewDist);
        return pose;
    }

    private long mImageSteps;

    // The image state of the control player runs once per prediction step, replays included (ImageStateMachine.Stepped).
    private void FeedControlImages()
    {
        ClientPsc psc = World.Psc;
        long total = Net?.Prediction.Steps ?? 0;
        int steps = (int)Math.Max(0, total - mImageSteps);
        mImageSteps = total;
        if (psc.ControlGhost < 0 || Model == null || !Model.Visuals.TryGetValue(psc.ControlGhost, out Visual? v) || v is not PlayerVisual pv)
            return;
        bool predicted = Net?.PredictWorld != null && Net.Prediction.Object is Player && Net.Prediction.Active && psc.ControlClass == "Player";
        pv.SetImageSteps(predicted, steps);
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
            float[] d = Net.Prediction.DisplayPosition();
            s = new ControlSample(d[0], d[1], d[2], p.Velocity[0], p.Velocity[1], p.Velocity[2], p.ContactFlag, p.Jetting, p.Crouching, p.Rotation[2]);
        }
        else
            s = ControlSample.FromControlState(psc, psc.ControlGhost);
        if (s is { } c)
            pv.ApplyControl(c);
    }

    // Step F15: the listener follows the camera.
    public void UpdateSound(double dt, uint now)
    {
        if (mAudio == null)
            return;
        DemoRender.CameraPose? c = Camera();
        if (c is { } pose)
            mAudio.SetListener(pose.X, pose.Y, pose.Z, pose.YawDeg * Math.PI / 180);
        mAudio.Frame(dt);
    }

    // Step F16, the world part: draws the 3D view and then `overlay2D` (the GUI). False when there is no scene yet.
    public bool Render(IGfxDevice dev, double fov, Action overlay2D)
    {
        if (Scene == null || Model == null)
            return false;
        DemoRender.CameraPose? pose = Camera();
        if (pose == null)
            return false;
        DemoRender.CameraPose c = pose.Value;
        var ctx = new FrameContext { HazeStart = Scene.Terrain.HazeStart, Visible = Scene.Terrain.Visible, FirstPersonOfControl = c.FirstPerson, ControlGhost = c.ControlGhost };
        string precip = mApp.Interp.PeekVariable("$pref::weatherPrecipitation");
        Model.WeatherPrecipitation = precip.Length == 0 || Numbers.ParseBool(precip);
        Scene.DynamicLights = Model.CollectLights().Points;
        ctx.ContainerOf = Scene.Interiors == null ? null : box => Scene.Interiors.ContainerOf(box, ctx.Camera);
        Scene.Prepare = cam =>
        {
            ctx.Camera = cam;
            Model.Build(ctx);
        };
        Scene.DrawContents = i => ctx.Lists.DrawContents(dev, i);
        if (Scene.Interiors != null)
            Scene.Interiors.SimSeconds = Model.Time;
        Scene.Render(c.X, c.Y, c.Z, c.YawDeg, c.PitchDeg, fov, cam =>
        {
            using (FramePerf.Scope("shapes-draw"))
                ctx.Lists.Draw(dev);
        }, overlay2D);
        return true;
    }

    public void Dispose()
    {
        Net?.Disconnect();
        Scene?.Dispose();
        Assets?.Dispose();
    }
}
