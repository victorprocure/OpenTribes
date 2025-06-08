// The light set of a shape (docs/shapes-render.md 2.5, 4.9): ambient colour plus the sun plus the point lights in range;
// a vertex colour is the sum clamped to 0..1 per channel. The per-light formula is ours (spec Q2): sun term N.L, point
// term (1 - d / range) * N.L towards the light.
namespace OpenTribes.Client.Shapes;

public readonly record struct PointLight(double X, double Y, double Z, float R, float G, float B, double Range);

public sealed class LightSet
{
    public float AmbR = 0.45f, AmbG = 0.45f, AmbB = 0.45f;
    public double SunX, SunY, SunZ = 1;        // unit vector towards the sun
    public float SunR = 0.55f, SunG = 0.55f, SunB = 0.55f;
    public readonly List<PointLight> Points = new();

    public static LightSet Default() => new() { SunX = 0.3, SunY = 0.3, SunZ = 0.9 };

    // The sun of a Planet (world-render.md 4.3): direction (cos inc cos(az + 90), cos inc sin(az + 90), sin inc).
    public void SetSun(double azimuthDeg, double incidenceDeg)
    {
        double az = (azimuthDeg + 90) * Math.PI / 180, inc = incidenceDeg * Math.PI / 180;
        SunX = Math.Cos(inc) * Math.Cos(az);
        SunY = Math.Cos(inc) * Math.Sin(az);
        SunZ = Math.Sin(inc);
    }

    public void Add(in PointLight l) => Points.Add(l);

    public (float r, float g, float b) At(double px, double py, double pz, double nx, double ny, double nz)
    {
        double ndl = Math.Max(0, nx * SunX + ny * SunY + nz * SunZ);
        double r = AmbR + SunR * ndl, g = AmbG + SunG * ndl, b = AmbB + SunB * ndl;
        for (int i = 0; i < Points.Count; ++i)
        {
            PointLight l = Points[i];
            double dx = l.X - px, dy = l.Y - py, dz = l.Z - pz;
            double d2 = dx * dx + dy * dy + dz * dz;
            if (d2 >= l.Range * l.Range || l.Range <= 0)
                continue;
            double d = Math.Sqrt(d2);
            double att = 1 - d / l.Range;
            double dot = d > 1e-9 ? Math.Max(0, (nx * dx + ny * dy + nz * dz) / d) : 1;
            double k = att * (0.35 + 0.65 * dot);
            r += l.R * k; g += l.G * k; b += l.B * k;
        }
        return ((float)Math.Min(r, 1), (float)Math.Min(g, 1), (float)Math.Min(b, 1));
    }
}
