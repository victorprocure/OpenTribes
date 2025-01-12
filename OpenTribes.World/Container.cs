// Port of src/world/container.hpp and src/world/container.cpp: container hierarchy (docs/interiors.md 6,
// docs/game-stream-ghosts.md 6.2). Container 7 (SimDefaultOpenContainer) holds a SimDefaultContainerDatabase: a vector
// in add order. Objects that are containers themselves (SimTerrain, InteriorShape with isContainer) have their own
// database and hold every object whose world box fits inside theirs. The C++ sim::SimObject pointers are `object`
// (reference identity), see Collision.cs.
using System.Runtime.CompilerServices;
using OpenTribes.Core;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.World;

// src/world/container.hpp enum ObjectType: object type bits (object +0x48).
public static class ObjectType
{
    public const uint kTerrainType = 0x2;
    public const uint kInteriorType = 0x4;
    public const uint kPlayerType = 0x80;
    public const uint kMarkerType = 0x100000;
    public const uint kItemType = 0x200000;
    public const uint kMoveableType = 0x400000;
    public const uint kStaticType = 0x800000;
    public const uint kMineType = 0x4000000;
    public const uint kTriggerType = 0x10000010;
    public const uint kVehicleType = 0x20000000;
}

// src/world/container.hpp class Container.
public sealed class Container
{
    // src/world/container.hpp Container::CastFn.
    public delegate bool CastFn(Vec3 s, Vec3 e, ref Vec3 hit, ref Vec3 normal);

    // Own database (+0x88) of a container object.
    public enum Database { None, Terrain, Interior }

    // src/world/container.hpp Container::Entry.
    public sealed class Entry
    {
        public object? Object;     // sim::SimObject*
        public uint Type;
        public Float6 Box;         // world AABB (+0x70..+0x84)
        public bool ClipSegment;   // unused: every line test gets the unclipped segment (collision.md 9 F4)
        public CastFn? Cast;
        // Material entry of the surface the last successful `cast` hit (record +0x20), null when it has none. Set by the
        // cast function of terrain and interior entries (std::shared_ptr<const Material*>).
        public StrongBox<Material?>? HitMaterial;
        public Database DatabaseKind = Database.None;
        public uint ContainerMask;  // +0x90: object types the database accepts
        public float VisibleDistance; // SimTerrain +0x2e0 (container vtable +0xac)
        // Interior database: collision geometry (instance +0xa0) and object transform.
        public InteriorGeometry? Geometry;
        public Mat Transform = new();
        // SimTerrain: surface data for collision and the container properties it gives the moving objects inside it
        // (docs/player-sim.md FC2: contGravity, contDrag).
        public Terrain? TerrainData;
        public Float3 Gravity;
        public float Drag;
        // Number of collision image queries (0x407) other code has made on the object: a ray whose segment crosses the
        // box (castRay step 3) and the candidate list of another mover (SimMovement_stepMove 0x00505a3c,
        // docs/vehicle-sim.md 15).
        public uint ImageQueries;

        public Entry()
        {
        }

        public Entry(object? obj, uint type, Float6 box, bool clipSegment, CastFn? cast)
        {
            Object = obj;
            Type = type;
            Box = box;
            ClipSegment = clipSegment;
            Cast = cast;
        }

        // The C++ struct copy.
        public Entry Clone() => (Entry)MemberwiseClone();
    }

    // src/world/container.hpp Container::ScopeQuery: scope traversal (docs/game-stream-ghosts.md 6.2). markable is rule
    // M (net flag 0x100 set, 0x40 clear); mark is markInScope. point and visibleDistance are camera record +0x04 and
    // +0x28.
    public sealed class ScopeQuery
    {
        public Vec3 Point;
        public float VisibleDistance;
        public Func<object?, bool> Markable = _ => false;
        public Action<object?> Mark = _ => { };
    }

    private enum Kind { None, Vector, Slots }

    // src/world/container.cpp Container::SweepEnd: sorted end list of the SimTerrain database (database +0x40, the x
    // ends). Each object has a min and a max end. The box query (0x004fc3fc) walks this list, so its result order is the
    // order of the objects' max ends.
    private struct SweepEnd
    {
        public Node NodeRef;
        public bool IsMax;
        public float Value;
    }

    // src/world/container.cpp Container::Node.
    private sealed class Node
    {
        public Entry E = new();
        public Node? Parent;             // +0x68
        public Kind NodeKind = Kind.None;
        public bool Terrain;
        public bool Interior;
        public readonly List<Node?> Items = new();       // vector order, or slots (null = empty slot)
        public readonly List<Node> Subs = new();         // terrain database: sub-containers (+0x14 list)
        public readonly List<SweepEnd> XEnds = new();    // terrain database: x end list (+0x40)
        // InteriorShapeDatabase (PVS, 00483548): one list per leaf number + 1 (0 for objects in solid leaves), newest
        // first; the object's list in its parent's database.
        public readonly List<List<Node>> Buckets = new();
        public int Bucket;
        public int Leaf = -1;              // leaf number stored with the entry (the update test compares it)
        public Mat Inverse = new();        // InteriorShape +0xc8
        public uint Tag = 0xffffffffu;     // +0x8c
        public uint VisitTag;              // InteriorShape +0x420 (ctor 0)
        // Link records (+0x408 count, +0x414 records): neighbour interior and the face bit of the link on this side.
        public struct Link
        {
            public Node NodeRef;
            public uint Bits;
        }
        public readonly List<Link> Links = new();
        public bool ViaLink;               // InteriorShape +0x418: 2 while scoped through a link record, else 0
        public ulong Seq;                  // add order (castRay tie rule)
        public bool HasDb() => NodeKind != Kind.None;
    }

    // src/world/container.cpp Container::Query.
    private sealed class Query
    {
        public object? Exclude;
        public uint Mask;
        public Float6 Box;
    }

    private readonly Node mRoot;
    private readonly List<Node> mNodes = new();
    private readonly Dictionary<object, Node> mByObject = new(ReferenceEqualityComparer.Instance);
    private Node? mNullObject; // the map entry of a null object pointer
    private ulong mSeq;
    private uint mTag; // DAT_006a8cec, post-incremented per query, starts at 0

    // src/world/container.cpp overlaps: SimContainer_boxOverlap (004f9ae8), inclusive.
    private static bool Overlaps(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        for (int i = 0; i < 3; ++i)
            if (a[i + 3] < b[i] || b[i + 3] < a[i])
                return false;
        return true;
    }

    // src/world/container.cpp inside: SimContainer_boxInside (004f9b3c), inner lies inside outer, inclusive.
    private static bool Inside(ReadOnlySpan<float> outer, ReadOnlySpan<float> inner)
    {
        for (int i = 0; i < 3; ++i)
            if (outer[i + 3] < inner[i + 3] || inner[i] < outer[i])
                return false;
        return true;
    }

    // src/world/container.cpp insideTerrainDb: SimTerrain database open-container test (004fd02c). The x/y comparisons
    // run on the positions of the box ends in sorted end lists; with distinct values they reduce to outer.min <
    // inner.min and inner.max <= outer.max. z is strict on both ends.
    private static bool InsideTerrainDb(ReadOnlySpan<float> outer, ReadOnlySpan<float> inner)
    {
        for (int i = 0; i < 2; ++i)
            if (!(outer[i] < inner[i]) || !(inner[i + 3] <= outer[i + 3]))
                return false;
        return outer[2] < inner[2] && inner[5] < outer[5];
    }

    // src/world/container.cpp clip: 0x004f97e0, clip [a, b] to the box with planes z-min, z-max, x-min, x-max, y-min,
    // y-max.
    private static bool Clip(ref Vec3 a, ref Vec3 b, ReadOnlySpan<float> box)
    {
        ReadOnlySpan<int> kOrder = stackalloc int[12] { 2, 0, 2, 1, 0, 0, 0, 1, 1, 0, 1, 1 };
        for (int k = 0; k < 6; ++k)
        {
            int axis = kOrder[2 * k];
            bool isMax = kOrder[2 * k + 1] != 0;
            float plane = box[axis + (isMax ? 3 : 0)];
            float pa = a[axis];
            float pb = b[axis];
            bool aOut = isMax ? pa > plane : pa < plane;
            bool bOut = isMax ? pb > plane : pb < plane;
            if (aOut && bOut)
                return false;
            if (pa == plane && pb == plane)
                return false; // lying in a clip plane
            if (aOut != bOut)
            {
                double t = Div(Sub(plane, pa), Sub(pb, pa));
                var m = new Vec3(Store(Fpu.Add(a.X, Mul(Sub(b.X, a.X), t))), Store(Fpu.Add(a.Y, Mul(Sub(b.Y, a.Y), t))),
                                 Store(Fpu.Add(a.Z, Mul(Sub(b.Z, a.Z), t))));
                m[axis] = plane;
                if (aOut)
                    a = m;
                else
                    b = m;
            }
        }
        return true;
    }

    // src/world/container.cpp absDiff: |a - b| as the original computes it: the 80-bit difference of two f32 values
    // stored as a double, then fabs.
    private static double AbsDiff(float a, float b) => Math.Abs(Sub(a, b));

    // src/world/container.cpp linkOverlap: overlap of the intervals [a1, a2] and [b1, b2] in the face test (00482028):
    // both ends within 0.001, or one interval reaching more than 1.0 into the other from below or above, or one strictly
    // inside the other.
    private static bool LinkOverlap(float a1, float a2, float b1, float b2)
    {
        double eps = 0.001f; // f32 0061dba0
        if (!(AbsDiff(a1, b1) > eps) && !(AbsDiff(a2, b2) > eps))
            return true;
        if (!(a1 > b1) && !(a2 > b2) && Fpu.Add(b1, 1.0f) < a2)
            return true;
        if (!(a2 < b2) && !(a1 < b1) && Sub(b2, 1.0f) > a1)
            return true;
        if (a1 > b1 && a2 < b2)
            return true;
        return a1 < b1 && a2 > b2;
    }

    // src/world/container.cpp insideHalfOpen: camera point inside the instance box: [min, max) on every axis (004807d8).
    private static bool InsideHalfOpen(Vec3 p, ReadOnlySpan<float> box)
    {
        for (int i = 0; i < 3; ++i)
            if (p[i] < box[i] || !(p[i] < box[i + 3]))
                return false;
        return true;
    }

    // src/world/container.cpp padInteriorBox: world box of an InteriorShape (0047e5a0): the transformed geometry box with
    // min - 3.0 and max + 3.0 on every axis (f32 0061db9c). Objects fit an interior database only inside this padded box.
    public static void PadInteriorBox(Span<float> box)
    {
        for (int i = 0; i < 3; ++i)
        {
            box[i] = Store(Sub(box[i], 3.0f));
            box[i + 3] = Store(Fpu.Add(box[i + 3], 3.0f));
        }
    }

    // src/world/container.cpp interiorLinkFace: face test of two interior boxes in one interior's space (00482028,
    // docs/interiors.md 3): the first face of `a` that lies within 0.001 of the opposite face of `b` while the boxes
    // overlap on the two other axes; 0 when none does.
    public static uint InteriorLinkFace(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        // Faces in test order: own face index into a, opposite face index into b, the two other axes, bit.
        ReadOnlySpan<int> kFaces = stackalloc int[30]
        {
            0, 3, 1, 2, 0x80, 3, 0, 1, 2, 0x10, 1, 4, 0, 2, 0x40,
            4, 1, 0, 2, 0x08, 2, 5, 0, 1, 0x20, 5, 2, 0, 1, 0x04,
        };
        for (int k = 0; k < 6; ++k)
        {
            int own = kFaces[5 * k], other = kFaces[5 * k + 1], u = kFaces[5 * k + 2], v = kFaces[5 * k + 3];
            if (!(AbsDiff(a[own], b[other]) < (double)0.001f))
                continue;
            if (LinkOverlap(a[u], a[u + 3], b[u], b[u + 3]) && LinkOverlap(a[v], a[v + 3], b[v], b[v + 3]))
                return (uint)kFaces[5 * k + 4];
        }
        return 0;
    }

    // src/world/container.cpp interiorNearFaces: faces of the instance box a point outside it lies within 3.0 of, on the
    // outer side (004807d8, same bits).
    public static uint InteriorNearFaces(Vec3 p, ReadOnlySpan<float> box)
    {
        ReadOnlySpan<uint> kBelow = stackalloc uint[3] { 0x80, 0x40, 0x20 };
        ReadOnlySpan<uint> kAbove = stackalloc uint[3] { 0x10, 0x08, 0x04 };
        uint bits = 0;
        for (int i = 0; i < 3; ++i)
            if (p[i] < box[i] && AbsDiff(p[i], box[i]) < 3.0f) // f32 0061db9c
                bits |= kBelow[i];
        for (int i = 0; i < 3; ++i)
            if (p[i] > box[i + 3] && AbsDiff(p[i], box[i + 3]) < 3.0f)
                bits |= kAbove[i];
        return bits;
    }

    // src/world/container.cpp sweepLower: 0x004fc918 / 0x004fca60, first position whose value is not below v.
    private static int SweepLower(List<SweepEnd> l, float v)
    {
        int lo = 0, hi = l.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (v <= l[mid].Value)
                hi = mid;
            else
                lo = mid + 1;
        }
        return lo;
    }

    // src/world/container.cpp sweepPos.
    private static int SweepPos(List<SweepEnd> l, Node n, bool isMax)
    {
        for (int i = 0; i < l.Count; ++i)
            if (l[i].NodeRef == n && l[i].IsMax == isMax)
                return i;
        return l.Count;
    }

    // src/world/container.cpp sweepMove: 0x004fc318, move the end at position p to target position t with a new value.
    // A target equal to p or p + 1 only rewrites the value.
    private static void SweepMove(List<SweepEnd> l, int p, int t, float value)
    {
        if (p >= l.Count)
            return;
        if (p == t || p + 1 == t)
        {
            SweepEnd x = l[p];
            x.Value = value;
            l[p] = x;
            return;
        }
        SweepEnd e = l[p];
        bool down = value <= e.Value;
        e.Value = value;
        if (down)
        {
            if (t > p)
                return;
            for (int i = p; i > t; --i)
                l[i] = l[i - 1];
            l[t] = e;
        }
        else
        {
            if (t == 0 || t - 1 < p || t - 1 >= l.Count)
                return;
            for (int i = p; i < t - 1; ++i)
                l[i] = l[i + 1];
            l[t - 1] = e;
        }
    }

    // src/world/container.cpp Container::sweepInsert: 0x004fc080, both ends go in front of the first end with a value not
    // below theirs.
    private static void SweepInsert(Node c, Node n)
    {
        List<SweepEnd> l = c.XEnds;
        int lo = SweepLower(l, n.E.Box[0]), hi = SweepLower(l, n.E.Box[3]);
        l.Insert(hi, new SweepEnd { NodeRef = n, IsMax = true, Value = n.E.Box[3] });
        l.Insert(lo, new SweepEnd { NodeRef = n, IsMax = false, Value = n.E.Box[0] });
    }

    // src/world/container.cpp Container::sweepRemove.
    private static void SweepRemove(Node c, Node n) => c.XEnds.RemoveAll(e => e.NodeRef == n);

    // src/world/container.cpp Container::sweepUpdate: 0x004fce3c -> 0x004fc2bc, the targets are found in the list as it
    // is (the object's own ends included); the min end moves first.
    private static void SweepUpdate(Node c, Node n)
    {
        List<SweepEnd> l = c.XEnds;
        int lo = SweepLower(l, n.E.Box[0]), hi = SweepLower(l, n.E.Box[3]);
        int pMin = SweepPos(l, n, false);
        if (pMin >= l.Count)
            return;
        bool before = pMin < hi;
        SweepMove(l, pMin, lo, n.E.Box[0]);
        SweepMove(l, SweepPos(l, n, true), before ? hi : hi + 1, n.E.Box[3]);
    }

    // src/world/container.cpp Container::Container: container 7 (004fb754), box +-1e30, every type, default database.
    public Container()
    {
        mRoot = new Node();
        mRoot.E = new Entry(null, 0, Float6.Of(-1e30f, -1e30f, -1e30f, 1e30f, 1e30f, 1e30f), false, null);
        mRoot.E.ContainerMask = 0xffffffffu;
        mRoot.NodeKind = Kind.Vector;
    }

    // src/world/container.cpp Container::nodeOf.
    private Node? NodeOf(object? obj)
    {
        if (obj == null)
            return mNullObject;
        return mByObject.TryGetValue(obj, out Node? n) ? n : null;
    }

    // src/world/container.cpp Container::find.
    public Entry? Find(object? obj) => NodeOf(obj)?.E;

    // src/world/container.cpp Container::setInverse: inverse transform (005054a4), transposed rotation, translation -p
    // rotated by it.
    private static void SetInverse(Node node)
    {
        Mat m = node.E.Transform;
        var inv = new Mat();
        ReadOnlySpan<int> t = stackalloc int[9] { 0, 3, 6, 1, 4, 7, 2, 5, 8 };
        for (int i = 0; i < 9; ++i)
            inv.M[i] = m.M[t[i]];
        Mat rot = inv;
        rot.P = new Vec3(0, 0, 0);
        inv.P = rot.ToWorldVector(new Vec3(-m.P.X, -m.P.Y, -m.P.Z));
        node.Inverse = inv;
    }

    // src/world/container.cpp Container::setTransform: a moved InteriorShape (SimObject_setTransform ->
    // InteriorShape_worldBox 0047e5a0 -> SimContainer_setBox). An interior with an instance and a database that is in a
    // container unlinks first (00481214), takes the new transform, and links again (0048125c) after the box is computed;
    // the box update below refiles it (004f9dc4). Other objects: same as setBox.
    public void SetTransform(object? obj, Mat m, ReadOnlySpan<float> box)
    {
        Node? n = NodeOf(obj);
        if (n == null || !n.Interior || n.E.Geometry == null)
        {
            SetBox(obj, box);
            return;
        }
        UnlinkAll(n);
        n.E.Transform = m;
        SetInverse(n);
        SetBox(obj, box);
        LinkNeighbours(n);
    }

    // src/world/container.cpp Container::add: SimContainer::addObject on container 7 (004f9c04): the object descends into
    // the first open sub-container holding its box; a new container pulls in the objects of the database it lands in
    // that it contains.
    public void Add(Entry e)
    {
        var node = new Node();
        node.E = e.Clone();
        if (node.E.DatabaseKind != Database.None)
        {
            node.Terrain = node.E.DatabaseKind == Database.Terrain;
            node.Interior = node.E.DatabaseKind == Database.Interior;
            node.NodeKind = node.Terrain ? Kind.Slots : Kind.Vector;
        }
        if (node.Interior && node.E.Geometry != null)
        {
            SetInverse(node);
            int count = node.E.Geometry.EmptyCount() + (int)node.E.Geometry.SolidCount() + 1;
            while (node.Buckets.Count < count)
                node.Buckets.Add(new List<Node>());
        }
        Node n = node;
        n.Seq = ++mSeq;
        if (n.E.Object == null)
            mNullObject = n;
        else
            mByObject[n.E.Object] = n;
        mNodes.Add(node);
        AddTo(mRoot, n);
        // InteriorShape_onAdd (0047f2bc): after the container add, unlinkAll and linkNeighbours.
        if (n.Interior && n.E.Geometry != null)
            LinkNeighbours(n);
    }

    // src/world/container.cpp boxIn: instance box of `of` in the space of `in` (00481e74): the two corners through the
    // world transform of `of` and the inverse of `in`, then sorted per axis.
    private static void BoxIn(ReadOnlySpan<float> box, in Mat toWorld, in Mat toLocal, Span<float> output)
    {
        Vec3 lo = toLocal.ToWorld(toWorld.ToWorld(new Vec3(box[0], box[1], box[2])));
        Vec3 hi = toLocal.ToWorld(toWorld.ToWorld(new Vec3(box[3], box[4], box[5])));
        for (int i = 0; i < 3; ++i)
        {
            output[i] = hi[i] < lo[i] ? hi[i] : lo[i];         // std::min
            output[i + 3] = lo[i] < hi[i] ? hi[i] : lo[i];     // std::max
        }
    }

    // src/world/container.cpp Container::linkNeighbours: InteriorShape_linkNeighbours (0048125c), a container 7 box query
    // (interior type, the world box grown by 1.0) for other interiors with a database; a face found from this side and
    // allowed by this .dis link mask is offered to the neighbour (InteriorShape_addLink 0048111c), which runs the face
    // test from its side with its own mask. Both get a record only when both sides pass.
    private void LinkNeighbours(Node n)
    {
        UnlinkAll(n);
        var q = new Query { Exclude = n.E.Object, Mask = ObjectType.kInteriorType };
        for (int i = 0; i < 3; ++i)
        {
            q.Box[i] = Store(Sub(n.E.Box[i], 1.0f));
            q.Box[i + 3] = Store(Fpu.Add(n.E.Box[i + 3], 1.0f));
        }
        var found = new List<Node>();
        FindIn(mRoot, q, found);
        InteriorGeometry g = n.E.Geometry!;
        Span<float> b = stackalloc float[6];
        foreach (Node m in found)
        {
            if (m == n || !m.Interior || m.E.Geometry == null || (m.E.Type & ObjectType.kInteriorType) == 0)
                continue;
            BoxIn(m.E.Geometry.Box, m.E.Transform, n.Inverse, b);
            uint bits = InteriorLinkFace(g.Box, b);
            if ((bits & g.LinkableFaces) == 0)
                continue;
            BoxIn(g.Box, n.E.Transform, m.Inverse, b);
            uint back = InteriorLinkFace(m.E.Geometry.Box, b);
            if ((back & m.E.Geometry.LinkableFaces) == 0)
                continue;
            m.Links.Add(new Node.Link { NodeRef = n, Bits = back });
            n.Links.Add(new Node.Link { NodeRef = m, Bits = bits });
        }
    }

    // src/world/container.cpp Container::unlinkAll: InteriorShape_unlinkAll (00481214), every neighbour drops its record
    // of this one (InteriorShape_removeLink 00481098: the first match, erased in place).
    private static void UnlinkAll(Node n)
    {
        foreach (Node.Link l in n.Links)
        {
            List<Node.Link> v = l.NodeRef.Links;
            int it = v.FindIndex(x => x.NodeRef == n);
            if (it >= 0)
                v.RemoveAt(it);
        }
        n.Links.Clear();
    }

    // src/world/container.cpp Container::remove.
    public void Remove(object? obj)
    {
        Node? n = NodeOf(obj);
        if (n == null)
            return;
        Node parent = n.Parent ?? mRoot;
        UnlinkAll(n);
        RemoveFrom(n);
        // The objects it held go to its former container (database destructors 004fbf78, 00483bc0, called by 004f99e0
        // with the container +0x68).
        var held = new List<Node>();
        foreach (Node? m in n.Items)
            if (m != null)
                held.Add(m);
        foreach (Node m in held)
        {
            m.Parent = null;
            AddTo(parent, m);
        }
        if (obj == null)
            mNullObject = null;
        else
            mByObject.Remove(obj);
        mNodes.Remove(n);
    }

    // src/world/container.cpp Container::findOpen: findOpenContainer (+0x18), the first open sub-container whose mask
    // accepts the type and whose box holds the object's box.
    private static Node? FindOpen(Node c, Node n)
    {
        if (c.Terrain)
        {
            foreach (Node s in c.Subs)
                if (s != n && (s.E.ContainerMask & n.E.Type) != 0 && InsideTerrainDb(s.E.Box, n.E.Box))
                    return s;
            return null;
        }
        foreach (Node? s in c.Items)
            if (s != null && s != n && s.HasDb() && (s.E.ContainerMask & n.E.Type) != 0 && Inside(s.E.Box, n.E.Box))
                return s;
        return null;
    }

    // src/world/container.cpp Container::findContained: findContained (+0x14) with the query of 004f9c04: every other
    // object whose type the new container accepts and whose box lies inside its box.
    private static List<Node> FindContained(Node c, Node n)
    {
        var output = new List<Node>();
        if (c.Terrain)
        {
            // 004fd1a8: x/y ends inside [min, max) of the container box, collected in the order of the objects' y-max
            // ends; then z (inclusive) failures are swap-removed.
            foreach (Node? m in c.Items)
                if (m != null && m != n && m.E.Box[0] >= n.E.Box[0] && m.E.Box[3] < n.E.Box[3] &&
                    m.E.Box[1] >= n.E.Box[1] && m.E.Box[4] < n.E.Box[4])
                    output.Add(m);
            output = output.OrderBy(x => x.E.Box[4]).ToList(); // std::stable_sort
            for (int i = 0; i < output.Count; ++i)
            {
                Node m = output[i];
                if (m.E.Box[2] < n.E.Box[2] || n.E.Box[5] < m.E.Box[5])
                {
                    output[i] = output[^1];
                    output.RemoveAt(output.Count - 1);
                    --i;
                }
            }
            return output;
        }
        foreach (Node? m in c.Items)
            if (m != null && m != n && (m.E.Type & n.E.ContainerMask) != 0 && Inside(n.E.Box, m.E.Box))
                output.Add(m);
        return output;
    }

    // src/world/container.cpp Container::leafOf: 00483738, the leaf number of the object's box centre in interior space.
    private static int LeafOf(Node c, Node n)
    {
        if (c.E.Geometry == null)
            return -1;
        var center = new Vec3();
        for (int i = 0; i < 3; ++i)
        {
            float s = Store(Fpu.Add(n.E.Box[i], n.E.Box[i + 3]));
            center[i] = Store(Mul(s, 0.5f));
        }
        return c.E.Geometry.LeafAt(c.Inverse.ToWorld(center));
    }

    // src/world/container.cpp Container::bucketInsert: 004837fc, solid leaves share list 0; new entries go to the head of
    // the list. The entry keeps the leaf number, or -1 for a solid leaf, so the update test (00483b74) finds a solid-leaf
    // entry changed on every update.
    private static void BucketInsert(Node c, Node n)
    {
        int leaf = LeafOf(c, n);
        n.Leaf = -1;
        n.Bucket = 0;
        if (c.E.Geometry != null && !c.E.Geometry.IsSolid(leaf) && (long)(uint)leaf + 1 < c.Buckets.Count)
        {
            n.Leaf = leaf;
            n.Bucket = leaf + 1;
        }
        if (c.Buckets.Count == 0)
            c.Buckets.Add(new List<Node>());
        c.Buckets[n.Bucket].Insert(0, n);
    }

    // src/world/container.cpp Container::bucketRemove.
    private static void BucketRemove(Node c, Node n)
    {
        if (n.Bucket >= c.Buckets.Count)
            return;
        c.Buckets[n.Bucket].RemoveAll(x => x == n);
    }

    // src/world/container.cpp Container::dbAdd.
    private static void DbAdd(Node c, Node n)
    {
        n.Parent = c;
        if (c.Interior)
        {
            c.Items.Add(n);
            BucketInsert(c, n);
            return;
        }
        if (c.Terrain)
        {
            // ContainerDatabase_allocSlot (004fbe00): lowest empty slot, else append.
            int slot = c.Items.IndexOf(null);
            if (slot >= 0)
                c.Items[slot] = n;
            else
                c.Items.Add(n);
            if (n.HasDb() && n.E.ContainerMask != 0)
                c.Subs.Add(n);
            SweepInsert(c, n);
            return;
        }
        c.Items.Add(n);
    }

    // src/world/container.cpp Container::removeFrom.
    private static void RemoveFrom(Node n)
    {
        Node? c = n.Parent;
        if (c == null)
            return;
        if (c.Terrain)
        {
            // ContainerDatabase_freeSlot (004fbeec): the slot empties; the sub-container list fills the hole with its
            // last element.
            for (int i = 0; i < c.Items.Count; ++i)
                if (c.Items[i] == n)
                    c.Items[i] = null;
            int it = c.Subs.IndexOf(n);
            if (it >= 0)
            {
                c.Subs[it] = c.Subs[^1];
                c.Subs.RemoveAt(c.Subs.Count - 1);
            }
            SweepRemove(c, n);
        }
        else
        {
            c.Items.RemoveAll(x => x == n);
            if (c.Interior)
                BucketRemove(c, n);
        }
        n.Parent = null;
    }

    // src/world/container.cpp Container::addTo.
    private static void AddTo(Node c, Node n)
    {
        if (n.Parent != null)
            RemoveFrom(n);
        Node? s = FindOpen(c, n);
        if (s != null)
        {
            AddTo(s, n);
            return;
        }
        DbAdd(c, n);
        if (n.HasDb())
            foreach (Node m in FindContained(c, n))
                if ((m.E.Type & n.E.ContainerMask) != 0)
                    AddTo(n, m);
    }

    // src/world/container.cpp Container::setBox: new world box (0x004f9a38 -> SimContainer::updateObject 004f9dc4).
    public void SetBox(object? obj, ReadOnlySpan<float> box)
    {
        Node? n = NodeOf(obj);
        if (n == null)
            return;
        n.E.Box = Float6.Of(box);
        Node? c = n.Parent;
        if (c == null)
            return;
        if (Inside(c.E.Box, n.E.Box))
        {
            // Database update (+0xc). Interior (00483b74): re-file under the new leaf. Others: move into a matching open
            // sub-container, if any (004fb930, 004fce3c).
            if (c.Interior)
            {
                // 00483b74: only a changed leaf re-files the entry (at the head of its new list).
                if (LeafOf(c, n) != n.Leaf)
                {
                    BucketRemove(c, n);
                    BucketInsert(c, n);
                }
            }
            else if (FindOpen(c, n) is Node s)
            {
                AddTo(s, n);
            }
            else if (c.Terrain)
            {
                SweepUpdate(c, n);
            }
            return;
        }
        Node? p = c.Parent;
        while (p != null && !Inside(p.E.Box, n.E.Box))
            p = p.Parent;
        AddTo(p ?? mRoot, n);
    }

    // src/world/container.cpp Container::parentOf: the container object directly holding obj; null for container 7 or no
    // entry.
    public object? ParentOf(object? obj)
    {
        Node? n = NodeOf(obj);
        return n != null && n.Parent != null ? n.Parent.E.Object : null;
    }

    // src/world/container.cpp Container::contents: objects held directly by obj's database (container 7 for null) in
    // database order: vector order, or terrain slot order with empty slots skipped.
    public List<object?> Contents(object? obj)
    {
        Node? c = obj != null ? NodeOf(obj) : mRoot;
        var output = new List<object?>();
        if (c != null)
            foreach (Node? m in c.Items)
                if (m != null)
                    output.Add(m.E.Object);
        return output;
    }

    // src/world/container.cpp Container::terrainOf: terrain container holding obj (directly or through nested
    // containers), or null.
    public Entry? TerrainOf(object? obj)
    {
        Node? n = NodeOf(obj);
        for (Node? c = n?.Parent; c != null; c = c.Parent)
            if (c.Terrain)
                return c.E;
        return null;
    }

    // src/world/container.cpp Container::visibleDistance: visible distance reported by obj's container (vtable +0xac):
    // SimTerrain's value, else the parent's, 1e6 at container 7 (004f9988). false when obj has no entry.
    public bool VisibleDistance(object? obj, ref float output)
    {
        Node? n = NodeOf(obj);
        if (n == null || n.Parent == null)
            return false;
        for (Node? c = n.Parent; c != null; c = c.Parent)
            if (c.Terrain)
            {
                output = c.E.VisibleDistance;
                return true;
            }
        output = 1e6f;
        return true;
    }

    // src/world/container.cpp Container::findIn: SimContainer_findObjects (004f9f3c).
    private static void FindIn(Node c, Query q, List<Node> output)
    {
        if (!c.HasDb() || (c.E.ContainerMask & q.Mask) == 0)
            return;
        int first = output.Count;
        // Database findObjects (+0x1c): sub-containers by box overlap, other objects by id, type and overlap.
        if (c.Terrain)
        {
            // SimTerrain database (0x004fcf7c -> 0x004fc3fc): the objects in the order of their x-max ends. The tests run
            // on end positions: min < q.max and q.min <= max in x and y; in z the object is rejected when q.max <= min or
            // max < q.min.
            foreach (SweepEnd end in c.XEnds)
            {
                if (!end.IsMax)
                    continue;
                Node m = end.NodeRef;
                ref Float6 b = ref m.E.Box;
                if (!m.HasDb() && (ReferenceEquals(m.E.Object, q.Exclude) || (m.E.Type & q.Mask) == 0))
                    continue;
                if (!(b[0] < q.Box[3]) || !(q.Box[0] <= b[3]) || !(b[1] < q.Box[4]) || !(q.Box[1] <= b[4]))
                    continue;
                if (q.Box[5] <= b[2] || b[5] < q.Box[2])
                    continue;
                output.Add(m);
            }
        }
        else
        {
            foreach (Node? m in c.Items)
                if (m != null && (m.HasDb() ? Overlaps(m.E.Box, q.Box)
                                            : !ReferenceEquals(m.E.Object, q.Exclude) && (m.E.Type & q.Mask) != 0 && Overlaps(m.E.Box, q.Box)))
                    output.Add(m);
        }
        int endIndex = output.Count;
        for (int i = first; i < endIndex; ++i)
        {
            Node m = output[i];
            if (!m.HasDb())
                continue;
            if (ReferenceEquals(m.E.Object, q.Exclude) || (q.Mask & m.E.Type) == 0)
            {
                output[i] = output[endIndex - 1];
                output[endIndex - 1] = output[^1];
                output.RemoveAt(output.Count - 1);
                --i;
                --endIndex;
            }
            FindIn(m, q, output);
        }
    }

    // src/world/container.cpp Container::findObjects: findObjects (docs/interiors.md 6.2) from container 7: database
    // order, results of sub-containers appended.
    public List<object?> FindObjects(ReadOnlySpan<float> box, uint mask, object? exclude)
    {
        var q = new Query { Exclude = exclude, Mask = mask, Box = Float6.Of(box) };
        var nodes = new List<Node>();
        FindIn(mRoot, q, nodes);
        var output = new List<object?>();
        foreach (Node n in nodes)
            output.Add(n.E.Object);
        return output;
    }

    // src/world/container.cpp Container::castRay: castRay (6.3). Returns the nearest hit; the most recently added wins
    // exact ties.
    public bool CastRay(Vec3 s, Vec3 eIn, uint mask, object? exclude, ref object? obj, ref Vec3 hit, ref Vec3 normal)
    {
        Vec3 before = default;
        return CastRay(s, eIn, mask, exclude, ref obj, ref hit, ref normal, ref before, false);
    }

    // `before`, when given, receives the end of the segment as the winning candidate saw it (the hit of the nearest
    // candidate tested ahead of it, or `e`).
    public bool CastRay(Vec3 s, Vec3 eIn, uint mask, object? exclude, ref object? obj, ref Vec3 hit, ref Vec3 normal,
                        ref Vec3 before, bool wantBefore = true)
    {
        var q = new Query
        {
            Exclude = exclude,
            Mask = mask,
            Box = Float6.Of(eIn.X < s.X ? eIn.X : s.X, eIn.Y < s.Y ? eIn.Y : s.Y, eIn.Z < s.Z ? eIn.Z : s.Z,
                            s.X < eIn.X ? eIn.X : s.X, s.Y < eIn.Y ? eIn.Y : s.Y, s.Z < eIn.Z ? eIn.Z : s.Z),
        };
        var found = new List<Node>();
        FindIn(mRoot, q, found);
        // Candidates are tested in findObjects order (docs/interiors.md 6.3): container 7 in vector order, then the
        // SimTerrain database in the order of the x-max ends, then the contents of nested containers. Every hit shortens
        // the segment for the candidates after it, and the nearer test is strict, so the order decides exact ties and the
        // segment a later candidate sees. The SimTerrain is in the list of container 7 and therefore ahead of everything
        // its own database appends (docs/collision.md 9, candidate order).
        var candidates = new List<Node>();
        candidates.AddRange(found.Where(a => a.Terrain)); // std::stable_partition
        candidates.AddRange(found.Where(a => !a.Terrain));
        float best = 1e30f;
        Vec3 e = eIn;
        obj = null;
        Span<float> w = stackalloc float[3];
        foreach (Node cn in candidates)
        {
            Entry c = cn.E;
            Vec3 ca = s, cb = e;
            if (!Clip(ref ca, ref cb, c.Box))
                continue;
            ++c.ImageQueries; // step 3: the images are asked for once the segment crosses the box
            if (c.Cast == null)
                continue;
            Vec3 h = default, n = default;
            if (!(c.ClipSegment ? c.Cast(ca, cb, ref h, ref n) : c.Cast(s, e, ref h, ref n)))
                continue;
            // 0x004fa4c0: start - point per component as f32, then m_Point3F_lenf.
            w[0] = X87.F32(Sub(s.X, h.X));
            w[1] = X87.F32(Sub(s.Y, h.Y));
            w[2] = X87.F32(Sub(s.Z, h.Z));
            float d = X87.Lenf(w);
            if (d < best)
            {
                best = d;
                if (wantBefore)
                    before = e;
                obj = c.Object;
                hit = h;
                normal = n;
                e = h;
            }
        }
        return obj != null;
    }

    // src/world/container.cpp Container::scope: container 7 scope (SimContainer::scope 004faae0) with a new query tag.
    public void Scope(ScopeQuery q)
    {
        var stack = new List<bool> { true }; // the initial entry is the start container
        ScopeContainer(mRoot, q, stack);
        ++mTag;
    }

    // src/world/container.cpp Container::startContainer: the first open sub-container of the database holding the camera
    // point (database +0x18 with container mask -1).
    private static Node? StartContainer(Node c, ScopeQuery q)
    {
        var point = new Node();
        point.E.Type = 0xffffffffu;
        point.E.Box = Float6.Of(q.Point.X, q.Point.Y, q.Point.Z, q.Point.X, q.Point.Y, q.Point.Z);
        return FindOpen(c, point);
    }

    // src/world/container.cpp Container::scopeNode.
    private bool ScopeNode(Node c, ScopeQuery q, List<bool> stack) =>
        c.Interior ? ScopeInterior(c, q, stack) : ScopeContainer(c, q, stack);

    // src/world/container.cpp Container::scopeContainer: SimContainer::scope (004faae0), container 7 and SimTerrain.
    private bool ScopeContainer(Node c, ScopeQuery q, List<bool> stack)
    {
        if (!c.HasDb())
            return true;
        c.Tag = mTag;
        if (stack[^1])
        {
            Node? s = StartContainer(c, q);
            if (s != null && s.Tag != mTag && !ScopeNode(s, q, stack))
                return false;
        }
        if (c.E.Object != null && q.Markable(c.E.Object))
            q.Mark(c.E.Object);
        stack.Add(false);
        Span<float> v = stackalloc float[3];
        for (int i = 0; i < c.Items.Count; ++i)
        {
            Node? n = c.Items[i];
            if (n == null)
                continue;
            if (c.Terrain)
            {
                // SimTerrainDatabase_scope (004fcc00): box centre within the visible distance.
                for (int k = 0; k < 3; ++k)
                    v[k] = Store(Sub(Mul(Fpu.Add(n.E.Box[k + 3], n.E.Box[k]), 0.5), q.Point[k]));
                double len = Sqrt(Fpu.Add(Fpu.Add(Mul(v[0], v[0]), Mul(v[1], v[1])), Mul(v[2], v[2])));
                if (!(len <= q.VisibleDistance))
                    continue;
            }
            ScopeEntry(n, q, stack);
        }
        stack.RemoveAt(stack.Count - 1);
        return true;
    }

    // src/world/container.cpp Container::scopeInterior: InteriorShape scope (00480648).
    private bool ScopeInterior(Node c, ScopeQuery q, List<bool> stack)
    {
        InteriorGeometry? g = c.E.Geometry;
        if (g == null)
            return false;
        // A linked interior is scoped only on the start container path or through a link record.
        bool start = stack[^1];
        if (g.LinkedInterior && !c.ViaLink && !start)
            return false;
        if (c.VisitTag == mTag)
            return false; // +0x420 starts at 0, so the very first query (tag 0) skips interiors
        c.VisitTag = mTag;
        if (!c.HasDb())
            return true;
        if (stack[^1])
        {
            Node? s = StartContainer(c, q);
            if (s != null && s.Tag != mTag && !ScopeNode(s, q, stack))
                return false;
        }
        if (c.E.Object != null && q.Markable(c.E.Object))
            q.Mark(c.E.Object);
        stack.Add(false);
        // PVS database scope (00484050): the lists of the leaves visible from the camera's leaf (leaf numbers above 42),
        // then list 0, then the outside leaves 0..42 when the camera leaf sees outside. A camera in a solid leaf scopes
        // nothing.
        int leaf = g.LeafAt(c.Inverse.ToWorld(q.Point));
        void ScopeList(int b)
        {
            if (b >= c.Buckets.Count)
                return;
            var list = new List<Node>(c.Buckets[b]); // scoping may re-file entries
            foreach (Node n in list)
                ScopeEntry(n, q, stack);
        }
        if (!g.IsSolid(leaf))
        {
            foreach (int v in g.VisibleLeaves(leaf))
                if (v > 42)
                    ScopeList(v + 1);
            ScopeList(0);
            if (g.SeesOutside(leaf))
                for (int i = 0; i < 43; ++i)
                    ScopeList(i + 1);
        }
        stack.RemoveAt(stack.Count - 1);
        return ScopeLinks(c, q, stack, start);
    }

    // src/world/container.cpp Container::scopeLinks: InteriorShape_scopeLinked (004807d8). The outside face bits of the
    // camera leaf (0055daa0), plus on the start container path with the camera outside the instance box the faces it is
    // within 3.0 of, select the link records whose neighbour is not yet visited; each is scoped with +0x418 = 2. A linked
    // interior walks its records only on the start path or when it was itself reached through a link, and returns the
    // neighbours' results alone. Any other interior returns whether its camera leaf sees outside, or-ed with the
    // neighbours' results when the camera is inside the instance box.
    private bool ScopeLinks(Node c, ScopeQuery q, List<bool> stack, bool start)
    {
        InteriorGeometry g = c.E.Geometry!;
        stack.Add(false);
        Vec3 p = c.Inverse.ToWorld(q.Point);
        uint faces = g.OutsideFaces(g.LeafAt(p));
        bool inBox = InsideHalfOpen(p, g.Box);
        bool linked = false;
        if (!g.LinkedInterior || c.ViaLink || start)
        {
            uint mask = faces;
            if (!inBox && start)
                mask |= InteriorNearFaces(p, g.Box);
            for (int i = 0; i < c.Links.Count; ++i)
            {
                Node m = c.Links[i].NodeRef;
                if ((c.Links[i].Bits & mask) == 0 || m.VisitTag == mTag)
                    continue;
                bool saved = m.ViaLink;
                m.ViaLink = true;
                linked |= ScopeNode(m, q, stack);
                m.ViaLink = saved;
            }
        }
        stack.RemoveAt(stack.Count - 1);
        if (g.LinkedInterior)
            return linked;
        return inBox ? (linked || faces != 0) : faces != 0;
    }

    // src/world/container.cpp Container::scopeEntry.
    private void ScopeEntry(Node n, ScopeQuery q, List<bool> stack)
    {
        if (!n.HasDb())
        {
            if (q.Markable(n.E.Object))
                q.Mark(n.E.Object);
        }
        else if (n.Tag != mTag)
        {
            ScopeNode(n, q, stack);
        }
    }
}
