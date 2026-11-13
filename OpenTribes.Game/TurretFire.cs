// Port of src/game/turret.hpp, src/game/turret_fire.cpp, src/game/turret_control.cpp and the activation part of
// src/game/turret_sim.cpp: the turret threads, target search, tracking and fire (docs/static-sim.md 4.3 - 4.8) and the
// turret as a control object (4.6, docs/game-stream.md 6.5). The x87 fpatan, fcos and fsin of the original are
// Math.Atan2, Math.Cos and Math.Sin (portable form of src/core/ldouble.hpp); the 80-bit constant 1 / (2 pi) is the
// 53-bit rounded double. The Chop class of the files is X87.Chop or Fpu.ChopScope; world::x87::f32 is X87.F32 and a
// plain float(...) store inside a Chop scope is Fpu.Store.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>The position of a shape thread as the instance keeps it. Every store is an f32 store toward zero.</summary>
/// <remarks>Port of namespace turret in src/game/turret.hpp (docs/static-sim.md 14.6).</remarks>
public static class TurretThread
{
    /// <summary>Normalises a thread position: below 0 or above 1 a cyclic sequence wraps by floor and a one-shot sequence clamps.</summary>
    /// <param name="pos">The position, updated in place.</param>
    /// <param name="cyclic">True for a cyclic sequence.</param>
    /// <remarks>Follows TSSequence_normalise (0x005b8d94).</remarks>
    public static void ThreadNormalise(ref float pos, bool cyclic)
    {
        if (pos < 0.0f)
            pos = cyclic ? X87.F32(Sub(pos, Math.Floor((double)pos))) : 0.0f;
        else if (pos > 1.0f)
            pos = cyclic ? X87.F32(Sub(pos, Math.Floor((double)pos))) : 1.0f;
    }

    /// <summary>Steps a thread position: <c>pos = f32(f32(scale x delta) + pos)</c> with time scale 1.0.</summary>
    /// <param name="pos">The position, updated in place.</param>
    /// <param name="delta">The step.</param>
    /// <param name="cyclic">True for a cyclic sequence.</param>
    /// <remarks>Follows 0x005c01f0 and 0x005c02f0.</remarks>
    public static void ThreadStep(ref float pos, float delta, bool cyclic)
    {
        var d = X87.F32(Mul(1.0f, delta));
        pos = X87.F32(Add(d, pos));
        ThreadNormalise(ref pos, cyclic);
    }

    /// <summary>Moves a thread position toward a target by a step of <c>f32((target - pos) / scale)</c>, not an assignment.</summary>
    /// <param name="pos">The position, updated in place.</param>
    /// <param name="target">The target position.</param>
    /// <param name="cyclic">True for a cyclic sequence.</param>
    /// <remarks>Follows TSThread_setPosition (0x005c0190).</remarks>
    public static void ThreadSetPos(ref float pos, float target, bool cyclic) =>
        ThreadStep(ref pos, X87.F32(Div(Sub(target, pos), 1.0f)), cyclic);

    /// <summary>Advances a thread position by a step of <c>f32(dt / duration of the sequence)</c>.</summary>
    /// <param name="pos">The position, updated in place.</param>
    /// <param name="dt">The step length in seconds.</param>
    /// <param name="duration">The duration of the sequence.</param>
    /// <param name="cyclic">True for a cyclic sequence.</param>
    /// <remarks>Follows TSThread_advance (0x005c01b0).</remarks>
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
    private static readonly double kInvTwoPi = 0xa2f9836e4e4416f4UL * Math.ScaleB(1.0, -66);

    // src/game/turret_fire.cpp turretFire: the function-static R250 of each deflection path.
    private static readonly R250 sTurretNormalPath = new(0x8377), sTurretSustainedPath = new(0x8377);

    /// <summary>Resets the two turret random generators to a fresh process state. For tests, which share one process.</summary>
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
        var r = Div(a, b);
        var neg = r < 0;
        var ar = neg ? -r : r;
        double v;
        if (ar == 1)
            v = Div(kPiD, 4);
        else if (ar < 1)
            v = Math.Atan2(ar, 1.0);
        else
            v = Sub(Math.Atan2(1.0, ar), kHalfPiD);
        // atan(r) = neg ? -v : v for ar <= 1; for ar > 1 the branch above holds fpatan(1, ar) - pi/2 = -(atan(ar)), so the
        // sign flips.
        var t = ar is < 1 or 1 ? (neg ? -v : v) : (neg ? v : -v);
        if (double.IsNegative(b))
            t = double.IsNegative(a) ? Sub(t, kPiD) : Add(t, kPiD);
        return t;
    }

    // src/game/turret_fire.cpp calcRotation: 0x00452fa8: atan2(y, x) + pi/2 as f32.
    private static float CalcRotation(float y, float x) => X87.F32(Add(Atan2Borland(y, x), kHalfPiD));

    // src/game/turret_fire.cpp calcElevation: 0x00452efc: pitch of the point (x, y, z) in the turret's frame.
    private static float CalcElevation(float x, float y, float z)
    {
        var h = Atan2Borland(y, x);
        var t = X87.F32(-h);
        var c = X87.F32(Mul(Math.Cos(t), x));
        var r = X87.F32(Sub(c, Mul(Math.Sin(t), y)));
        var nz = -z;
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
            for (var i = 0; i < 3; ++i)
                c[i] = o.Transform.P[i];
            return false;
        }
        for (var i = 0; i < 3; ++i)
        {
            var s = X87.F32(Add(mx[i], mn[i]));
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
        var s = t.Anim != null ? t.Anim.FindSequence(name) : -1;
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
        var g = StateOf(t);
        var inst = ShapeState.ShapeInstanceOf(g, t);
        var n = inst != null ? inst.ShapeData!.FindNode(node) : -1;
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
        var l = new TMat3
        {
            P = Float3.Of(nodeT)
        };
        float[] euler = [t.TurretElevation, 0.0f, t.TurretRotation];
        Shape.EulerMatrix(euler, ref l);
        l.Flags |= 2u;
        var o = new TMat3
        {
            Flags = t.Transform.Flags,
            M = t.Transform.M,
            P = t.Transform.P
        };
        Shape.TmatMul(l, o, out var w);
        output = new Transform
        {
            Flags = w.Flags,
            M = w.M,
            P = w.P
        };
        return true;
    }

    // src/game/turret_fire.cpp normalise.
    private static void TurretNormalise(Span<float> v)
    {
        var k = X87.F32(Div(1.0, X87.F32(X87.Len(v))));
        for (var i = 0; i < 3; ++i)
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
        Span<float> nodeT = [0, 0, 0];
        MuzzleNode(t, nodeT);
        ReadOnlySpan<float> m = t.Transform.M;
        Span<float> np = stackalloc float[3];
        X87.MulPoint(nodeT, m, t.Transform.P, np);
        float[] inv = [m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8]];
        float[] neg = [-np[0], -np[1], -np[2]];
        var tp = new float[3];
        X87.MulVector(neg, inv, tp);
        X87.MulPoint(point, inv, tp, l);
    }

    // src/game/turret_fire.cpp projectileType: projectile class and index of the TurretData (+0x120, +0x124).
    private static bool TurretProjectileType(GameBase t, out int group, out int index, out DataBlockManager.Block? block)
    {
        var g = StateOf(t);
        group = index = -1;
        block = null;
        if (g.Sim == null || t.DataBlockName == null)
            return false;
        var b = g.Sim.DataBlocks().Find(t.DataBlockName);
        var f = b != null ? Pj.Field(b, "projectileType") : null;
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
        var speed = 50.0f;
        if (TurretProjectileType(t, out var group, out _, out var b))
        {
            speed = group switch
            {
                Projectile.kRocketGroup => Pj.Real(b!, "terminalVelocity", 0.0f),
                Projectile.kBulletGroup or Projectile.kGrenadeGroup or Projectile.kSeekerGroup => Pj.Real(b!, "muzzleVelocity", 0.0f),
                _ => 1.0e7f,// lasers, lightning, repair: the constant at 0x00462920
            };
        }
        t.TurretProjectileSpeed = speed;
        return speed;
    }

    // src/game/turret_fire.cpp leadPoint: Turret_leadPoint (0x00453090).
    private static void LeadPoint(GameBase t, float d, ReadOnlySpan<float> vel, ReadOnlySpan<float> tp, Span<float> output)
    {
        var k = X87.F32(Div(d, TurretProjectileSpeedOf(t)));
        for (var i = 0; i < 3; ++i)
        {
            var a = X87.F32(Mul(vel[i], k));
            output[i] = X87.F32(Add(tp[i], a));
        }
    }

    // src/game/turret_fire.cpp outsideAimLimits: Turret_outsideAimLimits (0x00452ff0).
    private static bool OutsideAimLimits(GameBase t, float elevation, float rotation)
    {
        var outside = false;
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
        var elevateCyclic = CyclicSeq(this, SeqOf(this, "elevate"));
        var limits = false;
        if (Services() != null)
        {
            var g = StateOf(this);
            var inst = ShapeState.ShapeInstanceOf(g, this);
            var node = inst != null ? inst.ShapeData!.FindNode("dummy muzzle") : -1;
            if (node >= 0)
            {
                var lo = Shape.MatrixEulerX(inst!.Entries[node]);
                TurretThread.ThreadSetPos(ref TurretElevatePos, 1.0f, elevateCyclic);
                inst = ShapeState.ShapeInstanceOf(g, this);
                var hi = Shape.MatrixEulerX(inst!.Entries[node]);
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

    /// <summary>Starts the power thread: it takes <c>power</c> at its current position with priority -1.</summary>
    /// <returns>True when the thread started.</returns>
    /// <remarks>Follows 0x00452e1c. The elevation drops to 0 and the elevate thread follows.</remarks>
    public bool TurretUsePower()
    {
        if (!TurretHasPower)
            return false;
        using var chop = X87.Chop();
        var saved = TurretPowerPos;
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

    /// <summary>Starts the fire thread: it takes <c>fire</c> at position 1 with priority 0.</summary>
    /// <remarks>Follows 0x00452dd0.</remarks>
    public void TurretUseFire()
    {
        if (!TurretHasPower)
            return;
        using var chop = X87.Chop();
        TurretPowerPos = X87.F32(Div(0.0f, TurretFireDuration));
        TurretPowerOnFire = true;
        TurretThread.ThreadSetPos(ref TurretPowerPos, 1.0f, CyclicSeq(this, SeqOf(this, "fire")));
    }

    /// <summary>Brings the turret threads in step with the aim angles.</summary>
    /// <remarks>Follows the end of Turret_updateAim (0x00456eca). The elevate and turn threads step to the positions of the new angles, the power and fire thread advances in state 3, and the shape is animated: the pose the muzzle and the aim read until the next update.</remarks>
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
            var seq = TurretPowerOnFire ? "fire" : "power";
            TurretThread.ThreadAdvance(ref TurretPowerPos, 0.032f, TurretPowerOnFire ? TurretFireDuration : TurretPowerDuration,
                                       CyclicSeq(this, SeqOf(this, seq)));
        }
    }

    // ---- activation (4.8), turret_sim.cpp ----

    /// <summary>Runs one tick of the power-up sequence.</summary>
    /// <param name="dt">The step length in seconds.</param>
    /// <remarks>Follows 0x00455b34 (docs/static-sim.md 4.8). The power and fire thread runs <c>power</c> forward. At 0.99 (f64) it switches to <c>fire</c> at position 1 and the turret is on.</remarks>
    public void TurretActivate(float dt)
    {
        if (TurretMode is not 2 and not 3)
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

    /// <summary>Runs one tick of the power-down sequence: rotates home, then runs the power thread backwards.</summary>
    /// <param name="dt">The step length in seconds.</param>
    /// <remarks>Follows 0x00455cc4 (docs/static-sim.md 4.8).</remarks>
    public void TurretDeactivate(float dt)
    {
        if (TurretMode is not 0 and not 1)
            SetMaskBits(0x10000);
        var was = TurretMode;
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
            var local = TurretRotation;
            while (local > 3.141592653589793)
                local = Store(Sub(local, 6.283185307179586));
            var prod = Mul(TurretData.Speed, dt);
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

    /// <summary>Tests a player as a target.</summary>
    /// <param name="pl">The player, or null.</param>
    /// <param name="limit">The best value so far, updated when the player is closer.</param>
    /// <param name="range">The turret range.</param>
    /// <returns>True when the player is a valid target.</returns>
    /// <remarks>Follows 0x0045446c.</remarks>
    public bool TurretTestTarget(GameBase? pl, ref float limit, float range)
    {
        if (Services() == null)
            return false;
        var g = StateOf(this);
        var p = pl != null ? Player.From(pl) : null;
        if (p == null || p.Team == Team || !PlayerManager.VisibleToTeam(p, Team) || p.Dead)
            return false;
        if (g.Console != null)
        {
            var fn = Pj.FindMethod(g, this, "verifyTarget");
            if (fn.Length != 0)
            {
                var r = Pj.CallMethod(g, fn, new[] { Numbers.FormatInt(Id()), Numbers.FormatInt(p.Id()) });
                if (r.Length != 0 && (r[0] == 'F' || r[0] == 'f'))
                    return false;
            }
        }
        float[] tp = new float[3], centre = new float[3];
        TurretTargetPoint(g, p, tp);
        TurretBoxCentre(g, this, centre);
        var diff = new float[3];
        for (var i = 0; i < 3; ++i)
            diff[i] = X87.F32(Sub(centre[i], tp[i]));
        var d = X87.Lenf(diff);
        if (d < range)
            ++TurretInRange;
        if (!(d < limit))
            return false;
        var l = new float[3];
        ToNodeSpace(this, tp, l);
        var elevation = CalcElevation(l[0], l[1], l[2]);
        var rotation = CalcRotation(-l[1], -l[0]);
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

    /// <summary>Finds the best target in range.</summary>
    /// <returns>The target, or null.</returns>
    /// <remarks>Follows 0x00454ad8.</remarks>
    public GameBase? TurretFindTarget()
    {
        if (Services() == null)
            return null;
        // projectileType unset (-1): no target (the datablock getter cannot tell, docs/datablocks.md PR2).
        if (!TurretProjectileType(this, out _, out _, out _))
            return null;
        var g = StateOf(this);
        var m = g.Sim?.Server();
        if (m == null)
            return null;
        var range = TurretData.GunRange == -1.0f ? Store(SensorData.Range) : TurretData.GunRange;
        var limit = range;
        // The preferred target (Turret::setTarget) is tried first. The original keeps a raw pointer that dangles when the
        // player is deleted (D11): an id that no longer resolves is no target.
        if (TurretPreferred != 0)
        {
            if (m.FindById(TurretPreferred) is GameBase o && TurretTestTarget(o, ref limit, range))
                return o;
        }
        GameBase? best = null;
        // The player list (+0x340f4, humans and AI, newest first), not the human list.
        foreach (var cid in g.Players.PlayerIds())
        {
            var r = g.Players.Find(cid);
            if (r == null || r.OwnedObject == 0)
                continue;
            if (m.FindById(r.OwnedObject) is GameBase gb && Player.From(gb) != null && TurretTestTarget(gb, ref limit, range))
                best = gb;
        }
        return best;
    }

    /// <summary>Runs the target search of one tick.</summary>
    /// <remarks>Follows 0x00456288 and 0x00457cc0 after the control client, damage state and active tests (docs/static-sim.md 4.4).</remarks>
    public void TurretSearchStep()
    {
        var now = Manager() != null ? Manager()!.Time() : 0.0f;
        TurretInRange = 0;
        var target = TurretFindTarget();
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
        var aimed = 0;
        var oldRotation = TurretRotation;
        var step = X87.F32(Mul(TurretData.Speed, dt));
        var desired = CalcRotation(-l[1], -l[0]);
        while (desired < 0.0f)
            desired = X87.F32(Add(kTwoPiF, desired));
        while (desired > kTwoPiD)
            desired = X87.F32(Sub(desired, kTwoPiF));
        var delta = X87.F32(Sub(desired, TurretRotation));
        if (delta is > (float)kPiD or < (float)-kPiD)
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

        var oldElevation = TurretElevation;
        step = X87.F32(Mul(TurretData.Speed, dt));
        desired = CalcElevation(l[0], l[1], l[2]);
        delta = X87.F32(Sub(desired, TurretElevation));
        if (delta is > (float)kPiD or < (float)-kPiD)
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

    /// <summary>Steps both aim angles toward the lead point of a target. Each angle that reaches it counts.</summary>
    /// <param name="target">The target.</param>
    /// <param name="dt">The step length in seconds.</param>
    /// <remarks>Follows 0x00454bf8.</remarks>
    public void TurretTrack(GameBase target, float dt)
    {
        if (Services() == null)
            return;
        var g = StateOf(this);
        float[] centre = new float[3], tp = new float[3];
        TurretBoxCentre(g, this, centre);
        TurretTargetPoint(g, target, tp);
        var diff = new float[3];
        for (var i = 0; i < 3; ++i)
            diff[i] = X87.F32(Sub(centre[i], tp[i]));
        var d = X87.Lenf(diff);
        var lead = new float[3];
        LeadPoint(this, d, target.Velocity, tp, lead);
        var l = new float[3];
        ToNodeSpace(this, lead, l);
        var aimed = TurretStepAngles(l, dt);
        var now = Manager() != null ? Manager()!.Time() : 0.0f;
        if (aimed > 1 && TurretNextFire <= now)
            TurretFire(false, target);
    }

    // ---- muzzle ----

    /// <summary>Returns the eye transform in world space.</summary>
    /// <returns>The Turret <c>dummy eye</c> node transform, or the object transform without the node or for other classes.</returns>
    /// <remarks>Follows vtable +0x110 (Turret 0x004566ac).</remarks>
    public virtual Transform EyeTransform()
    {
        if (ShapeClass() != ShapeKind.Turret)
            return Transform;
        NodeTransform(this, "dummy eye", out var eye);
        return eye;
    }

    /// <summary>Computes the muzzle transform of the turret.</summary>
    /// <param name="output">Receives the transform. Without the node it is the object transform itself.</param>
    /// <returns>True when the <c>dummy muzzle</c> node exists.</returns>
    /// <remarks>Follows 0x004565e0.</remarks>
    public bool TurretMuzzleTransform(out Transform output) => NodeTransform(this, "dummy muzzle", out output);

    // ---- fire (4.7) ----

    // src/game/turret_fire.cpp playerVisibleToTeam: Player_isVisibleToTeam (0x00412674): a mounted Player is tested
    // through its mount object.
    private static bool PlayerVisibleToTeam(GameState g, Player p, int team)
    {
        if (p.MountId != 0 && g.Sim?.Server()?.FindById(p.MountId) is GameBase gb)
            return PlayerManager.VisibleToTeam(gb, team);
        return PlayerManager.VisibleToTeam(p, team);
    }

    // src/game/turret_fire.cpp seekerConeTarget: the best Player (set 719) or Vehicle (set 718) in the cone of a seeker
    // fired by a controlling player: seen by the turret's team, a clear line from the eye to the box centre (mask
    // 0x24e000a6), the largest cosine to the eye direction above the cone limit.
    private static GameBase? SeekerConeTarget(GameState g, GameBase t)
    {
        var m = g.Sim?.Server();
        if (m == null)
            return null;
        var eye = t.EyeTransform();
        float[] fwd = [eye.M[3], eye.M[4], eye.M[5]];
        TurretNormalise(fwd);
        GameBase? best = null;
        var bestDot = -2.0f;
        void Consider(GameBase o)
        {
            var c = new float[3];
            TurretBoxCentre(g, o, c);
            var hit = new RayHit();
            if (Projectile.CastWorld(g, eye.P, c, 0x24e000a6u, t, ref hit) && hit.Object != o)
                return;
            var d = new float[3];
            for (var i = 0; i < 3; ++i)
                d[i] = X87.F32(Sub(c[i], t.Transform.P[i]));
            TurretNormalise(d);
            var dp = X87.F32(X87.Dot(d, fwd));
            if (0.0f < dp && bestDot < dp)
            {
                bestDot = dp;
                best = o;
            }
        }
        if (m.FindById(719)?.AsSet() is SimSet set719)
            for (var i = 0; i < set719.Members().Count; ++i)
                if (Player.From(set719.Members()[i]) is Player p && PlayerVisibleToTeam(g, p, t.Team))
                    Consider(p);
        if (m.FindById(718)?.AsSet() is SimSet set718)
            for (var i = 0; i < set718.Members().Count; ++i)
                if (Vehicle.From(set718.Members()[i]) is Vehicle v && PlayerManager.VisibleToTeam(v, t.Team))
                    Consider(v);
        var limit = X87.F32(Shape.CrtCos(X87.F32(Mul(Mul(t.Fov, 0.5f), t.TurretData.TargetableFovRatio))));
        if (limit > 0.996f)
            limit = 0.996f;
        return best != null && bestDot > limit ? best : null;
    }

    /// <summary>Fires the turret.</summary>
    /// <param name="byPlayer">True when a player control client fires it.</param>
    /// <param name="target">The target, or null.</param>
    /// <remarks>Follows 0x004531e4.</remarks>
    public void TurretFire(bool byPlayer, GameBase? target)
    {
        if (Services() == null)
            return;
        var g = StateOf(this);
        var m = Manager();
        if (m == null)
            return;
        TurretProjectileType(this, out var group, out var index, out var block);
        var now = m.Time();
        if (block == null)
        {
            // Script method `onFire(turret)`; no stock script defines one.
            if (g.Console != null)
            {
                var fn = Pj.FindMethod(g, this, "onFire");
                if (fn.Length != 0)
                    Pj.CallMethod(g, fn, new[] { Numbers.FormatInt(Id()) });
            }
            return;
        }
        var sustained = TurretData.Sustained;
        var energyNow = Energy;
        if (!(TurretNextFire <= now) || energyNow < TurretData.MinGunEnergy)
            return;
        TurretMuzzleTransform(out var xf);
        Projectile? pr = Projectile.Create(g, group, index);
        if (pr == null)
            return; // D11: the original dereferences the null result
        if (!byPlayer && TurretData.Deflection != 0.0f)
        {
            // Random aim error on the Euler angles x and z: a function-static R250 per path.
            var rng = sustained ? sTurretSustainedPath : sTurretNormalPath;
            var e = new float[3];
            MissionTransform.MatrixToEuler(xf, e);
            foreach (var axis in new[] { 0, 2 })
            {
                var v = Mul(Sub(Mul(rng.Next(), (double)2.3283064e-10f), 0.5f), kTwoPiD);
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
            NodeTransform(this, "dummy eye", out var eye);
            var hit = new RayHit();
            if (ProjectileFire.ProjectileCastLOS(g, eye, 0xffffffffu, this, 1200.0f, ref hit))
            {
                float[] d = [X87.F32(Sub(hit.Point[0], xf.P[0])), X87.F32(Sub(hit.Point[1], xf.P[1])), X87.F32(Sub(hit.Point[2], xf.P[2]))];
                if (!(X87.Lenf(d) < 2.0f))
                {
                    float[] dir = new float[3], x = new float[3], z = new float[3];
                    var k = X87.F32(Div(1.0, X87.F32(X87.Len(d))));
                    for (var i = 0; i < 3; ++i)
                        dir[i] = X87.F32(Mul(d[i], k));
                    float[] up = [0, 0, 1];
                    TurretCross(up, dir, x);
                    if (X87.Len(x) > 0.0)
                    {
                        TurretNormalise(x);
                        TurretCross(x, dir, z);
                        TurretNormalise(z);
                        for (var i = 0; i < 3; ++i)
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
        float[] zero = [0, 0, 0];
        pr.Init(xf, zero, Id());
        if (!sustained)
        {
            if (pr.WantsTarget())
            {
                if (target != null)
                {
                    // 0x00453539: a mounted Player is followed through its mount object (+0x1028).
                    var tg = target;
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
                var used = energyNow > TurretData.MaxGunEnergy ? TurretData.MaxGunEnergy : energyNow;
                var fraction = X87.F32(Div(used, TurretData.MaxGunEnergy));
                pr.SetShotEnergy(used, fraction);
                SetEnergy(X87.F32(Sub(energyNow, used)));
            }
        }
        // The id comes with the registration (a failed one drops the object; the rest still runs).
        var projectileId = pr.RegisterWith(m) ? pr.Id() : 0;
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

    /// <summary>Stops the beam of a sustained turret.</summary>
    /// <remarks>Follows 0x00454420.</remarks>
    public void TurretStopBeam()
    {
        TurretStamp = TurretTickMs;
        TurretBarrelState = 0;
        var m = Manager();
        if (m != null && TurretBeamProjectile != 0)
            Projectile.From(m.FindById(TurretBeamProjectile))?.ShutOff();
        TurretBeamProjectile = 0;
        TurretBeamTarget = 0;
    }

    /// <summary>Runs the sustained fire tick: the beam of a sustained turret.</summary>
    /// <remarks>Follows 0x00457df0. Without a control client the target is tested and tracked again. The projectile always gets the eye transform.</remarks>
    public void TurretSustainedFireTick()
    {
        if (Services() == null)
            return;
        var g = StateOf(this);
        var m = Manager();
        if (ControlClient == 0 && DamageState == kEnabled && IsActiveFlag())
        {
            var to = m != null && TurretBeamTarget != 0 ? m.FindById(TurretBeamTarget) : null;
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
            var range = TurretData.GunRange == -1.0f ? Store(SensorData.Range) : TurretData.GunRange;
            var limit = range;
            if (!TurretTestTarget(target, ref limit, range))
            {
                TurretStopBeam();
                return;
            }
            const float dt = 0.032f;
            float[] centre = new float[3], tp = new float[3], diff = new float[3], lead = new float[3], l = new float[3];
            TurretBoxCentre(g, this, centre);
            TurretTargetPoint(g, target, tp);
            for (var i = 0; i < 3; ++i)
                diff[i] = X87.F32(Sub(centre[i], tp[i]));
            var d = X87.Lenf(diff);
            LeadPoint(this, d, target.Velocity, tp, lead);
            ToNodeSpace(this, lead, l);
            TurretStepAngles(l, dt);
        }
        if (TurretBeamProjectile != 0 && Projectile.From(m?.FindById(TurretBeamProjectile)) is Projectile pr)
        {
            NodeTransform(this, "dummy eye", out var xf); // vtable +0x110, the eye transform
            pr.UpdateTransform(xf);
        }
    }

    // ---- control (turret_control.cpp, docs/static-sim.md 4.6) ----

    // src/game/turret_control.cpp clampTo.
    private static float ClampTo(float v, float lim)
    {
        var lo = X87.F32(-(double)lim);
        float r;
        if (!(v >= lo))
            r = lo;
        else
            r = v;
        if (lim < v)
            r = lim;
        return r;
    }

    /// <summary>Calls the script method <c>jump(turret)</c> when one is defined. The base scripts define none.</summary>
    public void TurretJump()
    {
        var g = StateOf(this);
        var fn = Pj.FindMethod(g, this, "jump");
        if (fn.Length != 0)
            Pj.CallMethod(g, fn, new[] { Numbers.FormatInt(Id()) });
    }

    /// <summary>Updates the aim of the turret for one move.</summary>
    /// <param name="move">The move, or null for an uncontrolled turret.</param>
    /// <param name="dt">The step length in seconds.</param>
    /// <remarks>Follows 0x00456874.</remarks>
    public void TurretUpdateAim(TurretMove? move, float dt)
    {
        using var chop = X87.Chop();
        var sustained = TurretData.Sustained;
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
            var p = ClampTo(mv.Pitch, lim);
            var y = ClampTo(mv.Turn, lim);
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

    /// <summary>Applies the moves of a control packet.</summary>
    /// <param name="moves">The moves.</param>
    /// <param name="count">The number of moves.</param>
    /// <remarks>Follows 0x00457080: for each move the aim update runs with the previously stored move, the credit drops by one (at least 1), then the new move is stored.</remarks>
    public void TurretApplyMoves(Move[] moves, int count)
    {
        using var chop = X87.Chop();
        for (var i = 0; i < count; ++i)
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

    /// <summary>Writes the turret control state.</summary>
    /// <param name="bs">The stream to write.</param>
    /// <remarks>Follows 0x004572f8.</remarks>
    public void TurretWriteControlState(BitStream bs)
    {
        bs.WriteF32(TurretRotation);
        bs.WriteF32(TurretElevation);
        bs.WriteInt((uint)TurretFireState, 2);
        bs.WriteFlag(TurretBarrelState == 2);
        bs.WriteInt((uint)TurretMode, 4);
        bs.WriteF32(Energy);
    }

    /// <summary>Applies the moves of a control packet to the object.</summary>
    /// <param name="moves">The moves.</param>
    /// <param name="count">The number of moves.</param>
    /// <remarks>Follows vtable +0x150.</remarks>
    public virtual void ApplyMoves(Move[] moves, int count)
    {
        if (mShapeClass == ShapeKind.Turret)
            TurretApplyMoves(moves, count);
    }

    /// <summary>Writes the control state of the object.</summary>
    /// <param name="bs">The stream to write.</param>
    /// <param name="ctx">The connection context.</param>
    /// <remarks>Follows vtable +0x15c.</remarks>
    public virtual void WriteControlState(BitStream bs, ControlContext ctx)
    {
        if (mShapeClass == ShapeKind.Turret)
            TurretWriteControlState(bs);
    }
}
