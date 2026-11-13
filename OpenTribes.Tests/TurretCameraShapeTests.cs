// Turret_getCameraTransform 004564fc: no shape instance (+0x328 == 0), no camera transform. The control ghost of a turret can be
// positioned by the stream before its datablock and shape are loaded (issue 38: a close-up of the turret body as the view).
using OpenTribes.Client.Scene;

namespace OpenTribes.Tests;

public sealed class TurretCameraShapeTests
{
    [Fact]
    public void NoPoseWhileTheTurretHasNoShapeInstance()
    {
        var sv = new ShapeVisual();
        sv.SetSmoothed(0, 0, 0, 10, 20, 30);             // positioned, no shape yet
        Assert.False(sv.HasShapeInstance);
        Assert.Null(DemoRender.TurretCamera(sv, 5, 1.5f, 0.2f, 0f, null, null));
    }
}
