// Smoothing and look-ahead of remote Player and Vehicle ghosts (docs/client-net.md 4.4.2): the window arithmetic of 1.11
// (Player_applyControlState 0x004140f7 and 0x00414133, Vehicle_clientSetState 0x0044600d, 0x0044604f and 0x00446071) and the
// snap path.
using OpenTribes.Game;
using static OpenTribes.Tests.Fixtures;

namespace OpenTribes.Tests;

public class RemoteGhostsTests
{
    [Fact]
    public void TickCountIsPredictPlus32OverTickRoundedUp()
    {
        Assert.Equal(1, SmoothWindow.Ticks(0));
        Assert.Equal(2, SmoothWindow.Ticks(1));
        Assert.Equal(2, SmoothWindow.Ticks(32));
        Assert.Equal(3, SmoothWindow.Ticks(33));
        Assert.Equal(3, SmoothWindow.Ticks(64));
        Assert.Equal(0, SmoothWindow.Ticks(-32));          // edi = 0: jbe skips the loop
        Assert.Equal(1, SmoothWindow.Ticks(-1));          // 31
        Assert.Equal(SmoothWindow.MaxTicks, SmoothWindow.Ticks(-33));   // unsigned 0xffffffff, capped
    }

    [Fact]
    public void EndIsNowPlusInterpolatePlus31RoundedDownToATick()
    {
        Assert.Equal(2080u, SmoothWindow.End(2000, 64));
        Assert.Equal(2048u, SmoothWindow.End(1984, 64));    // 1984 + 95 = 2079 -> 2048
        Assert.Equal(1984u + 96, SmoothWindow.End(1984, 65));
        Assert.Equal(1984u, SmoothWindow.End(1984, 0));     // the value is not used with 0: that takes the snap path
        Assert.Equal(1984u, SmoothWindow.Floor(2000));
    }

    [Fact]
    public void SnapPathRules()
    {
        Assert.True(SmoothWindow.PlayerSnap(0, false));
        Assert.True(SmoothWindow.PlayerSnap(64, true));
        Assert.False(SmoothWindow.PlayerSnap(64, false));
        Assert.True(SmoothWindow.VehicleSnap(0));
        Assert.False(SmoothWindow.VehicleSnap(64));         // the vehicle has no snap flag
    }

    private static readonly float[] kStart = { 100, 100, 60 }, kVel = { 0, 0, 0 };

    private static (RemotePlayer, Player) MakePlayer(string name)
    {
        Player p = new ClientPredictionTests.Side(name).Spawn("100 100 60");
        return (new RemotePlayer(p), p);
    }

    private static Move Forward() => new Move { Forward = 1.0f, Turn = 0.05f, Pitch = 0.01f };

    private static void Apply(RemotePlayer rp, uint now, bool snapFlag, int interp, int predict) =>
        rp.Update(now, Forward(), kStart, kVel, false, 0.1f, 0.5f, 3, snapFlag, interp, predict);

    [Fact]
    public void PlayerWindowStartsAtTheOldStateAndEndsOnTheNewOne()
    {
        var (snap, sp) = MakePlayer("rg_ref");
        snap.Place(1000, kStart, kVel, 0.4f, 0.05f, false);
        Apply(snap, 2000, false, 0, 0);                   // the reference: the state after one step
        var (rp, p) = MakePlayer("rg_win");
        rp.Place(1000, kStart, kVel, 0.4f, 0.05f, false);
        Apply(rp, 2000, false, 64, 0);
        Assert.Equal(2080u, rp.End);
        Assert.Equal(992u, rp.SimTime);                   // not reset: the window runs from the last frame
        // The drawn state is the old one until the window runs.
        Assert.Equal(kStart[2], p.Transform.P[2]);
        Assert.Equal(0.4f, p.Rotation[2]);
        Assert.Equal(0.05f, p.Pitch);
        Assert.NotEqual(kStart[2], sp.Transform.P[2]);
        rp.Advance(2000);                                 // 1008 of 1088 ms
        float part = (p.Transform.P[2] - kStart[2]) / (sp.Transform.P[2] - kStart[2]);
        Assert.InRange(part, 1008.0f / 1088 - 1e-3f, 1008.0f / 1088 + 1e-3f);
        rp.Advance(2080);
        for (int i = 0; i < 3; ++i)
            Assert.Equal(sp.Transform.P[i], p.Transform.P[i], 2);
        Assert.Equal(sp.Rotation[2], p.Rotation[2], 4);
        Assert.Equal(sp.Pitch, p.Pitch, 4);
        Assert.Equal(2080u, rp.SimTime);
    }

    [Fact]
    public void PlayerSnapSkipsTheWindow()
    {
        foreach ((bool flag, int interp) in new[] { (false, 0), (true, 64) })
        {
            var (rp, p) = MakePlayer("rg_snap" + interp);
            rp.Place(1000, kStart, kVel, 0.4f, 0.05f, false);
            Apply(rp, 2000, flag, interp, 0);
            Assert.Equal(0u, rp.End);
            Assert.Equal(1984u, rp.SimTime);
            Assert.All(rp.PosRate, r => Assert.Equal(0.0f, r));
            Assert.Equal(0.0f, rp.YawRate);
            Assert.Equal(0.0f, rp.PitchRate);
            Assert.NotEqual(kStart[2], p.Transform.P[2]);  // the drawn state is the new one at once
            Assert.Equal(3, p.TicksWithoutMove);
        }
    }

    [Fact]
    public void PlayerLookAheadRunsPredictPlus32OverTickSteps()
    {
        foreach ((int predict, int steps) in new[] { (0, 1), (32, 2), (64, 3) })
        {
            var (rp, _) = MakePlayer("rg_pred" + predict);
            rp.Place(1000, kStart, kVel, 0.4f, 0.05f, false);
            Apply(rp, 2000, false, 0, predict);
            Assert.Equal(steps, rp.Steps);
        }
    }

    [Fact]
    public void PlayerYawTakesTheShortWayAroundPi()
    {
        var (rp, p) = MakePlayer("rg_yaw");
        rp.Place(1000, kStart, kVel, 3.0f, 0.0f, false);
        rp.Update(2000, new Move(), kStart, kVel, false, 0.0f, -3.0f, 0, false, 64, 0);   // 3.0 -> -3.0 is +0.283 rad
        Assert.True(rp.YawRate > 0);
        Assert.Equal(0.283185f, rp.YawRate * (rp.End - 992), 3);
    }

    [Fact]
    public void FrameStepsTheEchoedMoveForTheTimeLeft()
    {
        var (rp, _) = MakePlayer("rg_loop");
        rp.Place(1000, kStart, kVel, 0.4f, 0.05f, false);
        Apply(rp, 2000, false, 0, 0);                     // SimTime 1984
        long before = rp.Steps;
        rp.Advance(2048);                                 // 1984 -> 2016 -> 2048: two ticks starting on a boundary
        Assert.Equal(before + 2, rp.Steps);
        Assert.Equal(2048u, rp.SimTime);
        rp.Advance(2060);                                 // a tick starting on a boundary, ending inside it
        Assert.Equal(before + 3, rp.Steps);
        rp.Advance(2070);                                 // the rest of that tick: no step
        Assert.Equal(before + 3, rp.Steps);
    }

    private static (RemoteVehicle, Flier) MakeFlier(string name)
    {
        Flier f = new ClientPredictionTests.Side(name).Make("100 100 70");
        return (new RemoteVehicle(f), f);
    }

    private static readonly float[] kRot = { 0.1f, 0.0f, 0.3f }, kPos = { 120, 100, 70 };

    [Fact]
    public void VehicleWindowAndSnap()
    {
        var (snap, sf) = MakeFlier("rg_vref");
        snap.Place(new float[3], new[] { 100f, 100f, 70f });
        snap.SetState(2000, new Move { Forward = 1.0f }, kPos, kRot, 2, 0.0f, 20.0f, 0, 0);
        Assert.Equal(0u, snap.End);
        Assert.Equal(1984u, snap.SimTime);
        Assert.Equal(2, sf.TicksWithoutMove);
        var (rv, f) = MakeFlier("rg_vwin");
        rv.Place(new float[3], new[] { 100f, 100f, 70f });
        rv.SetState(2000, new Move { Forward = 1.0f }, kPos, kRot, 2, 0.0f, 20.0f, 64, 0);
        Assert.Equal(2080u, rv.End);
        Assert.Equal(1984u, rv.SimTime);                  // the vehicle resets its clock to the tick
        Assert.Equal(100.0f, f.Transform.P[0]);
        Assert.Equal(0.0f, f.Rotation[2]);
        rv.Advance(2080);
        for (int i = 0; i < 3; ++i)
        {
            Assert.Equal(sf.Transform.P[i], f.Transform.P[i], 2);
            Assert.Equal(sf.Rotation[i], f.Rotation[i], 4);
        }
        Assert.Equal(sf.SpeedF, f.SpeedF);                // the speeds of the update are used for the steps only
    }

    [Fact]
    public void VehicleLookAheadRunsPredictPlus32OverTickSteps()
    {
        foreach ((int predict, long steps) in new[] { (0, 1L), (32, 2L), (64, 3L) })
        {
            var (rv, _) = MakeFlier("rg_vpred" + predict);
            rv.Place(new float[3], new[] { 100f, 100f, 70f });
            rv.SetState(2000, new Move(), kPos, kRot, 0, 0.0f, 0.0f, 0, predict);
            Assert.Equal(steps, rv.Steps);
        }
    }

    [Fact]
    public void RegistrationSetsTheClockToTheClientTimeNotTheTick()
    {
        // GameBase_onAdd 0x00448108: +0x218 = +0x21c = now, after the unpack that rounded it down.
        var (rp, _) = MakePlayer("rg_reg");
        rp.Place(2000, kStart, kVel, 0.4f, 0.05f, false);
        Assert.Equal(1984u, rp.SimTime);
        rp.Register(2000);
        Assert.Equal(2000u, rp.SimTime);
        Assert.Equal(2000u, rp.LastTime);
        long steps = rp.Steps;
        rp.Advance(2000);                                 // nothing left to run
        Assert.Equal(steps, rp.Steps);
        var (rv, _) = MakeFlier("rg_vreg");
        rv.Place(new float[3], new[] { 100f, 100f, 70f });
        rv.SetState(2000, new Move { Forward = 1.0f }, kPos, kRot, 2, 0.0f, 20.0f, 64, 0);
        rv.Register(2000);
        Assert.Equal(2000u, rv.SimTime);
        rv.Advance(2000);                                 // min(end, now) = now: the window is dropped
        Assert.Equal(0u, rv.End);
    }

    [Fact]
    public void CorpseStepEasesRotationTowardTheGroundSlope()
    {
        // FUN_0041447c: 0.05 rad per tick (31.25 ticks per second) of rotation x and y toward the ground normal, no movement.
        var (rp, p) = MakePlayer("rg_corpse");
        float h = 0;
        Assert.True(p.Game().World.TerrainOf(p)!.TerrainData!.Height(100, 100, ref h));
        p.SetPosition(new[] { 100f, 100f, h + 0.1f });
        p.Dead = true;
        p.SetRotation(new[] { 0.3f, -0.2f, 0.5f });
        float[] before = { p.Transform.P[0], p.Transform.P[1], p.Transform.P[2] };
        p.ClientCorpseStep(0.032f);                       // one tick
        Assert.Equal(0.25f, p.Rotation[0], 4);
        Assert.Equal(-0.15f, p.Rotation[1], 4);
        Assert.Equal(0.5f, p.Rotation[2], 4);
        p.ClientCorpseStep(0.064f);                       // two ticks
        Assert.Equal(0.15f, p.Rotation[0], 4);
        Assert.Equal(-0.05f, p.Rotation[1], 4);
        p.ClientCorpseStep(0.1f);
        Assert.Equal(0.0f, p.Rotation[0], 5);             // stops on the target, no overshoot
        Assert.Equal(0.0f, p.Rotation[1], 5);
        for (int i = 0; i < 3; ++i)
            Assert.Equal(before[i], p.Transform.P[i]);
        p.SetPosition(new[] { 100f, 100f, h + 0.6f });    // more than 0.5 above the ground: untouched
        p.SetRotation(new[] { 0.3f, -0.2f, 0.5f });
        p.ClientCorpseStep(0.032f);
        Assert.Equal(0.3f, p.Rotation[0], 5);
        Assert.Equal(-0.2f, p.Rotation[1], 5);
    }

    [Fact]
    public void AdvanceRunsTheCorpseStepFromTheLastFrame()
    {
        var (rp, p) = MakePlayer("rg_corpse2");
        float h = 0;
        p.Game().World.TerrainOf(p)!.TerrainData!.Height(100, 100, ref h);
        p.SetPosition(new[] { 100f, 100f, h + 0.1f });
        rp.Place(1000, new[] { 100f, 100f, h + 0.1f }, kVel, 0.5f, 0.0f, true);
        rp.Register(1000);
        p.Dead = true;
        p.SetRotation(new[] { 0.3f, 0.0f, 0.5f });
        rp.Advance(1032);                                 // dt = 32 ms: one tick of 0.05
        Assert.Equal(0.25f, p.Rotation[0], 3);
        Assert.Equal(1032u, rp.LastTime);
    }

    [Fact]
    public void MountedPlayerFollowsTheMountPointEveryFrame()
    {
        // Player_applyControlState 0x0041405c and FUN_00414a00 (mounted branch): the transform is the mount point's times the yaw.
        var side = new ClientPredictionTests.Side("rg_mount");
        Player p = side.Spawn("100 100 60");
        Flier f = side.Make("100 100 70");
        f.SetPosition(new[] { 100f, 100f, 70f });
        var rp = new RemotePlayer(p);
        rp.Place(1000, new[] { 99f, 99f, 69f }, kVel, 0.4f, 0.05f, false);
        rp.Register(1000);
        p.MountId = f.Id();
        p.MountPoint = 2;
        rp.Update(2000, Forward(), new[] { 99f, 99f, 69f }, kVel, false, 0.1f, 0.5f, 3, false, 64, 0);
        Assert.Equal(100f, p.Transform.P[0], 3);            // the mount point, not the stream position
        Assert.Equal(70f, p.Transform.P[2], 3);
        f.SetPosition(new[] { 130f, 100f, 70f });
        rp.Advance(2040);
        Assert.Equal(130f, p.Transform.P[0], 3);            // follows the mount as it is now
        f.SetPosition(new[] { 140f, 90f, 70f });
        rp.Advance(2200);                                   // past the end of the window: the stepped state follows too
        Assert.Equal(140f, p.Transform.P[0], 3);
        Assert.Equal(90f, p.Transform.P[1], 3);
    }
}
