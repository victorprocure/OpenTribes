// The command line of the `tribes` client process (docs/client-app.md 1.2). Own options start with `--` and are removed
// before $cargv is built (D46); every other token (-mod, +connect, +exec, -host, ...) goes to console.cs unchanged.
using System.Globalization;

namespace OpenTribes.Client.App;

public sealed class ClientOptions
{
    public string Root = ".";
    // `--log FILE`: the console log (what the original writes to console.log when $Console::logMode is set).
    public string? LogFile;
    public bool Echo;                       // `--echo`: console lines also go to stdout
    // `--input FILE`: a timed script that drives the session (ClientScript).
    public string? InputScript;
    // `--screenshot-at T,T,...`: seconds of app time; PNGs are written to `--shots DIR` (default `.`).
    public List<double> ScreenshotAt = new();
    public string ShotDir = ".";
    public int Width = 640, Height = 480;
    public string? VideoDriver;
    public bool NoAudio;
    public bool NoVsync;
    // `--windowed`: setFullscreenMode(true) is ignored (the stock prefs ask for full screen).
    public bool Windowed;
    // `--seconds S`: leave after S seconds of app time (tests).
    public double Seconds;
    // R6: the client talks to 127.0.0.0/8 only unless the user opts in on the command line.
    public bool AllowRemote;
    // `HTMLOpen` and `Notepad` hand a URL or file to the system only with this option.
    public bool AllowBrowser;
    // `--no-predict`: the control object is drawn from the server state (client-net.md 4.4 off).
    public bool NoPredict;
    // `--virtual-time[=MS]`: the clock advances MS (default 16) per frame and the loop never sleeps.
    public uint VirtualStep;
    // `--list-unknown`: print the unknown commands at exit (tests).
    public bool ReportUnknown;
    // The tokens for $cargv1...
    public readonly List<string> GameArgs = new();

    private static readonly string[] ValueOptions =
        { "--root", "--log", "--input", "--screenshot-at", "--shots", "--size", "--video-driver", "--seconds" };

    // The options that select one of the single-purpose tools of C2 to C7 instead of the client.
    private static readonly string[] ToolOptions =
    {
        "--show", "--gui", "--gui-census", "--gui-list", "--gui-tree", "--shape", "--shape-census", "--shape-info", "--mission",
        "--demo", "--sound-census", "--audio-live", "--audio-out", "--cat",
    };

    public static bool IsToolInvocation(string[] args) => args.Any(a => ToolOptions.Contains(a));

    // Null with `error` set on a bad command line.
    public static ClientOptions? Parse(string[] args, out string error)
    {
        error = "";
        var o = new ClientOptions();
        for (int i = 0; i < args.Length; ++i)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                o.GameArgs.Add(a);
                continue;
            }
            string name = a;
            string? inline = null;
            int eq = a.IndexOf('=');
            if (eq > 0)
            {
                name = a[..eq];
                inline = a[(eq + 1)..];
            }
            string? Value()
            {
                if (inline != null)
                    return inline;
                if (i + 1 >= args.Length)
                    return null;
                return args[++i];
            }
            if (ValueOptions.Contains(name))
            {
                string? v = Value();
                if (v == null)
                {
                    error = name + " needs a value";
                    return null;
                }
                switch (name)
                {
                    case "--root": o.Root = v; break;
                    case "--log": o.LogFile = v; break;
                    case "--input": o.InputScript = v; break;
                    case "--shots": o.ShotDir = v; break;
                    case "--video-driver": o.VideoDriver = v; break;
                    case "--screenshot-at":
                        foreach (string part in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double t) || t < 0)
                            {
                                error = "--screenshot-at expects seconds, comma separated";
                                return null;
                            }
                            o.ScreenshotAt.Add(t);
                        }
                        o.ScreenshotAt.Sort();
                        break;
                    case "--seconds":
                        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out o.Seconds) || o.Seconds < 0)
                        {
                            error = "--seconds expects a number";
                            return null;
                        }
                        break;
                    case "--size":
                        string[] wh = v.Split('x');
                        if (wh.Length != 2 || !int.TryParse(wh[0], out o.Width) || !int.TryParse(wh[1], out o.Height) || o.Width < 1 || o.Height < 1)
                        {
                            error = "--size expects WxH";
                            return null;
                        }
                        break;
                }
                continue;
            }
            switch (name)
            {
                case "--echo": o.Echo = true; break;
                case "--no-audio": o.NoAudio = true; break;
                case "--no-vsync": o.NoVsync = true; break;
                case "--windowed": o.Windowed = true; break;
                case "--allow-remote": o.AllowRemote = true; break;
                case "--allow-browser": o.AllowBrowser = true; break;
                case "--no-predict": o.NoPredict = true; break;
                case "--list-unknown": o.ReportUnknown = true; break;
                case "--virtual-time":
                    o.VirtualStep = inline != null && uint.TryParse(inline, out uint step) && step > 0 ? step : 16;
                    break;
                default:
                    error = "unknown option " + name;
                    return null;
            }
        }
        return o;
    }
}
