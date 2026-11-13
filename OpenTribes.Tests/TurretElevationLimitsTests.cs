// The elevation limits of a Turret shape (Turret_onNewDataBlock 00452970). Synthetic shape only.
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;

namespace OpenTribes.Tests;

public class TurretElevationLimitsTests
{
    private static RsTransform AboutX(double a) => new() { Qx = -Math.Sin(a / 2), Qw = Math.Cos(a / 2) };

    // Node 1 (`dummy muzzle`): sequence 0 (`elevate`, one shot) turns it about x by 0.3 at position 0 and 0.9 from 0.5 on, sequence 1
    // (`power`) holds it at 1.4 (the folded pose).
    private static RenderShape MakeShape()
    {
        RenderShape? s = RenderShape.Parse(SyntheticShape.Build(), "synthetic");
        Assert.NotNull(s);
        s!.Names[s.Nodes[1].Name] = "dummy muzzle";
        s.Names[s.Sequences[0].Name] = "elevate";
        s.Names[s.Sequences[1].Name] = "power";
        s.Sequences[0].Cyclic = false;
        s.Sequences[1].NumIfl = 0;
        s.Transforms = new[] { AboutX(0.3), AboutX(0.9), AboutX(1.4) };
        s.Keys = new[]
        {
            new RsKey { Position = 0, Index = 0, Flags = 0x8000 }, new RsKey { Position = 0.5f, Index = 1, Flags = 0x8000 },
            new RsKey { Position = 0, Index = 2, Flags = 0x8000 },
        };
        s.SubSequences = new[]
        {
            new RsSubSequence { Sequence = 0, NumKeys = 2, FirstKey = 0 },
            new RsSubSequence { Sequence = 1, NumKeys = 1, FirstKey = 2 },
        };
        s.Nodes[1].NumSub = 2;
        s.Nodes[1].FirstSub = 0;
        return s;
    }

    [Fact]
    public void LimitsAreTheMuzzleAnglesOfTheElevateThreadAtZeroAndOne()
    {
        var inst = new RenderInstance(MakeShape());
        RenderThread elevate = inst.AddThread("elevate")!;
        elevate.Position = 0.4;
        (float min, float max) = ShapeVisual.ElevationLimitsOf(inst, 1, elevate, null, null);
        Assert.Equal(0.3f, min, 3);
        Assert.Equal(0.9f, max, 3);
        Assert.Equal(0.4, elevate.Position);
    }

    // The stock routine reads the nodes before the power thread exists, so a power pose that folds the barrels (read from a live
    // ghost, where the thread runs) must not move the limits, and the thread stays in the instance afterwards.
    [Fact]
    public void PowerPoseAndTurnDoNotMoveTheLimits()
    {
        var inst = new RenderInstance(MakeShape());
        RenderThread elevate = inst.AddThread("elevate")!;
        RenderThread power = inst.AddThread("power", 0, -1)!;
        RenderThread turn = inst.AddThread("elevate", 0.7)!;
        (float min, float max) = ShapeVisual.ElevationLimitsOf(inst, 1, elevate, turn, power);
        Assert.Equal(0.3f, min, 3);
        Assert.Equal(0.9f, max, 3);
        Assert.Equal(0.7, turn.Position);
        Assert.Contains(power, inst.Threads);
        (float folded, _) = ShapeVisual.ElevationLimitsOf(inst, 1, elevate, null, null);   // the running power thread would have won
        Assert.Equal(1.4f, folded, 3);
    }
}
