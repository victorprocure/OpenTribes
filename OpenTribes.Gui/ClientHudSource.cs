// docs/gui.md 4.1: the HUD data source of the live client, read from the PSC block and the control object ghost that the
// C3a unpack delivers (client-net.md 4.5). The server and the stream code are not touched.
using OpenTribes.Game;
using OpenTribes.Platform.Gfx;
using X87 = OpenTribes.World.X87;

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
    public bool ControlIsPlayer => HasPlayerState && Psc.ControlClass == "Player";
    // Set by the host: PSC +0xa0 on and +0x9c == 0 (MoveBuilder.FirstPersonNow).
    public Func<bool>? FirstPersonSource;
    public bool FirstPersonView => FirstPersonSource?.Invoke() ?? true;
    // Set by the host: the zoom state of the move builder.
    public Func<HudZoom?>? ZoomSource;
    public HudZoom? Zoom => ZoomSource?.Invoke();

    // Vtable +0xf8 of the control object (docs/gui.md 4.1). Player 00413828 returns +0x72c, which the ghost update sets to 1.0 on
    // death (Player_unpackUpdate, mask 0x100) and to the 6 bit damage otherwise. ObserverCamera 004349e4 forwards the call to its
    // orbit object (+0x288, set by the control state reader 00434d60 in mode 3 when the ghost exists) and returns 0.0 without one.
    public double DamageLevel
    {
        get
        {
            // A piloted Vehicle or Flier gets no state updates of its own ghost, its damage level (+0x72c, vtable +0xf8 `00430c80`) is the
            // 6 bit field of the control state, scaled by 1/63 [code 00446830 Vehicle_readControlState, constant at 00446a9c].
            if (Psc.ControlClass is "Vehicle" or "Flier" && Psc.ControlState.TryGetValue("dmg6", out object? dmg6))
                return Math.Clamp((float)(D(dmg6, 0) / 63.0), 0, 1);
            ClientGhost? g = Psc.ControlClass == "ObserverCamera" ? OrbitGhost : Ghost;
            if (g == null)
                return 0;
            if (g.State.ContainsKey("dead"))
                return 1;
            return Math.Clamp(D(g.State.GetValueOrDefault("damage"), 0), 0, 1);
        }
    }

    // The orbit object of an ObserverCamera control object (+0x288): the ghost of mode 3 of the control state.
    private ClientGhost? OrbitGhost =>
        Psc.ControlState.TryGetValue("camMode", out object? m) && D(m, 0) == 3
        && Psc.ControlState.TryGetValue("orbitGhost", out object? og) && D(og, -1) is double idx and >= 0
            ? mWorld.Ghosts.Get((int)idx)
            : null;

    // The energy fraction of the control object: energy / maxEnergy (vtable +0xfc of the control object returns 1 - this and the
    // HUD draws 1 - that, docs/static-sim.md). The control player's PSC block carries the energy as an absolute value (+0x724), the
    // ghost update as a 7 bit fraction; maxEnergy is a field of the object's datablock (group 3, +0x58).
    public double EnergyLevel
    {
        get
        {
            // ObserverCamera vtable +0xfc `00434a04` forwards to its orbit object and answers 0.0 (a full bar) without one.
            if (Psc.ControlClass == "ObserverCamera")
                return OrbitGhost is { } og && og.State.TryGetValue("energy", out object? oe) ? Math.Clamp(D(oe, 1), 0, 1) : 1;
            // A piloted Vehicle or Flier: energy (+0x724) is the 16 bit field of the control state times maxEnergy [code 00446830], and
            // vtable +0xfc `0044b74c` divides it by maxEnergy again, so the fraction is the field over 65535.
            if (Psc.ControlClass is "Vehicle" or "Flier" && Psc.ControlState.TryGetValue("E16", out object? e16))
                return Math.Clamp((float)(D(e16, 0) / 65535.0), 0, 1);
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

    // Vtable +0x178 of the control object (docs/gui.md 4.2 Compass): the Euler rotation of the object's transform (`00413850`, shared by
    // Player, Vehicle and Flier), third float. A piloted vehicle is the control object, so the dial turns with its drawn heading, and
    // a mounted Player (passenger) reads the Euler z of its mount transform: the host's value (ObjectYaw), else the "rot" vector of a
    // vehicle's control state, else the "rotZ" of a Player.
    public Func<int, double?>? ObjectYaw;
    public double Yaw
    {
        get
        {
            if (ObjectYaw?.Invoke(Psc.ControlGhost) is double drawn)
                return drawn;
            if (Psc.ControlClass is "Vehicle" or "Flier")
                return Psc.ControlState.GetValueOrDefault("rot") is double[] { Length: >= 3 } r ? r[2] : 0;
            return D(Psc.ControlState.GetValueOrDefault("rotZ"), Ghost != null ? D(Ghost.State.GetValueOrDefault("rotZ"), 0) : 0);
        }
    }
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
    // Set by the host: the commander map picture (rendered on demand from the world).
    public Func<CommandMapPicture?>? CommandMapSource;
    public CommandMapPicture? CommandMap => CommandMapSource?.Invoke();
    // The slot table of the PSC command block with the datablock fields and records the draw needs [code 004cf94c, 004d04e8].
    public CommandMapMarks? CommandMapMarks
    {
        get
        {
            ClientPlayerManager pm = mWorld.Players;
            if (pm.Get(mWorld.PlayerId) is not ClientPlayerRecord me)
                return null;
            var slots = new List<CommandMapSlotMark>();
            for (int i = 0; i < Psc.CommandSlots.Length; ++i)
            {
                ClientCommandSlot s = Psc.CommandSlots[i];
                if (!s.Present || !mWorld.DataBlocks.Blocks.TryGetValue((s.Group, s.Index), out ClientDataBlock? b))
                    continue;
                string Str(string name) { foreach (DecodedField fld in b.Fields) if (fld.Name == name) return fld.Value as string ?? ""; return ""; }
                int id = i < 128 ? 0x800 + i : -1;
                ClientPlayerRecord? rec = s.Group == 3 && id >= 0 ? pm.Get(id) : null;
                string? target = i >= 128 && pm.TargetNames.TryGetValue(i - 128, out string? tn) ? tn : null;
                slots.Add(new CommandMapSlotMark(i, s.X, s.Y, s.Team, s.Group, id, Str("mapIcon"), Str("description"), target, (int)Fld(b, "mapFilter", -1),
                    (int)Fld(b, "range", 0), Fld(b, "supression", 0) != 0, s.Active, rec?.Name ?? "", rec is { CmdIssuer: not -1, Commander: >= 0 and < 64 } ? rec.Commander : -1, rec?.CmdTarget ?? -1,
                    rec?.CommandStatus ?? 0, rec?.CmdWaypointX ?? 0, rec?.CmdWaypointY ?? 0));
            }
            // (The port keeps the command icon, +0x40, in the field the select-commander event also writes: a record counts as commanded once an order event named an issuer.)
            // The where-am-I mark turns with the control object's yaw: 256 - yaw * 256 / 2 pi [code 004cfc54].
            // (a Player's block carries the z rotation as `rotZ`, an observer camera's the three angles as `rot`)
            double yaw = Psc.ControlState.TryGetValue("rotZ", out object? zv) ? D(zv, 0)
                : Psc.ControlState.TryGetValue("rot", out object? rv) && rv is double[] { Length: >= 3 } rot ? rot[2] : 0;
            int heading = (int)(256.0 - yaw * 256.0 * (1.0 / (2 * Math.PI)));
            return new CommandMapMarks(mWorld.PlayerId, me.Team, heading, slots);
        }
    }
    public Func<string, ChatMenuState?>? NamedMenuSource;
    public ChatMenuState? NamedMenu(string name) => NamedMenuSource?.Invoke(name);

    // The command tree of the local player's team [code 004a85c0, 0040b46c, 004a8548]: the records of the team whose commander is not a
    // human record are the top level (oldest first [I: the client table has no join order, the id order stands for it]), the others hang
    // under their commander; the children of a node are listed while its expanded byte (+0x69, starts at 1) is set. Each row has the status
    // of its command map slot: the damage level is not read from the PSC command block yet (low), the order is the record's command
    // status + 1 [docs/client-net.md 4.5 mode 2]. A row is selected while the record's +0x68 byte is set.
    private ClientPlayerRecord? CommandParent(ClientPlayerRecord r) => r.Commander != -1 && mWorld.Players.Get(r.Commander) is { Ai: false } c ? c : null;

    private List<ClientPlayerRecord> CommandChildren(ClientPlayerRecord? parent, int team) =>
        mWorld.Players.Clients.Values.Where(r => parent == null ? CommandParent(r) == null && r.Team == team : CommandParent(r) == parent).ToList();

    public IReadOnlyList<CommandTeamRow> CommandTeam
    {
        get
        {
            var rows = new List<CommandTeamRow>();
            ClientPlayerManager pm = mWorld.Players;
            if (pm.Get(mWorld.PlayerId) is not ClientPlayerRecord me)
                return rows;
            void Add(List<ClientPlayerRecord> siblings, int depth, List<(int Depth, bool HasNext)> above)
            {
                for (int i = 0; i < siblings.Count; ++i)
                {
                    ClientPlayerRecord r = siblings[i];
                    List<ClientPlayerRecord> kids = CommandChildren(r, me.Team);
                    bool next = i + 1 < siblings.Count;
                    rows.Add(new CommandTeamRow(r.Id, r.Name, depth, kids.Count > 0, r.Expanded, next, CommandDamage(r.Id), r.CommandStatus + 1, above.ToArray(), r.Selected));
                    if (!r.Expanded)
                        continue;
                    var nested = new List<(int Depth, bool HasNext)> { (depth, next) };
                    nested.AddRange(above);
                    Add(kids, depth + 1, nested);
                }
            }
            Add(CommandChildren(null, me.Team), 0, new List<(int Depth, bool HasNext)>());
            return rows;
        }
    }

    // 004d29fc: the status field of the first group 3 slot whose id (+0x350, 0x800 + slot) is that client id, 0 (low) for none.
    private int CommandDamage(int clientId)
    {
        ClientCommandSlot[] slots = Psc.CommandSlots;
        for (int i = 0; i < 128; ++i)
            if (slots[i].Group == 3 && 0x800 + i == clientId)
                return slots[i].Status;
        return 0;
    }

    // 004a98b8 (CommandItemCtrl) and 004aa6b8 (CommandTurretCtrl) prepare: for the local human client, every slot of its team whose datablock group is
    // one the list shows and whose datablock index is set, in slot order. The name is the slot's label when there is one, else the datablock's
    // description; a slot with neither is left out.
    private List<CommandListRow> CommandSlotRows(Func<int, bool> shows)
    {
        var rows = new List<CommandListRow>();
        if (CommandLocal is not CommandLocalClient me)
            return rows;
        for (int i = 0; i < Psc.CommandSlots.Length; ++i)
        {
            ClientCommandSlot s = Psc.CommandSlots[i];
            if (!s.Present || s.Team != me.Team || !shows(s.Group))
                continue;
            string? name = i >= 128 && mWorld.Players.TargetNames.TryGetValue(i - 128, out string? label) && label.Length > 0 ? label : null;
            if (name == null)
            {
                if (!mWorld.DataBlocks.Blocks.TryGetValue((s.Group, s.Index), out ClientDataBlock? b))
                    continue;
                foreach (DecodedField f in b.Fields)
                    if (f.Name == "description" && f.Value is string d && d.Length > 0)
                        name = d;
            }
            if (name != null)
                rows.Add(new CommandListRow(i, name));
        }
        return rows;
    }

    public IReadOnlyList<CommandListRow> CommandObjects => CommandSlotRows(g => g is 4 or 8);
    public IReadOnlyList<CommandListRow> CommandControllables => CommandSlotRows(g => g is 0x13 or 10 or 0xb or 9);

    // 004b48bc: the walk over the client records takes the first with the selected byte (+0x68) set. [I: the client table has no join order, the id order stands for it]
    public int CommandFirstSelectedClient() => mWorld.Players.Clients.Values.OrderBy(r => r.Id).FirstOrDefault(r => r.Selected)?.Id ?? -1;

    public CommandLocalClient? CommandLocal =>
        mWorld.Players.Get(mWorld.PlayerId) is { Ai: false, Team: var team } ? new CommandLocalClient(mWorld.PlayerId, team) : null;

    public ClientCommandSlot[]? CommandSlotTable => Psc.CommandSlots;

    public int? CommandBlockFilter(int group, int index) =>
        mWorld.DataBlocks.Blocks.TryGetValue((group, index), out ClientDataBlock? b) ? (int)Fld(b, "mapFilter", -1) : null;

    public string? CommandClientName(int clientId) => mWorld.Players.Get(clientId) is { Ai: false } r ? r.Name : null;

    public string? CommandTargetName(int slot) => slot >= 128 && mWorld.Players.TargetNames.TryGetValue(slot - 128, out string? n) ? n : null;

    public void CommandSetSelected(int clientId, bool on)
    {
        if (mWorld.Players.Get(clientId) is { } r)
            r.Selected = on;
    }

    public void CommandToggleExpanded(int clientId)
    {
        if (mWorld.Players.Get(clientId) is { } r)
            r.Expanded = !r.Expanded;
    }

    public bool CommandInChain(int clientId, int commander)
    {
        for (ClientPlayerRecord? r = mWorld.Players.Get(clientId); r != null; r = mWorld.Players.Get(r.Commander) is { Ai: false } c ? c : null)
            if (r.Commander == commander)
                return true;
        return false;
    }

    // +0x74 is the first record under this one in the tree (the oldest [I as for the rows]), +0x78 the next one under the same commander.
    public (int FirstChild, int NextSibling, bool Selected)? CommandLinks(int clientId)
    {
        ClientPlayerManager pm = mWorld.Players;
        if (pm.Get(clientId) is not { } r || pm.Get(mWorld.PlayerId) is not { } me)
            return null;
        int first = CommandChildren(r, me.Team) is { Count: > 0 } kids ? kids[0].Id : -1;
        List<ClientPlayerRecord> siblings = CommandChildren(CommandParent(r), r.Team);
        int at = siblings.IndexOf(r);
        return (first, at >= 0 && at + 1 < siblings.Count ? siblings[at + 1].Id : -1, r.Selected);
    }
    public IReadOnlyList<string?> ObjectiveLines => mWorld.Players.ObjectiveLines;

    // The 2 sensor state bits of the control state (00413b90, Vehicle 00446830): 1 pinged, 2 jammed.
    private int SensorState => (int)D(Psc.ControlState.GetValueOrDefault("state2"), 0);
    public bool SensorPing => SensorState == 1;
    public bool SensorLocked => SensorState == 2;

    // Set by the host: the view camera of the frame, the position of mount point 2 of a player ghost (null: use the body position
    // plus 1.4) and the line test from the eye to a point (true when nothing blocks it).
    public Func<int, int, Camera3D?>? ViewCamera;
    public Func<int, (double X, double Y, double Z)?>? HeadMount;
    // Set by the host: the muzzle of the image in slot 0 of a Player ghost in world space (null: use the body position).
    public Func<int, (double X, double Y, double Z)?>? MuzzleMount;
    // Set by the host: the eye and muzzle transforms of a Vehicle, Flier or Turret control ghost (null while it has no drawn object).
    public Func<int, HudControlAim?>? ControlAim;
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

    // The ghost index of the object a Player ghost is mounted on (the "ghost/point" stream field), -1 when it has none.
    private static int MountGhost(ClientGhost g) =>
        g.State.GetValueOrDefault("mount") is string m && m.IndexOf('/') is int i and > 0 && int.TryParse(m.AsSpan(0, i), out int ghost) ? ghost : -1;

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
            // Crosshair_findTarget skips the control object and every Player whose mount object (+0x1028) is the control object: while
            // piloting, the pilot's own Player and the passengers of the vehicle get no mark and no label [code 004aca84].
            if (g.Class != "Player" || g.Index == Psc.ControlGhost || MountGhost(g) == Psc.ControlGhost
                || !g.State.TryGetValue("pos", out object? pv) || pv is not double[] { Length: >= 3 } pos)
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

    // ---- aim triangles (004ada90) ----

    private static double Fld(ClientDataBlock? b, string name, double def)
    {
        if (b != null)
            foreach (DecodedField f in b.Fields)
                if (f.Name == name)
                    return D(f.Value, def);
        return def;
    }

    // Player vtable +0x114 [code 00412218]: the projectile datablock (group, index) of the image in slot 0: ItemData -> imageType ->
    // ItemImageData projectileType (two 32 bit words: group at +0x70, index at +0x74). Null without an image or a projectile.
    private ClientDataBlock? WeaponProjectile(ClientGhost g, out int group)
    {
        group = -1;
        double item = D(g.State.GetValueOrDefault("item0"), -1);
        if (item < 0
            || !mWorld.DataBlocks.Blocks.TryGetValue((5, (int)item), out ClientDataBlock? ib)
            || !mWorld.DataBlocks.Blocks.TryGetValue((6, (int)Fld(ib, "imageType", -1)), out ClientDataBlock? img))
            return null;
        return ProjectileOf(img, out group);
    }

    // The projectile datablock a `projectileType` field names (two 32 bit words, group then index), null when it is unset.
    private ClientDataBlock? ProjectileOf(ClientDataBlock img, out int group)
    {
        group = -1;
        string raw = img.Fields.FirstOrDefault(f => f.Name == "projectileType").Value as string ?? "";
        if (raw.Length < 16)
            return null;
        byte[] bytes = Convert.FromHexString(raw);
        int grp = BitConverter.ToInt32(bytes, 0), idx = BitConverter.ToInt32(bytes, 4);
        if (grp == -1)
            return null;
        group = grp;
        return mWorld.DataBlocks.Blocks.TryGetValue((grp, idx), out ClientDataBlock? pb) ? pb : null;
    }

    // The gravity of the control player: force 1 of its SimMovement, the world gravity of the terrain ghost; minus its length [code 004adb98].
    private float Gravity()
    {
        foreach (ClientGhost t in mWorld.Ghosts.All())
            if (t.State.GetValueOrDefault("contGravity") is double[] { Length: >= 3 } v)
                return X87.F32(-X87.Len(new[] { (float)v[0], (float)v[1], (float)v[2] }));
        return 0f;
    }

    // Player vtable +0x114 [code 00412218] gives the projectile of the image in slot 0, Turret vtable +0x114 [code 00456074] the
    // projectileType (group at +0x120, index at +0x124) of its TurretData, Vehicle and Flier [code 00430c88] nothing. Null without one.
    private ClientDataBlock? TurretProjectile(ClientGhost g, out int group)
    {
        group = -1;
        if (!g.State.TryGetValue("datablock", out object? di) || !mWorld.DataBlocks.Blocks.TryGetValue((19, (int)D(di, -1)), out ClientDataBlock? tb))
            return null;
        return ProjectileOf(tb, out group);
    }

    // FUN_004ada90 [code 004ada90]: for every target of set 725 (objects with the target flag and enabled, the hit point of a targeting laser)
    // of the control object's team the datablock of its weapon solves the aim from the eye (low arc, then the high arc); the
    // direction x 2 from the eye is projected and gets a triangle, blue when the projectile reaches (0xfb), else 0xf1, with the distance
    // from the muzzle under it. Without a projectile datablock the solution is the straight line, never reaching. The routine casts the
    // control object to ShapeBase [code 004adb1a..004adb37]: it runs for a Player, a Vehicle, a Flier and a Turret alike and only the
    // virtual calls differ (eye +0x110, muzzle +0x10c, projectile +0x114, team +0x20c).
    public IReadOnlyList<HudTargetMarker> TargetMarkers(int width, int height)
    {
        var result = new List<HudTargetMarker>();
        string cls = Psc.ControlClass;
        if (Ghost is not ClientGhost me || cls is not ("Player" or "Vehicle" or "Flier" or "Turret") || ViewCamera?.Invoke(width, height) is not Camera3D cam
            || mWorld.PlayerId == 0 || mWorld.Players.Get(mWorld.PlayerId) is not ClientPlayerRecord rec)
            return result;
        ClientDataBlock? block;
        int group, ownTeam;
        float[] eye;
        (double, double, double) muzzle;
        if (cls == "Player")
        {
            block = WeaponProjectile(me, out group);
            ownTeam = rec.Team;
            eye = new[] { (float)cam.X, (float)cam.Y, (float)cam.Z };
            bool hasItem = D(me.State.GetValueOrDefault("item0"), -1) >= 0;
            var body = me.State.GetValueOrDefault("pos") is double[] { Length: >= 3 } bp ? (bp[0], bp[1], bp[2]) : (cam.X, cam.Y, cam.Z);
            muzzle = hasItem ? MuzzleMount?.Invoke(me.Index) ?? body : body;
        }
        else
        {
            // +0x20c of the control object: the team of its own ghost. Without the drawn object the eye and muzzle are not known.
            if (!me.State.TryGetValue("team", out object? tv) || ControlAim?.Invoke(me.Index) is not HudControlAim aim)
                return result;
            ownTeam = (int)D(tv, -1);
            group = -1;
            block = cls == "Turret" ? TurretProjectile(me, out group) : null;
            eye = aim.Eye;
            muzzle = aim.MuzzleLabelPoint();
        }
        float speed = group == Projectile.kRocketGroup ? (float)Fld(block, "field@0x6c", 0) : group is Projectile.kBulletGroup or Projectile.kGrenadeGroup or Projectile.kSeekerGroup
            ? (float)Fld(block, "field@0x68", 0) : 1.0e7f;
        float totalTime = (float)Fld(block, "totalTime", 10000);
        float g = Gravity();
        foreach (ClientGhost t in mWorld.Ghosts.All())
        {
            double[]? pos = null;
            int team = int.MinValue;
            if (t.Class == "TargetLaser")
            {
                if (D(t.State.GetValueOrDefault("hit"), 0) == 0 || t.State.GetValueOrDefault("end") is not double[] { Length: >= 3 } end)
                    continue;
                pos = end;
                team = (int)D(t.State.GetValueOrDefault("team"), -1);
            }
            else if (D(t.State.GetValueOrDefault("isTarget"), 0) != 0 && D(t.State.GetValueOrDefault("damageState"), 1) == 1
                && ObjectBox?.Invoke(t.Index) is var (bx0, by0, bz0, bx1, by1, bz1, _, _, _) && t.State.TryGetValue("team", out object? tv))
            {
                // StaticBase vtable +0x118 [code 0045047c]: the centre of the world box (+0x70, +0x7c) and the team (+0x20c).
                pos = new[] { (bx0 + bx1) * 0.5, (by0 + by1) * 0.5, (bz0 + bz1) * 0.5 };
                team = (int)D(tv, -1);
            }
            if (pos == null || team != ownTeam)
                continue;
            float[] target = { (float)pos[0], (float)pos[1], (float)pos[2] };
            var dirLow = new float[3];
            var dirHigh = new float[3];
            bool okLow = false, okHigh = false, haveLow, haveHigh = false;
            if (block == null)
            {
                float len = X87.F32(X87.Len(new[] { target[0] - eye[0], target[1] - eye[1], target[2] - eye[2] }));
                float k = X87.F32(1.0f / len);
                for (int i = 0; i < 3; ++i)
                    dirLow[i] = X87.F32(k * (target[i] - eye[i]));
                haveLow = true;
            }
            else
            {
                haveLow = AIAim.AiSolveAim(group, speed, totalTime, eye, target, false, g, ref okLow, dirLow);
                haveHigh = AIAim.AiSolveAim(group, speed, totalTime, eye, target, true, g, ref okHigh, dirHigh);
            }
            if (!haveLow)
                continue;
            HudMarkPoint? Point(float[] dir, bool ok)
            {
                double px = (float)(eye[0] + (float)(dir[0] * 2.0f)), py = (float)(eye[1] + (float)(dir[1] * 2.0f)), pz = (float)(eye[2] + (float)(dir[2] * 2.0f));
                return cam.Project(px, py, pz, out double sx, out double sy, out _) ? new HudMarkPoint(HudMarkers.Pixel(sx), HudMarkers.Pixel(sy), ok) : null;
            }
            HudMarkPoint? low = Point(dirLow, okLow), high = haveHigh ? Point(dirHigh, okHigh) : null;
            result.Add(new HudTargetMarker(low, high, HudMarkers.Label(HudMarkers.Distance(muzzle, (target[0], target[1], target[2])))));
        }
        return result;
    }
}
