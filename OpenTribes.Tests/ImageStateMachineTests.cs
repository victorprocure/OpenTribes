// Unit tests of the client image state machine (docs/player-sim.md 7.5, docs/audio.md 3.2). Machine without a shape: states,
// timers and sound names only.
namespace OpenTribes.Tests;

using OpenTribes.Client.Scene;

public class ImageStateMachineTests
{
    private static ImageStateMachine Disc(List<string> sounds, bool ammo = true) =>
        new(new ImageStateData { WeaponType = 3, AmmoType = 5, ActivateTime = 0.5, FireTime = 1.25, ReloadTime = 0.25, SpinUpTime = 0.25, SpinDownTime = 0.25 }, null)
        { PlaySound = sounds.Add, Ammo = ammo };

    private static void Run(ImageStateMachine m, double seconds)
    {
        for (double t = 0; t < seconds - 1e-9; t += ImageStateMachine.Step)
            m.Update(ImageStateMachine.Step);
    }

    [Fact]
    public void ANewGhostOfTheDiscLauncherReloadsSpinsUpAndIsReady() // PS 7.5 wt 3: Reset, Reload, SpinUp, Ready
    {
        var sounds = new List<string>();
        ImageStateMachine m = Disc(sounds);
        m.Unpack(false, true, 0, registered: false, initialFire: false);
        Assert.Equal(ImageStateMachine.Reset, m.State);
        m.Update(ImageStateMachine.Step);
        Assert.Equal(ImageStateMachine.Reload, m.State);
        Run(m, 0.3);
        Assert.Equal(ImageStateMachine.SpinUp, m.State);
        Run(m, 0.3);
        Assert.Equal(ImageStateMachine.Ready, m.State);
        Assert.Equal(new[] { "sfxReload", "sfxSpinUp", "sfxReady" }, sounds);
    }

    [Fact]
    public void AMountOnARegisteredPlayerPlaysActivateFirst() // PS 7.5 entry of state 8
    {
        var sounds = new List<string>();
        ImageStateMachine m = Disc(sounds);
        m.Mount();
        Assert.Equal(ImageStateMachine.Activate, m.State);
        Assert.Equal(0.5, m.Timer);
        Run(m, 0.55);
        Assert.Equal(ImageStateMachine.Reload, m.State);
        Assert.Equal("sfxActivate", sounds[0]);
    }

    [Fact]
    public void AChangedFireCountEntersFireThenReloadsAfterTheFireTime() // client-net.md 3.4, PS 7.5 state 1
    {
        var sounds = new List<string>();
        ImageStateMachine m = Disc(sounds);
        m.Unpack(false, true, 0, false, false);
        Run(m, 1.0);
        Assert.Equal(ImageStateMachine.Ready, m.State);
        sounds.Clear();
        var fired = 0;
        m.Fired = () => ++fired;
        m.Unpack(false, true, 0, true, false);
        Assert.Equal(ImageStateMachine.Ready, m.State);
        m.Unpack(false, true, 1, true, false);
        Assert.Equal(ImageStateMachine.Fire, m.State);
        Assert.Equal(1, fired);
        Assert.Equal(1.25, m.Timer);
        Run(m, 1.3);
        Assert.Equal(ImageStateMachine.Reload, m.State);
        Run(m, 0.6);
        Assert.Equal(ImageStateMachine.Ready, m.State);
        Assert.Equal(new[] { "sfxFire", "sfxReload", "sfxSpinUp", "sfxReady" }, sounds);
    }

    [Fact]
    public void AWeaponWithoutAmmoWaitsInNoAmmoUntilTheFlagReturns() // PS 7.5 states 2 and 5
    {
        var sounds = new List<string>();
        ImageStateMachine m = Disc(sounds, ammo: false);
        m.Unpack(false, false, 0, false, false);
        m.Update(ImageStateMachine.Step);
        Assert.Equal(ImageStateMachine.NoAmmo, m.State);
        m.Unpack(false, true, 0, true, false);
        Run(m, 0.1);
        Assert.Equal(ImageStateMachine.Reload, m.State);
        Assert.DoesNotContain("sfxFire", sounds);
    }

    [Fact]
    public void ASpinningWeaponSpinsUpWhileTriggeredAndSpinsDownAfter() // PS 7.5 wt 1
    {
        var sounds = new List<string>();
        var m = new ImageStateMachine(new ImageStateData { WeaponType = 1, AmmoType = 3, FireTime = 0.2, SpinUpTime = 0.5, SpinDownTime = 3 }, null) { PlaySound = sounds.Add, Ammo = true };
        m.Unpack(false, true, 0, false, false);
        m.Update(ImageStateMachine.Step);
        Assert.Equal(ImageStateMachine.Idle, m.State);
        m.Unpack(true, true, 0, true, false);
        Assert.Equal(ImageStateMachine.SpinUp, m.State);
        Run(m, 0.6);
        Assert.Equal(ImageStateMachine.Ready, m.State);
        m.Unpack(true, true, 1, true, false);
        Assert.Equal(ImageStateMachine.Fire, m.State);
        m.Unpack(false, true, 1, true, false);
        Run(m, 0.3);
        Assert.Equal(ImageStateMachine.SpinDown, m.State);
        Assert.Equal(new[] { "sfxSpinUp", "sfxReady", "sfxFire", "sfxSpinDown" }, sounds);
    }

    [Fact]
    public void ALoopingSoundStopsWhenTheStateChanges() // audio.md 3.2
    {
        ImageStateMachine m = Disc([]);
        var stops = 0;
        m.StopLoopingSound = () => ++stops;
        m.Mount();
        m.SetState(ImageStateMachine.Ready);
        Assert.Equal(2, stops);
    }

    [Fact]
    public void ASteppedMachineRunsOnePredictionStepPerUpdateAndIgnoresTime()
    {
        ImageStateMachine m = Disc([]);
        m.Unpack(false, true, 0, false, false);
        m.StepMoves(40);
        Assert.Equal(ImageStateMachine.Ready, m.State);
        m.Unpack(false, true, 1, true, false);
        Assert.Equal(ImageStateMachine.Fire, m.State);
        m.Stepped = true;
        m.Advance(5.0);
        Assert.Equal(ImageStateMachine.Fire, m.State);
        m.StepMoves(39);
        Assert.Equal(ImageStateMachine.Fire, m.State);
        m.StepMoves(2);
        Assert.Equal(ImageStateMachine.Reload, m.State);
    }
}
