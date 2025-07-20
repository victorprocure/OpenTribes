// The automation hook of the client (ROADMAP M9 C8): `tribes --input FILE` drives a whole session from a timed script.
//
// One command per line, `#` starts a comment. A line may start with `@SECONDS` (app time, the same clock as
// `--screenshot-at`); without it the line runs at the time of the line before it. `wait SECONDS` moves that time on.
//   move X Y | mouse DX DY | click [X Y] | rclick [X Y] | dblclick [X Y] | down | up | rdown | rup | wheel N
//   key NAME [shift|ctrl|alt]... | keydown NAME | keyup NAME | hold NAME SECONDS | type TEXT
//   eval CONSOLE TEXT | echo TEXT | var $NAME | where NAME_OR_ID | clickctl NAME_OR_ID | focus | shot NAME | quit | wait SECONDS
// `key`, `keydown` and `keyup` go through the same dispatcher as an SDL key event (modifiers, GUI first, then the maps);
// `mouse` is a raw relative count like an SDL motion event; `move` and `click` place the GUI cursor first.
using System.Globalization;
using OpenTribes.Gui;
using OpenTribes.Script;

namespace OpenTribes.Client.App;

public sealed class ClientScript
{
    private readonly ClientApp mApp;
    private readonly List<(double At, string Line)> mLines = new();
    private readonly List<(double At, Action Do)> mQueued = new();
    private int mNext;

    private ClientScript(ClientApp app) => mApp = app;

    public static ClientScript Load(ClientApp app, string path)
    {
        var s = new ClientScript(app);
        double t = 0;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line[0] == '@')
            {
                int sp = line.IndexOf(' ');
                string num = sp < 0 ? line[1..] : line[1..sp];
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double at))
                    t = at;
                line = sp < 0 ? "" : line[(sp + 1)..].Trim();
                if (line.Length == 0)
                    continue;
            }
            if (line.StartsWith("wait ", StringComparison.Ordinal) && double.TryParse(line[5..], NumberStyles.Float, CultureInfo.InvariantCulture, out double w))
            {
                t += w;
                continue;
            }
            s.mLines.Add((t, line));
        }
        return s;
    }

    public bool Finished => mNext >= mLines.Count && mQueued.Count == 0;

    // Runs every command that is due at `nowMs`.
    public void Run(uint nowMs)
    {
        double sec = nowMs / 1000.0;
        for (int i = 0; i < mQueued.Count;)
        {
            if (mQueued[i].At <= sec)
            {
                Action a = mQueued[i].Do;
                mQueued.RemoveAt(i);
                a();
            }
            else
                ++i;
        }
        while (mNext < mLines.Count && mLines[mNext].At <= sec)
            Exec(mLines[mNext++].Line, sec);
    }

    private void Echo(string s) => mApp.Interp.Print(1, s);

    // Finds the scan code of a key name or character: the table of client-scripts.md 3.4.
    public static (int Scan, bool Shift) Lookup(string name)
    {
        if (name.Length == 1)
        {
            char c = name[0];
            foreach (ClientCommands.KeyName k in ClientKeyTable.Keys)
                if (k.Lower == c && (k.Name == null || k.Name.Length > 1 || k.Lower == c))
                    return (k.Code, false);
            foreach (ClientCommands.KeyName k in ClientKeyTable.Keys)
                if (k.Upper == c)
                    return (k.Code, true);
        }
        foreach (ClientCommands.KeyName k in ClientKeyTable.Keys)
            if (k.Name != null && string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase))
                return (k.Code, false);
        switch (name.ToLowerInvariant())
        {
            case "esc": return (1, false);
            case "return": return (0x1c, false);
        }
        return (0, false);
    }

    private static readonly Dictionary<string, int> ModKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shift"] = 0x2a, ["ctrl"] = 0x1d, ["control"] = 0x1d, ["alt"] = 0x38,
    };

    private void PressKey(int scan, bool shift, params int[] mods)
    {
        var all = new List<int>(mods);
        if (shift && !all.Contains(0x2a))
            all.Add(0x2a);
        foreach (int m in all)
            mApp.InjectKey(m, true);
        mApp.InjectKey(scan, true);
        mApp.InjectKey(scan, false);
        for (int i = all.Count - 1; i >= 0; --i)
            mApp.InjectKey(all[i], false);
    }

    private void MoveTo(int x, int y)
    {
        GuiCanvas c = mApp.Canvas;
        int dx = x - (int)c.CursorX, dy = y - (int)c.CursorY;
        mApp.InjectAxis(0, dx);
        mApp.InjectAxis(1, dy);
    }

    private void Exec(string line, double now)
    {
        Echo("> " + line);
        string[] w = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string cmd = w[0].ToLowerInvariant();
        string rest = w.Length > 1 ? w[1] : "";
        string[] a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        void After(double dt, Action act) => mQueued.Add((now + dt, act));
        switch (cmd)
        {
            case "move": MoveTo(int.Parse(a[0], CultureInfo.InvariantCulture), int.Parse(a[1], CultureInfo.InvariantCulture)); break;
            case "mouse":
                mApp.InjectAxis(0, float.Parse(a[0], CultureInfo.InvariantCulture));
                mApp.InjectAxis(1, float.Parse(a[1], CultureInfo.InvariantCulture));
                break;
            case "click":
            case "rclick":
            case "dblclick":
                {
                    if (a.Length >= 2)
                        MoveTo(int.Parse(a[0], CultureInfo.InvariantCulture), int.Parse(a[1], CultureInfo.InvariantCulture));
                    int b = cmd == "rclick" ? 1 : 0;
                    int times = cmd == "dblclick" ? 2 : 1;
                    for (int i = 0; i < times; ++i)
                    {
                        double t0 = i * 0.06;
                        After(t0, () => mApp.InjectButton(b, true));
                        After(t0 + 0.03, () => mApp.InjectButton(b, false));
                    }
                    break;
                }
            case "down": mApp.InjectButton(0, true); break;
            case "up": mApp.InjectButton(0, false); break;
            case "rdown": mApp.InjectButton(1, true); break;
            case "rup": mApp.InjectButton(1, false); break;
            case "wheel": mApp.InjectAxis(2, float.Parse(a[0], CultureInfo.InvariantCulture) * 120f); break;
            case "key":
                {
                    (int scan, bool shift) = Lookup(a[0]);
                    if (scan == 0)
                    {
                        Echo("input: unknown key " + a[0]);
                        break;
                    }
                    var mods = a.Skip(1).Where(m => ModKeys.ContainsKey(m)).Select(m => ModKeys[m]).ToArray();
                    PressKey(scan, shift, mods);
                    break;
                }
            case "keydown":
            case "keyup":
                {
                    (int scan, bool shift) = Lookup(a[0]);
                    if (scan == 0)
                    {
                        Echo("input: unknown key " + a[0]);
                        break;
                    }
                    mApp.InjectKey(scan, cmd == "keydown");
                    break;
                }
            case "hold":
                {
                    (int scan, _) = Lookup(a[0]);
                    double secs = double.Parse(a[1], CultureInfo.InvariantCulture);
                    mApp.InjectKey(scan, true);
                    After(secs, () => mApp.InjectKey(scan, false));
                    break;
                }
            case "type":
                {
                    double t0 = 0;
                    foreach (char ch in rest)
                    {
                        (int scan, bool shift) = ch == ' ' ? (0x39, false) : Lookup(ch.ToString());
                        if (scan == 0)
                            continue;
                        After(t0, () => PressKey(scan, shift));
                        t0 += 0.03;
                    }
                    break;
                }
            case "eval": Echo("= " + mApp.Interp.Evaluate(rest)); break;
            case "echo": Echo(rest); break;
            case "var": Echo($"{a[0]} = {mApp.Interp.PeekVariable(a[0])}"); break;
            case "where":
            case "clickctl":
                {
                    GuiControl? c = mApp.GuiRt.FindControl(a[0]);
                    if (c == null)
                    {
                        Echo("input: no control " + a[0]);
                        break;
                    }
                    var (gx, gy) = c.GlobalOrigin();
                    if (cmd == "where")
                    {
                        Echo($"{c.ClassName} {c.Name} at {gx},{gy} {c.W}x{c.H}");
                        break;
                    }
                    MoveTo(gx + c.W / 2, gy + c.H / 2);
                    After(0.02, () => mApp.InjectButton(0, true));
                    After(0.06, () => mApp.InjectButton(0, false));
                    break;
                }
            case "focus": Echo("focus: " + ((mApp.Canvas.FirstResponder as GuiControl)?.ToString() ?? "root")); break;
            case "shot":
                mApp.QueueScreenshot(Path.Combine(mApp.Opt.ShotDir, rest.EndsWith(".png") ? rest : rest + ".png"));
                break;
            case "quit": mApp.RequestQuit(); break;
            default: Echo("input: unknown command " + cmd); break;
        }
    }
}
