// Client PlayerPSC (docs/client-net.md 4.3, 4.4, 4.5; docs/game-stream.md 6): the read of the server block, the effects it
// has on the PSC state (GUI mode, move buffer trim, control object, inventory, command map) and the client block that
// carries the moves.
namespace OpenTribes.Game;

using System.Globalization;

/// <summary>One block of the PSC member as read from the stream.</summary>
public sealed class DecodedPsc
{
    /// <summary>The direction of the block.</summary>
    public string Direction = "";
    /// <summary>The one line text.</summary>
    public string Text = "";
    /// <summary>The number of bits the block took.</summary>
    public int Bits;
    /// <summary>The decoded fields.</summary>
    public List<DecodedField> Fields = [];
}

/// <summary>One entry of the command map slot table (<c>DAT_006a83d8</c> + 0x340, 0x28 bytes a slot, docs/client-net.md 4.5 mode 2).</summary>
/// <remarks>The position is in 1/1024 of the mission area (the 11 bit value minus 512, +0x340 and +0x344). The entry also holds the object's team (+0x354) and status (+0x358) and the datablock group and index (+0x348, +0x34c). For slots of clients (below 128) it holds the command status (+0x35c, the record's command status + 1). For the other slots (target slots) it holds the controllable flag (+0x361) and the second status flag (+0x35c, active).</remarks>
public struct ClientCommandSlot
{
    /// <summary>True when the datablock index is not -1.</summary>
    public bool Present;
    /// <summary>The x position.</summary>
    public int X;
    /// <summary>The y position.</summary>
    public int Y;
    /// <summary>The team.</summary>
    public int Team;
    /// <summary>The status.</summary>
    public int Status;
    /// <summary>The command status of a client slot.</summary>
    public int CommandStatus;
    /// <summary>The datablock group.</summary>
    public int Group;
    /// <summary>The datablock index.</summary>
    public int Index;
    /// <summary>The controllable flag of a target slot.</summary>
    public bool Controllable;
    /// <summary>The active flag of a target slot.</summary>
    public bool Active;
}

/// <summary>The client PlayerPSC: reads the server block and applies its effects, and writes the client block that carries the moves.</summary>
/// <remarks>Covers the GUI mode, the move buffer trim, the control object, the inventory and the command map (docs/client-net.md 4.3, 4.4, 4.5; docs/game-stream.md 6).</remarks>
public sealed class ClientPsc
{
    /// <summary>The command map slot table, 256 entries.</summary>
    public readonly ClientCommandSlot[] CommandSlots = new ClientCommandSlot[256];

    /// <summary>The GUI mode (psc +0x3d8). Server block state is in docs/client-net.md 4.5.</summary>
    public int GuiMode;
    /// <summary>True when the score dialog is visible.</summary>
    public bool ScoreFlag;
    /// <summary>The last move number in the block.</summary>
    public int LastMove = -1;
    /// <summary>True when the block carries a control object.</summary>
    public bool HasControl;
    /// <summary>The damage flash (psc +0x3e0).</summary>
    public double DamageFlash;
    /// <summary>The ghost index of the control object (psc +0xa4), -1 for none (0x3ff on the wire).</summary>
    public int ControlGhost = -1;
    /// <summary>The class name of the control object.</summary>
    public string ControlClass = "";
    /// <summary>The decoded control state.</summary>
    public Dictionary<string, object?> ControlState = [];
    /// <summary>The item counts (+0xac).</summary>
    public readonly int[] ItemCounts = new int[200];
    /// <summary>The shopping item mask.</summary>
    public readonly uint[] Shopping = new uint[7];
    /// <summary>The buying item mask.</summary>
    public readonly uint[] Buying = new uint[7];
    /// <summary>The visibility mask.</summary>
    public readonly uint[] Visible = new uint[8];
    /// <summary>The command map entries by slot.</summary>
    public readonly Dictionary<int, string> CommandMap = [];
    /// <summary>The waypoint, or null.</summary>
    public (long X, long Y)? Waypoint;
    /// <summary>Called when the waypoint changes. 006a83e8 and 006a83ec are set from the play block pair.</summary>
    public Action<long, long>? OnWaypoint;
    /// <summary>The number of GUI mode changes.</summary>
    public int GuiModeChanges;
    /// <summary>True once loadPlayGui() was evaluated on the first change away from 0.</summary>
    public bool PlayGuiLoaded;

    /// <summary>The moves not yet acknowledged (docs/client-net.md 4.2, 4.3).</summary>
    public readonly List<Move> Moves = [];
    /// <summary>The global index of the first buffered move (+0x3d0).</summary>
    public uint FirstMove;
    /// <summary>The fov byte, unit(8) of currentFov / 135. 90 degrees sends 169.</summary>
    public uint FovByte = 169;

    /// <summary>Called to run a script line.</summary>
    public Action<string>? OnScript;
    /// <summary>Called as PlayDelegate_setGuiMode while the packet is read (004060f6), after the mode is stored.</summary>
    public Action<int>? OnGuiMode;

    /// <summary>Clears the PSC state.</summary>
    public void Reset()
    {
        GuiMode = 0;
        LastMove = -1;
        ControlGhost = -1;
        HasControl = false;
        ControlClass = "";
        Moves.Clear();
        FirstMove = 0;
        Array.Clear(ItemCounts);
        Array.Clear(Shopping);
        Array.Clear(Buying);
        Array.Clear(Visible);
        CommandMap.Clear();
        Array.Clear(CommandSlots);
        GuiModeChanges = 0;
        PlayGuiLoaded = false;
    }

    /// <summary>Gets the number of moves created so far, the next global move index.</summary>
    public uint MovesCreated => FirstMove + (uint)Moves.Count;

    /// <summary>Adds a move to the buffer.</summary>
    /// <param name="m">The move.</param>
    public void AddMove(in Move m)
    {
        if (Moves.Count < 30)
            Moves.Add(m);
    }

    // ---- client block (4.3, game-stream.md 6.2) ----

    /// <summary>Writes the client block: the fov byte, the first move index, every buffered move and the end flag.</summary>
    /// <param name="bs">The stream.</param>
    /// <remarks>Follows PlayerPSC_writePacket, client branch. The first move is full with no previous, the others are written against the previous one.</remarks>
    public void WriteClient(BitStream bs)
    {
        bs.WriteInt(FovByte, 8);
        bs.WriteInt(FirstMove, 32);
        Move? prev = null;
        foreach (var m in Moves)
        {
            bs.WriteFlag(true);
            var copy = m;
            Game.Moves.PackMove(bs, copy, true, prev);
            prev = m;
        }
        bs.WriteFlag(false);
    }

    /// <summary>Reads a client block, the c2s direction of a capture. The server reads it with <c>GameConnection.ReadPsc</c>.</summary>
    /// <param name="r">The reader.</param>
    /// <returns>The decoded block.</returns>
    public static DecodedPsc ReadClientBlock(ClientReader r)
    {
        var start = r.Pos;
        var d = new DecodedPsc { Direction = "c2s" };
        long fov = r.Int(8);
        long first = r.Int(32);
        var moves = new List<Move>();
        var m = new Move();
        var havePrev = false;
        while (r.Flag())
        {
            Game.Moves.UnpackMove(r.Bs, ref m, true, havePrev);
            if (r.Bs.Error())
                throw new OverrunException();
            havePrev = true;
            moves.Add(m);
        }
        d.Fields.Add(new DecodedField("fov", fov));
        d.Fields.Add(new DecodedField("firstMove", first));
        d.Fields.Add(new DecodedField("moves", (long)moves.Count));
        for (var i = 0; i < moves.Count; ++i)
            d.Fields.Add(new DecodedField("move" + (first + i), FormatMove(moves[i])));
        d.Text = "psc fov=" + fov + " (" + (fov / 255.0 * 135).ToString("0.0", CultureInfo.InvariantCulture) + " deg) firstMove=" + first +
                 " moves=" + moves.Count;
        d.Bits = r.Pos - start;
        return d;
    }

    /// <summary>Formats a move as text.</summary>
    /// <param name="m">The move.</param>
    /// <returns>The text.</returns>
    public static string FormatMove(in Move m) =>
        "f=" + m.Forward.ToString("0.00", CultureInfo.InvariantCulture) + " b=" + m.Backward.ToString("0.00", CultureInfo.InvariantCulture) +
        " l=" + m.Left.ToString("0.00", CultureInfo.InvariantCulture) + " r=" + m.Right.ToString("0.00", CultureInfo.InvariantCulture) +
        " jet=" + (m.Jet ? 1 : 0) + " crouch=" + (m.Crouch ? 1 : 0) + " jump=" + (m.Jump ? 1 : 0) + " fire=" + (m.Fire ? 1 : 0) +
        " useItem=" + (m.UseItem == -1 ? "None" : m.UseItem.ToString(CultureInfo.InvariantCulture)) +
        " pitch=" + ClientReader.G(m.Pitch) + " turn=" + ClientReader.G(m.Turn);

    // ---- server block (4.5, game-stream.md 6.3) ----

    /// <summary>Formats a decoded value as Python <c>repr</c> text.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The text.</returns>
    public static string PyStr(object? v) => v switch
    {
        null => "None",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => ClientReader.G(d),
        string s => s,
        double[] a => "(" + string.Join(",", a.Select(ClientReader.G)) + ")",
        System.Collections.IEnumerable e when v is not string => "[" + string.Join(", ", e.Cast<object?>().Select(PyStr)) + "]",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>Formats decoded fields as <c>name=value</c> text.</summary>
    /// <param name="f">The fields.</param>
    /// <returns>The text.</returns>
    public static string FieldsText(IEnumerable<DecodedField> f) => string.Join(" ", f.Select(x => x.Name + "=" + PyStr(x.Value)));

    /// <summary>Reads a server block on the client.</summary>
    /// <param name="r">The reader.</param>
    /// <returns>The decoded block.</returns>
    /// <remarks>Follows the PlayerPSC server block, client branch (00405ec8).</remarks>
    public DecodedPsc ReadServerBlock(ClientReader r)
    {
        var start = r.Pos;
        var d = new DecodedPsc { Direction = "s2c" };
        var save = r.Out;
        r.Out = d.Fields;
        try
        {
            var mode = (int)r.Int(3);
            var f = r.Flag();
            var last = unchecked((int)r.Int(32));
            var has = r.Flag();
            var head = "psc mode=" + mode + " flag6a=" + (f ? 1 : 0) + " lastMove=" + last;
            var headFields = new List<DecodedField>
            {
                new("mode", (long)mode), new("flag6a", f ? 1L : 0L), new("lastMove", (long)last),
            };
            d.Fields.AddRange(headFields);
            ApplyHead(mode, f, last);
            HasControl = has;
            if (!has)
            {
                ControlGhost = -1;
                d.Text = head + " control=none";
                d.Bits = r.Pos - start;
                return d;
            }
            r.Add("flash", DamageFlash = r.Unit(8));    // the control object's damage flash (vtable +0x100), not a fov
            var gidx = (int)r.Add("controlGhost", (long)r.Int(10));
            string? cls = null;
            if (gidx != 0x3ff)
            {
                cls = r.World.Ghosts.ClassOf(gidx);
                switch (cls)
                {
                    case "Player":
                        ControlPlayer(r);
                        break;
                    case "ObserverCamera":
                        ControlObserver(r);
                        break;
                    case "Flier":
                    case "Vehicle":
                        ControlVehicle(r);
                        break;
                    case "Turret":
                        ControlTurret(r);
                        break;
                    default:
                        d.Text = head + " " + FieldsText(d.Fields.Skip(3));
                        throw new UnsupportedException("control state of ghost " + gidx + " class " + cls);
                }
                r.Out.Insert(5, new DecodedField("class", cls));
            }
            ControlGhost = gidx == 0x3ff ? -1 : gidx;
            ControlClass = cls ?? "";
            ControlState = d.Fields.Skip(5).ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
            if (mode == 4)
            {
                var sens = new List<object?>();
                while (r.Flag())
                {
                    var i = r.Int(4);
                    var a = r.Flag() ? r.Int(32) : 0;
                    var b = r.Flag() ? r.Int(32) : 0;
                    sens.Add(i + ":" + a.ToString("x8", CultureInfo.InvariantCulture) + "/" + b.ToString("x8", CultureInfo.InvariantCulture));
                    if (i < 7)
                    {
                        Shopping[i] ^= a;
                        Buying[i] ^= b;
                    }
                }
                r.Add("mode4", sens);
            }
            var reset = r.AddFlag("invReset");
            if (reset)
                Array.Clear(ItemCounts);
            var inv = new List<object?>();
            while (r.Flag())
            {
                var item = r.Int(8);
                var delta = r.SInt(10);
                inv.Add(item + (delta >= 0 ? "+" : "") + delta);
                if (item < ItemCounts.Length)
                    ItemCounts[item] += delta;
            }
            if (inv.Count != 0)
                r.Add("inventory", inv);
            if (mode == 2)
                ReadCommandMap(r);
            else if (mode == 1)
            {
                if (r.Flag())
                {
                    long x = r.Int(10), y = r.Int(10);
                    Waypoint = (x, y);
                    OnWaypoint?.Invoke(x, y);
                    r.Add("waypoint", new object[] { x, y });
                }
                var vis = new List<object?>();
                while (r.Flag())
                {
                    var word = r.Int(3);
                    var x = r.Int(32);
                    vis.Add(word + ":" + x.ToString("x8", CultureInfo.InvariantCulture));
                    Visible[word] ^= x;
                }
                if (vis.Count != 0)
                    r.Add("visibleMaskXor", vis);
            }
            d.Text = head + " " + FieldsText(d.Fields.Skip(3));
            d.Bits = r.Pos - start;
            return d;
        }
        finally
        {
            r.Out = save;
        }
    }

    // 4.4 step 1 and 4.5: GUI mode, score flag, the move buffer trim.
    private void ApplyHead(int mode, bool scoreFlag, int lastMove)
    {
        if (mode != GuiMode)
        {
            ++GuiModeChanges;
            if (!PlayGuiLoaded && mode != 0)
            {
                PlayGuiLoaded = true;
                OnScript?.Invoke("loadPlayGui();");
            }
            GuiMode = mode;
            OnGuiMode?.Invoke(mode);
        }
        ScoreFlag = scoreFlag;
        LastMove = lastMove;
        var n = (long)lastMove - (int)FirstMove;
        while (n > 0 && Moves.Count > 0)
        {
            ++FirstMove;
            Moves.RemoveAt(0);
            --n;
        }
        if (n > 0)
            FirstMove += (uint)n;
    }

    private static void ControlObserver(ClientReader r)
    {
        r.AddVec("rot");
        var mode = r.AddInt("camMode", 3);
        if (mode < 3)
            r.AddVec("pos");
        else if (mode == 3)
        {
            r.AddInt("orbitGhost", 10);
            r.Add("orbit", new[] { (double)r.F32(), r.F32(), r.F32() });
        }
        else if (mode == 4)
        {
            r.AddVec("pos");
            r.Add("orbit", new[] { (double)r.F32(), r.F32(), r.F32() });
        }
    }

    private static void ControlPlayer(ClientReader r)
    {
        r.AddInt("state2", 2);
        r.AddF32("rotZ");
        if (r.Flag())
        {
            r.AddVec("pos");
            r.AddVec("vel");
        }
        r.AddF32("pitch");
        r.AddF32("energy");
        r.AddFlag("flag1f4");
        r.AddFlag("flagfe4");
        r.AddUnit("unit7", 7);
        r.AddFlag("crouchfee");
        r.AddInt("fac", 4);
    }

    // Turret_readControlState 0045711c (vtable +0x160): rotation f32 (+0x8b4), elevation f32 (+0x8b8), fire count int(2) (+0x908), the
    // beam flag (+0x918 == 2), state int(4) (+0x8cc), energy f32 (+0x724). The mirror of Turret_writeControlState 004572f8.
    private static void ControlTurret(ClientReader r)
    {
        r.AddF32("aimRot");
        r.AddF32("aimElev");
        r.AddInt("fire2", 2);
        r.AddFlag("beam");
        r.AddInt("state4", 4);
        r.AddF32("energy");
    }

    // Vehicle / Flier control state (vehicle-sim.md 6.2).
    private static void ControlVehicle(ClientReader r)
    {
        r.AddInt("state2", 2);
        foreach (var n in new[] { "rot", "trot", "pos" })
            r.Add(n, new[] { (double)r.F32(), r.F32(), r.F32() });
        foreach (var n in new[] { "F16", "T16", "E16" })
            r.AddInt(n, 16);
        r.AddInt("dmg6", 6);
        r.AddInt("fire2", 2);
        r.AddInt("snd2", 2);
        r.AddF32("L");
        r.AddF32("S");
        r.AddFlag("landed");
    }

    // Command map rows (4.5, mode 2): the first slot, then rows of 8 slots with one flag each.
    private void ReadCommandMap(ClientReader r)
    {
        var slot = r.Int(8);
        r.Add("cmdFirst", (long)slot);
        var rows = new List<object?>();
        var nrows = 0;
        while (r.Flag())
        {
            ++nrows;
            for (var k = 0; k < 8; ++k)
            {
                var s = slot;
                slot = (slot + 1) & 0xff;
                if (!r.Flag())
                {
                    CommandSlots[s].Present = false; // an empty slot sets the datablock index to -1 [code 00405ec8]
                    continue;
                }
                uint x = r.Int(11), y = r.Int(11), team = r.Int(3), a = r.Int(2);
                string extra;
                uint cmd = 0;
                bool c = false, act = false;
                if (s < 0x80)
                    extra = "cmdstate=" + (cmd = r.Int(3));
                else
                {
                    c = r.Flag();
                    act = r.Flag();
                    extra = "ctl=" + (c ? 1 : 0) + " active=" + (act ? 1 : 0);
                }
                var grp = r.Int(6);
                var idx = r.Int(r.DbBits((int)grp));
                CommandSlots[s] = new ClientCommandSlot
                {
                    Present = true,
                    X = (int)x - 512,
                    Y = (int)y - 512,
                    Team = (int)team,
                    Status = (int)a,
                    CommandStatus = (int)cmd,
                    Group = (int)grp,
                    Index = (int)idx,
                    Controllable = c,
                    Active = act,
                };
                var row = s + ":(" + x + "," + y + ") team=" + team + " status=" + a + " " + extra + " db=" + grp + "/" + idx;
                rows.Add(row);
                CommandMap[(int)s] = row;
            }
        }
        r.Add("cmdRows", (long)nrows);
        r.Add("cmdmap", rows);
    }
}
