// Debris (docs/shapes-render.md 4.4): the pieces of a dying shape fly with local physics. A piece is one object of the shape (PartDebris 00475e98, made by
// ShapeBase_explode 0044cdec through a PartDebrisCloud 00476434 for armors and structures). Launch: the cloud's spawn 00476858. Flight and end: Debris_tick
// 004721f8, Debris_collide 00472ad0, Debris_bounce 00473500, Debris_explode 00472954. What is random on the stock client stays random here.
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Game;
using OpenTribes.World;

namespace OpenTribes.Client.Effects;

// The DebrisData fields the pieces use (docs/shapes-render.md 4.4): `explodeOnBounce` is the chance of an explosion at a fresh bounce, `explodeOnRest` an explosion
// when the piece stands still, `spawnedExplosionID` the ExplosionData index (-1 none) of every explosion. `Type` (0x34), `SpawnMask` (0x80), `SpawnStrength` (0x84)
// and `SpawnRadius` (0x88) are what Debris_explode hands to the secondary DebrisCloud and what the cloud matches the rows with.
public readonly record struct DebrisParams(double MinTimeout = 3, double MaxTimeout = 6, double ExplodeOnBounce = 0.3, bool ExplodeOnRest = true, int Explosion = -1,
                                           double Elasticity = 0.25, double Friction = 0.5, double Mass = 100, int SpawnMask = 1, double SpawnStrength = 50,
                                           double SpawnRadius = 0.2)
{
    public static DebrisParams Default => new(3, 6, 0.3, true, -1, 0.25, 0.5, 100, 1, 50, 0.2);

    public static DebrisParams From(ClientDataBlock? b) =>
        b == null ? Default : new DebrisParams(Scene.Db.Num(b, "minTimeout", 3), Scene.Db.Num(b, "maxTimeout", 6), Scene.Db.Num(b, "explodeOnBounce", 0.3),
            Scene.Db.Flag(b, "explodeOnRest"), (int)Scene.Db.Num(b, "spawnedExplosionID", -1), Scene.Db.Num(b, "elasticity", 0.25), Scene.Db.Num(b, "friction", 0.5),
            Scene.Db.Num(b, "mass", 100), (int)Scene.Db.Num(b, "spawnedDebrisMask", 1), Scene.Db.Num(b, "spawnedDebrisStrength", 50),
            Scene.Db.Num(b, "spawnedDebrisRadius", 0.2));
}

// A ray against the static world: the first interior surface along (sx, sy, sz) + t * (dx, dy, dz), 0 <= t <= len, and its unit normal facing the ray origin.
public delegate (bool Hit, double T, double Nx, double Ny, double Nz) DebrisRay(double sx, double sy, double sz, double dx, double dy, double dz, double len);

// What Debris_explode (00472954) hands to DebrisCloud_ctorParams (00474268) when the piece's mask is set: the piece position and velocity, the piece mass times 0.9
// as the cloud budget, the strength and radius of the DebrisData and the piece budget that is left (+0x344) for the pieces it makes.
public readonly record struct DebrisCloudSpec(int Mask, double X, double Y, double Z, double Vx, double Vy, double Vz, double Budget, double Strength, double Radius, double Remaining);

// One flying piece and the Debris_tick state of it.
public sealed class DebrisBody
{
    public const double TimeSlice = 0.033333335;   // the SimMovement time slice 0x3d088889 (SimMovement_ctor), which Debris never changes
    public const double Gravity = -20;

    public double X, Y, Z;               // position of the piece origin (the centre of its box)
    public double Vx, Vy, Vz, Wx, Wy, Wz;
    public double Ax, Ay, Az;            // accumulated spin angles
    public double Age, Life;             // seconds lived, lifetime (minTimeout + (maxTimeout - minTimeout) * random)
    public double Radius = 0.25;         // collision sphere radius (Debris +0x318)
    public double Mass = 100;
    public bool Collided, PrevCollided, Frozen, Done;
    private double mSvx, mSvy, mSvz, mSwx, mSwy, mSwz;   // the smoothed velocities +0x358..+0x36c (0.9 old + 0.1 new, z velocity x 0.25)

    // Debris_tick 004721f8, the top: explosion decisions from the collision flags of the last two ticks. An explosion follows a bounce (a contact now and none in the
    // tick before) with the chance explodeOnBounce, two ticks of contact in a row, and a piece whose horizontal velocity components are both at most 1e-5 when
    // explodeOnRest is set.
    public bool WantsExplosion(DebrisParams p, Random rng)
    {
        bool explode = false;
        if (Collided && !PrevCollided && rng.NextDouble() < p.ExplodeOnBounce)
            explode = true;
        if (!explode && Collided && PrevCollided)
            explode = true;
        PrevCollided = Collided;
        Collided = false;
        if (Math.Abs(Vx) <= 1e-5 && Math.Abs(Vy) <= 1e-5 && p.ExplodeOnRest)
            explode = true;
        return explode;
    }

    // Debris_tick, the body: integrate one time slice (SimMovement_integrate 00505460: gravity, the position moves by the mean of the old and the new velocity),
    // collide against the interiors (`ray`, from the sphere centre `off` from the origin) and the terrain, bounce, smooth, freeze at rest. True when the lifetime is over.
    public bool Integrate(DebrisParams p, Terrain? terrain, DebrisRay? ray, Random rng, double offX, double offY, double offZ)
    {
        Age += TimeSlice;
        double v0x = Vx, v0y = Vy, v0z = Vz;
        Vz += Gravity * TimeSlice;
        double dx = (v0x + Vx) * 0.5 * TimeSlice, dy = (v0y + Vy) * 0.5 * TimeSlice, dz = (v0z + Vz) * 0.5 * TimeSlice;
        Ax += Wx * TimeSlice; Ay += Wy * TimeSlice; Az += Wz * TimeSlice;
        if (Frozen)
        {
            // Debris_collide 00472ad0, the frozen branch (flag +0x164 & 0x80): velocity zero, the transform stays.
            Vx = Vy = Vz = Wx = Wy = Wz = 0;
            Collided = true;
        }
        else
        {
            double nx = X + dx, ny = Y + dy, nz = Z + dz;
            bool hit = false;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (ray != null && len > 1e-9)
            {
                double ux = dx / len, uy = dy / len, uz = dz / len;
                var (ok, t, hx, hy, hz) = ray(X + offX, Y + offY, Z + offZ, ux, uy, uz, len + Radius);
                if (ok)
                {
                    // the sphere touches the plane when the centre is `Radius` from it along the normal
                    double cosine = Math.Max(0.2, -(ux * hx + uy * hy + uz * hz));
                    double s = Math.Clamp(t - Radius / cosine, 0, len);
                    (nx, ny, nz) = (X + ux * s, Y + uy * s, Z + uz * s);
                    hit |= Bounce(p, hx, hy, hz, rng);
                }
            }
            float h = 0;
            if (terrain != null && terrain.Height((float)(nx + offX), (float)(ny + offY), ref h) && nz + offZ - Radius < h)
            {
                double tx = 0, ty = 0, tz = 1;
                float hxp = 0, hxm = 0, hyp = 0, hym = 0;
                if (terrain.Height((float)(nx + offX + 0.5), (float)(ny + offY), ref hxp) && terrain.Height((float)(nx + offX - 0.5), (float)(ny + offY), ref hxm) &&
                    terrain.Height((float)(nx + offX), (float)(ny + offY + 0.5), ref hyp) && terrain.Height((float)(nx + offX), (float)(ny + offY - 0.5), ref hym))
                {
                    (tx, ty, tz) = (hxm - hxp, hym - hyp, 1);
                    double tl = Math.Sqrt(tx * tx + ty * ty + tz * tz);
                    (tx, ty, tz) = (tx / tl, ty / tl, tz / tl);
                }
                nz = h + Radius - offZ;
                hit |= Bounce(p, tx, ty, tz, rng);
            }
            (X, Y, Z) = (nx, ny, nz);
            Collided = hit;
        }
        // the smoothed velocities and the freeze (Debris_tick: after the first second, in contact, both slow)
        mSvx = mSvx * 0.9 + Vx * 0.1; mSvy = mSvy * 0.9 + Vy * 0.1; mSvz = (mSvz * 0.9 + Vz * 0.1) * 0.25;
        mSwx = mSwx * 0.9 + Wx * 0.1; mSwy = mSwy * 0.9 + Wy * 0.1; mSwz = mSwz * 0.9 + Wz * 0.1;
        if (Age > 1 && Collided && mSvx * mSvx + mSvy * mSvy + mSvz * mSvz < 0.1 && mSwx * mSwx + mSwy * mSwy + mSwz * mSwz < 0.1)
        {
            Vx = Vy = Vz = Wx = Wy = Wz = 0;
            Frozen = true;
        }
        return Age > Life;
    }

    // Debris_bounce 00473500 (collisionDetail 0): the normal part of the velocity goes, the sideways part loses friction x the normal speed, the normal speed
    // comes back times elasticity, and the spin is drawn again at random within half the sum of the old spin components. False when the piece moves away.
    private bool Bounce(DebrisParams p, double nx, double ny, double nz, Random rng)
    {
        double vn = Vx * nx + Vy * ny + Vz * nz;
        if (vn > 0)
            return false;
        Vx -= vn * nx; Vy -= vn * ny; Vz -= vn * nz;
        (double tx, double ty, double tz) = (Vx, Vy, Vz);
        double f = -vn * p.Friction, tl = Math.Sqrt(tx * tx + ty * ty + tz * tz);
        if (f < tl)
            (tx, ty, tz) = (tx * f / tl, ty * f / tl, tz * f / tl);
        Vx -= tx; Vy -= ty; Vz -= tz;
        Vx -= p.Elasticity * vn * nx; Vy -= p.Elasticity * vn * ny; Vz -= p.Elasticity * vn * nz;
        double m = (Math.Abs(Wx) + Math.Abs(Wy) + Math.Abs(Wz)) * 0.5;   // 1/3 x sum x 1.5
        Wx = rng.NextDouble() * 2 * m - m;
        Wy = rng.NextDouble() * 2 * m - m;
        Wz = rng.NextDouble() * 2 * m - m;
        return true;
    }

    // Debris_explode 00472954: the secondary cloud of the piece (mask spawnedDebrisMask, budget 0.9 x mass, strength and radius of the DebrisData), made while
    // the piece budget (+0x344: the constructor argument minus the seconds lived) is above zero.
    public DebrisCloudSpec? SecondaryCloud(DebrisParams p, double pieceBudget)
    {
        if (p.SpawnMask == 0 || pieceBudget <= 0)
            return null;
        return new DebrisCloudSpec(p.SpawnMask, X, Y, Z, Vx, Vy, Vz, Mass * 0.9, p.SpawnStrength, p.SpawnRadius, pieceBudget);
    }
}

// The pieces of a dying shape: the PartDebrisCloud of ShapeBase_explode (0044cdec).
public sealed class DebrisEffect : IEffect
{
    // ShapeBase_explode passes the burst strength 2000 (0x44fa0000) and the piece mass 250 (PartDebris 00475e98 stores 0x437a0000); the cloud applies the
    // impulse of that strength along (piece centre - shape centre) 00476858, so the velocity of a piece changes by strength / mass.
    public const double BurstStrength = 2000, PieceMass = 250;
    // The piece budget ShapeBase_explode passes (0x461c4000); Debris_explode counts the seconds lived off it.
    public const double PieceBudget = 10000;

    private sealed class Piece
    {
        public DebrisBody Body = new();
        public int Object;
        public double Cx, Cy, Cz;        // world centre of the piece at spawn (where it rotates)
    }

    private readonly RenderInstance mInst;
    private readonly Xf mBase;
    private readonly List<Piece> mPieces = new();
    private readonly Terrain? mTerrain;
    private readonly DebrisParams mParams;
    private readonly Random mRng;
    private readonly DebrisRay? mRay;
    private readonly Action<int, double, double, double>? mExplode;
    private readonly Action<DebrisCloudSpec>? mCloud;
    private double mAcc;

    public bool Alive => mPieces.Exists(p => !p.Body.Done);
    public PointLight? Light => null;
    public int PieceCount => mPieces.Count;
    public IEnumerable<DebrisBody> Bodies => mPieces.Select(p => p.Body);

    // 4.4 Dying shapes: the detail whose root node is named `debris0` if the shape has one, else detail 0; one piece per visible object. `baseXf` is the pose of the
    // dying shape, `v*` its velocity. `ray(...)` is the static world (interiors; the terrain is read from `terrain`); `explode(index, x, y, z)` spawns the piece's
    // ExplosionData where the piece ends (Debris_explode 00472954 deletes the piece); `cloud` gets the secondary DebrisCloud of a piece that ends.
    public DebrisEffect(RenderInstance inst, Xf baseXf, double vx, double vy, double vz, Terrain? terrain, Random rng,
                        DebrisParams? param = null, DebrisRay? ray = null, Action<int, double, double, double>? explode = null, Action<DebrisCloudSpec>? cloud = null)
    {
        mInst = inst;
        mBase = baseXf;
        mTerrain = terrain;
        mParams = param ?? DebrisParams.Default;
        mRng = rng;
        mRay = ray;
        mExplode = explode;
        mCloud = cloud;
        RenderShape sh = inst.Shape;
        int debrisNode = sh.FindNode("debris0");
        int detail = 0;
        if (debrisNode >= 0)
            for (int d = 0; d < sh.Details.Length; ++d)
                if (sh.Details[d].RootNode == debrisNode)
                    detail = d;
        inst.Animate(detail);
        Detail = detail;
        // the shape centre in the world: the point the burst moves the pieces away from (ShapeBase_explode 0044cdec, two m_mul_point)
        var (scx, scy, scz) = baseXf.Apply(sh.Cx, sh.Cy, sh.Cz);
        var nodes = new HashSet<int>(sh.Subtree(sh.DetailRoot(detail)));
        for (int oi = 0; oi < sh.Objects.Length; ++oi)
        {
            RsObject ob = sh.Objects[oi];
            if (!nodes.Contains(ob.Node) || ob.Mesh < 0 || ob.Mesh >= sh.Meshes.Length || sh.Meshes[ob.Mesh].Faces.Length == 0 || !inst.Objects[oi].Visible)
                continue;
            // centre of the frame 0 box (decoded vertices 0 and 1) in shape space: the part constructor 005c3b18 moves the part origin there
            RsMesh m = sh.Meshes[ob.Mesh];
            RsFrame f = m.Frames.Length > 0 ? m.Frames[0] : default;
            double lx = 0, ly = 0, lz = 0, ex = 0.1, ey = 0.1, ez = 0.1;
            if (m.Verts.Length >= (f.FirstVert + 2) * 4)
            {
                int a = f.FirstVert * 4, b = a + 4;
                lx = (m.Verts[a] + m.Verts[b]) * 0.5 * f.Sx + f.Ox;
                ly = (m.Verts[a + 1] + m.Verts[b + 1]) * 0.5 * f.Sy + f.Oy;
                lz = (m.Verts[a + 2] + m.Verts[b + 2]) * 0.5 * f.Sz + f.Oz;
                ex = Math.Abs(m.Verts[b] - m.Verts[a]) * 0.5 * f.Sx;
                ey = Math.Abs(m.Verts[b + 1] - m.Verts[a + 1]) * 0.5 * f.Sy;
                ez = Math.Abs(m.Verts[b + 2] - m.Verts[a + 2]) * 0.5 * f.Sz;
            }
            var (sx, sy, sz) = inst.ObjectTransform(oi).Apply(lx, ly, lz);
            var (wx, wy, wz) = baseXf.Apply(sx, sy, sz);
            var piece = new Piece { Object = oi, Cx = wx, Cy = wy, Cz = wz };
            DebrisBody body = piece.Body;
            body.X = wx; body.Y = wy; body.Z = wz;
            body.Mass = PieceMass;
            body.Radius = Math.Max(0.1, Math.Sqrt(ex * ex + ey * ey + ez * ez));
            // Debris_init: lifetime = minTimeout + (maxTimeout - minTimeout) * random
            body.Life = mParams.MinTimeout + (mParams.MaxTimeout - mParams.MinTimeout) * rng.NextDouble();
            // the cloud launch 00476858: the object velocity, plus the impulse along (piece - shape centre), plus a spin of +-strength * 0.0025 (at most 2.5) per axis
            double dx = wx - scx, dy = wy - scy, dz = wz - scz, dl = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            double k = dl > 1e-9 ? BurstStrength / PieceMass / dl : 0;
            body.Vx = vx + dx * k; body.Vy = vy + dy * k; body.Vz = vz + dz * k;
            double spin = Math.Min(BurstStrength * 0.005, 5);
            body.Wx = rng.NextDouble() * spin - spin * 0.5;
            body.Wy = rng.NextDouble() * spin - spin * 0.5;
            body.Wz = rng.NextDouble() * spin - spin * 0.5;
            mPieces.Add(piece);
        }
    }

    public int Detail { get; }

    public void Tick(double dt)
    {
        mAcc += dt;
        while (mAcc >= DebrisBody.TimeSlice)
        {
            mAcc -= DebrisBody.TimeSlice;
            Step();
        }
    }

    // One Debris_tick (004721f8) of every piece.
    private void Step()
    {
        foreach (Piece p in mPieces)
        {
            DebrisBody b = p.Body;
            if (b.Done)
                continue;
            bool explode = b.WantsExplosion(mParams, mRng);
            if (!explode)
                explode = b.Integrate(mParams, mTerrain, mRay, mRng, 0, 0, 0);
            if (!explode)
                continue;
            b.Done = true;
            // Debris_explode: the secondary cloud first, then the Explosion of spawnedExplosionID at the piece position
            if (b.SecondaryCloud(mParams, PieceBudget - b.Age) is { } spec)
                mCloud?.Invoke(spec);
            if (mParams.Explosion >= 0)
                mExplode?.Invoke(mParams.Explosion, b.X, b.Y, b.Z);
        }
    }

    public void Build(FrameContext ctx)
    {
        if (!Alive)
            return;
        foreach (Piece p in mPieces)
        {
            DebrisBody b = p.Body;
            if (b.Done)
                continue;
            Xf xf = mBase.Then(Xf.Translate(-p.Cx, -p.Cy, -p.Cz)).Then(Xf.Euler(b.Ax, b.Ay, b.Az)).Then(Xf.Translate(b.X, b.Y, b.Z));
            ShapeDraw d = ctx.Draw(xf, mInst.Shape.Name + " debris");
            d.ForceDetail = Detail;
            d.ObjectFilter = new HashSet<int> { p.Object };
            RenderImage? img = ctx.Renderer.Build(mInst, ctx.Camera, d);
            if (img != null)
                ctx.Lists.Add(img);
        }
    }
}
