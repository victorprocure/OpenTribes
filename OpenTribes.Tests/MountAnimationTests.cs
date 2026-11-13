namespace OpenTribes.Tests;

using OpenTribes.Client.Scene;

public class MountAnimationTests
{
    [Fact]
    public void PilotSlotUsesDriverPose() => Assert.Equal(22, PlayerVisual.MountAnimation(true, 22));

    [Fact]
    public void OtherSlotsUseAnimation23() => Assert.Equal(23, PlayerVisual.MountAnimation(false, 22));
}
