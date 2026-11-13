namespace OpenTribes.World;

using System.Runtime.CompilerServices;
using OpenTribes.Core;
using static OpenTribes.Core.Fpu;

/// <summary>The object type bits (object +0x48), tested against container masks.</summary>
public static class ObjectType
{
    /// <summary>The terrain type bit.</summary>
    public const uint TerrainType = 0x2;
    /// <summary>The interior type bit.</summary>
    public const uint InteriorType = 0x4;
    /// <summary>The player type bit.</summary>
    public const uint PlayerType = 0x80;
    /// <summary>The marker type bit.</summary>
    public const uint MarkerType = 0x100000;
    /// <summary>The item type bit.</summary>
    public const uint ItemType = 0x200000;
    /// <summary>The moveable type bit.</summary>
    public const uint MoveableType = 0x400000;
    /// <summary>The static type bit.</summary>
    public const uint StaticType = 0x800000;
    /// <summary>The mine type bit.</summary>
    public const uint MineType = 0x4000000;
    /// <summary>The trigger type bits.</summary>
    public const uint TriggerType = 0x10000010;
    /// <summary>The vehicle type bit.</summary>
    public const uint VehicleType = 0x20000000;
}

/// <summary>The container hierarchy (docs/interiors.md 6, docs/game-stream-ghosts.md 6.2): finds objects by box, casts rays and walks the scope.</summary>
/// <remarks>Container 7 (SimDefaultOpenContainer) holds a SimDefaultContainerDatabase, a vector in add order. Objects that are containers themselves (SimTerrain, InteriorShape with isContainer) have their own database and hold every object whose world box fits inside theirs. Sim objects are <c>object</c> (reference identity), see <see cref="Collision"/>. Port of src/world/container.cpp.</remarks>
public sealed class Container
{
    /// <summary>Casts a line against one object, as the object's own line test.</summary>
    /// <param name="s">The segment start.</param>
    /// <param name="e">The segment end.</param>
    /// <param name="hit">Receives the hit point.</param>
    /// <param name="normal">Receives the surface normal.</param>
    /// <returns>True on a hit.</returns>
    public delegate bool CastFn(Vec3 s, Vec3 e, ref Vec3 hit, ref Vec3 normal);

    /// <summary>The kind of the container's own database (+0x88).</summary>
    public enum Database
    {
        /// <summary>No own database.</summary>
        None,
        /// <summary>A SimTerrain database.</summary>
        Terrain,
        /// <summary>An interior (PVS) database.</summary>
        Interior,
    }

    /// <summary>One registered object with its type, world box and line test.</summary>
    public sealed class Entry
    {
        /// <summary>The sim object.</summary>
        public object? Object { get; set; }
        /// <summary>The object type bits.</summary>
        public uint Type { get; set; }
        /// <summary>The world box (+0x70 to +0x84).</summary>
        public ref Float6 Box => ref _box;
        private Float6 _box;
        /// <summary>Unused: every line test gets the unclipped segment (docs/collision.md 9 F4).</summary>
        public bool ClipSegment { get; set; }
        /// <summary>The line test of the object, or null.</summary>
        public CastFn? Cast { get; set; }
        /// <summary>The material of the surface the last successful <see cref="Cast"/> hit (record +0x20), null when it has none. Set by the cast function of terrain and interior entries.</summary>
        public StrongBox<Material?>? HitMaterial { get; set; }
        /// <summary>The kind of the object's own database.</summary>
        public Database DatabaseKind { get; set; } = Database.None;
        /// <summary>The object types the database accepts (+0x90).</summary>
        public uint ContainerMask { get; set; }
        /// <summary>The visible distance of a SimTerrain (+0x2e0, container vtable +0xac).</summary>
        public float VisibleDistance { get; set; }
        /// <summary>The collision geometry of an interior database (instance +0xa0).</summary>
        public InteriorGeometry? Geometry { get; set; }
        /// <summary>The object transform of an interior database.</summary>
        public ref Mat Transform => ref _transform;
        private Mat _transform = new();
        /// <summary>The surface data of a SimTerrain, for collision.</summary>
        public Terrain? TerrainData { get; set; }
        /// <summary>The gravity the container gives the moving objects inside it (docs/player-sim.md FC2, contGravity).</summary>
        public ref Float3 Gravity => ref _gravity;
        private Float3 _gravity;
        /// <summary>The drag the container gives the moving objects inside it (docs/player-sim.md FC2, contDrag).</summary>
        public float Drag { get; set; }
        /// <summary>The number of collision image queries (0x407) other code has made on the object: a ray whose segment crosses the box (castRay step 3) and the candidate list of another mover (SimMovement_stepMove 00505a3c, docs/vehicle-sim.md 15).</summary>
        public uint ImageQueries { get; set; }

        /// <summary>Creates an empty entry.</summary>
        public Entry()
        {
        }

        /// <summary>Creates an entry.</summary>
        /// <param name="obj">The sim object.</param>
        /// <param name="type">The object type bits.</param>
        /// <param name="box">The world box.</param>
        /// <param name="clipSegment">Unused, see <see cref="ClipSegment"/>.</param>
        /// <param name="cast">The line test, or null.</param>
        public Entry(object? obj, uint type, Float6 box, bool clipSegment, CastFn? cast)
        {
            Object = obj;
            Type = type;
            Box = box;
            ClipSegment = clipSegment;
            Cast = cast;
        }

        /// <summary>Copies the entry the way a C++ struct copy does.</summary>
        /// <returns>A shallow copy.</returns>
        public Entry Clone() => (Entry)MemberwiseClone();
    }

    /// <summary>The input and callbacks of a scope traversal (docs/game-stream-ghosts.md 6.2).</summary>
    public sealed class ScopeQuery
    {
        /// <summary>The camera point (camera record +0x04).</summary>
        public ref Vec3 Point => ref _point;
        private Vec3 _point;
        /// <summary>The visible distance (camera record +0x28).</summary>
        public float VisibleDistance { get; set; }
        /// <summary>Tests whether an object may be marked: net flag 0x100 set and 0x40 clear (rule M).</summary>
        public Func<object?, bool> Markable { get; set; } = _ => false;
        /// <summary>Marks an object in scope (markInScope).</summary>
        public Action<object?> Mark { get; set; } = _ => { };
        /// <summary>Called with the interior object that the walk reaches through a link record (InteriorShape +0x418 = 2), where the stock walk builds its render image (0047f794 copies bit 2 to the image link flag, 004804bc).</summary>
        public Action<object>? OnViaLink { get; set; }
    }

    private enum Kind { None, Vector, Slots }

    // src/world/container.cpp Container::SweepEnd: sorted end list of the SimTerrain database (database +0x40, the x
    // ends). Each object has a min and a max end. The box query (0x004fc3fc) walks this list, so its result order is the
    // order of the objects' max ends.
    private struct SweepEnd
    {
        public Node NodeRef { get; set; }
        public bool IsMax { get; set; }
        public float Value { get; set; }
    }

    // src/world/container.cpp Container::Node.
    private sealed class Node
    {
        public Entry E { get; set; } = new();
        public Node? Parent { get; set; }             // +0x68
        public Kind NodeKind { get; set; } = Kind.None;
        public bool Terrain { get; set; }
        public bool Interior { get; set; }
        public List<Node?> Items { get; } = [];       // vector order, or slots (null = empty slot)
        public List<Node> Subs { get; } = [];         // terrain database: sub-containers (+0x14 list)
        public List<SweepEnd> XEnds { get; } = [];    // terrain database: x end list (+0x40)
        // InteriorShapeDatabase (PVS, 00483548): one list per leaf number + 1 (0 for objects in solid leaves), newest
        // first; the object's list in its parent's database.
        public List<List<Node>> Buckets { get; } = [];
        public int Bucket { get; set; }
        public int Leaf { get; set; } = -1;              // leaf number stored with the entry (the update test compares it)
        public ref Mat Inverse => ref _inverse;        // InteriorShape +0xc8
        private Mat _inverse = new();        // InteriorShape +0xc8
        public uint Tag { get; set; } = 0xffffffffu;     // +0x8c
        public uint VisitTag { get; set; }              // InteriorShape +0x420 (ctor 0)
        // Link records (+0x408 count, +0x414 records): neighbour interior and the face bit of the link on this side.
        public struct Link
        {
            public Node NodeRef { get; set; }
            public uint Bits { get; set; }
        }
        public List<Link> Links { get; } = [];
        public bool ViaLink { get; set; }               // InteriorShape +0x418: 2 while scoped through a link record, else 0
        public ulong Seq { get; set; }                  // add order (castRay tie rule)
        public bool HasDb() => NodeKind != Kind.None;
    }

    // src/world/container.cpp Container::Query.
    private sealed class Query
    {
        public object? Exclude { get; set; }
        public uint Mask { get; set; }
        public ref Float6 Box => ref _box;
        private Float6 _box;
    }

    private readonly Node _root;
    private readonly List<Node> _nodes = [];
    private readonly Dictionary<object, Node> _byObject = new(ReferenceEqualityComparer.Instance);
    private Node? _nullObject; // the map entry of a null object pointer
    private ulong _seq;
    private uint _tag; // DAT_006a8cec, post-incremented per query, starts at 0

    // src/world/container.cpp overlaps: SimContainer_boxOverlap (004f9ae8), inclusive.
    private static bool Overlaps(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        for (var i = 0; i < 3; ++i)
            if (a[i + 3] < b[i] || b[i + 3] < a[i])
                return false;
        return true;
    }

    // src/world/container.cpp inside: SimContainer_boxInside (004f9b3c), inner lies inside outer, inclusive.
    private static bool Inside(ReadOnlySpan<float> outer, ReadOnlySpan<float> inner)
    {
        for (var i = 0; i < 3; ++i)
            if (outer[i + 3] < inner[i + 3] || inner[i] < outer[i])
                return false;
        return true;
    }

    // src/world/container.cpp insideTerrainDb: SimTerrain database open-container test (004fd02c). The x/y comparisons
    // run on the positions of the box ends in sorted end lists; with distinct values they reduce to outer.min <
    // inner.min and inner.max <= outer.max. z is strict on both ends.
    private static bool InsideTerrainDb(ReadOnlySpan<float> outer, ReadOnlySpan<float> inner)
    {
        for (var i = 0; i < 2; ++i)
            if (!(outer[i] < inner[i]) || !(inner[i + 3] <= outer[i + 3]))
                return false;
        return outer[2] < inner[2] && inner[5] < outer[5];
    }

    // src/world/container.cpp clip: 0x004f97e0, clip [a, b] to the box with planes z-min, z-max, x-min, x-max, y-min,
    // y-max.
    private static bool Clip(ref Vec3 a, ref Vec3 b, ReadOnlySpan<float> box)
    {
        ReadOnlySpan<int> kOrder = [2, 0, 2, 1, 0, 0, 0, 1, 1, 0, 1, 1];
        for (var k = 0; k < 6; ++k)
        {
            var axis = kOrder[2 * k];
            var isMax = kOrder[(2 * k) + 1] != 0;
            var plane = box[axis + (isMax ? 3 : 0)];
            var pa = a[axis];
            var pb = b[axis];
            var aOut = isMax ? pa > plane : pa < plane;
            var bOut = isMax ? pb > plane : pb < plane;
            if (aOut && bOut)
                return false;
            if (pa == plane && pb == plane)
                return false; // lying in a clip plane
            if (aOut != bOut)
            {
                var t = Div(Sub(plane, pa), Sub(pb, pa));
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
        for (var i = 0; i < 3; ++i)
            if (p[i] < box[i] || !(p[i] < box[i + 3]))
                return false;
        return true;
    }

    /// <summary>Pads an interior's world box by 3.0 on every axis (f32 0061db9c), as InteriorShape does (0047e5a0).</summary>
    /// <param name="box">The transformed geometry box, min xyz then max xyz, padded in place.</param>
    /// <remarks>Objects fit an interior database only inside this padded box.</remarks>
    public static void PadInteriorBox(Span<float> box)
    {
        for (var i = 0; i < 3; ++i)
        {
            box[i] = Store(Sub(box[i], 3.0f));
            box[i + 3] = Store(Fpu.Add(box[i + 3], 3.0f));
        }
    }

    /// <summary>Finds the link face between two interior boxes in one interior's space (00482028, docs/interiors.md 3).</summary>
    /// <param name="a">The first box.</param>
    /// <param name="b">The second box.</param>
    /// <returns>The face bit of the first face of <paramref name="a"/> that lies within 0.001 of the opposite face of <paramref name="b"/> while the boxes overlap on the two other axes, or 0 when none does.</returns>
    public static uint InteriorLinkFace(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        // Faces in test order: own face index into a, opposite face index into b, the two other axes, bit.
        ReadOnlySpan<int> kFaces =
        [
            0, 3, 1, 2, 0x80, 3, 0, 1, 2, 0x10, 1, 4, 0, 2, 0x40,
            4, 1, 0, 2, 0x08, 2, 5, 0, 1, 0x20, 5, 2, 0, 1, 0x04,
        ];
        for (var k = 0; k < 6; ++k)
        {
            int own = kFaces[5 * k], other = kFaces[(5 * k) + 1], u = kFaces[(5 * k) + 2], v = kFaces[(5 * k) + 3];
            if (!(AbsDiff(a[own], b[other]) < (double)0.001f))
                continue;
            if (LinkOverlap(a[u], a[u + 3], b[u], b[u + 3]) && LinkOverlap(a[v], a[v + 3], b[v], b[v + 3]))
                return (uint)kFaces[(5 * k) + 4];
        }
        return 0;
    }

    /// <summary>Finds the faces of an instance box that a point outside it lies within 3.0 of, on the outer side (004807d8, same bits as <see cref="InteriorLinkFace"/>).</summary>
    /// <param name="p">The point.</param>
    /// <param name="box">The instance box.</param>
    /// <returns>The face bits.</returns>
    public static uint InteriorNearFaces(Vec3 p, ReadOnlySpan<float> box)
    {
        ReadOnlySpan<uint> kBelow = [0x80, 0x40, 0x20];
        ReadOnlySpan<uint> kAbove = [0x10, 0x08, 0x04];
        uint bits = 0;
        for (var i = 0; i < 3; ++i)
            if (p[i] < box[i] && AbsDiff(p[i], box[i]) < 3.0f) // f32 0061db9c
                bits |= kBelow[i];
        for (var i = 0; i < 3; ++i)
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
            var mid = lo + ((hi - lo) / 2);
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
        for (var i = 0; i < l.Count; ++i)
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
            var x = l[p];
            x.Value = value;
            l[p] = x;
            return;
        }
        var e = l[p];
        var down = value <= e.Value;
        e.Value = value;
        if (down)
        {
            if (t > p)
                return;
            for (var i = p; i > t; --i)
                l[i] = l[i - 1];
            l[t] = e;
        }
        else
        {
            if (t == 0 || t - 1 < p || t - 1 >= l.Count)
                return;
            for (var i = p; i < t - 1; ++i)
                l[i] = l[i + 1];
            l[t - 1] = e;
        }
    }

    // src/world/container.cpp Container::sweepInsert: 0x004fc080, both ends go in front of the first end with a value not
    // below theirs.
    private static void SweepInsert(Node c, Node n)
    {
        var l = c.XEnds;
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
        var l = c.XEnds;
        int lo = SweepLower(l, n.E.Box[0]), hi = SweepLower(l, n.E.Box[3]);
        var pMin = SweepPos(l, n, false);
        if (pMin >= l.Count)
            return;
        var before = pMin < hi;
        SweepMove(l, pMin, lo, n.E.Box[0]);
        SweepMove(l, SweepPos(l, n, true), before ? hi : hi + 1, n.E.Box[3]);
    }

    /// <summary>Creates container 7 (004fb754): box +-1e30, every type, default database.</summary>
    public Container()
    {
        _root = new Node
        {
            E = new Entry(null, 0, Float6.Of(-1e30f, -1e30f, -1e30f, 1e30f, 1e30f, 1e30f), false, null)
            {
                ContainerMask = 0xffffffffu
            },
            NodeKind = Kind.Vector
        };
    }

    // src/world/container.cpp Container::nodeOf.
    private Node? NodeOf(object? obj)
    {
        if (obj == null)
            return _nullObject;
        return _byObject.TryGetValue(obj, out var n) ? n : null;
    }

    /// <summary>Finds the entry of an object.</summary>
    /// <param name="obj">The sim object.</param>
    /// <returns>The entry, or null when the object is not registered.</returns>
    public Entry? Find(object? obj) => NodeOf(obj)?.E;

    // src/world/container.cpp Container::setInverse: inverse transform (005054a4), transposed rotation, translation -p
    // rotated by it.
    private static void SetInverse(Node node)
    {
        var m = node.E.Transform;
        var inv = new Mat();
        ReadOnlySpan<int> t = [0, 3, 6, 1, 4, 7, 2, 5, 8];
        for (var i = 0; i < 9; ++i)
            inv.M[i] = m.M[t[i]];
        var rot = inv;
        rot.P = new Vec3(0, 0, 0);
        inv.P = rot.ToWorldVector(new Vec3(-m.P.X, -m.P.Y, -m.P.Z));
        node.Inverse = inv;
    }

    /// <summary>Moves an object to a new transform and box.</summary>
    /// <param name="obj">The sim object.</param>
    /// <param name="m">The new transform.</param>
    /// <param name="box">The new world box.</param>
    /// <remarks>Follows SimObject_setTransform and InteriorShape_worldBox (0047e5a0). An interior with an instance and a database that is in a container unlinks first (00481214), takes the new transform and links again (0048125c) after the box is computed. The box update refiles it (004f9dc4). Other objects behave as <see cref="SetBox"/>.</remarks>
    public void SetTransform(object? obj, Mat m, ReadOnlySpan<float> box)
    {
        var n = NodeOf(obj);
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

    /// <summary>Adds an object (SimContainer::addObject on container 7, 004f9c04).</summary>
    /// <param name="e">The entry.</param>
    /// <remarks>The object descends into the first open sub-container that holds its box. A new container pulls in the objects of the database it lands in that it contains.</remarks>
    public void Add(Entry e)
    {
        var node = new Node
        {
            E = e.Clone()
        };
        if (node.E.DatabaseKind != Database.None)
        {
            node.Terrain = node.E.DatabaseKind == Database.Terrain;
            node.Interior = node.E.DatabaseKind == Database.Interior;
            node.NodeKind = node.Terrain ? Kind.Slots : Kind.Vector;
        }
        if (node.Interior && node.E.Geometry != null)
        {
            SetInverse(node);
            var count = node.E.Geometry.EmptyCount() + (int)node.E.Geometry.SolidCount() + 1;
            while (node.Buckets.Count < count)
                node.Buckets.Add([]);
        }
        var n = node;
        n.Seq = ++_seq;
        if (n.E.Object == null)
            _nullObject = n;
        else
            _byObject[n.E.Object] = n;
        _nodes.Add(node);
        AddTo(_root, n);
        // InteriorShape_onAdd (0047f2bc): after the container add, unlinkAll and linkNeighbours.
        if (n.Interior && n.E.Geometry != null)
            LinkNeighbours(n);
    }

    // src/world/container.cpp boxIn: instance box of `of` in the space of `in` (00481e74): the two corners through the
    // world transform of `of` and the inverse of `in`, then sorted per axis.
    private static void BoxIn(ReadOnlySpan<float> box, in Mat toWorld, in Mat toLocal, Span<float> output)
    {
        var lo = toLocal.ToWorld(toWorld.ToWorld(new Vec3(box[0], box[1], box[2])));
        var hi = toLocal.ToWorld(toWorld.ToWorld(new Vec3(box[3], box[4], box[5])));
        for (var i = 0; i < 3; ++i)
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
        var q = new Query { Exclude = n.E.Object, Mask = ObjectType.InteriorType };
        for (var i = 0; i < 3; ++i)
        {
            q.Box[i] = Store(Sub(n.E.Box[i], 1.0f));
            q.Box[i + 3] = Store(Fpu.Add(n.E.Box[i + 3], 1.0f));
        }
        var found = new List<Node>();
        FindIn(_root, q, found);
        var g = n.E.Geometry!;
        Span<float> b = stackalloc float[6];
        foreach (var m in found)
        {
            if (m == n || !m.Interior || m.E.Geometry == null || (m.E.Type & ObjectType.InteriorType) == 0)
                continue;
            BoxIn(m.E.Geometry.Box, m.E.Transform, n.Inverse, b);
            var bits = InteriorLinkFace(g.Box, b);
            if ((bits & g.LinkableFaces) == 0)
                continue;
            BoxIn(g.Box, n.E.Transform, m.Inverse, b);
            var back = InteriorLinkFace(m.E.Geometry.Box, b);
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
        foreach (var l in n.Links)
        {
            var v = l.NodeRef.Links;
            var it = v.FindIndex(x => x.NodeRef == n);
            if (it >= 0)
                v.RemoveAt(it);
        }
        n.Links.Clear();
    }

    /// <summary>Removes an object.</summary>
    /// <param name="obj">The sim object.</param>
    /// <remarks>The objects it held go to its former container (database destructors 004fbf78 and 00483bc0, called by 004f99e0 with the container +0x68).</remarks>
    public void Remove(object? obj)
    {
        var n = NodeOf(obj);
        if (n == null)
            return;
        var parent = n.Parent ?? _root;
        UnlinkAll(n);
        RemoveFrom(n);
        // The objects it held go to its former container (database destructors 004fbf78, 00483bc0, called by 004f99e0
        // with the container +0x68).
        var held = new List<Node>();
        foreach (var m in n.Items)
            if (m != null)
                held.Add(m);
        foreach (var m in held)
        {
            m.Parent = null;
            AddTo(parent, m);
        }
        if (obj == null)
            _nullObject = null;
        else
            _byObject.Remove(obj);
        _nodes.Remove(n);
    }

    // src/world/container.cpp Container::findOpen: findOpenContainer (+0x18), the first open sub-container whose mask
    // accepts the type and whose box holds the object's box.
    private static Node? FindOpen(Node c, Node n)
    {
        if (c.Terrain)
        {
            foreach (var s in c.Subs)
                if (s != n && (s.E.ContainerMask & n.E.Type) != 0 && InsideTerrainDb(s.E.Box, n.E.Box))
                    return s;
            return null;
        }
        foreach (var s in c.Items)
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
            foreach (var m in c.Items)
                if (m != null && m != n && m.E.Box[0] >= n.E.Box[0] && m.E.Box[3] < n.E.Box[3] &&
                    m.E.Box[1] >= n.E.Box[1] && m.E.Box[4] < n.E.Box[4])
                    output.Add(m);
            output = [.. output.OrderBy(x => x.E.Box[4])]; // std::stable_sort
            for (var i = 0; i < output.Count; ++i)
            {
                var m = output[i];
                if (m.E.Box[2] < n.E.Box[2] || n.E.Box[5] < m.E.Box[5])
                {
                    output[i] = output[^1];
                    output.RemoveAt(output.Count - 1);
                    --i;
                }
            }
            return output;
        }
        foreach (var m in c.Items)
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
        for (var i = 0; i < 3; ++i)
        {
            var s = Store(Fpu.Add(n.E.Box[i], n.E.Box[i + 3]));
            center[i] = Store(Mul(s, 0.5f));
        }
        return c.E.Geometry.LeafAt(c.Inverse.ToWorld(center));
    }

    // src/world/container.cpp Container::bucketInsert: 004837fc, solid leaves share list 0; new entries go to the head of
    // the list. The entry keeps the leaf number, or -1 for a solid leaf, so the update test (00483b74) finds a solid-leaf
    // entry changed on every update.
    private static void BucketInsert(Node c, Node n)
    {
        var leaf = LeafOf(c, n);
        n.Leaf = -1;
        n.Bucket = 0;
        if (c.E.Geometry != null && !c.E.Geometry.IsSolid(leaf) && (long)(uint)leaf + 1 < c.Buckets.Count)
        {
            n.Leaf = leaf;
            n.Bucket = leaf + 1;
        }
        if (c.Buckets.Count == 0)
            c.Buckets.Add([]);
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
            var slot = c.Items.IndexOf(null);
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
        var c = n.Parent;
        if (c == null)
            return;
        if (c.Terrain)
        {
            // ContainerDatabase_freeSlot (004fbeec): the slot empties; the sub-container list fills the hole with its
            // last element.
            for (var i = 0; i < c.Items.Count; ++i)
                if (c.Items[i] == n)
                    c.Items[i] = null;
            var it = c.Subs.IndexOf(n);
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
        var s = FindOpen(c, n);
        if (s != null)
        {
            AddTo(s, n);
            return;
        }
        DbAdd(c, n);
        if (n.HasDb())
            foreach (var m in FindContained(c, n))
                if ((m.E.Type & n.E.ContainerMask) != 0)
                    AddTo(n, m);
    }

    /// <summary>Sets the world box of an object and refiles it (0x004f9a38 to SimContainer::updateObject 004f9dc4).</summary>
    /// <param name="obj">The sim object.</param>
    /// <param name="box">The new world box.</param>
    public void SetBox(object? obj, ReadOnlySpan<float> box)
    {
        var n = NodeOf(obj);
        if (n == null)
            return;
        n.E.Box = Float6.Of(box);
        var c = n.Parent;
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
        var p = c.Parent;
        while (p != null && !Inside(p.E.Box, n.E.Box))
            p = p.Parent;
        AddTo(p ?? _root, n);
    }

    /// <summary>Gets the container object that directly holds an object.</summary>
    /// <param name="obj">The sim object.</param>
    /// <returns>The container object, or null for container 7 or when the object has no entry.</returns>
    public object? ParentOf(object? obj)
    {
        var n = NodeOf(obj);
        return n != null && n.Parent != null ? n.Parent.E.Object : null;
    }

    /// <summary>Lists the objects held directly by an object's database.</summary>
    /// <param name="obj">The container object, null for container 7.</param>
    /// <returns>The objects in database order: vector order, or terrain slot order with empty slots skipped.</returns>
    public List<object?> Contents(object? obj)
    {
        var c = obj != null ? NodeOf(obj) : _root;
        var output = new List<object?>();
        if (c != null)
            foreach (var m in c.Items)
                if (m != null)
                    output.Add(m.E.Object);
        return output;
    }

    /// <summary>Finds the terrain container that holds an object, directly or through nested containers.</summary>
    /// <param name="obj">The sim object.</param>
    /// <returns>The terrain's entry, or null.</returns>
    public Entry? TerrainOf(object? obj)
    {
        var n = NodeOf(obj);
        for (var c = n?.Parent; c != null; c = c.Parent)
            if (c.Terrain)
                return c.E;
        return null;
    }

    /// <summary>Gets the visible distance reported by an object's container (vtable +0xac).</summary>
    /// <param name="obj">The sim object.</param>
    /// <param name="output">Receives the distance.</param>
    /// <returns>False when the object has no entry.</returns>
    /// <remarks>The value is the SimTerrain's, else the parent's, and 1e6 at container 7 (004f9988).</remarks>
    public bool VisibleDistance(object? obj, ref float output)
    {
        var n = NodeOf(obj);
        if (n == null || n.Parent == null)
            return false;
        for (var c = n.Parent; c != null; c = c.Parent)
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
        var first = output.Count;
        // Database findObjects (+0x1c): sub-containers by box overlap, other objects by id, type and overlap.
        if (c.Terrain)
        {
            // SimTerrain database (0x004fcf7c -> 0x004fc3fc): the objects in the order of their x-max ends. The tests run
            // on end positions: min < q.max and q.min <= max in x and y; in z the object is rejected when q.max <= min or
            // max < q.min.
            foreach (var end in c.XEnds)
            {
                if (!end.IsMax)
                    continue;
                var m = end.NodeRef;
                ref var b = ref m.E.Box;
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
            foreach (var m in c.Items)
                if (m != null && (m.HasDb() ? Overlaps(m.E.Box, q.Box)
                                            : !ReferenceEquals(m.E.Object, q.Exclude) && (m.E.Type & q.Mask) != 0 && Overlaps(m.E.Box, q.Box)))
                    output.Add(m);
        }
        var endIndex = output.Count;
        for (var i = first; i < endIndex; ++i)
        {
            var m = output[i];
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

    /// <summary>Finds the objects whose box overlaps a box (docs/interiors.md 6.2), starting at container 7.</summary>
    /// <param name="box">The query box, min xyz then max xyz.</param>
    /// <param name="mask">The object type bits to match.</param>
    /// <param name="exclude">An object to skip, or null.</param>
    /// <returns>The objects in database order, with the results of sub-containers appended.</returns>
    public List<object?> FindObjects(ReadOnlySpan<float> box, uint mask, object? exclude)
    {
        var q = new Query { Exclude = exclude, Mask = mask, Box = Float6.Of(box) };
        var nodes = new List<Node>();
        FindIn(_root, q, nodes);
        var output = new List<object?>();
        foreach (var n in nodes)
            output.Add(n.E.Object);
        return output;
    }

    /// <summary>Casts a ray against the registered objects (docs/interiors.md 6.3).</summary>
    /// <param name="s">The ray start.</param>
    /// <param name="eIn">The ray end.</param>
    /// <param name="mask">The object type bits to test.</param>
    /// <param name="exclude">An object to skip, or null.</param>
    /// <param name="obj">Receives the object hit.</param>
    /// <param name="hit">Receives the hit point.</param>
    /// <param name="normal">Receives the surface normal.</param>
    /// <returns>True on a hit. The nearest hit wins and the most recently added object wins exact ties.</returns>
    public bool CastRay(Vec3 s, Vec3 eIn, uint mask, object? exclude, ref object? obj, ref Vec3 hit, ref Vec3 normal)
    {
        Vec3 before = default;
        return CastRay(s, eIn, mask, exclude, ref obj, ref hit, ref normal, ref before, false);
    }

    /// <summary>Casts a ray and also reports where the segment ended for the winning candidate.</summary>
    /// <param name="s">The ray start.</param>
    /// <param name="eIn">The ray end.</param>
    /// <param name="mask">The object type bits to test.</param>
    /// <param name="exclude">An object to skip, or null.</param>
    /// <param name="obj">Receives the object hit.</param>
    /// <param name="hit">Receives the hit point.</param>
    /// <param name="normal">Receives the surface normal.</param>
    /// <param name="before">Receives the end of the segment as the winning candidate saw it: the hit of the nearest candidate tested ahead of it, or the ray end.</param>
    /// <param name="wantBefore">Whether <paramref name="before"/> is wanted.</param>
    /// <returns>True on a hit.</returns>
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
        FindIn(_root, q, found);
        // Candidates are tested in findObjects order (docs/interiors.md 6.3): container 7 in vector order, then the
        // SimTerrain database in the order of the x-max ends, then the contents of nested containers. Every hit shortens
        // the segment for the candidates after it, and the nearer test is strict, so the order decides exact ties and the
        // segment a later candidate sees. The SimTerrain is in the list of container 7 and therefore ahead of everything
        // its own database appends (docs/collision.md 9, candidate order).
        var candidates = new List<Node>();
        candidates.AddRange(found.Where(a => a.Terrain)); // std::stable_partition
        candidates.AddRange(found.Where(a => !a.Terrain));
        var best = 1e30f;
        var e = eIn;
        obj = null;
        Span<float> w = stackalloc float[3];
        foreach (var cn in candidates)
        {
            var c = cn.E;
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
            var d = X87.Lenf(w);
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

    /// <summary>Walks the scope of container 7 with a new query tag (SimContainer::scope, 004faae0).</summary>
    /// <param name="q">The scope query.</param>
    public void Scope(ScopeQuery q)
    {
        var stack = new List<bool> { true }; // the initial entry is the start container
        ScopeContainer(_root, q, stack);
        ++_tag;
    }

    /// <summary>Tests whether the render walk reaches the terrain image for a camera point.</summary>
    /// <param name="point">The camera point.</param>
    /// <param name="onViaLink">Called for interiors reached through a link record, or null.</param>
    /// <returns>False when the terrain is not drawn, for example a camera in a container interior whose leaf sees no outside.</returns>
    /// <remarks>The render walk of container 7 (vtable +0x98, 004fa9c8 for the root and the terrain, 004804bc for an interior) reaches the terrain only when the container the camera point lies in accepts the scope. 004fa9c8 calls the scope of the open container of the point (database +0x18) first and returns 0 when it returns 0, before the terrain query. Same walk and result as <see cref="Scope"/>, but no object is marked (Tribes.exe 1.11).</remarks>
    public bool RenderReaches(Vec3 point, Action<object>? onViaLink = null)
    {
        if (_tag == 0)
            ++_tag; // the interior visit tag (+0x420) starts at 0 and skips the first query
        var q = new ScopeQuery { Point = point, VisibleDistance = float.MaxValue, OnViaLink = onViaLink };
        var stack = new List<bool> { true };
        var reached = ScopeContainer(_root, q, stack);
        ++_tag;
        return reached;
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
        c.Tag = _tag;
        if (stack[^1])
        {
            var s = StartContainer(c, q);
            if (s != null && s.Tag != _tag && !ScopeNode(s, q, stack))
                return false;
        }
        if (c.E.Object != null && q.Markable(c.E.Object))
            q.Mark(c.E.Object);
        stack.Add(false);
        Span<float> v = stackalloc float[3];
        for (var i = 0; i < c.Items.Count; ++i)
        {
            var n = c.Items[i];
            if (n == null)
                continue;
            if (c.Terrain)
            {
                // SimTerrainDatabase_scope (004fcc00): box centre within the visible distance.
                for (var k = 0; k < 3; ++k)
                    v[k] = Store(Sub(Mul(Fpu.Add(n.E.Box[k + 3], n.E.Box[k]), 0.5), q.Point[k]));
                var len = Sqrt(Fpu.Add(Fpu.Add(Mul(v[0], v[0]), Mul(v[1], v[1])), Mul(v[2], v[2])));
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
        var g = c.E.Geometry;
        if (g == null)
            return false;
        // A linked interior is scoped only on the start container path or through a link record.
        var start = stack[^1];
        if (g.LinkedInterior && !c.ViaLink && !start)
            return false;
        if (c.VisitTag == _tag)
            return false; // +0x420 starts at 0, so the very first query (tag 0) skips interiors
        c.VisitTag = _tag;
        if (!c.HasDb())
            return true;
        if (stack[^1])
        {
            var s = StartContainer(c, q);
            if (s != null && s.Tag != _tag && !ScopeNode(s, q, stack))
                return false;
        }
        if (c.ViaLink && c.E.Object != null)
            q.OnViaLink?.Invoke(c.E.Object);
        if (c.E.Object != null && q.Markable(c.E.Object))
            q.Mark(c.E.Object);
        stack.Add(false);
        // PVS database scope (00484050): the lists of the leaves visible from the camera's leaf (leaf numbers above 42),
        // then list 0, then the outside leaves 0..42 when the camera leaf sees outside. A camera in a solid leaf scopes
        // nothing.
        var leaf = g.LeafAt(c.Inverse.ToWorld(q.Point));
        void ScopeList(int b)
        {
            if (b >= c.Buckets.Count)
                return;
            var list = new List<Node>(c.Buckets[b]); // scoping may re-file entries
            foreach (var n in list)
                ScopeEntry(n, q, stack);
        }
        if (!g.IsSolid(leaf))
        {
            foreach (var v in g.VisibleLeaves(leaf))
                if (v > 42)
                    ScopeList(v + 1);
            ScopeList(0);
            if (g.SeesOutside(leaf))
                for (var i = 0; i < 43; ++i)
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
        var g = c.E.Geometry!;
        stack.Add(false);
        var p = c.Inverse.ToWorld(q.Point);
        uint faces = g.OutsideFaces(g.LeafAt(p));
        var inBox = InsideHalfOpen(p, g.Box);
        var linked = false;
        if (!g.LinkedInterior || c.ViaLink || start)
        {
            var mask = faces;
            if (!inBox && start)
                mask |= InteriorNearFaces(p, g.Box);
            for (var i = 0; i < c.Links.Count; ++i)
            {
                var m = c.Links[i].NodeRef;
                if ((c.Links[i].Bits & mask) == 0 || m.VisitTag == _tag)
                    continue;
                var saved = m.ViaLink;
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
        else if (n.Tag != _tag)
        {
            ScopeNode(n, q, stack);
        }
    }
}
