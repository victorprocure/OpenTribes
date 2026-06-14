// Projectile family visuals (docs/shapes-render.md 4.5, 4.7): shape projectiles with trails and lights, and the beam
// classes (laser, target laser, lightning, repair).
using OpenTribes.Client.Effects;
using OpenTribes.Client.Shapes;
using OpenTribes.Game;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Scene;

public sealed class ProjectileVisual : Visual
{
    private int mGroup;
    private ClientDataBlock? mBlock;
    private RenderShape? mShape;
    private RenderInstance? mInst;
    private RenderThread? mAmbient;
    private double mX, mY, mZ, mVx, mVy, mVz;
    private double mT0, mUpdate;
    private bool mHavePos, mExploded;
    private double mLastSmokeX, mLastSmokeY, mLastSmokeZ;
    private bool mSmokeInit;
    private bool mTrailInit;                    // trailType 1: the ribbon starts at the first render and draws from the second
    private double mTailX, mTailY, mTailZ;
    private double mSpin;

    public override (double x, double y, double z)? Position => mHavePos ? (PosNow()) : null;
    public bool Exploded => mExploded;

    private (double, double, double) PosNow()
    {
        double dt = Math.Clamp(M.Time - mUpdate, 0, 5);
        double g = Class == "Grenade" ? -20 : 0;
        return (mX + mVx * dt, mY + mVy * dt, mZ + mVz * dt + 0.5 * g * dt * dt);
    }

    public override void Init(SceneModel m, ClientGhost g)
    {
        base.Init(m, g);
        mGroup = Class switch { "Bullet" => 14, "Grenade" => 15, "RocketDumb" => 16, "SeekingMissile" => 25, _ => 13 };
        mT0 = m.Time;
    }

    public override void OnUpdate(DecodedGhost u, bool isNew)
    {
        bool has(string n) => u.Fields.Any(f => f.Name == n);
        if (has("datablock") || mBlock == null)
        {
            mBlock = M.Db.Block(mGroup, (int)Num("datablock", -1));
            string name = Db.Str(mBlock, "bulletShapeName");
            if (name.EndsWith(".dts", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            mShape = M.Assets.Shape(name);
            if (mShape != null)
            {
                mInst = new RenderInstance(mShape);
                int amb = mShape.FindSequence("ambient");
                if (amb >= 0)
                    mAmbient = mInst.AddThread(amb, 0, 0);
            }
        }
        if (Class == "Bullet" && has("start"))
        {
            // Bullet initial data: start, elapsed ms, direction, speed (m/s = speed16 / 16)
            double[] s = Vec("start"), d = Vec("dir");
            double sp = Num("speed16") * 0.0625, age = Num("elapsedMs") * 0.001;
            (mVx, mVy, mVz) = (d[0] * sp, d[1] * sp, d[2] * sp);
            (mX, mY, mZ) = (s[0] + mVx * age, s[1] + mVy * age, s[2] + mVz * age);
            mUpdate = M.Time;
            mHavePos = true;
        }
        else if (has("pos"))
        {
            double[] p = Vec("pos");
            if (has("vel"))
            {
                double[] v = Vec("vel");
                double k = Class == "SeekingMissile" && isNew ? 0.5 : 1;
                (mVx, mVy, mVz) = (v[0] * k, v[1] * k, v[2] * k);
            }
            else if (Class == "SeekingMissile" && mHavePos)
            {
                // the client flies the missile toward each correction: velocity = 0.15 x (new - old position)
                (mVx, mVy, mVz) = ((p[0] - mX) * 0.15 / 0.1, (p[1] - mY) * 0.15 / 0.1, (p[2] - mZ) * 0.15 / 0.1);
            }
            else if (has("dir9"))
            {
                double[] n = Db.Vec(Ghost.State["dir9"]);
                double sp = Math.Sqrt(mVx * mVx + mVy * mVy + mVz * mVz);
                (mVx, mVy, mVz) = (n[0] * sp, n[1] * sp, n[2] * sp);
            }
            (mX, mY, mZ) = (p[0], p[1], p[2]);
            mUpdate = M.Time;
            mHavePos = true;
        }
        if (has("explode") && !mExploded)
            Explode(has("hitPos"));
    }

    private void Explode(bool hasPos)
    {
        mExploded = true;
        double x, y, z;
        double[] n = Ghost.State.TryGetValue("hitNormal", out object? hn) ? Db.Vec(hn) : new[] { 0.0, 0, 1 };
        int explosion = (int)Db.Num(mBlock, "explosionTag", -1);
        if (Ghost.State.TryGetValue("explosion", out object? ex))
            explosion = (int)Db.ToDouble(ex);
        if (Class == "Bullet")
        {
            // impact point = start + direction * speed * time
            double t = Num("timeMs") * 0.001;
            double[] s = Vec("start"), d = Vec("dir");
            double sp = Num("speed16") * 0.0625;
            (x, y, z) = (s[0] + d[0] * sp * t, s[1] + d[1] * sp * t, s[2] + d[2] * sp * t);
        }
        else if (hasPos)
        {
            double[] h = Vec("hitPos");
            (x, y, z) = (h[0], h[1], h[2]);
        }
        else
            (x, y, z) = PosNow();
        M.SpawnExplosion(explosion, x, y, z, n[0], n[1], n[2]);
        double radius = Db.Num(mBlock, "explosionRadius", 3);
        M.Smoke.RemoveInside(x, y, z, Math.Max(radius, 2));
        int hole = (int)Db.Num(mBlock, "bulletHoleIndex", -1);
        if (Class == "Bullet" && hole >= 0)
            M.AddBulletHole(x, y, z);
    }

    public override void Tick(double dt)
    {
        if (!mHavePos || mExploded || mInst == null && mBlock == null)
            return;
        mSpin += dt;
        if (mInst != null && mAmbient != null)
            mInst.Advance(mAmbient, dt);
        var (x, y, z) = PosNow();
        // Smoke: grenades puff `smokeName` and rockets of trailType 2 puff `trailString` every smokeDist units travelled.
        string smoke = Class == "Grenade" ? Db.Str(mBlock, "smokeName") : (int)Db.Num(mBlock, "trailType") == 2 ? Db.Str(mBlock, "trailString") : "";
        if (smoke.Length > 0)
        {
            double dist = Class == "Grenade" ? 1.0 : Db.Num(mBlock, "smokeDist", 1.8);
            if (!mSmokeInit)
            {
                (mLastSmokeX, mLastSmokeY, mLastSmokeZ, mSmokeInit) = (x, y, z, true);
            }
            double dx = x - mLastSmokeX, dy = y - mLastSmokeY, dz = z - mLastSmokeZ;
            if (dx * dx + dy * dy + dz * dz >= dist * dist)
            {
                if (smoke.EndsWith(".dts", StringComparison.OrdinalIgnoreCase))
                    smoke = smoke[..^4];
                RenderShape? sh = M.Assets.Shape(smoke);
                if (sh != null)
                    M.Smoke.Spawn(sh, x, y, z);
                (mLastSmokeX, mLastSmokeY, mLastSmokeZ) = (x, y, z);
            }
        }
    }

    public PointLight? Light
    {
        get
        {
            if (!mHavePos || mExploded)
                return null;
            double range = Db.Num(mBlock, "lightRange");
            if (range <= 0)
                return null;
            var (x, y, z) = PosNow();
            return new PointLight(x, y, z, (float)Db.Num(mBlock, "lightColor.r"), (float)Db.Num(mBlock, "lightColor.g"), (float)Db.Num(mBlock, "lightColor.b"), range);
        }
    }

    public override void Build(FrameContext ctx)
    {
        if (!mHavePos || mExploded)
            return;
        var (x, y, z) = PosNow();
        if (mInst != null && mShape != null && Db.Flag(mBlock, "isVisible"))
        {
            // 4.5: velocity aligned (rocket, missile, bullet) or the object orientation from the velocity (grenade)
            double sp = Math.Sqrt(mVx * mVx + mVy * mVy + mVz * mVz);
            double fx = sp > 1e-6 ? mVx / sp : 0, fy = sp > 1e-6 ? mVy / sp : 1, fz = sp > 1e-6 ? mVz / sp : 0;
            Xf xf = Class == "RocketDumb" ? Mathx.RocketBasis(x, y, z, fx, fy, fz) : Mathx.Basis(x, y, z, fx, fy, fz);
            double period = Db.Num(mBlock, "rotationPeriod");
            if (period > 0)
                xf = Mathx.SpinY((M.Time - mT0) % period / period * Math.PI * 2).Then(xf);
            ShapeDraw d = ctx.Draw(xf, mShape.Name);
            d.NoHaze = true;
            d.Light = new LightSet { AmbR = 1, AmbG = 1, AmbB = 1, SunR = 0, SunG = 0, SunB = 0 };
            RenderImage? img = ctx.Renderer.Build(mInst, ctx.Camera, d);
            if (img != null)
                ctx.Lists.Add(img);
        }
        if ((int)Db.Num(mBlock, "trailType") == 1)
            BuildTrail(ctx, x, y, z);
    }

    // 4.5 trailType 1 (RocketData `trailAdd2.bmp` ribbon): a quad from a tail point to the projectile. The tail stays where it was
    // until it is `trailLength` behind the projectile and then follows at that distance. The quad is `trailWidth` wide at the
    // projectile and a third of that at the tail, along the projectile's right axis (velocity cross world up), so it is a flat
    // horizontal ribbon and not camera facing. Additive, two sided, depth tested and written, v scrolls five times a second.
    private void BuildTrail(FrameContext ctx, double x, double y, double z)
    {
        double sp = Math.Sqrt(mVx * mVx + mVy * mVy + mVz * mVz);
        double fx = sp > 1e-6 ? mVx / sp : 0, fy = sp > 1e-6 ? mVy / sp : 1, fz = sp > 1e-6 ? mVz / sp : 0;
        Xf axes = Class == "RocketDumb" ? Mathx.RocketBasis(x, y, z, fx, fy, fz) : Mathx.Basis(x, y, z, fx, fy, fz);
        double length = Db.Num(mBlock, "trailLength", 10), width = Db.Num(mBlock, "trailWidth", 0.3);
        if (!mTrailInit)
        {
            (mTailX, mTailY, mTailZ, mTrailInit) = (x, y, z, true);
            return;
        }
        double vx = mTailX - x, vy = mTailY - y, vz = mTailZ - z;
        double len = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        if (len > length)
        {
            double k = length / len;
            (mTailX, mTailY, mTailZ) = (x + vx * k, y + vy * k, z + vz * k);
        }
        TexEntry? tex = ctx.Assets.Texture("trailAdd2.bmp");
        if (tex?.Texture == null)
            return;
        float f = (float)(M.Time * 5 - Math.Floor(M.Time * 5));
        double ht = width / 6, hh = width / 2;
        (double x, double y, double z) Off(double bx, double by, double bz, double h, int sign) =>
            (bx + axes.M0 * h * sign, by + axes.M1 * h * sign, bz + axes.M2 * h * sign);
        var p0 = Off(mTailX, mTailY, mTailZ, ht, 1);
        var p1 = Off(mTailX, mTailY, mTailZ, ht, -1);
        var p2 = Off(x, y, z, hh, -1);
        var p3 = Off(x, y, z, hh, 1);
        Vertex3D V((double x, double y, double z) p, float u, float v) => new((float)p.x, (float)p.y, (float)p.z, u, v, 0, 0, 0);
        Vertex3D a0 = V(p0, 1, f * 0.5f), a1 = V(p1, 0, f * 0.5f), a2 = V(p2, 0, (f + 1) * 0.5f), a3 = V(p3, 1, (f + 1) * 0.5f);
        var img = new RenderImage { Category = 1, Label = "trail" };
        img.Batches.Add(new DrawBatch
        {
            Vertices = new[] { a0, a1, a2, a0, a2, a3, a3, a2, a1, a3, a1, a0 },
            State = new DrawState3D(tex.Texture, null, true, 0, true, true, BlendMode.Additive),
        });
        // the sort key follows the tail point (near / depth of the tail)
        img.SortKey = RenderLists.SortKey(ctx.Camera.ToCamera(mTailX, mTailY, mTailZ).y, ctx.Camera.Near);
        ctx.Lists.Add(img);
    }
}

// LaserProjectile, TargetLaser, Lightning and RepairEffect (4.7).
public sealed class BeamVisual : Visual
{
    private ClientDataBlock? mBlock;
    private int mGroup;
    private double mStart;
    private Random mRng = new(1);
    private bool mHitShown;

    public override void Init(SceneModel m, ClientGhost g)
    {
        base.Init(m, g);
        mGroup = Class switch { "LaserProjectile" => 17, "TargetLaser" => 24, "Lightning" => 28, _ => 29 };
        mStart = m.Time;
        mRng = new Random(g.Index * 7919 + 13);
    }

    public override void OnUpdate(DecodedGhost u, bool isNew)
    {
        if (u.Fields.Any(f => f.Name == "datablock") || mBlock == null)
            mBlock = M.Db.Block(mGroup, (int)Num("datablock", -1));
        if (isNew)
            mStart = M.Time;
        if (u.Fields.Any(f => f.Name == "hit") && Num("hit") != 0 && !mHitShown && Class is "LaserProjectile" or "TargetLaser")
        {
            mHitShown = true;
            string hit = Db.Str(mBlock, Class == "LaserProjectile" ? "hitName" : "hitShapeName");
            if (hit.EndsWith(".dts", StringComparison.OrdinalIgnoreCase))
                hit = hit[..^4];
            RenderShape? sh = hit.Length > 0 ? M.Assets.Shape(hit) : null;
            if (sh == null && Class == "TargetLaser")
                sh = M.Assets.Shape("paint");
            if (sh == null && Class == "LaserProjectile")
                sh = M.Assets.Shape("laserhit");
            if (sh != null)
            {
                double[] e = Vec("end");
                M.Effects.Add(new ExplosionEffect(sh, null, e[0], e[1], e[2], 0, 0, 1, M.Rng));
            }
        }
    }

    private (double[] a, double[] b)? Endpoints()
    {
        if (Class is "LaserProjectile" or "TargetLaser")
        {
            double[] a;
            int sg = (int)Num("shooterGhost", -1);
            if (sg >= 0 && M.Visuals.TryGetValue(sg, out Visual? sv) && sv.MuzzlePoint is var (x, y, z))
                a = new[] { x, y, z };
            else if (Has("start"))
                a = Vec("start");
            else
                return null;
            return (a, Vec("end"));
        }
        // Lightning, Repair: shooter muzzle to the target ghost position + offset
        int shooter = (int)Num("shooterGhost", -1), target = (int)Num("targetGhost", -1);
        if (shooter < 0 || !M.Visuals.TryGetValue(shooter, out Visual? sh) || sh.MuzzlePoint is not var (mx, my, mz))
            return null;
        double[] end;
        if (target >= 0 && M.Visuals.TryGetValue(target, out Visual? tv) && tv.Position is var (tx, ty, tz))
        {
            double[] off = Vec("offset");
            end = new[] { tx + off[0], ty + off[1], tz + off[2] + 1.0 };
        }
        else
            return null;
        return (new[] { mx, my, mz }, end);
    }

    public PointLight? Light
    {
        get
        {
            double range = Db.Num(mBlock, "lightRange");
            if (range <= 0 || Endpoints() is not var (_, b))
                return null;
            return new PointLight(b[0], b[1], b[2], (float)Db.Num(mBlock, "lightColor.r"), (float)Db.Num(mBlock, "lightColor.g"), (float)Db.Num(mBlock, "lightColor.b"), range);
        }
    }

    public override void Build(FrameContext ctx)
    {
        if (Endpoints() is not var (a, b))
            return;
        RenderImage img;
        if (Class == "LaserProjectile")
        {
            TexEntry? tex = ctx.Assets.Texture(Db.Str(mBlock, "laserBitmapName") is { Length: > 0 } n ? n : "laserPulse.bmp");
            float strength = (float)(Num("energy5", 31) / 31.0);
            double age = M.Time - mStart;
            float fade = (float)Math.Clamp(1 - age / Math.Max(0.5, Db.Num(mBlock, "beamTime", 0.5)), 0.2, 1);
            img = Beams.Strip(ctx, tex, new[] { a, b }, 0.35 * (0.4 + 0.6 * strength), 1, 1, 1, fade, Math.Max(1, Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2])) / 8), "laser");
        }
        else if (Class == "TargetLaser")
        {
            TexEntry? tex = ctx.Assets.Texture(Db.Str(mBlock, "laserBitmapName") is { Length: > 0 } n ? n : "paintPulse.bmp");
            img = Beams.Strip(ctx, tex, new[] { a, b }, 0.12, 1, 1, 1, 0.9f, 8, "targetlaser");
        }
        else
        {
            // Lightning and Repair: numSegments bolts of 2^n + 1 points, regenerated every updateTime ms and blended
            // linearly between the old and the new point set.
            int bolts = Class == "Lightning" ? Math.Max(1, (int)Db.Num(mBlock, "numSegments", 5)) : 1;
            int div = (int)Db.Num(mBlock, "segmentDivisions", 4);
            double bias = Db.Num(mBlock, "displaceBias", 0.15), width = Db.Num(mBlock, "beamWidth", 0.125);
            double interval = Math.Max(Db.Num(mBlock, "updateTime", 120), 16) * 0.001;
            string bmp = Db.Str(mBlock, "bitmapName");
            TexEntry? tex = ctx.Assets.Texture(bmp.Length > 0 ? bmp : Class == "Lightning" ? "lightningNew.bmp" : "repairadd.bmp");
            img = new RenderImage { Category = 1, Label = Class };
            int step = (int)Math.Floor((M.Time - mStart) / interval);
            double t = ((M.Time - mStart) / interval) - step;
            for (int k = 0; k < bolts; ++k)
            {
                var r0 = new Random(unchecked(step * 31 + k * 977 + Index));
                var r1 = new Random(unchecked((step + 1) * 31 + k * 977 + Index));
                double[][] p0 = Beams.BoltPoints(a, b, div, bias, r0), p1 = Beams.BoltPoints(a, b, div, bias, r1);
                double[][] pts = Beams.Lerp(p0, p1, t);
                float cr = Class == "Lightning" ? 0.7f : 0.85f, cg = Class == "Lightning" ? 0.8f : 0.25f, cb = Class == "Lightning" ? 1f : 0.25f;
                RenderImage one = Beams.Strip(ctx, tex, pts, width * (Class == "Lightning" ? 3 : 4), cr, cg, cb, 1, 2, Class);
                img.Batches.AddRange(one.Batches);
            }
        }
        var (cx, cy, cz) = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2);
        img.SortKey = RenderLists.SortKey(Math.Max(ctx.Camera.ToCamera(cx, cy, cz).y, ctx.Camera.Near), ctx.Camera.Near);
        ctx.Lists.Add(img);
    }
}
