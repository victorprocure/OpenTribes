using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.World;

// world-render.md 4.1: the sky band of 16 panels with two caps.
public sealed class SkyRenderer : IDisposable
{
    public const int Panels = 16;

    private readonly IGfxDevice mDev;
    private readonly IGfxTexture?[] mPanelTextures;
    private readonly int[] mPanelIndex = new int[Panels];
    private readonly (float r, float g, float b) mTop, mBottom;
    public double Size = 600, FeaturePosition;
    public double Visible = 600;
    public bool Textured { get; }

    // 4.1 Init sets the band radius to 1.5 * visible. The stock shots (Broadside, Raindance, visible 600 and 450) match
    // best when the panel geometry has 1.8 * visible: a sweep of the factor over 1.0 to 1.6 had its minimum at 1.2 for both
    // missions (the cause in the exe is not found; it is measured).
    public const double BandRadiusFactor = 1.8;

    // materials: bitmap names of the sky `.dml` (null when the sky has no material list: solid sky).
    public SkyRenderer(IGfxDevice dev, WorldAssets assets, List<string?>? materials, int[] textures, double size,
                       double featurePosition, double visible, (float, float, float) skyColor, (float, float, float) hazeColor)
    {
        mDev = dev;
        Size = size;
        FeaturePosition = featurePosition;
        Visible = visible;
        mTop = skyColor;
        mBottom = hazeColor;
        mPanelTextures = new IGfxTexture?[Panels];
        var cache = new Dictionary<int, IGfxTexture?>();
        PbmpBitmap? first = null;
        if (materials != null && materials.Count > 0)
        {
            for (int i = 0; i < Panels; ++i)
            {
                int t = textures[i];
                mPanelIndex[i] = t;
                if (t < 0)
                    continue;
                int m = t % materials.Count;
                PbmpBitmap? bmp = materials[m] is string n ? assets.Bitmap(n) : null;
                if (bmp == null)
                    continue;
                first ??= bmp;
                if (!cache.TryGetValue(m, out IGfxTexture? tex))
                    cache[m] = tex = mDev.CreateTexture3D(TerrainData.QuantizeRgba5(assets.Rgba(bmp, true)), bmp.Width, bmp.Height, new TextureOptions(false, false, true));
                mPanelTextures[i] = tex;
            }
        }
        if (first != null)
        {
            // 4.1 Init: the sky colour is pixel (0, 0) of the first used panel bitmap, the haze colour pixel (0, height - 1),
            // both read back through the 5 bit packed upload of the stock client (the cap and clear colour of the stock shots).
            byte[] rgba = TerrainData.QuantizeRgba5(assets.Rgba(first, true));
            mTop = (rgba[0] / 255f, rgba[1] / 255f, rgba[2] / 255f);
            int o = (first.Height - 1) * first.Width * 4;
            mBottom = (rgba[o] / 255f, rgba[o + 1] / 255f, rgba[o + 2] / 255f);
            Textured = true;
        }
    }

    public (float r, float g, float b) SolidColor => mTop;

    // Azimuth in degrees of panel vertex i (4.1 Init: 90 + featurePosition - 22.5, 22.5 degrees clockwise per step).
    public static double Azimuth(int i, double featurePosition) => 90 + featurePosition - 22.5 - 22.5 * i;

    public void Draw(in Camera3D cam)
    {
        if (!Textured)
            return;
        double radius = BandRadiusFactor * Visible;
        var lower = new (float x, float y, float z)[Panels + 1];
        var upper = new (float x, float y, float z)[Panels + 1];
        for (int i = 0; i <= Panels; ++i)
        {
            double az = Azimuth(i, FeaturePosition) * Math.PI / 180;
            float x = (float)(cam.X + radius * Math.Cos(az)), y = (float)(cam.Y + radius * Math.Sin(az));
            lower[i] = (x, y, (float)(cam.Z - Size / 8));
            upper[i] = (x, y, (float)(cam.Z + Size * 7 / 8));
        }
        var state = new DrawState3D(null, null, false, 0f, false, false);
        var tris = new List<Vertex3D>();
        Vertex3D P((float x, float y, float z) p, float u, float v, (float r, float g, float b) c) =>
            new(p.x, p.y, p.z, u, v, 0, 0, 0, c.r, c.g, c.b);
        float bz = (float)(cam.Z - Size), tz = (float)(cam.Z + Size);
        for (int i = 0; i < Panels; ++i)
        {
            var bottom = (lower[i].x * 0 + (float)cam.X, (float)cam.Y, bz);
            var top = ((float)cam.X, (float)cam.Y, tz);
            tris.Add(P(lower[i], 0, 0, mBottom)); tris.Add(P(lower[i + 1], 0, 0, mBottom)); tris.Add(P(bottom, 0, 0, mBottom));
            tris.Add(P(upper[i], 0, 0, mTop)); tris.Add(P(top, 0, 0, mTop)); tris.Add(P(upper[i + 1], 0, 0, mTop));
        }
        mDev.Draw3D(tris.ToArray(), state);
        for (int i = 0; i < Panels; ++i)
        {
            IGfxTexture? tex = mPanelTextures[i];
            if (tex == null)
                continue;
            var white = (1f, 1f, 1f);
            // quad (lower i, upper i, upper i+1, lower i+1) with coordinates (0,1) (0,0) (1,0) (1,1)
            Vertex3D a = P(lower[i], 0, 1, white), b = P(upper[i], 0, 0, white), c = P(upper[i + 1], 1, 0, white), d = P(lower[i + 1], 1, 1, white);
            mDev.Draw3D(new[] { a, b, c, a, c, d }, new DrawState3D(tex, null, false, 0f, false, false));
        }
    }

    public void Dispose()
    {
        foreach (IGfxTexture? t in mPanelTextures.Distinct())
            t?.Dispose();
    }
}

// world-render.md 4.2: the star generator and its draw.
public static class StarField
{
    public const int Count = 3000;

    // The exe's C runtime generator (00607408): x = x * 0x15a4e35 + 1, result bits 16 to 30.
    public sealed class Lcg
    {
        private uint mState;
        public Lcg(uint seed) => mState = seed;
        public int Next()
        {
            mState = unchecked(mState * 0x15a4e35u + 1);
            return (int)((mState >> 16) & 0x7fff);
        }
    }

    public readonly record struct Star(double X, double Y, double Z, int Class);

    // Seed source (open): Random_seed calls srand with its seed, and the function-static generators seed with 0x8377
    // (docs/static-sim.md 1.7); that is the best reading of "the sequence after the last Random_seed".
    public const uint DefaultSeed = 0x8377;

    // 4.2 Init: rejection sampling of 3000 stars in a ball of radius 1000 (vertical axis excluded), brightness class
    // from rand() % 30: 0 -> 0, 1..8 -> 2, 9..22 -> 3, 23..29 -> 4. Positions are unit vectors scaled by `radius`.
    public static List<Star> Generate(uint seed, double radius)
    {
        var rng = new Lcg(seed);
        var stars = new List<Star>(Count);
        int guard = 0;
        while (stars.Count < Count && guard++ < 1000000)
        {
            int x = rng.Next() % 2000 - 1000, y = rng.Next() % 2000 - 1000, z = rng.Next() % 2000 - 1000;
            double len = Math.Sqrt((double)x * x + (double)y * y + (double)z * z);
            if (len > 1000 || len == 0 || (x == 0 && y == 0))
                continue;
            int r = rng.Next() % 30;
            int cls = r == 0 ? 0 : r <= 8 ? 2 : r <= 22 ? 3 : 4;
            stars.Add(new Star(x / len * radius, y / len * radius, z / len * radius, cls));
        }
        return stars;
    }

    public static void Draw(IGfxDevice dev, in Camera3D cam, List<Star> stars, (float, float, float)[] colors)
    {
        var tris = new List<Vertex3D>(stars.Count * 6);
        var r = cam.Right;
        var u = cam.Up;
        var f = cam.Forward;
        foreach (Star s in stars)
        {
            double dot = s.X * f.x + s.Y * f.y + s.Z * f.z;
            if (dot <= 0.2 * Math.Sqrt(s.X * s.X + s.Y * s.Y + s.Z * s.Z))
                continue; // behind or far off axis
            int px = s.Class <= 1 ? 2 : 1; // 4.2: classes 0 and 1 are 2 x 2 blocks, the rest one pixel
            double dist = Math.Sqrt(s.X * s.X + s.Y * s.Y + s.Z * s.Z);
            double w = px * dot / cam.FocalLength; // world size of px pixels at that depth
            var col = colors[s.Class switch { 0 or 2 => 0, 1 or 3 => 1, _ => 2 }];
            double cx = cam.X + s.X, cy = cam.Y + s.Y, cz = cam.Z + s.Z;
            Vertex3D V(double sx, double sy) => new((float)(cx + r.x * sx * w + u.x * sy * w), (float)(cy + r.y * sx * w + u.y * sy * w),
                                                     (float)(cz + r.z * sx * w + u.z * sy * w), 0, 0, 0, 0, 0, col.Item1, col.Item2, col.Item3);
            Vertex3D a = V(0, 0), b = V(1, 0), c = V(1, 1), d = V(0, 1);
            tris.Add(a); tris.Add(b); tris.Add(c); tris.Add(a); tris.Add(c); tris.Add(d);
            _ = dist;
        }
        dev.Draw3D(tris.ToArray(), new DrawState3D(null, null, false, 0f, false, false));
    }
}

// world-render.md 4.3: the sun or moon billboard.
public sealed class PlanetRenderer : IDisposable
{
    private readonly IGfxDevice mDev;
    private readonly IGfxTexture? mTexture;
    public readonly double Azimuth, Incidence, Size, Distance;

    public PlanetRenderer(IGfxDevice dev, WorldAssets assets, string fileName, double azimuth, double incidence, double size,
                          double distance)
    {
        mDev = dev;
        Azimuth = azimuth;
        Incidence = Math.Clamp(incidence, -89, 89); // 4.3: clamped to [-89, 89]
        Size = size;
        Distance = distance;
        PbmpBitmap? bmp = fileName.Length > 0 ? assets.Bitmap(fileName) : null;
        if (bmp != null)
            mTexture = dev.CreateTexture3D(assets.Rgba(bmp, false), bmp.Width, bmp.Height, new TextureOptions(false, false, true));
    }

    public bool HasBitmap => mTexture != null;

    // 4.3 Planet_setup: v = distance * (cos inc cos(az + 90), cos inc sin(az + 90), sin inc).
    public static (double x, double y, double z) Direction(double azimuth, double incidence, double distance)
    {
        double az = (azimuth + 90) * Math.PI / 180, inc = incidence * Math.PI / 180;
        return (distance * Math.Cos(inc) * Math.Cos(az), distance * Math.Cos(inc) * Math.Sin(az), distance * Math.Sin(inc));
    }

    public void Draw(in Camera3D cam)
    {
        if (mTexture == null)
            return;
        var (vx, vy, vz) = Direction(Azimuth, Incidence, Distance);
        double len = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        double dx = vx / len, dy = vy / len, dz = vz / len;
        // billboard plane perpendicular to the direction, up toward the celestial pole (+Z)
        double rx = dy, ry = -dx, rz = 0; // dir x (0,0,1) = (dy, -dx, 0), right-handed "right" as seen from the camera
        double rl = Math.Sqrt(rx * rx + ry * ry);
        rx /= rl; ry /= rl;
        double ux = ry * dz - rz * dy, uy = rz * dx - rx * dz, uz = rx * dy - ry * dx; // right x dir
        double cx = cam.X + vx, cy = cam.Y + vy, cz = cam.Z + vz;
        Vertex3D V(double a, double b, float u, float v) =>
            new((float)(cx + rx * a * Size + ux * b * Size), (float)(cy + ry * a * Size + uy * b * Size),
                (float)(cz + rz * a * Size + uz * b * Size), u, v, 0, 0, 0);
        Vertex3D p0 = V(-1, -1, 0, 1), p1 = V(1, -1, 1, 1), p2 = V(1, 1, 1, 0), p3 = V(-1, 1, 0, 0);
        mDev.Draw3D(new[] { p0, p1, p2, p0, p2, p3 }, new DrawState3D(mTexture, null, false, 0.5f, false, false));
    }

    public void Dispose() => mTexture?.Dispose();
}
