// Port of src/world/collision.hpp and src/world/collision.cpp: collision for moving objects (docs/collision.md): the
// narrow-phase tests a Player image runs (terrain plane, interior box clip, shape and player spheres), the candidate
// list and the SimMovement stepped move. Portable truncating form (docs/portable-float.md 1): x87::Chop is
// Fpu.ChopScope, x87::f32 stores toward zero whatever the mode (Fpu.F32Chop), every other double operation follows the
// mode of Fpu. The C++ sim::SimObject pointers are `object` here: OpenTribes.World does not reference OpenTribes.Sim
// (the Sim layer names world types), and the code below only compares and passes them on.
using static OpenTribes.Core.Fpu;

namespace OpenTribes.World;

// src/world/collision.hpp namespace x87: x87 helpers shared by the movement code.
public static class X87
{
    // src/world/collision.cpp x87::Chop: sets round-toward-zero for a scope (what crt_ftol leaves in the original).
    public static Chop Chop() => ChopScope();

    // src/world/collision.cpp x87::f32: f32 store toward zero, independent of the current rounding mode.
    public static float F32(double x) => F32Chop(x);

    // src/world/collision.cpp x87::dot: returned unrounded (0x005ece49).
    public static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) =>
        Add(Add(Mul(a[0], b[0]), Mul(a[1], b[1])), Mul(a[2], b[2]));

    // src/world/collision.cpp x87::len: returned unrounded (0x005ed014).
    public static double Len(ReadOnlySpan<float> a) => Sqrt(Dot(a, a));

    // src/world/collision.cpp x87::lenf: m_Point3F_lenf (0x005ed031), bits(f32(len^2)) >> 1 + 0x1fc00000.
    public static float Lenf(ReadOnlySpan<float> a)
    {
        float s = F32(Add(Add(Mul(a[0], a[0]), Mul(a[1], a[1])), Mul(a[2], a[2])));
        uint bits = BitConverter.SingleToUInt32Bits(s);
        bits = (bits >> 1) + 0x1fc00000u;
        return BitConverter.UInt32BitsToSingle(bits);
    }

    // src/world/collision.cpp x87::mulVector: m_mul_vector (0x005ed124) with a row-vector 3x3.
    public static void MulVector(ReadOnlySpan<float> v, ReadOnlySpan<float> m, Span<float> output)
    {
        float r0 = F32(Add(Add(Mul(v[0], m[0]), Mul(v[1], m[3])), Mul(v[2], m[6])));
        float r1 = F32(Add(Add(Mul(v[0], m[1]), Mul(v[1], m[4])), Mul(v[2], m[7])));
        float r2 = F32(Add(Add(Mul(v[0], m[2]), Mul(v[1], m[5])), Mul(v[2], m[8])));
        output[0] = r0;
        output[1] = r1;
        output[2] = r2;
    }

    // src/world/collision.cpp x87::mulPoint: m_mul_point (0x005ed17e) with a row-vector 3x3.
    public static void MulPoint(ReadOnlySpan<float> v, ReadOnlySpan<float> m, ReadOnlySpan<float> t, Span<float> output)
    {
        float r0 = F32(Add(Add(Add(Mul(v[0], m[0]), Mul(v[1], m[3])), Mul(v[2], m[6])), t[0]));
        float r1 = F32(Add(Add(Mul(v[0], m[1]), Mul(v[1], m[4])), Add(Mul(v[2], m[7]), t[1])));
        float r2 = F32(Add(Add(Mul(v[0], m[2]), Mul(v[1], m[5])), Add(Mul(v[2], m[8]), t[2])));
        output[0] = r0;
        output[1] = r1;
        output[2] = r2;
    }
}

// src/world/collision.hpp struct CollisionSurface: surface record (docs/collision.md 1.3), the fields the Player reads.
public struct CollisionSurface
{
    public Float3 Position;
    public Float3 Normal;      // in the space of CollisionHit::rot
    public float Distance;     // +0x1c: a negative value makes the Player ignore the contact
    public bool HasMaterial;   // +0x20 != 0
    public float Friction;     // material entry +0x40
    public int Material;       // interior surface: index in the .dml of the .dis, -1 none

    public CollisionSurface()
    {
        Position = default;
        Normal = Float3.Of(0, 0, 1);
        Distance = 0;
        HasMaterial = false;
        Friction = 1.0f;
        Material = -1;
    }
}

// src/world/collision.hpp struct CollisionHit: hit record (1.2).
public sealed class CollisionHit
{
    public object? Object;                    // sim::SimObject*
    public uint Type;                         // object type bits (+0x48)
    public Float9 Rot = Float9.Identity();    // R+0x10 3x3: surface space -> world
    public float StepHeight = -1.0f;          // R+0x80
    public List<CollisionSurface> Surfaces = new();
}

// src/world/collision.hpp struct CollisionImage: image of a candidate object (2.1 step 3).
public sealed class CollisionImage
{
    // Sphere is a Player image (5.3). TriggerSphere is the sphere image of a StaticBase with a triggerRadius (type 0,
    // StaticBase_getCollisionImages 0x0044fff0): the mover's shape is tested against it.
    public enum Kind { Terrain, Interior, Shape, Sphere, Trigger, TriggerSphere }
    public Kind ImageKind = Kind.Terrain;
    public object? Object;                    // sim::SimObject*
    public uint Type;
    public bool NonBlocking;                  // image byte +0xd: posts an event, never blocks
    public Terrain? TerrainData;
    public InteriorGeometry? Geometry;
    public Float9 Rot = Float9.Identity();    // image transform (object transform)
    public Float3 Pos;
    public Float6 Box;                        // Shape, Trigger: object-space box
    // Shape: the box meshes of the shape's collision detail (world/shape.hpp), when it has one.
    public List<Float6>? Boxes;
    public Float3 Centre;                     // Sphere, TriggerSphere: centre in image space
    public float Radius;
    // Shape: the ShapeCollisionImage (docs/collision.md T1.2). `Instance` is the object's shape instance with its thread
    // state, `Detail` the detail the sphere test runs on (image +0x74) and `MeshMode` the image mode (+0x44: faces, or
    // the bounds mode of Items). Without an instance the box stand-ins above are used (synthetic images of unit tests).
    public ShapeInstance? Instance;
    public int Detail;
    public bool MeshMode = true;
}

// src/world/collision.hpp struct MoverImage: the mover's image (Player image, docs/player-sim.md 3.1): only the
// translation of the transform is used by the terrain, interior and shape cases.
public sealed class MoverImage
{
    public Float3 Pos;
    public Float9 Rot = Float9.Identity();
    public Float3 BoxMin, BoxMax;  // box A
    public float Radius;           // sphere centre (0, 0, radius)
    // ShapeBase mover (an Item, docs/collision.md T3): the shape image rotates, the box is the shape box and the
    // bounding sphere centre is `CentreLocal` through the rotation. The terrain case is not covered (the Item code keeps
    // its own 0x00437b50).
    public bool ShapeMover;
    public Float3 CentreLocal;
    // The shape image of a ShapeBase mover: a Player candidate tests its own sphere against it (the image with the
    // higher type runs the test, 0x005036a4).
    public ShapeInstance? Instance;
    public int Detail;
    public bool MeshMode = true;
}

// src/world/collision.hpp struct MoveInfo: stepped move (section 3, 0x00505a3c).
public sealed class MoveInfo
{
    public int Iteration;
    public float Used;               // +0x04
    public float EndTime;            // +0x0c
    public float[]? Current;         // object position p
    public float[]? Attempted;       // image position q of the blocked substep
    public float[]? Target;          // target position, rewritten by the callback
    public List<CollisionImage>? Candidates;
    public List<CollisionHit>? Hits;
    public List<object>? Notify;     // sim::SimObject*
}

// src/world/collision.hpp struct SteppedMove.
public sealed class SteppedMove
{
    public float Dt = 0.032f;        // time slice +0x168
    public int MaxIterations = 3;    // +0x1f0
    public MoverImage Image = new();
    public List<CollisionImage> Candidates = new();
    // Collision callback (vtable +0xb8): true restarts toward the rewritten target.
    public Func<MoveInfo, bool>? OnCollision;
    // A non-blocking image was hit (event 0x41f to its owner, 0x005070fc).
    public Action<object?>? OnTouch;
    public List<object> Notify = new(); // notify list of the last move (info +0x3c)

    // src/world/collision.cpp SteppedMove::testAt: tests the image at q against every candidate (0x00506bfc).
    // Non-blocking candidates that collide are reported and leave the list. True when nothing blocks.
    public bool TestAt(ReadOnlySpan<float> q, List<CollisionHit> hits)
    {
        hits.Clear();
        for (int k = 0; k < 3; ++k)
            Image.Pos[k] = q[k];
        for (int i = 0; i < Candidates.Count;)
        {
            var hit = new CollisionHit();
            if (!Collision.TestImage(Image, Candidates[i], hit))
            {
                ++i;
                continue;
            }
            if (Candidates[i].NonBlocking)
            {
                // 2.3 step 5: one event, then the last entry takes its place.
                object? owner = Candidates[i].Object;
                Candidates[i] = Candidates[^1];
                Candidates.RemoveAt(Candidates.Count - 1);
                OnTouch?.Invoke(owner);
                continue;
            }
            hits.Add(hit);
            ++i;
        }
        return hits.Count == 0;
    }

    // src/world/collision.cpp SteppedMove::run: moves p toward target. False after maxIterations blocked iterations
    // (the caller zeroes the velocity).
    public bool Run(float[] p, float[] target, float step0, float step1)
    {
        Notify.Clear();
        float step = step0;
        float used = 0.0f;
        var hits = new List<CollisionHit>();
        var delta = new float[3];
        for (int it = 0; it < MaxIterations; ++it)
        {
            var q = new float[3];
            for (int i = 0; i < 3; ++i)
            {
                delta[i] = X87.F32(Sub(target[i], p[i]));
                q[i] = p[i];
            }
            float length = X87.F32(X87.Len(delta));
            // ftol(ceil(f64(L / step))). A count the original could not finish in a tick is bounded (D11): 2^20 substeps
            // is a move of more than 300 km.
            double quotient = Div(length, step);
            double up = Math.Ceiling(quotient);
            int n = up > -2147483649.0 && up < 2147483648.0 ? (int)up : 0;
            if (n > (1 << 20))
                n = 1 << 20;
            bool blocked = false;
            if (n > 0)
            {
                float k = X87.F32(Div(1.0, n));
                var d = new float[3];
                for (int i = 0; i < 3; ++i)
                    d[i] = X87.F32(Mul(k, delta[i]));
                for (int i = 0; i < n; ++i)
                {
                    for (int j = 0; j < 3; ++j)
                        q[j] = X87.F32(Add(d[j], q[j]));
                    if (TestAt(q, hits))
                    {
                        q.CopyTo(p, 0);
                        continue;
                    }
                    float slice = X87.F32(Div(Sub(Dt, used), n));
                    var info = new MoveInfo();
                    info.Iteration = it;
                    info.EndTime = X87.F32(Add(Mul(i + 1, slice), used));
                    used = X87.F32(Add(Mul(i, slice), used));
                    info.Used = used;
                    info.Current = p;
                    info.Attempted = q;
                    info.Target = target;
                    info.Candidates = Candidates;
                    info.Hits = hits;
                    info.Notify = Notify;
                    if (OnCollision != null && OnCollision(info))
                    {
                        blocked = true;
                        break;
                    }
                    q.CopyTo(p, 0);
                }
            }
            if (!blocked)
                return true;
            step = step1;
        }
        return false;
    }
}

// The free functions of src/world/collision.cpp.
public static class Collision
{
    private const float kClipEps = 1e-5f; // 0x0063f9a0 (0x3727c5ac)

    // src/world/collision.cpp transpose.
    private static void Transpose(ReadOnlySpan<float> m, Span<float> output)
    {
        Span<float> t = stackalloc float[9] { m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8] };
        t.CopyTo(output);
    }

    // src/world/collision.cpp relativeTransform: T = A * inverse(image transform) with A = identity rotation at `pos`
    // (5.2 B1-B5). With `moverRot` the rotation of A is that matrix: T.R = A.R * R' (0x005ed2b2).
    private static void RelativeTransform(ReadOnlySpan<float> rot, ReadOnlySpan<float> imagePos, ReadOnlySpan<float> pos,
                                          Span<float> outRot, Span<float> outT, ReadOnlySpan<float> moverRot = default)
    {
        Span<float> inv = stackalloc float[9];
        Transpose(rot, inv);
        ReadOnlySpan<float> neg = stackalloc float[3] { -imagePos[0], -imagePos[1], -imagePos[2] };
        Span<float> ti = stackalloc float[3];
        X87.MulVector(neg, inv, ti);
        for (int j = 0; j < 3; ++j)
            outT[j] = X87.F32(Add(Mul(pos[2], inv[6 + j]), Add(Add(Mul(pos[0], inv[j]), Mul(pos[1], inv[3 + j])), ti[j])));
        if (moverRot.IsEmpty)
        {
            inv.CopyTo(outRot);
            return;
        }
        for (int r = 0; r < 3; ++r)
            for (int c = 0; c < 3; ++c)
                outRot[r * 3 + c] = X87.F32(Add(Add(Mul(moverRot[r * 3], inv[c]), Mul(moverRot[r * 3 + 1], inv[3 + c])),
                                                Mul(moverRot[r * 3 + 2], inv[6 + c])));
    }

    // ---- interior box clip (5.2 parts 2.3 - 2.7) ----

    private struct Record
    {
        public int PlaneIndex; // -1: solid leaf
        public int Front, Back;
        public int Leaf;

        public Record()
        {
            PlaneIndex = -1;
            Front = -1;
            Back = -1;
            Leaf = 0;
        }
    }

    private sealed class ClipVertex
    {
        public Float3 P;
        public int Mark;
        public bool Seen;
    }

    private sealed class ClipFace
    {
        public int PlaneIndex = -1;
        public int LastVert;
        public bool Side;
    }

    private struct ClipEdge
    {
        public int V0, V1, F0, F1;

        public ClipEdge(int v0, int v1, int f0, int f1)
        {
            V0 = v0;
            V1 = v1;
            F0 = f0;
            F1 = f1;
        }
    }

    // src/world/collision.cpp struct BoxClip.
    private sealed class BoxClip
    {
        public readonly InteriorGeometry Geo;
        public Float3 C;
        public float R;
        public Float3 T; // T.t
        public Float3 Up;
        public readonly List<Record> Records = new();
        public readonly List<ClipVertex> Verts = new();
        public readonly List<ClipFace> Faces = new();
        public int Counter;
        public bool Touched;
        public float MaxHeight;
        public readonly CollisionHit Hit;

        public BoxClip(InteriorGeometry geo, CollisionHit hit)
        {
            Geo = geo;
            Hit = hit;
        }

        // boxLeaves (0x00560d4c): index of the record, or -1.
        public int Leaves(int child, int depth)
        {
            if (child < 0)
            {
                int leaf = -(child + 1);
                if (!Geo.IsSolid(leaf))
                    return -1;
                var rec = new Record();
                rec.Leaf = leaf;
                Records.Add(rec);
                return Records.Count - 1;
            }
            IReadOnlyList<BspNode> nodes = Geo.Nodes();
            // A well-formed BSP is a tree: a path longer than the node count is a cycle (D11).
            if (child >= nodes.Count || depth > nodes.Count)
                return -1;
            BspNode node = nodes[child];
            if (node.PlaneIndex >= Geo.Planes().Count)
                return -1;
            Plane pl = Geo.Planes()[node.PlaneIndex];
            ReadOnlySpan<float> n = stackalloc float[3] { pl.Nx, pl.Ny, pl.Nz };
            float d = X87.F32(Add(X87.Dot(n, C), pl.D));
            if (Math.Abs(d) >= R)
                return Leaves(d >= 0.0f ? node.Front : node.Back, depth + 1);
            int f = Leaves(node.Front, depth + 1);
            int b = Leaves(node.Back, depth + 1);
            if (f < 0 && b < 0)
                return -1;
            var r = new Record();
            r.PlaneIndex = node.PlaneIndex;
            r.Front = f;
            r.Back = b;
            Records.Add(r);
            return Records.Count - 1;
        }

        // Leaf callback (0x00560f60).
        public void Leaf(int leafIndex, List<ClipEdge> edges)
        {
            Touched = true;
            Span<float> w = stackalloc float[3];
            foreach (ClipEdge e in edges)
            {
                for (int which = 0; which < 2; ++which)
                {
                    int fi = which == 0 ? e.F0 : e.F1;
                    ClipFace f = Faces[fi];
                    if (f.PlaneIndex < 0)
                        continue;
                    int s = Geo.FindSurface(leafIndex, f.PlaneIndex);
                    if (s < 0)
                        continue;
                    Plane pl = Geo.Planes()[f.PlaneIndex];
                    var output = new CollisionSurface();
                    output.Normal[0] = f.Side ? -pl.Nx : pl.Nx;
                    output.Normal[1] = f.Side ? -pl.Ny : pl.Ny;
                    output.Normal[2] = f.Side ? -pl.Nz : pl.Nz;
                    output.Position = Verts[e.V0].P;
                    output.Distance = 1.0f;
                    // Material entry of the .dml: every stock entry has friction 1.0 (5.2 F7).
                    output.HasMaterial = Geo.SurfaceMaterial(s) != 0xff;
                    output.Material = output.HasMaterial ? Geo.SurfaceMaterial(s) : -1;
                    output.Friction = 1.0f;
                    Hit.Surfaces.Add(output);
                    f.PlaneIndex = -1;
                }
                for (int which = 0; which < 2; ++which)
                {
                    ClipVertex v = Verts[which == 0 ? e.V0 : e.V1];
                    if (v.Seen)
                        continue;
                    v.Seen = true;
                    for (int k = 0; k < 3; ++k)
                        w[k] = X87.F32(Sub(v.P[k], T[k]));
                    float h = X87.F32(X87.Dot(Up, w));
                    if (h > MaxHeight)
                        MaxHeight = h;
                }
            }
        }

        // clip (0x005632f0).
        public void Clip(int recordIndex, List<ClipEdge> edges)
        {
            Record rec = Records[recordIndex];
            Plane pl = Geo.Planes()[rec.PlaneIndex];
            ReadOnlySpan<float> n = stackalloc float[3] { pl.Nx, pl.Ny, pl.Nz };
            int savedVerts = Verts.Count;
            var kf = new ClipFace();
            kf.PlaneIndex = rec.PlaneIndex;
            Faces.Add(kf);
            int fi = Faces.Count - 1;
            ++Counter;
            int mark = 2 * Counter;
            var front = new List<ClipEdge>();
            var back = new List<ClipEdge>();
            Span<float> dv = stackalloc float[3];
            foreach (ClipEdge e in edges)
            {
                for (int which = 0; which < 2; ++which)
                {
                    ClipVertex v = Verts[which == 0 ? e.V0 : e.V1];
                    if (v.Mark < mark)
                    {
                        float s = X87.F32(X87.Dot(n, v.P));
                        bool isBack = !(Sub(s, kClipEps) > -(double)pl.D) && (-(double)pl.D > Add(s, kClipEps));
                        v.Mark = mark + (isBack ? 1 : 0);
                    }
                }
                int ma = Verts[e.V0].Mark, mb = Verts[e.V1].Mark;
                if (ma == mb)
                {
                    (ma == mark ? front : back).Add(e);
                    continue;
                }
                bool v0Back = ma != mark;
                Float3 a = Verts[e.V0].P;
                Float3 b = Verts[e.V1].P;
                for (int k = 0; k < 3; ++k)
                    dv[k] = X87.F32(Sub(b[k], a[k]));
                double den = X87.Dot(n, dv);
                if (den != 0.0)
                {
                    float den32 = X87.F32(den);
                    float tt = X87.F32(Div(-Add(X87.Dot(n, a), pl.D), den32));
                    var nvx = new ClipVertex();
                    for (int i = 0; i < 3; ++i)
                        nvx.P[i] = X87.F32(Add(Mul(dv[i], tt), a[i]));
                    Verts.Add(nvx);
                }
                int nv = Verts.Count - 1;
                front.Add(new ClipEdge(v0Back ? e.V1 : e.V0, nv, e.F0, e.F1));
                back.Add(new ClipEdge(v0Back ? e.V0 : e.V1, nv, e.F0, e.F1));
                for (int which = 0; which < 2; ++which)
                {
                    int fj = which == 0 ? e.F0 : e.F1;
                    ClipFace f = Faces[fj];
                    if (f.LastVert < savedVerts)
                    {
                        f.LastVert = nv;
                    }
                    else
                    {
                        front.Add(new ClipEdge(f.LastVert, nv, fj, fi));
                        back.Add(new ClipEdge(f.LastVert, nv, fj, fi));
                    }
                }
            }
            if (rec.Front >= 0 && front.Count != 0)
            {
                Faces[fi].Side = true;
                Record child = Records[rec.Front];
                if (child.PlaneIndex < 0)
                    Leaf(child.Leaf, front);
                else
                    Clip(rec.Front, front);
            }
            if (rec.Back >= 0 && back.Count != 0)
            {
                Faces[fi].Side = false;
                Record child = Records[rec.Back];
                if (child.PlaneIndex < 0)
                    Leaf(child.Leaf, back);
                else
                    Clip(rec.Back, back);
            }
            Faces.RemoveAt(Faces.Count - 1);
        }
    }

    // src/world/collision.cpp sphereFace: sphere against one face (section 6 T4.1 steps 1-8). Appends at most one
    // surface.
    private static void SphereFace(ReadOnlySpan<float> pm, float r, ReadOnlySpan<float> a, ReadOnlySpan<float> b,
                                   ReadOnlySpan<float> c, List<CollisionSurface> output)
    {
        var s = new CollisionSurface();
        if (!Shape.SphereTriangle(pm, r, a, b, c, s.Position, s.Normal, ref s.Distance))
            return;
        s.HasMaterial = true;
        s.Friction = 1.0f;
        output.Add(s);
    }

    // src/world/collision.cpp collideTerrain: 5.1 Player vs terrain (0x00436020). One surface, distance = signed plane
    // distance.
    public static bool CollideTerrain(Terrain terrain, ReadOnlySpan<float> pos, float radius, CollisionHit hit)
    {
        // 5.1 steps 2-3 with an unrotated terrain: M.t = P + (-terrainPos), c = (0, 0, r) * M.
        Vec3 tp = terrain.Position();
        ReadOnlySpan<float> neg = stackalloc float[3] { -tp.X, -tp.Y, -tp.Z };
        Span<float> c = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
            c[i] = X87.F32(Add(pos[i], neg[i]));
        c[2] = X87.F32(Add(radius, c[2]));
        float h = 0;
        Span<float> n = stackalloc float[3];
        byte material = 0;
        if (!terrain.SurfaceInfo(c[0], c[1], ref h, n, ref material))
            return false;
        ReadOnlySpan<float> point = stackalloc float[3] { c[0], c[1], h };
        float a = X87.F32(X87.Dot(c, n));
        double b = X87.Dot(point, n);
        float d = X87.F32(Sub(a, b));
        if (!(Math.Abs((double)d) < (double)radius))
            return false;
        var s = new CollisionSurface();
        s.Position = Float3.Of(point);
        s.Normal = Float3.Of(n);
        s.Distance = d;
        s.HasMaterial = true; // terrain material entry: friction 1.0 on stock data (8 E6)
        s.Friction = 1.0f;
        hit.Surfaces.Clear();
        hit.Surfaces.Add(s);
        hit.StepHeight = -1.0f;
        hit.Rot = Float9.Identity();
        return true;
    }

    // src/world/collision.cpp collideInteriorBox: 5.2 Player vs interior (0x004367d0 -> ITRCollision_collideBox
    // 0x00560b54). True with zero surfaces when the box is inside solid without a listed surface plane. `moverRot` is
    // the mover image's rotation (an empty span is the identity of the Player case).
    public static bool CollideInteriorBox(InteriorGeometry geo, ReadOnlySpan<float> rot, ReadOnlySpan<float> interiorPos,
                                          ReadOnlySpan<float> pos, ReadOnlySpan<float> boxMin, ReadOnlySpan<float> boxMax,
                                          CollisionHit hit, ReadOnlySpan<float> moverRot = default)
    {
        hit.Surfaces.Clear();
        hit.StepHeight = 0.0f; // S6 overwrites the caller's -1
        hit.Rot = Float9.Of(rot);
        if (geo.Nodes().Count == 0)
            return false;
        Span<float> m = stackalloc float[9];
        var bc = new BoxClip(geo, hit);
        Span<float> bt = stackalloc float[3];
        RelativeTransform(rot, interiorPos, pos, m, bt, moverRot);
        bc.T = Float3.Of(bt);
        // S1-S3: centre and radius of the bounding sphere.
        Span<float> cl = stackalloc float[3], e = stackalloc float[3], bcC = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
        {
            cl[i] = X87.F32(Mul(Add(boxMin[i], boxMax[i]), 0.5f));
            e[i] = X87.F32(Sub(boxMin[i], boxMax[i]));
        }
        X87.MulPoint(cl, m, bt, bcC);
        bc.C = Float3.Of(bcC);
        bc.R = X87.F32(Mul(X87.Lenf(e), 0.5f));
        bc.Up[0] = m[6];
        bc.Up[1] = m[7];
        bc.Up[2] = m[8];
        int root = bc.Leaves(0, 0);
        if (root < 0)
            return false;
        if (bc.Records[root].PlaneIndex < 0)
            return true; // one solid leaf: inside solid, no surface (R+0x84)
        // 2.4: the box as a polyhedron in geometry space.
        Span<float> ext = stackalloc float[3], axis = stackalloc float[9], o = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
        {
            ext[i] = X87.F32(Sub(boxMax[i], boxMin[i]));
            for (int j = 0; j < 3; ++j)
                axis[i * 3 + j] = X87.F32(Mul(ext[i], m[i * 3 + j]));
        }
        X87.MulPoint(boxMin, m, bt, o);
        static void AddV(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> output)
        {
            for (int j = 0; j < 3; ++j)
                output[j] = X87.F32(Add(a[j], b[j]));
        }
        for (int i = 0; i < 8; ++i)
            bc.Verts.Add(new ClipVertex());
        Span<float> tmp = stackalloc float[3], ox = stackalloc float[3];
        bc.Verts[0].P = Float3.Of(o);
        AddV(o, axis.Slice(3, 3), tmp);
        bc.Verts[1].P = Float3.Of(tmp);
        AddV(o, axis.Slice(0, 3), ox);
        AddV(ox, axis.Slice(3, 3), tmp);
        bc.Verts[2].P = Float3.Of(tmp);
        bc.Verts[3].P = Float3.Of(ox);
        for (int i = 0; i < 4; ++i)
        {
            AddV(bc.Verts[i].P, axis.Slice(6, 3), tmp);
            bc.Verts[i + 4].P = Float3.Of(tmp);
        }
        for (int i = 0; i < 6; ++i)
            bc.Faces.Add(new ClipFace());
        var edges = new List<ClipEdge>();
        for (int i = 0; i < 4; ++i)
        {
            int next = (i + 1) % 4, prev = (i + 3) % 4;
            edges.Add(new ClipEdge(i, next, i, 4));
            edges.Add(new ClipEdge(i + 4, next + 4, i, 5));
            edges.Add(new ClipEdge(i, i + 4, i, prev));
        }
        bc.Clip(root, edges);
        if (hit.Surfaces.Count != 0)
        {
            hit.StepHeight = bc.MaxHeight;
            return true;
        }
        return bc.Touched;
    }

    // src/world/collision.cpp collideSpheres: 5.3 Player vs player (0x00437414): spheres at c1 and c2 (world), normal =
    // (c1 - c2) / lenf.
    public static bool CollideSpheres(ReadOnlySpan<float> c1, float r1, ReadOnlySpan<float> c2, float r2, ReadOnlySpan<float> rot,
                                      CollisionHit hit)
    {
        // 5.3: the difference in the own image space (world difference through the inverse of the own rotation),
        // length through lenf.
        ReadOnlySpan<float> d = stackalloc float[3]
        {
            X87.F32(Sub(c1[0], c2[0])), X87.F32(Sub(c1[1], c2[1])), X87.F32(Sub(c1[2], c2[2])),
        };
        Span<float> inv = stackalloc float[9], w = stackalloc float[3];
        Transpose(rot, inv);
        X87.MulVector(d, inv, w);
        float l = X87.F32(X87.Lenf(w));
        if (!(Add(r1, r2) > l))
            return false;
        var s = new CollisionSurface();
        float k = X87.F32(Div(1.0, l));
        for (int i = 0; i < 3; ++i)
            s.Normal[i] = X87.F32(Mul(k, w[i]));
        s.Position[0] = s.Position[1] = 0;
        s.Position[2] = r1;
        s.Distance = l;
        s.HasMaterial = false;
        hit.Surfaces.Clear();
        hit.Surfaces.Add(s);
        hit.StepHeight = -1.0f;
        hit.Rot = Float9.Of(rot);
        return true;
    }

    // src/world/collision.cpp sphereTouchesBox: sphere against a box, overlap only (Trigger images).
    public static bool SphereTouchesBox(ReadOnlySpan<float> box, ReadOnlySpan<float> boxPos, ReadOnlySpan<float> centre, float radius)
    {
        float d2 = 0;
        for (int i = 0; i < 3; ++i)
        {
            float p = X87.F32(Sub(centre[i], boxPos[i]));
            if (p < box[i])
                d2 = X87.F32(Add(Mul(Sub(p, box[i]), Sub(p, box[i])), d2));
            else if (p > box[i + 3])
                d2 = X87.F32(Add(Mul(Sub(p, box[i + 3]), Sub(p, box[i + 3])), d2));
        }
        return d2 < X87.F32(Mul(radius, radius));
    }

    // src/world/collision.cpp collideSphereBox: 5.4 Player vs shape, approximated: the sphere against the faces of the
    // object-space box with the mesh rules of section 6 T4.1 (two triangles per box face).
    public static bool CollideSphereBox(ReadOnlySpan<float> box, ReadOnlySpan<float> rot, ReadOnlySpan<float> shapePos,
                                        ReadOnlySpan<float> centre, float radius, CollisionHit hit)
    {
        hit.Surfaces.Clear();
        hit.StepHeight = -1.0f;
        hit.Rot = Float9.Of(rot);
        // Sphere centre in shape space.
        Span<float> m = stackalloc float[9], t = stackalloc float[3];
        RelativeTransform(rot, shapePos, centre, m, t);
        ReadOnlySpan<float> pm = t;
        // Bounds gate (T4 step 2).
        float d2 = 0;
        for (int i = 0; i < 3; ++i)
        {
            if (pm[i] < box[i])
                d2 = X87.F32(Add(Mul(Sub(pm[i], box[i]), Sub(pm[i], box[i])), d2));
            else if (pm[i] > box[i + 3])
                d2 = X87.F32(Add(Mul(Sub(pm[i], box[i + 3]), Sub(pm[i], box[i + 3])), d2));
        }
        if (!(d2 < X87.F32(Mul(radius, radius))))
            return false;
        // Two triangles per box face, wound so that cross(B - C, A - C) points outward.
        Span<float> corner = stackalloc float[12];
        ReadOnlySpan<int> tris = stackalloc int[6] { 0, 1, 2, 0, 2, 3 };
        for (int axis = 0; axis < 3; ++axis)
        {
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            for (int side = 0; side < 2; ++side)
            {
                for (int k = 0; k < 4; ++k)
                {
                    corner[k * 3 + axis] = box[axis + (side != 0 ? 3 : 0)];
                    corner[k * 3 + u] = box[u + ((k == 1 || k == 2) ? 3 : 0)];
                    corner[k * 3 + v] = box[v + ((k >= 2) ? 3 : 0)];
                }
                // corner order 0,1,2,3 is counter-clockwise seen from +axis.
                for (int ti = 0; ti < 2; ++ti)
                {
                    ReadOnlySpan<int> tri = tris.Slice(ti * 3, 3);
                    ReadOnlySpan<float> a = corner.Slice(tri[side != 0 ? 1 : 0] * 3, 3);
                    ReadOnlySpan<float> b = corner.Slice(tri[side != 0 ? 0 : 1] * 3, 3);
                    ReadOnlySpan<float> c = corner.Slice(tri[2] * 3, 3);
                    SphereFace(pm, radius, a, b, c, hit.Surfaces);
                }
            }
        }
        return hit.Surfaces.Count != 0;
    }

    // src/world/collision.cpp collideSphereShape: sphere against a shape image (PlayerCollisionImage_collideShape
    // 0x00436ed0, ShapeCollisionImage_collideShape 0x0043873c): `centre` is the sphere centre in the space of the
    // sphere's own image, whose transform is (sphereRot, spherePos). The records are in the shape's root space and
    // hit.rot is the 3x3 of X * shape transform (R+0x10).
    public static bool CollideSphereShape(ShapeInstance inst, int detail, bool meshMode, ReadOnlySpan<float> shapeRot,
                                          ReadOnlySpan<float> shapePos, ReadOnlySpan<float> sphereRot, ReadOnlySpan<float> spherePos,
                                          ReadOnlySpan<float> centre, float radius, CollisionHit hit)
    {
        hit.Surfaces.Clear();
        hit.StepHeight = -1.0f;
        // R+0x10 = X * shape image transform, rel = sphere image transform * its inverse.
        var shape = new TMat3 { Flags = 3, M = Float9.Of(shapeRot), P = Float3.Of(shapePos) };
        Shape.TmatMul(inst.Root, shape, out TMat3 toWorld);
        Shape.TmatInverse(toWorld, out TMat3 inv);
        var own = new TMat3 { Flags = 3, M = Float9.Of(sphereRot), P = Float3.Of(spherePos) };
        Shape.TmatMul(own, inv, out TMat3 rel);
        hit.Rot = toWorld.M;
        var contacts = new List<ShapeContact>();
        if (!inst.CollideSphere(detail, centre, meshMode, rel, radius, contacts))
            return false;
        foreach (ShapeContact c in contacts)
        {
            var s = new CollisionSurface();
            s.Position = c.Position;
            s.Normal = c.Normal;
            s.Distance = c.Distance;
            // Mesh records carry the face's material entry (friction 1.0 in every stock list, 8 E6); bounds records have
            // none.
            s.HasMaterial = meshMode;
            s.Friction = 1.0f;
            hit.Surfaces.Add(s);
        }
        return true;
    }

    // src/world/collision.cpp collideBoxShape: box image against a shape (0x00435f60 -> 0x005c2264, T6): `box` is min,
    // max in the space of the box image transform.
    public static bool CollideBoxShape(ShapeInstance inst, int detail, ReadOnlySpan<float> shapeRot, ReadOnlySpan<float> shapePos,
                                       ReadOnlySpan<float> boxRot, ReadOnlySpan<float> boxPos, ReadOnlySpan<float> box)
    {
        // rel as in collideSphereShape; then 0x00435f60: half extents, the box centre carried into the translation by
        // the rotation alone.
        var shape = new TMat3 { Flags = 3, M = Float9.Of(shapeRot), P = Float3.Of(shapePos) };
        Shape.TmatMul(inst.Root, shape, out TMat3 toWorld);
        Shape.TmatInverse(toWorld, out TMat3 inv);
        var own = new TMat3 { Flags = 3, M = Float9.Of(boxRot), P = Float3.Of(boxPos) };
        Shape.TmatMul(own, inv, out TMat3 rel);
        Span<float> half = stackalloc float[3], centre = stackalloc float[3], moved = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
        {
            half[i] = X87.F32(Mul(X87.F32(Sub(box[i + 3], box[i])), 0.5f));
            centre[i] = X87.F32(Add(box[i], half[i]));
        }
        X87.MulVector(centre, rel.M, moved);
        for (int i = 0; i < 3; ++i)
            rel.P[i] = X87.F32(Add(moved[i], rel.P[i]));
        rel.Flags |= 2;
        return inst.CollideBox(detail, half, rel);
    }

    // src/world/collision.cpp testImage: image test of one candidate (0x005036a4 with the Player image as the mover).
    public static bool TestImage(MoverImage mover, CollisionImage image, CollisionHit hit)
    {
        hit.Object = image.Object;
        hit.Type = image.Type;
        hit.StepHeight = -1.0f;
        hit.Surfaces.Clear();
        switch (image.ImageKind)
        {
            case CollisionImage.Kind.Terrain:
                return image.TerrainData != null && CollideTerrain(image.TerrainData, mover.Pos, mover.Radius, hit);
            case CollisionImage.Kind.Interior:
                return image.Geometry != null &&
                       CollideInteriorBox(image.Geometry, image.Rot, image.Pos, mover.Pos, mover.BoxMin, mover.BoxMax, hit,
                                          mover.ShapeMover ? mover.Rot : default(ReadOnlySpan<float>));
            case CollisionImage.Kind.Shape:
            {
                if (image.Instance != null)
                {
                    ReadOnlySpan<float> own = stackalloc float[3] { 0, 0, mover.Radius };
                    return CollideSphereShape(image.Instance, image.Detail, image.MeshMode, image.Rot, image.Pos, mover.Rot,
                                              mover.Pos, mover.ShapeMover ? mover.CentreLocal : own, mover.Radius, hit);
                }
                Span<float> centre = stackalloc float[3] { mover.Pos[0], mover.Pos[1], X87.F32(Add(mover.Radius, mover.Pos[2])) };
                if (mover.ShapeMover)
                    X87.MulPoint(mover.CentreLocal, mover.Rot, mover.Pos, centre);
                if (image.Boxes == null)
                    return CollideSphereBox(image.Box, image.Rot, image.Pos, centre, mover.Radius, hit);
                // Collision detail: every box mesh in candidate order, records appended.
                var all = new List<CollisionSurface>();
                foreach (Float6 b in image.Boxes)
                    if (CollideSphereBox(b, image.Rot, image.Pos, centre, mover.Radius, hit))
                        all.AddRange(hit.Surfaces);
                hit.Surfaces = all;
                return hit.Surfaces.Count != 0;
            }
            case CollisionImage.Kind.Sphere:
            {
                // A ShapeBase mover against a Player: the Player image runs the test with its sphere against the mover's
                // shape (0x00436ed0).
                if (mover.ShapeMover && mover.Instance != null)
                    return CollideSphereShape(mover.Instance, mover.Detail, mover.MeshMode, mover.Rot, mover.Pos, image.Rot,
                                              image.Pos, image.Centre, image.Radius, hit);
                Span<float> c1 = stackalloc float[3] { mover.Pos[0], mover.Pos[1], X87.F32(Add(mover.Radius, mover.Pos[2])) };
                if (mover.ShapeMover)
                    X87.MulPoint(mover.CentreLocal, mover.Rot, mover.Pos, c1);
                Span<float> c2 = stackalloc float[3];
                X87.MulPoint(image.Centre, image.Rot, image.Pos, c2);
                return CollideSpheres(c1, mover.Radius, c2, image.Radius, mover.Rot, hit);
            }
            case CollisionImage.Kind.TriggerSphere:
                // PlayerCollisionImage_collide case 0, ShapeCollisionImage_collide case 0: the mover's shape against the
                // sphere (T3, T2.3).
                return mover.Instance != null &&
                       CollideSphereShape(mover.Instance, mover.Detail, mover.MeshMode, mover.Rot, mover.Pos, image.Rot,
                                          image.Pos, image.Centre, image.Radius, hit);
            case CollisionImage.Kind.Trigger:
            {
                // PlayerCollisionImage_collide case 1 (0x00435f60), ShapeCollisionImage_collide case 1: the mover's shape
                // against the box image (T6).
                if (mover.Instance != null)
                    return CollideBoxShape(mover.Instance, mover.Detail, mover.Rot, mover.Pos, image.Rot, image.Pos, image.Box);
                ReadOnlySpan<float> centre = stackalloc float[3] { mover.Pos[0], mover.Pos[1], X87.F32(Add(mover.Radius, mover.Pos[2])) };
                return SphereTouchesBox(image.Box, image.Pos, centre, mover.Radius);
            }
        }
        return false;
    }

    // src/world/collision.cpp sweptBox: candidate gate (2.1 steps 1-2): the world box at the target extended by v * dt.
    public static void SweptBox(ReadOnlySpan<float> boxMin, ReadOnlySpan<float> boxMax, ReadOnlySpan<float> target,
                                ReadOnlySpan<float> v, float dt, Span<float> output)
    {
        for (int i = 0; i < 3; ++i)
        {
            float lo = X87.F32(Add(boxMin[i], target[i]));
            float hi = X87.F32(Add(boxMax[i], target[i]));
            float a = X87.F32(Add(lo, Mul(v[i], dt)));
            float b = X87.F32(Add(hi, Mul(v[i], dt)));
            output[i] = a < lo ? a : lo;
            output[i + 3] = hi < b ? b : hi;
        }
    }

    // src/world/collision.cpp interiorWorldBox: InteriorShape world box, geometry box through m_Box3F_transform
    // (0x005edbb4: rotated centre, |M| applied to the half extents), padded by 3.0 (2.2).
    public static void InteriorWorldBox(ReadOnlySpan<float> local, ReadOnlySpan<float> rot, ReadOnlySpan<float> pos, Span<float> output)
    {
        Span<float> c = stackalloc float[3], e = stackalloc float[3], wc = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
        {
            float s = X87.F32(Add(local[i], local[i + 3]));
            c[i] = X87.F32(Mul(s, 0.5f));
            float d = X87.F32(Sub(local[i + 3], local[i]));
            e[i] = X87.F32(Mul(d, 0.5f));
        }
        X87.MulVector(c, rot, wc);
        for (int j = 0; j < 3; ++j)
        {
            double acc = Mul(Math.Abs((double)rot[j]), e[0]);
            acc = Add(Mul(Math.Abs((double)rot[3 + j]), e[1]), acc);
            float ext = X87.F32(Add(Mul(Math.Abs((double)rot[6 + j]), e[2]), acc));
            float lo = X87.F32(Sub(wc[j], ext));
            float hi = X87.F32(Add(ext, wc[j]));
            lo = X87.F32(Add(pos[j], lo));
            hi = X87.F32(Add(pos[j], hi));
            output[j] = X87.F32(Sub(lo, 3.0f)); // 0x0061db9c
            output[j + 3] = X87.F32(Add(hi, 3.0f));
        }
    }

    // src/world/collision.cpp boxesOverlap.
    public static bool BoxesOverlap(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        for (int i = 0; i < 3; ++i)
            if (a[i + 3] < b[i] || b[i + 3] < a[i])
                return false;
        return true;
    }
}
