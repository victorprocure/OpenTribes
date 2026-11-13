namespace OpenTribes.World;

using static OpenTribes.Core.Fpu;

/// <summary>x87 helpers shared by the movement code.</summary>
public static class X87
{
    /// <summary>Sets round-toward-zero for a scope, what crt_ftol leaves in the original.</summary>
    /// <returns>The scope. Dispose it to restore the previous mode.</returns>
    public static Chop Chop() => ChopScope();

    /// <summary>Stores a double as float toward zero, whatever the current rounding mode.</summary>
    /// <param name="x">The value.</param>
    /// <returns>The float.</returns>
    public static float F32(double x) => F32Chop(x);

    /// <summary>Computes a dot product without rounding the result (005ece49).</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>The dot product as a double.</returns>
    public static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) =>
        Add(Add(Mul(a[0], b[0]), Mul(a[1], b[1])), Mul(a[2], b[2]));

    /// <summary>Computes a vector length without rounding the result (005ed014).</summary>
    /// <param name="a">The vector.</param>
    /// <returns>The length as a double.</returns>
    public static double Len(ReadOnlySpan<float> a) => Sqrt(Dot(a, a));

    /// <summary>Computes a vector length with the approximate square root of m_Point3F_lenf (005ed031).</summary>
    /// <param name="a">The vector.</param>
    /// <returns>The length.</returns>
    /// <remarks>The result is seeded from bits(f32(len^2)) >> 1 + 0x1fc00000.</remarks>
    public static float Lenf(ReadOnlySpan<float> a)
    {
        var s = F32(Add(Add(Mul(a[0], a[0]), Mul(a[1], a[1])), Mul(a[2], a[2])));
        var bits = BitConverter.SingleToUInt32Bits(s);
        bits = (bits >> 1) + 0x1fc00000u;
        return BitConverter.UInt32BitsToSingle(bits);
    }

    /// <summary>Multiplies a vector by a row-vector 3x3 matrix, m_mul_vector (005ed124).</summary>
    /// <param name="v">The vector.</param>
    /// <param name="m">The 3x3 matrix.</param>
    /// <param name="output">Receives the product.</param>
    public static void MulVector(ReadOnlySpan<float> v, ReadOnlySpan<float> m, Span<float> output)
    {
        var r0 = F32(Add(Add(Mul(v[0], m[0]), Mul(v[1], m[3])), Mul(v[2], m[6])));
        var r1 = F32(Add(Add(Mul(v[0], m[1]), Mul(v[1], m[4])), Mul(v[2], m[7])));
        var r2 = F32(Add(Add(Mul(v[0], m[2]), Mul(v[1], m[5])), Mul(v[2], m[8])));
        output[0] = r0;
        output[1] = r1;
        output[2] = r2;
    }

    /// <summary>Multiplies a point by a row-vector 3x3 matrix and adds a translation, m_mul_point (005ed17e).</summary>
    /// <param name="v">The point.</param>
    /// <param name="m">The 3x3 matrix.</param>
    /// <param name="t">The translation.</param>
    /// <param name="output">Receives the transformed point.</param>
    public static void MulPoint(ReadOnlySpan<float> v, ReadOnlySpan<float> m, ReadOnlySpan<float> t, Span<float> output)
    {
        var r0 = F32(Add(Add(Add(Mul(v[0], m[0]), Mul(v[1], m[3])), Mul(v[2], m[6])), t[0]));
        var r1 = F32(Add(Add(Mul(v[0], m[1]), Mul(v[1], m[4])), Add(Mul(v[2], m[7]), t[1])));
        var r2 = F32(Add(Add(Mul(v[0], m[2]), Mul(v[1], m[5])), Add(Mul(v[2], m[8]), t[2])));
        output[0] = r0;
        output[1] = r1;
        output[2] = r2;
    }
}

/// <summary>A surface record the player reads after a collision (docs/collision.md 1.3).</summary>
public struct CollisionSurface
{
    /// <summary>The contact position.</summary>
    [System.Diagnostics.CodeAnalysis.UnscopedRef] public ref Float3 Position => ref _position;
    private Float3 _position;
    /// <summary>The surface normal, in the space of <see cref="CollisionHit.Rot"/>.</summary>
    [System.Diagnostics.CodeAnalysis.UnscopedRef] public ref Float3 Normal => ref _normal;
    private Float3 _normal;
    /// <summary>The contact distance (+0x1c). A negative value makes the player ignore the contact.</summary>
    public float Distance { get; set; }
    /// <summary>Whether the surface has a material entry (+0x20 is nonzero).</summary>
    public bool HasMaterial { get; set; }
    /// <summary>The friction of the material entry (+0x40).</summary>
    public float Friction { get; set; }
    /// <summary>The material index in the .dml of the .dis for an interior surface, -1 for none.</summary>
    public int Material { get; set; }

    /// <summary>Creates a surface with no material.</summary>
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

/// <summary>The result of testing one candidate: the surfaces hit and the space they are in (docs/collision.md 1.2).</summary>
public sealed class CollisionHit
{
    /// <summary>The sim object that was hit.</summary>
    public object? Object { get; set; }
    /// <summary>The object type bits (+0x48).</summary>
    public uint Type { get; set; }
    /// <summary>The 3x3 that maps surface space to world space (R+0x10).</summary>
    public ref Float9 Rot => ref _rot;
    private Float9 _rot = Float9.Identity();
    /// <summary>The step height (R+0x80), -1 when unset.</summary>
    public float StepHeight { get; set; } = -1.0f;
    /// <summary>The surfaces hit.</summary>
    public List<CollisionSurface> Surfaces { get; set; } = [];
}

/// <summary>The collision image of a candidate object (docs/collision.md 2.1 step 3).</summary>
public sealed class CollisionImage
{
    /// <summary>The kind of a collision image.</summary>
    /// <remarks><c>Sphere</c> is a player image (5.3). <c>TriggerSphere</c> is the sphere image of a StaticBase with a trigger radius (type 0, StaticBase_getCollisionImages 0044fff0): the mover's shape is tested against it.</remarks>
    public enum Kind
    {
        /// <summary>A terrain.</summary>
        Terrain,
        /// <summary>An interior.</summary>
        Interior,
        /// <summary>A shape.</summary>
        Shape,
        /// <summary>A player sphere.</summary>
        Sphere,
        /// <summary>A trigger box.</summary>
        Trigger,
        /// <summary>A trigger sphere.</summary>
        TriggerSphere,
    }
    /// <summary>The kind of this image.</summary>
    public Kind ImageKind { get; set; } = Kind.Terrain;
    /// <summary>The sim object that owns the image.</summary>
    public object? Object { get; set; }
    /// <summary>The object type bits.</summary>
    public uint Type { get; set; }
    /// <summary>Whether the image only posts an event and never blocks (image byte +0xd).</summary>
    public bool NonBlocking { get; set; }
    /// <summary>The terrain, for a terrain image.</summary>
    public Terrain? TerrainData { get; set; }
    /// <summary>The interior geometry, for an interior image.</summary>
    public InteriorGeometry? Geometry { get; set; }
    /// <summary>The image transform, the object transform.</summary>
    public ref Float9 Rot => ref _rot;
    private Float9 _rot = Float9.Identity();
    /// <summary>The image position.</summary>
    public ref Float3 Pos => ref _pos;
    private Float3 _pos;
    /// <summary>The object-space box of a shape or trigger image.</summary>
    public ref Float6 Box => ref _box;
    private Float6 _box;
    /// <summary>The box meshes of the shape's collision detail, when it has one.</summary>
    public List<Float6>? Boxes { get; set; }
    /// <summary>The sphere centre in image space, for a sphere or trigger sphere image.</summary>
    public ref Float3 Centre => ref _centre;
    private Float3 _centre;
    /// <summary>The sphere radius.</summary>
    public float Radius { get; set; }
    /// <summary>The shape instance with its thread state, for a shape image (docs/collision.md T1.2). Without one the box stand-ins are used, as in synthetic unit-test images.</summary>
    public ShapeInstance? Instance { get; set; }
    /// <summary>The detail the sphere test runs on (image +0x74).</summary>
    public int Detail { get; set; }
    /// <summary>The image mode (+0x44): true for faces, false for the bounds mode of items.</summary>
    public bool MeshMode { get; set; } = true;
}

/// <summary>The mover's image, a player image (docs/player-sim.md 3.1).</summary>
/// <remarks>The terrain, interior and shape cases use only the translation of the transform.</remarks>
public sealed class MoverImage
{
    /// <summary>The mover position.</summary>
    public ref Float3 Pos => ref _pos;
    private Float3 _pos;
    /// <summary>The mover rotation.</summary>
    public ref Float9 Rot => ref _rot;
    private Float9 _rot = Float9.Identity();
    /// <summary>The corners of box A.</summary>
    public ref Float3 BoxMin => ref _boxMin;
    private Float3 _boxMin;
    public ref Float3 BoxMax => ref _boxMax;
    private Float3 _boxMax;
    /// <summary>The sphere radius. The sphere centre is (0, 0, radius).</summary>
    public float Radius { get; set; }
    /// <summary>Whether the mover is a ShapeBase such as an item (docs/collision.md T3). The shape image rotates and the terrain case is not covered, since item code keeps its own (00437b50).</summary>
    public bool ShapeMover { get; set; }
    /// <summary>The bounding sphere centre in shape space, rotated into the world through the image rotation.</summary>
    public ref Float3 CentreLocal => ref _centreLocal;
    private Float3 _centreLocal;
    /// <summary>The shape image of a ShapeBase mover. A player candidate tests its own sphere against it, since the image with the higher type runs the test (005036a4).</summary>
    public ShapeInstance? Instance { get; set; }
    /// <summary>The detail the shape image is tested on.</summary>
    public int Detail { get; set; }
    /// <summary>The image mode of the shape image, true for faces.</summary>
    public bool MeshMode { get; set; } = true;
}

/// <summary>The state of a stepped move (docs/collision.md section 3, 00505a3c).</summary>
public sealed class MoveInfo
{
    /// <summary>The current iteration.</summary>
    public int Iteration { get; set; }
    /// <summary>The time used so far (+0x04).</summary>
    public float Used { get; set; }
    /// <summary>The end time (+0x0c).</summary>
    public float EndTime { get; set; }
    /// <summary>The object position.</summary>
    public float[]? Current { get; set; }
    /// <summary>The image position of the blocked substep.</summary>
    public float[]? Attempted { get; set; }
    /// <summary>The target position. The collision callback rewrites it.</summary>
    public float[]? Target { get; set; }
    /// <summary>The candidate images.</summary>
    public List<CollisionImage>? Candidates { get; set; }
    /// <summary>The hits of the blocked substep.</summary>
    public List<CollisionHit>? Hits { get; set; }
    /// <summary>The sim objects to notify.</summary>
    public List<object>? Notify { get; set; }
}

/// <summary>A stepped move of a mover through a list of candidate images.</summary>
public sealed class SteppedMove
{
    /// <summary>The time slice in seconds (+0x168).</summary>
    public float Dt { get; set; } = 0.032f;
    /// <summary>The most blocked iterations a move runs (+0x1f0).</summary>
    public int MaxIterations { get; set; } = 3;
    /// <summary>The mover's image.</summary>
    public MoverImage Image { get; set; } = new();
    /// <summary>The candidate images.</summary>
    public List<CollisionImage> Candidates { get; set; } = [];
    /// <summary>The collision callback (vtable +0xb8). Returning true restarts the move toward the rewritten target.</summary>
    public Func<MoveInfo, bool>? OnCollision { get; set; }
    /// <summary>Called when a non-blocking image was hit (event 0x41f to its owner, 005070fc).</summary>
    public Action<object?>? OnTouch { get; set; }
    /// <summary>The notify list of the last move (info +0x3c).</summary>
    public List<object> Notify { get; set; } = [];

    /// <summary>Tests the mover image at a position against every candidate (00506bfc).</summary>
    /// <param name="q">The image position to test.</param>
    /// <param name="hits">Receives the blocking hits.</param>
    /// <returns>True when nothing blocks.</returns>
    /// <remarks>Non-blocking candidates that collide are reported and leave the list.</remarks>
    public bool TestAt(ReadOnlySpan<float> q, List<CollisionHit> hits)
    {
        hits.Clear();
        for (var k = 0; k < 3; ++k)
            Image.Pos[k] = q[k];
        for (var i = 0; i < Candidates.Count;)
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
                var owner = Candidates[i].Object;
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

    /// <summary>Moves a position toward a target.</summary>
    /// <param name="p">The position, updated in place.</param>
    /// <param name="target">The target position.</param>
    /// <param name="step0">The first substep length.</param>
    /// <param name="step1">The second substep length.</param>
    /// <returns>False after <see cref="MaxIterations"/> blocked iterations. The caller then zeroes the velocity.</returns>
    public bool Run(float[] p, float[] target, float step0, float step1)
    {
        Notify.Clear();
        var step = step0;
        var used = 0.0f;
        var hits = new List<CollisionHit>();
        var delta = new float[3];
        for (var it = 0; it < MaxIterations; ++it)
        {
            var q = new float[3];
            for (var i = 0; i < 3; ++i)
            {
                delta[i] = X87.F32(Sub(target[i], p[i]));
                q[i] = p[i];
            }
            var length = X87.F32(X87.Len(delta));
            // ftol(ceil(f64(L / step))). A count the original could not finish in a tick is bounded (D11): 2^20 substeps
            // is a move of more than 300 km.
            var quotient = Div(length, step);
            var up = Math.Ceiling(quotient);
            var n = up is > (-2147483649.0) and < 2147483648.0 ? (int)up : 0;
            if (n > (1 << 20))
                n = 1 << 20;
            var blocked = false;
            if (n > 0)
            {
                var k = X87.F32(Div(1.0, n));
                var d = new float[3];
                for (var i = 0; i < 3; ++i)
                    d[i] = X87.F32(Mul(k, delta[i]));
                for (var i = 0; i < n; ++i)
                {
                    for (var j = 0; j < 3; ++j)
                        q[j] = X87.F32(Add(d[j], q[j]));
                    if (TestAt(q, hits))
                    {
                        q.CopyTo(p, 0);
                        continue;
                    }
                    var slice = X87.F32(Div(Sub(Dt, used), n));
                    var info = new MoveInfo
                    {
                        Iteration = it,
                        EndTime = X87.F32(Add(Mul(i + 1, slice), used))
                    };
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

/// <summary>The collision tests a moving object runs: terrain plane, interior box clip, shape and player spheres, and the candidate gate.</summary>
/// <remarks>Port of src/world/collision.cpp (docs/collision.md). Uses the truncating form of docs/portable-float.md 1: x87::Chop is <c>Fpu.ChopScope</c>, x87::f32 stores toward zero whatever the mode, and every other double operation follows the mode of <c>Fpu</c>. Sim objects are <c>object</c> here because this project does not reference the Sim layer.</remarks>
public static class Collision
{
    private const float ClipEps = 1e-5f; // 0x0063f9a0 (0x3727c5ac)

    private static void Transpose(ReadOnlySpan<float> m, Span<float> output)
    {
        Span<float> t = [m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8]];
        t.CopyTo(output);
    }

    // T = A * inverse(image transform) with A = identity rotation at `pos` (5.2 B1-B5). With `moverRot` the rotation of A is
    // that matrix: T.R = A.R * R' (005ed2b2).
    private static void RelativeTransform(ReadOnlySpan<float> rot, ReadOnlySpan<float> imagePos, ReadOnlySpan<float> pos,
                                          Span<float> outRot, Span<float> outT, ReadOnlySpan<float> moverRot = default)
    {
        Span<float> inv = stackalloc float[9];
        Transpose(rot, inv);
        ReadOnlySpan<float> neg = [-imagePos[0], -imagePos[1], -imagePos[2]];
        Span<float> ti = stackalloc float[3];
        X87.MulVector(neg, inv, ti);
        for (var j = 0; j < 3; ++j)
            outT[j] = X87.F32(Add(Mul(pos[2], inv[6 + j]), Add(Add(Mul(pos[0], inv[j]), Mul(pos[1], inv[3 + j])), ti[j])));
        if (moverRot.IsEmpty)
        {
            inv.CopyTo(outRot);
            return;
        }
        for (var r = 0; r < 3; ++r)
            for (var c = 0; c < 3; ++c)
                outRot[(r * 3) + c] = X87.F32(Add(Add(Mul(moverRot[r * 3], inv[c]), Mul(moverRot[(r * 3) + 1], inv[3 + c])),
                                                Mul(moverRot[(r * 3) + 2], inv[6 + c])));
    }

    // ---- interior box clip (5.2 parts 2.3 - 2.7) ----

    private struct Record
    {
        public int PlaneIndex { get; set; } // -1: solid leaf
        public int Front { get; set; }
        public int Back { get; set; }
        public int Leaf { get; set; }

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
        public ref Float3 P => ref _p;
        private Float3 _p;
        public int Mark { get; set; }
        public bool Seen { get; set; }
    }

    private sealed class ClipFace
    {
        public int PlaneIndex { get; set; } = -1;
        public int LastVert { get; set; }
        public bool Side { get; set; }
    }

    private struct ClipEdge
    {
        public int V0 { get; set; }
        public int V1 { get; set; }
        public int F0 { get; set; }
        public int F1 { get; set; }

        public ClipEdge(int v0, int v1, int f0, int f1)
        {
            V0 = v0;
            V1 = v1;
            F0 = f0;
            F1 = f1;
        }
    }

    private sealed class BoxClip
    {
        public InteriorGeometry Geo { get; }
        public ref Float3 C => ref _c;
        private Float3 _c;
        public float R { get; set; }
        public ref Float3 T => ref _t; // T.t
        private Float3 _t; // T.t
        public ref Float3 Up => ref _up;
        private Float3 _up;
        public List<Record> Records { get; } = [];
        public List<ClipVertex> Verts { get; } = [];
        public List<ClipFace> Faces { get; } = [];
        public int Counter { get; set; }
        public bool Touched { get; set; }
        public float MaxHeight { get; set; }
        public CollisionHit Hit { get; }

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
                var leaf = -(child + 1);
                if (!Geo.IsSolid(leaf))
                    return -1;
                var rec = new Record
                {
                    Leaf = leaf
                };
                Records.Add(rec);
                return Records.Count - 1;
            }
            var nodes = Geo.Nodes();
            // A well-formed BSP is a tree: a path longer than the node count is a cycle (D11).
            if (child >= nodes.Count || depth > nodes.Count)
                return -1;
            var node = nodes[child];
            if (node.PlaneIndex >= Geo.Planes().Count)
                return -1;
            var pl = Geo.Planes()[node.PlaneIndex];
            ReadOnlySpan<float> n = [pl.Nx, pl.Ny, pl.Nz];
            var d = X87.F32(Add(X87.Dot(n, C), pl.D));
            if (Math.Abs(d) >= R)
                return Leaves(d >= 0.0f ? node.Front : node.Back, depth + 1);
            var f = Leaves(node.Front, depth + 1);
            var b = Leaves(node.Back, depth + 1);
            if (f < 0 && b < 0)
                return -1;
            var r = new Record
            {
                PlaneIndex = node.PlaneIndex,
                Front = f,
                Back = b
            };
            Records.Add(r);
            return Records.Count - 1;
        }

        // Leaf callback (0x00560f60).
        public void Leaf(int leafIndex, List<ClipEdge> edges)
        {
            Touched = true;
            Span<float> w = stackalloc float[3];
            foreach (var e in edges)
            {
                for (var which = 0; which < 2; ++which)
                {
                    var fi = which == 0 ? e.F0 : e.F1;
                    var f = Faces[fi];
                    if (f.PlaneIndex < 0)
                        continue;
                    var s = Geo.FindSurface(leafIndex, f.PlaneIndex);
                    if (s < 0)
                        continue;
                    var pl = Geo.Planes()[f.PlaneIndex];
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
                for (var which = 0; which < 2; ++which)
                {
                    var v = Verts[which == 0 ? e.V0 : e.V1];
                    if (v.Seen)
                        continue;
                    v.Seen = true;
                    for (var k = 0; k < 3; ++k)
                        w[k] = X87.F32(Sub(v.P[k], T[k]));
                    var h = X87.F32(X87.Dot(Up, w));
                    if (h > MaxHeight)
                        MaxHeight = h;
                }
            }
        }

        // clip (0x005632f0).
        public void Clip(int recordIndex, List<ClipEdge> edges)
        {
            var rec = Records[recordIndex];
            var pl = Geo.Planes()[rec.PlaneIndex];
            ReadOnlySpan<float> n = [pl.Nx, pl.Ny, pl.Nz];
            var savedVerts = Verts.Count;
            var kf = new ClipFace
            {
                PlaneIndex = rec.PlaneIndex
            };
            Faces.Add(kf);
            var fi = Faces.Count - 1;
            ++Counter;
            var mark = 2 * Counter;
            var front = new List<ClipEdge>();
            var back = new List<ClipEdge>();
            Span<float> dv = stackalloc float[3];
            foreach (var e in edges)
            {
                for (var which = 0; which < 2; ++which)
                {
                    var v = Verts[which == 0 ? e.V0 : e.V1];
                    if (v.Mark < mark)
                    {
                        var s = X87.F32(X87.Dot(n, v.P));
                        var isBack = !(Sub(s, ClipEps) > -(double)pl.D) && (-(double)pl.D > Add(s, ClipEps));
                        v.Mark = mark + (isBack ? 1 : 0);
                    }
                }
                int ma = Verts[e.V0].Mark, mb = Verts[e.V1].Mark;
                if (ma == mb)
                {
                    (ma == mark ? front : back).Add(e);
                    continue;
                }
                var v0Back = ma != mark;
                var a = Verts[e.V0].P;
                var b = Verts[e.V1].P;
                for (var k = 0; k < 3; ++k)
                    dv[k] = X87.F32(Sub(b[k], a[k]));
                var den = X87.Dot(n, dv);
                if (den != 0.0)
                {
                    var den32 = X87.F32(den);
                    var tt = X87.F32(Div(-Add(X87.Dot(n, a), pl.D), den32));
                    var nvx = new ClipVertex();
                    for (var i = 0; i < 3; ++i)
                        nvx.P[i] = X87.F32(Add(Mul(dv[i], tt), a[i]));
                    Verts.Add(nvx);
                }
                var nv = Verts.Count - 1;
                front.Add(new ClipEdge(v0Back ? e.V1 : e.V0, nv, e.F0, e.F1));
                back.Add(new ClipEdge(v0Back ? e.V0 : e.V1, nv, e.F0, e.F1));
                for (var which = 0; which < 2; ++which)
                {
                    var fj = which == 0 ? e.F0 : e.F1;
                    var f = Faces[fj];
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
                var child = Records[rec.Front];
                if (child.PlaneIndex < 0)
                    Leaf(child.Leaf, front);
                else
                    Clip(rec.Front, front);
            }
            if (rec.Back >= 0 && back.Count != 0)
            {
                Faces[fi].Side = false;
                var child = Records[rec.Back];
                if (child.PlaneIndex < 0)
                    Leaf(child.Leaf, back);
                else
                    Clip(rec.Back, back);
            }
            Faces.RemoveAt(Faces.Count - 1);
        }
    }

    // Sphere against one face (section 6 T4.1 steps 1-8). Appends at most one surface.
    private static void SphereFace(ReadOnlySpan<float> pm, float r, ReadOnlySpan<float> a, ReadOnlySpan<float> b,
                                   ReadOnlySpan<float> c, List<CollisionSurface> output)
    {
        var s = new CollisionSurface();
        var distance = s.Distance;
        var hit = Shape.SphereTriangle(pm, r, a, b, c, s.Position, s.Normal, ref distance);
        s.Distance = distance;
        if (!hit)
            return;
        s.HasMaterial = true;
        s.Friction = 1.0f;
        output.Add(s);
    }

    /// <summary>Tests a player against terrain (docs/collision.md 5.1, 00436020).</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="pos">The player position in world space.</param>
    /// <param name="radius">The player sphere radius.</param>
    /// <param name="hit">Receives the hit. One surface with the signed plane distance.</param>
    /// <returns>True on a collision.</returns>
    public static bool CollideTerrain(Terrain terrain, ReadOnlySpan<float> pos, float radius, CollisionHit hit)
    {
        // 5.1 steps 2-3 with an unrotated terrain: M.t = P + (-terrainPos), c = (0, 0, r) * M.
        var tp = terrain.Position();
        ReadOnlySpan<float> neg = [-tp.X, -tp.Y, -tp.Z];
        Span<float> c = stackalloc float[3];
        for (var i = 0; i < 3; ++i)
            c[i] = X87.F32(Add(pos[i], neg[i]));
        c[2] = X87.F32(Add(radius, c[2]));
        float h = 0;
        Span<float> n = stackalloc float[3];
        byte material = 0;
        if (!terrain.SurfaceInfo(c[0], c[1], ref h, n, ref material))
            return false;
        ReadOnlySpan<float> point = [c[0], c[1], h];
        var a = X87.F32(X87.Dot(c, n));
        var b = X87.Dot(point, n);
        var d = X87.F32(Sub(a, b));
        if (!(Math.Abs((double)d) < (double)radius))
            return false;
        var s = new CollisionSurface
        {
            Position = Float3.Of(point),
            Normal = Float3.Of(n),
            Distance = d,
            HasMaterial = true, // terrain material entry: friction 1.0 on stock data (8 E6)
            Friction = 1.0f
        };
        hit.Surfaces.Clear();
        hit.Surfaces.Add(s);
        hit.StepHeight = -1.0f;
        hit.Rot = Float9.Identity();
        return true;
    }

    /// <summary>Tests a player box against an interior (docs/collision.md 5.2, 004367d0 to ITRCollision_collideBox 00560b54).</summary>
    /// <param name="geo">The interior geometry.</param>
    /// <param name="rot">The interior rotation.</param>
    /// <param name="interiorPos">The interior position.</param>
    /// <param name="pos">The mover position.</param>
    /// <param name="boxMin">The box minimum.</param>
    /// <param name="boxMax">The box maximum.</param>
    /// <param name="hit">Receives the hit surfaces.</param>
    /// <param name="moverRot">The mover image's rotation. An empty span is the identity of the player case.</param>
    /// <returns>True on a collision. True with zero surfaces when the box is inside solid without a listed surface plane.</returns>
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
        for (var i = 0; i < 3; ++i)
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
        var root = bc.Leaves(0, 0);
        if (root < 0)
            return false;
        if (bc.Records[root].PlaneIndex < 0)
            return true; // one solid leaf: inside solid, no surface (R+0x84)
        // 2.4: the box as a polyhedron in geometry space.
        Span<float> ext = stackalloc float[3], axis = stackalloc float[9], o = stackalloc float[3];
        for (var i = 0; i < 3; ++i)
        {
            ext[i] = X87.F32(Sub(boxMax[i], boxMin[i]));
            for (var j = 0; j < 3; ++j)
                axis[(i * 3) + j] = X87.F32(Mul(ext[i], m[(i * 3) + j]));
        }
        X87.MulPoint(boxMin, m, bt, o);
        static void AddV(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> output)
        {
            for (var j = 0; j < 3; ++j)
                output[j] = X87.F32(Add(a[j], b[j]));
        }
        for (var i = 0; i < 8; ++i)
            bc.Verts.Add(new ClipVertex());
        Span<float> tmp = stackalloc float[3], ox = stackalloc float[3];
        bc.Verts[0].P = Float3.Of(o);
        AddV(o, axis.Slice(3, 3), tmp);
        bc.Verts[1].P = Float3.Of(tmp);
        AddV(o, axis[..3], ox);
        AddV(ox, axis.Slice(3, 3), tmp);
        bc.Verts[2].P = Float3.Of(tmp);
        bc.Verts[3].P = Float3.Of(ox);
        for (var i = 0; i < 4; ++i)
        {
            AddV(bc.Verts[i].P, axis.Slice(6, 3), tmp);
            bc.Verts[i + 4].P = Float3.Of(tmp);
        }
        for (var i = 0; i < 6; ++i)
            bc.Faces.Add(new ClipFace());
        var edges = new List<ClipEdge>();
        for (var i = 0; i < 4; ++i)
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

    /// <summary>Tests a player sphere against another player sphere (docs/collision.md 5.3, 00437414).</summary>
    /// <param name="c1">The first sphere centre in world space.</param>
    /// <param name="r1">The first sphere radius.</param>
    /// <param name="c2">The second sphere centre in world space.</param>
    /// <param name="r2">The second sphere radius.</param>
    /// <param name="rot">The own image rotation.</param>
    /// <param name="hit">Receives the hit. The normal is (c1 - c2) / lenf.</param>
    /// <returns>True on a collision.</returns>
    public static bool CollideSpheres(ReadOnlySpan<float> c1, float r1, ReadOnlySpan<float> c2, float r2, ReadOnlySpan<float> rot,
                                      CollisionHit hit)
    {
        // 5.3: the difference in the own image space (world difference through the inverse of the own rotation),
        // length through lenf.
        ReadOnlySpan<float> d =
        [
            X87.F32(Sub(c1[0], c2[0])), X87.F32(Sub(c1[1], c2[1])), X87.F32(Sub(c1[2], c2[2])),
        ];
        Span<float> inv = stackalloc float[9], w = stackalloc float[3];
        Transpose(rot, inv);
        X87.MulVector(d, inv, w);
        var l = X87.F32(X87.Lenf(w));
        if (!(Add(r1, r2) > l))
            return false;
        var s = new CollisionSurface();
        var k = X87.F32(Div(1.0, l));
        for (var i = 0; i < 3; ++i)
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

    /// <summary>Tests a sphere against a box for overlap only, used for trigger images.</summary>
    /// <param name="box">The box, min xyz then max xyz.</param>
    /// <param name="boxPos">The box position.</param>
    /// <param name="centre">The sphere centre.</param>
    /// <param name="radius">The sphere radius.</param>
    /// <returns>True when they overlap.</returns>
    public static bool SphereTouchesBox(ReadOnlySpan<float> box, ReadOnlySpan<float> boxPos, ReadOnlySpan<float> centre, float radius)
    {
        float d2 = 0;
        for (var i = 0; i < 3; ++i)
        {
            var p = X87.F32(Sub(centre[i], boxPos[i]));
            if (p < box[i])
                d2 = X87.F32(Add(Mul(Sub(p, box[i]), Sub(p, box[i])), d2));
            else if (p > box[i + 3])
                d2 = X87.F32(Add(Mul(Sub(p, box[i + 3]), Sub(p, box[i + 3])), d2));
        }
        return d2 < X87.F32(Mul(radius, radius));
    }

    /// <summary>Tests a player sphere against the object-space box of a shape (docs/collision.md 5.4, approximated).</summary>
    /// <param name="box">The object-space box.</param>
    /// <param name="rot">The shape rotation.</param>
    /// <param name="shapePos">The shape position.</param>
    /// <param name="centre">The sphere centre.</param>
    /// <param name="radius">The sphere radius.</param>
    /// <param name="hit">Receives the hit surfaces.</param>
    /// <returns>True on a collision.</returns>
    /// <remarks>Uses the mesh rules of section 6 T4.1 with two triangles per box face.</remarks>
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
        for (var i = 0; i < 3; ++i)
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
        ReadOnlySpan<int> tris = [0, 1, 2, 0, 2, 3];
        for (var axis = 0; axis < 3; ++axis)
        {
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            for (var side = 0; side < 2; ++side)
            {
                for (var k = 0; k < 4; ++k)
                {
                    corner[(k * 3) + axis] = box[axis + (side != 0 ? 3 : 0)];
                    corner[(k * 3) + u] = box[u + ((k is 1 or 2) ? 3 : 0)];
                    corner[(k * 3) + v] = box[v + ((k >= 2) ? 3 : 0)];
                }
                // corner order 0,1,2,3 is counter-clockwise seen from +axis.
                for (var ti = 0; ti < 2; ++ti)
                {
                    var tri = tris.Slice(ti * 3, 3);
                    ReadOnlySpan<float> a = corner.Slice(tri[side != 0 ? 1 : 0] * 3, 3);
                    ReadOnlySpan<float> b = corner.Slice(tri[side != 0 ? 0 : 1] * 3, 3);
                    ReadOnlySpan<float> c = corner.Slice(tri[2] * 3, 3);
                    SphereFace(pm, radius, a, b, c, hit.Surfaces);
                }
            }
        }
        return hit.Surfaces.Count != 0;
    }

    /// <summary>Tests a sphere against a shape image (PlayerCollisionImage_collideShape 00436ed0, ShapeCollisionImage_collideShape 0043873c).</summary>
    /// <param name="inst">The shape instance.</param>
    /// <param name="detail">The detail to test.</param>
    /// <param name="meshMode">True for face records, false for bounds records.</param>
    /// <param name="shapeRot">The shape rotation.</param>
    /// <param name="shapePos">The shape position.</param>
    /// <param name="sphereRot">The rotation of the sphere's own image.</param>
    /// <param name="spherePos">The position of the sphere's own image.</param>
    /// <param name="centre">The sphere centre in the space of the sphere's own image.</param>
    /// <param name="radius">The sphere radius.</param>
    /// <param name="hit">Receives the hit surfaces.</param>
    /// <returns>True on a collision.</returns>
    /// <remarks>The records are in the shape's root space. <c>hit.Rot</c> is the 3x3 of X * shape transform (R+0x10).</remarks>
    public static bool CollideSphereShape(ShapeInstance inst, int detail, bool meshMode, ReadOnlySpan<float> shapeRot,
                                          ReadOnlySpan<float> shapePos, ReadOnlySpan<float> sphereRot, ReadOnlySpan<float> spherePos,
                                          ReadOnlySpan<float> centre, float radius, CollisionHit hit)
    {
        hit.Surfaces.Clear();
        hit.StepHeight = -1.0f;
        // R+0x10 = X * shape image transform, rel = sphere image transform * its inverse.
        var shape = new TMat3 { Flags = 3, M = Float9.Of(shapeRot), P = Float3.Of(shapePos) };
        Shape.TmatMul(inst.Root, shape, out var toWorld);
        Shape.TmatInverse(toWorld, out var inv);
        var own = new TMat3 { Flags = 3, M = Float9.Of(sphereRot), P = Float3.Of(spherePos) };
        Shape.TmatMul(own, inv, out var rel);
        hit.Rot = toWorld.M;
        var contacts = new List<ShapeContact>();
        if (!inst.CollideSphere(detail, centre, meshMode, rel, radius, contacts))
            return false;
        foreach (var c in contacts)
        {
            var s = new CollisionSurface
            {
                Position = c.Position,
                Normal = c.Normal,
                Distance = c.Distance,
                // Mesh records carry the face's material entry (friction 1.0 in every stock list, 8 E6); bounds records have
                // none.
                HasMaterial = meshMode,
                Friction = 1.0f
            };
            hit.Surfaces.Add(s);
        }
        return true;
    }

    /// <summary>Tests a box image against a shape (00435f60 to 005c2264, docs/collision.md T6).</summary>
    /// <param name="inst">The shape instance.</param>
    /// <param name="detail">The detail to test.</param>
    /// <param name="shapeRot">The shape rotation.</param>
    /// <param name="shapePos">The shape position.</param>
    /// <param name="boxRot">The rotation of the box image.</param>
    /// <param name="boxPos">The position of the box image.</param>
    /// <param name="box">The box, min then max, in the space of the box image transform.</param>
    /// <returns>True on a collision.</returns>
    public static bool CollideBoxShape(ShapeInstance inst, int detail, ReadOnlySpan<float> shapeRot, ReadOnlySpan<float> shapePos,
                                       ReadOnlySpan<float> boxRot, ReadOnlySpan<float> boxPos, ReadOnlySpan<float> box)
    {
        // rel as in collideSphereShape; then 0x00435f60: half extents, the box centre carried into the translation by
        // the rotation alone.
        var shape = new TMat3 { Flags = 3, M = Float9.Of(shapeRot), P = Float3.Of(shapePos) };
        Shape.TmatMul(inst.Root, shape, out var toWorld);
        Shape.TmatInverse(toWorld, out var inv);
        var own = new TMat3 { Flags = 3, M = Float9.Of(boxRot), P = Float3.Of(boxPos) };
        Shape.TmatMul(own, inv, out var rel);
        Span<float> half = stackalloc float[3], centre = stackalloc float[3], moved = stackalloc float[3];
        for (var i = 0; i < 3; ++i)
        {
            half[i] = X87.F32(Mul(X87.F32(Sub(box[i + 3], box[i])), 0.5f));
            centre[i] = X87.F32(Add(box[i], half[i]));
        }
        X87.MulVector(centre, rel.M, moved);
        for (var i = 0; i < 3; ++i)
            rel.P[i] = X87.F32(Add(moved[i], rel.P[i]));
        rel.Flags |= 2;
        return inst.CollideBox(detail, half, rel);
    }

    /// <summary>Tests one candidate image against the mover (005036a4 with the player image as the mover).</summary>
    /// <param name="mover">The mover image.</param>
    /// <param name="image">The candidate image.</param>
    /// <param name="hit">Receives the hit surfaces.</param>
    /// <returns>True on a collision.</returns>
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
                    ReadOnlySpan<float> own = [0, 0, mover.Radius];
                    return CollideSphereShape(image.Instance, image.Detail, image.MeshMode, image.Rot, image.Pos, mover.Rot,
                                              mover.Pos, mover.ShapeMover ? mover.CentreLocal : own, mover.Radius, hit);
                }
                Span<float> centre = [mover.Pos[0], mover.Pos[1], X87.F32(Add(mover.Radius, mover.Pos[2]))];
                if (mover.ShapeMover)
                    X87.MulPoint(mover.CentreLocal, mover.Rot, mover.Pos, centre);
                if (image.Boxes == null)
                    return CollideSphereBox(image.Box, image.Rot, image.Pos, centre, mover.Radius, hit);
                // Collision detail: every box mesh in candidate order, records appended.
                var all = new List<CollisionSurface>();
                foreach (var b in image.Boxes)
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
                Span<float> c1 = [mover.Pos[0], mover.Pos[1], X87.F32(Add(mover.Radius, mover.Pos[2]))];
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
                ReadOnlySpan<float> centre = [mover.Pos[0], mover.Pos[1], X87.F32(Add(mover.Radius, mover.Pos[2]))];
                return SphereTouchesBox(image.Box, image.Pos, centre, mover.Radius);
            }
        }
        return false;
    }

    /// <summary>Computes the candidate gate box: the world box at the target extended by v * dt (docs/collision.md 2.1 steps 1-2).</summary>
    /// <param name="boxMin">The box minimum.</param>
    /// <param name="boxMax">The box maximum.</param>
    /// <param name="target">The target position.</param>
    /// <param name="v">The velocity.</param>
    /// <param name="dt">The time slice.</param>
    /// <param name="output">Receives the box, six floats.</param>
    public static void SweptBox(ReadOnlySpan<float> boxMin, ReadOnlySpan<float> boxMax, ReadOnlySpan<float> target,
                                ReadOnlySpan<float> v, float dt, Span<float> output)
    {
        for (var i = 0; i < 3; ++i)
        {
            var lo = X87.F32(Add(boxMin[i], target[i]));
            var hi = X87.F32(Add(boxMax[i], target[i]));
            var a = X87.F32(Add(lo, Mul(v[i], dt)));
            var b = X87.F32(Add(hi, Mul(v[i], dt)));
            output[i] = a < lo ? a : lo;
            output[i + 3] = hi < b ? b : hi;
        }
    }

    /// <summary>Computes the world box of an interior shape (docs/collision.md 2.2).</summary>
    /// <param name="local">The geometry box.</param>
    /// <param name="rot">The interior rotation.</param>
    /// <param name="pos">The interior position.</param>
    /// <param name="output">Receives the box, six floats.</param>
    /// <remarks>The geometry box goes through m_Box3F_transform (005edbb4): rotated centre, |M| applied to the half extents. The result is padded by 3.0.</remarks>
    public static void InteriorWorldBox(ReadOnlySpan<float> local, ReadOnlySpan<float> rot, ReadOnlySpan<float> pos, Span<float> output)
    {
        Span<float> c = stackalloc float[3], e = stackalloc float[3], wc = stackalloc float[3];
        for (var i = 0; i < 3; ++i)
        {
            var s = X87.F32(Add(local[i], local[i + 3]));
            c[i] = X87.F32(Mul(s, 0.5f));
            var d = X87.F32(Sub(local[i + 3], local[i]));
            e[i] = X87.F32(Mul(d, 0.5f));
        }
        X87.MulVector(c, rot, wc);
        for (var j = 0; j < 3; ++j)
        {
            var acc = Mul(Math.Abs((double)rot[j]), e[0]);
            acc = Add(Mul(Math.Abs((double)rot[3 + j]), e[1]), acc);
            var ext = X87.F32(Add(Mul(Math.Abs((double)rot[6 + j]), e[2]), acc));
            var lo = X87.F32(Sub(wc[j], ext));
            var hi = X87.F32(Add(ext, wc[j]));
            lo = X87.F32(Add(pos[j], lo));
            hi = X87.F32(Add(pos[j], hi));
            output[j] = X87.F32(Sub(lo, 3.0f)); // 0x0061db9c
            output[j + 3] = X87.F32(Add(hi, 3.0f));
        }
    }

    /// <summary>Tests whether two boxes overlap.</summary>
    /// <param name="a">The first box, min xyz then max xyz.</param>
    /// <param name="b">The second box, min xyz then max xyz.</param>
    /// <returns>True when they overlap.</returns>
    public static bool BoxesOverlap(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        for (var i = 0; i < 3; ++i)
            if (a[i + 3] < b[i] || b[i + 3] < a[i])
                return false;
        return true;
    }
}
