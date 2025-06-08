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
        "usage: tribes --root DIR [--show RESOURCE] [--palette PPL|PAL] [--font PFT [--text STRING]]\n" +
        "              [--screenshot OUT.ppm [--frames N]] [--size WxH] [--video-driver NAME]\n" +
        "  --root DIR          game directory; every .vol in DIR/base is mounted\n" +
        "  --show NAME         draw that bitmap (.bmp) centred until the window is closed\n" +
        "  --palette NAME      palette for the bitmap: a .ppl set or a .pal (default feargui.pal, else a gray ramp)\n" +
        "  --font NAME         draw text with that .pft font at the top left\n" +
        "  --text STRING       the text for --font\n" +
        "  --screenshot FILE   render --frames frames (default 3) in a hidden window, write FILE as PPM, exit\n" +
        "  --size WxH          window size (default 640x480)\n" +
        "  --video-driver NAME SDL video driver, for example offscreen\n";

    private sealed class Options
    {
        public string Root = "";
        public string? Show, Palette, Font, Screenshot, VideoDriver;
        public string Text = "The quick brown fox jumps over the lazy dog 0123456789";
        public int Frames = 3;
        public int Width = 640, Height = 480;
    }

    public static int Main(string[] args)
    {
        Options? o = Parse(args);
        if (o == null)
        {
            Console.Error.Write(Usage);
            return 2;
        }

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

    private static Options? Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; ++i)
        {
            string a = args[i];
            if (a is "-h" or "--help")
                return null;
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"tribes: {a} needs a value");
                return null;
            }
            string v = args[++i];
            switch (a)
            {
                case "--root": o.Root = v; break;
                case "--show": o.Show = v; break;
                case "--palette": o.Palette = v; break;
                case "--font": o.Font = v; break;
                case "--text": o.Text = v; break;
                case "--screenshot": o.Screenshot = v; break;
                case "--video-driver": o.VideoDriver = v; break;
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
