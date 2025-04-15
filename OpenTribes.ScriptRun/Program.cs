// Port of tools/scriptrun.cpp: reads console lines from stdin, evaluates each like a console line, and prints that
// line's output followed by a line containing only "\x01".
using System.Text;
using OpenTribes.Core;
using OpenTribes.Script;
using OpenTribes.Sim;

namespace OpenTribes.ScriptRun;

public static class Program
{
    // std::getline on stdin: bytes up to '\n' (not kept); null at the end of the input.
    private static string? GetLine(Stream input)
    {
        var line = new StringBuilder();
        int b;
        while ((b = input.ReadByte()) >= 0)
        {
            if (b == '\n')
                return line.ToString();
            line.Append((char)b);
        }
        return line.Length == 0 ? null : line.ToString();
    }

    // tools/scriptrun.cpp main: scriptrun [root searchPath]: with a game root, file and resource commands work on it.
    public static int Main(string[] argv)
    {
        var interp = new Interpreter();
        string root = argv.Length > 0 ? argv[0] : ".";
        var res = new ResourceManager(root);
        if (argv.Length > 1)
            res.SetSearchPath(ByteString.FromHost(argv[1]));
        interp.SetResources(res);
        Builtins.RegisterBuiltins(interp, res, root);
        // A server world with a scheduler, like the dedicated start-up (newServer, server.cs).
        var ctx = new ClassContext { Resources = res };
        var sim = new SimConsole(interp, ctx);
        sim.RegisterCommands();
        interp.SetObjects(sim);
        var game = new OpenTribes.Game.GameState { Sim = sim, Console = interp };
        ctx.Game = game;
        OpenTribes.Game.MissionClasses.Register();
        game.Resources = res;
        OpenTribes.Game.GameCommands.Register(interp, game);
        if (Environment.GetEnvironmentVariable("OT_LASTHOPE") != null) // LastHope 1.30 script mode (tools/scriptdiff.py against a 1.30 reference)
            Builtins.ApplyLastHopeMode(interp);
        interp.Evaluate("newServer(); focusServer(); newObject(ConsoleScheduler, SimConsoleScheduler);");
        var captured = new StringBuilder();
        interp.SetOutput(line =>
        {
            if (captured.Length != 0)
                captured.Append('\n');
            captured.Append(line);
        });
        using Stream stdin = new BufferedStream(Console.OpenStandardInput());
        using Stream stdout = Console.OpenStandardOutput();
        string? l;
        while ((l = GetLine(stdin)) != null)
        {
            captured.Clear();
            interp.Evaluate(l);
            // printf("%s\n\x01\n"): the captured text up to its first NUL.
            string text = captured.ToString();
            int nul = text.IndexOf('\0');
            if (nul >= 0)
                text = text[..nul];
            stdout.Write(ByteString.ToBytes(text + "\n\x01\n"));
            stdout.Flush();
        }
        return 0;
    }
}
