// Port of src/game/projectile_beam.cpp: LaserProjectile, TargetLaser, Lightning, RepairEffect (docs/projectile-sim.md
// 2.7 - 2.10) and their ghost packers (5.2). The pj helpers of the file are in Projectile.cs (Pj).
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>The LaserProjectile, TargetLaser, Lightning and RepairEffect projectiles and their ghost packers.</summary>
/// <remarks>See docs/projectile-sim.md 2.7 to 2.10 and 5.2. The pj helpers are in Projectile.cs.</remarks>
public static class ProjectileBeam
{
    // src/game/projectile_beam.cpp vecText: "%g %g %g" inside quotes.
    internal static string VecText(ReadOnlySpan<float> v) =>
        "\"" + Pj.FmtG(v[0]) + " " + Pj.FmtG(v[1]) + " " + Pj.FmtG(v[2]) + "\"";

    // src/game/projectile_beam.cpp beamRange: terrain visibleDistance (object 8 +0x2e0), capped at 4000 m.
    internal static float BeamRange(GameState game)
    {
        var m = game.Sim?.Server();
        var t = m?.FindById(8);
        if (t is TerrainObject terr && t.ClassName() == "SimTerrain")
        {
            var vis = terr.VisibleDistance();
            if (vis < 4000.0f)
                return vis;
        }
        return 4000.0f;
    }

    /// <summary>Creates the beam projectile of a datablock group.</summary>
    /// <param name="game">The game state.</param>
    /// <param name="group">The datablock group.</param>
    /// <returns>The new projectile, or null for a group that is not a beam.</returns>
    public static Projectile? CreateBeamProjectile(GameState game, int group) => group switch
    {
        Projectile.LaserGroup => new Laser(game),
        Projectile.TargetLaserGroup => new TargetLaser(game),
        Projectile.SeekerGroup => new SeekingMissile(game),
        Projectile.LightningGroup => new Lightning(game),
        Projectile.RepairGroup => new RepairEffect(game),
        _ => null,
    };
}

// ---- LaserProjectile (2.7) ----

// src/game/projectile_beam.cpp class Laser.
internal class Laser : Projectile
{
    protected float[] Start { get; } = new float[3]; // +0x4ec, +0x4f8, +0x504
    protected float[] End { get; } = new float[3]; // +0x4ec, +0x4f8, +0x504
    protected float[] Dir { get; } = [0, 1, 0]; // +0x4ec, +0x4f8, +0x504
    protected float BeamEnergy { get; set; }  // +0x510, +0x514
    protected float Fraction { get; set; }  // +0x510, +0x514
    protected bool HitFlag { get; set; }                 // +0x518
    protected uint Created { get; set; }             // +0x608
    protected float Conversion { get; set; }
    protected float BeamTime { get; set; } = 10000;

    private const double Inv55 = 1.0 / 55.0; // 80-bit constant at 0x00463b9c

    public Laser(GameState g, string cls = "LaserProjectile", int group = LaserGroup) : base(g, cls, group) { }

    // 0x00463af8: the velocity argument is ignored and +0x210 is not written.
    public override void Init(in Transform t, ReadOnlySpan<float> velocity, int shooterId)
    {
        BeamEnergy = 0.0f;
        Transform = t;
        InitShooter = shooterId;
        Start[0] = t.P[0];
        Start[1] = t.P[1];
        Start[2] = t.P[2];
        Dir[0] = t.M[3];
        Dir[1] = t.M[4];
        Dir[2] = t.M[5];
    }

    // 0x00463b54.
    public override void SetShotEnergy(float used, float fraction)
    {
        using var chop = X87.Chop();
        BeamEnergy = X87.F32(Mul(used, fraction));
        Fraction = BeamEnergy > 55.0f ? 1.0f : X87.F32(Mul(BeamEnergy, Inv55));
    }

    public float Energy_() => BeamEnergy;
    public float EnergyFraction() => Fraction;
    public bool Hit() => HitFlag;

    protected override void ReadData(DataBlockManager.Block b)
    {
        Conversion = Pj.Real(b, "damageConversion", 0.0f);
        BeamTime = Pj.Real(b, "beamTime", 10000.0f);
    }

    protected override void ClassOnAdd()
    {
        RemoveFromContainer();
        Created = Game.Sim!.TickMs();
        Resolve();
        Array.Clear(Velocity);
    }

    public override void ServerTick(uint now)
    {
        if (now > unchecked(Pj.Ftol(Mul(BeamTime, 1000.0f)) + Created) && Manager() != null)
            Manager()!.DeleteObject(this);
    }

    public override void PackInitial(BitBuffer bs, GhostContext ctx)
    {
        // LaserProjectile_packInitial (0x00463c54): the start only when the client has no ghost of the shooter to take the
        // muzzle from; hit, end and energy always.
        var sh = Shooter();
        if (sh == null || ctx.Manager.GhostIndex(sh) == -1)
            Pj.WriteVec(bs, Start);
        bs.WriteFlag(HitFlag);
        Pj.WriteVec(bs, End);
        Pj.WriteUnit(bs, Fraction, 5);
    }

    // The whole shot (onAdd, server): cast to the end of the range, damage on a hit.
    protected void Resolve()
    {
        using var chop = X87.Chop();
        var range = ProjectileBeam.BeamRange(Game);
        for (var i = 0; i < 3; ++i)
            End[i] = X87.F32(Add(Start[i], X87.F32(Mul(Dir[i], range))));
        var h = new RayHit();
        if (CastWorld(Game, Start, End, 0x24e000a6u, Shooter(), ref h))
        {
            h.Point[..].CopyTo(End);
            HitFlag = true;
            var value = X87.F32(Mul(BeamEnergy, Conversion));
            if (h.HitObject != null && (h.Type & 0x24c00080u) != 0 && value != 0.0f)
            {
                float[] vec = new float[3], zero = [0, 0, 0];
                for (var i = 0; i < 3; ++i)
                    vec[i] = X87.F32(Sub(End[i], Start[i]));
                h.HitObject.ApplyDamage(6, value, End, vec, zero, OwnerClientId());
            }
        }
        else
        {
            HitFlag = false;
        }
    }
}

// ---- TargetLaser (2.8) ----

// src/game/projectile_beam.cpp class TargetLaser.
internal sealed class TargetLaser : Laser
{
    private bool _dirty, _shut; // +0x661, +0x660

    public TargetLaser(GameState g) : base(g, "TargetLaser", TargetLaserGroup) { }

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
        Game.JoinSet(this, 725);
        LastSend = Game.Sim!.TickMs();
    }

    // Vtable +0x1c4: start and direction follow the muzzle, the end point is the 4000 m ray.
    public override void UpdateTransform(in Transform t)
    {
        using var chop = X87.Chop();
        Transform = t;
        Start[0] = t.P[0];
        Start[1] = t.P[1];
        Start[2] = t.P[2];
        Dir[0] = t.M[3];
        Dir[1] = t.M[4];
        Dir[2] = t.M[5];
        var cand = new float[3];
        for (var i = 0; i < 3; ++i)
            cand[i] = X87.F32(Add(Start[i], X87.F32(Mul(Dir[i], 4000.0f))));
        var h = new RayHit();
        var hit = CastWorld(Game, Start, cand, 0x24e000a6u, Shooter(), ref h);
        if (hit)
            h.Point[..].CopyTo(cand);
        var d = new float[3];
        for (var i = 0; i < 3; ++i)
            d[i] = X87.F32(Sub(End[i], cand[i]));
        if (X87.Len(d) > 0.25f || hit != HitFlag)
        {
            cand.CopyTo(End, 0);
            HitFlag = hit;
            _dirty = true;
        }
    }

    public override void ShutOff() => _shut = true;

    public override void ServerTick(uint now)
    {
        if (_dirty && now > unchecked(LastSend + 100))
        {
            LastSend = now;
            SetMaskBits(0x08);
        }
        if (_shut && Manager() != null)
            Manager()!.DeleteObject(this);
    }

    public override bool BeamTargetPoint(Span<float> output, out int team)
    {
        team = 0;
        if (!HitFlag)
            return false;
        End.CopyTo(output);
        var sh = Shooter();
        team = sh != null ? sh.Team : 0;
        return true;
    }

    public override void PackInitial(BitBuffer bs, GhostContext ctx)
    {
        Pj.WriteVec(bs, Start);
        Pj.WriteVec(bs, End);
        var sh = Shooter();
        bs.WriteInt((uint)((sh != null ? sh.OwnerClient : 0) - 2048) & 0x3ff, 10);
        bs.WriteInt((uint)(sh != null ? sh.Team : 0) & 7, 3);
        bs.WriteFlag(HitFlag);
        _dirty = false;
    }

    public override void PackUpdate(BitBuffer bs, uint mask, GhostContext ctx)
    {
        Pj.WriteVec(bs, Start);
        Pj.WriteVec(bs, End);
        bs.WriteFlag(HitFlag);
        _dirty = false;
    }
}

// ---- Lightning (2.9) and RepairEffect (2.10) ----

// src/game/projectile_beam.cpp class Lightning.
internal class Lightning : Projectile
{
    protected uint LastSearch { get; set; }      // +0x45c
    protected bool Shut { get; set; }            // +0x468
    protected int TargetId { get; set; }           // +0x474 (object id)
    protected float[] Point { get; } = new float[3]; // +0x478
    protected float BoltLength { get; set; } = 5;
    protected float ConeAngle { get; set; }
    protected float DamagePerSec { get; set; }
    protected float DrainPerSec { get; set; }

    private const double DegToRad = 1.0 / 180.0;

    public Lightning(GameState g, string cls = "Lightning", int group = LightningGroup) : base(g, cls, group) { }

    // 0x004691f0: +0x210 is not written.
    public override void Init(in Transform t, ReadOnlySpan<float> velocity, int shooterId)
    {
        Transform = t;
        InitShooter = shooterId;
    }

    public override void UpdateTransform(in Transform t) => Transform = t;
    public override bool IsSustained() => true;

    public override void ShutOff()
    {
        if (Target() != null)
            SetMaskBits(0x08);
        TargetId = 0;
        Shut = true;
    }

    protected override void ReadData(DataBlockManager.Block b)
    {
        BoltLength = Pj.Real(b, "boltLength", 5.0f);
        ConeAngle = Pj.Real(b, "coneAngle", 0.0f);
        DamagePerSec = Pj.Real(b, "damagePerSec", 0.0f);
        DrainPerSec = Pj.Real(b, "energyDrainPerSec", 0.0f);
    }

    protected override void ClassOnAdd()
    {
        Array.Clear(Velocity);
        Game.JoinSet(this, 14);
        var now = Game.Sim!.TickMs();
        LastSend = now;
        LastSearch = unchecked(now - 100);
        Search(now);
    }

    public override void ServerTick(uint now)
    {
        if (Shut)
        {
            Manager()?.DeleteObject(this);
            return;
        }
        Search(now);
        if (unchecked(now - LastSend) > 300)
        {
            SetMaskBits(0x08);
            LastSend = now;
        }
        TickDamage(now);
    }

    public override void PackInitial(BitBuffer bs, GhostContext ctx) => PackTarget(bs, ctx);

    public override void PackUpdate(BitBuffer bs, uint mask, GhostContext ctx)
    {
        if (bs.WriteFlag((mask & 0x08) != 0))
            PackTarget(bs, ctx);
    }

    protected virtual void Search(uint now)
    {
        if (now < unchecked(LastSearch + 100))
            return;
        LastSearch = now;
        using var chop = X87.Chop();
        float[] fwd = [Transform.M[3], Transform.M[4], Transform.M[5]];
        float[] p = [Transform.P[0], Transform.P[1], Transform.P[2]];
        var l = BoltLength;
        float[] reach = new float[3], e = new float[3];
        for (var i = 0; i < 3; ++i)
        {
            reach[i] = X87.F32(Mul(fwd[i], l));
            e[i] = X87.F32(Add(p[i], reach[i]));
        }
        SimObject? ex = Shooter();
        var h = new RayHit();
        if (CastWorld(Game, p, e, 0x24c002a0u, ex, ref h) && h.HitObject != null)
        {
            float[] mn = new float[3], mx = new float[3], centre = new float[3];
            if (Pj.WorldBox(Game, h.HitObject, mn, mx))
            {
                for (var i = 0; i < 3; ++i)
                    centre[i] = X87.F32(Mul(Add(mx[i], mn[i]), 0.5f));
                var h2 = new RayHit();
                if (CastWorld(Game, p, centre, 0x6u, ex, ref h2) && h2.HitObject != h.HitObject)
                {
                    Drop(); // terrain or an interior is in the way
                    return;
                }
            }
            if (h.HitObject.Id() != TargetId)
            {
                SetMaskBits(0x08);
                h.Point[..].CopyTo(Point);
                if (Point[0] == p[0] && Point[1] == p[1] && Point[2] == p[2])
                    for (var i = 0; i < 3; ++i)
                        Point[i] = X87.F32(Add(Point[i], X87.F32(Mul(fwd[i], 0.1f))));
                TargetId = h.HitObject.Id();
            }
            return;
        }
        // Cone search, only when the ray hit nothing.
        var ang = Mul(Mul(ConeAngle, 3.14159265358979323846), DegToRad);
        float cosA = X87.F32(Math.Cos(ang)), sinA = X87.F32(Math.Sin(ang));
        float[] lo = new float[3], hi = new float[3];
        for (var i = 0; i < 3; ++i)
        {
            var off = X87.F32(Mul(reach[i], sinA));
            float a = X87.F32(Add(e[i], off)), b = X87.F32(Sub(e[i], off));
            lo[i] = MinF(p[i], MinF(a, b));
            hi[i] = MaxF(p[i], MaxF(a, b));
        }
        float[] box = [lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]];
        GameBase? best = null;
        var bestQ = 2.0f;
        float[] bestPoint = [0, 0, 0];
        foreach (var o in Game.World.FindObjects(box, 0x24c002a0u, ex))
        {
            float[] mn = new float[3], mx = new float[3];
            if (o is not GameBase gb || !Pj.WorldBox(Game, gb, mn, mx))
                continue;
            float[] c = new float[3], d = new float[3];
            for (var i = 0; i < 3; ++i)
            {
                c[i] = X87.F32(Mul(Add(mx[i], mn[i]), 0.5f));
                d[i] = X87.F32(Sub(c[i], p[i]));
            }
            var proj = X87.F32(X87.Dot(d, fwd));
            if (proj > l)
                continue;
            var q = X87.F32(Div(proj, X87.Len(d)));
            if (q < cosA || q >= bestQ)
                continue;
            var los = new RayHit();
            // [code 00468e76 to 00468e83]: the candidate passes when the ray misses or when the first dword of the collision info
            // equals the candidate. castRay never writes that dword (the object is at +4, the constructor clears +0), so any hit
            // fails the test: only a ray that reaches the centre without a hit passes, which a candidate with a collision image in
            // plain view never allows (the reference does not acquire it, N2 of projectile-sim.md 12).
            if (CastWorld(Game, p, c, 0x24c002a6u, ex, ref los))
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
        if (TargetId != 0)
            SetMaskBits(0x08);
        TargetId = best.Id();
        bestPoint.CopyTo(Point, 0);
    }

    private static float MinF(float a, float b) => b < a ? b : a; // std::min
    private static float MaxF(float a, float b) => a < b ? b : a; // std::max

    // Drop the current target (mask 0x08 when there was one).
    protected void Drop()
    {
        if (TargetId != 0)
            SetMaskBits(0x08);
        TargetId = 0;
    }

    protected GameBase? Target()
    {
        var m = Manager();
        var o = m != null && TargetId != 0 ? m.FindById(TargetId) : null;
        return o as GameBase;
    }

    // Tick steps 4-6: the engine side of the damage is the script method.
    protected void TickDamage(uint now)
    {
        using var chop = X87.Chop();
        var ms = unchecked((int)(now - Last));
        Last = now;
        var t = Target();
        if (ms == 0 || t == null)
            return;
        float[] d = new float[3], vec = new float[3];
        for (var i = 0; i < 3; ++i)
            d[i] = X87.F32(Sub(Point[i], Transform.P[i]));
        var k = X87.F32(Div(1.0, X87.F32(X87.Len(d))));
        for (var i = 0; i < 3; ++i)
            vec[i] = X87.F32(Mul(d[i], k));
        var fn = Pj.FindMethod(Game, this, "damageTarget");
        if (fn.Length == 0)
            return;
        var secs = Mul(ms, 0.001f);
        float[] zero = [0, 0, 0];
        Pj.CallMethod(Game, fn,
        [
            Numbers.FormatInt(t.Id()), Pj.FmtG(secs), Pj.FmtG(DamagePerSec), Pj.FmtG(DrainPerSec), ProjectileBeam.VecText(Point),
            ProjectileBeam.VecText(vec), ProjectileBeam.VecText(zero), Numbers.FormatInt(OwnerClientId()),
        ]);
    }

    protected void PackTarget(BitBuffer bs, GhostContext ctx)
    {
        var t = Target();
        var ghost = t != null ? ctx.Manager.GhostIndex(t) : -1;
        if (bs.WriteFlag(ghost != -1))
        {
            bs.WriteInt((uint)ghost, 10);
            using var chop = X87.Chop();
            var off = new float[3];
            for (var i = 0; i < 3; ++i)
                off[i] = X87.F32(Sub(Point[i], t!.Transform.P[i]));
            Pj.WriteVec(bs, off);
        }
    }
}

// src/game/projectile_beam.cpp class RepairEffect.
internal sealed class RepairEffect : Lightning
{
    // The first script argument is the return value of GameBase_idString (0x00448630), the address of its static buffer,
    // printed with %d. The stock scripts do not use it.
    private const string Handle = "6833472";
    private bool _acquired; // +0x4a4

    public RepairEffect(GameState g) : base(g, "RepairEffect", RepairGroup) { }

    public override void ServerTick(uint now)
    {
        if (Shut)
        {
            Manager()?.DeleteObject(this);
            return;
        }
        Search(now);
        Last = now;
        if (unchecked(now - LastSend) > 300)
        {
            SetMaskBits(0x08);
            LastSend = now;
        }
    }

    public override void ShutOff()
    {
        TargetId = 0;
        Shut = true;
        if (_acquired)
        {
            var fn = Pj.FindMethod(Game, this, "onRelease");
            if (fn.Length != 0)
                Pj.CallMethod(Game, fn, [Handle, Numbers.FormatInt(ShooterObject)]);
        }
    }

    private void CheckDone()
    {
        var fn = Pj.FindMethod(Game, this, "checkDone");
        if (fn.Length != 0)
            Pj.CallMethod(Game, fn, [Handle, Numbers.FormatInt(ShooterObject)]);
    }

    protected override void Search(uint now)
    {
        if (now < unchecked(LastSearch + 100))
            return;
        LastSearch = now;
        if (_acquired && TargetId == 0)
        {
            CheckDone(); // self repair
            return;
        }
        if (Player.From(Shooter()) is not Player s)
            return; // D11: the original calls through a null cast
        using var chop = X87.Chop();
        var h = new RayHit();
        GameBase? obj = null;
        if (ProjectileFire.ProjectileCastLOS(Game, ProjectileFire.ProjectileEyeTransform(s), 0x24c002a6u, s, BoltLength, ref h))
        {
            h.Point[..].CopyTo(Point);
            if (Point[0] == Transform.P[0] && Point[1] == Transform.P[1] && Point[2] == Transform.P[2])
                for (var i = 0; i < 3; ++i)
                    Point[i] = X87.F32(Add(Point[i], X87.F32(Mul(Transform.M[3 + i], 0.1f))));
            if (h.HitObject != null && (h.Type & 0x6u) == 0) // terrain and interiors count as no object
                obj = h.HitObject;
        }
        var prev = TargetId;
        TargetId = obj != null ? obj.Id() : 0;
        if (_acquired)
        {
            if (prev != 0 && prev != TargetId)
                s.TriggerImage(0, false); // Player_releaseImageTrigger (0x0041a9b8)
            else
                CheckDone();
        }
        else
        {
            var fn = Pj.FindMethod(Game, this, "onAcquire");
            if (fn.Length != 0)
                Pj.CallMethod(Game, fn,
                [
                    Handle, Numbers.FormatInt(ShooterObject), Numbers.FormatInt(obj != null ? obj.Id() : ShooterObject),
                ]);
            _acquired = true;
        }
    }
}
