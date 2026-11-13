namespace OpenTribes.World;

using System.Buffers.Binary;
using OpenTribes.Core;
using static OpenTribes.Core.Fpu;

/// <summary>One terrain block: a grid of heights plus a flags and material byte pair per square.</summary>
public sealed class GridBlock
{
    /// <summary>The number of squares per side. Blocks are square in all data.</summary>
    public int Size;
    /// <summary>The corner heights, (Size + 1)^2 values in row-major order.</summary>
    public float[] Heights = [];
    /// <summary>The per-square data, Size^2 pairs of flags byte and material byte.</summary>
    public byte[] Squares = [];

    /// <summary>Reads a corner height.</summary>
    /// <param name="x">The corner column, 0 to <see cref="Size"/>.</param>
    /// <param name="y">The corner row, 0 to <see cref="Size"/>.</param>
    /// <returns>The height at the corner.</returns>
    public float H(int x, int y) => Heights[((long)y * (Size + 1)) + x];
    /// <summary>Tests whether a square is empty (no surface).</summary>
    /// <param name="x">The square column.</param>
    /// <param name="y">The square row.</param>
    /// <returns>True when the square's empty bits (flags bits 3 to 5) are set.</returns>
    public bool Empty(int x, int y) => ((Squares[(((long)y * Size) + x) * 2] >> 3) & 7) != 0;

    private static bool Get(byte[] d, ref long pos, out int v)
    {
        v = 0;
        if (pos + 4 > d.Length)
            return false;
        v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan((int)pos, 4));
        pos += 4;
        return true;
    }

    private static bool GetF(byte[] d, ref long pos, out float v)
    {
        v = 0;
        if (pos + 4 > d.Length)
            return false;
        v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)pos, 4));
        pos += 4;
        return true;
    }

    private static byte[] LzhSection(byte[] d, ref long pos, long outSize, ref bool ok)
    {
        if (!ok || !Get(d, ref pos, out _) || pos > d.Length)
        {
            ok = false;
            return new byte[outSize];
        }
        var output = new byte[outSize];
        pos += Lzh.LzhDecodeCounted(d.AsSpan((int)pos), output);
        return output;
    }

    /// <summary>Parses a block file.</summary>
    /// <param name="d">The decoded file bytes.</param>
    /// <param name="output">Receives the block.</param>
    /// <returns>True on success.</returns>
    public static bool Parse(byte[] d, GridBlock output) => Parse(d, output, out _);

    /// <summary>Parses a block file and reports where it ends.</summary>
    /// <param name="d">The decoded file bytes.</param>
    /// <param name="output">Receives the block.</param>
    /// <param name="endOffset">Receives the offset just past the parsed data.</param>
    /// <returns>True on success.</returns>
    public static bool Parse(byte[] d, GridBlock output, out long endOffset)
    {
        endOffset = 0;
        if (d.Length < 12 || d[0] != 'G' || d[1] != 'B' || d[2] != 'L' || d[3] != 'K')
            return false;
        long pos = 8;
        Get(d, ref pos, out var versionI);
        var version = (uint)versionI;
        pos += 16; // name
        if (!Get(d, ref pos, out _) || !Get(d, ref pos, out _) || !GetF(d, ref pos, out _) || !GetF(d, ref pos, out _) ||
            !Get(d, ref pos, out var sx) || !Get(d, ref pos, out var sy) || sx <= 0 || sx != sy || sx > 4096)
            return false;
        output.Size = sx;
        var hcount = (long)(sx + 1) * (sx + 1);
        var ok = true;
        if (version >= 4)
        {
            var h = LzhSection(d, ref pos, hcount * 4, ref ok);
            output.Squares = LzhSection(d, ref pos, (long)sx * sx * 2, ref ok);
            output.Heights = new float[hcount];
            for (long i = 0; i < hcount; ++i)
                output.Heights[i] = BinaryPrimitives.ReadSingleLittleEndian(h.AsSpan((int)(i * 4), 4));
        }
        else if (version == 0)
        {
            if (pos + (hcount * 4) + ((long)sx * sx * 2) > d.Length)
                return false;
            output.Heights = new float[hcount];
            for (long i = 0; i < hcount; ++i)
                output.Heights[i] = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)(pos + (i * 4)), 4));
            pos += hcount * 4;
            output.Squares = d.AsSpan((int)pos, sx * sx * 2).ToArray();
            pos += (long)sx * sx * 2;
        }
        else
        {
            return false; // delta-row blocks (v 1-3) do not occur in base data
        }
        endOffset = pos;
        return ok;
    }
}

/// <summary>A terrain: the .dtf height map, its blocks and its material list, with height, surface and line queries.</summary>
/// <remarks>Follows docs/terrain.md. Double operations use the current <see cref="Fpu"/> rounding mode and float stores truncate in the script mode, as in the original. Port of src/world/terrain.cpp.</remarks>
public sealed class Terrain
{
    private int mDetail, mScale, mWidth, mHeight;
    private float mHmin, mHmax;
    private int[] mMap = [];
    private readonly Dictionary<int, GridBlock> mBlocks = [];
    private readonly List<string> mBlockFiles = []; // block list order
    private string mMaterialList = "";
    // Held while the terrain lives: the .dtf, the blocks and the .dml. Call ReleaseLocks when the owner is deleted.
    private readonly ResourceLocks mLocks = new();
    private Vec3 mPos;

    /// <summary>Sets the world position of the terrain origin.</summary>
    /// <param name="p">The new position.</param>
    public void SetPosition(Vec3 p) => mPos = p;
    /// <summary>Gets the world position of the terrain origin.</summary>
    /// <returns>The position.</returns>
    public Vec3 Position() => mPos;
    /// <summary>Gets the name of the material list (.dml) file.</summary>
    /// <returns>The file name.</returns>
    public string MaterialListName() => mMaterialList;
    /// <summary>Gets the lowest height in the terrain.</summary>
    /// <returns>The height.</returns>
    public float Hmin() => mHmin;
    /// <summary>Gets the highest height in the terrain.</summary>
    /// <returns>The height.</returns>
    public float Hmax() => mHmax;

    // Truncation as crt_ftol does. A value out of range gives 0x80000000 as cvttsd2si does.
    private static int Ftol(double v) => v is > (-2147483649.0) and < 2147483648.0 ? (int)v : int.MinValue;

    /// <summary>Interpolates the 4.2 height on square (sx, sy) at square-relative (u, v).</summary>
    /// <remarks>Every intermediate runs in the current rounding mode and the result is stored as float.</remarks>
    private static float Interp(GridBlock b, int sx, int sy, float s, float u, float v)
    {
        double h00 = b.H(sx, sy), h10 = b.H(sx + 1, sy), h01 = b.H(sx, sy + 1), h11 = b.H(sx + 1, sy + 1);
        double r;
        if (((sx ^ sy) & 1) == 0)
        {
            if (u <= v)
                r = Add(Div(Add(Mul(Sub(h00, h01), Sub(s, v)), Mul(Sub(h11, h01), u)), s), h01);
            else
                r = Add(Div(Add(Mul(Sub(h00, h10), Sub(s, u)), Mul(Sub(h11, h10), v)), s), h10);
        }
        else
        {
            if (Sub(s, u) > v)
                r = Add(Div(Add(Mul(Sub(h10, h00), u), Mul(Sub(h01, h00), v)), s), h00);
            else
                r = Add(Div(Add(Mul(Sub(h01, h11), Sub(s, u)), Mul(Sub(h10, h11), Sub(s, v))), s), h11);
        }
        return Store(r);
    }

    private static void RawNormal(GridBlock b, int sx, int sy, float s, float u, float v, Span<float> n)
    {
        float h00 = b.H(sx, sy), h10 = b.H(sx + 1, sy), h01 = b.H(sx, sy + 1), h11 = b.H(sx + 1, sy + 1);
        if (((sx ^ sy) & 1) == 0)
        {
            if (u <= v)
            {
                n[0] = SubF(h01, h11);
                n[1] = SubF(h00, h01);
            }
            else
            {
                n[0] = SubF(h00, h10);
                n[1] = SubF(h10, h11);
            }
        }
        else
        {
            if (Sub(s, u) > v)
            {
                n[0] = SubF(h00, h10);
                n[1] = SubF(h00, h01);
            }
            else
            {
                n[0] = SubF(h01, h11);
                n[1] = SubF(h10, h11);
            }
        }
        n[2] = s;
    }

    private static Vec3 Normalize(ReadOnlySpan<float> n)
    {
        var len = Store(Sqrt(Add(Add(Mul(n[0], n[0]), Mul(n[1], n[1])), Mul(n[2], n[2]))));
        var inv = Store(Div(1.0, len));
        return new Vec3(Store(Mul(inv, n[0])), Store(Mul(inv, n[1])), Store(Mul(inv, n[2])));
    }

    private static bool Get(byte[] d, ref long pos, out int v)
    {
        v = 0;
        if (pos + 4 > d.Length)
            return false;
        v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan((int)pos, 4));
        pos += 4;
        return true;
    }

    private static void GetInto(byte[] d, ref long pos, ref int v)
    {
        if (Get(d, ref pos, out var x))
            v = x;
    }

    private static void GetInto(byte[] d, ref long pos, ref float v)
    {
        if (pos + 4 > d.Length)
            return;
        v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)pos, 4));
        pos += 4;
    }

    /// <summary>Loads a terrain and its blocks through the resource manager.</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="dtfName">The .dtf file name.</param>
    /// <returns>The terrain, or null when a file is missing or malformed.</returns>
    /// <remarks>Locks the .dtf (SimTerrain_load 0051792c), the block list (GridBlockList 00567270) and the material list with its extension replaced by .dml (00517e04), see docs/resources.md 8.7.</remarks>
    public static Terrain? Load(ResourceManager res, string dtfName)
    {
        if (!res.Read(dtfName, out var d) || d.Length < 12 || d[0] != 'G' || d[1] != 'F' || d[2] != 'I' || d[3] != 'L')
            return null;
        long pos = 8;
        int version = 0, n = 0;
        GetInto(d, ref pos, ref version);
        GetInto(d, ref pos, ref n);
        if (pos + (uint)n > d.Length)
            return null;
        var t = new Terrain
        {
            mMaterialList = Reader.CString(d, pos, (uint)n)
        };
        pos += (uint)n;
        var nextId = 0;
        GetInto(d, ref pos, ref nextId);
        GetInto(d, ref pos, ref t.mDetail);
        GetInto(d, ref pos, ref t.mScale);
        pos += 24 + 8;
        GetInto(d, ref pos, ref t.mHmin);
        GetInto(d, ref pos, ref t.mHmax);
        GetInto(d, ref pos, ref t.mWidth);
        GetInto(d, ref pos, ref t.mHeight);
        if (version == 1)
            pos += 4;
        if (t.mWidth <= 0 || t.mHeight <= 0 || t.mWidth > 64 || t.mHeight > 64)
            return null;
        t.mMap = new int[t.mWidth * t.mHeight];
        for (var i = 0; i < t.mMap.Length; ++i)
            if (!Get(d, ref pos, out t.mMap[i]))
                return null;
        var count = 0;
        GetInto(d, ref pos, ref count);
        var dot = dtfName.LastIndexOf('.');
        var stem = dtfName[..(dot < 0 ? dtfName.Length : dot)];
        for (uint i = 0; i < (uint)count; ++i)
        {
            if (!Get(d, ref pos, out var id) || !Get(d, ref pos, out var len))
                return null;
            pos += (uint)len;
            var name = stem + "#" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".dtb";
            if (name.Length > 511) // char name[512]
                name = name[..511];
            var blk = new GridBlock();
            if (!res.Read(name, out var b) || !GridBlock.Parse(b, blk))
                return null;
            t.mBlocks[id] = blk;
            t.mBlockFiles.Add(name);
        }
        // Resource loads, held while the terrain lives and kept until purge after it (docs/resources.md
        // 8.7): the .dtf (SimTerrain_load 0051792c), the blocks (GridBlockList 00567270, every stock
        // terrain has one) and the material list with its extension replaced by .dml (00517e04), no textures.
        t.mLocks.Load(res, dtfName);
        foreach (var name in t.mBlockFiles)
            t.mLocks.Load(res, name);
        var dml = t.mMaterialList;
        var mdot = dml.LastIndexOf('.');
        t.mLocks.Load(res, (mdot <= 0 ? dml : dml[..mdot]) + ".dml");
        return t;
    }

    /// <summary>Unlocks the resources the terrain holds.</summary>
    public void ReleaseLocks() => mLocks.Release();

    private static readonly uint[] sCrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; ++n)
        {
            var c = n;
            for (var k = 0; k < 8; ++k)
                c = (c & 1) != 0 ? (c >> 1) ^ 0xedb88320u : c >> 1;
            table[n] = c;
        }
        return table;
    }

    // CRC-32, reflected polynomial 0xedb88320, no final inversion (0x00588324).
    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (var b in data)
            crc = sCrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
        return crc;
    }

    /// <summary>Reads the surface at a terrain-space point.</summary>
    /// <param name="lx">The x relative to the terrain origin.</param>
    /// <param name="ly">The y relative to the terrain origin.</param>
    /// <param name="h">Receives the height.</param>
    /// <param name="n">Receives the unit normal, three floats.</param>
    /// <param name="material">Receives the square's material byte.</param>
    /// <returns>False off the map and on empty squares.</returns>
    /// <remarks>Follows GridFile_getSurfaceInfo (0056c138, docs/collision.md 5.1 step 4).</remarks>
    public bool SurfaceInfo(float lx, float ly, ref float h, Span<float> n, ref byte material)
    {
        var shift = mDetail + mScale - 1;
        if (!(lx > -2147483648.0f && lx < 2147483648.0f && ly > -2147483648.0f && ly < 2147483648.0f))
            return false;
        int bx = Ftol(lx) >> shift, by = Ftol(ly) >> shift;
        if (bx < 0 || by < 0 || bx >= mWidth || by >= mHeight)
            return false;
        if (!mBlocks.TryGetValue(mMap[(by * mWidth) + bx], out var b))
            return false;
        float px = Store(Sub(lx, bx << shift)), py = Store(Sub(ly, by << shift));
        int sx = Ftol(px) >> mScale, sy = Ftol(py) >> mScale;
        if (sx < 0 || sy < 0 || sx >= b.Size || sy >= b.Size || b.Empty(sx, sy))
            return false;
        var s = Store(1 << mScale);
        float u = Store(Sub(px, sx << mScale)), v = Store(Sub(py, sy << mScale));
        h = Interp(b, sx, sy, s, u, v);
        Span<float> raw = stackalloc float[3];
        RawNormal(b, sx, sy, s, u, v, raw);
        var unit = Normalize(raw);
        n[0] = unit.X;
        n[1] = unit.Y;
        n[2] = unit.Z;
        material = b.Squares[((((long)sy * b.Size) + sx) * 2) + 1];
        return true;
    }

    /// <summary>Computes the verification CRC over the .dtf, the block files and the material list.</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="dtfName">The .dtf file name.</param>
    /// <param name="output">Receives the CRC.</param>
    /// <returns>False when a file cannot be read.</returns>
    /// <remarks>Follows the stock terrain CRC (00517048, docs/terrain.md 3).</remarks>
    public bool Crc(ResourceManager res, string dtfName, ref uint output)
    {
        var c = 0xffffffffu;
        if (!res.Read(dtfName, out var d))
            return false;
        c = Crc32(d, c);
        foreach (var name in mBlockFiles)
        {
            if (!res.Read(name, out d))
                return false;
            c = Crc32(d, c);
        }
        if (!res.Read(mMaterialList, out d))
            return false;
        output = Crc32(d, c);
        return true;
    }

    /// <summary>Writes the world-space bounding box of the terrain.</summary>
    /// <param name="box">Receives min xyz then max xyz, six floats.</param>
    /// <remarks>Follows 005193cc. Used for container queries.</remarks>
    public void WorldBox(Span<float> box)
    {
        var shift = mDetail + mScale - 1;
        box[0] = mPos.X;
        box[1] = mPos.Y;
        box[2] = AddF(mHmin, mPos.Z);
        box[3] = Store(Add(mWidth << shift, mPos.X));
        box[4] = Store(Add(mHeight << shift, mPos.Y));
        box[5] = AddF(mHmax, mPos.Z);
    }

    /// <summary>Reads the terrain height at a world point.</summary>
    /// <param name="x">The world x.</param>
    /// <param name="y">The world y.</param>
    /// <param name="output">Receives the height.</param>
    /// <returns>False where there is no terrain.</returns>
    /// <remarks>Follows 005183e8.</remarks>
    public bool Height(float x, float y, ref float output)
    {
        Vec3 n = default;
        var material = 0;
        return Surface(x, y, ref output, ref n, ref material);
    }

    /// <summary>Reads the terrain height and normal at a world point.</summary>
    /// <param name="x">The world x.</param>
    /// <param name="y">The world y.</param>
    /// <param name="output">Receives the height.</param>
    /// <param name="normal">Receives the unit normal.</param>
    /// <returns>False where there is no terrain.</returns>
    public bool Height(float x, float y, ref float output, ref Vec3 normal)
    {
        Vec3 n = default;
        var material = 0;
        if (!Surface(x, y, ref output, ref n, ref material))
            return false;
        normal = n;
        return true;
    }

    /// <summary>Reads height, normal and material at a world point.</summary>
    /// <param name="x">The world x.</param>
    /// <param name="y">The world y.</param>
    /// <param name="output">Receives the height.</param>
    /// <param name="normal">Receives the unit normal.</param>
    /// <param name="material">Receives the material index.</param>
    /// <returns>False where there is no terrain.</returns>
    public bool Surface(float x, float y, ref float output, ref Vec3 normal, ref int material) =>
        SurfaceLocal(Store(Sub(x, mPos.X)), Store(Sub(y, mPos.Y)), ref output, ref normal, ref material);

    /// <summary>Reads height, normal and material at a point relative to the terrain origin.</summary>
    /// <param name="lx">The x relative to the terrain origin.</param>
    /// <param name="ly">The y relative to the terrain origin.</param>
    /// <param name="output">Receives the height.</param>
    /// <param name="normal">Receives the unit normal.</param>
    /// <param name="material">Receives the material index.</param>
    /// <returns>False where there is no terrain.</returns>
    public bool SurfaceLocal(float lx, float ly, ref float output, ref Vec3 normal, ref int material)
    {
        var shift = mDetail + mScale - 1;
        int bx = Ftol(lx) >> shift, by = Ftol(ly) >> shift;
        if (bx < 0 || by < 0 || bx >= mWidth || by >= mHeight)
            return false;
        if (!mBlocks.TryGetValue(mMap[(by * mWidth) + bx], out var b))
            return false;
        float px = Store(Sub(lx, bx << shift)), py = Store(Sub(ly, by << shift));
        int sx = Ftol(px) >> mScale, sy = Ftol(py) >> mScale;
        if (sx < 0 || sy < 0 || sx >= b.Size || sy >= b.Size || b.Empty(sx, sy))
            return false;
        var s = Store(1 << mScale);
        float u = Store(Sub(px, sx << mScale)), v = Store(Sub(py, sy << mScale));
        output = Interp(b, sx, sy, s, u, v);
        Span<float> n = stackalloc float[3];
        RawNormal(b, sx, sy, s, u, v, n);
        normal = Normalize(n);
        material = b.Squares[((((long)sy * b.Size) + sx) * 2) + 1];
        return true;
    }

    // Line family of the block and square walks (00567bc7, 005683de; docs/collision.md 9 section 3 step 2): the parameter of
    // the first crossing and the step between crossings.
    private static void Family(float vs, float ve, out float t, out float dt)
    {
        if (ve == vs)
        {
            t = dt = 1e20f;
            return;
        }
        dt = Store(Div(1.0, Sub(ve, vs)));
        if (dt < 0)
        {
            t = Store(Mul(Sub(Math.Floor((double)vs), vs), dt));
            dt = -dt;
        }
        else
        {
            t = Store(Mul(Sub(Math.Ceiling((double)vs), vs), dt));
        }
    }

    // crt_ftol of a value that may not fit: the original stores 0x80000000.
    private static int FtolSafe(double v)
    {
        if (v is not (> (-2147483648.0) and < 2147483648.0))
            return int.MinValue;
        return (int)v;
    }

    private struct TriPlane
    {
        public Float3 N;
        public float D;
    }

    // Unit normal and distance of a triangle plane (0056cca0.., section 4.4 step 10).
    private static TriPlane PlaneOf(ReadOnlySpan<float> raw, ReadOnlySpan<float> v)
    {
        var pl = new TriPlane();
        var len = Store(Sqrt(Add(Add(Mul(raw[0], raw[0]), Mul(raw[1], raw[1])), Mul(raw[2], raw[2]))));
        var r = Store(Div(1.0, len));
        for (var i = 0; i < 3; ++i)
            pl.N[i] = Store(Mul(r, raw[i]));
        pl.D = Store(-Add(Add(Mul(v[0], pl.N[0]), Mul(v[1], pl.N[1])), Mul(v[2], pl.N[2])));
        return pl;
    }

    // Plane_intersectSegment (005edc24): out is written only on success.
    private static bool SegPlane(in TriPlane pl, ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> output)
    {
        Span<float> dv = stackalloc float[3];
        for (var i = 0; i < 3; ++i)
            dv[i] = Store(Sub(b[i], a[i]));
        var den80 = Add(Add(Mul(pl.N[0], dv[0]), Mul(pl.N[1], dv[1])), Mul(pl.N[2], dv[2]));
        if (den80 == 0)
            return false;
        var den = Store(den80);
        var num = Store(-Add(Add(Add(Mul(pl.N[0], a[0]), Mul(pl.N[1], a[1])), Mul(pl.N[2], a[2])), pl.D));
        if (den > 0 ? (num < 0 || num > den) : (num > 0 || num < den))
            return false;
        if (den == 0)
            return false; // a denominator below the f32 range: the original divides by zero (D11)
        for (var i = 0; i < 3; ++i)
            output[i] = Store(Add(Div(Mul(dv[i], num), den), a[i]));
        return true;
    }

    // Square walk inside one block (005680ec, line mode; docs/collision.md 9 section 4). a and b are the block-relative piece.
    // A side change at a line crossing is a hit at once (F1). Only the end of the walk tests the triangle bounds.
    private bool BlockLine(GridBlock blk, ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> hit, Span<float> normal,
                           ref int material)
    {
        int S = mScale, N = blk.Size;
        var sq = Store(1 << S);
        float inv = Store(Div(1.0, sq)), half = Store(Mul(0.5, inv));
        var row = N + 1;
        long nHeights = blk.Heights.Length, nSquares = blk.Squares.Length;
        if (N < 1 || nHeights < (long)row * row || nSquares < (long)N * N * 2)
            return false;
        var oob = false;
        // Height read with both indices clamped to [0, N].
        float H(int x, int y)
        {
            x = Math.Min(Math.Max(x, 0), N);
            y = Math.Min(Math.Max(y, 0), N);
            return blk.Heights[((long)y * row) + x];
        }
        // Flag byte (0x00567974): indices clamped to [0, N], so row and column N read past the square array in the
        // original. Those reads end the test without a hit (section 9 Q1).
        int Flags(int x, int y)
        {
            x = Math.Min(Math.Max(x, 0), N);
            y = Math.Min(Math.Max(y, 0), N);
            var i = (((long)y * N) + x) * 2;
            if (i < 0 || i >= nSquares)
            {
                oob = true;
                return 0;
            }
            return blk.Squares[i];
        }
        // Corner heights through the unclamped pointer (0x00564820).
        bool Corners(int ix, int iy, Span<float> h)
        {
            var i = ((long)iy * row) + ix;
            if (i < 0 || i + row + 1 >= nHeights)
            {
                oob = true;
                return false;
            }
            h[0] = blk.Heights[i];
            h[1] = blk.Heights[i + 1];
            h[2] = blk.Heights[i + row];
            h[3] = blk.Heights[i + row + 1];
            return true;
        }
        int ClampSq(double v)
        {
            var q = FtolSafe(v) >> S;
            return q >= N ? N - 1 : q < 0 ? 0 : q;
        }
        int sx = ClampSq(a[0]), sy = ClampSq(a[1]), ex = ClampSq(b[0]), ey = ClampSq(b[1]);
        var hs = Interp(blk, sx, sy, sq, Store(Sub(a[0], sx << S)), Store(Sub(a[1], sy << S)));
        var he = Interp(blk, ex, ey, sq, Store(Sub(b[0], ex << S)), Store(Sub(b[1], ey << S)));
        var above = a[2] > hs;
        Span<float> d = stackalloc float[3];
        for (var i = 0; i < 3; ++i)
            d[i] = Store(Sub(b[i], a[i]));
        Span<float> t = stackalloc float[4], dt = stackalloc float[4];
        Family(Store(Mul(a[1], inv)), Store(Mul(b[1], inv)), out t[0], out dt[0]);
        Family(Store(Mul(a[0], inv)), Store(Mul(b[0], inv)), out t[1], out dt[1]);
        Family(Store(Mul(Sub(a[0], a[1]), half)), Store(Mul(Sub(b[0], b[1]), half)), out t[2], out dt[2]);
        Family(Store(Mul(Add(a[0], a[1]), half)), Store(Mul(Add(b[0], b[1]), half)), out t[3], out dt[3]);
        Span<float> q = stackalloc float[3], h4 = stackalloc float[4], raw = stackalloc float[3], v = stackalloc float[3];
        // Every iteration advances one family; a step that does not advance (dt of 0 after the f32 store) would never
        // end in the original (D11).
        for (var guard = 0; guard < 1 << 20; ++guard)
        {
            var tm = 1.0f;
            var k = -1;
            for (var i = 0; i < 4; ++i)
                if (t[i] < tm)
                {
                    k = i;
                    tm = t[i];
                }
            if (k < 0)
                break;
            t[k] = Store(Add(dt[k], t[k]));
            for (var i = 0; i < 3; ++i)
                q[i] = Store(Add(Mul(d[i], tm), a[i]));
            float gx = Store(Mul(q[0], inv)), gy = Store(Mul(q[1], inv));
            int ix, iy;
            float hA, hB, fr;
            if (k == 0)
            {
                ix = FtolSafe(gx);
                iy = FtolSafe(Math.Floor(Add(gy, 0.5)));
                hA = H(ix, iy);
                hB = H(ix + 1, iy);
                fr = Store(Sub(gx, ix));
            }
            else if (k == 1)
            {
                iy = FtolSafe(gy);
                ix = FtolSafe(Math.Floor(Add(gx, 0.5)));
                hA = H(ix, iy);
                hB = H(ix, iy + 1);
                fr = Store(Sub(gy, iy));
            }
            else if (k == 2)
            {
                ix = FtolSafe(gx);
                iy = FtolSafe(gy);
                hA = H(ix, iy);
                hB = H(ix + 1, iy + 1);
                fr = Store(Sub(gx, ix));
            }
            else
            {
                ix = FtolSafe(gx);
                iy = FtolSafe(gy);
                hA = H(ix, iy + 1);
                hB = H(ix + 1, iy);
                fr = Store(Sub(gx, ix));
            }
            var hedge = Store(Add(Mul(Sub(hB, hA), fr), hA));
            var now = q[2] > hedge;
            if (now == above)
                continue;
            if (k == 0 && d[1] > 0)
                iy -= 1;
            else if (k == 1 && d[0] > 0)
                ix -= 1;
            var fl = Flags(ix, iy);
            if (oob)
                return false;
            if (((fl >> 3) & 7) != 0)
            {
                above = !above;
                continue;
            }
            q[2] = hedge;
            var par = (ix ^ iy) & 1;
            // 0 = LR, 1 = UL, 2 = LL, 3 = UR (section 4.4 step 9).
            int tri;
            if (k == 0)
                tri = d[1] < 0 ? (par != 0 ? 2 : 0) : (par != 0 ? 3 : 1);
            else if (k == 1)
                tri = d[0] < 0 ? (par != 0 ? 2 : 1) : (par != 0 ? 3 : 0);
            else if (k == 2)
                tri = d[0] > d[1] ? 1 : 0;
            else
                tri = -d[1] < d[0] ? 2 : 3;
            if (!Corners(ix, iy, h4))
                return false;
            raw[0] = 0;
            raw[1] = 0;
            raw[2] = sq;
            if (tri == 1)
            {
                raw[0] = Store(Sub(h4[2], h4[3]));
                raw[1] = Store(Sub(h4[0], h4[2]));
            }
            else if (tri == 0)
            {
                raw[0] = Store(Sub(h4[0], h4[1]));
                raw[1] = Store(Sub(h4[1], h4[3]));
            }
            else if (tri == 2)
            {
                raw[0] = Store(Sub(h4[0], h4[1]));
                raw[1] = Store(Sub(h4[0], h4[2]));
            }
            else
            {
                raw[0] = Store(Sub(h4[2], h4[3]));
                raw[1] = Store(Sub(h4[1], h4[3]));
            }
            if (tri < 2)
            {
                v[0] = Store(ix << S);
                v[1] = Store(iy << S);
                v[2] = H(ix, iy);
            }
            else
            {
                v[0] = Store(ix << S);
                v[1] = Store((iy + 1) << S);
                v[2] = H(ix, iy + 1);
            }
            var pl = PlaneOf(raw, v);
            q.CopyTo(hit);
            SegPlane(pl, a, b, hit); // result flag ignored: a failed test keeps the crossing point
            ((ReadOnlySpan<float>)pl.N).CopyTo(normal);
            var mi = ((((long)Math.Min(Math.Max(iy, 0), N) * N) + Math.Min(Math.Max(ix, 0), N)) * 2) + 1;
            material = mi < nSquares ? blk.Squares[mi] : 0;
            return true;
        }
        // End of the walk (0x0056a1f3).
        if ((b[2] > he) == above)
            return false;
        if (((Flags(ex, ey) >> 3) & 7) != 0)
            return false;
        ReadOnlySpan<float> o = [Store(ex << S), Store(ey << S), 0.0f];
        Span<float> sa = stackalloc float[3], sb = stackalloc float[3];
        for (var i = 0; i < 3; ++i)
        {
            sa[i] = Store(Sub(a[i], o[i]));
            sb[i] = Store(Sub(b[i], o[i]));
        }
        Span<float> h = stackalloc float[4];
        if (!Corners(ex, ey, h))
            return false;
        float h00 = h[0], h10 = h[1], h01 = h[2], h11 = h[3];
        var parity = (ex ^ ey) & 1;
        const float eps = 0.0001f;
        Span<float> p = stackalloc float[3];
        var found = false;
        // GridSquare_intersectLine (0x0056cebc).
        if (parity == 0)
        {
            ReadOnlySpan<float> n1 = [Store(Sub(h01, h11)), Store(Sub(h00, h01)), sq];
            ReadOnlySpan<float> v0 = [0, 0, h00];
            if (SegPlane(PlaneOf(n1, v0), sa, sb, p) && p[0] > -eps && Add(sq, eps) > p[1] && !(p[0] > p[1]))
            {
                found = true;
            }
            else
            {
                ReadOnlySpan<float> n2 = [Store(Sub(h00, h10)), Store(Sub(h10, h11)), sq];
                found = SegPlane(PlaneOf(n2, v0), sa, sb, p) && p[0] > p[1];
            }
        }
        else
        {
            ReadOnlySpan<float> n1 = [Store(Sub(h00, h10)), Store(Sub(h00, h01)), sq];
            ReadOnlySpan<float> v0 = [0, sq, h01];
            if (SegPlane(PlaneOf(n1, v0), sa, sb, p) && p[0] > -eps && p[1] > -eps && Sub(sq, p[0]) > p[1])
            {
                found = true;
            }
            else
            {
                ReadOnlySpan<float> n2 = [Store(Sub(h01, h11)), Store(Sub(h10, h11)), sq];
                found = SegPlane(PlaneOf(n2, v0), sa, sb, p) && !(Sub(sq, p[0]) > p[1]);
            }
        }
        if (!found)
            return false;
        // Normal of the triangle under the hit (0x0056cc38).
        Span<float> rawEnd = [0, 0, sq];
        if (parity == 0)
        {
            if (p[0] > p[1])
            {
                rawEnd[0] = Store(Sub(h00, h10));
                rawEnd[1] = Store(Sub(h10, h11));
            }
            else
            {
                rawEnd[0] = Store(Sub(h01, h11));
                rawEnd[1] = Store(Sub(h00, h01));
            }
        }
        else
        {
            if (Sub(sq, p[0]) > p[1])
            {
                rawEnd[0] = Store(Sub(h00, h10));
                rawEnd[1] = Store(Sub(h00, h01));
            }
            else
            {
                rawEnd[0] = Store(Sub(h01, h11));
                rawEnd[1] = Store(Sub(h10, h11));
            }
        }
        ReadOnlySpan<float> zero = [0, 0, 0];
        var plEnd = PlaneOf(rawEnd, zero);
        ((ReadOnlySpan<float>)plEnd.N).CopyTo(normal);
        for (var i = 0; i < 3; ++i)
            hit[i] = Store(Add(o[i], p[i]));
        material = blk.Squares[((((long)ey * N) + ex) * 2) + 1];
        return true;
    }

    /// <summary>Finds the first terrain hit along a segment.</summary>
    /// <param name="start">The segment start in world space.</param>
    /// <param name="end">The segment end in world space.</param>
    /// <param name="hit">Receives the hit point.</param>
    /// <param name="normal">Receives the surface normal at the hit.</param>
    /// <returns>True on a hit.</returns>
    /// <remarks>Follows the line test at 005679f0.</remarks>
    public bool CastLine(Vec3 start, Vec3 end, ref Vec3 hit, ref Vec3 normal)
    {
        var material = 0;
        return CastLine(start, end, ref hit, ref normal, ref material);
    }

    /// <summary>Finds the first terrain hit along a segment and reports the material.</summary>
    /// <param name="start">The segment start in world space.</param>
    /// <param name="end">The segment end in world space.</param>
    /// <param name="hitOut">Receives the hit point.</param>
    /// <param name="normalOut">Receives the surface normal at the hit.</param>
    /// <param name="material">Receives the material of the hit square.</param>
    /// <returns>True on a hit.</returns>
    /// <remarks>Follows GridCollision_collideLine (005679f0) and the block walk (00567ae8), docs/collision.md 9 section 3. The segment is the caller's and is not clipped to the terrain box (F4).</remarks>
    public bool CastLine(Vec3 start, Vec3 end, ref Vec3 hitOut, ref Vec3 normalOut, ref int material)
    {
        using var chop = ChopScope();
        ReadOnlySpan<float> pos = [mPos.X, mPos.Y, mPos.Z];
        ReadOnlySpan<float> ws = [start.X, start.Y, start.Z];
        ReadOnlySpan<float> we = [end.X, end.Y, end.Z];
        Span<float> s = stackalloc float[3], e = stackalloc float[3], d = stackalloc float[3], cur = stackalloc float[3];
        for (var i = 0; i < 3; ++i)
        {
            if (!float.IsFinite(ws[i]) || !float.IsFinite(we[i]))
                return false;
            s[i] = Store(Sub(ws[i], pos[i]));
            e[i] = Store(Sub(we[i], pos[i]));
        }
        var B = mScale + mDetail - 1;
        var inv = Store(Div(1.0, 1 << B));
        for (var i = 0; i < 3; ++i)
        {
            d[i] = Store(Sub(e[i], s[i]));
            cur[i] = s[i];
        }
        Span<float> t = [0, 0, 0], dt = [0, 0, 0];
        Family(Store(Mul(s[1], inv)), Store(Mul(e[1], inv)), out t[0], out dt[0]);
        Family(Store(Mul(s[0], inv)), Store(Mul(e[0], inv)), out t[1], out dt[1]);
        var visited = false;
        Span<float> p = stackalloc float[3], a = stackalloc float[3], b = stackalloc float[3];
        Span<float> h = stackalloc float[3], n = stackalloc float[3], o = stackalloc float[3];
        for (var guard = 0; guard < 1 << 20; ++guard)
        {
            float tc;
            int k;
            if (t[0] < t[1])
            {
                tc = t[0];
                k = 0;
            }
            else
            {
                tc = t[1];
                k = 1;
            }
            if (!(tc < 1.0f))
            {
                tc = 1.0f;
                k = 2;
            }
            t[k] = Store(Add(dt[k], t[k]));
            for (var i = 0; i < 3; ++i)
            {
                p[i] = Store(Mul(tc, d[i]));
                p[i] = Store(Add(s[i], p[i]));
            }
            float gx = Store(Mul(p[0], inv)), gy = Store(Mul(p[1], inv));
            int bx, by;
            if (k == 0)
            {
                bx = FtolSafe(Math.Floor((double)gx));
                by = FtolSafe(Math.Floor(Add(gy, 0.5)));
                if (d[1] > 0)
                    by -= 1;
            }
            else if (k == 1)
            {
                bx = FtolSafe(Math.Floor(Add(gx, 0.5)));
                by = FtolSafe(Math.Floor((double)gy));
                if (d[0] > 0)
                    bx -= 1;
            }
            else
            {
                bx = FtolSafe(Math.Floor((double)gx));
                by = FtolSafe(Math.Floor((double)gy));
            }
            if (bx >= 0 && bx < mWidth && by >= 0 && by < mHeight)
            {
                mBlocks.TryGetValue(mMap[(by * mWidth) + bx], out var blk);
                o[0] = Store(bx << B);
                o[1] = Store(by << B);
                o[2] = 0.0f;
                for (var i = 0; i < 3; ++i)
                {
                    a[i] = Store(Sub(cur[i], o[i]));
                    b[i] = Store(Sub(p[i], o[i]));
                    cur[i] = p[i];
                }
                visited = true;
                if (blk != null && BlockLine(blk, a, b, h, n, ref material))
                {
                    hitOut = new Vec3(Store(Add(Store(Add(o[0], h[0])), pos[0])), Store(Add(Store(Add(o[1], h[1])), pos[1])),
                                      Store(Add(Store(Add(o[2], h[2])), pos[2])));
                    normalOut = new Vec3(n[0], n[1], n[2]);
                    return true;
                }
            }
            else if (visited)
            {
                return false;
            }
            if (k == 2)
                return false;
        }
        return false;
    }
}
