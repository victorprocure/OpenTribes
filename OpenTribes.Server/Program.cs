// Port of src/server/main.cpp: the dedicated server executable.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Script;
using OpenTribes.Sim;

namespace OpenTribes.Server;

// Port convention (no C++ file): fgets on a byte file, the C++ console line readers.
internal sealed class CFile : IDisposable
{
    private readonly Stream mStream;

    private CFile(Stream s) => mStream = s;

    // fopen(path, "rb"): null when the file cannot be opened.
    public static CFile? Open(string path)
    {
        try
        {
            return new CFile(new FileStream(path, FileMode.Open, FileAccess.Read));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // fgets(buf, size): at most size - 1 bytes, through the first '\n'; null at the end of the file.
    public string? Fgets(int size)
    {
        var line = new System.Text.StringBuilder();
        while (line.Length < size - 1)
        {
            int b = mStream.ReadByte();
            if (b < 0)
                break;
            line.Append((char)b);
            if (b == '\n')
                break;
        }
        return line.Length == 0 ? null : line.ToString();
    }

    public void Dispose() => mStream.Dispose();
}

public static class Program
{
    private static volatile bool sStop;

    // src/server/main.cpp usage.
    private static void Usage()
    {
        StdIo.Err(
            "usage: tribes-server [--port N] [--hostname S] [--password S] [--max-players N]\n" +
            "                     [--info S] [--lasthope] [--console-log FILE] [--allow-masters]\n" +
            "                     [--root DIR [--report-unknown | --report-unknown-at-exit] [-- ARGS...]]\n" +
            "                     [--virtual-time[=MS]] [--frame-eval FILE]\n" +
            "  --root DIR          game directory (console.cs, base/); runs console.cs with ARGS as $cargv\n" +
            "  --virtual-time[=MS] test mode: the clock advances MS (default 1) per frame, no sleeping\n" +
            "  --frame-eval FILE   console lines, one per frame at the frame-end step; \"@wait MS\"\n" +
            "                      runs frames for MS of clock time; the server exits at the end\n");
    }

    private static string StripLineEnd(string l)
    {
        int n = l.Length;
        while (n > 0 && (l[n - 1] == '\n' || l[n - 1] == '\r'))
            --n;
        return l[..n];
    }

    private static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);

    // strcasecmp(a, b) == 0 (ASCII).
    private static bool StrcaseEquals(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; ++i)
            if ((a[i] >= 'A' && a[i] <= 'Z' ? a[i] + 32 : a[i]) != (b[i] >= 'A' && b[i] <= 'Z' ? b[i] + 32 : b[i]))
                return false;
        return true;
    }

    private static void ReportUnknown(Interpreter console)
    {
        StdIo.Err("unknown commands (" + Num(console.UnknownCommands().Count) + "):\n");
        foreach (var kv in console.UnknownCommands())
            StdIo.Err(Num(kv.Value).PadLeft(6) + " " + kv.Key + "\n");
    }

    // src/server/main.cpp main.
    public static int Main(string[] argv)
    {
        var config = new ServerConfig();
        string root = "";
        bool reportUnknown = false;
        bool reportUnknownAtExit = false;
        bool listDataBlocks = false;
        string evalFile = "";
        string frameEvalFile = "";
        uint virtualStep = 0;       // ms per frame; 0: real time
        bool allowMasters = false;  // R6: heartbeats and name lookups leave the machine only with this
        string consoleLogPath = "console.log"; // relative to the working directory, like the original
        var gameArgs = new List<string>();
        for (int i = 0; i < argv.Length; ++i)
        {
            string arg = argv[i];
            if (arg == "--")
            {
                for (++i; i < argv.Length; ++i)
                    gameArgs.Add(argv[i]);
                break;
            }
            string v = "";
            bool needsValue = arg is "--port" or "--hostname" or "--password" or "--max-players" or "--info" or "--root" or
                                     "--console-log" or "--eval" or "--frame-eval";
            if (needsValue)
            {
                if (i + 1 >= argv.Length)
                {
                    Usage();
                    return 2;
                }
                v = argv[++i];
            }
            if (arg == "--port")
                config.Port = unchecked((ushort)NetServer.CAtoi(v));
            else if (arg == "--hostname")
                config.Hostname = ByteString.FromHost(v);
            else if (arg == "--password")
                config.Password = ByteString.FromHost(v);
            else if (arg == "--max-players")
                config.MaxPlayers = ByteString.FromHost(v);
            else if (arg == "--info")
                config.Info = ByteString.FromHost(v);
            else if (arg == "--lasthope")
                config.LastHope = true;
            else if (arg == "--allow-masters")
                allowMasters = true;
            else if (arg == "--root")
                root = v;
            else if (arg == "--report-unknown")
                reportUnknown = true;
            else if (arg == "--report-unknown-at-exit")
                reportUnknownAtExit = true;
            else if (arg == "--list-datablocks")
                listDataBlocks = true;
            else if (arg == "--console-log")
                consoleLogPath = v;
            else if (arg == "--eval")
                evalFile = v;
            else if (arg == "--frame-eval")
                frameEvalFile = v;
            else if (arg.StartsWith("--virtual-time", StringComparison.Ordinal) && (arg.Length == 14 || arg[14] == '='))
                virtualStep = arg.Length > 14 ? (uint)Math.Max(1, NetServer.CAtoi(arg[15..])) : 1;
            else
            {
                Usage();
                return 2;
            }
        }
        var server = new NetServer(config);

        // Script startup: console.cs parses $cargv like the original exe (docs/resources.md 1).
        ResourceManager? resources = null;
        Interpreter? console = null;
        var simContext = new ClassContext();
        SimConsole? sim = null;
        var game = new GameState();
        TelnetConsole? telnet = null;
        MasterHeartbeat? master = null;
        using var consoleLog = new ConsoleLog(consoleLogPath);
        if (root.Length != 0)
        {
            resources = new ResourceManager(root);
            console = new Interpreter();
            // Console output: the terminal, the log file and every authenticated telnet client
            // (docs/server-ops.md 3.4, 6).
            console.SetOutput(line =>
            {
                StdIo.Out(line);
                consoleLog.Write(line);
                telnet?.ConsoleLine(line);
            });
            console.BindVariable("$Console::logMode", v => consoleLog.SetMode(Numbers.BorlandAtoi(v)));
            console.SetVariable("$Console::Prompt", "% ");
            console.SetResources(resources);
            Builtins.RegisterBuiltins(console, resources, root);
            simContext.Resources = resources;
            // server.cs creates serverDelegate (FearCSDelegate): (isServer, transport, port, ...).
            simContext.StartNetwork = args =>
            {
                for (int i = 1; i + 1 < args.Count; i += 2)
                    if (StrcaseEquals(args[i], "IP"))
                    {
                        if (!server.Open(unchecked((ushort)NetServer.CAtoi(args[i + 1]))))
                            return false;
                        // 0x0054fxxx: a server that is not $SinglePlayer announces itself.
                        if (master != null && sim != null && sim.Server() != null &&
                            !Numbers.ParseBool(console.PeekVariable("$SinglePlayer")))
                        {
                            SimManager m = sim.Server()!;
                            master.Start((delay, action) => m.PostEvent(m, Fpu.AddF(m.Time(), delay), action));
                        }
                        return true;
                    }
                return true;
            };
            sim = new SimConsole(console, simContext);
            sim.RegisterCommands();
            console.SetObjects(sim);
            game.Sim = sim;
            game.Console = console;
            simContext.Game = game;
            simContext.ServerCreated = m => game.Players.StartPingTimer(m);
            MissionClasses.Register();
            GhostClasses.Register();
            game.Resources = resources;
            GameCommands.Register(console, game);
            SimConsole simOps = sim;
            {
                var ops = new ServerCommandContext
                {
                    Net = server,
                    RootDir = root,
                    IsChatMenu = name =>
                    {
                        SimObject? obj = simOps.Server()?.Find(name);
                        return obj != null && obj.ClassName() == "ChatMenu";
                    },
                    Quit = () => sStop = true,
                };
                ServerCommands.RegisterServerCommands(console, ops);
                master = new MasterHeartbeat(console, server, allowMasters);
                master.RegisterCommands();
                Interpreter inLog = console;
                server.SetLeveledLog((level, line) => inLog.Print(level, line));
            }
            ResourceManager res = resources;
            console.BindVariable("$ConsoleWorld::DefaultSearchPath", path => res.SetSearchPath(path));
            // Engine variable; the reference under Wine reports 0 (docs/server-ops.md 2.3).
            console.SetVariable("$CPU::estimatedSpeed", "0");
            console.SetVariable("$cargv0", "Tribes.exe");
            for (int i = 0; i < gameArgs.Count; ++i)
                console.SetVariable("$cargv" + Num(i + 1), ByteString.FromHost(gameArgs[i]));
            if (config.LastHope)
            {
                Builtins.ApplyLastHopeMode(console); // after every command is registered
                consoleLog.SetLastHope(true);
            }
            // The exe reads console.cs from the game directory itself; it is outside every search
            // path. Whether it prints "Executing console.cs." is not visible in the log (unverified).
            byte[] text;
            try
            {
                text = File.ReadAllBytes(root + "/console.cs");
            }
            catch (Exception)
            {
                StdIo.Err("no console.cs in " + ByteString.FromHost(root) + "\n");
                return 1;
            }
            console.Evaluate(ByteString.FromBytes(text), "console.cs");
            StdIo.Flush();
            if (listDataBlocks)
            {
                // group, index, name, then each field that a constructor initializes, with its count.
                sim.DataBlocks().ForEach(b =>
                {
                    foreach (DataBlockManager.Field f in b.Fields)
                        if (!f.Def.Uninit)
                            StdIo.Write(Num(b.Group) + "\t" + Num(b.Index) + "\t" + b.Name + "\t" + f.Def.Name + "\t" + Num(f.Def.Count) + "\n");
                });
                StdIo.Flush();
                return 0;
            }
            if (evalFile.Length != 0)
            {
                // Test hook: evaluate console lines after start-up; each line's output ends with "\x01".
                using CFile? input = CFile.Open(evalFile);
                StdIo.Write("\x01\n"); // end of start-up output
                string? line;
                while (input != null && (line = input.Fgets(8192)) != null)
                {
                    console.Evaluate(StripLineEnd(line));
                    StdIo.Write("\x01\n");
                }
                return 0;
            }
            if (reportUnknown)
            {
                ReportUnknown(console);
                return 0;
            }
        }

        if (console != null)
        {
            // Query replies read the live script state (docs/net-protocol.md 6.1).
            server.SetGameInfo(GameInfoProvider.Create(console, game));
        }
        GameServer? gameServer = null;
        if (console != null)
        {
            Interpreter inTelnet = console;
            var th = new TelnetConsole.Host
            {
                Port = () => Numbers.BorlandAtoi(inTelnet.PeekVariable("$TelnetPort")),
                Password = () => inTelnet.PeekVariable("$TelnetPassword"),
                Prompt = () => inTelnet.PeekVariable("$Console::Prompt"),
                Evaluate = line => inTelnet.Evaluate(line),
                Print = line => inTelnet.Print(1, line),
            };
            telnet = new TelnetConsole(th);
            gameServer = new GameServer(game, console, server);
            gameServer.RegisterCommands();
            game.AllocateRecordId = () => (int)server.AllocatePlayerId();
            game.ReleaseRecordId = id => server.ReleasePlayerId((uint)id);
        }
        if (!server.IsOpen() && !server.Open())
        {
            StdIo.Err("cannot bind UDP port " + Num(config.Port) + "\n");
            return 1;
        }
        StdIo.Write("OpenTribes dedicated server on UDP " + Num(server.Port()) + " (" + (config.LastHope ? "1.30" : "1.11") + ")\n");
        StdIo.Flush();

        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
        {
            ctx.Cancel = true;
            sStop = true;
        });
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            sStop = true;
        });
        // Frame loop (docs/sim.md 8.3, TK1-TK9). The connection layer runs on the unscaled
        // millisecond clock (GetTickCount in the original), the simulation on the scaled one.
        var start = Stopwatch.StartNew();
        uint virtualMs = 0;
        uint ClockMs()
        {
            if (virtualStep != 0)
                return virtualMs;
            return unchecked((uint)start.ElapsedMilliseconds);
        }
        if (virtualStep != 0 && sim != null)
            sim.SetClockSource(() => Fpu.Mul(virtualMs, 0.001));
        CFile? frameInput = frameEvalFile.Length == 0 ? null : CFile.Open(frameEvalFile);
        if (frameEvalFile.Length != 0 && frameInput == null)
        {
            StdIo.Err("cannot read " + ByteString.FromHost(frameEvalFile) + "\n");
            return 1;
        }
        uint waitUntil = 0;
        while (!sStop)
        {
            uint ms = ClockMs();
            sim?.UpdateClock();        // step 2
            server.ReceivePackets(ms); // step 3: receive, ticks, send
            sim?.AdvanceServer();
            server.SendPackets(ms);
            if (virtualStep != 0)      // step 4: Sleep(1)
                virtualMs += virtualStep;
            else
                Thread.Sleep(1);
            telnet?.Poll();            // step 6: remote console lines
            if (frameInput != null && console != null && unchecked((int)(ClockMs() - waitUntil)) >= 0)
            {
                // Step 6, console input: one line per frame, like a remote console line.
                string? line = frameInput.Fgets(8192);
                if (line == null)
                    break;
                string l = StripLineEnd(line);
                if (l.StartsWith("@wait ", StringComparison.Ordinal))
                    waitUntil = ClockMs() + unchecked((uint)NetServer.CAtoi(l[6..]));
                else
                    console.Evaluate(l);
            }
            sim?.EndFrame();           // step 6: Game::EndFrame
        }
        frameInput?.Dispose();
        if (reportUnknownAtExit && console != null)
        {
            // A running server reports what the session called without a registered handler.
            ReportUnknown(console);
        }
        telnet?.Dispose();
        return 0;
    }
}
