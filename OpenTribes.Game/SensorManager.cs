// Port of src/game/sensor_manager.hpp and src/game/sensor_manager.cpp: the SensorManager (object 720,
// docs/static-sim.md 3): the sensor lists per team, the suppressor list and the sweep that refreshes the visibility word
// (+0x200) of one target and of one or more clients' owned objects per tick. The Player parts (detection by eye, 3.5
// step 6, and the jamming distance of a Player, 3.8) are stage P4b.
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

// src/game/sensor_manager.hpp class SensorManager.
public sealed class SensorManager : SimObject
{
    private readonly ClassContext mCtx;
    private int mJamHead, mJamHeadPrev; // head.next (+0x7d4), head.prev (+0x7d8, unused)
    private readonly List<GameBase>[] mTeams = new List<GameBase>[8];
    private readonly List<GameBase> mSuppressors = new();
    private readonly List<GameBase> mDummy = new(); // team -1: a list the sweep never reads
    private int mTargetCursor = -1, mClientCursor = -1;

    public GameState? State; // set by the game layer

    public SensorManager(ClassContext ctx)
    {
        mCtx = ctx;
        for (int i = 0; i < 8; ++i)
            mTeams[i] = new List<GameBase>();
    }

    public override string ClassName() => "SensorManager";
    public override int FixedId() => 720;
    public override bool ProcessArguments(IReadOnlyList<string> args) => true;
    public int JamFirst() => mJamHead;
    public IReadOnlyList<GameBase> TeamList(int team) => mTeams[team];
    public IReadOnlyList<GameBase> Suppressors() => mSuppressors;
    public int TargetCursor() => mTargetCursor;
    public int ClientCursor() => mClientCursor;

    // src/game/sensor_manager.cpp lenf: s = f32((x*x + y*y) + z*z); the float with bits (bits(s) >> 1) + 0x1fc00000
    // (0x005ed031), s stored in the current mode.
    private static float Lenf(ReadOnlySpan<float> v)
    {
        double d = Mul(v[0], v[0]);
        d = Add(d, Mul(v[1], v[1]));
        d = Add(d, Mul(v[2], v[2]));
        float s = Store(d);
        uint bits = BitConverter.SingleToUInt32Bits(s);
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
            for (int i = 0; i < 3; ++i)
                c[i] = Store(Mul(Add(e.Box[i], e.Box[i + 3]), 0.5));
            return true;
        }
        for (int i = 0; i < 3; ++i)
            c[i] = obj.Transform.P[i];
        return false;
    }

    // src/game/sensor_manager.cpp SensorManager::listFor.
    private List<GameBase> ListFor(int team)
    {
        if (team >= 0 && team < 8)
            return mTeams[team];
        if (team == 8)
            return mSuppressors; // team 8 resolves to the suppressor head (3.2)
        return mDummy;
    }

    // src/game/sensor_manager.cpp SensorManager::place: new entries first within their class; classes in the order of
    // sensorClass.
    private static void Place(List<GameBase> list, GameBase sensor)
    {
        int c = SensorClass(sensor);
        int at = 0;
        while (at < list.Count && SensorClass(list[at]) < c)
            ++at;
        list.Insert(at, sensor);
    }

    // src/game/sensor_manager.cpp SensorManager::insert: sensor list membership (3.2): Sensor and Turret objects once
    // their datablock binds.
    public void Insert(GameBase sensor)
    {
        Remove(sensor);
        Place(sensor.SensorData.Supression ? mSuppressors : ListFor(sensor.Team), sensor);
        sensor.SensorListed = true;
    }

    // src/game/sensor_manager.cpp SensorManager::remove.
    public void Remove(GameBase sensor)
    {
        mSuppressors.RemoveAll(s => s == sensor);
        mDummy.RemoveAll(s => s == sensor);
        foreach (List<GameBase> l in mTeams)
            l.RemoveAll(s => s == sensor);
        sensor.SensorListed = false;
    }

    // src/game/sensor_manager.cpp SensorManager::teamChanged: same list, new team.
    public void TeamChanged(GameBase sensor)
    {
        if (sensor.SensorListed)
            Insert(sensor);
    }

    // src/game/sensor_manager.cpp SensorManager::detects: 0x004391b0 (Sensor), Turret override 0x004560a4 adds the field
    // of view.
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
            Span<float> d = stackalloc float[3] { Store(Sub(tc[0], sc[0])), Store(Sub(tc[1], sc[1])), Store(Sub(tc[2], sc[2])) };
            double l = Sqrt(Add(Add(Mul(d[0], d[0]), Mul(d[1], d[1])), Mul(d[2], d[2])));
            if (l > 0)
            {
                double ce = Math.Cos(s.TurretElevation), se = Math.Sin(s.TurretElevation);
                double cr = Math.Cos(s.TurretRotation), sr = Math.Sin(s.TurretRotation);
                double f0 = Mul(-sr, ce), f1 = Mul(cr, ce), f2 = se;
                double dotp = Div(Add(Add(Mul(d[0], f0), Mul(d[1], f1)), Mul(d[2], f2)), l);
                if (dotp < s.TurretData.Fov)
                    return false;
            }
        }
        Span<float> diff = stackalloc float[3]
        {
            Store(Sub(tc[0], s.Transform.P[0])), Store(Sub(tc[1], s.Transform.P[1])), Store(Sub(tc[2], s.Transform.P[2])),
        };
        // The centre of T against the sensor position (+0x188), approximate length, range as an int.
        if (!(Lenf(diff) <= (double)s.SensorData.Range))
            return false;
        if (s.SensorData.Doppler != 0)
        {
            float v = Lenf(t.Velocity);
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

    // src/game/sensor_manager.cpp SensorManager::jamUnlink: 0x0040fc58: the neighbours are rewired through the stored
    // links, the player's own links stay as they were. -1 is the head; a link to an object that no longer exists is read
    // as the end of the list and written to nowhere (D11).
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

    // src/game/sensor_manager.cpp SensorManager::jamLink: 0x004398c8: after the head.
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

    // src/game/sensor_manager.cpp SensorManager::update: 0x00439d70 (3.5): refresh of one object.
    public void Update(GameBase t)
    {
        t.SensorWord &= 0x00ffffffu; // state 0
        bool enabled = true;
        if (State?.Console != null)
        {
            string v = State.Console.PeekVariable("$SensorNetworkEnabled");
            enabled = v.Length == 0 || Numbers.ParseBool(v);
        }
        int teams = State != null ? State.TeamCount : 0;
        if (!enabled)
        {
            for (int i = 0; i < teams && i < 8; ++i)
                t.SensorWord &= ~(1u << i);
            return;
        }
        // Every loop of the update runs over the teams of the PlayerManager (+0x24edc), not over all eight bits.
        for (int i = 0; i < teams && i < 8; ++i)
        {
            if (i == t.Team)
                t.SensorWord |= (1u << i) & ~(t.SensorWord >> 16);
            else
                t.SensorWord &= ~(1u << i);
        }
        bool jammed = false;
        foreach (GameBase s in mSuppressors)
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
            int id = mJamHead;
            for (int guard = 0; id != 0 && guard < 4096; ++guard)
            {
                GameBase? jp = JamNode(id);
                if (jp == null)
                    break;
                // P4b: Player: a jamming Player within its sensorSuppression of T jams it (lenf of the position
                // difference against Player +0xfe0).
                id = jp.JamNext;
            }
        }
        for (int i = 0; i < teams && i < 8; ++i)
        {
            if (i == t.Team)
                continue;
            bool detected = false;
            foreach (GameBase s in mTeams[i])
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
        // P4b: Player: eyeDetect(t): detection by eye of T by the Player of every client record whose team does not see
        // it yet (3.5 step 6, Player_seesObject 0x004128ac).
    }

    // src/game/sensor_manager.cpp SensorManager::onTimerEvent: 0x00439cc4 (3.4): the next occupied target slot, then
    // max(1, players >> 4) client slots.
    public override void OnTimerEvent(float time, int timerId, float interval)
    {
        if (State == null)
            return;
        List<GameBase?> targets = State.Targets;
        bool any = false;
        foreach (GameBase? g in targets)
            if (g != null)
                any = true;
        if (any)
        {
            for (int step = 0; step < 128; ++step)
            {
                mTargetCursor = (mTargetCursor + 1) & 127;
                if (mTargetCursor < targets.Count && targets[mTargetCursor] != null)
                {
                    Update(targets[mTargetCursor]!);
                    break;
                }
            }
        }
        int players = State.Players.PlayerCount();
        if (players != 0)
        {
            int n = Math.Max(1, players >> 4);
            for (int k = 0; k < n; ++k)
            {
                for (int step = 0; step < 128; ++step)
                {
                    mClientCursor = (mClientCursor + 1) & 127;
                    ClientRecord? r = State.Players.Find(2048 + mClientCursor);
                    if (r == null || !r.Used)
                        continue;
                    if (r.OwnedObject != 0 && State.Sim?.Server()?.FindById(r.OwnedObject) is GameBase o)
                        Update(o);
                    break;
                }
            }
        }
    }

    // src/game/sensor_manager.cpp registerSensorManager: the "SensorManager" class (the server creates object 720 from
    // it).
    public static void Register() =>
        SimClasses.RegisterClass("SensorManager", c => new SensorManager(c) { State = c.Game as GameState });
}
