// Client PlayerPSC (docs/client-net.md 4.3, 4.4, 4.5; docs/game-stream.md 6): the read of the server block, the effects it
// has on the PSC state (GUI mode, move buffer trim, control object, inventory, command map) and the client block that
// carries the moves. Prediction and replay are the next stage (part b); this file keeps the data they need.
using System.Globalization;

namespace OpenTribes.Game;

// One block of the PSC member as read from the stream.
public sealed class DecodedPsc
{
    public string Direction = "";
    public string Text = "";
    public int Bits;
    public List<DecodedField> Fields = new();
}

public sealed class ClientPsc
{
    // Server block state (4.5).
    public int GuiMode;                  // psc +0x3d8
    public bool ScoreFlag;               // score dialog visible
    public int LastMove = -1;            // last move number in the block
    public bool HasControl;
    public double DamageFlash;           // psc +0x3e0
    public int ControlGhost = -1;        // 0x3ff = none; psc +0xa4 as a ghost index
    public string ControlClass = "";
    public Dictionary<string, object?> ControlState = new();
    public readonly int[] ItemCounts = new int[200];     // +0xac
    public readonly uint[] Shopping = new uint[7], Buying = new uint[7];
    public readonly uint[] Visible = new uint[8];
    public readonly Dictionary<int, string> CommandMap = new();
    public (long X, long Y)? Waypoint;
    public int GuiModeChanges;
    public bool PlayGuiLoaded;                           // loadPlayGui() was evaluated on the first change away from 0

    // Move buffer (4.2, 4.3): moves not yet acknowledged, the global index of the first entry and the ones created so far.
    public readonly List<Move> Moves = new();
    public uint FirstMove;               // +0x3d0
    public uint FovByte = 169;           // unit(8) of currentFov / 135: 90 degrees sends 169

    public Action<string>? OnScript;

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
        GuiModeChanges = 0;
        PlayGuiLoaded = false;
    }

    // Number of moves created so far (the next global move index).
    public uint MovesCreated => FirstMove + (uint)Moves.Count;

    public void AddMove(in Move m)
    {
        if (Moves.Count < 30)
            Moves.Add(m);
    }

    // ---- client block (4.3, game-stream.md 6.2) ----

    // PlayerPSC_writePacket client branch: fov byte, first move index, every buffered move (the first full with no
    // previous, the others against the previous one), end flag.
    public void WriteClient(BitStream bs)
    {
        bs.WriteInt(FovByte, 8);
        bs.WriteInt(FirstMove, 32);
        Move? prev = null;
        foreach (Move m in Moves)
        {
            bs.WriteFlag(true);
            Move copy = m;
            OpenTribes.Game.Moves.PackMove(bs, copy, true, prev);
            prev = m;
        }
        bs.WriteFlag(false);
    }

    // Client block read (the c2s direction of a capture; the server reads it with GameConnection.ReadPsc).
    public static DecodedPsc ReadClientBlock(ClientReader r)
    {
        int start = r.Pos;
        var d = new DecodedPsc { Direction = "c2s" };
        long fov = r.Int(8);
        long first = r.Int(32);
        var moves = new List<Move>();
        var m = new Move();
        bool havePrev = false;
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
        for (int i = 0; i < moves.Count; ++i)
            d.Fields.Add(new DecodedField("move" + (first + i), FormatMove(moves[i])));
        d.Text = "psc fov=" + fov + " (" + (fov / 255.0 * 135).ToString("0.0", CultureInfo.InvariantCulture) + " deg) firstMove=" + first +
                 " moves=" + moves.Count;
        d.Bits = r.Pos - start;
        return d;
    }

    public static string FormatMove(in Move m) =>
        "f=" + m.Forward.ToString("0.00", CultureInfo.InvariantCulture) + " b=" + m.Backward.ToString("0.00", CultureInfo.InvariantCulture) +
        " l=" + m.Left.ToString("0.00", CultureInfo.InvariantCulture) + " r=" + m.Right.ToString("0.00", CultureInfo.InvariantCulture) +
        " jet=" + (m.Jet ? 1 : 0) + " crouch=" + (m.Crouch ? 1 : 0) + " jump=" + (m.Jump ? 1 : 0) + " fire=" + (m.Fire ? 1 : 0) +
        " useItem=" + (m.UseItem == -1 ? "None" : m.UseItem.ToString(CultureInfo.InvariantCulture)) +
        " pitch=" + ClientReader.G(m.Pitch) + " turn=" + ClientReader.G(m.Turn);

    // ---- server block (4.5, game-stream.md 6.3) ----

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

    public static string FieldsText(IEnumerable<DecodedField> f) => string.Join(" ", f.Select(x => x.Name + "=" + PyStr(x.Value)));

    // PlayerPSC server block, client branch 00405ec8.
    public DecodedPsc ReadServerBlock(ClientReader r)
    {
        int start = r.Pos;
        var d = new DecodedPsc { Direction = "s2c" };
        var save = r.Out;
        r.Out = d.Fields;
        try
        {
            int mode = (int)r.Int(3);
            bool f = r.Flag();
            int last = unchecked((int)r.Int(32));
            bool has = r.Flag();
            string head = "psc mode=" + mode + " flag6a=" + (f ? 1 : 0) + " lastMove=" + last;
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
            int gidx = (int)r.Add("controlGhost", (long)r.Int(10));
            string? cls = null;
            if (gidx != 0x3ff)
            {
                cls = r.World.Ghosts.ClassOf(gidx);
                switch (cls)
                {
                    case "Player": ControlPlayer(r); break;
                    case "ObserverCamera": ControlObserver(r); break;
                    case "Flier":
                    case "Vehicle": ControlVehicle(r); break;
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
                    uint i = r.Int(4);
                    uint a = r.Flag() ? r.Int(32) : 0;
                    uint b = r.Flag() ? r.Int(32) : 0;
                    sens.Add(i + ":" + a.ToString("x8", CultureInfo.InvariantCulture) + "/" + b.ToString("x8", CultureInfo.InvariantCulture));
                    if (i < 7)
                    {
                        Shopping[i] ^= a;
                        Buying[i] ^= b;
                    }
                }
                r.Add("mode4", sens);
            }
            bool reset = r.AddFlag("invReset");
            if (reset)
                Array.Clear(ItemCounts);
            var inv = new List<object?>();
            while (r.Flag())
            {
                uint item = r.Int(8);
                int delta = r.SInt(10);
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
                    r.Add("waypoint", new object[] { x, y });
                }
                var vis = new List<object?>();
                while (r.Flag())
                {
                    uint word = r.Int(3);
                    uint x = r.Int(32);
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
        }
        ScoreFlag = scoreFlag;
        LastMove = lastMove;
        long n = (long)lastMove - (int)FirstMove;
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
        long mode = r.AddInt("camMode", 3);
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

    // Vehicle / Flier control state (vehicle-sim.md 6.2).
    private static void ControlVehicle(ClientReader r)
    {
        r.AddInt("state2", 2);
        foreach (string n in new[] { "rot", "trot", "pos" })
            r.Add(n, new[] { (double)r.F32(), r.F32(), r.F32() });
        foreach (string n in new[] { "F16", "T16", "E16" })
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
        uint slot = r.Int(8);
        r.Add("cmdFirst", (long)slot);
        var rows = new List<object?>();
        int nrows = 0;
        while (r.Flag())
        {
            ++nrows;
            for (int k = 0; k < 8; ++k)
            {
                uint s = slot;
                slot = (slot + 1) & 0xff;
                if (!r.Flag())
                    continue;
                uint x = r.Int(11), y = r.Int(11), team = r.Int(3), a = r.Int(2);
                string extra;
                if (s < 0x80)
                    extra = "cmdstate=" + r.Int(3);
                else
                {
                    bool c = r.Flag(), act = r.Flag();
                    extra = "ctl=" + (c ? 1 : 0) + " active=" + (act ? 1 : 0);
                }
                uint grp = r.Int(6);
                uint idx = r.Int(r.DbBits((int)grp));
                string row = s + ":(" + x + "," + y + ") team=" + team + " status=" + a + " " + extra + " db=" + grp + "/" + idx;
                rows.Add(row);
                CommandMap[(int)s] = row;
            }
        }
        r.Add("cmdRows", (long)nrows);
        r.Add("cmdmap", rows);
    }
}
