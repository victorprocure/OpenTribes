// The control player's sample for its ghost visual (docs/audio.md 3.2), synthetic control state (R5, R9).
namespace OpenTribes.Tests;

using OpenTribes.Client.Scene;
using OpenTribes.Game;

public class ControlSampleTests
{
    private static ClientPsc Psc(string cls = "Player", int ghost = 5)
    {
        var psc = new ClientPsc { ControlGhost = ghost, ControlClass = cls };
        psc.ControlState["pos"] = new[] { 1.0, 2.0, 3.0 };
        psc.ControlState["vel"] = new[] { 4.0, 5.0, 6.0 };
        psc.ControlState["rotZ"] = 0.5;
        psc.ControlState["flag1f4"] = 1L;
        psc.ControlState["flagfe4"] = 0L;
        psc.ControlState["crouchfee"] = 1L;
        return psc;
    }

    [Fact]
    public void ControlStateGivesPositionVelocityAndFlags()
    {
        var c = ControlSample.FromControlState(Psc(), 5)!.Value;
        Assert.Equal((1.0, 2.0, 3.0), (c.X, c.Y, c.Z));
        Assert.Equal((4.0, 5.0, 6.0), (c.Vx, c.Vy, c.Vz));
        Assert.Equal(0.5, c.Yaw);
        Assert.True(c.Contact);
        Assert.False(c.Jetting);
        Assert.True(c.Crouch);
    }

    [Fact]
    public void OtherGhostsAndControlClassesGiveNothing()
    {
        Assert.Null(ControlSample.FromControlState(Psc(), 6));
        Assert.Null(ControlSample.FromControlState(Psc("ObserverCamera"), 5));
    }

    [Fact]
    public void NoPositionGivesNothingAndNoVelocityIsZero()
    {
        var psc = Psc();
        psc.ControlState.Remove("vel");
        Assert.Equal(0.0, ControlSample.FromControlState(psc, 5)!.Value.Vz);
        psc.ControlState.Remove("pos");
        Assert.Null(ControlSample.FromControlState(psc, 5));
    }
}
