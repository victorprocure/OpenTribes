// Tests of the client input path (ROADMAP M9 C8, docs/client-scripts.md 3.4 to 3.9, docs/client-app.md 3.4): scan code
// table, modifier tracking, held keys, binding match, value shaping, push and pop. Synthetic maps only (R5).
using OpenTribes.Core;
using OpenTribes.Script;

namespace OpenTribes.Tests;

public sealed class ClientInputTests
{
    private sealed class Sink : IActionConsumer
    {
        public readonly List<(int Action, int Kind, float Value)> Got = new();
        public bool Removing { get; set; }
        public bool Result = true;
        public bool OnAction(int action, int kind, float value)
        {
            Got.Add((action, kind, value));
            return Result;
        }
    }

    private static (Interpreter I, ClientState S) Setup()
    {
        var i = new Interpreter();
        ClientState s = Builtins.RegisterBuiltins(i, null, ".");
        i.SetOutput(_ => { });
        // tags used by the binds: IDACTION_x = 220000 + n
        i.Evaluate("$A = 1;");
        return (i, s);
    }

    private static InputEvent Key(int scan, bool make) => new() { DeviceType = 3, DeviceInst = 0, ObjType = 10, ObjInst = (byte)scan, Action = (byte)(make ? 1 : 2) };

    [Theory]
    [InlineData(41, 0x01)]   // escape
    [InlineData(4, 0x1e)]    // A
    [InlineData(26, 0x11)]   // W
    [InlineData(22, 0x1f)]   // S
    [InlineData(7, 0x20)]    // D
    [InlineData(29, 0x2c)]   // Z
    [InlineData(30, 0x02)]   // 1
    [InlineData(39, 0x0b)]   // 0
    [InlineData(44, 0x39)]   // space
    [InlineData(58, 0x3b)]   // F1
    [InlineData(69, 0x58)]   // F12
    [InlineData(82, 0xc8)]   // up
    [InlineData(81, 0xd0)]   // down
    [InlineData(75, 0xc9)]   // prior
    [InlineData(78, 0xd1)]   // next
    [InlineData(70, 0xb7)]   // sysreq
    [InlineData(225, 0x2a)]  // left shift
    [InlineData(229, 0x36)]  // right shift
    [InlineData(224, 0x1d)]  // left control
    [InlineData(228, 0x9d)]  // right control
    [InlineData(226, 0x38)]  // left alt
    [InlineData(230, 0xb8)]  // right alt
    [InlineData(93, 0x4c)]   // keypad 5 (docs/client-app.md 3.4: 0x4b..0x4d is keypad 4 5 6)
    [InlineData(98, 0x52)]   // keypad 0
    [InlineData(88, 0x9c)]   // keypad enter
    [InlineData(72, 0)]      // pause: no code
    public void ScanCodesFollowTheKeyTable(int sdl, int code)
    {
        // client-app.md 3.4 table; Q1 of client-scripts.md 7.
        Assert.Equal(code, ScanCodeMap.FromSdl(sdl));
    }

    [Fact]
    public void EveryMappedCodeIsInTheKeyTable()
    {
        // client-scripts.md 3.4: the table has an entry for every code a mapped key produces.
        var codes = new HashSet<int>(ClientKeyTable.Keys.Select(k => (int)k.Code));
        for (int sdl = 0; sdl < 512; ++sdl)
        {
            int c = ScanCodeMap.FromSdl(sdl);
            if (c != 0)
                Assert.Contains(c, codes);
        }
    }

    [Fact]
    public void LettersMapToDistinctCodes()
    {
        var seen = new HashSet<int>();
        for (int sdl = 4; sdl <= 29; ++sdl)
            Assert.True(seen.Add(ScanCodeMap.FromSdl(sdl)));
    }

    [Fact]
    public void CharacterIsShiftedWithEitherShiftBit()
    {
        // 3.4: shifted entry when either shift bit is set, else unshifted; caps lock plays no part.
        Assert.Equal('w', InputDispatcher.CharacterFor(0x11, 0));
        Assert.Equal('W', InputDispatcher.CharacterFor(0x11, 1));
        Assert.Equal('W', InputDispatcher.CharacterFor(0x11, 2));
        Assert.Equal('w', InputDispatcher.CharacterFor(0x11, 4));
        Assert.Equal('!', InputDispatcher.CharacterFor(0x02, 3));
        Assert.Equal('\0', InputDispatcher.CharacterFor(0xde, 0));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(1, 3, true)]
    [InlineData(1, 2, false)]
    [InlineData(2, 2, true)]
    [InlineData(2, 3, true)]
    [InlineData(2, 1, false)]
    [InlineData(3, 1, true)]
    [InlineData(3, 2, true)]
    [InlineData(3, 3, true)]
    [InlineData(3, 0, false)]
    public void ModifierGroupTable(int binding, int ev, bool match)
    {
        // 3.6a: the 4 by 4 table of 0063352c.
        Assert.Equal(match, InputDispatcher.GroupMatches(binding, ev));
    }

    [Fact]
    public void BindingWithoutModifierDoesNotMatchShiftedEventUntilSecondPass()
    {
        var (i, s) = Setup();
        i.Evaluate("newActionMap(\"t.sae\"); bindAction(keyboard, make, w, TO, 220010, 1.0);");
        var sink = new Sink();
        var d = new InputDispatcher(s);
        d.Push(s.FindMap("t.sae")!, sink);
        // shift down, then w: no shift-w binding, so the retry without modifiers finds the plain w (3.6 step 6).
        d.Process(Key(0x2a, true), 1);
        Assert.True(d.Process(Key(0x11, true), 1));
        Assert.Single(sink.Got);
        Assert.Equal(220010, sink.Got[0].Action);
        Assert.Equal(2, sink.Got[0].Kind);
        Assert.Equal(1f, sink.Got[0].Value);
    }

    [Fact]
    public void ModifiedBindingBeatsPlainOneOnlyWhenModifierHeld()
    {
        var (i, s) = Setup();
        i.Evaluate("newActionMap(\"t.sae\"); bindCommand(keyboard, make, control, y, TO, \"$Hit = 1;\"); bindCommand(keyboard, make, y, TO, \"$Hit = 2;\");");
        var d = new InputDispatcher(s) { Eval = t => i.Evaluate(t) };
        d.Push(s.FindMap("t.sae")!, new Sink());
        d.Process(Key(0x15, true), 1);
        Assert.Equal("2", i.PeekVariable("$Hit"));
        d.Process(Key(0x15, false), 0);
        d.Process(Key(0x1d, true), 1);
        d.Process(Key(0x15, true), 1);
        Assert.Equal("1", i.PeekVariable("$Hit"));
        Assert.Equal(4, d.Modifiers);
        d.Process(Key(0x1d, false), 0);
        Assert.Equal(0, d.Modifiers);
    }

    [Fact]
    public void BreakWithoutHeldMakeIsDropped()
    {
        // 3.6 step 4.
        var (i, s) = Setup();
        i.Evaluate("newActionMap(\"t.sae\"); bindAction(keyboard, break, w, TO, 220010, 0.0);");
        var sink = new Sink();
        var d = new InputDispatcher(s);
        d.Push(s.FindMap("t.sae")!, sink);
        Assert.False(d.Process(Key(0x11, false), 0));
        Assert.Empty(sink.Got);
    }

    [Fact]
    public void BreakTakesTheModifiersOfItsMake()
    {
        // 3.6 step 4: shift down, ctrl-less make of w with shift; releasing shift first, the break of w still carries shift.
        var (i, s) = Setup();
        i.Evaluate("newActionMap(\"t.sae\"); bindAction(keyboard, make, shift, w, TO, 220010, 1.0); bindAction(keyboard, break, shift, w, TO, 220010, 0.0);");
        var sink = new Sink();
        var d = new InputDispatcher(s);
        d.Push(s.FindMap("t.sae")!, sink);
        d.Process(Key(0x2a, true), 1);
        d.Process(Key(0x11, true), 1);
        d.Process(Key(0x2a, false), 0);
        d.Process(Key(0x11, false), 0);
        Assert.Equal(2, sink.Got.Count);
        Assert.Equal(0f, sink.Got[1].Value);
    }

    [Fact]
    public void GuiGetsTheEventFirstButNotTheBreakOfAHeldKey()
    {
        // 3.6 step 3.
        var (i, s) = Setup();
        i.Evaluate("newActionMap(\"t.sae\"); bindAction(keyboard, make, w, TO, 220010, 1.0); bindAction(keyboard, break, w, TO, 220010, 0.0);");
        var sink = new Sink();
        var d = new InputDispatcher(s);
        d.Push(s.FindMap("t.sae")!, sink);
        int offered = 0;
        bool swallow = false;
        d.OfferToGui = (ev, v, ch) => { ++offered; return swallow; };
        d.Process(Key(0x11, true), 1);
        Assert.Equal(1, offered);
        Assert.Single(sink.Got);
        swallow = true;
        d.Process(Key(0x11, false), 0);   // held make: straight to the maps, the GUI is not asked
        Assert.Equal(1, offered);
        Assert.Equal(2, sink.Got.Count);
        d.Process(Key(0x20, true), 1);    // not held: offered, swallowed
        Assert.Equal(2, offered);
        Assert.Equal(2, sink.Got.Count);
    }

    [Fact]
    public void MouseAxisGetsKind4AndShaping()
    {
        // 3.6 step 5 kind; 3.7 shaping: Flip then Scale.
        var (i, s) = Setup();
        i.Evaluate("newActionMap(\"t.sae\"); bindAction(mouse0, xaxis0, TO, 220004, Flip, Scale, 0.5);");
        var sink = new Sink();
        var d = new InputDispatcher(s);
        d.Push(s.FindMap("t.sae")!, sink);
        d.Process(new InputEvent { DeviceType = 2, ObjType = 11, ObjInst = 0, Action = 3 }, 10f);
        Assert.Equal((220004, 4, -5f), sink.Got[0]);
    }

    [Theory]
    [InlineData(0x2a)]  // shift
    [InlineData(0x1d)]  // control
    [InlineData(0x38)]  // alt
    public void MouseAxisIgnoresHeldModifiers(int modifierScan)
    {
        // 00508854: modifiers are compared for keyboard bindings only, so the unmodified axis binding still gets the event.
        var (i, s) = Setup();
        i.Evaluate("newActionMap(\"t.sae\"); bindAction(mouse0, xaxis0, TO, 220004, Scale, 0.5);");
        var sink = new Sink();
        var d = new InputDispatcher(s);
        d.Push(s.FindMap("t.sae")!, sink);
        d.Process(Key(modifierScan, true), 1);
        Assert.True(d.Process(new InputEvent { DeviceType = 2, ObjType = 11, ObjInst = 0, Action = 3 }, 3f));
        Assert.Equal((220004, 4, 1.5f), sink.Got[0]);
    }

    [Fact]
    public void MouseBindingWithModifierStillMatchesWithoutIt()
    {
        // 00508854 ignores the binding's modifier groups for a mouse device, in either direction.
        var (i, s) = Setup();
        i.Evaluate("newActionMap(\"t.sae\"); bindAction(mouse0, shift, xaxis0, TO, 220004, 1.0);");
        var sink = new Sink();
        var d = new InputDispatcher(s);
        d.Push(s.FindMap("t.sae")!, sink);
        Assert.True(d.Process(new InputEvent { DeviceType = 2, ObjType = 11, ObjInst = 0, Action = 3 }, 1f));
        Assert.Single(sink.Got);
    }

    [Fact]
    public void ShapeValueOrder()
    {
        // 3.7: Center, DeadZone, Flip, Square, Cube, Scale, in that order; Value replaces all.
        var b = new ActionBinding { Flags = ActionBinding.DeadZone | ActionBinding.Scale, DeadZoneF = 0.2f, ScaleF = 10f };
        Assert.Equal(0f, InputDispatcher.ShapeValue(b, 0.1f, false));
        Assert.Equal(5f, InputDispatcher.ShapeValue(b, 0.6f, false), 3);
        Assert.Equal(-5f, InputDispatcher.ShapeValue(b, -0.6f, false), 3);
        b = new ActionBinding { Flags = ActionBinding.Center };
        Assert.Equal(-1f, InputDispatcher.ShapeValue(b, 1f, false));
        Assert.Equal(1f, InputDispatcher.ShapeValue(b, 0f, false));
        b = new ActionBinding { Flags = ActionBinding.Square };
        Assert.Equal(-4f, InputDispatcher.ShapeValue(b, -2f, false));
        b = new ActionBinding { Flags = ActionBinding.Value | ActionBinding.Scale, ValueF = 0.25f, ScaleF = 9f };
        Assert.Equal(0.25f, InputDispatcher.ShapeValue(b, 7f, false));
        b = new ActionBinding { Flags = ActionBinding.Flip };
        Assert.Equal(0.75f, InputDispatcher.ShapeValue(b, 0.25f, false));
        Assert.Equal(-0.25f, InputDispatcher.ShapeValue(b, 0.25f, true));
    }

    [Fact]
    public void FalseReturnGoesOnWithTheNextPair()
    {
        // 3.6 step 5: a consumer that returns false lets the next pushed map see the event; the newest map is first.
        var (i, s) = Setup();
        i.Evaluate("newActionMap(\"a.sae\"); bindAction(keyboard, make, w, TO, 1, 1.0); newActionMap(\"b.sae\"); bindAction(keyboard, make, w, TO, 2, 1.0);");
        var low = new Sink();
        var top = new Sink { Result = false };
        var d = new InputDispatcher(s);
        d.Push(s.FindMap("a.sae")!, low);
        d.Push(s.FindMap("b.sae")!, top);
        d.Process(Key(0x11, true), 1);
        Assert.Single(top.Got);
        Assert.Single(low.Got);
        Assert.Equal(1, low.Got[0].Action);
    }

    [Fact]
    public void PushOfAMapBreaksHeldKeysItBinds()
    {
        // 3.8 ActionMap_push step 2.
        var (i, s) = Setup();
        i.Evaluate("newActionMap(\"a.sae\"); bindAction(keyboard, make, w, TO, 10, 1.0); bindAction(keyboard, break, w, TO, 10, 0.0);" +
                   "newActionMap(\"b.sae\"); bindAction(keyboard, break, w, TO, 11, 0.0);");
        var low = new Sink();
        var top = new Sink();
        var d = new InputDispatcher(s);
        d.Push(s.FindMap("a.sae")!, low);
        d.Process(Key(0x11, true), 1);
        Assert.Single(low.Got);
        d.Push(s.FindMap("b.sae")!, top);
        Assert.Equal(2, low.Got.Count);       // the synthesised break reached the old map
        Assert.Equal(0f, low.Got[1].Value);
        Assert.Equal(2, d.StackDepth);
        d.Pop(s.FindMap("b.sae")!, top);
        Assert.Equal(1, d.StackDepth);
    }

    [Fact]
    public void ModifierAloneBindingMatchesEitherKey()
    {
        // 3.6a last sentence.
        var (i, s) = Setup();
        i.Evaluate("newActionMap(\"t.sae\"); bindCommand(keyboard, make, shift, TO, \"$S = 1;\");");
        var d = new InputDispatcher(s) { Eval = t => i.Evaluate(t) };
        d.Push(s.FindMap("t.sae")!, new Sink());
        d.Process(Key(0x36, true), 1);
        Assert.Equal("1", i.PeekVariable("$S"));
    }

    [Fact]
    public void DefaultBindsStayInSimGameMap()
    {
        // 3.9: `bind` always writes SimGame.sae.
        var (i, s) = Setup();
        i.Evaluate("newActionMap(\"other.sae\"); bind(keyboard, make, control, o, to, \"$O = 1;\");");
        Assert.Single(s.FindMap("SimGame.sae")!.Bindings);
        Assert.Empty(s.FindMap("other.sae")!.Bindings);
    }

    [Fact]
    public void GlobalMapBindingEndsTheSearchBeforeTheUnmodifiedRetry()
    {
        // 3.6 step 5 and 6: a SimGame `bind` of control-o handles the key, so the plain `o` binding of another map does not run.
        var (i, s) = Setup();
        i.Evaluate("newActionMap(\"t.sae\"); bindCommand(keyboard, make, o, TO, \"$PLAIN = 1;\");" +
                   "bind(keyboard, make, control, o, to, \"$GLOBAL = 1;\");");
        var d = new InputDispatcher(s) { Eval = t => i.Evaluate(t), GlobalMap = s.FindMap("SimGame.sae") };
        d.Push(s.FindMap("t.sae")!, new Sink());
        d.Process(Key(0x1d, true), 1);
        d.Process(Key(0x18, true), 1);   // o with control held
        Assert.Equal("1", i.PeekVariable("$GLOBAL"));
        Assert.NotEqual("1", i.PeekVariable("$PLAIN"));
    }
}
