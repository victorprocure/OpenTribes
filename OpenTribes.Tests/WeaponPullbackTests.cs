namespace OpenTribes.Tests;

using OpenTribes.Client.Scene;

public class WeaponPullbackTests
{
    // Player_getImageMountTransform 00419ba0: Player +0xfd0 is added to the image offset y when the mount point is 0.
    [Fact]
    public void PullbackMovesOnlyMountPointZeroImages()
    {
        var weapon = PlayerVisual.ImageLocal(0.25, 0.5, 0.125, 0, 0, 0, 0, -0.75);
        Assert.Equal(0.25, weapon.Px, 9);
        Assert.Equal(-0.25, weapon.Py, 9);
        Assert.Equal(0.125, weapon.Pz, 9);
        var pack = PlayerVisual.ImageLocal(0.25, 0.5, 0.125, 0, 0, 0, 2, -0.75);
        Assert.Equal(0.5, pack.Py, 9);
    }

    // Player_updateWeaponPullback 004198d4: (hit - start) . axis - (end - start) . axis - 0.2.
    [Fact]
    public void PullbackIsTheDepthOfTheHitBehindTheMuzzleMinusTwoTenths()
    {
        var p = PlayerVisual.PullbackFrom((0, 0, 0), (0, 1.0, 0), (0, 0.25, 0), (0, 1, 0));
        Assert.Equal(0.25 - 1.0 - 0.2, p, 9);
        // Only the component along the mount's y axis counts.
        var q = PlayerVisual.PullbackFrom((1, 2, 3), (1, 3, 3), (4, 2.5, 3), (0, 1, 0));
        Assert.Equal(0.5 - 1.0 - 0.2, q, 9);
    }
}
