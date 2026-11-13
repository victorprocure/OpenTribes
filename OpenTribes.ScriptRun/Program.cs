namespace OpenTribes.ScriptRun;

using System.Text;
using OpenTribes.Core;
using OpenTribes.Script;
using OpenTribes.Sim;

/// <summary>Evaluates console lines from stdin for the script regression tests. The output of each line is followed by a line that holds only <c>\x01</c>.</summary>
/// <remarks>Port of tools/scriptrun.cpp.</remarks>
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

    /// <summary>Runs the loop until the input ends.</summary>
    /// <param name="argv">Optionally a game root and a search path. With a game root, file and resource commands work on it.</param>
    /// <returns>The process exit code.</returns>
    public static int Main(string[] argv)
    {
        var interp = new Interpreter();
        var root = argv.Length > 0 ? argv[0] : ".";
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
        var game = new Game.GameState { Sim = sim, Console = interp };
        ctx.Game = game;
        Game.MissionClasses.Register();
        game.Resources = res;
        Game.GameCommands.Register(interp, game);
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
        using var stdout = Console.OpenStandardOutput();
        string? l;
        while ((l = GetLine(stdin)) != null)
        {
            captured.Clear();
            interp.Evaluate(l);
            // printf("%s\n\x01\n"): the captured text up to its first NUL.
            var text = captured.ToString();
            var nul = text.IndexOf('\0');
            if (nul >= 0)
                text = text[..nul];
            stdout.Write(ByteString.ToBytes(text + "\n\x01\n"));
            stdout.Flush();
        }
        return 0;
    }
}
