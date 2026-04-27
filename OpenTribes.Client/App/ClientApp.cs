// The `tribes` client process (ROADMAP M9 C8): docs/client-app.md 1 (start-up), 2 (frame), 3 (input), 6 (shutdown) and
// docs/client-scripts.md 6 (the stock start-up). console.cs runs on the client branch; everything after it is the stock
// scripts talking to the engine through the commands of ClientCommandHandlers and the callbacks raised here.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using OpenTribes.Client.Audio;
using OpenTribes.Client.Scene;
using OpenTribes.Client.World;
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Platform.Audio;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Gl;
using OpenTribes.Platform.Sdl;
using OpenTribes.Script;
using OpenTribes.Sim;

namespace OpenTribes.Client.App;

public sealed class ClientApp : IDisposable
{
    public readonly ClientOptions Opt;
    public Interpreter Interp = null!;
    public ResourceManager Res = null!;
    public SimConsole SimWorld = null!;
    public ClientState Maps = null!;
    public InputDispatcher Input = null!;
    public GuiHost Host = null!;
    public GuiCanvas Canvas = null!;
    public GuiRuntime GuiRt = null!;
    public GlWindow? Win;
    public GlGfxDevice? Dev;
    public SoundManager? Sound;
    public SfxCommands? Sfx;
    public AudioOutput? AudioDev;
    public PlayDelegate Play = null!;
    public ClientCommandHandlers Handlers = null!;
    public ClientDelegates Delegates = null!;
    public LiveGame? Game;
    public readonly IActionConsumer ConsoleConsumer = new NullConsumer();

    public float Gamma = 1f;
    public PaletteBank? CurrentPalette;
    public bool IsFullscreen => Win?.IsFullscreen() ?? false;
    public (int W, int H) FullscreenSize = (640, 480);
    public bool LockSize, WindowsMouse, WindowsKeyboard;
    public bool ForceWindowed;

    private readonly Stopwatch mClock = Stopwatch.StartNew();
    private uint mVirtualMs;
    private bool mQuit;
    public int ExitCode;
    private StreamWriter? mLogFile;
    public readonly List<string> LogTail = new();
    private ClientScript? mScript;
    private readonly List<string> mPendingShots = new();
    private int mNextAutoShot;
    private bool mPaletteSet;
    private uint mLastFrameMs;
    private bool mGrab, mRelativeMouse;
    public long Frames;

    private sealed class NullConsumer : IActionConsumer
    {
        public bool Removing => false;
        public bool OnAction(int action, int kind, float value) => false;
    }

    public ClientApp(ClientOptions opt) => Opt = opt;

    public uint NowMs => Opt.VirtualStep != 0 ? mVirtualMs : (uint)mClock.ElapsedMilliseconds;

    // ---- log ----

    public void Log(string line)
    {
        Interp?.Print(1, line);
    }

    private void Sink(string line)
    {
        if (LogTail.Count >= 4000)
            LogTail.RemoveRange(0, 1000);
        LogTail.Add(line);
        mLogFile?.WriteLine(line);
        if (Opt.Echo)
            Console.Out.WriteLine(line);
    }

    // ---- start-up (client-app.md 1.3 and 1.4) ----

    public bool Start(out string error)
    {
        error = "";
        string consoleCs = Path.Combine(Opt.Root, "console.cs");
        if (!File.Exists(consoleCs))
        {
            error = "console.cs not found in " + Opt.Root;
            return false;
        }
        if (Opt.LogFile != null)
        {
            mLogFile = new StreamWriter(Opt.LogFile, false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        if (!Opt.Headless)
        {
            Win = GlWindow.TryCreate("Tribes", Opt.Width, Opt.Height, false, Opt.VideoDriver, out string winError);
            if (Win == null)
            {
                error = "cannot open a window with an OpenGL 3.3 core context: " + winError;
                return false;
            }
            Dev = new GlGfxDevice(Win);
            FramePerf.Device = Dev as IGfxStats;
            Win.SetSwapInterval(Opt.NoVsync || Opt.VirtualStep != 0 ? 0 : 1);
            Win.SetRelativeMouse(false);
        }
        ForceWindowed = Opt.Windowed;

        Res = new ResourceManager(Opt.Root);
        Interp = new Interpreter();
        Interp.SetOutput(Sink);
        Interp.SetResources(Res);
        Interp.SetVariable("$Console::Prompt", "% ");
        Interp.BindVariable("$ConsoleWorld::DefaultSearchPath", path => Res.SetSearchPath(path));
        Interp.BindVariable("$Console::logMode", _ => { });
        Maps = Builtins.RegisterBuiltins(Interp, Res, Opt.Root);
        var ctx = new ClassContext { Resources = Res, StartNetwork = _ => true };
        SimWorld = new SimConsole(Interp, ctx) { ClientMode = true };
        SimWorld.RegisterCommands();
        Interp.SetObjects(SimWorld);
        SimWorld.SetClockSource(() => NowMs / 1000.0);
        // The commands the dedicated server registers as inert stubs and the client scripts call on start-up
        // (addCMCommand, setMaterialProperty, ...): the game plugin of the port, over the client world.
        var game = new GameState { Sim = SimWorld, Console = Interp, Resources = Res };
        mGameState = game;
        ctx.Game = game;
        GameCommands.Register(Interp, game);

        Host = new GuiHost(Interp, Res) { Actions = Maps, Root = Opt.Root, Now = () => NowMs, Log = l => Interp.Print(1, l) };
        Canvas = new GuiCanvas(Host, Dev?.Width ?? Opt.Width, Dev?.Height ?? Opt.Height);
        GuiRt = new GuiRuntime(Interp, Host, Canvas);
        GuiRt.RenderRequested = () => { RenderFrame(); };
        Host.OnPalette = name =>
        {
            PaletteBank? p = GfxAssets.LoadPalette(Res, name);
            if (p != null)
                SetPalette(p);
        };

        Host.OnMoviePalette = MoviePalette;
        Host.OnMovieEnd = () =>
        {
            if (CurrentPalette != null)
                Dev?.SetPalette(CurrentPalette, Gamma);
        };
        Host.StartMovieAudio = StartMovieAudio;

        Input = new InputDispatcher(Maps) { Eval = t => Interp.Evaluate(t) };
        Input.OfferToGui = OfferToGui;
        Input.GlobalMap = Maps.FindMap("SimGame.sae");
        Play = new PlayDelegate(this);
        Handlers = new ClientCommandHandlers(this);

        SetupAudio();
        Handlers.Register();
        Delegates = new ClientDelegates(this);
        Delegates.Install();
        RegisterEngineVariables();

        Interp.SetVariable("$cargv0", "Tribes.exe");
        for (int i = 0; i < Opt.GameArgs.Count; ++i)
            Interp.SetVariable("$cargv" + (i + 1).ToString(CultureInfo.InvariantCulture), ByteString.FromHost(Opt.GameArgs[i]));
        Interp.SetVariable("$cargc", (Opt.GameArgs.Count + 1).ToString(CultureInfo.InvariantCulture));
        Interp.SetVariable("$CommandLinePath", Opt.Root);
        Interp.SetVariable("$CPU::estimatedSpeed", "0");

        if (Opt.InputScript != null)
            mScript = ClientScript.Load(this, Opt.InputScript);

        SimWorld.UpdateClock();
        Interp.Evaluate(ByteString.FromBytes(File.ReadAllBytes(consoleCs)), "console.cs");
        mLastFrameMs = NowMs;
        return true;
    }

    private void SetupAudio()
    {
        Sound = new SoundManager { ReadWave = n => GfxAssets.Read(Res, n, out byte[] d) ? d : null };
        Sfx = new SfxCommands(Interp, Sound)
        {
            OpenDevice = () =>
            {
                if (Opt.NoAudio)
                {
                    Sound.Mixer ??= new SoundMixer(48000);
                    return true;
                }
                AudioDev ??= AudioOutput.Open();
                if (!AudioDev.Opened)
                {
                    // No device: the sound system runs silent on an offline mixer, like a stock client without a card
                    // would print the failure; here the scripts keep working (docs/audio.md 2.1).
                    Sound.Mixer ??= new SoundMixer(48000);
                    return true;
                }
                Sound.Mixer = AudioDev.Mixer;
                return true;
            },
            CloseDevice = () => { Sound.Mixer = null; },
        };
        Host.Audio = new GuiAudio(Sound);
    }

    // The engine variables of client-scripts.md 5.1 that the engine reads back (the rest live as plain script variables).
    private void RegisterEngineVariables()
    {
        EngineVariables.Register(Interp);
        Play.Moves.PrefFov = () => Numbers.BorlandAtof(Interp.PeekVariable("$pref::PlayerFov")) is var f and > 1 ? f : 90;
        Play.Moves.ReversePitch = () => false;
    }

    // ---- palette and video ----

    public void SetPalette(PaletteBank bank)
    {
        CurrentPalette = bank;
        mPaletteSet = true;
        Dev?.SetPalette(bank, Gamma);
    }

    // A Smacker movie palette (768 RGB bytes) replaces `count` entries of the active palette from `start` (docs/gui.md Q12). The
    // active palette itself (CurrentPalette) stays, so the end of the movie brings it back.
    private void MoviePalette(byte[] rgb, int start, int count)
    {
        PaletteBank cur = CurrentPalette ?? PaletteTables.Grayscale();
        SubPalette sub = cur.SubPalettes[0];
        var colors = (byte[])sub.Colors.Clone();
        for (int i = 0; i < count && start + i < 256; ++i)
            Array.Copy(rgb, i * 3, colors, (start + i) * 4, 3);
        var bank = new PaletteBank
        {
            ShadeBits = cur.ShadeBits,
            HazeSteps = cur.HazeSteps,
            HazeColor = cur.HazeColor,
            AllowedColors = cur.AllowedColors,
            ColorWeights = cur.ColorWeights,
            UsedStart = cur.UsedStart,
            UsedEnd = cur.UsedEnd,
        };
        bank.SubPalettes.AddRange(cur.SubPalettes);
        bank.SubPalettes[0] = new SubPalette { Colors = colors, Id = sub.Id, Kind = sub.Kind, ShadeHazeTable = sub.ShadeHazeTable, BlendTable = sub.BlendTable, IdentityMap = sub.IdentityMap, IndexToRed = sub.IndexToRed, IndexToGreen = sub.IndexToGreen, IndexToBlue = sub.IndexToBlue, IndexToAlpha = sub.IndexToAlpha };
        Dev?.SetPalette(bank, Gamma);
    }

    // The movie sound goes through the same mixer as every other sound (opened on demand when no script did).
    private Action? StartMovieAudio(WavData wav)
    {
        if (Sound == null)
            return null;
        if (Sound.Mixer == null)
            Sfx?.OpenDevice?.Invoke();
        SoundMixer? mixer = Sound.Mixer;
        MixVoice? voice = mixer?.Start(wav, false, 1f, 1f);
        Interp.Print(1, voice == null ? "movie sound: no mixer voice" : $"movie sound: {wav.Frames} frames at {wav.Rate} Hz, {wav.Channels} channels, started");
        return voice == null ? null : () => mixer!.Stop(voice);
    }

    public void SetGamma(float g)
    {
        Gamma = g;
        if (CurrentPalette != null)
            Dev?.SetPalette(CurrentPalette, g);
    }

    public void SetFullscreen(bool on)
    {
        if (Win == null || ForceWindowed && on)
            return;
        Win.SetFullscreen(on);
    }

    public void ResizeWindow(int w, int h)
    {
        if (!LockSize || IsFullscreen)
            Win?.SetSize(w, h);
    }

    public void GrabMouse(bool on)
    {
        mGrab = on;
        UpdateRelativeMouse();
    }

    private void UpdateRelativeMouse()
    {
        // client-app.md 3.8 (D48): relative mouse mode whenever the window has focus and mouse0 is active.
        bool want = mGrab || Canvas != null && (Canvas.WindowActive && !WindowsMouse);
        want = want && Canvas?.WindowActive == true && Handlers.DeviceActive("mouse0");
        if (want == mRelativeMouse)
            return;
        mRelativeMouse = want;
        Win?.SetRelativeMouse(want);
    }

    public void QueueScreenshot(string path) => mPendingShots.Add(path);

    public void RequestQuit() => mQuit = true;
    public bool QuitRequested => mQuit;

    // ---- addresses and the net (R6) ----

    public bool IsLocalAddress(string address)
    {
        string a = address.Trim();
        if (a.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
            a = a[3..];
        if (a.StartsWith("LOOPBACK", StringComparison.OrdinalIgnoreCase))
            return true;
        int colon = a.LastIndexOf(':');
        string host = colon >= 0 ? a[..colon] : a;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? ip) && System.Net.IPAddress.IsLoopback(ip);
    }

    public bool Connect(string address)
    {
        Game?.Stop();
        Game?.Dispose();
        Game = null;
        string a = address.Trim();
        if (a.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
            a = a[3..];
        if (!IsLocalAddress(a) && !Opt.AllowRemote)
        {
            Interp.SetVariable("$errorString", "Only local servers can be joined (127.0.0.0/8); start with --allow-remote to lift this.");
            Log("connect: " + address + " is not a local address");
            Interp.CallFunction("onConnection", new[] { "Rejected" });
            return false;
        }
        if (a.StartsWith("LOOPBACK", StringComparison.OrdinalIgnoreCase))
            a = "127.0.0.1" + (a.Contains(':') ? a[a.IndexOf(':')..] : "");
        if (a.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            a = "127.0.0.1";
        a = a.Replace("localhost:", "127.0.0.1:", StringComparison.OrdinalIgnoreCase);
        Game = new LiveGame(this);
        Play.Reset();
        bool ok;
        try
        {
            ok = Game.StartNet(a, Interp);
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            Log("connect: bad address " + address);
            ok = false;
        }
        if (!ok)
        {
            Interp.SetVariable("$errorString", "Could not connect to " + address);
            Interp.CallFunction("onConnection", new[] { "Rejected" });
            Game.Dispose();
            Game = null;
        }
        return ok;
    }

    public void Disconnect()
    {
        if (Game == null)
        {
            Log("Disconnect: no CSDelegate.");
            return;
        }
        Game.Disconnect();
        Game.Dispose();
        Game = null;
        Interp.SetVariable("$playingDemo", "0");
        Play.Reset();
        Host.Hud = null;
    }

    public bool PlayDemo(string file)
    {
        Game?.Stop();
        Game?.Dispose();
        string name = file;
        string[] tries = { name, "recordings\\" + name, "recordings\\" + name + ".rec", name + ".rec" };
        string? path = null;
        foreach (string t in tries)
        {
            string host = Builtins.ResolveGamePath(Opt.Root, t);
            if (host.Length > 0 && File.Exists(host))
            {
                path = host;
                break;
            }
        }
        if (path == null && Res.Find(file) != null)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "ot-demo-" + Guid.NewGuid().ToString("N") + ".rec");
            if (Res.Read(file, out byte[] data))
            {
                File.WriteAllBytes(tmp, data);
                path = tmp;
            }
        }
        if (path == null)
        {
            Log("PlayDemo: cannot open " + file);
            Game = null;
            return false;
        }
        Game = new LiveGame(this);
        Play.Reset();
        if (!Game.StartDemo(path, Interp))
        {
            Game.Dispose();
            Game = null;
            return false;
        }
        return true;
    }

    private static object? sHitObj;
    private static OpenTribes.World.Vec3 sHit, sNormal;

    // Called when a game starts: the HUD source and the chat ring follow the new world.
    public void InitSecondaryHooks(LiveGame g)
    {
        Host.Chat.Clear();
        Host.CenterPrint("", 0);
        Host.Hud = new ClientHudSource(g.World, () => GuiRt.ClockSeconds)
        {
            MenuSource = CurrentMenu,
            PlayMenuSource = PlayMenu,
            ViewCamera = (w, h) => g.Camera() is { } c
                ? new Camera3D(c.X, c.Y, c.Z, c.YawDeg, c.PitchDeg, Play.Moves.CurrentFov, w, h, World.WorldScene.NearPlane, 1000)
                : null,
            HeadMount = ghost => g.Model != null && g.Model.Visuals.TryGetValue(ghost, out Visual? v) && v is PlayerVisual pv ? pv.MidBack() : null,
            // the line from the eye to the target meets terrain or interior [code 004aca84, mask 0x20e00006]
            LineClear = (a, b) => g.Net?.PredictWorld is not { } pw ||
                !pw.Game.World.CastRay(new OpenTribes.World.Vec3((float)a.X, (float)a.Y, (float)a.Z), new OpenTribes.World.Vec3((float)b.X, (float)b.Y, (float)b.Z), 0x20e00006u, null,
                    ref sHitObj, ref sHit, ref sNormal),
        };
    }

    // The console world of the client: the chat menus of the scripts live in it.
    private GameState mGameState = null!;

    // The chat menu a script has on display (remoteNewMenu: setCMMode(CurServerMenu, 1)) with the items of its top level.
    public ServerMenuView? CurrentMenu()
    {
        if (mGameState.ChatMenus.Showing(mGameState, "CurServerMenu") is not var (title, tree))
            return null;
        return new ServerMenuView(title, tree.Root.Children.Select(c => (c.Key, c.Text)).ToList());
    }

    // The player chat menu (`v`) as the chat display draws it [code 004a633c].
    public PlayMenuView? PlayMenu()
    {
        if (mGameState.ChatMenus.Showing(mGameState, "PlayChatMenu") is not var (title, tree))
            return null;
        string path = tree.Path();
        return new PlayMenuView(path.Length == 0 ? title : path, tree.Lines());
    }

    // A key event while a chat menu is on (the menus take events before the canvas, `client-scripts.md` 3.6 step 3): the item with that
    // key runs its command words as a console command [code 00402068, 004021cc].
    private bool MenuKey(InputEvent ev, char ch) =>
        ev.DeviceType == 3 && ev.DeviceInst == 0 && ev.ObjType == 10 &&
        mGameState.ChatMenus.OnKey(mGameState, ev.ObjInst, ev.Action, ch, ev.Modifiers, words => Interp.CallFunction(words[0], words.Skip(1).ToArray()));

    // Chat lines of the client PlayerManager go to the HUD chat ring (client-net.md 2.4).
    public void PumpChat(LiveGame g, ref int seen)
    {
        List<string> chat = g.World.Players.Chat;
        if (seen > chat.Count)
            seen = 0;
        List<int> types = g.World.Players.ChatTypes;
        for (; seen < chat.Count; ++seen)
        {
            int type = seen < types.Count ? types[seen] : 0;
            // Type 4 is the one-line current command (FearGuiChatDisplay slot 0x100), not a ring line (004a525c).
            if (type == 4)
                Host.CommandText = chat[seen];
            else
                Host.AddChat(chat[seen], (uint)type);
            // PlayerManager_message 0040dbfc also prints the line to the console as MSG: "<escaped text>".
            Interp.Print(1, "MSG: \"" + Builtins.ExportEscape(chat[seen]) + "\"");
        }
        if (Host.Chat.Count > 200)
            Host.Chat.RemoveRange(0, Host.Chat.Count - 200);
        // The command line shows while the local record holds a command (+0x40 >= 0), green once its status is 2 (004a633c).
        ClientPlayerRecord? me = g.World.Players.Get(g.World.PlayerId);
        Host.CommandActive = me != null && me.Commander >= 0;
        Host.CommandAccepted = me != null && me.CommandStatus == 2;
    }

    // ---- frame loop (client-app.md 2.2) ----

    public int Run()
    {
        if (!Start(out string error))
        {
            Console.Error.WriteLine("tribes: " + error);
            return 1;
        }
        Win?.Show();
        var realStart = Stopwatch.StartNew();
        while (!mQuit)
        {
            Step();
            if (Opt.Seconds > 0 && NowMs / 1000.0 >= Opt.Seconds)
                break;
            if (Opt.VirtualStep == 0 && Opt.NoVsync)
            {
                using (FramePerf.Scope("nosync-sleep"))
                    Thread.Sleep(1);
            }
        }
        _ = realStart;
        Shutdown();
        return ExitCode;
    }

    // One frame on the virtual clock when `--virtual-time` or `--headless` is on (the clock moves by the step first).
    public void Step()
    {
        if (Opt.VirtualStep != 0)
            mVirtualMs += Opt.VirtualStep;
        Frame();
    }

    public void Frame()
    {
        ++Frames;
        FramePerf.FrameStart();
        // F1: message pump.
        PumpEvents();
        // F2: clock.
        SimWorld.UpdateClock();
        uint now = NowMs;
        // test hooks: the timed script runs where the input poll runs (F6).
        mScript?.Run(now);
        if (mScript != null)
        {
            foreach (string t in Canvas.Trace)
                Interp.Print(1, "  [canvas] " + t);
            Canvas.Trace.Clear();
        }
        // F5/F6 follow: the canvas content decides which maps are pushed.
        Play.UpdateContent(Canvas.Content?.Id ?? 0);
        Delegates.Poll();
        // F8-F14: moves, receive, send, sim advance, prediction.
        using (FramePerf.Scope("net-sim"))
        {
            Game?.Frame(now, Play.Moves);
            SimWorld.AdvanceServer();
        }
        // F15: sound.
        double dt = Math.Max(0, (now - mLastFrameMs) / 1000.0);
        mLastFrameMs = now;
        if (Sound != null)
        {
            Game?.UpdateSound(dt, now);
            Sound.Tick(now);
        }
        // F16: render.
        RenderFrame();
        // F17: end of frame.
        SimWorld.EndFrame();
        if (mQuit)
            return;
        if (Win != null && !Canvas.WindowActive && Opt.VirtualStep == 0)
        {
            using (FramePerf.Scope("idle-sleep"))
                Thread.Sleep(10);
        }
    }

    internal bool PlayViewActive => Canvas.Content?.Id is PlayDelegate.PlayGuiId;

    public void RenderFrame()
    {
        if (Dev == null || Win == null)
            return;
        (int w, int h) = Win.PixelSize();
        if (w > 0 && h > 0)
        {
            Canvas.Width = w;
            Canvas.Height = h;
        }
        if (!mPaletteSet)
        {
            string shell = Host.TagText(180001) is string t ? GuiHost.FirstItem(t) : "feargui.pal";
            PaletteBank? pal = GfxAssets.LoadPalette(Res, shell) ?? GfxAssets.LoadPalette(Res, "feargui.pal");
            if (pal != null)
                SetPalette(pal);
        }
        UpdateRelativeMouse();
        using (FramePerf.Scope("gui-frame"))
            Canvas.Frame();
        bool drewWorld = false;
        // The play view draws world and HUD with the mission palette. A shell palette control that wakes late (the loading
        // screen) or a gamma or movie restore must not leave the shell palette active during play (HUD colours).
        if (Game?.WorldPalette is { } worldPalette && PlayViewActive && !ReferenceEquals(CurrentPalette, worldPalette))
            SetPalette(worldPalette);
        if (Game != null && PlayViewActive)
            drewWorld = Game.Render(Dev, Play.Moves.CurrentFov, () =>
            {
                Canvas.Render(Dev);
                DrawDamageFlash();
            });
        if (!drewWorld)
        {
            Dev.BeginFrame();
            Dev.ClearRgb(0, 0, 0);
            Canvas.Render(Dev);
            Dev.EndFrame();
        }
        TakeShots();
    }

    // The damage flash: the whole frame, HUD included, blends toward red by the flash value of the last server block
    // (0..1, the server lets it fall by 0.22 per second). Measured on stock frames: out = frame * (1 - f) + red * f.
    private void DrawDamageFlash()
    {
        float f = (float)(Game?.World.Psc.DamageFlash ?? 0);
        if (Dev != null && f > 0f)
            Dev.FillRectRgb(new GfxRect(0, 0, Dev.Width - 1, Dev.Height - 1), 255, 0, 0, Math.Min(f, 1f));
    }

    private void TakeShots()
    {
        double sec = NowMs / 1000.0;
        while (mNextAutoShot < Opt.ScreenshotAt.Count && Opt.ScreenshotAt[mNextAutoShot] <= sec)
        {
            double t = Opt.ScreenshotAt[mNextAutoShot++];
            mPendingShots.Add(Path.Combine(Opt.ShotDir, "shot_" + t.ToString("0.###", CultureInfo.InvariantCulture) + ".png"));
        }
        if (mPendingShots.Count == 0 || Dev == null)
            return;
        byte[] px = Dev.ReadPixels();
        foreach (string path in mPendingShots)
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (full.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                WriteBmp(full, px, Dev.Width, Dev.Height);
            else
                MissionRender.WritePng(full, px, Dev.Width, Dev.Height);
            Log("screenshot: " + full);
        }
        mPendingShots.Clear();
    }

    // temp\sshotNNNN.bmp: 24-bit Windows bitmap, bottom-up rows.
    private static void WriteBmp(string path, byte[] rgba, int w, int h)
    {
        int row = (w * 3 + 3) & ~3;
        using var f = File.Create(path);
        using var bw = new BinaryWriter(f);
        bw.Write((ushort)0x4d42);
        bw.Write(54 + row * h);
        bw.Write(0);
        bw.Write(54);
        bw.Write(40);
        bw.Write(w);
        bw.Write(h);
        bw.Write((ushort)1);
        bw.Write((ushort)24);
        bw.Write(0);
        bw.Write(row * h);
        bw.Write(2835);
        bw.Write(2835);
        bw.Write(0);
        bw.Write(0);
        var line = new byte[row];
        for (int y = h - 1; y >= 0; --y)
        {
            for (int x = 0; x < w; ++x)
            {
                int s = (y * w + x) * 4;
                line[x * 3] = rgba[s + 2];
                line[x * 3 + 1] = rgba[s + 1];
                line[x * 3 + 2] = rgba[s];
            }
            bw.Write(line);
        }
    }

    // ---- input (client-app.md 3) ----

    private bool OfferToGui(InputEvent ev, float value, char ch)
    {
        if (Canvas.FirstResponder is not GuiTextEdit && MenuKey(ev, ch))
            return true;
        var raw = new RawInput
        {
            Device = ev.DeviceType == 3 ? RawInput.DevKeyboard : RawInput.DevMouse,
            ObjType = ev.ObjType,
            Index = ev.ObjInst,
            Action = (byte)(ev.Action == 1 ? 1 : 0),
            Ascii = ch,
            Mod = ev.Modifiers,
            Value = value,
        };
        return Canvas.HandleInput(raw);
    }

    public void InjectKey(int scan, bool down)
    {
        if (scan <= 0 || !Handlers.DeviceActive("keyboard0"))
            return;
        Input.Process(new InputEvent { DeviceType = 3, DeviceInst = 0, ObjType = 10, ObjInst = (byte)scan, Action = (byte)(down ? 1 : 2) }, down ? 1f : 0f);
    }

    public void InjectButton(int index, bool down)
    {
        if (!Handlers.DeviceActive("mouse0"))
            return;
        Input.Process(new InputEvent { DeviceType = 2, DeviceInst = 0, ObjType = 2, ObjInst = (byte)index, Action = (byte)(down ? 1 : 2) }, down ? 1f : 0f);
    }

    public void InjectAxis(int axis, float counts)
    {
        if (counts == 0 || !Handlers.DeviceActive("mouse0"))
            return;
        Input.Process(new InputEvent { DeviceType = 2, DeviceInst = 0, ObjType = (byte)(11 + axis), ObjInst = 0, Action = 3 }, counts);
    }

    private void PumpEvents()
    {
        if (Win == null)
            return;
        while (Sdl3.PollEvent(out SdlEvent ev))
        {
            switch (ev.Type)
            {
                case Sdl3.EventQuit:
                case Sdl3.EventWindowCloseRequested:
                    OnCloseBox();
                    break;
                case Sdl3.EventKeyDown:
                case Sdl3.EventKeyUp:
                    if (ev.Type == Sdl3.EventKeyDown && ev.Repeat != 0)
                        break;
                    InjectKey(ScanCodeMap.FromSdl((int)ev.Scancode), ev.Type == Sdl3.EventKeyDown);
                    break;
                case Sdl3.EventMouseMotion:
                    InjectAxis(0, ev.XRel);
                    InjectAxis(1, ev.YRel);
                    break;
                case Sdl3.EventMouseButtonDown:
                case Sdl3.EventMouseButtonUp:
                    {
                        // SDL: 1 left, 2 middle, 3 right; the device: button0 left, button1 right, button2 middle.
                        int b = ev.Button switch { 1 => 0, 3 => 1, 2 => 2, _ => -1 };
                        if (b >= 0)
                            InjectButton(b, ev.Type == Sdl3.EventMouseButtonDown);
                        break;
                    }
                case Sdl3.EventMouseWheel:
                    InjectAxis(2, ev.WheelY * 120f);
                    break;
                case Sdl3.EventWindowFocusLost:
                    Canvas.WindowActive = false;
                    Input.Clear();
                    break;
                case Sdl3.EventWindowFocusGained:
                    Canvas.WindowActive = true;
                    break;
            }
        }
    }

    // The close box (client-app.md 6.1): the canvas quit flag is true in the stock script, so the window ends the process.
    private void OnCloseBox()
    {
        if (Interp.HasFunction("onQuit"))
            Interp.CallFunction("onQuit", Array.Empty<string>());
        else
            mQuit = true;
    }

    // ---- shutdown (client-app.md 6.2) ----

    public void Shutdown()
    {
        try
        {
            Interp.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\quit.gui\");");
            if (Interp.HasFunction("onExit"))
                Interp.CallFunction("onExit", Array.Empty<string>());
        }
        catch (Exception e)
        {
            Log("shutdown: " + e.Message);
        }
        if (Opt.ReportUnknown)
        {
            Log("unknown commands (" + Interp.UnknownCommands().Count + "):");
            foreach (var kv in Interp.UnknownCommands())
                Log(kv.Value.ToString(CultureInfo.InvariantCulture).PadLeft(6) + " " + kv.Key);
        }
        TakeShots();
    }

    public void Dispose()
    {
        Game?.Dispose();
        AudioDev?.Dispose();
        Dev?.Dispose();
        Win?.Dispose();
        mLogFile?.Dispose();
    }
}
