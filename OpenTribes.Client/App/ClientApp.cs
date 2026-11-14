namespace OpenTribes.Client.App;

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

/// <summary>The <c>tribes</c> client process: start-up, frame loop, input and shutdown.</summary>
/// <remarks>docs/client-app.md 1 (start-up), 2 (frame), 3 (input) and 6 (shutdown), and docs/client-scripts.md 6 (the stock start-up). console.cs runs on the client branch. Everything after it is the stock scripts talking to the engine through the commands of <see cref="ClientCommandHandlers"/> and the callbacks raised here.</remarks>
public sealed class ClientApp : IDisposable
{
    /// <summary>The command line options.</summary>
    public ClientOptions Opt { get; }
    /// <summary>The script interpreter.</summary>
    public Interpreter Interp { get; set; } = null!;
    /// <summary>The mounted resources.</summary>
    public ResourceManager Res { get; set; } = null!;
    /// <summary>The simulation console objects.</summary>
    public SimConsole SimWorld { get; set; } = null!;
    /// <summary>The action maps.</summary>
    public ClientState Maps { get; set; } = null!;
    /// <summary>The input dispatcher.</summary>
    public InputDispatcher Input { get; set; } = null!;
    /// <summary>The GUI host.</summary>
    public GuiHost Host { get; set; } = null!;
    /// <summary>The canvas.</summary>
    public GuiCanvas Canvas { get; set; } = null!;
    /// <summary>The GUI runtime.</summary>
    public GuiRuntime GuiRt { get; set; } = null!;
    /// <summary>The window, or null in headless runs.</summary>
    public GlWindow? Win { get; set; }
    /// <summary>The graphics device, or null in headless runs.</summary>
    public GlGfxDevice? Dev { get; set; }
    /// <summary>The joysticks found when the input system opened (client-app.md 3.1). None in headless runs.</summary>
    public JoystickSet? Joysticks { get; set; }
    /// <summary>The sound manager, or null without audio.</summary>
    public SoundManager? Sound { get; set; }
    /// <summary>The sfx console commands, or null without audio.</summary>
    public SfxCommands? Sfx { get; set; }
    /// <summary>The audio output, or null without audio.</summary>
    public AudioOutput? AudioDev { get; set; }
    /// <summary>The play delegate.</summary>
    public PlayScreenHandler Play { get; set; } = null!;
    /// <summary>The console command handlers.</summary>
    public ClientCommandHandlers Handlers { get; set; } = null!;
    /// <summary>The GUI delegates.</summary>
    public ClientDelegates Delegates { get; set; } = null!;
    /// <summary>The joined game or the playing demo, or null.</summary>
    public LiveGame? Game { get; set; }
    /// <summary>The server world of a hosted game (createServer, client-app.md 1.7), in this process next to the client world.</summary>
    public Server.HostedServer Hosted { get; set; } = null!;
    /// <summary>The consumer for actions nobody handles.</summary>
    public IActionConsumer ConsoleConsumer { get; } = new NullConsumer();

    /// <summary>The display gamma.</summary>
    public float Gamma { get; set; } = 1f;
    /// <summary>The palette in use, or null before the first mission palette.</summary>
    public PaletteBank? CurrentPalette { get; set; }
    /// <summary>The renderer of the skin view of the Player Setup screen.</summary>
    public SkinViewRenderer? SkinViews { get; set; }
    /// <summary>True while the window is full screen.</summary>
    public bool IsFullscreen => Win?.IsFullscreen() ?? false;
    /// <summary>The size of the full screen mode.</summary>
    public (int W, int H) FullscreenSize { get; set; } = (640, 480);
    /// <summary>True while the window size is locked (<c>lockWindowSize</c>).</summary>
    public bool LockSize { get; set; }
    /// <summary>True when the canvas handles the mouse as a Windows mouse (<c>windowsMouseEnable</c>).</summary>
    public bool WindowsMouse { get; set; }
    /// <summary>True when the canvas handles the keyboard as a Windows keyboard (<c>windowsKeyboardEnable</c>).</summary>
    public bool WindowsKeyboard { get; set; }
    /// <summary>True to ignore requests for full screen.</summary>
    public bool ForceWindowed { get; set; }

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private uint _virtualMs;

    /// <summary>The process exit code.</summary>
    public int ExitCode { get; set; }
    private StreamWriter? _logFile;
    /// <summary>The last console lines.</summary>
    public List<string> LogTail { get; } = [];
    private ClientScript? _script;
    private readonly List<string> _pendingShots = [];
    private int _nextAutoShot;
    private bool _paletteSet;
    private uint _lastFrameMs;
    private bool _grab, _relativeMouse;
    /// <summary>The number of frames run.</summary>
    public long Frames { get; set; }

    private sealed class NullConsumer : IActionConsumer
    {
        public bool Removing => false;
        public bool OnAction(int action, int kind, float value) => false;
    }

    /// <summary>Creates the client.</summary>
    /// <param name="opt">The command line options.</param>
    public ClientApp(ClientOptions opt) => Opt = opt;

    /// <summary>The app time in milliseconds: the virtual clock when <c>--virtual-time</c> is on, else the real clock.</summary>
    public uint NowMs => Opt.VirtualStep != 0 ? _virtualMs : (uint)_clock.ElapsedMilliseconds;

    // ---- log ----

    /// <summary>Writes a line to the console.</summary>
    /// <param name="line">The line.</param>
    public void Log(string line) => Interp?.Print(1, line);

    private void Sink(string line)
    {
        if (LogTail.Count >= 4000)
            LogTail.RemoveRange(0, 1000);
        LogTail.Add(line);
        _logFile?.WriteLine(line);
        if (Opt.Echo)
            Console.Out.WriteLine(line);
    }

    // ---- start-up (client-app.md 1.3 and 1.4) ----

    /// <summary>Runs the start-up (client-app.md 1.3 and 1.4): opens the window, the device and the audio, and runs <c>console.cs</c>.</summary>
    /// <param name="error">The error text on failure.</param>
    /// <returns>True when the client started.</returns>
    public bool Start(out string error)
    {
        error = "";
        var consoleCs = Path.Combine(Opt.Root, "console.cs");
        if (!File.Exists(consoleCs))
        {
            error = "console.cs not found in " + Opt.Root;
            return false;
        }
        if (Opt.LogFile != null)
        {
            _logFile = new StreamWriter(Opt.LogFile, false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        if (!Opt.Headless)
        {
            Win = GlWindow.TryCreate("Tribes", Opt.Width, Opt.Height, false, Opt.VideoDriver, out var winError);
            if (Win == null)
            {
                error = "cannot open a window with an OpenGL 3.3 core context: " + winError;
                return false;
            }
            Dev = new GlGfxDevice(Win);
            FramePerf.Device = Dev;
            GlWindow.SetSwapInterval(Opt.NoVsync || Opt.VirtualStep != 0 ? 0 : 1);
            Win.SetRelativeMouse(false);
            Joysticks = JoystickSet.Open();
        }
        ForceWindowed = Opt.Windowed;

        Res = new ResourceManager(Opt.Root);
        Interp = new Interpreter();
        Interp.SetOutput(Sink);
        Interp.SetResources(Res);
        Interp.SetVariable("$Console::Prompt", "% ");
        Dev?.PackedTextures = () => !EngineVariables.IsSet(Interp, "$pref::OpenGL::NoPackedTextures") &&
                                       !EngineVariables.IsSet(Interp, "$pref::OpenGL::Use32BitTex");
        Interp.BindVariable("$ConsoleWorld::DefaultSearchPath", path => Res.SetSearchPath(path));
        Interp.BindVariable("$Console::logMode", _ => { });
        Maps = Builtins.RegisterBuiltins(Interp, Res, Opt.Root);
        var ctx = new ClassContext { Resources = Res, StartNetwork = _ => true };
        SimWorld = new SimConsole(Interp, ctx) { ClientMode = true };
        SimWorld.RegisterCommands();
        Hosted = new Server.HostedServer(Interp, Res, Opt.Root, SimWorld, Opt.AllowRemote);
        SimWorld.Worlds = Hosted;
        Interp.SetObjects(SimWorld);
        SimWorld.SetClockSource(() => NowMs / 1000.0);
        // The commands the dedicated server registers as inert stubs and the client scripts call on start-up
        // (addCMCommand, setMaterialProperty, ...): the game plugin of the port, over the client world.
        var game = new GameState { Sim = SimWorld, Console = Interp, Resources = Res };
        _gameState = game;
        ctx.Game = game;
        GameCommands.Register(Interp, game);

        Host = new GuiHost(Interp, Res) { Actions = Maps, Root = Opt.Root, Now = () => NowMs, Log = l => Interp.Print(1, l) };
        // The command map drives the command chat menu by name [code 004d2a98, 00401fe0, 00402068].
        SimObject? FindMenu(string name) => game.Sim?.Server()?.Find(name) is SimObject o && o.ClassName() == "ChatMenu" ? o : null;
        Host.ChatMenuExists = name => FindMenu(name) != null;
        Host.SetChatMenuMode = (name, mode) =>
        {
            if (FindMenu(name) is SimObject o)
                game.ChatMenus.SetMode(o, name, mode);
        };
        game.ChatMenus.CommandMenuOff = () => CommandMap?.SetOrderMode(0);
        game.ChatMenus.CancelOrder = () =>
        {
            var map = CommandMap;
            var ordering = map is { OrderMode: 1 };
            map?.SetOrderMode(0);
            return ordering;
        };
        Canvas = new GuiCanvas(Host, Dev?.Width ?? Opt.Width, Dev?.Height ?? Opt.Height)
        {
            // A TSControl with a camera object other than the play view (the observer panel of the command screen) draws the world itself.
            ViewRenderer = r => Game != null && Dev != null && Game.Render(Dev, Play.Moves.CurrentFov, () => { }, r)
        };
        GuiRt = new GuiRuntime(Interp, Host, Canvas)
        {
            RenderRequested = RenderFrame
        };
        Host.OnPalette = name =>
        {
            var p = GfxAssets.LoadPalette(Res, name);
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
        SkinViews = new SkinViewRenderer(this);
        Host.ModelViews = SkinViews;

        Input = new InputDispatcher(Maps)
        {
            Eval = t => Interp.Evaluate(t),
            OfferToGui = OfferToGui,
            GlobalMap = Maps.FindMap("SimGame.sae")
        };
        Play = new PlayScreenHandler(this);
        Handlers = new ClientCommandHandlers(this);

        SetupAudio();
        Handlers.Register();
        Delegates = new ClientDelegates(this);
        Delegates.Install();
        RegisterEngineVariables();

        Interp.SetVariable("$cargv0", "Tribes.exe");
        for (var i = 0; i < Opt.GameArgs.Count; ++i)
            Interp.SetVariable("$cargv" + (i + 1).ToString(CultureInfo.InvariantCulture), ByteString.FromHost(Opt.GameArgs[i]));
        Interp.SetVariable("$cargc", (Opt.GameArgs.Count + 1).ToString(CultureInfo.InvariantCulture));
        Interp.SetVariable("$CommandLinePath", Opt.Root);
        Interp.SetVariable("$CPU::estimatedSpeed", "0");

        if (Opt.InputScript != null)
            _script = ClientScript.Load(this, Opt.InputScript);

        SimWorld.UpdateClock();
        Interp.Evaluate(ByteString.FromBytes(File.ReadAllBytes(consoleCs)), "console.cs");
        _lastFrameMs = NowMs;
        return true;
    }

    private void SetupAudio()
    {
        Sound = new SoundManager { ReadWave = n => GfxAssets.Read(Res, n, out var d) ? d : null };
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
            CloseDevice = () => Sound.Mixer = null,
        };
        Host.Audio = new GuiAudio(Sound);
    }

    /// <summary>True once a terrain renderer was created and registered its variables (<see cref="EngineVariables.TerrainCells"/>).</summary>
    public bool TerrainVarsBound { get; set; }

    private void RegisterEngineVariables()
    {
        EngineVariables.Register(Interp);
        Play.Moves.ZoomSpeed = () => Numbers.BorlandAtof(Interp.PeekVariable("$pref::PlayerZoomSpeed")) is var zs ? Math.Clamp(zs, 0.0, 1.0) : 0.0;
        Play.Moves.PrefFov = () => Numbers.BorlandAtof(Interp.PeekVariable("$pref::PlayerFov")) is var f and > 1 ? f : 90;
        // PlayerPSC_onAction PITCH (0x35b63) and PITCH_SPEED (0x35c41) negate the pitch for a control object of type 0x20000000 (the
        // Flier/Vehicle ghost classes) while the byte at 0x0061704c ($pref::flipFlierY) is set. No other pitch inversion exists in the
        // stock code: the mouse's own flip is the Flip flag of the yaxis binding (InputDispatcher).
        Play.Moves.TakeControlPitch = () =>
        {
            if (Game?.Net is { PredictWorld: not null } n && n.Prediction.ControlledObject is Player p && n.Prediction.Active && Game.World.Psc.ControlClass == "Player")
            {
                var held = p.Pitch;
                p.Pitch = 0;
                return held;
            }
            return null;
        };
        Play.Moves.ReversePitch = () => Game?.World.Psc.ControlClass is "Flier" or "Vehicle" && EngineVariables.IsSet(Interp, "$pref::flipFlierY");
    }

    // ---- palette and video ----

    /// <summary>Sets the palette of the device.</summary>
    /// <param name="bank">The palette.</param>
    public void SetPalette(PaletteBank bank)
    {
        CurrentPalette = bank;
        _paletteSet = true;
        Dev?.SetPalette(bank, Gamma);
    }

    // A Smacker movie palette (768 RGB bytes) replaces `count` entries of the active palette from `start` (docs/gui.md Q12). The
    // active palette itself (CurrentPalette) stays, so the end of the movie brings it back.
    private void MoviePalette(byte[] rgb, int start, int count)
    {
        var cur = CurrentPalette ?? PaletteTables.Grayscale();
        var sub = cur.SubPalettes[0];
        var colors = (byte[])sub.Colors.Clone();
        for (var i = 0; i < count && start + i < 256; ++i)
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
        var mixer = Sound.Mixer;
        var voice = mixer?.Start(wav, false, 1f, 1f);
        Interp.Print(1, voice == null ? "movie sound: no mixer voice" : $"movie sound: {wav.Frames} frames at {wav.Rate} Hz, {wav.Channels} channels, started");
        return voice == null ? null : () => mixer!.Stop(voice);
    }

    /// <summary>Sets the display gamma and applies it to the palette in use.</summary>
    /// <param name="g">The gamma.</param>
    public void SetGamma(float g)
    {
        Gamma = g;
        if (CurrentPalette != null)
            Dev?.SetPalette(CurrentPalette, g);
    }

    /// <summary>Switches between full screen and a window.</summary>
    /// <param name="on">True for full screen.</param>
    public void SetFullscreen(bool on)
    {
        if (Win == null || (ForceWindowed && on))
            return;
        // Stock switches to $pref::VideoFullScreenRes (setFSResolution, switchMode 005806d8): the canvas becomes that size.
        _desktopApplied = DesktopModeWanted;
        Win.SetFullscreen(on, FullscreenSize.W, FullscreenSize.H, _desktopApplied);
    }

    /// <summary>The preference that holds the "Fullscreen windowed" setting.</summary>
    public const string FullscreenWindowedPref = "$pref::OpenTribes::FullscreenWindowed";
    private bool _desktopApplied;

    /// <summary>True when full screen is the borderless desktop mode: the command line option, else the pref (the Video options check box).</summary>
    public bool DesktopModeWanted => FullscreenPlan.UseDesktopMode(Opt.FullscreenWindowed, Interp.PeekVariable(FullscreenWindowedPref));

    /// <summary>Applies a change of the desktop mode setting at once while in full screen.</summary>
    public void ApplyFullscreenModeChange()
    {
        if (Win != null && IsFullscreen && DesktopModeWanted != _desktopApplied)
            SetFullscreen(true);
    }

    /// <summary>Resizes the window. In full screen it applies the full screen mode, and it does nothing while the size is locked.</summary>
    /// <param name="w">The width in pixels.</param>
    /// <param name="h">The height in pixels.</param>
    public void ResizeWindow(int w, int h)
    {
        if (IsFullscreen)
            SetFullscreen(true);
        else if (!LockSize)
            Win?.SetSize(w, h);
    }

    /// <summary>Grabs or releases the mouse.</summary>
    /// <param name="on">True to grab.</param>
    public void GrabMouse(bool on)
    {
        _grab = on;
        UpdateRelativeMouse();
    }

    private void UpdateRelativeMouse()
    {
        // client-app.md 3.8 (D48): relative mouse mode whenever the window has focus and mouse0 is active.
        var want = _grab || (Canvas != null && Canvas.WindowActive && !WindowsMouse);
        want = want && Canvas?.WindowActive == true && Handlers.DeviceActive("mouse0");
        if (want == _relativeMouse)
            return;
        _relativeMouse = want;
        Win?.SetRelativeMouse(want);
    }

    /// <summary>Queues a screenshot for the end of the frame.</summary>
    /// <param name="path">The PNG path.</param>
    public void QueueScreenshot(string path) => _pendingShots.Add(path);

    /// <summary>Asks the main loop to end.</summary>
    public void RequestQuit() => QuitRequested = true;
    /// <summary>True after <see cref="RequestQuit"/>.</summary>
    public bool QuitRequested { get; private set; }

    // ---- addresses and the net (R6) ----

    /// <summary>Tells whether an address is a loopback address (R6).</summary>
    /// <param name="address">The address, optionally with <c>IP:</c> and a port.</param>
    /// <returns>True for <c>LOOPBACK</c>, <c>localhost</c> and 127.0.0.0/8.</returns>
    public static bool IsLocalAddress(string address)
    {
        var a = address.Trim();
        if (a.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
            a = a[3..];
        if (a.StartsWith("LOOPBACK", StringComparison.OrdinalIgnoreCase))
            return true;
        var colon = a.LastIndexOf(':');
        var host = colon >= 0 ? a[..colon] : a;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }

    /// <summary>Joins a game.</summary>
    /// <param name="address">The server address.</param>
    /// <returns>True when the connection started.</returns>
    public bool Connect(string address)
    {
        Game?.Stop();
        Game?.Dispose();
        Game = null;
        var a = address.Trim();
        if (a.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
            a = a[3..];
        if (!IsLocalAddress(a) && !Opt.AllowRemote)
        {
            Interp.SetVariable("$errorString", "Only local servers can be joined (127.0.0.0/8); start with --allow-remote to lift this.");
            Log("connect: " + address + " is not a local address");
            Interp.CallFunction("onConnection", ["Rejected"]);
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
            Interp.CallFunction("onConnection", ["Rejected"]);
            Game.Dispose();
            Game = null;
        }
        return ok;
    }

    /// <summary>Leaves the current game or demo.</summary>
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

    /// <summary>Plays a recording.</summary>
    /// <param name="file">The file name, with or without the <c>recordings\\</c> folder and the <c>.rec</c> extension.</param>
    /// <returns>True when the demo started.</returns>
    public bool PlayDemo(string file)
    {
        Game?.Stop();
        Game?.Dispose();
        var name = file;
        string[] tries = [name, "recordings\\" + name, "recordings\\" + name + ".rec", name + ".rec"];
        string? path = null;
        foreach (var t in tries)
        {
            var host = Builtins.ResolveGamePath(Opt.Root, t);
            if (host.Length > 0 && File.Exists(host))
            {
                path = host;
                break;
            }
        }
        if (path == null && Res.Find(file) != null)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "ot-demo-" + Guid.NewGuid().ToString("N") + ".rec");
            if (Res.Read(file, out var data))
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

    private static object? _hitObj;
    private static OpenTribes.World.Vec3 _hit, _normal;

    /// <summary>Points the HUD source and the chat ring at the world of a new game.</summary>
    /// <param name="g">The game.</param>
    public void InitSecondaryHooks(LiveGame g)
    {
        Host.Chat.Clear();
        Host.CenterPrint("", 0);
        Host.Hud = new ClientHudSource(g.World, () => GuiRt.ClockSeconds)
        {
            MenuSource = CurrentMenu,
            PlayMenuSource = PlayMenu,
            NamedMenuSource = NamedMenu,
            CommandMapSource = g.CommandMapPicture,
            FirstPersonSource = () => Play.Moves.FirstPersonNow,
            ZoomSource = () => Play.Moves.HudZoomState(),
            ViewCamera = (w, h) => g.Camera() is { } c
                ? new Camera3D(c.X, c.Y, c.Z, c.YawDeg, c.PitchDeg, Play.Moves.CurrentFov, w, h, g.ViewNear(), 1000)
                : null,
            HeadMount = ghost => g.Model != null && g.Model.Visuals.TryGetValue(ghost, out var v) && v is PlayerVisual pv ? pv.MidBack() : null,
            MuzzleMount = ghost => g.Model != null && g.Model.Visuals.TryGetValue(ghost, out var v) && v is PlayerVisual pv ? pv.Muzzle() : null,
            ControlAim = g.ControlAimInputs,
            ObjectYaw = g.ControlHeading,
            ObjectBox = ghost => g.Model != null && g.Model.Visuals.TryGetValue(ghost, out var v) && v is ShapeVisual sv ? sv.HudBox() : null,
            // the line from the eye to the target meets terrain or interior [code 004aca84, mask 0x20e00006]
            LineClear = (a, b) => g.Net?.PredictWorld is not { } pw ||
                !pw.Game.World.CastRay(new OpenTribes.World.Vec3((float)a.X, (float)a.Y, (float)a.Z), new OpenTribes.World.Vec3((float)b.X, (float)b.Y, (float)b.Z), 0x20e00006u, null,
                    ref _hitObj, ref _hit, ref _normal),
        };
    }

    /// <summary>The command map control (DAT_006a83d8), found by its id while the command screen is loaded.</summary>
    public GuiCommandMap? CommandMap => GuiRt?.FindControl(PlayScreenHandler.CommanderMapId.ToString(CultureInfo.InvariantCulture)) as GuiCommandMap;

    // The console world of the client: the chat menus of the scripts live in it.
    private GameState _gameState = null!;

    /// <summary>Gets the chat menu a script has on display (<c>remoteNewMenu</c>: <c>setCMMode(CurServerMenu, 1)</c>) with the items of its top level.</summary>
    /// <returns>The menu, or null.</returns>
    public ServerMenuView? CurrentMenu()
    {
        if (_gameState.ChatMenus.Showing(_gameState, "CurServerMenu") is not var (title, tree))
            return null;
        return new ServerMenuView(title, [.. tree.Root.Children.Select(c => (c.Key, c.Text))]);
    }

    /// <summary>Gets the chat menu object a FGChatMenu control names (004a7934).</summary>
    /// <param name="name">The menu name.</param>
    /// <returns>The state, or null when no ChatMenu has that name.</returns>
    public ChatMenuState? NamedMenu(string name)
    {
        if (_gameState.Sim?.Server()?.Find(name) is not SimObject obj || obj.ClassName() != "ChatMenu")
            return null;
        return _gameState.ChatMenus.Showing(_gameState, name) is var (_, tree)
            ? new ChatMenuState(true, tree.Path(), tree.Lines())
            : new ChatMenuState(false, "", []);
    }

    /// <summary>Gets the player chat menu (<c>v</c>) as the chat display draws it (004a633c).</summary>
    /// <returns>The menu, or null.</returns>
    public PlayMenuView? PlayMenu()
    {
        if (_gameState.ChatMenus.Showing(_gameState, "PlayChatMenu") is not var (title, tree))
            return null;
        var path = tree.Path();
        return new PlayMenuView(path.Length == 0 ? title : path, tree.Lines());
    }

    // A key event while a chat menu is on (the menus take events before the canvas, `client-scripts.md` 3.6 step 3): the item with that
    // key runs its command words as a console command [code 00402068, 004021cc].
    private bool MenuKey(InputEvent ev, char ch) =>
        ev.DeviceType == 3 && ev.DeviceInst == 0 && ev.ObjType == 10 &&
        _gameState.ChatMenus.OnKey(_gameState, ev.ObjInst, ev.Action, ch, ev.Modifiers, words => Interp.CallFunction(words[0], [.. words.Skip(1)]));

    /// <summary>Moves the chat lines of the client PlayerManager to the HUD chat ring (client-net.md 2.4).</summary>
    /// <param name="g">The game.</param>
    /// <param name="seen">The number of lines already moved.</param>
    public void PumpChat(LiveGame g, ref int seen)
    {
        var chat = g.World.Players.Chat;
        if (seen > chat.Count)
            seen = 0;
        var types = g.World.Players.ChatTypes;
        for (; seen < chat.Count; ++seen)
        {
            var type = seen < types.Count ? types[seen] : 0;
            // The lookup of PlayerManager_message 0040dbfc ran when the message was made, in the packet read (ClientPlayerManager.Message).
            var display = seen < g.World.Players.ChatDisplayed.Count && g.World.Players.ChatDisplayed[seen];
            // Type 4 is the one-line current command (FearGuiChatDisplay slot 0x100), not a ring line (004a525c).
            if (display && type == 4)
                Host.CommandText = chat[seen];
            else if (display && GuiHost.ChatLineShown(chat[seen]))
                Host.AddChat(chat[seen], (uint)type);
            // PlayerManager_message 0040dbfc also prints the line to the console as MSG: "<escaped text>".
            Interp.Print(1, "MSG: \"" + Builtins.ExportEscape(chat[seen]) + "\"");
        }
        if (Host.Chat.Count > 200)
            Host.Chat.RemoveRange(0, Host.Chat.Count - 200);
        // The command line shows while the local record holds a command (+0x40 >= 0), green once its status is 2 (004a633c).
        var me = g.World.Players.Get(g.World.PlayerId);
        Host.CommandActive = me != null && me.Commander >= 0;
        Host.CommandAccepted = me != null && me.CommandStatus == 2;
    }

    // ---- frame loop (client-app.md 2.2) ----

    /// <summary>Starts the client and runs the main loop until it quits.</summary>
    /// <returns>The process exit code.</returns>
    public int Run()
    {
        if (!Start(out var error))
        {
            Console.Error.WriteLine("tribes: " + error);
            return 1;
        }
        Win?.Show();
        var realStart = Stopwatch.StartNew();
        while (!QuitRequested)
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

    /// <summary>Runs one frame on the virtual clock, used when <c>--virtual-time</c> or <c>--headless</c> is on. The clock moves by the step first.</summary>
    public void Step()
    {
        if (Opt.VirtualStep != 0)
            _virtualMs += Opt.VirtualStep;
        Frame();
    }

    /// <summary>Runs one frame.</summary>
    public void Frame()
    {
        ++Frames;
        FramePerf.FrameStart();
        // F1: message pump.
        PumpEvents();
        // F2: clock.
        SimWorld.UpdateClock();
        var now = NowMs;
        // F3: the server world of a hosted game runs before the client world (FearApp_onIdle 004ebc34 calls
        // FearWorld_advance 004eba40 first), then the client focus is set for the rest of the frame (004eb7ac).
        Hosted.Frame(now);
        Interp.Focus(null);
        // test hooks: the timed script runs where the input poll runs (F6).
        _script?.Run(now);
        if (_script != null)
        {
            foreach (var t in Canvas.Trace)
                Interp.Print(1, "  [canvas] " + t);
            Canvas.Trace.Clear();
        }
        // F5/F6 follow: the canvas content decides which maps are pushed.
        Play.UpdateContent(Canvas.Content?.Id ?? 0);
        Delegates.Poll();
        Delegates.Tick();
        _ircHistory.Keep(Interp);
        // F8-F14: moves, receive, send, sim advance, prediction.
        using (FramePerf.Scope("net-sim"))
        {
            Game?.Frame(now, Play.Moves);
            SimWorld.AdvanceServer();
        }
        // F15: sound.
        var dt = Math.Max(0, (now - _lastFrameMs) / 1000.0);
        _lastFrameMs = now;
        if (Sound != null)
        {
            Game?.UpdateSound();
            Sound.Tick(now);
        }
        // F16: render.
        RenderFrame();
        // F17: end of frame.
        SimWorld.EndFrame();
        if (QuitRequested)
            return;
        if (Win != null && !Canvas.WindowActive && Opt.VirtualStep == 0)
        {
            using (FramePerf.Scope("idle-sleep"))
                Thread.Sleep(10);
        }
    }

    private readonly IrcHistory _ircHistory = new();

    internal bool PlayViewActive => Canvas.Content?.Id is PlayScreenHandler.PlayGuiId;

    /// <summary>Renders the 3D view and the GUI.</summary>
    public void RenderFrame()
    {
        if (Dev == null || Win == null)
            return;
        ApplyFullscreenModeChange();
        (var w, var h) = Win.PixelSize();
        if (w > 0 && h > 0)
        {
            Canvas.Width = w;
            Canvas.Height = h;
        }
        if (!_paletteSet)
        {
            var shell = Host.TagText(180001) is string t ? GuiHost.FirstItem(t) : "feargui.pal";
            var pal = GfxAssets.LoadPalette(Res, shell) ?? GfxAssets.LoadPalette(Res, "feargui.pal");
            if (pal != null)
                SetPalette(pal);
        }
        UpdateRelativeMouse();
        using (FramePerf.Scope("gui-frame"))
            Canvas.Frame();
        var drewWorld = false;
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
        Canvas.ViewHasCamera = drewWorld || Game == null || !PlayViewActive;
        if (!drewWorld)
        {
            Dev.BeginFrame();
            Dev.ClearRgb(0, 0, 0);
            Canvas.Render(Dev);
            Dev.EndFrame();
        }
        TakeShots();
    }

    // The damage flash [code 004077a8, 004a1bbc, 005d2c20]: the camera query of the PSC (vtable +0x24, message 0x400) answers the
    // colour (1, 0, 0) and the alpha: the flash value of the last server block, kept at 0 when it is below 0 and capped at the float
    // 0x3f428f5c (0.76), or 0 and no colour when it is 0. The FGCommandView hands both to the surface (vtable +0x3c) every frame.
    // The OpenGL surface draws them at flip over the whole frame, HUD included, as a screen sized quad with the blend
    // (src alpha, 1 - src alpha): out = frame * (1 - f) + red * f. The base surface class and the class at vtable 0x0064c000 leave the slot empty.
    private const float DamageFlashCap = 0.76f;

    private void DrawDamageFlash()
    {
        var f = Math.Min((float)(Game?.World.Psc.DamageFlash ?? 0), DamageFlashCap);
        if (Dev != null && f > 0f)
            Dev.FillRectRgb(new GfxRect(0, 0, Dev.Width - 1, Dev.Height - 1), 255, 0, 0, f);
    }

    private void TakeShots()
    {
        var sec = NowMs / 1000.0;
        while (_nextAutoShot < Opt.ScreenshotAt.Count && Opt.ScreenshotAt[_nextAutoShot] <= sec)
        {
            var t = Opt.ScreenshotAt[_nextAutoShot++];
            _pendingShots.Add(Path.Combine(Opt.ShotDir, "shot_" + t.ToString("0.###", CultureInfo.InvariantCulture) + ".png"));
        }
        if (_pendingShots.Count == 0 || Dev == null)
            return;
        var px = Dev.ReadPixels();
        foreach (var path in _pendingShots)
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (full.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                WriteBmp(full, px, Dev.Width, Dev.Height);
            else
                MissionRender.WritePng(full, px, Dev.Width, Dev.Height);
            Log("screenshot: " + full);
        }
        _pendingShots.Clear();
    }

    // temp\sshotNNNN.bmp: 24-bit Windows bitmap, bottom-up rows.
    private static void WriteBmp(string path, byte[] rgba, int w, int h)
    {
        var row = ((w * 3) + 3) & ~3;
        using var f = File.Create(path);
        using var bw = new BinaryWriter(f);
        bw.Write((ushort)0x4d42);
        bw.Write(54 + (row * h));
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
        for (var y = h - 1; y >= 0; --y)
        {
            for (var x = 0; x < w; ++x)
            {
                var s = ((y * w) + x) * 4;
                line[x * 3] = rgba[s + 2];
                line[(x * 3) + 1] = rgba[s + 1];
                line[(x * 3) + 2] = rgba[s];
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
            // Other devices (the joystick) go to the canvas as their own device: it consumes them only with capture-all (gui.md 2.7).
            Device = ev.DeviceType == 3 ? RawInput.DevKeyboard : ev.DeviceType == 2 ? RawInput.DevMouse : ev.DeviceType,
            ObjType = ev.ObjType,
            Index = ev.ObjInst,
            Action = (byte)(ev.Action == 1 ? 1 : 0),
            Ascii = ch,
            Mod = ev.Modifiers,
            Value = value,
        };
        return Canvas.HandleInput(raw);
    }

    /// <summary>Sends a key event to the input dispatcher.</summary>
    /// <param name="scan">The scan code.</param>
    /// <param name="down">True for a press, false for a release.</param>
    public void InjectKey(int scan, bool down)
    {
        if (scan <= 0 || !Handlers.DeviceActive("keyboard0"))
            return;
        Input.Process(new InputEvent { DeviceType = 3, DeviceInst = 0, ObjType = 10, ObjInst = (byte)scan, Action = (byte)(down ? 1 : 2) }, down ? 1f : 0f);
    }

    /// <summary>Sends a mouse button event to the input dispatcher.</summary>
    /// <param name="index">The button index.</param>
    /// <param name="down">True for a press, false for a release.</param>
    public void InjectButton(int index, bool down)
    {
        if (!Handlers.DeviceActive("mouse0"))
            return;
        Input.Process(new InputEvent { DeviceType = 2, DeviceInst = 0, ObjType = 2, ObjInst = (byte)index, Action = (byte)(down ? 1 : 2) }, down ? 1f : 0f);
    }

    /// <summary>Sends a mouse axis event to the input dispatcher.</summary>
    /// <param name="axis">The axis, 0 for x and 1 for y.</param>
    /// <param name="counts">The relative movement in counts.</param>
    public void InjectAxis(int axis, float counts)
    {
        if (counts == 0 || !Handlers.DeviceActive("mouse0"))
            return;
        Input.Process(new InputEvent { DeviceType = 2, DeviceInst = 0, ObjType = (byte)(11 + axis), ObjInst = 0, Action = 3 }, counts);
    }

    /// <summary>Sends a joystick button event (client-app.md 3.2, 3.3 and 3.6; JoystickEvents). An inactive device makes no events (0050f550 reads only active devices).</summary>
    /// <param name="dev">The joystick index.</param>
    /// <param name="index">The button index.</param>
    /// <param name="down">True for a press, false for a release.</param>
    public void InjectJoyButton(int dev, int index, bool down)
    {
        if (!Handlers.DeviceActive("joystick" + dev))
            return;
        Input.Process(JoystickEvents.Button(dev, index, down), down ? 1f : 0f);
    }

    /// <summary>Sends a joystick axis event.</summary>
    /// <param name="dev">The joystick index.</param>
    /// <param name="sdlAxis">The SDL axis.</param>
    /// <param name="raw">The raw axis value.</param>
    public void InjectJoyAxis(int dev, int sdlAxis, int raw)
    {
        if (!Handlers.DeviceActive("joystick" + dev))
            return;
        (var type, var inst) = JoystickEvents.AxisObject(sdlAxis);
        Input.Process(new InputEvent { DeviceType = JoystickEvents.DeviceType, DeviceInst = (byte)dev, ObjType = type, ObjInst = inst, Action = 3 },
            JoystickEvents.AxisValue(raw));
    }

    /// <summary>Sends a joystick hat event. A buffered device passes the new angle as the old one, so only the two move events are made.</summary>
    /// <param name="dev">The joystick index.</param>
    /// <param name="hat">The hat index.</param>
    /// <param name="sdlHat">The SDL hat position.</param>
    public void InjectJoyHat(int dev, int hat, int sdlHat)
    {
        if (!Handlers.DeviceActive("joystick" + dev))
            return;
        var angle = JoystickEvents.HatAngle(sdlHat);
        foreach (var (type, action, value) in JoystickEvents.PovEvents(angle, angle))
            Input.Process(new InputEvent { DeviceType = JoystickEvents.DeviceType, DeviceInst = (byte)dev, ObjType = type, ObjInst = (byte)hat, Action = action }, value);
    }

    private void PumpEvents()
    {
        if (Win == null)
            return;
        while (Sdl3.PollEvent(out var ev))
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
                    var b = ev.Button switch { 1 => 0, 3 => 1, 2 => 2, _ => -1 };
                    if (b >= 0)
                        InjectButton(b, ev.Type == Sdl3.EventMouseButtonDown);
                    break;
                }
                case Sdl3.EventMouseWheel:
                    InjectAxis(2, ev.WheelY * 120f);
                    break;
                case Sdl3.EventJoyAxisMotion:
                case Sdl3.EventJoyHatMotion:
                case Sdl3.EventJoyButtonDown:
                case Sdl3.EventJoyButtonUp:
                {
                    var dev = Joysticks?.InstanceOf(ev.JoyWhich) ?? -1;
                    if (dev < 0)
                        break;
                    if (ev.Type == Sdl3.EventJoyAxisMotion)
                        InjectJoyAxis(dev, ev.JoyIndex, ev.JoyAxisValue);
                    else if (ev.Type == Sdl3.EventJoyHatMotion)
                        InjectJoyHat(dev, ev.JoyIndex, ev.JoyHat);
                    else
                        InjectJoyButton(dev, ev.JoyIndex, ev.Type == Sdl3.EventJoyButtonDown);
                    break;
                }
                case Sdl3.EventWindowFocusLost:
                    Canvas.WindowActive = false;
                    Input.Clear();
                    break;
                case Sdl3.EventWindowFocusGained:
                    Canvas.WindowActive = true;
                    break;
                default:
                    break;
            }
        }
    }

    // The close box (client-app.md 6.1): the canvas quit flag is true in the stock script, so the window ends the process.
    private void OnCloseBox()
    {
        if (Interp.HasFunction("onQuit"))
            Interp.CallFunction("onQuit", []);
        else
            QuitRequested = true;
    }

    // ---- shutdown (client-app.md 6.2) ----

    /// <summary>Shuts the client down (client-app.md 6.2).</summary>
    public void Shutdown()
    {
        try
        {
            Interp.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\quit.gui\");");
            if (Interp.HasFunction("onExit"))
                Interp.CallFunction("onExit", []);
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

    /// <inheritdoc/>
    public void Dispose()
    {
        Game?.Dispose();
        Hosted?.Dispose();
        SkinViews?.Dispose();
        Delegates.Browser.Dispose();
        AudioDev?.Dispose();
        Dev?.Dispose();
        Joysticks?.Dispose();
        Win?.Dispose();
        _logFile?.Dispose();
    }
}
