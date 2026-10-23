// The view toggle (third person distance) and the zoom target of the key binding test (ROADMAP M9).
using OpenTribes.Gui;
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
        b.UpdateFov(100);
        Assert.Equal(90, b.CurrentFov);
        b.OnAction(MoveBuilder.View, 0f);
        b.UpdateFov(132);
        Assert.Equal(100, b.CurrentFov);
    }

    [Fact]
    public void ZoomStartsAt22AndAHalfAndStepsTheTargetUntilTheZoomIsOn()
    {
        var b = new MoveBuilder();
        Assert.Equal(22.5, b.ZoomTarget);                       // 004040b0: 90 * 0.25
        b.OnAction(MoveBuilder.IncSniperFov, 1f);
        b.UpdateFov(100);
        Assert.Equal(90, b.CurrentFov);
        Assert.Equal(11.25, b.ZoomTarget);
        b.OnAction(MoveBuilder.SniperFov, 1f);
        b.UpdateFov(132);
        Assert.Equal(11.25, b.CurrentFov);
        b.OnAction(MoveBuilder.IncSniperFov, -1f);
        b.UpdateFov(164);
        Assert.Equal(22.5, b.CurrentFov);
        b.OnAction(MoveBuilder.SniperFov, 0f);
        b.UpdateFov(196);
        Assert.Equal(90, b.CurrentFov);
    }

    [Fact]
    public void ZoomAnimatesOverTheZoomSpeedAndStampsTheStepWhileOff()
    {
        var b = new MoveBuilder { ZoomSpeed = () => 0.5 };
        b.LastNow = 1000;
        b.UpdateFov(1000);
        b.OnAction(MoveBuilder.SniperFov, 1f);                  // 90 -> 22.5 from 1000 to 1500
        b.UpdateFov(1250);
        Assert.Equal(56.25, b.CurrentFov, 3);
        b.UpdateFov(1500);
        Assert.Equal(22.5, b.CurrentFov);
        b.OnAction(MoveBuilder.SniperFov, 0f);
        b.UpdateFov(2000);
        Assert.Equal(90, b.CurrentFov);
        b.LastNow = 2600;
        b.OnAction(MoveBuilder.IncSniperFov, 1f);               // zoom off: only the stamp
        Assert.Equal(2600u, b.ZoomStepStamp);
        Assert.Equal(11.25, b.ZoomTarget);
        Assert.Equal(new HudZoom(90, 11.25, 90, 2600, 1500, 2600, 2600), b.HudZoomState());
    }

    [Fact]
    public void ZoomBoxOfTheCrosshairHalvesAsTheZoomDeepens()
    {
        (int x0, _, int x1, _) = GuiCrosshair.ZoomBox(11.25, 640, 480);
        Assert.Equal(10, 640 / (x1 - x0));                      // "ZOOM: 10x"
        (x0, _, x1, _) = GuiCrosshair.ZoomBox(22.5, 640, 480);
        Assert.Equal(5, 640 / (x1 - x0));
    }

    [Fact]
    public void ZoomIsRefusedInThirdPerson()
    {
        var b = new MoveBuilder();
        b.OnAction(MoveBuilder.View, 0f);
        b.OnAction(MoveBuilder.SniperFov, 1f);
        b.UpdateFov(100);
        Assert.False(b.ZoomOn);
        Assert.Equal(90, b.CurrentFov);
    }

    // PlayerPSC_onActionDefault 0x35c46: the pending pitch drops by the player's pitch and the player's pitch is 0 at once.
    [Fact]
    public void CenterViewZeroesThePlayerPitchAndCancelsItInTheMove()
    {
        float pitch = -0.8f;
        var b = new MoveBuilder { TakeControlPitch = () => { float h = pitch; pitch = 0; return h; } };
        b.PitchAcc = 0.1f;
        Assert.True(b.OnAction(MoveBuilder.CenterView, 1f));
        Assert.Equal(0f, pitch);
        Assert.Equal(0.9f, b.PitchAcc, 5);
    }

    [Fact]
    public void CenterViewDoesNothingWithoutAnOwnedObjectOrWithoutLook()
    {
        var b = new MoveBuilder();
        b.PitchAcc = 0.25f;
        b.OnAction(MoveBuilder.CenterView, 1f);
        Assert.Equal(0.25f, b.PitchAcc);
        float pitch = 0.5f;
        b.TakeControlPitch = () => { float h = pitch; pitch = 0; return h; };
        b.CanLook = () => false;
        b.OnAction(MoveBuilder.CenterView, 1f);
        Assert.Equal(0f, pitch);          // the pitch is still set to 0, the move keeps its value
        Assert.Equal(0.25f, b.PitchAcc);
    }
}
