// Debris (docs/shapes-render.md 4.4): the pieces of a dying shape fly with local physics. A piece is one object of the
// shape (PartDebris, made by ShapeBase_explode for armors and structures); the numbers are random on the stock client too.
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Game;
using OpenTribes.World;

namespace OpenTribes.Client.Effects;

public sealed class DebrisPiece
{
    public int Object;
    public double Cx, Cy, Cz;        // world position of the piece centre at spawn
    public double Dx, Dy, Dz;        // displacement
    public double Vx, Vy, Vz;
    public double Ax, Ay, Az, Wx, Wy, Wz;   // spin angles and rates
    public double Age, Life;                // seconds lived, lifetime (minTimeout + (maxTimeout - minTimeout) * random)
    public bool Bounced, Done;
}

// The DebrisData fields the pieces use (docs/shapes-render.md 4.4): `explodeOnBounce` is the chance of an explosion at a fresh bounce, `explodeOnRest` an explosion
// when the piece comes to rest, `spawnedExplosionID` the ExplosionData index (-1 none) of every explosion.
public readonly record struct DebrisParams(double MinTimeout = 3, double MaxTimeout = 6, double ExplodeOnBounce = 0.3, bool ExplodeOnRest = true, int Explosion = -1,
                                           double Elasticity = 0.25, double Friction = 0.5)
{
    public static DebrisParams Default => new(3, 6, 0.3, true, -1, 0.25, 0.5);

    public static DebrisParams From(ClientDataBlock? b) =>
        b == null ? Default : new DebrisParams(Scene.Db.Num(b, "minTimeout", 3), Scene.Db.Num(b, "maxTimeout", 6), Scene.Db.Num(b, "explodeOnBounce", 0.3),
            Scene.Db.Flag(b, "explodeOnRest"), (int)Scene.Db.Num(b, "spawnedExplosionID", -1), Scene.Db.Num(b, "elasticity", 0.25), Scene.Db.Num(b, "friction", 0.5));
}

public sealed class DebrisEffect : IEffect
{
    private readonly RenderInstance mInst;
    private readonly Xf mBase;
    private readonly List<DebrisPiece> mPieces = new();
    private readonly Terrain? mTerrain;
    private readonly double mGravity, mElasticity, mFriction;
    private readonly DebrisParams mParams;
    private readonly Random mRng;
    private readonly Func<double, double, double, double, double?>? mFloor;
    private readonly Action<int, double, double, double>? mExplode;
    private double mAge;

    public bool Alive => mPieces.Exists(p => !p.Done);
    public PointLight? Light => null;
    public int PieceCount => mPieces.Count;

    // 4.4 Dying shapes: the detail whose root node is named `debris0` if the shape has one, else detail 0; one piece per
    // visible object. `baseXf` is the pose of the dying shape, `v*` its velocity.
    // `floor(x, y, z, length)` is the height of the nearest interior surface below (x, y, z) within `length`, null when none (the terrain is read
    // from `terrain`); `explode(index, x, y, z)` spawns the piece's ExplosionData where the piece ends (Debris_explode 00472954 deletes the piece).
    public DebrisEffect(RenderInstance inst, Xf baseXf, double vx, double vy, double vz, Terrain? terrain, Random rng,
                        DebrisParams? param = null, Func<double, double, double, double, double?>? floor = null, Action<int, double, double, double>? explode = null,
                        double gravity = -20)
    {
        mInst = inst;
        mBase = baseXf;
        mTerrain = terrain;
        mGravity = gravity;
        mParams = param ?? DebrisParams.Default;
        mElasticity = mParams.Elasticity;
        mFriction = mParams.Friction;
        mRng = rng;
        mFloor = floor;
        mExplode = explode;
        RenderShape sh = inst.Shape;
        int debrisNode = sh.FindNode("debris0");
        int detail = 0;
        if (debrisNode >= 0)
            for (int d = 0; d < sh.Details.Length; ++d)
                if (sh.Details[d].RootNode == debrisNode)
                    detail = d;
        inst.Animate(detail);
        Detail = detail;
        var nodes = new HashSet<int>(sh.Subtree(sh.DetailRoot(detail)));
        for (int oi = 0; oi < sh.Objects.Length; ++oi)
        {
            RsObject ob = sh.Objects[oi];
            if (!nodes.Contains(ob.Node) || ob.Mesh < 0 || ob.Mesh >= sh.Meshes.Length || sh.Meshes[ob.Mesh].Faces.Length == 0 || !inst.Objects[oi].Visible)
                continue;
            // centre of the frame 0 box (decoded vertices 0 and 1) in shape space
            RsMesh m = sh.Meshes[ob.Mesh];
            RsFrame f = m.Frames.Length > 0 ? m.Frames[0] : default;
            double lx = 0, ly = 0, lz = 0;
            if (m.Verts.Length >= (f.FirstVert + 2) * 4)
            {
                int a = f.FirstVert * 4, b = a + 4;
                lx = (m.Verts[a] + m.Verts[b]) * 0.5 * f.Sx + f.Ox;
                ly = (m.Verts[a + 1] + m.Verts[b + 1]) * 0.5 * f.Sy + f.Oy;
                lz = (m.Verts[a + 2] + m.Verts[b + 2]) * 0.5 * f.Sz + f.Oz;
            }
            var (sx, sy, sz) = inst.ObjectTransform(oi).Apply(lx, ly, lz);
            var (wx, wy, wz) = baseXf.Apply(sx, sy, sz);
            double ang = rng.NextDouble() * Math.PI * 2, el = rng.NextDouble() * 1.2 - 0.2, sp = 5 + rng.NextDouble() * 9;
            mPieces.Add(new DebrisPiece
            {
                Object = oi, Cx = wx, Cy = wy, Cz = wz,
                Vx = vx * 0.5 + Math.Cos(ang) * Math.Cos(el) * sp, Vy = vy * 0.5 + Math.Sin(ang) * Math.Cos(el) * sp, Vz = vz * 0.5 + Math.Sin(el) * sp + 4,
                Wx = (rng.NextDouble() - 0.5) * 12, Wy = (rng.NextDouble() - 0.5) * 12, Wz = (rng.NextDouble() - 0.5) * 12,
                Life = mParams.MinTimeout + (mParams.MaxTimeout - mParams.MinTimeout) * rng.NextDouble(),
            });
        }
    }

    public int Detail { get; }

    public void Tick(double dt)
    {
        mAge += dt;
        foreach (DebrisPiece p in mPieces)
        {
            if (p.Done)
                continue;
            p.Age += dt;
            p.Vz += mGravity * dt;
            p.Dx += p.Vx * dt; p.Dy += p.Vy * dt; p.Dz += p.Vz * dt;
            p.Ax += p.Wx * dt; p.Ay += p.Wy * dt; p.Az += p.Wz * dt;
            double x = p.Cx + p.Dx, y = p.Cy + p.Dy, z = p.Cz + p.Dz;
            double? floor = null;
            float h = 0;
            if (mTerrain != null && mTerrain.Height((float)x, (float)y, ref h))
                floor = h;
            if (mFloor?.Invoke(x, y, z + 0.5, 0.6 + Math.Max(0, -p.Vz * dt)) is double fi && (floor == null || fi > floor))
                floor = fi;
            bool bounce = floor is double f && z < f + 0.1;
            bool fresh = bounce && !p.Bounced;
            if (bounce)
            {
                p.Dz = floor!.Value + 0.1 - p.Cz;
                if (p.Vz < 0)
                    p.Vz = -p.Vz * mElasticity;
                p.Vx *= mFriction + 0.2; p.Vy *= mFriction + 0.2;
                p.Wx *= 0.5; p.Wy *= 0.5; p.Wz *= 0.5;
            }
            p.Bounced = bounce;
            // Debris_tick 004721f8: an explosion on a fresh bounce with the chance `explodeOnBounce`, at rest (a second after the spawn, bounced, both
            // velocities under 0.1) when `explodeOnRest`, and when the lifetime is over.
            bool rest = bounce && p.Age > 1 && p.Vx * p.Vx + p.Vy * p.Vy + p.Vz * p.Vz < 0.01;
            if (rest)
                (p.Vx, p.Vy, p.Vz) = (0, 0, 0);
            if ((fresh && mRng.NextDouble() < mParams.ExplodeOnBounce) || (rest && mParams.ExplodeOnRest) || p.Age >= p.Life)
            {
                p.Done = true;
                if (mParams.Explosion >= 0)
                    mExplode?.Invoke(mParams.Explosion, p.Cx + p.Dx, p.Cy + p.Dy, p.Cz + p.Dz);
            }
        }
    }

    public void Build(FrameContext ctx)
    {
        if (!Alive)
            return;
        foreach (DebrisPiece p in mPieces)
        {
            if (p.Done)
                continue;
            Xf xf = mBase.Then(Xf.Translate(-p.Cx, -p.Cy, -p.Cz)).Then(Xf.Euler(p.Ax, p.Ay, p.Az)).Then(Xf.Translate(p.Cx + p.Dx, p.Cy + p.Dy, p.Cz + p.Dz));
            ShapeDraw d = ctx.Draw(xf, mInst.Shape.Name + " debris");
            d.ForceDetail = Detail;
            d.ObjectFilter = new HashSet<int> { p.Object };
            RenderImage? img = ctx.Renderer.Build(mInst, ctx.Camera, d);
            if (img != null)
                ctx.Lists.Add(img);
        }
    }
}
