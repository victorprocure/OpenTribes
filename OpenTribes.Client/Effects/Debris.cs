namespace OpenTribes.Client.Effects;

using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Game;
using OpenTribes.World;

/// <summary>The DebrisData fields the pieces use (docs/shapes-render.md 4.4).</summary>
/// <param name="MinTimeout">The shortest life in seconds.</param>
/// <param name="MaxTimeout">The longest life in seconds.</param>
/// <param name="ExplodeOnBounce">The chance of an explosion at a fresh bounce.</param>
/// <param name="ExplodeOnRest">True for an explosion when the piece stands still.</param>
/// <param name="Explosion">The ExplosionData index of every explosion, -1 for none (<c>spawnedExplosionID</c>).</param>
/// <param name="Elasticity">The bounce elasticity.</param>
/// <param name="Friction">The friction.</param>
/// <param name="Mass">The mass.</param>
/// <param name="SpawnMask">The mask Debris_explode hands to the secondary DebrisCloud, which matches the rows with it (0x80).</param>
/// <param name="SpawnStrength">The strength the secondary cloud gets (0x84).</param>
/// <param name="SpawnRadius">The radius the secondary cloud gets (0x88).</param>
/// <param name="CollisionDetail">The collision detail.</param>
/// <param name="Damage">The self damage the piece starts with.</param>
/// <param name="DamageThreshold">The damage threshold.</param>
public readonly record struct DebrisParams(double MinTimeout = 3, double MaxTimeout = 6, double ExplodeOnBounce = 0.3, bool ExplodeOnRest = true, int Explosion = -1,
                                           double Elasticity = 0.25, double Friction = 0.5, double Mass = 100, int SpawnMask = 1, double SpawnStrength = 50,
                                           double SpawnRadius = 0.2, int CollisionDetail = 0, double Damage = 100, double DamageThreshold = 1)
{
    /// <summary>The defaults of a piece without a DebrisData block.</summary>
    public static DebrisParams Default => new(3, 6, 0.3, true, -1, 0.25, 0.5, 100, 1, 50, 0.2, 0, 100, 1);

    /// <summary>Reads the parameters from a DebrisData block.</summary>
    /// <param name="b">The block, or null for the defaults.</param>
    /// <returns>The parameters.</returns>
    public static DebrisParams From(ClientDataBlock? b) =>
        b == null ? Default : new DebrisParams(Db.Num(b, "minTimeout", 3), Db.Num(b, "maxTimeout", 6), Db.Num(b, "explodeOnBounce", 0.3),
            Db.Flag(b, "explodeOnRest"), (int)Db.Num(b, "spawnedExplosionID", -1), Db.Num(b, "elasticity", 0.25), Db.Num(b, "friction", 0.5),
            Db.Num(b, "mass", 100), (int)Db.Num(b, "spawnedDebrisMask", 1), Db.Num(b, "spawnedDebrisStrength", 50),
            Db.Num(b, "spawnedDebrisRadius", 0.2), (int)Db.Num(b, "collisionDetail", 0), Db.Num(b, "damage", 100),
            Db.Num(b, "damageThreshold", 1));
}

/// <summary>A ray against the static world: the first interior surface along (sx, sy, sz) + t * (dx, dy, dz), 0 &lt;= t &lt;= len.</summary>
/// <param name="sx">The start x.</param>
/// <param name="sy">The start y.</param>
/// <param name="sz">The start z.</param>
/// <param name="dx">The direction x.</param>
/// <param name="dy">The direction y.</param>
/// <param name="dz">The direction z.</param>
/// <param name="len">The ray length.</param>
/// <returns>Whether a surface was hit, the distance t and its unit normal facing the ray origin.</returns>
public delegate (bool Hit, double T, double Nx, double Ny, double Nz) DebrisRay(double sx, double sy, double sz, double dx, double dy, double dz, double len);

/// <summary>What Debris_explode (00472954) hands to DebrisCloud_ctorParams (00474268) when the mask of the piece is set.</summary>
/// <param name="Mask">The cloud mask.</param>
/// <param name="X">The piece position x.</param>
/// <param name="Y">The piece position y.</param>
/// <param name="Z">The piece position z.</param>
/// <param name="Vx">The piece velocity x.</param>
/// <param name="Vy">The piece velocity y.</param>
/// <param name="Vz">The piece velocity z.</param>
/// <param name="Budget">The cloud budget: the piece mass times 0.9.</param>
/// <param name="Strength">The strength of the DebrisData.</param>
/// <param name="Radius">The radius of the DebrisData.</param>
/// <param name="Remaining">The piece budget that is left (+0x344) for the pieces the cloud makes.</param>
public readonly record struct DebrisCloudSpec(int Mask, double X, double Y, double Z, double Vx, double Vy, double Vz, double Budget, double Strength, double Radius, double Remaining);

/// <summary>One flying piece and the Debris_tick state of it.</summary>
public sealed class DebrisBody
{
    /// <summary>The SimMovement time slice 0x3d088889 (SimMovement_ctor), which Debris never changes.</summary>
    public const double TimeSlice = 0.033333335;
    /// <summary>The gravity.</summary>
    public const double Gravity = -20;

    /// <summary>The position of the piece origin (the centre of its box).</summary>
    public double X;
    /// <summary>The y position.</summary>
    public double Y;
    /// <summary>The z position.</summary>
    public double Z;
    /// <summary>The linear velocity x.</summary>
    public double Vx;
    /// <summary>The linear velocity y.</summary>
    public double Vy;
    /// <summary>The linear velocity z.</summary>
    public double Vz;
    /// <summary>The spin about x.</summary>
    public double Wx;
    /// <summary>The spin about y.</summary>
    public double Wy;
    /// <summary>The spin about z.</summary>
    public double Wz;
    /// <summary>The accumulated spin angle about x.</summary>
    public double Ax;
    /// <summary>The accumulated spin angle about y.</summary>
    public double Ay;
    /// <summary>The accumulated spin angle about z.</summary>
    public double Az;
    /// <summary>The seconds lived.</summary>
    public double Age;
    /// <summary>The lifetime: minTimeout + (maxTimeout - minTimeout) * random.</summary>
    public double Life;
    /// <summary>The collision sphere radius (Debris +0x318), from PartDebris loadShape (0047603c), see <see cref="PartDebris.Measure"/>.</summary>
    public double Radius = 0.25;
    /// <summary>The mass.</summary>
    public double Mass = 100;
    /// <summary>The self damage left (+0x348, DebrisData <c>damage</c>). Below zero the piece explodes.</summary>
    public double Damage = double.PositiveInfinity;
    /// <summary>True when the piece collided in the last tick.</summary>
    public bool Collided;
    /// <summary>True when the piece collided in the tick before.</summary>
    public bool PrevCollided;
    /// <summary>True when the piece lies still.</summary>
    public bool Frozen;
    /// <summary>True when the piece ended.</summary>
    public bool Done;
    private double mSvx, mSvy, mSvz, mSwx, mSwy, mSwz;   // the smoothed velocities +0x358..+0x36c (0.9 old + 0.1 new, z velocity x 0.25)

    /// <summary>Decides whether the piece explodes now.</summary>
    /// <param name="p">The debris parameters.</param>
    /// <param name="rng">The random source.</param>
    /// <returns>True for an explosion.</returns>
    /// <remarks>Follows the top of Debris_tick (004721f8): the explosion decisions from the collision flags of the last two ticks. An explosion follows a bounce (a contact now and none in the tick before) with the chance explodeOnBounce, two ticks of contact in a row, and a piece whose horizontal velocity components are both at most 1e-5 when explodeOnRest is set.</remarks>
    public bool WantsExplosion(DebrisParams p, Random rng)
    {
        var explode = false;
        if (Collided && !PrevCollided && rng.NextDouble() < p.ExplodeOnBounce)
            explode = true;
        if (!explode && (Damage < 0 || (Collided && PrevCollided)))
            explode = true;
        PrevCollided = Collided;
        Collided = false;
        if (Math.Abs(Vx) <= 1e-5 && Math.Abs(Vy) <= 1e-5 && p.ExplodeOnRest)
            explode = true;
        return explode;
    }

    /// <summary>Integrates one time slice.</summary>
    /// <param name="p">The debris parameters.</param>
    /// <param name="terrain">The terrain, or null.</param>
    /// <param name="ray">The ray against the interiors, or null.</param>
    /// <param name="rng">The random source.</param>
    /// <param name="offX">The x offset of the sphere centre from the origin.</param>
    /// <param name="offY">The y offset of the sphere centre from the origin.</param>
    /// <param name="offZ">The z offset of the sphere centre from the origin.</param>
    /// <returns>True when the lifetime is over.</returns>
    /// <remarks>Follows the body of Debris_tick: SimMovement_integrate (00505460: gravity, the position moves by the mean of the old and the new velocity), collide against the interiors (<paramref name="ray"/>, from the sphere centre) and the terrain, bounce, smooth, freeze at rest.</remarks>
    public bool Integrate(DebrisParams p, Terrain? terrain, DebrisRay? ray, Random rng, double offX, double offY, double offZ)
    {
        Age += TimeSlice;
        double v0x = Vx, v0y = Vy, v0z = Vz;
        Vz += Gravity * TimeSlice;
        double dx = (v0x + Vx) * 0.5 * TimeSlice, dy = (v0y + Vy) * 0.5 * TimeSlice, dz = (v0z + Vz) * 0.5 * TimeSlice;
        Ax += Wx * TimeSlice;
        Ay += Wy * TimeSlice;
        Az += Wz * TimeSlice;
        if (Frozen)
        {
            // Debris_collide 00472ad0, the frozen branch (flag +0x164 & 0x80): velocity zero, the transform stays.
            Vx = Vy = Vz = Wx = Wy = Wz = 0;
            Collided = true;
        }
        else
        {
            double nx = X + dx, ny = Y + dy, nz = Z + dz;
            var hit = false;
            var len = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (ray != null && len > 1e-9)
            {
                double ux = dx / len, uy = dy / len, uz = dz / len;
                var (ok, _, hx, hy, hz) = ray(X + offX, Y + offY, Z + offZ, ux, uy, uz, len + Radius);
                if (ok)
                {
                    // Debris_collide 00472ad0 tests the sphere at the new position against the surface and only changes the velocity: the piece goes on to the
                    // integrated position (Debris_tick sets the transform from the integrate result, and Debris_explode reads it at +0xbc). A ray from the old centre
                    // stands in for the plane distance test: contact when the surface is within the movement plus the radius.
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
                    var tl = Math.Sqrt((tx * tx) + (ty * ty) + (tz * tz));
                    (tx, ty, tz) = (tx / tl, ty / tl, tz / tl);
                }
                hit |= Bounce(p, tx, ty, tz, rng);
            }
            (X, Y, Z) = (nx, ny, nz);
            Collided = hit;
        }
        // the smoothed velocities and the freeze (Debris_tick: after the first second, in contact, both slow)
        mSvx = (mSvx * 0.9) + (Vx * 0.1);
        mSvy = (mSvy * 0.9) + (Vy * 0.1);
        mSvz = ((mSvz * 0.9) + (Vz * 0.1)) * 0.25;
        mSwx = (mSwx * 0.9) + (Wx * 0.1);
        mSwy = (mSwy * 0.9) + (Wy * 0.1);
        mSwz = (mSwz * 0.9) + (Wz * 0.1);
        if (Age > 1 && Collided && (mSvx * mSvx) + (mSvy * mSvy) + (mSvz * mSvz) < 0.1 && (mSwx * mSwx) + (mSwy * mSwy) + (mSwz * mSwz) < 0.1)
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
        var vn = (Vx * nx) + (Vy * ny) + (Vz * nz);
        // Debris_collide 00472ad0, only on the detailed collision path (collisionDetail not 0; every stock DebrisData has 0): the dot of velocity and surface
        // normal less damageThreshold (+0x7c), when positive, is taken off the self damage (+0x348) at 004731cb.
        if (p.CollisionDetail != 0 && vn - p.DamageThreshold > 0)
            Damage -= vn - p.DamageThreshold;
        if (vn > 0)
            return false;
        Vx -= vn * nx;
        Vy -= vn * ny;
        Vz -= vn * nz;
        (var tx, var ty, var tz) = (Vx, Vy, Vz);
        double f = -vn * p.Friction, tl = Math.Sqrt((tx * tx) + (ty * ty) + (tz * tz));
        if (f < tl)
            (tx, ty, tz) = (tx * f / tl, ty * f / tl, tz * f / tl);
        Vx -= tx;
        Vy -= ty;
        Vz -= tz;
        Vx -= p.Elasticity * vn * nx;
        Vy -= p.Elasticity * vn * ny;
        Vz -= p.Elasticity * vn * nz;
        var m = (Math.Abs(Wx) + Math.Abs(Wy) + Math.Abs(Wz)) * 0.5;   // 1/3 x sum x 1.5
        Wx = (rng.NextDouble() * 2 * m) - m;
        Wy = (rng.NextDouble() * 2 * m) - m;
        Wz = (rng.NextDouble() * 2 * m) - m;
        return true;
    }

    /// <summary>Makes the secondary cloud of the piece.</summary>
    /// <param name="p">The debris parameters.</param>
    /// <param name="pieceBudget">The piece budget, the constructor argument.</param>
    /// <returns>The cloud, or null when the budget is used up.</returns>
    /// <remarks>Follows Debris_explode (00472954): the mask is spawnedDebrisMask, the budget 0.9 x mass, strength and radius come from the DebrisData. The cloud is made while the piece budget (+0x344: the constructor argument minus the seconds lived) is above zero.</remarks>
    public DebrisCloudSpec? SecondaryCloud(DebrisParams p, double pieceBudget)
    {
        if (p.SpawnMask == 0 || pieceBudget <= 0)
            return null;
        return new DebrisCloudSpec(p.SpawnMask, X, Y, Z, Vx, Vy, Vz, Mass * 0.9, p.SpawnStrength, p.SpawnRadius, pieceBudget);
    }
}

/// <summary>The pieces of a dying shape: the PartDebrisCloud of ShapeBase_explode (0044cdec).</summary>
public sealed class DebrisEffect : IEffect
{
    /// <summary>The burst strength ShapeBase_explode passes (0x44fa0000). The cloud applies the impulse of that strength along (piece centre - shape centre) (00476858), so the velocity of a piece changes by strength / mass.</summary>
    public const double BurstStrength = 2000;
    /// <summary>The piece mass (PartDebris 00475e98 stores 0x437a0000).</summary>
    public const double PieceMass = 250;
    /// <summary>The piece budget ShapeBase_explode passes (0x461c4000). Debris_explode counts the seconds lived off it.</summary>
    public const double PieceBudget = 10000;

    private sealed class Piece
    {
        public DebrisBody Body = new();
        public int Object;
        public double Cx, Cy, Cz;        // world centre of the piece at spawn (where it rotates)
    }

    private readonly RenderInstance mInst;
    private readonly Xf mBase;
    private readonly List<Piece> mPieces = [];
    private readonly Terrain? mTerrain;
    private readonly DebrisParams mParams;
    private readonly Random mRng;
    private readonly DebrisRay? mRay;
    private readonly Action<int, double, double, double>? mExplode;
    private readonly Action<DebrisCloudSpec>? mCloud;
    private double mAcc;

    /// <inheritdoc/>
    public bool Alive => mPieces.Exists(p => !p.Body.Done);
    /// <inheritdoc/>
    public PointLight? Light => null;
    /// <summary>The number of pieces.</summary>
    public int PieceCount => mPieces.Count;
    /// <summary>The bodies of the pieces.</summary>
    public IEnumerable<DebrisBody> Bodies => mPieces.Select(p => p.Body);

    /// <summary>Creates the effect with one piece per visible object of the dying shape.</summary>
    /// <param name="inst">The shape instance of the dying shape.</param>
    /// <param name="baseXf">The pose of the dying shape.</param>
    /// <param name="vx">The velocity x of the shape.</param>
    /// <param name="vy">The velocity y of the shape.</param>
    /// <param name="vz">The velocity z of the shape.</param>
    /// <param name="terrain">The terrain.</param>
    /// <param name="rng">The random source.</param>
    /// <param name="param">The debris parameters, or null for the defaults.</param>
    /// <param name="ray">The ray against the static world (interiors; the terrain is read from <paramref name="terrain"/>), or null.</param>
    /// <param name="explode">Spawns the ExplosionData of the piece where it ends (Debris_explode 00472954 deletes the piece).</param>
    /// <param name="cloud">Gets the secondary DebrisCloud of a piece that ends.</param>
    /// <remarks>docs/shapes-render.md 4.4 "Dying shapes": the detail whose root node is named <c>debris0</c> if the shape has one, else detail 0.</remarks>
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
        var debrisNode = sh.FindNode("debris0");
        var detail = 0;
        if (debrisNode >= 0)
            for (var d = 0; d < sh.Details.Length; ++d)
                if (sh.Details[d].RootNode == debrisNode)
                    detail = d;
        inst.Animate(detail);
        Detail = detail;
        // the shape centre in the world: the point the burst moves the pieces away from (ShapeBase_explode 0044cdec, two m_mul_point)
        var (scx, scy, scz) = baseXf.Apply(sh.Cx, sh.Cy, sh.Cz);
        var nodes = new HashSet<int>(sh.Subtree(sh.DetailRoot(detail)));
        for (var oi = 0; oi < sh.Objects.Length; ++oi)
        {
            RsObject ob = sh.Objects[oi];
            if (!nodes.Contains(ob.Node) || ob.Mesh < 0 || ob.Mesh >= sh.Meshes.Length || sh.Meshes[ob.Mesh].Faces.Length == 0 || !inst.Objects[oi].Visible)
                continue;
            // the part box of 005c3b18 / 005ad14c at the object's current frame: the part origin moves to its centre (the middle of the frame box)
            RsMesh m = sh.Meshes[ob.Mesh];
            PartBox box = PartDebris.Measure(m, inst.Objects[oi].Frame);
            var hasBox = m.Frames.Length > 0 && m.Verts.Length >= (m.Frames[Math.Clamp(inst.Objects[oi].Frame, 0, m.Frames.Length - 1)].FirstVert + 2) * 4;
            double lx = box.Cx, ly = box.Cy, lz = box.Cz;
            var (sx, sy, sz) = inst.ObjectTransform(oi).Apply(lx, ly, lz);
            var (wx, wy, wz) = baseXf.Apply(sx, sy, sz);
            var piece = new Piece { Object = oi, Cx = wx, Cy = wy, Cz = wz };
            DebrisBody body = piece.Body;
            body.X = wx;
            body.Y = wy;
            body.Z = wz;
            body.Mass = PieceMass;
            // PartDebris loadShape 0047603c: radius +0x318 = 0.5 x m_Point3F_lenf (005ed031, the bit trick root) of the part's bounds size, sphere centre +0x30c = the
            // box centre = the piece origin (PartDebris.Measure); a mesh without a box (never in a stock shape) keeps a small sphere
            body.Radius = hasBox ? box.Radius : 0.17;
            // Debris_init: lifetime = minTimeout + (maxTimeout - minTimeout) * random
            body.Life = mParams.MinTimeout + ((mParams.MaxTimeout - mParams.MinTimeout) * rng.NextDouble());
            body.Damage = mParams.Damage;   // Debris_init: +0x348 = DebrisData damage (+0x78)
            // the cloud launch 00476858: the object velocity, plus the impulse along (piece - shape centre), plus a spin of +-strength * 0.0025 (at most 2.5) per axis
            double dx = wx - scx, dy = wy - scy, dz = wz - scz, dl = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            var k = dl > 1e-9 ? BurstStrength / PieceMass / dl : 0;
            body.Vx = vx + (dx * k);
            body.Vy = vy + (dy * k);
            body.Vz = vz + (dz * k);
            var spin = Math.Min(BurstStrength * 0.005, 5);
            body.Wx = (rng.NextDouble() * spin) - (spin * 0.5);
            body.Wy = (rng.NextDouble() * spin) - (spin * 0.5);
            body.Wz = (rng.NextDouble() * spin) - (spin * 0.5);
            mPieces.Add(piece);
        }
    }

    /// <summary>The detail of the shape that the pieces come from.</summary>
    public int Detail { get; }

    /// <inheritdoc/>
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
            var explode = b.WantsExplosion(mParams, mRng);
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

    /// <inheritdoc/>
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
            d.ObjectFilter = [p.Object];
            RenderImage? img = ctx.Renderer.Build(mInst, ctx.Camera, d);
            if (img != null)
                ctx.Lists.Add(img);
        }
    }
}
