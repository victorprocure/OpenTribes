// Tests of OpenTribes.Gui (ROADMAP M9 C6, docs/gui.md): synthetic `.gui` bytes only (R5), no game data.
namespace OpenTribes.Tests;

using System.Text;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Gl;
using OpenTribes.Script;
using Xunit.Abstractions;

// Builds persistent blocks the way docs/gui.md 1.2 and 1.3 describe them.
internal sealed class GuiW : IDisposable
{
    public MemoryStream M { get; } = new();
    public GuiW U8(int v) { M.WriteByte((byte)v); return this; }
    public GuiW U32(uint v) { M.Write(BitConverter.GetBytes(v)); return this; }
    public GuiW Pstr(string s) { M.WriteByte((byte)s.Length); M.Write(Encoding.Latin1.GetBytes(s)); return this; }
    public GuiW Buf81(string s)
    {
        var b = new byte[81];
        Encoding.Latin1.GetBytes(s).CopyTo(b, 0);
        M.Write(b);
        return this;
    }
    public GuiW Raw(byte[] b) { M.Write(b); return this; }
    public byte[] ToArray() => M.ToArray();

    // The 1.3 control body (version 4) followed by `kids` and their names.
    public static GuiW Control(string cmd = "", int x = 0, int y = 0, int w = 50, int h = 50, uint flags = 3, uint id = 0, uint hs = 0, uint vs = 0,
        string var = "", byte opaque = 0, byte fill = 0, params (byte[] Block, string Name)[] kids)
    {
        var c = new GuiW().U8(4).U32(0).U8(opaque).U8(fill).U8(0).U8(0).U8(0).U8(0).U8(0).U8(0).Pstr(cmd).Pstr("");
        c.U32((uint)x).U32((uint)y).U32((uint)w).U32((uint)h).U32(flags).U32(id).U32(hs).U32(vs).U32(0).Buf81(var).U32((uint)kids.Length);
        foreach (var k in kids)
            c.Raw(k.Block);
        foreach (var k in kids)
            c.Pstr(k.Name);
        return c;
    }

    // A block: tag, size (everything after the size field), version 0, body, padded to even.
    public static byte[] Block(string tag, GuiW body)
    {
        var b = body.ToArray();
        var m = new GuiW().Raw(Encoding.Latin1.GetBytes(tag)).U32((uint)(b.Length + 4)).U32(0).Raw(b);
        if ((b.Length & 1) != 0)
            m.U8(0);
        return m.ToArray();
    }

    /// <inheritdoc/>
    public void Dispose() => M.Dispose();
}

internal sealed class GuiFixture
{
    public Interpreter Interp { get; } = new();
    public List<string> Output { get; } = [];
    public GuiHost Host { get; }
    public GuiCanvas Canvas { get; }
    public GuiRuntime Runtime { get; }
    public long Clock { get; set; } = 1000;

    public GuiFixture(int w = 640, int h = 480)
    {
        Interp.SetOutput(Output.Add);
        Host = new GuiHost(Interp) { Now = () => Clock, Log = Output.Add };
        Canvas = new GuiCanvas(Host, w, h);
        Runtime = new GuiRuntime(Interp, Host, Canvas);
    }

    public static GuiControl Make(string cls, int x = 0, int y = 0, int w = 50, int h = 50, params (string, object)[] fields)
    {
        var c = GuiFactory.Create(GuiClassTable.ByName(cls)!);
        c.X = x;
        c.Y = y;
        c.W = w;
        c.H = h;
        foreach (var (k, v) in fields)
            c.F[k] = v;
        return c;
    }

    public GuiControl Content(GuiControl root, GuiScreenResponder? del = null)
    {
        Canvas.SetContent(root, del);
        Canvas.Frame();
        return root;
    }

    public void Frame(int ms = 16)
    {
        Clock += ms;
        Canvas.Frame();
    }

    public void Move(int x, int y)
    {
        int dx = x - (int)Canvas.CursorX, dy = y - (int)Canvas.CursorY;
        if (dx != 0)
            Canvas.HandleInput(new RawInput { Device = RawInput.DevMouse, ObjType = RawInput.ObjXAxis, Value = dx });
        if (dy != 0)
            Canvas.HandleInput(new RawInput { Device = RawInput.DevMouse, ObjType = RawInput.ObjYAxis, Value = dy });
    }

    public void Button(bool down, int index = 0) =>
        Canvas.HandleInput(new RawInput { Device = RawInput.DevMouse, ObjType = RawInput.ObjButton, Index = (byte)index, Action = (byte)(down ? 1 : 0) });

    public void Click(int x, int y)
    {
        Move(x, y);
        Button(true);
        Frame();
        Button(false);
        Frame();
    }

    public bool Key(string name, bool make, byte mod = 0, char ascii = '\0')
    {
        var scan = GuiCanvas.ScanForName(name);
        if (ascii == '\0' && name.Length == 1)
            ascii = name[0];
        return Canvas.HandleInput(new RawInput { Device = RawInput.DevKeyboard, Index = (byte)scan, Action = (byte)(make ? 1 : 0), Ascii = ascii, Mod = mod });
    }

    public void Tap(string name, byte mod = 0, char ascii = '\0')
    {
        Key(name, true, mod, ascii);
        Key(name, false, mod, ascii);
    }

    public void Type(string text)
    {
        foreach (var ch in text)
            Tap(ch == ' ' ? "space" : ch.ToString(), 0, ch);
    }
}

public class GuiTests
{
    private readonly ITestOutputHelper _out;
    public GuiTests(ITestOutputHelper output) => _out = output;

    // ---- docs/gui.md 1: loading ----

    [Fact]
    public void ControlBodyFieldsAndChildNames() // 1.2, 1.3
    {
        var child = GuiW.Block("SGct", GuiW.Control(x: 4, y: 5, w: 10, h: 11, id: 77));
        var root = GuiW.Block("SGct", GuiW.Control("cmd();", 1, 2, 300, 200, 3, 9, 1, 2, "$var", 1, 7, (child, "Kid")));
        var c = GuiLoader.Load(root, out var err);
        Assert.True(c != null, err);
        Assert.Equal("SimGui::Control", c.ClassName);
        Assert.Equal((1, 2, 300, 200), (c.X, c.Y, c.W, c.H));
        Assert.Equal("cmd();", c.Command);
        Assert.Equal("$var", c.ConsoleVar);
        Assert.Equal(((uint)9, (uint)1, (uint)2, (byte)1, (byte)7), (c.Id, c.HSizing, c.VSizing, c.Opaque, c.FillColor));
        var kid = Assert.Single(c.Children);
        Assert.Equal(("Kid", (uint)77), (kid.Name, kid.Id));
        Assert.Same(c, kid.Parent);
        Assert.Same(kid, c.FindByName("kid"));
        Assert.Same(kid, c.FindById(77));
    }

    [Fact]
    public void FieldTypesOfSimpleText() // 1.4 routine 005406bc: u32 tags, buf81, u32 alignment, then Active (u8, u32) and the control
    {
        var body = new GuiW().U32(1).U32(2).U32(150003).U32(150001).U32(150002).U32(160009).Buf81("Hello").U32(2).U32(3)
            .U8(1).U32(100100).Raw(GuiW.Control(w: 80, h: 16).ToArray());
        var c = GuiLoader.Load(GuiW.Block("SGst", body), out var err);
        Assert.True(c != null, err);
        Assert.IsType<GuiSimpleText>(c);
        Assert.Equal(((uint)150003, (uint)150001, (uint)150002, (uint)160009), (c.U("disabledFontTag"), c.U("fontTag"), c.U("highlightFontTag"), c.U("textTag")));
        Assert.Equal("Hello", c.S("text"));
        Assert.Equal(((uint)2, (uint)3, (uint)100100, (uint)1), (c.U("alignment"), c.U("textVOffset"), c.U("messageTag"), c.U("active")));
    }

    [Fact]
    public void EveryClassOfTable31RoundTripsItsPersistentFields() // 3.1: all 140 rows, 1.4 loaders and the writer of D44
    {
        var loadable = 0;
        foreach (var row in GuiClassTable.Rows.Where(GuiClassTable.IsControl))
        {
            var c = GuiFactory.Create(row);
            c.W = 33;
            c.H = 22;
            c.Command = "x();";
            GuiLoader.Routines.TryGetValue(GuiLoader.RoutineFor(row) ?? "", out var ops);
            // Give every field of the routine a value.
            void Fill(string routine)
            {
                if (!GuiLoader.Routines.TryGetValue(routine, out var o))
                    return;
                foreach (var op in o)
                {
                    if (op[0] == '@')
                    { Fill(op[1..]); continue; }
                    var p = op.Split(' ');
                    if (p[0] is "u8")
                        c.F[p[1]] = (byte)1;
                    else if (p[0] is "u32" or "i32")
                        c.F[p[1]] = (uint)7;
                    else if (p[0] is "pstr" or "buf81")
                        c.F[p[1]] = "s" + p[1];
                }
            }
            if (GuiLoader.RoutineFor(row) is string r)
                Fill(r);
            if (GuiLoader.RoutineFor(row) is "UniversalButton")
            {
                c.F["bodyVersion"] = (uint)3;
                c.F["radioId"] = (uint)5;
                c.F["bitmapRoot"] = "BTN_X";
                c.F["lowResX"] = (uint)1;
                c.F["lowResY"] = (uint)2;
                c.F["on"] = (uint)1;
                c.F["isSwitch"] = (uint)1;
                c.F["mirrorVar"] = (uint)0;
            }
            if (row.Name is "FearGui::FGShellBorder" or "FearGui::MMShellBorder")
                c.F["bitmapName"] = "Background0.bmp";
            if (GuiLoader.RoutineFor(row) is "Smacker")
                foreach (var k in new[] { "movieTag", "movieDoneTag", "palStart", "palColors" })
                    c.F[k] = (uint)3;
            if (GuiLoader.RoutineFor(row) is "Smacker")
                foreach (var k in new[] { "loop", "preload", "stretch" })
                    c.F[k] = (byte)1;
            c.PersistVersion = 1;
            var bytes = GuiWriter.Write(c);
            var back = GuiLoader.Load(bytes, out var err);
            Assert.True(back != null, row.Name + ": " + err);
            Assert.Equal(row.Name, back.ClassName);
            Assert.Equal((33, 22, "x();"), (back.W, back.H, back.Command));
            foreach (var kv in c.F)
                if (back.F.TryGetValue(kv.Key, out var v))
                    Assert.True(Convert.ToString(kv.Value) == Convert.ToString(v), $"{row.Name}.{kv.Key}: {kv.Value} vs {v}");
            ++loadable;
        }
        Assert.Equal(129, loadable);   // 140 rows less the canvas and the ten delegates, which are not controls
    }

    [Fact]
    public void UnknownTagAndShortBodyFail() // 1.1 failure, 1.6 (the 14 legacy files fail the same way)
    {
        Assert.Null(GuiLoader.Load(GuiW.Block("FGbt", GuiW.Control()), out var e1));
        Assert.Contains("unknown class tag FGbt", e1);
        var good = GuiW.Block("SGct", GuiW.Control());
        // A truncated block, and a body that does not end where its size says (an older layout).
        Assert.Null(GuiLoader.Load(good[..(good.Length - 6)], out _));
        var extra = GuiW.Block("SGct", GuiW.Control().U8(1).U8(2));
        Assert.Null(GuiLoader.Load(extra, out var e2));
        Assert.Contains("read", e2);
    }

    [Fact]
    public void TrailingBytesAfterTheRootBlockAreIgnored() // 1.2
    {
        var root = GuiW.Block("SGct", GuiW.Control(w: 12));
        var file = root.Concat(new byte[] { 1, 2, 3, 4, 5 }).ToArray();
        var c = GuiLoader.Load(file, out var err);
        Assert.True(c != null, err);
        Assert.Equal(12, c.W);
    }

    [Fact]
    public void PersBlockLoadsHudClass() // 1.2 PERS, 1.4 004afef4 (HealthHud)
    {
        var name = "FearGui::HealthHud";
        var body = new GuiW().U32(0).U32(0).U32(131).U32(35).U32(1).Raw(GuiW.Control(w: 131, h: 35, id: 140104).ToArray());
        var blk = new GuiW().Raw(Encoding.Latin1.GetBytes("PERS"));
        var b = body.ToArray();
        var head = 2 + ((name.Length + 1) & ~1);
        blk.U32((uint)(head + 4 + b.Length)).Raw(BitConverter.GetBytes((ushort)name.Length)).Raw(Encoding.Latin1.GetBytes(name));
        if ((name.Length & 1) != 0)
            blk.U8(0);
        blk.U32(0).Raw(b);
        var c = GuiLoader.Load(blk.ToArray(), out var err);
        Assert.True(c != null, err);
        Assert.IsType<GuiHealthHud>(c);
        Assert.Equal(140104u, c.Id);
    }

    // ---- 2.4 resize ----

    [Theory]
    [InlineData(0u, 10, 20, 50, 30)]      // none
    [InlineData(1u, 10, 20, 150, 30)]     // width += dx
    [InlineData(2u, 110, 20, 50, 30)]     // x += dx
    [InlineData(3u, 225, 20, 50, 30)]     // x = (new width - w) / 2: (500 - 50) / 2
    public void HorizontalSizing(uint rule, int x, int y, int w, int h) // 2.4
    {
        var parent = GuiFixture.Make("SimGui::Control", 0, 0, 400, 300);
        var kid = GuiFixture.Make("SimGui::Control", 10, 20, 50, 30);
        kid.HSizing = rule;
        parent.AddChild(kid);
        parent.Resize(0, 0, 500, 300);
        Assert.Equal((x, y, w, h), (kid.X, kid.Y, kid.W, kid.H));
    }

    [Theory]
    [InlineData(0u, 20, 30)]
    [InlineData(1u, 20, 130)]
    [InlineData(2u, 120, 30)]
    [InlineData(3u, 185, 30)]   // (400 - 30) / 2 as an arithmetic shift
    public void VerticalSizingAndDescendantsFollow(uint rule, int y, int h) // 2.4
    {
        var parent = GuiFixture.Make("SimGui::Control", 0, 0, 400, 300);
        var kid = GuiFixture.Make("SimGui::Control", 10, 20, 50, 30);
        kid.VSizing = rule;
        var grand = GuiFixture.Make("SimGui::Control", 1, 1, 10, 10);
        grand.VSizing = 2;
        kid.AddChild(grand);
        parent.AddChild(kid);
        parent.Resize(0, 0, 400, 400);
        Assert.Equal((y, h), (kid.Y, kid.H));
        if (rule == 1)
            Assert.Equal(101, grand.Y); // the grandchild keeps its bottom edge: dy = 100 passed down
    }

    [Fact]
    public void EveryStackElementFillsTheSurfaceEachFrame() // 2.4, D53
    {
        var fx = new GuiFixture(800, 600);
        var root = fx.Content(GuiFixture.Make("SimGui::Control", 5, 6, 640, 480));
        Assert.Equal((0, 0, 800, 600), (root.X, root.Y, root.W, root.H));
    }

    // ---- 2.5 render order, hit test ----

    [Fact]
    public void HitTestPicksLastChildFirstAndSkipsInvisible() // 2.3
    {
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 100, 100);
        var a = GuiFixture.Make("SimGui::Control", 10, 10, 50, 50);
        var b = GuiFixture.Make("SimGui::Control", 20, 20, 50, 50);
        root.AddChild(a);
        root.AddChild(b);
        Assert.Same(b, root.Pick(30, 30));
        b.Visible = false;
        Assert.Same(a, root.Pick(30, 30));
        Assert.Same(root, root.Pick(5, 5));
        Assert.Same(a, root.Pick(10, 10));          // 0 <= x is inside
        Assert.Same(root, root.Pick(60, 60));       // x < extent: 10 + 50 is outside
    }

    // ---- 2.6 focus, tab order ----

    private static GuiControl TabScreen(out GuiControl e1, out GuiControl e2, out GuiControl hidden, out GuiControl btn)
    {
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        e1 = GuiFixture.Make("FearGui::TestEdit", 10, 10, 100, 20, ("active", (byte)1), ("maxLength", (uint)40));
        hidden = GuiFixture.Make("FearGui::TestEdit", 10, 40, 100, 20, ("active", (byte)1), ("maxLength", (uint)40));
        hidden.Visible = false;
        btn = GuiFixture.Make("FearGui::FGUniversalButton", 10, 70, 40, 20, ("active", (byte)1), ("bodyVersion", (uint)3));
        e2 = GuiFixture.Make("SimGui::TextEdit", 10, 100, 100, 20, ("active", (byte)1), ("maxLength", (uint)40));
        foreach (var c in new[] { e1, hidden, btn, e2 })
            root.AddChild(c);
        return root;
    }

    [Fact]
    public void ContentChangeFocusesFirstTabStopAndTabWraps() // 2.6 tab list, 2.7 step 6
    {
        var fx = new GuiFixture();
        fx.Content(TabScreen(out var e1, out var e2, out var hidden, out var btn));
        Assert.Equal(new[] { e1, e2 }, fx.Canvas.TabList);      // invisible edit and the picture button are no tab stops
        Assert.Same(e1, fx.Canvas.FirstResponder);
        fx.Tap("tab");
        Assert.Same(e2, fx.Canvas.FirstResponder);
        fx.Tap("tab");
        Assert.Same(e1, fx.Canvas.FirstResponder);               // wraps
        fx.Tap("tab", 0x03);
        Assert.Same(e2, fx.Canvas.FirstResponder);               // shift: backwards
        fx.Tap("down");
        Assert.Same(e1, fx.Canvas.FirstResponder);               // Down and Right move forward
        fx.Tap("up");
        Assert.Same(e2, fx.Canvas.FirstResponder);
    }

    [Fact]
    public void ArrowsAreNotFocusKeysWhenTheTopControlHasFlagBit2() // 2.7 step 6
    {
        var fx = new GuiFixture();
        var root = TabScreen(out var e1, out var e2, out _, out _);
        root.Flags |= 4;
        fx.Content(root);
        fx.Tap("down");
        Assert.Same(e1, fx.Canvas.FirstResponder);
        fx.Tap("tab");
        Assert.Same(e2, fx.Canvas.FirstResponder);
    }

    // ---- 2.7 key repeat and routing ----

    [Fact]
    public void KeyRepeatStartsAfter300MsThenEvery50Ms() // 2.7 step 3 and 5, D51
    {
        var fx = new GuiFixture();
        fx.Content(TabScreen(out _, out var e2, out _, out _));
        fx.Tap("tab");                                           // focus the SimGui::TextEdit
        var edit = (GuiTextEdit)e2;
        fx.Key("a", true, 0, 'a');
        Assert.Equal("a", edit.Text);
        var t0 = fx.Clock;
        Assert.Equal(t0 + 300, fx.Canvas.KeyRepeatAt);
        fx.Frame(299);
        Assert.Equal("a", edit.Text);
        fx.Frame(2);                                             // now > t0 + 300
        Assert.Equal("aa", edit.Text);
        Assert.Equal(fx.Clock + 50, fx.Canvas.KeyRepeatAt);
        fx.Frame(49);
        Assert.Equal("aa", edit.Text);
        fx.Frame(2);
        Assert.Equal("aaa", edit.Text);
        fx.Key("a", false, 0, 'a');
        Assert.Equal(-1, fx.Canvas.KeyRepeatAt);
        fx.Frame(400);
        Assert.Equal("aaa", edit.Text);
    }

    [Fact]
    public void TabNeverRepeats() // 2.7 step 3
    {
        var fx = new GuiFixture();
        fx.Content(TabScreen(out var e1, out _, out _, out _));
        fx.Key("tab", true);
        Assert.Equal(-1, fx.Canvas.KeyRepeatAt);
        fx.Frame(1000);
        Assert.NotSame(e1, fx.Canvas.FirstResponder);
    }

    [Fact]
    public void KeysFallThroughToActionMapsWhenOnlyTheRootHasFocus() // 2.7 step 4
    {
        var fx = new GuiFixture();
        fx.Content(GuiFixture.Make("SimGui::Control", 0, 0, 640, 480));
        Assert.Same(fx.Canvas.Root, fx.Canvas.FirstResponder);
        Assert.False(fx.Key("w", true));                        // not consumed: the action maps see it
        Fixture(fx);
        fx.Canvas.CaptureAll = true;
        Assert.True(fx.Key("w", true));
        static void Fixture(GuiFixture f) => f.Key("w", false);
    }

    [Fact]
    public void MouseWheelIsDroppedAndCursorOffReleasesTheMouse() // 2.7 mouse 2, 'ignored when the cursor is off'
    {
        var fx = new GuiFixture();
        fx.Content(GuiFixture.Make("SimGui::Control", 0, 0, 640, 480));
        Assert.True(fx.Canvas.HandleInput(new RawInput { Device = RawInput.DevMouse, ObjType = 9, Value = 1 }));
        fx.Canvas.CursorOn = false;
        Assert.False(fx.Canvas.HandleInput(new RawInput { Device = RawInput.DevMouse, ObjType = RawInput.ObjXAxis, Value = 5 }));
    }

    [Fact]
    public void CursorIsClampedToTheTopStackControl() // 2.7 mouse 1
    {
        var fx = new GuiFixture();
        fx.Content(GuiFixture.Make("SimGui::Control", 0, 0, 640, 480));
        fx.Move(5000, -5000);
        Assert.Equal((639, 0), ((int)fx.Canvas.CursorX, (int)fx.Canvas.CursorY));
    }

    // ---- 3.3 ActiveCtrl, buttons ----

    [Fact]
    public void ActiveCtrlActionSequenceIsCommandThenOnActionThenMessage() // 2.8 action sequence
    {
        var fx = new GuiFixture();
        fx.Interp.Evaluate("function MyBtn::onAction() { echo(\"onAction\"); } function cmd() { echo(\"command\"); } function MsgFn() { echo(\"message\"); }");
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var btn = GuiFixture.Make("SimGui::ActiveCtrl", 10, 10, 60, 20, ("active", (byte)1), ("messageTag", (uint)120500));
        btn.Name = "MyBtn";
        btn.Command = "cmd();";
        root.AddChild(btn);
        fx.Interp.Evaluate("IDCMD_MSG = 120500, \"MsgFn();\";");
        fx.Content(root);
        fx.Output.Clear();
        fx.Click(20, 15);
        Assert.Equal(["command", "onAction", "message"], fx.Output.Where(l => l is "command" or "onAction" or "message"));
    }

    [Fact]
    public void ActiveCtrlPressArmsAndReleaseOutsideCancels() // 3.3 base states
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var btn = (GuiActive)GuiFixture.Make("SimGui::ActiveCtrl", 10, 10, 60, 20, ("active", (byte)1));
        btn.Command = "echo(\"fired\");";
        root.AddChild(btn);
        fx.Content(root);
        fx.Move(20, 15);
        fx.Button(true);
        Assert.True(btn.Pressed);
        Assert.Same(btn, fx.Canvas.Lock);
        fx.Move(200, 200);                                       // dragged outside: pressed clears, lock stays
        Assert.False(btn.Pressed);
        Assert.Same(btn, fx.Canvas.Lock);
        fx.Move(20, 15);
        Assert.True(btn.Pressed);                                // moving back in re-arms
        fx.Move(200, 200);
        fx.Output.Clear();
        fx.Button(false);
        Assert.Null(fx.Canvas.Lock);
        Assert.DoesNotContain("fired", fx.Output);
    }

    [Fact]
    public void InactiveActiveCtrlIsTransparentToTheMouse() // 2.5 Active
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var outer = (GuiActive)GuiFixture.Make("SimGui::ActiveCtrl", 0, 0, 200, 200, ("active", (byte)1));
        outer.Command = "echo(\"outer\");";
        var inner = (GuiActive)GuiFixture.Make("SimGui::ActiveCtrl", 10, 10, 50, 50, ("active", (byte)0));
        outer.AddChild(inner);
        root.AddChild(outer);
        fx.Content(root);
        fx.Click(20, 20);
        Assert.Contains("outer", fx.Output);
    }

    [Fact]
    public void EnterPressesAnActiveCtrlWithoutTheMouse() // 3.3 key down and key up
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var btn = GuiFixture.Make("SimGui::ActiveCtrl", 10, 10, 60, 20, ("active", (byte)1));
        btn.Command = "echo(\"enter\");";
        root.AddChild(btn);
        fx.Content(root);
        Assert.Same(btn, fx.Canvas.FirstResponder);
        fx.Tap("space");                                         // Space does nothing
        Assert.DoesNotContain("enter", fx.Output);
        fx.Tap("enter");
        Assert.Contains("enter", fx.Output);
    }

    private static GuiUniversalButton Uni(string root, uint radio = 0, bool sw = false, bool on = false, string cmd = "", string alt = "")
    {
        var b = (GuiUniversalButton)GuiFixture.Make("FearGui::FGUniversalButton", 0, 0, 20, 20, ("active", (byte)1), ("bodyVersion", (uint)3),
            ("radioId", radio), ("isSwitch", sw ? 1u : 0u), ("on", on ? 1u : 0u), ("bitmapRoot", root));
        b.Command = cmd;
        b.AltCommand = alt;
        return b;
    }

    [Fact]
    public void UniversalButtonMirrorReadsTheVariableIgnoringCase() // 0049fa3c: stricmp against TRUE; a bool cell reads back "True"
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var b = Uni("BTN_X", sw: true);
        b.ConsoleVar = "$mir";
        b.F["mirrorVar"] = (byte)1;
        root.AddChild(b);
        fx.Content(root);
        fx.Interp.SetVariable("$mir", "True");
        b.PreRender();
        Assert.Equal("TRUE", b.GetValue());
        fx.Interp.SetVariable("$mir", "False");
        b.PreRender();
        Assert.Equal("FALSE", b.GetValue());
        b.SetValue("true");                  // 0049fa00: setValue compares ignoring case too, and "1" is not TRUE
        Assert.Equal("TRUE", b.GetValue());
        b.SetValue("1");
        Assert.Equal("FALSE", b.GetValue());
    }

    [Fact]
    public void UniversalButtonSwitchTogglesWritesTheVariableAndPlaysSounds() // 3.3 FGUniversalButton input
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var b = Uni("BTN_X", sw: true, cmd: "echo(\"on-cmd\");", alt: "echo(\"off-cmd\");");
        b.X = b.Y = 10;
        b.ConsoleVar = "$sw";
        root.AddChild(b);
        fx.Content(root);
        var audio = new RecordingAudio();
        fx.Host.Audio = audio;
        Assert.Equal("FALSE", b.GetValue());
        fx.Click(15, 15);
        Assert.Equal("TRUE", b.GetValue());
        Assert.Equal("TRUE", fx.Interp.PeekVariable("$sw"));
        Assert.Contains("on-cmd", fx.Output);
        Assert.Equal("Button2.wav", audio.Played[0]);            // off to on: sound object 0x15
        fx.Click(15, 15);
        Assert.Equal("FALSE", b.GetValue());
        Assert.Equal("FALSE", fx.Interp.PeekVariable("$sw"));
        Assert.Contains("off-cmd", fx.Output);
        Assert.Equal("Button1.wav", audio.Played[1]);            // plain press sound
        Assert.Contains("Button1.wav", audio.Played.Skip(2));     // turning off also plays Button1
        Assert.False(b.WantsTab);                                // never a tab stop, never takes focus
        Assert.False(b.CanTake());
    }

    [Fact]
    public void UniversalButtonRadioSetSelectsOneAndShowsItsPage() // 3.3 radio and sibling message 3
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var pageA = GuiFixture.Make("SimGui::Control", 0, 100, 50, 50);
        pageA.Id = 501;
        var pageB = GuiFixture.Make("SimGui::Control", 0, 100, 50, 50);
        pageB.Id = 502;
        pageB.Visible = false;
        GuiUniversalButton a = Uni("A", radio: 140212, on: true), b = Uni("B", radio: 140212);
        a.X = 10;
        b.X = 40;
        a.F["messageTag"] = 501u;
        b.F["messageTag"] = 502u;
        foreach (var c in new GuiControl[] { pageA, pageB, a, b })
            root.AddChild(c);
        fx.Content(root);
        a.SetValue("TRUE");
        fx.Click(45, 5);
        Assert.Equal(("FALSE", "TRUE"), (a.GetValue(), b.GetValue()));
        Assert.True(pageB.Visible);
        Assert.False(pageA.Visible);                             // the sibling hides its own page
    }

    // ---- 3.3 text edit ----

    private static (GuiFixture, GuiTextEdit) EditFixture(string cls)
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var edit = (GuiTextEdit)GuiFixture.Make(cls, 10, 10, 200, 20, ("active", (byte)1), ("maxLength", (uint)8));
        root.AddChild(edit);
        fx.Content(root);
        Assert.Same(edit, fx.Canvas.FirstResponder);
        return (fx, edit);
    }

    [Fact]
    public void TextEditCommitsOnEveryKeyUpToTheVariableAndTheAction() // 3.3 key up
    {
        var (fx, edit) = EditFixture("SimGui::TextEdit");
        edit.ConsoleVar = "$field";
        edit.Command = "echo(\"commit\");";
        fx.Key("h", true, 0, 'h');
        Assert.Equal("", fx.Interp.PeekVariable("$field"));      // key down edits the buffer only
        Assert.DoesNotContain("commit", fx.Output);
        fx.Key("h", false, 0, 'h');
        Assert.Equal("h", fx.Interp.PeekVariable("$field"));
        Assert.Contains("commit", fx.Output);
    }

    [Fact]
    public void TextEditKeysSelectionOverwriteAndMaximumLength() // 3.3 key down
    {
        var (fx, e) = EditFixture("SimGui::TextEdit");
        fx.Type("abcdefghij");
        Assert.Equal("abcdefgh", e.Text);                        // maximum length 8
        fx.Tap("home");
        Assert.Equal(0, e.Cursor);
        fx.Tap("right", 0x03);
        fx.Tap("right", 0x03);
        Assert.Equal((0, 2), (e.SelStart, e.SelEnd));
        fx.Tap("x", 0, 'X');                                     // replaces the selection
        Assert.Equal("Xcdefgh", e.Text);
        fx.Tap("insert");
        fx.Tap("end");
        fx.Tap("left");
        fx.Tap("z", 0, 'z');                                     // overwrite
        Assert.Equal("Xcdefgz", e.Text);
        fx.Tap("delete");
        fx.Tap("home");
        fx.Tap("delete");
        Assert.Equal("cdefgz", e.Text);
        fx.Tap("end");
        fx.Tap("backspace");
        Assert.Equal("cdefg", e.Text);
    }

    [Fact]
    public void TextEditUndoSwapsAndClipboardCopyCutPaste() // 3.3 ctrl keys
    {
        var (fx, e) = EditFixture("SimGui::TextEdit");
        fx.Type("abc");
        fx.Tap("backspace");
        Assert.Equal("ab", e.Text);
        fx.Tap("backspace", 0x0c);                               // ctrl+backspace: undo
        Assert.Equal("abc", e.Text);
        fx.Tap("backspace", 0x0c);                               // a second press redoes
        Assert.Equal("ab", e.Text);
        fx.Tap("home");
        fx.Tap("right", 0x03);
        fx.Tap("c", 0x0c);                                       // ctrl+c copies the selection
        Assert.Equal("a", fx.Host.Clipboard);
        fx.Tap("end");
        fx.Tap("v", 0x0c);
        Assert.Equal("aba", e.Text);
        fx.Tap("home");
        fx.Tap("right", 0x03);
        fx.Tap("x", 0x0c);                                       // ctrl+x cuts
        Assert.Equal("ba", e.Text);
    }

    [Fact]
    public void NumbersOnlyAcceptsDigitsAndALeadingMinus() // 3.3 key down
    {
        var (fx, e) = EditFixture("SimGui::TextEdit");
        e.F["numbersOnly"] = 1u;
        fx.Type("1a2");
        Assert.Equal("12", e.Text);
        fx.Tap("home");
        fx.Tap("-", 0, '-');
        fx.Tap("-", 0, '-');                                     // not when a minus is already first
        Assert.Equal("-12", e.Text);
        fx.Tap("end");
        fx.Tap("-", 0, '-');                                     // not away from position 0
        Assert.Equal("-12", e.Text);
    }

    [Fact]
    public void TestEditRunsItsCommandOnEveryKeyAndTheAlternateCommandOnEnter() // 3.3 TestEdit
    {
        var (fx, e) = EditFixture("FearGui::TestEdit");
        e.Command = "echo(\"cmd\");";
        e.AltCommand = "echo(\"alt\");";
        e.ConsoleVar = "$te";
        fx.Key("q", true, 0, 'q');
        Assert.Equal(["cmd"], fx.Output.Where(l => l is "cmd" or "alt"));
        Assert.Equal("q", fx.Interp.PeekVariable("$te"));        // key down also writes the variable
        fx.Output.Clear();
        fx.Key("enter", true);
        Assert.Equal(["cmd", "alt"], fx.Output.Where(l => l is "cmd" or "alt"));
    }

    [Fact]
    public void TestEditFocusOutRunsTheAlternateCommandAndAllowsTheMove() // 2.6, 3.3
    {
        var fx = new GuiFixture();
        fx.Content(TabScreen(out var e1, out var e2, out _, out _));
        e1.AltCommand = "echo(\"leave\");";
        fx.Tap("tab");
        Assert.Contains("leave", fx.Output);
        Assert.Same(e2, fx.Canvas.FirstResponder);
    }

    // ---- 3.3 dialogs, dimming ----

    private static GuiControl DialogRoot(out GuiControl ok, out GuiControl cancel, out GuiControl edit)
    {
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var box = GuiFixture.Make("FearGui::FGDlgBox", 100, 100, 300, 200, ("active", (byte)1));
        box.Id = 141000;
        box.Name = "MessageDialog";
        ok = GuiFixture.Make("FearGui::FGUniversalButton", 10, 10, 40, 20, ("active", (byte)1), ("bodyVersion", (uint)3));
        ok.Name = "DialogReturnButton";
        ok.Command = "echo(\"ok\");";
        cancel = GuiFixture.Make("FearGui::FGUniversalButton", 60, 10, 40, 20, ("active", (byte)1), ("bodyVersion", (uint)3));
        cancel.Name = "DialogEscapeButton";
        cancel.Command = "echo(\"cancel\");";
        edit = GuiFixture.Make("FearGui::TestEdit", 10, 50, 100, 20, ("active", (byte)1), ("maxLength", (uint)10));
        foreach (var c in new[] { ok, cancel, edit })
            box.AddChild(c);
        root.AddChild(box);
        return root;
    }

    [Fact]
    public void DialogBoxEnterAndEscapeActivateTheNamedButtons() // 3.3 FGDlgBox
    {
        var fx = new GuiFixture();
        fx.Content(GuiFixture.Make("SimGui::Control", 0, 0, 640, 480));
        var d = DialogRoot(out _, out _, out var e);
        e.Parent!.RemoveChild(e);                                // no text field: nothing else to focus
        fx.Canvas.PushDialog(d);
        fx.Frame();
        Assert.IsType<GuiDlgBox>(fx.Canvas.FirstResponder);       // the named dialog takes the focus by itself
        fx.Tap("enter");
        fx.Tap("escape");
        Assert.Equal(["ok", "cancel"], fx.Output.Where(l => l is "ok" or "cancel"));
    }

    [Fact]
    public void EnterInADialogTextFieldPressesTheReturnButton() // 3.3 TestEdit, DialogReturnButton / DialogEscapeButton
    {
        var fx = new GuiFixture();
        fx.Content(GuiFixture.Make("SimGui::Control", 0, 0, 640, 480));
        fx.Canvas.PushDialog(DialogRoot(out _, out _, out var edit));
        fx.Frame();
        fx.Canvas.SetFirstResponder(edit);
        fx.Type("hi");
        fx.Tap("enter");
        Assert.Contains("ok", fx.Output);
        fx.Tap("escape");
        Assert.Contains("cancel", fx.Output);
    }

    [Fact]
    public void ADialogWithControl141000DimsTheScreenBehindIt() // 2.5 dimmed rule, D53
    {
        var fx = new GuiFixture();
        var content = fx.Content(GuiFixture.Make("SimGui::Control", 0, 0, 640, 480));
        var under = GuiFixture.Make("FearGui::FGControl", 0, 0, 10, 10);
        content.AddChild(under);
        Assert.False(under.Dimmed);
        var dlg = DialogRoot(out var ok, out _, out _);
        fx.Canvas.PushDialog(dlg);
        Assert.True(under.Dimmed);
        Assert.False(ok.Dimmed);                                 // the dialog's own controls are not dimmed
        fx.Canvas.PopDialog(110001);
        Assert.False(under.Dimmed);
        fx.Canvas.PushDialog(GuiFixture.Make("SimGui::Control", 0, 0, 640, 480));   // a dialog without 141000 does not dim
        Assert.False(under.Dimmed);
    }

    // ---- 2.2, 2.8 canvas messages, dialogs ----

    [Fact]
    public void MessageTagsLoadContentPushAndPopDialogsAndRunCommands() // 2.8 table
    {
        var dir = Path.Combine(Path.GetTempPath(), "gui-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.gui"), GuiW.Block("SGct", GuiW.Control(w: 11)));
            File.WriteAllBytes(Path.Combine(dir, "d.gui"), GuiW.Block("SGct", GuiW.Control(w: 22)));
            var fx = new GuiFixture();
            fx.Host.Root = dir;
            fx.Interp.Evaluate("IDGUI_A = 100200, \"a.gui\"; IDDLG_D = 110200, \"d.gui\"; IDCMD_E = 120200, \"echo(\\\"ran\\\");\";");
            fx.Content(GuiFixture.Make("SimGui::Control", 0, 0, 640, 480));
            Assert.True(fx.Canvas.Root.Message(null, 100200));
            Assert.Single(fx.Canvas.Stack);
            Assert.Equal("SimGui::Control", fx.Canvas.Content!.ClassName);
            Assert.True(fx.Canvas.Root.Message(null, 110200));
            Assert.Equal(1, fx.Canvas.DialogCount);
            Assert.True(fx.Canvas.Root.Message(null, 110001));   // IDDLG_OK pops
            Assert.Equal(0, fx.Canvas.DialogCount);
            Assert.True(fx.Canvas.Root.Message(null, 120200));
            Assert.Contains("ran", fx.Output);
            Assert.False(fx.Canvas.Root.Message(null, 5));
            Assert.False(fx.Canvas.Root.Message(null, 100001));  // the range is above 100001
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DelegateCanKeepADialogOpen() // 2.2 pop dialog
    {
        var fx = new GuiFixture();
        var del = new GuiScreenResponder { Name = "x", OnPop = (d, m) => -1 };
        fx.Content(GuiFixture.Make("SimGui::Control", 0, 0, 640, 480), del);
        fx.Canvas.PushDialog(GuiFixture.Make("SimGui::Control", 0, 0, 640, 480));
        fx.Canvas.PopDialog(0);
        Assert.Equal(1, fx.Canvas.DialogCount);
        del.OnPop = (d, m) => 0;
        fx.Canvas.PopDialog(0);
        Assert.Equal(0, fx.Canvas.DialogCount);
    }

    [Fact]
    public void ContentChangeResetsLockAndHover() // 2.2 set content
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var btn = GuiFixture.Make("SimGui::ActiveCtrl", 10, 10, 60, 20, ("active", (byte)1));
        root.AddChild(btn);
        fx.Content(root);
        fx.Move(20, 15);
        fx.Button(true);
        Assert.NotNull(fx.Canvas.Lock);
        fx.Content(GuiFixture.Make("SimGui::Control", 0, 0, 640, 480));
        Assert.Null(fx.Canvas.Lock);
        Assert.NotSame(btn, fx.Canvas.Hover);                    // recomputed by the synthetic move under the cursor
        Assert.Same(fx.Canvas.Content, fx.Canvas.Hover);
    }

    // ---- 5 commands ----

    [Fact]
    public void ControlCommandsAccessByNameOrId() // 5.2
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var t = GuiFixture.Make("SimGui::SimpleText", 5, 6, 70, 8, ("active", (byte)1), ("text", "hi"));
        t.Name = "Lbl";
        t.Id = 4242;
        root.AddChild(t);
        fx.Content(root);
        Assert.Equal("hi", fx.Interp.Evaluate("Control::getValue(Lbl);"));
        fx.Interp.Evaluate("Control::setValue(4242, \"changed\");");
        Assert.Equal("changed", fx.Interp.Evaluate("Control::getText(\"Lbl\");"));
        Assert.Equal("5 6", fx.Interp.Evaluate("Control::getPosition(Lbl);"));
        Assert.Equal("70 8", fx.Interp.Evaluate("Control::getExtent(Lbl);"));
        Assert.Equal("True", fx.Interp.Evaluate("Control::getVisible(Lbl);"));
        fx.Interp.Evaluate("Control::setVisible(Lbl, false);");
        Assert.Equal("False", fx.Interp.Evaluate("Control::getVisible(Lbl);"));
        Assert.Equal("True", fx.Interp.Evaluate("Control::setActive(Lbl, false);"));
        Assert.Equal("False", fx.Interp.Evaluate("Control::getActive(Lbl);"));
        fx.Interp.Evaluate("Control::setExtent(Lbl, 9, 10);");
        Assert.Equal("9 10", fx.Interp.Evaluate("Control::getExtent(Lbl);"));
        fx.Output.Clear();
        Assert.Equal("False", fx.Interp.Evaluate("Control::getActive();"));
        Assert.Contains("Control::getActive(control);", fx.Output);   // the usage line
    }

    [Fact]
    public void ComboAndListCommands() // 2.14, 5.3
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var combo = GuiFixture.Make("FearGui::FGStandardComboBox", 0, 0, 100, 19, ("active", (byte)1));
        combo.Name = "Combo";
        var list = GuiFixture.Make("FearGui::StandardListCtrl", 0, 30, 100, 100, ("active", (byte)1));
        list.Name = "List";
        root.AddChild(combo);
        root.AddChild(list);
        fx.Content(root);
        fx.Interp.Evaluate("FGCombo::addEntry(Combo, \"one\", 11); FGCombo::addEntry(Combo, \"two\", 22);");
        fx.Interp.Evaluate("FGCombo::setSelected(Combo, 22);");
        Assert.Equal("22", fx.Interp.Evaluate("FGCombo::getSelected(Combo);"));
        Assert.Equal("two", fx.Interp.Evaluate("FGCombo::getSelectedText(Combo);"));
        Assert.Equal("11", fx.Interp.Evaluate("FGCombo::findEntry(Combo, \"one\");"));
        fx.Interp.Evaluate("FGCombo::selectPrev(Combo);");
        Assert.Equal("11", fx.Interp.Evaluate("FGCombo::getSelected(Combo);"));
        fx.Interp.Evaluate("FGCombo::deleteEntry(Combo, 11);");
        Assert.Equal("-1", fx.Interp.Evaluate("FGCombo::getSelected(Combo);"));
        fx.Interp.Evaluate("FGArray::addEntry(List, \"b\", 2); FGArray::addEntry(List, \"a\", 1); FGTextList::sort(List);");
        Assert.Equal("1", fx.Interp.Evaluate("FGArray::findEntry(List, \"b\");"));   // the row index for a text
        fx.Interp.Evaluate("FGArray::setSelected(List, 1);");
        Assert.Equal("a", fx.Interp.Evaluate("FGArray::getSelectedText(List);"));
        fx.Interp.Evaluate("FGArray::clear(List);");
        Assert.Empty(((GuiList)list).Entries);
    }

    [Fact]
    public void SliderDiscretePositionsSnapAndReportTheStepIndex() // 3.3 FGSlider
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var s = GuiFixture.Make("FearGui::FGSlider", 10, 10, 112, 17, ("active", (byte)1), ("sliderVersion", (uint)3),
            ("minValue", 0u), ("maxValue", BitConverter.SingleToUInt32Bits(1f)), ("value", 0u));
        s.Name = "Sl";
        root.AddChild(s);
        fx.Content(root);
        Assert.StartsWith("0.", fx.Interp.Evaluate("Control::getValue(Sl);"));
        fx.Interp.Evaluate("FGSlider::setDiscretePositions(Sl, 5);");
        fx.Interp.Evaluate("Control::setValue(Sl, 3);");
        Assert.Equal("3", fx.Interp.Evaluate("Control::getValue(Sl);"));
        fx.Interp.Evaluate("FGSlider::setDiscretePositions(Sl, 0);");
        fx.Interp.Evaluate("Control::setValue(Sl, 0.5);");
        var v = double.Parse(fx.Interp.Evaluate("Control::getValue(Sl);"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(v, 0.45, 0.55);
    }

    [Fact]
    public void LoadObjectAndStoreObjectRoundTripALayout() // 5.1, D44
    {
        var dir = Path.Combine(Path.GetTempPath(), "gui-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "play.gui"), dst = Path.Combine(dir, "out.gui");
            var hud = new GuiW().U32(BitConverter.SingleToUInt32Bits(0.897f)).U32(BitConverter.SingleToUInt32Bits(0.035f)).U32(66).U32(19).U32(1)
                .Raw(GuiW.Control(w: 66, h: 19, id: 140100).ToArray());
            File.WriteAllBytes(src, GuiW.Block("SGts", new GuiW().U8(1).Raw(GuiW.Control(w: 640, h: 480, id: 140114).ToArray())));
            var fx = new GuiFixture();
            fx.Interp.Evaluate($"loadObject(playGui, \"{src.Replace("\\", "\\\\")}\");");
            Assert.True(fx.Runtime.Named.ContainsKey("playGui"));
            Assert.Equal("True", fx.Interp.Evaluate($"storeObject(playGui, \"{dst.Replace("\\", "\\\\")}\");"));
            var back = GuiLoader.Load(File.ReadAllBytes(dst), out var err);
            Assert.True(back != null, err);
            Assert.Equal((140114u, 640, 480), (back.Id, back.W, back.H));
            Assert.Equal(1u, back.U("produce3dEvents"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WrongCanvasNamePrintsTheStockLine() // 5
    {
        var fx = new GuiFixture();
        Assert.Equal("False", fx.Interp.Evaluate("GuiLoadContentCtrl(Nope, \"x.gui\");"));
        Assert.Contains("GuiLoadContentCtrl: Nope is not a valid SimGui::Canvas", fx.Output);
    }

    [Fact]
    public void CenterPrintAndHudTimerCommands() // 4.2, 5.4
    {
        var fx = new GuiFixture();
        fx.Interp.Evaluate("Client::centerPrint(\"hello\", 2);");
        Assert.Equal(("hello", 2), (fx.Host.CenterText, fx.Host.CenterType));
        fx.Interp.Evaluate("setHudTimer(-5);");
        fx.Clock += 7000;
        Assert.Equal(2.0, fx.Runtime.ClockSeconds, 3);
    }

    // ---- 4 HUD ----

    [Theory]
    [InlineData(1.0, 85, 0xfc)]
    [InlineData(0.5, 42, 0xf2)]
    [InlineData(0.3, 25, 0xf3)]
    [InlineData(0.01, 1, 0xf3)]     // forced to at least 1 while value > 0
    [InlineData(0.0, 0, 0xf3)]
    [InlineData(0.67, 56, 0xf2)]    // 56 is the last of the middle third
    [InlineData(0.68, 57, 0xfc)]    // 57 is the first of the top third
    public void HealthBarLengthAndColourThresholds(double value, int length, int color) // 4.2 HealthHud
    {
        Assert.Equal(length, GuiBarHud.BarLength(value, false));
        Assert.Equal(color, GuiBarHud.BarColor(length, false));
    }

    [Fact]
    public void HealthBarLowResolutionScale() // 4.2: low resolution x 51
    {
        Assert.Equal(51, GuiBarHud.BarLength(1.0, true));
        Assert.Equal(25, GuiBarHud.BarLength(0.5, true));
        Assert.Equal(0xfc, GuiBarHud.BarColor(34, true));
    }

    private sealed class RecDev : IGfxDevice
    {
        public List<string> Calls { get; } = [];
        public int Width => 640;
        public int Height => 480;
        public void Dispose() { }
        public void SetPalette(PaletteBank bank, float gamma = 1f) { }
        public void FlushTextureCache() { }
        public void FlushTexture(PbmpBitmap bitmap) { }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void Clear(int colorIndex) { }
        public void ClearRgb(byte r, byte g, byte b) { }
        public void SetClip(GfxRect? clip) => Calls.Add("clip " + (clip is GfxRect c ? $"{c.Left},{c.Top},{c.Right},{c.Bottom}" : "off"));
        public void DrawLine(int x0, int y0, int x1, int y1, int colorIndex) { }
        public void DrawRectOutline(GfxRect rect, int colorIndex) { }
        public void FillRect(GfxRect rect, int colorIndex, float alpha = 1f) => Calls.Add($"fill {rect.Left},{rect.Top},{rect.Right},{rect.Bottom} c{colorIndex:x}");
        public void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitRegion(PbmpBitmap bitmap, GfxRect source, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitStretch(PbmpBitmap bitmap, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public void BlitRegionStretch(PbmpBitmap bitmap, GfxRect source, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public byte[] ReadPixels() => [];
        public IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options) => throw new NotSupportedException();
        public void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB) { }
        public void SetCamera3D(in Camera3D camera) { }
        public void Draw3D(ReadOnlySpan<Vertex3D> triangles, in DrawState3D state) { }
        public void EndScene3D() { }
    }

    private static List<string> DrawHealth(double damage, bool state = true)
    {
        var fx = new GuiFixture();
        fx.Host.Hud = new StaticHudSource { DamageLevel = damage, HasPlayerState = state };
        var root = GuiFixture.Make("SimGui::TSControl", 0, 0, 640, 480);
        var hud = (GuiHud)GuiFixture.Make("FearGui::HealthHud", 0, 0, 131, 35, ("hudX", 0u), ("hudY", 0u));
        root.AddChild(hud);
        fx.Content(root);
        var dev = new RecDev();
        fx.Canvas.Render(dev);
        return dev.Calls;
    }

    [Fact]
    public void HealthHudDrawsTheBarClippedToItsLength() // 4.2: origin (x + 37, y + 10), clip to origin.x .. origin.x + L
    {
        var calls = DrawHealth(0.5);                    // value 0.5: L = 42, palette 0xf2
        Assert.Contains("clip 37,10,78,17", calls);
        Assert.Contains(calls, c => c.StartsWith("fill 37,10,120,17 c" + "f2"));
    }

    [Fact]
    public void PlayViewWithoutCameraFillsBlackAndDrawsNoHud() // 3.3: TSControl draw 00546dc4, set-up 00545f7c failed
    {
        var fx = new GuiFixture();
        fx.Host.Hud = new StaticHudSource { DamageLevel = 0.5, HasPlayerState = true };
        var root = GuiFixture.Make("SimGui::TSControl", 0, 0, 640, 480);
        root.AddChild(GuiFixture.Make("FearGui::HealthHud", 0, 0, 131, 35, ("hudX", 0u), ("hudY", 0u)));
        fx.Content(root);
        fx.Canvas.ViewHasCamera = false;
        var dev = new RecDev();
        fx.Canvas.Render(dev);
        Assert.Equal(new[] { "fill 0,0,639,479 c0" }, dev.Calls.Where(c => c.StartsWith("fill", StringComparison.Ordinal)).ToArray());
        Assert.DoesNotContain("clip 37,10,78,17", dev.Calls);
        fx.Canvas.ViewHasCamera = true;
        var dev2 = new RecDev();
        fx.Canvas.Render(dev2);
        Assert.Contains("clip 37,10,78,17", dev2.Calls);
    }

    [Fact]
    public void HealthHudDrawsNothingWithoutPlayerStateOrWhenDead() // 4.2
    {
        Assert.DoesNotContain(DrawHealth(0.5, state: false), c => c.StartsWith("fill"));
        Assert.DoesNotContain(DrawHealth(1.0), c => c.StartsWith("fill 37"));   // value 0: no bar
    }

    [Fact]
    public void HudPositionIsAFractionOfTheFreeParentArea() // 4.1
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::TSControl", 0, 0, 640, 480);
        var clock = (GuiHud)GuiFixture.Make("FearGui::ClockHud", 0, 0, 66, 19, ("hudX", BitConverter.SingleToUInt32Bits(0.897f)), ("hudY", BitConverter.SingleToUInt32Bits(0.035f)));
        root.AddChild(clock);
        fx.Content(root);
        Assert.Equal((0, 0), (clock.X, clock.Y));                // the file position stays until the parent is resized (stock capture)
        fx.Canvas.Width = 800;
        fx.Canvas.Frame();
        Assert.Equal((int)(0.897f * (800 - 66)), clock.X);
        clock.X = 3;
        clock.Clamp();                                           // under 8 pixels from the edge snaps to 0
        Assert.Equal(0, clock.X);
    }

    // 004afc9c (HudCtrl slot 0x88, run by Control::resize 0053a350 for every child with the new parent extent): x = ftol(fx * (parentW - W)),
    // y = ftol(fy * (parentH - H)). Fractions are the play.gui floats; the sensor frame bitmap is drawn 1 pixel inside its control, which
    // is what the stock captures at these sizes show (sensor frame left edge 638, 824, 1037, 1303, 1569).
    [Theory]
    [InlineData(800, 600, 658, 20, 637)]
    [InlineData(1024, 768, 859, 25, 823)]
    [InlineData(1280, 720, 1089, 24, 1036)]
    [InlineData(1600, 1200, 1376, 40, 1302)]
    [InlineData(1920, 1080, 1663, 36, 1568)]
    public void ClockAndSensorHudsLayOutFromTheirFractionsAtEverySize(int w, int h, int clockX, int clockY, int sensorX)
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::TSControl", 0, 0, 640, 480);
        var clock = (GuiHud)GuiFixture.Make("FearGui::ClockHud", 0, 0, 66, 19, ("hudX", BitConverter.SingleToUInt32Bits(0.8972125f)), ("hudY", BitConverter.SingleToUInt32Bits(0.034707155f)));
        var sensor = (GuiHud)GuiFixture.Make("FearGui::FGHSensorPing", 0, 0, 32, 23, ("hudX", BitConverter.SingleToUInt32Bits(0.8305921f)), ("hudY", 0u));
        root.AddChild(clock);
        root.AddChild(sensor);
        fx.Content(root);
        fx.Canvas.Width = w;
        fx.Canvas.Height = h;
        fx.Canvas.Frame();
        Assert.Equal((clockX, clockY), (clock.X, clock.Y));
        Assert.Equal((sensorX, 0), (sensor.X, sensor.Y));
    }

    [Theory]
    [InlineData(0.0, "00:00:00.0")]
    [InlineData(54.34, "00:00:54.3")]
    [InlineData(-5.0, "00:00:05.0")]
    [InlineData(3725.9, "01:02:05.9")]
    public void ClockHudFormat(double seconds, string text) => Assert.Equal(text, GuiClockHud.Format(seconds)); // 4.2

    // ---- headless render ----

    [Fact]
    public void HeadlessRenderOfASyntheticDialog() // 2.5, 2.9: fills and dimmed colours reach the framebuffer
    {
        GlWindow? win = null;
        foreach (var driver in new string?[] { "offscreen", null })
        {
            win = GlWindow.TryCreate("gui-test", 64, 48, hidden: true, driver, out var err);
            if (win != null)
                break;
        }
        if (win == null)
        {
            _out.WriteLine("SKIPPED: no OpenGL 3.3 context could be made");
            return;
        }
        using (win)
        {
            using var gd = new GlGfxDevice(win);
            var rgba = new byte[1024];
            void Set(int i, byte r, byte g, byte b)
            { rgba[i * 4] = r; rgba[(i * 4) + 1] = g; rgba[(i * 4) + 2] = b; }
            Set(5, 10, 200, 10);     // FGControl fill
            Set(6, 200, 10, 10);     // FGControl disabled fill
            Set(0xf4, 20, 30, 220);  // FGDlgBox fill
            gd.SetPalette(PaletteBank.FromColors(rgba, 256), 1f);
            var fx = new GuiFixture(64, 48);
            var content = fx.Content(GuiFixture.Make("SimGui::Control", 0, 0, 64, 48));
            var panel = GuiFixture.Make("FearGui::FGControl", 2, 2, 20, 20);
            panel.Opaque = 1;
            panel.FillColor = 5;
            panel.DisabledFill = 6;
            content.AddChild(panel);
            var dlg = GuiFixture.Make("SimGui::Control", 0, 0, 64, 48);
            var box = GuiFixture.Make("FearGui::FGDlgBox", 30, 10, 30, 30, ("active", (byte)1));
            box.Id = 141000;
            dlg.AddChild(box);

            gd.BeginFrame();
            gd.ClearRgb(0, 0, 0);
            fx.Canvas.Render(gd);
            gd.EndFrame();
            var px = gd.ReadPixels();
            (int, int, int) P(int x, int y) => (px[((y * 64) + x) * 4], px[(((y * 64) + x) * 4) + 1], px[(((y * 64) + x) * 4) + 2]);
            Assert.Equal((10, 200, 10), P(10, 10));                  // not dimmed: the fill colour

            fx.Canvas.PushDialog(dlg);
            fx.Canvas.Frame();
            gd.BeginFrame();
            gd.ClearRgb(0, 0, 0);
            fx.Canvas.Render(gd);
            gd.EndFrame();
            px = gd.ReadPixels();
            Assert.Equal((200, 10, 10), P(10, 10));                  // dimmed: the disabled fill, no overlay
            Assert.Equal((20, 30, 220), P(45, 25));                  // the dialog box fill 0xf4
            Assert.Equal((0, 0, 0), P(60, 45));                      // untouched surface
        }
    }
}
