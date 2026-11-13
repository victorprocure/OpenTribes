namespace OpenTribes.Client.World;

using System.Buffers.Binary;
using OpenTribes.World;

/// <summary>The render block the T2 renderer builds from the first <c>.dtb</c> block of a terrain, with the light map of that block.</summary>
/// <remarks>world-render.md 2.3 and 2.9. The render domain is the 3 x 3 block file of 256 x 256 squares (768 cells, 6144 m), every cell of which is one of the same block (2.3 "block map"), so a block with 128 or 64 squares repeats 2 x 2 or 4 x 4 inside a render block and the render repeats the terrain past the real map edge (D33).</remarks>
public sealed class TerrainData
{
    /// <summary>The squares per render block side.</summary>
    public const int Cells = 256;
    /// <summary>The render blocks per side.</summary>
    public const int BlockCount = 3;
    /// <summary>The cells per side of the render domain.</summary>
    public const int Domain = Cells * BlockCount;
    /// <summary>The side of a cell in metres: 2^scale shift (3), docs/terrain.md 1.3.</summary>
    public const double CellSize = 8.0;
    /// <summary>The number of levels, 0 to 8. A level k square has side 2^k cells.</summary>
    public const int Levels = 9;

    /// <summary>The squares per side of the source block.</summary>
    public int SourceSize { get; }
    /// <summary>The number of materials of the terrain.</summary>
    public int MaterialCount { get; }

    private readonly float[] mHeights;     // 257 x 257, 11.5 fixed point already applied
    private readonly byte[] mFlags;        // 256 x 256
    private readonly ushort[] mMaterial;   // 256 x 256
    private readonly ushort[] mLight;      // 256 x 256 raw light map words
    private readonly float[][] mMin = new float[Levels][];
    private readonly float[][] mMax = new float[Levels][];
    private readonly bool[][] mEmpty = new bool[Levels][];

    /// <summary>Builds the render block.</summary>
    /// <param name="block">The heights (N+1)^2 and squares as parsed by GridBlock.</param>
    /// <param name="materialCount">The number of materials.</param>
    /// <param name="lightMap">The light map as ((N &lt;&lt; L) + 1)^2 words (L = 0), or empty.</param>
    public TerrainData(GridBlock block, int materialCount, ReadOnlySpan<ushort> lightMap)
    {
        var n = block.Size;
        SourceSize = n;
        MaterialCount = materialCount;
        mHeights = new float[(Cells + 1) * (Cells + 1)];
        mFlags = new byte[Cells * Cells];
        mMaterial = new ushort[Cells * Cells];
        mLight = new ushort[Cells * Cells];
        var tiled = n != Cells;
        for (var y = 0; y <= Cells; ++y)
            for (var x = 0; x <= Cells; ++x)
            {
                int sx = tiled ? x % n : x, sy = tiled ? y % n : y;
                // 2.3 heights: clamp to 0 .. 2047, 11.5 fixed point: u16 = int(h * 32).
                var h = Math.Clamp((double)block.H(sx, sy), 0.0, 2047.0);
                mHeights[x + (y * (Cells + 1))] = (int)(h * 32) * 0.03125f;
            }
        var lw = n + 1;
        for (var y = 0; y < Cells; ++y)
            for (var x = 0; x < Cells; ++x)
            {
                int sx = x % n, sy = y % n;
                var si = ((sy * n) + sx) * 2;
                mFlags[(y * Cells) + x] = block.Squares[si];
                int m = block.Squares[si + 1];
                mMaterial[(y * Cells) + x] = (ushort)(m >= materialCount ? 0 : m); // 2.3 materials
                if (lightMap.Length >= lw * lw)
                    mLight[(y * Cells) + x] = lightMap[(sy * lw) + sx];
                else
                    mLight[(y * Cells) + x] = 0xffff;
            }
        BuildHierarchy();
    }

    /// <summary>Reads the height at a corner of the render block.</summary>
    /// <param name="lx">The corner x, 0 to 256.</param>
    /// <param name="ly">The corner y, 0 to 256.</param>
    /// <returns>The height.</returns>
    public float LocalHeight(int lx, int ly) => mHeights[lx + (ly * (Cells + 1))];

    // Domain index 0 .. 768 to a render block coordinate 0 .. 256: block b covers [256 b, 256 b + 256], the file is
    // one block repeated, and the last corner is the end of the last block.
    private static int Local(int g) => g >= Domain ? Cells : g & (Cells - 1);

    /// <summary>Reads the height at a corner of the render domain.</summary>
    /// <param name="gx">The corner x in cells, 0 to 768.</param>
    /// <param name="gy">The corner y in cells, 0 to 768.</param>
    /// <returns>The height.</returns>
    public float Height(int gx, int gy) => mHeights[Local(gx) + (Local(gy) * (Cells + 1))];

    /// <summary>Reads the flags of a cell.</summary>
    /// <param name="cx">The cell x.</param>
    /// <param name="cy">The cell y.</param>
    /// <returns>The flag byte.</returns>
    public byte Flags(int cx, int cy) => mFlags[((cy & (Cells - 1)) * Cells) + (cx & (Cells - 1))];
    /// <summary>Reads the material index of a cell.</summary>
    /// <param name="cx">The cell x.</param>
    /// <param name="cy">The cell y.</param>
    /// <returns>The material index.</returns>
    public int Material(int cx, int cy) => mMaterial[((cy & (Cells - 1)) * Cells) + (cx & (Cells - 1))];
    /// <summary>Tells whether a cell is flagged empty.</summary>
    /// <param name="cx">The cell x.</param>
    /// <param name="cy">The cell y.</param>
    /// <returns>True when the cell is empty.</returns>
    public bool IsEmptySquare(int cx, int cy) => ((Flags(cx, cy) >> 3) & 7) != 0;
    /// <summary>Reads the raw light map word of a cell.</summary>
    /// <param name="cx">The cell x.</param>
    /// <param name="cy">The cell y.</param>
    /// <returns>The word.</returns>
    public ushort LightWord(int cx, int cy) => mLight[((cy & (Cells - 1)) * Cells) + (cx & (Cells - 1))];

    /// <summary>Reads the lowest height in a square.</summary>
    /// <param name="level">The level of the square.</param>
    /// <param name="cx">A cell x inside the square.</param>
    /// <param name="cy">A cell y inside the square.</param>
    /// <returns>The height.</returns>
    public float MinHeight(int level, int cx, int cy) => mMin[level][(((cy >> level) & ((Cells >> level) - 1)) * (Cells >> level)) + ((cx >> level) & ((Cells >> level) - 1))];
    /// <summary>Reads the highest height in a square.</summary>
    /// <param name="level">The level of the square.</param>
    /// <param name="cx">A cell x inside the square.</param>
    /// <param name="cy">A cell y inside the square.</param>
    /// <returns>The height.</returns>
    public float MaxHeight(int level, int cx, int cy) => mMax[level][(((cy >> level) & ((Cells >> level) - 1)) * (Cells >> level)) + ((cx >> level) & ((Cells >> level) - 1))];
    /// <summary>Tells whether every cell of a square is empty.</summary>
    /// <param name="level">The level of the square.</param>
    /// <param name="cx">A cell x inside the square.</param>
    /// <param name="cy">A cell y inside the square.</param>
    /// <returns>True when the square is empty.</returns>
    public bool IsEmpty(int level, int cx, int cy) => mEmpty[level][(((cy >> level) & ((Cells >> level) - 1)) * (Cells >> level)) + ((cx >> level) & ((Cells >> level) - 1))];

    private readonly Dictionary<int, bool> mSplit = [];

    /// <summary>Tells the split diagonal of a square.</summary>
    /// <param name="level">The level of the square.</param>
    /// <param name="cx">A cell x inside the square.</param>
    /// <param name="cy">A cell y inside the square.</param>
    /// <returns>True when the square splits along the (0,0) to (1,1) corner diagonal.</returns>
    /// <remarks>Follows 00574b84 and 00574868 (2.3). Level 0 follows the checkerboard of the square indices. Levels 1 to 3 follow it when it agrees with the split of the parent. Otherwise, and always from level 4, the diagonal with the smaller maximum deviation wins, strictly: equal deviations choose the other diagonal. The deviation of a lattice point is the integer part of |plane height - height|, where the plane runs through three corners of the triangle of the point. The corner heights are the 11.5 fixed point integers and the own height of the point is in metres: the exe mixes both units and this is kept.</remarks>
    public bool SplitMain(int level, int cx, int cy)
    {
        var dim = Cells >> level;
        int ix = (cx >> level) & (dim - 1), iy = (cy >> level) & (dim - 1);
        var key = (level << 20) | (iy << 10) | ix;
        if (mSplit.TryGetValue(key, out var known))
            return known;
        var checker = ((ix ^ iy) & 1) == 0;
        var result = checker;
        if (level != 0)
        {
            var parent = level < Levels - 1 && SplitMain(level + 1, cx, cy);
            if (level > 3 || checker != parent)
            {
                SplitDeviations(level, ix, iy, out var dMain, out var dAnti);
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
        for (var dx = 0; dx <= s; ++dx)
            for (var dy = 0; dy <= s; ++dy)
            {
                var z = LocalHeight(ox + dx, oy + dy);
                float fx = (float)dx / s, fy = (float)dy / s;
                var zm = dx < dy ? a + ((c - b) * fx) + ((b - a) * fy) : a + ((d - a) * fx) + ((c - d) * fy);
                var za = dx + dy < s ? a + ((d - a) * fx) + ((b - a) * fy) : d + b - c + ((c - b) * fx) + ((c - d) * fy);
                dMain = Math.Max(dMain, Deviation(zm, z));
                dAnti = Math.Max(dAnti, Deviation(za, z));
            }
    }

    private static int Deviation(float plane, float z)
    {
        var v = Math.Abs(plane - z);
        return v > 65535f ? 65535 : (int)v;
    }

    private void BuildHierarchy()
    {
        for (var l = 0; l < Levels; ++l)
        {
            int dim = Cells >> l, s = 1 << l;
            mMin[l] = new float[dim * dim];
            mMax[l] = new float[dim * dim];
            mEmpty[l] = new bool[dim * dim];
            for (var y = 0; y < dim; ++y)
                for (var x = 0; x < dim; ++x)
                {
                    var i = (y * dim) + x;
                    if (l == 0)
                    {
                        float a = LocalHeight(x, y), b = LocalHeight(x + 1, y), c = LocalHeight(x, y + 1), d = LocalHeight(x + 1, y + 1);
                        mMin[0][i] = Math.Min(Math.Min(a, b), Math.Min(c, d));
                        mMax[0][i] = Math.Max(Math.Max(a, b), Math.Max(c, d));
                        mEmpty[0][i] = IsEmptySquare(x, y);
                    }
                    else
                    {
                        var pd = Cells >> (l - 1);
                        float mn = float.MaxValue, mx = float.MinValue;
                        var em = true;
                        for (var k = 0; k < 4; ++k)
                        {
                            var j = (((y * 2) + (k >> 1)) * pd) + (x * 2) + (k & 1);
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

    /// <summary>Expands a light map word M R G B (bits 12-15, 8-11, 4-7, 0-3) to 8 bit RGB as (nibble &lt;&lt; 4) | 0xf, not nibble * 17. The mono nibble is ignored.</summary>
    /// <param name="w">The light map word.</param>
    /// <returns>The red, green and blue bytes.</returns>
    /// <remarks>world-render.md 2.9.</remarks>
    public static (byte r, byte g, byte b) ExpandLight(ushort w) =>
        ((byte)((((w >> 8) & 0xf) << 4) | 0xf), (byte)((((w >> 4) & 0xf) << 4) | 0xf), (byte)(((w & 0xf) << 4) | 0xf));

    /// <summary>Expands a light map word the way the stock client stores it by default (16 bit packed textures, 4 bits per channel): the nibble n reads back as n / 15, so 8 bit n * 17. The mono nibble is ignored.</summary>
    /// <param name="w">The light map word.</param>
    /// <returns>The red, green and blue bytes.</returns>
    public static (byte r, byte g, byte b) ExpandLightPacked(ushort w) =>
        ((byte)(((w >> 8) & 0xf) * 17), (byte)(((w >> 4) & 0xf) * 17), (byte)((w & 0xf) * 17));

    /// <summary>Expands an interior surface light map word the way the light map upload of the stock client reads it back: each of R, G, B becomes (nibble &lt;&lt; 4) | 0xf, the mono nibble is ignored.</summary>
    /// <param name="w">The light map word.</param>
    /// <returns>The red, green and blue bytes.</returns>
    /// <remarks>The 32 bit expansion of the upload (005d45d0 to 005d6230, the branch for Use32BitTex, NoPackedTextures or no packed pixels; the packed branch 005d7e00 writes 5551). The stock client takes this branch for light maps while its palette textures stay packed (5551). The branch condition in the decompile is the same for both, so why is open (world-render.md 3.5).</remarks>
    public static (byte r, byte g, byte b) ExpandLightInterior(ushort w) => ((byte)((((w >> 8) & 0xf) << 4) | 0xf), (byte)((((w >> 4) & 0xf) << 4) | 0xf),
                                                                              (byte)(((w & 0xf) << 4) | 0xf));

    /// <summary>Quantizes a colour channel as it comes back from the 5 bit packed upload of the stock client: the top 5 bits are kept, then read back as (v &lt;&lt; 3) | (v &gt;&gt; 2), the expansion of the GL driver.</summary>
    /// <param name="c">The channel value.</param>
    /// <returns>The quantized value.</returns>
    public static byte Quantize5(byte c) => (byte)(((c >> 3) << 3) | (c >> 5));

    /// <summary>Quantizes the colour channels of an RGBA8 image in place. The alpha stays.</summary>
    /// <param name="rgba">The pixels.</param>
    /// <returns>The same array.</returns>
    public static byte[] QuantizeRgba5(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = Quantize5(rgba[i]);
            rgba[i + 1] = Quantize5(rgba[i + 1]);
            rgba[i + 2] = Quantize5(rgba[i + 2]);
        }
        return rgba;
    }

    /// <summary>Builds the 256 x 256 RGBA8 light map, row y = cell row y (the packed 4 bit read back).</summary>
    /// <returns>The pixels.</returns>
    public byte[] LightRgba()
    {
        var rgba = new byte[Cells * Cells * 4];
        for (var i = 0; i < Cells * Cells; ++i)
        {
            var (r, g, b) = ExpandLightPacked(mLight[i]);
            rgba[i * 4] = r;
            rgba[(i * 4) + 1] = g;
            rgba[(i * 4) + 2] = b;
            rgba[(i * 4) + 3] = 255;
        }
        return rgba;
    }

    /// <summary>Reads the light map of a <c>.dtb</c> (docs/terrain.md 1.4): version 4 or 5 sections are LZH. Only light scale 0 is supported (all stock files).</summary>
    /// <param name="d">The file bytes.</param>
    /// <param name="size">The squares per side of the block.</param>
    /// <returns>The light map words, or an empty array when the block has none (light scale -1) or the file is malformed.</returns>
    public static ushort[] ReadLightMap(byte[] d, int size)
    {
        try
        {
            if (d.Length < 12 || d[0] != 'G' || d[1] != 'B' || d[2] != 'L' || d[3] != 'K')
                return [];
            long pos = 8;
            int Int()
            {
                var v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan((int)pos, 4));
                pos += 4;
                return v;
            }
            var version = Int();
            pos += 16;
            Int(); // detail count
            var lightScale = Int();
            pos += 8; // height range
            var sx = Int();
            Int();
            if (version < 4 || lightScale != 0 || sx != size)
                return [];
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
                for (var i = 0; i < 11; ++i)
                {
                    var n = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan((int)pos, 2));
                    pos += 2 + Math.Max(0, (int)n);
                }
            var count = (long)(sx + 1) * (sx + 1);
            var raw = Section(count * 2);
            var r = new ushort[count];
            for (long i = 0; i < count; ++i)
                r[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan((int)(i * 2), 2));
            return r;
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException)
        {
            return [];
        }
    }
}
