// Port of the World fixture of tests/fixtures.hpp for stage P2: the interpreter with the script built-ins on a game
// root. The sim and game layers of the C++ fixture (ClassContext, SimConsole, GameState, registerMissionClasses,
// registerGameCommands) are added with stages P3 and P4.
using OpenTribes.Core;
using OpenTribes.Script;

namespace OpenTribes.Tests;

internal sealed class TestWorld
{
    public readonly Interpreter In = new();
    public readonly ResourceManager Res;
    private string mOut = "";
    private int mLines;

    public TestWorld(string root)
    {
        Res = new ResourceManager(root);
        Res.SetSearchPath("base");
        In.SetResources(Res);
        Builtins.RegisterBuiltins(In, Res, root);
        // P3: ctx.resources, SimConsole sim(in, ctx), sim.registerCommands(), in.setObjects(sim), registerMissionClasses()
        // P4: GameState game, registerGameCommands(in, game)
        In.SetOutput(l =>
        {
            mOut += (mLines++ != 0 ? "\n" : "") + l;
        });
        Run("newServer(); focusServer(); newObject(ConsoleScheduler, SimConsoleScheduler);");
    }

    public string Run(string s)
    {
        mOut = "";
        mLines = 0;
        In.Evaluate(s);
        return mOut;
    }
}
