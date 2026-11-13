namespace OpenTribes.Client.Scene;

using OpenTribes.Client.Effects;
using OpenTribes.Client.Shapes;
using OpenTribes.Game;
using OpenTribes.Platform.Gfx;

/// <summary>The visual of a shape projectile (bullet, grenade, rocket, seeking missile) with trail and light.</summary>
/// <remarks>docs/shapes-render.md 4.5 and 4.7.</remarks>
public sealed class ProjectileVisual : Visual
{
    private int _group;
    private ClientDataBlock? _block;
    private RenderShape? _shape;
    private RenderInstance? _inst;
    private RenderThread? _ambient;
    private double _x, _y, _z, _vx, _vy, _vz;
    private double _t0, _update;
    private bool _havePos;
    private double _lastSmokeX, _lastSmokeY, _lastSmokeZ;
    private bool _smokeInit;
    private bool _trailInit;                    // trailType 1: the ribbon starts at the first render and draws from the second
    private double _tailX, _tailY, _tailZ;
    private double _spin;

    /// <inheritdoc/>
    public override (double x, double y, double z)? Position => _havePos ? PosNow() : null;
    /// <summary>True once the projectile exploded.</summary>
    public bool Exploded { get; private set; }

    private (double, double, double) PosNow()
    {
        var dt = Math.Clamp(M.Time - _update, 0, 5);
        double g = Class == "Grenade" ? -20 : 0;
        return (_x + (_vx * dt), _y + (_vy * dt), _z + (_vz * dt) + (0.5 * g * dt * dt));
    }

    /// <inheritdoc/>
    public override void Init(SceneModel m, ClientGhost g)
    {
        base.Init(m, g);
        _group = Class switch { "Bullet" => 14, "Grenade" => 15, "RocketDumb" => 16, "SeekingMissile" => 25, _ => 13 };
        _t0 = m.Time;
    }

    /// <inheritdoc/>
    public override void OnUpdate(DecodedGhost u, bool isNew)
    {
        bool has(string n) => u.Fields.Any(f => f.Name == n);
        if (has("datablock") || _block == null)
        {
            _block = M.Db.Block(_group, (int)Num("datablock", -1));
            var name = Db.Str(_block, "bulletShapeName");
            if (name.EndsWith(".dts", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            _shape = M.Assets.Shape(name);
            if (_shape != null)
            {
                _inst = new RenderInstance(_shape);
                var amb = _shape.FindSequence("ambient");
                if (amb >= 0)
                    _ambient = _inst.AddThread(amb, 0, 0);
            }
        }
        if (Class == "Bullet" && has("start"))
        {
            // Bullet initial data: start, elapsed ms, direction, speed (m/s = speed16 / 16)
            double[] s = Vec("start"), d = Vec("dir");
            double sp = Num("speed16") * 0.0625, age = Num("elapsedMs") * 0.001;
            (_vx, _vy, _vz) = (d[0] * sp, d[1] * sp, d[2] * sp);
            (_x, _y, _z) = (s[0] + (_vx * age), s[1] + (_vy * age), s[2] + (_vz * age));
            _update = M.Time;
            _havePos = true;
        }
        else if (has("pos"))
        {
            var p = Vec("pos");
            if (has("vel"))
            {
                var v = Vec("vel");
                var k = Class == "SeekingMissile" && isNew ? 0.5 : 1;
                (_vx, _vy, _vz) = (v[0] * k, v[1] * k, v[2] * k);
            }
            else if (Class == "SeekingMissile" && _havePos)
            {
                // the client flies the missile toward each correction: velocity = 0.15 x (new - old position)
                (_vx, _vy, _vz) = ((p[0] - _x) * 0.15 / 0.1, (p[1] - _y) * 0.15 / 0.1, (p[2] - _z) * 0.15 / 0.1);
            }
            else if (has("dir9"))
            {
                var n = Db.Vec(Ghost.State["dir9"]);
                var sp = Math.Sqrt((_vx * _vx) + (_vy * _vy) + (_vz * _vz));
                (_vx, _vy, _vz) = (n[0] * sp, n[1] * sp, n[2] * sp);
            }
            (_x, _y, _z) = (p[0], p[1], p[2]);
            _update = M.Time;
            _havePos = true;
        }
        if (has("explode") && !Exploded)
            Explode(has("hitPos"));
    }

    private void Explode(bool hasPos)
    {
        Exploded = true;
        double x, y, z;
        var n = Ghost.State.TryGetValue("hitNormal", out var hn) ? Db.Vec(hn) : [0.0, 0, 1];
        var explosion = (int)Db.Num(_block, "explosionTag", -1);
        if (Ghost.State.TryGetValue("explosion", out var ex))
            explosion = (int)Db.ToDouble(ex);
        if (Class == "Bullet")
        {
            // impact point = start + direction * speed * time
            var t = Num("timeMs") * 0.001;
            double[] s = Vec("start"), d = Vec("dir");
            var sp = Num("speed16") * 0.0625;
            (x, y, z) = (s[0] + (d[0] * sp * t), s[1] + (d[1] * sp * t), s[2] + (d[2] * sp * t));
        }
        else if (hasPos)
        {
            var h = Vec("hitPos");
            (x, y, z) = (h[0], h[1], h[2]);
        }
        else
            (x, y, z) = PosNow();
        M.SpawnExplosion(explosion, x, y, z, n[0], n[1], n[2]);
        var radius = Db.Num(_block, "explosionRadius", 3);
        M.Smoke.RemoveInside(x, y, z, Math.Max(radius, 2));
        var hole = (int)Db.Num(_block, "bulletHoleIndex", -1);
        if (Class == "Bullet" && hole >= 0)
            M.AddBulletHole(x, y, z, n[0], n[1], n[2]);
    }

    /// <summary>Tells whether the <c>ambient</c> thread of a projectile class advances.</summary>
    /// <param name="cls">The class name.</param>
    /// <returns>False for a grenade, true for the others.</returns>
    /// <remarks>The client update of Bullet (0045c1ec path), RocketDumb and SeekingMissile advances the thread (ShapeThread_advanceTime 005c01b0). Grenade_clientUpdate (0045fb7d to 0045ffd0, tribes111 bytes: no call to 005c01b0) never does, so the shell shows frame 0 of its blink (olite00, a white palette entry) for its whole flight.</remarks>
    public static bool AdvancesAmbient(string cls) => cls != "Grenade";

    /// <inheritdoc/>
    public override void Tick(double dt)
    {
        if (!_havePos || Exploded || (_inst == null && _block == null))
            return;
        _spin += dt;
        if (_inst != null && _ambient != null && AdvancesAmbient(Class))
            _inst.Advance(_ambient, dt);
        var (x, y, z) = PosNow();
        // Smoke: grenades puff `smokeName` and rockets of trailType 2 puff `trailString` every smokeDist units travelled.
        var smoke = Class == "Grenade" ? Db.Str(_block, "smokeName") : (int)Db.Num(_block, "trailType") == 2 ? Db.Str(_block, "trailString") : "";
        if (smoke.Length > 0)
        {
            bool puff;
            if (Class == "Grenade")
                puff = GrenadePuffDue(M.Time);
            else
            {
                var dist = Db.Num(_block, "smokeDist", 1.8);
                if (!_smokeInit)
                    (_lastSmokeX, _lastSmokeY, _lastSmokeZ, _smokeInit) = (x, y, z, true);
                double dx = x - _lastSmokeX, dy = y - _lastSmokeY, dz = z - _lastSmokeZ;
                puff = (dx * dx) + (dy * dy) + (dz * dz) >= dist * dist;
                if (puff)
                    (_lastSmokeX, _lastSmokeY, _lastSmokeZ) = (x, y, z);
            }
            if (puff)
            {
                if (smoke.EndsWith(".dts", StringComparison.OrdinalIgnoreCase))
                    smoke = smoke[..^4];
                var sh = M.Assets.Shape(smoke);
                if (sh != null)
                    M.Smoke.Spawn(sh, x, y, z, 1, 0, 0, 1);
            }
        }
    }

    private double _lastPuff = -1;
    private int _puffInterval;                  // ms

    // FUN_0045ffd0 (1.11 bytes 0045ffd0..0046010a): a puff when now > lastPuff + interval (ms, lastPuff starts at 0), then
    // interval = ftol(1000 * special * (1 + 2 r)), special = projSpecialTime (+0x7c), r = (S - |v|) / S with S the stored
    // maxLevelFlightDist (+0x68, sqrt(20 d)) and r < 0.1 taken as 0. The puff flies up at 1 m/s (velocity 0,0,1), scale 1.
    private bool GrenadePuffDue(double now)
    {
        var nowMs = (long)(now * 1000);
        if (nowMs <= (long)Math.Max(_lastPuff, 0) + _puffInterval)
            return false;
        _lastPuff = nowMs;
        var dt = Math.Clamp(M.Time - _update, 0, 5);
        var vz = _vz - (20 * dt);
        var speed = Math.Sqrt((_vx * _vx) + (_vy * _vy) + (vz * vz));
        double s = (float)Db.Num(_block, "field@0x68"), special = Db.Num(_block, "field@0x7c");
        var r = (s - speed) / s;
        if (!(r >= 0.1))
            r = 0;
        _puffInterval = SmokeInterval(special, r);
        return true;
    }

    /// <summary>Computes the smoke puff interval of a grenade.</summary>
    /// <param name="special">The datablock projSpecialTime (+0x7c).</param>
    /// <param name="r">The speed loss ratio.</param>
    /// <returns>The interval in milliseconds, ftol(1000 * special * (1 + 2 r)) (0045ffd0).</returns>
    public static int SmokeInterval(double special, double r) => (int)(special * (1 + (2 * r)) * 1000);

    /// <summary>The light the projectile casts, or null when it has none or exploded.</summary>
    public PointLight? Light
    {
        get
        {
            if (!_havePos || Exploded)
                return null;
            var range = Db.Num(_block, "lightRange");
            if (range <= 0)
                return null;
            var (x, y, z) = PosNow();
            return new PointLight(x, y, z, (float)Db.Num(_block, "lightColor.r"), (float)Db.Num(_block, "lightColor.g"), (float)Db.Num(_block, "lightColor.b"), range);
        }
    }

    /// <summary>Tells whether the image of a projectile class faces the camera.</summary>
    /// <param name="cls">The class name.</param>
    /// <returns>True for Bullet and SeekingMissile.</returns>
    /// <remarks>Orientation of the projectile image, FUN_0045a5b8 (render query, vtable 0061b558 path 0045a170). Bullet_onAdd (0045bd00, flag +0x4c bit 2 = ghost branch) and SeekingMissile_onAdd (004663c8, same branch) set the image flag +0x2d4 = 1 and switch the image to camera facing: FUN_0052a2a0 sets the axis to (position - camera) normalised, (0,1,0) when closer than 1e-3, and FUN_0052a3b4 builds the basis from it with FUN_00529e6c. Every other class keeps the Projectile_ctor default (+0x458 = 1): the axis is the normalised velocity (+0x194) through the same FUN_00529e6c basis, whose world up fallback for |axis.z| >= 0.95 is RocketBasis.</remarks>
    public static bool FacesCamera(string cls) => cls is "Bullet" or "SeekingMissile";

    /// <summary>Builds the orientation of the projectile image.</summary>
    /// <param name="cls">The class name.</param>
    /// <param name="x">The projectile x.</param>
    /// <param name="y">The projectile y.</param>
    /// <param name="z">The projectile z.</param>
    /// <param name="vx">The velocity x.</param>
    /// <param name="vy">The velocity y.</param>
    /// <param name="vz">The velocity z.</param>
    /// <param name="cx">The camera x.</param>
    /// <param name="cy">The camera y.</param>
    /// <param name="cz">The camera z.</param>
    /// <returns>The image basis at the projectile position.</returns>
    public static Xf ImageBasis(string cls, double x, double y, double z, double vx, double vy, double vz, double cx, double cy, double cz)
    {
        double ax, ay, az;
        if (FacesCamera(cls))
        {
            (ax, ay, az) = (x - cx, y - cy, z - cz);
            var l2 = (ax * ax) + (ay * ay) + (az * az);
            if (l2 * l2 >= 1e-12)
            {
                var inv = 1 / Math.Sqrt(l2);
                (ax, ay, az) = (ax * inv, ay * inv, az * inv);
            }
            else
                (ax, ay, az) = (0, 1, 0);
        }
        else
        {
            var sp = Math.Sqrt((vx * vx) + (vy * vy) + (vz * vz));
            (ax, ay, az) = sp > 1e-6 ? (vx / sp, vy / sp, vz / sp) : (0, 1, 0);
        }
        return Mathx.RocketBasis(x, y, z, ax, ay, az);
    }

    /// <inheritdoc/>
    public override void Build(FrameContext ctx)
    {
        if (!_havePos || Exploded)
            return;
        var (x, y, z) = PosNow();
        if (_inst != null && _shape != null && Db.Flag(_block, "isVisible"))
        {
            var xf = ImageBasis(Class, x, y, z, _vx, _vy, _vz - (Class == "Grenade" ? 20 * Math.Clamp(M.Time - _update, 0, 5) : 0),
                ctx.Camera.X, ctx.Camera.Y, ctx.Camera.Z);
            var period = Db.Num(_block, "rotationPeriod");
            if (period > 0)
                xf = Mathx.SpinY((M.Time - _t0) % period / period * Math.PI * 2).Then(xf);
            var d = ctx.Draw(xf, _shape.Name);
            d.NoHaze = true;
            d.Light = new LightSet { AmbR = 1, AmbG = 1, AmbB = 1, SunR = 0, SunG = 0, SunB = 0 };
            var img = ctx.Renderer.Build(_inst, ctx.Camera, d);
            if (img != null)
                ctx.Lists.Add(img);
        }
        if ((int)Db.Num(_block, "trailType") == 1)
            BuildTrail(ctx, x, y, z);
    }

    // 4.5 trailType 1 (RocketData `trailAdd2.bmp` ribbon): a quad from a tail point to the projectile. The tail stays where it was
    // until it is `trailLength` behind the projectile and then follows at that distance. The quad is `trailWidth` wide at the
    // projectile and a third of that at the tail, along the projectile's right axis (velocity cross world up), so it is a flat
    // horizontal ribbon and not camera facing. Additive, one winding facing the camera (the stock device culls the second draw), depth tested and written, v scrolls five times a second with the sim time.
    private void BuildTrail(FrameContext ctx, double x, double y, double z)
    {
        var sp = Math.Sqrt((_vx * _vx) + (_vy * _vy) + (_vz * _vz));
        double fx = sp > 1e-6 ? _vx / sp : 0, fy = sp > 1e-6 ? _vy / sp : 1, fz = sp > 1e-6 ? _vz / sp : 0;
        var axes = Class == "RocketDumb" ? Mathx.RocketBasis(x, y, z, fx, fy, fz) : Mathx.Basis(x, y, z, fx, fy, fz);
        double length = Db.Num(_block, "trailLength", 10), width = Db.Num(_block, "trailWidth", 0.3);
        if (!_trailInit)
        {
            (_tailX, _tailY, _tailZ, _trailInit) = (x, y, z, true);
            return;
        }
        double vx = _tailX - x, vy = _tailY - y, vz = _tailZ - z;
        var len = Math.Sqrt((vx * vx) + (vy * vy) + (vz * vz));
        if (len > length)
        {
            var k = length / len;
            (_tailX, _tailY, _tailZ) = (x + (vx * k), y + (vy * k), z + (vz * k));
        }
        var tex = ctx.Assets.Texture("trailAdd2.bmp");
        if (tex?.Texture == null)
            return;
        // v scrolls with the sim clock: the update gets the float at sim manager +0x80 (seconds) and the draw takes frac(5 t), v from f / 2 at the tail to (f + 1) / 2 at the projectile
        var t5 = (double)(float)M.Time * 5.0;
        var f = (float)(t5 - Math.Truncate(t5));
        double ht = width / 6, hh = width / 2;
        (double x, double y, double z) Off(double bx, double by, double bz, double h, int sign) =>
            (bx + (axes.M0 * h * sign), by + (axes.M1 * h * sign), bz + (axes.M2 * h * sign));
        var p0 = Off(_tailX, _tailY, _tailZ, ht, 1);
        var p1 = Off(_tailX, _tailY, _tailZ, ht, -1);
        var p2 = Off(x, y, z, hh, -1);
        var p3 = Off(x, y, z, hh, 1);
        Vertex3D V((double x, double y, double z) p, float u, float v) => new((float)p.x, (float)p.y, (float)p.z, u, v, 0, 0, 0);
        Vertex3D a0 = V(p0, 1, f * 0.5f), a1 = V(p1, 0, f * 0.5f), a2 = V(p2, 0, (f + 1) * 0.5f), a3 = V(p3, 1, (f + 1) * 0.5f);
        var img = new RenderImage { Category = 1, Label = "trail" };
        // 00465648 draws the quad twice, once per winding, and the device culls back faces (renderer.md 5.4): one winding shows from
        // either side, so the additive blend is applied once. Pick the winding that faces the camera.
        double nx = ((p1.y - p0.y) * (p2.z - p0.z)) - ((p1.z - p0.z) * (p2.y - p0.y)),
               ny = ((p1.z - p0.z) * (p2.x - p0.x)) - ((p1.x - p0.x) * (p2.z - p0.z)),
               nz = ((p1.x - p0.x) * (p2.y - p0.y)) - ((p1.y - p0.y) * (p2.x - p0.x));
        var front = (nx * (ctx.Camera.X - p0.x)) + (ny * (ctx.Camera.Y - p0.y)) + (nz * (ctx.Camera.Z - p0.z)) >= 0;
        img.Batches.Add(new DrawBatch
        {
            Vertices = front ? [a0, a1, a2, a0, a2, a3] : [a3, a2, a1, a3, a1, a0],
            State = new DrawState3D(tex.Texture, null, true, 0, true, true, BlendMode.Additive),
        });
        // the sort key follows the tail point (near / depth of the tail)
        img.SortKey = RenderLists.SortKey(ctx.Camera.ToCamera(_tailX, _tailY, _tailZ).y, ctx.Camera.Near);
        ctx.Lists.Add(img);
    }
}

/// <summary>The visual of the beam classes: LaserProjectile, TargetLaser, Lightning and RepairEffect (4.7).</summary>
public sealed class BeamVisual : Visual
{
    private ClientDataBlock? _block;
    private int _group;
    private double _start;
    private Random _rng = new(1);
    private bool _hitShown;

    /// <inheritdoc/>
    public override void Init(SceneModel m, ClientGhost g)
    {
        base.Init(m, g);
        _group = Class switch { "LaserProjectile" => 17, "TargetLaser" => 24, "Lightning" => 28, _ => 29 };
        _start = m.Time;
        _rng = new Random((g.Index * 7919) + 13);
    }

    /// <inheritdoc/>
    public override void OnUpdate(DecodedGhost u, bool isNew)
    {
        if (u.Fields.Any(f => f.Name == "datablock") || _block == null)
            _block = M.Db.Block(_group, (int)Num("datablock", -1));
        if (isNew)
            _start = M.Time;
        if (u.Fields.Any(f => f.Name == "hit") && Num("hit") != 0 && !_hitShown && Class is "LaserProjectile" or "TargetLaser")
        {
            _hitShown = true;
            var hit = Db.Str(_block, Class == "LaserProjectile" ? "hitName" : "hitShapeName");
            if (hit.EndsWith(".dts", StringComparison.OrdinalIgnoreCase))
                hit = hit[..^4];
            var sh = hit.Length > 0 ? M.Assets.Shape(hit) : null;
            if (sh == null && Class == "TargetLaser")
                sh = M.Assets.Shape("paint");
            if (sh == null && Class == "LaserProjectile")
                sh = M.Assets.Shape("laserhit");
            if (sh != null)
            {
                var e = Vec("end");
                if (Class == "LaserProjectile")   // 00463810: lives beamTime from the beam start, ambient thread over beamTime
                    M.Effects.Add(new ExplosionEffect(sh, e[0], e[1], e[2], Math.Max(Db.Num(_block, "beamTime", 0.5), 1e-3), M.Time - _start));
                else
                    M.Effects.Add(new ExplosionEffect(sh, null, e[0], e[1], e[2], 0, 0, 1, M.Rng));
            }
        }
    }

    private (double[] a, double[] b)? Endpoints()
    {
        if (Class is "LaserProjectile" or "TargetLaser")
        {
            double[] a;
            var sg = (int)Num("shooterGhost", -1);
            if (sg >= 0 && M.Visuals.TryGetValue(sg, out var sv) && sv.MuzzlePoint is var (x, y, z))
                a = [x, y, z];
            else if (Has("start"))
                a = Vec("start");
            else
                return null;
            return (a, Vec("end"));
        }
        // Lightning, Repair: shooter muzzle to the target ghost position + offset
        int shooter = (int)Num("shooterGhost", -1), target = (int)Num("targetGhost", -1);
        if (shooter < 0 || !M.Visuals.TryGetValue(shooter, out var sh) || sh.MuzzlePoint is not var (mx, my, mz))
            return null;
        double[] end;
        if (target >= 0 && M.Visuals.TryGetValue(target, out var tv) && tv.Position is var (tx, ty, tz))
        {
            var off = Vec("offset");
            end = [tx + off[0], ty + off[1], tz + off[2] + 1.0];
        }
        else
            return null;
        return (new[] { mx, my, mz }, end);
    }

    /// <summary>The light at the end point of the beam, or null when the datablock has none.</summary>
    public PointLight? Light
    {
        get
        {
            var range = Db.Num(_block, "lightRange");
            if (range <= 0 || Endpoints() is not var (_, b))
                return null;
            return new PointLight(b[0], b[1], b[2], (float)Db.Num(_block, "lightColor.r"), (float)Db.Num(_block, "lightColor.g"), (float)Db.Num(_block, "lightColor.b"), range);
        }
    }

    /// <inheritdoc/>
    public override void Build(FrameContext ctx)
    {
        if (Endpoints() is not var (a, b))
            return;
        RenderImage img;
        if (Class == "LaserProjectile")
        {
            var tex = ctx.Assets.Texture(Db.Str(_block, "laserBitmapName") is { Length: > 0 } n ? n : "laserPulse.bmp");
            var strength = (float)(Num("energy5", 31) / 31.0);
            double beamTime = Math.Max(Db.Num(_block, "beamTime", 0.5), 1e-3), age = M.Time - _start;
            if (age > beamTime)
                return;   // 00463810: no image after beamTime
            // u = (1 - strength) + age / beamTime * strength (inst +0x654)
            img = Beams.LaserQuad(ctx, tex, a, b, 1 - strength + (age / beamTime * strength));
        }
        else if (Class == "TargetLaser")
        {
            var tex = ctx.Assets.Texture(Db.Str(_block, "laserBitmapName") is { Length: > 0 } n ? n : "paintPulse.bmp");
            img = Beams.Strip(ctx, tex, new[] { a, b }, 0.12, 1, 1, 1, 0.9f, 8, "targetlaser");
        }
        else
        {
            // Lightning and Repair: numSegments bolts of 2^n + 1 points, regenerated every updateTime ms and blended
            // linearly between the old and the new point set.
            var bolts = Class == "Lightning" ? Math.Max(1, (int)Db.Num(_block, "numSegments", 5)) : 1;
            var div = (int)Db.Num(_block, "segmentDivisions", 4);
            double bias = Db.Num(_block, "displaceBias", 0.15), width = Db.Num(_block, "beamWidth", 0.125);
            var interval = Math.Max(Db.Num(_block, "updateTime", 120), 16) * 0.001;
            var bmp = Db.Str(_block, "bitmapName");
            var tex = ctx.Assets.Texture(bmp.Length > 0 ? bmp : Class == "Lightning" ? "lightningNew.bmp" : "repairadd.bmp");
            img = new RenderImage { Category = 1, Label = Class };
            var step = (int)Math.Floor((M.Time - _start) / interval);
            var t = ((M.Time - _start) / interval) - step;
            for (var k = 0; k < bolts; ++k)
            {
                var r0 = new Random(unchecked((step * 31) + (k * 977) + Index));
                var r1 = new Random(unchecked(((step + 1) * 31) + (k * 977) + Index));
                double[][] p0 = Beams.BoltPoints(a, b, div, bias, r0), p1 = Beams.BoltPoints(a, b, div, bias, r1);
                var pts = Beams.Lerp(p0, p1, t);
                float cr = Class == "Lightning" ? 0.7f : 0.85f, cg = Class == "Lightning" ? 0.8f : 0.25f, cb = Class == "Lightning" ? 1f : 0.25f;
                var one = Beams.Strip(ctx, tex, pts, width * (Class == "Lightning" ? 3 : 4), cr, cg, cb, 1, 2, Class);
                img.Batches.AddRange(one.Batches);
            }
        }
        var (cx, cy, cz) = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2);
        img.SortKey = RenderLists.SortKey(Math.Max(ctx.Camera.ToCamera(cx, cy, cz).y, ctx.Camera.Near), ctx.Camera.Near);
        ctx.Lists.Add(img);
    }
}
