namespace OpenTribes.Tests;

using OpenTribes.Client.World;
using static Fixtures;

/// <summary>Malformed <c>.dig</c> and <c>.dml</c> files fail the render loaders instead of throwing during the scene build.</summary>
public class LoaderBoundsTests
{
    // One surface of three vertices on plane 0, three vertex records and three points of each kind.
    private static byte[] Dig(uint firstVertex = 0, byte vertexCount = 3, ushort plane = 0, ushort point3Of2 = 2, ushort point2Of2 = 2)
    {
        var b = new List<byte>();
        PutU32(b, 0x1234);
        PutF32(b, 1f);
        foreach (var f in new[] { -10f, -10f, -10f, 10f, 10f, 10f })
            PutF32(b, f);
        foreach (var c in new uint[] { 1, 0, 0, 0, 0, 3, 3, 3, 1 })
            PutU32(b, c);
        var surf = new byte[20];
        surf[16] = vertexCount;
        BitConverter.GetBytes(plane).CopyTo(surf, 6);
        BitConverter.GetBytes(firstVertex).CopyTo(surf, 8);
        b.AddRange(surf);
        for (ushort i = 0; i < 3; ++i)
        {
            PutU16(b, i == 2 ? point3Of2 : i);
            PutU16(b, i == 2 ? point2Of2 : i);
        }
        for (var i = 0; i < 9; ++i)
            PutF32(b, i);
        for (var i = 0; i < 6; ++i)
            PutF32(b, i * 0.1f);
        foreach (var f in new[] { 0f, 0f, 1f, 0f })
            PutF32(b, f);
        PutU32(b, 0);
        PutU32(b, 0);
        return [.. Pers("ITRGeometry", 7, b)];
    }

    [Fact]
    public void ValidInteriorGeometryStillLoads()
    {
        var g = InteriorGeometryData.Parse(Dig());
        Assert.NotNull(g);
        Assert.Single(g.Surfaces);
        Assert.Equal(3, g.VertexPoint3.Length);
        Assert.Equal(9, g.Point3.Length);
    }

    [Theory]
    [InlineData(1u, (byte)3)]    // records 1..3 of 3
    [InlineData(0u, (byte)4)]
    [InlineData(uint.MaxValue, (byte)3)]
    public void SurfaceVertexRangeOutsideTheRecordsIsRejected(uint first, byte count) => Assert.Null(InteriorGeometryData.Parse(Dig(first, count)));

    [Fact]
    public void VertexPointIndexOutsideThePointsIsRejected()
    {
        Assert.Null(InteriorGeometryData.Parse(Dig(point3Of2: 3)));
        Assert.Null(InteriorGeometryData.Parse(Dig(point2Of2: 3)));
        Assert.Null(InteriorGeometryData.Parse(Dig(point3Of2: ushort.MaxValue)));
    }

    [Fact]
    public void SurfaceTheMeshBuildSkipsIsNotChecked()
    {
        Assert.NotNull(InteriorGeometryData.Parse(Dig(firstVertex: 99, vertexCount: 2)));   // fewer than 3 vertices
        Assert.NotNull(InteriorGeometryData.Parse(Dig(firstVertex: 99, plane: 5)));         // no such plane
    }

    private static byte[] Dml(ushort nameLen, int bodyBytes)
    {
        var b = new List<byte>();
        PutStr(b, "PERS");
        PutU32(b, 0);
        PutU16(b, nameLen);
        b.AddRange(new byte[bodyBytes]);
        return [.. b];
    }

    [Theory]
    [InlineData(65535, 4)]   // the name runs past the end
    [InlineData(16, 2)]      // the fields after the name are cut
    [InlineData(16, 12)]     // version 0 is not a list
    public void ShortOrLyingMaterialListIsRejected(ushort nameLen, int body) => Assert.Null(RenderMaterialList.Parse(Dml(nameLen, body)));

    [Fact]
    public void ValidMaterialListStillLoads()
    {
        var b = new List<byte>();
        PutStr(b, "PERS");
        PutU32(b, 0);
        PutU16(b, 16);
        PutStr(b, "TS::MaterialList");
        PutU32(b, 4);
        PutU32(b, 1);
        PutU32(b, 1);
        var e = new byte[0x40];
        BitConverter.GetBytes(0x103u).CopyTo(e, 0);
        System.Text.Encoding.ASCII.GetBytes("a.bmp").CopyTo(e, 0x10);
        b.AddRange(e);
        Assert.Equal(["a.bmp"], RenderMaterialList.Parse([.. b])!);
    }
}
