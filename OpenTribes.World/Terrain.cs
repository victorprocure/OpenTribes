// Port of src/world/terrain.hpp and src/world/terrain.cpp: terrain (docs/terrain.md): .dtf GridFile, .dtb GridBlocks,
// height and line queries with the original's float behaviour. Double operations follow the current rounding mode of
// Fpu, float stores are Fpu.Store (stores truncate toward zero in the script mode).
using System.Buffers.Binary;
using OpenTribes.Core;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.World;

// src/world/terrain.hpp struct GridBlock.
public sealed class GridBlock
{
    public int Size;                                // squares per side (size_x == size_y in all data)
    public float[] Heights = Array.Empty<float>();  // (size+1)^2, row major
    public byte[] Squares = Array.Empty<byte>();    // size^2 x {flags, material}

    public float H(int x, int y) => Heights[(long)y * (Size + 1) + x];
    public bool Empty(int x, int y) => ((Squares[((long)y * Size + x) * 2] >> 3) & 7) != 0;

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

    // src/world/terrain.cpp lzhSection.
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

    // src/world/terrain.cpp GridBlock::parse.
    public static bool Parse(byte[] d, GridBlock output) => Parse(d, output, out _);

    public static bool Parse(byte[] d, GridBlock output, out long endOffset)
    {
        endOffset = 0;
        if (d.Length < 12 || d[0] != 'G' || d[1] != 'B' || d[2] != 'L' || d[3] != 'K')
            return false;
        long pos = 8;
        Get(d, ref pos, out int versionI);
        uint version = (uint)versionI;
        pos += 16; // name
        if (!Get(d, ref pos, out _) || !Get(d, ref pos, out _) || !GetF(d, ref pos, out _) || !GetF(d, ref pos, out _) ||
            !Get(d, ref pos, out int sx) || !Get(d, ref pos, out int sy) || sx <= 0 || sx != sy || sx > 4096)
            return false;
        output.Size = sx;
        long hcount = (long)(sx + 1) * (sx + 1);
        bool ok = true;
        if (version >= 4)
        {
            byte[] h = LzhSection(d, ref pos, hcount * 4, ref ok);
            output.Squares = LzhSection(d, ref pos, (long)sx * sx * 2, ref ok);
            output.Heights = new float[hcount];
            for (long i = 0; i < hcount; ++i)
                output.Heights[i] = BinaryPrimitives.ReadSingleLittleEndian(h.AsSpan((int)(i * 4), 4));
        }
        else if (version == 0)
        {
            if (pos + hcount * 4 + (long)sx * sx * 2 > d.Length)
                return false;
            output.Heights = new float[hcount];
            for (long i = 0; i < hcount; ++i)
                output.Heights[i] = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)(pos + i * 4), 4));
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

// src/world/terrain.hpp class Terrain.
public sealed class Terrain
{
    private int mDetail, mScale, mWidth, mHeight;
    private float mHmin, mHmax;
    private int[] mMap = Array.Empty<int>();
    private readonly Dictionary<int, GridBlock> mBlocks = new();
    private readonly List<string> mBlockFiles = new(); // block list order
    private string mMaterialList = "";
    // The .dtf, the blocks and the .dml, held while the terrain lives (the C++ destructor releases
    // them; the port calls ReleaseLocks when the owner is deleted).
    private readonly ResourceLocks mLocks = new();
    private Vec3 mPos;

    public void SetPosition(Vec3 p) => mPos = p;
    public Vec3 Position() => mPos;
    public string MaterialListName() => mMaterialList;
    public float Hmin() => mHmin;
    public float Hmax() => mHmax;

    // src/world/terrain.cpp ftol: truncation (crt_ftol); out of range gives 0x80000000 as cvttsd2si does.
    private static int Ftol(double v) => v > -2147483649.0 && v < 2147483648.0 ? (int)v : int.MinValue;

    // src/world/terrain.cpp interp: 4.2 height on square (sx, sy) at square-relative (u, v); every intermediate in the
    // current mode, the result stored as float.
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

    // src/world/terrain.cpp rawNormal.
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

    // src/world/terrain.cpp normalize.
    private static Vec3 Normalize(ReadOnlySpan<float> n)
    {
        float len = Store(Sqrt(Add(Add(Mul(n[0], n[0]), Mul(n[1], n[1])), Mul(n[2], n[2]))));
        float inv = Store(Div(1.0, len));
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
        if (Get(d, ref pos, out int x))
            v = x;
    }

    private static void GetInto(byte[] d, ref long pos, ref float v)
    {
        if (pos + 4 > d.Length)
            return;
        v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)pos, 4));
        pos += 4;
    }

    // src/world/terrain.cpp Terrain::load: loads <dtf> and its blocks through the resource manager.
    public static Terrain? Load(ResourceManager res, string dtfName)
    {
        if (!res.Read(dtfName, out byte[] d) || d.Length < 12 || d[0] != 'G' || d[1] != 'F' || d[2] != 'I' || d[3] != 'L')
            return null;
        long pos = 8;
        int version = 0, n = 0;
        GetInto(d, ref pos, ref version);
        GetInto(d, ref pos, ref n);
        if (pos + (uint)n > d.Length)
            return null;
        var t = new Terrain();
        t.mMaterialList = Reader.CString(d, pos, (uint)n);
        pos += (uint)n;
        int nextId = 0;
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
        for (int i = 0; i < t.mMap.Length; ++i)
            if (!Get(d, ref pos, out t.mMap[i]))
                return null;
        int count = 0;
        GetInto(d, ref pos, ref count);
        int dot = dtfName.LastIndexOf('.');
        string stem = dtfName[..(dot < 0 ? dtfName.Length : dot)];
        for (uint i = 0; i < (uint)count; ++i)
        {
            if (!Get(d, ref pos, out int id) || !Get(d, ref pos, out int len))
                return null;
            pos += (uint)len;
            string name = stem + "#" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".dtb";
            if (name.Length > 511) // char name[512]
                name = name[..511];
            var blk = new GridBlock();
            if (!res.Read(name, out byte[] b) || !GridBlock.Parse(b, blk))
                return null;
            t.mBlocks[id] = blk;
            t.mBlockFiles.Add(name);
        }
        // Resource loads, held while the terrain lives and kept until purge after it (docs/resources.md
        // 8.7): the .dtf (SimTerrain_load 0051792c), the blocks (GridBlockList 00567270, every stock
        // terrain has one) and the material list with its extension replaced by .dml (00517e04), no textures.
        t.mLocks.Load(res, dtfName);
        foreach (string name in t.mBlockFiles)
            t.mLocks.Load(res, name);
        string dml = t.mMaterialList;
        int mdot = dml.LastIndexOf('.');
        t.mLocks.Load(res, (mdot <= 0 ? dml : dml[..mdot]) + ".dml");
        return t;
    }

    // src/world/terrain.hpp Terrain::~Terrain: the held resources are unlocked.
    public void ReleaseLocks() => mLocks.Release();

    private static readonly uint[] sCrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; ++n)
        {
            uint c = n;
            for (int k = 0; k < 8; ++k)
                c = (c & 1) != 0 ? (c >> 1) ^ 0xedb88320u : c >> 1;
            table[n] = c;
        }
        return table;
    }

    // src/world/terrain.cpp crc32: CRC-32, reflected polynomial 0xedb88320, no final inversion (0x00588324).
    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (byte b in data)
            crc = sCrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
        return crc;
    }

    // src/world/terrain.cpp Terrain::surfaceInfo: GridFile_getSurfaceInfo (0x0056c138, docs/collision.md 5.1 step 4) at
    // the terrain-space point: height, unit normal and the square's material byte. False off the map and on empty
    // squares.
    public bool SurfaceInfo(float lx, float ly, ref float h, Span<float> n, ref byte material)
    {
        int shift = mDetail + mScale - 1;
        if (!(lx > -2147483648.0f && lx < 2147483648.0f && ly > -2147483648.0f && ly < 2147483648.0f))
            return false;
        int bx = Ftol(lx) >> shift, by = Ftol(ly) >> shift;
        if (bx < 0 || by < 0 || bx >= mWidth || by >= mHeight)
            return false;
        if (!mBlocks.TryGetValue(mMap[by * mWidth + bx], out GridBlock? b))
            return false;
        float px = Store(Sub(lx, bx << shift)), py = Store(Sub(ly, by << shift));
        int sx = Ftol(px) >> mScale, sy = Ftol(py) >> mScale;
        if (sx < 0 || sy < 0 || sx >= b.Size || sy >= b.Size || b.Empty(sx, sy))
            return false;
        float s = Store(1 << mScale);
        float u = Store(Sub(px, sx << mScale)), v = Store(Sub(py, sy << mScale));
        h = Interp(b, sx, sy, s, u, v);
        Span<float> raw = stackalloc float[3];
        RawNormal(b, sx, sy, s, u, v, raw);
        Vec3 unit = Normalize(raw);
        n[0] = unit.X;
        n[1] = unit.Y;
        n[2] = unit.Z;
        material = b.Squares[((long)sy * b.Size + sx) * 2 + 1];
        return true;
    }

    // src/world/terrain.cpp Terrain::crc: verification CRC (0x00517048, docs/terrain.md 3).
    public bool Crc(ResourceManager res, string dtfName, ref uint output)
    {
        uint c = 0xffffffffu;
        if (!res.Read(dtfName, out byte[] d))
            return false;
        c = Crc32(d, c);
        foreach (string name in mBlockFiles)
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

    // src/world/terrain.cpp Terrain::worldBox: 0x005193cc, for container queries.
    public void WorldBox(Span<float> box)
    {
        int shift = mDetail + mScale - 1;
        box[0] = mPos.X;
        box[1] = mPos.Y;
        box[2] = AddF(mHmin, mPos.Z);
        box[3] = Store(Add(mWidth << shift, mPos.X));
        box[4] = Store(Add(mHeight << shift, mPos.Y));
        box[5] = AddF(mHmax, mPos.Z);
    }

    // src/world/terrain.cpp Terrain::height: height at world (x, y) (0x005183e8); false where there is no terrain.
    public bool Height(float x, float y, ref float output)
    {
        Vec3 n = default;
        int material = 0;
        return Surface(x, y, ref output, ref n, ref material);
    }

    public bool Height(float x, float y, ref float output, ref Vec3 normal)
    {
        Vec3 n = default;
        int material = 0;
        if (!Surface(x, y, ref output, ref n, ref material))
            return false;
        normal = n;
        return true;
    }

    // src/world/terrain.cpp Terrain::surface.
    public bool Surface(float x, float y, ref float output, ref Vec3 normal, ref int material) =>
        SurfaceLocal(Store(Sub(x, mPos.X)), Store(Sub(y, mPos.Y)), ref output, ref normal, ref material);

    // src/world/terrain.cpp Terrain::surfaceLocal.
    public bool SurfaceLocal(float lx, float ly, ref float output, ref Vec3 normal, ref int material)
    {
        int shift = mDetail + mScale - 1;
        int bx = Ftol(lx) >> shift, by = Ftol(ly) >> shift;
        if (bx < 0 || by < 0 || bx >= mWidth || by >= mHeight)
            return false;
        if (!mBlocks.TryGetValue(mMap[by * mWidth + bx], out GridBlock? b))
            return false;
        float px = Store(Sub(lx, bx << shift)), py = Store(Sub(ly, by << shift));
        int sx = Ftol(px) >> mScale, sy = Ftol(py) >> mScale;
        if (sx < 0 || sy < 0 || sx >= b.Size || sy >= b.Size || b.Empty(sx, sy))
            return false;
        float s = Store(1 << mScale);
        float u = Store(Sub(px, sx << mScale)), v = Store(Sub(py, sy << mScale));
        output = Interp(b, sx, sy, s, u, v);
        Span<float> n = stackalloc float[3];
        RawNormal(b, sx, sy, s, u, v, n);
        normal = Normalize(n);
        material = b.Squares[((long)sy * b.Size + sx) * 2 + 1];
        return true;
    }

    // src/world/terrain.cpp family: line family of the block and square walks (0x00567bc7, 0x005683de;
    // docs/collision.md 9 section 3 step 2): parameter of the first crossing and the step between crossings.
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

    // src/world/terrain.cpp ftolSafe: crt_ftol of a value that may not fit: the original stores 0x80000000.
    private static int FtolSafe(double v)
    {
        if (!(v > -2147483648.0 && v < 2147483648.0))
            return int.MinValue;
        return (int)v;
    }

    // src/world/terrain.cpp struct TriPlane.
    private struct TriPlane
    {
        public Float3 N;
        public float D;
    }

    // src/world/terrain.cpp planeOf: unit normal and distance of a triangle plane (0x0056cca0.., section 4.4 step 10).
    private static TriPlane PlaneOf(ReadOnlySpan<float> raw, ReadOnlySpan<float> v)
    {
        var pl = new TriPlane();
        float len = Store(Sqrt(Add(Add(Mul(raw[0], raw[0]), Mul(raw[1], raw[1])), Mul(raw[2], raw[2]))));
        float r = Store(Div(1.0, len));
        for (int i = 0; i < 3; ++i)
            pl.N[i] = Store(Mul(r, raw[i]));
        pl.D = Store(-Add(Add(Mul(v[0], pl.N[0]), Mul(v[1], pl.N[1])), Mul(v[2], pl.N[2])));
        return pl;
    }

    // src/world/terrain.cpp segPlane: Plane_intersectSegment (0x005edc24): out is written only on success.
    private static bool SegPlane(in TriPlane pl, ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> output)
    {
        Span<float> dv = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
            dv[i] = Store(Sub(b[i], a[i]));
        double den80 = Add(Add(Mul(pl.N[0], dv[0]), Mul(pl.N[1], dv[1])), Mul(pl.N[2], dv[2]));
        if (den80 == 0)
            return false;
        float den = Store(den80);
        float num = Store(-Add(Add(Add(Mul(pl.N[0], a[0]), Mul(pl.N[1], a[1])), Mul(pl.N[2], a[2])), pl.D));
        if (den > 0 ? (num < 0 || num > den) : (num > 0 || num < den))
            return false;
        if (den == 0)
            return false; // a denominator below the f32 range: the original divides by zero (D11)
        for (int i = 0; i < 3; ++i)
            output[i] = Store(Add(Div(Mul(dv[i], num), den), a[i]));
        return true;
    }

    // src/world/terrain.cpp Terrain::blockLine: square walk inside one block (0x005680ec, line mode; docs/collision.md 9
    // section 4). a, b: block-relative piece. A side change at a line crossing is a hit at once (F1); only the end of
    // the walk tests the triangle bounds.
    private bool BlockLine(GridBlock blk, ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> hit, Span<float> normal,
                           ref int material)
    {
        int S = mScale, N = blk.Size;
        float sq = Store(1 << S);
        float inv = Store(Div(1.0, sq)), half = Store(Mul(0.5, inv));
        int row = N + 1;
        long nHeights = blk.Heights.Length, nSquares = blk.Squares.Length;
        if (N < 1 || nHeights < (long)row * row || nSquares < (long)N * N * 2)
            return false;
        bool oob = false;
        // Height read with both indices clamped to [0, N].
        float H(int x, int y)
        {
            x = Math.Min(Math.Max(x, 0), N);
            y = Math.Min(Math.Max(y, 0), N);
            return blk.Heights[(long)y * row + x];
        }
        // Flag byte (0x00567974): indices clamped to [0, N], so row and column N read past the square array in the
        // original. Those reads end the test without a hit (section 9 Q1).
        int Flags(int x, int y)
        {
            x = Math.Min(Math.Max(x, 0), N);
            y = Math.Min(Math.Max(y, 0), N);
            long i = ((long)y * N + x) * 2;
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
            long i = (long)iy * row + ix;
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
            int q = FtolSafe(v) >> S;
            return q >= N ? N - 1 : q < 0 ? 0 : q;
        }
        int sx = ClampSq(a[0]), sy = ClampSq(a[1]), ex = ClampSq(b[0]), ey = ClampSq(b[1]);
        float hs = Interp(blk, sx, sy, sq, Store(Sub(a[0], sx << S)), Store(Sub(a[1], sy << S)));
        float he = Interp(blk, ex, ey, sq, Store(Sub(b[0], ex << S)), Store(Sub(b[1], ey << S)));
        bool above = a[2] > hs;
        Span<float> d = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
            d[i] = Store(Sub(b[i], a[i]));
        Span<float> t = stackalloc float[4], dt = stackalloc float[4];
        Family(Store(Mul(a[1], inv)), Store(Mul(b[1], inv)), out t[0], out dt[0]);
        Family(Store(Mul(a[0], inv)), Store(Mul(b[0], inv)), out t[1], out dt[1]);
        Family(Store(Mul(Sub(a[0], a[1]), half)), Store(Mul(Sub(b[0], b[1]), half)), out t[2], out dt[2]);
        Family(Store(Mul(Add(a[0], a[1]), half)), Store(Mul(Add(b[0], b[1]), half)), out t[3], out dt[3]);
        Span<float> q = stackalloc float[3], h4 = stackalloc float[4], raw = stackalloc float[3], v = stackalloc float[3];
        // Every iteration advances one family; a step that does not advance (dt of 0 after the f32 store) would never
        // end in the original (D11).
        for (int guard = 0; guard < 1 << 20; ++guard)
        {
            float tm = 1.0f;
            int k = -1;
            for (int i = 0; i < 4; ++i)
                if (t[i] < tm)
                {
                    k = i;
                    tm = t[i];
                }
            if (k < 0)
                break;
            t[k] = Store(Add(dt[k], t[k]));
            for (int i = 0; i < 3; ++i)
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
            float hedge = Store(Add(Mul(Sub(hB, hA), fr), hA));
            bool now = q[2] > hedge;
            if (now == above)
                continue;
            if (k == 0 && d[1] > 0)
                iy -= 1;
            else if (k == 1 && d[0] > 0)
                ix -= 1;
            int fl = Flags(ix, iy);
            if (oob)
                return false;
            if (((fl >> 3) & 7) != 0)
            {
                above = !above;
                continue;
            }
            q[2] = hedge;
            int par = (ix ^ iy) & 1;
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
            TriPlane pl = PlaneOf(raw, v);
            q.CopyTo(hit);
            SegPlane(pl, a, b, hit); // result flag ignored: a failed test keeps the crossing point
            ((ReadOnlySpan<float>)pl.N).CopyTo(normal);
            long mi = ((long)Math.Min(Math.Max(iy, 0), N) * N + Math.Min(Math.Max(ix, 0), N)) * 2 + 1;
            material = mi < nSquares ? blk.Squares[mi] : 0;
            return true;
        }
        // End of the walk (0x0056a1f3).
        if ((b[2] > he) == above)
            return false;
        if (((Flags(ex, ey) >> 3) & 7) != 0)
            return false;
        ReadOnlySpan<float> o = stackalloc float[3] { Store(ex << S), Store(ey << S), 0.0f };
        Span<float> sa = stackalloc float[3], sb = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
        {
            sa[i] = Store(Sub(a[i], o[i]));
            sb[i] = Store(Sub(b[i], o[i]));
        }
        Span<float> h = stackalloc float[4];
        if (!Corners(ex, ey, h))
            return false;
        float h00 = h[0], h10 = h[1], h01 = h[2], h11 = h[3];
        int parity = (ex ^ ey) & 1;
        const float eps = 0.0001f;
        Span<float> p = stackalloc float[3];
        bool found = false;
        // GridSquare_intersectLine (0x0056cebc).
        if (parity == 0)
        {
            ReadOnlySpan<float> n1 = stackalloc float[3] { Store(Sub(h01, h11)), Store(Sub(h00, h01)), sq };
            ReadOnlySpan<float> v0 = stackalloc float[3] { 0, 0, h00 };
            if (SegPlane(PlaneOf(n1, v0), sa, sb, p) && p[0] > -eps && Add(sq, eps) > p[1] && !(p[0] > p[1]))
            {
                found = true;
            }
            else
            {
                ReadOnlySpan<float> n2 = stackalloc float[3] { Store(Sub(h00, h10)), Store(Sub(h10, h11)), sq };
                found = SegPlane(PlaneOf(n2, v0), sa, sb, p) && p[0] > p[1];
            }
        }
        else
        {
            ReadOnlySpan<float> n1 = stackalloc float[3] { Store(Sub(h00, h10)), Store(Sub(h00, h01)), sq };
            ReadOnlySpan<float> v0 = stackalloc float[3] { 0, sq, h01 };
            if (SegPlane(PlaneOf(n1, v0), sa, sb, p) && p[0] > -eps && p[1] > -eps && Sub(sq, p[0]) > p[1])
            {
                found = true;
            }
            else
            {
                ReadOnlySpan<float> n2 = stackalloc float[3] { Store(Sub(h01, h11)), Store(Sub(h10, h11)), sq };
                found = SegPlane(PlaneOf(n2, v0), sa, sb, p) && !(Sub(sq, p[0]) > p[1]);
            }
        }
        if (!found)
            return false;
        // Normal of the triangle under the hit (0x0056cc38).
        Span<float> rawEnd = stackalloc float[3] { 0, 0, sq };
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
        ReadOnlySpan<float> zero = stackalloc float[3] { 0, 0, 0 };
        TriPlane plEnd = PlaneOf(rawEnd, zero);
        ((ReadOnlySpan<float>)plEnd.N).CopyTo(normal);
        for (int i = 0; i < 3; ++i)
            hit[i] = Store(Add(o[i], p[i]));
        material = blk.Squares[((long)ey * N + ex) * 2 + 1];
        return true;
    }

    // src/world/terrain.cpp Terrain::castLine: line test (0x005679f0), first hit along start -> end, world space.
    public bool CastLine(Vec3 start, Vec3 end, ref Vec3 hit, ref Vec3 normal)
    {
        int material = 0;
        return CastLine(start, end, ref hit, ref normal, ref material);
    }

    // src/world/terrain.cpp Terrain::castLine with the material: GridCollision_collideLine (0x005679f0) and the block
    // walk (0x00567ae8), docs/collision.md 9 section 3. The segment is the caller's, not clipped to the terrain box (F4).
    public bool CastLine(Vec3 start, Vec3 end, ref Vec3 hitOut, ref Vec3 normalOut, ref int material)
    {
        using var chop = ChopScope();
        ReadOnlySpan<float> pos = stackalloc float[3] { mPos.X, mPos.Y, mPos.Z };
        ReadOnlySpan<float> ws = stackalloc float[3] { start.X, start.Y, start.Z };
        ReadOnlySpan<float> we = stackalloc float[3] { end.X, end.Y, end.Z };
        Span<float> s = stackalloc float[3], e = stackalloc float[3], d = stackalloc float[3], cur = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
        {
            if (!float.IsFinite(ws[i]) || !float.IsFinite(we[i]))
                return false;
            s[i] = Store(Sub(ws[i], pos[i]));
            e[i] = Store(Sub(we[i], pos[i]));
        }
        int B = mScale + mDetail - 1;
        float inv = Store(Div(1.0, 1 << B));
        for (int i = 0; i < 3; ++i)
        {
            d[i] = Store(Sub(e[i], s[i]));
            cur[i] = s[i];
        }
        Span<float> t = stackalloc float[3] { 0, 0, 0 }, dt = stackalloc float[3] { 0, 0, 0 };
        Family(Store(Mul(s[1], inv)), Store(Mul(e[1], inv)), out t[0], out dt[0]);
        Family(Store(Mul(s[0], inv)), Store(Mul(e[0], inv)), out t[1], out dt[1]);
        bool visited = false;
        Span<float> p = stackalloc float[3], a = stackalloc float[3], b = stackalloc float[3];
        Span<float> h = stackalloc float[3], n = stackalloc float[3], o = stackalloc float[3];
        for (int guard = 0; guard < 1 << 20; ++guard)
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
            for (int i = 0; i < 3; ++i)
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
                mBlocks.TryGetValue(mMap[by * mWidth + bx], out GridBlock? blk);
                o[0] = Store(bx << B);
                o[1] = Store(by << B);
                o[2] = 0.0f;
                for (int i = 0; i < 3; ++i)
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
