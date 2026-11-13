// Port of tests/test_container.cpp: container placement, database order and scope order
// (docs/game-stream-ghosts.md 6.2). The C++ main enters the script FPU mode first; every test here starts in it.
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;

public class ContainerTests
{
    private sealed class Obj : SimObject
    {
        public string Label { get; }
        public Obj(string n) => Label = n;
        public override string ClassName() => "Obj";
    }

    private static Container.Entry Entry(Obj o, float x0, float y0, float z0, float x1, float y1, float z1,
                                         uint type = ObjectType.StaticType) =>
        new(o, type, Float6.Of(x0, y0, z0, x1, y1, z1), false, null);

    private static Container.Entry TerrainEntry(Obj o)
    {
        var e = Entry(o, 0, 0, 0, 100, 100, 50, ObjectType.TerrainType);
        e.DatabaseKind = Container.Database.Terrain;
        e.ContainerMask = 0xffffffffu;
        e.VisibleDistance = 600;
        return e;
    }

    private static string Labels(List<object?> v)
    {
        var s = "";
        foreach (var o in v)
            s += ((Obj)o!).Label;
        return s;
    }

    private static void Start() => Numbers.EnterScriptFpuMode();

    [Fact]
    public void Placement()
    {
        Start();
        var c = new Container();
        Obj t = new("T"), a = new("a"), b = new("b"), d = new("d"), high = new("h");
        c.Add(TerrainEntry(t));
        c.Add(Entry(a, 10, 10, 10, 12, 12, 12));
        c.Add(Entry(high, 20, 20, 40, 22, 22, 60)); // sticks out above the terrain box
        c.Add(Entry(b, 30, 30, 10, 32, 32, 12));
        Assert.True(c.ParentOf(a) == t && c.ParentOf(b) == t);
        Assert.Null(c.ParentOf(high));
        Assert.Equal("Th", Labels(c.Contents(null)));
        Assert.Equal("ab", Labels(c.Contents(t)));
        // A removed object leaves an empty slot; the next add takes the lowest one.
        c.Remove(a);
        c.Add(Entry(d, 50, 50, 10, 52, 52, 12));
        Assert.Equal("db", Labels(c.Contents(t)));
        // Moving out of the terrain box climbs to container 7 (appended there).
        float[] up = [50, 50, 45, 52, 52, 55];
        c.SetBox(d, up);
        Assert.Null(c.ParentOf(d));
        Assert.Equal("Thd", Labels(c.Contents(null)));
        Assert.Equal("b", Labels(c.Contents(t)));
        // ...and moving back descends into the terrain again.
        float[] down = [50, 50, 10, 52, 52, 12];
        c.SetBox(d, down);
        Assert.True(c.ParentOf(d) == t);
        float vis = 0;
        Assert.True(c.VisibleDistance(d, ref vis) && vis == 600);
        Assert.True(c.VisibleDistance(high, ref vis) && vis == 1e6f);
    }

    [Fact]
    public void PullContained()
    {
        Start();
        // A container added after its contents pulls them out of the terrain database in the order of their y-max ends.
        var c = new Container();
        Obj t = new("T"), p = new("p"), q = new("q"), r = new("r"), box = new("B");
        c.Add(TerrainEntry(t));
        c.Add(Entry(p, 10, 30, 10, 12, 32, 12));
        c.Add(Entry(q, 14, 10, 10, 16, 12, 12));
        c.Add(Entry(r, 80, 80, 10, 82, 82, 12)); // outside the new container
        var e = Entry(box, 5, 5, 5, 40, 40, 30, ObjectType.InteriorType);
        e.DatabaseKind = Container.Database.Interior;
        e.ContainerMask = 0xffffffffu;
        c.Add(e);
        Assert.True(c.ParentOf(box) == t);
        Assert.Equal("qp", Labels(c.Contents(box)));
        Assert.Equal("rB", Labels(c.Contents(t))); // B was appended, then p and q left slots 0, 1
    }

    [Fact]
    public void FindObjects()
    {
        Start();
        var c = new Container();
        Obj t = new("T"), a = new("a"), high = new("h");
        c.Add(TerrainEntry(t));
        c.Add(Entry(a, 10, 10, 10, 12, 12, 12));
        c.Add(Entry(high, 10, 10, 45, 12, 12, 60));
        float[] q = [0, 0, 0, 100, 100, 100];
        // Container 7 order (terrain removed by the type mask), then the terrain's contents.
        Assert.Equal("ha", Labels(c.FindObjects(q, ObjectType.StaticType, null)));
        Assert.Equal("Tha", Labels(c.FindObjects(q, ObjectType.StaticType | ObjectType.TerrainType, null)));
        Assert.Equal("h", Labels(c.FindObjects(q, ObjectType.StaticType, a)));
    }

    // SimTerrain database box query (0x004fc3fc, docs/interiors.md 6.2): result order is the order of the x-max ends; an
    // equal end value puts the newer object first; a moved object takes the place its new ends sort to. The value tests
    // are min < q.max and q.min <= max in x and y, and in z a reject when q.max <= min or max < q.min.
    [Fact]
    public void TerrainQueryOrder()
    {
        Start();
        var c = new Container();
        Obj t = new("T"), a = new("a"), b = new("b"), d = new("d"), e = new("e");
        c.Add(TerrainEntry(t));
        c.Add(Entry(a, 60, 10, 10, 70, 12, 12));
        c.Add(Entry(b, 10, 10, 10, 20, 12, 12));
        c.Add(Entry(d, 30, 10, 10, 40, 12, 12));
        c.Add(Entry(e, 35, 10, 10, 40, 12, 12)); // same x-max as d, added later
        Assert.Equal("abde", Labels(c.Contents(t))); // slot order is unchanged
        float[] all = [1, 1, 1, 99, 99, 49];
        Assert.Equal("beda", Labels(c.FindObjects(all, ObjectType.StaticType, null)));
        // x: an object starting exactly at q.max is left out, one ending exactly at q.min is in.
        float[] qx = [20, 1, 1, 30, 99, 49];
        Assert.Equal("b", Labels(c.FindObjects(qx, ObjectType.StaticType, null)));
        // z: q.max equal to the object's min rejects, q.min equal to its max does not.
        float[] below = [1, 1, 1, 99, 99, 10], above = [1, 1, 12, 99, 99, 49];
        Assert.Equal(0, Labels(c.FindObjects(below, ObjectType.StaticType, null)).Length);
        Assert.Equal("beda", Labels(c.FindObjects(above, ObjectType.StaticType, null)));
        // b moves past a: its max end now sorts last.
        float[] moved = [80, 10, 10, 90, 12, 12];
        c.SetBox(b, moved);
        Assert.Equal("edab", Labels(c.FindObjects(all, ObjectType.StaticType, null)));
        // Moving back down to the x-max of d and e: the end is placed in front of the equal ones.
        float[] back = [10, 10, 10, 40, 12, 12];
        c.SetBox(b, back);
        Assert.Equal("beda", Labels(c.FindObjects(all, ObjectType.StaticType, null)));
        c.Remove(e);
        Assert.Equal("bda", Labels(c.FindObjects(all, ObjectType.StaticType, null)));
    }

    // castRay tests the candidates in findObjects order and shortens the segment at every hit (docs/interiors.md 6.3).
    // The object with the lower x-max is tested first whatever the add order; with the strict nearer test it keeps an
    // exact tie.
    [Fact]
    public void RayCandidateOrder()
    {
        Start();
        var c = new Container();
        Obj t = new("T"), a = new("a"), b = new("b");
        c.Add(TerrainEntry(t));
        var seen = new List<string>();
        Container.CastFn Caster(string label, float at) => (s, e, ref hit, ref n) =>
        {
            seen.Add(label + (e.X == 90 ? ":full" : ":short"));
            hit = new Vec3(at, s.Y, s.Z);
            n = new Vec3(-1, 0, 0);
            return true;
        };
        Container.Entry ea = Entry(a, 20, 10, 10, 70, 12, 12), eb = Entry(b, 20, 10, 10, 60, 12, 12);
        ea.Cast = Caster("a", 30);
        eb.Cast = Caster("b", 30);
        c.Add(ea);
        c.Add(eb);
        object? obj = null;
        Vec3 hitp = default, nn = default;
        Assert.True(c.CastRay(new Vec3(5, 11, 11), new Vec3(90, 11, 11), ObjectType.StaticType, null, ref obj, ref hitp, ref nn));
        Assert.True(seen.Count == 2 && seen[0] == "b:full" && seen[1] == "a:short");
        Assert.True(obj == b && hitp.X == 30);
    }

    [Fact]
    public void ScopeOrder()
    {
        Start();
        var c = new Container();
        Obj t = new("T"), a = new("a"), far = new("f"), high = new("h"), b = new("b");
        c.Add(TerrainEntry(t));
        c.Add(Entry(a, 10, 10, 10, 12, 12, 12));
        c.Add(Entry(high, 10, 10, 45, 12, 12, 60));
        c.Add(Entry(far, 98, 98, 10, 99, 99, 12));
        c.Add(Entry(b, 20, 20, 10, 22, 22, 12));
        var marked = "";
        var q = new Container.ScopeQuery
        {
            Point = new Vec3(11, 11, 20),
            VisibleDistance = 50,
            Markable = o => ((Obj)o!).Label != "T",
            Mark = o => marked += ((Obj)o!).Label
        };
        // Camera inside the terrain box: the terrain database first (slot order, distance test drops f), then container 7
        // in vector order (h, no distance test).
        c.Scope(q);
        Assert.Equal("abh", marked);
        // Camera above the terrain box: container 7 in order, the terrain at its position.
        marked = "";
        q.Point = new Vec3(11, 11, 70);
        q.VisibleDistance = 1000;
        c.Scope(q);
        Assert.Equal("afbh", marked);
    }

    // Minimal ITRGeometry: box +-10, one node on the plane x = 0 with an empty leaf (44) in front and a solid leaf (43)
    // behind; every outside leaf sees leaf 44. outsideByte and insideByte, when not zero, are the first PVS byte (the
    // outside face bits, 0055449c) of the outside leaves and of leaf 44; leaf 44 has an empty PVS when insideByte is 0.
    private static InteriorGeometry? TestGeometry(byte outsideByte = 0, byte insideByte = 0)
    {
        var b = new List<byte>();
        void U32(uint v)
        {
            for (var i = 0; i < 4; ++i)
                b.Add((byte)(v >> (8 * i)));
        }
        void U16(ushort v)
        {
            b.Add((byte)v);
            b.Add((byte)(v >> 8));
        }
        void F32(float f) => U32(BitConverter.SingleToUInt32Bits(f));
        b.AddRange("PERS"u8.ToArray());
        U32(0);
        U16(11);
        b.AddRange("ITRGeometry"u8.ToArray());
        b.Add(0);
        U32(7);
        U32(1);
        F32(1);
        foreach (var f in new[] { -10f, -10f, -10f, 10f, 10f, 10f })
            F32(f);
        const uint nEmpty = 44; // 0..42 outside, 43 -> leaf 44
        // Five zero bytes, then bit 4 of byte 5: leaf 44. A non-zero first byte replaces the first zero byte.
        var pvs = new List<byte> { 0, 5, 0x08 };
        if (outsideByte != 0)
            pvs = [outsideByte, 0, 4, 0x08];
        var outsideSize = (uint)pvs.Count;
        if (insideByte != 0)
            pvs.AddRange([insideByte, 0, 4, 0x08]);
        foreach (var n in new uint[] { 0, 1, 1, nEmpty, (uint)pvs.Count, 0, 0, 0, 1 })
            U32(n);
        U16(0);                      // node: plane 0
        U16(unchecked((ushort)(short)-45)); // front: leaf 44
        U16(unchecked((ushort)(short)-44)); // back: leaf 43 (solid)
        U16(0);
        for (var i = 0; i < 12; ++i)
            b.Add(0); // solid leaf
        for (uint i = 0; i < nEmpty; ++i)
        {
            U16(i < 43 ? (ushort)(outsideSize << 1) : insideByte != 0 ? (ushort)(4 << 1) : (ushort)0);
            U16(0);
            U32(i < 43 ? 0 : outsideSize);
            for (var k = 0; k < 36; ++k)
                b.Add(0);
        }
        b.AddRange(pvs);
        foreach (var f in new[] { 1f, 0f, 0f, 0f })
            F32(f);
        U32(0);
        U32(0);
        return InteriorGeometry.Parse([.. b]);
    }

    [Fact]
    public void InteriorPvs()
    {
        Start();
        var geo = TestGeometry();
        Assert.NotNull(geo);
        Assert.True(geo!.LeafAt(new Vec3(5, 0, 0)) == 44 && geo.LeafAt(new Vec3(-5, 0, 0)) == 43 && geo.LeafAt(new Vec3(20, 20, 0)) == 10);
        Assert.True(geo.VisibleLeaves(10).SequenceEqual([44]));
        Assert.False(geo.SeesOutside(10));
        var c = new Container();
        Obj t = new("T"), box = new("B"), front = new("f"), behind = new("b"), s = new("s");
        c.Add(TerrainEntry(t));
        var e = Entry(box, 10, 10, 5, 30, 30, 25, ObjectType.InteriorType);
        e.DatabaseKind = Container.Database.Interior;
        e.ContainerMask = 0xffffffffu;
        e.Geometry = geo;
        e.Transform.P = new Vec3(20, 20, 15);
        c.Add(e);
        c.Add(Entry(front, 25, 20, 10, 27, 22, 12));  // local x 6: leaf 44
        c.Add(Entry(behind, 12, 20, 10, 14, 22, 12)); // local x -7: solid leaf, list 0
        c.Add(Entry(s, 60, 60, 10, 62, 62, 12));
        Assert.True(c.ParentOf(front) == box && c.ParentOf(behind) == box);
        var marked = "";
        var q = new Container.ScopeQuery
        {
            Point = new Vec3(80, 80, 20), // outside leaf 10 (x and y above the box)
            VisibleDistance = 1000,
            // Rule M: the terrain and the interior are scope-always (0x40), never marked here.
            Markable = o =>
                {
                    var l = ((Obj)o!).Label;
                    return l is not "T" and not "B";
                },
            Mark = o => marked += ((Obj)o!).Label
        };
        // InteriorShape +0x420 starts at 0: the first query (tag 0) skips every interior.
        c.Scope(q);
        Assert.Equal("s", marked);
        // Then the visible leaf's list, then list 0 (solid leaves).
        marked = "";
        c.Scope(q);
        Assert.Equal("fbs", marked);
        // 00483b74: a list runs newest first, and an update re-files an entry only when its leaf changed.
        var g = new Obj("g");
        c.Add(Entry(g, 25, 20, 10, 27, 22, 12));
        float[] movedSameLeaf = [25, 21, 10, 27, 23, 12];
        c.SetBox(front, movedSameLeaf);
        marked = "";
        c.Scope(q);
        Assert.Equal("gfbs", marked);
        float[] movedLeaf = [12, 20, 10, 14, 22, 12]; // solid leaf: list 0
        c.SetBox(g, movedLeaf);
        marked = "";
        c.Scope(q);
        Assert.Equal("fgbs", marked);
        // A solid-leaf entry stores leaf -1 (004837fc), so every update re-files it at the head of list 0.
        float[] stillSolid = [12, 20, 10, 14, 22, 12];
        c.SetBox(behind, stillSolid);
        marked = "";
        c.Scope(q);
        Assert.Equal("fbgs", marked);
    }

    // The render walk (004fa9c8 -> 004804bc) reaches the terrain only when the container interior holding the camera point accepts
    // the scope: a camera in a leaf that sees no outside gets no terrain (Broadside play-flag 15), one in a leaf with outside faces, or
    // outside every container interior, does.
    [Fact]
    public void RenderReaches()
    {
        Start();
        foreach ((var insideByte, var reaches) in new[] { ((byte)0, false), ((byte)0x80, true) })
        {
            var geo = TestGeometry(0, insideByte);
            Assert.NotNull(geo);
            var c = new Container();
            c.Add(TerrainEntry(new Obj("T")));
            var e = Entry(new Obj("B"), 10, 10, 5, 30, 30, 25, ObjectType.InteriorType);
            e.DatabaseKind = Container.Database.Interior;
            e.ContainerMask = 0xffffffffu;
            e.Geometry = geo;
            e.Transform.P = new Vec3(20, 20, 15);
            c.Add(e);
            Assert.True(c.RenderReaches(new Vec3(25, 20, 15)) == reaches); // local x 5: leaf 44
            Assert.True(c.RenderReaches(new Vec3(25, 20, 15)) == reaches); // repeatable
            Assert.True(c.RenderReaches(new Vec3(80, 80, 20)));            // outside the interior box
        }
    }

    // InteriorShape world box padding of 3.0 (0047e5a0): an object 2.5 m above the geometry box is inside the database
    // of the interior (docs/game-stream-ghosts.md 6.2; G16: Siege vehicle pad on the roof).
    [Fact]
    public void PaddedInteriorBox()
    {
        Start();
        float[] box = [20, 20, 20, 30, 30, 30];
        Container.PadInteriorBox(box);
        Assert.True(box[0] == 17 && box[2] == 17 && box[3] == 33 && box[5] == 33);
        var c = new Container();
        Obj terrain = new("t"), inside = new("i"), roof = new("r");
        c.Add(TerrainEntry(terrain));
        var e = Entry(inside, box[0], box[1], box[2], box[3], box[4], box[5], ObjectType.InteriorType);
        e.DatabaseKind = Container.Database.Interior;
        e.ContainerMask = 0xffffffffu;
        c.Add(e);
        c.Add(Entry(roof, 24, 24, 31, 26, 26, 32.5f));
        Assert.True(c.ParentOf(roof) == inside);
    }

    // Face test of two instance boxes (00482028, docs/interiors.md 3).
    [Fact]
    public void LinkFace()
    {
        Start();
        float[] a = [0, 0, 0, 10, 10, 10];
        uint Face(float x0, float y0, float z0, float x1, float y1, float z1)
        {
            float[] b = [x0, y0, z0, x1, y1, z1];
            return Container.InteriorLinkFace(a, b);
        }
        // Each face of a and its bit: x min 0x80, x max 0x10, y min 0x40, y max 0x08, z min 0x20, z max 0x04.
        Assert.Equal(0x80u, Face(-10, 0, 0, 0, 10, 10));
        Assert.Equal(0x10u, Face(10, 0, 0, 20, 10, 10));
        Assert.Equal(0x40u, Face(0, -10, 0, 10, 0, 10));
        Assert.Equal(0x08u, Face(0, 10, 0, 10, 20, 10));
        Assert.Equal(0x20u, Face(0, 0, -10, 10, 10, 0));
        Assert.Equal(0x04u, Face(0, 0, 10, 10, 10, 20));
        // The faces must lie within 0.001.
        Assert.Equal(0x10u, Face(10.0005f, 0, 0, 20, 10, 10));
        Assert.Equal(0u, Face(10.01f, 0, 0, 20, 10, 10));
        // Overlap on the other axes: equal ends within 0.001, more than 1.0 from one side, or strictly inside.
        Assert.Equal(0x10u, Face(10, 0.0005f, 0, 20, 9.9995f, 10));
        Assert.Equal(0x10u, Face(10, 8.9f, 0, 20, 20, 10)); // reaches 1.1 into a from above
        Assert.Equal(0u, Face(10, 9, 0, 20, 20, 10));       // exactly 1.0 is not enough
        Assert.Equal(0x10u, Face(10, -20, 0, 20, 1.1f, 10)); // 1.1 from below
        Assert.Equal(0u, Face(10, -20, 0, 20, 1, 10));
        Assert.Equal(0x10u, Face(10, 2, 2, 20, 8, 8));      // strictly inside a
        Assert.Equal(0x10u, Face(10, -5, -5, 20, 15, 15));  // a strictly inside b
        Assert.Equal(0u, Face(10, 20, 0, 30, 30, 10));      // apart in y
        // The first face in test order wins: x min before x max for a degenerate box.
        float[] flat = [0, 0, 0, 0, 10, 10];
        float[] bb = [0, 0, 0, 0, 10, 10];
        Assert.Equal(0x80u, Container.InteriorLinkFace(flat, bb));
    }

    // Faces near a camera point outside the instance box (004807d8): within 3.0 on the outer side.
    [Fact]
    public void NearFaces()
    {
        Start();
        float[] box = [-10, -10, -10, 10, 10, 10];
        Assert.Equal(0x80u, Container.InteriorNearFaces(new Vec3(-12, 0, 0), box));
        Assert.Equal(0u, Container.InteriorNearFaces(new Vec3(-13, 0, 0), box));
        Assert.True(Container.InteriorNearFaces(new Vec3(11, 12.5f, 0), box) == (0x10 | 0x08));
        Assert.True(Container.InteriorNearFaces(new Vec3(0, -11, 10.5f), box) == (0x40 | 0x04));
        Assert.Equal(0x20u, Container.InteriorNearFaces(new Vec3(0, 0, -12.9f), box));
        Assert.Equal(0u, Container.InteriorNearFaces(new Vec3(0, 0, 0), box));
    }

    // Scope through link records (00480648, 004807d8; docs/game-stream-ghosts.md 6.2): interiors A at x 20..40 and B at
    // x 40..60 touch at x = 40 (A side 0x10, B side 0x80; B turned by 180 degrees about z: 0x10 too). Kits a in A
    // (local x 4), b in B (local x 4), o outside both.
    private sealed class LinkFixture
    {
        public Container C { get; } = new();
        public Obj T { get; } = new("T");
        public Obj Ia { get; } = new("A");
        public Obj Ib { get; } = new("B");
        public Obj A { get; } = new("a");
        public Obj B { get; } = new("b");
        public Obj O { get; } = new("o");
        public string Marked { get; set; } = "";
        public Container.ScopeQuery Q { get; } = new();

        public LinkFixture(InteriorGeometry ga, InteriorGeometry gb, bool turnB = false)
        {
            C.Add(TerrainEntry(T));
            void Interior(Obj obj, InteriorGeometry g, float cx, bool turn)
            {
                float[] box = [Fpu.SubF(cx, 10), 40, 15, Fpu.AddF(cx, 10), 60, 35];
                Container.PadInteriorBox(box);
                var e = Entry(obj, box[0], box[1], box[2], box[3], box[4], box[5], ObjectType.InteriorType);
                e.DatabaseKind = Container.Database.Interior;
                e.ContainerMask = 0xffffffffu;
                e.Geometry = g;
                e.Transform.P = new Vec3(cx, 50, 25);
                if (turn)
                    e.Transform.M[0] = e.Transform.M[4] = -1;
                C.Add(e);
            }
            Interior(Ia, ga, 30, false);
            Interior(Ib, gb, 50, turnB);
            C.Add(Entry(A, 33, 49, 24, 35, 51, 26));
            if (turnB)
                C.Add(Entry(B, 45, 49, 24, 47, 51, 26));
            else
                C.Add(Entry(B, 53, 49, 24, 55, 51, 26));
            C.Add(Entry(O, 80, 80, 10, 82, 82, 12));
            Assert.True(C.ParentOf(A) == Ia && C.ParentOf(B) == Ib && C.ParentOf(O) == T);
            Q.VisibleDistance = 1000;
            Q.Markable = x =>
            {
                var l = ((Obj)x!).Label;
                return l is "a" or "b" or "o";
            };
            Q.Mark = x => Marked += ((Obj)x!).Label;
            C.Scope(Q); // tag 0 skips every interior
        }

        public string Scope(Vec3 p)
        {
            Marked = "";
            Q.Point = p;
            C.Scope(Q);
            return Marked;
        }
    }

    // 004804bc / 0047f794: the image of an interior reached through a link record of a neighbour carries the link flag (+0x418 bit 2),
    // the one the camera's own interior and a plain container hit do not.
    [Fact]
    public void ViaLinkIsReportedOnlyForTheLinkedNeighbour()
    {
        Start();
        var g = TestGeometry(0, 0x10)!;
        g.LinkedInterior = true;
        var f = new LinkFixture(g, g);
        var via = new List<object>();
        f.Q.OnViaLink = via.Add;
        f.Scope(new Vec3(35, 50, 25));
        Assert.Equal(new object[] { f.Ib }, via);
        via.Clear();
        f.Scope(new Vec3(80, 80, 20)); // outside both: no link walked
        Assert.Empty(via);
    }

    [Fact]
    public void LinkScope()
    {
        Start();
        // Linked pair, camera leaf 44 of A with face bit 0x10: B is scoped through the link (camera in its outside leaf 1,
        // which sees leaf 44). A linked interior returns only its neighbours' results, B none, so the query stops at the
        // start container and o is not scoped.
        {
            var g = TestGeometry(0, 0x10)!;
            g.LinkedInterior = true;
            var f = new LinkFixture(g, g);
            Assert.Equal("ab", f.Scope(new Vec3(35, 50, 25)));
            // Camera outside both: a linked interior off the start path and not reached through a link is skipped.
            Assert.Equal("o", f.Scope(new Vec3(80, 80, 20)));
            // Camera in leaf 44 of B: its face bit 0x10 matches no record of B (its link is 0x80), A is not scoped.
            Assert.Equal("b", f.Scope(new Vec3(55, 50, 25)));
            // A link is removed with its interior.
            f.C.Remove(f.Ib);
            Assert.Equal("a", f.Scope(new Vec3(35, 50, 25)));
        }
        // Leaf 44 without face bits: no link is walked.
        {
            var g = TestGeometry(0, 0x08)!;
            g.LinkedInterior = true;
            var f = new LinkFixture(g, g);
            Assert.Equal("a", f.Scope(new Vec3(35, 50, 25)));
        }
        // Camera in A's padded box but outside its instance box (local x 10.5, outside leaf 2 with face bit 0x40): the
        // near face 0x10 selects the link. B is turned so that the camera is in its leaf 44.
        {
            var g = TestGeometry(0x40, 0x08)!;
            g.LinkedInterior = true;
            var f = new LinkFixture(g, g, true);
            Assert.Equal("ab", f.Scope(new Vec3(40.5f, 50, 25)));
            // From inside A's instance box the near faces do not apply, and leaf 44's 0x08 matches no record.
            Assert.Equal("a", f.Scope(new Vec3(35, 50, 25)));
        }
        // Not linked: the walk runs too, the result is "camera leaf sees outside" or-ed with the links inside the box, so
        // the query goes on to o; B was visited through the link and is not scoped again.
        {
            var g = TestGeometry(0, 0x10)!;
            var f = new LinkFixture(g, g);
            Assert.Equal("abo", f.Scope(new Vec3(35, 50, 25)));
            Assert.Equal("abo", f.Scope(new Vec3(80, 80, 20)));
        }
        // The .dis link mask of either side can refuse the link (A 0x10 or B 0x80 removed).
        foreach (var maskA in new uint[] { 0xec, 0xfc })
        {
            var ga = TestGeometry(0, 0x10)!;
            var gb = TestGeometry(0, 0x10)!;
            ga.LinkedInterior = gb.LinkedInterior = true;
            ga.LinkableFaces = maskA;
            gb.LinkableFaces = maskA == 0xfc ? 0x7cu : 0xfcu;
            var f = new LinkFixture(ga, gb);
            Assert.Equal("a", f.Scope(new Vec3(35, 50, 25)));
        }
    }

    // A moved interior (SimObject_setTransform -> InteriorShape_worldBox 0047e5a0 -> SimContainer_updateObject 004f9dc4;
    // docs/interiors.md 3, docs/game-stream-ghosts.md 6.10 Q7).
    [Fact]
    public void MovedInterior()
    {
        Start();
        var g = TestGeometry(0, 0x10)!;
        g.LinkedInterior = true;
        var f = new LinkFixture(g, g);
        void MoveB(float cx, float cy)
        {
            float[] box = [Fpu.SubF(cx, 10), Fpu.SubF(cy, 10), 15, Fpu.AddF(cx, 10), Fpu.AddF(cy, 10), 35];
            Container.PadInteriorBox(box);
            var m = new Mat
            {
                P = new Vec3(cx, cy, 25)
            };
            f.C.SetTransform(f.Ib, m, box);
        }
        Assert.Equal("ab", f.Scope(new Vec3(35, 50, 25)));
        // Still inside the terrain box: updated in place, B keeps its container and its contents.
        MoveB(52, 50);
        Assert.True(f.C.ParentOf(f.Ib) == f.T && f.C.ParentOf(f.B) == f.Ib);
        Assert.Equal(1, f.C.Contents(f.Ib).Count);
        // Links are rebuilt: the gap of 2 unlinks the pair, touching again relinks it.
        Assert.Equal("a", f.Scope(new Vec3(35, 50, 25)));
        MoveB(50, 50);
        Assert.Equal("ab", f.Scope(new Vec3(35, 50, 25)));
        // Leaving the terrain box re-adds it at container 7 (004f9c04): its contents stay in it, the links go, and an
        // object of container 7 inside the new box is pulled into it.
        var far = new Obj("f");
        f.C.Add(Entry(far, 105, 49, 24, 107, 51, 26));
        Assert.Null(f.C.ParentOf(far));
        MoveB(95, 50);
        Assert.True(f.C.ParentOf(f.Ib) == null && f.C.ParentOf(f.B) == f.Ib);
        Assert.True(f.C.ParentOf(far) == f.Ib);
        Assert.Equal("a", f.Scope(new Vec3(35, 50, 25)));
        Assert.True(f.C.ParentOf(f.B) == f.Ib);
    }

    // SimTerrain database findObjects (004fcf7c): the order of the max x ends, not the slot order. Among equal ends the
    // object whose box was set last comes first, also when the box is unchanged (SimContainer_setBox always runs the
    // database update).
    [Fact]
    public void SweepOrder()
    {
        Start();
        var c = new Container();
        Obj t = new("T"), a = new("a"), b = new("b"), d = new("d"), e = new("e");
        c.Add(TerrainEntry(t));
        c.Add(Entry(a, 30, 10, 10, 40, 12, 12));
        c.Add(Entry(b, 10, 10, 10, 20, 12, 12));
        c.Add(Entry(d, 25, 10, 10, 30, 12, 12));
        float[] all = [0, 0, 0, 100, 100, 50];
        Assert.Equal("abd", Labels(c.Contents(t)));
        Assert.Equal("bda", Labels(c.FindObjects(all, 0xffffffffu, t)));
        // The tests are half open: min < query max and max >= query min.
        float[] q1 = [20, 0, 0, 30, 100, 50]; // b ends at 20 (in), d inside, a starts at 30 (out)
        Assert.Equal("bd", Labels(c.FindObjects(q1, 0xffffffffu, t)));
        float[] q2 = [0, 12, 0, 100, 20, 50]; // y max of every box equals the query min: in
        Assert.Equal("bda", Labels(c.FindObjects(q2, 0xffffffffu, t)));
        float[] q3 = [0, 0, 12.5f, 100, 100, 50]; // above every box
        Assert.Equal(0, c.FindObjects(q3, 0xffffffffu, t).Count);
        // Equal max ends: the later add goes in front, then every update moves its object to the front.
        c.Add(Entry(e, 35, 10, 10, 40, 12, 12));
        Assert.Equal("bdea", Labels(c.FindObjects(all, 0xffffffffu, t)));
        float[] same = [30, 10, 10, 40, 12, 12];
        c.SetBox(a, same);
        Assert.Equal("bdae", Labels(c.FindObjects(all, 0xffffffffu, t)));
        float[] lower = [35, 10, 9.5f, 40, 12, 12]; // z only
        c.SetBox(e, lower);
        Assert.Equal("bdea", Labels(c.FindObjects(all, 0xffffffffu, t)));
        // A moved box takes its new place; a removed object leaves the list.
        float[] far = [1, 10, 10, 5, 12, 12];
        c.SetBox(a, far);
        Assert.Equal("abde", Labels(c.FindObjects(all, 0xffffffffu, t)));
        c.Remove(d);
        Assert.Equal("abe", Labels(c.FindObjects(all, 0xffffffffu, t)));
    }

    // Ray order inside an InteriorShape database: the entries are tested in add order, as for a box query
    // (docs/interiors.md IQ1, 6.3; `staticdiff` iq2_rayorder, iq2_raysolid), whatever leaf each one is filed in. With the
    // strict nearer test the first added of equal hits wins.
    [Fact]
    public void RayInteriorDatabase()
    {
        Start();
        var geo = TestGeometry();
        var c = new Container();
        Obj t = new("T"), box = new("B"), a = new("a"), b = new("b"), d = new("d");
        c.Add(TerrainEntry(t));
        var e = Entry(box, 10, 10, 5, 30, 30, 25, ObjectType.InteriorType);
        e.DatabaseKind = Container.Database.Interior;
        e.ContainerMask = 0xffffffffu;
        e.Geometry = geo;
        e.Transform.P = new Vec3(20, 20, 15);
        c.Add(e);
        var seen = "";
        void AddCast(Obj o, float x0, float x1)
        {
            var en = Entry(o, x0, 20, 10, x1, 22, 12);
            var label = o.Label;
            en.Cast = (s, _, ref hit, ref n) =>
            {
                seen += label;
                hit = new Vec3(28.9f, s.Y, s.Z);
                n = new Vec3(-1, 0, 0);
                return true;
            };
            c.Add(en);
        }
        AddCast(a, 10.5f, 29);  // box centre at local x -0.25: solid leaf
        AddCast(b, 25, 29.5f);  // local x 7: leaf 44
        AddCast(d, 25, 29.5f);
        Assert.True(c.ParentOf(a) == box && c.ParentOf(b) == box && c.ParentOf(d) == box);
        object? obj = null;
        Vec3 hitp = default, nn = default;
        Assert.True(c.CastRay(new Vec3(5, 21, 11), new Vec3(40, 21, 11), ObjectType.StaticType, null, ref obj, ref hitp, ref nn));
        Assert.True(seen == "abd" && obj == a);
        seen = "";
        c.Remove(a);
        Assert.True(c.CastRay(new Vec3(5, 21, 11), new Vec3(40, 21, 11), ObjectType.StaticType, null, ref obj, ref hitp, ref nn));
        Assert.True(seen == "bd" && obj == b);
    }
}
