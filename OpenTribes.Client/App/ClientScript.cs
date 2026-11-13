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
    private readonly ClientApp _app;
    private readonly List<(double At, string Line)> _lines = [];
    private readonly List<(double At, Action Do)> _queued = [];
    private int _next;
    private string? _waitFor;
    private double _waitStart, _waitLimit, _shift, _busyUntil;
    private bool _yield;

    private ClientScript(ClientApp app) => _app = app;

    private bool Check(string what)
    {
        var w = what.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (w[0].ToLowerInvariant())
        {
            case "play":
                return _app.Game != null && _app.Game.HasScene && _app.Canvas.Content?.Id == PlayScreenHandler.PlayGuiId;
            case "player":
                return _app.Game?.World.Psc.ControlClass == "Player";
            case "connected":
                return _app.Game != null && _app.Game.Joined;
            case "content":
                return w.Length > 1 && _app.Canvas.Content?.Id.ToString(CultureInfo.InvariantCulture) == w[1];
            default:
                break;
        }
        var r = what.StartsWith('$') && !what.Contains(' ') && !what.Contains('(')
            ? _app.Interp.PeekVariable(what)
            : _app.Interp.Evaluate(what.EndsWith(';') ? what : what + ";");
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
            s._lines.Add((t, line));
        }
        return s;
    }

    /// <summary>True when every line has run and no queued action is left.</summary>
    public bool Finished => _next >= _lines.Count && _queued.Count == 0;

    /// <summary>Runs every command that is due.</summary>
    /// <param name="nowMs">The app time in milliseconds.</param>
    public void Run(uint nowMs)
    {
        var sec = nowMs / 1000.0;
        for (var i = 0; i < _queued.Count;)
        {
            if (_queued[i].At <= sec)
            {
                var a = _queued[i].Do;
                _queued.RemoveAt(i);
                a();
            }
            else
                ++i;
        }
        if (_waitFor != null)
        {
            var done = Check(_waitFor);
            if (!done && sec - _waitStart < _waitLimit)
                return;
            if (!done)
                Echo("waitfor " + _waitFor + ": timed out");
            else
                Echo("waitfor " + _waitFor + ": true after " + (sec - _waitStart).ToString("0.0", CultureInfo.InvariantCulture) + " s");
            _shift += sec - _waitStart;
            _waitFor = null;
        }
        _yield = false;
        // `type`, `click` and `shot` hold the next line back until their own actions are done (a click is 0.1 s, a typed
        // character 0.03 s, a shot one frame), so a script reads top to bottom.
        while (sec >= _busyUntil && _next < _lines.Count && _lines[_next].At + _shift <= sec)
        {
            Exec(_lines[_next++].Line, sec);
            if (_waitFor != null || _yield)
                break;
        }
    }

    private void Echo(string s) => _app.Interp.Print(1, s);

    /// <summary>Finds the scan code of a key name or character, using the table of client-scripts.md 3.4.</summary>
    /// <param name="name">The key name or a single character.</param>
    /// <returns>The scan code and whether the character needs shift.</returns>
    public static (int Scan, bool Shift) Lookup(string name)
    {
        if (name.Length == 1)
        {
            var c = name[0];
            foreach (var k in ClientKeyTable.Keys)
                if (k.Lower == c && (k.Name == null || k.Name.Length > 1 || k.Lower == c))
                    return (k.Code, false);
            foreach (var k in ClientKeyTable.Keys)
                if (k.Upper == c)
                    return (k.Code, true);
        }
        foreach (var k in ClientKeyTable.Keys)
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
            _app.InjectKey(m, true);
        _app.InjectKey(scan, true);
        _app.InjectKey(scan, false);
        for (var i = all.Count - 1; i >= 0; --i)
            _app.InjectKey(all[i], false);
    }

    private void MoveTo(int x, int y)
    {
        var c = _app.Canvas;
        int dx = x - (int)c.CursorX, dy = y - (int)c.CursorY;
        _app.InjectAxis(0, dx);
        _app.InjectAxis(1, dy);
    }

    private void Exec(string line, double now)
    {
        Echo("> " + line);
        var w = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = w[0].ToLowerInvariant();
        var rest = w.Length > 1 ? w[1] : "";
        var a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        void After(double dt, Action act) => _queued.Add((now + dt, act));
        switch (cmd)
        {
            case "move":
                MoveTo(int.Parse(a[0], CultureInfo.InvariantCulture), int.Parse(a[1], CultureInfo.InvariantCulture));
                break;
            case "mouse":
                _app.InjectAxis(0, float.Parse(a[0], CultureInfo.InvariantCulture));
                _app.InjectAxis(1, float.Parse(a[1], CultureInfo.InvariantCulture));
                break;
            case "click":
            case "rclick":
            case "dblclick":
            {
                if (a.Length >= 2)
                    MoveTo(int.Parse(a[0], CultureInfo.InvariantCulture), int.Parse(a[1], CultureInfo.InvariantCulture));
                var b = cmd == "rclick" ? 1 : 0;
                var times = cmd == "dblclick" ? 2 : 1;
                _busyUntil = now + (0.1 * times);
                for (var i = 0; i < times; ++i)
                {
                    var t0 = i * 0.06;
                    After(t0, () => _app.InjectButton(b, true));
                    After(t0 + 0.03, () => _app.InjectButton(b, false));
                }
                break;
            }
            case "down":
                _app.InjectButton(0, true);
                break;
            case "up":
                _app.InjectButton(0, false);
                break;
            case "rdown":
                _app.InjectButton(1, true);
                break;
            case "rup":
                _app.InjectButton(1, false);
                break;
            case "wheel":
                _app.InjectAxis(2, float.Parse(a[0], CultureInfo.InvariantCulture) * 120f);
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
                _app.InjectKey(scan, cmd == "keydown");
                break;
            }
            case "hold":
            {
                (var scan, _) = Lookup(a[0]);
                var secs = double.Parse(a[1], CultureInfo.InvariantCulture);
                _app.InjectKey(scan, true);
                After(secs, () => _app.InjectKey(scan, false));
                break;
            }
            case "type":
            {
                double t0 = 0;
                _busyUntil = now + (0.03 * rest.Length) + 0.06;
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
                Echo("= " + _app.Interp.Evaluate(rest));
                break;
            case "echo":
                Echo(rest);
                break;
            case "var":
                Echo($"{a[0]} = {_app.Interp.PeekVariable(a[0])}");
                break;
            case "where":
            case "clickctl":
            {
                var c = _app.GuiRt.FindControl(a[0]);
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
                After(0.02, () => _app.InjectButton(0, true));
                After(0.06, () => _app.InjectButton(0, false));
                break;
            }
            case "focus":
                Echo("focus: " + ((_app.Canvas.FirstResponder as GuiControl)?.ToString() ?? "root"));
                break;
            case "shot":
                _app.QueueScreenshot(Path.Combine(_app.Opt.ShotDir, rest.EndsWith(".png") ? rest : rest + ".png"));
                _yield = true;
                break;
            case "quit":
                _app.RequestQuit();
                break;
            case "waitfor":
            {
                var last = a.Length > 1 && double.TryParse(a[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lim) && !a[0].Equals("content", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                _waitLimit = last == 1 ? double.Parse(a[^1], CultureInfo.InvariantCulture) : 60;
                _waitFor = last == 1 ? string.Join(' ', a[..^1]) : rest;
                _waitStart = now;
                break;
            }
            default:
                Echo("input: unknown command " + cmd);
                break;
        }
    }
}
