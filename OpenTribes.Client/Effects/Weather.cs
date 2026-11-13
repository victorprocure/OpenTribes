using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Effects;

/// <summary>Snowfall and rain.</summary>
/// <remarks>docs/shapes-render.md 4.8. Count, speed, box and streak shape follow the exe (FUN_004411c8, FUN_004412a4, FUN_00442338, Snowfall_onAdd): the particles live in a cube of side 2 * 200 around the camera (radius 200 sphere), rain falls at 2 * 75 units per second plus twice the wind, snow at 35. A rain streak is a kite: the tip at the particle, the widest point (0.15 across) 2 units along the fall direction and the end 2.25 units along it. The particles are not simulated one by one: each one has a fixed pseudo random start and wraps in the cube, so the picture depends on the time only.</remarks>
public sealed class WeatherEffect
{
    /// <summary>The precipitation intensity, 0 to 1.</summary>
    public double Intensity = 0.5;
    /// <summary>The wind x.</summary>
    public double WindX;
    /// <summary>The wind y.</summary>
    public double WindY;
    /// <summary>True for rain, false for snow.</summary>
    public bool Rain;
    /// <summary>True while the ghost suspends its rendering.</summary>
    public bool Suspended;
    /// <summary>The most rain particles (004419b8).</summary>
    public const int MaxRain = 2048;
    /// <summary>The most snow particles (004419b8).</summary>
    public const int MaxSnow = 1024;
    /// <summary>The half side of the spawn cube and the recycle radius (+0x2000c).</summary>
    public const double Radius = 200;
    /// <summary>The rain fall speed: wind z -75 applied twice.</summary>
    public const double RainFall = 150;
    /// <summary>The snow fall speed: wind z -35.</summary>
    public const double SnowFall = 35;
    /// <summary>The depth along the view axis under which nothing draws (+0x20014).</summary>
    public const double MinDepth = 2;
    /// <summary>The grey of a rain streak. A flat fill: the colour bytes (255, 255, 255; Snowfall_init 00441120) scaled by 2/3 (constant 00442978) and snapped to the surface palette, so grey 170 before the snap.</summary>
    public const float RainShade = 170f / 255f;
    /// <summary>The constant alpha of a rain streak on the OpenGL path (0x3f0ccccd, 004424de).</summary>
    public const float RainAlpha = 0.55f;
    /// <summary>The distance along the fall direction to the end of a streak.</summary>
    public const double StreakEnd = 2.25;
    /// <summary>The distance along the fall direction to the widest point of a streak.</summary>
    public const double StreakWide = 2.0;
    /// <summary>The half width of a streak at its widest point.</summary>
    public const double StreakHalfWidth = 0.075;

    /// <summary>Tells whether precipitation draws.</summary>
    /// <param name="preference">The <c>$pref::weatherPrecipitation</c> switch.</param>
    /// <param name="suspended">True when the ghost suspends its rendering.</param>
    /// <param name="intensity">The intensity.</param>
    /// <returns>True when the particles draw.</returns>
    /// <remarks>Follows 004420a4 (the preference) and 00441c50 (the suspend flag). The same switch holds the loop sound back, because the sound starts inside the same block.</remarks>
    public static bool Draws(bool preference, bool suspended, double intensity) => preference && !suspended && intensity > 0;

    /// <summary>Gives the particle count for an intensity: it scales linearly with the maximum of the kind.</summary>
    /// <param name="intensity">The intensity, 0 to 1.</param>
    /// <param name="rain">True for rain, false for snow.</param>
    /// <returns>The particle count.</returns>
    public static int Count(double intensity, bool rain) => (int)Math.Clamp(intensity * (rain ? MaxRain : MaxSnow), 0, rain ? MaxRain : MaxSnow);

    /// <summary>Builds the four points of one rain streak.</summary>
    /// <param name="p">The particle position.</param>
    /// <param name="windX">The wind x.</param>
    /// <param name="windY">The wind y.</param>
    /// <param name="forward">The forward axis of the camera.</param>
    /// <returns>The tip, left, end and right points.</returns>
    /// <remarks>Follows 00442338. d = normalize(wind.x, wind.y, -1). The left and right points are 2 units along d from the tip and 0.075 to each side along the side vector. The end is 2.25 units along d. The side vector is the cross product of the camera forward axis (matrix row 1, saved at 00684250 by 00442188) and d, not normalised.</remarks>
    public static ((double x, double y, double z) tip, (double x, double y, double z) left, (double x, double y, double z) end,
                   (double x, double y, double z) right) Streak((double x, double y, double z) p, double windX, double windY, (double x, double y, double z) forward)
    {
        double l = Math.Sqrt(windX * windX + windY * windY + 1);
        double dx = windX / l, dy = windY / l, dz = -1 / l;
        (double x, double y, double z) side = (forward.y * dz - forward.z * dy, forward.z * dx - forward.x * dz, forward.x * dy - forward.y * dx);
        (double, double, double) At(double along, double across) => (p.x + dx * along + side.x * across, p.y + dy * along + side.y * across, p.z + dz * along + side.z * across);
        return (p, At(StreakWide, -StreakHalfWidth), At(StreakEnd, 0), At(StreakWide, StreakHalfWidth));
    }

    /// <summary>Adds the render image of the particles to the frame.</summary>
    /// <param name="ctx">The frame context.</param>
    /// <param name="time">The time in seconds.</param>
    public void Build(FrameContext ctx, double time)
    {
        if (!Draws(true, Suspended, Intensity))
            return;
        int n = Count(Intensity, Rain);
        double fall = Rain ? RainFall : SnowFall, drift = Rain ? 2 : 1, side = 2 * Radius;
        var v = new List<Vertex3D>(n * 6);
        var cam = ctx.Camera;
        var f = cam.Forward;
        var r = cam.Right;
        for (int i = 0; i < n; ++i)
        {
            // Fixed pseudo random start per particle, wrapped around the camera.
            uint h = unchecked((uint)i * 2654435761u);
            double ux = (h & 0xffff) / 65535.0, uy = ((h >> 8) & 0xffff) / 65535.0, uz = ((h * 40503u) >> 4 & 0xffff) / 65535.0;
            double x = Wrap(ux * side + WindX * drift * time, cam.X), y = Wrap(uy * side + WindY * drift * time, cam.Y), z = Wrap(uz * side - fall * time, cam.Z);
            double dx = x - cam.X, dy = y - cam.Y, dz = z - cam.Z;
            if (dx * dx + dy * dy + dz * dz > Radius * Radius || dx * f.x + dy * f.y + dz * f.z < MinDepth)
                continue;
            if (Rain)
            {
                var q = Streak((x, y, z), WindX, WindY, (f.x, f.y, f.z));
                Vertex3D V((double x, double y, double z) p) => new((float)p.x, (float)p.y, (float)p.z, 0, 0, 0, 0, 0, RainShade, RainShade, RainShade, RainAlpha);
                Vertex3D t = V(q.tip), a = V(q.left), e = V(q.end), b = V(q.right);
                v.Add(t); v.Add(a); v.Add(e); v.Add(t); v.Add(e); v.Add(b);
            }
            else
                Quad(v, x, y, z, r.x * 0.04, r.y * 0.04, 0, 0, 0, 0.05, 0.9f);
        }
        var img = new RenderImage { Category = 1, SortKey = 0.5, Label = "weather" };
        img.Batches.Add(new DrawBatch { Vertices = v.ToArray(), State = new DrawState3D(null, null, true, 0, true, false, BlendMode.Translucent) });
        ctx.Lists.Add(img);
    }

    private static double Wrap(double v, double center)
    {
        double side = 2 * Radius;
        double d = (v - (center - Radius)) % side;
        if (d < 0) d += side;
        return center - Radius + d;
    }

    private static void Quad(List<Vertex3D> v, double x, double y, double z, double rx, double ry, double rz, double ux, double uy, double uz, float a)
    {
        Vertex3D V(double sx, double sz) => new((float)(x + rx * sx), (float)(y + ry * sx), (float)(z + sz * uz + rz * sx), 0, 0, 0, 0, 0, 1, 1, 1, a);
        double h = uz <= 0.06 ? 0.05 : uz;
        v.Add(V(-1, 0)); v.Add(V(1, 0)); v.Add(V(1, h / Math.Max(uz, 1e-6)));
        v.Add(V(-1, 0)); v.Add(V(1, h / Math.Max(uz, 1e-6))); v.Add(V(-1, h / Math.Max(uz, 1e-6)));
        _ = ux; _ = uy;
    }
}
