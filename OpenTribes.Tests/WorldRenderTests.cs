using OpenTribes.Client.World;
using OpenTribes.Core;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Gl;
using OpenTribes.World;
using Xunit.Abstractions;

namespace OpenTribes.Tests;

// Unit tests of the deterministic pieces of the world render (M9 C4). Each test names the section of
// docs/world-render.md (WR) or docs/renderer.md (R) it follows (R9).
public class WorldRenderTests
{
    private readonly ITestOutputHelper mOut;

    public WorldRenderTests(ITestOutputHelper output) => mOut = output;

    private static Camera3D Cam(double x = 0, double y = 0, double z = 0, double yaw = 0, double pitch = 0, double fov = 90,
                                int w = 640, int h = 480, double near = 0.5, double far = 1000) =>
        new(x, y, z, yaw, pitch, fov, w, h, near, far);

    // ---- WR 1.3 camera and projection ----

    [Fact]
    public void ProjectionPutsTheViewAxisAtTheScreenCentre() // WR 1.3: +Y forward, +X right, +Z up
    {
        Camera3D c = Cam();
        Assert.True(c.Project(0, 100, 0, out double sx, out double sy, out _));
        Assert.Equal(320, sx, 6);
        Assert.Equal(240, sy, 6);
        // 90 degree horizontal fov at 640 pixels: a point at (d, d) lands on the right edge, (-d, d) on the left edge
        Assert.True(c.Project(50, 50, 0, out sx, out _, out _));
        Assert.Equal(640, sx, 6);
        Assert.True(c.Project(-50, 50, 0, out sx, out _, out _));
        Assert.Equal(0, sx, 6);
        Assert.True(c.Project(0, 50, 50, out _, out sy, out _)); // up is toward row 0
        Assert.Equal(240 - 320, sy, 6);
        Assert.False(c.Project(0, -10, 0, out _, out _, out _)); // behind
        Assert.False(c.Project(0, 0.25, 0, out _, out _, out _)); // in front of near
    }

    [Fact]
    public void YawTurnsLeftAndPitchLooksUp() // interiors.md 5.2: local +Y maps to (-sin yaw, cos yaw, 0)
    {
        Camera3D c = Cam(yaw: 90);
        var f = c.Forward;
        Assert.Equal(-1, f.x, 9);
        Assert.Equal(0, f.y, 9);
        Assert.True(c.Project(-100, 0, 0, out double sx, out _, out _));
        Assert.Equal(320, sx, 6);
        Camera3D p = Cam(pitch: 30);
        Assert.Equal(Math.Sin(Math.PI / 6), p.Forward.z, 9);
        Assert.True(p.Project(0, 100 * Math.Cos(Math.PI / 6), 100 * Math.Sin(Math.PI / 6), out _, out double sy, out _));
        Assert.Equal(240, sy, 6);
    }

    [Fact]
    public void ProjectionDistanceAndRadiusAreInverses() // WR 2.4 projectionDistance / projectRadius
    {
        Camera3D c = Cam();
        Assert.Equal(320, c.FocalLength, 9);
        double d = c.ProjectionDistance(50, 8);
        Assert.Equal(8 * 320 / 50.0, d, 9);
        Assert.Equal(50, c.ProjectRadius(d, 8), 9);
    }

    [Fact]
    public void ViewProjectionMatrixAgreesWithProject() // R 5.2: ndc depth is 2 near / d - 1 (reversed, as the device stores near / d)
    {
        Camera3D c = Cam(10, -20, 30, 33, -12, 75);
        var m = new float[16];
        c.ViewProjection(m);
        foreach (var (x, y, z) in new[] { (15.0, 40.0, 25.0), (-30.0, 100.0, 60.0), (80.0, 200.0, 0.0) })
        {
            double cx = m[0] * x + m[4] * y + m[8] * z + m[12], cy = m[1] * x + m[5] * y + m[9] * z + m[13],
                   cz = m[2] * x + m[6] * y + m[10] * z + m[14], cw = m[3] * x + m[7] * y + m[11] * z + m[15];
            Assert.True(c.Project(x, y, z, out double sx, out double sy, out double depth));
            Assert.Equal(sx, (cx / cw * 0.5 + 0.5) * 640, 2);
            Assert.Equal(sy, (0.5 - cy / cw * 0.5) * 480, 2);
            Assert.Equal(depth, cz / cw * 0.5 + 0.5, 5);
        }
    }

    // ---- WR 2.2 and 2.6, 2.7 haze ----

    [Fact]
    public void MissionFieldsGiveTheRenderDistances() // WR 2.2: Broadside (600, 400) gives 266.7
    {
        TerrainParams p = TerrainParams.FromMission(600, 400, 50);
        Assert.Equal(600, p.Visible);
        Assert.Equal(266.6667, p.HazeStart, 3);
        // at most 0.75 * visible
        Assert.Equal(0.75 * 300, TerrainParams.FromMission(300, 2000, 50).HazeStart, 9);
        // the pref caps the visible distance
        Assert.Equal(10000, TerrainParams.FromMission(50000, 1000, 50).Visible);
    }

    [Fact]
    public void TerrainHazeCurveRisesToOneWithZeroSlopeAtTheEnd() // WR 2.6
    {
        Assert.Equal(0, TerrainLod.TerrainHaze(99, 100, 500));
        Assert.Equal(0, TerrainLod.TerrainHaze(100, 100, 500), 12);
        Assert.Equal(1, TerrainLod.TerrainHaze(500, 100, 500), 12);
        // halfway: 1 - (0.5 - 1)^2 = 0.75
        Assert.Equal(0.75, TerrainLod.TerrainHaze(300, 100, 500), 12);
        // zero slope at the far end
        Assert.True(Math.Abs(TerrainLod.TerrainHaze(499.9, 100, 500) - 1) < 1e-6);
        // no 0.01 floor on the terrain curve
        Assert.Equal(0, TerrainLod.TerrainHaze(50, 100, 500));
    }

    [Fact]
    public void ObjectHazeCurveHasTheFloorAndTheFactor() // WR 2.7
    {
        Assert.Equal(0.01, TerrainLod.ObjectHaze(10, 100, 500));
        // scale = 1 / (1.02 * 400); at dist 500 the term is (400 / 408 - 1)^2
        double t = 400.0 / (1.02 * 400) - 1;
        Assert.Equal(1 - t * t, TerrainLod.ObjectHaze(500, 100, 500), 12);
        Assert.Equal(TerrainLod.ObjectHaze(500, 100, 500), TerrainLod.ObjectHaze(900, 100, 500), 12); // distance clamped to visible
        Assert.True(TerrainLod.ObjectHaze(500, 100, 500) < TerrainLod.TerrainHaze(500, 100, 500));
    }

    // ---- WR 2.9 light maps ----

    [Fact]
    public void LightNibblesExpandAsNibbleTimes16Plus15() // WR 2.9 and 7 item 6
    {
        Assert.Equal(((byte)0x0f, (byte)0x0f, (byte)0x0f), TerrainData.ExpandLight(0x0000));
        Assert.Equal(((byte)0xff, (byte)0xff, (byte)0xff), TerrainData.ExpandLight(0xffff));
        Assert.Equal(((byte)0x4f, (byte)0x4f, (byte)0x4f), TerrainData.ExpandLight(0x4444)); // not 4 * 17 = 0x44
        // M R G B: the mono nibble is ignored, R is bits 8-11, G 4-7, B 0-3
        Assert.Equal(((byte)0x1f, (byte)0x2f, (byte)0x3f), TerrainData.ExpandLight(0xf123));
    }

    // ---- WR 2.3 tiling ----

    private static GridBlock Block(int n, Func<int, int, float> h, Func<int, int, (byte flags, byte mat)> sq)
    {
        var b = new GridBlock { Size = n, Heights = new float[(n + 1) * (n + 1)], Squares = new byte[n * n * 2] };
        for (int y = 0; y <= n; ++y)
            for (int x = 0; x <= n; ++x)
                b.Heights[y * (n + 1) + x] = h(x, y);
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
            {
                var (f, m) = sq(x, y);
                b.Squares[(y * n + x) * 2] = f;
                b.Squares[(y * n + x) * 2 + 1] = m;
            }
        return b;
    }

    [Fact]
    public void SmallBlocksTileUpTo256AndTheRenderRepeatsPastTheMapEdge() // WR 2.3, D33
    {
        GridBlock b = Block(4, (x, y) => 10 * x + y, (x, y) => (0, (byte)(x + 4 * y)));
        var t = new TerrainData(b, 16, Array.Empty<ushort>());
        Assert.Equal(4, t.SourceSize);
        // heights: source at (x mod N, y mod N), quantised to 11.5 fixed point
        Assert.Equal(b.H(1, 2), t.LocalHeight(1, 2));
        Assert.Equal(b.H(1, 2), t.LocalHeight(1 + 4, 2 + 8));
        Assert.Equal(b.H(0, 0), t.LocalHeight(256, 256)); // the last corner wraps too (N is not 256)
        // materials tile the same way
        Assert.Equal(1 + 4 * 2, t.Material(1, 2));
        Assert.Equal(1 + 4 * 2, t.Material(1 + 4 * 17, 2 + 4 * 3));
        // the 3 x 3 file: the domain repeats the render block, 768 cells in all
        Assert.Equal(t.Height(5, 6), t.Height(5 + 256, 6 + 512));
        Assert.Equal(768, TerrainData.Domain);
    }

    [Fact]
    public void HeightsAreClampedAndQuantisedToElevenPointFive() // WR 2.3 heights
    {
        GridBlock b = Block(4, (x, y) => x == 0 ? -5f : x == 1 ? 3000f : 1.04f, (x, y) => (0, (byte)0));
        var t = new TerrainData(b, 1, Array.Empty<ushort>());
        Assert.Equal(0, t.LocalHeight(0, 0));
        Assert.Equal(2047, t.LocalHeight(1, 0));
        Assert.Equal((int)(1.04f * 32) * 0.03125f, t.LocalHeight(2, 0));
    }

    [Fact]
    public void MaterialIndexAtOrAboveTheCountBecomesZero() // WR 2.3 materials
    {
        GridBlock b = Block(4, (x, y) => 0, (x, y) => (0, (byte)(x == 0 ? 7 : 2)));
        var t = new TerrainData(b, 5, Array.Empty<ushort>());
        Assert.Equal(0, t.Material(0, 0));
        Assert.Equal(2, t.Material(1, 0));
    }

    [Fact]
    public void LightMapTilesWithTheSameWrapAndOnlyTheFirst256Rows() // WR 2.3 light map, 2.9
    {
        GridBlock b = Block(4, (x, y) => 0, (x, y) => (0, (byte)0));
        var light = new ushort[25];
        for (int i = 0; i < 25; ++i)
            light[i] = (ushort)i;
        var t = new TerrainData(b, 1, light);
        Assert.Equal(7, t.LightWord(2, 1));        // entry y * (N + 1) + x
        Assert.Equal(7, t.LightWord(2 + 8, 1 + 4)); // wraps at N
        byte[] rgba = t.LightRgba();
        Assert.Equal(256 * 256 * 4, rgba.Length);
        Assert.Equal(0x00, rgba[0]);
        Assert.Equal(((byte)0xff, (byte)0xff, (byte)0xff), TerrainData.ExpandLightPacked(0xffff));
        Assert.Equal(((byte)0x44, (byte)0x44, (byte)0x44), TerrainData.ExpandLightPacked(0x4444)); // n * 17
        Assert.Equal(((byte)0x11, (byte)0x22, (byte)0x33), TerrainData.ExpandLightPacked(0xf123));
        Assert.Equal(((byte)0xff, (byte)0xff, (byte)0xff), TerrainData.ExpandLight5551(0xffff));
        Assert.Equal(((byte)0x08, (byte)0x8c, (byte)0x7b), TerrainData.ExpandLight5551(0x0087)); // nibbles 0, 8, 7 are 5 bit 1, 17, 15
        Assert.Equal((byte)198, TerrainData.Quantize5(194));
        Assert.Equal((byte)0, TerrainData.Quantize5(7));
        Assert.Equal((byte)255, TerrainData.Quantize5(255));
    }

    // The packed upload table by palette kind (005d4880): 5551 for kinds 0, 1, 6, 8, 4444 for kind 2.
    [Fact]
    public void PackedTextureFormatFollowsThePaletteKind()
    {
        byte[] Px() => new byte[] { 194, 7, 255, 255, 100, 40, 17, 130 };
        byte[] k1 = OpenTribes.Platform.Gfx.PaletteTables.Pack(Px(), new OpenTribes.Platform.Gfx.SubPalette { Kind = 1 });
        Assert.Equal(new byte[] { 198, 0, 255, 255, 99, 41, 16, 130 }, k1); // 5 bit colour, alpha untouched
        byte[] k2 = OpenTribes.Platform.Gfx.PaletteTables.Pack(Px(), new OpenTribes.Platform.Gfx.SubPalette { Kind = 2 });
        Assert.Equal(new byte[] { 204, 0, 255, 255, 102, 34, 17, 136 }, k2); // n * 17 on all four channels
        byte[] k3 = OpenTribes.Platform.Gfx.PaletteTables.Pack(Px(), new OpenTribes.Platform.Gfx.SubPalette { Kind = 3 });
        Assert.Equal(Px(), k3);
    }

    // ---- WR 2.4, 2.5 square selection ----

    private static TerrainData Flat(int n = 256, Func<int, int, (byte, byte)>? sq = null) =>
        new(Block(n, (x, y) => 100f, sq ?? ((x, y) => (0, (byte)0))), 1, Array.Empty<ushort>());

    [Fact]
    public void DetailDistanceFollowsTheScreenSizeRule() // WR 2.5: distanceScale * size - size
    {
        Camera3D c = Cam();
        // screenSize 50: distanceScale = projectionDistance(50, 1) = 320 / 50 = 6.4
        Assert.Equal(6.4 * 16 - 16, TerrainLod.DetailDistance(c, 50, 1), 9);
        Assert.Equal(6.4 * 64 - 64, TerrainLod.DetailDistance(c, 50, 3), 9);
        Assert.Equal(0, TerrainLod.Grow(0.75 * 100 - 1, 100));
        Assert.Equal(1, TerrainLod.Grow(100, 100), 12);
        Assert.Equal(0.5, TerrainLod.Grow(87.5, 100), 12);
    }

    [Fact]
    public void ScreenErrorWidensTheFrustumTest() // WR 2.4 step 3: pixelError = 1 / projectRadius(screenError, 1), $screenError is script-settable in 1.11
    {
        TerrainData t = Flat();
        Camera3D c = Cam(3072, 3072, 110, 0, -20, 90, 640, 480, 0.5, 600);
        TerrainParams p = TerrainParams.FromMission(600, 400, 50);
        Assert.Equal(9.0, p.ScreenError);
        int tight = TerrainLod.Select(t, c, 0, 0, 0, p with { ScreenError = 0 }).Count;
        int stock = TerrainLod.Select(t, c, 0, 0, 0, p).Count;
        int loose = TerrainLod.Select(t, c, 0, 0, 0, p with { ScreenError = 5000 }).Count;
        Assert.True(tight <= stock && stock <= loose);
    }

    [Fact]
    public void NearSquaresAreFineAndFarSquaresAreCoarse() // WR 2.5
    {
        TerrainData t = Flat();
        // camera 10 m above the flat terrain at the centre of the 3 x 3 domain
        Camera3D c = Cam(3072, 3072, 110, 0, -20, 90, 640, 480, 0.5, 600);
        List<TerrainSquare> sq = TerrainLod.Select(t, c, 0, 0, 0, TerrainParams.FromMission(600, 400, 50));
        Assert.NotEmpty(sq);
        Assert.Contains(sq, s => s.Level == 0);
        double MinDist(TerrainSquare s)
        {
            double x0 = s.X * 8.0, y0 = s.Y * 8.0, x1 = x0 + 8.0 * (1 << s.Level), y1 = y0 + 8.0 * (1 << s.Level);
            double dx = Math.Max(Math.Max(x0 - 3072, 0), 3072 - x1), dy = Math.Max(Math.Max(y0 - 3072, 0), 3072 - y1);
            return Math.Sqrt(dx * dx + dy * dy + 100.0 * 100.0 * 0 + 10.0 * 10.0);
        }
        // every square is closer than the visible distance, none is beyond it
        Assert.All(sq, s => Assert.True(MinDist(s) < 600 + 1e-6));
        // a level 0 square is never farther than the detail distance of level 1 plus its own size
        Assert.All(sq.Where(s => s.Level == 0), s => Assert.True(MinDist(s) < TerrainLod.DetailDistance(c, 50, 1) + 24));
        // the coarsest level present is farther than the finest
        double nearMax = sq.Where(s => s.Level == 0).Max(MinDist), farMin = sq.Where(s => s.Level >= 3).Min(MinDist);
        Assert.True(farMin > 0.5 * nearMax);
        // level k squares exist only where d >= detailDistance(k) or k is the coarsest allowed
        Assert.All(sq.Where(s => s.Level > 0), s => Assert.True(MinDist(s) >= TerrainLod.DetailDistance(c, 50, s.Level) - 1e-6 || MinDist(s) < 1));
    }

    [Fact]
    public void EmptySquaresAndSquaresOutsideTheViewAreDropped() // WR 2.5 reject, frustum
    {
        TerrainData all = Flat(sq: (x, y) => ((byte)(1 << 3), (byte)0));
        Camera3D c = Cam(3072, 3072, 110, 0, -20, 90, 640, 480, 0.5, 600);
        Assert.Empty(TerrainLod.Select(all, c, 0, 0, 0, TerrainParams.FromMission(600, 400, 50)));
        // looking away from a corner: nothing of the far side is selected
        TerrainData t = Flat();
        Camera3D away = Cam(0, 0, 110, 135, -20, 60, 640, 480, 0.5, 600); // toward -x, -y: outside the domain
        Assert.Empty(TerrainLod.Select(t, away, 0, 0, 0, TerrainParams.FromMission(600, 400, 50)));
    }

    [Fact]
    public void SelectedSquaresShareVerticesWithoutCracks() // WR 2.5: the mid points of neighbours are shared (T-junction free)
    {
        TerrainData t = Flat();
        Camera3D c = Cam(3072, 3072, 110, 20, -20, 90, 640, 480, 0.5, 600);
        TerrainParams p = TerrainParams.FromMission(600, 400, 50);
        List<TerrainSquare> sq = TerrainLod.Select(t, c, 0, 0, 0, p);
        TerrainMesh mesh = TerrainMesher.Build(t, sq, c, 0, 0, 0, p);
        Assert.True(mesh.TriangleCount > sq.Count);
        // the flat terrain keeps one height whatever the morph does
        Assert.All(mesh.Atlas.Concat(mesh.ByMaterial.Values.SelectMany(v => v)), v => Assert.Equal(100f, v.Z, 3));
    }

    [Fact]
    public void ReusedMeshEqualsAFreshMesh() // the renderer refills one mesh each frame: no vertex of the last frame may remain
    {
        TerrainData t = Flat();
        TerrainParams p = TerrainParams.FromMission(600, 400, 50);
        var reuse = new TerrainMesh();
        foreach (var (x, yaw) in new[] { (3072.0, 20.0), (3300.0, 200.0), (3072.0, 20.0) })
        {
            Camera3D c = Cam(x, 3072, 110, yaw, -20, 90, 640, 480, 0.5, 600);
            List<TerrainSquare> sq = TerrainLod.Select(t, c, 0, 0, 0, p);
            TerrainMesh fresh = TerrainMesher.Build(t, sq, c, 0, 0, 0, p);
            TerrainMesh again = TerrainMesher.Build(t, sq, c, 0, 0, 0, p, reuse);
            Assert.Same(reuse, again);
            Assert.Equal(fresh.Atlas, again.Atlas);
            foreach (var (mat, list) in fresh.ByMaterial)
                Assert.Equal(list, again.ByMaterial[mat]);
            Assert.All(again.ByMaterial.Where(kv => !fresh.ByMaterial.ContainsKey(kv.Key)), kv => Assert.Empty(kv.Value));
        }
    }

    [Fact]
    public void MorphMovesAVertexToItsParentAverage() // WR 2.5 vertex morph
    {
        // a ramp in x only has parent averages equal to the heights, so use a spike: h = 0 except one corner
        GridBlock b = Block(256, (x, y) => x == 1 && y == 1 ? 40f : 0f, (x, y) => (0, (byte)0));
        var t = new TerrainData(b, 1, Array.Empty<ushort>());
        TerrainParams p = TerrainParams.FromMission(600, 400, 50);
        // (1, 1) is the centre of the level 1 square at (0, 0): created at level 0, morph toward the diagonal average 0
        Camera3D near = Cam(8, 8, 100, 0, -90, 90, 640, 480, 0.5, 600);
        Camera3D far = Cam(8 + 500, 8, 100, 0, -90, 90, 640, 480, 0.5, 600);
        Assert.Equal(40f, TerrainLod.MorphedHeight(t, 1, 1, near, 0, 0, 0, p), 3);
        Assert.Equal(0f, TerrainLod.MorphedHeight(t, 1, 1, far, 0, 0, 0, p), 3);
    }

    [Fact]
    public void SplitDiagonalFollowsTheCheckerboardAtLevelZeroAndTiesChooseTheOtherDiagonal() // WR 2.3
    {
        TerrainData t = Flat();
        Assert.True(t.SplitMain(0, 0, 0));
        Assert.False(t.SplitMain(0, 1, 0));
        Assert.True(t.SplitMain(0, 1, 1));
        // flat ground: both diagonals deviate equally from level 4 up, the strict comparison picks the other diagonal
        Assert.False(t.SplitMain(4, 0, 0));
        Assert.False(t.SplitMain(8, 0, 0));
    }

    [Fact]
    public void TrianglesWhollyBeyondTheVisibleDistanceAreDropped() // WR 2.6
    {
        TerrainData t = Flat();
        TerrainParams p = TerrainParams.FromMission(600, 400, 50);
        Camera3D c = Cam(3072, 3072, 110, 0, -20, 90, 640, 480, 0.5, 600);
        List<TerrainSquare> sq = TerrainLod.Select(t, c, 0, 0, 0, p);
        TerrainMesh mesh = TerrainMesher.Build(t, sq, c, 0, 0, 0, p);
        var all = new List<Vertex3D>(mesh.Atlas);
        foreach (List<Vertex3D> l in mesh.ByMaterial.Values)
            all.AddRange(l);
        for (int i = 0; i + 2 < all.Count; i += 3)
            Assert.Contains(Enumerable.Range(i, 3), k =>
            {
                double dx = all[k].X - c.X, dy = all[k].Y - c.Y, dz = all[k].Z - c.Z;
                return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= 600 + 1e-3;
            });
    }

    [Fact]
    public void FlagValuesMapToTheExeOrientations() // WR 2.8; the table equals the exe's UV tables [code 00576784 to 005768d2]
    {
        // flag 4 is the identity; flag 6 mirrors u; flag 0 mirrors v; flag 7 swaps u and v
        Assert.Equal((0f, 1f), TerrainMesher.CornerUv(0, 0, 0));
        Assert.Equal((1f, 0f), TerrainMesher.CornerUv(0, 0, 6));
        Assert.Equal((0f, 0f), TerrainMesher.CornerUv(0, 0, 4));
        Assert.Equal((1f, 0f), TerrainMesher.CornerUv(0, 1, 7));
        Assert.Equal((1f, 1f), TerrainMesher.CornerUv(0, 0, 2)); // rotated 180 degrees
        var seen = new HashSet<(float, float, float, float)>();
        for (int f = 0; f < 8; ++f)
        {
            var (u, v) = TerrainMesher.CornerUv(0, 0, f);
            var (u2, v2) = TerrainMesher.CornerUv(1, 0, f);
            seen.Add((u, v, u2, v2));
        }
        Assert.Equal(8, seen.Count); // eight distinct orientations
        Assert.Equal(8, TerrainMesher.FlagOrientation.Distinct().Count());
    }

    [Fact]
    public void FlagOrientationIsTheExeUvTableConstruction() // [code 00576784]: tables of flag 1..7 built from the table of flag 0
    {
        // Corner positions of the exe (x, y) by corner index and the base UV table of flag 0.
        (int, int)[] pos = { (0, 1), (1, 1), (1, 0), (0, 0) };
        for (int f = 0; f < 8; ++f)
        {
            var e = new (float, float)[] { (0, 0), (1, 0), (1, 1), (0, 1) };
            if ((f & 2) != 0) { (e[0], e[1]) = (e[1], e[0]); (e[2], e[3]) = (e[3], e[2]); }
            if ((f & 4) != 0) { (e[0], e[3]) = (e[3], e[0]); (e[1], e[2]) = (e[2], e[1]); }
            if ((f & 1) != 0) e = new[] { e[3], e[0], e[1], e[2] };
            for (int c = 0; c < 4; ++c)
                Assert.Equal(e[c], TerrainMesher.CornerUv(pos[c].Item1, pos[c].Item2, f));
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(33, 0)]
    [InlineData(-120, 40)]
    [InlineData(135, -60)]
    [InlineData(290, 89)]
    public void WorldUpStaysUpOnScreenForAnyYawAndPitchBelowNinety(double yaw, double pitch) // WR 1.3
    {
        foreach (double p in new[] { pitch, -pitch, pitch / 2 })
        {
            Camera3D c = Cam(10, 20, 30, yaw, p * 0.99);
            Assert.True(c.Up.z > 0, $"yaw {yaw} pitch {p}");
            Assert.True(c.Project(10, 20, 30, out _, out _, out _) == false); // the eye itself is not in front
            // a point straight above a point on the view axis lands higher on screen (smaller row)
            var f = c.Forward;
            double dx = 10 + f.x * 50, dy = 20 + f.y * 50, dz = 30 + f.z * 50;
            Assert.True(c.Project(dx, dy, dz, out _, out double rowAxis, out _));
            Assert.True(c.Project(dx, dy, dz + 5, out _, out double rowUp, out _));
            Assert.True(rowUp < rowAxis, $"yaw {yaw} pitch {p}: row {rowUp} not above {rowAxis}");
        }
    }

    // ---- WR 4.2 stars, 4.1 sky, 4.3 planet ----

    [Fact]
    public void StarGeneratorFirstValuesFollowTheExeRandLcg() // WR 4.2, D35: x = x * 0x15a4e35 + 1, bits 16-30
    {
        var rng = new StarField.Lcg(0x8377);
        Assert.Equal(new[] { 22271, 16796, 20564, 26778, 27698, 14405, 25654, 21932 }, Enumerable.Range(0, 8).Select(_ => rng.Next()).ToArray());
        List<StarField.Star> stars = StarField.Generate(0x8377, 1000);
        Assert.Equal(StarField.Count, stars.Count);
        // first star: components (-729, -204, -436) of length 873.6, brightness draw 26778 % 30 = 18 -> class 3
        Assert.Equal(3, stars[0].Class);
        double len = Math.Sqrt(729.0 * 729 + 204.0 * 204 + 436.0 * 436);
        Assert.Equal(-729 / len * 1000, stars[0].X, 6);
        Assert.Equal(-204 / len * 1000, stars[0].Y, 6);
        Assert.Equal(-436 / len * 1000, stars[0].Z, 6);
        Assert.Equal(199 / Math.Sqrt(199.0 * 199 + 347.0 * 347 + 889.0 * 889) * 1000, stars[1].X, 6); // second accepted star
        Assert.All(stars, s => Assert.Equal(1000, Math.Sqrt(s.X * s.X + s.Y * s.Y + s.Z * s.Z), 6));
        Assert.DoesNotContain(stars, s => s.Class == 1); // class 1 is never produced
        Assert.Equal(stars.Select(s => s.Class), StarField.Generate(0x8377, 50).Select(s => s.Class)); // radius does not change the draw
    }

    [Fact]
    public void SkyPanelsRunClockwiseFromTheFeatureAngle() // WR 4.1 Init
    {
        Assert.Equal(90 - 22.5, SkyRenderer.Azimuth(0, 0));
        Assert.Equal(90 + 45 - 22.5 - 22.5 * 3, SkyRenderer.Azimuth(3, 45));
        Assert.Equal(SkyRenderer.Azimuth(0, 0) - 360, SkyRenderer.Azimuth(16, 0));
    }

    [Fact]
    public void SkyGhostCarriesSizeFeaturePositionAndHazeColourToTheRenderer() // WR 4.1: Sky_unpackUpdate 00477824, fields 004776fc
    {
        var state = new Dictionary<string, object?>
        {
            ["dml"] = "marsdusk.dml",
            ["vecB8"] = new[] { 0.25, 0.5, 1.0 },
            ["vecC4"] = new[] { 0.75, 0.125, 0.0 },
            ["f11c"] = 45.0,
            ["f124"] = 1400.0,
            ["textures"] = new List<object?> { 0L, 1L, 15L },
        };
        var o = new MissionObject { Class = "Sky" };
        OpenTribes.Client.Scene.DemoRender.SkyFields(o, state);
        Assert.Equal("marsdusk.dml", o.Get("dmlName"));
        Assert.Equal(1400, o.GetDouble("size", 600));
        Assert.Equal(45, o.GetDouble("featurePosition", 0));
        Assert.Equal(new[] { 0.25, 0.5, 1.0 }, o.GetVector("skyColor"));
        Assert.Equal(new[] { 0.75, 0.125, 0.0 }, o.GetVector("hazeColor"));
        Assert.Equal(15, o.GetDouble("textures[2]", -1));
    }

    [Fact]
    public void PlanetDirectionFollowsAzimuthAndIncidence() // WR 4.3 Planet_setup
    {
        var (x, y, z) = PlanetRenderer.Direction(0, 0, 1000);
        Assert.Equal(0, x, 6);
        Assert.Equal(1000, y, 6); // azimuth 0 = +Y
        Assert.Equal(0, z, 6);
        (x, y, z) = PlanetRenderer.Direction(90, 60, 100);
        Assert.Equal(-50, x, 6);
        Assert.Equal(0, y, 6);
        Assert.Equal(100 * Math.Sin(Math.PI / 3), z, 6);
    }

    // ---- WR 3 interiors ----

    [Fact]
    public void DetailIsForcedFullInsideOrWhenLinkFlagged() // 00480be4, ITRInstance_changeDetail(0)
    {
        Assert.Equal(0, InteriorRenderer.ChooseDetail(true, false, true, 2, 1));
        Assert.Equal(0, InteriorRenderer.ChooseDetail(false, true, true, 2, 1));
        Assert.Equal(1, InteriorRenderer.ChooseDetail(false, false, true, 2, 1));
    }

    [Fact]
    public void DetailKeepsThePreviousLevelWhenTheDataIsMissing() // ITRInstance_updateDetail 00556fa4
    {
        Assert.Equal(2, InteriorRenderer.ChooseDetail(false, false, false, 2, 1));
        Assert.Equal(0, InteriorRenderer.ChooseDetail(false, true, false, 2, 1)); // the link flag wins, no updateDetail
    }

    [Fact]
    public void InteriorCullDropsCounterClockwiseTrianglesOnly() // renderer.md 5.6 point 4, beginScene3d 005daa30
    {
        // Stock surfaces run clockwise seen from their front side (+Y forward, +X right, +Z up, camera at the origin looking along +Y).
        var a = new Vertex3D(-1, 10, -1, 0, 0, 0, 0, 0);
        var b = new Vertex3D(-1, 10, 1, 0, 0, 0, 0, 0);
        var c = new Vertex3D(1, 10, 1, 0, 0, 0, 0, 0);
        Assert.False(InteriorRenderer.BackFacing(a, b, c, 0, 0, 0)); // a -> b -> c is clockwise seen from the camera
        Assert.True(InteriorRenderer.BackFacing(c, b, a, 0, 0, 0));  // reversed: seen from behind
        Assert.True(InteriorRenderer.BackFacing(a, b, c, 0, 20, 0)); // the same triangle from the far side
    }

    [Fact]
    public void LodIsTheFirstWhoseMinPixelsIsBelowTheProjectedSize() // WR 3.2
    {
        var mins = new[] { 500, 100, 0 };
        Assert.Equal(0, InteriorRenderer.FindLod(mins, 501));
        Assert.Equal(1, InteriorRenderer.FindLod(mins, 500));
        Assert.Equal(1, InteriorRenderer.FindLod(mins, 101));
        Assert.Equal(2, InteriorRenderer.FindLod(mins, 100));
        Assert.Equal(2, InteriorRenderer.FindLod(mins, 0)); // else the last
    }

    [Fact]
    public void DetailSphereIsTheLongestExtentAroundTheBoxCentre() // 00480be4, ITRInstance_setState 005570c0
    {
        // acommand.*.dis: every level has the box (-30,-30,-41.75) to (30,46,49)
        var (cx, cy, cz, r) = InteriorRenderer.DetailSphere(new float[] { -30, -30, -41.75f, 30, 46, 49 });
        Assert.Equal(0, cx, 6);
        Assert.Equal(8, cy, 6);
        Assert.Equal(3.625, cz, 6);
        Assert.Equal(90.75, r, 6); // not half the diagonal (66.4)
    }

    [Fact]
    public void DetailPixelsDivideByViewDepthAndDouble() // 005aa704, 00481094 (2.0), 00556fa4 (truncation)
    {
        var cam = new OpenTribes.Platform.Gfx.Camera3D(0, 0, 0, 0, 0, 90, 640, 480, 0.01, 1000);
        Assert.Equal(320.0, cam.FocalLength, 9);
        Assert.Equal((int)(2.0f * (float)(90.75 * 320.0) / 100.0f), InteriorRenderer.DetailPixels(100, 90.75, 320));
        Assert.Equal(580, InteriorRenderer.DetailPixels(100, 90.75, 320)); // 580.8 truncated
        // behind the camera the depth is clamped to 0.001: the largest size, so the first detail level
        Assert.Equal(58079996, InteriorRenderer.DetailPixels(-5, 90.75, 320));
        Assert.Equal(0, InteriorRenderer.FindLod(new[] { 1000, 500, 150, 80 }, InteriorRenderer.DetailPixels(-5, 90.75, 320)));
    }

    [Fact]
    public void DetailThresholdsOfTheBaseFollowTheLongestExtent() // acommand: minPixels 1000, 500, 150, 80
    {
        var mins = new[] { 1000, 500, 150, 80 };
        // 640 wide, 90 degrees: the first level out to 58 m of depth, the second to 116 m, the third to 387 m
        Assert.Equal(0, InteriorRenderer.FindLod(mins, InteriorRenderer.DetailPixels(57, 90.75, 320)));
        Assert.Equal(1, InteriorRenderer.FindLod(mins, InteriorRenderer.DetailPixels(59, 90.75, 320)));
        Assert.Equal(1, InteriorRenderer.FindLod(mins, InteriorRenderer.DetailPixels(115, 90.75, 320)));
        Assert.Equal(2, InteriorRenderer.FindLod(mins, InteriorRenderer.DetailPixels(117, 90.75, 320)));
        Assert.Equal(3, InteriorRenderer.FindLod(mins, InteriorRenderer.DetailPixels(500, 90.75, 320)));
    }

    private static InteriorLighting.State St(float end, ushort c = 0, short dc = 0, short di = 0) =>
        new() { R = c, G = c, B = c, Mono = c, EndTime = end, DataCount = dc, DataIndex = di };

    [Fact]
    public void LightStateCountsFollowingRecordsWhoseEndTimeIsPast() // WR 3.7 Timing, interiors.md 5.4
    {
        InteriorLighting.State[] s = { St(0.5f), St(1.0f), St(2.0f), St(3.0f) };
        Assert.Equal(0, InteriorLighting.SelectState(s, 4, 0f));      // the first record never counts
        Assert.Equal(0, InteriorLighting.SelectState(s, 4, 1.0f));    // end time must be below the time
        Assert.Equal(1, InteriorLighting.SelectState(s, 4, 1.01f));
        Assert.Equal(2, InteriorLighting.SelectState(s, 4, 2.5f));
        Assert.Equal(3, InteriorLighting.SelectState(s, 4, 100f));    // limited to state count - 1
        Assert.Equal(1, InteriorLighting.SelectState(s, 2, 100f));
    }

    [Fact]
    public void LightColourPacksAndAddsToTheNibbles() // WR 3.7 steps 3
    {
        var c = InteriorLighting.PackedColor(St(0, 0xb27f)); // (0xb27f + 0x80) >> 8 = 0xb2
        Assert.Equal((0xb2, 0xb2, 0xb2, 0xb2), c);
        // pixel nibbles start at 0x4444; intensity 255: (0xb2 * 255) >> 12 = 11 added, clamped to 15
        Assert.Equal((ushort)0xffff, InteriorLighting.AddLight(0x4444, c, 255));
        Assert.Equal((ushort)0x4444, InteriorLighting.AddLight(0x4444, c, 15)); // intensity below 16 adds nothing
        Assert.Equal((ushort)0xbbbb, InteriorLighting.AddLight(0x4444, (0x80, 0x80, 0x80, 0x80), 255)); // (0x80 * 255) >> 12 = 7
    }

    [Fact]
    public void ConstantAndRawSurfaceMapsDecode() // WR 3.6 surface word
    {
        var lt = new InteriorLighting
        {
            Surfaces = new[]
            {
                new InteriorLighting.Surface { Word = 0x8000abcd, SizeX = 2, SizeY = 2 },
                new InteriorLighting.Surface { Word = 0x00000002, SizeX = 2, SizeY = 1 },
                new InteriorLighting.Surface { Word = 0x00000000, SizeX = 0, SizeY = 0 },
            },
            MapData = new byte[] { 0, 0, 0x34, 0x12, 0x78, 0x56 },
        };
        Assert.Equal(new ushort[] { 0xabcd, 0xabcd, 0xabcd, 0xabcd }, lt.SurfaceMap(0, null));
        Assert.Equal(new ushort[] { 0x1234, 0x5678 }, lt.SurfaceMap(1, null));
        Assert.Null(lt.SurfaceMap(2, null));
    }

    [Fact]
    public void HuffmanMapsDecodeLsbFirstThroughTheTree() // WR 3.6
    {
        // two leaves: node 0 -> bit 1: leaf 1 (-2), bit 0: leaf 0 (-1)
        var lt = new InteriorLighting { HasHuffman = true, Nodes = new int[1, 2] { { -2, -1 } }, Leaves = new uint[] { 0x1111, 0x2222 } };
        lt.MapData = new byte[] { 0b0000_0101 }; // bits LSB first: 1, 0, 1, 0, 0...
        Assert.Equal(new ushort[] { 0x2222, 0x1111, 0x2222, 0x1111 }, lt.DecodeHuffman(0, 4));
    }

    [Fact]
    public void MissionLightingFindsMapsBySourceIndex() // WR 3.6 ITRMissionLighting, Q10
    {
        var lt = new InteriorLighting { MissionSource = new uint[] { 10, 20, 30 }, MissionDest = new uint[] { 0, 115, 230 } };
        Assert.True(lt.TryMissionOffset(20, out uint d));
        Assert.Equal(115u, d);
        Assert.False(lt.TryMissionOffset(21, out _));
    }

    [Fact]
    public void InstanceTransformIsTheRowVectorEulerMatrix() // interiors.md 5.2
    {
        float[] m = InteriorRenderer.EulerMatrix(0, 0, Math.PI / 2);
        // world = local * M: local +Y maps to (m3, m4, m5) = (-sin z, cos z, 0)
        Assert.Equal(-1, m[3], 6);
        Assert.Equal(0, m[4], 6);
        Assert.Equal(1, m[0] * 0 + m[1], 6); // local +X maps to (cos z, sin z, 0)
        float[] g = InteriorRenderer.EulerMatrix(0.3, 0.2, 0.1);
        // orthonormal rows
        for (int i = 0; i < 3; ++i)
            Assert.Equal(1, g[i * 3] * g[i * 3] + g[i * 3 + 1] * g[i * 3 + 1] + g[i * 3 + 2] * g[i * 3 + 2], 5);
    }

    [Fact]
    public void MissionLightingNameDropsTheMissionSuffix() // interiors.md L4
    {
        Assert.Equal("tank8-000.dil", InteriorRenderer.BaseLightingName("tank8-000-0.dil"));
        Assert.Equal("acommand-010.dil", InteriorRenderer.BaseLightingName("acommand-010-0.dil"));
        Assert.Equal("floatpad-000.dil", InteriorRenderer.BaseLightingName("floatpad-000.dil"));
    }

    [Fact]
    public void LightAtlasPackingNeverOverlaps() // Internal: the atlas of the interior light maps
    {
        var sizes = new List<(int, int)> { (13, 3), (4, 10), (4, 10), (10, 4), (3, 10), (1, 1), (0, 0), (17, 9) };
        (int x, int y)[] pos = AtlasPacker.Pack(sizes, 32, out int h);
        for (int i = 0; i < sizes.Count; ++i)
        {
            Assert.True(pos[i].x + sizes[i].Item1 <= 32 && pos[i].y + sizes[i].Item2 <= h);
            for (int j = i + 1; j < sizes.Count; ++j)
            {
                bool overlap = pos[i].x < pos[j].x + sizes[j].Item1 && pos[j].x < pos[i].x + sizes[i].Item1 &&
                               pos[i].y < pos[j].y + sizes[j].Item2 && pos[j].y < pos[i].y + sizes[i].Item2;
                Assert.False(overlap && sizes[i].Item1 > 0 && sizes[j].Item1 > 0, $"{i} and {j} overlap");
            }
        }
    }

    // ---- mission and camera files ----

    [Fact]
    public void MissionFileParsesNestedObjectsAndFields()
    {
        List<MissionObject> roots = MissionFile.Parse(
            "//--- export object begin ---//\ninstant SimGroup \"MissionGroup\" {\n\tinstant SimTerrain \"Terrain\" {\n" +
            "\t\ttedFileName = \"a\\\\b.dtf\";\n\t\tvisibleDistance = \"600\";\n\t\tposition = \"-3072 -3072 0\";\n\t};\n" +
            "\tinstant Sky \"Sky\" {\n\t\ttextures[3] = \"7\";\n\t\tcastShadows = \"True\";\n\t};\n};\n");
        Assert.Single(roots);
        MissionObject[] kids = roots[0].Children.ToArray();
        Assert.Equal(new[] { "SimTerrain", "Sky" }, kids.Select(k => k.Class));
        Assert.Equal("a\\b.dtf", kids[0].Get("tedFileName"));
        Assert.Equal(600, kids[0].GetDouble("visibleDistance", 0));
        Assert.Equal(new[] { -3072.0, -3072, 0 }, kids[0].GetVector("position"));
        Assert.Equal(7, kids[1].GetDouble("textures[3]", -1));
        Assert.True(kids[1].GetBool("castShadows", false));
    }

    [Fact]
    public void CameraFilesParseAndSelectByMissionPrefix()
    {
        List<MissionRender.CameraSpec> l = MissionRender.ParseCameraFile("# c\n\nBroadside_drop -196.2,-193.8,197.9,29.7,-56.0\nX_a 1,2,3,4,5,60\n", 90, out string err);
        Assert.Equal("", err);
        Assert.Equal(2, l.Count);
        Assert.Equal(new MissionRender.CameraSpec("Broadside_drop", -196.2, -193.8, 197.9, 29.7, -56.0, 90), l[0]);
        Assert.Equal(60, l[1].Fov);
        MissionRender.ParseCameraFile("bad 1,2,3\n", 90, out err);
        Assert.Contains("line 1", err);
    }

    [Fact]
    public void MaterialListNamesKeepEntryPositions() // WR 2.8: the terrain material byte indexes the list
    {
        // PERS TS::MaterialList v4, 1 detail, 3 entries: entry 1 is not a texture (flags 0)
        var b = new List<byte>();
        Fixtures.PutStr(b, "PERS");
        Fixtures.PutU32(b, 0);
        Fixtures.PutU16(b, 16);
        Fixtures.PutStr(b, "TS::MaterialList");
        Fixtures.PutU32(b, 4);
        Fixtures.PutU32(b, 1);
        Fixtures.PutU32(b, 3);
        for (int i = 0; i < 3; ++i)
        {
            var e = new byte[0x40];
            BitConverter.GetBytes(i == 1 ? 0u : 0x103u).CopyTo(e, 0);
            System.Text.Encoding.ASCII.GetBytes($"tex{i}.bmp").CopyTo(e, 0x10);
            b.AddRange(e);
        }
        List<string?>? names = RenderMaterialList.Parse(b.ToArray());
        Assert.Equal(new string?[] { "tex0.bmp", null, "tex2.bmp" }, names);
    }

    // ---- headless GL: synthetic tiny terrain and interior ----

    private static PaletteBank Palette() => PaletteTables.Grayscale();

    [Fact]
    public void HeadlessTerrainAndInteriorRender()
    {
        GlWindow? win = null;
        foreach (string? driver in new string?[] { "offscreen", null })
        {
            win = GlWindow.TryCreate("test", 128, 96, true, driver, out string err);
            if (win != null)
            {
                mOut.WriteLine($"RAN: video driver {win.VideoDriver}, OpenGL {win.GlVersion}");
                break;
            }
            mOut.WriteLine($"SKIPPED {driver ?? "default"}: {err}");
        }
        if (win == null)
            return;
        using GlWindow w = win;
        using var dev = new GlGfxDevice(w);
        IGfxDevice d = dev;
        d.SetPalette(Palette());

        // tiny terrain: 4 x 4 squares tiled up to 256, flat at height 0, light map 0x8888 (nibble 8 -> 8 / 15 = 136)
        GridBlock b = Block(4, (x, y) => 0f, (x, y) => (0, (byte)0));
        var light = new ushort[25];
        Array.Fill(light, (ushort)0x8888);
        var data = new TerrainData(b, 1, light);
        var res = new ResourceManager(Path.GetTempPath());
        var assets = new WorldAssets(res, Palette());
        TerrainParams p = TerrainParams.FromMission(400, 200, 5); // a small screen size: this surface is 128 pixels wide
        using var terrain = new TerrainRenderer(d, assets, data, new List<string?> { null }, 0, 0, 0, p);

        // tiny interior: a 4 m cube at (40, 120, 5) whose -Y face (facing the camera) has a constant white light map
        var geo = new InteriorGeometryData { Box = new float[] { -2, -2, -2, 2, 2, 2 } };
        geo.Point3 = new float[] { -2, -2, -2, 2, -2, -2, 2, -2, 2, -2, -2, 2 };
        geo.Point2 = new float[] { 0, 1, 1, 1, 1, 0, 0, 0 };
        geo.VertexPoint3 = new ushort[] { 0, 3, 2, 1 }; // clockwise seen from the front side, as the stock surfaces
        geo.VertexPoint2 = new ushort[] { 0, 3, 2, 1 };
        geo.Planes = new float[] { 0, -1, 0, -2 }; // n . p + d > 0 for y < -2: the camera side is the front
        geo.Surfaces = new[]
        {
            new InteriorGeometryData.Surface { Flags = 0x80, Material = 255, TexSizeX = 3, TexSizeY = 3, Plane = 0, FirstVertex = 0, VertexCount = 4 },
        };
        var lt = new InteriorLighting
        {
            LightScaleShift = 4,
            Surfaces = new[] { new InteriorLighting.Surface { Word = 0x8000ffff, SizeX = 3, SizeY = 3 } },
        };
        var model = new InteriorModel { Name = "synthetic" };
        var lod = new InteriorLodModel { Geo = geo, Base = lt };
        using var interiors = new InteriorRenderer(d, assets);
        interiors.Install(model, lod, 40, 120, 5, 0, 0, 0);

        // camera 10 m above the ground at (40, 100), looking along +Y and slightly down
        var cam = new Camera3D(40, 100, 10, 0, -15, 90, d.Width, d.Height, 0.5, 400);
        d.BeginFrame();
        d.ClearRgb(10, 20, 30);
        d.BeginScene3D(cam, 0.7f, 0.7f, 0.7f);
        terrain.Draw(cam);
        interiors.Draw(cam, p);
        d.EndScene3D();
        d.EndFrame();
        Assert.Equal(0u, dev.GetError());
        byte[] px = d.ReadPixels();
        (int r, int g, int bl) At(int x, int y) => (px[(y * d.Width + x) * 4], px[(y * d.Width + x) * 4 + 1], px[(y * d.Width + x) * 4 + 2]);

        Assert.True(terrain.LastSquares > 0);
        Assert.Equal(1, interiors.DrawnInstances);
        Assert.Equal(1, interiors.DrawnSurfaces);
        // the top row sees the sky area above the horizon: the clear colour
        Assert.Equal((10, 20, 30), At(2, 1));
        // bottom of the view: near terrain, an untextured material times the 0x8 light map nibble read back as 8 / 15 (136), no haze this close
        var near = At(d.Width / 2, d.Height - 3);
        Assert.InRange(near.r, 133, 139);
        Assert.InRange(near.g, 133, 139);
        // the cube face: white texture (material 255) times the constant 0xffff light map, haze 0 (distance 20 m < hazeStart)
        var wall = At(d.Width / 2, d.Height / 2);
        Assert.InRange(wall.r, 250, 255);
        Assert.InRange(wall.g, 250, 255);
        mOut.WriteLine($"squares {terrain.LastSquares} triangles {terrain.LastTriangles} near {near} wall {wall}");
    }

    [Fact]
    public void HeadlessDepthAndHazeBlend()
    {
        GlWindow? win = null;
        foreach (string? driver in new string?[] { "offscreen", null })
        {
            win = GlWindow.TryCreate("test", 32, 32, true, driver, out _);
            if (win != null)
                break;
        }
        if (win == null)
            return;
        using GlWindow w = win;
        using var dev = new GlGfxDevice(w);
        IGfxDevice d = dev;
        var cam = new Camera3D(0, 0, 0, 0, 0, 90, 32, 32, 0.5, 100);
        // two overlapping quads facing the camera: the nearer one (red) drawn first, the farther (green) second: depth keeps red
        Vertex3D V(float x, float y, float z, float haze, float r, float g) => new(x, y, z, 0, 0, 0, 0, haze, r, g, 0);
        Vertex3D[] Quad(float yDist, float haze, float r, float g) => new[]
        {
            V(-20, yDist, -20, haze, r, g), V(20, yDist, -20, haze, r, g), V(20, yDist, 20, haze, r, g),
            V(-20, yDist, -20, haze, r, g), V(20, yDist, 20, haze, r, g), V(-20, yDist, 20, haze, r, g),
        };
        d.BeginFrame();
        d.ClearRgb(0, 0, 0);
        d.BeginScene3D(cam, 0f, 0f, 1f);
        d.Draw3D(Quad(10, 0f, 1, 0), new DrawState3D(null));
        d.Draw3D(Quad(20, 0f, 0, 1), new DrawState3D(null));
        d.EndScene3D();
        byte[] px = d.ReadPixels();
        Assert.Equal((255, 0, 0), (px[(16 * 32 + 16) * 4], px[(16 * 32 + 16) * 4 + 1], px[(16 * 32 + 16) * 4 + 2]));
        // haze 0.5 toward the blue haze colour: mix(red, blue, 0.5)
        d.BeginScene3D(cam, 0f, 0f, 1f);
        d.Draw3D(Quad(10, 0.5f, 1, 0), new DrawState3D(null));
        d.EndScene3D();
        d.EndFrame();
        px = d.ReadPixels();
        Assert.InRange(px[(16 * 32 + 16) * 4], 126, 129);
        Assert.InRange(px[(16 * 32 + 16) * 4 + 2], 126, 129);
    }
}
