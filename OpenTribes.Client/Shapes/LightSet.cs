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

    // The image light adjust of a shape whose light probe found a surface (0x449a90): the sun colour moves toward the probe colour by the
    // strength s, and the ambient is scaled by min(1, 2 s (0.3 r + 0.5 g + 0.2 b)).
    public LightSet WithProbe(float pr, float pg, float pb, float s)
    {
        var l = new LightSet { AmbR = AmbR, AmbG = AmbG, AmbB = AmbB, SunX = SunX, SunY = SunY, SunZ = SunZ, SunR = SunR, SunG = SunG, SunB = SunB };
        l.Points.AddRange(Points);
        if (SunR != 0 || SunG != 0 || SunB != 0)
        {
            l.SunR = SunR * (1 - s) + pr * s;
            l.SunG = SunG * (1 - s) + pg * s;
            l.SunB = SunB * (1 - s) + pb * s;
        }
        float f = Math.Min(1f, (0.3f * pr + 0.5f * pg + 0.2f * pb) * s * 2f);
        l.AmbR *= f; l.AmbG *= f; l.AmbB *= f;
        return l;
    }

    // The point lights as one shape sees them: the light setup of the type 2 lights (005ae964) picks a mode per light from the
    // distance d of the shape's bounding sphere centre (radius r) to the light (range R): 0 none when d > R + r; 1 per vertex when
    // d <= 20 r or d <= 1; else the shape is lit from the direction to its centre with the factor 1 / ftol(c3 d^2 + c2 d + c1)
    // (0 when the integer is 0), mode 2 when d <= R - 2 r, mode 3 (with the per vertex range test of mode 1) otherwise. The three
    // divisor floats (+0x78, +0x7c, +0x80) are never written for item, projectile, explosion and image lights: the light object
    // sits in memory the allocator (mem.dll, MS_Malloc) hands out unzeroed, so they hold whatever the block held before.
    // Stock memory of three runs (Broadside, flag, disc and explosion lights) shows c1 as 0 or a denormal in most lights (ftol 0:
    // factor 0), 1.0 in some flag and explosion lights, and rarely a large value; c2 and c3 are 0 or denormal (the disc fired
    // from the first person view in 3 of 3 runs held c3 about 85, which divides that light by 85 d^2 on shape vertices; the
    // server spawned disc and the other lights did not). The modal case is used: no vertex divisor (t <= 1, mode 1 untouched) and
    // the far modes 2 and 3 light nothing (docs/shapes-render.md 2.5).
    public sealed class ShapeFrame
    {
        private readonly LightSet mSet;
        private readonly bool[] mNear;

        public ShapeFrame(LightSet set, double cx, double cy, double cz, double radius)
        {
            mSet = set;
            mNear = new bool[set.Points.Count];
            for (int i = 0; i < mNear.Length; ++i)
            {
                PointLight l = set.Points[i];
                double dx = cx - l.X, dy = cy - l.Y, dz = cz - l.Z, d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                mNear[i] = l.Range + radius >= d && (radius * FarRadii >= d || d <= 1.0);
            }
        }

        public (float r, float g, float b) At(double px, double py, double pz, double nx, double ny, double nz) =>
            mSet.Shade(px, py, pz, nx, ny, nz, mNear);
    }

    public const double FarRadii = 20.0; // the first float of a light object (0x41a00000)

    public ShapeFrame ForShape(double cx, double cy, double cz, double radius) => new(this, cx, cy, cz, radius);

    public (float r, float g, float b) At(double px, double py, double pz, double nx, double ny, double nz) =>
        Shade(px, py, pz, nx, ny, nz, null);

    private (float r, float g, float b) Shade(double px, double py, double pz, double nx, double ny, double nz, bool[]? near)
    {
        // The sun is a type 6 light: (1 + N.L) / 2 times its colour (005aee50, the planet light object has type 6).
        double ndl = (1 + nx * SunX + ny * SunY + nz * SunZ) * 0.5;
        double r = AmbR + SunR * ndl, g = AmbG + SunG * ndl, b = AmbB + SunB * ndl;
        for (int i = 0; i < Points.Count; ++i)
        {
            PointLight l = Points[i];
            if (near != null && !near[i])
                continue;
            double dx = l.X - px, dy = l.Y - py, dz = l.Z - pz;
            double d2 = dx * dx + dy * dy + dz * dz;
            if (d2 > l.Range * l.Range || l.Range <= 0)
                continue;
            // The point light of a vertex (005aeecc, mode 1): nothing beyond the range or behind the surface, otherwise
            // colour * cos(angle to the light), divided by c3 d^2 + c2 d + c1 only when that exceeds 1 (not in the modal case, above).
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
