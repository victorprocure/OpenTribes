// The light set of a shape (docs/shapes-render.md 2.5, 4.9): ambient colour plus the sun plus the point lights in range;
// a vertex colour is the sum clamped to 0..1 per channel. The sun term is N.L (ours, spec Q2), the point term is the
// stock vertex function 005aeecc: colour * N.L towards the light inside the range, nothing outside.
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


    // The point lights as one shape sees them: the light setup of the type 2 lights (005ae964) picks a mode per light from the
    // distance d of the shape's bounding sphere centre (radius r) to the light (range R): 0 none when d > R + r; 1 per vertex when
    // d <= 20 r or d <= 1; else the direction to the centre is used for every vertex, mode 2 when d <= R - 2 r, mode 3 (with the
    // per vertex range test of mode 1) otherwise. The distance divisor ftol((c3 d + c2) d + c1) is 1 here (c1 = 1, c2 = c3 = 0: the
    // exe leaves the three fields of an item light unset).
    public sealed class ShapeFrame
    {
        private readonly LightSet mSet;
        private readonly int[] mMode;
        private readonly (double x, double y, double z)[] mDir;

        public ShapeFrame(LightSet set, double cx, double cy, double cz, double radius)
        {
            mSet = set;
            mMode = new int[set.Points.Count];
            mDir = new (double, double, double)[set.Points.Count];
            for (int i = 0; i < mMode.Length; ++i)
            {
                PointLight l = set.Points[i];
                double dx = cx - l.X, dy = cy - l.Y, dz = cz - l.Z, d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (l.Range + radius < d)
                    mMode[i] = 0;
                else if (radius * FarRadii >= d || d <= 1.0)
                    mMode[i] = 1;
                else
                {
                    mDir[i] = (dx / d, dy / d, dz / d);
                    mMode[i] = l.Range - 2.0 * radius >= d ? 2 : 3;
                }
            }
        }

        public (float r, float g, float b) At(double px, double py, double pz, double nx, double ny, double nz) =>
            mSet.Shade(px, py, pz, nx, ny, nz, mMode, mDir);
    }

    public const double FarRadii = 20.0; // the first float of a light object (0x41a00000)

    public ShapeFrame ForShape(double cx, double cy, double cz, double radius) => new(this, cx, cy, cz, radius);

    public (float r, float g, float b) At(double px, double py, double pz, double nx, double ny, double nz) =>
        Shade(px, py, pz, nx, ny, nz, null, null);

    private (float r, float g, float b) Shade(double px, double py, double pz, double nx, double ny, double nz, int[]? modes,
                                              (double x, double y, double z)[]? dirs)
    {
        double ndl = Math.Max(0, nx * SunX + ny * SunY + nz * SunZ);
        double r = AmbR + SunR * ndl, g = AmbG + SunG * ndl, b = AmbB + SunB * ndl;
        for (int i = 0; i < Points.Count; ++i)
        {
            PointLight l = Points[i];
            int mode = modes?[i] ?? 1;
            if (mode == 0)
                continue;
            double dx = l.X - px, dy = l.Y - py, dz = l.Z - pz;
            double d2 = dx * dx + dy * dy + dz * dz;
            if (mode >= 2)
            {
                // 005aeecc modes 2 and 3: mode 3 keeps the per vertex range test, both light by the cosine between the normal and
                // the direction from the shape's centre to the light.
                if (mode == 3 && d2 > l.Range * l.Range)
                    continue;
                var (ux, uy, uz) = dirs![i];
                double c = -(nx * ux + ny * uy + nz * uz);
                if (c < 0)
                    continue;
                r += l.R * c; g += l.G * c; b += l.B * c;
                continue;
            }
            if (d2 > l.Range * l.Range || l.Range <= 0)
                continue;
            // The point light of a vertex (005aeecc, mode 1): nothing beyond the range or behind the surface, otherwise
            // colour * cos(angle to the light) with no distance falloff (the divisor 1 + c2 d + c3 d^2 stays 1 for item lights).
            double d = Math.Sqrt(d2);
            double dot = nx * dx + ny * dy + nz * dz;
            if (dot < 0)
                continue;
            double k = d > 0.001 ? dot / d : 1;
            r += l.R * k; g += l.G * k; b += l.B * k;
        }
        return ((float)Math.Min(r, 1), (float)Math.Min(g, 1), (float)Math.Min(b, 1));
    }
}
