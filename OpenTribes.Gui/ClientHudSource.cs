// docs/gui.md 4.1: the HUD data source of the live client, read from the PSC block and the control object ghost that the
// C3a unpack delivers (client-net.md 4.5). The server and the stream code are not touched.
using OpenTribes.Game;
using OpenTribes.Platform.Gfx;

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

    public bool InPlayerView => HasPlayerState && Psc.ControlClass != "ObserverCamera";

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

    // 006a83e4 != -1: the waypoint position and the control object's x and y (+0x188, +0x18c of the transform) [code 004e7e5c].
    public HudWaypoint? Waypoint =>
        mWorld.Players.WaypointTarget != -1 && Psc.ControlState.GetValueOrDefault("pos") is double[] { Length: >= 2 } p
            ? new HudWaypoint(mWorld.Players.WaypointX, mWorld.Players.WaypointY, p[0], p[1])
            : null;

    // The weapon HUD rows [code 004e8fe8]: every item with a count above zero, a hudIcon and showWeaponBar, in datablock order. The
    // item is mounted when an image slot of the control player holds the item (the stream carries the item index); with hiliteOnActive set the
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
                    if (img == i)
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
    public Func<PlayMenuView?>? PlayMenuSource;
    public PlayMenuView? PlayMenu => PlayMenuSource?.Invoke();
    public IReadOnlyList<string?> ObjectiveLines => mWorld.Players.ObjectiveLines;

    public bool SensorPing => false;
    public bool SensorLocked => false;

    // Set by the host: the view camera of the frame, the position of mount point 2 of a player ghost (null: use the body position
    // plus 1.4) and the line test from the eye to a point (true when nothing blocks it).
    public Func<int, int, Camera3D?>? ViewCamera;
    public Func<int, (double X, double Y, double Z)?>? HeadMount;
    public Func<(double X, double Y, double Z), (double X, double Y, double Z), bool>? LineClear;
    // Set by the host: the world box (min, max) and the transform origin of a shape ghost, for the objects of the label pick.
    public Func<int, (double X0, double Y0, double Z0, double X1, double Y1, double Z1, double Px, double Py, double Pz)?>? ObjectBox;

    // The fraction of the segment from the eye along (dx, dy, dz) at which it enters the box (0 when the eye is inside, 1 when it misses).
    private static double EntryFraction((double X, double Y, double Z) eye, double dx, double dy, double dz, (double X, double Y, double Z) lo, (double X, double Y, double Z) hi)
    {
        double t0 = 0, t1 = 1;
        foreach (var (o, d, a, b) in new[] { (eye.X, dx, lo.X, hi.X), (eye.Y, dy, lo.Y, hi.Y), (eye.Z, dz, lo.Z, hi.Z) })
        {
            if (Math.Abs(d) < 1e-12)
            {
                if (o < a || o > b)
                    return 1;
                continue;
            }
            double ta = (a - o) / d, tb = (b - o) / d;
            t0 = Math.Max(t0, Math.Min(ta, tb));
            t1 = Math.Min(t1, Math.Max(ta, tb));
        }
        return t0 > t1 ? 1 : t0;
    }

    // Datablock group of the classes that take part in the label pick: the sensor set holds the Sensors (and their Turrets) and every
    // object with a target index (StaticShape, Item) [code 0053a1f4 callers: Sensor_onAdd, GameBase_onNewDataBlock].
    private static int ObjectGroup(string cls) => cls switch { "Turret" => 19, "Sensor" => 8, "StaticShape" => 4, "Item" => 5, _ => -1 };

    private string? BlockString(ClientGhost g, string field)
    {
        if (ObjectGroup(g.Class) is var grp && grp >= 0 && g.State.TryGetValue("datablock", out object? idx)
            && mWorld.DataBlocks.Blocks.TryGetValue((grp, (int)D(idx, -1)), out ClientDataBlock? b))
            foreach (DecodedField f in b.Fields)
                if (f.Name == field)
                    return f.Value as string;
        return null;
    }

    // Crosshair_findTarget [code 004aca84]: the other players the local player's client may see (the visible mask of the PSC, +0x490),
    // with their marks and the one under the view centre.
    public HudTargetResult? CrosshairTarget(int width, int height)
    {
        if (ViewCamera?.Invoke(width, height) is not Camera3D cam || Psc.ControlGhost < 0 || mWorld.PlayerId == 0)
            return null;
        ClientPlayerManager pm = mWorld.Players;
        ClientPlayerRecord? me = pm.Get(mWorld.PlayerId);
        if (me == null)
            return null;
        var eye = (cam.X, cam.Y, cam.Z);
        var list = new List<HudTargetCandidate>();
        foreach (ClientGhost g in mWorld.Ghosts.All())
        {
            if (g.Class != "Player" || g.Index == Psc.ControlGhost || !g.State.TryGetValue("pos", out object? pv) || pv is not double[] { Length: >= 3 } pos)
                continue;
            int owner = (int)D(g.State.GetValueOrDefault("ownerClient"), 0);
            if (owner < 0x800 || pm.Get(owner) is not ClientPlayerRecord rec)
                continue;
            bool visible = ((owner - 0x800) >> 5) is int w && w < Psc.Visible.Length && (Psc.Visible[w] >> (owner & 31) & 1) != 0;
            var head = HeadMount?.Invoke(g.Index) ?? (pos[0], pos[1], pos[2] + 1.4);
            double bw = 1, bd = 1, bh = 2;
            if (g.State.TryGetValue("datablock", out object? idx) && mWorld.DataBlocks.Blocks.TryGetValue((3, (int)D(idx, -1)), out ClientDataBlock? b))
                foreach (DecodedField f in b.Fields)
                    switch (f.Name)
                    {
                        case "boxWidth": bw = D(f.Value, bw); break;
                        case "boxDepth": bd = D(f.Value, bd); break;
                        case "boxNormalHeight": bh = D(f.Value, bh); break;
                    }
            bool dead = g.State.ContainsKey("dead");
            double damage = dead ? 1 : Math.Clamp(D(g.State.GetValueOrDefault("damage"), 0), 0, 1);
            var top = (head.Item1, head.Item2, head.Item3 + 0.65);
            bool clear = LineClear?.Invoke(eye, (pos[0], pos[1], pos[2] + bh * 0.5)) ?? true;
            list.Add(new HudTargetCandidate(rec.Name, top, (pos[0], pos[1], pos[2]), Math.Min(bw, bd), bh, rec.Team, damage, dead, visible, clear));
        }
        // Objects of the sensor set: the name is the target name of its slot, else the datablock description; the icon is the green
        // bitmap of the datablock's mapIcon [code GameBaseData_preload: "%s_green.bmp"].
        foreach (ClientGhost g in mWorld.Ghosts.All())
        {
            if (ObjectGroup(g.Class) < 0 || ObjectBox?.Invoke(g.Index) is not var (bx0, by0, bz0, bx1, by1, bz1, px, py, pz) || !g.State.ContainsKey("team"))
                continue;
            bool sensor = g.Class is "Sensor" or "Turret";
            if (!sensor && !g.State.ContainsKey("target"))
                continue;
            string name = g.State.TryGetValue("target", out object? ti) && pm.TargetNames.TryGetValue((int)D(ti, -1), out string? tn) ? tn : "";
            if (name.Length == 0)
                name = BlockString(g, "description") ?? "";
            string? icon = BlockString(g, "mapIcon") is { Length: > 0 } mi ? mi + "_green.bmp" : null;
            int team = (int)D(g.State["team"], -1);
            if (team == 15)
                team = -1;
            double dmg = g.State.ContainsKey("damageState") && D(g.State["damageState"], 1) != 1 ? 1 : Math.Clamp(D(g.State.GetValueOrDefault("damage"), 0), 0, 1);
            // [code 004aca84] the label sits on the box centre in x and y and its top in z; the pick box is the smaller of the x and y
            // extents wide and the z extent high; the distance is measured to the transform origin.
            double cx = (bx0 + bx1) * 0.5, cy = (by0 + by1) * 0.5, cz = (bz0 + bz1) * 0.5;
            var top = (cx, cy, bz1);
            // The stock test passes when the first hit of the line from the eye to the box centre is the object itself: stop the ray where it enters the box.
            double ex = cx - eye.Item1, ey = cy - eye.Item2, ez = cz - eye.Item3, k = EntryFraction(eye, ex, ey, ez, (bx0, by0, bz0), (bx1, by1, bz1));
            bool clear = LineClear?.Invoke(eye, (eye.Item1 + ex * k, eye.Item2 + ey * k, eye.Item3 + ez * k)) ?? true;
            list.Add(new HudTargetCandidate(name, top, (px, py, pz), Math.Min(bx1 - bx0, by1 - by0), bz1 - bz0, team, dmg, false, true, clear, true, icon));
        }
        return HudTargetPicker.Find(cam, list, me.Team, pm.TeamCount);
    }
}
