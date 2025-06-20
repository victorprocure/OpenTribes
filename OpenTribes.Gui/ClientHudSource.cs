// docs/gui.md 4.1: the HUD data source of the live client, read from the PSC block and the control object ghost that the
// C3a unpack delivers (client-net.md 4.5). The server and the stream code are not touched.
using OpenTribes.Game;

namespace OpenTribes.Gui;

public sealed class ClientHudSource : IHudSource
{
    private readonly ClientWorld mWorld;
    private readonly Func<double> mClock;

    public ClientHudSource(ClientWorld world, Func<double>? clock = null)
    {
        mWorld = world;
        mClock = clock ?? (() => 0);
    }

    private ClientPsc Psc => mWorld.Psc;
    private ClientGhost? Ghost => mWorld.Ghosts.Get(Psc.ControlGhost);

    private static double D(object? v, double def) => v switch
    {
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        uint u => u,
        _ => def,
    };

    public bool HasPlayerState => Psc.ControlGhost >= 0;

    public double DamageLevel
    {
        get
        {
            if (Ghost is not ClientGhost g)
                return 0;
            if (g.State.ContainsKey("dead"))
                return 1;
            return Math.Clamp(D(g.State.GetValueOrDefault("damage"), 0), 0, 1);
        }
    }

    public double EnergyLevel
    {
        get
        {
            if (Psc.ControlState.TryGetValue("energy", out object? v))
                return Math.Clamp(D(v, 1), 0, 1);
            return Ghost != null && Ghost.State.TryGetValue("energy", out object? e) ? Math.Clamp(D(e, 1), 0, 1) : 1;
        }
    }

    public double Yaw => D(Psc.ControlState.GetValueOrDefault("rotZ"), Ghost != null ? D(Ghost.State.GetValueOrDefault("rotZ"), 0) : 0);
    public double ClockSeconds => mClock();

    public IReadOnlyList<(string Name, bool Mounted)> Weapons
    {
        get
        {
            var list = new List<(string, bool)>();
            for (int i = 0; i < Psc.ItemCounts.Length; ++i)
                if (Psc.ItemCounts[i] > 0 && mWorld.DataBlocks.Blocks.TryGetValue((5, i), out ClientDataBlock? b) && IsWeapon(b))
                    list.Add((Label(b, i), false));
            return list;
        }
    }

    // An item with an image type is a weapon (the datablock flag at +0x11c); ammo and armor have imageType -1.
    private static bool IsWeapon(ClientDataBlock b) => b.Fields.Any(f => f.Name == "imageType" && D(f.Value, -1) >= 0);
    private static string Label(ClientDataBlock b, int i) =>
        b.Fields.FirstOrDefault(f => f.Name == "description" && f.Value is string).Value as string ?? "item " + i;

    public int Ammo => -1;
    public bool SensorPing => false;
    public bool SensorLocked => false;
}
