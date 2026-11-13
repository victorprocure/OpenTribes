// docs/gui.md 4.2 Crosshair: Crosshair_findTarget 004aca84 skips the control object and the Players mounted on it.
namespace OpenTribes.Tests;

using OpenTribes.Game;
using OpenTribes.Gui;
using static OpenTribes.Tests.GuiVehicleHudTests;

public class GuiVehicleCrosshairTests
{
    [Fact]
    public void PilotCrosshairSkipsThePlayersMountedOnTheVehicle()
    {
        var (world, src) = Scene("Flier", kVehicle);
        world.Ghosts.Get(kOther)!.State["pos"] = new[] { 30.0, 60.0, -1.75 };
        var r = src.CrosshairTarget(640, 480);
        // the pilot's own Player (6) and the passenger (7) sit dead ahead and are not listed, the walking Player (8) is off to the side
        Assert.NotNull(r);
        Assert.Single(r!.Marks);
        Assert.Null(r.Name);
    }

    [Fact]
    public void PassengerCrosshairStillMarksTheOtherRidersOfTheVehicle()
    {
        var (world, src) = Scene("Player", kPilot);
        world.Ghosts.Get(kOther)!.State["pos"] = new[] { 30.0, 60.0, -1.75 };
        var r = src.CrosshairTarget(640, 480);
        Assert.NotNull(r);
        // the control Player (6) is skipped, the Player (7) on the same vehicle is on the other side of the label test
        Assert.Equal("Pilot", r!.Name);
    }
}

public class GuiTurretCrosshairTests
{
    // Crosshair_findTarget 004aca84: the control object is skipped for every object kind, so a controlled Turret under the crosshair has no label.
    [Fact]
    public void ControlledTurretGetsNoLabelButAnotherTurretDoes()
    {
        var (world, src) = Scene("Turret", 20);
        foreach (var body in new[] { 6, 7, 8 })
            world.Ghosts.Get(body)!.State["pos"] = new[] { 0.0, -50.0, -1.75 };   // behind the view
        foreach (var index in new[] { 20, 21 })
        {
            var t = new ClientGhost { Index = index, Class = "Turret" };
            t.State["team"] = 0L;
            t.State["target"] = (long)index;
            world.Ghosts.Insert(t);
            world.Players.TargetNames[index] = "Turret " + index;
        }
        src.ObjectBox = g => g is 20 or 21 ? (-1, 19, -1, 1, 21, 1, 0, 20, 0) : null;
        Assert.Equal("Turret 21", src.CrosshairTarget(640, 480)?.Name);
        world.Psc.ControlGhost = 21;
        Assert.Equal("Turret 20", src.CrosshairTarget(640, 480)?.Name);
    }
}
