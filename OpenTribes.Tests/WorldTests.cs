// Port of tests/test_world.cpp: terrain height and line queries (docs/terrain.md), the interior BSP line cast
// (docs/interiors.md 7), the shape model and instance (docs/collision.md 6) and interior lights on synthetic data. The
// C++ main runs testTerrain first, which enters the script FPU mode; the tests after it start in that mode. The test
// arithmetic on doubles and floats follows the mode through Fpu like the C++.
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Script;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;
using static OpenTribes.Tests.Fixtures;
using Node = OpenTribes.Tests.Fixtures.Node;

public class WorldTests
{
    // terrain.md 3: reflected CRC-32, no final inversion.
    private static uint SpecCrc(List<byte> b, uint crc)
    {
        foreach (var c in b)
        {
            crc ^= c;
            for (var k = 0; k < 8; ++k)
                crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1)));
        }
        return crc;
    }

    private static bool Near(float a, double b, double eps = 1e-6) => Math.Abs(Sub(a, b)) <= eps;

    private static void CheckNormal(Vec3 n, double x, double y, double z)
    {
        var len = Sqrt(Add(Add(Mul(x, x), Mul(y, y)), Mul(z, z)));
        Assert.True(Near(n.X, Div(x, len)) && Near(n.Y, Div(y, len)) && Near(n.Z, Div(z, len)));
    }

    private static string Root(string name)
    {
        var root = Path.Combine(TestPaths.TmpDir(""), name);
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        return root;
    }

    private static uint BitsOf(float v) => BitConverter.SingleToUInt32Bits(v);
    private static float F(uint b) => BitConverter.UInt32BitsToSingle(b);

    [Fact]
    public void TestTerrain()
    {
        TowardZero = false; // process start
        var root = Root("world_root");
        // 5 x 5 corner heights of one 4 x 4 block of 8 m squares, repeated over a 2 x 1 map.
        var t = new TerrainSpec
        {
            Heights = [0, 8, 2, 6, 1, 16, 4, 10, 3, 7, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5]
        };
        t.Squares = [.. new byte[t.N * t.N * 2]];
        t.Squares[((0 * 4) + 2) * 2] = 1 << 3; // square (2, 0) empty (1.4 flags bits 3-5)
        List<byte> dtf = DtfFile(t, 0, 16), dtb = DtbFile(t, 0, 16), dml = [(byte)'d', (byte)'m', (byte)'l'];
        PutFile(root + "/base/t.dtf", dtf);
        PutFile(root + "/base/t#0.dtb", dtb);
        PutFile(root + "/base/t.dml", dml);
        Numbers.EnterScriptFpuMode(); // 4.3: the x87 truncates every float store
        var res = new ResourceManager(root);
        res.SetSearchPath("base");
        var terrain = Terrain.Load(res, "t.dtf");
        Assert.NotNull(terrain);
        terrain!.SetPosition(new Vec3(-16, -8, 0)); // 2.3: local = world - position

        // 3: CRC of the .dtf, each block file in list order, then the material list.
        uint crc = 0;
        Assert.True(terrain.Crc(res, "t.dtf", ref crc));
        Assert.True(crc == SpecCrc(dml, SpecCrc(dtb, SpecCrc(dtf, 0xffffffffu))));
        // resources.md 8.7: the terrain holds the .dtf, its blocks and the .dml until it goes.
        foreach (var nm in new[] { "t.dtf", "t#0.dtb", "t.dml" })
            Assert.True(res.Find(nm)!.Locks == 1 && res.Find(nm)!.HasInstance);

        // 2.4 world box: terrain-space (0, 0, hmin) - (W 2^B, H 2^B, hmax), B = D + S - 1 = 5.
        var box = new float[6];
        terrain.WorldBox(box);
        Assert.True(box[0] == -16 && box[1] == -8 && box[2] == 0 && box[3] == 48 && box[4] == 24 && box[5] == 16);

        // 4.2 parity split. Square (0, 0), parity 0: h00 0, h10 8, h01 16, h11 4.
        float h = 0;
        Vec3 n = default;
        Assert.True(terrain.Height(-14, -2, ref h, ref n) && h == 9); // u 2 <= v 6: (h00, h01, h11)
        CheckNormal(n, 16 - 4, 0 - 16, 8);
        Assert.True(terrain.Height(-10, -6, ref h, ref n) && h == 5); // u 6 > v 2: (h00, h10, h11)
        CheckNormal(n, 0 - 8, 8 - 4, 8);
        Assert.True(terrain.Height(-12, -4, ref h) && h == 2); // tie u == v: first triangle
        // Square (1, 0), parity 1: h00 8, h10 2, h01 4, h11 10.
        Assert.True(terrain.Height(-6, -6, ref h, ref n) && h == 5.5f); // s - u 6 > v 2: (h00, h10, h01)
        CheckNormal(n, 8 - 2, 8 - 4, 8);
        Assert.True(terrain.Height(-2, -2, ref h, ref n) && h == 6.5f); // else: (h10, h01, h11)
        CheckNormal(n, 4 - 10, 2 - 10, 8);
        Assert.True(terrain.Height(-4, -4, ref h) && h == 3); // tie s - u == v: second triangle
        // The second map cell repeats block 0; each block uses its own corner row/column.
        Assert.True(terrain.Height(18, -2, ref h) && h == 9);
        // Empty square, outside the map, and the trunc edge rule (-1 < local < 0 extrapolates).
        Assert.False(terrain.Height(4, -6, ref h));
        Assert.True(!terrain.Height(48.5f, -6, ref h) && !terrain.Height(-14, 24.5f, ref h));
        Assert.True(terrain.Height(-14, -8.5f, ref h) && h == 2.25f); // u 2, v -0.5 on (h00, h10, h11)
        Assert.False(terrain.Height(-14, -9.5f, ref h));

        // 5.3 line test. Vertical segments hit iff the end is at or below the height.
        Vec3 hit = default;
        Assert.True(terrain.CastLine(new Vec3(-14, -2, 20), new Vec3(-14, -2, 8.9f), ref hit, ref n));
        Assert.True(hit.X == -14 && hit.Y == -2 && Near(hit.Z, 9, 1e-5));
        CheckNormal(n, 12, -16, 8);
        Assert.False(terrain.CastLine(new Vec3(-14, -2, 20), new Vec3(-14, -2, 9.5f), ref hit, ref n));
        Assert.False(terrain.CastLine(new Vec3(4, -6, 20), new Vec3(4, -6, -5), ref hit, ref n));          // empty square
        Assert.False(terrain.CastLine(new Vec3(-14, -8.5f, 20), new Vec3(-14, -8.5f, -5), ref hit, ref n)); // floor: no terrain below 0
        // A slanted ray across squares and into the second block: the hit lies on the surface and every earlier point of
        // the ray is above it.
        Vec3 a = new(-15.5f, -6.5f, 30), e = new(40, -6.5f, -10);
        Assert.True(terrain.CastLine(a, e, ref hit, ref n));
        Assert.True(terrain.Height(hit.X, hit.Y, ref h) && Near(h, hit.Z, 1e-3));
        var tHit = Div(Sub(hit.X, a.X), Sub(e.X, a.X));
        for (double s = 0; s < Sub(tHit, 1e-3); s = Add(s, 1e-3))
        {
            float x = Store(Add(a.X, Mul(SubF(e.X, a.X), s))), z = Store(Add(a.Z, Mul(SubF(e.Z, a.Z), s)));
            if (terrain.Height(x, a.Y, ref h))
                Assert.True(z > h);
        }
    }

    // 7.2 BSP descent on a slab solid for 0 < x < 10: node 0 splits on x = 0 (front x > 0), node 1 on -x + 10 = 0 (front
    // x < 10). Leaf 43 is the solid leaf, 44 empty (4.2).
    [Fact]
    public void TestInterior()
    {
        Numbers.EnterScriptFpuMode();
        using var chop = X87.Chop(); // the game's rounding: f32 stores toward zero
        float[] box = [0, -1, -1, 10, 1, 1];
        var nodes = new List<Node> { new(0, 1, -45), new(1, -44, -45) };
        var planes = new List<float[]> { new float[] { 1, 0, 0, 0 }, new float[] { -1, 0, 0, 10 } };
        var g = InteriorGeometry.Parse([.. DigFile(7, box, nodes, 1, 45, planes)]);
        Assert.NotNull(g);
        for (var i = 0; i < 6; ++i)
            Assert.True(g!.Box[i] == box[i]);
        var hit = new InteriorGeometry.Hit();
        // R5 then R4: entering at x = 0, normal of the last plane, negated toward the start (R6).
        Assert.True(g!.CastLine(new Vec3(-5, 0, 0), new Vec3(20, 0, 0), ref hit));
        Assert.True(hit.Point.X == 0 && hit.Leaf == 43 && hit.PlaneIndex == 0);
        Assert.True(hit.Normal.X == -1 && hit.Normal.Y == 0 && hit.Normal.Z == 0);
        // From the other side: enters at x = 10 through plane 1, normal +x.
        Assert.True(g.CastLine(new Vec3(20, 0, 0), new Vec3(-5, 0, 0), ref hit));
        Assert.True(hit.Point.X == 10 && hit.PlaneIndex == 1 && hit.Normal.X == 1);
        // R6: a start inside the solid hits at the start with normalize(b - a).
        Assert.True(g.CastLine(new Vec3(5, 0, 0), new Vec3(20, 0, 0), ref hit));
        Assert.True(hit.Point.X == 5 && hit.PlaneIndex == -1 && hit.Normal.X == 0.99999994f && hit.Normal.Y == 0);
        // 0x005607aa: f32(1 / 15) * 15 is stored as 0.99999994 under the game's rounding. A zero length (f32) only
        // replaces z.
        Assert.True(g.CastLine(new Vec3(5, 0, 0), new Vec3(5, 0, 0), ref hit));
        Assert.True(hit.Normal.X == 0 && hit.Normal.Y == 0 && hit.Normal.Z == 1);
        // R3: a segment on a plane descends only into node children, back first. On plane 0 the front child is node 1
        // (solid ahead); on plane 1 both children are leaves: no hit.
        Assert.True(g.CastLine(new Vec3(0, -5, 0), new Vec3(0, 5, 0), ref hit));
        Assert.True(hit.Point.Y == -5 && hit.PlaneIndex == -1 && hit.Normal.Y == 0.99999994f);
        Assert.False(g.CastLine(new Vec3(10, -5, 0), new Vec3(10, 5, 0), ref hit));
        // Within epsilon 1e-5 of a plane counts as on it.
        Assert.False(g.CastLine(new Vec3(10.000001f, -5, 0), new Vec3(10.000001f, 5, 0), ref hit));
        // Misses: entirely in front of the slab or behind plane 0.
        Assert.False(g.CastLine(new Vec3(-5, 0, 0), new Vec3(-1, 0, 0), ref hit));
        Assert.False(g.CastLine(new Vec3(15, 0, 0), new Vec3(11, 0, 0), ref hit));
        // Malformed data is rejected.
        var bad = DigFile(7, box, nodes, 1, 45, planes);
        bad.RemoveRange(bad.Count - 30, 30);
        Assert.Null(InteriorGeometry.Parse([.. bad]));
    }

    // docs/collision.md 9 (terrain line): every ray of tests/terrain_line_vectors.inc, generated from the verified model
    // tools/player/terr_line.py on the synthetic terrain of tools/player/gen_terrain_vectors.py, gives the same hit flag
    // and the same f32 bits.
    [Fact]
    public void TestTerrainLineVectors()
    {
        Numbers.EnterScriptFpuMode();
        var root = Root("world_line_root");
        var t = new TerrainSpec
        {
            N = 8,
            Detail = 4,
            Scale = 3
        };
        t.Width = t.Height = 2;
        for (var y = 0; y <= 8; ++y)
            for (var x = 0; x <= 8; ++x)
                t.Heights.Add(DivF(((x * 37) + (y * 101) + (x * y * 13)) % 193, 8.0f));
        t.Squares = [.. new byte[8 * 8 * 2]];
        for (var y = 0; y < 8; ++y)
            for (var x = 0; x < 8; ++x)
                t.Squares[(((y * 8) + x) * 2) + 1] = (byte)((x + (y * 3)) & 7);
        t.Squares[((5 * 8) + 2) * 2] = 1 << 3;
        PutFile(root + "/base/t.dtf", DtfFile(t, 0, 24));
        PutFile(root + "/base/t#0.dtb", DtbFile(t, 0, 24));
        var res = new ResourceManager(root);
        res.SetSearchPath("base");
        var terrain = Terrain.Load(res, "t.dtf");
        Assert.NotNull(terrain);
        terrain!.SetPosition(new Vec3(-64, -32, 10));
        var rows = TestPaths.IncRows("tests/terrain_line_vectors.inc");
        int hits = 0, bad = 0;
        for (var ri = 0; ri < rows.Length; ++ri)
        {
            var r = rows[ri];
            Vec3 hit = default, n = default;
            var got = terrain.CastLine(new Vec3(F(r[0]), F(r[1]), F(r[2])), new Vec3(F(r[3]), F(r[4]), F(r[5])), ref hit, ref n);
            var ok = got == (r[6] != 0);
            if (ok && got)
            {
                ++hits;
                ok = BitsOf(hit.X) == r[7] && BitsOf(hit.Y) == r[8] && BitsOf(hit.Z) == r[9] && BitsOf(n.X) == r[10] &&
                     BitsOf(n.Y) == r[11] && BitsOf(n.Z) == r[12];
            }
            if (!ok)
            {
                ++bad;
                Console.Error.WriteLine("terrain line vector " + ri + " differs (hit " + (got ? 1 : 0) + ", want " + (r[6] != 0 ? 1 : 0) + ")");
            }
        }
        Assert.True(bad == 0 && hits > 80);
        // The material byte of the square that was hit (4.4 step 12): a vertical ray into square (3, 2) of the first
        // block.
        Vec3 hit2 = default, n2 = default;
        var material = -1;
        Assert.True(terrain.CastLine(new Vec3(-64 + 28, -32 + 20, 80), new Vec3(-64 + 28, -32 + 20, 0), ref hit2, ref n2, ref material));
        Assert.True(material == ((3 + (2 * 3)) & 7));
    }

    // ---- shape model, instance and line test (docs/collision.md 6 T1-T8) ----

    // Nodes: 0 bounds, 1 root (detail 0), 2 arm (child of 1, default transform 1, keyed by sequence `swing`), 3 always,
    // 4 mark (child of 3). Objects: body on node 1 (hidden by the `visibility` sequence at position 1), hand on node 2,
    // wreck on node 1 (flag 1: hidden unless a key shows it).
    private static DtsSpec ShapeSpec()
    {
        var s = new DtsSpec
        {
            Names = ["bounds", "root", "arm", "always", "mark", "visibility", "swing", "body", "hand", "wreck"],
            Nodes = [new(0, -1, 0, 0, 0), new(1, 0, 0, 0, 0), new(2, 1, 1, 0, 1), new(3, 0, 0, 0, 0), new(4, 3, 0, 0, 2)],
            Sequences = [new(5, 0, 1.0f, 4096), new(6, 0, 2.0f, 2)],
            // sub 0: node 2 on swing; sub 1: body on visibility; sub 2: wreck on visibility.
            Subs = [new(1, 3, 0), new(0, 2, 3), new(0, 2, 5)],
            Keys = [ new(0.0f, 1, 0x8000), new(0.5f, 3, 0x8000), new(1.0f, 4, 0x8000),
                             new(0.0f, 0, 0xc000), new(1.0f, 0, 0x4000), new(0.0f, 0, 0x4000), new(1.0f, 0, 0xc000) ],
            Transforms =
            [
                new([0, 0, 0, 32767], [0, 0, 0]),
                new([0, 0, 0, 32767], [4, 0, 0]),
                new([0, 0, 23170, 23170], [1, 2, 3]), // yaw 90 degrees
                new([0, 0, 23170, 23170], [4, 0, 2]),
                new([0, 0, 32767, 0], [4, 0, 4]),     // yaw 180 degrees
            ],
            Objects = [ new(7, 0, 0, 1, [0, 0, 0], 1, 1), new(8, 0, 1, 2, [0, 0, 0.5f], 0, 0),
                                new(9, 1, 0, 1, [0, 0, 0], 1, 2) ],
            Details = [new(1, 10.0f)]
        };
        float[] body = [-1, -1, -1, 1, 1, 1], hand = [-0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f];
        s.Meshes = [DtsBoxMesh(body), DtsBoxMesh(hand)];
        s.AlwaysNode = 3;
        return s;
    }

    [Fact]
    public void TestShapeModel()
    {
        Numbers.EnterScriptFpuMode();
        var file = DtsModelFile(ShapeSpec()).ToArray();
        var m = new ShapeModel();
        Assert.True(ShapeModel.Parse(file, m));
        Assert.True(m.Nodes.Length == 5 && m.Objects.Length == 3 && m.Meshes.Length == 2 && m.Meshes[0].Faces.Length == 12);
        Assert.True(m.FindNode("ARM") == 2 && m.FindNode("none") == -1 && m.FindSequence("swing") == 1 && m.AlwaysNode == 3);
        // Truncated files are rejected, never read past the end.
        foreach (var cut in new[] { 10, file.Length / 3, file.Length / 2, file.Length - 5 })
        {
            var badFile = file[..cut];
            var x = new ShapeModel();
            Assert.False(ShapeModel.Parse(badFile, x));
        }

        // m_mul_TMat3F (0x005ed2b2): rotation rows, translation ((a.x b0 + a.y b3) + b.t) + a.z b6.
        TMat3 a = new()
        {
            M = Float9.Of([0, 1, 0, -1, 0, 0, 0, 0, 1]),
            P = Float3.Of(1, 2, 3),
            Flags = 3
        }, b = new()
        {
            M = Float9.Of([1, 0, 0, 0, 0, 1, 0, -1, 0]),
            P = Float3.Of(10, 20, 30),
            Flags = 2
        };
        Shape.TmatMul(a, b, out var c);
        Assert.True(c.Flags == 3 && c.M[2] == 1 && c.M[3] == -1 && c.M[7] == -1);
        Assert.True(c.P[0] == 11 && c.P[1] == 17 && c.P[2] == 32);
        // Inverse of a rotation (T1.1): transpose, t' = (-t) R'.
        Shape.TmatInverse(c, out var inv);
        Shape.TmatMul(c, inv, out var id);
        for (var i = 0; i < 9; ++i)
            Assert.True(id.M[i] == (i % 4 == 0 ? 1.0f : 0.0f));
        Assert.True(id.P[0] == 0 && id.P[1] == 0 && id.P[2] == 0);

        // m_mat3_setEuler (0x005ed42e) with the x87 fsincos. Yaw pi / 2 as f32 (0x3fc90fdb) is the Player's lowerback
        // override matrix: cos b33bbd2e, sin 3f7fffff.
        var g = new TMat3();
        float[] yaw = [0, 0, 1.57079637f];
        Shape.EulerMatrix(yaw, ref g);
        Assert.True(BitsOf(g.M[0]) == 0xb33bbd2eu && BitsOf(g.M[1]) == 0x3f7fffffu && BitsOf(g.M[3]) == 0xbf7fffffu);
        Assert.True(BitsOf(g.M[4]) == 0xb33bbd2eu && g.M[8] == 1.0f && (g.Flags & 5) == 1);
        // Pitch only: the general case with y = z = 0 gives -0 in m2 and m3 (reference GameBase::getMuzzleTransform prints
        // 0.999799 0.019998 for pitch 0.02).
        var p = new TMat3();
        float[] pitch = [0.02f, 0, 0];
        Shape.EulerMatrix(pitch, ref p);
        Assert.True(BitsOf(p.M[2]) == 0x80000000u && BitsOf(p.M[3]) == 0x80000000u && p.M[0] == 1.0f);
        Assert.True(Math.Abs(SubF(p.M[4], 0.999799f)) < 1e-6f && Math.Abs(SubF(p.M[5], 0.019998f)) < 1e-6f && p.M[7] == -p.M[5]);
        float[] zero = [0, 0, 0];
        p.Flags = 7;
        Shape.EulerMatrix(zero, ref p);
        Assert.True(p.Flags == 2 && p.M[4] == 1.0f);

        // Quaternion matrix (0x005ee124): twelve f32 products, then sums.
        var q = new TMat3();
        float[] quat = [0, 0, 0.707107484f, 0.707107484f];
        Shape.QuatToMatrix(quat, ref q);
        var z2 = MulF(quat[2], 2.0f);
        Assert.True(q.M[0] == SubF(1.0f, MulF(quat[2], z2)) && q.M[1] == -MulF(quat[3], z2) && q.M[3] == MulF(quat[3], z2) && q.M[8] == 1.0f);

        // Instance build (T2.4, T2.5): node 0 and its root-type siblings keep identity, children are local x parent. The
        // always subtree is animated too.
        var inst = new ShapeInstance();
        inst.Build(m);
        Assert.True(inst.DetailRoot(0) == 1 && inst.DetailRoot(1) == -1);
        Assert.True(inst.Entries[1].P[0] == 0 && inst.Entries[2].P[0] == 4 && inst.Entries[2].M[0] == 1);
        Assert.True(inst.Entries[4].P[0] == 1 && inst.Entries[4].P[1] == 2 && inst.Entries[4].P[2] == 3);
        Assert.True(inst.Objects[0].Visible && !inst.Objects[2].Visible);

        // T7 line test. Along +x from outside: the body's -x face at x = -1 (t = 0.25).
        var sf = new ShapeSurface();
        float[] s1 = [-2, 0.25f, 0.25f], e1 = [2, 0.25f, 0.25f];
        Assert.True(inst.CastLine(s1, e1, true, null, ref sf));
        Assert.True(Near(sf.T, 0.25f) && Near(sf.Position[0], -1) && Near(sf.Normal[0], -1) && Near(sf.Normal[1], 0) && sf.Part == 0);
        Assert.True(sf.Material == 7 && sf.Face >= 0);
        // Bounds mode: the bounds t of the first candidate, no normal.
        Assert.True(inst.CastLine(s1, e1, false, null, ref sf) && Near(sf.T, 0.25f) && Near(sf.Normal[0], 0) && sf.Material == -1);
        // From inside the body only back faces are met: no hit on it; the hand (node 2 at x = 4, object offset z 0.5) is
        // hit at x = 3.5.
        float[] s2 = [0, 0, 0.5f], e2 = [8, 0, 0.5f];
        Assert.True(inst.CastLine(s2, e2, true, null, ref sf));
        Assert.True(sf.Part == 1 && Near(sf.Position[0], 3.5f) && Near(sf.Normal[0], -1));
        // The slab test needs the entry strictly inside a face (T7.1): a ray along an edge misses.
        float[] s3 = [-2, 1, 1], e3 = [2, 1, 1];
        Assert.False(inst.CastLine(s3, e3, true, null, ref sf));
        // A miss, and a segment that ends before the box.
        float[] s4 = [-2, 0, 5], e4 = [2, 0, 5], e5 = [-1.5f, 0.25f, 0.25f];
        Assert.True(!inst.CastLine(s4, e4, true, null, ref sf) && !inst.CastLine(s1, e5, true, null, ref sf));

        // Threads. Visibility at 1 hides the body and shows the wreck (T2.6, key 0x005b8e58).
        inst.Threads = [new(0, 1.0f, 4096)];
        inst.Animate();
        Assert.True(!inst.Objects[0].Visible && inst.Objects[2].Visible);
        Assert.True(inst.CastLine(s1, e1, true, null, ref sf) && sf.Part == 2);
        // Swing at 0: key 0 exactly (fraction 0), the default pose. At 1: the last key, yaw 180 at (4, 0, 4). At 0.5:
        // key 1, yaw 90 at (4, 0, 2).
        inst.Threads = [new(1, 0.0f, 2)];
        inst.Animate();
        Assert.True(inst.Entries[2].P[0] == 4 && inst.Entries[2].P[2] == 0 && inst.Entries[2].M[0] == 1);
        inst.Threads = [new(1, 1.0f, 2)];
        inst.Animate();
        Assert.True(inst.Entries[2].P[2] == 4 && Near(inst.Entries[2].M[0], -1) && Near(inst.Entries[2].M[4], -1));
        inst.Threads = [new(1, 0.5f, 2)];
        inst.Animate();
        Assert.True(inst.Entries[2].P[2] == 2 && Math.Abs(AddF(inst.Entries[2].M[1], 1.0f)) < 1e-4f);
        // Between keys 0 and 1 (fraction 0.5): the translation is the f32 lerp, the rotation a slerp half way (yaw 45
        // degrees).
        inst.Threads = [new(1, 0.25f, 2)];
        inst.Animate();
        Assert.True(inst.Entries[2].P[0] == 4 && inst.Entries[2].P[2] == 1);
        Assert.True(Math.Abs(SubF(inst.Entries[2].M[0], 0.70710678f)) < 2e-4f && Math.Abs(AddF(inst.Entries[2].M[1], 0.70710678f)) < 2e-4f);
        // The hand follows its node: at swing 1 it sits at z 4.5 (offset through the node entry).
        inst.Threads = [new(1, 1.0f, 2)];
        inst.Animate();
        float[] s6 = [4, 0, 9], e6 = [4, 0, -9];
        Assert.True(inst.CastLine(s6, e6, true, null, ref sf) && sf.Part == 1 && Near(sf.Position[2], 5.0f) && Near(sf.Normal[2], 1));
        // Thread order (0x005c0a1c): the lowest priority value that lists a node controls it.
        inst.Threads = [new(1, 1.0f, 5000), new(1, 0.0f, -1)];
        inst.Animate();
        Assert.Equal(0, inst.Entries[2].P[2]);

        // shapeLine: world space through the object transform (T2.3).
        var obj = new TMat3
        {
            P = Float3.Of(100, 50, 10),
            Flags = 2
        };
        inst.Threads.Clear();
        inst.Animate();
        float[] ws = [98, 50.25f, 10.25f], we = [102, 50.25f, 10.25f];
        float[] wp = new float[3], wn = new float[3];
        Assert.True(Shape.ShapeLine(inst, obj, ws, we, true, wp, wn));
        Assert.True(Near(wp[0], 99) && Near(wp[1], 50.25f) && Near(wp[2], 10.25f) && Near(wn[0], -1));
    }

    // docs/collision.md 6 T4 (sphere against a shape, mesh mode T4.1 and bounds mode T4.2): every row of
    // tests/shape_sphere_vectors.inc, generated by the exact model tools/player/m_shape.py on the synthetic shape built
    // here, gives the same records with the same f32 values.
    [Fact]
    public void TestShapeSphereVectors()
    {
        Numbers.EnterScriptFpuMode();
        var s = new DtsSpec
        {
            Names = ["bounds", "root", "a", "b"],
            Nodes = [new(0, -1, 0, 0, 0), new(1, 0, 0, 0, 1)],
            Transforms =
            [
                new([0, 0, 0, 32767], [0, 0, 0]),
                new([0, 0, 0, 32767], [0.25f, -0.5f, 0.125f]),
            ],
            Objects = [new(2, 0, 0, 1, [0.5f, 0.0f, 0.25f], 0, 0), new(3, 0, 1, 1, [-3.0f, 1.0f, 0.0f], 0, 0)],
            Details = [new(1, 10.0f)]
        };
        float[] scaleA = [(float)(2.0 / 255), (float)(3.0 / 255), (float)(1.5 / 255)], originA = [-1.0f, -1.5f, 0.0f];
        float[] scaleB = [(float)(1.0 / 255), (float)(1.0 / 255), (float)(4.0 / 255)], originB = [-0.5f, -0.5f, 0.0f];
        s.Meshes = [DtsBoxMeshFrame(scaleA, originA), DtsBoxMeshFrame(scaleB, originB)];
        var m = new ShapeModel();
        Assert.True(ShapeModel.Parse([.. DtsModelFile(s)], m));
        var inst = new ShapeInstance();
        inst.Build(m);
        Assert.True(m.CollisionDetail() == 0 && inst.DetailNodes(0).Count == 1 && inst.DetailNodes(3).Count == 0);
        var rows = TestPaths.IncRows("tests/shape_sphere_vectors.inc");
        int bad = 0, withRecords = 0, bounds = 0;
        for (var ri = 0; ri < rows.Length; ++ri)
        {
            var r = rows[ri];
            float[] p0 = [F(r[2]), F(r[3]), F(r[4])];
            var rel = new TMat3
            {
                Flags = 3
            };
            for (var i = 0; i < 9; ++i)
                rel.M[i] = F(r[5 + i]);
            for (var i = 0; i < 3; ++i)
                rel.P[i] = F(r[14 + i]);
            var output = new List<ShapeContact>();
            var hit = inst.CollideSphere(0, p0, r[0] != 0, rel, F(r[1]), output);
            var ok = hit == (r[17] != 0) && output.Count == r[17];
            for (var k = 0; ok && k < output.Count; ++k)
            {
                var e = 18 + (k * 7);
                for (var i = 0; i < 3; ++i)
                    ok = ok && output[k].Position[i] == F(r[e + i]) && output[k].Normal[i] == F(r[e + 3 + i]);
                ok = ok && output[k].Distance == F(r[e + 6]) && output[k].Part >= 0 && output[k].Material == 7 == (r[0] != 0);
            }
            withRecords += r[17] != 0 ? 1 : 0;
            bounds += r[0] == 0 && r[17] != 0 ? 1 : 0;
            if (!ok)
            {
                ++bad;
                Console.Error.WriteLine("shape sphere vector " + ri + " differs (hit " + (hit ? 1 : 0) + ", want " + (r[17] != 0 ? 1 : 0) +
                                        ", records " + output.Count + "/" + r[17] + ")");
            }
        }
        Assert.True(bad == 0 && withRecords >= 60 && bounds >= 10);
        // The list is not reset (TQ9): records are appended and the result is "list not empty".
        var keep = new List<ShapeContact> { new() };
        float[] far = [100, 100, 100];
        Assert.True(inst.CollideSphere(0, far, true, null, 1.0f, keep) && keep.Count == 1);
        keep.Clear();
        Assert.True(!inst.CollideSphere(0, far, true, null, 1.0f, keep) && !inst.CollideSphere(1, far, false, null, 500.0f, keep));
        // A hidden object is no candidate (T2.7 step 1).
        float[] above = [0.75f, -0.5f, 2.0f];
        Assert.True(inst.CollideSphere(0, above, true, null, 0.5f, keep) && keep[0].Part == 0 && Near(keep[0].Normal[2], 1));
        keep.Clear();
        inst.Objects[0].Visible = false;
        Assert.False(inst.CollideSphere(0, above, true, null, 0.5f, keep));

        // T2.1: the collision detail is the last detail rooted at the node named "collision" (any case); its nodes keep
        // identity entries (T2.5), so the mesh sits at the object offset only.
        var c = new DtsSpec
        {
            Names = ["bounds", "root", "Collision", "body"],
            Nodes = [new(0, -1, 0, 0, 0), new(1, 0, 0, 0, 1), new(2, 0, 0, 0, 1)],
            Transforms =
            [
                new([0, 0, 0, 32767], [0, 0, 0]),
                new([0, 0, 0, 32767], [5, 0, 0]),
            ],
            Objects = [new(3, 0, 0, 1, [0, 0, 0], 0, 0), new(3, 0, 0, 2, [0, 0, 0], 0, 0)],
            Details = [new(1, 10.0f), new(2, 0.0f)]
        };
        float[] unit = [-1, -1, 0, 1, 1, 1];
        c.Meshes = [DtsBoxMesh(unit)];
        var cm = new ShapeModel();
        Assert.True(ShapeModel.Parse([.. DtsModelFile(c)], cm) && cm.CollisionDetail() == 1);
        var ci = new ShapeInstance();
        ci.Build(cm);
        float[] top = [0, 0, 1.25f], topMoved = [5, 0, 1.25f];
        var rec = new List<ShapeContact>();
        Assert.True(ci.Entries[1].P[0] == 5 && ci.Entries[2].P[0] == 0);
        Assert.True(ci.CollideSphere(1, top, true, null, 0.5f, rec) && Near(rec[0].Distance, 0.25f) && Near(rec[0].Position[2], 1));
        rec.Clear();
        Assert.True(!ci.CollideSphere(1, topMoved, true, null, 0.5f, rec) && ci.CollideSphere(0, topMoved, true, null, 0.5f, rec));

        // Image test (0x00436ed0, 0x0043873c): the records stay in root space and hit.rot is the 3x3 of X * shape
        // transform, which turns the normal to world space.
        float[] quarter = [0, 1, 0, -1, 0, 0, 0, 0, 1], identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        float[] shapePos = [10, 20, 30], ownPos = [11.25f, 20, 30.25f], centre = [0, 0, 0.25f];
        var chit = new CollisionHit();
        Assert.True(Collision.CollideSphereShape(ci, 1, true, quarter, shapePos, identity, ownPos, centre, 0.5f, chit));
        // The shape is turned by 90 degrees: its local -y side faces world +x; the sphere at world x 11.25 is 0.25 off
        // that side.
        var wn = new float[3];
        var s0 = chit.Surfaces[0];
        X87.MulVector(s0.Normal, chit.Rot, wn);
        Assert.True(Near(chit.Surfaces[0].Normal[1], -1) && Near(wn[0], 1) && Near(chit.Surfaces[0].Distance, 0.25f));
        Assert.True(chit.Surfaces[0].HasMaterial && chit.StepHeight == -1.0f);
        // Bounds mode (Item images): axis records, no material, and a centre inside the box collides.
        float[] inside = [10, 20, 30.5f], zero = [0, 0, 0];
        Assert.True(Collision.CollideSphereShape(ci, 1, false, quarter, shapePos, identity, inside, zero, 1.25f, chit));
        Assert.True(chit.Surfaces.Count == 3 && !chit.Surfaces[0].HasMaterial && chit.Surfaces[2].Normal[2] == 1.0f);
        Assert.False(Collision.CollideSphereShape(ci, 1, true, quarter, shapePos, identity, inside, zero, 1.25f, chit));
    }

    // Reference vector (Broadside, losdiff seed 1 ray 33): a ray that starts inside the solid of an InteriorShape rotated
    // about z. a and b are the interior-space segment that reaches the solid leaf (b is a BSP split point). The reference
    // prints the world normal 0.290101 -0.936481 0.197086; with the direction divided by the unrounded length the first
    // component printed 0.290102 (interiors.md 7.2 R6).
    [Fact]
    public void TestInteriorSolidStartNormal()
    {
        Numbers.EnterScriptFpuMode();
        float[] box = [-100, -100, -100, 100, 100, 100];
        var nodes = new List<Node> { new(0, 1, -45), new(1, -44, -45) };
        var planes = new List<float[]> { new float[] { 1, 0, 0, 100 }, new float[] { -1, 0, 0, 100 } };
        var g = InteriorGeometry.Parse([.. DigFile(7, box, nodes, 1, 45, planes)]);
        Assert.NotNull(g);
        using var chop = X87.Chop();
        // -0x1.33a6ecp+3f, 0x1.6191cap+2f, 0x1.91446p+3f and -0x1.2401fep+3f, 0x1.8p+2f, 0x1.95a778p+3f as bits.
        Vec3 a = new(F(0xc119d376), F(0x40b0c8e5), F(0x4148a230)), b = new(F(0xc11200ff), F(0x40c00000), F(0x414ad3bc));
        var hit = new InteriorGeometry.Hit();
        Assert.True(g!.CastLine(a, b, ref hit));
        Assert.True(hit.PlaneIndex == -1 && hit.Point.X == a.X && hit.Point.Y == a.Y && hit.Point.Z == a.Z);
        Assert.True(hit.Normal.X == F(0x3f33eb27) && hit.Normal.Y == F(0x3f2efc1b) && hit.Normal.Z == F(0x3e49d113));
        // Object transform of the InteriorShape (rotation 0 0 -2.04188): normal to world space.
        float[] m = [F(0xbee86078), F(0xbf641d53), 0, F(0x3f641d53), F(0xbee86078), 0, 0, 0, 1];
        float[] ln = [hit.Normal.X, hit.Normal.Y, hit.Normal.Z];
        var wn = new float[3];
        X87.MulVector(ln, m, wn);
        Assert.True(wn[0] is >= 0.290101f and < 0.290102f);
        Assert.True(wn[1] is > (-0.936482f) and <= (-0.936481f));
        Assert.True(wn[2] is >= 0.197086f and < 0.197087f);
    }

    // General inverse of a flag 4 matrix (docs/collision.md T1.1, inlined at 0x0043786d): the vectors are the output of
    // the exact model tools/player/m_inverse.py (f32 cofactors, the determinant from unrounded cofactors, cofactor x
    // f32(1 / det)). The last one has det^2 below 1e-12: the 3x3 is left as copied. No stock server transform sets flag
    // 4, so there is no live reference for these.
    [Fact]
    public void TestGeneralInverse()
    {
        Numbers.EnterScriptFpuMode();
        uint[][][] kVectors =
        [
            [ [0xc04d88c2u, 0x3efb1c3bu, 0xc08be2b8u, 0x3fa6ed6bu, 0x3bc59fefu, 0xbf8c7686u, 0x3d4067b5u, 0x3eaf5032u, 0xbe050e9cu],
                    [0xbdf7f251u, 0x3eece886u, 0x3e293a4eu, 0xbd1bef1du, 0xbe4ddc47u, 0x403ea0acu, 0xbe138b8fu, 0xbeb997d7u, 0x3e59dfdeu] ],
            [ [0x40bf2ce6u, 0x4023b7e1u, 0xbf590280u, 0xbadabf8bu, 0x3f7c8555u, 0xc0beab9eu, 0xc12c4855u, 0xbe73f0a1u, 0xbf15e865u],
                    [0xbc6482f4u, 0x3c428298u, 0xbdce0d15u, 0x3ee5696cu, 0xbdb4973eu, 0x3e7e9563u, 0x3d97ec35u, 0xbe3acef5u, 0x3d28b2d9u] ],
            [ [0xbe1829cfu, 0x3edd01fcu, 0x3e95f937u, 0x41381b6au, 0x405ac839u, 0x3f6e413du, 0xbfad5366u, 0x3ea91153u, 0x41215da9u],
                    [0xbf244817u, 0x3da3b9a0u, 0x3c387f67u, 0x400d00aau, 0x3ca98c21u, 0xbd86f598u, 0xbe221855u, 0x3c24c1e5u, 0x3dd294eau] ],
            [ [0xbe470cb4u, 0xbdeba855u, 0x3f9649adu, 0xbe94e3f9u, 0x41070aa8u, 0xbc7b72c4u, 0x3f21f01fu, 0xbfeaffbcu, 0x3ecb7b24u],
                    [0xbf07258bu, 0x3eab74e8u, 0x3fc94a1fu, 0xbc89a554u, 0x3e05447du, 0x3d5fe94eu, 0x3f433d2cu, 0x3d8baa21u, 0x3e880ae7u] ],
            [ [0xbe8f225au, 0xc08adeb2u, 0xbf75c9b3u, 0xbdfee527u, 0xc0ebbf6cu, 0x3f1f6d22u, 0x3fd3097bu, 0x4152905au, 0xc062660au],
                    [0xbf81b7aeu, 0x3fcb352eu, 0x3f0df613u, 0xbd08447eu, 0xbe15648au, 0xbc886e5cu, 0xbf1899a1u, 0x3e49e733u, 0xbdb0844au] ],
            [ [0x3a83126fu, 0x3951b717u, 0x00000000u, 0x00000000u, 0x3a83126fu, 0x399d4952u, 0x38d1b717u, 0x00000000u, 0x3a83126fu],
                    [0x3a83126fu, 0x3951b717u, 0x00000000u, 0x00000000u, 0x3a83126fu, 0x399d4952u, 0x38d1b717u, 0x00000000u, 0x3a83126fu] ],
        ];
        using var chop = X87.Chop();
        foreach (var v in kVectors)
        {
            var a = new TMat3();
            for (var i = 0; i < 9; ++i)
                a.M[i] = F(v[0][i]);
            a.P = Float3.Of(1, -2, 0.5f);
            a.Flags = 7;
            Shape.TmatInverse(a, out var inv);
            var same = true;
            for (var i = 0; i < 9; ++i)
                same = same && BitsOf(inv.M[i]) == v[1][i];
            Assert.True(same);
            // t' = (-t) R' (m_mul_vector with the new 3x3).
            float[] neg = [-1, 2, -0.5f];
            var t = new float[3];
            X87.MulVector(neg, inv.M, t);
            Assert.True(BitsOf(inv.P[0]) == BitsOf(t[0]) && BitsOf(inv.P[1]) == BitsOf(t[1]) && BitsOf(inv.P[2]) == BitsOf(t[2]));
            Assert.Equal(7u, inv.Flags);
        }
    }

    // resources.md 8.7: the resources an InteriorShape's ITRInstance loads (0x00555f48) and holds.
    [Fact]
    public void TestInteriorResources()
    {
        var root = Root("world_res_root");
        float[] box = [0, -1, -1, 10, 1, 1];
        var nodes = new List<Node> { new(0, 1, -45), new(1, -44, -45) };
        var planes = new List<float[]> { new float[] { 1, 0, 0, 0 }, new float[] { -1, 0, 0, 10 } };
        PutFile(root + "/base/hut.dis", DisFile("hut-00.dig", "hut-000-0.dil"));
        PutFile(root + "/base/hut.0.dis", DisFile("hut-00.dig", "hut-000-0.dil"));
        PutFile(root + "/base/bad.0.dis", DisFile("hut-00.dig", "bad-000-0.dil"));
        PutFile(root + "/base/hut-00.dig", DigFile(7, box, nodes, 1, 45, planes));
        PutFile(root + "/base/hut-000-0.dil", DilFile(7, [], [], ""));
        PutFile(root + "/base/hut-000.dil", DilFile(7, [], [], ""));
        PutFile(root + "/base/bad-000-0.dil", DilFile(8, [], [], "")); // buildId differs from the geometry
        PutFile(root + "/base/bad-000.dil", DilFile(7, [], [], ""));
        // x.dml (DisFile): one detail, three materials; only type 3 is a texture.
        var ml = new List<byte>();
        PutU32(ml, 1);
        PutU32(ml, 3);
        foreach (var (type, map) in new (uint, string)[] { (3, "a.bmp"), (2, "nope.bmp"), (0x103, "B.BMP") })
        {
            var e = new List<byte>(new byte[0x40]);
            SetU32(e, 0, type);
            var mb = ByteString.ToBytes(map);
            for (var i = 0; i < mb.Length; ++i)
                e[0x10 + i] = mb[i];
            ml.AddRange(e);
        }
        PutFile(root + "/base/x.dml", Pers("TS::MaterialList", 4, ml));
        foreach (var nm in new[] { "a.bmp", "b.bmp", "nope.bmp" })
            PutFile(root + "/base/" + nm, "bmp");
        var res = new ResourceManager(root);
        res.SetSearchPath("base");
        uint Locks(string nm) => res.Find(nm)!.Locks;
        {
            var held = new ResourceLocks();
            Interior.LoadInteriorResources(res, "hut.0.dis", held); // mission-lit name
            foreach (var nm in new[] { "hut.0.dis", "x.dml", "a.bmp", "b.bmp", "hut-00.dig", "hut-000-0.dil", "hut-000.dil" })
                Assert.Equal(1u, Locks(nm));
            Assert.True(!res.Find("nope.bmp")!.HasInstance && !res.Find("hut.dis")!.HasInstance);
            // L3 fallback: hut.1.dis is missing, hut.dis loads without the mission lighting.
            Interior.LoadInteriorResources(res, "hut.1.dis", held);
            Assert.True(Locks("hut.dis") == 1 && Locks("hut-000-0.dil") == 1 && Locks("hut-000.dil") == 2);
            // Lighting buildId 8 against geometry 7: the mission .dil stays loaded but unlocked.
            Interior.LoadInteriorResources(res, "bad.0.dis", held);
            Assert.True(Locks("bad-000-0.dil") == 0 && res.Find("bad-000-0.dil")!.HasInstance && Locks("bad-000.dil") == 1);
            held.Release(); // the C++ holder's destructor at the end of the block
        }
        foreach (var nm in new[] { "hut.0.dis", "x.dml", "a.bmp", "hut-00.dig", "hut-000.dil", "bad-000.dil" })
            Assert.True(Locks(nm) == 0 && res.Find(nm)!.HasInstance);
    }

    // docs/interiors.md 5.4: light names, lower-bound id search, states, time and the clamps.
    [Fact]
    public void TestInteriorLights()
    {
        Numbers.EnterScriptFpuMode();
        // Three lights: "lamp" (2 states, switch), "pulse" (3 states, end times 0, 0.25, 0.5), unnamed (offset -1).
        var names = "lamp\0pulse\0";
        var defs = new List<LightDef> { new(10, 0, 2, 0, 1.0f, 0), new(20, 5, 3, 2, 2.0f, 0), new(30, 0xffffffffu, 1, 5, 1.0f, 0) };
        var states = new List<StateDef>
        {
            new([0, 0, 0, 0], 0.0f),
            new([65280, 32768, 128, 65535], 0.5f),
            new([1, 2, 3, 4], 0.0f),
            new([5, 6, 7, 8], 0.25f),
            new([9, 10, 11, 12], 0.5f),
            new([0, 0, 0, 0], 0.0f),
        };
        var l = new InteriorLights();
        Assert.True(l.Parse([.. DilLightingFile(7, defs, states, names)]));
        Assert.Equal(3, l.Size());
        Assert.True(l.FindByName("lamp") == 10 && l.FindByName("pulse") == 20 && l.FindByName("Lamp") == -1);
        Assert.True(l.FindByName("") == -1 && l.FindByName("pul") == -1); // the unnamed light never matches
        // 0x0055c780: first light whose id is not below the argument; -1 past the end.
        Assert.True(l.FindIndex(5) == 0 && l.FindIndex(10) == 0 && l.FindIndex(11) == 1 && l.FindIndex(30) == 2);
        Assert.True(l.FindIndex(31) == -1);
        Assert.True(l.StateOf(10) == 0 && l.StateOf(99) == -1);
        // 0x00556898: the state clamps to 0..numStates, the time and colour come from that record.
        l.SetState(10, 1);
        Assert.True(l.StateOf(10) == 1 && l.GetRun(0).Time == 0.5f);
        Assert.True(l.GetRun(0).Color == ((((65535u + 0x80) >> 8) << 24) | (((65280u + 0x80) >> 8) << 16) | (((32768u + 0x80) >> 8) << 8) | ((128u + 0x80) >> 8)));
        l.SetState(10, 9);
        Assert.Equal(2, l.StateOf(10)); // numStates, not numStates - 1
        l.SetState(10, -4);
        Assert.Equal(0, l.StateOf(10));
        // 0x00556dd4: time clamps to 0..duration; the state is the count of later records with end time below it.
        l.SetTime(20, 0.3f);
        Assert.True(l.GetRun(1).Time == 0.3f && l.StateOf(20) == 1);
        l.SetTime(20, 5.0f);
        Assert.True(l.GetRun(1).Time == 2.0f && l.StateOf(20) == 2);
        l.SetTime(20, -1.0f);
        Assert.True(l.GetRun(1).Time == 0.0f && l.StateOf(20) == 0);
        l.SetTime(20, float.NaN);
        Assert.True(l.GetRun(1).Time == 0.0f && l.StateOf(20) == 0);
        l.SetTime(20, 0.25f); // not below: stays on state 0
        Assert.Equal(0, l.StateOf(20));
        l.SetTime(99, 1.0f); // unknown id: nothing changes
        Assert.Equal(0, l.StateOf(20));
        // Truncated file.
        var cut = DilLightingFile(7, defs, states, names);
        cut.RemoveRange(cut.Count - 20, 20);
        Assert.False(l.Parse([.. cut]));
    }
}
