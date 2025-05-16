// Port of src/game/turret.hpp, src/game/turret_fire.cpp, src/game/turret_control.cpp and the activation part of
// src/game/turret_sim.cpp: the turret threads, target search, tracking and fire (docs/static-sim.md 4.3 - 4.8) and the
// turret as a control object (4.6, docs/game-stream.md 6.5). The x87 fpatan, fcos and fsin of the original are
// Math.Atan2, Math.Cos and Math.Sin (portable form of src/core/ldouble.hpp); the 80-bit constant 1 / (2 pi) is the
// 53-bit rounded double. The Chop class of the files is X87.Chop or Fpu.ChopScope; world::x87::f32 is X87.F32 and a
// plain float(...) store inside a Chop scope is Fpu.Store.
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

// src/game/turret.hpp namespace turret: position of a shape thread as the instance keeps it (docs/static-sim.md 14.6).
// Every store is an f32 store toward zero.
public static class TurretThread
{
    // src/game/turret.hpp threadNormalise: TSSequence_normalise (0x005b8d94): below 0 or above 1 a cyclic sequence wraps
    // by floor, a one-shot sequence clamps.
    public static void ThreadNormalise(ref float pos, bool cyclic)
    {
        if (pos < 0.0f)
            pos = cyclic ? X87.F32(Sub(pos, Math.Floor((double)pos))) : 0.0f;
        else if (pos > 1.0f)
            pos = cyclic ? X87.F32(Sub(pos, Math.Floor((double)pos))) : 1.0f;
    }

    // src/game/turret.hpp threadStep: thread step (0x005c01f0 -> 0x005c02f0, time scale 1.0):
    // pos = f32(f32(scale x delta) + pos).
    public static void ThreadStep(ref float pos, float delta, bool cyclic)
    {
        float d = X87.F32(Mul(1.0f, delta));
        pos = X87.F32(Add(d, pos));
        ThreadNormalise(ref pos, cyclic);
    }

    // src/game/turret.hpp threadSetPos: TSThread_setPosition (0x005c0190): a step by f32((target - pos) / scale), not an
    // assignment.
    public static void ThreadSetPos(ref float pos, float target, bool cyclic) =>
        ThreadStep(ref pos, X87.F32(Div(Sub(target, pos), 1.0f)), cyclic);

    // src/game/turret.hpp threadAdvance: TSThread_advance (0x005c01b0): a step by f32(dt / duration of the sequence).
    public static void ThreadAdvance(ref float pos, float dt, float duration, bool cyclic) =>
        ThreadStep(ref pos, X87.F32(Div(dt, duration)), cyclic);
}

public partial class GameBase
{
    private const double kPiD = 3.14159265358979323846;
    private const double kTwoPiD = 6.283185307179586;
    private const double kHalfPiD = 1.5707963267948966;
    private const float kTwoPiF = 6.2831855f;

    // 1 / (2 pi) as the 80-bit constant at 0x00457070, rounded to 53 bits.
    private static readonly double kInvTwoPi = (double)0xa2f9836e4e4416f4UL * Math.ScaleB(1.0, -66);

    // src/game/turret_fire.cpp turretFire: the function-static R250 of each deflection path.
    private static readonly R250 sTurretNormalPath = new(0x8377), sTurretSustainedPath = new(0x8377);

    // The two generators as a freshly started process has them (the tests share one process).
    public static void ResetTurretRandoms()
    {
        sTurretNormalPath.Reseed(0x8377);
        sTurretSustainedPath.Reseed(0x8377);
    }

    // src/game/turret_fire.cpp stateOf.
    private static GameState StateOf(GameBase t) => (GameState)t.Services()!;

    // src/game/turret_fire.cpp atan2Borland: Borland atan2 (0x006067ac) for the finite, non-degenerate arguments the
    // turret feeds it; the same rules as the script math command (src/script/math_commands.cpp).
    private static double Atan2Borland(double a, double b)
    {
        if (a == 0.0 && b == 0.0)
            return 0; // the original's error handler never returns (DECISIONS D11)
        if (a == 0.0)
            return double.IsNegative(b) ? (double.IsNegative(a) ? -kPiD : kPiD) : a;
        if (b == 0.0)
            return double.IsNegative(a) ? -kHalfPiD : kHalfPiD;
        double r = Div(a, b);
        bool neg = r < 0;
        double ar = neg ? -r : r;
        double v;
        if (ar == 1)
            v = Div(kPiD, 4);
        else if (ar < 1)
            v = Math.Atan2(ar, 1.0);
        else
            v = Sub(Math.Atan2(1.0, ar), kHalfPiD);
        // atan(r) = neg ? -v : v for ar <= 1; for ar > 1 the branch above holds fpatan(1, ar) - pi/2 = -(atan(ar)), so the
        // sign flips.
        double t = ar < 1 || ar == 1 ? (neg ? -v : v) : (neg ? v : -v);
        if (double.IsNegative(b))
            t = double.IsNegative(a) ? Sub(t, kPiD) : Add(t, kPiD);
        return t;
    }

    // src/game/turret_fire.cpp calcRotation: 0x00452fa8: atan2(y, x) + pi/2 as f32.
    private static float CalcRotation(float y, float x) => X87.F32(Add(Atan2Borland(y, x), kHalfPiD));

    // src/game/turret_fire.cpp calcElevation: 0x00452efc: pitch of the point (x, y, z) in the turret's frame.
    private static float CalcElevation(float x, float y, float z)
    {
        double h = Atan2Borland(y, x);
        float t = X87.F32(-h);
        float c = X87.F32(Mul(Math.Cos(t), x));
        float r = X87.F32(Sub(c, Mul(Math.Sin(t), y)));
        float nz = -z;
        return X87.F32(Sub(Atan2Borland(r, nz), kHalfPiD));
    }

    // src/game/turret_fire.cpp wrapRotation: 0x00453120.
    private static void WrapRotation(ref float r)
    {
        while (r > kTwoPiD)
            r = X87.F32(Sub(r, kTwoPiD));
        while (r < 0.0f)
            r = X87.F32(Add(r, kTwoPiD));
    }

    // src/game/turret_fire.cpp wrapElevation: 0x0045317c.
    private static void WrapElevation(ref float e)
    {
        while (e < -kPiD)
            e = X87.F32(Add(e, kTwoPiD));
        while (e > kPiD)
            e = X87.F32(Sub(e, kTwoPiD));
    }

    // src/game/turret_fire.cpp boxCentre: centre of a container entry box: (max + min) * 0.5, each step stored as f32
    // (0x004125a0).
    private static bool TurretBoxCentre(GameState g, GameBase o, Span<float> c)
    {
        Span<float> mn = stackalloc float[3], mx = stackalloc float[3];
        if (!Pj.WorldBox(g, o, mn, mx))
        {
            for (int i = 0; i < 3; ++i)
                c[i] = o.Transform.P[i];
            return false;
        }
        for (int i = 0; i < 3; ++i)
        {
            float s = X87.F32(Add(mx[i], mn[i]));
            c[i] = X87.F32(Mul(s, 0.5f));
        }
        return true;
    }

    // src/game/turret_fire.cpp targetPoint: Player_getTargetPoint (0x004125a0): the box centre.
    private static void TurretTargetPoint(GameState g, GameBase p, Span<float> output) => TurretBoxCentre(g, p, output);

    // src/game/turret_fire.cpp cyclicSeq.
    private static bool CyclicSeq(GameBase t, int seq) =>
        t.Anim != null && seq >= 0 && seq < t.Anim.Sequences.Count && t.Anim.Sequences[seq].Cyclic;

    // src/game/turret_fire.cpp seqOf: a missing sequence gives handle 0 (TSThread_setSequence by name 0x005c00c4).
    private static int SeqOf(GameBase t, string name)
    {
        int s = t.Anim != null ? t.Anim.FindSequence(name) : -1;
        return s < 0 ? 0 : s;
    }

    // src/game/turret_fire.cpp elevateTarget: position of the elevate thread for the current elevation (0x00456ee5,
    // 0x00452e80).
    private static float ElevateTarget(GameBase t) =>
        X87.F32(Div(Sub(t.TurretElevation, t.TurretMinElevation), Sub(t.TurretMaxElevation, t.TurretMinElevation)));

    // src/game/turret_fire.cpp nodePosition: translation of a node entry of the turret's shape instance (+0x28 of the
    // instance node, 0x004565e0, 0x004567b0) in the pose of the current thread positions. False without the shape or
    // node.
    private static bool NodePosition(GameBase t, string node, Span<float> output)
    {
        GameState g = StateOf(t);
        ShapeInstance? inst = ShapeState.ShapeInstanceOf(g, t);
        int n = inst != null ? inst.ShapeData!.FindNode(node) : -1;
        if (n < 0 || n >= inst!.Entries.Length)
            return false;
        output[0] = inst.Entries[n].P[0];
        output[1] = inst.Entries[n].P[1];
        output[2] = inst.Entries[n].P[2];
        return true;
    }

    // src/game/turret_fire.cpp muzzleNode.
    private static bool MuzzleNode(GameBase t, Span<float> output) => NodePosition(t, "dummy muzzle", output);

    // src/game/turret_fire.cpp nodeTransform: transform at a node: the Euler rotation (elevation, 0, rotation) with the
    // node translation, applied to the object transform (Turret_getMuzzleTransform 0x004565e0, getEyeTransform
    // 0x004566ac): Mat3_fromEulerWrap 0x005ed000, flag 2, m_mul_TMat3F 0x005ed2b2.
    private static bool NodeTransform(GameBase t, string node, out Transform output)
    {
        Span<float> nodeT = stackalloc float[3];
        if (!NodePosition(t, node, nodeT))
        {
            output = t.Transform;
            return false;
        }
        var l = new TMat3();
        l.P = Float3.Of(nodeT);
        float[] euler = { t.TurretElevation, 0.0f, t.TurretRotation };
        Shape.EulerMatrix(euler, ref l);
        l.Flags |= 2u;
        var o = new TMat3();
        o.Flags = t.Transform.Flags;
        o.M = t.Transform.M;
        o.P = t.Transform.P;
        Shape.TmatMul(l, o, out TMat3 w);
        output = new Transform();
        output.Flags = w.Flags;
        output.M = w.M;
        output.P = w.P;
        return true;
    }

    // src/game/turret_fire.cpp normalise.
    private static void TurretNormalise(Span<float> v)
    {
        float k = X87.F32(Div(1.0, X87.F32(X87.Len(v))));
        for (int i = 0; i < 3; ++i)
            v[i] = X87.F32(Mul(v[i], k));
    }

    // src/game/turret_fire.cpp cross.
    private static void TurretCross(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> output)
    {
        output[0] = X87.F32(Sub(Mul(a[1], b[2]), Mul(a[2], b[1])));
        output[1] = X87.F32(Sub(Mul(a[2], b[0]), Mul(a[0], b[2])));
        output[2] = X87.F32(Sub(Mul(a[0], b[1]), Mul(a[1], b[0])));
    }

    // src/game/turret_fire.cpp toNodeSpace: the turret's frame at the muzzle node: world position of the node, and the
    // object rotation. L = (point - node position) in object space, in the order of m_mul_vector / m_mul_point with the
    // transposed rotation (0x00454e16 - 0x004551b6).
    private static void ToNodeSpace(GameBase t, ReadOnlySpan<float> point, Span<float> l)
    {
        Span<float> nodeT = stackalloc float[3] { 0, 0, 0 };
        MuzzleNode(t, nodeT);
        ReadOnlySpan<float> m = t.Transform.M;
        Span<float> np = stackalloc float[3];
        X87.MulPoint(nodeT, m, t.Transform.P, np);
        float[] inv = { m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8] };
        float[] neg = { -np[0], -np[1], -np[2] };
        float[] tp = new float[3];
        X87.MulVector(neg, inv, tp);
        X87.MulPoint(point, inv, tp, l);
    }

    // src/game/turret_fire.cpp projectileType: projectile class and index of the TurretData (+0x120, +0x124).
    private static bool TurretProjectileType(GameBase t, out int group, out int index, out DataBlockManager.Block? block)
    {
        GameState g = StateOf(t);
        group = index = -1;
        block = null;
        if (g.Sim == null || t.DataBlockName == null)
            return false;
        DataBlockManager.Block? b = g.Sim.DataBlocks().Find(t.DataBlockName);
        DataBlockManager.Field? f = b != null ? Pj.Field(b, "projectileType") : null;
        if (f == null || f.Elems.Count == 0 || f.Elems[0].I < 0)
            return false;
        group = f.Elems[0].I;
        index = f.Elems[0].I2;
        block = g.Sim.DataBlocks().At(group, index);
        return block != null;
    }

    // src/game/turret_fire.cpp projectileSpeed: projectile speed of 4.2 step 2: datablock getter at vtable +0xc.
    private static float TurretProjectileSpeedOf(GameBase t)
    {
        if (t.TurretProjectileSpeed >= 0)
            return t.TurretProjectileSpeed;
        float speed = 50.0f;
        if (TurretProjectileType(t, out int group, out _, out DataBlockManager.Block? b))
        {
            switch (group)
            {
                case Projectile.kRocketGroup:
                    speed = Pj.Real(b!, "terminalVelocity", 0.0f);
                    break;
                case Projectile.kBulletGroup:
                case Projectile.kGrenadeGroup:
                case Projectile.kSeekerGroup:
                    speed = Pj.Real(b!, "muzzleVelocity", 0.0f);
                    break;
                default:
                    speed = 1.0e7f; // lasers, lightning, repair: the constant at 0x00462920
                    break;
            }
        }
        t.TurretProjectileSpeed = speed;
        return speed;
    }

    // src/game/turret_fire.cpp leadPoint: Turret_leadPoint (0x00453090).
    private static void LeadPoint(GameBase t, float d, ReadOnlySpan<float> vel, ReadOnlySpan<float> tp, Span<float> output)
    {
        float k = X87.F32(Div(d, TurretProjectileSpeedOf(t)));
        for (int i = 0; i < 3; ++i)
        {
            float a = X87.F32(Mul(vel[i], k));
            output[i] = X87.F32(Add(tp[i], a));
        }
    }

    // src/game/turret_fire.cpp outsideAimLimits: Turret_outsideAimLimits (0x00452ff0).
    private static bool OutsideAimLimits(GameBase t, float elevation, float rotation)
    {
        bool outside = false;
        if (rotation > kPiD)
            rotation = X87.F32(Sub(rotation, kTwoPiF));
        if (t.TurretRotationMax != t.TurretRotationMin)
        {
            if ((rotation > 0.0f && rotation > t.TurretRotationMax) || (!(rotation >= 0.0f) && !(rotation >= t.TurretRotationMin)))
                outside = true;
        }
        if (elevation > t.TurretMaxElevation || !(elevation >= t.TurretMinElevation))
            outside = true;
        return outside;
    }

    // ---- shape threads (4.2, 4.6 step 7, 14.6) ----

    // src/game/turret_fire.cpp GameBase::bindTurretShape: 0x00452970. The threads exist when the shape has any sequence,
    // in the order turn, elevate, power / fire.
    private void BindTurretShape()
    {
        if (Anim == null || Anim.Sequences.Count == 0)
            return;
        using var chop = X87.Chop();
        // Turn: sequence `turn` at position 0, rotation limits 0 and rotation 0.
        TurretTurnPos = 0;
        TurretRotationMax = TurretRotationMin = 0;
        TurretRotation = 0;
        // Elevate: the limits are the Euler x (0x005eceb4) of the entry of node `dummy muzzle` with the thread at position
        // 0 and at position 1; a minimum above pi has 2 pi (f32) subtracted.
        TurretElevatePos = 0;
        TurretHasPower = false;
        bool elevateCyclic = CyclicSeq(this, SeqOf(this, "elevate"));
        bool limits = false;
        if (Services() != null)
        {
            GameState g = StateOf(this);
            ShapeInstance? inst = ShapeState.ShapeInstanceOf(g, this);
            int node = inst != null ? inst.ShapeData!.FindNode("dummy muzzle") : -1;
            if (node >= 0)
            {
                float lo = Shape.MatrixEulerX(inst!.Entries[node]);
                TurretThread.ThreadSetPos(ref TurretElevatePos, 1.0f, elevateCyclic);
                inst = ShapeState.ShapeInstanceOf(g, this);
                float hi = Shape.MatrixEulerX(inst!.Entries[node]);
                if (lo > kPiD)
                    lo = X87.F32(Sub(lo, kTwoPiF));
                TurretMinElevation = lo;
                TurretMaxElevation = hi;
                limits = true;
            }
        }
        float plo = 0, phi = 0;
        if (!limits && Anim.NodePitchRange("elevate", "dummy muzzle", ref plo, ref phi))
        {
            // No shape model (synthetic shapes): the keyframe rotations of the node chain.
            TurretThread.ThreadSetPos(ref TurretElevatePos, 1.0f, elevateCyclic);
            if (plo > kPiD)
                plo = X87.F32(Sub(plo, kTwoPiF));
            TurretMinElevation = plo;
            TurretMaxElevation = phi;
            limits = true;
        }
        if (limits)
        {
            TurretElevation = 0;
            if (TurretMaxElevation != TurretMinElevation)
                TurretThread.ThreadSetPos(ref TurretElevatePos, ElevateTarget(this), elevateCyclic);
        }
        // Power / fire thread: only with both sequences. It starts on `power` at position 0.
        int fire = Anim.FindSequence("fire"), power = Anim.FindSequence("power");
        TurretHasPower = fire != -1 && power != -1;
        if (TurretHasPower)
        {
            TurretPowerDuration = Anim.Sequences[power].Duration;
            TurretFireDuration = Anim.Sequences[fire].Duration;
            TurretPowerPos = 0;
            TurretUsePower();
            TurretThread.ThreadSetPos(ref TurretPowerPos, 0.0f, CyclicSeq(this, power));
        }
    }

    // src/game/turret_fire.cpp GameBase::turretUsePower: 0x00452e1c: the thread takes `power` at its current position with
    // priority -1; the elevation drops to 0 and the elevate thread follows.
    public bool TurretUsePower()
    {
        if (!TurretHasPower)
            return false;
        using var chop = X87.Chop();
        float saved = TurretPowerPos;
        TurretPowerPos = X87.F32(Div(0.0f, TurretPowerDuration));
        TurretPowerOnFire = false;
        TurretThread.ThreadSetPos(ref TurretPowerPos, saved, CyclicSeq(this, SeqOf(this, "power")));
        if (TurretElevatePos >= 0)
        {
            TurretElevation = 0;
            if (TurretMaxElevation != TurretMinElevation) // equal limits divide by zero in the original (D11)
                TurretThread.ThreadSetPos(ref TurretElevatePos, ElevateTarget(this), CyclicSeq(this, SeqOf(this, "elevate")));
        }
        return true;
    }

    // src/game/turret_fire.cpp GameBase::turretUseFire: 0x00452dd0: the thread takes `fire` at position 1 with priority 0.
    public void TurretUseFire()
    {
        if (!TurretHasPower)
            return;
        using var chop = X87.Chop();
        TurretPowerPos = X87.F32(Div(0.0f, TurretFireDuration));
        TurretPowerOnFire = true;
        TurretThread.ThreadSetPos(ref TurretPowerPos, 1.0f, CyclicSeq(this, SeqOf(this, "fire")));
    }

    // src/game/turret_fire.cpp GameBase::turretSyncThreads: end of Turret_updateAim (0x00456eca): the elevate and turn
    // threads step to the positions of the new angles, the power / fire thread advances in state 3, and the shape is
    // animated: the pose the muzzle and the aim read until the next update.
    public void TurretSyncThreads()
    {
        if (TurretElevatePos >= 0 && TurretMaxElevation != TurretMinElevation)
            TurretThread.ThreadSetPos(ref TurretElevatePos, ElevateTarget(this), CyclicSeq(this, SeqOf(this, "elevate")));
        if (TurretTurnPos >= 0)
        {
            float target;
            if (Math.Abs((double)X87.F32(Sub(TurretRotationMax, TurretRotationMin))) <= (double)1e-5f)
                target = X87.F32(Mul(kInvTwoPi, TurretRotation));
            else
                target = X87.F32(Div(Sub(TurretRotation, TurretRotationMin), Sub(TurretRotationMax, TurretRotationMin)));
            TurretThread.ThreadSetPos(ref TurretTurnPos, target, CyclicSeq(this, SeqOf(this, "turn")));
        }
        if (TurretHasPower && TurretMode == 3)
        {
            string seq = TurretPowerOnFire ? "fire" : "power";
            TurretThread.ThreadAdvance(ref TurretPowerPos, 0.032f, TurretPowerOnFire ? TurretFireDuration : TurretPowerDuration,
                                       CyclicSeq(this, SeqOf(this, seq)));
        }
    }

    // ---- activation (4.8), turret_sim.cpp ----

    // src/game/turret_sim.cpp GameBase::turretActivate: 0x00455b34 (4.8). The power / fire thread runs `power` forward;
    // at 0.99 (f64) it switches to `fire` at position 1 and the turret is on.
    public void TurretActivate(float dt)
    {
        if (TurretMode != 2 && TurretMode != 3)
            SetMaskBits(0x10000);
        if (!TurretUsePower())
        {
            TurretMode = 3;
            return;
        }
        if (TurretMode == 0)
            TurretThread.ThreadSetPos(ref TurretPowerPos, 0.0f, false);
        if (!(TurretPowerPos < 0.99))
        {
            TurretUseFire();
            TurretMode = 3;
            SetMaskBits(0x10000);
        }
        else
        {
            TurretMode = 2;
            TurretThread.ThreadAdvance(ref TurretPowerPos, dt, TurretPowerDuration, false);
        }
    }

    // src/game/turret_sim.cpp GameBase::turretDeactivate: 0x00455cc4 (4.8): rotate home, then run the power thread
    // backwards.
    public void TurretDeactivate(float dt)
    {
        if (TurretMode != 0 && TurretMode != 1)
            SetMaskBits(0x10000);
        int was = TurretMode;
        if (!TurretUsePower())
        {
            TurretMode = 0;
            return;
        }
        if (was == 3)
            TurretThread.ThreadSetPos(ref TurretPowerPos, 1.0f, false);
        TurretMode = 1;
        if (TurretRotation != 0)
        {
            // Rotate home the short way: `local` is the rotation folded to at most pi; a positive one counts down, a
            // non-positive one up, and the rotation snaps to 0 when the step crosses it. The rotation itself is not
            // wrapped (0x00455f3e).
            SetMaskBits(0x2000);
            float local = TurretRotation;
            while (local > 3.141592653589793)
                local = Store(Sub(local, 6.283185307179586));
            double prod = Mul(TurretData.Speed, dt);
            if (local > 0.0f)
            {
                TurretRotation = Store(Sub(TurretRotation, prod));
                local = Store(Sub(local, prod));
                if (!(local >= 0.0f))
                    TurretRotation = 0;
            }
            else
            {
                TurretRotation = Store(Add(prod, TurretRotation));
                local = Store(Add(prod, local));
                if (local > 0.0f)
                    TurretRotation = 0;
            }
            return;
        }
        SetMaskBits(0x2000);
        TurretThread.ThreadAdvance(ref TurretPowerPos, -dt, TurretPowerDuration, false);
        if (!(TurretPowerPos > 0.0f))
        {
            TurretMode = 0;
            SetMaskBits(0x10000);
        }
    }

    // ---- target selection (4.4) ----

    // src/game/turret_fire.cpp GameBase::turretTestTarget: 0x0045446c.
    public bool TurretTestTarget(GameBase? pl, ref float limit, float range)
    {
        if (Services() == null)
            return false;
        GameState g = StateOf(this);
        Player? p = pl != null ? Player.From(pl) : null;
        if (p == null || p.Team == Team || !g.Players.VisibleToTeam(p, Team) || p.Dead)
            return false;
        if (g.Console != null)
        {
            string fn = Pj.FindMethod(g, this, "verifyTarget");
            if (fn.Length != 0)
            {
                string r = Pj.CallMethod(g, fn, new[] { Numbers.FormatInt(Id()), Numbers.FormatInt(p.Id()) });
                if (r.Length != 0 && (r[0] == 'F' || r[0] == 'f'))
                    return false;
            }
        }
        float[] tp = new float[3], centre = new float[3];
        TurretTargetPoint(g, p, tp);
        TurretBoxCentre(g, this, centre);
        float[] diff = new float[3];
        for (int i = 0; i < 3; ++i)
            diff[i] = X87.F32(Sub(centre[i], tp[i]));
        float d = X87.Lenf(diff);
        if (d < range)
            ++TurretInRange;
        if (!(d < limit))
            return false;
        float[] l = new float[3];
        ToNodeSpace(this, tp, l);
        float elevation = CalcElevation(l[0], l[1], l[2]);
        float rotation = CalcRotation(-l[1], -l[0]);
        if (OutsideAimLimits(this, elevation, rotation))
            return false;
        // Line of sight in container 7 against terrain and interiors (mask 6), from the turret's box centre to the target
        // point: clear, or the player itself is hit.
        var hit = new RayHit();
        if (!Projectile.CastWorld(g, centre, tp, 6u, this, ref hit) || hit.Object == p)
        {
            limit = d;
            return true;
        }
        return false;
    }

    // src/game/turret_fire.cpp GameBase::turretFindTarget: 0x00454ad8.
    public GameBase? TurretFindTarget()
    {
        if (Services() == null)
            return null;
        // projectileType unset (-1): no target (the datablock getter cannot tell, docs/datablocks.md PR2).
        if (!TurretProjectileType(this, out _, out _, out _))
            return null;
        GameState g = StateOf(this);
        SimManager? m = g.Sim?.Server();
        if (m == null)
            return null;
        float range = TurretData.GunRange == -1.0f ? Store(SensorData.Range) : TurretData.GunRange;
        float limit = range;
        // The preferred target (Turret::setTarget) is tried first. The original keeps a raw pointer that dangles when the
        // player is deleted (D11): an id that no longer resolves is no target.
        if (TurretPreferred != 0)
        {
            if (m.FindById(TurretPreferred) is GameBase o && TurretTestTarget(o, ref limit, range))
                return o;
        }
        GameBase? best = null;
        // The player list (+0x340f4, humans and AI, newest first), not the human list.
        foreach (int cid in g.Players.PlayerIds())
        {
            ClientRecord? r = g.Players.Find(cid);
            if (r == null || r.OwnedObject == 0)
                continue;
            if (m.FindById(r.OwnedObject) is GameBase gb && Player.From(gb) != null && TurretTestTarget(gb, ref limit, range))
                best = gb;
        }
        return best;
    }

    // src/game/turret_fire.cpp GameBase::turretSearchStep: 0x00456288 / 0x00457cc0 after the control client, damage state
    // and active tests (4.4).
    public void TurretSearchStep()
    {
        float now = Manager() != null ? Manager()!.Time() : 0.0f;
        TurretInRange = 0;
        GameBase? target = TurretFindTarget();
        if (TurretInRange != 0)
            TurretSleep = X87.F32(Add(now, 3.0f));
        if (target != null)
        {
            if (TurretMode == 3)
                TurretTrack(target, 0.032f);
            else
                TurretActivate(0.032f);
        }
        else if (TurretInRange == 0 && now > TurretSleep && TurretMode != 0)
        {
            TurretDeactivate(0.032f);
        }
    }

    // ---- tracking (4.5) ----

    // One aim step of rotation and elevation toward the node-space point L (turretTrack and turretSustainedFireTick).
    private int TurretStepAngles(ReadOnlySpan<float> l, float dt)
    {
        int aimed = 0;
        float oldRotation = TurretRotation;
        float step = X87.F32(Mul(TurretData.Speed, dt));
        float desired = CalcRotation(-l[1], -l[0]);
        while (desired < 0.0f)
            desired = X87.F32(Add(kTwoPiF, desired));
        while (desired > kTwoPiD)
            desired = X87.F32(Sub(desired, kTwoPiF));
        float delta = X87.F32(Sub(desired, TurretRotation));
        if (delta > kPiD || delta < -kPiD)
            step = -step;
        if (delta >= step || -step >= delta)
        {
            if (delta < 0.0f)
                TurretRotation = X87.F32(Sub(TurretRotation, step));
            else
                TurretRotation = X87.F32(Add(step, TurretRotation));
        }
        else
        {
            TurretRotation = desired;
            ++aimed;
        }
        WrapRotation(ref TurretRotation);
        if (TurretRotation != oldRotation)
            SetMaskBits(0x2000);

        float oldElevation = TurretElevation;
        step = X87.F32(Mul(TurretData.Speed, dt));
        desired = CalcElevation(l[0], l[1], l[2]);
        delta = X87.F32(Sub(desired, TurretElevation));
        if (delta > kPiD || delta < -kPiD)
            step = -step;
        if (delta >= step || -step >= delta)
        {
            if (delta < 0.0f)
                TurretElevation = X87.F32(Sub(TurretElevation, step));
            else
                TurretElevation = X87.F32(Add(step, TurretElevation));
        }
        else
        {
            TurretElevation = desired;
            ++aimed;
        }
        WrapElevation(ref TurretElevation);
        if (TurretElevation != oldElevation)
            SetMaskBits(0x4000);
        return aimed;
    }

    // src/game/turret_fire.cpp GameBase::turretTrack: 0x00454bf8. Both angles step towards the lead point; each that
    // reaches it counts.
    public void TurretTrack(GameBase target, float dt)
    {
        if (Services() == null)
            return;
        GameState g = StateOf(this);
        float[] centre = new float[3], tp = new float[3];
        TurretBoxCentre(g, this, centre);
        TurretTargetPoint(g, target, tp);
        float[] diff = new float[3];
        for (int i = 0; i < 3; ++i)
            diff[i] = X87.F32(Sub(centre[i], tp[i]));
        float d = X87.Lenf(diff);
        float[] lead = new float[3];
        LeadPoint(this, d, target.Velocity, tp, lead);
        float[] l = new float[3];
        ToNodeSpace(this, lead, l);
        int aimed = TurretStepAngles(l, dt);
        float now = Manager() != null ? Manager()!.Time() : 0.0f;
        if (aimed > 1 && TurretNextFire <= now)
            TurretFire(false, target);
    }

    // ---- muzzle ----

    // src/game/turret_fire.cpp GameBase::eyeTransform: vtable +0x110: the eye transform in world space (Turret 0x004566ac,
    // `dummy eye`); the object transform otherwise or without the node.
    public virtual Transform EyeTransform()
    {
        if (ShapeClass() != ShapeKind.Turret)
            return Transform;
        NodeTransform(this, "dummy eye", out Transform eye);
        return eye;
    }

    // src/game/turret_fire.cpp GameBase::turretMuzzleTransform: 0x004565e0; without the node the object transform itself.
    public bool TurretMuzzleTransform(out Transform output) => NodeTransform(this, "dummy muzzle", out output);

    // ---- fire (4.7) ----

    // src/game/turret_fire.cpp playerVisibleToTeam: Player_isVisibleToTeam (0x00412674): a mounted Player is tested
    // through its mount object.
    private static bool PlayerVisibleToTeam(GameState g, Player p, int team)
    {
        if (p.MountId != 0 && g.Sim?.Server()?.FindById(p.MountId) is GameBase gb)
            return g.Players.VisibleToTeam(gb, team);
        return g.Players.VisibleToTeam(p, team);
    }

    // src/game/turret_fire.cpp seekerConeTarget: the best Player (set 719) or Vehicle (set 718) in the cone of a seeker
    // fired by a controlling player: seen by the turret's team, a clear line from the eye to the box centre (mask
    // 0x24e000a6), the largest cosine to the eye direction above the cone limit.
    private static GameBase? SeekerConeTarget(GameState g, GameBase t)
    {
        SimManager? m = g.Sim?.Server();
        if (m == null)
            return null;
        Transform eye = t.EyeTransform();
        float[] fwd = { eye.M[3], eye.M[4], eye.M[5] };
        TurretNormalise(fwd);
        GameBase? best = null;
        float bestDot = -2.0f;
        void Consider(GameBase o)
        {
            float[] c = new float[3];
            TurretBoxCentre(g, o, c);
            var hit = new RayHit();
            if (Projectile.CastWorld(g, eye.P, c, 0x24e000a6u, t, ref hit) && hit.Object != o)
                return;
            float[] d = new float[3];
            for (int i = 0; i < 3; ++i)
                d[i] = X87.F32(Sub(c[i], t.Transform.P[i]));
            TurretNormalise(d);
            float dp = X87.F32(X87.Dot(d, fwd));
            if (0.0f < dp && bestDot < dp)
            {
                bestDot = dp;
                best = o;
            }
        }
        if (m.FindById(719)?.AsSet() is SimSet set719)
            for (int i = 0; i < set719.Members().Count; ++i)
                if (Player.From(set719.Members()[i]) is Player p && PlayerVisibleToTeam(g, p, t.Team))
                    Consider(p);
        if (m.FindById(718)?.AsSet() is SimSet set718)
            for (int i = 0; i < set718.Members().Count; ++i)
                if (Vehicle.From(set718.Members()[i]) is Vehicle v && g.Players.VisibleToTeam(v, t.Team))
                    Consider(v);
        float limit = X87.F32(Shape.CrtCos(X87.F32(Mul(Mul(t.Fov, 0.5f), t.TurretData.TargetableFovRatio))));
        if (limit > 0.996f)
            limit = 0.996f;
        return best != null && bestDot > limit ? best : null;
    }

    // src/game/turret_fire.cpp GameBase::turretFire: 0x004531e4.
    public void TurretFire(bool byPlayer, GameBase? target)
    {
        if (Services() == null)
            return;
        GameState g = StateOf(this);
        SimManager? m = Manager();
        if (m == null)
            return;
        TurretProjectileType(this, out int group, out int index, out DataBlockManager.Block? block);
        float now = m.Time();
        if (block == null)
        {
            // Script method `onFire(turret)`; no stock script defines one.
            if (g.Console != null)
            {
                string fn = Pj.FindMethod(g, this, "onFire");
                if (fn.Length != 0)
                    Pj.CallMethod(g, fn, new[] { Numbers.FormatInt(Id()) });
            }
            return;
        }
        bool sustained = TurretData.Sustained;
        float energyNow = Energy;
        if (!(TurretNextFire <= now) || energyNow < TurretData.MinGunEnergy)
            return;
        TurretMuzzleTransform(out Transform xf);
        Projectile? pr = Projectile.Create(g, group, index);
        if (pr == null)
            return; // D11: the original dereferences the null result
        if (!byPlayer && TurretData.Deflection != 0.0f)
        {
            // Random aim error on the Euler angles x and z: a function-static R250 per path.
            R250 rng = sustained ? sTurretSustainedPath : sTurretNormalPath;
            float[] e = new float[3];
            MissionTransform.MatrixToEuler(xf, e);
            foreach (int axis in new[] { 0, 2 })
            {
                double v = Mul(Sub(Mul((double)rng.Next(), (double)2.3283064e-10f), 0.5f), kTwoPiD);
                v = Mul(v, TurretData.Deflection);
                e[axis] = X87.F32(Add(v, e[axis]));
            }
            MissionTransform.EulerToMatrix(e, ref xf);
            xf.Flags |= 2;
        }
        else if (byPlayer)
        {
            // 0x0044bfb4: the eye ray (1200 m) picks the point the projectile is aimed at, unless it is less than 2 m from
            // the muzzle; the position stays the muzzle's.
            NodeTransform(this, "dummy eye", out Transform eye);
            var hit = new RayHit();
            if (ProjectileFire.ProjectileCastLOS(g, eye, 0xffffffffu, this, 1200.0f, ref hit))
            {
                float[] d = { X87.F32(Sub(hit.Point[0], xf.P[0])), X87.F32(Sub(hit.Point[1], xf.P[1])), X87.F32(Sub(hit.Point[2], xf.P[2])) };
                if (!(X87.Lenf(d) < 2.0f))
                {
                    float[] dir = new float[3], x = new float[3], z = new float[3];
                    float k = X87.F32(Div(1.0, X87.F32(X87.Len(d))));
                    for (int i = 0; i < 3; ++i)
                        dir[i] = X87.F32(Mul(d[i], k));
                    float[] up = { 0, 0, 1 };
                    TurretCross(up, dir, x);
                    if (X87.Len(x) > 0.0)
                    {
                        TurretNormalise(x);
                        TurretCross(x, dir, z);
                        TurretNormalise(z);
                        for (int i = 0; i < 3; ++i)
                        {
                            xf.M[i] = x[i];
                            xf.M[3 + i] = dir[i];
                            xf.M[6 + i] = z[i];
                        }
                        xf.Flags = 3;
                    }
                }
            }
        }
        float[] zero = { 0, 0, 0 };
        pr.Init(xf, zero, Id());
        if (!sustained)
        {
            if (pr.WantsTarget())
            {
                if (target != null)
                {
                    // 0x00453539: a mounted Player is followed through its mount object (+0x1028).
                    GameBase tg = target;
                    if (Player.From(target) is Player tp && tp.MountId != 0 && m.FindById(tp.MountId) is SimObject mo)
                        tg = mo as GameBase ?? target;
                    pr.SetTarget(tg);
                }
                else if (byPlayer)
                {
                    if (SeekerConeTarget(g, this) is GameBase best)
                        pr.SetTarget(best);
                }
            }
            if (TurretData.MaxGunEnergy != 0.0f)
            {
                float used = energyNow > TurretData.MaxGunEnergy ? TurretData.MaxGunEnergy : energyNow;
                float fraction = X87.F32(Div(used, TurretData.MaxGunEnergy));
                pr.SetShotEnergy(used, fraction);
                SetEnergy(X87.F32(Sub(energyNow, used)));
            }
        }
        // The id comes with the registration (a failed one drops the object; the rest still runs).
        int projectileId = pr.RegisterWith(m) ? pr.Id() : 0;
        if (!sustained)
            TurretNextFire = X87.F32(Add(now, TurretData.ReloadDelay));
        if (TurretHasPower)
        {
            // 0x00453fa1: the thread restarts `fire` at position 0.
            TurretUseFire();
            TurretThread.ThreadSetPos(ref TurretPowerPos, 0.0f, false);
        }
        ++TurretFireState;
        SetMaskBits(0x8000);
        if (sustained)
        {
            TurretBarrelState = 2;
            TurretStamp = TurretTickMs;
            TurretBeamProjectile = m.FindById(projectileId) != null ? projectileId : 0;
            TurretBeamTarget = target != null ? target.Id() : 0;
        }
    }

    // src/game/turret_fire.cpp GameBase::turretStopBeam: 0x00454420.
    public void TurretStopBeam()
    {
        TurretStamp = TurretTickMs;
        TurretBarrelState = 0;
        SimManager? m = Manager();
        if (m != null && TurretBeamProjectile != 0)
            Projectile.From(m.FindById(TurretBeamProjectile))?.ShutOff();
        TurretBeamProjectile = 0;
        TurretBeamTarget = 0;
    }

    // src/game/turret_fire.cpp GameBase::turretSustainedFireTick: 0x00457df0: the beam of a sustained turret. Without a
    // control client the target is tested and tracked again; the projectile always gets the eye transform.
    public void TurretSustainedFireTick()
    {
        if (Services() == null)
            return;
        GameState g = StateOf(this);
        SimManager? m = Manager();
        if (ControlClient == 0 && DamageState == kEnabled && IsActiveFlag())
        {
            SimObject? to = m != null && TurretBeamTarget != 0 ? m.FindById(TurretBeamTarget) : null;
            if (to is not GameBase target)
            {
                TurretStopBeam();
                return;
            }
            if (unchecked((uint)TurretData.FiringTimeMS + TurretStamp) <= TurretTickMs)
            {
                TurretStopBeam();
                return;
            }
            float range = TurretData.GunRange == -1.0f ? Store(SensorData.Range) : TurretData.GunRange;
            float limit = range;
            if (!TurretTestTarget(target, ref limit, range))
            {
                TurretStopBeam();
                return;
            }
            const float dt = 0.032f;
            float[] centre = new float[3], tp = new float[3], diff = new float[3], lead = new float[3], l = new float[3];
            TurretBoxCentre(g, this, centre);
            TurretTargetPoint(g, target, tp);
            for (int i = 0; i < 3; ++i)
                diff[i] = X87.F32(Sub(centre[i], tp[i]));
            float d = X87.Lenf(diff);
            LeadPoint(this, d, target.Velocity, tp, lead);
            ToNodeSpace(this, lead, l);
            TurretStepAngles(l, dt);
        }
        if (TurretBeamProjectile != 0 && Projectile.From(m?.FindById(TurretBeamProjectile)) is Projectile pr)
        {
            NodeTransform(this, "dummy eye", out Transform xf); // vtable +0x110, the eye transform
            pr.UpdateTransform(xf);
        }
    }

    // ---- control (turret_control.cpp, docs/static-sim.md 4.6) ----

    // src/game/turret_control.cpp clampTo.
    private static float ClampTo(float v, float lim)
    {
        float lo = X87.F32(-(double)lim);
        float r;
        if (!(v >= lo))
            r = lo;
        else
            r = v;
        if (lim < v)
            r = lim;
        return r;
    }

    // src/game/turret_control.cpp GameBase::turretJump: script method `jump(turret)` when one is defined (none in the base
    // scripts).
    public void TurretJump()
    {
        GameState g = StateOf(this);
        string fn = Pj.FindMethod(g, this, "jump");
        if (fn.Length != 0)
            Pj.CallMethod(g, fn, new[] { Numbers.FormatInt(Id()) });
    }

    // src/game/turret_control.cpp GameBase::turretUpdateAim: 0x00456874. `move` is null for an uncontrolled turret.
    public void TurretUpdateAim(TurretMove? move, float dt)
    {
        using var chop = X87.Chop();
        bool sustained = TurretData.Sustained;
        if (ControlClient != 0 && TurretMode != 3)
            TurretActivate(dt);
        if (TurretMode != 3)
            move = null;
        float oldRotation = TurretRotation, oldElevation = TurretElevation;
        if (move is TurretMove mv)
        {
            // The power / fire thread must be absent, or past the `power` sequence (priority not -1), or at its end: in
            // state 3 the thread runs `fire` (priority 0).
            float lim, scale;
            if (!sustained)
            {
                lim = X87.F32(Mul(TurretData.Speed, TurretData.SpeedModifier));
                scale = X87.F32(Mul(dt, 16.0f));
            }
            else
            {
                lim = X87.F32(Mul(TurretData.Speed, dt));
                scale = X87.F32(Mul(dt, 32.0f));
            }
            float p = ClampTo(mv.Pitch, lim);
            float y = ClampTo(mv.Turn, lim);
            TurretElevation = X87.F32(Add(Mul(p, scale), TurretElevation));
            TurretRotation = X87.F32(Add(Mul(y, scale), TurretRotation));
            WrapElevation(ref TurretElevation);
            if (TurretMaxElevation != TurretMinElevation)
            {
                if (TurretElevation > TurretMaxElevation)
                    TurretElevation = TurretMaxElevation;
                if (TurretElevation < TurretMinElevation)
                    TurretElevation = TurretMinElevation;
            }
            WrapRotation(ref TurretRotation);
            if (TurretRotationMax != TurretRotationMin)
            {
                if (TurretRotation > TurretRotationMax)
                    TurretRotation = TurretRotationMax;
                if (TurretRotation < TurretRotationMin)
                    TurretRotation = TurretRotationMin;
            }
            if (!sustained)
            {
                if (mv.Fire)
                    TurretFire(true, null);
                if (mv.Jump)
                    TurretJump();
            }
            else
            {
                if (mv.Fire && TurretBarrelState == 1)
                    TurretFire(true, null);
                else if (!mv.Fire && TurretBarrelState == 2)
                    TurretStopBeam();
                if (mv.Jump)
                {
                    if (TurretBarrelState == 2)
                        TurretStopBeam();
                    TurretJump();
                }
            }
        }
        TurretSyncThreads();
        if (TurretElevation != oldElevation)
            SetMaskBits(0x4000);
        if (TurretRotation != oldRotation)
            SetMaskBits(0x2000);
    }

    // src/game/turret_control.cpp GameBase::turretApplyMoves: 0x00457080: for each move, the aim update runs with the
    // previously stored move, the credit drops by one (at least 1), then the new move is stored.
    public void TurretApplyMoves(Move[] moves, int count)
    {
        using var chop = X87.Chop();
        for (int i = 0; i < count; ++i)
        {
            TurretUpdateAim(TurretLastMove, 0.032f);
            --TurretCredit;
            if (TurretCredit < 1)
                TurretCredit = 1;
            TurretLastMove.Turn = moves[i].Turn;
            TurretLastMove.Pitch = moves[i].Pitch;
            TurretLastMove.Jump = moves[i].Jump;
            TurretLastMove.Fire = moves[i].Fire;
        }
        if (TurretCredit > 3)
            TurretCredit = 1;
    }

    // src/game/turret_control.cpp GameBase::turretWriteControlState: 0x004572f8.
    public void TurretWriteControlState(BitStream bs)
    {
        bs.WriteF32(TurretRotation);
        bs.WriteF32(TurretElevation);
        bs.WriteInt((uint)TurretFireState, 2);
        bs.WriteFlag(TurretBarrelState == 2);
        bs.WriteInt((uint)TurretMode, 4);
        bs.WriteF32(Energy);
    }

    // src/game/turret_control.cpp GameBase::applyMoves: vtable +0x150.
    public virtual void ApplyMoves(Move[] moves, int count)
    {
        if (mShapeClass == ShapeKind.Turret)
            TurretApplyMoves(moves, count);
    }

    // src/game/turret_control.cpp GameBase::writeControlState: vtable +0x15c.
    public virtual void WriteControlState(BitStream bs, ControlContext ctx)
    {
        if (mShapeClass == ShapeKind.Turret)
            TurretWriteControlState(bs);
    }
}
