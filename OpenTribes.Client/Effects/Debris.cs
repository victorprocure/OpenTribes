// Debris (docs/shapes-render.md 4.4): the pieces of a dying shape fly with local physics. A piece is one object of the
// shape (PartDebris, made by ShapeBase_explode for armors and structures); the numbers are random on the stock client too.
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.World;

namespace OpenTribes.Client.Effects;

public sealed class DebrisPiece
{
    public int Object;
    public double Cx, Cy, Cz;        // world position of the piece centre at spawn
    public double Dx, Dy, Dz;        // displacement
    public double Vx, Vy, Vz;
    public double Ax, Ay, Az, Wx, Wy, Wz;   // spin angles and rates
}

public sealed class DebrisEffect : IEffect
{
    private readonly RenderInstance mInst;
    private readonly Xf mBase;
    private readonly List<DebrisPiece> mPieces = new();
    private readonly Terrain? mTerrain;
    private readonly double mGravity, mElasticity, mFriction;
    private double mAge;
    public double Lifetime { get; }

    public bool Alive => mAge < Lifetime;
    public PointLight? Light => null;
    public int PieceCount => mPieces.Count;

    // 4.4 Dying shapes: the detail whose root node is named `debris0` if the shape has one, else detail 0; one piece per
    // visible object. `baseXf` is the pose of the dying shape, `v*` its velocity.
    public DebrisEffect(RenderInstance inst, Xf baseXf, double vx, double vy, double vz, Terrain? terrain, Random rng,
                        double minTimeout = 3, double maxTimeout = 6, double gravity = -20, double elasticity = 0.25, double friction = 0.5)
    {
        mInst = inst;
        mBase = baseXf;
        mTerrain = terrain;
        mGravity = gravity;
        mElasticity = elasticity;
        mFriction = friction;
        Lifetime = minTimeout + (maxTimeout - minTimeout) * rng.NextDouble();
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
            });
        }
    }

    public int Detail { get; }

    public void Tick(double dt)
    {
        mAge += dt;
        foreach (DebrisPiece p in mPieces)
        {
            p.Vz += mGravity * dt;
            p.Dx += p.Vx * dt; p.Dy += p.Vy * dt; p.Dz += p.Vz * dt;
            p.Ax += p.Wx * dt; p.Ay += p.Wy * dt; p.Az += p.Wz * dt;
            float h = 0;
            if (mTerrain != null && mTerrain.Height((float)(p.Cx + p.Dx), (float)(p.Cy + p.Dy), ref h) && p.Cz + p.Dz < h + 0.1)
            {
                p.Dz = h + 0.1 - p.Cz;
                if (p.Vz < 0)
                    p.Vz = -p.Vz * mElasticity;
                p.Vx *= mFriction + 0.2; p.Vy *= mFriction + 0.2;
                p.Wx *= 0.5; p.Wy *= 0.5; p.Wz *= 0.5;
            }
        }
    }

    public void Build(FrameContext ctx)
    {
        if (!Alive)
            return;
        double fade = mAge > Lifetime - 0.7 ? Math.Clamp((mAge - (Lifetime - 0.7)) / 0.7, 0, 1) : 0;
        foreach (DebrisPiece p in mPieces)
        {
            Xf xf = mBase.Then(Xf.Translate(-p.Cx, -p.Cy, -p.Cz)).Then(Xf.Euler(p.Ax, p.Ay, p.Az)).Then(Xf.Translate(p.Cx + p.Dx, p.Cy + p.Dy, p.Cz + p.Dz));
            ShapeDraw d = ctx.Draw(xf, mInst.Shape.Name + " debris");
            d.ForceDetail = Detail;
            d.ObjectFilter = new HashSet<int> { p.Object };
            d.Fade = fade;
            RenderImage? img = ctx.Renderer.Build(mInst, ctx.Camera, d);
            if (img != null)
                ctx.Lists.Add(img);
        }
    }
}
