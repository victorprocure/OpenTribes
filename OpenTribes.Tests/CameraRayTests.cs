// The pull-back ray of the third person camera (GameBase_placeBehind 00449604, called by Player_getCameraTransform 00412324) and the
// flier pitch reversal of PlayerPSC_onAction (docs/player-sim.md 9.1, docs/client-net.md 4.2).
namespace OpenTribes.Tests;

using OpenTribes.Client.App;
using OpenTribes.Game;
using OpenTribes.Script;
using OpenTribes.World;

public sealed class CameraRayTests
{
    private sealed class Wall : Sim.SimObject
    {
        public override string ClassName() => "StaticShape";
    }

    // A wall whose ray test reports a hit at y = wallY with the given normal, whatever the segment.
    private static Container WallAt(float wallY, Vec3 normal, uint type = ObjectType.InteriorType)
    {
        var c = new Container();
        var e = new Container.Entry(new Wall(), type, Float6.Of(-50, -50, -50, 50, 50, 50), false, null)
        {
            Cast = (s, end, ref hit, ref n) =>
            {
                hit = new Vec3(s.X, wallY, s.Z);
                n = normal;
                return true;
            },
        };
        c.Add(e);
        return c;
    }

    private static float[] Place(Container? c, float dist)
    {
        Numbers.EnterScriptFpuMode();
        var p = new float[] { 0, 0, 10 };
        // yaw 0, pitch 0: the forward vector (row 1) is +y, the camera moves toward -y.
        ObserverCamera.PlaceBehind(c, null, p, 0, 1, 0, dist);
        return p;
    }

    [Fact]
    public void NoHitMovesBackTheFullDistance()
    {
        var p = Place(new Container(), 4f);
        Assert.Equal(-4f, p[1]);
        Assert.Equal(10f, p[2]);
        Assert.Equal(-4f, Place(null, 4f)[1]);
    }

    [Fact]
    public void HitStopsTheCameraTwoHundredthsInFrontOfTheSurface()
    {
        // wall 2 m behind the start, normal facing the camera: back = 2 - 0.025 / 1
        var p = Place(WallAt(-2f, new Vec3(0, 1, 0)), 4f);
        Assert.Equal(-1.975f, p[1], 4);
    }

    [Fact]
    public void RayReachesTwoAndAHalfTimesTheDistance()
    {
        // 9 m behind is inside 2.5 * 4 = 10 m, and beyond the 4 m distance: the distance wins
        var p = Place(WallAt(-9f, new Vec3(0, 1, 0)), 4f);
        Assert.Equal(-4f, p[1]);
    }

    [Fact]
    public void GrazingHitKeepsTheFullDistance()
    {
        // normal . forward below 0.01: the hit is ignored
        var p = Place(WallAt(-2f, new Vec3(1, 0.005f, 0)), 4f);
        Assert.Equal(-4f, p[1]);
    }

    [Fact]
    public void HitBehindTheStartClampsToZero()
    {
        var p = Place(WallAt(0.5f, new Vec3(0, 1, 0)), 4f);
        Assert.Equal(0f, p[1]);
    }

    [Fact]
    public void ObjectTypesOutsideTheMaskAreNotHit()
    {
        // 0x20c00006 = terrain, interior, 0x400000, 0x800000 (static), vehicle; a player (0x10000) is not
        var p = Place(WallAt(-2f, new Vec3(0, 1, 0), ObjectType.PlayerType), 4f);
        Assert.Equal(-4f, p[1]);
    }

    [Fact]
    public void PitchReversalNegatesMouseAndKeySpeeds()
    {
        var b = new MoveBuilder { ReversePitch = () => true };
        b.OnAction(MoveBuilder.Pitch, 0.01f);
        var n = new MoveBuilder();
        n.OnAction(MoveBuilder.Pitch, 0.01f);
        Assert.Equal(-n.PitchAcc, b.PitchAcc);
        Assert.NotEqual(0f, b.PitchAcc);
        b.OnAction(MoveBuilder.PitchSpeed, 0.5f);
        Assert.Equal(-0.5f, b.PitchSpeedKey);
    }
}
