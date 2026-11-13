// Port of tests/test_timing.cpp: frame, tick and event timing (docs/sim.md 8.1-8.3: EV, TM, TK rules). The C++ main
// enters the script FPU mode first; every test here starts in it.
namespace OpenTribes.Tests;

using System.Globalization;
using OpenTribes.Core;
using OpenTribes.Script;
using OpenTribes.Sim;

public class TimingTests
{
    private sealed class Ticker : SimObject
    {
        public string TickerName { get; }
        public List<string>? Log { get; set; }
        public Action? OnTick { get; set; }
        public List<float> Timers { get; } = [];

        public Ticker(string n) => TickerName = n;
        public override string ClassName() => "Ticker";

        public override void ServerTick(uint ms)
        {
            Log!.Add(TickerName + "@" + ms.ToString(CultureInfo.InvariantCulture));
            OnTick?.Invoke();
        }

        public override void OnTimerEvent(float time, int id, float interval)
        {
            Timers.Add(time);
            Assert.True(id == 12 && interval == 0.03f);
        }
    }

    private sealed class Fx
    {
        public StringTable Strings { get; } = new();
        public Interpreter In { get; }
        public ClassContext Ctx { get; } = new();
        public SimConsole Sim { get; }
        public string Out { get; set; } = "";
        public double Clock { get; set; } // seconds
        public List<string> Ticks { get; } = [];

        public Fx()
        {
            In = new Interpreter(Strings);
            Sim = new SimConsole(In, Ctx, Strings);
            In.SetOutput(l => Out += (Out.Length == 0 ? "" : "\n") + l);
            Sim.RegisterCommands();
            In.SetObjects(Sim);
            Sim.SetClockSource(() => Clock);
            Run("newServer(); focusServer(); newObject(ConsoleScheduler, SimConsoleScheduler);");
        }

        public string Run(string s)
        {
            Out = "";
            In.Evaluate(s);
            return Out;
        }

        // One frame without network or frame end: clock to `seconds`, then the tick loop.
        public int Frame(double seconds)
        {
            Clock = seconds;
            Sim.UpdateClock();
            return Sim.AdvanceServer();
        }

        public SimManager M() => Sim.Server()!;

        public Ticker MakeTicker(string name, int set)
        {
            var t = new Ticker(name) { Log = Ticks };
            M().AddToRoot(t);
            M().FindById(set)!.AsSet()!.AddObject(t);
            return t;
        }
    }

    private static void Start() => Numbers.EnterScriptFpuMode();

    // TM5, TM6, EV8: queue time during dispatch, target reached after, equal target is a no-op.
    [Fact]
    public void TestAdvance()
    {
        Start();
        var strings = new StringTable();
        var m = new SimManager(strings);
        var log = new List<string>();
        Action Note(string tag) => () => log.Add(tag + Numbers.FormatFloat(m.Time()));
        m.PostEvent(m, 0.5f, Note("a"));
        m.PostEvent(m, 0.25f, Note("b"));
        m.PostEvent(m, 0.5f, () =>
        {
            Note("c")();
            m.PostEvent(m, m.Time(), Note("n"));                 // same time: after the queued equal ones
            m.PostEvent(m, Fpu.AddF(m.Time(), 1.0f), Note("l"));
        });
        m.PostEvent(m, 0.5f, Note("d"));
        Assert.True(m.AdvanceTo(0.125f) == 0 && m.Time() == 0.125f);
        Assert.True(m.AdvanceTo(0.75f) == 5 && m.Time() == 0.75f);
        Assert.True(log.SequenceEqual(new[] { "b0.25", "a0.5", "c0.5", "d0.5", "n0.5" }));
        // TM6: an event posted at or before the current target waits for a new target.
        m.PostEvent(m, 0.5f, Note("p"));
        Assert.True(m.AdvanceTo(0.75f) == 0 && log.Count == 5);
        Assert.True(m.AdvanceTo(1.0f) == 1 && log[^1] == "p0.75"); // queue time never goes back
        Assert.Equal(1, m.PendingEvents());
    }

    // TM3: rebase on the first event; nothing is dispatched by the reset itself; a reset inside an event continues the
    // same advance with target 0.
    [Fact]
    public void TestReset()
    {
        Start();
        var strings = new StringTable();
        var m = new SimManager(strings);
        var log = new List<string>();
        Action Note(string tag) => () => log.Add(tag + Numbers.FormatFloat(m.Time()));
        m.AdvanceTo(10.0f);
        m.PostEvent(m, 10.25f, Note("a"));
        m.PostEvent(m, 10.25f, Note("b"));
        m.PostEvent(m, 11.0f, Note("c"));
        m.ResetTime();
        Assert.True(log.Count == 0 && m.Time() == 0.0f && m.PendingEvents() == 3);
        Assert.Equal(0, m.AdvanceTo(0.0f)); // equal target: not even events at 0 run
        Assert.True(m.AdvanceTo(0.5f) == 2 && log.SequenceEqual(new[] { "a0", "b0" }));
        log.Clear();
        // Inside an event: "r" at 0.625 resets; "s" (0.625) becomes the first event at 0 and runs in the same advance;
        // "c" moves from 0.75 to 0.125 and waits.
        m.PostEvent(m, 0.625f, () =>
        {
            Note("r")();
            m.ResetTime();
            Note("R")();
        });
        m.PostEvent(m, 0.625f, Note("s"));
        Assert.Equal(2, m.AdvanceTo(2.0f));
        Assert.True(log.SequenceEqual(new[] { "r0.625", "R0", "s0" }));
        Assert.True(m.Time() == 0.0f && m.PendingEvents() == 1);
        Assert.True(m.AdvanceTo(0.25f) == 1 && log[^1] == "c0.125");
        // An empty queue: only the clocks reset.
        m.ResetTime();
        Assert.True(m.Time() == 0.0f && m.AdvanceTo(0.0f) == 0);
    }

    // RD1, EV7: unregistering the destination cancels engine events too.
    [Fact]
    public void TestCancel()
    {
        Start();
        var strings = new StringTable();
        var m = new SimManager(strings);
        var runs = 0;
        var s = new SimSet();
        m.AddToRoot(s);
        m.PostEvent(s, 0.5f, () => ++runs);
        m.PostEvent(m, 0.5f, () => ++runs);
        m.DeleteObject(s);
        Assert.True(m.PendingEvents() == 1 && m.AdvanceTo(1.0f) == 1 && runs == 1);
    }

    // TK1, TK2, TM2: 32 ms ticks from the scaled clock, catch-up without a limit, tick time as
    // float((tick - base) * 0.001), events before the object updates of the same tick.
    [Fact]
    public void TestTicks()
    {
        Start();
        var fx = new Fx();
        Assert.True(fx.Frame(0.031) == 0 && fx.Frame(0.0325) == 1 && fx.Sim.TickMs() == 32);
        Assert.Equal("0.0319999", fx.Run("echo(getSimTime());"));
        Assert.True(fx.Frame(0.0639) == 0 && fx.Frame(0.1) == 2 && fx.Sim.TickMs() == 96);
        // A stalled frame runs every missed tick: 3 s is 93 more ticks, each with its events.
        fx.Run("schedule(\"echo(a, getSimTime());\", 1); schedule(\"echo(b, getSimTime());\", 2.5);");
        fx.Out = "";
        Assert.True(fx.Frame(3.1) == 93 && fx.Sim.TickMs() == 3072);
        Assert.Equal("a1.09599\nb2.59599", fx.Out); // float(0.096 + delay), printed truncated (EV2)
        Assert.Equal("3.07199", fx.Run("echo(getSimTime());"));
        // The clock never moves the tick count back.
        Assert.True(fx.Frame(1.0) == 0 && fx.Sim.TickMs() == 3072);

        // TK3: per tick, events first, then sets 718, 719, 727 in that order.
        fx.MakeTicker("c", 727);
        fx.MakeTicker("b", 719);
        fx.MakeTicker("a", 718);
        fx.Run("schedule(\"echo(ev);\", 0);");
        fx.Out = "";
        fx.Ticks.Clear();
        var a2 = fx.MakeTicker("a2", 718);
        a2.OnTick = () => fx.Ticks.Add("out=" + fx.Out);
        Assert.Equal(3, fx.Frame(3.17));
        Assert.True(fx.Ticks.Count == 15 && fx.Ticks[0] == "a@3104" && fx.Ticks[1] == "a2@3104" && fx.Ticks[2] == "out=ev" &&
                    fx.Ticks[3] == "b@3104" && fx.Ticks[4] == "c@3104" && fx.Ticks[5] == "a@3136");
    }

    // TK4: index iteration over the live member list.
    [Fact]
    public void TestTickIteration()
    {
        Start();
        var fx = new Fx();
        var t1 = fx.MakeTicker("t1", 718);
        var t2 = fx.MakeTicker("t2", 718);
        var t3 = fx.MakeTicker("t3", 718);
        fx.MakeTicker("t4", 718);
        // A member added during the pass is ticked in the same pass.
        var added = false;
        t2.OnTick = () =>
        {
            if (!added)
            {
                added = true;
                fx.MakeTicker("new", 718);
            }
        };
        fx.Frame(0.033);
        Assert.True(fx.Ticks.SequenceEqual(new[] { "t1@32", "t2@32", "t3@32", "t4@32", "new@32" }));
        // Removing the current member moves the last one into its slot: that one is skipped in this pass (GS4), the
        // order changes for the next ones.
        var set = fx.M().FindById(718)!.AsSet()!;
        fx.Ticks.Clear();
        t2.OnTick = () => set.RemoveObject(t2);
        fx.Frame(0.065);
        Assert.True(fx.Ticks.SequenceEqual(new[] { "t1@64", "t2@64", "t3@64", "t4@64" }));
        fx.Ticks.Clear();
        fx.Frame(0.097);
        Assert.True(fx.Ticks.SequenceEqual(new[] { "t1@96", "new@96", "t3@96", "t4@96" }));
        // Removing an earlier member also skips the moved one.
        fx.Ticks.Clear();
        t3.OnTick = () => set.RemoveObject(t1);
        fx.Frame(0.129);
        Assert.True(fx.Ticks.SequenceEqual(new[] { "t1@128", "new@128", "t3@128" }));
        fx.Ticks.Clear();
        fx.Frame(0.161);
        Assert.True(fx.Ticks.SequenceEqual(new[] { "t4@160", "new@160", "t3@160" }));
    }

    // TK6: the SimTimerSet event every 0.03 s of sim time, times accumulated in float.
    [Fact]
    public void TestTimerSet()
    {
        Start();
        var fx = new Fx();
        Assert.Equal("   720: SensorManager ", fx.Run("listObjects(12);"));
        var t = new Ticker("timer") { Log = fx.Ticks };
        fx.M().AddToRoot(t);
        fx.M().FindById(12)!.AsSet()!.AddObject(t);
        fx.Frame(0.2); // ticks up to 0.192: timer events at 0.03 .. 0.18
        Assert.Equal(6, t.Timers.Count);
        float expect = 0, step = 0.03f; // run-time float sums in round-toward-zero mode
        foreach (var v in t.Timers)
        {
            expect = Fpu.AddF(expect, step);
            Assert.True(v == expect);
        }
        // It is the first queued event for a reset between frames: its 0.21 becomes 0 (TM3).
        fx.Run("schedule(\"echo(x, getSimTime());\", 0.5); resetSimTime();");
        fx.Out = "";
        fx.Frame(0.8);
        Assert.True(t.Timers[6] == 0.0f && t.Timers[7] == 0.03f);
        Assert.Equal("x0.482", fx.Out); // 0.192 + 0.5 - 0.21
    }

    // TM4: the scale applies from the assignment on, the clock stays continuous, minimum 0.0001.
    [Fact]
    public void TestTimeScale()
    {
        Start();
        var fx = new Fx();
        Assert.Equal("1.0", fx.Run("echo($SimGame::TimeScale);"));
        fx.Frame(1.0);
        Assert.Equal(992u, fx.Sim.TickMs());
        fx.Clock = 1.5;
        fx.Run("$SimGame::TimeScale = 2;");
        fx.Frame(2.5); // 1.5 + 2 * 1.0
        Assert.True(fx.Sim.ClockSeconds() == 3.5 && fx.Sim.TickMs() == 3488);
        Assert.Equal("0", fx.Run("$SimGame::TimeScale = 0; echo($SimGame::TimeScale);"));
        fx.Frame(1002.5); // 1000 s at 0.0001
        Assert.Equal(3584u, fx.Sim.TickMs());
    }

    // TK8: a new server starts its tick clock at the scaled clock; sim time restarts at 0.
    [Fact]
    public void TestNewServer()
    {
        Start();
        var fx = new Fx();
        fx.Frame(5.0);
        fx.Run("newServer(); focusServer();");
        Assert.True(fx.Sim.TickMs() == 4992 && fx.Run("echo(getSimTime());") == "0");
        Assert.True(fx.Frame(5.03) == 1 && fx.Run("echo(getSimTime());") == "0.0319999");
        // resetSimTime (TM3): base = the current tick.
        fx.Frame(6.0);
        fx.Run("resetSimTime();");
        Assert.Equal("0", fx.Run("echo(getSimTime());"));
        fx.Frame(6.04);
        Assert.Equal("0.0319999", fx.Run("echo(getSimTime());"));
    }

    // TK9: Game::EndFrame runs every frame; an undefined one prints the unknown-command line.
    [Fact]
    public void TestEndFrame()
    {
        Start();
        var fx = new Fx
        {
            Out = ""
        };
        fx.Sim.EndFrame();
        Assert.Equal("Game::EndFrame: Unknown command.", fx.Out);
        fx.Run("function Game::EndFrame() { $n++; }");
        for (var i = 0; i < 40; ++i)
        {
            fx.Frame(Fpu.Mul(0.001, i));
            fx.Clock = Fpu.Add(fx.Clock, 0.0005);
            fx.Sim.EndFrame();
        }
        Assert.Equal("40", fx.Run("echo($n);"));
        Assert.Equal("11", fx.Run("echo($ConsoleWorld::msecsPerFrame != \"\", $ConsoleWorld::FrameRate != \"\");"));
    }
}
