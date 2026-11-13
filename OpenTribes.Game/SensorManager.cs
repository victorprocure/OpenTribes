// Port of src/game/sensor_manager.hpp and src/game/sensor_manager.cpp: the SensorManager (object 720,
// docs/static-sim.md 3): the sensor lists per team, the suppressor list and the sweep that refreshes the visibility word
// (+0x200) of one target and of one or more clients' owned objects per tick, with the detection by eye (3.5 step 6) and the
// jamming distance of a Player (3.8).
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>The sensor manager object (id 720): the sensor network lists, jamming and target tracking.</summary>
/// <remarks>Port of class SensorManager in src/game/sensor_manager.hpp.</remarks>
public sealed class SensorManager : SimObject
{
    private readonly ClassContext mCtx;
    private int mJamHead, mJamHeadPrev; // head.next (+0x7d4), head.prev (+0x7d8, unused)
    private readonly List<GameBase>[] mTeams = new List<GameBase>[8];
    private readonly List<GameBase> mSuppressors = [];
    private readonly List<GameBase> mDummy = []; // team -1: a list the sweep never reads
    private int mTargetCursor = -1, mClientCursor = -1;

    /// <summary>The game state. Set by the game layer.</summary>
    public GameState? State;

    /// <summary>Creates the manager.</summary>
    /// <param name="ctx">The class context.</param>
    public SensorManager(ClassContext ctx)
    {
        mCtx = ctx;
        for (var i = 0; i < 8; ++i)
            mTeams[i] = [];
    }

    /// <inheritdoc/>
    public override string ClassName() => "SensorManager";
    /// <inheritdoc/>
    public override int FixedId() => 720;
    /// <inheritdoc/>
    public override bool ProcessArguments(IReadOnlyList<string> args) => true;
    /// <summary>Returns the head of the jamming list (head.next, +0x7d4).</summary>
    /// <returns>The object id, or -1 for the head itself.</returns>
    public int JamFirst() => mJamHead;
    /// <summary>Returns the sensor list of a team.</summary>
    /// <param name="team">The team id.</param>
    /// <returns>The sensors.</returns>
    public IReadOnlyList<GameBase> TeamList(int team) => mTeams[team];
    /// <summary>Returns the list of suppressor objects.</summary>
    /// <returns>The suppressors.</returns>
    public IReadOnlyList<GameBase> Suppressors() => mSuppressors;
    /// <summary>Returns the target slot the timer visits next.</summary>
    /// <returns>The slot, or -1.</returns>
    public int TargetCursor() => mTargetCursor;
    /// <summary>Returns the client slot the timer visits next.</summary>
    /// <returns>The slot, or -1.</returns>
    public int ClientCursor() => mClientCursor;

    // src/game/sensor_manager.cpp lenf: s = f32((x*x + y*y) + z*z); the float with bits (bits(s) >> 1) + 0x1fc00000
    // (0x005ed031), s stored in the current mode.
    private static float Lenf(ReadOnlySpan<float> v)
    {
        var d = Mul(v[0], v[0]);
        d = Add(d, Mul(v[1], v[1]));
        d = Add(d, Mul(v[2], v[2]));
        var s = Store(d);
        var bits = BitConverter.SingleToUInt32Bits(s);
        bits = (bits >> 1) + 0x1fc00000u;
        return BitConverter.UInt32BitsToSingle(bits);
    }

    // src/game/sensor_manager.cpp sensorClass: class of a sensor within its list (3.2): not supressable first, pinger
    // before non-pinger.
    private static int SensorClass(GameBase s) => (s.SensorData.Supressable ? 2 : 0) + (s.SensorData.Pinger ? 0 : 1);

    // src/game/sensor_manager.cpp boxCentre.
    private static bool BoxCentre(GameState state, GameBase obj, Span<float> c)
    {
        if (state.World.Find(obj) is Container.Entry e)
        {
            for (var i = 0; i < 3; ++i)
                c[i] = Store(Mul(Add(e.Box[i], e.Box[i + 3]), 0.5));
            return true;
        }
        for (var i = 0; i < 3; ++i)
            c[i] = obj.Transform.P[i];
        return false;
    }

    // src/game/sensor_manager.cpp SensorManager::listFor.
    private List<GameBase> ListFor(int team)
    {
        if (team is >= 0 and < 8)
            return mTeams[team];
        if (team == 8)
            return mSuppressors; // team 8 resolves to the suppressor head (3.2)
        return mDummy;
    }

    // src/game/sensor_manager.cpp SensorManager::place: new entries first within their class; classes in the order of
    // sensorClass.
    private static void Place(List<GameBase> list, GameBase sensor)
    {
        var c = SensorClass(sensor);
        var at = 0;
        while (at < list.Count && SensorClass(list[at]) < c)
            ++at;
        list.Insert(at, sensor);
    }

    /// <summary>Adds an object to the sensor list of its team (docs/static-sim.md 3.2).</summary>
    /// <param name="sensor">The Sensor or Turret object, once its datablock binds.</param>
    public void Insert(GameBase sensor)
    {
        Remove(sensor);
        Place(sensor.SensorData.Supression ? mSuppressors : ListFor(sensor.Team), sensor);
        sensor.SensorListed = true;
    }

    /// <summary>Removes an object from the sensor lists.</summary>
    /// <param name="sensor">The object.</param>
    public void Remove(GameBase sensor)
    {
        mSuppressors.RemoveAll(s => s == sensor);
        mDummy.RemoveAll(s => s == sensor);
        foreach (var l in mTeams)
            l.RemoveAll(s => s == sensor);
        sensor.SensorListed = false;
    }

    /// <summary>Moves an object to the list of its new team.</summary>
    /// <param name="sensor">The object.</param>
    public void TeamChanged(GameBase sensor)
    {
        if (sensor.SensorListed)
            Insert(sensor);
    }

    /// <summary>Tells whether a sensor detects a target.</summary>
    /// <param name="s">The sensor.</param>
    /// <param name="t">The target.</param>
    /// <returns>True when the target is detected.</returns>
    /// <remarks>Follows 0x004391b0 (Sensor). The Turret override (0x004560a4) adds the field of view.</remarks>
    public bool Detects(GameBase s, GameBase t)
    {
        if (!s.IsActiveFlag())
            return false;
        using var chop = ChopScope();
        Span<float> sc = stackalloc float[3], tc = stackalloc float[3];
        BoxCentre(State!, s, sc);
        BoxCentre(State!, t, tc);
        if (s.ShapeClass() == GameBase.ShapeKind.Turret && s.TurretData.Fov > -1.0f)
        {
            // dot(normalize(centre(T) - centre(S)), eye forward) >= FOV. [TODO: the eye transform (dummy eye node) is
            // approximated by the turret aim.]
            Span<float> d = [Store(Sub(tc[0], sc[0])), Store(Sub(tc[1], sc[1])), Store(Sub(tc[2], sc[2]))];
            var l = Sqrt(Add(Add(Mul(d[0], d[0]), Mul(d[1], d[1])), Mul(d[2], d[2])));
            if (l > 0)
            {
                double ce = Math.Cos(s.TurretElevation), se = Math.Sin(s.TurretElevation);
                double cr = Math.Cos(s.TurretRotation), sr = Math.Sin(s.TurretRotation);
                double f0 = Mul(-sr, ce), f1 = Mul(cr, ce), f2 = se;
                var dotp = Div(Add(Add(Mul(d[0], f0), Mul(d[1], f1)), Mul(d[2], f2)), l);
                if (dotp < s.TurretData.Fov)
                    return false;
            }
        }
        Span<float> diff =
        [
            Store(Sub(tc[0], s.Transform.P[0])), Store(Sub(tc[1], s.Transform.P[1])), Store(Sub(tc[2], s.Transform.P[2])),
        ];
        // The centre of T against the sensor position (+0x188), approximate length, range as an int.
        if (!(Lenf(diff) <= (double)s.SensorData.Range))
            return false;
        if (s.SensorData.Doppler != 0)
        {
            var v = Lenf(t.Velocity);
            if (!(v >= (double)s.SensorData.Doppler))
                return false;
        }
        if (s.SensorData.CastLOS)
        {
            object? hitObj = null;
            Vec3 hit = default, n = default;
            if (State!.World.CastRay(new Vec3(sc[0], sc[1], sc[2]), new Vec3(tc[0], tc[1], tc[2]), 0x20c00006u, s, ref hitObj, ref hit, ref n) &&
                hitObj != t)
                return false;
        }
        return true;
    }

    // ---- jamming-player list (3.8) ----

    // src/game/sensor_manager.cpp SensorManager::jamNode.
    private GameBase? JamNode(int id)
    {
        if (id <= 0 || State?.Sim?.Server() == null)
            return null;
        return State.Sim.Server()!.FindById(id) as GameBase;
    }

    /// <summary>Unlinks a player from the jamming list.</summary>
    /// <param name="p">The player.</param>
    /// <remarks>Follows 0x0040fc58. The neighbours are rewired through the stored links and the player's own links stay as they were. -1 is the head. A link to an object that no longer exists is read as the end of the list and written to nowhere (D11).</remarks>
    public void JamUnlink(GameBase p)
    {
        if (p.JamNext != 0)
        {
            if (p.JamNext == -1)
                mJamHeadPrev = p.JamPrev;
            else if (JamNode(p.JamNext) is GameBase g)
                g.JamPrev = p.JamPrev;
        }
        if (p.JamPrev != 0)
        {
            if (p.JamPrev == -1)
                mJamHead = p.JamNext;
            else if (JamNode(p.JamPrev) is GameBase g)
                g.JamNext = p.JamNext;
        }
    }

    /// <summary>Links a player into the jamming list after the head.</summary>
    /// <param name="p">The player.</param>
    /// <remarks>Follows 0x004398c8.</remarks>
    public void JamLink(GameBase p)
    {
        p.JamNext = mJamHead;
        p.JamPrev = -1;
        if (p.JamNext != 0)
        {
            if (p.JamNext == -1)
                mJamHeadPrev = p.Id();
            else if (JamNode(p.JamNext) is GameBase g)
                g.JamPrev = p.Id();
        }
        mJamHead = p.Id();
    }

    /// <summary>Refreshes the sensor state of one object (docs/static-sim.md 3.5).</summary>
    /// <param name="t">The object.</param>
    /// <remarks>Follows 0x00439d70.</remarks>
    public void Update(GameBase t)
    {
        t.SensorWord &= 0x00ffffffu; // state 0
        var enabled = true;
        if (State?.Console != null)
        {
            var v = State.Console.PeekVariable("$SensorNetworkEnabled");
            enabled = v.Length == 0 || Numbers.ParseBool(v);
        }
        var teams = State != null ? State.TeamCount : 0;
        if (!enabled)
        {
            for (var i = 0; i < teams && i < 8; ++i)
                t.SensorWord &= ~(1u << i);
            return;
        }
        // Every loop of the update runs over the teams of the PlayerManager (+0x24edc), not over all eight bits.
        for (var i = 0; i < teams && i < 8; ++i)
        {
            if (i == t.Team)
                t.SensorWord |= (1u << i) & ~(t.SensorWord >> 16);
            else
                t.SensorWord &= ~(1u << i);
        }
        var jammed = false;
        foreach (var s in mSuppressors)
            if (Detects(s, t))
            {
                t.SensorWord = (t.SensorWord & 0x00ffffffu) | (2u << 24);
                jammed = true;
                break;
            }
        // Jamming players (3.8): the list is walked from the head, neither teams nor the jammers' own state matter. A
        // list that loops (stale links) would hang the original; the walk is bounded (D11).
        {
            using var chop = ChopScope();
            var id = mJamHead;
            for (var guard = 0; id != 0 && guard < 4096; ++guard)
            {
                var jp = JamNode(id);
                if (jp == null)
                    break;
                float[] diff = [ Store(Sub(t.Transform.P[0], jp.Transform.P[0])), Store(Sub(t.Transform.P[1], jp.Transform.P[1])),
                                 Store(Sub(t.Transform.P[2], jp.Transform.P[2])) ];
                if (Player.From(jp) is Player player && Lenf(diff) < player.SensorSuppression)
                {
                    t.SensorWord = (t.SensorWord & 0x00ffffffu) | (2u << 24);
                    jammed = true;
                    break;
                }
                id = jp.JamNext;
            }
        }
        for (var i = 0; i < teams && i < 8; ++i)
        {
            if (i == t.Team)
                continue;
            var detected = false;
            foreach (var s in mTeams[i])
            {
                if (jammed && s.SensorData.Supressable)
                    break;
                if (Detects(s, t))
                {
                    detected = true;
                    if (s.SensorData.Pinger)
                    {
                        t.SensorWord = (t.SensorWord & 0x00ffffffu) | (1u << 24);
                        break;
                    }
                }
            }
            if (detected)
                t.SensorWord |= (1u << i) & ~(t.SensorWord >> 16);
            else
                t.SensorWord &= ~(1u << i);
        }
        if (State != null)
            EyeDetect(t);
    }

    // src/game/sensor_manager.cpp SensorManager::hazeAt: FUN_004495dc (0x004495dc): the haze value of the container that
    // holds `t` for the eye position. The base container (vtable +0xa4, 0x004fac64) asks its parent and gives 0 at the
    // root; SimTerrain (0x00518468) gives 0.01 inside the haze start, else 1 - x^2 with x = (d - start) * scale - 1 (3.5).
    // start = hazeDistance^2 / visibleDistance capped at 0.75 * min(visibleDistance, 10000); scale = 1 / (1.02 * (cap -
    // start)) (SimTerrain_updateRenderParams 0x00517fe0).
    private double HazeAt(GameBase t, ReadOnlySpan<float> eye)
    {
        var te = State!.World.TerrainOf(t);
        if (te == null || te.Object == null)
            return 0.0;
        var tobj = (PersistObject)te.Object;
        var vis = tobj.Fields().Real("visibleDistance", 3000.0f);
        var haze = tobj.Fields().Real("hazeDistance", 2000.0f);
        var cap = vis < 10000.0f ? vis : 10000.0f;
        var q = Store(Div(haze, vis));
        var start = Store(Mul(haze, q));
        if (!(Mul(cap, 0.75) >= start))
            start = Store(Mul(cap, 0.75));
        var span = Store(Sub(cap, start));
        var inv = Store(Div(1.0, Add(Mul(span, 0.02), span)));
        float[] diff = [Store(Sub(eye[0], t.Transform.P[0])), Store(Sub(eye[1], t.Transform.P[1])), Store(Sub(eye[2], t.Transform.P[2]))];
        var d = Lenf(diff);
        if (!(d < cap))
            d = cap;
        if (!(d >= start))
            return 0.01f;
        var x = Store(Sub(Mul(Sub(d, start), inv), 1.0));
        return Sub(1.0, Mul(x, x));
    }

    // src/game/sensor_manager.cpp SensorManager::seesByEye: Player_seesObject (0x004128ac, 3.5): `player` sees `t` through
    // the eye of its client's control object.
    private bool SeesByEye(Player pl, GameBase t)
    {
        if (pl.Fov == 0.0f || State == null)
            return false;
        using var chop = ChopScope();
        var rec = State.Players.FindHuman(pl.OwnerClient);
        if (rec == null || rec.ControlObject == 0 || State.Sim == null || State.Sim.Server() == null)
            return false;
        if (State.Sim.Server()!.FindById(rec.ControlObject) is not GameBase ctl)
            return false;
        var half = Mul(pl.Fov, 0.5);
        var cosHalf = Store(Math.Cos(half));
        var tanHalf = Store(Math.Tan(half));
        var eye = ctl.EyeTransform();
        Span<float> c = stackalloc float[3];
        BoxCentre(State, t, c);
        float[] d3 = [Store(Sub(c[0], eye.P[0])), Store(Sub(c[1], eye.P[1])), Store(Sub(c[2], eye.P[2]))];
        // the point in the eye frame: the rows of the matrix are the axes (row 1 looks forward)
        var local = new float[3];
        for (var r = 0; r < 3; ++r)
            local[r] = Store(Add(Add(Mul(eye.M[r * 3], d3[0]), Mul(eye.M[(r * 3) + 1], d3[1])), Mul(eye.M[(r * 3) + 2], d3[2])));
        var d = Store(Sqrt(Add(Add(Mul(local[0], local[0]), Mul(local[1], local[1])), Mul(local[2], local[2]))));
        if (!(d >= 1.0f))
            return true;
        if (!(cosHalf <= Div(local[1], d)))
            return false;
        float ex = 0, ez = 0;
        if (State.World.Find(t) is Container.Entry e)
        {
            ez = Store(Sub(e.Box[5], e.Box[2]));
            ex = Store(Sub(e.Box[3], e.Box[0]));
        }
        var size = Store(Div(ez <= ex ? ex : ez, Mul(d, tanHalf)));
        var h = HazeAt(t, eye.P);
        if (!(pl.DetectHazeFactor < Mul(Sub(1.0, Mul(h, pl.DetectMinVisibility)), size)))
            return false;
        object? hitObj = null;
        Vec3 hit = default, n = default;
        if (State.World.CastRay(new Vec3(eye.P[0], eye.P[1], eye.P[2]), new Vec3(c[0], c[1], c[2]), 0x20c00006u, ctl, ref hitObj,
                                ref hit, ref n) && hitObj != t)
            return false;
        return true;
    }

    // src/game/sensor_manager.cpp SensorManager::eyeDetect: 3.5 step 6: every client record (humans and AI, newest first)
    // whose team does not see T yet.
    private void EyeDetect(GameBase t)
    {
        var m = State!.Sim?.Server();
        if (m == null)
            return;
        foreach (var id in State.Players.PlayerIds())
        {
            var r = State.Players.Find(id);
            if (r == null || PlayerManager.VisibleToTeam(t, r.Team) || r.OwnedObject == 0)
                continue;
            if (Player.From(m.FindById(r.OwnedObject)) is Player pl && SeesByEye(pl, t))
                t.SensorWord |= (1u << (int)((uint)r.Team & 31u)) & ~(t.SensorWord >> 16);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Follows 0x00439cc4 (docs/static-sim.md 3.4): the next occupied target slot, then <c>max(1, players >> 4)</c> client slots.</remarks>
    public override void OnTimerEvent(float time, int timerId, float interval)
    {
        if (State == null)
            return;
        var targets = State.Targets;
        var any = false;
        foreach (var g in targets)
            if (g != null)
                any = true;
        if (any)
        {
            for (var step = 0; step < 128; ++step)
            {
                mTargetCursor = (mTargetCursor + 1) & 127;
                if (mTargetCursor < targets.Count && targets[mTargetCursor] != null)
                {
                    Update(targets[mTargetCursor]!);
                    break;
                }
            }
        }
        var players = State.Players.PlayerCount();
        if (players != 0)
        {
            var n = Math.Max(1, players >> 4);
            for (var k = 0; k < n; ++k)
            {
                for (var step = 0; step < 128; ++step)
                {
                    mClientCursor = (mClientCursor + 1) & 127;
                    var r = State.Players.Find(2048 + mClientCursor);
                    if (r == null || !r.Used)
                        continue;
                    if (r.OwnedObject != 0 && State.Sim?.Server()?.FindById(r.OwnedObject) is GameBase o)
                        Update(o);
                    break;
                }
            }
        }
    }

    /// <summary>Registers the SensorManager class. The server creates object 720 from it.</summary>
    public static void Register() =>
        SimClasses.RegisterClass("SensorManager", c => new SensorManager(c) { State = c.Game as GameState });
}
