// Snowfall / rain (docs/shapes-render.md 4.8, Q10 open): a reasonable version. Particles fill a box around the camera,
// fall with the wind of Snowfall_setWind (z -35 snow, -75 rain, scaled down to a pleasant speed) and wrap in the box;
// snow is a small camera facing quad, rain a streak. The count scales with the ghost's intensity.
using OpenTribes.Client.Scene;
using OpenTribes.Client.Shapes;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Effects;

public sealed class WeatherEffect
{
    public double Intensity = 0.5, WindX, WindY;
    public bool Rain, Suspended;
    public const int MaxParticles = 1500;
    public const double Box = 40;

    // Particles for an intensity 0..1 (count scales linearly, at least a few).
    public static int Count(double intensity) => (int)Math.Clamp(Math.Round(intensity * MaxParticles), 0, MaxParticles);

    public void Build(FrameContext ctx, double time)
    {
        if (Suspended || Intensity <= 0)
            return;
        int n = Count(Intensity);
        double fall = Rain ? 30 : 4;
        var v = new List<Vertex3D>(n * 6);
        var cam = ctx.Camera;
        var r = ctx.Camera.Right;
        for (int i = 0; i < n; ++i)
        {
            // Fixed pseudo random start per particle, wrapped around the camera.
            uint h = unchecked((uint)i * 2654435761u);
            double ux = (h & 0xffff) / 65535.0, uy = ((h >> 8) & 0xffff) / 65535.0, uz = ((h * 40503u) >> 4 & 0xffff) / 65535.0;
            double x = Wrap(ux * Box + (WindX * 0.1 + 0.5) * time, cam.X), y = Wrap(uy * Box + (WindY * 0.1 + 0.3) * time, cam.Y), z = Wrap(uz * Box - fall * time, cam.Z);
            double size = Rain ? 0.02 : 0.05;
            double len = Rain ? 0.8 : 0.05;
            Quad(v, x, y, z, r.x * size, r.y * size, 0, 0, 0, len, Rain ? 0.5f : 0.9f);
        }
        var img = new RenderImage { Category = 1, SortKey = 0.5, Label = "weather" };
        img.Batches.Add(new DrawBatch { Vertices = v.ToArray(), State = new DrawState3D(null, null, true, 0, true, false, BlendMode.Translucent) });
        ctx.Lists.Add(img);
    }

    private static double Wrap(double v, double center)
    {
        double d = (v - (center - Box / 2)) % Box;
        if (d < 0) d += Box;
        return center - Box / 2 + d;
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
