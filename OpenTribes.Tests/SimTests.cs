// Port of tests/test_sim.cpp: ids, groups, fixed ids, sets, schedule ordering and ticks (docs/sim.md).
using OpenTribes.Core;
using OpenTribes.Script;
using OpenTribes.Sim;

namespace OpenTribes.Tests;

public class SimTests
{
    [Fact]
    public void TestSim()
    {
        Fpu.TowardZero = false; // process start
        var strings = new StringTable();
        var interp = new Interpreter(strings);
        string output = "";
        interp.SetOutput(l => output += (output.Length == 0 ? "" : "\n") + l);
        var ctx = new ClassContext();
        var sim = new SimConsole(interp, ctx, strings);
        sim.RegisterCommands();
        interp.SetObjects(sim);
        string Run(string s)
        {
            output = "";
            interp.Evaluate(s);
            return output;
        }
        Run("newServer(); focusServer(); newObject(ConsoleScheduler, SimConsoleScheduler);");

        // ID2-ID4: pool starts at 8192; LIFO reuse; a free at the top trims the counter.
        Assert.True(Run("for (%i = 0; %i < 5; %i++) echo(newObject(\"P\" @ %i, SimSet));") == "8192\n8193\n8194\n8195\n8196");
        Run("deleteObject(P1); deleteObject(P4);");
        Assert.True(Run("echo(newObject(Q0, SimSet), \" \", newObject(Q1, SimSet), \" \", newObject(Q2, SimSet));") ==
                    "8193 8196 8197");

        // RD2: deleting a group frees children newest first, then the group.
        Run("$g = newObject(G, SimGroup); $c1 = newObject(C1, SimSet); $c2 = newObject(C2, SimSet); addToSet(G, C1, C2);");
        Assert.True(Run("echo($g, \" \", $c1, \" \", $c2);") == "8198 8199 8200");
        Run("deleteObject(G);");
        Assert.True(Run("echo(isObject(\"G/C1\"), isObject(C1));") == "FalseFalse");

        // Fixed ids (ID1) and the root path rules (3.2).
        Assert.True(Run("echo(isObject(43), isObject(ConsoleScheduler), isObject(0), isObject(NamedGuiSet), isObject(721));") ==
                    "TrueTrueTrueTrueTrue");

        // GS4 swap-remove: (n1, n2, n3, n4) minus n2 gives (n1, n4, n3).
        Run("newObject(S, SimSet); for (%i = 1; %i <= 4; %i++) { newObject(\"N\" @ %i, SimSet); addToSet(S, \"N\" @ %i); } deleteObject(N2);");
        Assert.True(Run("echo(getNextObject(S, N1) == nameToID(N4), getNextObject(S, N4) == nameToID(N3));") == "11");

        // EV2-EV8: schedule ordering, FIFO for equal times, 32 ms ticks, getSimTime inside events.
        Run("schedule(\"echo(A);\", 0.5); schedule(\"echo(B);\", 0.5); schedule(\"echo(E);\", 0.3); schedule(\"echo(N);\", -5);");
        output = "";
        sim.AdvanceRealTime(31);
        Assert.True(output.Length == 0);
        sim.AdvanceRealTime(1);
        Assert.True(output == "N");
        output = "";
        sim.AdvanceRealTime(500);
        Assert.True(output == "E\nA\nB");
        Assert.True(Run("echo(getSimTime());") == "0.511999"); // 16 ticks of 32 ms, printed truncated

        // EV7: deleting the target cancels its events.
        Run("newObject(T, SimSet); schedule(\"echo(never);\", 0.1, T); deleteObject(T);");
        output = "";
        sim.AdvanceRealTime(1000);
        Assert.True(output.Length == 0);
    }
}
