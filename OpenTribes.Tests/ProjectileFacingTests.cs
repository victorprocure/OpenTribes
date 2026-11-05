using OpenTribes.Client.Shapes;
using OpenTribes.Client.Scene;
using Xunit;

namespace OpenTribes.Tests;

// Projectile image orientation, FUN_0045a5b8: Bullet and SeekingMissile ghosts face the camera (0045bd00, 004663c8 set +0x2d4),
// the other classes align the +Y axis with the velocity.
public class ProjectileFacingTests
{
    [Theory]
    [InlineData("Bullet", true)]
    [InlineData("SeekingMissile", true)]
    [InlineData("Grenade", false)]
    [InlineData("RocketDumb", false)]
    public void OnlyBulletAndMissileFaceTheCamera(string cls, bool faces) => Assert.Equal(faces, ProjectileVisual.FacesCamera(cls));

    [Fact]
    public void ABoltFlyingAtTheCameraKeepsTheViewRayAxis()
    {
        // turret bolt at (0,100,0) flying toward the camera at the origin: the axis is the view ray, not the velocity sideways
        Xf xf = ProjectileVisual.ImageBasis("Bullet", 3, 4, 0, 0, -50, 0, 0, 0, 0);
        Assert.Equal(0.6, xf.M3, 9);
        Assert.Equal(0.8, xf.M4, 9);
        Assert.Equal(0, xf.M5, 9);
        Assert.Equal(3, xf.Px);
    }

    [Fact]
    public void ABoltCrossingTheViewStillFacesTheCamera()
    {
        Xf xf = ProjectileVisual.ImageBasis("Bullet", 0, 10, 0, 80, 0, 0, 0, 0, 0);
        Assert.Equal(0, xf.M3, 9);
        Assert.Equal(1, xf.M4, 9);
    }

    [Fact]
    public void ACameraOnTheProjectileUsesYAxis()
    {
        Xf xf = ProjectileVisual.ImageBasis("Bullet", 1, 1, 1, 0, 0, 5, 1, 1, 1);
        Assert.Equal((0, 1, 0), (xf.M3, xf.M4, xf.M5));
    }

    [Fact]
    public void AShellAlignsWithItsVelocity()
    {
        Xf xf = ProjectileVisual.ImageBasis("Grenade", 0, 0, 0, 0, 3, 4, 100, 0, 0);
        Assert.Equal(0, xf.M3, 9);
        Assert.Equal(0.6, xf.M4, 9);
        Assert.Equal(0.8, xf.M5, 9);
    }
}
