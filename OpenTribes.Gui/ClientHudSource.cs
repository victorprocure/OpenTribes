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

    // The weapon HUD rows [code 004e8fe8]: every item with a count above zero, a hudIcon and showWeaponBar, in datablock order. The
    // item is mounted when an image slot of the control player holds its image (the item's imageType); with hiliteOnActive set the
    // "on" icon shows only while that slot fires (state 1). The ammo is the count of the image's ammoType item, negative without one.
    public IReadOnlyList<WeaponRow> Weapons
    {
        get
        {
            var list = new List<WeaponRow>();
            if (Ghost is not ClientGhost g)
                return list;
            var slots = MountedImages(g);
            for (int i = 0; i < Psc.ItemCounts.Length; ++i)
            {
                if (Psc.ItemCounts[i] <= 0 || !mWorld.DataBlocks.Blocks.TryGetValue((5, i), out ClientDataBlock? b))
                    continue;
                string icon = b.Fields.FirstOrDefault(f => f.Name == "hudIcon" && f.Value is string).Value as string ?? "";
                if (icon.Length == 0 || !b.Fields.Any(f => f.Name == "showWeaponBar" && D(f.Value, 0) != 0))
                    continue;
                int image = (int)b.Fields.Where(f => f.Name == "imageType").Select(f => D(f.Value, -1)).FirstOrDefault(-1);
                bool hilite = b.Fields.Any(f => f.Name == "hiliteOnActive" && D(f.Value, 0) != 0);
                bool active = false;
                foreach (var (img, state) in slots)
                    if (img == image && image >= 0)
                    {
                        active = !hilite || state == 1;
                        break;
                    }
                int ammo = -1;
                if (image >= 0 && mWorld.DataBlocks.Blocks.TryGetValue((6, image), out ClientDataBlock? ib))
                {
                    int type = (int)ib.Fields.Where(f => f.Name == "ammoType").Select(f => D(f.Value, -1)).FirstOrDefault(-1);
                    if (type >= 0 && type < Psc.ItemCounts.Length)
                        ammo = Psc.ItemCounts[type];
                }
                list.Add(new WeaponRow(icon, active, ammo));
            }
            return list;
        }
    }

    // The (image, state) of each filled image slot of a Player ghost: the strings "i:img=N state=S ..." of the unpack.
    private static List<(int Image, int State)> MountedImages(ClientGhost g)
    {
        var l = new List<(int, int)>();
        if (g.State.GetValueOrDefault("images") is not System.Collections.IEnumerable e || g.State.GetValueOrDefault("images") is string)
            return l;
        foreach (object? o in e)
            if (o is string s && Num(s, "img=", out int img))
            {
                Num(s, "state=", out int st);
                l.Add((img, st));
            }
        return l;
    }

    private static bool Num(string s, string key, out int v)
    {
        v = -1;
        int p = s.IndexOf(key, StringComparison.Ordinal);
        if (p < 0)
            return false;
        p += key.Length;
        int e = p;
        while (e < s.Length && (char.IsDigit(s[e]) || s[e] == '-'))
            ++e;
        return int.TryParse(s.AsSpan(p, e - p), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out v);
    }

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
            var teams = pm.Teams.Values.Where(t => t.Index >= 0).OrderByDescending(t => t.SortValue).ThenBy(t => t.Index)
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

    public bool SensorPing => false;
    public bool SensorLocked => false;
}
