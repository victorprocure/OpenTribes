// Client turret pose from the ghost mode (Turret_unpackUpdate 0x0045750c, Turret_updateAim 0x00456874 ghost branch,
// Turret_activate 0x00455b34, Turret_deactivate 0x00455cc4).
namespace OpenTribes.Tests;

using OpenTribes.Game;

public class TurretGhostPoseTests
{
    private static TurretGhostPose New() => new(true, 2.0, false, 1.0, false, 1.0);

    [Fact]
    public void StartsOnPowerAtZero()
    {
        var p = New();
        Assert.False(p.OnFire);
        Assert.Equal(0, p.Position);
        Assert.Equal(0, p.Mode);
    }

    [Fact]
    public void ActivatingRunsPowerForwardThenSwitchesToFire()
    {
        var p = New();
        p.OnMode(2);
        Assert.Equal(0, p.Position); // off -> activating restarts at 0
        for (var i = 0; i < 42; ++i)
            p.Tick(0.05);
        Assert.True(p.OnFire);
        Assert.Equal(1.0, p.Position);
        Assert.Equal(2, p.Mode); // only the server's mode 3 ends it
        p.OnMode(3);
        Assert.Equal(3, p.Mode);
    }

    [Fact]
    public void ModeThreeAdvancesPowerThread()
    {
        var p = New();
        p.OnMode(3);
        p.Tick(1.0);
        Assert.Equal(0.5, p.Position, 6);
        Assert.False(p.OnFire);
    }

    [Fact]
    public void DeactivatingJumpsToEndAndRunsBackToOff()
    {
        var p = New();
        p.OnMode(3);
        p.OnMode(1);
        Assert.False(p.OnFire);
        Assert.Equal(1.0, p.Position);
        p.Tick(1.0);
        Assert.Equal(0.5, p.Position, 6);
        p.Tick(1.0);
        Assert.Equal(0, p.Position);
        Assert.Equal(0, p.Mode);
    }

    [Fact]
    public void ModeZeroIsIgnoredWhileDeactivating()
    {
        var p = New();
        p.OnMode(3);
        p.OnMode(1);
        p.OnMode(0);
        Assert.Equal(1, p.Mode);
    }

    [Fact]
    public void DeactivatingTurnsRotationHomeFirst()
    {
        var p = New();
        p.OnMode(3);
        p.Rotation = 1.0;
        p.OnMode(1);
        p.Tick(0.5);
        Assert.Equal(0.5, p.Rotation, 6);
        Assert.Equal(1.0, p.Position);
        p.Tick(0.6);
        Assert.Equal(0, p.Rotation);
        Assert.Equal(1.0, p.Position);
        p.Tick(1.0);
        Assert.Equal(0.5, p.Position, 6);
    }

    [Fact]
    public void FireCountRestartsFireSequence()
    {
        var p = New();
        p.OnMode(3);
        p.OnFireCount();
        Assert.True(p.OnFire);
        Assert.Equal(0, p.Position);
        p.Tick(0.5);
        Assert.Equal(0.5, p.Position, 6);
    }

    [Fact]
    public void WithoutThreadModeIsStoredAndActivateEndsAtOnce()
    {
        var p = new TurretGhostPose(false, 1, false, 1, false, 1);
        p.OnMode(2);
        Assert.Equal(2, p.Mode);
        p.Tick(0.1);
        Assert.Equal(2, p.Mode);
    }

    [Fact]
    public void ActivationSoundStartsOnceAtThreadPositionZero()
    {
        var p = New();
        p.OnMode(2);
        p.Tick(0.05);
        Assert.Equal(1, p.PowerSound);
        p.Tick(0.05);
        Assert.Equal(0, p.PowerSound);
    }

    [Fact]
    public void NoPowerSoundWithoutThread()
    {
        TurretGhostPose p = new(false, 2.0, false, 1.0, false, 1.0);
        p.OnMode(2);
        p.Tick(0.05);
        Assert.Equal(0, p.PowerSound);
    }

    [Fact]
    public void DeactivationSoundStartsAtThreadPositionOneWhenRotationIsHome()
    {
        var p = New();
        p.OnMode(3);
        p.OnMode(1); // on -> deactivating jumps to the end of the sequence
        Assert.Equal(1.0, p.Position);
        p.Tick(0.05);
        Assert.Equal(-1, p.PowerSound);
        p.Tick(0.05);
        Assert.Equal(0, p.PowerSound);
    }

    [Fact]
    public void DeactivationSoundWaitsForTheRotationToTurnHome()
    {
        var p = New();
        p.OnMode(3);
        p.Rotation = 1.0;
        p.OnMode(1);
        p.Tick(0.05);
        Assert.Equal(0, p.PowerSound);
        for (var i = 0; i < 40 && p.PowerSound == 0; ++i)
            p.Tick(0.05);
        Assert.Equal(-1, p.PowerSound);
    }
}
