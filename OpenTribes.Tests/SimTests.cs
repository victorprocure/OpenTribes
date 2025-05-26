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

        // GS4 ordered flag (docs/sim.md GS4): set 25 (SimInputConsumerSet) keeps the order on removal. N2 is gone, so
        // the set holds (N1, N3, N4) behind the action handler and deleting N1 leaves (N3, N4), not (N4, N3).
        Run("addToSet(25, N1, N3, N4); deleteObject(N1);");
        Assert.True(Run("echo(getNextObject(25, N3) == nameToID(N4), getNextObject(25, N4) == nameToID(N3));") == "10");
        Run("deleteObject(N3); deleteObject(N4);");

        // sim.md Q2: listVolumes prints the name and the file of every member of object 2; a member that is no
        // SimVolume has a null file pointer.
        Assert.True(Run("listVolumes();") == "");
        // [live] a created ChatMenu joins set 25 and leaves on delete.
        Run("newObject(M1, ChatMenu, x);");
        Assert.Contains("ChatMenu", Run("listObjects(25);"));
        Run("deleteObject(M1);");
        Assert.True(Run("listObjects(25);") == "   1: SimActionHandler ");
        Run("addToSet(2, S);");
        Assert.True(Run("listVolumes();") == "   \"S\": \"(null)\"");
        Run("removeFromSet(2, S);");

        // sim.md Q5: GUI controls are groups, a scroll control creates its content control, a delegate takes no
        // arguments, the canvas needs one, ServerListCtrl prints a line when registered.
        Run("newObject(GC1, FearGui::FGTab);");
        Assert.True(Run("listObjects(GC1);") == ""); // a set without members
        Assert.Contains("FearGui::FGTab (g)", Run("listObjects();"));
        Assert.Contains("SimGui::ScrollContentCtrl (g)", Run("newObject(GC2, SimGui::ScrollCtrl); listObjects(GC2);"));
        Assert.True(Run("echo(newObject(GC3, SimGui::quitDelegate, x));") ==
                    "newObject for: SimGui::quitDelegate failed arg list: x\n0");
        Assert.True(Run("echo(newObject(GC4, SimGui::Canvas));") ==
                    "SimCanvas: title width height quit(t/f)\nnewObject for: SimGui::Canvas failed arg list: \n0");
        Assert.True(Run("newObject(GC5, SimGui::Canvas, t); listObjects(GC5);") == "listObjects: Object is not a set");
        Assert.True(Run("newObject(GC6, FearGui::ServerListCtrl);") == "ServerListCtrl::onAdd() mbQueryMoreServers: FALSE");
        Run("deleteObject(GC1); deleteObject(GC2); deleteObject(GC5); deleteObject(GC6);");

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
