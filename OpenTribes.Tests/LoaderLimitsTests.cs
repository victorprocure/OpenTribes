namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Gui;
using OpenTribes.World;
using static Fixtures;

/// <summary>Counts and nesting taken from a file are bounded before they drive work or recursion.</summary>
public class LoaderLimitsTests
{
    private static ResourceManager TerrainRoot(string name, uint blockEntries, uint entryCountField)
    {
        var root = Path.Combine(TestPaths.TmpDir(""), name);
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        var t = new TerrainSpec { Heights = [.. Enumerable.Repeat(1f, 25)] };
        t.Squares = [.. new byte[t.N * t.N * 2]];
        var dtf = DtfFile(t, 0, 16);
        dtf.RemoveRange(dtf.Count - 12, 12);                 // drop the block list written by the fixture
        PutU32(dtf, entryCountField);
        for (var i = 0u; i < blockEntries; ++i)
        {
            PutU32(dtf, 0);                                  // every entry names block 0
            PutU32(dtf, 0);
        }
        SetU32(dtf, 4, (uint)(dtf.Count - 8));
        PutFile(root + "/base/t.dtf", dtf);
        PutFile(root + "/base/t#0.dtb", DtbFile(t, 0, 16));
        PutFile(root + "/base/t.dml", [(byte)'d']);
        var res = new ResourceManager(root);
        res.SetSearchPath("base");
        return res;
    }

    [Fact]
    public void TerrainBlockListStillLoads()
    {
        var t = Terrain.Load(TerrainRoot("lim_t1", 1, 1), "t.dtf");
        Assert.NotNull(t);
    }

    [Fact]
    public void TerrainRepeatedBlockIdsStillLoadAndEachEntryStaysLocked()
    {
        var res = TerrainRoot("lim_t2", 5000, 5000);
        Assert.NotNull(Terrain.Load(res, "t.dtf"));
        Assert.Equal(5000u, res.Find("t#0.dtb")!.Locks);
    }

    [Theory]
    [InlineData(0x7fffffffu)]
    [InlineData(0xffffffffu)]   // negative as read
    [InlineData(2u)]            // two entries claimed, one present
    public void TerrainBlockCountBeyondTheFileIsRejected(uint count) => Assert.Null(Terrain.Load(TerrainRoot("lim_t3", 1, count), "t.dtf"));

    // A chain of single child controls, `depth` levels deep.
    private static byte[] Chain(int depth)
    {
        var block = GuiW.Block("SGct", GuiW.Control());
        for (var i = 1; i < depth; ++i)
            block = GuiW.Block("SGct", GuiW.Control(kids: (block, "k")));
        return block;
    }

    private static int DepthOf(GuiControl c)
    {
        var n = 1;
        while (c.Children.Count > 0)
        {
            c = c.Children[0];
            ++n;
        }
        return n;
    }

    [Fact]
    public void GuiNestingAtTheLimitLoads()
    {
        var c = GuiLoader.Load(Chain(GuiLoadContext.MaxDepth), out var err);
        Assert.True(c != null, err);
        Assert.Equal(GuiLoadContext.MaxDepth, DepthOf(c));
    }

    [Fact]
    public void GuiNestingBeyondTheLimitIsRejected()
    {
        Assert.Null(GuiLoader.Load(Chain(GuiLoadContext.MaxDepth + 1), out var err));
        Assert.Contains("nested", err);
    }

    [Fact]
    public void GuiNestingLimitFitsAOneMegabyteStack()
    {
        // The limit must be safe on a thread with a small stack: a file one level over the limit fails cleanly.
        var data = Chain(GuiLoadContext.MaxDepth + 1);
        string? err = null;
        var th = new Thread(() => GuiLoader.Load(data, out err), 1024 * 1024);
        th.Start();
        th.Join();
        Assert.Contains("nested", err);
    }

    // CollisionSubtree must give the nodes the chain walk per node gave: the root, then by descending index every node
    // whose parent chain reaches the root (loops and out of range parents do not).
    private static List<int> Reference(int root, int count, int[] parent)
    {
        var nodes = new List<int> { root };
        for (var i = count - 1; i >= 0; --i)
        {
            var p = i;
            for (var depth = 0; depth < count && p >= 0 && p < count && p != root; ++depth)
                p = parent[p];
            if (i != root && p == root)
                nodes.Add(i);
        }
        return nodes;
    }

    [Fact]
    public void CollisionSubtreeMatchesTheChainWalkOnRandomParents()
    {
        var rnd = new Random(7);
        for (var round = 0; round < 3000; ++round)
        {
            var count = rnd.Next(1, 24);
            var parent = new int[count];
            for (var i = 0; i < count; ++i)
                parent[i] = rnd.Next(5) == 0 ? rnd.Next(-3, count + 3) : rnd.Next(-1, count);
            var root = rnd.Next(count);
            Assert.Equal(Reference(root, count, parent), Shape.CollisionSubtree(root, count, p => parent[p]));
        }
    }

    [Fact]
    public void CollisionSubtreeOfALongChainIsLinear()
    {
        const int n = 200000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var nodes = Shape.CollisionSubtree(0, n, p => p - 1);   // each node under the one before it
        Assert.Equal(n, nodes.Count);
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }
}
