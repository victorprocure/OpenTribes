// PartDebris radius, end and split (docs/shapes-render.md 4.4): loadShape 0047603c, part box 005c3b18 / 005ad14c, end of a piece 004762e0,
// split cloud 004764f8 / 004765c0, row choice 00475edc, part split 005c4060 / 005c41ac.
using OpenTribes.Client.Effects;
using OpenTribes.Client.Shapes;
using OpenTribes.Game;
using Xunit;

namespace OpenTribes.Tests;

public class PartDebrisTests
{
    // A mesh with the packed vertices of its frames: 4 bytes each (x, y, z, normal), frame k reads the two vertices from FirstVert.
    private static RsMesh Mesh(params (byte[] v0, byte[] v1, float[] scale, float[] origin)[] frames)
    {
        var m = new RsMesh();
        var verts = new List<byte>();
        var fr = new List<RsFrame>();
        foreach (var f in frames)
        {
            fr.Add(new RsFrame { FirstVert = verts.Count / 4, Sx = f.scale[0], Sy = f.scale[1], Sz = f.scale[2], Ox = f.origin[0], Oy = f.origin[1], Oz = f.origin[2] });
            verts.AddRange(f.v0); verts.Add(0);
            verts.AddRange(f.v1); verts.Add(0);
        }
        m.Verts = verts.ToArray();
        m.Frames = fr.ToArray();
        return m;
    }

    [Fact]
    public void RadiusIsHalfTheBitTrickLengthOfTheFrameBoxSize() // 0047603c, 005ed031, 005c3b18
    {
        // box (-1, -0.5, -0.25)..(1, 0.5, 0.25): size (2, 1, 0.5), squared length 5.25. m_Point3F_lenf gives 2.3125 (bits >> 1 + 0x1fc00000), not 2.2913
        var m = Mesh((new byte[] { 0, 0, 0 }, new byte[] { 200, 100, 50 }, new[] { 0.01f, 0.01f, 0.01f }, new[] { -1f, -0.5f, -0.25f }));
        PartBox b = PartDebris.Measure(m, 0);
        Assert.Equal(1.15625f, b.Radius);
        Assert.NotEqual((float)(Math.Sqrt(5.25) * 0.5), b.Radius);
        Assert.InRange(Math.Abs(b.Cx) + Math.Abs(b.Cy) + Math.Abs(b.Cz), 0, 1e-6);   // a box symmetric about 0 (up to float rounding of the decode)
        Assert.InRange(Math.Abs(b.SphereX) + Math.Abs(b.SphereY) + Math.Abs(b.SphereZ), 0, 1e-6);   // the part box is centred (up to float rounding): the sphere sits on the piece origin
    }

    [Fact]
    public void BoxIsReadAtTheObjectsCurrentFrameAndTheCentreIsTheMiddleOfIt() // 005ad14c, 005c3b18
    {
        var m = Mesh((new byte[] { 0, 0, 0 }, new byte[] { 200, 100, 50 }, new[] { 0.01f, 0.01f, 0.01f }, new[] { -1f, -0.5f, -0.25f }),
                     (new byte[] { 10, 20, 30 }, new byte[] { 110, 220, 130 }, new[] { 0.02f, 0.01f, 0.05f }, new[] { 1f, 2f, 3f }));
        PartBox b = PartDebris.Measure(m, 1);
        Assert.Equal((1.2f, 2.2f, 4.5f), (b.MinX, b.MinY, b.MinZ));
        Assert.Equal((3.2f, 4.2f, 9.5f), (b.MaxX, b.MaxY, b.MaxZ), new BoxComparer());
        Assert.Equal(2.2f, b.Cx);
        Assert.Equal(7f, b.Cz);
        Assert.Equal(3.03125f, b.Radius, 1e-5f);   // size (2, 2, 5), squared length 33: the bit trick root 6.0625 halved (the exact half diagonal is 2.872)
        Assert.Equal(PartDebris.Measure(m, 1).Radius, PartDebris.Measure(m, 7).Radius);   // an index past the mesh clamps to the last frame
    }

    private sealed class BoxComparer : IEqualityComparer<(float, float, float)>
    {
        public bool Equals((float, float, float) a, (float, float, float) b) => Math.Abs(a.Item1 - b.Item1) < 1e-5 && Math.Abs(a.Item2 - b.Item2) < 1e-5 && Math.Abs(a.Item3 - b.Item3) < 1e-5;
        public int GetHashCode((float, float, float) o) => 0;
    }

    [Fact]
    public void ApartWithOneObjectEndsInDebrisExplodeAndAnyOtherCountSplitsAgain() // 004762e0
    {
        PartDebris.End one = PartDebris.Ending(1, 10000, 2, 5, 50, false);
        Assert.True(one.ExplodeLikeDebris);
        Assert.False(one.Split);

        PartDebris.End many = PartDebris.Ending(3, 10000, 2.5, 5, 50, true);
        Assert.False(many.ExplodeLikeDebris);
        Assert.True(many.Split);
        Assert.True(many.Explosion);
        Assert.Equal(9997.5, many.Budget);               // the budget less the seconds lived
        Assert.Equal(50, many.Strength);                 // spawnedDebrisStrength of the DebrisData
        Assert.Equal(0.6499999761581421, many.SplitChance);   // 0x3f266666 of 004765c0
        Assert.True(many.CopyOwner);

        Assert.True(PartDebris.Ending(0, 10000, 1, 5, 50, false).Split);   // the test is "!= 1"
    }

    [Fact]
    public void ASplitNeedsBudgetLeftAndTheExplosionTestIsForIdZero() // 004762e0
    {
        PartDebris.End spent = PartDebris.Ending(2, 3, 3, 5, 50, false);   // budget 3 - 3 seconds = 0: nothing is made, the piece is only deleted
        Assert.False(spent.Split);
        Assert.False(spent.Explosion);
        Assert.False(spent.ExplodeLikeDebris);

        PartDebris.End noExplosion = PartDebris.Ending(2, 100, 1, 0, 50, false);   // 004762e0 skips id 0 (Debris_explode skips -1)
        Assert.True(noExplosion.Split);
        Assert.False(noExplosion.Explosion);
    }

    [Fact]
    public void RowChoiceSkipsRowsByPartSizeWithoutDrawingAndDrawsTwiceForTheRowTaken() // 00475edc
    {
        // size (2, 2, 2): quarter of the squared length = 3. Row 0 has factor 1 (1 * 1 < 3 skipped), row 1 factor 4 (4 * 1 = 4 passes) with chance 1
        var rows = new List<PartDebris.Row>
        {
            new(10, 100f, 1f, 1f, 0.5f, 1),
            new(11, 200f, 4f, 1f, 0.5f, 2),
            new(12, 300f, 9f, 1f, 1f, 3),
        };
        var rng = new R250(0x8377);
        var replay = new R250(0x8377);
        PartDebris.RowPick pick = PartDebris.PickRow(rows, 2, 2, 2, rng);
        Assert.Equal(1, pick.Index);
        PartDebris.Draw(replay);                                                // row 1: the chance draw, then the end-at-once draw
        PartDebris.Draw(replay);
        Assert.Equal(replay.Next(), rng.Next());                                // exactly two draws were used (row 0 was skipped by size, no draw)
        Assert.False(pick.EndsAtOnce);                                          // (1 - 0.5) * 1.0 + 0.5 = 1.0 is never below a draw * 0.99
    }

    [Fact]
    public void RowChoiceDrawsOnceForEachRowThatIsBigEnoughAndFailsItsChance() // 00475edc
    {
        // both rows pass the size test, row 0 has chance 0 (any draw * 0.99 above it fails unless the draw is 0), row 1 takes the piece
        var rows = new List<PartDebris.Row> { new(1, 1f, 100f, 0f, 0f, 0), new(2, 1f, 100f, 1f, 0f, 0) };
        var rng = new R250(0x8377);
        var replay = new R250(0x8377);
        PartDebris.RowPick pick = PartDebris.PickRow(rows, 1, 1, 1, rng);
        Assert.Equal(1, pick.Index);
        double a = PartDebris.Draw(replay);
        Assert.True(a * 0.99 > 0);                                              // so row 0 (chance 0) is skipped
        PartDebris.Draw(replay);
        PartDebris.Draw(replay);
        Assert.Equal(replay.Next(), rng.Next());                                // three draws: row 0's chance, row 1's chance, the end-at-once draw
        Assert.False(pick.EndsAtOnce);                                          // KeepChance 0: (1 - 0) * 1.0 + 0 = 1.0 is never below a draw * 0.99
    }

    [Fact]
    public void RowChoiceEndsAtOnceWhenKeepChanceIsBelowTheDraw() // 00475edc: the expression (1 - k) * 1.0 + k is 1.0 for any k, so the flag is never set with the stock constant
    {
        var rows = new List<PartDebris.Row> { new(1, 1f, 100f, 1f, 0.25f, 0) };
        for (uint seed = 1; seed < 50; ++seed)
            Assert.False(PartDebris.PickRow(rows, 1, 1, 1, new R250(seed)).EndsAtOnce);
    }

    [Fact]
    public void RowChoiceWithNoFittingRowReturnsMinusOne()
    {
        var rows = new List<PartDebris.Row> { new(1, 1f, 1f, 1f, 0f, 0) };
        Assert.Equal(-1, PartDebris.PickRow(rows, 10, 10, 10, new R250(1)).Index);
    }

    private static SplitNode Node(params int[] objects)
    {
        var n = new SplitNode();
        foreach (int o in objects)
            n.Objects.Add(new SplitObject { Index = o });
        return n;
    }

    // root(0) with children a(1) -> sibling b(2), a has child c(3)
    private static SplitNode Tree()
    {
        SplitNode root = Node(0), a = Node(1), b = Node(2), c = Node(3);
        root.Child = a;
        a.Sibling = b;
        a.Child = c;
        return root;
    }

    [Fact]
    public void SplitChanceBelowEveryDrawKeepsTheWholeTreeInOnePart() // 005c41ac: split only when the draw is below the chance
    {
        var rng = new R250(0x8377);
        List<SplitPart> parts = PartSplitter.Split(Tree(), 0.0, rng);
        Assert.Single(parts);
        Assert.Equal(new[] { 0, 1, 3, 2 }, parts[0].Objects);   // node, first child, the child of that, then its sibling (children before the sibling)
        Assert.Equal(4, parts[0].Count);
        // one draw per node visited: 4 nodes
        var replay = new R250(0x8377);
        for (int i = 0; i < 4; ++i)
            replay.Next();
        Assert.Equal(replay.Next(), rng.Next());
    }

    [Fact]
    public void SplitChanceAboveEveryDrawMakesAPartOfEachChildSubtree() // 005c41ac: the children become parts of their own
    {
        var rng = new R250(0x8377);
        List<SplitPart> parts = PartSplitter.Split(Tree(), 1.0, rng);
        // the root keeps its own object, then child a (with its child c split off again) and the sibling b start parts of their own
        Assert.Equal(4, parts.Count);
        Assert.Equal(new[] { 0 }, parts[0].Objects);
        Assert.Equal(new[] { 1 }, parts[1].Objects);
        Assert.Equal(new[] { 3 }, parts[2].Objects);   // c is split off while a is built, before the part of b is added
        Assert.Equal(new[] { 2 }, parts[3].Objects);
        var replay = new R250(0x8377);
        for (int i = 0; i < 4; ++i)
            replay.Next();
        Assert.Equal(replay.Next(), rng.Next());
    }

    [Fact]
    public void PartsWithoutObjectsAreDroppedByMovingTheLastPartIntoTheirSlot() // 005c4060
    {
        // root has no object, its two children have one each, the chance is 1: parts are root (empty), a, b -> a (the last, b) takes the slot of the empty root
        SplitNode root = Node(), a = Node(1), b = Node(2);
        root.Child = a;
        a.Sibling = b;
        List<SplitPart> parts = PartSplitter.Split(root, 1.0, new R250(0x8377));
        Assert.Equal(2, parts.Count);
        Assert.Equal(new[] { 2 }, parts[0].Objects);
        Assert.Equal(new[] { 1 }, parts[1].Objects);
    }

    [Fact]
    public void LowerDetailObjectsJoinThePartOfTheirNodeButAreNotCounted() // 005c41ac, 005c44c0, 005c48dc
    {
        SplitNode root = Node(0), lod = Node(7);
        root.Lod = lod;
        List<SplitPart> parts = PartSplitter.Split(root, 0.0, new R250(1));
        Assert.Single(parts);
        Assert.Equal(new[] { 0 }, parts[0].Objects);
        Assert.Equal(new[] { 7 }, parts[0].LodObjects);
        Assert.Equal(1, parts[0].Count);   // +0x154 counts only the objects of the first detail
    }

    [Fact]
    public void AnObjectGoesToOnePartOnly() // 005c48dc clears the flag of the object it takes
    {
        SplitNode root = Node(0, 1);
        List<SplitPart> first = PartSplitter.Split(root, 0.0, new R250(1));
        Assert.Equal(2, first[0].Count);
        List<SplitPart> again = PartSplitter.Split(root, 0.0, new R250(1));
        Assert.Empty(again);   // the objects are already taken
    }
}
