// Client prediction and server correction of the control object (docs/client-net.md 4.2 step 4, 4.4). The client steps
// its own copy of the control object with the server's step code (Player.UpdateMove, Flier.Update) on every move it
// creates; each server PSC block resets the copy to the server state and the unacknowledged moves run again.
// The copy lives in a prediction world the host supplies (a GameState with the mission's terrain, interiors and shapes):
// `CreateObject` makes it for a control ghost. The copy carries the ghost flag (GameBase.ClientSide), so the step skips
// its server-only parts.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>The predicted or the server state of the control object after a move.</summary>
public sealed class PredictedState
{
    /// <summary>The global move index.</summary>
    public long Move { get; set; }
    /// <summary>True when the control state carries a position.</summary>
    public bool HasPosition { get; set; } = true;
    /// <summary>True when the control state carries a velocity. A Flier sends none.</summary>
    public bool HasVelocity { get; set; } = true;
    /// <summary>The position.</summary>
    public float[] Position { get; } = new float[3];
    /// <summary>The velocity.</summary>
    public float[] Velocity { get; } = new float[3];
    /// <summary>The rotation.</summary>
    public float[] Rotation { get; } = new float[3];
    /// <summary>The pitch.</summary>
    public float Pitch { get; set; }
    /// <summary>The energy.</summary>
    public float Energy { get; set; }

    /// <summary>Captures the state of an object.</summary>
    /// <param name="o">The object.</param>
    /// <param name="move">The global move index.</param>
    /// <returns>The state.</returns>
    public static PredictedState Of(GameBase o, long move)
    {
        var s = new PredictedState { Move = move, Energy = o.Energy };
        for (var i = 0; i < 3; ++i)
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

/// <summary>One correction: the prediction the server state replaces and how far apart they were.</summary>
public sealed class CorrectionRecord
{
    /// <summary>The receive time (net time, ms).</summary>
    public uint Time { get; set; }
    /// <summary>The lastMove of the block. The server state follows move LastMove - 1.</summary>
    public long LastMove { get; set; }
    /// <summary>The class name.</summary>
    public string Class { get; set; } = "";
    /// <summary>The prediction for move LastMove - 1, or null when none was made.</summary>
    public ref PredictedState? Predicted => ref _predicted;
    private PredictedState? _predicted;
    /// <summary>The server state.</summary>
    public PredictedState Server { get; set; } = new();
    /// <summary>The unacknowledged moves left in the buffer.</summary>
    public int Pending { get; set; }
    /// <summary>The steps the replay ran, filled by the next Advance.</summary>
    public int Replayed { get; set; } = -1;
    /// <summary>The position error.</summary>
    public double PosError { get; set; }
    /// <summary>The velocity error.</summary>
    public double VelError { get; set; }
    /// <summary>The rotation error.</summary>
    public double RotError { get; set; }
    /// <summary>The pitch error.</summary>
    public double PitchError { get; set; }
    /// <summary>The energy error.</summary>
    public double EnergyError { get; set; }

    /// <summary>Measures the distances between the prediction and the server state: Euclidean for vectors, the shortest angle for the yaw.</summary>
    public void Measure()
    {
        if (Predicted == null)
            return;
        static double Dist(float[] a, float[] b)
        {
            double s = 0;
            for (var i = 0; i < 3; ++i)
                s += ((double)a[i] - b[i]) * ((double)a[i] - b[i]);
            return Math.Sqrt(s);
        }
        static double Angle(double a, double b)
        {
            var d = Math.IEEERemainder(a - b, 2 * Math.PI);
            return Math.Abs(d);
        }
        PosError = Server.HasPosition ? Dist(Predicted.Position, Server.Position) : 0;
        VelError = Server.HasVelocity ? Dist(Predicted.Velocity, Server.Velocity) : 0;
        RotError = 0;
        for (var i = 0; i < 3; ++i)
            RotError = Math.Max(RotError, Angle(Predicted.Rotation[i], Server.Rotation[i]));
        PitchError = Math.Abs((double)Predicted.Pitch - Server.Pitch);
        EnergyError = Math.Abs((double)Predicted.Energy - Server.Energy);
    }
}

/// <summary>Client prediction and server correction of the control object (docs/client-net.md 4.2 step 4, 4.4).</summary>
/// <remarks>The client steps its own copy of the control object with the server's step code (Player.UpdateMove, Flier.Update) on every move it creates. Each server PSC block resets the copy to the server state and the unacknowledged moves run again. The copy lives in a prediction world the host supplies (a GameState with the mission's terrain, interiors and shapes), and <see cref="CreateObject"/> makes it for a control ghost. The copy carries the ghost flag (<c>GameBase.ClientSide</c>), so the step skips its server-only parts.</remarks>
public sealed class ClientPrediction
{
    /// <summary>Makes the client copy of a control ghost in the prediction world. Returns null for no prediction.</summary>
    public Func<ClientGhost, ClientPsc, GameBase?>? CreateObject { get; set; }
    /// <summary>Deletes a client copy.</summary>
    public Action<GameBase>? DeleteObject { get; set; }

    /// <summary>Gets the client copy of the control object.</summary>
    public GameBase? ControlledObject { get; private set; }
    /// <summary>Gets the ghost index of the control object, -1 for none.</summary>
    public int Ghost { get; private set; } = -1;
    /// <summary>The time the copy is simulated up to, in ms (object +0x218).</summary>
    public uint SimTime { get; set; }
    /// <summary>The last frame time (object +0x21c).</summary>
    public uint LastFrame { get; set; }
    /// <summary>True once a server state was applied, so the replay runs.</summary>
    public bool Active { get; set; }
    /// <summary>The Player_interpolate factor of the drawn state (docs/client-net.md 4.4.1). Display only.</summary>
    public float DisplayFraction { get; set; }
    /// <summary>The per-tick position delta (+0x788, docs/client-net.md 4.4.1).</summary>
    public float[] TickDelta { get; } = new float[3];
    /// <summary>The corrections made so far.</summary>
    public List<CorrectionRecord> Corrections { get; } = [];
    /// <summary>The number of steps run in all.</summary>
    public long Steps { get; set; }
    /// <summary>Called for every correction.</summary>
    public Action<CorrectionRecord>? OnCorrection { get; set; }

    private readonly Dictionary<long, PredictedState> _history = [];
    private CorrectionRecord? _pending;

    /// <summary>Clears the prediction state.</summary>
    public void Reset()
    {
        Release();
        Corrections.Clear();
        Steps = 0;
    }

    /// <summary>The aim of a Turret that is the control object. It has no copy.</summary>
    public TurretControl Turret { get; } = new();

    /// <summary>Reads the control state block of a Turret and replays its moves per frame, without a prediction world.</summary>
    /// <param name="world">The client world.</param>
    /// <param name="receiveTime">The receive time in ms.</param>
    /// <remarks>Follows Turret_readControlState (0045711c) and Turret_clientUpdate (00456428).</remarks>
    public void OnTurretBlock(ClientWorld world, uint receiveTime)
    {
        var psc = world.Psc;
        var g = psc.HasControl && psc.ControlClass == "Turret" && psc.ControlGhost >= 0 ? world.Ghosts.Get(psc.ControlGhost) : null;
        if (g is { Class: "Turret" })
            Turret.OnServerBlock(psc, g, receiveTime);
        else
            Turret.Release();
    }

    /// <summary>Advances the Turret control for a frame.</summary>
    /// <param name="psc">The client PSC.</param>
    /// <param name="now">The current time in ms.</param>
    /// <returns>The steps run.</returns>
    public int AdvanceTurret(ClientPsc psc, uint now) => Turret.Advance(psc, now);

    private void Release()
    {
        if (ControlledObject != null)
        {
            if (ControlledObject is Player p)
                p.LocallyControlled = false;   // vtable +0x144(0)
            DeleteObject?.Invoke(ControlledObject);
        }
        ControlledObject = null;
        Ghost = -1;
        Active = false;
        _history.Clear();
        _pending = null;
    }

    /// <summary>Packs a move in the full form and unpacks it again (docs/client-net.md 4.2 step 4).</summary>
    /// <param name="m">The move.</param>
    /// <returns>The move: axes in 1/15, pitch and turn clamped, NaN to 0.</returns>
    /// <remarks>The buffered move is packed once more for every packet (4.3), so the round trip that fills the buffer runs in round-to-nearest, where packing its result gives the same 4-bit axes again. Toward zero it does not (7/15 stored toward zero packs as 6).</remarks>
    public static Move Quantize(in Move m) => RoundTrip(m, false);

    /// <summary>Returns the move as the server reads it from the wire.</summary>
    /// <param name="m">The move.</param>
    /// <returns>The move read in the script FPU mode (toward zero) as <c>GameConnection.ReadPsc</c> does. The prediction steps with this one, so it runs the server's floats.</returns>
    public static Move ServerMove(in Move m) => RoundTrip(m, true);

    private static Move RoundTrip(in Move m, bool towardZero)
    {
        var old = TowardZero;
        TowardZero = towardZero;
        try
        {
            var buf = new byte[64];
            var bs = new BitBuffer(buf, buf.Length);
            Moves.PackMove(bs, m, true, null);
            var rd = new BitBuffer(buf, buf.Length);
            var q = new Move();
            Moves.UnpackMove(rd, ref q, true, false);
            return q;
        }
        finally
        {
            TowardZero = old;
        }
    }

    /// <summary>Looks up the move of a tick.</summary>
    /// <param name="psc">The client PSC.</param>
    /// <param name="t">The tick time.</param>
    /// <param name="now">The current time.</param>
    /// <param name="m">Receives the move.</param>
    /// <param name="index">Receives the entry index.</param>
    /// <returns>False when the buffer holds 30 entries or the tick is older than the buffer. Otherwise the entry is <c>(count - 1) - (((now - 1) >> 5) - (t >> 5))</c>.</returns>
    /// <remarks>Follows the PlayerPSC move lookup (00404c04).</remarks>
    public static bool MoveForTick(ClientPsc psc, uint t, uint now, out Move m, out int index)
    {
        m = new Move();
        index = -1;
        var count = psc.Moves.Count;
        var back = (long)((now - 1) >> 5) - (t >> 5);
        if (count == 30 || count <= back || back < 0)
            return false;
        index = (int)(count - 1 - back);
        m = psc.Moves[index];
        return true;
    }

    /// <summary>Applies the control object part of a server block (docs/client-net.md 4.4, 4.5), after <c>ClientPsc.ReadServerBlock</c>.</summary>
    /// <param name="world">The client world.</param>
    /// <param name="receiveTime">The receive time in ms.</param>
    /// <returns>The correction record.</returns>
    /// <remarks>The move buffer is trimmed by the reader. Here the copy follows the control ghost, takes the server state and sets +0x218.</remarks>
    public CorrectionRecord? OnServerBlock(ClientWorld world, uint receiveTime)
    {
        var psc = world.Psc;
        if (!psc.HasControl)
            return null;
        var g = psc.ControlGhost >= 0 ? world.Ghosts.Get(psc.ControlGhost) : null;
        var predictable = g != null && (g.Class == "Player" || g.Class == "Flier") && !(g.State.TryGetValue("dead", out var dv) && dv is long dl && dl != 0);
        if (g == null || !predictable)
        {
            if (ControlledObject != null)
                Release();
            return null;
        }
        if (Ghost != g.Index || ControlledObject == null)
        {
            Release();
            ControlledObject = CreateObject?.Invoke(g, psc);
            if (ControlledObject == null)
                return null;
            Ghost = g.Index;
            ControlledObject.ClientSide = true;
            if (ControlledObject is Player np)
                np.LocallyControlled = true;   // vtable +0x144(1), 00414468
            if (ControlledObject is Vehicle nv)
                nv.Piloted = true;             // Vehicle_clientUpdate: +0x8a1 = control client != 0
        }
        SyncGhost(g);
        var rec = new CorrectionRecord { Time = receiveTime, LastMove = psc.LastMove, Class = g.Class, Pending = psc.Moves.Count };
        _history.TryGetValue(psc.LastMove - 1L, out rec.Predicted);
        Apply(psc.ControlState);
        rec.Server = PredictedState.Of(ControlledObject, psc.LastMove - 1L);
        rec.Server.HasPosition = psc.ControlState.ContainsKey("pos");
        rec.Server.HasVelocity = psc.ControlState.ContainsKey("vel");
        rec.Measure();
        // Older predictions are acknowledged.
        foreach (var k in _history.Keys.Where(k => k < psc.LastMove).ToList())
            _history.Remove(k);
        // 4.4 step 3: buffer entry i is the move of the tick at +0x218 + 32 i.
        SimTime = unchecked((uint)((int)((receiveTime - 1) & ~31u) - ((psc.Moves.Count - 1) * 32)));
        if (!Active)
            LastFrame = receiveTime;
        Active = true;
        Corrections.Add(rec);
        _pending = rec;
        OnCorrection?.Invoke(rec);
        return rec;
    }

    // The ghost fields the step reads that the control state does not carry: recharge rate (+0x728) and the items of
    // the image slots (the mass).
    private void SyncGhost(ClientGhost g)
    {
        if (ControlledObject is Player p)
            SyncPlayerGhost(p, g);
    }

    /// <summary>Copies the state of a Player ghost to the client copy.</summary>
    /// <param name="p">The Player copy.</param>
    /// <param name="g">The ghost.</param>
    public static void SyncPlayerGhost(Player p, ClientGhost g)
    {
        if (g.State.TryGetValue("f728", out var r) && r is double rr)
            p.RechargeRate = (float)rr;
        Span<int> items = stackalloc int[8];
        var any = false;
        for (var i = 0; i < 8; ++i)
        {
            items[i] = g.State.TryGetValue("item" + i, out var v) && v is long l ? (int)l : -1;
            any |= items[i] != -1;
        }
        if (any)
            p.SetClientItems(items);
    }

    private static float F(Dictionary<string, object?> s, string k) => s.TryGetValue(k, out var v) && v is double d ? (float)d : 0.0f;
    private static long L(Dictionary<string, object?> s, string k) => s.TryGetValue(k, out var v) && v is long l ? l : 0;

    private static void Vec(Dictionary<string, object?> s, string k, float[] output)
    {
        if (s.TryGetValue(k, out var v) && v is double[] a)
            for (var i = 0; i < 3; ++i)
                output[i] = (float)a[i];
    }

    // The control state fields of ClientPsc (Player 00413b90, Vehicle 00446830, Flier 0043079c) onto the copy.
    private void Apply(Dictionary<string, object?> s)
    {
        if (ControlledObject is Player p)
        {
            var c = new Player.ClientControl
            {
                Yaw = F(s, "rotZ"),
                HasPosition = s.ContainsKey("pos"),
                Pitch = F(s, "pitch"),
                Energy = F(s, "energy"),
                Contact = L(s, "flag1f4") != 0,
                Jetting = L(s, "flagfe4") != 0,
                Crouch = L(s, "crouchfee") != 0,
                JumpContact = (int)L(s, "fac"),
            };
            if (s.TryGetValue("unit7", out var u) && u is double ud)
                c.Traction7 = (uint)Math.Round(ud * 127.0);
            Vec(s, "pos", c.Position);
            Vec(s, "vel", c.Vel);
            p.ApplyClientControlState(c);
            Array.Clear(TickDelta);
        }
        else if (ControlledObject is Vehicle v)
        {
            var c = new Vehicle.ClientControl
            {
                SpeedF16 = (uint)L(s, "F16"),
                SpeedT16 = (uint)L(s, "T16"),
                Energy16 = (uint)L(s, "E16"),
                HasFlier = s.ContainsKey("L"),
                L = F(s, "L"),
                S = F(s, "S"),
                Landed = L(s, "landed") != 0,
            };
            Vec(s, "rot", c.Rot);
            Vec(s, "trot", c.TargetRot);
            Vec(s, "pos", c.Position);
            v.ApplyClientControlState(c);
        }
    }

    /// <summary>Runs the tick loop over the buffered moves for the control object, once per frame (docs/client-net.md 4.4).</summary>
    /// <param name="psc">The client PSC.</param>
    /// <param name="now">The current time in ms.</param>
    /// <returns>The steps run.</returns>
    /// <remarks>Follows Player_clientUpdate (00414c38) and for a Flier Flier_clientUpdate (0042e254) and Vehicle_clientUpdate (0044549c).</remarks>
    public int Advance(ClientPsc psc, uint now)
    {
        if (!Active || ControlledObject == null)
            return 0;
        var steps = 0;
        if (ControlledObject is Player p)
            steps = AdvancePlayer(p, psc, now);
        else if (ControlledObject is Flier f)
            steps = AdvanceFlier(f, psc, now);
        LastFrame = now;
        _pending?.Replayed = steps;
        _pending = null;
        Steps += steps;
        return steps;
    }

    private void Record(GameBase o, ClientPsc psc, int index)
    {
        var move = psc.FirstMove + index;
        _history[move] = PredictedState.Of(o, move);
    }

    private int AdvancePlayer(Player p, ClientPsc psc, uint now)
    {
        var steps = 0;
        while ((int)(SimTime - now) < 0)
        {
            var next = Math.Min((SimTime + 32) & ~31u, now);
            var covered = next - SimTime;
            if ((SimTime & 31) == 0)
            {
                float[] before = [p.Transform.P[0], p.Transform.P[1], p.Transform.P[2]];
                if (MoveForTick(psc, SimTime, now, out var bm, out var idx))
                {
                    var m = ServerMove(bm);
                    p.LastMove = m;           // +0xff0
                    p.UpdateMove(m);          // Player_updateMove(move, 0)
                    p.UpdateAnimation(0.032f);
                    ++steps;
                    for (var i = 0; i < 3; ++i)
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
        var steps = 0;
        while ((int)(SimTime - now) < 0)
        {
            var next = Math.Min((SimTime + 32) & ~31u, now);
            if (MoveForTick(psc, SimTime, now, out var bm, out var idx))
            {
                var m = ServerMove(bm);
                // Vehicle_clientUpdate: dt = covered part of the tick, in seconds (the frame may end inside a tick).
                var ms = ((next - 1) & 31) + 1 - (SimTime & 31);
                var dt = (float)(ms * 0.001);
                var mv = m;
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

    /// <summary>Binds the control Player to the mount of a vehicle that is not its control object (a passenger).</summary>
    /// <param name="world">The client world.</param>
    /// <param name="remote">The remote ghosts.</param>
    /// <remarks>The unpack of the mount fields binds +0x1028 and +0x102c to the client object of the mount ghost (a remote vehicle copy). Called before the step of every frame.</remarks>
    public void BindControlMount(ClientWorld world, RemoteGhosts remote)
    {
        if (ControlledObject is not Player p || Ghost < 0)
            return;
        var g = world.Ghosts.Get(Ghost);
        if (g != null && RemoteGhosts.ParseMount(g, out var mg, out var point) && remote.RemoteMount(mg) is { } mo)
        {
            if (p.MountId != mo.Id())
                p.MountId = mo.Id();
            p.MountPoint = point;
        }
        else if (p.MountId != 0)
            p.MountId = 0;
    }

    /// <summary>Places the mounted control Player on its mount, so the camera of a passenger moves with the vehicle.</summary>
    /// <remarks>Follows the interpolation 00414a00. The transform follows the mount as it is now, after the vehicle copies advanced.</remarks>
    public void PlaceControlRider()
    {
        if (ControlledObject is Player { MountId: not 0 } p && p.PlaceOnMount())
            Array.Clear(TickDelta);
    }

    /// <summary>Returns the drawn position of a Player copy: the stepped state plus the sub-tick fraction of the last delta (docs/client-net.md 4.4.1).</summary>
    /// <returns>The position.</returns>
    public float[] DisplayPosition()
    {
        var r = new float[3];
        if (ControlledObject == null)
            return r;
        for (var i = 0; i < 3; ++i)
            r[i] = ControlledObject.Transform.P[i] + (DisplayFraction * TickDelta[i]);
        return r;
    }
}
