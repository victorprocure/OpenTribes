using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using OpenTribes.Core;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.World;

/// <summary>A three-float vector.</summary>
public struct Vec3
{
    /// <summary>The components.</summary>
    public float X, Y, Z;

    /// <summary>Creates a vector.</summary>
    /// <param name="x">The x component.</param>
    /// <param name="y">The y component.</param>
    /// <param name="z">The z component.</param>
    public Vec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>Gets or sets a component by index, 0 for x, 1 for y, anything else for z.</summary>
    /// <param name="i">The component index.</param>
    public float this[int i]
    {
        readonly get => i == 0 ? X : i == 1 ? Y : Z;
        set
        {
            if (i == 0)
                X = value;
            else if (i == 1)
                Y = value;
            else
                Z = value;
        }
    }
}

/// <summary>A plane: n.p + d = 0, with the front side at n.p + d &gt; 0.</summary>
public struct Plane
{
    /// <summary>The normal components and the distance.</summary>
    public float Nx, Ny, Nz, D;
}

/// <summary>A BSP node. A child index of 0 or more is a node, below 0 it is the leaf -(c + 1).</summary>
public struct BspNode
{
    /// <summary>The index of the node's plane.</summary>
    public ushort PlaneIndex;
    /// <summary>The front and back child indices.</summary>
    public short Front, Back;
}

/// <summary>Four unsigned 16-bit values stored inline.</summary>
[InlineArray(4)]
public struct UShort4
{
    private ushort mElement;
}

// The Reader of the anonymous namespaces of src/world/interior.cpp and src/world/shape.cpp: little-endian reads that
// fail (ok = false, value 0, position kept) past the end; skip moves past the end and fails.
internal sealed class Reader
{
    public readonly byte[] D;
    public long Pos;
    public bool Ok = true;

    public Reader(byte[] d) => D = d;

    private bool Take(int n, out ReadOnlySpan<byte> s)
    {
        if (Pos + n > D.Length)
        {
            Ok = false;
            s = default;
            return false;
        }
        s = D.AsSpan((int)Pos, n);
        Pos += n;
        return true;
    }

    public byte GetU8() => Take(1, out var s) ? s[0] : (byte)0;
    public ushort GetU16() => Take(2, out var s) ? BinaryPrimitives.ReadUInt16LittleEndian(s) : (ushort)0;
    public short GetI16() => Take(2, out var s) ? BinaryPrimitives.ReadInt16LittleEndian(s) : (short)0;
    public uint GetU32() => Take(4, out var s) ? BinaryPrimitives.ReadUInt32LittleEndian(s) : 0u;
    public int GetI32() => Take(4, out var s) ? BinaryPrimitives.ReadInt32LittleEndian(s) : 0;
    public float GetF32() => Take(4, out var s) ? BinaryPrimitives.ReadSingleLittleEndian(s) : 0f;

    public void Skip(long n)
    {
        if (Pos + n > D.Length)
            Ok = false;
        Pos += n;
    }

    // src/world/shape.cpp Reader::pers: PERS header: tag, u32 size (bit 31: padding flag), u16 name length, name padded
    // to even, u32 version. Returns the version; end receives the block end.
    public uint Pers(out long end)
    {
        long start = Pos;
        end = 0;
        if (Pos + 4 > D.Length || D[Pos] != 'P' || D[Pos + 1] != 'E' || D[Pos + 2] != 'R' || D[Pos + 3] != 'S')
        {
            Ok = false;
            return 0;
        }
        Skip(4);
        uint size = GetU32();
        end = start + 8 + (size & 0x7fffffffu);
        ushort nameLen = GetU16();
        Skip((nameLen + 1u) & ~1u);
        return GetU32();
    }

    // Port convention: the little-endian fields read by memcpy at a fixed offset.
    public static short I16At(byte[] d, long p) => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan((int)p, 2));
    public static ushort U16At(byte[] d, long p) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)p, 2));
    public static int I32At(byte[] d, long p) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan((int)p, 4));
    public static uint U32At(byte[] d, long p) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)p, 4));
    public static float F32At(byte[] d, long p) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)p, 4));

    // std::string(p, strnlen(p, max)) of a byte buffer, as a Latin-1 string.
    public static string CString(byte[] d, long p, long max)
    {
        long n = 0;
        while (n < max && p + n < d.Length && d[p + n] != 0)
            ++n;
        return ByteString.FromBytes(d.AsSpan((int)p, (int)n));
    }
}

/// <summary>The collision geometry of an interior: the state-0 LOD-0 .dig (ITRGeometry) with its BSP, and the server's BSP line cast.</summary>
/// <remarks>Port of src/world/interior.cpp (docs/interiors.md). All arithmetic follows the current rounding mode of <c>Fpu</c> (ldouble is double in the portable build, docs/portable-float.md 1) and a float store is <c>Fpu.Store</c>.</remarks>
public sealed class InteriorGeometry
{
    private const float kPlaneEps = 1e-5f; // DAT_0063f9a0

    /// <summary>The result of a line cast.</summary>
    public struct Hit
    {
        /// <summary>The hit point and the surface normal, in geometry space.</summary>
        public Vec3 Point, Normal;
        /// <summary>The leaf that was entered and the index of the plane that was crossed.</summary>
        public int Leaf, PlaneIndex;
    }

    /// <summary>The geometry box, min xyz then max xyz.</summary>
    public Float6 Box;
    /// <summary>The .dml of the .dis (docs/collision.md 4.3), empty when unknown.</summary>
    public string MaterialListName = "";
    /// <summary>The linkedInterior byte of the .dis (docs/interiors.md 3), state 0, LOD 0.</summary>
    public bool LinkedInterior;
    /// <summary>The linkableFaces mask of the .dis (docs/interiors.md 3), state 0, LOD 0.</summary>
    public uint LinkableFaces = 0xfc;

    private struct Surface
    {
        public byte Flags, Material;
        public ushort PlaneIndex;
        public uint FirstVertex;
        public byte VertexCount;
    }
    private struct SolidLeaf
    {
        public uint Offset;
        public ushort Count;
        public uint PlaneOffset;
        public ushort PlaneCount;
    }
    private struct EmptyLeaf
    {
        public ushort PvsCount; // byte count << 1
        public uint PvsOffset;
        public ushort SurfaceCount;
        public uint SurfaceOffset, PlaneOffset;
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public ushort PlaneCount;
    }

    private readonly List<Surface> mSurfaces = new();
    private readonly List<SolidLeaf> mSolidLeaves = new();
    private readonly List<ushort> mVertices = new(); // point3 index of each polygon vertex
    private readonly List<Vec3> mPoints = new();     // point3 after the read-time adjustment (4.3)
    private readonly List<BspNode> mNodes = new();
    private readonly List<Plane> mPlanes = new();
    private readonly List<EmptyLeaf> mEmptyLeaves = new();
    private byte[] mBits = Array.Empty<byte>();
    private uint mSolidCount;

    /// <summary>Tests whether a leaf number is a solid leaf.</summary>
    /// <param name="leaf">The leaf number.</param>
    /// <returns>True for a solid leaf.</returns>
    public bool IsSolid(int leaf) => IsSolidLeaf(leaf);
    /// <summary>Gets the number of solid leaves.</summary>
    /// <returns>The count.</returns>
    public uint SolidCount() => mSolidCount;
    /// <summary>Gets the number of empty leaves.</summary>
    /// <returns>The count.</returns>
    public int EmptyCount() => mEmptyLeaves.Count;
    /// <summary>Gets the BSP nodes.</summary>
    /// <returns>The nodes.</returns>
    public IReadOnlyList<BspNode> Nodes() => mNodes;
    /// <summary>Gets the BSP planes.</summary>
    /// <returns>The planes.</returns>
    public IReadOnlyList<Plane> Planes() => mPlanes;
    private bool IsSolidLeaf(int leaf) => leaf >= 43 && leaf < 43 + (int)mSolidCount;

    /// <summary>Gets the material byte of a surface.</summary>
    /// <param name="surface">The surface index.</param>
    /// <returns>The material byte, 0xff for none.</returns>
    public byte SurfaceMaterial(int surface) =>
        surface >= 0 && surface < mSurfaces.Count ? mSurfaces[surface].Material : (byte)0xff;

    // src/world/interior.cpp side.
    private static int Side(in Plane pl, Vec3 p)
    {
        float s = Store(Add(Add(Mul(pl.Nx, p.X), Mul(pl.Ny, p.Y)), Mul(pl.Nz, p.Z)));
        if (Sub(s, kPlaneEps) <= -(double)pl.D)
            return -(double)pl.D <= Add(s, kPlaneEps) ? 0 : -1;
        return 1;
    }

    // src/world/interior.cpp split.
    private static Vec3 Split(in Plane pl, Vec3 a, Vec3 b)
    {
        float dx = Store(Sub(b.X, a.X)), dy = Store(Sub(b.Y, a.Y)), dz = Store(Sub(b.Z, a.Z));
        float den = Store(Add(Add(Mul(pl.Nx, dx), Mul(pl.Ny, dy)), Mul(pl.Nz, dz)));
        float num = Store(-Add(Add(Add(Mul(pl.Nx, a.X), Mul(pl.Ny, a.Y)), Mul(pl.Nz, a.Z)), pl.D));
        if (den == 0)
            return a;
        return new Vec3(Store(Add(Div(Mul(dx, num), den), a.X)), Store(Add(Div(Mul(dy, num), den), a.Y)),
                        Store(Add(Div(Mul(dz, num), den), a.Z)));
    }

    /// <summary>Parses a PERS ITRGeometry block.</summary>
    /// <param name="data">The file bytes.</param>
    /// <returns>The geometry, or null on malformed data.</returns>
    public static InteriorGeometry? Parse(byte[] data)
    {
        var r = new Reader(data);
        if (data.Length < 10 || data[0] != 'P' || data[1] != 'E' || data[2] != 'R' || data[3] != 'S')
            return null;
        r.Skip(8);
        ushort nameLen = r.GetU16();
        r.Skip((nameLen + 1u) & ~1u);
        r.GetU32(); // version
        r.GetU32(); // buildId
        r.GetF32(); // textureScale
        var g = new InteriorGeometry();
        for (int i = 0; i < 6; ++i)
            g.Box[i] = r.GetF32();
        var counts = new uint[9];
        for (int i = 0; i < 9; ++i)
            counts[i] = r.GetU32();
        long[] kSizes = { 20, 8, 12, 44, 1, 4, 12, 8, 16 };
        for (int i = 0; i < 9 && r.Ok; ++i)
        {
            // A count the file cannot hold is malformed (D11): stop before allocating for it.
            if (r.Pos > data.Length || (long)counts[i] * kSizes[i] > data.Length - r.Pos)
            {
                r.Ok = false;
                break;
            }
            if (i == 0)
            {
                // Surface (20 bytes): +0 flags, +1 material, +6 plane index.
                for (uint k = 0; k < counts[0]; ++k)
                {
                    long start = r.Pos;
                    var s = new Surface();
                    s.Flags = r.GetU8();
                    s.Material = r.GetU8();
                    r.Skip(4);
                    s.PlaneIndex = r.GetU16();
                    s.FirstVertex = r.GetU32();
                    r.Skip(4);
                    s.VertexCount = r.GetU8();
                    r.Pos = start;
                    r.Skip(20);
                    g.mSurfaces.Add(s);
                }
            }
            else if (i == 2)
            {
                // Solid leaf (12 bytes): +0 u32 bitList offset of its surface list, +8 u16 byte count.
                for (uint k = 0; k < counts[2]; ++k)
                {
                    long start = r.Pos;
                    var l = new SolidLeaf();
                    l.Offset = r.GetU32();
                    l.PlaneOffset = r.GetU32();
                    l.Count = r.GetU16();
                    l.PlaneCount = r.GetU16();
                    r.Pos = start;
                    r.Skip(12);
                    g.mSolidLeaves.Add(l);
                }
            }
            else if (i == 1)
            {
                for (uint k = 0; k < counts[1]; ++k)
                {
                    var n = new BspNode();
                    n.PlaneIndex = r.GetU16();
                    n.Front = r.GetI16();
                    n.Back = r.GetI16();
                    r.Skip(2);
                    g.mNodes.Add(n);
                }
            }
            else if (i == 3)
            {
                // Empty leaf (44 bytes): +0 u16 PVS byte count << 1, +4 u32 PVS offset in bitList.
                for (uint k = 0; k < counts[3]; ++k)
                {
                    long start = r.Pos;
                    var l = new EmptyLeaf();
                    l.PvsCount = r.GetU16();
                    l.SurfaceCount = r.GetU16();
                    l.PvsOffset = r.GetU32();
                    l.SurfaceOffset = r.GetU32();
                    l.PlaneOffset = r.GetU32();
                    l.MinX = r.GetF32(); l.MinY = r.GetF32(); l.MinZ = r.GetF32();
                    l.MaxX = r.GetF32(); l.MaxY = r.GetF32(); l.MaxZ = r.GetF32();
                    l.PlaneCount = r.GetU16();
                    r.Pos = start;
                    r.Skip(44);
                    g.mEmptyLeaves.Add(l);
                }
            }
            else if (i == 4)
            {
                long start = r.Pos;
                r.Skip(counts[4]);
                if (r.Ok)
                    g.mBits = data.AsSpan((int)start, (int)counts[4]).ToArray();
            }
            else if (i == 5)
            {
                // Vertex (4 bytes): +0 u16 point3 index.
                for (uint k = 0; k < counts[5]; ++k)
                {
                    g.mVertices.Add(r.GetU16());
                    r.Skip(2);
                }
            }
            else if (i == 6)
            {
                // point3, with the read-time adjustment of 0x005544e0: a coordinate equal to the box minimum moves
                // down by 0.0125, one equal to the maximum moves up.
                for (uint k = 0; k < counts[6]; ++k)
                {
                    var v = new Vec3();
                    for (int a = 0; a < 3; ++a)
                    {
                        v[a] = r.GetF32();
                        if (v[a] == g.Box[a])
                            v[a] = Store(Sub(v[a], 0.0125f));
                        else if (v[a] == g.Box[a + 3])
                            v[a] = Store(Add(v[a], 0.0125f));
                    }
                    g.mPoints.Add(v);
                }
            }
            else if (i == 8)
            {
                for (uint k = 0; k < counts[8]; ++k)
                {
                    var p = new Plane();
                    p.Nx = r.GetF32();
                    p.Ny = r.GetF32();
                    p.Nz = r.GetF32();
                    p.D = r.GetF32();
                    g.mPlanes.Add(p);
                }
            }
            else
            {
                r.Skip(counts[i] * kSizes[i]);
            }
        }
        g.mSolidCount = counts[2];
        if (!r.Ok || g.mNodes.Count == 0)
            return null;
        return g;
    }

    /// <summary>Computes the box outcode of a point (ITRGeometry_boxOutcode, 00554408).</summary>
    /// <param name="p">The point in geometry space.</param>
    /// <returns>The outcode bits: x 1 and 2, y 4 and 8, z 0x10 and 0x20 for below and above the box.</returns>
    public uint Outcode(Vec3 p)
    {
        uint c = 0;
        for (int i = 0; i < 3; ++i)
        {
            if (p[i] < Box[i])
                c |= 1u << (2 * i);
            else if (Box[i + 3] < p[i])
                c |= 2u << (2 * i);
        }
        return c;
    }

    /// <summary>Finds the leaf number of a point.</summary>
    /// <param name="p">The point in geometry space.</param>
    /// <returns>Outside the box the outcode, which is one of the reserved empty leaves 0 to 42. Inside, the BSP leaf of the point.</returns>
    /// <remarks>Follows 0048366c: side 1 goes front, 0 and -1 go back.</remarks>
    public int LeafAt(Vec3 p)
    {
        uint c = Outcode(p);
        if (c != 0)
            return (int)c;
        int node = 0;
        while (node >= 0)
        {
            BspNode n = mNodes[node];
            node = Side(mPlanes[n.PlaneIndex], p) == 1 ? n.Front : n.Back;
        }
        return -(node + 1);
    }

    // src/world/interior.cpp InteriorGeometry::emptyLeaf: index of the empty leaf record, -1 when none.
    private int EmptyLeafIndex(int leaf)
    {
        if (leaf < 0 || IsSolidLeaf(leaf))
            return -1;
        long i = leaf < 43 ? leaf : leaf - (long)mSolidCount;
        return i < mEmptyLeaves.Count ? (int)i : -1;
    }

    /// <summary>Lists the leaves visible from an empty leaf (0056234c).</summary>
    /// <param name="leaf">The empty leaf number.</param>
    /// <returns>The visible leaf numbers.</returns>
    /// <remarks>The PVS is a bit vector over leaf numbers, most significant bit first. A zero byte is followed by a count of the zero bytes it stands for.</remarks>
    public List<int> VisibleLeaves(int leaf)
    {
        var output = new List<int>();
        int li = EmptyLeafIndex(leaf);
        if (li < 0)
            return output;
        EmptyLeaf l = mEmptyLeaves[li];
        long pos = l.PvsOffset, end = pos + (l.PvsCount >> 1);
        if (end > mBits.Length)
            return output;
        int bas = -8;
        while (true)
        {
            while (pos < end && mBits[pos] == 0)
            {
                bas += (pos + 1 < mBits.Length ? mBits[pos + 1] : 0) * 8;
                pos += 2;
            }
            if (pos >= end)
                break;
            bas += 8;
            byte bt = mBits[pos++];
            for (int bit = 0; bit < 8; ++bit)
                if ((bt & (0x80 >> bit)) != 0)
                    output.Add(bas + bit);
        }
        return output;
    }

    // OR of a compressed bit list (a zero byte followed by n stands for n zero bytes, 005622d0) into `set`.
    private void OrList(byte[] set, long offset, int count)
    {
        long pos = offset, end = offset + count, at = 0;
        while (pos < end && pos < mBits.Length)
        {
            if (mBits[pos] == 0)
            {
                at += pos + 1 < mBits.Length ? mBits[pos + 1] : 0;
                pos += 2;
            }
            else
            {
                if (at < set.Length)
                    set[at] |= mBits[pos];
                ++at;
                ++pos;
            }
        }
    }

    /// <summary>Gets the total number of surfaces.</summary>
    public int SurfaceCountTotal => mSurfaces.Count;

    /// <summary>Selects the surfaces the stock renderer draws for a camera (code 0055db5c, 0055e1a4, 0055e2d8, 0055e4b0, 0055e578).</summary>
    /// <param name="cam">The camera point in interior space.</param>
    /// <param name="sphereVisible">Tests a bounding sphere given as four floats, the centre and the radius, against the view.</param>
    /// <returns>A flag per surface.</returns>
    /// <remarks>Takes the surface and plane lists of the camera leaf, plus those of every leaf of its PVS from 6 up whose bounding sphere passes <paramref name="sphereVisible"/>. The sphere centre is the centre of the leaf box and its radius is the diagonal. Planes the camera is not in front of are dropped, then the surfaces whose plane flag disagrees. A solid camera leaf only has its own lists.</remarks>
    public bool[] SelectSurfaces(Vec3 cam, Func<float, float, float, float, bool> sphereVisible)
    {
        int leaf = LeafAt(cam);
        var surfaceSet = new byte[(mSurfaces.Count >> 3) + 1];
        var planeSet = new byte[(mPlanes.Count >> 3) + 1];
        int ei = EmptyLeafIndex(leaf);
        if (ei >= 0)
        {
            AddLeaf(surfaceSet, planeSet, mEmptyLeaves[ei]);
            foreach (int n in VisibleLeaves(leaf))
            {
                if (n < 6 || IsSolidLeaf(n))
                    continue;
                int vi = EmptyLeafIndex(n);
                if (vi < 0)
                    continue;
                EmptyLeaf l = mEmptyLeaves[vi];
                float dx = l.MaxX - l.MinX, dy = l.MaxY - l.MinY, dz = l.MaxZ - l.MinZ;
                float radius = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (sphereVisible((l.MinX + l.MaxX) * 0.5f, (l.MinY + l.MaxY) * 0.5f, (l.MinZ + l.MaxZ) * 0.5f, radius))
                    AddLeaf(surfaceSet, planeSet, l);
            }
        }
        else if (IsSolidLeaf(leaf) && leaf - 43 < mSolidLeaves.Count)
        {
            SolidLeaf sl = mSolidLeaves[leaf - 43];
            OrList(surfaceSet, sl.Offset, sl.Count);
            OrList(planeSet, sl.PlaneOffset, sl.PlaneCount);
        }
        for (int i = 0; i < mPlanes.Count; ++i)
        {
            if ((planeSet[i >> 3] & (0x80 >> (i & 7))) != 0 && Side(mPlanes[i], cam) != 1)
                planeSet[i >> 3] &= (byte)~(0x80 >> (i & 7));
        }
        var result = new bool[mSurfaces.Count];
        for (int s = 0; s < mSurfaces.Count; ++s)
        {
            if ((surfaceSet[s >> 3] & (0x80 >> (s & 7))) == 0)
                continue;
            Surface sf = mSurfaces[s];
            int pi = sf.PlaneIndex;
            bool inSet = (pi >> 3) < planeSet.Length && (planeSet[pi >> 3] & (0x80 >> (pi & 7))) != 0;
            result[s] = inSet != ((sf.Flags & 0x80) == 0);
        }
        return result;
    }

    private void AddLeaf(byte[] surfaceSet, byte[] planeSet, in EmptyLeaf l)
    {
        OrList(surfaceSet, l.SurfaceOffset, l.SurfaceCount);
        OrList(planeSet, l.PlaneOffset, l.PlaneCount);
    }

    /// <summary>Tests whether an empty leaf sees outside.</summary>
    /// <param name="leaf">The leaf number.</param>
    /// <returns>True when <see cref="OutsideFaces"/> is not zero.</returns>
    public bool SeesOutside(int leaf) => OutsideFaces(leaf) != 0;

    /// <summary>Gets the outside face bits of an empty leaf (0055449c).</summary>
    /// <param name="leaf">The leaf number.</param>
    /// <returns>The first PVS byte masked with 0xfc, 0 when none.</returns>
    public byte OutsideFaces(int leaf)
    {
        int li = EmptyLeafIndex(leaf);
        if (li < 0)
            return 0;
        EmptyLeaf l = mEmptyLeaves[li];
        if ((l.PvsCount >> 1) == 0 || l.PvsOffset >= mBits.Length)
            return 0;
        return (byte)(mBits[l.PvsOffset] & 0xfc);
    }

    // src/world/interior.cpp InteriorGeometry::rec.
    private bool Rec(int node, int lastPlane, Vec3 a, Vec3 b, out Hit hit)
    {
        hit = default;
        if (node < 0)
        {
            int leaf = -(node + 1);
            if (!IsSolidLeaf(leaf))
                return false;
            Vec3 n;
            if (lastPlane == -1)
            {
                // 0x005607aa: the difference, the length and its reciprocal are each stored as f32 before the three
                // products; a zero length only replaces z.
                float dx = Store(Sub(b.X, a.X)), dy = Store(Sub(b.Y, a.Y)), dz = Store(Sub(b.Z, a.Z));
                float len = Store(Sqrt(Add(Add(Mul(dx, dx), Mul(dy, dy)), Mul(dz, dz))));
                if (len == 0)
                {
                    n = new Vec3(dx, dy, 1);
                }
                else
                {
                    float inv = Store(Div(1, len));
                    n = new Vec3(Store(Mul(inv, dx)), Store(Mul(inv, dy)), Store(Mul(inv, dz)));
                }
            }
            else
            {
                Plane pl = mPlanes[lastPlane];
                n = new Vec3(pl.Nx, pl.Ny, pl.Nz);
                if (Side(pl, b) != -1)
                    n = new Vec3(-n.X, -n.Y, -n.Z);
            }
            hit = new Hit { Point = a, Normal = n, Leaf = leaf, PlaneIndex = lastPlane };
            return true;
        }
        if (node >= mNodes.Count)
            return false;
        BspNode nd = mNodes[node];
        if (nd.PlaneIndex >= mPlanes.Count)
            return false;
        Plane plane = mPlanes[nd.PlaneIndex];
        int k = Side(plane, b) + 3 * Side(plane, a);
        switch (k)
        {
            case 0: // both on the plane: node children only, back first (R3)
                if (nd.Back >= 0 && Rec(nd.Back, lastPlane, a, b, out hit))
                    return true;
                hit = default;
                return nd.Front >= 0 && Rec(nd.Front, lastPlane, a, b, out hit);
            case 1:
            case 3:
            case 4:
                return Rec(nd.Front, lastPlane, a, b, out hit);
            case -1:
            case -3:
            case -4:
                return Rec(nd.Back, lastPlane, a, b, out hit);
            case 2:
            {
                Vec3 m = Split(plane, a, b);
                return Rec(nd.Front, lastPlane, a, m, out hit) || Rec(nd.Back, nd.PlaneIndex, m, b, out hit);
            }
            default: // -2
            {
                Vec3 m = Split(plane, a, b);
                return Rec(nd.Back, lastPlane, a, m, out hit) || Rec(nd.Front, nd.PlaneIndex, m, b, out hit);
            }
        }
    }

    /// <summary>Finds the first solid leaf entered along a segment (00560420).</summary>
    /// <param name="a">The segment start in geometry space.</param>
    /// <param name="b">The segment end in geometry space.</param>
    /// <param name="hit">Receives the hit.</param>
    /// <returns>True on a hit.</returns>
    public bool CastLine(Vec3 a, Vec3 b, ref Hit hit)
    {
        if (!Rec(0, -1, a, b, out Hit h))
            return false;
        hit = h;
        return true;
    }

    // src/world/interior.cpp polyTest (0x00561744, docs/collision.md 5.2 part 3.2) with R2 = 0: the point is inside or
    // on every edge of the polygon.
    private static bool PolyTest(List<Vec3> pts, ReadOnlySpan<float> n, ReadOnlySpan<float> p)
    {
        int count = pts.Count;
        if (count == 0 || count > 32)
            return count == 0;
        static void V3(Vec3 v, Span<float> output)
        {
            output[0] = v.X;
            output[1] = v.Y;
            output[2] = v.Z;
        }
        static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) =>
            Add(Add(Mul(a[0], b[0]), Mul(a[1], b[1])), Mul(a[2], b[2]));
        uint mask = 0;
        Span<float> prev = stackalloc float[3], cur = stackalloc float[3];
        Span<float> e = stackalloc float[3], x = stackalloc float[3], w = stackalloc float[3];
        V3(pts[count - 1], prev);
        for (int i = 0; i < count; ++i)
        {
            V3(pts[i], cur);
            for (int k = 0; k < 3; ++k)
            {
                e[k] = Store(Sub(cur[k], prev[k]));
                w[k] = Store(Sub(p[k], prev[k]));
            }
            x[0] = Store(Sub(Mul(e[1], n[2]), Mul(e[2], n[1])));
            x[1] = Store(Sub(Mul(e[2], n[0]), Mul(e[0], n[2])));
            x[2] = Store(Sub(Mul(e[0], n[1]), Mul(e[1], n[0])));
            float s = Store(Dot(w, x)), l = Store(Dot(x, x));
            if (s < 0.0f)
            {
                if (Mul(s, s) > Mul(0.0f, l))
                    return false;
                mask |= 1u << i;
            }
            cur.CopyTo(prev);
        }
        if (mask == 0)
            return true;
        bool prevFlag = false;
        V3(pts[count - 1], prev);
        for (int i = 0; i < count; ++i)
        {
            V3(pts[i], cur);
            bool flag = false;
            if ((mask & (1u << i)) != 0)
            {
                for (int k = 0; k < 3; ++k)
                    e[k] = Store(Sub(cur[k], prev[k]));
                float a = Store(Dot(p, e));
                if (Dot(prev, e) > a)
                {
                    if (!prevFlag)
                    {
                        for (int k = 0; k < 3; ++k)
                            w[k] = Store(Sub(p[k], prev[k]));
                        if (Dot(w, w) < 0.0f)
                            return true;
                    }
                }
                else if (Dot(cur, e) >= a)
                {
                    return true;
                }
                else
                {
                    for (int k = 0; k < 3; ++k)
                        w[k] = Store(Sub(p[k], cur[k]));
                    if (Dot(w, w) < 0.0f)
                        return true;
                    flag = true;
                }
            }
            cur.CopyTo(prev);
            prevFlag = flag;
        }
        return false;
    }

    /// <summary>Finds the first surface of a solid leaf on a plane whose polygon holds a point (ITRCollision_surfaceInfo, 00561ba8, docs/collision.md 5.2 part 4.1).</summary>
    /// <param name="leaf">The solid leaf number.</param>
    /// <param name="plane">The plane index.</param>
    /// <param name="point">The point.</param>
    /// <returns>The surface index, or -1.</returns>
    public int SurfaceAt(int leaf, int plane, Vec3 point)
    {
        if (!IsSolidLeaf(leaf) || leaf - 43 >= mSolidLeaves.Count || plane < 0 || plane >= mPlanes.Count)
            return -1;
        SolidLeaf l = mSolidLeaves[leaf - 43];
        long p = l.Offset, end = (long)l.Offset + l.Count;
        if (end > mBits.Length)
            end = mBits.Length;
        ReadOnlySpan<float> P = stackalloc float[3] { point.X, point.Y, point.Z };
        Span<float> N = stackalloc float[3];
        long bas = -8;
        for (;;)
        {
            while (p + 1 < end && mBits[p] == 0)
            {
                bas += (long)mBits[p + 1] * 8;
                p += 2;
            }
            if (p >= end || mBits[p] == 0)
                break;
            bas += 8;
            byte bt = mBits[p++];
            for (int bit = 0; bit < 8; ++bit)
            {
                if ((bt & (0x80 >> bit)) == 0)
                    continue;
                long si = bas + bit;
                if (si < 0 || si >= mSurfaces.Count || mSurfaces[(int)si].PlaneIndex != plane)
                    continue;
                Surface sf = mSurfaces[(int)si];
                Plane pl = mPlanes[plane];
                // N = plane normal, negated when planeFront (flags bit 7) is clear.
                bool front = (sf.Flags & 0x80) != 0;
                N[0] = front ? pl.Nx : -pl.Nx;
                N[1] = front ? pl.Ny : -pl.Ny;
                N[2] = front ? pl.Nz : -pl.Nz;
                var pts = new List<Vec3>();
                bool ok = true;
                for (uint k = 0; k < sf.VertexCount && ok; ++k)
                {
                    long vi = (long)sf.FirstVertex + k;
                    ok = vi < mVertices.Count && mVertices[(int)vi] < mPoints.Count;
                    if (ok)
                        pts.Add(mPoints[mVertices[(int)vi]]);
                }
                if (ok && PolyTest(pts, N, P))
                    return (int)si;
            }
        }
        return -1;
    }

    /// <summary>Finds the first surface of a solid leaf whose plane index equals a plane (005611e0).</summary>
    /// <param name="leaf">The solid leaf number.</param>
    /// <param name="plane">The plane index.</param>
    /// <returns>The surface index, or -1.</returns>
    public int FindSurface(int leaf, int plane)
    {
        if (!IsSolidLeaf(leaf) || leaf - 43 >= mSolidLeaves.Count)
            return -1;
        SolidLeaf l = mSolidLeaves[leaf - 43];
        long p = l.Offset, end = (long)l.Offset + l.Count;
        if (end > mBits.Length)
            end = mBits.Length;
        long bas = -8;
        for (;;)
        {
            // Zero runs: {0, n} skips n bytes of surfaces. The original does not test the end inside this loop; here
            // the list bounds it.
            while (p + 1 < end && mBits[p] == 0)
            {
                bas += (long)mBits[p + 1] * 8;
                p += 2;
            }
            if (p >= end || mBits[p] == 0)
                break;
            bas += 8;
            byte bt = mBits[p++];
            for (int bit = 0; bit < 8; ++bit)
                if ((bt & (0x80 >> bit)) != 0)
                {
                    long s = bas + bit;
                    if (s >= 0 && s < mSurfaces.Count && mSurfaces[(int)s].PlaneIndex == plane)
                        return (int)s;
                }
        }
        return -1;
    }
}

/// <summary>A .dis interior shape file (docs/interiors.md 3).</summary>
public sealed class InteriorShapeFile
{
    /// <summary>One level of detail of the shape.</summary>
    public sealed class Lod
    {
        /// <summary>The .dig name.</summary>
        public string Geometry = "";
        /// <summary>The index of the first lodLightStates entry of this LOD.</summary>
        public uint LightStateIndex;
        /// <summary>The linkable faces mask.</summary>
        public uint LinkableFaces;
        /// <summary>The projected size below which the LOD is used when rendering (world-render.md 3.2).</summary>
        public uint MinPixels;
    }
    /// <summary>The .dig name of state 0, LOD 0.</summary>
    public string CollisionGeometry = "";
    /// <summary>The .dml name, stored after the name buffer.</summary>
    public string MaterialList = "";
    /// <summary>The linkedInterior byte after the material list name (+0x48).</summary>
    public bool LinkedInterior;
    /// <summary>The index in <see cref="Lods"/> of state 0, LOD 0.</summary>
    public uint State0Lod;
    /// <summary>Every LOD of every state.</summary>
    public readonly List<Lod> Lods = new();
    /// <summary>The lodLightStates entries as .dil names.</summary>
    public readonly List<string> LodLights = new();

    /// <summary>Parses a .dis file.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="output">Receives the parsed file.</param>
    /// <returns>True on success.</returns>
    public static bool Parse(byte[] data, InteriorShapeFile output)
    {
        var r = new Reader(data);
        if (data.Length < 12 || data[0] != 'I' || data[1] != 'T' || data[2] != 'R' || data[3] != 's')
            return false;
        r.Skip(8);
        r.GetU32(); // version
        uint nStates = r.GetU32();
        for (uint i = 0; i < nStates && r.Ok; ++i)
        {
            r.GetU32();
            uint lod = r.GetU32();
            r.GetU32();
            if (i == 0)
                output.State0Lod = lod;
        }
        uint nLods = r.GetU32();
        var geometryName = new List<uint>();
        var lightIndex = new List<uint>();
        var linkable = new List<uint>();
        var minPixels = new List<uint>();
        for (uint i = 0; i < nLods && r.Ok; ++i)
        {
            minPixels.Add(r.GetU32());
            geometryName.Add(r.GetU32());
            lightIndex.Add(r.GetU32());
            linkable.Add(r.GetU32());
        }
        uint nLodLights = r.GetU32();
        var lodLightName = new List<uint>();
        for (uint i = 0; i < nLodLights && r.Ok; ++i)
            lodLightName.Add(r.GetU32());
        r.Skip((long)r.GetU32() * 4); // light states
        uint nameSize = r.GetU32();
        long names = r.Pos;
        r.Skip(nameSize);
        if (!r.Ok || nStates == 0 || output.State0Lod >= nLods)
            return false;
        bool Name(uint off, out string s)
        {
            s = "";
            if (off >= nameSize)
                return false;
            s = Reader.CString(data, names + off, nameSize - off);
            return true;
        }
        output.Lods.Clear();
        for (uint i = 0; i < nLods; ++i)
            output.Lods.Add(new Lod());
        for (int i = 0; i < nLods; ++i)
        {
            if (!Name(geometryName[i], out string g))
                return false;
            output.Lods[i].Geometry = g;
            output.Lods[i].LightStateIndex = lightIndex[i];
            output.Lods[i].LinkableFaces = linkable[i];
            output.Lods[i].MinPixels = minPixels[i];
        }
        output.LodLights.Clear();
        for (uint i = 0; i < nLodLights; ++i)
            output.LodLights.Add("");
        for (int i = 0; i < nLodLights; ++i)
        {
            if (!Name(lodLightName[i], out string l))
                return false;
            output.LodLights[i] = l;
        }
        output.CollisionGeometry = output.Lods[(int)output.State0Lod].Geometry;
        // After the name buffer: u32 material list name offset (+0x4c).
        uint ml = r.GetU32();
        if (r.Ok && Name(ml, out string mls))
            output.MaterialList = mls;
        // Then the linkedInterior byte (00562718).
        byte linked = r.GetU8();
        if (r.Ok)
            output.LinkedInterior = linked != 0;
        return true;
    }
}

/// <summary>The light state of an ITRInstance (docs/interiors.md 5.4): the lights of the base lighting of state 0, LOD 0 and one run state per light (instance +0x34, 16 bytes each).</summary>
/// <remarks>Only the console commands change it. The ghost light records do not follow it.</remarks>
public sealed class InteriorLights
{
    /// <summary>One light record of the lighting.</summary>
    public sealed class Light
    {
        /// <summary>The light id (record +0x00).</summary>
        public int Id;
        /// <summary>Whether the light has a name, that is the name offset is not -1.</summary>
        public bool Named;
        /// <summary>The name from the name buffer (record +0x04).</summary>
        public string Name = "";
        /// <summary>The number of states (+0x08).</summary>
        public int NumStates;
        /// <summary>The index of the first state record (+0x0c).</summary>
        public int FirstState;
        /// <summary>The duration (+0x10).</summary>
        public float Duration;
        /// <summary>The flags (+0x14). Bit 2 loops to the last state and bit 4 is random.</summary>
        public uint Flags;
    }
    /// <summary>One 16-byte state record of the lighting (+0x30).</summary>
    public struct StateRecord // 16 bytes of the lighting (+0x30)
    {
        /// <summary>The state color.</summary>
        public UShort4 Color;
        /// <summary>The state end time (+8).</summary>
        public float Time;
    }
    /// <summary>The run state of one light.</summary>
    public struct Run
    {
        /// <summary>The current state.</summary>
        public int State;
        /// <summary>The time within the current state.</summary>
        public float Time;
        /// <summary>Extra run data.</summary>
        public int Aux;
        /// <summary>The current color.</summary>
        public uint Color;
    }

    private readonly List<Light> mLights = new();
    private StateRecord[] mStates = Array.Empty<StateRecord>();
    private Run[] mRun = Array.Empty<Run>();

    /// <summary>Gets the number of lights.</summary>
    /// <returns>The count.</returns>
    public int Size() => mLights.Count;
    /// <summary>Gets a light record.</summary>
    /// <param name="i">The light index.</param>
    /// <returns>The light.</returns>
    public Light GetLight(int i) => mLights[i];
    /// <summary>Gets the run state of a light.</summary>
    /// <param name="i">The light index.</param>
    /// <returns>The run state.</returns>
    public Run GetRun(int i) => mRun[i];

    /// <summary>Parses the light records of an ITRLighting block.</summary>
    /// <param name="d">The file bytes.</param>
    /// <returns>True on success.</returns>
    public bool Parse(byte[] d)
    {
        mLights.Clear();
        mStates = Array.Empty<StateRecord>();
        mRun = Array.Empty<Run>();
        var r = new Reader(d);
        if (!Interior.PersBody(d, r))
            return false;
        r.Skip(12); // buildId, two words
        uint nState = r.GetU32(), nStateData = r.GetU32(), nLights = r.GetU32();
        uint nLightState = r.GetU32(), nMapData = r.GetU32();
        long statePos = r.Pos;
        r.Skip((long)nState * 16 + (long)nStateData * 8);
        long lightPos = r.Pos;
        r.Skip((long)nLights * 24 + (long)nLightState * 12 + nMapData);
        uint nameSize = r.GetU32();
        long namePos = r.Pos;
        r.Skip(nameSize);
        if (!r.Ok)
            return false;
        mStates = new StateRecord[nState];
        for (uint i = 0; i < nState; ++i)
        {
            long p = statePos + (long)i * 16;
            for (int k = 0; k < 4; ++k)
                mStates[i].Color[k] = Reader.U16At(d, p + 2 * k);
            mStates[i].Time = Reader.F32At(d, p + 8);
        }
        for (uint i = 0; i < nLights; ++i)
        {
            long p = lightPos + (long)i * 24;
            var l = new Light();
            l.Id = Reader.I32At(d, p);
            uint nameOff = Reader.U32At(d, p + 4);
            l.NumStates = Reader.I32At(d, p + 8);
            l.FirstState = Reader.I32At(d, p + 12);
            l.Duration = Reader.F32At(d, p + 16);
            l.Flags = Reader.U32At(d, p + 20);
            if (nameOff != 0xffffffffu && nameOff < nameSize)
            {
                l.Named = true;
                l.Name = Reader.CString(d, namePos + nameOff, nameSize - nameOff);
            }
            mLights.Add(l);
        }
        mRun = new Run[nLights]; // the instance constructor zeroes the array (0x00555f48)
        return true;
    }

    /// <summary>Finds a light by name (0055c7ec).</summary>
    /// <param name="name">The exact name.</param>
    /// <returns>The id of the first light with this name, -1 when none.</returns>
    public int FindByName(string name)
    {
        foreach (Light l in mLights)
            if (l.Named && l.Name == name)
                return l.Id;
        return -1;
    }

    /// <summary>Finds the first light whose id is not below an id by halving search (0055d804).</summary>
    /// <param name="id">The light id.</param>
    /// <returns>The light index, -1 past the end.</returns>
    public int FindIndex(int id)
    {
        int first = 0, len = mLights.Count;
        while (len > 0)
        {
            int half = len / 2;
            if (mLights[first + half].Id < id)
            {
                first += half + 1;
                len -= half + 1;
            }
            else
            {
                len = half;
            }
        }
        return first == mLights.Count ? -1 : first;
    }

    /// <summary>Gets the current state of a light (00556e4c).</summary>
    /// <param name="id">The light id.</param>
    /// <returns>The state, -1 for an unknown id.</returns>
    public int StateOf(int id)
    {
        int i = FindIndex(id);
        return i < 0 ? -1 : mRun[i].State;
    }

    /// <summary>Sets the state of a light (00556898).</summary>
    /// <param name="id">The light id.</param>
    /// <param name="state">The new state.</param>
    public void SetState(int id, int state)
    {
        int idx = FindIndex(id);
        if (idx < 0 || state == mRun[idx].State)
            return;
        Light l = mLights[idx];
        ref Run run = ref mRun[idx];
        if (state < 0)
            state = 0;
        if (state > l.NumStates)
            state = l.NumStates;
        run.State = state;
        // The original reads record firstState + state, which for state == numStates is the first record of the next
        // light and past the array for the last one (read as 0 here).
        long at = (long)l.FirstState + state;
        if (at < 0 || at >= mStates.Length)
            return;
        StateRecord rec = mStates[at];
        run.Time = rec.Time;
        run.Color = (uint)((rec.Color[3] + 0x80) >> 8) << 24 | (uint)((rec.Color[0] + 0x80) >> 8) << 16 |
                    (uint)((rec.Color[1] + 0x80) >> 8) << 8 | (uint)((rec.Color[2] + 0x80) >> 8);
    }

    /// <summary>Sets the time of a light (00556dd4).</summary>
    /// <param name="id">The light id.</param>
    /// <param name="time">The new time.</param>
    public void SetTime(int id, float time)
    {
        int idx = FindIndex(id);
        if (idx < 0)
            return;
        Light l = mLights[idx];
        if (!(time >= 0)) // fcomp/sahf: below zero and NaN
            time = 0;
        if (time > l.Duration)
            time = l.Duration;
        mRun[idx].Time = time;
        Animate(idx);
    }

    // src/world/interior.cpp InteriorLights::animate: 0x00556b28.
    private void Animate(int index)
    {
        Light l = mLights[index];
        ref Run run = ref mRun[index];
        if ((l.Flags & 4) == 0)
        {
            // The state is the number of following records whose end time lies below the time.
            int i = 0;
            for (long at = l.FirstState; i < l.NumStates - 1; ++at, ++i)
            {
                if (at + 1 < 0 || at + 1 >= mStates.Length || !(mStates[at + 1].Time < run.Time))
                    break;
            }
            run.State = i;
            return;
        }
        // Random light (0x00556a14): the time wraps by the end time of record 1, then the original picks a state with a
        // generator private to this code (0x006aeef0), which nothing else reads. The state stays as it was. A record
        // time of 0 would loop forever in the original.
        long at1 = (long)l.FirstState + 1;
        if (at1 < 0 || at1 >= mStates.Length)
            return;
        float period = mStates[at1].Time;
        if (period > 0 && float.IsFinite(period) && float.IsFinite(run.Time))
            for (int n = 0; n < (1 << 20) && period <= run.Time; ++n) // bounded: a tiny period is slow
                run.Time = SubF(run.Time, period);
    }
}

/// <summary>What the InteriorShape initial ghost update sends (docs/game-stream-ghosts.md 3.4).</summary>
public sealed class InteriorNetInfo
{
    /// <summary>The base light state of one light.</summary>
    public struct Light
    {
        /// <summary>The intensity (.dil light record +0x10).</summary>
        public float Intensity;
        /// <summary>Whether the light is on (.dil light record +0x14 bit 0).</summary>
        public bool On;
    }
    /// <summary>The buildId of the .dig of every LOD, then the checksum of the base .dil of every LOD in light state 0 (00484428).</summary>
    public List<uint> StateWords = new();
    /// <summary>The base .dil lights of state 0, LOD 0 (0047f00c).</summary>
    public List<Light> Lights = new();
    /// <summary>The same lighting with its run state (instance +0x98, +0x34).</summary>
    public InteriorLights Lighting = new();
}

/// <summary>A row-vector transform: world = local * M + p (docs/interiors.md 5.2).</summary>
public struct Mat
{
    /// <summary>The 3x3 rotation.</summary>
    public Float9 M;
    /// <summary>The translation.</summary>
    public Vec3 P;

    /// <summary>Creates the identity transform.</summary>
    public Mat()
    {
        M = Float9.Identity();
        P = default;
    }

    /// <summary>Transforms a point to world space.</summary>
    /// <param name="v">The local point.</param>
    /// <returns>The world point.</returns>
    public readonly Vec3 ToWorld(Vec3 v) =>
        new(Store(Add(Add(Add(Mul(v.X, M[0]), Mul(v.Y, M[3])), Mul(v.Z, M[6])), P.X)),
            Store(Add(Add(Add(Mul(v.X, M[1]), Mul(v.Y, M[4])), Mul(v.Z, M[7])), P.Y)),
            Store(Add(Add(Add(Mul(v.X, M[2]), Mul(v.Y, M[5])), Mul(v.Z, M[8])), P.Z)));

    /// <summary>Transforms a vector to world space, without the translation.</summary>
    /// <param name="v">The local vector.</param>
    /// <returns>The world vector.</returns>
    public readonly Vec3 ToWorldVector(Vec3 v) =>
        new(Store(Add(Add(Mul(v.X, M[0]), Mul(v.Y, M[3])), Mul(v.Z, M[6]))),
            Store(Add(Add(Mul(v.X, M[1]), Mul(v.Y, M[4])), Mul(v.Z, M[7]))),
            Store(Add(Add(Mul(v.X, M[2]), Mul(v.Y, M[5])), Mul(v.Z, M[8]))));

    /// <summary>Transforms a world point to local space.</summary>
    /// <param name="v">The world point.</param>
    /// <returns>The local point.</returns>
    public readonly Vec3 ToLocal(Vec3 v)
    {
        double dx = Sub(v.X, P.X), dy = Sub(v.Y, P.Y), dz = Sub(v.Z, P.Z);
        return new Vec3(Store(Add(Add(Mul(dx, M[0]), Mul(dy, M[1])), Mul(dz, M[2]))),
                        Store(Add(Add(Mul(dx, M[3]), Mul(dy, M[4])), Mul(dz, M[5]))),
                        Store(Add(Add(Mul(dx, M[6]), Mul(dy, M[7])), Mul(dz, M[8]))));
    }
}

/// <summary>Loads interior files and lighting through the resource manager.</summary>
public static class Interior
{
    /// <summary>Resolves an InteriorShape file name, with the mission-lit fallback (L3).</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="fileName">The InteriorShape file name.</param>
    /// <returns>The resolved name, empty when absent.</returns>
    public static string ResolveInteriorName(ResourceManager res, string fileName)
    {
        if (res.Find(fileName) != null)
            return fileName;
        // L3: <base>.<digits>.<ext> retries <base>.dis.
        int last = fileName.LastIndexOf('.');
        int prev = last < 0 || last == 0 ? -1 : fileName.LastIndexOf('.', last - 1);
        if (prev < 0)
            return "";
        string digits = fileName.Substring(prev + 1, last - prev - 1);
        if (digits.Length == 0 || digits.Any(c => c < '0' || c > '9'))
            return "";
        string bas = fileName[..prev] + ".dis";
        return res.Find(bas) != null ? bas : "";
    }

    /// <summary>Gets the material list (.dml) name of an InteriorShape file.</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="fileName">The InteriorShape file name.</param>
    /// <returns>The name, empty when absent.</returns>
    public static string InteriorMaterialList(ResourceManager res, string fileName)
    {
        string name = ResolveInteriorName(res, fileName);
        var shape = new InteriorShapeFile();
        if (name.Length == 0 || !res.Read(name, out byte[] dis) || !InteriorShapeFile.Parse(dis, shape))
            return "";
        return shape.MaterialList;
    }

    /// <summary>Resolves an InteriorShape file name, with the mission-lit fallback (L3), and loads its geometry.</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="fileName">The InteriorShape file name.</param>
    /// <returns>The geometry, or null when absent or malformed.</returns>
    public static InteriorGeometry? LoadInterior(ResourceManager res, string fileName)
    {
        string name = ResolveInteriorName(res, fileName);
        var shape = new InteriorShapeFile();
        byte[] dig = Array.Empty<byte>();
        if (name.Length == 0 || !res.Read(name, out byte[] dis) || !InteriorShapeFile.Parse(dis, shape) ||
            !res.Read(shape.CollisionGeometry, out dig))
            return null;
        InteriorGeometry? geo = InteriorGeometry.Parse(dig);
        if (geo != null)
        {
            geo.MaterialListName = shape.MaterialList;
            geo.LinkedInterior = shape.LinkedInterior;
            geo.LinkableFaces = shape.Lods[(int)shape.State0Lod].LinkableFaces;
        }
        return geo;
    }

    // src/world/interior.cpp persBody: offset of the body after a PERS block header and its version
    // (docs/interiors.md 2).
    internal static bool PersBody(byte[] d, Reader r)
    {
        if (d.Length < 10 || d[0] != 'P' || d[1] != 'E' || d[2] != 'R' || d[3] != 'S')
            return false;
        r.Skip(8);
        ushort nameLen = r.GetU16();
        r.Skip((nameLen + 1u) & ~1u);
        r.GetU32(); // version
        return r.Ok;
    }

    // src/world/interior.cpp foldChecksum: 0x00583a78, rotate left by one and xor each byte, then fold to 16 bits.
    private static ushort FoldChecksum(byte[] p, long at, long n)
    {
        uint v = 0;
        for (long i = 0; i < n; ++i)
            v = ((v << 1) | (v >> 31)) ^ p[at + i];
        return (ushort)((v >> 16) ^ v);
    }

    // src/world/interior.cpp baseLightingName: 0x00555ec4, a mission lighting name <base>-<digits>-<n>.dil maps to
    // <base>-<digits>.dil.
    private static string BaseLightingName(string name)
    {
        int dash = name.LastIndexOf('-');
        if (dash < 0 || dash == 0)
            return name;
        int i = dash - 1;
        while (i > 0 && name[i] >= '0' && name[i] <= '9')
            --i;
        return name[i] == '-' ? name[..dash] + ".dil" : name;
    }

    // src/world/interior.cpp readLighting: ITRLighting (read 0x0055c8c0): checksum (0x0055d148) and the light records.
    private static bool ReadLighting(byte[] d, out uint checksum, List<InteriorNetInfo.Light>? lights,
                                     InteriorLights? lighting = null)
    {
        checksum = 0;
        var r = new Reader(d);
        if (!PersBody(d, r))
            return false;
        r.Skip(12); // buildId, two words
        uint nState = r.GetU32(), nStateData = r.GetU32(), nLights = r.GetU32();
        uint nLightState = r.GetU32(), nMapData = r.GetU32();
        r.Skip((long)nState * 16 + (long)nStateData * 8);
        long lightPos = r.Pos;
        r.Skip((long)nLights * 24 + (long)nLightState * 12);
        long mapPos = r.Pos;
        r.Skip(nMapData);
        uint nameSize = r.GetU32();
        long namePos = r.Pos;
        r.Skip(nameSize);
        if (!r.Ok)
            return false;
        checksum = (uint)FoldChecksum(d, mapPos, nMapData) << 16 | FoldChecksum(d, namePos, nameSize);
        if (lights != null)
        {
            lights.Clear();
            for (uint i = 0; i < nLights; ++i)
            {
                var l = new InteriorNetInfo.Light();
                l.Intensity = Reader.F32At(d, lightPos + i * 24 + 0x10);
                uint flags = Reader.U32At(d, lightPos + i * 24 + 0x14);
                l.On = (flags & 1) != 0;
                lights.Add(l);
            }
        }
        if (lighting != null && !lighting.Parse(d))
            return false;
        return true;
    }

    /// <summary>Loads what the InteriorShape initial ghost update sends.</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="fileName">The InteriorShape file name.</param>
    /// <param name="output">Receives the net info.</param>
    /// <returns>True on success.</returns>
    public static bool LoadInteriorNetInfo(ResourceManager res, string fileName, InteriorNetInfo output)
    {
        output.StateWords = new List<uint>();
        output.Lights = new List<InteriorNetInfo.Light>();
        output.Lighting = new InteriorLights();
        string name = ResolveInteriorName(res, fileName);
        var shape = new InteriorShapeFile();
        if (name.Length == 0 || !res.Read(name, out byte[] dis) || !InteriorShapeFile.Parse(dis, shape))
            return false;
        foreach (InteriorShapeFile.Lod lod in shape.Lods)
        {
            if (!res.Read(lod.Geometry, out byte[] dig))
                return false;
            var r = new Reader(dig);
            if (!PersBody(dig, r))
                return false;
            output.StateWords.Add(r.GetU32()); // buildId
        }
        for (int i = 0; i < shape.Lods.Count; ++i)
        {
            uint li = shape.Lods[i].LightStateIndex; // light state 0
            uint checksum = 0;
            if (li >= shape.LodLights.Count || !res.Read(BaseLightingName(shape.LodLights[(int)li]), out byte[] dil) ||
                !ReadLighting(dil, out checksum, i == shape.State0Lod ? output.Lights : null,
                              i == shape.State0Lod ? output.Lighting : null))
                return false;
            output.StateWords.Add(checksum);
        }
        return true;
    }

    /// <summary>Loads the resources of an InteriorShape's ITRInstance (0047ec6c, 00555f48).</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="fileName">The InteriorShape file name.</param>
    /// <param name="locks">Receives the locks. The object holds them and the instances stay until purge after it (docs/resources.md 8.7).</param>
    /// <remarks>Loads the .dis, its .dml and the bitmap of every texture material, and per LOD the .dig, the .dil of light state 0 when the file name is mission-lit and exists, and the base .dil.</remarks>
    public static void LoadInteriorResources(ResourceManager res, string fileName, ResourceLocks locks)
    {
        string name = ResolveInteriorName(res, fileName);
        var shape = new InteriorShapeFile();
        if (name.Length == 0 || !res.Read(name, out byte[] dis) || !InteriorShapeFile.Parse(dis, shape))
            return;
        locks.Load(res, name);
        // InteriorShape_isMissionLitName (0x0047ec6c): only digits between the last two dots. The fallback to
        // <base>.dis loads without mission lighting.
        bool missionLit = name == fileName;
        int last = fileName.LastIndexOf('.');
        int prev = last <= 0 ? -1 : fileName.LastIndexOf('.', last - 1);
        if (prev < 0)
            missionLit = false;
        else
            for (int i = prev + 1; i < last; ++i)
                if (fileName[i] < '0' || fileName[i] > '9')
                    missionLit = false;
        // The material list and its textures (the server object loads them, flags +0x4c bit 1 clear).
        var textures = new List<string>();
        if (locks.Load(res, shape.MaterialList) && res.Read(shape.MaterialList, out byte[] dml) &&
            MaterialList.MaterialListTextures(dml, textures))
            foreach (string t in textures)
                locks.Load(res, t);
        foreach (InteriorShapeFile.Lod lod in shape.Lods)
            locks.Load(res, lod.Geometry);
        var lit = new List<ResourceManager.Entry>(); // mission-lit .dil of the LODs so far
        foreach (InteriorShapeFile.Lod lod in shape.Lods)
        {
            if (lod.LightStateIndex >= shape.LodLights.Count)
                continue;
            string dil = shape.LodLights[(int)lod.LightStateIndex]; // light state 0
            if (missionLit)
            {
                if (res.Load(dil) is ResourceManager.Entry e)
                    lit.Add(e);
                // A missing file or a lighting buildId other than the geometry's prints an error, unlocks the
                // mission-lit .dil loaded so far (their instances stay until purge) and drops the mission lighting
                // for the later LODs.
                bool ok = false;
                if (res.Read(lod.Geometry, out byte[] g) && res.Read(dil, out byte[] l))
                {
                    var rg = new Reader(g);
                    var rl = new Reader(l);
                    ok = PersBody(g, rg) && PersBody(l, rl);
                    ok = ok && rl.GetU32() == rg.GetU32() && rl.Ok && rg.Ok;
                }
                if (!ok)
                {
                    foreach (ResourceManager.Entry x in lit)
                        ResourceManager.Unlock(x);
                    lit.Clear();
                    missionLit = false;
                }
            }
            locks.Load(res, BaseLightingName(dil));
        }
        foreach (ResourceManager.Entry x in lit)
            locks.Adopt(x);
    }
}
