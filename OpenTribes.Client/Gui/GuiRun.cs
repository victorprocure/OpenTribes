// `tribes --root DIR --gui NAME [--exec SCRIPT.cs] [--input EVENTS] --screenshot OUT.png`: loads a GUI over the canvas,
// optionally runs a script first, feeds synthetic input events, and renders the result (ROADMAP M9 C6).
//
// Input file, one command per line (`#` starts a comment):
//   move X Y | click X Y | rclick X Y | dblclick X Y | down | up | key NAME [shift|ctrl|alt]... | keydown NAME | keyup NAME
//   type TEXT | wait MS | frame | eval CONSOLE TEXT | echo TEXT | shot FILE.png | focus
using System.Globalization;
using System.Text;
using OpenTribes.Client.World;
using OpenTribes.Core;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Gl;
using OpenTribes.Script;

namespace OpenTribes.Client.Gui;

public sealed class GuiSession
{
    public Interpreter Interp = null!;
    public ResourceManager Res = null!;
    public GuiHost Host = null!;
    public GuiCanvas Canvas = null!;
    public GuiRuntime Runtime = null!;
    public long Clock;
    public IGfxDevice? Dev;
    public Action<string> Log = Console.WriteLine;

    public static GuiSession Create(string root, int width, int height)
    {
        var s = new GuiSession();
        s.Res = GfxAssets.Mount(root, out _);
        s.Interp = new Interpreter();
        s.Interp.SetResources(s.Res);
        Builtins.RegisterBuiltins(s.Interp, s.Res, root);
        s.Interp.SetOutput(line => s.Log(line));
        s.Host = new GuiHost(s.Interp, s.Res) { Root = root, Now = () => s.Clock, Log = l => s.Log(l) };
        s.Canvas = new GuiCanvas(s.Host, width, height);
        s.Runtime = new GuiRuntime(s.Interp, s.Host, s.Canvas);
        s.Runtime.LoadTagDictionary();
        s.Interp.Evaluate("setCursor(MainWindow, \"CUR_Arrow.bmp\");");
        return s;
    }

    public void Advance(int ms)
    {
        for (int left = ms; left > 0; left -= 16)
        {
            Clock += Math.Min(16, left);
            Canvas.Frame();
        }
    }

    public void Exec(string path)
    {
        string text = ByteString.FromBytes(File.ReadAllBytes(path));
        Interp.Evaluate(text, Path.GetFileName(path));
    }

    public void Render()
    {
        Dev!.BeginFrame();
        Dev.ClearRgb(0, 0, 0);
        Canvas.Render(Dev);
        Dev.EndFrame();
    }

    private void Mouse(byte objType, byte index, byte action, float value = 0) =>
        Canvas.HandleInput(new RawInput { Device = RawInput.DevMouse, ObjType = objType, Index = index, Action = action, Value = value });

    public void MoveTo(int x, int y)
    {
        int dx = x - (int)Canvas.CursorX, dy = y - (int)Canvas.CursorY;
        if (dx != 0) Mouse(RawInput.ObjXAxis, 0, 0, dx);
        if (dy != 0) Mouse(RawInput.ObjYAxis, 0, 0, dy);
    }

    public void Button(int index, bool down) => Mouse(RawInput.ObjButton, (byte)index, (byte)(down ? 1 : 0));

    private byte mMods;

    public void Key(string name, bool make, bool brk)
    {
        int scan = GuiCanvas.ScanForName(name);
        if (scan == 0)
        {
            Log($"input: unknown key {name}");
            return;
        }
        char ascii = name.Length == 1 ? name[0] : name.ToLowerInvariant() switch { "space" => ' ', _ => '\0' };
        if ((mMods & 3) != 0 && char.IsLetter(ascii))
            ascii = char.ToUpperInvariant(ascii);
        var ev = new RawInput { Device = RawInput.DevKeyboard, Index = (byte)scan, Ascii = ascii, Mod = mMods };
        if (make) { ev.Action = 1; Canvas.HandleInput(ev); }
        if (brk) { ev.Action = 0; Canvas.HandleInput(ev); }
    }

    public void RunInput(string path)
    {
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] w = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string rest = w.Length > 1 ? w[1] : "";
            string[] a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Log("> " + line);
            switch (w[0])
            {
                case "move": MoveTo(int.Parse(a[0]), int.Parse(a[1])); break;
                case "click":
                    MoveTo(int.Parse(a[0]), int.Parse(a[1]));
                    Button(0, true); Advance(32); Button(0, false); Advance(16);
                    break;
                case "dblclick":
                    MoveTo(int.Parse(a[0]), int.Parse(a[1]));
                    for (int i = 0; i < 2; ++i) { Button(0, true); Advance(16); Button(0, false); Advance(16); }
                    break;
                case "rclick": MoveTo(int.Parse(a[0]), int.Parse(a[1])); Button(1, true); Button(1, false); break;
                case "down": Button(0, true); break;
                case "up": Button(0, false); break;
                case "key":
                    mMods = 0;
                    foreach (string m in a.Skip(1))
                        mMods |= m switch { "shift" => (byte)3, "ctrl" => (byte)0x0c, "alt" => (byte)0x30, _ => (byte)0 };
                    Key(a[0], true, true);
                    mMods = 0;
                    Advance(16);
                    break;
                case "keydown": Key(a[0], true, false); break;
                case "keyup": Key(a[0], false, true); break;
                case "type":
                    foreach (char ch in rest)
                    {
                        Key(ch == ' ' ? "space" : ch.ToString(), true, true);
                        Advance(16);
                    }
                    break;
                case "wait": Advance(int.Parse(a[0])); break;
                case "frame": Advance(16); break;
                case "eval": Log("= " + Interp.Evaluate(rest)); break;
                case "echo": Log(rest); break;
                case "shot": Render(); MissionRender.WritePng(rest, Dev!.ReadPixels(), Dev.Width, Dev.Height); Log("wrote " + rest); break;
                case "focus": Log("focus: " + (Canvas.FirstResponder as GuiControl)?.ToString() ?? "root"); break;
                default: Log("input: unknown command " + w[0]); break;
            }
        }
    }
}

public static class GuiRun
{
    public static int Run(string root, string gui, string? exec, string? input, string? output, int width, int height, string? videoDriver)
    {
        GuiSession s = GuiSession.Create(root, width, height);
        using GlWindow? win = GlWindow.TryCreate("Tribes", width, height, true, videoDriver, out string error);
        if (win == null)
        {
            Console.Error.WriteLine("tribes: cannot open a window with an OpenGL 3.3 core context: " + error);
            return 1;
        }
        using var dev = new GlGfxDevice(win);
        win.SetSwapInterval(0);
        s.Dev = dev;
        // The shell palette (tag 180001) is the one every shell screen runs with; the main menu selects it with a PaletteCtrl.
        string shell = s.Host.TagText(180001) is string t ? GuiHost.FirstItem(t) : "feargui.pal";
        PaletteBank? pal = GfxAssets.LoadPalette(s.Res, shell) ?? GfxAssets.LoadPalette(s.Res, "feargui.pal");
        dev.SetPalette(pal ?? PaletteTables.Grayscale());
        s.Host.OnPalette = name =>
        {
            PaletteBank? p = GfxAssets.LoadPalette(s.Res, name);
            if (p != null)
                dev.SetPalette(p);
        };
        if (exec != null)
            s.Exec(exec);
        string file = gui.Contains('.') ? gui : gui + ".gui";
        if (!s.Canvas.LoadContent(file.Contains('\\') || file.Contains('/') ? file : "gui\\" + file))
        {
            Console.Error.WriteLine("tribes: cannot load " + gui);
            return 1;
        }
        s.Advance(32);
        if (input != null)
            s.RunInput(input);
        s.Advance(16);
        if (output != null)
        {
            s.Render();
            s.Render();
            MissionRender.WritePng(output, dev.ReadPixels(), dev.Width, dev.Height);
            Console.Error.WriteLine($"wrote {output} ({dev.Width}x{dev.Height})");
        }
        return 0;
    }
}
