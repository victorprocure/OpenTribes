namespace OpenTribes.Tests;

using OpenTribes.Client.Shapes;

/// <summary>Malformed <c>.dts</c> files fail the render reader instead of hanging or throwing later.</summary>
public class RenderShapeHardeningTests
{
    [Fact]
    public void ValidShapeStillLoads()
    {
        var s = RenderShape.Parse(SyntheticShape.Build(), "t");
        Assert.NotNull(s);
        Assert.Equal(3, s.Nodes.Length);
        Assert.Equal(2, s.RemapSize);
        Assert.Equal([0, 1, 2], [.. s.Subtree(0).Order()]);
        _ = new RenderInstance(s);
    }

    [Fact]
    public void ParentCycleIsRejected() => Assert.Null(RenderShape.Parse(SyntheticShape.Build(node0Parent: 1), "t")); // 0 -> 1 -> 0

    [Fact]
    public void NameIndexOutOfRangeIsRejected()
    {
        Assert.Null(RenderShape.Parse(SyntheticShape.Build(node2Name: 99), "t"));
        Assert.Null(RenderShape.Parse(SyntheticShape.Build(node2Name: -1), "t"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0x1001)]
    [InlineData(int.MaxValue)]
    public void RemapSizeOutOfRangeIsRejected(int size) => Assert.Null(RenderShape.Parse(SyntheticShape.Build(remapSize: size), "t"));

    [Fact]
    public void RemapSizeLimitLoads() => Assert.Equal(0x1000, RenderShape.Parse(SyntheticShape.Build(remapSize: 0x1000), "t")!.RemapSize);

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void FrameFirstVertOutsideTheVerticesIsRejected(int first) => Assert.Null(RenderShape.Parse(SyntheticShape.Build(firstVert: first), "t"));

    [Fact]
    public void FrameFirstVertAtTheEndLoads() => Assert.NotNull(RenderShape.Parse(SyntheticShape.Build(firstVert: 4), "t"));
}
