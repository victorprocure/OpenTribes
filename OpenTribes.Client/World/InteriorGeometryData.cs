using System.Buffers.Binary;

namespace OpenTribes.Client.World;

// world-render.md 3.4: the `.dig` arrays the renderer reads (docs/interiors.md 4.1 for the layout): surfaces with their
// texture fields, vertex records, positions, texture coordinates and planes.
public sealed class InteriorGeometryData
{
    public struct Surface
    {
        public byte Flags;      // bit 0 type, bits 1-4 texture scale shift, 5 applyAmbient, 6 visibleToOutside, 7 planeFront
        public byte Material;   // 255 = none
        public byte TexSizeX, TexSizeY, TexOffsetX, TexOffsetY; // size stored as value + 1 in the file
        public ushort Plane;
        public uint FirstVertex;
        public uint FirstPointBit;
        public byte VertexCount, PointBitCount;
        public int ScaleShift => (Flags >> 1) & 0xf;
        public bool PlaneFront => (Flags & 0x80) != 0;
        public bool VisibleToOutside => (Flags & 0x40) != 0;
    }

    public uint BuildId;
    public float TextureScale;
    public float[] Box = new float[6];
    public uint HighestMipLevel, GeometryFlags;
    public Surface[] Surfaces = Array.Empty<Surface>();
    public ushort[] VertexPoint3 = Array.Empty<ushort>(), VertexPoint2 = Array.Empty<ushort>();
    public float[] Point3 = Array.Empty<float>(); // x y z per point
    public float[] Point2 = Array.Empty<float>(); // u v per point
    public float[] Planes = Array.Empty<float>(); // nx ny nz d

    public static InteriorGeometryData? Parse(byte[] d)
    {
        try
        {
            if (d.Length < 10 || d[0] != 'P' || d[1] != 'E' || d[2] != 'R' || d[3] != 'S')
                return null;
            long pos = 8;
            ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)pos, 2));
            pos += 2 + ((nameLen + 1) & ~1);
            pos += 4;
            uint U32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos, 4)); pos += 4; return v; }
            float F32() { float v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)pos, 4)); pos += 4; return v; }
            var g = new InteriorGeometryData { BuildId = U32(), TextureScale = F32() };
            for (int i = 0; i < 6; ++i)
                g.Box[i] = F32();
            var counts = new uint[9];
            for (int i = 0; i < 9; ++i)
                counts[i] = U32();
            long[] sizes = { 20, 8, 12, 44, 1, 4, 12, 8, 16 };
            long total = 0;
            foreach (var (c, s) in counts.Zip(sizes))
                total += c * s;
            if (pos + total > d.Length)
                return null;
            long surfacePos = pos;
            pos += counts[0] * 20 + counts[1] * 8 + counts[2] * 12 + counts[3] * 44 + counts[4];
            long vertexPos = pos;
            pos += counts[5] * 4;
            long p3Pos = pos;
            pos += counts[6] * 12;
            long p2Pos = pos;
            pos += counts[7] * 8;
            long planePos = pos;
            pos += counts[8] * 16;
            if (pos + 8 <= d.Length)
            {
                g.HighestMipLevel = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos, 4));
                g.GeometryFlags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos + 4, 4));
            }
            g.Surfaces = new Surface[counts[0]];
            for (int i = 0; i < counts[0]; ++i)
            {
                int o = (int)surfacePos + i * 20;
                g.Surfaces[i] = new Surface
                {
                    Flags = d[o], Material = d[o + 1], TexSizeX = d[o + 2], TexSizeY = d[o + 3], TexOffsetX = d[o + 4], TexOffsetY = d[o + 5],
                    Plane = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 6, 2)),
                    FirstVertex = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 8, 4)),
                    FirstPointBit = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 12, 4)),
                    VertexCount = d[o + 16], PointBitCount = d[o + 17],
                };
            }
            g.VertexPoint3 = new ushort[counts[5]];
            g.VertexPoint2 = new ushort[counts[5]];
            for (int i = 0; i < counts[5]; ++i)
            {
                g.VertexPoint3[i] = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)vertexPos + i * 4, 2));
                g.VertexPoint2[i] = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)vertexPos + i * 4 + 2, 2));
            }
            g.Point3 = new float[counts[6] * 3];
            for (int i = 0; i < g.Point3.Length; ++i)
            {
                float v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)p3Pos + i * 4, 4));
                // interiors.md 4.3: a coordinate equal to the box minimum moves down by 0.0125, to the maximum up.
                int a = i % 3;
                if (v == g.Box[a])
                    v -= 0.0125f;
                else if (v == g.Box[a + 3])
                    v += 0.0125f;
                g.Point3[i] = v;
            }
            g.Point2 = new float[counts[7] * 2];
            for (int i = 0; i < g.Point2.Length; ++i)
                g.Point2[i] = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)p2Pos + i * 4, 4));
            g.Planes = new float[counts[8] * 4];
            for (int i = 0; i < g.Planes.Length; ++i)
                g.Planes[i] = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)planePos + i * 4, 4));
            return g;
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
