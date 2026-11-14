// docs/gui.md 4.2: the HUD while riding or piloting a vehicle. Health and energy bars read the control object's vtable +0xf8 / +0xfc
// (a piloted Vehicle or Flier takes both from its control state, Vehicle_readControlState 00446830).
namespace OpenTribes.Tests;

using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;

public class GuiVehicleHudTests
{
    internal const int Vehicle = 5, Pilot = 6, Passenger = 7, Other = 8;

    internal static (ClientWorld World, ClientHudSource Src) Scene(string controlClass, int control)
    {
        var world = new ClientWorld { PlayerId = 0x800 };
        world.Players.TeamCount = 2;
        world.Players.Clients[0x800] = new ClientPlayerRecord { Id = 0x800, Name = "Me", Team = 0 };
        world.Players.Clients[0x801] = new ClientPlayerRecord { Id = 0x801, Name = "Pilot", Team = 1 };
        world.Players.Clients[0x802] = new ClientPlayerRecord { Id = 0x802, Name = "Rider", Team = 1 };
        for (var i = 0; i < world.Psc.Visible.Length; ++i)
            world.Psc.Visible[i] = uint.MaxValue;
        world.Ghosts.Insert(new ClientGhost { Index = Vehicle, Class = "Flier" });
        world.Ghosts.Insert(Body(Pilot, 0x800, 6, "5/1"));
        world.Ghosts.Insert(Body(Passenger, 0x801, 6, "5/2"));
        world.Ghosts.Insert(Body(Other, 0x802, 6, "none"));
        world.Psc.ControlGhost = control;
        world.Psc.ControlClass = controlClass;
        var src = new ClientHudSource(world) { ViewCamera = (w, h) => new Camera3D(0, 0, 0, 0, 0, 90, w, h, 0.1, 1000) };
        return (world, src);
    }

    internal static ClientGhost Body(int index, int owner, double y, string mount)
    {
        var g = new ClientGhost { Index = index, Class = "Player" };
        g.State["pos"] = new[] { 0.0, y, -1.75 };
        g.State["ownerClient"] = (long)owner;
        g.State["mount"] = mount;
        return g;
    }

    [Fact]
    public void PilotHealthBarReadsTheControlStateDamageOfTheVehicle()
    {
        var (world, src) = Scene("Flier", Vehicle);
        world.Ghosts.Get(Vehicle)!.State["damage"] = 0.9;
        world.Psc.ControlState = new Dictionary<string, object?> { ["dmg6"] = 21L };
        Assert.Equal((float)(21 / 63.0), (float)src.DamageLevel);
        world.Psc.ControlState = new Dictionary<string, object?> { ["dmg6"] = 63L };
        Assert.Equal(1.0, src.DamageLevel);
    }

    [Fact]
    public void PilotEnergyBarReadsTheControlStateEnergyOfTheVehicle()
    {
        var (world, src) = Scene("Flier", Vehicle);
        world.Psc.ControlState = new Dictionary<string, object?> { ["E16"] = 65535L };
        Assert.Equal(1.0, src.EnergyLevel);
        world.Psc.ControlState = new Dictionary<string, object?> { ["E16"] = 32768L };
        Assert.Equal((float)(32768 / 65535.0), (float)src.EnergyLevel);
        world.Psc.ControlState = new Dictionary<string, object?> { ["E16"] = 0L };
        Assert.Equal(0.0, src.EnergyLevel);
    }

    [Fact]
    public void PassengerBarsStayThoseOfThePlayer()
    {
        var (world, src) = Scene("Player", Pilot);
        world.Ghosts.Get(Pilot)!.State["damage"] = 0.25;
        world.Psc.ControlState = new Dictionary<string, object?> { ["dmg6"] = 63L, ["E16"] = 0L };
        Assert.Equal(0.25, src.DamageLevel);
    }
}
