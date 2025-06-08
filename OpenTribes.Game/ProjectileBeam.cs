// Port of src/game/projectile_beam.cpp: LaserProjectile, TargetLaser, Lightning, RepairEffect (docs/projectile-sim.md
// 2.7 - 2.10) and their ghost packers (5.2). The pj helpers of the file are in Projectile.cs (Pj).
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

public static class ProjectileBeam
{
    // src/game/projectile_beam.cpp vecText: "%g %g %g" inside quotes.
    internal static string VecText(ReadOnlySpan<float> v) =>
        "\"" + Pj.FmtG(v[0]) + " " + Pj.FmtG(v[1]) + " " + Pj.FmtG(v[2]) + "\"";

    // src/game/projectile_beam.cpp beamRange: terrain visibleDistance (object 8 +0x2e0), capped at 4000 m.
    internal static float BeamRange(GameState game)
    {
        SimManager? m = game.Sim?.Server();
        SimObject? t = m?.FindById(8);
        if (t is TerrainObject terr && t.ClassName() == "SimTerrain")
        {
            float vis = terr.VisibleDistance();
            if (vis < 4000.0f)
                return vis;
        }
        return 4000.0f;
    }

    // src/game/projectile_beam.cpp createBeamProjectile.
    public static Projectile? CreateBeamProjectile(GameState game, int group) => group switch
    {
        Projectile.kLaserGroup => new Laser(game),
        Projectile.kTargetLaserGroup => new TargetLaser(game),
        Projectile.kSeekerGroup => new SeekingMissile(game),
        Projectile.kLightningGroup => new Lightning(game),
        Projectile.kRepairGroup => new RepairEffect(game),
        _ => null,
    };
}

// ---- LaserProjectile (2.7) ----

// src/game/projectile_beam.cpp class Laser.
internal class Laser : Projectile
{
    protected readonly float[] mStart = new float[3], mEnd = new float[3], mDir = { 0, 1, 0 }; // +0x4ec, +0x4f8, +0x504
    protected float mEnergy, mFraction;  // +0x510, +0x514
    protected bool mHit;                 // +0x518
    protected uint mCreated;             // +0x608
    protected float mConversion, mBeamTime = 10000;

    private const double kInv55 = 1.0 / 55.0; // 80-bit constant at 0x00463b9c

    public Laser(GameState g, string cls = "LaserProjectile", int group = kLaserGroup) : base(g, cls, group) { }

    // 0x00463af8: the velocity argument is ignored and +0x210 is not written.
    public override void Init(in Transform t, ReadOnlySpan<float> velocity, int shooterId)
    {
        mEnergy = 0.0f;
        Transform = t;
        mInitShooter = shooterId;
        mStart[0] = t.P[0];
        mStart[1] = t.P[1];
        mStart[2] = t.P[2];
        mDir[0] = t.M[3];
        mDir[1] = t.M[4];
        mDir[2] = t.M[5];
    }

    // 0x00463b54.
    public override void SetShotEnergy(float used, float fraction)
    {
        using var chop = X87.Chop();
        mEnergy = X87.F32(Mul(used, fraction));
        mFraction = mEnergy > 55.0f ? 1.0f : X87.F32(Mul(mEnergy, kInv55));
    }

    public float Energy_() => mEnergy;
    public float EnergyFraction() => mFraction;
    public bool Hit() => mHit;

    protected override void ReadData(DataBlockManager.Block b)
    {
        mConversion = Pj.Real(b, "damageConversion", 0.0f);
        mBeamTime = Pj.Real(b, "beamTime", 10000.0f);
    }

    protected override void ClassOnAdd()
    {
        RemoveFromContainer();
        mCreated = mGame.Sim!.TickMs();
        Resolve();
        Array.Clear(Velocity);
    }

    public override void ServerTick(uint now)
    {
        if (now > unchecked(Pj.Ftol(Mul(mBeamTime, 1000.0f)) + mCreated) && Manager() != null)
            Manager()!.DeleteObject(this);
    }

    public override void PackInitial(BitStream bs, GhostContext ctx)
    {
        // LaserProjectile_packInitial (0x00463c54): the start only when the client has no ghost of the shooter to take the
        // muzzle from; hit, end and energy always.
        GameBase? sh = Shooter();
        if (sh == null || ctx.Manager.GhostIndex(sh) == -1)
            Pj.WriteVec(bs, mStart);
        bs.WriteFlag(mHit);
        Pj.WriteVec(bs, mEnd);
        Pj.WriteUnit(bs, mFraction, 5);
    }

    // The whole shot (onAdd, server): cast to the end of the range, damage on a hit.
    protected void Resolve()
    {
        using var chop = X87.Chop();
        float range = ProjectileBeam.BeamRange(mGame);
        for (int i = 0; i < 3; ++i)
            mEnd[i] = X87.F32(Add(mStart[i], X87.F32(Mul(mDir[i], range))));
        var h = new RayHit();
        if (CastWorld(mGame, mStart, mEnd, 0x24e000a6u, Shooter(), ref h))
        {
            h.Point[..].CopyTo(mEnd);
            mHit = true;
            float value = X87.F32(Mul(mEnergy, mConversion));
            if (h.Object != null && (h.Type & 0x24c00080u) != 0 && value != 0.0f)
            {
                float[] vec = new float[3], zero = { 0, 0, 0 };
                for (int i = 0; i < 3; ++i)
                    vec[i] = X87.F32(Sub(mEnd[i], mStart[i]));
                h.Object.ApplyDamage(6, value, mEnd, vec, zero, OwnerClientId());
            }
        }
        else
        {
            mHit = false;
        }
    }
}

// ---- TargetLaser (2.8) ----

// src/game/projectile_beam.cpp class TargetLaser.
internal sealed class TargetLaser : Laser
{
    private bool mDirty, mShut; // +0x661, +0x660

    public TargetLaser(GameState g) : base(g, "TargetLaser", kTargetLaserGroup) { }

    public override bool IsSustained() => true;

    // D11: the original reads the shooter's team through a null pointer when the first ghost is packed. A TargetLaser
    // without a shooter is not created.
    public override bool OnAdd()
    {
        if (!base.OnAdd())
            return false;
        if (Shooter() == null)
        {
            RemoveFromContainer();
            return false;
        }
        return true;
    }

    protected override void ClassOnAdd()
    {
        base.ClassOnAdd();
        mGame.JoinSet(this, 725);
        mLastSend = mGame.Sim!.TickMs();
    }

    // Vtable +0x1c4: start and direction follow the muzzle, the end point is the 4000 m ray.
    public override void UpdateTransform(in Transform t)
    {
        using var chop = X87.Chop();
        Transform = t;
        mStart[0] = t.P[0];
        mStart[1] = t.P[1];
        mStart[2] = t.P[2];
        mDir[0] = t.M[3];
        mDir[1] = t.M[4];
        mDir[2] = t.M[5];
        float[] cand = new float[3];
        for (int i = 0; i < 3; ++i)
            cand[i] = X87.F32(Add(mStart[i], X87.F32(Mul(mDir[i], 4000.0f))));
        var h = new RayHit();
        bool hit = CastWorld(mGame, mStart, cand, 0x24e000a6u, Shooter(), ref h);
        if (hit)
            h.Point[..].CopyTo(cand);
        float[] d = new float[3];
        for (int i = 0; i < 3; ++i)
            d[i] = X87.F32(Sub(mEnd[i], cand[i]));
        if (X87.Len(d) > 0.25f || hit != mHit)
        {
            cand.CopyTo(mEnd, 0);
            mHit = hit;
            mDirty = true;
        }
    }

    public override void ShutOff() => mShut = true;

    public override void ServerTick(uint now)
    {
        if (mDirty && now > unchecked(mLastSend + 100))
        {
            mLastSend = now;
            SetMaskBits(0x08);
        }
        if (mShut && Manager() != null)
            Manager()!.DeleteObject(this);
    }

    public override bool BeamTargetPoint(Span<float> output, out int team)
    {
        team = 0;
        if (!mHit)
            return false;
        mEnd.CopyTo(output);
        GameBase? sh = Shooter();
        team = sh != null ? sh.Team : 0;
        return true;
    }

    public override void PackInitial(BitStream bs, GhostContext ctx)
    {
        Pj.WriteVec(bs, mStart);
        Pj.WriteVec(bs, mEnd);
        GameBase? sh = Shooter();
        bs.WriteInt((uint)((sh != null ? sh.OwnerClient : 0) - 2048) & 0x3ff, 10);
        bs.WriteInt((uint)(sh != null ? sh.Team : 0) & 7, 3);
        bs.WriteFlag(mHit);
        mDirty = false;
    }

    public override void PackUpdate(BitStream bs, uint mask, GhostContext ctx)
    {
        Pj.WriteVec(bs, mStart);
        Pj.WriteVec(bs, mEnd);
        bs.WriteFlag(mHit);
        mDirty = false;
    }
}

// ---- Lightning (2.9) and RepairEffect (2.10) ----

// src/game/projectile_beam.cpp class Lightning.
internal class Lightning : Projectile
{
    protected uint mLastSearch;      // +0x45c
    protected bool mShut;            // +0x468
    protected int mTarget;           // +0x474 (object id)
    protected readonly float[] mPoint = new float[3]; // +0x478
    protected float mBoltLength = 5, mConeAngle, mDamagePerSec, mDrainPerSec;

    private const double kDegToRad = 1.0 / 180.0;

    public Lightning(GameState g, string cls = "Lightning", int group = kLightningGroup) : base(g, cls, group) { }

    // 0x004691f0: +0x210 is not written.
    public override void Init(in Transform t, ReadOnlySpan<float> velocity, int shooterId)
    {
        Transform = t;
        mInitShooter = shooterId;
    }

    public override void UpdateTransform(in Transform t) => Transform = t;
    public override bool IsSustained() => true;

    public override void ShutOff()
    {
        if (Target() != null)
            SetMaskBits(0x08);
        mTarget = 0;
        mShut = true;
    }

    protected override void ReadData(DataBlockManager.Block b)
    {
        mBoltLength = Pj.Real(b, "boltLength", 5.0f);
        mConeAngle = Pj.Real(b, "coneAngle", 0.0f);
        mDamagePerSec = Pj.Real(b, "damagePerSec", 0.0f);
        mDrainPerSec = Pj.Real(b, "energyDrainPerSec", 0.0f);
    }

    protected override void ClassOnAdd()
    {
        Array.Clear(Velocity);
        mGame.JoinSet(this, 14);
        uint now = mGame.Sim!.TickMs();
        mLastSend = now;
        mLastSearch = unchecked(now - 100);
        Search(now);
    }

    public override void ServerTick(uint now)
    {
        if (mShut)
        {
            Manager()?.DeleteObject(this);
            return;
        }
        Search(now);
        if (unchecked(now - mLastSend) > 300)
        {
            SetMaskBits(0x08);
            mLastSend = now;
        }
        TickDamage(now);
    }

    public override void PackInitial(BitStream bs, GhostContext ctx) => PackTarget(bs, ctx);

    public override void PackUpdate(BitStream bs, uint mask, GhostContext ctx)
    {
        if (bs.WriteFlag((mask & 0x08) != 0))
            PackTarget(bs, ctx);
    }

    protected virtual void Search(uint now)
    {
        if (now < unchecked(mLastSearch + 100))
            return;
        mLastSearch = now;
        using var chop = X87.Chop();
        float[] fwd = { Transform.M[3], Transform.M[4], Transform.M[5] };
        float[] p = { Transform.P[0], Transform.P[1], Transform.P[2] };
        float l = mBoltLength;
        float[] reach = new float[3], e = new float[3];
        for (int i = 0; i < 3; ++i)
        {
            reach[i] = X87.F32(Mul(fwd[i], l));
            e[i] = X87.F32(Add(p[i], reach[i]));
        }
        SimObject? ex = Shooter();
        var h = new RayHit();
        if (CastWorld(mGame, p, e, 0x24c002a0u, ex, ref h) && h.Object != null)
        {
            float[] mn = new float[3], mx = new float[3], centre = new float[3];
            if (Pj.WorldBox(mGame, h.Object, mn, mx))
            {
                for (int i = 0; i < 3; ++i)
                    centre[i] = X87.F32(Mul(Add(mx[i], mn[i]), 0.5f));
                var h2 = new RayHit();
                if (CastWorld(mGame, p, centre, 0x6u, ex, ref h2) && h2.Object != h.Object)
                {
                    Drop(); // terrain or an interior is in the way
                    return;
                }
            }
            if (h.Object.Id() != mTarget)
            {
                SetMaskBits(0x08);
                h.Point[..].CopyTo(mPoint);
                if (mPoint[0] == p[0] && mPoint[1] == p[1] && mPoint[2] == p[2])
                    for (int i = 0; i < 3; ++i)
                        mPoint[i] = X87.F32(Add(mPoint[i], X87.F32(Mul(fwd[i], 0.1f))));
                mTarget = h.Object.Id();
            }
            return;
        }
        // Cone search, only when the ray hit nothing.
        double ang = Mul(Mul(mConeAngle, 3.14159265358979323846), kDegToRad);
        float cosA = X87.F32(Math.Cos(ang)), sinA = X87.F32(Math.Sin(ang));
        float[] lo = new float[3], hi = new float[3];
        for (int i = 0; i < 3; ++i)
        {
            float off = X87.F32(Mul(reach[i], sinA));
            float a = X87.F32(Add(e[i], off)), b = X87.F32(Sub(e[i], off));
            lo[i] = MinF(p[i], MinF(a, b));
            hi[i] = MaxF(p[i], MaxF(a, b));
        }
        float[] box = { lo[0], lo[1], lo[2], hi[0], hi[1], hi[2] };
        GameBase? best = null;
        float bestQ = 2.0f;
        float[] bestPoint = { 0, 0, 0 };
        foreach (object? o in mGame.World.FindObjects(box, 0x24c002a0u, ex))
        {
            float[] mn = new float[3], mx = new float[3];
            if (o is not GameBase gb || !Pj.WorldBox(mGame, gb, mn, mx))
                continue;
            float[] c = new float[3], d = new float[3];
            for (int i = 0; i < 3; ++i)
            {
                c[i] = X87.F32(Mul(Add(mx[i], mn[i]), 0.5f));
                d[i] = X87.F32(Sub(c[i], p[i]));
            }
            float proj = X87.F32(X87.Dot(d, fwd));
            if (proj > l)
                continue;
            float q = X87.F32(Div(proj, X87.Len(d)));
            if (q < cosA || q >= bestQ)
                continue;
            var los = new RayHit();
            // [code 00468e76 to 00468e83]: the candidate passes when the ray misses or when the first dword of the collision info
            // equals the candidate. castRay never writes that dword (the object is at +4, the constructor clears +0), so any hit
            // fails the test: only a ray that reaches the centre without a hit passes, which a candidate with a collision image in
            // plain view never allows (the reference does not acquire it, N2 of projectile-sim.md 12).
            if (CastWorld(mGame, p, c, 0x24c002a6u, ex, ref los))
                continue;
            bestQ = q;
            best = gb;
            c.CopyTo(bestPoint, 0);
        }
        if (best == null)
        {
            Drop();
            return;
        }
        // [code 00468eb9 to 00468eed]: the exe compares the held target with the same first dword of the collision info (0), so the
        // mask bit is set when a target is held, whether or not it is the winner, and not when none is held.
        if (mTarget != 0)
            SetMaskBits(0x08);
        mTarget = best.Id();
        bestPoint.CopyTo(mPoint, 0);
    }

    private static float MinF(float a, float b) => b < a ? b : a; // std::min
    private static float MaxF(float a, float b) => a < b ? b : a; // std::max

    // Drop the current target (mask 0x08 when there was one).
    protected void Drop()
    {
        if (mTarget != 0)
            SetMaskBits(0x08);
        mTarget = 0;
    }

    protected GameBase? Target()
    {
        SimManager? m = Manager();
        SimObject? o = m != null && mTarget != 0 ? m.FindById(mTarget) : null;
        return o as GameBase;
    }

    // Tick steps 4-6: the engine side of the damage is the script method.
    protected void TickDamage(uint now)
    {
        using var chop = X87.Chop();
        int ms = unchecked((int)(now - mLast));
        mLast = now;
        GameBase? t = Target();
        if (ms == 0 || t == null)
            return;
        float[] d = new float[3], vec = new float[3];
        for (int i = 0; i < 3; ++i)
            d[i] = X87.F32(Sub(mPoint[i], Transform.P[i]));
        float k = X87.F32(Div(1.0, X87.F32(X87.Len(d))));
        for (int i = 0; i < 3; ++i)
            vec[i] = X87.F32(Mul(d[i], k));
        string fn = Pj.FindMethod(mGame, this, "damageTarget");
        if (fn.Length == 0)
            return;
        double secs = Mul(ms, 0.001f);
        float[] zero = { 0, 0, 0 };
        Pj.CallMethod(mGame, fn, new[]
        {
            Numbers.FormatInt(t.Id()), Pj.FmtG(secs), Pj.FmtG(mDamagePerSec), Pj.FmtG(mDrainPerSec), ProjectileBeam.VecText(mPoint),
            ProjectileBeam.VecText(vec), ProjectileBeam.VecText(zero), Numbers.FormatInt(OwnerClientId()),
        });
    }

    protected void PackTarget(BitStream bs, GhostContext ctx)
    {
        GameBase? t = Target();
        int ghost = t != null ? ctx.Manager.GhostIndex(t) : -1;
        if (bs.WriteFlag(ghost != -1))
        {
            bs.WriteInt((uint)ghost, 10);
            using var chop = X87.Chop();
            float[] off = new float[3];
            for (int i = 0; i < 3; ++i)
                off[i] = X87.F32(Sub(mPoint[i], t!.Transform.P[i]));
            Pj.WriteVec(bs, off);
        }
    }
}

// src/game/projectile_beam.cpp class RepairEffect.
internal sealed class RepairEffect : Lightning
{
    // The first script argument is the return value of GameBase_idString (0x00448630), the address of its static buffer,
    // printed with %d. The stock scripts do not use it.
    private const string kHandle = "6833472";
    private bool mAcquired; // +0x4a4

    public RepairEffect(GameState g) : base(g, "RepairEffect", kRepairGroup) { }

    public override void ServerTick(uint now)
    {
        if (mShut)
        {
            Manager()?.DeleteObject(this);
            return;
        }
        Search(now);
        mLast = now;
        if (unchecked(now - mLastSend) > 300)
        {
            SetMaskBits(0x08);
            mLastSend = now;
        }
    }

    public override void ShutOff()
    {
        mTarget = 0;
        mShut = true;
        if (mAcquired)
        {
            string fn = Pj.FindMethod(mGame, this, "onRelease");
            if (fn.Length != 0)
                Pj.CallMethod(mGame, fn, new[] { kHandle, Numbers.FormatInt(mShooterObject) });
        }
    }

    private void CheckDone()
    {
        string fn = Pj.FindMethod(mGame, this, "checkDone");
        if (fn.Length != 0)
            Pj.CallMethod(mGame, fn, new[] { kHandle, Numbers.FormatInt(mShooterObject) });
    }

    protected override void Search(uint now)
    {
        if (now < unchecked(mLastSearch + 100))
            return;
        mLastSearch = now;
        if (mAcquired && mTarget == 0)
        {
            CheckDone(); // self repair
            return;
        }
        if (Player.From(Shooter()) is not Player s)
            return; // D11: the original calls through a null cast
        using var chop = X87.Chop();
        var h = new RayHit();
        GameBase? obj = null;
        if (ProjectileFire.ProjectileCastLOS(mGame, ProjectileFire.ProjectileEyeTransform(s), 0x24c002a6u, s, mBoltLength, ref h))
        {
            h.Point[..].CopyTo(mPoint);
            if (mPoint[0] == Transform.P[0] && mPoint[1] == Transform.P[1] && mPoint[2] == Transform.P[2])
                for (int i = 0; i < 3; ++i)
                    mPoint[i] = X87.F32(Add(mPoint[i], X87.F32(Mul(Transform.M[3 + i], 0.1f))));
            if (h.Object != null && (h.Type & 0x6u) == 0) // terrain and interiors count as no object
                obj = h.Object;
        }
        int prev = mTarget;
        mTarget = obj != null ? obj.Id() : 0;
        if (mAcquired)
        {
            if (prev != 0 && prev != mTarget)
                s.TriggerImage(0, false); // Player_releaseImageTrigger (0x0041a9b8)
            else
                CheckDone();
        }
        else
        {
            string fn = Pj.FindMethod(mGame, this, "onAcquire");
            if (fn.Length != 0)
                Pj.CallMethod(mGame, fn, new[]
                {
                    kHandle, Numbers.FormatInt(mShooterObject), Numbers.FormatInt(obj != null ? obj.Id() : mShooterObject),
                });
            mAcquired = true;
        }
    }
}
