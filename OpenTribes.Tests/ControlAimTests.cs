// The aim marker routine FUN_004ada90 for a Vehicle, Flier and Turret control object: eye (vtable +0x110), muzzle (+0x10c), projectile
// (+0x114) and team (+0x20c) of the object (docs/gui.md, crosshair aim markers).
using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;
using OpenTribes.Sim;
using Xunit;

namespace OpenTribes.Tests;

public class ControlAimTests
{
    private static Transform Obj(float x, float y, float z)
    {
        var t = new Transform { Flags = 3u };
        t.M[0] = t.M[4] = t.M[8] = 1f;
        t.P[0] = x; t.P[1] = y; t.P[2] = z;
        return t;
    }

    private static Transform Node(float x, float y, float z) => Obj(x, y, z);

    [Fact]
    public void VehicleEyeIsTheNodeTimesTheObjectTransform()
    {
        Transform o = Obj(10, 20, 30);
        o.M[0] = 0; o.M[1] = 1; o.M[3] = -1; o.M[4] = 0;      // yaw 90 degrees: object x is world y
        Transform eye = ControlAim.VehicleEye(o, Node(0, 1, 2));
        Assert.Equal(new[] { 9f, 20f, 32f }, new[] { eye.P[0], eye.P[1], eye.P[2] });
    }

    [Fact]
    public void VehicleEyeWithoutTheNodeIsTheObjectTransform()
    {
        Transform eye = ControlAim.VehicleEye(Obj(1, 2, 3), null);
        Assert.Equal(new[] { 1f, 2f, 3f }, new[] { eye.P[0], eye.P[1], eye.P[2] });
    }

    [Fact]
    public void VehicleMuzzleTableCyclesWithTheFireCounterAndTheSingleNodeIsTheFallback()
    {
        int[] table = { 5, 6, 7 };
        Assert.Equal(5, ControlAim.VehicleMuzzleNode(9, table, 0));
        Assert.Equal(6, ControlAim.VehicleMuzzleNode(9, table, 4));
        Assert.Equal(7, ControlAim.VehicleMuzzleNode(9, table, 2));
        Assert.Equal(9, ControlAim.VehicleMuzzleNode(9, Array.Empty<int>(), 3));
        Assert.Equal(-1, ControlAim.VehicleMuzzleNode(-1, Array.Empty<int>(), 3));
    }

    [Fact]
    public void VehicleMuzzleWithoutANodeReportsFalseAndCopiesTheObject()
    {
        Assert.False(ControlAim.VehicleMuzzle(Obj(4, 5, 6), null, out Transform m));
        Assert.Equal(5f, m.P[1]);
        Assert.True(ControlAim.VehicleMuzzle(Obj(4, 5, 6), Node(1, 1, 1), out m));
        Assert.Equal(new[] { 5f, 6f, 7f }, new[] { m.P[0], m.P[1], m.P[2] });
    }

    [Fact]
    public void TurretNodeIsTheTurretCameraAtDistanceZero()
    {
        Transform o = Obj(100, 200, 300);
        Transform n = Node(0.5f, 0.25f, 2f);
        Transform a = ControlAim.TurretNode(o, n, 0.7f, 0.2f);
        Transform b = TurretCamera.Compute(o, n, 0.7f, 0.2f, 0f, null, null);
        for (int i = 0; i < 3; ++i)
            Assert.Equal(b.P[i], a.P[i]);
        for (int i = 0; i < 9; ++i)
            Assert.Equal(b.M[i], a.M[i]);
        Assert.Equal(o.P[1], ControlAim.TurretNode(o, null, 1f, 1f).P[1]);
    }

    [Fact]
    public void MuzzleLabelPointTransformsAWorldMuzzleByTheObjectTransformAgain()
    {
        var aim = new HudControlAim(new float[3], new[] { 1f, 2f, 3f }, new[] { 1f, 0, 0, 0, 1f, 0, 0, 0, 1f }, new[] { 100f, 0f, 0f });
        Assert.Equal((101.0, 2.0, 3.0), aim.MuzzleLabelPoint());
        Assert.Equal((100.0, 0.0, 0.0), (aim with { MuzzleP = null }).MuzzleLabelPoint());
    }

    // ---- TargetMarkers ----

    private const int kControl = 5, kBeacon = 6;

    private static (ClientWorld World, ClientHudSource Src) Scene(string controlClass, int controlTeam, float[]? muzzle)
    {
        var world = new ClientWorld { PlayerId = 0x800 };
        world.Players.TeamCount = 2;
        world.Players.Clients[0x800] = new ClientPlayerRecord { Id = 0x800, Name = "Me", Team = 0 };
        var control = new ClientGhost { Index = kControl, Class = controlClass };
        control.State["team"] = (long)controlTeam;
        control.State["datablock"] = 0L;
        world.Ghosts.Insert(control);
        var beacon = new ClientGhost { Index = kBeacon, Class = "StaticShape" };
        beacon.State["isTarget"] = 1L;
        beacon.State["damageState"] = 1L;
        beacon.State["team"] = 0L;
        world.Ghosts.Insert(beacon);
        world.Psc.ControlGhost = kControl;
        world.Psc.ControlClass = controlClass;
        var src = new ClientHudSource(world)
        {
            ViewCamera = (w, h) => new Camera3D(0, 0, 0, 0, 0, 90, w, h, 0.1, 1000),
            ObjectBox = g => g == kBeacon ? (-1, 19, -1, 1, 21, 1, 0, 20, 0) : null,
            ControlAim = g => g == kControl ? new HudControlAim(new float[3], muzzle, new[] { 1f, 0, 0, 0, 1f, 0, 0, 0, 1f }, new[] { 0f, 2f, 0f }) : null,
        };
        return (world, src);
    }

    [Theory]
    [InlineData("Vehicle")]
    [InlineData("Flier")]
    public void VehiclePilotGetsAStraightLineMarkerThatNeverReachesWithTheDistanceFromTheTwiceTransformedMuzzle(string cls)
    {
        var (_, src) = Scene(cls, 0, new float[3]);
        HudTargetMarker m = Assert.Single(src.TargetMarkers(640, 480));
        Assert.NotNull(m.Low);
        Assert.False(m.Low!.Value.Reaches);
        Assert.Null(m.High);
        var cam = new Camera3D(0, 0, 0, 0, 0, 90, 640, 480, 0.1, 1000);
        Assert.True(cam.Project(0, 2, 0, out double sx, out double sy, out _));
        Assert.Equal(HudMarkers.Pixel(sx), m.Low.Value.X);
        Assert.Equal(HudMarkers.Pixel(sy), m.Low.Value.Y);
        // muzzle (0, 0, 0) through the object transform with translation (0, 2, 0): 2 m from the origin, the target is at y = 20
        Assert.Equal("18.0 m", m.Label);
    }

    [Fact]
    public void VehiclePilotOfAnotherTeamMarksNothing()
    {
        var (_, src) = Scene("Flier", 1, new float[3]);
        Assert.Empty(src.TargetMarkers(640, 480));
    }

    [Fact]
    public void VehiclePilotWithoutATeamOrADrawnObjectMarksNothing()
    {
        var (world, src) = Scene("Flier", 0, new float[3]);
        src.ControlAim = _ => null;
        Assert.Empty(src.TargetMarkers(640, 480));
        var (world2, src2) = Scene("Flier", 0, new float[3]);
        world2.Ghosts.Get(kControl)!.State.Remove("team");
        Assert.Empty(src2.TargetMarkers(640, 480));
        Assert.NotNull(world);
    }

    [Fact]
    public void ObserverControlMarksNothing()
    {
        var (_, src) = Scene("Observer", 0, new float[3]);
        Assert.Empty(src.TargetMarkers(640, 480));
    }

    [Fact]
    public void VehicleWithoutAMuzzleMeasuresFromTheObjectPosition()
    {
        var (_, src) = Scene("Flier", 0, null);
        Assert.Equal("18.0 m", Assert.Single(src.TargetMarkers(640, 480)).Label);
    }

    [Fact]
    public void TurretWithoutAProjectileGetsTheStraightLineMarker()
    {
        var (_, src) = Scene("Turret", 0, new float[3]);
        HudTargetMarker m = Assert.Single(src.TargetMarkers(640, 480));
        Assert.False(m.Low!.Value.Reaches);
        Assert.Null(m.High);
    }

    [Fact]
    public void TurretWithAProjectileSolvesTheAimFromItsTurretData()
    {
        var (world, src) = Scene("Turret", 0, new float[3]);
        byte[] raw = new byte[8];
        BitConverter.GetBytes(Projectile.kBulletGroup).CopyTo(raw, 0);
        BitConverter.GetBytes(0).CopyTo(raw, 4);
        var turretData = new ClientDataBlock { Group = 19, Index = 0 };
        turretData.Fields.Add(new DecodedField("projectileType", Convert.ToHexString(raw)));
        world.DataBlocks.Blocks[(19, 0)] = turretData;
        var bullet = new ClientDataBlock { Group = Projectile.kBulletGroup, Index = 0 };
        bullet.Fields.Add(new DecodedField("field@0x68", 1000.0));
        bullet.Fields.Add(new DecodedField("totalTime", 5.0));
        world.DataBlocks.Blocks[(Projectile.kBulletGroup, 0)] = bullet;
        HudTargetMarker m = Assert.Single(src.TargetMarkers(640, 480));
        Assert.True(m.Low!.Value.Reaches);
    }
}
