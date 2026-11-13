namespace OpenTribes.Gui;

using OpenTribes.Game;
using OpenTribes.Platform.Gfx;
using X87 = OpenTribes.World.X87;

/// <summary>The HUD data source of the live client, read from the PSC block and the control object ghost that the C3a unpack delivers (docs/gui.md 4.1, client-net.md 4.5).</summary>
public sealed class ClientHudSource : IHudSource
{
    private readonly ClientWorld _world;
    private readonly Func<double> _clock;

    /// <summary>Creates a source over a client world.</summary>
    /// <param name="world">The client world.</param>
    /// <param name="clock">The HUD timer clock in seconds, or null for 0.</param>
    public ClientHudSource(ClientWorld world, Func<double>? clock = null)
    {
        _world = world;
        _clock = clock ?? (() => 0);
    }

    private ClientPsc Psc => _world.Psc;
    private ClientGhost? Ghost => _world.Ghosts.Get(Psc.ControlGhost);

    private static double D(object? v, double def) => v switch
    {
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        uint u => u,
        _ => def,
    };

    /// <inheritdoc/>
    public bool HasPlayerState => Psc.ControlGhost >= 0;

    /// <inheritdoc/>
    public bool InPlayerView => HasPlayerState && Psc.ControlClass != "ObserverCamera";
    /// <inheritdoc/>
    public bool SensorHudShown => HasPlayerState && Psc.ControlClass is "Player" or "Vehicle" or "Flier";
    /// <inheritdoc/>
    public bool ControlIsPlayer => HasPlayerState && Psc.ControlClass == "Player";
    /// <summary>Gets or sets the callback for the first person state: PSC +0xa0 on and +0x9c at 0 (MoveBuilder.FirstPersonNow). Set by the host.</summary>
    public Func<bool>? FirstPersonSource { get; set; }
    /// <inheritdoc/>
    public bool FirstPersonView => FirstPersonSource?.Invoke() ?? true;
    /// <summary>Gets or sets the callback for the zoom state of the move builder. Set by the host.</summary>
    public Func<HudZoom?>? ZoomSource { get; set; }
    /// <inheritdoc/>
    public HudZoom? Zoom => ZoomSource?.Invoke();

    /// <inheritdoc/>
    /// <remarks>Follows vtable +0xf8 of the control object (docs/gui.md 4.1). Player 00413828 returns +0x72c, which the ghost update sets to 1.0 on death (Player_unpackUpdate, mask 0x100) and to the 6 bit damage otherwise. ObserverCamera 004349e4 forwards the call to its orbit object (+0x288, set by the control state reader 00434d60 in mode 3 when the ghost exists) and returns 0.0 without one.</remarks>
    public double DamageLevel
    {
        get
        {
            // A piloted Vehicle or Flier gets no state updates of its own ghost, its damage level (+0x72c, vtable +0xf8 `00430c80`) is the
            // 6 bit field of the control state, scaled by 1/63 [code 00446830 Vehicle_readControlState, constant at 00446a9c].
            if (Psc.ControlClass is "Vehicle" or "Flier" && Psc.ControlState.TryGetValue("dmg6", out var dmg6))
                return Math.Clamp((float)(D(dmg6, 0) / 63.0), 0, 1);
            var g = Psc.ControlClass == "ObserverCamera" ? OrbitGhost : Ghost;
            if (g == null)
                return 0;
            if (g.State.ContainsKey("dead"))
                return 1;
            return Math.Clamp(D(g.State.GetValueOrDefault("damage"), 0), 0, 1);
        }
    }

    // The orbit object of an ObserverCamera control object (+0x288): the ghost of mode 3 of the control state.
    private ClientGhost? OrbitGhost =>
        Psc.ControlState.TryGetValue("camMode", out var m) && D(m, 0) == 3
        && Psc.ControlState.TryGetValue("orbitGhost", out var og) && D(og, -1) is double idx and >= 0
            ? _world.Ghosts.Get((int)idx)
            : null;

    /// <inheritdoc/>
    /// <remarks>The energy fraction of the control object is energy / maxEnergy (vtable +0xfc of the control object returns 1 - this and the HUD draws 1 - that, docs/static-sim.md). The control player's PSC block carries the energy as an absolute value (+0x724), the ghost update as a 7 bit fraction. maxEnergy is a field of the object's datablock (group 3, +0x58).</remarks>
    public double EnergyLevel
    {
        get
        {
            // ObserverCamera vtable +0xfc `00434a04` forwards to its orbit object and answers 0.0 (a full bar) without one.
            if (Psc.ControlClass == "ObserverCamera")
                return OrbitGhost is { } og && og.State.TryGetValue("energy", out var oe) ? Math.Clamp(D(oe, 1), 0, 1) : 1;
            // A piloted Vehicle or Flier: energy (+0x724) is the 16 bit field of the control state times maxEnergy [code 00446830], and
            // vtable +0xfc `0044b74c` divides it by maxEnergy again, so the fraction is the field over 65535.
            if (Psc.ControlClass is "Vehicle" or "Flier" && Psc.ControlState.TryGetValue("E16", out var e16))
                return Math.Clamp((float)(D(e16, 0) / 65535.0), 0, 1);
            if (Psc.ControlState.TryGetValue("energy", out var v))
                return Fraction(D(v, 1));
            return Ghost != null && Ghost.State.TryGetValue("energy", out var e) ? Math.Clamp(D(e, 1), 0, 1) : 1;
        }
    }

    /// <summary>Gets the maximum energy of the control object's datablock, 0 when unknown.</summary>
    public double MaxEnergy
    {
        get
        {
            if (Ghost != null && Ghost.State.TryGetValue("datablock", out var idx) &&
                _world.DataBlocks.Blocks.TryGetValue((3, (int)D(idx, -1)), out var b))
                foreach (var f in b.Fields)
                    if (f.Name == "maxEnergy" && D(f.Value, 0) > 0)
                        return D(f.Value, 0);
            return 0;
        }
    }

    /// <summary>Converts an energy value to a fraction of the maximum.</summary>
    /// <param name="energy">The energy value.</param>
    /// <returns>The fraction, 0 to 1.</returns>
    private double Fraction(double energy)
    {
        var max = MaxEnergy;
        return Math.Clamp(max > 0 ? energy / max : energy, 0, 1);
    }

    /// <summary>Gets or sets the callback for the drawn yaw of a ghost by index, null when it is not drawn. Set by the host.</summary>
    /// <remarks>A mounted Player (passenger) reads the Euler z of its mount transform through it.</remarks>
    public Func<int, double?>? ObjectYaw { get; set; }
    /// <inheritdoc/>
    /// <remarks>Follows vtable +0x178 of the control object (docs/gui.md 4.2 Compass): the third float of the Euler rotation of the object's transform (00413850, shared by Player, Vehicle and Flier). A piloted vehicle is the control object, so the dial turns with its drawn heading. Without the drawn value it reads the "rot" vector of a vehicle's control state, else the "rotZ" of a Player.</remarks>
    public double Yaw
    {
        get
        {
            if (ObjectYaw?.Invoke(Psc.ControlGhost) is double drawn)
                return drawn;
            if (Psc.ControlClass == "ObserverCamera")
                return ObserverYaw();
            if (Psc.ControlClass is "Vehicle" or "Flier")
                return Psc.ControlState.GetValueOrDefault("rot") is double[] { Length: >= 3 } r ? r[2] : 0;
            return D(Psc.ControlState.GetValueOrDefault("rotZ"), Ghost != null ? D(Ghost.State.GetValueOrDefault("rotZ"), 0) : 0);
        }
    }
    /// <summary>Returns the yaw of an observer camera.</summary>
    /// <returns>The Euler z of the camera transform.</returns>
    /// <remarks>ObserverCamera vtable +0x178 is 00413850 too (vtable 00617624): the Euler z of the camera transform that ObserverCamera_step (00435348) rebuilds from the rotation +0x264 (the control state "rot"), so the dial turns with the observer heading. Orbit mode 3 with the stored distance -1 takes the orbit object's own transform: a Player's Euler z is its rotZ.</remarks>
    private double ObserverYaw()
    {
        var st = Psc.ControlState;
        var rotZ = st.GetValueOrDefault("rot") is double[] { Length: >= 3 } r ? r[2] : 0;
        if (st.GetValueOrDefault("orbit") is double[] { Length: >= 3 } o && o[2] < 0 && OrbitGhost is { Class: "Player" } g)
            return D(g.State.GetValueOrDefault("rotZ"), rotZ);
        return rotZ;
    }
    /// <inheritdoc/>
    public double ClockSeconds => _clock();

    /// <inheritdoc/>
    /// <remarks>Present while 006a83e4 is not -1: the waypoint position and the control object's x and y (+0x188, +0x18c of the transform) (004e7e5c).</remarks>
    public HudWaypoint? Waypoint =>
        _world.Players.WaypointTarget != -1 && Psc.ControlState.GetValueOrDefault("pos") is double[] { Length: >= 2 } p
            ? new HudWaypoint(_world.Players.WaypointX, _world.Players.WaypointY, p[0], p[1])
            : null;

    /// <inheritdoc/>
    /// <remarks>Follows 004e8fe8: every item with a count above zero, a hudIcon and showWeaponBar, in datablock order. The item is mounted when an image slot of the control player holds the item (the stream carries the item index). With hiliteOnActive set the "on" icon shows only while that slot fires (state 1). The ammo is the count of the image's ammoType item, negative without one.</remarks>
    public IReadOnlyList<WeaponRow> Weapons
    {
        get
        {
            var list = new List<WeaponRow>();
            if (Ghost is not ClientGhost g)
                return list;
            var slots = MountedImages(g);
            for (var i = 0; i < Psc.ItemCounts.Length; ++i)
            {
                if (Psc.ItemCounts[i] <= 0 || !_world.DataBlocks.Blocks.TryGetValue((5, i), out var b))
                    continue;
                var icon = b.Fields.FirstOrDefault(f => f.Name == "hudIcon" && f.Value is string).Value as string ?? "";
                if (icon.Length == 0 || !b.Fields.Any(f => f.Name == "showWeaponBar" && D(f.Value, 0) != 0))
                    continue;
                var image = (int)b.Fields.Where(f => f.Name == "imageType").Select(f => D(f.Value, -1)).FirstOrDefault(-1);
                var hilite = b.Fields.Any(f => f.Name == "hiliteOnActive" && D(f.Value, 0) != 0);
                var active = false;
                foreach (var (img, state) in slots)
                    if (img == i)
                    {
                        active = !hilite || state == 1;
                        break;
                    }
                var ammo = -1;
                if (image >= 0 && _world.DataBlocks.Blocks.TryGetValue((6, image), out var ib))
                {
                    var type = (int)ib.Fields.Where(f => f.Name == "ammoType").Select(f => D(f.Value, -1)).FirstOrDefault(-1);
                    if (type >= 0 && type < Psc.ItemCounts.Length)
                        ammo = Psc.ItemCounts[type];
                }
                list.Add(new WeaponRow(icon, active, ammo));
            }
            return list;
        }
    }

    /// <summary>Reads the (image, state) of each filled image slot of a Player ghost from the strings "i:img=N state=S ..." of the unpack.</summary>
    private static List<(int Image, int State)> MountedImages(ClientGhost g)
    {
        var l = new List<(int, int)>();
        if (g.State.GetValueOrDefault("images") is not System.Collections.IEnumerable e || g.State.GetValueOrDefault("images") is string)
            return l;
        foreach (var o in e)
            if (o is string s && Num(s, "img=", out var img))
            {
                Num(s, "state=", out var st);
                l.Add((img, st));
            }
        return l;
    }

    private static bool Num(string s, string key, out int v)
    {
        v = -1;
        var p = s.IndexOf(key, StringComparison.Ordinal);
        if (p < 0)
            return false;
        p += key.Length;
        var e = p;
        while (e < s.Length && (char.IsDigit(s[e]) || s[e] == '-'))
            ++e;
        return int.TryParse(s.AsSpan(p, e - p), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    /// <inheritdoc/>
    /// <remarks>The item datablocks (group 5) with showInventory, in index order (0048afcc, 0048b05c).</remarks>
    public IReadOnlyList<InventoryEntry> InventoryItems
    {
        get
        {
            var list = new List<InventoryEntry>();
            if (!_world.DataBlocks.Counts.TryGetValue(5, out var n))
                return list;
            for (var i = 0; i < n && i < Psc.ItemCounts.Length; ++i)
            {
                if (!_world.DataBlocks.Blocks.TryGetValue((5, i), out var b) ||
                    !b.Fields.Any(f => f.Name == "showInventory" && D(f.Value, 0) != 0))
                    continue;
                string S(string name) => b.Fields.FirstOrDefault(f => f.Name == name && f.Value is string).Value as string ?? "";
                var price = (int)b.Fields.Where(f => f.Name == "price").Select(f => D(f.Value, 0)).FirstOrDefault();
                list.Add(new InventoryEntry(i, S("heading"), S("description"), price, Psc.ItemCounts[i],
                    Bit(Psc.Shopping, i), Bit(Psc.Buying, i)));
            }
            return list;
        }
    }

    private static bool Bit(uint[] mask, int i) => i / 32 < mask.Length && ((mask[i / 32] >> (i % 32)) & 1) != 0;

    /// <inheritdoc/>
    /// <remarks>Built from the client player manager: teams by score and clients by score, both highest first.</remarks>
    public ScoreBoard? Scores
    {
        get
        {
            var pm = _world.Players;
            var teams = pm.Teams.Values.Where(t => t.Index >= 0).OrderByDescending(t => t.SortValue).ThenBy(t => t.Index)
                .Select(t => new ScoreRow(t.Index, Expand(t.ScoreText, null, t.Name), t.SortValue)).ToList();
            var clients = pm.Clients.Values.Where(c => !c.Ai).OrderByDescending(c => c.SortValue).ThenBy(c => c.Id)
                .Select(c => new ScoreRow(c.Id, Expand(c.ScoreText, c, c.Team >= 0 && pm.Teams.TryGetValue(c.Team, out var t) ? t.Name : ""),
                    c.SortValue)).ToList();
            return new ScoreBoard(pm.TeamHeading, pm.ClientHeading, teams, clients, _world.PlayerId);
        }
    }

    /// <summary>Replaces the placeholders of a score string.</summary>
    /// <param name="text">The score string.</param>
    /// <param name="player">The player record, or null for a team row.</param>
    /// <param name="teamName">The team name.</param>
    /// <returns>The text with %n the player name, %t the team name, %p the ping and %l the loss in percent replaced (004c2198).</returns>
    public static string Expand(string text, ClientPlayerRecord? player, string teamName)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; ++i)
        {
            if (text[i] == '%' && i + 1 < text.Length)
            {
                switch (text[i + 1])
                {
                    case 'n' when player != null:
                        sb.Append(player.Name);
                        ++i;
                        continue;
                    case 't':
                        sb.Append(teamName);
                        ++i;
                        continue;
                    case 'p' when player != null:
                        sb.Append(player.PingMs);
                        ++i;
                        continue;
                    case 'l' when player != null:
                        sb.Append((int)(player.Loss * 100));
                        ++i;
                        continue;
                    default:
                        break;
                }
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    /// <summary>Gets or sets the callback for the chat menu a script switched on (the server menu of the Tab key). Set by the host.</summary>
    public Func<ServerMenuView?>? MenuSource { get; set; }
    /// <inheritdoc/>
    public ServerMenuView? ServerMenu => MenuSource?.Invoke();
    /// <summary>Gets or sets the callback for the player chat menu. Set by the host.</summary>
    public Func<PlayMenuView?>? PlayMenuSource { get; set; }
    /// <inheritdoc/>
    public PlayMenuView? PlayMenu => PlayMenuSource?.Invoke();
    /// <summary>Gets or sets the callback for the commander map picture, rendered on demand from the world. Set by the host.</summary>
    public Func<CommandMapPicture?>? CommandMapSource { get; set; }
    /// <inheritdoc/>
    public CommandMapPicture? CommandMap => CommandMapSource?.Invoke();
    /// <inheritdoc/>
    /// <remarks>The slot table of the PSC command block with the datablock fields and records the draw needs (004cf94c, 004d04e8).</remarks>
    public CommandMapMarks? CommandMapMarks
    {
        get
        {
            var pm = _world.Players;
            if (pm.Get(_world.PlayerId) is not ClientPlayerRecord me)
                return null;
            var slots = new List<CommandMapSlotMark>();
            for (var i = 0; i < Psc.CommandSlots.Length; ++i)
            {
                var s = Psc.CommandSlots[i];
                if (!s.Present || !_world.DataBlocks.Blocks.TryGetValue((s.Group, s.Index), out var b))
                    continue;
                string Str(string name)
                { foreach (var fld in b.Fields) if (fld.Name == name) return fld.Value as string ?? ""; return ""; }
                var id = i < 128 ? 0x800 + i : -1;
                var rec = s.Group == 3 && id >= 0 ? pm.Get(id) : null;
                var target = i >= 128 && pm.TargetNames.TryGetValue(i - 128, out var tn) ? tn : null;
                slots.Add(new CommandMapSlotMark(i, s.X, s.Y, s.Team, s.Group, id, Str("mapIcon"), Str("description"), target, (int)Fld(b, "mapFilter", -1),
                    (int)Fld(b, "range", 0), Fld(b, "supression", 0) != 0, s.Active, rec?.Name ?? "", rec is { CmdIssuer: not -1, Commander: >= 0 and < 64 } ? rec.Commander : -1, rec?.CmdTarget ?? -1,
                    rec?.CommandStatus ?? 0, rec?.CmdWaypointX ?? 0, rec?.CmdWaypointY ?? 0));
            }
            // (The port keeps the command icon, +0x40, in the field the select-commander event also writes: a record counts as commanded once an order event named an issuer.)
            // The where-am-I mark turns with the control object's yaw: 256 - yaw * 256 / 2 pi [code 004cfc54].
            // (a Player's block carries the z rotation as `rotZ`, an observer camera's the three angles as `rot`)
            var yaw = Psc.ControlState.TryGetValue("rotZ", out var zv) ? D(zv, 0)
                : Psc.ControlState.TryGetValue("rot", out var rv) && rv is double[] { Length: >= 3 } rot ? rot[2] : 0;
            var heading = (int)(256.0 - (yaw * 256.0 * (1.0 / (2 * Math.PI))));
            return new CommandMapMarks(_world.PlayerId, me.Team, heading, slots);
        }
    }
    /// <summary>Gets or sets the callback for the state of a named chat menu object. Set by the host.</summary>
    public Func<string, ChatMenuState?>? NamedMenuSource { get; set; }
    /// <inheritdoc/>
    public ChatMenuState? NamedMenu(string name) => NamedMenuSource?.Invoke(name);

    /// <summary>Returns the parent of a record in the command tree.</summary>
    /// <param name="r">The record.</param>
    /// <returns>The commander record when it is a human, else null.</returns>
    private ClientPlayerRecord? CommandParent(ClientPlayerRecord r) => r.Commander != -1 && _world.Players.Get(r.Commander) is { Ai: false } c ? c : null;

    /// <summary>Returns the records directly under a node of the command tree.</summary>
    /// <param name="parent">The node, or null for the top level of a team.</param>
    /// <param name="team">The team of the top level.</param>
    /// <returns>The records.</returns>
    private List<ClientPlayerRecord> CommandChildren(ClientPlayerRecord? parent, int team) =>
        [.. _world.Players.Clients.Values.Where(r => parent == null ? CommandParent(r) == null && r.Team == team : CommandParent(r) == parent)];

    /// <inheritdoc/>
    /// <remarks>
    /// <para>Follows 004a85c0, 0040b46c and 004a8548: the records of the team whose commander is not a human record are the top level, the others hang under their commander. The client table has no join order, so the id order stands for it. The children of a node are listed while its expanded byte (+0x69, starts at 1) is set.</para>
    /// <para>Each row has the status of its command map slot: the damage level is not read from the PSC command block yet (low), the order is the record's command status + 1 (docs/client-net.md 4.5 mode 2). A row is selected while the record's +0x68 byte is set.</para>
    /// </remarks>
    public IReadOnlyList<CommandTeamRow> CommandTeam
    {
        get
        {
            var rows = new List<CommandTeamRow>();
            var pm = _world.Players;
            if (pm.Get(_world.PlayerId) is not ClientPlayerRecord me)
                return rows;
            void Add(List<ClientPlayerRecord> siblings, int depth, List<(int Depth, bool HasNext)> above)
            {
                for (var i = 0; i < siblings.Count; ++i)
                {
                    var r = siblings[i];
                    var kids = CommandChildren(r, me.Team);
                    var next = i + 1 < siblings.Count;
                    rows.Add(new CommandTeamRow(r.Id, r.Name, depth, kids.Count > 0, r.Expanded, next, CommandDamage(r.Id), r.CommandStatus + 1, [.. above], r.Selected));
                    if (!r.Expanded)
                        continue;
                    var nested = new List<(int Depth, bool HasNext)> { (depth, next) };
                    nested.AddRange(above);
                    Add(kids, depth + 1, nested);
                }
            }
            Add(CommandChildren(null, me.Team), 0, []);
            return rows;
        }
    }

    /// <summary>Returns the status field of the first group 3 slot whose id (+0x350, 0x800 + slot) is a client id (004d29fc).</summary>
    /// <param name="clientId">The client id.</param>
    /// <returns>The status, 0 (low) for none.</returns>
    private int CommandDamage(int clientId)
    {
        var slots = Psc.CommandSlots;
        for (var i = 0; i < 128; ++i)
            if (slots[i].Group == 3 && 0x800 + i == clientId)
                return slots[i].Status;
        return 0;
    }

    /// <summary>Builds the rows of a command list for the local human client.</summary>
    /// <param name="shows">Returns whether the list shows a datablock group.</param>
    /// <returns>Every slot of its team whose datablock group the list shows and whose datablock index is set, in slot order. The name is the slot's label when there is one, else the datablock's description. A slot with neither is left out.</returns>
    /// <remarks>Follows the prepare of CommandItemCtrl (004a98b8) and CommandTurretCtrl (004aa6b8).</remarks>
    private List<CommandListRow> CommandSlotRows(Func<int, bool> shows)
    {
        var rows = new List<CommandListRow>();
        if (CommandLocal is not CommandLocalClient me)
            return rows;
        for (var i = 0; i < Psc.CommandSlots.Length; ++i)
        {
            var s = Psc.CommandSlots[i];
            if (!s.Present || s.Team != me.Team || !shows(s.Group))
                continue;
            var name = i >= 128 && _world.Players.TargetNames.TryGetValue(i - 128, out var label) && label.Length > 0 ? label : null;
            if (name == null)
            {
                if (!_world.DataBlocks.Blocks.TryGetValue((s.Group, s.Index), out var b))
                    continue;
                foreach (var f in b.Fields)
                    if (f.Name == "description" && f.Value is string d && d.Length > 0)
                        name = d;
            }
            if (name != null)
                rows.Add(new CommandListRow(i, name));
        }
        return rows;
    }

    /// <inheritdoc/>
    public IReadOnlyList<CommandListRow> CommandObjects => CommandSlotRows(g => g is 4 or 8);
    /// <inheritdoc/>
    public IReadOnlyList<CommandListRow> CommandControllables => CommandSlotRows(g => g is 0x13 or 10 or 0xb or 9);

    /// <inheritdoc/>
    /// <remarks>Follows 004b48bc: the walk over the client records takes the first with the selected byte (+0x68) set. The client table has no join order, so the id order stands for it.</remarks>
    public int CommandFirstSelectedClient() => _world.Players.Clients.Values.OrderBy(r => r.Id).FirstOrDefault(r => r.Selected)?.Id ?? -1;

    /// <inheritdoc/>
    public CommandLocalClient? CommandLocal =>
        _world.Players.Get(_world.PlayerId) is { Ai: false, Team: var team } ? new CommandLocalClient(_world.PlayerId, team) : null;

    /// <inheritdoc/>
    public ClientCommandSlot[]? CommandSlotTable => Psc.CommandSlots;

    /// <inheritdoc/>
    public int? CommandBlockFilter(int group, int index) =>
        _world.DataBlocks.Blocks.TryGetValue((group, index), out var b) ? (int)Fld(b, "mapFilter", -1) : null;

    /// <inheritdoc/>
    public string? CommandClientName(int clientId) => _world.Players.Get(clientId) is { Ai: false } r ? r.Name : null;

    /// <inheritdoc/>
    public string? CommandTargetName(int slot) => slot >= 128 && _world.Players.TargetNames.TryGetValue(slot - 128, out var n) ? n : null;

    /// <inheritdoc/>
    public void CommandSetSelected(int clientId, bool selected)
    {
        if (_world.Players.Get(clientId) is { } r)
            r.Selected = selected;
    }

    /// <inheritdoc/>
    public void CommandToggleExpanded(int clientId)
    {
        if (_world.Players.Get(clientId) is { } r)
            r.Expanded = !r.Expanded;
    }

    /// <inheritdoc/>
    public bool CommandInChain(int clientId, int commander)
    {
        for (var r = _world.Players.Get(clientId); r != null; r = _world.Players.Get(r.Commander) is { Ai: false } c ? c : null)
            if (r.Commander == commander)
                return true;
        return false;
    }

    /// <inheritdoc/>
    /// <remarks>+0x74 is the first record under this one in the tree (the oldest, as for the rows), +0x78 the next one under the same commander.</remarks>
    public (int FirstChild, int NextSibling, bool Selected)? CommandLinks(int clientId)
    {
        var pm = _world.Players;
        if (pm.Get(clientId) is not { } r || pm.Get(_world.PlayerId) is not { } me)
            return null;
        var first = CommandChildren(r, me.Team) is { Count: > 0 } kids ? kids[0].Id : -1;
        var siblings = CommandChildren(CommandParent(r), r.Team);
        var at = siblings.IndexOf(r);
        return (first, at >= 0 && at + 1 < siblings.Count ? siblings[at + 1].Id : -1, r.Selected);
    }
    /// <inheritdoc/>
    public IReadOnlyList<string?> ObjectiveLines => _world.Players.ObjectiveLines;

    /// <summary>Gets the 2 sensor state bits of the control state (00413b90, Vehicle 00446830): 1 pinged, 2 jammed.</summary>
    private int SensorState => (int)D(Psc.ControlState.GetValueOrDefault("state2"), 0);
    /// <inheritdoc/>
    public bool SensorPing => SensorState == 1;
    /// <inheritdoc/>
    public bool SensorLocked => SensorState == 2;

    /// <summary>Gets or sets the callback for the view camera of the frame, given the view size. Set by the host.</summary>
    public Func<int, int, Camera3D?>? ViewCamera { get; set; }
    /// <summary>Gets or sets the callback for the position of mount point 2 of a player ghost, null to use the body position plus 1.4. Set by the host.</summary>
    public Func<int, (double X, double Y, double Z)?>? HeadMount { get; set; }
    /// <summary>Gets or sets the callback for the muzzle of the image in slot 0 of a Player ghost in world space, null to use the body position. Set by the host.</summary>
    public Func<int, (double X, double Y, double Z)?>? MuzzleMount { get; set; }
    /// <summary>Gets or sets the callback for the eye and muzzle transforms of a Vehicle, Flier or Turret control ghost, null while it has no drawn object. Set by the host.</summary>
    public Func<int, HudControlAim?>? ControlAim { get; set; }
    /// <summary>Gets or sets the line test from the eye to a point: true when nothing blocks it. Set by the host.</summary>
    public Func<(double X, double Y, double Z), (double X, double Y, double Z), bool>? LineClear { get; set; }
    /// <summary>Gets or sets the callback for the world box (min, max) and the transform origin of a shape ghost, for the objects of the label pick. Set by the host.</summary>
    public Func<int, (double X0, double Y0, double Z0, double X1, double Y1, double Z1, double Px, double Py, double Pz)?>? ObjectBox { get; set; }

    /// <summary>Returns the fraction of a segment at which it enters a box.</summary>
    /// <param name="eye">The segment start.</param>
    /// <param name="dx">The segment x extent.</param>
    /// <param name="dy">The segment y extent.</param>
    /// <param name="dz">The segment z extent.</param>
    /// <param name="lo">The box minimum.</param>
    /// <param name="hi">The box maximum.</param>
    /// <returns>0 when the start is inside the box, 1 when the segment misses it.</returns>
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

    /// <summary>Returns the datablock group of the classes that take part in the label pick.</summary>
    /// <param name="cls">The ghost class.</param>
    /// <returns>The group, -1 for a class that does not take part.</returns>
    /// <remarks>The sensor set holds the Sensors (and their Turrets) and every object with a target index (StaticShape, Item). Callers of 0053a1f4: Sensor_onAdd, GameBase_onNewDataBlock.</remarks>
    private static int ObjectGroup(string cls) => cls switch { "Turret" => 19, "Sensor" => 8, "StaticShape" => 4, "Item" => 5, _ => -1 };

    private string? BlockString(ClientGhost g, string field)
    {
        if (ObjectGroup(g.Class) is var grp && grp >= 0 && g.State.TryGetValue("datablock", out var idx)
            && _world.DataBlocks.Blocks.TryGetValue((grp, (int)D(idx, -1)), out var b))
            foreach (var f in b.Fields)
                if (f.Name == field)
                    return f.Value as string;
        return null;
    }

    /// <summary>Returns the ghost index of the object a Player ghost is mounted on (the "ghost/point" stream field), -1 when it has none.</summary>
    private static int MountGhost(ClientGhost g) =>
        g.State.GetValueOrDefault("mount") is string m && m.IndexOf('/') is int i and > 0 && int.TryParse(m.AsSpan(0, i), out var ghost) ? ghost : -1;

    /// <inheritdoc/>
    /// <remarks>Follows Crosshair_findTarget (004aca84): the other players the local player's client may see (the visible mask of the PSC, +0x490), with their marks and the one under the view centre.</remarks>
    public HudTargetResult? CrosshairTarget(int width, int height)
    {
        if (ViewCamera?.Invoke(width, height) is not Camera3D cam || Psc.ControlGhost < 0 || _world.PlayerId == 0)
            return null;
        var pm = _world.Players;
        var me = pm.Get(_world.PlayerId);
        if (me == null)
            return null;
        var eye = (cam.X, cam.Y, cam.Z);
        var list = new List<HudTargetCandidate>();
        foreach (var g in _world.Ghosts.All())
        {
            // Crosshair_findTarget skips the control object and every Player whose mount object (+0x1028) is the control object: while
            // piloting, the pilot's own Player and the passengers of the vehicle get no mark and no label [code 004aca84].
            if (g.Class != "Player" || g.Index == Psc.ControlGhost || MountGhost(g) == Psc.ControlGhost
                || !g.State.TryGetValue("pos", out var pv) || pv is not double[] { Length: >= 3 } pos)
                continue;
            var owner = (int)D(g.State.GetValueOrDefault("ownerClient"), 0);
            if (owner < 0x800 || pm.Get(owner) is not ClientPlayerRecord rec)
                continue;
            var visible = ((owner - 0x800) >> 5) is int w && w < Psc.Visible.Length && ((Psc.Visible[w] >> (owner & 31)) & 1) != 0;
            var (x, y, z) = HeadMount?.Invoke(g.Index) ?? (pos[0], pos[1], pos[2] + 1.4);
            double bw = 1, bd = 1, bh = 2;
            if (g.State.TryGetValue("datablock", out var idx) && _world.DataBlocks.Blocks.TryGetValue((3, (int)D(idx, -1)), out var b))
                foreach (var f in b.Fields)
                    switch (f.Name)
                    {
                        case "boxWidth":
                            bw = D(f.Value, bw);
                            break;
                        case "boxDepth":
                            bd = D(f.Value, bd);
                            break;
                        case "boxNormalHeight":
                            bh = D(f.Value, bh);
                            break;
                        default:
                            break;
                    }
            var dead = g.State.ContainsKey("dead");
            var damage = dead ? 1 : Math.Clamp(D(g.State.GetValueOrDefault("damage"), 0), 0, 1);
            var top = (x, y, z + 0.65);
            var clear = LineClear?.Invoke(eye, (pos[0], pos[1], pos[2] + (bh * 0.5))) ?? true;
            list.Add(new HudTargetCandidate(rec.Name, top, (pos[0], pos[1], pos[2]), Math.Min(bw, bd), bh, rec.Team, damage, dead, visible, clear));
        }
        // Objects of the sensor set: the name is the target name of its slot, else the datablock description; the icon is the green
        // bitmap of the datablock's mapIcon [code GameBaseData_preload: "%s_green.bmp"].
        foreach (var g in _world.Ghosts.All())
        {
            // The loop of Crosshair_findTarget [code 004aca84, test at 004accdc..004accee] skips the control object before it sorts the object
            // into the Player or the sensor branch: a controlled Turret gets no label.
            if (g.Index == Psc.ControlGhost)
                continue;
            if (ObjectGroup(g.Class) < 0 || ObjectBox?.Invoke(g.Index) is not var (bx0, by0, bz0, bx1, by1, bz1, px, py, pz) || !g.State.ContainsKey("team"))
                continue;
            var sensor = g.Class is "Sensor" or "Turret";
            if (!sensor && !g.State.ContainsKey("target"))
                continue;
            var name = g.State.TryGetValue("target", out var ti) && pm.TargetNames.TryGetValue((int)D(ti, -1), out var tn) ? tn : "";
            if (name.Length == 0)
                name = BlockString(g, "description") ?? "";
            var icon = BlockString(g, "mapIcon") is { Length: > 0 } mi ? mi + "_green.bmp" : null;
            var team = (int)D(g.State["team"], -1);
            if (team == 15)
                team = -1;
            var dmg = g.State.TryGetValue("damageState", out var value) && D(value, 1) != 1 ? 1 : Math.Clamp(D(g.State.GetValueOrDefault("damage"), 0), 0, 1);
            // [code 004aca84] the label sits on the box centre in x and y and its top in z; the pick box is the smaller of the x and y
            // extents wide and the z extent high; the distance is measured to the transform origin.
            double cx = (bx0 + bx1) * 0.5, cy = (by0 + by1) * 0.5, cz = (bz0 + bz1) * 0.5;
            var top = (cx, cy, bz1);
            // The stock test passes when the first hit of the line from the eye to the box centre is the object itself: stop the ray where it enters the box.
            double ex = cx - eye.X, ey = cy - eye.Y, ez = cz - eye.Z, k = EntryFraction(eye, ex, ey, ez, (bx0, by0, bz0), (bx1, by1, bz1));
            var clear = LineClear?.Invoke(eye, (eye.X + (ex * k), eye.Y + (ey * k), eye.Z + (ez * k))) ?? true;
            list.Add(new HudTargetCandidate(name, top, (px, py, pz), Math.Min(bx1 - bx0, by1 - by0), bz1 - bz0, team, dmg, false, true, clear, true, icon));
        }
        return HudTargetPicker.Find(cam, list, me.Team, pm.TeamCount);
    }

    // ---- aim triangles (004ada90) ----

    private static double Fld(ClientDataBlock? b, string name, double def)
    {
        if (b != null)
            foreach (var f in b.Fields)
                if (f.Name == name)
                    return D(f.Value, def);
        return def;
    }

    /// <summary>Returns the projectile datablock of the image in slot 0 of a Player.</summary>
    /// <param name="g">The Player ghost.</param>
    /// <param name="group">Receives the datablock group.</param>
    /// <returns>The datablock, null without an image or a projectile.</returns>
    /// <remarks>Follows Player vtable +0x114 (00412218): ItemData to imageType to ItemImageData projectileType (two 32 bit words: group at +0x70, index at +0x74).</remarks>
    private ClientDataBlock? WeaponProjectile(ClientGhost g, out int group)
    {
        group = -1;
        var item = D(g.State.GetValueOrDefault("item0"), -1);
        if (item < 0
            || !_world.DataBlocks.Blocks.TryGetValue((5, (int)item), out var ib)
            || !_world.DataBlocks.Blocks.TryGetValue((6, (int)Fld(ib, "imageType", -1)), out var img))
            return null;
        return ProjectileOf(img, out group);
    }

    /// <summary>Returns the projectile datablock a `projectileType` field names (two 32 bit words, group then index).</summary>
    /// <param name="img">The datablock with the field.</param>
    /// <param name="group">Receives the datablock group.</param>
    /// <returns>The datablock, null when it is unset.</returns>
    private ClientDataBlock? ProjectileOf(ClientDataBlock img, out int group)
    {
        group = -1;
        var raw = img.Fields.FirstOrDefault(f => f.Name == "projectileType").Value as string ?? "";
        if (raw.Length < 16)
            return null;
        var bytes = Convert.FromHexString(raw);
        int grp = BitConverter.ToInt32(bytes, 0), idx = BitConverter.ToInt32(bytes, 4);
        if (grp == -1)
            return null;
        group = grp;
        return _world.DataBlocks.Blocks.TryGetValue((grp, idx), out var pb) ? pb : null;
    }

    /// <summary>Returns the gravity of the control player: force 1 of its SimMovement, the world gravity of the terrain ghost, minus its length (004adb98).</summary>
    private float Gravity()
    {
        foreach (var t in _world.Ghosts.All())
            if (t.State.GetValueOrDefault("contGravity") is double[] { Length: >= 3 } v)
                return X87.F32(-X87.Len([(float)v[0], (float)v[1], (float)v[2]]));
        return 0f;
    }

    /// <summary>Returns the projectile datablock of a Turret.</summary>
    /// <param name="g">The Turret ghost.</param>
    /// <param name="group">Receives the datablock group.</param>
    /// <returns>The datablock, null without one.</returns>
    /// <remarks>Turret vtable +0x114 (00456074) gives the projectileType (group at +0x120, index at +0x124) of its TurretData. Player vtable +0x114 (00412218) gives the projectile of the image in slot 0, Vehicle and Flier (00430c88) nothing.</remarks>
    private ClientDataBlock? TurretProjectile(ClientGhost g, out int group)
    {
        group = -1;
        if (!g.State.TryGetValue("datablock", out var di) || !_world.DataBlocks.Blocks.TryGetValue((19, (int)D(di, -1)), out var tb))
            return null;
        return ProjectileOf(tb, out group);
    }

    /// <inheritdoc/>
    /// <remarks>Follows FUN_004ada90. For every target of set 725 (objects with the target flag and enabled, the hit point of a targeting laser) of the control object's team the datablock of its weapon solves the aim from the eye (low arc, then the high arc). The direction x 2 from the eye is projected and gets a triangle, blue when the projectile reaches (0xfb), else 0xf1, with the distance from the muzzle under it. Without a projectile datablock the solution is the straight line, never reaching. The routine casts the control object to ShapeBase (004adb1a to 004adb37): it runs for a Player, a Vehicle, a Flier and a Turret alike and only the virtual calls differ (eye +0x110, muzzle +0x10c, projectile +0x114, team +0x20c).</remarks>
    public IReadOnlyList<HudTargetMarker> TargetMarkers(int width, int height)
    {
        var result = new List<HudTargetMarker>();
        var cls = Psc.ControlClass;
        if (Ghost is not ClientGhost me || cls is not ("Player" or "Vehicle" or "Flier" or "Turret") || ViewCamera?.Invoke(width, height) is not Camera3D cam
            || _world.PlayerId == 0 || _world.Players.Get(_world.PlayerId) is not ClientPlayerRecord rec)
            return result;
        ClientDataBlock? block;
        int group, ownTeam;
        float[] eye;
        (double, double, double) muzzle;
        if (cls == "Player")
        {
            block = WeaponProjectile(me, out group);
            ownTeam = rec.Team;
            eye = [(float)cam.X, (float)cam.Y, (float)cam.Z];
            var hasItem = D(me.State.GetValueOrDefault("item0"), -1) >= 0;
            var body = me.State.GetValueOrDefault("pos") is double[] { Length: >= 3 } bp ? (bp[0], bp[1], bp[2]) : (cam.X, cam.Y, cam.Z);
            muzzle = hasItem ? MuzzleMount?.Invoke(me.Index) ?? body : body;
        }
        else
        {
            // +0x20c of the control object: the team of its own ghost. Without the drawn object the eye and muzzle are not known.
            if (!me.State.TryGetValue("team", out var tv) || ControlAim?.Invoke(me.Index) is not HudControlAim aim)
                return result;
            ownTeam = (int)D(tv, -1);
            group = -1;
            block = cls == "Turret" ? TurretProjectile(me, out group) : null;
            eye = aim.Eye;
            muzzle = aim.MuzzleLabelPoint();
        }
        var speed = group == Projectile.RocketGroup ? (float)Fld(block, "field@0x6c", 0) : group is Projectile.BulletGroup or Projectile.GrenadeGroup or Projectile.SeekerGroup
            ? (float)Fld(block, "field@0x68", 0) : 1.0e7f;
        var totalTime = (float)Fld(block, "totalTime", 10000);
        var g = Gravity();
        foreach (var t in _world.Ghosts.All())
        {
            double[]? pos = null;
            var team = int.MinValue;
            if (t.Class == "TargetLaser")
            {
                if (D(t.State.GetValueOrDefault("hit"), 0) == 0 || t.State.GetValueOrDefault("end") is not double[] { Length: >= 3 } end)
                    continue;
                pos = end;
                team = (int)D(t.State.GetValueOrDefault("team"), -1);
            }
            else if (D(t.State.GetValueOrDefault("isTarget"), 0) != 0 && D(t.State.GetValueOrDefault("damageState"), 1) == 1
                && ObjectBox?.Invoke(t.Index) is var (bx0, by0, bz0, bx1, by1, bz1, _, _, _) && t.State.TryGetValue("team", out var tv))
            {
                // StaticBase vtable +0x118 [code 0045047c]: the centre of the world box (+0x70, +0x7c) and the team (+0x20c).
                pos = [(bx0 + bx1) * 0.5, (by0 + by1) * 0.5, (bz0 + bz1) * 0.5];
                team = (int)D(tv, -1);
            }
            if (pos == null || team != ownTeam)
                continue;
            float[] target = [(float)pos[0], (float)pos[1], (float)pos[2]];
            var dirLow = new float[3];
            var dirHigh = new float[3];
            bool okLow = false, okHigh = false, haveLow, haveHigh = false;
            if (block == null)
            {
                var len = X87.F32(X87.Len([target[0] - eye[0], target[1] - eye[1], target[2] - eye[2]]));
                var k = X87.F32(1.0f / len);
                for (var i = 0; i < 3; ++i)
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
                return cam.Project(px, py, pz, out var sx, out var sy, out _) ? new HudMarkPoint(HudMarkers.Pixel(sx), HudMarkers.Pixel(sy), ok) : null;
            }
            HudMarkPoint? low = Point(dirLow, okLow), high = haveHigh ? Point(dirHigh, okHigh) : null;
            result.Add(new HudTargetMarker(low, high, HudMarkers.Label(HudMarkers.Distance(muzzle, (target[0], target[1], target[2])))));
        }
        return result;
    }
}
