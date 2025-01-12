// Port of src/world/shape.hpp and src/world/shape.cpp: .dts shape bounds (docs/interiors.md 8.2-8.4), the shape model
// and instance with its line, sphere and box tests (docs/collision.md 6 T1-T8). Portable truncating form
// (docs/portable-float.md 1): the x87::Chop guards are Fpu.ChopScope, every double operation follows the mode of Fpu,
// x87::f32 is Fpu.F32Chop, a plain float store is Fpu.Store, and the x87 helpers fpatanLD, fptanLD, fldpiLD and
// fsincosLD are Math.Atan2, Math.Tan, the double pi and Math.Sin, Math.Cos (round to nearest).
using System.Runtime.CompilerServices;
using OpenTribes.Core;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.World;

[InlineArray(3)]
public struct Int3
{
    private int mElement;
}

[InlineArray(4)]
public struct Short4
{
    private short mElement;
}

// src/world/shape.hpp struct TMat3 (TMat3F, T1.1): flags 1 = rotation present, 2 = translation present, 4 = general
// matrix.
public struct TMat3
{
    public uint Flags;
    public Float9 M;
    public Float3 P;

    public TMat3()
    {
        Flags = 0;
        M = Float9.Identity();
        P = default;
    }
}

// src/world/shape.hpp struct ShapeSequence.
public sealed class ShapeSequence
{
    public string Name = "";
    public bool Cyclic;
    public float Duration;
}

// src/world/shape.hpp struct ShapeAnim: animation data the server uses (docs/interiors.md 8.2,
// docs/game-stream-ghosts.md 4.2): sequence names, cyclic flags and durations for the ShapeBase threads, and the node
// rotations a Turret reads for its elevation limits.
public sealed class ShapeAnim
{
    public struct Node
    {
        public int Name, Parent, NumSub, FirstSub, DefaultTransform;
    }
    public struct SubSequence
    {
        public int Sequence, NumKeys, FirstKey;
    }
    public struct Key
    {
        public float Position;
        public uint Transform;
    }
    public struct Quat
    {
        public float X, Y, Z, W;
    }

    public List<ShapeSequence> Sequences = new();
    public List<string> Names = new();
    public List<int> SequenceNames = new();
    public List<Node> Nodes = new();
    public List<SubSequence> SubSequences = new();
    public List<Key> Keys = new();
    public List<Quat> Rotations = new();

    // src/world/shape.cpp ShapeAnim::findSequence: index of the first sequence whose name matches (ASCII
    // case-insensitive), or -1 (0x005b9798: the first matching entry of the name table decides).
    public int FindSequence(string name)
    {
        int n = Shape.FindName(this, name);
        for (int i = 0; i < SequenceNames.Count; ++i)
            if (SequenceNames[i] == n)
                return i;
        return -1;
    }

    // src/world/shape.cpp ShapeAnim::nodePitchRange: Euler x of a node's rotation in shape space at the first and the
    // last keyframe of a sequence (Turret onNewDataBlock 0x00452970). False when the node or sequence is missing.
    public bool NodePitchRange(string sequence, string node, ref float first, ref float last)
    {
        int seq = FindSequence(sequence);
        int nodeName = Shape.FindName(this, node);
        int target = -1;
        for (int i = 0; i < Nodes.Count; ++i)
            if (Nodes[i].Name == nodeName)
            {
                target = i;
                break;
            }
        if (seq < 0 || target < 0)
            return false;
        Span<float> output = stackalloc float[2];
        Span<double> total = stackalloc double[9], m = stackalloc double[9], next = stackalloc double[9];
        for (int end = 0; end < 2; ++end)
        {
            // Shape-space rotation: the node's rotation, then its ancestors'. A node the sequence animates takes its
            // first (position 0) or last (position 1) keyframe.
            total.Clear();
            total[0] = total[4] = total[8] = 1;
            int guard = 0;
            for (int n = target; n >= 0 && n < Nodes.Count && guard < 256; n = Nodes[n].Parent, ++guard)
            {
                Node nd = Nodes[n];
                uint t = (uint)nd.DefaultTransform;
                for (int k = 0; k < nd.NumSub; ++k)
                {
                    long si = (long)nd.FirstSub + k;
                    if (si < 0 || si >= SubSequences.Count || SubSequences[(int)si].Sequence != seq ||
                        SubSequences[(int)si].NumKeys < 1)
                        continue;
                    long ki = SubSequences[(int)si].FirstKey + (end != 0 ? SubSequences[(int)si].NumKeys - 1 : 0);
                    if (ki >= 0 && ki < Keys.Count)
                        t = Keys[(int)ki].Transform;
                }
                if (t >= Rotations.Count)
                    return false;
                Shape.QuatMatrix(Rotations[(int)t], m);
                Shape.Mul3(total, m, next);
                next.CopyTo(total);
            }
            double s = total[5];
            if (s > 1)
                s = 1;
            if (s < -1)
                s = -1;
            output[end] = Store(Math.Asin(s));
        }
        first = output[0];
        last = output[1];
        return true;
    }
}

// src/world/shape.hpp struct ShapeModel: the data of a TS::Shape the server reads, in the version 8 layout
// (0x005b9998 converts older files). Every index is validated when it is used, not when it is read.
public sealed class ShapeModel
{
    public struct Node
    {
        public short Name, Parent, NumSub, FirstSub, DefaultTransform;
    }
    public struct Sequence
    {
        public int Name;
        public bool Cyclic;
        public float Duration;
        public int Priority;
    }
    public struct SubSequence
    {
        public short Sequence, NumKeys, FirstKey;
    }
    public struct Key
    {
        public float Position;
        public ushort Index; // transform index (node keys) or frame index (object keys)
        public ushort Flags; // 0x8000 visible, 0x4000 visibility valid, 0x2000 material valid, 0x1000 frame valid
    }
    public struct Transform
    {
        public Short4 Q;
        public Float3 T;

        public Transform()
        {
            Q = default;
            Q[3] = 32767;
            T = default;
        }
    }
    public struct Object
    {
        public short Name;
        public ushort Flags;
        public int Mesh;
        public short Node;
        public Float3 Offset;
        public short NumSub, FirstSub;
    }
    public struct Detail
    {
        public int RootNode;
        public float Size;
    }
    public struct Transition
    {
        public int StartSequence, EndSequence;
        public float StartPosition, EndPosition, Duration;
        public Transform Transform;
    }
    public struct Face
    {
        public Int3 V;
        public int Material;
    }
    public struct Frame
    {
        public int FirstVert;
        public Float3 Scale, Origin;
    }
    public sealed class Mesh
    {
        public byte[] Verts = Array.Empty<byte>(); // 4 bytes per packed vertex: x, y, z, normal index
        public Face[] Faces = Array.Empty<Face>();
        public Frame[] Frames = Array.Empty<Frame>();

        // src/world/shape.cpp ShapeModel::Mesh::vertex: decoded vertex `index` of a frame (T2.7):
        // f32(u8 * scale + origin). False when the index is outside the vertex array.
        public bool Vertex(in Frame f, int index, Span<float> output)
        {
            long i = (long)f.FirstVert + index;
            if (i < 0 || i * 4 + 3 >= Verts.Length)
                return false;
            for (int k = 0; k < 3; ++k)
                output[k] = F32Chop(Add(Mul(Verts[i * 4 + k], f.Scale[k]), f.Origin[k]));
            return true;
        }
    }

    public uint Version;
    public float Radius;
    public Float3 Center;
    public Float6 Box;
    public int AlwaysNode = -1; // TS::Shape +0xe4 (version >= 6)
    public Node[] Nodes = Array.Empty<Node>();
    public Sequence[] Sequences = Array.Empty<Sequence>();
    public SubSequence[] SubSequences = Array.Empty<SubSequence>();
    public Key[] Keys = Array.Empty<Key>();
    public Transform[] Transforms = Array.Empty<Transform>();
    public string[] Names = Array.Empty<string>();
    public Object[] Objects = Array.Empty<Object>();
    public Detail[] Details = Array.Empty<Detail>();
    public Transition[] Transitions = Array.Empty<Transition>();
    public Mesh[] Meshes = Array.Empty<Mesh>();

    // src/world/shape.cpp ShapeModel::findName: first name equal to `name` (ASCII case-insensitive), -1 if none
    // (0x005b96d8).
    public int FindName(string name)
    {
        for (int i = 0; i < Names.Length; ++i)
            if (Shape.NameEquals(Names[i], name))
                return i;
        return -1;
    }

    // src/world/shape.cpp ShapeModel::findNode: first node whose name index is that name, -1 if none (0x005b9660).
    public int FindNode(string name)
    {
        int n = FindName(name);
        if (n < 0)
            return -1;
        for (int i = 0; i < Nodes.Length; ++i)
            if (Nodes[i].Name == n)
                return i;
        return -1;
    }

    // src/world/shape.cpp ShapeModel::collisionDetail: the last detail whose root is the node named "collision", 0 when
    // there is none (image +0x74, StaticBase_bindShape 0x0044f68c, T2.1).
    public int CollisionDetail()
    {
        int node = FindNode("collision");
        int detail = 0;
        for (int d = 0; d < Details.Length; ++d)
            if (Details[d].RootNode == node)
                detail = d;
        return detail;
    }

    // src/world/shape.cpp ShapeModel::findSequence.
    public int FindSequence(string name)
    {
        int n = FindName(name);
        if (n < 0)
            return -1;
        for (int i = 0; i < Sequences.Length; ++i)
            if (Sequences[i].Name == n)
                return i;
        return -1;
    }

    // src/world/shape.cpp ShapeModel::parse.
    public static bool Parse(byte[] dts, ShapeModel output)
    {
        output.Reset();
        var r = new Reader(dts);
        uint ver = r.Pers(out _);
        if (!r.Ok || ver < 1 || ver > 8)
            return false;
        output.Version = ver;
        var counts = new int[9];
        for (int i = 0; i < 9; ++i)
            counts[i] = r.GetI32();
        int nTransitions = ver >= 2 ? r.GetI32() : 0;
        int nTriggers = ver >= 4 ? r.GetI32() : 0;
        output.Radius = r.GetF32();
        for (int i = 0; i < 3; ++i)
            output.Center[i] = r.GetF32();
        if (ver >= 8)
        {
            for (int i = 0; i < 6; ++i)
                output.Box[i] = r.GetF32();
        }
        else
        {
            for (int i = 0; i < 3; ++i)
            {
                output.Box[i] = Store(Sub(output.Center[i], output.Radius));
                output.Box[i + 3] = Store(Add(output.Radius, output.Center[i]));
            }
        }
        long[] sizes;
        if (ver == 8)
            sizes = new long[] { 10, 32, 6, 8, 20, 24, 28, 8, 40, 8 };
        else
            sizes = Shape.RecordSizes(ver);
        int[] n = { counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6], counts[7], nTransitions, nTriggers };
        var at = new long[10];
        for (int i = 0; i < 10 && r.Ok; ++i)
        {
            // A count the file cannot hold is malformed (D11): every record is at least 6 bytes.
            if (n[i] < 0 || n[i] > dts.Length / 6)
                return false;
            at[i] = r.Pos;
            r.Skip(n[i] * sizes[i]);
        }
        if (ver >= 5)
            r.Skip(4);
        if (ver >= 6)
            output.AlwaysNode = r.GetI32();
        if (!r.Ok || counts[8] < 0 || counts[8] > dts.Length / 16)
            return false;
        // Every array lies inside the file from here on (the skips above checked the ends).
        bool v8 = ver == 8;
        output.Nodes = new Node[n[0]];
        for (int i = 0; i < output.Nodes.Length; ++i)
        {
            long o = at[0] + i * sizes[0], step = v8 ? 2 : 4; // older files hold i32 fields
            ref Node nd = ref output.Nodes[i];
            nd.Name = Reader.I16At(dts, o);
            nd.Parent = Reader.I16At(dts, o + step);
            nd.NumSub = Reader.I16At(dts, o + 2 * step);
            nd.FirstSub = Reader.I16At(dts, o + 3 * step);
            nd.DefaultTransform = Reader.I16At(dts, o + 4 * step);
        }
        output.Sequences = new Sequence[n[1]];
        for (int i = 0; i < output.Sequences.Length; ++i)
        {
            long o = at[1] + i * sizes[1];
            ref Sequence s = ref output.Sequences[i];
            s.Name = Reader.I32At(dts, o);
            s.Cyclic = Reader.I32At(dts, o + 4) != 0;
            s.Duration = Reader.F32At(dts, o + 8);
            s.Priority = Reader.I32At(dts, o + 12);
        }
        output.SubSequences = new SubSequence[n[2]];
        for (int i = 0; i < output.SubSequences.Length; ++i)
        {
            long o = at[2] + i * sizes[2], step = v8 ? 2 : 4;
            ref SubSequence s = ref output.SubSequences[i];
            s.Sequence = Reader.I16At(dts, o);
            s.NumKeys = Reader.I16At(dts, o + step);
            s.FirstKey = Reader.I16At(dts, o + 2 * step);
        }
        output.Keys = new Key[n[3]];
        for (int i = 0; i < output.Keys.Length; ++i)
        {
            long o = at[3] + i * sizes[3];
            ref Key k = ref output.Keys[i];
            k.Position = Reader.F32At(dts, o);
            uint key = Reader.U32At(dts, o + 4);
            k.Index = (ushort)key;
            if (v8)
            {
                k.Flags = (ushort)(key >> 16);
            }
            else if (ver >= 3)
            {
                // {position, key, m}: the low 16 bits of m and its four top bits (0x005b9998).
                uint m = Reader.U32At(dts, o + 8);
                k.Flags = (ushort)((m & 0xffffu) | ((m >> 16) & 0xf000u));
            }
            else
            {
                k.Flags = (ushort)(0x1000u | ((key >> 16) & 0xc000u));
            }
        }
        output.Transforms = new Transform[n[4]];
        for (int i = 0; i < output.Transforms.Length; ++i)
        {
            long o = at[4] + i * sizes[4];
            ref Transform t = ref output.Transforms[i];
            t = new Transform();
            if (ver >= 7)
            {
                for (int k = 0; k < 4; ++k)
                    t.Q[k] = Reader.I16At(dts, o + k * 2);
                for (int k = 0; k < 3; ++k)
                    t.T[k] = Reader.F32At(dts, o + 8 + k * 4);
            }
            else
            {
                // Float quaternions are converted with crt_ftol(q * 32767) (editor shapes only).
                for (int k = 0; k < 4; ++k)
                {
                    double v = Mul(Reader.F32At(dts, o + k * 4), 32767.0);
                    t.Q[k] = v > -32769.0 && v < 32768.0 ? (short)(int)v : (short)0;
                }
                for (int k = 0; k < 3; ++k)
                    t.T[k] = Reader.F32At(dts, o + 16 + k * 4);
            }
        }
        output.Names = new string[n[5]];
        for (int i = 0; i < output.Names.Length; ++i)
            output.Names[i] = Reader.CString(dts, at[5] + i * 24, 24);
        output.Objects = new Object[n[6]];
        for (int i = 0; i < output.Objects.Length; ++i)
        {
            long o = at[6] + i * sizes[6];
            ref Object ob = ref output.Objects[i];
            ob.Name = Reader.I16At(dts, o);
            ob.Flags = Reader.U16At(dts, o + 2);
            ob.Mesh = Reader.I32At(dts, o + 4);
            ob.Node = Reader.I16At(dts, o + 8);
            long off = v8 ? o + 12 : o + 52;
            for (int k = 0; k < 3; ++k)
                ob.Offset[k] = Reader.F32At(dts, off + k * 4);
            ob.NumSub = Reader.I16At(dts, v8 ? o + 24 : o + 64);
            ob.FirstSub = Reader.I16At(dts, v8 ? o + 26 : o + 68);
        }
        output.Details = new Detail[n[7]];
        for (int i = 0; i < output.Details.Length; ++i)
        {
            output.Details[i].RootNode = Reader.I32At(dts, at[7] + i * 8);
            output.Details[i].Size = Reader.F32At(dts, at[7] + i * 8 + 4);
        }
        output.Transitions = new Transition[n[8]];
        for (int i = 0; i < output.Transitions.Length; ++i)
        {
            long o = at[8] + i * sizes[8];
            ref Transition t = ref output.Transitions[i];
            t.Transform = new Transform();
            t.StartSequence = Reader.I32At(dts, o);
            t.EndSequence = Reader.I32At(dts, o + 4);
            t.StartPosition = Reader.F32At(dts, o + 8);
            t.EndPosition = Reader.F32At(dts, o + 12);
            t.Duration = Reader.F32At(dts, o + 16);
            if (ver >= 7)
            {
                for (int k = 0; k < 4; ++k)
                    t.Transform.Q[k] = Reader.I16At(dts, o + 20 + k * 2);
                for (int k = 0; k < 3; ++k)
                    t.Transform.T[k] = Reader.F32At(dts, o + 28 + k * 4);
            }
            else
            {
                for (int k = 0; k < 4; ++k)
                {
                    double v = Mul(Reader.F32At(dts, o + 20 + k * 4), 32767.0);
                    t.Transform.Q[k] = v > -32769.0 && v < 32768.0 ? (short)(int)v : (short)0;
                }
                for (int k = 0; k < 3; ++k)
                    t.Transform.T[k] = Reader.F32At(dts, o + 36 + k * 4);
            }
        }
        // Meshes: nested PERS TS::CelAnimMesh blocks (docs/interiors.md 8.3). Only version 3 meshes are decoded; an
        // older mesh (editor shapes) stays empty and is never hit.
        output.Meshes = new Mesh[counts[8]];
        for (int mi = 0; mi < output.Meshes.Length; ++mi)
        {
            var mesh = output.Meshes[mi] = new Mesh();
            uint mver = r.Pers(out long meshEnd);
            if (!r.Ok || meshEnd <= r.Pos || meshEnd > dts.Length)
                return false;
            var m = new Reader(dts) { Pos = r.Pos };
            r.Pos = meshEnd;
            if (mver != 3)
                continue;
            int nVerts = m.GetI32();
            m.GetI32(); // vertsPerFrame
            int nTex = m.GetI32(), nFaces = m.GetI32(), nFrames = m.GetI32();
            m.Skip(8); // texVertsPerFrame, radius
            long left = m.Ok && m.Pos <= meshEnd ? meshEnd - m.Pos : 0;
            if (!m.Ok || nVerts < 0 || nTex < 0 || nFaces < 0 || nFrames < 0 || nVerts > left / 4 || nTex > left / 8 ||
                nFaces > left / 28 || nFrames > left / 28 || (long)nVerts * 4 + (long)nTex * 8 + (long)nFaces * 28 + (long)nFrames * 28 > left)
                return false;
            mesh.Verts = dts.AsSpan((int)m.Pos, nVerts * 4).ToArray();
            m.Skip((long)nVerts * 4 + (long)nTex * 8);
            mesh.Faces = new Face[nFaces];
            for (int fi = 0; fi < nFaces; ++fi)
            {
                ref Face f = ref mesh.Faces[fi];
                for (int k = 0; k < 3; ++k)
                {
                    f.V[k] = m.GetI32();
                    m.GetI32(); // texture vertex
                }
                f.Material = m.GetI32();
            }
            mesh.Frames = new Frame[nFrames];
            for (int fi = 0; fi < nFrames; ++fi)
            {
                ref Frame f = ref mesh.Frames[fi];
                f.FirstVert = m.GetI32();
                for (int k = 0; k < 3; ++k)
                    f.Scale[k] = m.GetF32();
                for (int k = 0; k < 3; ++k)
                    f.Origin[k] = m.GetF32();
            }
            if (!m.Ok)
                return false;
        }
        if (ver < 8)
        {
            // 0x005b9998: the frame-0 box of the mesh of the first object on node 0 replaces centre +- radius.
            foreach (Object ob in output.Objects)
            {
                if (ob.Node != 0)
                    continue;
                if (ob.Mesh >= 0 && ob.Mesh < output.Meshes.Length)
                {
                    Mesh mesh = output.Meshes[ob.Mesh];
                    Span<float> lo = stackalloc float[3], hi = stackalloc float[3];
                    if (mesh.Frames.Length != 0 && mesh.Vertex(mesh.Frames[0], 0, lo) && mesh.Vertex(mesh.Frames[0], 1, hi))
                    {
                        for (int i = 0; i < 3; ++i)
                        {
                            output.Box[i] = lo[i];
                            output.Box[i + 3] = hi[i];
                        }
                    }
                }
                break;
            }
        }
        return true;
    }

    // out = ShapeModel{} of ShapeModel::parse.
    private void Reset()
    {
        Version = 0;
        Radius = 0;
        Center = default;
        Box = default;
        AlwaysNode = -1;
        Nodes = Array.Empty<Node>();
        Sequences = Array.Empty<Sequence>();
        SubSequences = Array.Empty<SubSequence>();
        Keys = Array.Empty<Key>();
        Transforms = Array.Empty<Transform>();
        Names = Array.Empty<string>();
        Objects = Array.Empty<Object>();
        Details = Array.Empty<Detail>();
        Transitions = Array.Empty<Transition>();
        Meshes = Array.Empty<Mesh>();
    }
}

// src/world/shape.hpp struct ShapeThread: one animation thread of an instance, as far as node transforms and object
// states depend on it (docs/collision.md T2.5a).
public struct ShapeThread
{
    public int Sequence;
    public float Position;
    public int Priority;

    public ShapeThread()
    {
        Sequence = -1;
        Position = 0;
        Priority = 0;
    }

    public ShapeThread(int sequence, float position, int priority)
    {
        Sequence = sequence;
        Position = position;
        Priority = priority;
    }
}

// src/world/shape.hpp struct ShapeSurface: result of a line test (T1.7), in shape root space.
public struct ShapeSurface
{
    public float T;
    public Float3 Position;
    public Float3 Normal;
    public int Material; // face material index, -1 in bounds mode
    public int Face;
    public int Part;     // object index

    public ShapeSurface()
    {
        T = 0;
        Position = default;
        Normal = default;
        Material = -1;
        Face = -1;
        Part = -1;
    }
}

// src/world/shape.hpp struct ShapeContact: one record of a sphere test (T1.7, T4), in shape root space.
public struct ShapeContact
{
    public Float3 Position;
    public Float3 Normal;
    public float Distance; // +0x1c
    public int Material;   // face material index, -1 in bounds mode (+0x20 = 0)
    public int Face;
    public int Part;       // object index

    public ShapeContact()
    {
        Position = default;
        Normal = default;
        Distance = 0;
        Material = -1;
        Face = -1;
        Part = -1;
    }
}

// src/world/shape.hpp struct ShapeInstance: TSShapeInstance (T1.3), node entries and object states of one shape on the
// server.
public sealed class ShapeInstance
{
    public struct ObjectState
    {
        public int Frame;
        public bool Visible;

        public ObjectState()
        {
            Frame = 0;
            Visible = true;
        }
    }

    public ShapeModel? ShapeData;
    public TMat3[] Entries = Array.Empty<TMat3>(); // node -> root space, identity until animated
    public byte[] NodeActive = Array.Empty<byte>(); // node instance +0x08
    public ObjectState[] Objects = Array.Empty<ObjectState>();
    public List<ShapeThread> Threads = new();
    public byte ViewMode;        // instance +0x9e (the Player sets 2 for first person animations)
    public TMat3 Root = new();   // instance +0xa0: default transform of node 0, flags 3
    // Node overrides (T2.5a, 0x005c2f54): a thread-driven node marked here takes, in view mode 2, the override matrix
    // with the height of the source node as its parent.
    public byte[] OverrideNode = Array.Empty<byte>();
    public int OverrideSource = -1;
    public TMat3 OverrideMatrix = new();
    private readonly List<KeyValuePair<int, List<int>>> mDetailNodes = new();

    // src/world/shape.cpp ShapeInstance::detailRoot: root node of a detail (T2.2), -1 when there is none.
    public int DetailRoot(int detail)
    {
        if (ShapeData == null || detail < 0)
            return -1;
        int node = 1; // a shape without details uses node 1 (T2.2)
        if (ShapeData.Details.Length != 0)
        {
            if (detail >= ShapeData.Details.Length)
                return -1;
            node = ShapeData.Details[detail].RootNode;
        }
        return node >= 0 && node < ShapeData.Nodes.Length ? node : -1;
    }

    // src/world/shape.cpp ShapeInstance::build: TSShapeInstance_build (0x005c0ba8), identity entries, default object
    // states, then one animate pass.
    public void Build(ShapeModel model)
    {
        ShapeData = model;
        mDetailNodes.Clear();
        Entries = new TMat3[model.Nodes.Length];
        for (int i = 0; i < Entries.Length; ++i)
            Entries[i] = new TMat3();
        NodeActive = new byte[model.Nodes.Length];
        Array.Fill(NodeActive, (byte)1);
        Objects = new ObjectState[model.Objects.Length];
        for (int i = 0; i < Objects.Length; ++i)
            Objects[i] = new ObjectState();
        OverrideNode = new byte[model.Nodes.Length];
        OverrideSource = -1;
        Root = new TMat3();
        if (model.Nodes.Length != 0)
        {
            int t = model.Nodes[0].DefaultTransform;
            if (t >= 0 && t < model.Transforms.Length)
            {
                ref ShapeModel.Transform tr = ref model.Transforms[t];
                Span<float> q = stackalloc float[4];
                for (int k = 0; k < 4; ++k)
                    q[k] = F32Chop(Mul(Shape.kQuatScale, tr.Q[k]));
                Shape.QuatToMatrix(q, ref Root);
                Root.P = tr.T;
            }
        }
        Root.Flags = 3;
        Animate();
    }

    // src/world/shape.cpp ShapeInstance::animate: TSShapeInstance_animate (0x005c1210), the subtree of detail 0 and of
    // the "always" node.
    public void Animate()
    {
        if (ShapeData == null)
            return;
        using var chop = ChopScope();
        ShapeModel sh = ShapeData;
        List<int>[] kids = Shape.ChildLists(sh);
        List<int> threadsInOrder = Shape.ThreadOrder(this);
        var order = new List<int>();
        // 0x005c1210: the "always" node first, then the root of the current detail (0 on a server).
        if (sh.AlwaysNode >= 0 && sh.AlwaysNode < sh.Nodes.Length)
            Shape.Subtree(kids, sh.AlwaysNode, order);
        Shape.Subtree(kids, DetailRoot(0), order);
        foreach (int node in order)
        {
            // TSNodeInstance_animate (0x005bd9b0): nothing for a node without a parent entry.
            if (!Shape.HasParentEntry(sh, node))
                continue;
            Shape.AnimateObjects(this, threadsInOrder, node);
            var local = new TMat3();
            TMat3 right = Entries[sh.Nodes[node].Parent];
            if (Shape.ThreadTransform(this, threadsInOrder, node, ref local))
            {
                // View mode overrides apply on the thread path only.
                if (OverrideNode[node] != 0 && (ViewMode == 1 || ViewMode == 2))
                {
                    TMat3 over = OverrideMatrix;
                    if (ViewMode == 1)
                    {
                        over.P = right.P;
                        right = over;
                    }
                    else if (OverrideSource >= 0 && OverrideSource < Entries.Length)
                    {
                        over.P[0] = 0.0f;
                        over.P[1] = 0.0f;
                        over.P[2] = Entries[OverrideSource].P[2];
                        right = over;
                    }
                }
            }
            else
            {
                int t = sh.Nodes[node].DefaultTransform;
                if (t < 0 || t >= sh.Transforms.Length)
                    continue;
                Shape.TransformMatrix(sh.Transforms[t], ref local);
            }
            Shape.TmatMul(local, right, out TMat3 entry);
            Entries[node] = entry;
        }
    }

    // src/world/shape.cpp ShapeInstance::setOverride.
    public void SetOverride(string prefix, string sourceNode)
    {
        if (ShapeData == null)
            return;
        // 0x005c2f54: every node whose name starts with the prefix. The matrix is the Player's global at 0x0065f628:
        // yaw pi / 2 (0x3fc90fdb), flags 3.
        for (int i = 0; i < ShapeData.Nodes.Length; ++i)
        {
            int n = ShapeData.Nodes[i].Name;
            if (n >= 0 && n < ShapeData.Names.Length && ShapeData.Names[n].StartsWith(prefix, StringComparison.Ordinal))
                OverrideNode[i] = 1;
        }
        OverrideSource = ShapeData.FindNode(sourceNode);
        ReadOnlySpan<float> yaw = stackalloc float[3] { 0.0f, 0.0f, 1.57079637f };
        OverrideMatrix = new TMat3();
        Shape.EulerMatrix(yaw, ref OverrideMatrix);
        OverrideMatrix.Flags |= 2u;
    }

    private struct Candidate
    {
        public int Obj;
        public ShapeModel.Mesh Mesh;
        public int Frame;
        public TMat3 T;
        public Float3 Sm, Em;
        public float Tval;
    }

    // src/world/shape.cpp ShapeInstance::castLine: TSShapeInstance_castRay (0x005c1300, T7) on detail 0. s0, e0:
    // segment in the space `rel` maps to root space (null: already root space). faces false is the bounds mode.
    public bool CastLine(ReadOnlySpan<float> s0, ReadOnlySpan<float> e0, bool faces, TMat3? rel, ref ShapeSurface output)
    {
        if (ShapeData == null)
            return false;
        using var chop = ChopScope();
        ShapeModel sh = ShapeData;
        Span<float> S = stackalloc float[3], E = stackalloc float[3];
        if (rel is TMat3 rm)
        {
            X87.MulPoint(s0, rm.M, rm.P, S);
            X87.MulPoint(e0, rm.M, rm.P, E);
        }
        else
        {
            s0[..3].CopyTo(S);
            e0[..3].CopyTo(E);
        }
        var cands = new List<Candidate>();
        var order = new List<int>();
        Shape.Subtree(Shape.ChildLists(sh), DetailRoot(0), order);
        Span<float> lo = stackalloc float[3], hi = stackalloc float[3];
        foreach (int node in order)
        {
            if (NodeActive[node] == 0)
                continue;
            for (int oi = 0; oi < sh.Objects.Length; ++oi)
            {
                ref ShapeModel.Object ob = ref sh.Objects[oi];
                if (ob.Node != node || !Objects[oi].Visible)
                    continue;
                if (ob.Mesh < 0 || ob.Mesh >= sh.Meshes.Length)
                    continue;
                ShapeModel.Mesh mesh = sh.Meshes[ob.Mesh];
                int frame = Objects[oi].Frame;
                if (frame < 0 || frame >= mesh.Frames.Length)
                    continue;
                var c = new Candidate();
                c.Obj = oi;
                c.Mesh = mesh;
                c.Frame = frame;
                // T2.7: the node entry with the object offset carried into its translation.
                c.T = Entries[node];
                X87.MulPoint(ob.Offset, c.T.M, Entries[node].P, c.T.P);
                Shape.TmatInverse(c.T, out TMat3 Ti);
                X87.MulPoint(S, Ti.M, Ti.P, c.Sm);
                X87.MulPoint(E, Ti.M, Ti.P, c.Em);
                if (!mesh.Vertex(mesh.Frames[frame], 0, lo) || !mesh.Vertex(mesh.Frames[frame], 1, hi))
                    continue;
                if (!Shape.SlabTest(c.Sm, c.Em, lo, hi, ref c.Tval))
                    continue;
                cands.Add(c);
            }
        }
        if (cands.Count == 0)
            return false;
        output = new ShapeSurface();
        void SetPosition(ref ShapeSurface o, ReadOnlySpan<float> s, ReadOnlySpan<float> e, float t)
        {
            for (int i = 0; i < 3; ++i)
            {
                float p = F32Chop(Sub(e[i], s[i]));
                p = F32Chop(Mul(t, p));
                o.Position[i] = F32Chop(Add(s[i], p));
            }
        }
        if (!faces)
        {
            // Bounds mode: the bounds t of the first candidate in collection order.
            output.T = cands[0].Tval;
            output.Part = cands[0].Obj;
            SetPosition(ref output, S, E, output.T);
            return true;
        }
        var sorted = new int[cands.Count];
        for (int i = 0; i < sorted.Length; ++i)
            sorted[i] = i;
        Borland.BorlandQsort<int>(sorted, (a, b) => cands[a].Tval < cands[b].Tval ? -1 : cands[b].Tval < cands[a].Tval ? 1 : 0);
        output.T = 1.1f;
        bool hit = false;
        Span<float> D = stackalloc float[3], A = stackalloc float[3], B = stackalloc float[3], C = stackalloc float[3];
        Span<float> ac = stackalloc float[3], bc = stackalloc float[3], N = stackalloc float[3], H = stackalloc float[3];
        Span<float> bestN = stackalloc float[3];
        foreach (int ci in sorted)
        {
            Candidate c = cands[ci];
            if (!(c.Tval < output.T))
                break;
            // TSCelAnimMesh_lineFaces (0x005ab0fc, T7.2).
            for (int i = 0; i < 3; ++i)
                D[i] = F32Chop(Sub(c.Em[i], c.Sm[i]));
            float tmin = F32Chop(Mul(0.9f, c.Tval));
            float best = output.T;
            int bestFace = -1;
            bestN.Clear();
            ref ShapeModel.Frame fr = ref c.Mesh.Frames[c.Frame];
            for (int fi = 0; fi < c.Mesh.Faces.Length; ++fi)
            {
                ref ShapeModel.Face face = ref c.Mesh.Faces[fi];
                if (!c.Mesh.Vertex(fr, face.V[0], A) || !c.Mesh.Vertex(fr, face.V[1], B) || !c.Mesh.Vertex(fr, face.V[2], C))
                    continue;
                for (int i = 0; i < 3; ++i)
                {
                    ac[i] = F32Chop(Sub(A[i], C[i]));
                    bc[i] = F32Chop(Sub(B[i], C[i]));
                }
                Shape.Cross(bc, ac, N);
                float dN = F32Chop(X87.Dot(N, C));
                float den = F32Chop(X87.Dot(N, D));
                if (den >= 0.0f)
                    continue;
                float num = F32Chop(Sub(dN, X87.Dot(N, c.Sm)));
                if (!(Mul(tmin, den) > num && Mul(best, den) <= num))
                    continue;
                float t = F32Chop(Div(num, den));
                for (int i = 0; i < 3; ++i)
                    H[i] = F32Chop(Add(Mul(t, D[i]), c.Sm[i]));
                if (Shape.PointInTriangle(H, N, A, B, C))
                {
                    best = t;
                    bestFace = fi;
                    N.CopyTo(bestN);
                }
            }
            if (bestFace < 0)
                continue;
            hit = true;
            output.T = best;
            output.Face = bestFace;
            output.Material = c.Mesh.Faces[bestFace].Material;
            output.Part = c.Obj;
            X87.MulVector(bestN, c.T.M, output.Normal);
        }
        if (!hit)
            return false;
        SetPosition(ref output, S, E, output.T);
        // 0x005c1555: the length is stored as f32 before the division.
        float k = F32Chop(Div(1.0, F32Chop(X87.Len(output.Normal))));
        for (int i = 0; i < 3; ++i)
            output.Normal[i] = F32Chop(Mul(k, output.Normal[i]));
        return true;
    }

    // src/world/shape.cpp ShapeInstance::detailNodes: nodes of a detail subtree in visiting order (T2.4), cached per
    // detail.
    public List<int> DetailNodes(int detail)
    {
        foreach (var e in mDetailNodes)
            if (e.Key == detail)
                return e.Value;
        var order = new List<int>();
        if (ShapeData != null)
            Shape.Subtree(Shape.ChildLists(ShapeData), DetailRoot(detail), order);
        mDetailNodes.Add(new KeyValuePair<int, List<int>>(detail, order));
        return order;
    }

    // src/world/shape.cpp ShapeInstance::collideBox: TSShapeInstance_collideBox (0x005c2264, T6): true when the frame
    // box of a visible object of the detail subtree overlaps the box with half extents `half` whose centre frame `rel`
    // maps to root space.
    public bool CollideBox(int detail, ReadOnlySpan<float> half, in TMat3 rel)
    {
        if (ShapeData == null)
            return false;
        using var chop = ChopScope();
        ShapeModel sh = ShapeData;
        Span<float> lo = stackalloc float[3], hi = stackalloc float[3], a = stackalloc float[3];
        foreach (int node in DetailNodes(detail))
        {
            if (NodeActive[node] == 0)
                continue;
            for (int oi = 0; oi < sh.Objects.Length; ++oi)
            {
                ref ShapeModel.Object ob = ref sh.Objects[oi];
                if (ob.Node != node || !Objects[oi].Visible)
                    continue;
                if (ob.Mesh < 0 || ob.Mesh >= sh.Meshes.Length)
                    continue;
                ShapeModel.Mesh mesh = sh.Meshes[ob.Mesh];
                int fi = Objects[oi].Frame;
                if (fi < 0 || fi >= mesh.Frames.Length)
                    continue;
                ref ShapeModel.Frame frame = ref mesh.Frames[fi];
                // TSObjectInstance_boxTest (0x005bf4d8): T and its inverse as T2.7, the box centre frame into mesh
                // space.
                TMat3 T = Entries[node];
                X87.MulPoint(ob.Offset, T.M, Entries[node].P, T.P);
                Shape.TmatInverse(T, out TMat3 Ti);
                Shape.TmatMul(rel, Ti, out TMat3 M);
                if (!mesh.Vertex(frame, 0, lo) || !mesh.Vertex(frame, 1, hi))
                    continue;
                // TSCelAnimMesh_boxTest (0x005acff8): the frame box as centre and half extents.
                for (int i = 0; i < 3; ++i)
                {
                    a[i] = F32Chop(Mul(Sub(hi[i], lo[i]), 0.5f));
                    M.P[i] = F32Chop(Sub(M.P[i], lo[i]));
                    M.P[i] = F32Chop(Sub(M.P[i], a[i]));
                }
                if (Shape.OrientedBoxesOverlap(a, half, M.M, M.P))
                    return true;
            }
        }
        return false;
    }

    // src/world/shape.cpp ShapeInstance::collideSphere: TSShapeInstance_collideSphere (0x005c1d7c, T4) on the subtree of
    // `detail`. p0: centre in the space `rel` maps to root space (null: already root space). faces true is the mesh mode
    // (T4.1), false the bounds mode of Item images (T4.2). Records are appended to `out` in root space. True when `out`
    // is not empty afterwards (the original does not reset the list).
    public bool CollideSphere(int detail, ReadOnlySpan<float> p0, bool faces, TMat3? rel, float radius, List<ShapeContact> output)
    {
        if (ShapeData == null)
            return output.Count != 0;
        using var chop = ChopScope();
        ShapeModel sh = ShapeData;
        Span<float> P = stackalloc float[3];
        if (rel is TMat3 rm)
            X87.MulPoint(p0, rm.M, rm.P, P);
        else
            p0[..3].CopyTo(P);
        float rr = F32Chop(Mul(radius, radius)); // stored before the bounds call (0x005ac665)
        Span<float> pm = stackalloc float[3], lo = stackalloc float[3], hi = stackalloc float[3];
        Span<float> A = stackalloc float[3], B = stackalloc float[3], C = stackalloc float[3];
        Span<float> pos = stackalloc float[3], n = stackalloc float[3];
        Span<float> h = stackalloc float[3], ctr = stackalloc float[3], d = stackalloc float[3];
        Span<float> local = stackalloc float[3], w = stackalloc float[3];
        foreach (int node in DetailNodes(detail))
        {
            if (NodeActive[node] == 0)
                continue;
            for (int oi = 0; oi < sh.Objects.Length; ++oi)
            {
                ref ShapeModel.Object ob = ref sh.Objects[oi];
                if (ob.Node != node || !Objects[oi].Visible)
                    continue;
                if (ob.Mesh < 0 || ob.Mesh >= sh.Meshes.Length)
                    continue;
                ShapeModel.Mesh mesh = sh.Meshes[ob.Mesh];
                int fi = Objects[oi].Frame;
                if (fi < 0 || fi >= mesh.Frames.Length)
                    continue;
                ref ShapeModel.Frame frame = ref mesh.Frames[fi];
                // T2.7: the node entry with the object offset carried into its translation.
                TMat3 T = Entries[node];
                X87.MulPoint(ob.Offset, T.M, Entries[node].P, T.P);
                Shape.TmatInverse(T, out TMat3 Ti);
                X87.MulPoint(P, Ti.M, Ti.P, pm);
                if (!mesh.Vertex(frame, 0, lo) || !mesh.Vertex(frame, 1, hi))
                    continue;
                // Sphere bounds (0x005bf128 -> 0x005ac5a4 -> 0x005ec688): squared distance to the frame box, strictly
                // below r * r.
                float d2 = 0;
                for (int i = 0; i < 3; ++i)
                {
                    if (pm[i] < lo[i])
                        d2 = F32Chop(Add(Mul(Sub(pm[i], lo[i]), Sub(pm[i], lo[i])), d2));
                    else if (pm[i] > hi[i])
                        d2 = F32Chop(Add(Mul(Sub(pm[i], hi[i]), Sub(pm[i], hi[i])), d2));
                }
                if (!(d2 < rr))
                    continue;
                if (faces)
                {
                    // T4.1 (0x005bf3a0 -> 0x005ac684): every face, then the records into root space.
                    for (int f = 0; f < mesh.Faces.Length; ++f)
                    {
                        ref ShapeModel.Face face = ref mesh.Faces[f];
                        if (!mesh.Vertex(frame, face.V[0], A) || !mesh.Vertex(frame, face.V[1], B) ||
                            !mesh.Vertex(frame, face.V[2], C))
                            continue;
                        float dist = 0;
                        if (!Shape.SphereTriangle(pm, radius, A, B, C, pos, n, ref dist))
                            continue;
                        var c = new ShapeContact();
                        X87.MulPoint(pos, T.M, T.P, c.Position);
                        X87.MulVector(n, T.M, c.Normal);
                        c.Distance = dist;
                        c.Material = face.Material;
                        c.Face = f;
                        c.Part = oi;
                        output.Add(c);
                    }
                    continue;
                }
                // T4.2 (0x005c1e18): one record per axis the sphere reaches past the half extent.
                for (int i = 0; i < 3; ++i)
                {
                    h[i] = F32Chop(Mul(0.5, F32Chop(Sub(hi[i], lo[i]))));
                    ctr[i] = F32Chop(Mul(0.5, F32Chop(Add(hi[i], lo[i]))));
                    d[i] = F32Chop(Sub(pm[i], ctr[i]));
                }
                for (int i = 0; i < 3; ++i)
                {
                    if (!(Add(Math.Abs((double)d[i]), radius) >= h[i]))
                        continue;
                    n.Clear();
                    n[i] = 1.0f;
                    if (d[i] < 0.0f)
                        for (int k = 0; k < 3; ++k)
                            n[k] = F32Chop(Mul(-1.0, n[k]));
                    for (int j = 0; j < 3; ++j)
                        local[j] = F32Chop(Add(ctr[j], F32Chop(Mul(radius, n[j]))));
                    var c = new ShapeContact();
                    X87.MulPoint(local, T.M, T.P, c.Position);
                    for (int k = 0; k < 3; ++k)
                        w[k] = F32Chop(Sub(c.Position[k], p0[k]));
                    c.Distance = F32Chop(X87.Lenf(w));
                    X87.MulVector(n, T.M, c.Normal);
                    c.Part = oi;
                    output.Add(c);
                }
            }
        }
        return output.Count != 0;
    }
}

// The free functions of src/world/shape.cpp.
public static class Shape
{
    // src/world/shape.cpp recordSizes: array record sizes of shape versions 1-7 (docs/interiors.md 8.2): nodes,
    // sequences, subSequences, keyframes, transforms, names, objects, details, transitions, frameTriggers.
    internal static long[] RecordSizes(uint v)
    {
        long nodes = 20, names = 24, objects = 72, details = 8, triggers = 8;
        long seq = v >= 5 ? 32 : v == 4 ? 24 : 16;
        long key = v >= 3 ? 12 : 8;
        long trans = v == 7 ? 32 : 40;
        long sub = 12;
        long transitions = v == 7 ? 52 : 60;
        return new[] { nodes, seq, sub, key, trans, names, objects, details, transitions, triggers };
    }

    // src/world/shape.cpp readShapeBox: shape box (TS::Shape +0x14..+0x28) of a PERS TS::Shape block. Versions >= 8
    // store it in the header; older versions take the frame-0 box of the first object on node 0. False on malformed
    // data.
    public static bool ReadShapeBox(byte[] dts, Span<float> box)
    {
        var r = new Reader(dts);
        uint ver = r.Pers(out _);
        if (!r.Ok || ver < 1 || ver > 8)
            return false;
        var counts = new int[9];
        for (int i = 0; i < 9; ++i)
            counts[i] = r.GetI32();
        int nTransitions = ver >= 2 ? r.GetI32() : 0;
        int nTriggers = ver >= 4 ? r.GetI32() : 0;
        r.Skip(16); // radius, center
        if (ver >= 8)
        {
            for (int i = 0; i < 6; ++i)
                box[i] = r.GetF32();
            return r.Ok;
        }
        long[] sizes = RecordSizes(ver);
        int[] n = { counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6], counts[7], nTransitions, nTriggers };
        long objects = 0;
        for (int i = 0; i < 10 && r.Ok; ++i)
        {
            if (n[i] < 0)
                return false;
            if (i == 6)
                objects = r.Pos;
            r.Skip(n[i] * sizes[i]);
        }
        if (ver >= 5)
            r.Skip(4);
        if (ver >= 6)
            r.Skip(4);
        if (!r.Ok)
            return false;
        // The first object whose node index is 0 names the mesh.
        int mesh = -1;
        for (int k = 0; k < counts[6]; ++k)
        {
            long o = objects + (long)k * 72;
            short node = Reader.I16At(dts, o + 8);
            if (node == 0)
            {
                mesh = Reader.I32At(dts, o + 4);
                break;
            }
        }
        if (mesh < 0 || mesh >= counts[8])
            return false;
        for (int m = 0; m < mesh; ++m)
        {
            r.Pers(out long me);
            if (!r.Ok)
                return false;
            r.Pos = me;
        }
        uint mver = r.Pers(out _);
        int nVerts = r.GetI32();
        r.GetI32(); // vertsPerFrame
        int nTex = r.GetI32(), nFaces = r.GetI32(), nFrames = r.GetI32();
        if (mver >= 2)
            r.Skip(4);
        if (mver < 3)
            r.Skip(24);
        r.Skip(4); // radius
        long verts = r.Pos;
        if (!r.Ok || nVerts < 2 || nTex < 0 || nFaces < 0 || nFrames < 1)
            return false;
        r.Skip((long)nVerts * 4 + (long)nTex * 8 + (long)nFaces * 28);
        int first = r.GetI32();
        Span<float> scale = stackalloc float[3], origin = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
            scale[i] = r.GetF32();
        for (int i = 0; i < 3; ++i)
            origin[i] = r.GetF32();
        if (!r.Ok || first < 0 || first >= nVerts - 1)
            return false;
        for (int k = 0; k < 2; ++k)
        {
            long v = verts + (long)(first + k) * 4;
            for (int i = 0; i < 3; ++i)
                box[k * 3 + i] = Store(Add(Mul(dts[v + i], scale[i]), origin[i]));
        }
        return true;
    }

    // src/world/shape.cpp readShapeSphere: bounding sphere of the shape (TS::Shape +0x04 radius, +0x08 centre), the
    // collision image of an Item (docs/collision.md T1.2).
    public static bool ReadShapeSphere(byte[] dts, ref float radius, Span<float> center)
    {
        var r = new Reader(dts);
        uint ver = r.Pers(out _);
        if (!r.Ok || ver < 1 || ver > 8)
            return false;
        for (int i = 0; i < 9; ++i)
            r.GetI32();
        if (ver >= 2)
            r.GetI32();
        if (ver >= 4)
            r.GetI32();
        radius = r.GetF32();
        for (int i = 0; i < 3; ++i)
            center[i] = r.GetF32();
        return r.Ok;
    }

    private sealed class CachedSphere
    {
        public bool Ok;
        public float Radius;
        public readonly float[] Center = new float[3];
    }
    private static readonly Dictionary<string, CachedSphere> sSphereCache = new(StringComparer.Ordinal);

    // src/world/shape.cpp shapeSphere: cached by name.
    public static bool ShapeSphere(ResourceManager res, string name, ref float radius, Span<float> center)
    {
        if (!sSphereCache.TryGetValue(name, out CachedSphere? c))
        {
            c = new CachedSphere();
            if (res.Read(name + ".dts", out byte[] data))
                c.Ok = ReadShapeSphere(data, ref c.Radius, c.Center);
            sSphereCache.Add(name, c);
        }
        if (c.Ok)
        {
            radius = c.Radius;
            c.Center.CopyTo(center);
        }
        return c.Ok;
    }

    private sealed class CachedBox
    {
        public bool Ok;
        public readonly float[] Box = new float[6];
    }
    private static readonly Dictionary<string, CachedBox> sBoxCache = new(StringComparer.Ordinal);

    // src/world/shape.cpp shapeBox: loads "<name>.dts" through the resource manager; results are cached by name.
    public static bool ShapeBox(ResourceManager res, string name, Span<float> box)
    {
        if (!sBoxCache.TryGetValue(name, out CachedBox? c))
        {
            c = new CachedBox();
            if (res.Read(name + ".dts", out byte[] data))
                c.Ok = ReadShapeBox(data, c.Box);
            sBoxCache.Add(name, c);
        }
        if (c.Ok)
            c.Box.CopyTo(box);
        return c.Ok;
    }

    // src/world/shape.cpp asciiCompareNoCase.
    private static int AsciiCompareNoCase(string a, string b)
    {
        if (a.Length != b.Length)
            return 1;
        for (int i = 0; i < a.Length; ++i)
        {
            char x = a[i], y = b[i];
            if (x >= 'A' && x <= 'Z')
                x = (char)(x + 32);
            if (y >= 'A' && y <= 'Z')
                y = (char)(y + 32);
            if (x != y)
                return 1;
        }
        return 0;
    }

    // src/world/shape.cpp findName (ShapeAnim).
    internal static int FindName(ShapeAnim a, string name)
    {
        for (int i = 0; i < a.Names.Count; ++i)
            if (AsciiCompareNoCase(a.Names[i], name) == 0)
                return i;
        return -1;
    }

    // src/world/shape.cpp quatMatrix: row-vector rotation matrix of a stored quaternion. Shape files hold the inverse
    // rotation, so this is the transpose of the usual matrix: m[1][2] = 2(yz - wx).
    internal static void QuatMatrix(ShapeAnim.Quat q, Span<double> m)
    {
        double x = q.X, y = q.Y, z = q.Z, w = q.W;
        m[0] = Sub(1, Mul(2, Add(Mul(y, y), Mul(z, z))));
        m[1] = Mul(2, Sub(Mul(x, y), Mul(w, z)));
        m[2] = Mul(2, Add(Mul(x, z), Mul(w, y)));
        m[3] = Mul(2, Add(Mul(x, y), Mul(w, z)));
        m[4] = Sub(1, Mul(2, Add(Mul(x, x), Mul(z, z))));
        m[5] = Mul(2, Sub(Mul(y, z), Mul(w, x)));
        m[6] = Mul(2, Sub(Mul(x, z), Mul(w, y)));
        m[7] = Mul(2, Add(Mul(y, z), Mul(w, x)));
        m[8] = Sub(1, Mul(2, Add(Mul(x, x), Mul(y, y))));
    }

    // src/world/shape.cpp mul3.
    internal static void Mul3(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> output)
    {
        for (int r = 0; r < 3; ++r)
            for (int c = 0; c < 3; ++c)
                output[r * 3 + c] = Add(Add(Mul(a[r * 3], b[c]), Mul(a[r * 3 + 1], b[3 + c])), Mul(a[r * 3 + 2], b[6 + c]));
    }

    // src/world/shape.cpp readShapeAnim.
    public static bool ReadShapeAnim(byte[] dts, ShapeAnim output)
    {
        var r = new Reader(dts);
        uint ver = r.Pers(out _);
        if (!r.Ok || ver < 1 || ver > 8)
            return false;
        var counts = new int[9];
        for (int i = 0; i < 9; ++i)
            counts[i] = r.GetI32();
        if (ver >= 2)
            r.GetI32();
        if (ver >= 4)
            r.GetI32();
        r.Skip(16);
        if (ver >= 8)
            r.Skip(24);
        for (int i = 0; i < 6; ++i)
            if (counts[i] < 0 || counts[i] > dts.Length)
                return false;
        output.Sequences = new List<ShapeSequence>();
        output.Names = new List<string>();
        output.SequenceNames = new List<int>();
        output.Nodes = new List<ShapeAnim.Node>();
        output.SubSequences = new List<ShapeAnim.SubSequence>();
        output.Keys = new List<ShapeAnim.Key>();
        output.Rotations = new List<ShapeAnim.Quat>();
        for (int i = 0; i < counts[0] && r.Ok; ++i)
        {
            var n = new ShapeAnim.Node();
            if (ver >= 8)
            {
                n.Name = r.GetI16();
                n.Parent = r.GetI16();
                n.NumSub = r.GetI16();
                n.FirstSub = r.GetI16();
                n.DefaultTransform = r.GetI16();
            }
            else
            {
                n.Name = r.GetI32();
                n.Parent = r.GetI32();
                n.NumSub = r.GetI32();
                n.FirstSub = r.GetI32();
                n.DefaultTransform = r.GetI32();
            }
            output.Nodes.Add(n);
        }
        long seqSize = ver >= 5 ? 32 : ver == 4 ? 24 : 16;
        var seqs = new List<ShapeSequence>();
        for (int i = 0; i < counts[1] && r.Ok; ++i)
        {
            var s = new ShapeSequence();
            output.SequenceNames.Add(r.GetI32());
            s.Cyclic = r.GetI32() != 0;
            s.Duration = r.GetF32();
            r.Skip(seqSize - 12);
            seqs.Add(s);
        }
        for (int i = 0; i < counts[2] && r.Ok; ++i)
        {
            var s = new ShapeAnim.SubSequence();
            if (ver >= 8)
            {
                s.Sequence = r.GetI16();
                s.NumKeys = r.GetI16();
                s.FirstKey = r.GetI16();
            }
            else
            {
                s.Sequence = r.GetI32();
                s.NumKeys = r.GetI32();
                s.FirstKey = r.GetI32();
            }
            output.SubSequences.Add(s);
        }
        for (int i = 0; i < counts[3] && r.Ok; ++i)
        {
            var k = new ShapeAnim.Key();
            k.Position = r.GetF32();
            uint v = r.GetU32();
            k.Transform = ver >= 8 ? (v & 0xffffu) : v;
            if (ver >= 3 && ver < 8)
                r.Skip(4);
            output.Keys.Add(k);
        }
        for (int i = 0; i < counts[4] && r.Ok; ++i)
        {
            var q = new ShapeAnim.Quat { W = 1 };
            if (ver >= 7)
            {
                q.X = DivF(r.GetI16(), 32767.0f);
                q.Y = DivF(r.GetI16(), 32767.0f);
                q.Z = DivF(r.GetI16(), 32767.0f);
                q.W = DivF(r.GetI16(), 32767.0f);
                r.Skip(ver >= 8 ? 12 : 24);
            }
            else
            {
                q.X = r.GetF32();
                q.Y = r.GetF32();
                q.Z = r.GetF32();
                q.W = r.GetF32();
                r.Skip(24);
            }
            output.Rotations.Add(q);
        }
        var name = new byte[24];
        for (int i = 0; i < counts[5] && r.Ok; ++i)
        {
            for (int k = 0; k < 24; ++k)
                name[k] = r.GetU8();
            output.Names.Add(Reader.CString(name, 0, 24));
        }
        if (!r.Ok)
            return false;
        for (int i = 0; i < seqs.Count; ++i)
        {
            int n = output.SequenceNames[i];
            if (n >= 0 && n < output.Names.Count)
                seqs[i].Name = output.Names[n];
        }
        output.Sequences = seqs;
        return true;
    }

    private static readonly Dictionary<string, (bool ok, ShapeAnim anim)> sAnimCache = new(StringComparer.Ordinal);

    // src/world/shape.cpp shapeAnim: cached by name; null when the shape is missing or malformed.
    public static ShapeAnim? GetShapeAnim(ResourceManager res, string name)
    {
        if (!sAnimCache.TryGetValue(name, out var c))
        {
            var anim = new ShapeAnim();
            bool ok = res.Read(name + ".dts", out byte[] data) && ReadShapeAnim(data, anim);
            c = (ok, anim);
            sAnimCache.Add(name, c);
        }
        return c.ok ? c.anim : null;
    }

    // src/world/shape.cpp rotatedWorldBox: world box of an object box under a transform (0x005edbb4 -> 0x005ed8bc):
    // rotated centre plus |M| applied to the half extents, then the translation.
    public static void RotatedWorldBox(ReadOnlySpan<float> local, in Mat m, Span<float> output)
    {
        // Half-sum centre and half extents, each stored as f32.
        var c = new Vec3();
        var e = new Vec3();
        for (int i = 0; i < 3; ++i)
        {
            float s = Store(Add(local[i], local[i + 3]));
            c[i] = Store(Mul(s, 0.5f));
            float d = Store(Sub(local[i + 3], local[i]));
            e[i] = Store(Mul(d, 0.5f));
        }
        Vec3 wc = m.ToWorldVector(c);
        // Extent axis j: sum over i of |m[i*3 + j]| * e[i]; products and partial sums are stored as f64, the result as
        // f32.
        for (int j = 0; j < 3; ++j)
        {
            double acc = Mul(Math.Abs((double)m.M[j]), e[0]);
            acc = Add(Mul(Math.Abs((double)m.M[3 + j]), e[1]), acc);
            float ext = Store(Add(Mul(Math.Abs((double)m.M[6 + j]), e[2]), acc));
            output[j] = Store(Sub(wc[j], ext));
            output[j + 3] = Store(Add(ext, wc[j]));
        }
        for (int i = 0; i < 3; ++i)
        {
            output[i + 3] = Store(Add(m.P[i], output[i + 3]));
            output[i] = Store(Add(m.P[i], output[i]));
        }
    }

    // src/world/shape.cpp sameNameNoCase.
    private static bool SameNameNoCase(string a, string b)
    {
        for (int i = 0; ; ++i)
        {
            char x = i < a.Length ? a[i] : '\0', y = i < b.Length ? b[i] : '\0';
            if (x >= 'A' && x <= 'Z')
                x = (char)(x + 32);
            if (y >= 'A' && y <= 'Z')
                y = (char)(y + 32);
            if (x != y)
                return false;
            if (x == 0)
                return true;
        }
    }

    // src/world/shape.cpp readShapeCollisionBoxes: collision detail (docs/collision.md 6 T2.1, T9): the detail whose
    // root node is named "collision". Each object is returned as the frame box of its mesh plus the object offset, in
    // shape root space. False when the shape has no such detail (versions 7 and 8 only).
    public static bool ReadShapeCollisionBoxes(byte[] dts, List<Float6> output)
    {
        output.Clear();
        var r = new Reader(dts);
        uint ver = r.Pers(out _);
        if (!r.Ok || (ver != 7 && ver != 8))
            return false;
        var counts = new int[9];
        for (int i = 0; i < 9; ++i)
            counts[i] = r.GetI32();
        int nTransitions = r.GetI32(), nTriggers = r.GetI32();
        r.Skip(ver >= 8 ? 40 : 16); // radius, centre, box
        bool v8 = ver == 8;
        long[] sizes = { v8 ? 10 : 20, 32, v8 ? 6 : 12, v8 ? 8 : 12, v8 ? 20 : 32, 24, v8 ? 28 : 72, 8, v8 ? 40 : 52, 8 };
        int[] n = { counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6], counts[7], nTransitions, nTriggers };
        var at = new long[10];
        for (int i = 0; i < 10 && r.Ok; ++i)
        {
            // A count the file cannot hold is malformed (D11).
            if (n[i] < 0 || n[i] > dts.Length)
                return false;
            at[i] = r.Pos;
            r.Skip(n[i] * sizes[i]);
        }
        r.Skip(8); // the v >= 5 and v >= 6 words
        if (!r.Ok || counts[8] < 0 || counts[8] > dts.Length)
            return false;
        int NodeName(int node) => v8 ? Reader.I16At(dts, at[0] + (long)node * 10) : Reader.I32At(dts, at[0] + (long)node * 20);
        int NodeParent(int node) =>
            v8 ? Reader.I16At(dts, at[0] + (long)node * 10 + 2) : Reader.I32At(dts, at[0] + (long)node * 20 + 4);
        // T2.1: the first name "collision", the first node with that name, a detail rooted at that node.
        int name = -1;
        for (int i = 0; i < counts[5] && name < 0; ++i)
            if (SameNameNoCase(Reader.CString(dts, at[5] + (long)i * 24, 24), "collision"))
                name = i;
        int root = -1;
        for (int i = 0; i < counts[0] && root < 0 && name >= 0; ++i)
            if (NodeName(i) == name)
                root = i;
        bool detail = false;
        for (int i = 0; i < counts[7] && root >= 0; ++i)
            detail = detail || Reader.I32At(dts, at[7] + (long)i * 8) == root;
        if (!detail)
            return false;
        // Nodes of the subtree: the root, then by descending index (T2.4 visits children in descending node index).
        var nodes = new List<int> { root };
        for (int i = counts[0] - 1; i >= 0; --i)
        {
            int p = i;
            for (int depth = 0; depth < counts[0] && p >= 0 && p < counts[0] && p != root; ++depth)
                p = NodeParent(p);
            if (i != root && p == root)
                nodes.Add(i);
        }
        var meshes = new List<long>();
        for (int m = 0; m < counts[8] && r.Ok; ++m)
        {
            meshes.Add(r.Pos);
            r.Pers(out long meshEnd);
            if (!r.Ok || meshEnd <= r.Pos)
                return false;
            r.Pos = meshEnd;
        }
        Span<float> offset = stackalloc float[3], scale = stackalloc float[3], origin = stackalloc float[3];
        foreach (int node in nodes)
            for (int k = 0; k < counts[6]; ++k)
            {
                long o = at[6] + (long)k * sizes[6];
                if (Reader.I16At(dts, o + 8) != node)
                    continue;
                int mesh = Reader.I32At(dts, o + 4);
                if (mesh < 0 || mesh >= meshes.Count)
                    continue;
                offset.Clear();
                if (v8)
                    for (int i = 0; i < 3; ++i)
                        offset[i] = Reader.F32At(dts, o + 12 + i * 4);
                var m = new Reader(dts) { Pos = meshes[mesh] };
                uint mver = m.Pers(out _);
                int nVerts = m.GetI32();
                m.GetI32();
                int nTex = m.GetI32(), nFaces = m.GetI32(), nFrames = m.GetI32();
                if (mver >= 2)
                    m.Skip(4);
                if (mver < 3)
                    m.Skip(24);
                m.Skip(4);
                long verts = m.Pos;
                if (!m.Ok || nVerts < 2 || nTex < 0 || nFaces < 0 || nFrames < 1 || nVerts > dts.Length ||
                    nTex > dts.Length || nFaces > dts.Length)
                    continue;
                m.Skip((long)nVerts * 4 + (long)nTex * 8 + (long)nFaces * 28);
                int first = m.GetI32();
                for (int i = 0; i < 3; ++i)
                    scale[i] = m.GetF32();
                for (int i = 0; i < 3; ++i)
                    origin[i] = m.GetF32();
                if (!m.Ok || first < 0 || first >= nVerts - 1)
                    continue;
                // Frame box: decoded vertices firstVert and firstVert + 1, plus the object offset.
                var box = new Float6();
                for (int c = 0; c < 2; ++c)
                {
                    long v = verts + (long)(first + c) * 4;
                    for (int i = 0; i < 3; ++i)
                        box[c * 3 + i] = Store(Add(Store(Add(Mul(dts[v + i], scale[i]), origin[i])), offset[i]));
                }
                output.Add(box);
            }
        return output.Count != 0;
    }

    private static readonly Dictionary<string, List<Float6>> sCollisionCache = new(StringComparer.Ordinal);

    // src/world/shape.cpp shapeCollisionBoxes: cached by name; null without a collision detail.
    public static List<Float6>? ShapeCollisionBoxes(ResourceManager res, string name)
    {
        if (!sCollisionCache.TryGetValue(name, out List<Float6>? boxes))
        {
            boxes = new List<Float6>();
            if (res.Read(name + ".dts", out byte[] data))
                ReadShapeCollisionBoxes(data, boxes);
            sCollisionCache.Add(name, boxes);
        }
        return boxes.Count == 0 ? null : boxes;
    }

    // ---- shape model and instance (docs/collision.md 6 T1-T8) ----

    // src/world/shape.cpp ldexpl(LD(mantissa), exp2) of the portable build: the 64-bit mantissa rounded to the 53 bits
    // of a double (the compiler folds the conversion to nearest), then scaled.
    private static double LdConstant(ulong mantissa, int exp2)
    {
        const int shift = 11;
        ulong kept = mantissa >> shift;
        ulong rest = mantissa & ((1UL << shift) - 1);
        ulong half = 1UL << (shift - 1);
        if (rest > half || (rest == half && (kept & 1) != 0))
            ++kept;
        return Math.ScaleB(kept, exp2 + shift);
    }

    // src/world/shape.cpp kQuatScale: 1/32767 as the engine holds it, the 80-bit constant at 0x005bdd2c
    // (3ff0 8001000200040008).
    internal static readonly double kQuatScale = LdConstant(0x8001000200040008UL, -78);

    // src/world/shape.cpp pointInTriangle: 0x005eb898, T2.8.
    internal static bool PointInTriangle(ReadOnlySpan<float> q, ReadOnlySpan<float> n, ReadOnlySpan<float> a,
                                         ReadOnlySpan<float> b, ReadOnlySpan<float> c)
    {
        int u = 0, v = 1;
        float ax = Math.Abs(n[0]), ay = Math.Abs(n[1]), az = Math.Abs(n[2]);
        if (ax < ay && az < ay)
        {
            u = 0;
            v = 2;
        }
        else if (ay < ax && az < ax)
        {
            u = 1;
            v = 2;
        }
        float s1 = F32Chop(Add(Mul(Sub(a[u], q[u]), Sub(a[v], b[v])), Mul(Sub(a[v], q[v]), Sub(b[u], a[u]))));
        float s2 = F32Chop(Add(Mul(Sub(b[u], q[u]), Sub(b[v], c[v])), Mul(Sub(b[v], q[v]), Sub(c[u], b[u]))));
        float s3 = F32Chop(Add(Mul(Sub(c[u], q[u]), Sub(c[v], a[v])), Mul(Sub(c[v], q[v]), Sub(a[u], c[u]))));
        return !(Mul(s1, s2) < 0.0 || Mul(s2, s3) < 0.0);
    }

    // src/world/shape.cpp slabTest: m_Box3F_collideLine (0x005ebe80, T7.1).
    internal static bool SlabTest(ReadOnlySpan<float> s, ReadOnlySpan<float> e, ReadOnlySpan<float> mn, ReadOnlySpan<float> mx,
                                  ref float t)
    {
        for (int a = 0; a < 3; ++a)
        {
            int b = (a + 1) % 3, c = (a + 2) % 3;
            float num, den;
            if (s[a] < mn[a])
            {
                if (e[a] < mn[a])
                    continue;
                num = F32Chop(Sub(mn[a], s[a]));
                den = F32Chop(Sub(e[a], s[a]));
            }
            else if (s[a] > mx[a])
            {
                if (e[a] > mx[a])
                    continue;
                num = F32Chop(Sub(s[a], mx[a]));
                den = F32Chop(Sub(s[a], e[a]));
            }
            else
            {
                continue;
            }
            bool Inside(ReadOnlySpan<float> s, ReadOnlySpan<float> e, ReadOnlySpan<float> mn, ReadOnlySpan<float> mx, int k) =>
                Mul(Sub(mx[k], s[k]), den) > Mul(Sub(e[k], s[k]), num) && Mul(Sub(mn[k], s[k]), den) < Mul(Sub(e[k], s[k]), num);
            if (Inside(s, e, mn, mx, b) && Inside(s, e, mn, mx, c))
            {
                t = F32Chop(Div(num, den));
                return true;
            }
        }
        for (int a = 0; a < 3; ++a)
            if (!(mn[a] <= s[a] && s[a] <= mx[a]))
                return false;
        t = 0.0f;
        return true;
    }

    // src/world/shape.cpp cross: 0x005ece6f, each component one f32 store.
    internal static void Cross(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> output)
    {
        float x = F32Chop(Sub(Mul(a[1], b[2]), Mul(a[2], b[1])));
        float y = F32Chop(Sub(Mul(a[2], b[0]), Mul(a[0], b[2])));
        float z = F32Chop(Sub(Mul(a[0], b[1]), Mul(a[1], b[0])));
        output[0] = x;
        output[1] = y;
        output[2] = z;
    }

    // src/world/shape.cpp nameEquals.
    internal static bool NameEquals(string a, string b) => SameNameNoCase(a, b);

    // src/world/shape.cpp boxCollideLine: m_Box3F_collideLine (0x005ebe80): segment s..e against the box. t is the entry
    // parameter as an f32, 0 when s is inside the box. Faces are entered strictly inside their edges.
    public static bool BoxCollideLine(ReadOnlySpan<float> s, ReadOnlySpan<float> e, ReadOnlySpan<float> mn, ReadOnlySpan<float> mx,
                                      ref float t) => SlabTest(s, e, mn, mx, ref t);

    // src/world/shape.cpp tmatMul: m_mul_TMat3F (0x005ed2b2): out = a x b (apply a, then b).
    public static void TmatMul(in TMat3 a, in TMat3 b, out TMat3 output)
    {
        var o = new TMat3();
        for (int r = 0; r < 3; ++r)
            for (int c = 0; c < 3; ++c)
                o.M[r * 3 + c] = F32Chop(Add(Add(Mul(a.M[r * 3], b.M[c]), Mul(a.M[r * 3 + 1], b.M[3 + c])), Mul(a.M[r * 3 + 2], b.M[6 + c])));
        for (int c = 0; c < 3; ++c)
            o.P[c] = F32Chop(Add(Add(Add(Mul(a.P[0], b.M[c]), Mul(a.P[1], b.M[3 + c])), b.P[c]), Mul(a.P[2], b.M[6 + c])));
        o.Flags = a.Flags | b.Flags;
        output = o;
    }

    // src/world/shape.cpp tmatInverse: inverse as inlined by the collision code (T1.1).
    public static void TmatInverse(in TMat3 a, out TMat3 output)
    {
        TMat3 o = a;
        if ((a.Flags & 4) == 0)
        {
            (o.M[1], o.M[3]) = (o.M[3], o.M[1]);
            (o.M[2], o.M[6]) = (o.M[6], o.M[2]);
            (o.M[5], o.M[7]) = (o.M[7], o.M[5]);
        }
        else
        {
            // General matrix (flag 4), as inlined at 0x0043786d and 0x005bf190: nine cofactors stored as f32, the
            // determinant from unrounded cofactors along the first column, the unrounded square compared with 1e-12f,
            // then cofactor x f32(1 / det). No stock transform sets flag 4 on a server (docs/collision.md T1.1).
            ReadOnlySpan<float> m = a.M;
            Span<float> co = stackalloc float[9];
            co[0] = F32Chop(Sub(Mul(m[4], m[8]), Mul(m[5], m[7])));
            co[1] = F32Chop(Sub(Mul(m[7], m[2]), Mul(m[8], m[1])));
            co[2] = F32Chop(Sub(Mul(m[1], m[5]), Mul(m[2], m[4])));
            co[3] = F32Chop(Sub(Mul(m[5], m[6]), Mul(m[3], m[8])));
            co[4] = F32Chop(Sub(Mul(m[8], m[0]), Mul(m[6], m[2])));
            co[5] = F32Chop(Sub(Mul(m[2], m[3]), Mul(m[0], m[5])));
            co[6] = F32Chop(Sub(Mul(m[3], m[7]), Mul(m[4], m[6])));
            co[7] = F32Chop(Sub(Mul(m[6], m[1]), Mul(m[7], m[0])));
            co[8] = F32Chop(Sub(Mul(m[0], m[4]), Mul(m[1], m[3])));
            float det = F32Chop(Add(Add(Mul(Sub(Mul(m[4], m[8]), Mul(m[5], m[7])), m[0]),
                                        Mul(Sub(Mul(m[2], m[7]), Mul(m[1], m[8])), m[3])),
                                    Mul(Sub(Mul(m[1], m[5]), Mul(m[2], m[4])), m[6])));
            if (Mul(det, det) >= 1e-12f)
            {
                float inv = F32Chop(Div(1.0f, det));
                for (int i = 0; i < 9; ++i)
                    o.M[i] = F32Chop(Mul(co[i], inv));
            }
        }
        ReadOnlySpan<float> neg = stackalloc float[3] { -a.P[0], -a.P[1], -a.P[2] };
        X87.MulVector(neg, o.M, o.P);
        output = o;
    }

    // src/world/shape.cpp quatToMatrix: quaternion to matrix (0x005ee124, T2.5 step 2): sets the 3x3 and the rotation
    // flags.
    public static void QuatToMatrix(ReadOnlySpan<float> q, ref TMat3 output)
    {
        float x = q[0], y = q[1], z = q[2], w = q[3];
        if (Mul(x, x) < 1e-12f && Mul(y, y) < 1e-12f && Mul(z, z) < 1e-12f)
        {
            output.M = Float9.Identity();
            output.Flags &= ~5u;
            return;
        }
        // Twelve f32 intermediates, then sums of them (0x005ee1c7..0x005ee249).
        float x2 = F32Chop(Mul(x, 2.0f)), y2 = F32Chop(Mul(y, 2.0f)), z2 = F32Chop(Mul(z, 2.0f));
        float wx = F32Chop(Mul(w, x2)), wy = F32Chop(Mul(w, y2)), wz = F32Chop(Mul(w, z2));
        float xx = F32Chop(Mul(x, x2)), xy = F32Chop(Mul(x, y2)), xz = F32Chop(Mul(x, z2));
        float yy = F32Chop(Mul(y, y2)), yz = F32Chop(Mul(y, z2)), zz = F32Chop(Mul(z, z2));
        output.M[0] = F32Chop(Sub(1.0, Add(yy, zz)));
        output.M[1] = F32Chop(Sub(xy, wz));
        output.M[2] = F32Chop(Add(xz, wy));
        output.M[3] = F32Chop(Add(xy, wz));
        output.M[4] = F32Chop(Sub(1.0, Add(xx, zz)));
        output.M[5] = F32Chop(Sub(yz, wx));
        output.M[6] = F32Chop(Sub(xz, wy));
        output.M[7] = F32Chop(Add(yz, wx));
        output.M[8] = F32Chop(Sub(1.0, Add(xx, yy)));
        output.Flags = (output.Flags & ~4u) | 1u;
    }

    // src/world/shape.cpp eulerMatrix: m_mat3_setEuler (0x005ed42e), the 3x3 and flag bits 0 and 2 from Euler angles
    // x, y, z. The translation and flag bit 1 are left as they are. Portable form: fsincosLD is sin and cos of the
    // double (libm, not rounded toward zero).
    public static void EulerMatrix(ReadOnlySpan<float> e, ref TMat3 output)
    {
        using var chop = ChopScope();
        static bool Zero(float v) => !(v != 0.0f) || float.IsNaN(v); // ftst: 0, -0 and NaN
        static void SinCos(float a, out double s, out double c)
        {
            s = Math.Sin(a);
            c = Math.Cos(a);
        }
        if (Zero(e[0]) && Zero(e[1]))
        {
            if (Zero(e[2]))
            {
                output.M = Float9.Identity();
                output.Flags &= ~5u;
                return;
            }
            SinCos(e[2], out double sz1, out double cz1);
            Float9 m = default;
            m[0] = F32Chop(cz1);
            m[1] = F32Chop(sz1);
            m[3] = F32Chop(-sz1);
            m[4] = F32Chop(cz1);
            m[8] = 1;
            output.M = m;
            output.Flags = (output.Flags | 1u) & ~4u;
            return;
        }
        SinCos(e[2], out double sz, out double cz);
        SinCos(e[1], out double sy, out double cy);
        SinCos(e[0], out double sx, out double cx);
        double cysz = Mul(cy, sz), cycz = Mul(cy, cz), szsy = Mul(sz, sy), czsy = Mul(cz, sy);
        output.M[8] = F32Chop(Mul(cy, cx));
        output.M[4] = F32Chop(Mul(cz, cx));
        output.M[2] = F32Chop(Mul(sy, -cx));
        output.M[3] = F32Chop(Mul(-cx, sz));
        output.M[5] = F32Chop(sx);
        output.M[7] = F32Chop(Sub(szsy, Mul(cycz, sx)));
        output.M[0] = F32Chop(Sub(cycz, Mul(szsy, sx)));
        output.M[1] = F32Chop(Add(cysz, Mul(czsy, sx)));
        output.M[6] = F32Chop(Add(czsy, Mul(cysz, sx)));
        output.Flags = (output.Flags | 1u) & ~4u;
    }

    // src/world/shape.cpp shapeModel: cached by name ("<name>.dts"); null when the shape is missing or malformed.
    private static readonly Dictionary<string, (bool ok, ShapeModel model)> sModelCache = new(StringComparer.Ordinal);

    public static ShapeModel? GetShapeModel(ResourceManager res, string name)
    {
        if (!sModelCache.TryGetValue(name, out var c))
        {
            var model = new ShapeModel();
            bool ok = res.Read(name + ".dts", out byte[] data) && ShapeModel.Parse(data, model);
            c = (ok, model);
            sModelCache.Add(name, c);
        }
        return c.ok ? c.model : null;
    }

    // src/world/shape.cpp childLists: children of every node in visiting order (T2.4): each node is inserted at the head
    // of its parent's list, so children are met in descending node index. Nodes whose parent index is not an earlier
    // node are roots (a file the original would misread, D11).
    internal static List<int>[] ChildLists(ShapeModel s)
    {
        var kids = new List<int>[s.Nodes.Length];
        for (int i = 0; i < kids.Length; ++i)
            kids[i] = new List<int>();
        for (int i = s.Nodes.Length - 1; i >= 1; --i)
        {
            int p = s.Nodes[i].Parent;
            if (p >= 0 && p < i)
                kids[p].Add(i);
        }
        return kids;
    }

    // src/world/shape.cpp subtree: preorder over the subtree of root (T2.4).
    internal static void Subtree(List<int>[] kids, int root, List<int> output)
    {
        if (root < 0 || root >= kids.Length)
            return;
        var stack = new List<int> { root };
        while (stack.Count != 0)
        {
            int n = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            output.Add(n);
            List<int> k = kids[n];
            for (int i = k.Count - 1; i >= 0; --i)
                stack.Add(k[i]);
        }
    }

    // src/world/shape.cpp hasParentEntry.
    internal static bool HasParentEntry(ShapeModel s, int node)
    {
        int p = s.Nodes[node].Parent;
        return node >= 1 && p >= 0 && p < node;
    }

    // src/world/shape.cpp currentKey: current key of a sub-sequence at a thread position (0x005b8e58): the last key
    // whose position is not above it. Before the first key a cyclic sequence gives the last key, a one-shot sequence the
    // first. -1 (null) when the sub-sequence is malformed.
    private static int CurrentKey(ShapeModel sh, in ShapeModel.SubSequence sub, float position)
    {
        if (sub.NumKeys <= 0 || sub.FirstKey < 0 || sub.FirstKey + sub.NumKeys > sh.Keys.Length)
            return -1;
        int last = sub.FirstKey - 1;
        for (int i = sub.FirstKey; i < sub.FirstKey + sub.NumKeys; ++i)
        {
            if (sh.Keys[i].Position > position)
                break;
            last = i;
        }
        if (last < sub.FirstKey)
        {
            bool cyclic = sub.Sequence >= 0 && sub.Sequence < sh.Sequences.Length && sh.Sequences[sub.Sequence].Cyclic;
            last = cyclic ? sub.FirstKey + sub.NumKeys - 1 : sub.FirstKey;
        }
        return last;
    }

    // src/world/shape.cpp subSequenceFor: sub-sequence of a node or object (first, count) that belongs to a sequence,
    // or -1 (null).
    private static int SubSequenceFor(ShapeModel sh, int first, int count, int sequence)
    {
        if (first < 0 || count <= 0)
            return -1;
        for (int i = first; i < first + count && i < sh.SubSequences.Length; ++i)
            if (sh.SubSequences[i].Sequence == sequence)
                return i;
        return -1;
    }

    // ---- x87 library routines of the interpolation (spec T2.5a, crt_acos 0x00606769, crt_sin 0x00606ff0), portable
    // form: fpatanLD is atan2, fptanLD tan, fldpiLD the double pi. ----

    // src/world/shape.cpp x87Fpatan: atan(y / x).
    private static double X87Fpatan(double y, double x) => Math.Atan2(y, x);

    // src/world/shape.cpp x87Fptan.
    private static double X87Fptan(double x) => Math.Tan(x);

    // src/world/shape.cpp x87Pi.
    private static double X87Pi() => 3.14159265358979323846;

    // src/world/shape.cpp crtAcos: acos for 0 <= x < 1.
    private static double CrtAcos(double x)
    {
        if (x == 0.0)
            return Mul(X87Pi(), 0.5);
        double s = Sqrt(Sub(1.0, Mul(x, x)));
        if (s > x)
            return Sub(Mul(X87Pi(), 0.5), X87Fpatan(x, s));
        if (s == x)
            return Mul(X87Pi(), 0.25);
        return X87Fpatan(s, x);
    }

    // src/world/shape.cpp crtAsin: asin (crt_asin 0x00606784 -> 0x00606680): the sign is taken off, sqrt(1 - x^2) is
    // compared with |x| and the smaller over the larger goes to fpatan. |x| >= 1 gives pi / 2 (above 1 the original
    // reports a domain error, D11).
    private static double CrtAsin(double x)
    {
        bool neg = double.IsNegative(x);
        double ax = Math.Abs(x);
        double r;
        if (ax == 0.0)
        {
            r = 0.0;
        }
        else if (ax >= 1.0)
        {
            r = Mul(X87Pi(), 0.5);
        }
        else
        {
            double s = Sqrt(Sub(1.0, Mul(ax, ax)));
            if (s > ax)
                r = X87Fpatan(ax, s);
            else if (s == ax)
                r = Mul(X87Pi(), 0.25);
            else
                r = Sub(Mul(X87Pi(), 0.5), X87Fpatan(s, ax));
        }
        return neg ? -r : r;
    }

    private static readonly double kQuarterPi = LdConstant(0xc90fdaa22168c235UL, -64);

    // src/world/shape.cpp crtSin: sin for 0 <= x < pi: reduction by pi / 4 (80-bit constant at 0x0065c624), fptan.
    private static double CrtSin(double x)
    {
        if (x == 0.0)
            return x;
        double quarter = kQuarterPi;
        double r = x;
        int k = 0;
        while (r >= quarter && k < 8)
        { // fprem is exact, and so is each subtraction here
            r = Sub(r, quarter);
            ++k;
        }
        if ((k & 1) != 0)
            r = Sub(quarter, r);
        double tn = X87Fptan(r);
        double root = Sqrt(Add(1.0, Mul(tn, tn)));
        if ((k >> 2) != 0)
            root = -root;
        return ((k & 3) == 0 || (k & 3) == 3) ? Div(tn, root) : Div(1.0, root);
    }

    // src/world/shape.cpp crtCosCore: cos (crt_cos 0x00606890 -> crt_trig_core 0x00606a90 with the quadrant offset 2):
    // the same reduction by pi / 4 and fptan as the sine; below pi / 4 the result is 1 / sqrt(1 + tan^2).
    private static double CrtCosCore(double x)
    {
        double ax = Math.Abs(x);
        if (ax == 0.0)
            return 1.0;
        double quarter = kQuarterPi;
        if (!(ax < Mul(8.0, quarter)))
            return Math.Cos(ax); // more than one turn: the fprem loop of the original, not needed by any caller
        double r = ax;
        int k = 0;
        while (r >= quarter && k < 8)
        {
            r = Sub(r, quarter);
            ++k;
        }
        k = (k + 2) & 7;
        if ((k & 1) != 0)
            r = Sub(quarter, r);
        double tn = X87Fptan(r);
        double root = Sqrt(Add(Mul(tn, tn), 1.0));
        if ((k >> 2) != 0)
            root = -root;
        return ((k & 3) == 0 || (k & 3) == 3) ? Div(tn, root) : Div(1.0, root);
    }

    // src/world/shape.cpp quatInterpolate: m_quat_interpolate (0x005ee2e0), slerp with f64 scale factors, linear when
    // the quaternions are within 1e-5 of each other.
    private static void QuatInterpolate(ReadOnlySpan<float> q1, ReadOnlySpan<float> q2, float t, Span<float> output)
    {
        double c = Add(Add(Add(Mul(q1[0], q2[0]), Mul(q1[1], q2[1])), Mul(q1[2], q2[2])), Mul(q1[3], q2[3]));
        float sign = 1.0f;
        if (c < 0.0)
        {
            c = -c;
            sign = -1.0f;
        }
        double s0, s1;
        if (Sub(1.0, c) > 1e-5)
        {
            double omega = CrtAcos(c);
            double sinom = CrtSin(omega);
            s0 = Div(CrtSin(Mul(Sub(1.0, t), omega)), sinom);
            s1 = Div(Mul(CrtSin(Mul(t, omega)), sign), sinom);
        }
        else
        {
            s0 = Sub(1.0, t);
            s1 = Mul(sign, t);
        }
        for (int i = 0; i < 4; ++i)
            output[i] = F32Chop(Add(Mul(s0, q1[i]), Mul(s1, q2[i])));
    }

    // src/world/shape.cpp interpolateToMatrix: TSTransform_interpolateToMatrix (0x005c53d4).
    private static void InterpolateToMatrix(in ShapeModel.Transform ta, in ShapeModel.Transform tb, float t, ref TMat3 output)
    {
        float omt = F32Chop(Sub(1.0, t));
        Span<float> qa = stackalloc float[4], qb = stackalloc float[4], q = stackalloc float[4];
        var trans = new Float3();
        for (int i = 0; i < 3; ++i)
            trans[i] = F32Chop(Add(Mul(ta.T[i], omt), Mul(tb.T[i], t)));
        for (int i = 0; i < 4; ++i)
        {
            qb[i] = F32Chop(Mul(kQuatScale, tb.Q[i]));
            qa[i] = F32Chop(Mul(kQuatScale, ta.Q[i]));
        }
        QuatInterpolate(qa, qb, t, q);
        output.Flags = 0;
        output.P = trans;
        QuatToMatrix(q, ref output);
        output.Flags |= 2u;
    }

    // src/world/shape.cpp nodeSubSequence: TSNode_findSubSequence (0x005b9380), binary search of the node's
    // sub-sequences; -1 (null) when none.
    private static int NodeSubSequence(ShapeModel sh, in ShapeModel.Node nd, int s)
    {
        int n = nd.NumSub, first = nd.FirstSub;
        if (n <= 0 || first < 0 || first + n > sh.SubSequences.Length || s < 0)
            return -1;
        int mid = s < n ? first + s : first + (n >> 1);
        int lo = first, hi = first + n;
        while (hi > lo + 1)
        {
            int d = sh.SubSequences[mid].Sequence - s;
            if (d > 0)
                hi = mid;
            else if (d < 0)
                lo = mid;
            else
                return mid;
            mid = (hi + lo) >> 1;
        }
        return sh.SubSequences[mid].Sequence == s ? mid : -1;
    }

    // src/world/shape.cpp findKeys: TSSubSequence_findKeys (0x005b8ef0), the two keys around a position and the
    // fraction.
    private static bool FindKeys(ShapeModel sh, in ShapeModel.SubSequence sub, float p, out int loOut, out int hiOut, out float frac)
    {
        loOut = hiOut = 0;
        frac = 0;
        int n = sub.NumKeys, f = sub.FirstKey;
        if (n <= 0 || f < 0 || f + n > sh.Keys.Length || !float.IsFinite(p))
            return false;
        float K(int i) => sh.Keys[i].Position;
        int lo = f - 1, hi = f + n;
        double probe = Add(Mul(Sub(n, 0.01f), p), f);
        int mid = probe > f ? (probe < f + n - 1 ? (int)probe : f + n - 1) : f; // outside: clamped (D11)
        // A key position that is not a number moves neither bound: the original never leaves the loop then (D11). The
        // search halves the range otherwise.
        for (int guard = 0; lo + 1 < hi && guard < 64; ++guard)
        {
            float kp = K(mid);
            if (Add(p, 0.001f) >= kp)
                lo = mid;
            if (Sub(p, 0.001f) <= kp)
                hi = mid;
            mid = (lo + hi) >> 1;
        }
        bool cyclic = sub.Sequence >= 0 && sub.Sequence < sh.Sequences.Length && sh.Sequences[sub.Sequence].Cyclic;
        if (cyclic)
        {
            float span;
            if (f > lo)
            {
                lo = f + n - 1;
                if (hi > f + n - 1)
                    hi = f + n - 1;
                span = F32Chop(Add(Sub(K(hi), K(lo)), 1.0f));
            }
            else if (f + n == hi)
            {
                hi = f;
                span = F32Chop(Sub(Add(K(hi), 1.0f), K(lo)));
            }
            else
            {
                span = F32Chop(Sub(K(hi), K(lo)));
            }
            if (lo == hi)
            {
                frac = 0.0f;
            }
            else if (Mul(span, span) < 1e-12f)
            {
                float d = F32Chop(Sub(p, K(lo)));
                frac = Mul(d, d) < 1e-12f ? 0.0f : 1.0f;
            }
            else
            {
                frac = F32Chop(Div(Sub(p, K(lo)), span));
            }
        }
        else
        {
            if (f > lo)
            {
                lo = f;
                if (hi > f + n - 1)
                    hi = f + n - 1;
                frac = 0.0f;
            }
            else if (f + n == hi)
            {
                hi = f + n - 1;
                frac = 1.0f;
            }
            else if (lo == hi)
            {
                frac = 0.0f;
            }
            else
            {
                float d = F32Chop(Sub(K(hi), K(lo)));
                frac = d == 0.0f ? 0.0f : F32Chop(Div(Sub(p, K(lo)), d));
            }
        }
        loOut = lo;
        hiOut = hi;
        return true;
    }

    // src/world/shape.cpp threadOrder: thread vector order of an animate pass (0x005c0a1c): ascending priority. The
    // vector is sorted only when it is out of order, with the C runtime qsort.
    internal static List<int> ThreadOrder(ShapeInstance inst)
    {
        var order = new int[inst.Threads.Count];
        bool sorted = true;
        for (int i = 0; i < order.Length; ++i)
        {
            order[i] = i;
            if (i != 0 && inst.Threads[i].Priority < inst.Threads[i - 1].Priority)
                sorted = false;
        }
        if (!sorted)
            Borland.BorlandQsort<int>(order, (a, b) =>
            {
                int pa = inst.Threads[a].Priority, pb = inst.Threads[b].Priority;
                return pa < pb ? -1 : pb < pa ? 1 : 0;
            });
        return new List<int>(order);
    }

    // src/world/shape.cpp threadTransform: local transform of a node from its controlling thread (0x005bd1ec,
    // 0x005bd9b0 state 1): the first thread in vector order whose sequence has keys for the node. False when no thread
    // controls the node.
    internal static bool ThreadTransform(ShapeInstance inst, List<int> order, int node, ref TMat3 local)
    {
        ShapeModel sh = inst.ShapeData!;
        ref ShapeModel.Node nd = ref sh.Nodes[node];
        foreach (int ti in order)
        {
            ShapeThread th = inst.Threads[ti];
            if (SubSequenceFor(sh, nd.FirstSub, nd.NumSub, th.Sequence) < 0)
                continue;
            int sub = NodeSubSequence(sh, nd, th.Sequence);
            if (sub < 0 || !FindKeys(sh, sh.SubSequences[sub], th.Position, out int lo, out int hi, out float frac))
                return false; // the original reads through a null sub-sequence here (D11)
            ref ShapeModel.Key ka = ref sh.Keys[lo];
            ref ShapeModel.Key kb = ref sh.Keys[hi];
            if (ka.Index >= sh.Transforms.Length || kb.Index >= sh.Transforms.Length)
                return false;
            inst.NodeActive[node] = (byte)((ka.Flags & 0x8000) != 0 ? 1 : 0);
            InterpolateToMatrix(sh.Transforms[ka.Index], sh.Transforms[kb.Index], frac, ref local);
            local.Flags &= ~4u;
            return true;
        }
        return false;
    }

    // src/world/shape.cpp objectThreadState: frame and visibility of an object from the thread keyframes (T2.6 step 2):
    // threads from the last to the first of the vector order, each key overriding what its flags mark as valid.
    private static void ObjectThreadState(ShapeInstance inst, List<int> order, int obj, ref ShapeInstance.ObjectState st)
    {
        ShapeModel sh = inst.ShapeData!;
        ref ShapeModel.Object ob = ref sh.Objects[obj];
        for (int i = order.Count; i-- > 0;)
        {
            ShapeThread th = inst.Threads[order[i]];
            int sub = SubSequenceFor(sh, ob.FirstSub, ob.NumSub, th.Sequence);
            int key = sub >= 0 ? CurrentKey(sh, sh.SubSequences[sub], th.Position) : -1;
            if (key < 0)
                continue;
            ref ShapeModel.Key k = ref sh.Keys[key];
            if ((k.Flags & 0x1000) != 0)
                st.Frame = k.Index;
            if ((k.Flags & 0x4000) != 0)
                st.Visible = (k.Flags & 0x8000) != 0;
        }
    }

    // src/world/shape.cpp crtCos: cos of the C runtime (crt_cos 0x00606890), extended result. The caller holds a Chop
    // scope.
    public static double CrtCos(double x) => CrtCosCore(x);

    // src/world/shape.cpp matrixEulerX: x angle of Mat3_getEuler (0x005eceb4): asin of m[5] as a double through the
    // runtime's asin, stored as f32. 0 for a matrix without the rotation flag.
    public static float MatrixEulerX(in TMat3 m)
    {
        if ((m.Flags & 1u) == 0)
            return 0.0f;
        using var chop = ChopScope();
        return F32Chop(CrtAsin(m.M[5]));
    }

    // src/world/shape.cpp transformMatrix: local transform of a stored key (T2.5 steps 1-3).
    internal static void TransformMatrix(in ShapeModel.Transform tr, ref TMat3 local)
    {
        Span<float> q = stackalloc float[4];
        for (int k = 0; k < 4; ++k)
            q[k] = F32Chop(Mul(kQuatScale, tr.Q[k]));
        local.Flags = 0;
        QuatToMatrix(q, ref local);
        local.P = tr.T;
        local.Flags = (local.Flags & ~4u) | 2u;
    }

    // src/world/shape.cpp animateObjects: TSObjectInstance_animate (0x005be56c, T2.6) for the objects of one animated
    // node.
    internal static void AnimateObjects(ShapeInstance inst, List<int> order, int node)
    {
        ShapeModel sh = inst.ShapeData!;
        for (int oi = 0; oi < sh.Objects.Length; ++oi)
        {
            ref ShapeModel.Object ob = ref sh.Objects[oi];
            if (ob.Node != node)
                continue;
            ref ShapeInstance.ObjectState st = ref inst.Objects[oi];
            st.Frame = 0;
            st.Visible = (ob.Flags & 1) == 0;
            ObjectThreadState(inst, order, oi, ref st);
        }
    }

    // src/world/shape.cpp sphereTriangle: TSCelAnimMesh_collideSphere (0x005ac684, T4.1 steps 1-7): `pm` is the centre
    // in mesh space. On a hit `position` is the contact point, `normal` the unit face normal and `distance` the plane
    // distance of the centre (0 <= distance < r).
    public static bool SphereTriangle(ReadOnlySpan<float> pm, float r, ReadOnlySpan<float> a, ReadOnlySpan<float> b,
                                      ReadOnlySpan<float> c, Span<float> position, Span<float> normal, ref float distance)
    {
        // Steps 1-2: unit normal of cross(B - C, A - C), both differences stored first.
        Span<float> bc = stackalloc float[3], ac = stackalloc float[3], n = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
            bc[i] = F32Chop(Sub(b[i], c[i]));
        for (int i = 0; i < 3; ++i)
            ac[i] = F32Chop(Sub(a[i], c[i]));
        Cross(bc, ac, n);
        float l = F32Chop(X87.Len(n));
        if (l == 0.0f)
            return false;
        float k = F32Chop(Div(1.0, l));
        for (int i = 0; i < 3; ++i)
            n[i] = F32Chop(Mul(k, n[i]));
        // Steps 3-5: plane distance of the centre, its projection and the squared reach left.
        float dist = F32Chop(Sub(F32Chop(X87.Dot(n, pm)), F32Chop(X87.Dot(n, a))));
        if (!(dist >= 0.0f && dist < r))
            return false;
        Span<float> q = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
            q[i] = F32Chop(Sub(pm[i], Mul(dist, n[i])));
        float rem = F32Chop(Sub(Mul(r, r), Mul(dist, dist)));
        bool emit = false;
        if (PointInTriangle(q, n, a, b, c))
        {
            q.CopyTo(position);
            emit = true;
        }
        else
        {
            // Step 7: the first edge whose nearest point is within reach.
            Span<float> e = stackalloc float[3], w = stackalloc float[3], x = stackalloc float[3];
            for (int i = 0; i < 3 && !emit; ++i)
            {
                ReadOnlySpan<float> v0 = i == 0 ? a : i == 1 ? b : c;
                ReadOnlySpan<float> v1 = i == 0 ? b : i == 1 ? c : a;
                for (int j = 0; j < 3; ++j)
                {
                    e[j] = F32Chop(Sub(v1[j], v0[j]));
                    w[j] = F32Chop(Sub(q[j], v0[j]));
                }
                float el = F32Chop(X87.Len(e));
                if (el == 0.0f)
                    continue;
                float inv = F32Chop(Div(1.0, el));
                for (int j = 0; j < 3; ++j)
                    e[j] = F32Chop(Mul(inv, e[j]));
                float t = F32Chop(X87.Dot(w, e));
                for (int j = 0; j < 3; ++j)
                    x[j] = F32Chop(Add(Mul(t, e[j]), v0[j]));
                float d2 = F32Chop(Add(Add(Mul(Sub(q[0], x[0]), Sub(q[0], x[0])), Mul(Sub(q[1], x[1]), Sub(q[1], x[1]))),
                                       Mul(Sub(q[2], x[2]), Sub(q[2], x[2]))));
                if (d2 > rem)
                    continue;
                float slack = F32Chop(Sub(rem, d2));
                if (t < 0.0f)
                {
                    if (Mul(t, t) > slack)
                        continue;
                }
                else if (t > el)
                {
                    float u = F32Chop(Sub(t, el));
                    if (Mul(u, u) > slack)
                        continue;
                }
                if (t < 0.0f)
                    t = 0.0f;
                if (t > el)
                    t = el;
                for (int j = 0; j < 3; ++j)
                    position[j] = F32Chop(Add(Mul(t, e[j]), v0[j]));
                emit = true;
            }
        }
        if (!emit)
            return false;
        n.CopyTo(normal);
        distance = dist;
        return true;
    }

    private struct Edge
    {
        public int Ta, Ma, Tb, Mb;          // p = |t[ta] * m[ma] - t[tb] * m[mb]|
        public int A0, C0r, C0c, A1, C1r, C1c; // a[a0] * C[c0] + a[a1] * C[c1]
        public int B0, D0r, D0c, B1, D1r, D1c; // + b[b0] * C[d0] + b[b1] * C[d1]

        public Edge(params int[] v)
        {
            Ta = v[0]; Ma = v[1]; Tb = v[2]; Mb = v[3];
            A0 = v[4]; C0r = v[5]; C0c = v[6]; A1 = v[7]; C1r = v[8]; C1c = v[9];
            B0 = v[10]; D0r = v[11]; D0c = v[12]; B1 = v[13]; D1r = v[14]; D1c = v[15];
        }
    }

    private static readonly Edge[] sEdges =
    {
        new(2, 1, 1, 2, 1, 0, 2, 2, 0, 1, 1, 2, 0, 2, 1, 0), new(2, 4, 1, 5, 1, 1, 2, 2, 1, 1, 0, 2, 0, 2, 0, 0),
        new(2, 7, 1, 8, 1, 2, 2, 2, 2, 1, 0, 1, 0, 1, 0, 0), new(0, 2, 2, 0, 0, 0, 2, 2, 0, 0, 1, 2, 1, 2, 1, 1),
        new(0, 5, 2, 3, 0, 1, 2, 2, 1, 0, 0, 2, 1, 2, 0, 1), new(0, 8, 2, 6, 0, 2, 2, 2, 2, 0, 0, 1, 1, 1, 0, 1),
        new(1, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 2, 2, 2, 1, 2), new(1, 3, 0, 4, 0, 1, 1, 1, 1, 0, 0, 2, 2, 2, 0, 2),
        new(1, 6, 0, 7, 0, 2, 1, 1, 2, 0, 0, 1, 2, 1, 0, 2),
    };

    // src/world/shape.cpp orientedBoxesOverlap: m_Box3F_collideOrientedBox (0x005ec754, T6): separating axis test of a
    // box with half extents `a` on the axes of the frame against a box with half extents `b` whose axes are the rows of
    // `m` and whose centre is `t`. Touching boxes overlap.
    public static bool OrientedBoxesOverlap(ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<float> m, ReadOnlySpan<float> t)
    {
        using var chop = ChopScope();
        Span<float> C = stackalloc float[9];
        for (int i = 0; i < 9; ++i)
            C[i] = (float)Math.Abs((double)m[i]);
        static double Mag(double v) => Math.Abs(v); // rounded to a double first
        // 1 to 3: the frame axes.
        for (int j = 0; j < 3; ++j)
        {
            float slack = F32Chop(Sub(Add(Add(Add(Mul(b[0], C[j]), a[j]), Mul(b[1], C[3 + j])), Mul(b[2], C[6 + j])),
                                      Math.Abs((double)t[j])));
            if (slack < 0.0f)
                return false;
        }
        // 4 to 6: the axes of the other box.
        for (int i = 0; i < 3; ++i)
        {
            double p = Mag(Add(Add(Mul(t[0], m[3 * i]), Mul(t[1], m[3 * i + 1])), Mul(t[2], m[3 * i + 2])));
            float slack = F32Chop(Sub(Add(Add(Add(Mul(a[0], C[3 * i]), Mul(a[1], C[3 * i + 1])), Mul(a[2], C[3 * i + 2])), b[i]), p));
            if (slack < 0.0f)
                return false;
        }
        // 7 to 15: the edge pairs, frame axis i against box axis r (extended sums, pass on >=).
        foreach (Edge e in sEdges)
        {
            double p = Mag(Sub(Mul(t[e.Ta], m[e.Ma]), Mul(t[e.Tb], m[e.Mb])));
            double reach = Add(Add(Add(Mul(a[e.A0], C[3 * e.C0r + e.C0c]), Mul(a[e.A1], C[3 * e.C1r + e.C1c])),
                                   Mul(b[e.B0], C[3 * e.D0r + e.D0c])),
                               Mul(b[e.B1], C[3 * e.D1r + e.D1c]));
            if (!(reach >= p))
                return false;
        }
        return true;
    }

    // src/world/shape.cpp shapeLine: ShapeCollisionImage against a line (T2.3, T3 case 3): `object` is the image
    // transform (object to world). On a hit `point` and `normal` are in world space.
    public static bool ShapeLine(ShapeInstance inst, in TMat3 obj, ReadOnlySpan<float> s, ReadOnlySpan<float> e, bool faces,
                                 Span<float> point, Span<float> normal)
    {
        var sf = new ShapeSurface();
        return ShapeLine(inst, obj, s, e, faces, point, normal, ref sf);
    }

    public static bool ShapeLine(ShapeInstance inst, in TMat3 obj, ReadOnlySpan<float> s, ReadOnlySpan<float> e, bool faces,
                                 Span<float> point, Span<float> normal, ref ShapeSurface surface)
    {
        using var chop = ChopScope();
        // T2.3: list -> world = X x image transform, rel = line image (identity) x its inverse.
        var identity = new TMat3();
        TmatMul(inst.Root, obj, out TMat3 toWorld);
        TmatInverse(toWorld, out TMat3 inv);
        TmatMul(identity, inv, out TMat3 rel);
        var sf = new ShapeSurface();
        if (!inst.CastLine(s, e, faces, rel, ref sf))
            return false;
        X87.MulPoint(sf.Position, toWorld.M, toWorld.P, point);
        X87.MulVector(sf.Normal, toWorld.M, normal);
        surface = sf;
        return true;
    }
}
