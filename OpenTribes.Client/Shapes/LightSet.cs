namespace OpenTribes.Client.Shapes;

/// <summary>A point light.</summary>
/// <param name="X">The x position.</param>
/// <param name="Y">The y position.</param>
/// <param name="Z">The z position.</param>
/// <param name="R">The red.</param>
/// <param name="G">The green.</param>
/// <param name="B">The blue.</param>
/// <param name="Range">The range.</param>
public readonly record struct PointLight(double X, double Y, double Z, float R, float G, float B, double Range);

/// <summary>The light set of a shape: ambient colour plus the sun plus the point lights in range. A vertex colour is the sum clamped to 0..1 per channel.</summary>
/// <remarks>docs/shapes-render.md 2.5 and 4.9. The sun term is N.L (ours, spec Q2), the point term is the stock vertex function 005aeecc: colour * N.L towards the light inside the range, nothing outside.</remarks>
public sealed class LightSet
{
    /// <summary>The ambient red.</summary>
    public float AmbR = 0.45f;
    /// <summary>The ambient green.</summary>
    public float AmbG = 0.45f;
    /// <summary>The ambient blue.</summary>
    public float AmbB = 0.45f;
    /// <summary>The x of the unit vector towards the sun.</summary>
    public double SunX;
    /// <summary>The y of the unit vector towards the sun.</summary>
    public double SunY;
    /// <summary>The z of the unit vector towards the sun.</summary>
    public double SunZ = 1;
    /// <summary>The sun red.</summary>
    public float SunR = 0.55f;
    /// <summary>The sun green.</summary>
    public float SunG = 0.55f;
    /// <summary>The sun blue.</summary>
    public float SunB = 0.55f;
    /// <summary>The point lights.</summary>
    public readonly List<PointLight> Points = new();

    /// <summary>Creates the default set.</summary>
    /// <returns>The set.</returns>
    public static LightSet Default() => new() { SunX = 0.3, SunY = 0.3, SunZ = 0.9 };

    /// <summary>Sets the sun of a Planet (world-render.md 4.3): direction (cos inc cos(az + 90), cos inc sin(az + 90), sin inc).</summary>
    /// <param name="azimuthDeg">The azimuth in degrees.</param>
    /// <param name="incidenceDeg">The incidence in degrees.</param>
    public void SetSun(double azimuthDeg, double incidenceDeg)
    {
        double az = (azimuthDeg + 90) * Math.PI / 180, inc = incidenceDeg * Math.PI / 180;
        SunX = Math.Cos(inc) * Math.Cos(az);
        SunY = Math.Cos(inc) * Math.Sin(az);
        SunZ = Math.Sin(inc);
    }

    /// <summary>Adds a point light.</summary>
    /// <param name="l">The light.</param>
    public void Add(in PointLight l) => Points.Add(l);

    /// <summary>Builds the image light adjust of a shape whose light probe found a surface (0x449a90): the sun colour moves toward the probe colour by the strength s, and the ambient is scaled by min(1, 2 s (0.3 r + 0.5 g + 0.2 b)).</summary>
    /// <param name="pr">The probe red.</param>
    /// <param name="pg">The probe green.</param>
    /// <param name="pb">The probe blue.</param>
    /// <param name="s">The strength.</param>
    /// <returns>The adjusted set.</returns>
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

    /// <summary>The point lights as one shape sees them.</summary>
    /// <remarks>
    /// <para>The light setup of the type 2 lights (005ae964) picks a mode per light from the distance d of the bounding sphere centre of the shape (radius r) to the light (range R): 0 none when d &gt; R + r. 1 per vertex when d &lt;= 20 r or d &lt;= 1. Else the shape is lit from the direction to its centre with the factor 1 / ftol(c3 d^2 + c2 d + c1) (0 when the integer is 0): mode 2 when d &lt;= R - 2 r, mode 3 (with the per vertex range test of mode 1) otherwise.</para>
    /// <para>The three divisor floats (+0x78, +0x7c, +0x80) are never written for item, projectile, explosion and image lights. The light object sits in memory the allocator (mem.dll, MS_Malloc) hands out unzeroed, so they hold whatever the block held before. Stock memory shows c1 as 0 or a denormal in most lights (ftol 0: factor 0), 1.0 in some flag and explosion lights, and rarely a large value. c2 and c3 are 0 or denormal. The modal case is used: no vertex divisor (t &lt;= 1, mode 1 untouched) and the far modes 2 and 3 light nothing (docs/shapes-render.md 2.5).</para>
    /// </remarks>
    public sealed class ShapeFrame
    {
        private readonly LightSet mSet;
        private readonly bool[] mNear;

        /// <summary>Computes which lights reach the shape.</summary>
        /// <param name="set">The light set.</param>
        /// <param name="cx">The centre x of the bounding sphere.</param>
        /// <param name="cy">The centre y of the bounding sphere.</param>
        /// <param name="cz">The centre z of the bounding sphere.</param>
        /// <param name="radius">The radius of the bounding sphere.</param>
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

        /// <summary>Shades a point with the sun, the ambient and the lights that reach the shape.</summary>
        /// <param name="px">The point x.</param>
        /// <param name="py">The point y.</param>
        /// <param name="pz">The point z.</param>
        /// <param name="nx">The normal x.</param>
        /// <param name="ny">The normal y.</param>
        /// <param name="nz">The normal z.</param>
        /// <returns>The colour, 0 to 1 per channel.</returns>
        public (float r, float g, float b) At(double px, double py, double pz, double nx, double ny, double nz) =>
            mSet.Shade(px, py, pz, nx, ny, nz, mNear);
    }

    /// <summary>The distance in radii within which a light shades per vertex, the first float of a light object (0x41a00000).</summary>
    public const double FarRadii = 20.0;

    /// <summary>Computes the light frame of a shape.</summary>
    /// <param name="cx">The centre x of the bounding sphere.</param>
    /// <param name="cy">The centre y of the bounding sphere.</param>
    /// <param name="cz">The centre z of the bounding sphere.</param>
    /// <param name="radius">The radius of the bounding sphere.</param>
    /// <returns>The frame.</returns>
    public ShapeFrame ForShape(double cx, double cy, double cz, double radius) => new(this, cx, cy, cz, radius);

    /// <summary>Shades a point with every light.</summary>
    /// <param name="px">The point x.</param>
    /// <param name="py">The point y.</param>
    /// <param name="pz">The point z.</param>
    /// <param name="nx">The normal x.</param>
    /// <param name="ny">The normal y.</param>
    /// <param name="nz">The normal z.</param>
    /// <returns>The colour, 0 to 1 per channel.</returns>
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
