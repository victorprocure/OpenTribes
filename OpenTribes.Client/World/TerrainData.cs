using System.Buffers.Binary;
using OpenTribes.Core;
using OpenTribes.World;

namespace OpenTribes.Client.World;

// world-render.md 2.3: the render block the T2 renderer builds from the first `.dtb` block of a terrain, and the
// light map of that block (2.9). The render domain is the 3 x 3 block file of 256 x 256 squares (768 cells, 6144 m),
// every cell of which is one of the same block (2.3 "block map"), so a block with 128 or 64 squares repeats 2 x 2 or
// 4 x 4 inside a render block and the render repeats the terrain past the real map edge (D33).
public sealed class TerrainData
{
    public const int Cells = 256;       // squares per render block side
    public const int BlockCount = 3;    // render blocks per side
    public const int Domain = Cells * BlockCount;
    public const double CellSize = 8.0; // 2^scale shift (3), docs/terrain.md 1.3
    public const int Levels = 9;        // 0 .. 8, level k squares have side 2^k cells

    public int SourceSize { get; }
    public int MaterialCount { get; }

    private readonly float[] mHeights;     // 257 x 257, 11.5 fixed point already applied
    private readonly byte[] mFlags;        // 256 x 256
    private readonly ushort[] mMaterial;   // 256 x 256
    private readonly ushort[] mLight;      // 256 x 256 raw light map words
    private readonly float[][] mMin = new float[Levels][];
    private readonly float[][] mMax = new float[Levels][];
    private readonly bool[][] mEmpty = new bool[Levels][];

    // block: heights (N+1)^2 and squares as parsed by GridBlock; lightMap: ((N << L) + 1)^2 words (L = 0) or null.
    public TerrainData(GridBlock block, int materialCount, ReadOnlySpan<ushort> lightMap)
    {
        int n = block.Size;
        SourceSize = n;
        MaterialCount = materialCount;
        mHeights = new float[(Cells + 1) * (Cells + 1)];
        mFlags = new byte[Cells * Cells];
        mMaterial = new ushort[Cells * Cells];
        mLight = new ushort[Cells * Cells];
        bool tiled = n != Cells;
        for (int y = 0; y <= Cells; ++y)
            for (int x = 0; x <= Cells; ++x)
            {
                int sx = tiled ? x % n : x, sy = tiled ? y % n : y;
                // 2.3 heights: clamp to 0 .. 2047, 11.5 fixed point: u16 = int(h * 32).
                double h = Math.Clamp((double)block.H(sx, sy), 0.0, 2047.0);
                mHeights[x + y * (Cells + 1)] = (int)(h * 32) * 0.03125f;
            }
        int lw = n + 1;
        for (int y = 0; y < Cells; ++y)
            for (int x = 0; x < Cells; ++x)
            {
                int sx = x % n, sy = y % n;
                int si = (sy * n + sx) * 2;
                mFlags[y * Cells + x] = block.Squares[si];
                int m = block.Squares[si + 1];
                mMaterial[y * Cells + x] = (ushort)(m >= materialCount ? 0 : m); // 2.3 materials
                if (lightMap.Length >= lw * lw)
                    mLight[y * Cells + x] = lightMap[sy * lw + sx];
                else
                    mLight[y * Cells + x] = 0xffff;
            }
        BuildHierarchy();
    }

    public float LocalHeight(int lx, int ly) => mHeights[lx + ly * (Cells + 1)];

    // Domain index 0 .. 768 to a render block coordinate 0 .. 256: block b covers [256 b, 256 b + 256], the file is
    // one block repeated, and the last corner is the end of the last block.
    private static int Local(int g) => g >= Domain ? Cells : g & (Cells - 1);

    // Height at a corner of the render domain (cells, 0 .. 768).
    public float Height(int gx, int gy) => mHeights[Local(gx) + Local(gy) * (Cells + 1)];

    public byte Flags(int cx, int cy) => mFlags[(cy & (Cells - 1)) * Cells + (cx & (Cells - 1))];
    public int Material(int cx, int cy) => mMaterial[(cy & (Cells - 1)) * Cells + (cx & (Cells - 1))];
    public bool IsEmptySquare(int cx, int cy) => ((Flags(cx, cy) >> 3) & 7) != 0;
    public ushort LightWord(int cx, int cy) => mLight[(cy & (Cells - 1)) * Cells + (cx & (Cells - 1))];

    public float MinHeight(int level, int cx, int cy) => mMin[level][((cy >> level) & ((Cells >> level) - 1)) * (Cells >> level) + ((cx >> level) & ((Cells >> level) - 1))];
    public float MaxHeight(int level, int cx, int cy) => mMax[level][((cy >> level) & ((Cells >> level) - 1)) * (Cells >> level) + ((cx >> level) & ((Cells >> level) - 1))];
    public bool IsEmpty(int level, int cx, int cy) => mEmpty[level][((cy >> level) & ((Cells >> level) - 1)) * (Cells >> level) + ((cx >> level) & ((Cells >> level) - 1))];

    // 2.3: the split diagonal of a square. True splits along the (0,0) to (1,1) corner diagonal. Level 0 follows the
    // checkerboard of the square indices. Levels 1 to 3 follow it when it agrees with the parent's split. Otherwise (and
    // always from level 4) the diagonal with the smaller maximum deviation wins, strictly: equal deviations choose the other
    // diagonal. The deviation of a lattice point is the integer part of |plane height - height|, where the plane runs
    // through three corners of the point's triangle: the corner heights are the 11.5 fixed point integers and the point's
    // own height is in metres (the exe mixes both units, kept) [code 00574b84, 00574868].
    private readonly Dictionary<int, bool> mSplit = new();

    public bool SplitMain(int level, int cx, int cy)
    {
        int dim = Cells >> level;
        int ix = (cx >> level) & (dim - 1), iy = (cy >> level) & (dim - 1);
        int key = (level << 20) | (iy << 10) | ix;
        if (mSplit.TryGetValue(key, out bool known))
            return known;
        bool checker = ((ix ^ iy) & 1) == 0;
        bool result = checker;
        if (level != 0)
        {
            bool parent = level < Levels - 1 && SplitMain(level + 1, cx, cy);
            if (level > 3 || checker != parent)
            {
                SplitDeviations(level, ix, iy, out int dMain, out int dAnti);
                result = dMain < dAnti;
            }
        }
        mSplit[key] = result;
        return result;
    }

    private void SplitDeviations(int level, int ix, int iy, out int dMain, out int dAnti)
    {
        int s = 1 << level, ox = ix * s, oy = iy * s;
        float a = (int)(LocalHeight(ox, oy) * 32), b = (int)(LocalHeight(ox, oy + s) * 32),
              c = (int)(LocalHeight(ox + s, oy + s) * 32), d = (int)(LocalHeight(ox + s, oy) * 32);
        dMain = 0;
        dAnti = 0;
        for (int dx = 0; dx <= s; ++dx)
            for (int dy = 0; dy <= s; ++dy)
            {
                float z = LocalHeight(ox + dx, oy + dy);
                float fx = (float)dx / s, fy = (float)dy / s;
                float zm = dx < dy ? a + (c - b) * fx + (b - a) * fy : a + (d - a) * fx + (c - d) * fy;
                float za = dx + dy < s ? a + (d - a) * fx + (b - a) * fy : d + b - c + (c - b) * fx + (c - d) * fy;
                dMain = Math.Max(dMain, Deviation(zm, z));
                dAnti = Math.Max(dAnti, Deviation(za, z));
            }
    }

    private static int Deviation(float plane, float z)
    {
        float v = Math.Abs(plane - z);
        return v > 65535f ? 65535 : (int)v;
    }

    private void BuildHierarchy()
    {
        for (int l = 0; l < Levels; ++l)
        {
            int dim = Cells >> l, s = 1 << l;
            mMin[l] = new float[dim * dim];
            mMax[l] = new float[dim * dim];
            mEmpty[l] = new bool[dim * dim];
            for (int y = 0; y < dim; ++y)
                for (int x = 0; x < dim; ++x)
                {
                    int i = y * dim + x;
                    if (l == 0)
                    {
                        float a = LocalHeight(x, y), b = LocalHeight(x + 1, y), c = LocalHeight(x, y + 1), d = LocalHeight(x + 1, y + 1);
                        mMin[0][i] = Math.Min(Math.Min(a, b), Math.Min(c, d));
                        mMax[0][i] = Math.Max(Math.Max(a, b), Math.Max(c, d));
                        mEmpty[0][i] = IsEmptySquare(x, y);
                    }
                    else
                    {
                        int pd = Cells >> (l - 1);
                        float mn = float.MaxValue, mx = float.MinValue;
                        bool em = true;
                        for (int k = 0; k < 4; ++k)
                        {
                            int j = (y * 2 + (k >> 1)) * pd + x * 2 + (k & 1);
                            mn = Math.Min(mn, mMin[l - 1][j]);
                            mx = Math.Max(mx, mMax[l - 1][j]);
                            em &= mEmpty[l - 1][j];
                        }
                        mMin[l][i] = mn;
                        mMax[l][i] = mx;
                        mEmpty[l][i] = em;
                    }
                }
            _ = s;
        }
    }

    // 2.9: the nibble expansion of a light map word M R G B (bits 12-15, 8-11, 4-7, 0-3) to 8-bit RGB:
    // (nibble << 4) | 0xf, not nibble * 17. The mono nibble is ignored.
    public static (byte r, byte g, byte b) ExpandLight(ushort w) =>
        ((byte)((((w >> 8) & 0xf) << 4) | 0xf), (byte)((((w >> 4) & 0xf) << 4) | 0xf), (byte)(((w & 0xf) << 4) | 0xf));

    // The light map as the stock client stores it by default (16-bit packed textures, 4 bits per channel): the nibble n
    // reads back as n / 15, so 8-bit n * 17. The mono nibble is ignored.
    public static (byte r, byte g, byte b) ExpandLightPacked(ushort w) =>
        ((byte)(((w >> 8) & 0xf) * 17), (byte)(((w >> 4) & 0xf) * 17), (byte)((w & 0xf) * 17));

    // The interior surface light map as the stock client stores it by default: the lightmap upload path of the interior renderer
    // (005d45d0 -> 005d7e00) packs each nibble n into a 5 bit channel 2n + 1 of a 5551 texture (the mono nibble is dropped, alpha 1),
    // which the driver reads back as (v << 3) | (v >> 2).
    public static (byte r, byte g, byte b) ExpandLight5551(ushort w) => (Expand5(((w >> 8) & 0xf) * 2 + 1), Expand5(((w >> 4) & 0xf) * 2 + 1),
                                                                         Expand5((w & 0xf) * 2 + 1));

    private static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));

    // A texture colour channel as it comes back from the stock client's 5 bit packed upload: the top 5 bits are kept,
    // then read back as (v << 3) | (v >> 2), the expansion of the GL driver (a sky colour of 194 reads back as 198).
    public static byte Quantize5(byte c) => (byte)(((c >> 3) << 3) | (c >> 5));

    // Quantizes the colour channels of an RGBA8 image in place (alpha unchanged).
    public static byte[] QuantizeRgba5(byte[] rgba)
    {
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = Quantize5(rgba[i]);
            rgba[i + 1] = Quantize5(rgba[i + 1]);
            rgba[i + 2] = Quantize5(rgba[i + 2]);
        }
        return rgba;
    }

    // 256 x 256 RGBA8 of the light map, row y = cell row y (the packed 4 bit read back).
    public byte[] LightRgba()
    {
        var rgba = new byte[Cells * Cells * 4];
        for (int i = 0; i < Cells * Cells; ++i)
        {
            var (r, g, b) = ExpandLightPacked(mLight[i]);
            rgba[i * 4] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    // docs/terrain.md 1.4: reads the light map of a `.dtb` (version 4 or 5 sections are LZH). Returns an empty array when
    // the block has none (light scale -1) or the file is malformed. Only light scale 0 is supported (all stock files).
    public static ushort[] ReadLightMap(byte[] d, int size)
    {
        try
        {
            if (d.Length < 12 || d[0] != 'G' || d[1] != 'B' || d[2] != 'L' || d[3] != 'K')
                return Array.Empty<ushort>();
            long pos = 8;
            int Int()
            {
                int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan((int)pos, 4));
                pos += 4;
                return v;
            }
            int version = Int();
            pos += 16;
            Int(); // detail count
            int lightScale = Int();
            pos += 8; // height range
            int sx = Int();
            Int();
            if (version < 4 || lightScale != 0 || sx != size)
                return Array.Empty<ushort>();
            byte[] Section(long outSize)
            {
                Int(); // byte count of the section
                var o = new byte[outSize];
                pos += Core.Lzh.LzhDecodeCounted(d.AsSpan((int)pos), o);
                return o;
            }
            Section((long)(sx + 1) * (sx + 1) * 4);
            Section((long)sx * sx * 2);
            if (version >= 2)
                for (int i = 0; i < 11; ++i)
                {
                    short n = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan((int)pos, 2));
                    pos += 2 + Math.Max(0, (int)n);
                }
            long count = (long)(sx + 1) * (sx + 1);
            byte[] raw = Section(count * 2);
            var r = new ushort[count];
            for (long i = 0; i < count; ++i)
                r[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan((int)(i * 2), 2));
            return r;
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException)
        {
            return Array.Empty<ushort>();
        }
    }
}
