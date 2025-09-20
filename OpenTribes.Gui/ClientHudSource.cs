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

    // The energy fraction of the control object: energy / maxEnergy (vtable +0xfc of the control object returns 1 - this and the
    // HUD draws 1 - that, docs/static-sim.md). The control player's PSC block carries the energy as an absolute value (+0x724), the
    // ghost update as a 7 bit fraction; maxEnergy is a field of the object's datablock (group 3, +0x58).
    public double EnergyLevel
    {
        get
        {
            if (Psc.ControlState.TryGetValue("energy", out object? v))
                return Fraction(D(v, 1));
            return Ghost != null && Ghost.State.TryGetValue("energy", out object? e) ? Math.Clamp(D(e, 1), 0, 1) : 1;
        }
    }

    public double MaxEnergy
    {
        get
        {
            if (Ghost != null && Ghost.State.TryGetValue("datablock", out object? idx) &&
                mWorld.DataBlocks.Blocks.TryGetValue((3, (int)D(idx, -1)), out ClientDataBlock? b))
                foreach (DecodedField f in b.Fields)
                    if (f.Name == "maxEnergy" && D(f.Value, 0) > 0)
                        return D(f.Value, 0);
            return 0;
        }
    }

    private double Fraction(double energy)
    {
        double max = MaxEnergy;
        return Math.Clamp(max > 0 ? energy / max : energy, 0, 1);
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

    // The item datablocks (group 5) the lists show: the ones with showInventory, in index order [code 0048afcc, 0048b05c].
    public IReadOnlyList<InventoryEntry> InventoryItems
    {
        get
        {
            var list = new List<InventoryEntry>();
            if (!mWorld.DataBlocks.Counts.TryGetValue(5, out int n))
                return list;
            for (int i = 0; i < n && i < Psc.ItemCounts.Length; ++i)
            {
                if (!mWorld.DataBlocks.Blocks.TryGetValue((5, i), out ClientDataBlock? b) ||
                    !b.Fields.Any(f => f.Name == "showInventory" && D(f.Value, 0) != 0))
                    continue;
                string S(string name) => b.Fields.FirstOrDefault(f => f.Name == name && f.Value is string).Value as string ?? "";
                int price = (int)b.Fields.Where(f => f.Name == "price").Select(f => D(f.Value, 0)).FirstOrDefault();
                list.Add(new InventoryEntry(i, S("heading"), S("description"), price, Psc.ItemCounts[i],
                    Bit(Psc.Shopping, i), Bit(Psc.Buying, i)));
            }
            return list;
        }
    }

    private static bool Bit(uint[] mask, int i) => i / 32 < mask.Length && (mask[i / 32] >> (i % 32) & 1) != 0;

    // The scoreboard from the client player manager: teams by score, clients by score, both highest first.
    public ScoreBoard? Scores
    {
        get
        {
            ClientPlayerManager pm = mWorld.Players;
            var teams = pm.Teams.Values.OrderByDescending(t => t.SortValue).ThenBy(t => t.Index)
                .Select(t => new ScoreRow(t.Index, Expand(t.ScoreText, null, t.Name), t.SortValue)).ToList();
            var clients = pm.Clients.Values.Where(c => !c.Ai).OrderByDescending(c => c.SortValue).ThenBy(c => c.Id)
                .Select(c => new ScoreRow(c.Id, Expand(c.ScoreText, c, c.Team >= 0 && pm.Teams.TryGetValue(c.Team, out ClientTeam? t) ? t.Name : ""),
                    c.SortValue)).ToList();
            return new ScoreBoard(pm.TeamHeading, pm.ClientHeading, teams, clients, mWorld.PlayerId);
        }
    }

    // The placeholders of a score string [code 004c2198]: %n the player name, %t the team name, %p the ping, %l the loss in percent.
    public static string Expand(string text, ClientPlayerRecord? player, string teamName)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < text.Length; ++i)
        {
            if (text[i] == '%' && i + 1 < text.Length)
            {
                switch (text[i + 1])
                {
                    case 'n' when player != null: sb.Append(player.Name); ++i; continue;
                    case 't': sb.Append(teamName); ++i; continue;
                    case 'p' when player != null: sb.Append(player.PingMs); ++i; continue;
                    case 'l' when player != null: sb.Append((int)(player.Loss * 100)); ++i; continue;
                }
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    // Set by the host: the chat menu a script switched on (the server menu of the Tab key).
    public Func<ServerMenuView?>? MenuSource;
    public ServerMenuView? ServerMenu => MenuSource?.Invoke();

    public int Ammo => -1;
    public bool SensorPing => false;
    public bool SensorLocked => false;
}
