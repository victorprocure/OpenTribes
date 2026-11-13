// Port of src/game/projectile_seeker.cpp: the SeekingMissile (docs/projectile-sim.md 2.6, 5.2) and its steering helpers
// (pj::seekerRotate, seekerLead, seekerSteer). The x87 fsincos of the original is Math.Sin and Math.Cos (portable form of
// src/core/ldouble.hpp).
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>The SeekingMissile steering helpers (pj::seekerRotate, seekerLead, seekerSteer).</summary>
/// <remarks>See docs/projectile-sim.md 2.6 and 5.2. The x87 fsincos of the original is Math.Sin and Math.Cos.</remarks>
public static class Seeker
{
    /// <summary>Turns the heading toward the aim by an angle, scaled to the muzzle velocity.</summary>
    /// <param name="vn">The unit heading.</param>
    /// <param name="an">The unit aim.</param>
    /// <param name="ang">The turn angle.</param>
    /// <param name="muzzle">The muzzle velocity.</param>
    /// <param name="output">Receives the new velocity.</param>
    /// <remarks>Follows the SeekingMissile steering rotation (0x0046680f, docs/projectile-sim.md 2.6).</remarks>
    public static void SeekerRotate(ReadOnlySpan<float> vn, ReadOnlySpan<float> an, float ang, float muzzle, Span<float> output)
    {
        // axis = an x vn (m_cross 005ece6f: products and difference in extended precision, one f32 store), normalised; the
        // axis-angle matrix of 005ee0ac (quaternion (axis sin(ang/2), cos(ang/2)) with fsincos 005ee4b1, matrix
        // 005ee124); vn times the matrix (m_mul_vector 005ed124) times the muzzle velocity.
        float[] ax =
        [
            X87.F32(Sub(Mul(an[1], vn[2]), Mul(an[2], vn[1]))), X87.F32(Sub(Mul(an[2], vn[0]), Mul(an[0], vn[2]))),
            X87.F32(Sub(Mul(an[0], vn[1]), Mul(an[1], vn[0]))),
        ];
        {
            var k = X87.F32(Div(1.0, X87.F32(X87.Len(ax))));
            for (var i = 0; i < 3; ++i)
                ax[i] = X87.F32(Mul(ax[i], k));
        }
        var half = X87.F32(Mul(ang, 0.5f));
        double sn = Math.Sin(half), cs = Math.Cos(half);
        float sine = X87.F32(sn), cosine = X87.F32(cs);
        float[] q = [X87.F32(Mul(ax[0], sine)), X87.F32(Mul(ax[1], sine)), X87.F32(Mul(ax[2], sine)), cosine];
        float[] m = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        const float kTiny = 1e-12f; // 0x2b8cbccc
        if (!(Mul(q[0], q[0]) < kTiny && Mul(q[1], q[1]) < kTiny && Mul(q[2], q[2]) < kTiny))
        {
            float x2 = X87.F32(Mul(q[0], 2.0f)), y2 = X87.F32(Mul(q[1], 2.0f)), z2 = X87.F32(Mul(q[2], 2.0f));
            float wx = X87.F32(Mul(q[3], x2)), wy = X87.F32(Mul(q[3], y2)), wz = X87.F32(Mul(q[3], z2));
            float xx = X87.F32(Mul(q[0], x2)), xy = X87.F32(Mul(q[0], y2)), xz = X87.F32(Mul(q[0], z2));
            float yy = X87.F32(Mul(q[1], y2)), yz = X87.F32(Mul(q[1], z2)), zz = X87.F32(Mul(q[2], z2));
            m[0] = X87.F32(Sub(1.0, Add(yy, zz)));
            m[1] = X87.F32(Sub(xy, wz));
            m[2] = X87.F32(Add(xz, wy));
            m[3] = X87.F32(Add(xy, wz));
            m[4] = X87.F32(Sub(1.0, Add(xx, zz)));
            m[5] = X87.F32(Sub(yz, wx));
            m[6] = X87.F32(Sub(xz, wy));
            m[7] = X87.F32(Add(yz, wx));
            m[8] = X87.F32(Sub(1.0, Add(xx, yy)));
        }
        var r = new float[3];
        X87.MulVector(vn, m, r);
        for (var i = 0; i < 3; ++i)
            output[i] = X87.F32(Mul(muzzle, r[i]));
    }

    /// <summary>Computes the missile aim without obstacles.</summary>
    /// <param name="p">The missile position.</param>
    /// <param name="tp">The target position.</param>
    /// <param name="targetVelocity">The target velocity, or null for a missile without target.</param>
    /// <param name="muzzle">The muzzle velocity.</param>
    /// <param name="d">Receives the vector from the missile to the target.</param>
    /// <param name="lead">Receives the lead point.</param>
    /// <remarks>Follows 0x004671b8 and 0x00467252: <c>d = tp - p</c>, <c>t = f32(f32(|d|) / muzzleVelocity)</c>, <c>lead = d + f32(targetVelocity x t)</c>.</remarks>
    public static void SeekerLead(ReadOnlySpan<float> p, ReadOnlySpan<float> tp, float[]? targetVelocity, float muzzle, Span<float> d,
                                  Span<float> lead)
    {
        for (var i = 0; i < 3; ++i)
            d[i] = X87.F32(Sub(tp[i], p[i]));
        var t = X87.F32(Div(X87.F32(X87.Len(d)), muzzle));
        for (var i = 0; i < 3; ++i)
            lead[i] = X87.F32(Add(d[i], targetVelocity != null ? X87.F32(Mul(targetVelocity[i], t)) : 0.0f));
    }

    // The 80-bit constant 0.001 at 0x004669a4, rounded to 53 bits.
    private const double Milli = 0.001;

    /// <summary>Steers the missile velocity toward the aim.</summary>
    /// <param name="v">The current velocity.</param>
    /// <param name="aim">The aim direction.</param>
    /// <param name="ms">The step length in milliseconds.</param>
    /// <param name="turnRate">The turn rate.</param>
    /// <param name="muzzle">The muzzle velocity.</param>
    /// <param name="tracking">True while the missile tracks a target.</param>
    /// <param name="output">Receives the new velocity.</param>
    /// <returns>0 when the velocity is kept (aim behind, or not tracking), 1 when it snaps to the aim direction, 2 when it is rotated toward it by the turn rate.</returns>
    /// <remarks>Follows SeekingMissile_steer (0x00466664).</remarks>
    public static int SeekerSteer(ReadOnlySpan<float> v, ReadOnlySpan<float> aim, int ms, float turnRate, float muzzle, bool tracking,
                                  Span<float> output)
    {
        float[] vn = [v[0], v[1], v[2]], an = [aim[0], aim[1], aim[2]];
        foreach (var n in new[] { vn, an })
        {
            var k = X87.F32(Div(1.0, X87.F32(X87.Len(n))));
            for (var i = 0; i < 3; ++i)
                n[i] = X87.F32(Mul(n[i], k));
        }
        output[0] = v[0];
        output[1] = v[1];
        output[2] = v[2];
        var c = X87.F32(X87.Dot(an, vn));
        if (c < 0.0f || !tracking)
            return 0;
        var ang = X87.F32(Mul(Mul(ms, Milli), turnRate));
        var ca = X87.F32(Shape.CrtCos(ang)); // crt_cos 0x00606890 on the double
        if (c >= ca)
        {
            var l = X87.F32(X87.Len(v));
            for (var i = 0; i < 3; ++i)
                output[i] = X87.F32(Mul(an[i], l));
            return 1;
        }
        SeekerRotate(vn, an, ang, muzzle, output);
        return 2;
    }
}

// src/game/projectile_seeker.cpp class SeekingMissile.
internal sealed class SeekingMissile : Projectile
{
    private int _target;          // +0x474
    private bool _tracking = true; // +0x484
    private bool _lost;           // +0x485
    private readonly bool _expired; // +0x486
    private float _turnRate;      // +0x488
    private uint _lastPct;        // +0x4ac
    private float _pct = 1.0f;    // +0x4b0
    private float _seekRadius = 15, _freeRadius = 50, _proximity = 2;

    public SeekingMissile(GameState g) : base(g, "SeekingMissile", SeekerGroup) { }

    public override bool WantsTarget() => true;                         // vtable +0x1c8
    public override float TargetRange() => 1000.0f;                     // vtable +0x1cc
    public override void SetTarget(GameBase? t) => _target = t != null ? t.Id() : 0; // vtable +0x1d0
    public override int SeekerTarget() => _target;

    protected override void ReadData(DataBlockManager.Block b)
    {
        _seekRadius = Pj.Real(b, "seekingTurningRadius", 15.0f);
        _freeRadius = Pj.Real(b, "nonSeekingTurningRadius", 50.0f);
        _proximity = Pj.Real(b, "proximityDist", 2.0f);
    }

    protected override void ClassOnAdd()
    {
        Last = Game.Sim!.TickMs();
        RefreshPercentage();
    }

    // Vtable +0x188 (liveTime passed): the missile starts to fall.
    protected override void OnLiveTimePassed() => Gravity = true;

    public override void ServerTick(uint now)
    {
        using var chop = X87.Chop();
        if (Dying)
        {
            if (now >= DeleteTime && Manager() != null)
                Manager()!.DeleteObject(this);
            return;
        }
        if (now > unchecked(_lastPct + 150))
            RefreshPercentage();
        var ms = unchecked((int)(now - Last));
        if (ms == 0)
            return;
        if (!_lost)
            Steer(ms);
        var tp = new float[3];
        TargetPoint(tp);
        var d = new float[3];
        for (var i = 0; i < 3; ++i)
            d[i] = X87.F32(Sub(tp[i], Transform.P[i]));
        var hit = new RayHit();
        bool hitNow;
        if (X87.F32(X87.Len(d)) > _proximity)
            hitNow = Advance(now, true, ref hit);
        else
            hitNow = true;
        if (!hitNow && now > LiveEnd)
            hitNow = true;
        if (hitNow)
        {
            float[] pos = [Transform.P[0], Transform.P[1], Transform.P[2]];
            RadiusDamageAt(pos);
            ExplodeAtPosition(pos);
            Dying = true;
            DeleteTime = unchecked(now + 500);
        }
        SetMaskBits(0x03);
    }

    public override void PackInitial(BitStream bs, GhostContext ctx)
    {
        Pj.WriteVec(bs, Transform.P);
        Pj.WriteVec(bs, Velocity);
    }

    public override void PackExplode(BitStream bs)
    {
        if (!bs.WriteFlag(_expired))
        {
            Pj.WriteVec(bs, ExpPos);
            Pj.WriteNormal(bs, ExpNormal, 5);
            Pj.WriteIndex(bs, ExpIndex, Pj.ExplosionCount(Game));
        }
    }

    public override void PackUpdate(BitStream bs, uint mask, GhostContext ctx)
    {
        if (bs.WriteFlag((mask & 0x01) != 0))
            Pj.WriteVec(bs, Transform.P);
    }

    private GameBase? Target()
    {
        var m = Manager();
        var o = m != null && _target != 0 ? m.FindById(_target) : null;
        return o as GameBase;
    }

    // 0x0046785c.
    private void RefreshPercentage()
    {
        using var chop = X87.Chop();
        var t = Target();
        var fn = t != null ? Pj.FindMethod(Game, this, "updateTargetPercentage") : "";
        if (fn.Length != 0)
            _pct = Store(Numbers.BorlandAtof(Pj.CallMethod(Game, fn, new[] { Numbers.FormatInt(t!.Id()) }))); // "%s(%d);"
        else
            _pct = 1.0f;
        var r = X87.F32(Add(Mul(Sub(_seekRadius, _freeRadius), _pct), _freeRadius));
        const double kTwoPi = 6.283185307179586; // 0x00467900
        _turnRate = X87.F32(Div(mData.MuzzleVelocity, Mul(kTwoPi, r)));
        _lastPct = Last;
    }

    // 0x00467620.
    private void TargetPoint(Span<float> output)
    {
        var t = Target();
        Span<float> mn = stackalloc float[3], mx = stackalloc float[3];
        if (t != null && Pj.WorldBox(Game, t, mn, mx))
        {
            for (var i = 0; i < 3; ++i)
                output[i] = X87.F32(Mul(Add(mx[i], mn[i]), 0.5f));
            return;
        }
        for (var i = 0; i < 3; ++i)
            output[i] = X87.F32(Add(Transform.P[i], X87.F32(Mul(Velocity[i], 1000.0f))));
    }

    // 0x004671b8: the direction to fly to.
    private void Aim(Span<float> output)
    {
        var t = Target();
        float[] tp = new float[3], d = new float[3], lead = new float[3];
        TargetPoint(tp);
        Seeker.SeekerLead(Transform.P, tp, t?.Velocity, mData.MuzzleVelocity, d, lead);
        const uint mask = 0x24e000a6u & ~0x80u;
        float[] e1 = new float[3], e2 = new float[3];
        for (var i = 0; i < 3; ++i)
        {
            e1[i] = X87.F32(Add(Transform.P[i], lead[i]));
            e2[i] = X87.F32(Add(Transform.P[i], d[i]));
        }
        var h = new RayHit();
        // 0x00467424: no hit, or the first ray (to the lead point) hits the target itself: aim at the lead point.
        if (!CastWorld(Game, Transform.P, e1, mask, null, ref h) || h.Object == t)
        {
            lead.CopyTo(output);
            return;
        }
        // Something else is in the way: the direct line (second ray) decides between d and a lost track.
        var h2 = new RayHit();
        if (CastWorld(Game, Transform.P, e2, mask, null, ref h2) && h2.Object != t)
        {
            _tracking = false;
            for (var i = 0; i < 3; ++i)
                output[i] = X87.F32(Mul(Velocity[i], 1000.0f));
            return;
        }
        d.CopyTo(output);
    }

    // 0x00466664.
    private void Steer(int ms)
    {
        float[] a = new float[3], v = new float[3];
        Aim(a);
        if (Seeker.SeekerSteer(Velocity, a, ms, _turnRate, mData.MuzzleVelocity, _tracking, v) == 0)
        {
            if (Target() != null)
            {
                _lost = true;
                OnLiveTimePassed();
            }
            return;
        }
        v.CopyTo(Velocity, 0);
    }
}
