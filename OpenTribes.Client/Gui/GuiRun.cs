namespace OpenTribes.Client.Gui;

using OpenTribes.Client.World;
using OpenTribes.Core;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Gl;
using OpenTribes.Script;

/// <summary>A GUI over a canvas without the game: the interpreter, the resources, the canvas, a clock and synthetic input.</summary>
public sealed class GuiSession
{
    /// <summary>The script interpreter.</summary>
    public Interpreter Interp = null!;
    /// <summary>The mounted resources.</summary>
    public ResourceManager Res = null!;
    /// <summary>The GUI host.</summary>
    public GuiHost Host = null!;
    /// <summary>The canvas.</summary>
    public GuiCanvas Canvas = null!;
    /// <summary>The GUI runtime.</summary>
    public GuiRuntime Runtime = null!;
    /// <summary>The clock in milliseconds.</summary>
    public long Clock;
    /// <summary>The graphics device, or null before rendering.</summary>
    public IGfxDevice? Dev;
    /// <summary>Receives the output lines.</summary>
    public Action<string> Log = Console.WriteLine;

    /// <summary>Creates a session with the built in commands and the tag dictionary.</summary>
    /// <param name="root">The game root directory.</param>
    /// <param name="width">The canvas width in pixels.</param>
    /// <param name="height">The canvas height in pixels.</param>
    /// <returns>The session.</returns>
    public static GuiSession Create(string root, int width, int height)
    {
        var s = new GuiSession
        {
            Res = GfxAssets.Mount(root, out _),
            Interp = new Interpreter()
        };
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

    /// <summary>Runs canvas frames for a span of time, 16 ms each.</summary>
    /// <param name="ms">The span in milliseconds.</param>
    public void Advance(int ms)
    {
        for (var left = ms; left > 0; left -= 16)
        {
            Clock += Math.Min(16, left);
            Canvas.Frame();
        }
    }

    /// <summary>Evaluates a script file.</summary>
    /// <param name="path">The script path.</param>
    public void Exec(string path)
    {
        var text = ByteString.FromBytes(File.ReadAllBytes(path));
        Interp.Evaluate(text, Path.GetFileName(path));
    }

    /// <summary>Renders one frame of the canvas.</summary>
    public void Render()
    {
        Dev!.BeginFrame();
        Dev.ClearRgb(0, 0, 0);
        Canvas.Render(Dev);
        Dev.EndFrame();
    }

    private void Mouse(byte objType, byte index, byte action, float value = 0) =>
        Canvas.HandleInput(new RawInput { Device = RawInput.DevMouse, ObjType = objType, Index = index, Action = action, Value = value });

    /// <summary>Moves the mouse cursor.</summary>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    public void MoveTo(int x, int y)
    {
        int dx = x - (int)Canvas.CursorX, dy = y - (int)Canvas.CursorY;
        if (dx != 0)
            Mouse(RawInput.ObjXAxis, 0, 0, dx);
        if (dy != 0)
            Mouse(RawInput.ObjYAxis, 0, 0, dy);
    }

    /// <summary>Presses or releases a mouse button.</summary>
    /// <param name="index">The button index.</param>
    /// <param name="down">True to press.</param>
    public void Button(int index, bool down) => Mouse(RawInput.ObjButton, (byte)index, (byte)(down ? 1 : 0));

    private byte mMods;

    /// <summary>Sends a key event.</summary>
    /// <param name="name">The key name.</param>
    /// <param name="make">True to send the key down event.</param>
    /// <param name="brk">True to send the key up event.</param>
    public void Key(string name, bool make, bool brk)
    {
        var scan = GuiCanvas.ScanForName(name);
        var mods = mMods;
        if (scan == 0 && name.Length == 1 && "!@#$%^&*()_+".IndexOf(name[0]) is int si and >= 0)
        {
            scan = GuiCanvas.ScanForName("1234567890-="[si == 10 ? 10 : si == 11 ? 11 : si].ToString());
            mods |= 3;
        }
        if (scan == 0 && name.Length == 1 && ",/;'".IndexOf(name[0]) is int pi and >= 0)
            scan = new[] { 0x33, 0x35, 0x27, 0x28 }[pi];
        if (scan == 0)
        {
            Log($"input: unknown key {name}");
            return;
        }
        var ascii = name.Length == 1 ? name[0] : name.ToLowerInvariant() switch { "space" => ' ', _ => '\0' };
        if ((mMods & 3) != 0 && char.IsLetter(ascii))
            ascii = char.ToUpperInvariant(ascii);
        var ev = new RawInput { Device = RawInput.DevKeyboard, Index = (byte)scan, Ascii = ascii, Mod = mods };
        if (make)
        { ev.Action = 1; Canvas.HandleInput(ev); }
        if (brk)
        { ev.Action = 0; Canvas.HandleInput(ev); }
    }

    /// <summary>Runs an input file, one command per line.</summary>
    /// <param name="path">The input file path.</param>
    /// <remarks>
    /// <c>#</c> starts a comment. The commands are:
    /// <c>move X Y | click X Y | rclick X Y | dblclick X Y | down | up | key NAME [shift|ctrl|alt]... | keydown NAME | keyup NAME | clickctl NAME_OR_ID | where NAME_OR_ID | var $NAME | type TEXT | wait MS | frame | eval CONSOLE TEXT | echo TEXT | shot FILE.png | focus</c>
    /// </remarks>
    public void RunInput(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            var w = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var rest = w.Length > 1 ? w[1] : "";
            var a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Log("> " + line);
            switch (w[0])
            {
                case "move":
                    MoveTo(int.Parse(a[0]), int.Parse(a[1]));
                    break;
                case "click":
                    MoveTo(int.Parse(a[0]), int.Parse(a[1]));
                    Button(0, true);
                    Advance(32);
                    Button(0, false);
                    Advance(16);
                    break;
                case "dblclick":
                    MoveTo(int.Parse(a[0]), int.Parse(a[1]));
                    for (var i = 0; i < 2; ++i)
                    { Button(0, true); Advance(16); Button(0, false); Advance(16); }
                    break;
                case "rclick":
                    MoveTo(int.Parse(a[0]), int.Parse(a[1]));
                    Button(1, true);
                    Button(1, false);
                    break;
                case "down":
                    Button(0, true);
                    break;
                case "up":
                    Button(0, false);
                    break;
                case "key":
                    mMods = 0;
                    foreach (var m in a.Skip(1))
                        mMods |= m switch { "shift" => (byte)3, "ctrl" => (byte)0x0c, "alt" => (byte)0x30, _ => (byte)0 };
                    Key(a[0], true, true);
                    mMods = 0;
                    Advance(16);
                    break;
                case "keydown":
                    Key(a[0], true, false);
                    break;
                case "keyup":
                    Key(a[0], false, true);
                    break;
                case "type":
                    foreach (var ch in rest)
                    {
                        Key(ch == ' ' ? "space" : ch.ToString(), true, true);
                        Advance(16);
                    }
                    break;
                case "clickctl":
                case "where":
                {
                    GuiControl? c = Runtime.FindControl(a[0]);
                    if (c == null)
                    { Log("input: no control " + a[0]); break; }
                    var (gx, gy) = c.GlobalOrigin();
                    if (w[0] == "where")
                    { Log($"{c.ClassName} {c.Name} at {gx},{gy} {c.W}x{c.H}"); break; }
                    MoveTo(gx + (c.W / 2), gy + (c.H / 2));
                    Button(0, true);
                    Advance(32);
                    Button(0, false);
                    Advance(16);
                    break;
                }
                case "var":
                    Log($"{a[0]} = {Interp.PeekVariable(a[0])}");
                    break;
                case "wait":
                    Advance(int.Parse(a[0]));
                    break;
                case "frame":
                    Advance(16);
                    break;
                case "eval":
                    Log("= " + Interp.Evaluate(rest));
                    break;
                case "echo":
                    Log(rest);
                    break;
                case "shot":
                    Render();
                    MissionRender.WritePng(rest, Dev!.ReadPixels(), Dev.Width, Dev.Height);
                    Log("wrote " + rest);
                    break;
                case "focus":
                    Log("focus: " + ((Canvas.FirstResponder as GuiControl)?.ToString() ?? "root"));
                    break;
                default:
                    Log("input: unknown command " + w[0]);
                    break;
            }
            foreach (var t in Canvas.Trace)
                Log("  [canvas] " + t);
            Canvas.Trace.Clear();
        }
    }
}

/// <summary>The GUI screenshot tool: loads a GUI over the canvas, optionally runs a script first, feeds synthetic input events and renders the result.</summary>
/// <remarks>Started with <c>tribes --root DIR --gui NAME [--exec SCRIPT.cs] [--input EVENTS] --screenshot OUT.png</c>.</remarks>
public static class GuiRun
{
    /// <summary>Runs the session and writes the screenshot.</summary>
    /// <param name="root">The game root directory.</param>
    /// <param name="gui">The screen name.</param>
    /// <param name="exec">A script to run first, or null.</param>
    /// <param name="input">An input file, or null.</param>
    /// <param name="output">The PNG path.</param>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="videoDriver">The video driver name, or null for the default.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string root, string gui, string? exec, string? input, string? output, int width, int height, string? videoDriver)
    {
        GuiSession s = GuiSession.Create(root, width, height);
        using GlWindow? win = GlWindow.TryCreate("Tribes", width, height, true, videoDriver, out var error);
        if (win == null)
        {
            Console.Error.WriteLine("tribes: cannot open a window with an OpenGL 3.3 core context: " + error);
            return 1;
        }
        using var dev = new GlGfxDevice(win);
        GlWindow.SetSwapInterval(0);
        s.Dev = dev;
        // The shell palette (tag 180001) is the one every shell screen runs with; the main menu selects it with a PaletteCtrl.
        var shell = s.Host.TagText(180001) is string t ? GuiHost.FirstItem(t) : "feargui.pal";
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
        var file = gui.Contains('.') ? gui : gui + ".gui";
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
