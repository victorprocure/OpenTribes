// Smoke puff pool (docs/shapes-render.md 4.5): a ring of camera facing one-shot shapes ($pref::maxNumSmokePuffs, default
// 100). A puff plays `Sequence01` once and dies when it ends.
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;

namespace OpenTribes.Client.Effects;

public sealed class SmokePool
{
    private sealed class Puff
    {
        public RenderInstance Inst = null!;
        public RenderThread Thread = null!;
        public double X, Y, Z, Scale;
        public bool Active;
    }

    private readonly Puff[] mRing;
    private int mNext;
    public int Capacity => mRing.Length;

    public SmokePool(int capacity = 100)
    {
        mRing = new Puff[Math.Max(capacity, 1)];
        for (int i = 0; i < mRing.Length; ++i)
            mRing[i] = new Puff();
    }

    public int Live => mRing.Count(p => p.Active);

    // FUN_0045b4a4: takes the next ring slot (evicting the oldest live puff) and plays Sequence01 once.
    public void Spawn(RenderShape shape, double x, double y, double z, double scale = 1)
    {
        Puff p = mRing[mNext];
        mNext = (mNext + 1) % mRing.Length;
        int seq = shape.FindSequence("Sequence01");
        if (seq < 0)
            seq = shape.Sequences.Length > 0 ? 0 : -1;
        p.Inst = new RenderInstance(shape);
        p.Thread = p.Inst.AddThread(seq, 0, 0);
        (p.X, p.Y, p.Z, p.Scale, p.Active) = (x, y, z, scale, true);
    }

    // FUN_0045b6d4: removes every puff inside the radius (a projectile exploded).
    public int RemoveInside(double x, double y, double z, double radius)
    {
        int n = 0;
        foreach (Puff p in mRing)
        {
            if (!p.Active)
                continue;
            double dx = p.X - x, dy = p.Y - y, dz = p.Z - z;
            if (dx * dx + dy * dy + dz * dz <= radius * radius)
            {
                p.Active = false;
                ++n;
            }
        }
        return n;
    }

    public void Tick(double dt)
    {
        foreach (Puff p in mRing)
        {
            if (!p.Active)
                continue;
            p.Inst.Advance(dt);
            if (p.Thread.AtEnd(p.Inst.Shape))
                p.Active = false;
        }
    }

    public void Build(FrameContext ctx)
    {
        foreach (Puff p in mRing)
        {
            if (!p.Active)
                continue;
            double fx = ctx.Camera.X - p.X, fy = ctx.Camera.Y - p.Y, fz = ctx.Camera.Z - p.Z;
            double l = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            if (l < 1e-6)
                continue;
            Xf xf = Mathx.Basis(p.X, p.Y, p.Z, fx / l, fy / l, fz / l);
            if (Math.Abs(p.Scale - 1) > 1e-9)
                xf = new Xf { M0 = p.Scale, M4 = p.Scale, M8 = p.Scale }.Then(xf);
            ShapeDraw d = ctx.Draw(xf, p.Inst.Shape.Name);
            d.ForceTranslucent = true;
            d.Light = new LightSet { AmbR = 1, AmbG = 1, AmbB = 1, SunR = 0, SunG = 0, SunB = 0 };
            RenderImage? img = ctx.Renderer.Build(p.Inst, ctx.Camera, d);
            if (img != null)
                ctx.Lists.Add(img);
        }
    }
}
