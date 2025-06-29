// Client prediction and server correction of the control object (docs/client-net.md 4.2 step 4, 4.4). The client steps
// its own copy of the control object with the server's step code (Player.UpdateMove, Flier.Update) on every move it
// creates; each server PSC block resets the copy to the server state and the unacknowledged moves run again.
// The copy lives in a prediction world the host supplies (a GameState with the mission's terrain, interiors and shapes):
// `CreateObject` makes it for a control ghost. The copy carries the ghost flag (GameBase.ClientSide), so the step skips
// its server-only parts.
using OpenTribes.Core;
using OpenTribes.Net;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

// The predicted or the server state of the control object, after the move `Move` (global move index).
public sealed class PredictedState
{
    public long Move;
    public bool HasPosition = true, HasVelocity = true; // what the control state carries (a Flier sends no velocity)
    public readonly float[] Position = new float[3], Velocity = new float[3], Rotation = new float[3];
    public float Pitch, Energy;

    public static PredictedState Of(GameBase o, long move)
    {
        var s = new PredictedState { Move = move, Energy = o.Energy };
        for (int i = 0; i < 3; ++i)
        {
            s.Position[i] = o.Transform.P[i];
            s.Velocity[i] = o.Velocity[i];
            s.Rotation[i] = o.Rotation[i];
        }
        if (o is Player p)
            s.Pitch = p.Pitch;
        return s;
    }
}

// One correction: the prediction the server state replaces and how far apart they were.
public sealed class CorrectionRecord
{
    public uint Time;                 // receive time (net time, ms)
    public long LastMove;             // the block's lastMove: the server state follows move LastMove - 1
    public string Class = "";
    public PredictedState? Predicted; // the prediction for move LastMove - 1, null when none was made
    public PredictedState Server = new();
    public int Pending;               // unacknowledged moves left in the buffer
    public int Replayed = -1;         // steps the replay ran (filled by the next Advance)
    public double PosError, VelError, RotError, PitchError, EnergyError;

    // Distances between the prediction and the server state (Euclidean for vectors, shortest angle for the yaw).
    public void Measure()
    {
        if (Predicted == null)
            return;
        static double Dist(float[] a, float[] b)
        {
            double s = 0;
            for (int i = 0; i < 3; ++i)
                s += ((double)a[i] - b[i]) * ((double)a[i] - b[i]);
            return Math.Sqrt(s);
        }
        static double Angle(double a, double b)
        {
            double d = Math.IEEERemainder(a - b, 2 * Math.PI);
            return Math.Abs(d);
        }
        PosError = Server.HasPosition ? Dist(Predicted.Position, Server.Position) : 0;
        VelError = Server.HasVelocity ? Dist(Predicted.Velocity, Server.Velocity) : 0;
        RotError = 0;
        for (int i = 0; i < 3; ++i)
            RotError = Math.Max(RotError, Angle(Predicted.Rotation[i], Server.Rotation[i]));
        PitchError = Math.Abs((double)Predicted.Pitch - Server.Pitch);
        EnergyError = Math.Abs((double)Predicted.Energy - Server.Energy);
    }
}

public sealed class ClientPrediction
{
    // Makes the client copy of a control ghost in the prediction world (null: no prediction for it).
    public Func<ClientGhost, ClientPsc, GameBase?>? CreateObject;
    public Action<GameBase>? DeleteObject;

    public GameBase? Object { get; private set; }
    public int Ghost { get; private set; } = -1;
    public uint SimTime;              // object +0x218: simulated up to (ms)
    public uint LastFrame;            // object +0x21c
    public bool Active;               // a server state was applied, the replay runs
    public float DisplayFraction;     // Player_interpolate factor of the drawn state (4.4.1), display only
    public readonly float[] TickDelta = new float[3]; // per-tick position delta +0x788 (4.4.1)
    public readonly List<CorrectionRecord> Corrections = new();
    public long Steps;                // steps run in all
    public Action<CorrectionRecord>? OnCorrection;

    private readonly Dictionary<long, PredictedState> mHistory = new();
    private CorrectionRecord? mPending;

    public void Reset()
    {
        Release();
        Corrections.Clear();
        Steps = 0;
    }

    private void Release()
    {
        if (Object != null)
        {
            if (Object is Player p)
                p.LocallyControlled = false;   // vtable +0x144(0)
            DeleteObject?.Invoke(Object);
        }
        Object = null;
        Ghost = -1;
        Active = false;
        mHistory.Clear();
        mPending = null;
    }

    // 4.2 step 4: the move is packed in the full form and unpacked again (axes in 1/15, pitch and turn clamped, NaN to
    // 0). The buffered move is packed once more for every packet (4.3), so the round trip that fills the buffer runs in
    // round-to-nearest, where packing its result gives the same 4-bit axes again; toward zero it does not (7/15 stored
    // toward zero packs as 6).
    public static Move Quantize(in Move m) => RoundTrip(m, false);

    // The move as the server reads it from the wire (GameConnection.ReadPsc in the script FPU mode, toward zero): the
    // prediction steps with this one, so it runs the server's floats.
    public static Move ServerMove(in Move m) => RoundTrip(m, true);

    private static Move RoundTrip(in Move m, bool towardZero)
    {
        bool old = Fpu.TowardZero;
        Fpu.TowardZero = towardZero;
        try
        {
            byte[] buf = new byte[64];
            var bs = new BitStream(buf, buf.Length);
            Moves.PackMove(bs, m, true, null);
            var rd = new BitStream(buf, buf.Length);
            var q = new Move();
            Moves.UnpackMove(rd, ref q, true, false);
            return q;
        }
        finally
        {
            Fpu.TowardZero = old;
        }
    }

    // PlayerPSC move lookup 00404c04: no move when the buffer holds 30 entries or the tick is older than the buffer;
    // otherwise entry (count - 1) - (((now - 1) >> 5) - (t >> 5)). `index` is the entry.
    public static bool MoveForTick(ClientPsc psc, uint t, uint now, out Move m, out int index)
    {
        m = new Move();
        index = -1;
        int count = psc.Moves.Count;
        long back = (long)((now - 1) >> 5) - (t >> 5);
        if (count == 30 || count <= back || back < 0)
            return false;
        index = (int)(count - 1 - back);
        m = psc.Moves[index];
        return true;
    }

    // 4.4 and 4.5, after ClientPsc.ReadServerBlock: the control object part. The move buffer is trimmed by the reader;
    // here the copy follows the control ghost, takes the server state and sets +0x218. Returns the correction record.
    public CorrectionRecord? OnServerBlock(ClientWorld world, uint receiveTime)
    {
        ClientPsc psc = world.Psc;
        if (!psc.HasControl)
            return null;
        ClientGhost? g = psc.ControlGhost >= 0 ? world.Ghosts.Get(psc.ControlGhost) : null;
        bool predictable = g != null && (g.Class == "Player" || g.Class == "Flier") && !(g.State.TryGetValue("dead", out object? dv) && dv is long dl && dl != 0);
        if (g == null || !predictable)
        {
            if (Object != null)
                Release();
            return null;
        }
        if (Ghost != g.Index || Object == null)
        {
            Release();
            Object = CreateObject?.Invoke(g, psc);
            if (Object == null)
                return null;
            Ghost = g.Index;
            Object.ClientSide = true;
            if (Object is Player np)
                np.LocallyControlled = true;   // vtable +0x144(1), 00414468
            if (Object is Vehicle nv)
                nv.Piloted = true;             // Vehicle_clientUpdate: +0x8a1 = control client != 0
        }
        SyncGhost(g);
        var rec = new CorrectionRecord { Time = receiveTime, LastMove = psc.LastMove, Class = g.Class, Pending = psc.Moves.Count };
        mHistory.TryGetValue(psc.LastMove - 1L, out rec.Predicted);
        Apply(psc.ControlState);
        rec.Server = PredictedState.Of(Object, psc.LastMove - 1L);
        rec.Server.HasPosition = psc.ControlState.ContainsKey("pos");
        rec.Server.HasVelocity = psc.ControlState.ContainsKey("vel");
        rec.Measure();
        // Older predictions are acknowledged.
        foreach (long k in mHistory.Keys.Where(k => k < psc.LastMove).ToList())
            mHistory.Remove(k);
        // 4.4 step 3: buffer entry i is the move of the tick at +0x218 + 32 i.
        SimTime = unchecked((uint)((int)((receiveTime - 1) & ~31u) - (psc.Moves.Count - 1) * 32));
        if (!Active)
            LastFrame = receiveTime;
        Active = true;
        Corrections.Add(rec);
        mPending = rec;
        OnCorrection?.Invoke(rec);
        return rec;
    }

    // The ghost fields the step reads that the control state does not carry: recharge rate (+0x728) and the items of
    // the image slots (the mass).
    private void SyncGhost(ClientGhost g)
    {
        if (Object is Player p)
        {
            if (g.State.TryGetValue("f728", out object? r) && r is double rr)
                p.RechargeRate = (float)rr;
            Span<int> items = stackalloc int[8];
            bool any = false;
            for (int i = 0; i < 8; ++i)
            {
                items[i] = g.State.TryGetValue("item" + i, out object? v) && v is long l ? (int)l : -1;
                any |= items[i] != -1;
            }
            if (any)
                p.SetClientItems(items);
        }
    }

    private static float F(Dictionary<string, object?> s, string k) => s.TryGetValue(k, out object? v) && v is double d ? (float)d : 0.0f;
    private static long L(Dictionary<string, object?> s, string k) => s.TryGetValue(k, out object? v) && v is long l ? l : 0;

    private static void Vec(Dictionary<string, object?> s, string k, float[] output)
    {
        if (s.TryGetValue(k, out object? v) && v is double[] a)
            for (int i = 0; i < 3; ++i)
                output[i] = (float)a[i];
    }

    // The control state fields of ClientPsc (Player 00413b90, Vehicle 00446830, Flier 0043079c) onto the copy.
    private void Apply(Dictionary<string, object?> s)
    {
        if (Object is Player p)
        {
            var c = new Player.ClientControl
            {
                Yaw = F(s, "rotZ"), HasPosition = s.ContainsKey("pos"), Pitch = F(s, "pitch"), Energy = F(s, "energy"),
                Contact = L(s, "flag1f4") != 0, Jetting = L(s, "flagfe4") != 0, Crouch = L(s, "crouchfee") != 0,
                JumpContact = (int)L(s, "fac"),
            };
            if (s.TryGetValue("unit7", out object? u) && u is double ud)
                c.Traction7 = (uint)Math.Round(ud * 127.0);
            Vec(s, "pos", c.Position);
            Vec(s, "vel", c.Vel);
            p.ApplyClientControlState(c);
            Array.Clear(TickDelta);
        }
        else if (Object is Vehicle v)
        {
            var c = new Vehicle.ClientControl
            {
                SpeedF16 = (uint)L(s, "F16"), SpeedT16 = (uint)L(s, "T16"), Energy16 = (uint)L(s, "E16"),
                HasFlier = s.ContainsKey("L"), L = F(s, "L"), S = F(s, "S"), Landed = L(s, "landed") != 0,
            };
            Vec(s, "rot", c.Rot);
            Vec(s, "trot", c.TargetRot);
            Vec(s, "pos", c.Position);
            v.ApplyClientControlState(c);
        }
    }

    // Player_clientUpdate 00414c38 (Player) and Flier_clientUpdate 0042e254 / Vehicle_clientUpdate 0044549c (Flier) for the
    // control object, once per frame: the tick loop over the buffered moves (4.4). Returns the steps run.
    public int Advance(ClientPsc psc, uint now)
    {
        if (!Active || Object == null)
            return 0;
        int steps = 0;
        if (Object is Player p)
            steps = AdvancePlayer(p, psc, now);
        else if (Object is Flier f)
            steps = AdvanceFlier(f, psc, now);
        LastFrame = now;
        if (mPending != null)
        {
            mPending.Replayed = steps;
            mPending = null;
        }
        Steps += steps;
        return steps;
    }

    private void Record(GameBase o, ClientPsc psc, int index)
    {
        long move = psc.FirstMove + (long)index;
        mHistory[move] = PredictedState.Of(o, move);
    }

    private int AdvancePlayer(Player p, ClientPsc psc, uint now)
    {
        int steps = 0;
        while ((int)(SimTime - now) < 0)
        {
            uint next = Math.Min((SimTime + 32) & ~31u, now);
            uint covered = next - SimTime;
            if ((SimTime & 31) == 0)
            {
                float[] before = { p.Transform.P[0], p.Transform.P[1], p.Transform.P[2] };
                if (MoveForTick(psc, SimTime, now, out Move bm, out int idx))
                {
                    Move m = ServerMove(bm);
                    p.LastMove = m;           // +0xff0
                    p.UpdateMove(m);          // Player_updateMove(move, 0)
                    p.UpdateAnimation(0.032f);
                    ++steps;
                    for (int i = 0; i < 3; ++i)
                        TickDelta[i] = X87.F32(Sub(p.Transform.P[i], before[i]));
                    Record(p, psc, idx);
                }
                else
                {
                    Array.Clear(TickDelta);   // per-tick deltas +0x788..+0x798 cleared
                }
                // 4.4.1: a partial step is drawn (32 - covered) / 32 of a tick back.
                DisplayFraction = covered < 32 ? -(32 - covered) / 32.0f : 0.0f;
            }
            else
            {
                DisplayFraction += covered / 32.0f;
            }
            SimTime = next;
        }
        return steps;
    }

    private int AdvanceFlier(Flier f, ClientPsc psc, uint now)
    {
        // Flier_clientUpdate: an active or braking piloted Flier is not landed.
        if (f.Piloted && (f.ActiveFlag || f.Braking))
            f.Landed = false;
        int steps = 0;
        while ((int)(SimTime - now) < 0)
        {
            uint next = Math.Min((SimTime + 32) & ~31u, now);
            if (MoveForTick(psc, SimTime, now, out Move bm, out int idx))
            {
                Move m = ServerMove(bm);
                // Vehicle_clientUpdate: dt = covered part of the tick, in seconds (the frame may end inside a tick).
                uint ms = ((next - 1) & 31) + 1 - (SimTime & 31);
                float dt = (float)(ms * 0.001);
                Move mv = m;
                f.LastMove = m;
                f.Update(ref mv, dt);
                if (((next) & 31) == 0)
                {
                    ++steps;
                    Record(f, psc, idx);
                }
            }
            SimTime = next;
        }
        return steps;
    }

    // The drawn position of a Player copy (4.4.1): the stepped state plus the sub-tick fraction of the last delta.
    public float[] DisplayPosition()
    {
        var r = new float[3];
        if (Object == null)
            return r;
        for (int i = 0; i < 3; ++i)
            r[i] = (float)(Object.Transform.P[i] + DisplayFraction * TickDelta[i]);
        return r;
    }
}
