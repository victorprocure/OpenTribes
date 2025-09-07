using System.Diagnostics;
using System.Globalization;
using OpenTribes.Core;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Gl;
using OpenTribes.Platform.Sdl;

namespace OpenTribes.Client;

// The `tribes` executable (ROADMAP M9). C2: window, GL context, event loop, `--show` and `--screenshot`.
public static class Program
{
    private const string Usage =
        "usage: tribes [--root DIR] [--windowed] [--input SCRIPT] [--log FILE] [--screenshot-at T,...] [--shots DIR] [--size WxH]\n" +
        "              [--video-driver NAME] [--no-audio] [--no-predict] [--allow-remote] [--allow-browser] [--seconds S] [GAME ARGS...]\n" +
        "       the client: runs console.cs of DIR; GAME ARGS (-mod x, +connect ADDR, +exec FILE, -host MISSION) go to $cargv\n" +
        "usage: tribes --root DIR [--show RESOURCE] [--palette PPL|PAL] [--font PFT [--text STRING]]\n" +
        "              [--screenshot OUT.ppm [--frames N]] [--size WxH] [--video-driver NAME]\n" +
        "       tribes --root DIR --demo FILE.rec --at SEC,SEC,... [--camera follow|X,Y,Z,YAW,PITCH|ghost:N] [--fov DEG] --screenshot OUT\n" +
        "       tribes --root DIR --gui NAME.gui [--exec SCRIPT.cs] [--input EVENTS] [--screenshot OUT.png] [--size WxH]\n" +
        "       tribes --root DIR --demo FILE.rec --at SEC --hud --screenshot OUT.png\n" +
        "       tribes --root DIR --gui-census | --gui-list PATTERN\n" +
        "       tribes --root DIR --shape-census | --shape-info NAME\n" +
        "       tribes --root DIR --mission NAME (--camera X,Y,Z,YAW,PITCH | --cameras FILE) [--fov DEG] --screenshot OUT\n" +
        "  --root DIR          game directory; every .vol in DIR/base is mounted\n" +
        "  --show NAME         draw that bitmap (.bmp) centred until the window is closed\n" +
        "  --palette NAME      palette for the bitmap: a .ppl set or a .pal (default feargui.pal, else a gray ramp)\n" +
        "  --font NAME         draw text with that .pft font at the top left\n" +
        "  --text STRING       the text for --font\n" +
        "  --screenshot FILE   render --frames frames (default 3) in a hidden window, write FILE as PPM, exit\n" +
        "  --size WxH          window size (default 640x480)\n" +
        "  --mission NAME      load the mission locally (no server) and render the world; needs --camera or --cameras\n" +
        "  --camera X,Y,Z,YAW,PITCH  camera position (world units) and angles in degrees (yaw 0 looks along +Y, positive turns left)\n" +
        "  --cameras FILE      one camera per line: NAME X,Y,Z,YAW,PITCH[,FOV]; --screenshot is then a directory, NAME.png each;\n                      names that start with MISSION_ select that mission's cameras when any do\n" +
        "  --fov DEG           horizontal field of view in degrees (default 90)\n" +
        "  --video-driver NAME SDL video driver, for example offscreen\n" +
        "       tribes --root DIR --demo FILE.rec --at T --audio-out OUT.wav --seconds S   (offline mix of a demo span, no device)\n" +
        "       tribes --root DIR --sound-census | --audio-live WAVE [--seconds S] | --cat RESOURCE\n";

    private sealed class Options
    {
        public string Root = "";
        public string? Gui, Exec, Input, Show, Palette, Font, Screenshot, VideoDriver, Mission, Camera, Cameras, Demo, At, Shape, Seq;
        public double Pos;
        public string Skin = "";
        public string? AudioOut, Cat, AudioLive;
        public double Seconds = 20;
        public double Fov = World.WorldScene.DefaultFov;
        public string Text = "The quick brown fox jumps over the lazy dog 0123456789";
        public int Frames = 3;
        public int Width = 640, Height = 480;
    }

    public static int Main(string[] args)
    {
        if (!App.ClientOptions.IsToolInvocation(args) && !args.Contains("-h") && !args.Contains("--help"))
            return RunClient(args);
        Options? o = Parse(args);
        if (o == null)
        {
            Console.Error.Write(Usage);
            return 2;
        }

        int si = Array.IndexOf(args, "--shape-info");
        if (si >= 0 && si + 1 < args.Length)
            return Shapes.ShapeInfo.Report(o.Root, args[si + 1]);
        if (args.Contains("--shape-census"))
            return Shapes.ShapeCensus.Report(o.Root);

        if (o.Cat != null)
            return Audio.AudioTools.Cat(o.Root, o.Cat);
        if (args.Contains("--sound-census"))
            return Audio.AudioTools.Census(o.Root);
        if (o.AudioLive != null)
            return Audio.AudioTools.Live(o.Root, o.AudioLive, o.Seconds);
        if (o.Demo != null && o.AudioOut != null)
            return Audio.AudioTools.RenderDemo(o.Root, o.Demo, o.At ?? "0", o.Seconds, o.AudioOut);

        int gl = Array.IndexOf(args, "--gui-list");
        if (gl >= 0 && gl + 1 < args.Length)
            return Gui.GuiCensus.List(o.Root, args[gl + 1]);
        int gt = Array.IndexOf(args, "--gui-tree");
        if (gt >= 0 && gt + 1 < args.Length)
            return Gui.GuiCensus.Tree(o.Root, args[gt + 1]);
        if (args.Contains("--gui-census"))
            return Gui.GuiCensus.Run(o.Root);

        if (o.Shape != null)
            return Shapes.ShapeView.Run(o.Root, o.Shape, o.Palette, o.Seq, o.Pos, o.Camera, o.Screenshot, o.Width, o.Height, o.VideoDriver, o.Skin);

        if (o.Gui != null)
            return Gui.GuiRun.Run(o.Root, o.Gui, o.Exec, o.Input, o.Screenshot, o.Width, o.Height, o.VideoDriver);

        if (o.Demo != null)
            return Scene.DemoRender.Run(o.Root, o.Demo, o.At ?? "0", o.Camera, o.Fov, o.Screenshot, o.Width, o.Height, o.VideoDriver, null, args.Contains("--hud"));

        if (o.Mission != null)
            return World.MissionRender.Run(o.Root, o.Mission, o.Camera, o.Cameras, o.Fov, o.Screenshot, o.Width, o.Height, o.VideoDriver);

        ResourceManager res = GfxAssets.Mount(o.Root, out int volumes);
        Console.Error.WriteLine($"mounted {volumes} volumes from {o.Root}");

        PaletteBank? palette = null;
        string paletteName = "";
        foreach (string name in o.Palette != null ? new[] { o.Palette } : new[] { "feargui.pal", "darkpal.pal" })
        {
            palette = GfxAssets.LoadPalette(res, name);
            if (palette != null)
            {
                paletteName = name;
                break;
            }
            if (o.Palette != null)
            {
                Console.Error.WriteLine($"tribes: palette not found or unreadable: {name}");
                return 1;
            }
        }

        PbmpBitmap? bitmap = null;
        if (o.Show != null)
        {
            bitmap = GfxAssets.LoadBitmap(res, o.Show, includePalette: true);
            if (bitmap == null)
            {
                Console.Error.WriteLine($"tribes: bitmap not found or unreadable: {o.Show}");
                return 1;
            }
            if (palette == null && bitmap.Palette != null)
                palette = bitmap.Palette;
        }
        PftFont? font = null;
        if (o.Font != null)
        {
            font = GfxAssets.LoadFont(res, o.Font);
            if (font == null)
            {
                Console.Error.WriteLine($"tribes: font not found or unreadable: {o.Font}");
                return 1;
            }
        }
        if (palette == null)
            palette = PaletteTables.Grayscale();
        else
            Console.Error.WriteLine($"palette {paletteName.Length switch { 0 => "(embedded)", _ => paletteName }}");

        bool shot = o.Screenshot != null;
        using GlWindow? win = GlWindow.TryCreate("Tribes", o.Width, o.Height, shot, o.VideoDriver, out string error);
        if (win == null)
        {
            Console.Error.WriteLine("tribes: cannot open a window with an OpenGL 3.3 core context: " + error);
            return 1;
        }
        Console.Error.WriteLine($"video {win.VideoDriver}, OpenGL {win.GlVersion}");
        using var glDevice = new GlGfxDevice(win);
        IGfxDevice dev = glDevice;
        dev.SetPalette(palette);
        win.SetSwapInterval(shot ? 0 : 1);

        void Frame()
        {
            dev.BeginFrame();
            dev.ClearRgb(32, 32, 40);
            if (bitmap != null)
                dev.Blit(bitmap, (dev.Width - bitmap.Width) / 2, (dev.Height - bitmap.Height) / 2);
            if (font != null)
                dev.DrawText(font, 8, 8, o.Text);
            dev.EndFrame();
        }

        if (shot)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < o.Frames; ++i)
                Frame();
            byte[] px = dev.ReadPixels();
            WritePpm(o.Screenshot!, px, dev.Width, dev.Height);
            Console.Error.WriteLine($"{o.Frames} frames in {sw.ElapsedMilliseconds} ms, wrote {o.Screenshot} ({dev.Width}x{dev.Height})");
            return 0;
        }

        win.Show();
        bool running = true;
        while (running)
        {
            while (Sdl3.PollEvent(out SdlEvent ev))
            {
                if (ev.Type == Sdl3.EventQuit || ev.Type == Sdl3.EventWindowCloseRequested)
                    running = false;
                else if (ev.Type == Sdl3.EventKeyDown && ev.Key == Sdl3.KeyEscape)
                    running = false;
            }
            Frame();
        }
        return 0;
    }

    // The real client process (ROADMAP M9 C8): console.cs, the stock scripts, the frame loop.
    private static int RunClient(string[] args)
    {
        App.ClientOptions? opt = App.ClientOptions.Parse(args, out string error);
        if (opt == null)
        {
            Console.Error.WriteLine("tribes: " + error);
            Console.Error.Write(Usage);
            return 2;
        }
        string? root = Platform.PlatformPaths.EnsureWritableRoot(opt.Root,
            Platform.PlatformPaths.UserDataDir(Platform.PlatformPaths.CurrentOs, Platform.PlatformPaths.Env,
                                               Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), out string rootError);
        if (root == null)
        {
            Console.Error.WriteLine("tribes: " + rootError);
            return 1;
        }
        if (root != opt.Root)
            Console.Error.WriteLine($"tribes: {opt.Root} is read-only, using {root} for config, temp and recordings (D54)");
        opt.Root = root;
        using var app = new App.ClientApp(opt);
        try
        {
            return app.Run();
        }
        finally
        {
            Console.Error.Flush();
        }
    }

    private static Options? Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; ++i)
        {
            string a = args[i];
            if (a is "-h" or "--help")
                return null;
            if (a is "--shape-census" or "--gui-census" or "--hud" or "--sound-census")
                continue;
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"tribes: {a} needs a value");
                return null;
            }
            string v = args[++i];
            switch (a)
            {
                case "--root": o.Root = v; break;
                case "--cat": o.Cat = v; break;
                case "--audio-out": o.AudioOut = v; break;
                case "--audio-live": o.AudioLive = v; break;
                case "--seconds": o.Seconds = double.Parse(v, CultureInfo.InvariantCulture); break;
                case "--shape-info": case "--gui-list": case "--gui-tree": break;
                case "--show": o.Show = v; break;
                case "--gui": o.Gui = v; break;
                case "--exec": o.Exec = v; break;
                case "--input": o.Input = v; break;
                case "--palette": o.Palette = v; break;
                case "--font": o.Font = v; break;
                case "--text": o.Text = v; break;
                case "--screenshot": o.Screenshot = v; break;
                case "--video-driver": o.VideoDriver = v; break;
                case "--mission": o.Mission = v; break;
                case "--demo": o.Demo = v; break;
                case "--shape": o.Shape = v; break;
                case "--seq": o.Seq = v; break;
                case "--skin": o.Skin = v; break;
                case "--pos": o.Pos = double.Parse(v, CultureInfo.InvariantCulture); break;
                case "--at": o.At = v; break;
                case "--camera": o.Camera = v; break;
                case "--cameras": o.Cameras = v; break;
                case "--fov":
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out o.Fov) || o.Fov < 5 || o.Fov > 170)
                        return null;
                    break;
                case "--frames":
                    if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out o.Frames) || o.Frames < 1)
                        return null;
                    break;
                case "--size":
                    string[] wh = v.Split('x');
                    if (wh.Length != 2 || !int.TryParse(wh[0], out o.Width) || !int.TryParse(wh[1], out o.Height) ||
                        o.Width < 1 || o.Height < 1)
                        return null;
                    break;
                default:
                    Console.Error.WriteLine($"tribes: unknown option {a}");
                    return null;
            }
        }
        if (o.Root.Length == 0)
        {
            Console.Error.WriteLine("tribes: --root is required");
            return null;
        }
        return o;
    }

    // Binary PPM (P6) from RGBA rows.
    internal static void WritePpm(string path, byte[] rgba, int w, int h)
    {
        using var f = File.Create(path);
        byte[] header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        f.Write(header);
        var row = new byte[w * 3];
        for (int y = 0; y < h; ++y)
        {
            for (int x = 0; x < w; ++x)
            {
                row[x * 3] = rgba[(y * w + x) * 4];
                row[x * 3 + 1] = rgba[(y * w + x) * 4 + 1];
                row[x * 3 + 2] = rgba[(y * w + x) * 4 + 2];
            }
            f.Write(row);
        }
    }
}
