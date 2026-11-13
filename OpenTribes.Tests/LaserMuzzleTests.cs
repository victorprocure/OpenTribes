namespace OpenTribes.Tests;

using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;

public class LaserMuzzleTests
{
    // L2: the first person muzzle (beam start) is taken from the same pose the weapon is drawn in: the body that puts the eye node on
    // the camera, with the mount pitch of the camera (stock reads the control object's own transform and pitch, 00419ba0, 00419d88).
    [Fact]
    public void FirstPersonBodyPutsTheEyeOnTheCamera()
    {
        var body = PlayerVisual.EyeAlignedBody(-196.25, -193.698, 196.725, Math.PI, 0.0, 0.3, 1.6);
        var (x, y, z) = body.Apply(0.0, 0.3, 1.6);
        Assert.Equal(-196.25, x, 6);
        Assert.Equal(-193.698, y, 6);
        Assert.Equal(196.725, z, 6);
    }
}
