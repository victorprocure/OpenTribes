using System.Buffers.Binary;
using System.Text;

namespace OpenTribes.Client.Shapes;

/// <summary>A node of the shape tree.</summary>
public struct RsNode
{
    /// <summary>The index of the name in the name table.</summary>
    public int Name;
    /// <summary>The parent node, -1 for none.</summary>
    public int Parent;
    /// <summary>The number of sub sequences of the node.</summary>
    public int NumSub;
    /// <summary>The index of the first sub sequence.</summary>
    public int FirstSub;
    /// <summary>The index of the default transform.</summary>
    public int DefaultTransform;
}

/// <summary>A sequence of the shape.</summary>
public struct RsSequence
{
    /// <summary>The index of the name in the name table.</summary>
    public int Name;
    /// <summary>True when the sequence loops.</summary>
    public bool Cyclic;
    /// <summary>The duration in seconds.</summary>
    public float Duration;
    /// <summary>The priority.</summary>
    public int Priority;
    /// <summary>The index of the first trigger.</summary>
    public int FirstTrigger;
    /// <summary>The number of triggers.</summary>
    public int NumTriggers;
    /// <summary>The number of image lists.</summary>
    public int NumIfl;
    /// <summary>The index of the first image list.</summary>
    public int FirstIfl;
}

/// <summary>The part of a sequence that animates one node or object.</summary>
public struct RsSubSequence
{
    /// <summary>The sequence index.</summary>
    public int Sequence;
    /// <summary>The number of keys.</summary>
    public int NumKeys;
    /// <summary>The index of the first key.</summary>
    public int FirstKey;
}

/// <summary>A key of a sub sequence.</summary>
public struct RsKey
{
    /// <summary>The position in the sequence, 0 to 1.</summary>
    public float Position;
    /// <summary>The index of the transform, frame or material the key selects.</summary>
    public int Index;
    /// <summary>The flags: 0x8000 visible, 0x4000 visibility valid, 0x2000 material valid, 0x1000 frame valid.</summary>
    public int Flags;
}

/// <summary>A node transform: a rotation quaternion and a translation.</summary>
public struct RsTransform
{
    /// <summary>The quaternion x.</summary>
    public double Qx;
    /// <summary>The quaternion y.</summary>
    public double Qy;
    /// <summary>The quaternion z.</summary>
    public double Qz;
    /// <summary>The quaternion w.</summary>
    public double Qw;
    /// <summary>The translation x.</summary>
    public double Tx;
    /// <summary>The translation y.</summary>
    public double Ty;
    /// <summary>The translation z.</summary>
    public double Tz;
}

/// <summary>An object of the shape: a mesh on a node.</summary>
public struct RsObject
{
    /// <summary>The index of the name in the name table.</summary>
    public int Name;
    /// <summary>The flags.</summary>
    public int Flags;
    /// <summary>The mesh index.</summary>
    public int Mesh;
    /// <summary>The node the object hangs on.</summary>
    public int Node;
    /// <summary>The number of sub sequences of the object.</summary>
    public int NumSub;
    /// <summary>The index of the first sub sequence.</summary>
    public int FirstSub;
    /// <summary>The offset x from the node.</summary>
    public double Ox;
    /// <summary>The offset y from the node.</summary>
    public double Oy;
    /// <summary>The offset z from the node.</summary>
    public double Oz;
}

/// <summary>A detail level of the shape.</summary>
public struct RsDetail
{
    /// <summary>The root node of the detail.</summary>
    public int RootNode;
    /// <summary>The size the detail applies from.</summary>
    public float Size;
}

/// <summary>A trigger of a sequence.</summary>
public struct RsTrigger
{
    /// <summary>The position in the sequence, 0 to 1.</summary>
    public float Position;
    /// <summary>The trigger value.</summary>
    public uint Value;
}

/// <summary>A face of a mesh.</summary>
public struct RsFace
{
    /// <summary>The first vertex index.</summary>
    public int V0;
    /// <summary>The first texture vertex index.</summary>
    public int T0;
    /// <summary>The second vertex index.</summary>
    public int V1;
    /// <summary>The second texture vertex index.</summary>
    public int T1;
    /// <summary>The third vertex index.</summary>
    public int V2;
    /// <summary>The third texture vertex index.</summary>
    public int T2;
    /// <summary>The material index.</summary>
    public int Material;
}

/// <summary>A frame of a mesh: where its vertices start and how its packed bytes decode.</summary>
public struct RsFrame
{
    /// <summary>The index of the first vertex of the frame.</summary>
    public int FirstVert;
    /// <summary>The scale x of the packed vertices.</summary>
    public float Sx;
    /// <summary>The scale y of the packed vertices.</summary>
    public float Sy;
    /// <summary>The scale z of the packed vertices.</summary>
    public float Sz;
    /// <summary>The origin x of the packed vertices.</summary>
    public float Ox;
    /// <summary>The origin y of the packed vertices.</summary>
    public float Oy;
    /// <summary>The origin z of the packed vertices.</summary>
    public float Oz;
}

/// <summary>A mesh of the shape.</summary>
public sealed class RsMesh
{
    /// <summary>The packed vertices, 4 bytes each: x, y, z, normal index.</summary>
    public byte[] Verts = Array.Empty<byte>();
    /// <summary>The texture vertices as u, v pairs.</summary>
    public float[] TexVerts = Array.Empty<float>();
    /// <summary>The faces.</summary>
    public RsFace[] Faces = Array.Empty<RsFace>();
    /// <summary>The frames.</summary>
    public RsFrame[] Frames = Array.Empty<RsFrame>();
    /// <summary>The texture vertices per frame.</summary>
    public int TexVertsPerFrame;
    /// <summary>The vertices per frame.</summary>
    public int VertsPerFrame;
    /// <summary>The mesh version.</summary>
    public int Version;
}

/// <summary>One entry of the material list (shapes-render.md 1.3).</summary>
/// <remarks>File offsets: +0 flags, +4 alpha, +8 palette index, +0xc RGB, +0x10 name.</remarks>
public struct RsMaterial
{
    /// <summary>The flags.</summary>
    public uint Flags;
    /// <summary>The palette index.</summary>
    public int PaletteIndex;
    /// <summary>The red.</summary>
    public byte R;
    /// <summary>The green.</summary>
    public byte G;
    /// <summary>The blue.</summary>
    public byte B;
    /// <summary>The bitmap name.</summary>
    public string Name;
    /// <summary>The material type, the low four flag bits. Type 3 names a bitmap.</summary>
    public int Type => (int)(Flags & 0xf);
    /// <summary>The shading mode, flag bits 8 to 11.</summary>
    public int Shading => (int)((Flags >> 8) & 0xf);
    /// <summary>True for a keyed (transparent colour) material.</summary>
    public bool Keyed => (Flags & 0x1000) != 0;
    /// <summary>True for a translucent material.</summary>
    public bool Translucent => (Flags & 0x2000) != 0;
}

/// <summary>The render side <c>.dts</c> reader: everything the collision loader of OpenTribes.World skips (texture vertices, face texture indices, the material list, the sequence tail with triggers and image lists, the per instance remap size) next to the node, object, detail and key tables.</summary>
/// <remarks>docs/shapes-render.md 1.1 and 1.2, layouts in docs/interiors.md 8.2 and 8.3. It is independent of <c>ShapeModel</c>, so the server path stays untouched.</remarks>
public sealed class RenderShape
{
    /// <summary>The shape file name.</summary>
    public string Name = "";
    /// <summary>The file version.</summary>
    public uint Version;
    /// <summary>The radius of the bounding sphere.</summary>
    public float Radius;
    /// <summary>The centre of the bounding sphere.</summary>
    public double Cx;
    /// <summary>The centre y.</summary>
    public double Cy;
    /// <summary>The centre z.</summary>
    public double Cz;
    /// <summary>The shape box, as the <c>ShapeBox</c> of the server.</summary>
    public float[] Box = new float[6];
    /// <summary>The always node, -1 for none.</summary>
    public int AlwaysNode = -1;
    /// <summary>The size of the image list remap of an instance.</summary>
    public int RemapSize;
    /// <summary>The nodes.</summary>
    public RsNode[] Nodes = Array.Empty<RsNode>();
    /// <summary>The sequences.</summary>
    public RsSequence[] Sequences = Array.Empty<RsSequence>();
    /// <summary>The sub sequences.</summary>
    public RsSubSequence[] SubSequences = Array.Empty<RsSubSequence>();
    /// <summary>The keys.</summary>
    public RsKey[] Keys = Array.Empty<RsKey>();
    /// <summary>The node transforms.</summary>
    public RsTransform[] Transforms = Array.Empty<RsTransform>();
    /// <summary>The name table.</summary>
    public string[] Names = Array.Empty<string>();
    /// <summary>The objects.</summary>
    public RsObject[] Objects = Array.Empty<RsObject>();
    /// <summary>The details.</summary>
    public RsDetail[] Details = Array.Empty<RsDetail>();
    /// <summary>The triggers.</summary>
    public RsTrigger[] Triggers = Array.Empty<RsTrigger>();
    /// <summary>The meshes.</summary>
    public RsMesh[] Meshes = Array.Empty<RsMesh>();
    /// <summary>The material list.</summary>
    public RsMaterial[] Materials = Array.Empty<RsMaterial>();
    /// <summary>The number of material details. The list holds <c>MaterialDetails * MaterialCount</c> entries.</summary>
    public int MaterialDetails = 1;
    /// <summary>The number of materials per detail.</summary>
    public int MaterialCount;
    /// <summary>The children of each node.</summary>
    public List<int>[] Children = Array.Empty<List<int>>();

    /// <summary>Finds a name in the name table, ignoring case.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The index, or -1 when it is not there.</returns>
    public int FindName(string name)
    {
        for (int i = 0; i < Names.Length; ++i)
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Finds a node by name.</summary>
    /// <param name="name">The node name.</param>
    /// <returns>The node index, or -1 when it is not there.</returns>
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

    /// <summary>Finds a node in the subtree of a detail whose name is a base name plus the digits of the detail suffix (<c>dummy hand</c> finds <c>dummy hand36</c> in detail 0 of the armors).</summary>
    /// <param name="baseName">The base name.</param>
    /// <param name="detail">The detail index.</param>
    /// <returns>The node index, or -1 when none.</returns>
    public int FindNodeBase(string baseName, int detail)
    {
        if (mNodeBase.TryGetValue((baseName, detail), out int cached))
            return cached;
        return mNodeBase[(baseName, detail)] = FindNodeBaseUncached(baseName, detail);
    }

    /// <summary>Gets the instance root transform (shapes-render.md 3.6): the default transform of node 0, which an instance keeps from its creation and puts in front of every node when it draws.</summary>
    /// <returns>The transform.</returns>
    /// <remarks>Node 0 itself is never animated and stays at identity in the node table, so the node positions (mounts, muzzle) do not contain it. Only the Player resets it to identity.</remarks>
    public Xf RootTransform()
    {
        if (Nodes.Length == 0 || Nodes[0].DefaultTransform < 0 || Nodes[0].DefaultTransform >= Transforms.Length)
            return Xf.Identity;
        RsTransform t = Transforms[Nodes[0].DefaultTransform];
        Xf r = Xf.FromQuat(t.Qx, t.Qy, t.Qz, t.Qw);
        (r.Px, r.Py, r.Pz) = (t.Tx, t.Ty, t.Tz);
        return r;
    }

    private readonly Dictionary<(string, int), int> mNodeBase = new();
    private float[]? mDetailSizes;
    private readonly Dictionary<int, DetailNodeSet> mDetailNodes = new();

    /// <summary>The nodes drawn and animated for one detail: the subtree of the always node and the subtree of the detail root, as a sorted list (the order of the node walk) and as a flag per node. Built once per detail.</summary>
    public sealed class DetailNodeSet
    {
        /// <summary>The node indices in walk order.</summary>
        public int[] Sorted = Array.Empty<int>();
        /// <summary>The flag of each node.</summary>
        public bool[] Mask = Array.Empty<bool>();
    }

    /// <summary>Gets the nodes of a detail.</summary>
    /// <param name="detail">The detail index.</param>
    /// <returns>The node set.</returns>
    public DetailNodeSet NodesOfDetail(int detail)
    {
        if (mDetailNodes.TryGetValue(detail, out DetailNodeSet? set))
            return set;
        var nodes = new List<int>();
        if (AlwaysNode >= 0 && AlwaysNode < Nodes.Length)
            nodes.AddRange(Subtree(AlwaysNode));
        nodes.AddRange(Subtree(DetailRoot(detail)));
        nodes.Sort();
        set = new DetailNodeSet { Sorted = nodes.ToArray(), Mask = new bool[Nodes.Length] };
        foreach (int n in nodes)
            set.Mask[n] = true;
        mDetailNodes[detail] = set;
        return set;
    }

    private int FindNodeBaseUncached(string baseName, int detail)
    {
        foreach (int n in Subtree(DetailRoot(detail)))
        {
            int ni = Nodes[n].Name;
            if (ni < 0 || ni >= Names.Length)
                continue;
            string nm = Names[ni];
            if (nm.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) && nm.AsSpan(baseName.Length).ToString().All(char.IsDigit))
                return n;
        }
        return FindNode(baseName);
    }

    /// <summary>Finds a sequence by name.</summary>
    /// <param name="name">The sequence name.</param>
    /// <returns>The sequence index, or -1 when it is not there.</returns>
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

    /// <summary>Gets the name of a sequence.</summary>
    /// <param name="s">The sequence index.</param>
    /// <returns>The name, or empty for an invalid index.</returns>
    public string SequenceName(int s) => s >= 0 && s < Sequences.Length && Sequences[s].Name >= 0 && Sequences[s].Name < Names.Length ? Names[Sequences[s].Name] : "";

    /// <summary>Gets the root node of a detail.</summary>
    /// <param name="detail">The detail index.</param>
    /// <returns>The node index.</returns>
    public int DetailRoot(int detail)
    {
        int node = 1;
        if (Details.Length != 0)
        {
            if (detail < 0 || detail >= Details.Length)
                return -1;
            node = Details[detail].RootNode;
        }
        return node >= 0 && node < Nodes.Length ? node : -1;
    }

    /// <summary>Lists the nodes of the subtree under a root, root first, parents before children.</summary>
    /// <param name="root">The root node.</param>
    /// <returns>The node indices.</returns>
    public List<int> Subtree(int root)
    {
        var l = new List<int>();
        if (root < 0 || root >= Nodes.Length)
            return l;
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            l.Add(n);
            for (int i = Children[n].Count - 1; i >= 0; --i)
                stack.Push(Children[n][i]);
        }
        return l;
    }

    /// <summary>Chooses the detail for a size.</summary>
    /// <param name="sizes">The size of each detail.</param>
    /// <param name="size">The size to choose for.</param>
    /// <param name="minDetail">The lowest detail allowed.</param>
    /// <param name="maxDetail">The highest detail allowed.</param>
    /// <returns>The detail index, or -1 when nothing draws.</returns>
    /// <remarks>shapes-render.md 2.3 steps 2 and 3 (005b9704): with fewer than 2 details the result is 0, else the first detail whose size is at most |size|. A stored size of 0 or no match gives -1 (not drawn). The result is clamped to [minDetail, nDetails - 1].</remarks>
    public static int ChooseDetail(IReadOnlyList<float> sizes, double size, int minDetail = -1, int maxDetail = 1000)
    {
        int n = sizes.Count;
        int d;
        if (n < 2)
            d = 0;
        else
        {
            d = -1;
            double a = Math.Abs(size);
            for (int i = 0; i < n; ++i)
                if (sizes[i] <= a)
                {
                    d = sizes[i] == 0 ? -1 : i;
                    break;
                }
        }
        if (d > maxDetail) d = maxDetail;
        if (d < minDetail) d = minDetail;
        if (d > n - 1) d = n - 1;
        return d;
    }

    /// <summary>Chooses the detail of this shape for a size.</summary>
    /// <param name="size">The size.</param>
    /// <returns>The detail index, or -1 when nothing draws.</returns>
    public int ChooseDetail(double size) => ChooseDetail(mDetailSizes ??= Details.Select(x => x.Size).ToArray(), size);

    private static long[] RecordSizes(uint v)
    {
        long nodes = 20, names = 24, objects = 72, details = 8, triggers = 8;
        long seq = v >= 5 ? 32 : v == 4 ? 24 : 16;
        long key = v >= 3 ? 12 : 8;
        long trans = v == 7 ? 32 : 40;
        long sub = 12;
        long transitions = v == 7 ? 52 : 60;
        return new[] { nodes, seq, sub, key, trans, names, objects, details, transitions, triggers };
    }

    private sealed class Cur
    {
        public readonly byte[] D;
        public int Pos;
        public bool Ok = true;
        public Cur(byte[] d) => D = d;
        public int I32() { if (Pos + 4 > D.Length) { Ok = false; return 0; } int v = BinaryPrimitives.ReadInt32LittleEndian(D.AsSpan(Pos)); Pos += 4; return v; }
        public float F32() { if (Pos + 4 > D.Length) { Ok = false; return 0; } float v = BinaryPrimitives.ReadSingleLittleEndian(D.AsSpan(Pos)); Pos += 4; return v; }
        public void Skip(long n) { if (Pos + n > D.Length || n < 0) Ok = false; Pos += (int)n; }
        public uint Pers(out int end)
        {
            end = 0;
            if (Pos + 12 > D.Length || D[Pos] != 'P' || D[Pos + 1] != 'E' || D[Pos + 2] != 'R' || D[Pos + 3] != 'S') { Ok = false; return 0; }
            int start = Pos;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(D.AsSpan(Pos + 4));
            end = start + 8 + (int)(size & 0x7fffffffu);
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(D.AsSpan(Pos + 8));
            Pos += 10 + ((nameLen + 1) & ~1);
            return (uint)I32();
        }
    }

    private static short S16(byte[] d, long p) => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan((int)p));
    private static int S32(byte[] d, long p) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan((int)p));
    private static uint U32(byte[] d, long p) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)p));
    private static float F(byte[] d, long p) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan((int)p));
    private static string CStr(byte[] d, long p, int max)
    {
        int n = 0;
        while (n < max && d[p + n] != 0)
            ++n;
        return Encoding.Latin1.GetString(d, (int)p, n);
    }

    /// <summary>Reads a PERS TS::Shape block.</summary>
    /// <param name="dts">The file bytes.</param>
    /// <param name="name">The shape name.</param>
    /// <returns>The shape, or null when the file is malformed.</returns>
    public static RenderShape? Parse(byte[] dts, string name = "")
    {
        var r = new Cur(dts);
        uint ver = r.Pers(out _);
        if (!r.Ok || ver < 1 || ver > 8)
            return null;
        var s = new RenderShape { Name = name, Version = ver };
        var counts = new int[9];
        for (int i = 0; i < 9; ++i)
            counts[i] = r.I32();
        int nTransitions = ver >= 2 ? r.I32() : 0;
        int nTriggers = ver >= 4 ? r.I32() : 0;
        s.Radius = r.F32();
        s.Cx = r.F32(); s.Cy = r.F32(); s.Cz = r.F32();
        if (!OpenTribes.World.Shape.ReadShapeBox(dts, s.Box))
            for (int i = 0; i < 3; ++i)
            {
                s.Box[i] = (float)((i == 0 ? s.Cx : i == 1 ? s.Cy : s.Cz) - s.Radius);
                s.Box[i + 3] = (float)((i == 0 ? s.Cx : i == 1 ? s.Cy : s.Cz) + s.Radius);
            }
        if (ver >= 8)
            r.Skip(24);
        long[] sizes = ver == 8 ? new long[] { 10, 32, 6, 8, 20, 24, 28, 8, 40, 8 } : RecordSizes(ver);
        int[] n = { counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6], counts[7], nTransitions, nTriggers };
        var at = new long[10];
        for (int i = 0; i < 10 && r.Ok; ++i)
        {
            if (n[i] < 0 || n[i] > dts.Length / 6)
                return null;
            at[i] = r.Pos;
            r.Skip(n[i] * sizes[i]);
        }
        if (ver >= 5)
            s.RemapSize = r.I32();
        if (ver >= 6)
            s.AlwaysNode = r.I32();
        if (!r.Ok || counts[8] < 0 || counts[8] > dts.Length / 16)
            return null;
        bool v8 = ver == 8;
        int step = v8 ? 2 : 4;
        s.Nodes = new RsNode[n[0]];
        for (int i = 0; i < n[0]; ++i)
        {
            long o = at[0] + i * sizes[0];
            s.Nodes[i] = new RsNode
            {
                Name = S16(dts, o), Parent = S16(dts, o + step), NumSub = S16(dts, o + 2 * step), FirstSub = S16(dts, o + 3 * step),
                DefaultTransform = S16(dts, o + 4 * step),
            };
        }
        s.Sequences = new RsSequence[n[1]];
        for (int i = 0; i < n[1]; ++i)
        {
            long o = at[1] + i * sizes[1];
            var q = new RsSequence { Name = S32(dts, o), Cyclic = S32(dts, o + 4) != 0, Duration = F(dts, o + 8), Priority = S32(dts, o + 12) };
            if (ver >= 5)
            {
                q.FirstTrigger = S32(dts, o + 16);
                q.NumTriggers = S32(dts, o + 20);
                q.NumIfl = S32(dts, o + 24);
                q.FirstIfl = S32(dts, o + 28);
            }
            s.Sequences[i] = q;
        }
        s.SubSequences = new RsSubSequence[n[2]];
        for (int i = 0; i < n[2]; ++i)
        {
            long o = at[2] + i * sizes[2];
            s.SubSequences[i] = new RsSubSequence { Sequence = S16(dts, o), NumKeys = S16(dts, o + step), FirstKey = S16(dts, o + 2 * step) };
        }
        s.Keys = new RsKey[n[3]];
        for (int i = 0; i < n[3]; ++i)
        {
            long o = at[3] + i * sizes[3];
            uint key = U32(dts, o + 4);
            int flags;
            if (v8)
                flags = (int)(key >> 16);
            else if (ver >= 3)
            {
                uint m = U32(dts, o + 8);
                flags = (int)((m & 0xffffu) | ((m >> 16) & 0xf000u));
            }
            else
                flags = (int)(0x1000u | ((key >> 16) & 0xc000u));
            s.Keys[i] = new RsKey { Position = F(dts, o), Index = (ushort)key, Flags = flags };
        }
        s.Transforms = new RsTransform[n[4]];
        for (int i = 0; i < n[4]; ++i)
        {
            long o = at[4] + i * sizes[4];
            var t = new RsTransform();
            if (ver >= 7)
            {
                t.Qx = S16(dts, o) / 32767.0; t.Qy = S16(dts, o + 2) / 32767.0; t.Qz = S16(dts, o + 4) / 32767.0; t.Qw = S16(dts, o + 6) / 32767.0;
                t.Tx = F(dts, o + 8); t.Ty = F(dts, o + 12); t.Tz = F(dts, o + 16);
            }
            else
            {
                t.Qx = F(dts, o); t.Qy = F(dts, o + 4); t.Qz = F(dts, o + 8); t.Qw = F(dts, o + 12);
                t.Tx = F(dts, o + 16); t.Ty = F(dts, o + 20); t.Tz = F(dts, o + 24);
            }
            s.Transforms[i] = t;
        }
        s.Names = new string[n[5]];
        for (int i = 0; i < n[5]; ++i)
            s.Names[i] = CStr(dts, at[5] + i * 24, 24);
        s.Objects = new RsObject[n[6]];
        for (int i = 0; i < n[6]; ++i)
        {
            long o = at[6] + i * sizes[6];
            long off = v8 ? o + 12 : o + 52;
            s.Objects[i] = new RsObject
            {
                Name = S16(dts, o), Flags = (ushort)S16(dts, o + 2), Mesh = S32(dts, o + 4), Node = S16(dts, o + 8),
                Ox = F(dts, off), Oy = F(dts, off + 4), Oz = F(dts, off + 8),
                NumSub = S16(dts, v8 ? o + 24 : o + 64), FirstSub = S16(dts, v8 ? o + 26 : o + 68),
            };
        }
        s.Details = new RsDetail[n[7]];
        for (int i = 0; i < n[7]; ++i)
            s.Details[i] = new RsDetail { RootNode = S32(dts, at[7] + i * 8), Size = F(dts, at[7] + i * 8 + 4) };
        s.Triggers = new RsTrigger[n[9]];
        for (int i = 0; i < n[9]; ++i)
            s.Triggers[i] = new RsTrigger { Position = F(dts, at[9] + i * sizes[9]), Value = U32(dts, at[9] + i * sizes[9] + 4) };

        s.Meshes = new RsMesh[counts[8]];
        for (int mi = 0; mi < s.Meshes.Length; ++mi)
        {
            var mesh = s.Meshes[mi] = new RsMesh();
            uint mver = r.Pers(out int meshEnd);
            if (!r.Ok || meshEnd <= r.Pos || meshEnd > dts.Length)
                return null;
            var m = new Cur(dts) { Pos = r.Pos };
            r.Pos = meshEnd;
            mesh.Version = (int)mver;
            if (mver != 3)
                continue;
            int nVerts = m.I32();
            mesh.VertsPerFrame = m.I32();
            int nTex = m.I32(), nFaces = m.I32(), nFrames = m.I32();
            mesh.TexVertsPerFrame = m.I32();
            m.I32();
            long left = m.Ok && m.Pos <= meshEnd ? meshEnd - m.Pos : 0;
            if (!m.Ok || nVerts < 0 || nTex < 0 || nFaces < 0 || nFrames < 0 || (long)nVerts * 4 + (long)nTex * 8 + (long)nFaces * 28 + (long)nFrames * 28 > left)
                return null;
            mesh.Verts = dts.AsSpan(m.Pos, nVerts * 4).ToArray();
            m.Skip(nVerts * 4);
            mesh.TexVerts = new float[nTex * 2];
            for (int i = 0; i < nTex * 2; ++i)
                mesh.TexVerts[i] = m.F32();
            mesh.Faces = new RsFace[nFaces];
            for (int i = 0; i < nFaces; ++i)
                mesh.Faces[i] = new RsFace { V0 = m.I32(), T0 = m.I32(), V1 = m.I32(), T1 = m.I32(), V2 = m.I32(), T2 = m.I32(), Material = m.I32() };
            mesh.Frames = new RsFrame[nFrames];
            for (int i = 0; i < nFrames; ++i)
                mesh.Frames[i] = new RsFrame { FirstVert = m.I32(), Sx = m.F32(), Sy = m.F32(), Sz = m.F32(), Ox = m.F32(), Oy = m.F32(), Oz = m.F32() };
            if (!m.Ok)
                return null;
        }
        // Material list (1.3): i32 hasMaterials, then a PERS TS::MaterialList.
        int has = r.I32();
        if (r.Ok && has != 0)
            s.ReadMaterials(dts, r.Pos);
        s.Children = new List<int>[s.Nodes.Length];
        for (int i = 0; i < s.Nodes.Length; ++i)
            s.Children[i] = new List<int>();
        for (int i = 0; i < s.Nodes.Length; ++i)
        {
            int p = s.Nodes[i].Parent;
            if (p >= 0 && p < s.Nodes.Length && p != i)
                s.Children[p].Add(i);
        }
        return s;
    }

    private void ReadMaterials(byte[] d, int pos)
    {
        var c = new Cur(d) { Pos = pos };
        uint ver = c.Pers(out _);
        if (!c.Ok || ver < 1 || ver > 4)
            return;
        int details = c.I32(), count = c.I32();
        long entry = ver <= 2 ? 0x30 : ver == 3 ? 0x3c : 0x40;
        if (details < 1 || details > 4096 || count < 0 || count > 65536 || c.Pos + (long)details * count * entry > d.Length)
            return;
        MaterialDetails = details;
        MaterialCount = count;
        Materials = new RsMaterial[details * count];
        for (int i = 0; i < Materials.Length; ++i)
        {
            long e = c.Pos + i * entry;
            Materials[i] = new RsMaterial
            {
                Flags = U32(d, e), PaletteIndex = S32(d, e + 8), R = d[e + 0xc], G = d[e + 0xd], B = d[e + 0xe],
                Name = (U32(d, e) & 0xf) == 3 ? CStr(d, e + 0x10, 0x20) : "",
            };
        }
    }
}
