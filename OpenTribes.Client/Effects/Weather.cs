// Snowfall / rain (docs/shapes-render.md 4.8). Count, speed, box and streak shape follow the exe (FUN_004411c8, FUN_004412a4,
// FUN_00442338, Snowfall_onAdd): the particles live in a cube of side 2 * 200 around the camera (radius 200 sphere),
// rain falls at 2 * 75 units per second plus twice the wind, snow at 35. A rain streak is a kite: the tip at the particle, the widest
// point (0.15 across) 2 units along the fall direction and the end 2.25 units along it. The particles are not simulated one by
// one: each one has a fixed pseudo random start and wraps in the cube, so the picture depends on the time only.
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Effects;

public sealed class WeatherEffect
{
    public double Intensity = 0.5, WindX, WindY;
    public bool Rain, Suspended;
    public const int MaxRain = 2048, MaxSnow = 1024; // 004419b8
    public const double Radius = 200;                 // +0x2000c: spawn cube half side and recycle radius
    public const double RainFall = 150, SnowFall = 35; // rain: wind z -75 applied twice; snow: wind z -35
    public const double MinDepth = 2;                 // +0x20014: nothing closer than this along the view axis draws
    // The streak is a flat fill: the colour bytes (255, 255, 255; Snowfall_init 00441120) scaled by 2/3 (constant 00442978) and snapped to
    // the surface palette, so grey 170 before the snap; on the OpenGL path it blends with the constant alpha 0.55 (0x3f0ccccd, 004424de).
    public const float RainShade = 170f / 255f, RainAlpha = 0.55f;
    public const double StreakEnd = 2.25, StreakWide = 2.0, StreakHalfWidth = 0.075;

    // Particles for an intensity 0..1 (count scales linearly with the kind's maximum).
    // Rain and snow draw only while `$pref::weatherPrecipitation` is on [code 004420a4] and the ghost does not suspend its rendering
    // [code 00441c50]; the same switch holds the loop sound back (the sound starts inside the same block).
    public static bool Draws(bool preference, bool suspended, double intensity) => preference && !suspended && intensity > 0;

    public static int Count(double intensity, bool rain) => (int)Math.Clamp(intensity * (rain ? MaxRain : MaxSnow), 0, rain ? MaxRain : MaxSnow);

    // The four points of one rain streak (tip, left, end, right): d = normalize(wind.x, wind.y, -1), the left and right points
    // 2 units along d from the tip and 0.075 to each side along `side`, the end 2.25 units along d [code 00442338]. The side
    // vector is the cross product of the camera's forward axis (matrix row 1, saved at 00684250 by 00442188) and d, not normalised.
    public static ((double x, double y, double z) tip, (double x, double y, double z) left, (double x, double y, double z) end,
                   (double x, double y, double z) right) Streak((double x, double y, double z) p, double windX, double windY, (double x, double y, double z) forward)
    {
        double l = Math.Sqrt(windX * windX + windY * windY + 1);
        double dx = windX / l, dy = windY / l, dz = -1 / l;
        (double x, double y, double z) side = (forward.y * dz - forward.z * dy, forward.z * dx - forward.x * dz, forward.x * dy - forward.y * dx);
        (double, double, double) At(double along, double across) => (p.x + dx * along + side.x * across, p.y + dy * along + side.y * across, p.z + dz * along + side.z * across);
        return (p, At(StreakWide, -StreakHalfWidth), At(StreakEnd, 0), At(StreakWide, StreakHalfWidth));
    }

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
