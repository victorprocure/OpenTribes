namespace OpenTribes.Client.World;

using OpenTribes.Client.Shapes;

/// <summary>The dynamic light of terrain squares.</summary>
/// <remarks>world-render.md 2.9. A square touched by a light gets a private 9 x 9 light map of its own (nodes at one eighth of the square side) that starts as the static light map and takes every light's contribution per node, the way the stock terrain renderer does (005775bc light mask, 00577270 static nodes, 00576eac and its helpers 00576a34, 00576ab0, 00576b88, 00576c84, 00576d94 light per node, 00577514 nibbles). The terrain does not use the interior formula: a node adds trunc(L * (1 - d^2 / range^2) * 255) to its 8 bit channel (L the colour channel as it is, 0 to 1), and the nibble is the channel >> 4, 15 from 256 up.</remarks>
public sealed class TerrainLightSet
{
    /// <summary>The most lights of a frame, 31 (0057956c).</summary>
    public const int MaxLights = 31;

    private readonly struct Entry
    {
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public float Radius { get; }
        public float Radius2 { get; }
        public float R { get; }
        public float G { get; }
        public float B { get; }

        public Entry(PointLight l, double ox, double oy, double oz)
        {
            X = (float)(l.X - ox);
            Y = (float)(l.Y - oy);
            Z = (float)(l.Z - oz);
            Radius = (float)l.Range;
            Radius2 = Radius * Radius;
            (R, G, B) = (l.R, l.G, l.B);
        }
    }

    private readonly Entry[] _lights;
    /// <summary>The number of lights in the set.</summary>
    public int Count => _lights.Length;

    /// <summary>Creates the set with the light positions moved to the terrain origin.</summary>
    /// <param name="lights">The lights of the frame.</param>
    /// <param name="ox">The x of the terrain origin.</param>
    /// <param name="oy">The y of the terrain origin.</param>
    /// <param name="oz">The z of the terrain origin.</param>
    public TerrainLightSet(IReadOnlyList<PointLight> lights, double ox, double oy, double oz)
    {
        var n = Math.Min(lights.Count, MaxLights);
        _lights = new Entry[n];
        for (var i = 0; i < n; ++i)
            _lights[i] = new Entry(lights[i], ox, oy, oz);
    }

    /// <summary>Finds the lights that reach a square.</summary>
    /// <param name="t">The terrain.</param>
    /// <param name="level">The level of the square.</param>
    /// <param name="x">The square x.</param>
    /// <param name="y">The square y.</param>
    /// <returns>A mask where bit i is set when the sphere of light i reaches the square.</returns>
    /// <remarks>Follows 005775bc. The sphere is tested against the box of the square (its corners in x and y, its lowest and highest height in z), strictly inside the radius.</remarks>
    public uint Mask(TerrainData t, int level, int x, int y)
    {
        const float cs = (float)TerrainData.CellSize;
        float x0 = x * cs, y0 = y * cs, side = (1 << level) * cs;
        float z0 = t.MinHeight(level, x, y), zr = t.MaxHeight(level, x, y) - z0;
        uint mask = 0;
        for (var i = 0; i < _lights.Length; ++i)
        {
            var l = _lights[i];
            var dz = l.Z < z0 ? z0 - l.Z : l.Z > z0 + zr ? l.Z - (z0 + zr) : 0;
            var dx = l.X < x0 ? x0 - l.X : l.X > x0 + side ? l.X - (x0 + side) : 0;
            var dy = l.Y < y0 ? y0 - l.Y : l.Y > y0 + side ? l.Y - (y0 + side) : 0;
            if ((float)Math.Sqrt(((double)dx * dx) + ((double)dy * dy) + ((double)dz * dz)) < l.Radius)
                mask |= 1u << i;
        }
        return mask;
    }

    /// <summary>Computes the 9 x 9 light nibbles of a square.</summary>
    /// <param name="t">The terrain.</param>
    /// <param name="level">The level of the square.</param>
    /// <param name="x">The square x.</param>
    /// <param name="y">The square y.</param>
    /// <param name="mask">The light mask of the square.</param>
    /// <param name="nibbles">The output: R, G, B per node, row major.</param>
    /// <remarks>Follows 00577270, then 00576eac per light of the mask, then 00577514.</remarks>
    public void Nodes(TerrainData t, int level, int x, int y, uint mask, byte[] nibbles)
    {
        var e = new int[81 * 3];
        var h = new float[81]; // height minus the light height, kept for the interpolated nodes of the small squares
        if (level >= 3)
            StaticLarge(t, level, x, y, e);
        else
            StaticSmall(t, level, x, y, e);
        for (var i = 0; i < _lights.Length; ++i)
            if ((mask & (1u << i)) != 0)
            {
                if (level >= 3)
                    LightLarge(t, level, x, y, _lights[i], e);
                else
                    LightSmall(t, level, x, y, _lights[i], e, h);
            }
        for (var i = 0; i < 81 * 3; ++i)
            nibbles[i] = (byte)(e[i] < 0x100 ? e[i] >> 4 : 15);
    }

    private static void SetWord(int[] e, int node, ushort w)
    {
        int r = (w >> 8) & 0xf, g = (w >> 4) & 0xf, b = w & 0xf;
        e[node * 3] = r | (r << 4);
        e[(node * 3) + 1] = g | (g << 4);
        e[(node * 3) + 2] = b | (b << 4);
    }

    private static void StaticLarge(TerrainData t, int level, int x, int y, int[] e)
    {
        var sh = level - 3;
        for (var j = 0; j < 9; ++j)
            for (var i = 0; i < 9; ++i)
                SetWord(e, (j * 9) + i, t.LightWord(x + (i << sh), y + (j << sh)));
    }

    // 576c84 and 576d94: a run of nodes between two nodes, the channel difference shifted right by the run's log2 length
    // (arithmetic, so a negative step rounds toward minus infinity) added once per node.
    private static void Run(int[] e, int from, int to, int stride, int s)
    {
        for (var c = 0; c < 3; ++c)
        {
            int v = e[(from * 3) + c], d = (e[(to * 3) + c] - v) >> s;
            for (var k = 1; k < 1 << s; ++k)
            {
                v += d;
                e[((from + (k * stride)) * 3) + c] = v;
            }
        }
    }

    private static void StaticSmall(TerrainData t, int level, int x, int y, int[] e)
    {
        int s = 3 - level, n = 1 << level, step = 1 << s;
        for (var j = 0; j <= n; ++j)
            for (var i = 0; i <= n; ++i)
            {
                var flat = (((y & 0xff) + j) * 256) + (x & 0xff) + i; // the bitmap is read flat, no wrap inside the row
                SetWord(e, ((j << s) * 9) + (i << s), t.LightWord(flat & 255, flat >> 8));
                if (i != 0)
                    Run(e, ((j << s) * 9) + ((i - 1) << s), ((j << s) * 9) + (i << s), 1, s);
                if (j != 0)
                    Run(e, (((j - 1) << s) * 9) + (i << s), ((j << s) * 9) + (i << s), 9, s);
                if (i != 0 && j != 0)
                    for (var k = 1; k < step; ++k)
                        Run(e, ((((j - 1) << s) + k) * 9) + ((i - 1) << s), ((((j - 1) << s) + k) * 9) + (i << s), 1, s);
            }
    }

    // 576a34: within the radius (inclusive) a node adds trunc(L * t) to its channel, t = (1 - d2 / radius2) * 255.
    private static void Add(int[] e, int node, float d2, in Entry l)
    {
        if (d2 > l.Radius2)
            return;
        var k = (float)((1.0f - ((double)d2 / l.Radius2)) * 255.0f);
        e[node * 3] = (int)(e[node * 3] + (l.R * (double)k));
        e[(node * 3) + 1] = (int)(e[(node * 3) + 1] + (l.G * (double)k));
        e[(node * 3) + 2] = (int)(e[(node * 3) + 2] + (l.B * (double)k));
    }

    // 00576eac, level 3 and up: nodes at one eighth of the side. The height index adds the node number without the node step
    // (the exe reads the height of the square's corner plus (i, j) cells whatever the level, kept).
    private static void LightLarge(TerrainData t, int level, int x, int y, in Entry l, int[] e)
    {
        const float cs = (float)TerrainData.CellSize;
        var step = 1 << (level - 3);
        for (var j = 0; j < 9; ++j)
            for (var i = 0; i < 9; ++i)
            {
                float dx = (((i * step) + x) * (int)cs) - l.X, dy = (((j * step) + y) * (int)cs) - l.Y;
                var dz = t.LocalHeight((x + i) & 0xff, (y + j) & 0xff) - l.Z;
                Add(e, (j * 9) + i, (float)(((double)dx * dx) + ((double)dy * dy) + ((double)dz * dz)), l);
            }
    }

    // 00576eac, levels 0 to 2: the corners of the square, then the nodes between them interpolated (heights and distances).
    private static void LightSmall(TerrainData t, int level, int x, int y, in Entry l, int[] e, float[] h)
    {
        const float cs = (float)TerrainData.CellSize;
        int s = 3 - level, n = 1 << level, run = 1 << s;
        var step = cs / run;
        for (var j = 0; j <= n; ++j)
            for (var i = 0; i <= n; ++i)
            {
                float fx = ((x + i) * (int)cs) - l.X, fy = ((y + j) * (int)cs) - l.Y;
                var fz = t.LocalHeight((x + i) & 0xff, (y + j) & 0xff) - l.Z;
                var node = ((j << s) * 9) + (i << s);
                h[node] = fz;
                Add(e, node, (float)(((double)fz * fz) + ((double)fy * fy) + ((double)fx * fx)), l);
                if (i != 0)
                    Horizontal(e, h, ((j << s) * 9) + ((i - 1) << s), run, step, fy * fy, fx - cs, l);
                if (j != 0)
                    Vertical(e, h, (((j - 1) << s) * 9) + (i << s), run, step, fx * fx, fy - cs, l);
                if (i != 0 && j != 0)
                    for (var k = 1; k < run; ++k)
                    {
                        var gy = (k * step) + ((y + j - 1) * (int)cs) - l.Y;
                        Horizontal(e, h, ((((j - 1) << s) + k) * 9) + ((i - 1) << s), run, step, gy * gy,
                                   ((x + i - 1) * (int)cs) - l.X, l);
                    }
            }
    }

    // 576ab0: the nodes between two nodes of a row.
    private static void Horizontal(int[] e, float[] h, int from, int run, float step, float dy2, float dx, in Entry l)
    {
        float hv = h[from], d = h[from + run] - hv;
        for (var k = 1; k < run; ++k)
        {
            dx += step;
            hv = (d * (1.0f / run)) + hv;
            Add(e, from + k, (float)(((double)hv * hv) + ((double)dx * dx) + dy2), l);
        }
    }

    // 576b88: the nodes between two nodes of a column; their heights are kept for the rows between.
    private static void Vertical(int[] e, float[] h, int from, int run, float step, float dx2, float dy, in Entry l)
    {
        float hv = h[from], d = h[from + (run * 9)] - hv;
        for (var k = 1; k < run; ++k)
        {
            dy += step;
            hv = (d * (1.0f / run)) + hv;
            h[from + (k * 9)] = hv;
            Add(e, from + (k * 9), (float)(((double)hv * hv) + ((double)dy * dy) + dx2), l);
        }
    }
}
