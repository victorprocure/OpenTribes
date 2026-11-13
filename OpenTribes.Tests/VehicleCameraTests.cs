// Vehicle_getCameraTransform 00445d88 (docs/client-net.md 4.2), the roll of Camera3D and the HUD gates of a piloted vehicle.
namespace OpenTribes.Tests;

using OpenTribes.Client.Scene;
using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;

public sealed class VehicleCameraTests
{
    private sealed class Wall : SimObject
    {
        public override string ClassName() => "StaticShape";
    }

    private static Transform Make(float rx, float ry, float rz, float x, float y, float z)
    {
        var t = new Transform();
        MissionTransform.EulerToMatrix([rx, ry, rz], ref t);
        t.Flags |= 3u;
        t.P[0] = x;
        t.P[1] = y;
        t.P[2] = z;
        return t;
    }

    [Fact]
    public void WithoutEyeNodeTheObjectTransformIsTheCamera()
    {
        Numbers.EnterScriptFpuMode();
        Transform obj = Make(0.2f, 0.1f, 1f, 5, 6, 7);
        Transform t = VehicleCamera.Compute(obj, null, 3f, null, null);
        for (var i = 0; i < 9; ++i)
            Assert.Equal(obj.M[i], t.M[i]);
        Assert.Equal(7f, t.P[2]);
    }

    [Fact]
    public void FirstPersonIsTheEyeNodeTimesTheObject()
    {
        Numbers.EnterScriptFpuMode();
        Transform obj = Make(0, 0, (float)(Math.PI / 2), 10, 20, 30);
        Transform eye = Make(0, 0, 0, 0, 1, 2);          // 1 m forward, 2 m up in object space
        Transform t = VehicleCamera.Compute(obj, eye, 0f, null, null);
        // yaw 90 degrees turns +y to -x
        Assert.Equal(9f, t.P[0], 3);
        Assert.Equal(20f, t.P[1], 3);
        Assert.Equal(32f, t.P[2], 3);
        // distance 0: no placeBehind and no Euler scaling, the rotation is the object's
        for (var i = 0; i < 9; ++i)
            Assert.Equal(obj.M[i], t.M[i], 4);
    }

    [Fact]
    public void ThirdPersonMovesBackTwiceTheArgumentAndScalesPitchAndRoll()
    {
        Numbers.EnterScriptFpuMode();
        Transform obj = Make(0.5f, 0.4f, 0f, 0, 0, 100);
        Transform eye = Make(0, 0, 0, 0, 0, 0);
        Transform t = VehicleCamera.Compute(obj, eye, 2f, null, null);
        var e = new float[3];
        MissionTransform.MatrixToEuler(t, e);
        Assert.Equal(0.4f, e[0], 4);   // pitch * 0.8
        Assert.Equal(0.2f, e[1], 4);   // roll * 0.5
        Assert.Equal(0f, e[2], 4);     // yaw kept
        // the position moved back by 4 (2.0 * 2) along the forward row of the unscaled transform
        var back = Math.Sqrt((t.P[0] * t.P[0]) + (t.P[1] * t.P[1]) + ((t.P[2] - 100) * (t.P[2] - 100)));
        Assert.Equal(4.0, back, 3);
        Assert.True(t.P[1] < 0);
    }

    [Fact]
    public void ThirdPersonStopsInFrontOfAWall()
    {
        Numbers.EnterScriptFpuMode();
        var c = new Container();
        c.Add(new Container.Entry(new Wall(), ObjectType.kInteriorType, Float6.Of(-50, -50, -50, 50, 50, 50), false, null)
        {
            Cast = (s, end, ref hit, ref n) =>
            {
                hit = new Vec3(s.X, -2f, s.Z);
                n = new Vec3(0, 1, 0);
                return true;
            },
        });
        Transform obj = Make(0, 0, 0, 0, 0, 10);
        Transform t = VehicleCamera.Compute(obj, Make(0, 0, 0, 0, 0, 0), 1f, c, null);
        Assert.Equal(-1.975f, t.P[1], 3);
    }

    [Fact]
    public void CameraRollMatchesTheEulerMatrix()
    {
        Numbers.EnterScriptFpuMode();
        Transform t = Make(0.3f, -0.6f, 1.1f, 1, 2, 3);
        DemoRender.CameraPose pose = DemoRender.PoseOf(t, true, 7);
        var cam = new Camera3D(0, 0, 0, pose.YawDeg, pose.PitchDeg, 90, 640, 480, 0.1, 1000, pose.RollDeg);
        var (x, y, z) = cam.Right;
        var f = cam.Forward;
        var u = cam.Up;
        Assert.Equal(t.M[0], (float)x, 4);
        Assert.Equal(t.M[1], (float)y, 4);
        Assert.Equal(t.M[2], (float)z, 4);
        Assert.Equal(t.M[3], (float)f.x, 4);
        Assert.Equal(t.M[4], (float)f.y, 4);
        Assert.Equal(t.M[5], (float)f.z, 4);
        Assert.Equal(t.M[6], (float)u.x, 4);
        Assert.Equal(t.M[7], (float)u.y, 4);
        Assert.Equal(t.M[8], (float)u.z, 4);
    }

    [Fact]
    public void WeaponListNeedsThePlayerButPingAndCrosshairTakeAVehicle()
    {
        var world = new ClientWorld();
        var src = new ClientHudSource(world);
        world.Psc.ControlGhost = 5;
        world.Psc.ControlClass = "Flier";
        Assert.False(src.ControlIsPlayer);
        Assert.True(src.InPlayerView);
        world.Psc.ControlClass = "Player";
        Assert.True(src.ControlIsPlayer);
        world.Psc.ControlClass = "ObserverCamera";
        Assert.False(src.ControlIsPlayer);
    }
}
