// Tests of the joystick path (docs/client-app.md 3.2, 3.3, 3.6): object numbering (0050ed5c), axis normalisation, POV angle
// to events (0050f658) and the stock config\joystick.cs bindings through the dispatcher. No device is needed.
namespace OpenTribes.Tests;

using OpenTribes.Script;

public sealed class JoystickTests
{
    private sealed class Sink : IActionConsumer
    {
        public readonly List<(int Action, int Kind, float Value)> Got = [];
        public bool Removing => false;
        public bool OnAction(int action, int kind, float value)
        {
            Got.Add((action, kind, value));
            return true;
        }
    }

    [Theory]
    [InlineData(-32768, 0f)]
    [InlineData(32767, 1f)]
    [InlineData(0, 32768f / 65535f)]
    public void AxisValueIsPositionInRange(int raw, float expect) =>
        // 0050f658: (raw - min) / (max - min) for an axis of a device that is not a mouse.
        Assert.Equal(expect, JoystickEvents.AxisValue(raw), 6);

    [Theory]
    [InlineData(0, 11, 0)]
    [InlineData(1, 12, 0)]
    [InlineData(2, 13, 0)]
    [InlineData(3, 14, 0)]
    [InlineData(4, 15, 0)]
    [InlineData(5, 16, 0)]
    [InlineData(6, 17, 0)]
    [InlineData(7, 17, 1)]
    public void AxesAreNumberedPerObjectType(int axis, int type, int inst) =>
        // 0050ed5c: object type codes 0x0b..0x10 for x..rz, 0x11 for sliders, each type counted from 0.
        Assert.Equal(((byte)type, (byte)inst), JoystickEvents.AxisObject(axis));

    [Theory]
    [InlineData(0, 0xffff)]
    [InlineData(1, 0)]
    [InlineData(3, 4500)]
    [InlineData(2, 9000)]
    [InlineData(6, 13500)]
    [InlineData(4, 18000)]
    [InlineData(12, 22500)]
    [InlineData(8, 27000)]
    [InlineData(9, 31500)]
    [InlineData(5, 0xffff)]
    public void HatBitsGiveTheAngle(int hat, int angle) => Assert.Equal(angle, JoystickEvents.HatAngle(hat));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4500, 1)]
    [InlineData(9000, 1)]
    [InlineData(13500, 2)]
    [InlineData(18000, 2)]
    [InlineData(22500, 3)]
    [InlineData(27000, 3)]
    [InlineData(31500, 0)]
    [InlineData(0xffff, 5)]
    public void PovSectorFollowsTheStockFormula(int angle, int sector) =>
        // 0050f658: ((angle + 31500) / 9000 - 3) & 3, 0 up, 1 right, 2 down, 3 left; 5 when centred.
        Assert.Equal(sector, JoystickEvents.PovSector(angle));

    [Fact]
    public void CentredPovMakesTwoHalfMoves()
    {
        var e = JoystickEvents.PovEvents(0xffff, 0xffff);
        Assert.Equal(new (byte, byte, float)[] { (4, 3, 0.5f), (5, 3, 0.5f) }, e);
    }

    [Theory]
    [InlineData(0, 0.5f, 0f)]       // up: y = 0
    [InlineData(9000, 1f, 0.5f)]    // right: x = 1
    [InlineData(18000, 0.5f, 1f)]   // down: y = 1
    [InlineData(27000, 0f, 0.5f)]   // left: x = 0
    public void PovAngleGivesXAndYPositions(int angle, float x, float y)
    {
        var e = JoystickEvents.PovEvents(angle, angle);
        Assert.Equal(2, e.Count);
        Assert.Equal((byte)4, e[0].ObjType);
        Assert.Equal(x, e[0].Value, 4);
        Assert.Equal((byte)5, e[1].ObjType);
        Assert.Equal(y, e[1].Value, 4);
    }

    [Fact]
    public void PolledPovMakesDirectionEventsOnSectorChange()
    {
        // 0050f658: break of the old direction object (value 0), then make of the new one (value 1).
        var e = JoystickEvents.PovEvents(9000, 0);
        Assert.Equal(4, e.Count);
        Assert.Equal(((byte)6, (byte)2, 0f), e[2]);   // upov break
        Assert.Equal(((byte)9, (byte)1, 1f), e[3]);   // rpov make
        e = JoystickEvents.PovEvents(0xffff, 18000);
        Assert.Equal(3, e.Count);
        Assert.Equal(((byte)7, (byte)2, 0f), e[2]);   // dpov break only
        e = JoystickEvents.PovEvents(27000, 0xffff);
        Assert.Equal(((byte)8, (byte)1, 1f), e[2]);   // lpov make only
    }

    [Fact]
    public void ButtonMakesAndBreaks()
    {
        InputEvent m = JoystickEvents.Button(1, 5, true), b = JoystickEvents.Button(1, 5, false);
        Assert.Equal(((byte)4, (byte)1, (byte)2, (byte)5, (byte)1), (m.DeviceType, m.DeviceInst, m.ObjType, m.ObjInst, m.Action));
        Assert.Equal((byte)2, b.Action);
    }

    private static (InputDispatcher D, Sink S) StockJoystickMap()
    {
        var i = new Interpreter();
        var s = Builtins.RegisterBuiltins(i, null, ".");
        i.SetOutput(_ => { });
        // The two movement lines of the stock config\joystick.cs.
        i.Evaluate("newActionMap(\"playMap.sae\");" +
                   "bindAction(joystick0, xAxis0, TO, 220001, Center, DeadZone, 0.04, Scale, 100);" +
                   "bindAction(joystick0, yAxis0, TO, 220002, Center, DeadZone, 0.04, Scale, 100);" +
                   "bindAction(joystick0, make, button3, TO, 220003, None);bindAction(joystick0, break, button3, TO, 220003, None);" +
                   "bindAction(joystick0, slider1, TO, 220004, Flip, Center, DeadZone, 0.01, Scale, 0.6);");
        var sink = new Sink();
        var d = new InputDispatcher(s);
        d.Push(s.FindMap("playMap.sae")!, sink);
        return (d, sink);
    }

    private static InputEvent Axis(byte type, byte inst = 0, byte dev = 0) =>
        new() { DeviceType = 4, DeviceInst = dev, ObjType = type, ObjInst = inst, Action = 3 };

    [Fact]
    public void StockAxisBindingsShapeTheNormalisedValue()
    {
        var (d, sink) = StockJoystickMap();
        // Centre: 1 - 2v is about 0 inside the dead zone of 0.04 (raw 0 is 0.50000762 of the range).
        d.Process(Axis(11), JoystickEvents.AxisValue(0));
        Assert.Single(sink.Got);
        Assert.Equal(220001, sink.Got[0].Action);
        Assert.Equal(0f, sink.Got[0].Value);
        // Full travel: v = 1 gives -1 and v = 0 gives +1. The action target parser (00522af0) clamps the dead zone and the scale
        // to 0..1, so the stock "Scale, 100" is a scale of 1.
        d.Process(Axis(12), JoystickEvents.AxisValue(32767));
        d.Process(Axis(12), JoystickEvents.AxisValue(-32768));
        Assert.Equal(220002, sink.Got[1].Action);
        Assert.Equal(-1f, sink.Got[1].Value, 3);
        Assert.Equal(1f, sink.Got[2].Value, 3);
        // Other axis kinds are not mouse movement (kind 1).
        Assert.Equal(1, sink.Got[1].Kind);
    }

    [Fact]
    public void JoystickButtonAndSliderReachTheirBindings()
    {
        var (d, sink) = StockJoystickMap();
        d.Process(JoystickEvents.Button(0, 3, true), 1f);
        Assert.Equal((220003, 2, 1f), sink.Got[0]);
        d.Process(JoystickEvents.Button(0, 3, false), 0f);
        Assert.Equal(0f, sink.Got[1].Value);
        d.Process(Axis(17, 1), JoystickEvents.AxisValue(32767));
        Assert.Equal(220004, sink.Got[2].Action);
        // Center gives -1, the dead zone keeps -1, Flip negates it under Center, Scale 0.6.
        Assert.Equal(0.6f, sink.Got[2].Value, 3);
    }

    [Fact]
    public void AnotherDeviceInstanceDoesNotMatch()
    {
        var (d, sink) = StockJoystickMap();
        d.Process(Axis(11, 0, 1), 1f);
        Assert.Empty(sink.Got);
    }

    [Fact]
    public void PovEventsKindIsTen()
    {
        var i = new Interpreter();
        var s = Builtins.RegisterBuiltins(i, null, ".");
        i.SetOutput(_ => { });
        i.Evaluate("newActionMap(\"p.sae\"); bindAction(joystick0, xpov0, TO, 220010);");
        var sink = new Sink();
        var d = new InputDispatcher(s);
        d.Push(s.FindMap("p.sae")!, sink);
        foreach (var (t, a, v) in JoystickEvents.PovEvents(9000, 9000))
            d.Process(new InputEvent { DeviceType = 4, DeviceInst = 0, ObjType = t, ObjInst = 0, Action = a }, v);
        Assert.Single(sink.Got);
        Assert.Equal(10, sink.Got[0].Kind);
        Assert.Equal(1f, sink.Got[0].Value, 4);
    }
}
