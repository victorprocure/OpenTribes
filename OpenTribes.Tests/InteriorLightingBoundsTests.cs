namespace OpenTribes.Tests;

using OpenTribes.Client.World;
using static Fixtures;

/// <summary>The Huffman table counts of a <c>.dil</c> are checked against the file length before allocation.</summary>
public class InteriorLightingBoundsTests
{
    private static byte[] Dil(uint nodeCount, uint leafCount, int nodesWritten, int leavesWritten)
    {
        var b = new List<byte>();
        PutU32(b, 1);                       // build id
        PutU32(b, 0);                       // light scale shift
        PutU32(b, 0);                       // map list size
        foreach (var c in new uint[] { 0, 0, 0, 0, 0 })
            PutU32(b, c);                   // states, data, lights, surfaces, map bytes
        PutU32(b, 0);                       // name bytes
        b.Add(1);                           // has a Huffman table
        PutU32(b, nodeCount);
        PutU32(b, leafCount);
        for (var i = 0; i < nodesWritten; ++i)
        {
            PutU32(b, (uint)-(i + 1));
            PutU32(b, (uint)-(i + 2));
        }
        for (var i = 0; i < leavesWritten; ++i)
            PutU32(b, (uint)(0x1111 * (i + 1)));
        return [.. Pers("ITRLighting", 3, b)];
    }

    [Fact]
    public void ValidTableStillLoads()
    {
        var lt = InteriorLighting.Parse(Dil(2, 3, 2, 3));
        Assert.NotNull(lt);
        Assert.True(lt.HasHuffman);
        Assert.Equal(2, lt.Nodes.GetLength(0));
        Assert.Equal(unchecked((int)0xffffffffu), lt.Nodes[0, 0]);
        Assert.Equal(unchecked((int)0xfffffffeu), lt.Nodes[0, 1]);
        Assert.Equal([0x1111u, 0x2222u, 0x3333u], lt.Leaves);
    }

    [Fact]
    public void EmptyTableLoads() => Assert.NotNull(InteriorLighting.Parse(Dil(0, 0, 0, 0)));

    [Theory]
    [InlineData(uint.MaxValue, 0u)]
    [InlineData(0x7fffffffu, 0u)]
    [InlineData(0u, uint.MaxValue)]
    [InlineData(0u, 0x40000000u)]
    [InlineData(uint.MaxValue, uint.MaxValue)]
    [InlineData(3u, 3u)]            // 3 nodes and 3 leaves claimed, 2 and 3 present
    public void CountsBeyondTheFileAreRejected(uint nodes, uint leaves) => Assert.Null(InteriorLighting.Parse(Dil(nodes, leaves, 2, 3)));
}
