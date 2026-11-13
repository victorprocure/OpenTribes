namespace OpenTribes.Client.App;

using System.Globalization;
using OpenTribes.Gui;
using OpenTribes.Script;

/// <summary>The automation hook of the client: <c>tribes --input FILE</c> drives a whole session from a timed script.</summary>
/// <remarks>
/// <para>One command per line, <c>#</c> starts a comment. A line may start with <c>@SECONDS</c> (app time, the same clock as <c>--screenshot-at</c>). Without it the line runs at the time of the line before it. <c>wait SECONDS</c> moves that time on.</para>
/// <list type="bullet">
/// <item><description><c>move X Y | mouse DX DY | click [X Y] | rclick [X Y] | dblclick [X Y] | down | up | rdown | rup | wheel N</c></description></item>
/// <item><description><c>key NAME [shift|ctrl|alt]... | keydown NAME | keyup NAME | hold NAME SECONDS | type TEXT</c></description></item>
/// <item><description><c>eval CONSOLE TEXT | echo TEXT | var $NAME | where NAME_OR_ID | clickctl NAME_OR_ID | focus | shot NAME | quit | wait SECONDS</c></description></item>
/// <item><description><c>waitfor WHAT [SECONDS]</c> holds the later lines until WHAT is true or SECONDS (default 60) passed. WHAT is <c>play</c> (the play screen shows the world), <c>player</c> (the control object is a Player), <c>connected</c>, <c>content ID</c>, or console text that gives true or 1.</description></item>
/// </list>
/// <para><c>key</c>, <c>keydown</c> and <c>keyup</c> go through the same dispatcher as an SDL key event (modifiers, GUI first, then the maps). <c>mouse</c> is a raw relative count like an SDL motion event. <c>move</c> and <c>click</c> place the GUI cursor first.</para>
/// </remarks>
public sealed class ClientScript
{
    private readonly ClientApp mApp;
    private readonly List<(double At, string Line)> mLines = [];
    private readonly List<(double At, Action Do)> mQueued = [];
    private int mNext;
    private string? mWaitFor;
    private double mWaitStart, mWaitLimit, mShift, mBusyUntil;
    private bool mYield;

    private ClientScript(ClientApp app) => mApp = app;

    private bool Check(string what)
    {
        var w = what.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (w[0].ToLowerInvariant())
        {
            case "play":
                return mApp.Game != null && mApp.Game.HasScene && mApp.Canvas.Content?.Id == PlayDelegate.PlayGuiId;
            case "player":
                return mApp.Game?.World.Psc.ControlClass == "Player";
            case "connected":
                return mApp.Game != null && mApp.Game.Joined;
            case "content":
                return w.Length > 1 && mApp.Canvas.Content?.Id.ToString(CultureInfo.InvariantCulture) == w[1];
        }
        var r = what.StartsWith('$') && !what.Contains(' ') && !what.Contains('(')
            ? mApp.Interp.PeekVariable(what)
            : mApp.Interp.Evaluate(what.EndsWith(';') ? what : what + ";");
        return r.Equals("true", StringComparison.OrdinalIgnoreCase) || r == "1";
    }

    /// <summary>Reads a script file.</summary>
    /// <param name="app">The client the script drives.</param>
    /// <param name="path">The script path.</param>
    /// <returns>The script.</returns>
    public static ClientScript Load(ClientApp app, string path)
    {
        var s = new ClientScript(app);
        double t = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line[0] == '@')
            {
                var sp = line.IndexOf(' ');
                var num = sp < 0 ? line[1..] : line[1..sp];
                if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var at))
                    t = at;
                line = sp < 0 ? "" : line[(sp + 1)..].Trim();
                if (line.Length == 0)
                    continue;
            }
            if (line.StartsWith("wait ", StringComparison.Ordinal) && double.TryParse(line[5..], NumberStyles.Float, CultureInfo.InvariantCulture, out var w))
            {
                t += w;
                continue;
            }
            s.mLines.Add((t, line));
        }
        return s;
    }

    /// <summary>True when every line has run and no queued action is left.</summary>
    public bool Finished => mNext >= mLines.Count && mQueued.Count == 0;

    /// <summary>Runs every command that is due.</summary>
    /// <param name="nowMs">The app time in milliseconds.</param>
    public void Run(uint nowMs)
    {
        var sec = nowMs / 1000.0;
        for (var i = 0; i < mQueued.Count;)
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
        if (mWaitFor != null)
        {
            var done = Check(mWaitFor);
            if (!done && sec - mWaitStart < mWaitLimit)
                return;
            if (!done)
                Echo("waitfor " + mWaitFor + ": timed out");
            else
                Echo("waitfor " + mWaitFor + ": true after " + (sec - mWaitStart).ToString("0.0", CultureInfo.InvariantCulture) + " s");
            mShift += sec - mWaitStart;
            mWaitFor = null;
        }
        mYield = false;
        // `type`, `click` and `shot` hold the next line back until their own actions are done (a click is 0.1 s, a typed
        // character 0.03 s, a shot one frame), so a script reads top to bottom.
        while (sec >= mBusyUntil && mNext < mLines.Count && mLines[mNext].At + mShift <= sec)
        {
            Exec(mLines[mNext++].Line, sec);
            if (mWaitFor != null || mYield)
                break;
        }
    }

    private void Echo(string s) => mApp.Interp.Print(1, s);

    /// <summary>Finds the scan code of a key name or character, using the table of client-scripts.md 3.4.</summary>
    /// <param name="name">The key name or a single character.</param>
    /// <returns>The scan code and whether the character needs shift.</returns>
    public static (int Scan, bool Shift) Lookup(string name)
    {
        if (name.Length == 1)
        {
            var c = name[0];
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
        return name.ToLowerInvariant() switch
        {
            "esc" => (1, false),
            "return" => (0x1c, false),
            _ => (0, false),
        };
    }

    private static readonly Dictionary<string, int> ModKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shift"] = 0x2a,
        ["ctrl"] = 0x1d,
        ["control"] = 0x1d,
        ["alt"] = 0x38,
    };

    private void PressKey(int scan, bool shift, params int[] mods)
    {
        var all = new List<int>(mods);
        if (shift && !all.Contains(0x2a))
            all.Add(0x2a);
        foreach (var m in all)
            mApp.InjectKey(m, true);
        mApp.InjectKey(scan, true);
        mApp.InjectKey(scan, false);
        for (var i = all.Count - 1; i >= 0; --i)
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
        var w = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = w[0].ToLowerInvariant();
        var rest = w.Length > 1 ? w[1] : "";
        var a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        void After(double dt, Action act) => mQueued.Add((now + dt, act));
        switch (cmd)
        {
            case "move":
                MoveTo(int.Parse(a[0], CultureInfo.InvariantCulture), int.Parse(a[1], CultureInfo.InvariantCulture));
                break;
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
                var b = cmd == "rclick" ? 1 : 0;
                var times = cmd == "dblclick" ? 2 : 1;
                mBusyUntil = now + (0.1 * times);
                for (var i = 0; i < times; ++i)
                {
                    var t0 = i * 0.06;
                    After(t0, () => mApp.InjectButton(b, true));
                    After(t0 + 0.03, () => mApp.InjectButton(b, false));
                }
                break;
            }
            case "down":
                mApp.InjectButton(0, true);
                break;
            case "up":
                mApp.InjectButton(0, false);
                break;
            case "rdown":
                mApp.InjectButton(1, true);
                break;
            case "rup":
                mApp.InjectButton(1, false);
                break;
            case "wheel":
                mApp.InjectAxis(2, float.Parse(a[0], CultureInfo.InvariantCulture) * 120f);
                break;
            case "key":
            {
                (var scan, var shift) = Lookup(a[0]);
                if (scan == 0)
                {
                    Echo("input: unknown key " + a[0]);
                    break;
                }
                var mods = a.Skip(1).Where(ModKeys.ContainsKey).Select(m => ModKeys[m]).ToArray();
                PressKey(scan, shift, mods);
                break;
            }
            case "keydown":
            case "keyup":
            {
                (var scan, var shift) = Lookup(a[0]);
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
                (var scan, _) = Lookup(a[0]);
                var secs = double.Parse(a[1], CultureInfo.InvariantCulture);
                mApp.InjectKey(scan, true);
                After(secs, () => mApp.InjectKey(scan, false));
                break;
            }
            case "type":
            {
                double t0 = 0;
                mBusyUntil = now + (0.03 * rest.Length) + 0.06;
                foreach (var ch in rest)
                {
                    (var scan, var shift) = ch == ' ' ? (0x39, false) : Lookup(ch.ToString());
                    if (scan == 0)
                        continue;
                    After(t0, () => PressKey(scan, shift));
                    t0 += 0.03;
                }
                break;
            }
            case "eval":
                Echo("= " + mApp.Interp.Evaluate(rest));
                break;
            case "echo":
                Echo(rest);
                break;
            case "var":
                Echo($"{a[0]} = {mApp.Interp.PeekVariable(a[0])}");
                break;
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
                MoveTo(gx + (c.W / 2), gy + (c.H / 2));
                After(0.02, () => mApp.InjectButton(0, true));
                After(0.06, () => mApp.InjectButton(0, false));
                break;
            }
            case "focus":
                Echo("focus: " + ((mApp.Canvas.FirstResponder as GuiControl)?.ToString() ?? "root"));
                break;
            case "shot":
                mApp.QueueScreenshot(Path.Combine(mApp.Opt.ShotDir, rest.EndsWith(".png") ? rest : rest + ".png"));
                mYield = true;
                break;
            case "quit":
                mApp.RequestQuit();
                break;
            case "waitfor":
            {
                var last = a.Length > 1 && double.TryParse(a[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lim) && !a[0].Equals("content", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                mWaitLimit = last == 1 ? double.Parse(a[^1], CultureInfo.InvariantCulture) : 60;
                mWaitFor = last == 1 ? string.Join(' ', a[..^1]) : rest;
                mWaitStart = now;
                break;
            }
            default:
                Echo("input: unknown command " + cmd);
                break;
        }
    }
}
