namespace OpenTribes.Client.App;

using System.Globalization;

/// <summary>The command line of the <c>tribes</c> client process.</summary>
/// <remarks>docs/client-app.md 1.2. Own options start with <c>--</c> and are removed before <c>$cargv</c> is built (D46). Every other token (-mod, +connect, +exec, -host, ...) goes to console.cs unchanged.</remarks>
public sealed class ClientOptions
{
    /// <summary>The game root directory (<c>--root</c>).</summary>
    public string Root { get; set; } = ".";
    /// <summary>The console log file (<c>--log FILE</c>): what the original writes to console.log when <c>$Console::logMode</c> is set.</summary>
    public string? LogFile { get; set; }
    /// <summary>True when console lines also go to stdout (<c>--echo</c>).</summary>
    public bool Echo { get; set; }
    /// <summary>A timed script that drives the session (<c>--input FILE</c>, see <see cref="ClientScript"/>).</summary>
    public string? InputScript { get; set; }
    /// <summary>The seconds of app time at which to write a screenshot (<c>--screenshot-at T,T,...</c>). The PNGs go to <see cref="ShotDir"/>.</summary>
    public List<double> ScreenshotAt { get; set; } = [];
    /// <summary>The directory of the screenshots (<c>--shots DIR</c>).</summary>
    public string ShotDir { get; set; } = ".";
    /// <summary>The window width (<c>--size WxH</c>).</summary>
    public ref int Width => ref _width;
    private int _width = 640;
    /// <summary>The window height.</summary>
    public ref int Height => ref _height;
    private int _height = 480;
    /// <summary>The video driver name (<c>--video-driver</c>), or null for the default.</summary>
    public string? VideoDriver { get; set; }
    /// <summary>True to run without audio output (<c>--no-audio</c>).</summary>
    public bool NoAudio { get; set; }
    /// <summary>A fixed camera for the live world (<c>--camera X,Y,Z,YAW,PITCH</c>): degrees, yaw 0 looks along +Y, positive turns left.</summary>
    public double[]? FixedCamera { get; set; }
    /// <summary>True to disable the vertical sync.</summary>
    public bool NoVsync { get; set; }
    /// <summary>True for no window, no input and no audio output (<c>--headless</c>). The frame loop runs on the virtual clock (client-app.md 1.2).</summary>
    public bool Headless { get; set; }
    /// <summary>True to ignore <c>setFullscreenMode(true)</c>, because the stock prefs ask for full screen (<c>--windowed</c>).</summary>
    public bool Windowed { get; set; }
    /// <summary>True when full screen is the borderless desktop mode (canvas at the desktop size) and not a display mode change (<c>--fullscreen-windowed</c>).</summary>
    public bool FullscreenWindowed { get; set; }
    /// <summary>The seconds of app time after which to leave, for tests (<c>--seconds S</c>).</summary>
    public ref double Seconds => ref _seconds;
    private double _seconds;
    /// <summary>True when the user opted in to remote addresses. Otherwise the client talks to 127.0.0.0/8 only (R6).</summary>
    public bool AllowRemote { get; set; }
    /// <summary>True when <c>HTMLOpen</c> and <c>Notepad</c> may hand a URL or file to the system.</summary>
    public bool AllowBrowser { get; set; }
    /// <summary>True to draw the control object from the server state, with client-net.md 4.4 off (<c>--no-predict</c>).</summary>
    public bool NoPredict { get; set; }
    /// <summary>The milliseconds the clock advances per frame, with a loop that never sleeps (<c>--virtual-time[=MS]</c>, default 16). Zero for the real clock.</summary>
    public uint VirtualStep { get; set; }
    /// <summary>True to print the unknown commands at exit, for tests (<c>--list-unknown</c>).</summary>
    public bool ReportUnknown { get; set; }
    /// <summary>The tokens for <c>$cargv1</c> and up.</summary>
    public List<string> GameArgs { get; } = [];

    private static readonly string[] ValueOptions =
        ["--root", "--log", "--input", "--screenshot-at", "--shots", "--size", "--video-driver", "--seconds", "--camera"];

    // The options that select one of the single purpose tools instead of the client.
    private static readonly string[] ToolOptions =
    [
        "--show", "--gui", "--gui-census", "--gui-list", "--gui-tree", "--shape", "--shape-census", "--shape-info", "--mission",
        "--demo", "--sound-census", "--audio-live", "--audio-out", "--cat",
    ];

    /// <summary>Tells whether the arguments select one of the single purpose tools instead of the client.</summary>
    /// <param name="args">The command line arguments.</param>
    /// <returns>True for a tool invocation.</returns>
    public static bool IsToolInvocation(string[] args) => args.Any(a => ToolOptions.Contains(a));

    /// <summary>Parses a command line.</summary>
    /// <param name="args">The command line arguments.</param>
    /// <param name="error">The error text on a bad command line.</param>
    /// <returns>The options, or null with <paramref name="error"/> set on a bad command line.</returns>
    public static ClientOptions? Parse(string[] args, out string error)
    {
        error = "";
        var o = new ClientOptions();
        for (var i = 0; i < args.Length; ++i)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                o.GameArgs.Add(a);
                continue;
            }
            var name = a;
            string? inline = null;
            var eq = a.IndexOf('=');
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
                var v = Value();
                if (v == null)
                {
                    error = name + " needs a value";
                    return null;
                }
                switch (name)
                {
                    case "--root":
                        o.Root = v;
                        break;
                    case "--log":
                        o.LogFile = v;
                        break;
                    case "--input":
                        o.InputScript = v;
                        break;
                    case "--shots":
                        o.ShotDir = v;
                        break;
                    case "--camera":
                        var cam = v.Split(',').Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
                        if (cam.Length != 5 || cam.Any(double.IsNaN))
                        {
                            error = "--camera expects X,Y,Z,YAW,PITCH";
                            return null;
                        }
                        o.FixedCamera = cam;
                        break;
                    case "--video-driver":
                        o.VideoDriver = v;
                        break;
                    case "--screenshot-at":
                        foreach (var part in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) || t < 0)
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
                        var wh = v.Split('x');
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
                case "--echo":
                    o.Echo = true;
                    break;
                case "--no-audio":
                    o.NoAudio = true;
                    break;
                case "--no-vsync":
                    o.NoVsync = true;
                    break;
                case "--windowed":
                    o.Windowed = true;
                    break;
                case "--fullscreen-windowed":
                    o.FullscreenWindowed = true;
                    break;
                case "--headless":
                    o.Headless = true;
                    if (o.VirtualStep == 0)
                        o.VirtualStep = 16;
                    o.NoAudio = true;
                    break;
                case "--allow-remote":
                    o.AllowRemote = true;
                    break;
                case "--allow-browser":
                    o.AllowBrowser = true;
                    break;
                case "--no-predict":
                    o.NoPredict = true;
                    break;
                case "--list-unknown":
                    o.ReportUnknown = true;
                    break;
                case "--virtual-time":
                    o.VirtualStep = inline != null && uint.TryParse(inline, out var step) && step > 0 ? step : 16;
                    break;
                default:
                    error = "unknown option " + name;
                    return null;
            }
        }
        return o;
    }
}
