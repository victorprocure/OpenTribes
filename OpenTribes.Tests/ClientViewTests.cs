// The view toggle (third person distance) and the zoom target of the key binding test (ROADMAP M9).
using OpenTribes.Client.App;

namespace OpenTribes.Tests;

public sealed class ClientViewTests
{
    [Fact]
    public void ViewToggleEasesTheDistanceAtFifteenPerSecond()
    {
        var b = new MoveBuilder();
        Assert.Equal(0f, b.ViewDist);
        b.OnAction(MoveBuilder.View, 0f);
        Assert.True(b.ThirdPerson);
        b.UpdateViewDist(1000, 1100);
        Assert.Equal(1.5f, b.ViewDist, 3);
        b.UpdateViewDist(1100, 2000);
        Assert.Equal(4f, b.ViewDist);
        b.OnAction(MoveBuilder.View, 0f);
        b.UpdateViewDist(2000, 2200);
        Assert.Equal(1f, b.ViewDist, 3);
        b.UpdateViewDist(2200, 3000);
        Assert.Equal(0f, b.ViewDist);
    }

    [Fact]
    public void ViewToggleSetsTheFovFromThePref()
    {
        var b = new MoveBuilder { PrefFov = () => 100 };
        b.OnAction(MoveBuilder.View, 0f);
        Assert.Equal(90, b.CurrentFov);
        b.OnAction(MoveBuilder.View, 0f);
        Assert.Equal(100, b.CurrentFov);
    }

    [Fact]
    public void ZoomKeyStepsOnlyTheTargetUntilTheZoomIsOn()
    {
        var b = new MoveBuilder();
        b.OnAction(MoveBuilder.IncSniperFov, 1f);
        Assert.Equal(90, b.CurrentFov);
        Assert.Equal(22.5, b.ZoomTarget);
        b.OnAction(MoveBuilder.SniperFov, 1f);
        Assert.Equal(22.5, b.CurrentFov);
        b.OnAction(MoveBuilder.IncSniperFov, -1f);
        Assert.Equal(45, b.CurrentFov);
        b.OnAction(MoveBuilder.SniperFov, 0f);
        Assert.Equal(90, b.CurrentFov);
    }

    [Fact]
    public void ZoomIsRefusedInThirdPerson()
    {
        var b = new MoveBuilder();
        b.OnAction(MoveBuilder.View, 0f);
        b.OnAction(MoveBuilder.SniperFov, 1f);
        Assert.False(b.ZoomOn);
        Assert.Equal(90, b.CurrentFov);
    }
}
