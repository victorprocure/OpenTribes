// The bolt count of a Lightning beam: numSegments (LightningData +0xb8) bolt records in the render helper of Lightning_ctor 00467ef4,
// drawn by FUN_0046a530.
namespace OpenTribes.Tests;

using OpenTribes.Client.Scene;

public sealed class LightningBoltTests
{
    [Theory]
    [InlineData(5, 5)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void ValidCountsAreKept(double numSegments, int bolts) => Assert.Equal(bolts, BeamVisual.BoltCount("Lightning", numSegments));

    [Theory]
    [InlineData(9)]
    [InlineData(1000000)]
    [InlineData(2147483647)]
    public void HugeCountsAreClampedToTheStockRecords(double numSegments) => Assert.Equal(BeamVisual.MaxBolts, BeamVisual.BoltCount("Lightning", numSegments));

    [Fact]
    public void OtherBeamsDrawOneBolt() => Assert.Equal(1, BeamVisual.BoltCount("RepairEffect", 50));
}
