namespace OpenTribes.Tests;

using OpenTribes.Client.Effects;
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Platform.Gfx;

// shapes-render.md 4.7: LaserProjectile image 00463810 and draw 00463fd0.
public class LaserBeamTests
{
    [Fact]
    public void LaserQuadIsOneQuadAtLeastOnePixelWideWithOneTextureColumn()
    {
        var cam = new Camera3D(0, 0, 0, 0, 0, 90, 640, 480, 0.5, 1000);
        var ctx = new FrameContext { Camera = cam };
        var img = Beams.LaserQuad(ctx, null, [1.0, 2.0, 0.0], [1.0, 200.0, 0.0], 0.25);
        var v = img.Batches.Single().Vertices;
        Assert.Equal(6, v.Length);
        Assert.All(v, q => Assert.Equal(0.25f, q.U));                         // u is one value over the beam
        Assert.Equal([0f, 1f], v.Select(q => q.V).Distinct().OrderBy(q => q).ToArray());
        // near end: radius 0.04 projects to 320 * 0.04 / 2 = 6.4 px (over 1), so the half width is 0.04
        double nearWidth = Math.Abs(v[1].X - v[0].X + (v[1].Z - v[0].Z)) / 2;
        Assert.Equal(0.04, nearWidth, 4);
        // far end (200 m): 0.064 px, so the half width grows to 0.04 / 0.064 = 0.625 (one pixel)
        double farWidth = Math.Abs(v[2].X - v[5].X + (v[2].Z - v[5].Z)) / 2;
        Assert.Equal(0.04 / (0.04 * 320 / 200), farWidth, 3);
    }
}

// 00463810 / 00463204: the laser hit shape lives beamTime and its ambient cycle spans beamTime.
public class LaserHitShapeTests
{
    [Fact]
    public void HitShapeLivesBeamTimeAndCycleSpansBeamTime()
    {
        var shape = RenderShape.Parse(SyntheticShape.Build(), "synthetic")!;
        var e = new ExplosionEffect(shape, 0, 0, 0, 0.5, 0.1);
        Assert.Equal(500, e.LifeMs);
        Assert.Equal(100, e.AgeMs, 3);
        Assert.True(e.Alive);
        e.Tick(0.39);
        Assert.True(e.Alive);
        e.Tick(0.02);
        Assert.False(e.Alive);
    }
}
