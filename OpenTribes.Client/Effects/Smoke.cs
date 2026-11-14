namespace OpenTribes.Client.Effects;

using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;

/// <summary>The smoke puff pool: a ring of camera facing one shot shapes (<c>$pref::maxNumSmokePuffs</c>, default 100). A puff plays <c>Sequence01</c> once and dies when it ends.</summary>
/// <remarks>docs/shapes-render.md 4.5.</remarks>
public sealed class SmokePool
{
    private sealed class Puff
    {
        public RenderInstance Inst { get; set; } = null!;
        public RenderThread Thread { get; set; } = null!;
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double Scale { get; set; }
        public double Vx { get; set; }
        public double Vy { get; set; }
        public double Vz { get; set; }
        public double Age { get; set; }
        public double Life { get; set; }
        public double Roll { get; set; }
        public bool Active { get; set; }
    }

    private readonly Puff[] _ring;
    private int _next;
    private readonly Random _roll = new(0x8377);
    /// <summary>The size of the ring.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Creates the pool.</summary>
    /// <param name="capacity">The size of the ring.</param>
    public SmokePool(int capacity = 100)
    {
        _ring = new Puff[Math.Max(capacity, 1)];
        for (var i = 0; i < _ring.Length; ++i)
            _ring[i] = new Puff();
    }

    /// <summary>The number of live puffs.</summary>
    public int Live => _ring.Count(p => p.Active);

    /// <summary>Starts a puff in the next ring slot, evicting the oldest live puff.</summary>
    /// <param name="shape">The puff shape.</param>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <param name="z">The z position.</param>
    /// <param name="scale">The life in seconds.</param>
    /// <param name="vx">The drift velocity x.</param>
    /// <param name="vy">The drift velocity y.</param>
    /// <param name="vz">The drift velocity z.</param>
    /// <remarks>Follows FUN_0045b4a4: takes the next ring slot and plays Sequence01 once. The puff keeps drifting with the velocity and is removed <paramref name="scale"/> seconds after the spawn (+0x04 deadline = ftol(scale * 1000) + now, FUN_0045b070 reads it), whichever of that and the sequence end comes first. The roll about the view axis is random (+0x98, 0 to 2 pi).</remarks>
    public void Spawn(RenderShape shape, double x, double y, double z, double scale = 1, double vx = 0, double vy = 0, double vz = 0)
    {
        var p = _ring[_next];
        _next = (_next + 1) % _ring.Length;
        var seq = shape.FindSequence("Sequence01");
        if (seq < 0)
            seq = shape.Sequences.Length > 0 ? 0 : -1;
        p.Inst = new RenderInstance(shape);
        p.Thread = p.Inst.AddThread(seq, 0, 0);
        (p.X, p.Y, p.Z, p.Scale, p.Active) = (x, y, z, scale, true);
        (p.Vx, p.Vy, p.Vz, p.Age, p.Life, p.Roll) = (vx, vy, vz, 0, scale, _roll.NextDouble() * 2 * Math.PI);
    }

    /// <summary>Removes every puff inside a radius, because a projectile exploded (FUN_0045b6d4).</summary>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <param name="z">The z position.</param>
    /// <param name="radius">The radius.</param>
    /// <returns>The number of puffs removed.</returns>
    public int RemoveInside(double x, double y, double z, double radius)
    {
        var n = 0;
        foreach (var p in _ring)
        {
            if (!p.Active)
                continue;
            double dx = p.X - x, dy = p.Y - y, dz = p.Z - z;
            if ((dx * dx) + (dy * dy) + (dz * dz) <= radius * radius)
            {
                p.Active = false;
                ++n;
            }
        }
        return n;
    }

    /// <summary>Advances the live puffs.</summary>
    /// <param name="dt">The time step in seconds.</param>
    public void Tick(double dt)
    {
        foreach (var p in _ring)
        {
            if (!p.Active)
                continue;
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                p.Active = false;
                continue;
            }
            (p.X, p.Y, p.Z) = (p.X + (p.Vx * dt), p.Y + (p.Vy * dt), p.Z + (p.Vz * dt));
            p.Inst.Advance(dt);
            if (p.Thread.AtEnd(p.Inst.Shape))
                p.Active = false;
        }
    }

    /// <summary>Adds the render images of the live puffs to the frame.</summary>
    /// <param name="ctx">The frame context.</param>
    public void Build(FrameContext ctx)
    {
        foreach (var p in _ring)
        {
            if (!p.Active)
                continue;
            double fx = ctx.Camera.X - p.X, fy = ctx.Camera.Y - p.Y, fz = ctx.Camera.Z - p.Z;
            var l = Math.Sqrt((fx * fx) + (fy * fy) + (fz * fz));
            if (l < 1e-6)
                continue;
            // +Y is the view ray, camera to puff (the same basis as the explosion images), rolled about it by the puff's random angle.
            var xf = Mathx.SpinY(p.Roll).Then(Mathx.Basis(p.X, p.Y, p.Z, -fx / l, -fy / l, -fz / l));
            if (Math.Abs(p.Scale - 1) > 1e-9)
                xf = new Xf { M0 = p.Scale, M4 = p.Scale, M8 = p.Scale }.Then(xf);
            var d = ctx.Draw(xf, p.Inst.Shape.Name);
            d.ForceTranslucent = true;
            d.Light = new LightSet { AmbR = 1, AmbG = 1, AmbB = 1, SunR = 0, SunG = 0, SunB = 0 };
            var img = ctx.Renderer.Build(p.Inst, ctx.Camera, d);
            if (img != null)
                ctx.Lists.Add(img);
        }
    }
}
