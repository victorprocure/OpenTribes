namespace OpenTribes.Client.World;

using System.Buffers.Binary;

/// <summary>The <c>.dig</c> arrays the renderer reads: surfaces with their texture fields, vertex records, positions, texture coordinates and planes.</summary>
/// <remarks>world-render.md 3.4. The layout is in docs/interiors.md 4.1.</remarks>
public sealed class InteriorGeometryData
{
    /// <summary>A surface of the interior.</summary>
    public struct Surface
    {
        /// <summary>The flags: bit 0 type, bits 1 to 4 texture scale shift, 5 applyAmbient, 6 visibleToOutside, 7 planeFront.</summary>
        public byte Flags { get; set; }
        /// <summary>The material index, 255 for none.</summary>
        public byte Material { get; set; }
        /// <summary>The texture width. The file stores the value + 1.</summary>
        public byte TexSizeX { get; set; }
        /// <summary>The texture height. The file stores the value + 1.</summary>
        public byte TexSizeY { get; set; }
        /// <summary>The texture offset x.</summary>
        public byte TexOffsetX { get; set; }
        /// <summary>The texture offset y.</summary>
        public byte TexOffsetY { get; set; }
        /// <summary>The plane index.</summary>
        public ushort Plane { get; set; }
        /// <summary>The index of the first vertex record.</summary>
        public uint FirstVertex { get; set; }
        /// <summary>The index of the first point bit.</summary>
        public uint FirstPointBit { get; set; }
        /// <summary>The number of vertices.</summary>
        public byte VertexCount { get; set; }
        /// <summary>The number of point bits.</summary>
        public byte PointBitCount { get; set; }
        /// <summary>The texture scale shift.</summary>
        public readonly int ScaleShift => (Flags >> 1) & 0xf;
        /// <summary>True when the surface faces the front of its plane.</summary>
        public readonly bool PlaneFront => (Flags & 0x80) != 0;
        /// <summary>True when the surface is visible from outside.</summary>
        public readonly bool VisibleToOutside => (Flags & 0x40) != 0;
    }

    /// <summary>The build id of the file.</summary>
    public uint BuildId { get; set; }
    /// <summary>The texture scale.</summary>
    public float TextureScale { get; set; }
    /// <summary>The box as minimum x, y, z then maximum x, y, z.</summary>
    public float[] Box { get; set; } = new float[6];
    /// <summary>The highest mip level.</summary>
    public uint HighestMipLevel { get; set; }
    /// <summary>The geometry flags.</summary>
    public uint GeometryFlags { get; set; }
    /// <summary>The surfaces.</summary>
    public Surface[] Surfaces { get; set; } = [];
    /// <summary>The index of the 3D point of each vertex record.</summary>
    public ushort[] VertexPoint3 { get; set; } = [];
    /// <summary>The index of the texture coordinate of each vertex record.</summary>
    public ushort[] VertexPoint2 { get; set; } = [];
    /// <summary>The 3D points: x, y, z per point.</summary>
    public float[] Point3 { get; set; } = [];
    /// <summary>The texture coordinates: u, v per point.</summary>
    public float[] Point2 { get; set; } = [];
    /// <summary>The planes: nx, ny, nz, d.</summary>
    public float[] Planes { get; set; } = [];

    /// <summary>Reads the arrays of a <c>.dig</c> file.</summary>
    /// <param name="d">The file bytes.</param>
    /// <returns>The data, or null when the file is not valid.</returns>
    public static InteriorGeometryData? Parse(byte[] d)
    {
        try
        {
            if (d.Length < 10 || d[0] != 'P' || d[1] != 'E' || d[2] != 'R' || d[3] != 'S')
                return null;
            long pos = 8;
            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)pos, 2));
            pos += 2 + ((nameLen + 1) & ~1);
            pos += 4;
            uint U32()
            { var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos, 4)); pos += 4; return v; }
            float F32()
            { var v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)pos, 4)); pos += 4; return v; }
            var g = new InteriorGeometryData { BuildId = U32(), TextureScale = F32() };
            for (var i = 0; i < 6; ++i)
                g.Box[i] = F32();
            var counts = new uint[9];
            for (var i = 0; i < 9; ++i)
                counts[i] = U32();
            long[] sizes = [20, 8, 12, 44, 1, 4, 12, 8, 16];
            long total = 0;
            foreach (var (c, s) in counts.Zip(sizes))
                total += c * s;
            if (pos + total > d.Length)
                return null;
            var surfacePos = pos;
            pos += (counts[0] * 20) + (counts[1] * 8) + (counts[2] * 12) + (counts[3] * 44) + counts[4];
            var vertexPos = pos;
            pos += counts[5] * 4;
            var p3Pos = pos;
            pos += counts[6] * 12;
            var p2Pos = pos;
            pos += counts[7] * 8;
            var planePos = pos;
            pos += counts[8] * 16;
            if (pos + 8 <= d.Length)
            {
                g.HighestMipLevel = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos, 4));
                g.GeometryFlags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)pos + 4, 4));
            }
            g.Surfaces = new Surface[counts[0]];
            for (var i = 0; i < counts[0]; ++i)
            {
                var o = (int)surfacePos + (i * 20);
                g.Surfaces[i] = new Surface
                {
                    Flags = d[o],
                    Material = d[o + 1],
                    TexSizeX = d[o + 2],
                    TexSizeY = d[o + 3],
                    TexOffsetX = d[o + 4],
                    TexOffsetY = d[o + 5],
                    Plane = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 6, 2)),
                    FirstVertex = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 8, 4)),
                    FirstPointBit = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 12, 4)),
                    VertexCount = d[o + 16],
                    PointBitCount = d[o + 17],
                };
            }
            g.VertexPoint3 = new ushort[counts[5]];
            g.VertexPoint2 = new ushort[counts[5]];
            for (var i = 0; i < counts[5]; ++i)
            {
                g.VertexPoint3[i] = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)vertexPos + (i * 4), 2));
                g.VertexPoint2[i] = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)vertexPos + (i * 4) + 2, 2));
            }
            g.Point3 = new float[counts[6] * 3];
            for (var i = 0; i < g.Point3.Length; ++i)
            {
                var v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)p3Pos + (i * 4), 4));
                // interiors.md 4.3: a coordinate equal to the box minimum moves down by 0.0125, to the maximum up.
                var a = i % 3;
                if (v == g.Box[a])
                    v -= 0.0125f;
                else if (v == g.Box[a + 3])
                    v += 0.0125f;
                g.Point3[i] = v;
            }
            g.Point2 = new float[counts[7] * 2];
            for (var i = 0; i < g.Point2.Length; ++i)
                g.Point2[i] = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)p2Pos + (i * 4), 4));
            g.Planes = new float[counts[8] * 4];
            for (var i = 0; i < g.Planes.Length; ++i)
                g.Planes[i] = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)planePos + (i * 4), 4));
            return g;
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
