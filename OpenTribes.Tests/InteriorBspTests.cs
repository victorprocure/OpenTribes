namespace OpenTribes.Tests;

using OpenTribes.World;
using static Fixtures;

/// <summary>The interior BSP is checked once at load so the walks need no guard.</summary>
public class InteriorBspTests
{
    private static readonly float[] Box = [-10, -10, -10, 10, 10, 10];
    private static readonly float[] Plane = [1, 0, 0, 0];

    private static InteriorGeometry? Load(Node[] nodes, int planes = 1) =>
        InteriorGeometry.Parse([.. DigFile(1, Box, nodes, 1, 45, [.. Enumerable.Repeat(Plane, planes)])]);

    // Leaf children: -44 is solid leaf 43, -45 an empty leaf.
    [Fact]
    public void ValidTreeLoadsAndWalksAsBefore()
    {
        var g = Load([new Node(0, 1, -44), new Node(0, -45, 2), new Node(0, -45, -44)]);
        Assert.NotNull(g);
        Assert.Equal(44, g.LeafAt(new Vec3(5, 0, 0)));      // x > 0 front, node 1 front: leaf 44
        Assert.Equal(43, g.LeafAt(new Vec3(-5, 0, 0)));     // back of node 0: solid leaf 43
    }

    [Fact]
    public void SharedChildIsNotACycle() => Assert.NotNull(Load([new Node(0, 1, 1), new Node(0, -44, -45)]));

    [Fact]
    public void DeepValidChainLoads()
    {
        var nodes = Enumerable.Range(0, 1000).Select(i => new Node(0, (short)(i + 1), -44)).ToArray();
        nodes[^1] = new Node(0, -45, -44);
        Assert.NotNull(Load(nodes));
    }

    [Fact]
    public void SelfLoopIsRejected() => Assert.Null(Load([new Node(0, 0, -44)]));

    [Fact]
    public void LoopToAnAncestorIsRejected() => Assert.Null(Load([new Node(0, 1, -44), new Node(0, 2, -44), new Node(0, -45, 0)]));

    [Fact]
    public void ChildOutsideTheNodesIsRejected()
    {
        Assert.Null(Load([new Node(0, 5, -44)]));
        Assert.Null(Load([new Node(0, -44, 1)]));
    }

    [Fact]
    public void PlaneOutsideThePlanesIsRejected() => Assert.Null(Load([new Node(3, -44, -45)]));

    [Fact]
    public void ChainDeeperThanTheLimitIsRejected()
    {
        var nodes = Enumerable.Range(0, 2000).Select(i => new Node(0, (short)(i + 1), -44)).ToArray();
        nodes[^1] = new Node(0, -45, -44);
        Assert.Null(Load(nodes));
    }

    [Fact]
    public void UnreachableBadNodeIsNotChecked() => Assert.NotNull(Load([new Node(0, -44, -45), new Node(9, 77, 77)]));
}
