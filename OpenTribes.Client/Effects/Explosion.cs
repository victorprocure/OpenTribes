using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Game;

namespace OpenTribes.Client.Effects;

/// <summary>The light curve of an explosion.</summary>
/// <param name="TimeScale">The time scale of the datablock.</param>
/// <param name="Duration">The duration of the explosion shape sequence in seconds.</param>
/// <param name="TimeZero">The first key time as a fraction of the life.</param>
/// <param name="TimeOne">The second key time as a fraction of the life.</param>
/// <param name="Colors">The light colour at the three key points.</param>
/// <param name="Rad">The light radius factor at the three key points.</param>
/// <param name="Range">The light range.</param>
public readonly record struct LightCurve(double TimeScale, double Duration, double TimeZero, double TimeOne, float[][] Colors, double[] Rad, double Range)
{
    /// <summary>The life in milliseconds: trunc(timeScale * duration * 1000) (4.3).</summary>
    public double LifeMs => Math.Truncate(TimeScale * Duration * 1000);
    /// <summary>The first key time in milliseconds, the same fraction of the life as <c>TimeZero</c>.</summary>
    public double TA => Math.Truncate(TimeScale * Duration * 1000 * TimeZero);
    /// <summary>The second key time in milliseconds, the same fraction of the life as <c>TimeOne</c>.</summary>
    public double TB => Math.Truncate(TimeScale * Duration * 1000 * TimeOne);

    /// <summary>Gives the light at a time after the start (4.3): phases 0 to tA, tA to tB and tB to life.</summary>
    /// <param name="e">The time since the start in milliseconds.</param>
    /// <returns>The colour and radius, or null after the life.</returns>
    /// <remarks>The radius of phase 2 is the continuous form rad[1] * g + rad[2] * (1 - g) (Q8 chooses it over the literal export).</remarks>
    public (float r, float g, float b, double radius)? At(double e)
    {
        double life = LifeMs, tA = TA, tB = TB;
        if (e < 0 || e >= life)
            return null;
        float[] c0 = Colors[0], c1 = Colors[1], c2 = Colors[2];
        if (e < tA)
        {
            double f = tA > 0 ? (tA - e) / tA : 0;
            return Mix(c0, c1, f, (Rad[0] - Rad[1]) * f + Rad[1]);
        }
        if (e < tB)
        {
            double g = tB > tA ? (tB - e) / (tB - tA) : 0;
            return Mix(c1, c2, g, Rad[1] * g + Rad[2] * (1 - g));
        }
        double h = life > tB ? (life - e) / (life - tB) : 0;
        return ((float)(c2[0] * h), (float)(c2[1] * h), (float)(c2[2] * h), Rad[2] * h);
    }

    private static (float, float, float, double) Mix(float[] a, float[] b, double f, double rad) =>
        ((float)(a[0] * f + b[0] * (1 - f)), (float)(a[1] * f + b[1] * (1 - f)), (float)(a[2] * f + b[2] * (1 - f)), rad);
}

/// <summary>A one shot shape with a life, a three phase light curve, camera facing, random spin and an optional position shift.</summary>
/// <remarks>docs/shapes-render.md 4.3. Created by a projectile explode update, ShapeBase_explode and debris.</remarks>
public sealed class ExplosionEffect : IEffect
{
    private readonly RenderInstance mInst;
    private readonly RenderThread mMain;
    private readonly double mX, mY, mZ, mAx, mAy, mAz, mSpin;
    private readonly bool mFace, mShift;
    private double mAge;                       // ms
    /// <summary>The light curve, or null when the datablock has none.</summary>
    public readonly LightCurve? Curve;
    /// <summary>The position of the explosion.</summary>
    public (double x, double y, double z) Origin => (mX, mY, mZ);
    /// <summary>The age in milliseconds.</summary>
    public double AgeMs => mAge;
    /// <summary>The life in milliseconds.</summary>
    public double LifeMs { get; }

    /// <inheritdoc/>
    public bool Alive => mAge < LifeMs;
    /// <inheritdoc/>
    public PointLight? Light
    {
        get
        {
            if (Curve is not LightCurve c || c.At(mAge) is not (float r, float g, float b, double rad) || rad <= 0)
                return null;
            return new PointLight(mX, mY, mZ, r, g, b, c.Range * rad);
        }
    }

    /// <summary>Creates an explosion of a datablock.</summary>
    /// <param name="shape">The explosion shape.</param>
    /// <param name="db">The explosion datablock, or null.</param>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <param name="z">The z position.</param>
    /// <param name="ax">The surface axis x.</param>
    /// <param name="ay">The surface axis y.</param>
    /// <param name="az">The surface axis z.</param>
    /// <param name="rng">The random source for the spin.</param>
    public ExplosionEffect(RenderShape shape, ClientDataBlock? db, double x, double y, double z, double ax, double ay, double az, Random rng)
    {
        (mX, mY, mZ) = (x, y, z);
        double l = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (l < 0.1)
            (ax, ay, az, l) = (0, 0, 1, 1);
        (mAx, mAy, mAz) = (ax / l, ay / l, az / l);
        double ts = Db.Num(db, "timeScale", 1);
        if (ts <= 0)
            ts = 1;
        mFace = Db.Flag(db, "faceCamera");
        mShift = Db.Flag(db, "shiftPosition");
        mSpin = Db.Flag(db, "randomSpin") ? rng.NextDouble() * Math.PI * 2 : 0;
        mInst = new RenderInstance(shape);
        int main = shape.Sequences.Length > 0 ? 0 : -1;
        mMain = mInst.AddThread(main, 0, 0);
        mMain.TimeScale = 1 / ts;
        int vis = shape.FindSequence("visibility");
        if (vis >= 0 && vis != main)
        {
            RenderThread v = mInst.AddThread(vis, 0, 0);
            v.Position = 2.0 / 3 + rng.NextDouble() / 3;
            v.TimeScale = 0;
        }
        double dur = main >= 0 ? shape.Sequences[main].Duration : 0.5;
        LifeMs = Math.Truncate(ts * dur * 1000);
        if (Db.Flag(db, "hasLight"))
        {
            var cols = new float[3][];
            var rad = new double[3];
            for (int i = 0; i < 3; ++i)
            {
                cols[i] = new[] { (float)Db.Num(db, $"colors[{i}].r"), (float)Db.Num(db, $"colors[{i}].g"), (float)Db.Num(db, $"colors[{i}].b") };
                rad[i] = Db.Num(db, $"radFactors[{i}]");
            }
            Curve = new LightCurve(ts, dur, Db.Num(db, "timeZero"), Db.Num(db, "timeOne"), cols, rad, Db.Num(db, "lightRange"));
        }
    }

    /// <summary>Creates the hit shape of a LaserProjectile.</summary>
    /// <param name="shape">The hit shape.</param>
    /// <param name="x">The x position of the end point.</param>
    /// <param name="y">The y position of the end point.</param>
    /// <param name="z">The z position of the end point.</param>
    /// <param name="beamTime">The beam time in seconds.</param>
    /// <param name="elapsed">The seconds of the beam already spent.</param>
    /// <remarks>Follows LaserProjectile_loadResources (00463204) and the image setup (00463810): identity transform at the end point, only the <c>ambient</c> thread (none when the shape lacks it) with timeScale = sequence duration / beamTime so one cycle spans beamTime. The image exists only while the beam lives: beamTime seconds after it started.</remarks>
    public ExplosionEffect(RenderShape shape, double x, double y, double z, double beamTime, double elapsed)
    {
        (mX, mY, mZ) = (x, y, z);
        (mAx, mAy, mAz) = (0, 0, 1);
        mInst = new RenderInstance(shape);
        int amb = shape.FindSequence("ambient");
        mMain = mInst.AddThread(amb, 0, 0);
        double dur = amb >= 0 ? Math.Max(shape.Sequences[amb].Duration, 1e-6) : 1;
        mMain.TimeScale = dur / Math.Max(beamTime, 1e-3);
        mInst.Advance(mMain, Math.Max(elapsed, 0));
        mAge = Math.Max(elapsed, 0) * 1000;
        LifeMs = beamTime * 1000;
    }

    /// <inheritdoc/>
    public void Tick(double dt)
    {
        mAge += dt * 1000;
        mInst.Advance(dt);
    }

    /// <inheritdoc/>
    public void Build(FrameContext ctx)
    {
        if (!Alive)
            return;
        double px = mX, py = mY, pz = mZ;
        Xf xf;
        if (mFace)
        {
            double fx = ctx.Camera.X - mX, fy = ctx.Camera.Y - mY, fz = ctx.Camera.Z - mZ;
            double l = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            if (l < 1e-6)
                return;
            fx /= l; fy /= l; fz /= l;
            if (mShift)
            {
                // 4.3: when the camera is in front of the hit surface (axis against the view ray) the image moves toward the camera by
                // radius / |cos| (at most 3 radii and 0.3 of the distance), which puts the quad's plane clear of the surface.
                double dot = -(mAx * fx + mAy * fy + mAz * fz);
                if (dot < 0)
                {
                    double k = dot <= -1.0 / 3 ? 1 / dot : -3;
                    double s = Math.Min(mInst.Shape.Radius * -k, l * 0.3);
                    px += fx * s; py += fy * s; pz += fz * s;
                }
            }
            // The shape's +Y axis is the view ray, camera to image (4.3): the mesh depth of the cone shaped clouds (local y from -3 to +1)
            // bulges toward the camera.
            xf = Mathx.SpinY(mSpin).Then(Mathx.Basis(px, py, pz, -fx, -fy, -fz));
        }
        else
            xf = AxisBasis();
        ShapeDraw d = ctx.Draw(xf, mInst.Shape.Name);
        d.ForceTranslucent = true;
        d.CullClipped = true;
        d.Light = new LightSet { AmbR = 1, AmbG = 1, AmbB = 1, SunR = 0, SunG = 0, SunB = 0 };
        RenderImage? img = ctx.Renderer.Build(mInst, ctx.Camera, d);
        if (img != null)
            ctx.Lists.Add(img);
    }

    // `faceCamera` false: the axis is the shape's z axis (the hit normal).
    private Xf AxisBasis()
    {
        // local +Z along the axis: forward (local +Y) is any vector perpendicular to the axis.
        double ux = Math.Abs(mAz) < 0.99 ? 0 : 1, uy = 0, uz = Math.Abs(mAz) < 0.99 ? 1 : 0;
        double fx = mAy * uz - mAz * uy, fy = mAz * ux - mAx * uz, fz = mAx * uy - mAy * ux;
        double l = Math.Sqrt(fx * fx + fy * fy + fz * fz);
        return Mathx.Basis(mX, mY, mZ, fx / l, fy / l, fz / l, mAx, mAy, mAz);
    }
}
