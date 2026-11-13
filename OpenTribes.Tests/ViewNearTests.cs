// The near plane of the PSC camera query (PlayerPSC camera query 004077a8, bytes 004078a0 to 00407901) and what it does to the stored
// depth of the 16 bit buffer: the device keeps z = near / d (setDepthRange 005daa14).
using OpenTribes.Client.Scene;
using OpenTribes.Client.World;
using OpenTribes.Game;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Tests;

public sealed class ViewNearTests
{
    [Theory]
    [InlineData(true, false, 0.2)]
    [InlineData(true, true, 0.01)]
    [InlineData(false, false, 0.01)]
    [InlineData(false, true, 0.01)]
    public void NearFollowsViewState(bool firstPerson, bool observer, double expected) =>
        Assert.Equal(expected, WorldScene.ViewNear(firstPerson, observer));

    [Theory]
    [InlineData("Player", true, 0.2)]
    [InlineData("Player", false, 0.01)]
    [InlineData("Vehicle", true, 0.2)]
    [InlineData("ObserverCamera", true, 0.01)]
    public void DemoRenderUsesTheSameQuery(string controlClass, bool firstPerson, double expected)
    {
        // 00546dc4 asks the control object's camera query in demo playback as in live play (004077a8).
        var w = new ClientWorld();
        w.Psc.ControlClass = controlClass;
        var pose = new DemoRender.CameraPose(0, 0, 0, 0, 0, firstPerson, 1);
        Assert.Equal(expected, DemoRender.NearFor(pose, w));
    }

    // 16 bit window depth of a point at forward distance d: ndc z = 2 near / d - 1, window z = (ndc + 1) / 2 * 65535.
    private static double Window16(double near, double d) => near / d * 65535.0;

    [Fact]
    public void FirstPersonNearSeparatesSurfacesThatTheSmallNearTies()
    {
        // Two walls 0.5 m apart seen from 110 m: the small near puts them 0.03 steps of the 16 bit buffer apart (a tie), the first
        // person near half a step.
        double d = 110;
        Assert.True(Window16(0.01, d) - Window16(0.01, d + 0.5) < 0.05);
        Assert.True(Window16(0.2, d) - Window16(0.2, d + 0.5) > 0.5);
    }

    [Fact]
    public void CameraDepthMatrixUsesTheNear()
    {
        var cam = new Camera3D(0, 0, 0, 0, 0, 90, 640, 480, WorldScene.FirstPersonNearPlane, 1000);
        var m = new float[16];
        cam.ViewProjection(m);
        // a point 110 m ahead (+Y): clip z = 2 near - w, ndc z = 2 near / w - 1
        double w = 110;
        double cz = m[1 * 4 + 2] * w + m[12 + 2];
        double cw = m[1 * 4 + 3] * w + m[12 + 3];
        Assert.Equal(2 * WorldScene.FirstPersonNearPlane / w - 1, cz / cw, 6);
    }
}
