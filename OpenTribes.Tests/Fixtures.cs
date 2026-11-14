// Port of tests/fixtures.hpp: shared helpers for the game-layer unit tests: synthetic files and a server world set up
// like the dedicated start-up (tools/scriptrun.cpp). One function to one; the float arithmetic of the builders follows
// the mode of Fpu like the C++ (the test programs run in the script FPU mode).
namespace OpenTribes.Tests;

using System.Buffers.Binary;
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Script;
using OpenTribes.Sim;
using static OpenTribes.Core.Fpu;

internal static class Fixtures
{
    // fixtures::putU8.
    public static void PutU8(List<byte> b, byte v) => b.Add(v);

    // fixtures::putU16.
    public static void PutU16(List<byte> b, ushort v)
    {
        b.Add((byte)v);
        b.Add((byte)(v >> 8));
    }

    // fixtures::putU32.
    public static void PutU32(List<byte> b, uint v)
    {
        for (var i = 0; i < 4; ++i)
            b.Add((byte)(v >> (i * 8)));
    }

    // fixtures::putF32.
    public static void PutF32(List<byte> b, float f) => PutU32(b, BitConverter.SingleToUInt32Bits(f));

    // fixtures::putStr.
    public static void PutStr(List<byte> b, string s) => b.AddRange(ByteString.ToBytes(s));

    // fixtures::setU32.
    public static void SetU32(List<byte> b, int at, uint v)
    {
        for (var i = 0; i < 4; ++i)
            b[at + i] = (byte)(v >> (i * 8));
    }

    // fixtures::pers: PERS block (docs/interiors.md 2): tag, size, name, version, body.
    public static List<byte> Pers(string cls, uint version, List<byte> body)
    {
        var b = new List<byte>();
        PutStr(b, "PERS");
        PutU32(b, 0);
        PutU16(b, (ushort)cls.Length);
        PutStr(b, cls);
        if (cls.Length % 2 != 0)
            b.Add(0);
        PutU32(b, version);
        b.AddRange(body);
        SetU32(b, 4, (uint)(b.Count - 8));
        return b;
    }

    // fixtures::putFile (bytes).
    public static void PutFile(string p, List<byte> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, [.. data]);
    }

    // fixtures::putFile (text).
    public static void PutFile(string p, string text) => PutFile(p, [.. ByteString.ToBytes(text)]);

    // fixtures::PackedEntry: compression 1 (RLE) entries are given as {name, packed bytes, uncompressed size}: the VBLK
    // size field is the uncompressed length (docs/resources.md 7).
    public sealed class PackedEntry
    {
        public string Name { get; set; } = "";
        public string Packed { get; set; } = "";
        public uint Size { get; set; }
        public byte Compression { get; set; } = 1;
    }

    // fixtures::putVol: minimal PVOL writer, same layout as tools/mkvol.py.
    public static void PutVol(string p, IReadOnlyList<(string name, string data)> entries, IReadOnlyList<PackedEntry>? packed = null)
    {
        var b = new List<byte>();
        PutStr(b, "PVOL");
        PutU32(b, 0);
        var offsets = new List<uint>();
        var sizes = new List<uint>();
        var kinds = new List<byte>();
        var nameList = new List<string>();
        foreach (var (name, data) in entries)
        {
            offsets.Add((uint)b.Count);
            PutStr(b, "VBLK");
            PutU32(b, (uint)data.Length | 0x80000000u);
            PutStr(b, data);
            while (b.Count % 4 != 0)
                b.Add(0);
            sizes.Add((uint)data.Length);
            kinds.Add(0);
            nameList.Add(name);
        }
        foreach (var e in packed ?? [])
        {
            offsets.Add((uint)b.Count);
            PutStr(b, "VBLK");
            PutU32(b, e.Size | 0x80000000u);
            PutStr(b, e.Packed);
            while (b.Count % 4 != 0)
                b.Add(0);
            sizes.Add(e.Size);
            kinds.Add(e.Compression);
            nameList.Add(e.Name);
        }
        SetU32(b, 4, (uint)b.Count);
        var names = "";
        var nameOffsets = new List<uint>();
        foreach (var n in nameList)
        {
            nameOffsets.Add((uint)names.Length);
            names += n + '\0';
        }
        PutStr(b, "vols");
        PutU32(b, (uint)names.Length);
        PutStr(b, names);
        if (names.Length % 2 != 0)
            b.Add(0);
        PutStr(b, "voli");
        PutU32(b, (uint)(17 * nameList.Count));
        for (var i = 0; i < nameList.Count; ++i)
        {
            PutU32(b, 0);
            PutU32(b, nameOffsets[i]);
            PutU32(b, offsets[i]);
            PutU32(b, sizes[i]);
            b.Add(kinds[i]);
        }
        PutFile(p, b);
    }

    // ---- terrain (docs/terrain.md 1.3, 1.4) ----

    // fixtures::TerrainSpec: one block of n x n squares (detail = log2(n) + 1), raw heights (version 0 block).
    public sealed class TerrainSpec
    {
        public int N { get; set; } = 4;
        public int Detail { get; set; } = 3;
        public int Scale { get; set; } = 3;
        public int Width { get; set; } = 2;
        public int Height { get; set; } = 1;
        public List<float> Heights { get; set; } = [];  // (n+1)^2, row major
        public List<byte> Squares { get; set; } = [];   // n^2 x {flags, material}
        public string MaterialList { get; set; } = "t.dml";
    }

    // fixtures::dtfFile.
    public static List<byte> DtfFile(TerrainSpec t, float hmin, float hmax)
    {
        var b = new List<byte>();
        PutStr(b, "GFIL");
        PutU32(b, 0);
        PutU32(b, 1); // version
        PutU32(b, (uint)t.MaterialList.Length);
        PutStr(b, t.MaterialList);
        PutU32(b, 1); // next block id
        PutU32(b, (uint)t.Detail);
        PutU32(b, (uint)t.Scale);
        b.AddRange(new byte[24 + 8]);
        PutF32(b, hmin);
        PutF32(b, hmax);
        PutU32(b, (uint)t.Width);
        PutU32(b, (uint)t.Height);
        PutU32(b, 0); // version 1 field
        for (var i = 0; i < t.Width * t.Height; ++i)
            PutU32(b, 0); // every map cell is block 0
        PutU32(b, 1);     // block list: (0, no name)
        PutU32(b, 0);
        PutU32(b, 0);
        SetU32(b, 4, (uint)(b.Count - 8));
        return b;
    }

    // fixtures::dtbFile.
    public static List<byte> DtbFile(TerrainSpec t, float hmin, float hmax)
    {
        var b = new List<byte>();
        PutStr(b, "GBLK");
        PutU32(b, 0);
        PutU32(b, 0); // version 0: raw arrays
        var name = new byte[16];
        ByteString.ToBytes("block-0").CopyTo(name, 0);
        b.AddRange(name);
        PutU32(b, (uint)t.Detail);
        PutU32(b, unchecked((uint)-1)); // no light map
        PutF32(b, hmin);
        PutF32(b, hmax);
        PutU32(b, (uint)t.N);
        PutU32(b, (uint)t.N);
        foreach (var h in t.Heights)
            PutF32(b, h);
        b.AddRange(t.Squares);
        SetU32(b, 4, (uint)(b.Count - 8));
        return b;
    }

    // ---- interiors (docs/interiors.md 3, 4) ----

    // fixtures::Node.
    public readonly record struct Node(ushort Plane, short Front, short Back);

    // fixtures::digFile: PERS ITRGeometry: box, BSP nodes, nSolid solid and nEmpty empty leaves, planes.
    public static List<byte> DigFile(uint buildId, ReadOnlySpan<float> box, IReadOnlyList<Node> nodes, uint nSolid, uint nEmpty,
                                     IReadOnlyList<float[]> planes)
    {
        var b = new List<byte>();
        PutU32(b, buildId);
        PutF32(b, 1.0f); // textureScale
        for (var i = 0; i < 6; ++i)
            PutF32(b, box[i]);
        uint[] counts = [0, (uint)nodes.Count, nSolid, nEmpty, 0, 0, 0, 0, (uint)planes.Count];
        foreach (var c in counts)
            PutU32(b, c);
        foreach (var n in nodes)
        {
            PutU16(b, n.Plane);
            PutU16(b, (ushort)n.Front);
            PutU16(b, (ushort)n.Back);
            PutU16(b, 0);
        }
        b.AddRange(new byte[(nSolid * 12) + (nEmpty * 44)]);
        foreach (var p in planes)
            foreach (var f in p)
                PutF32(b, f);
        PutU32(b, 0); // highestMipLevel
        PutU32(b, 0); // flags
        return Pers("ITRGeometry", 7, b);
    }

    // fixtures::disFile: ITRs shape with one state and one LOD.
    public static List<byte> DisFile(string geometry, string lighting)
    {
        var names = "";
        uint AddName(string s)
        {
            var off = (uint)names.Length;
            names += s + '\0';
            return off;
        }
        uint state = AddName("State 0"), geo = AddName(geometry), light = AddName(lighting), def = AddName("default"),
             dml = AddName("x.dml");
        var b = new List<byte>();
        PutStr(b, "ITRs");
        PutU32(b, 0);
        PutU32(b, 3); // version
        PutU32(b, 1);
        PutU32(b, state);
        PutU32(b, 0);
        PutU32(b, 1);
        PutU32(b, 1);
        PutU32(b, 0);
        PutU32(b, geo);
        PutU32(b, 0);
        PutU32(b, 0);
        PutU32(b, 1);
        PutU32(b, light);
        PutU32(b, 1);
        PutU32(b, def);
        PutU32(b, (uint)names.Length);
        PutStr(b, names);
        PutU32(b, dml);
        PutU8(b, 0);
        SetU32(b, 4, (uint)(b.Count - 8));
        return b;
    }

    // fixtures::LightRec.
    public readonly record struct LightRec(float Intensity, uint Flags);

    // fixtures::dilFile: PERS ITRLighting (read 0x0055c8c0) with lights, map data bytes and a name buffer.
    public static List<byte> DilFile(uint buildId, IReadOnlyList<LightRec> lights, List<byte> map, string names)
    {
        var b = new List<byte>();
        PutU32(b, buildId);
        PutU32(b, 0);
        PutU32(b, 0);
        PutU32(b, 0); // states
        PutU32(b, 0); // state data
        PutU32(b, (uint)lights.Count);
        PutU32(b, 0); // light states
        PutU32(b, (uint)map.Count);
        foreach (var l in lights)
        {
            var rec = new byte[24];
            BinaryPrimitives.WriteSingleLittleEndian(rec.AsSpan(0x10), l.Intensity);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(0x14), l.Flags);
            b.AddRange(rec);
        }
        b.AddRange(map);
        PutU32(b, (uint)names.Length);
        PutStr(b, names);
        return Pers("ITRLighting", 7, b);
    }

    // fixtures::LightDef: PERS ITRLighting with full light records and 16-byte state records (docs/interiors.md 5.4).
    public readonly record struct LightDef(int Id, uint NameOffset, int NumStates, int FirstState, float Duration, uint Flags);

    // fixtures::StateDef.
    public readonly record struct StateDef(ushort[] Color, float Time);

    // fixtures::dilLightingFile.
    public static List<byte> DilLightingFile(uint buildId, IReadOnlyList<LightDef> lights, IReadOnlyList<StateDef> states, string names)
    {
        var b = new List<byte>();
        PutU32(b, buildId);
        PutU32(b, 0);
        PutU32(b, 0);
        PutU32(b, (uint)states.Count);
        PutU32(b, 0); // state data
        PutU32(b, (uint)lights.Count);
        PutU32(b, 0); // light states
        PutU32(b, 0); // map bytes
        foreach (var s in states)
        {
            var rec = new byte[16];
            for (var k = 0; k < 4; ++k)
                BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(2 * k), s.Color[k]);
            BinaryPrimitives.WriteSingleLittleEndian(rec.AsSpan(8), s.Time);
            b.AddRange(rec);
        }
        foreach (var l in lights)
        {
            var rec = new byte[24];
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(0), l.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), l.NameOffset);
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(8), l.NumStates);
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(12), l.FirstState);
            BinaryPrimitives.WriteSingleLittleEndian(rec.AsSpan(16), l.Duration);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(20), l.Flags);
            b.AddRange(rec);
        }
        PutU32(b, (uint)names.Length);
        PutStr(b, names);
        return Pers("ITRLighting", 7, b);
    }

    // ---- shapes (docs/interiors.md 8.2, 8.3) ----

    // fixtures::dtsCollisionFile: TS::Shape v8 (docs/interiors.md 8.2, 8.3) with a collision detail: node 0 "bounds",
    // node 1 "collision" (child of 0), one object on node 1 whose CelAnimMesh has one frame with the box min / max as its
    // first two vertices (docs/collision.md 6 T2.1, T9).
    public static List<byte> DtsCollisionFile(ReadOnlySpan<float> box)
    {
        var body = new List<byte>();
        int[] counts = [2, 0, 0, 0, 0, 2, 1, 1, 1, 0, 0];
        foreach (var c in counts)
            PutU32(body, (uint)c);
        PutF32(body, 3.0f); // radius
        for (var i = 0; i < 3; ++i)
            PutF32(body, 0);
        for (var i = 0; i < 6; ++i)
            PutF32(body, box[i]); // shape box
        short[][] nodes = [[0, -1, 0, 0, 0], [1, 0, 0, 0, 0]];
        foreach (var n in nodes)
            foreach (var v in n)
                PutU16(body, (ushort)v);
        foreach (var name in new[] { "bounds", "Collision" })
        {
            var text = new byte[24];
            ByteString.ToBytes(name).CopyTo(text, 0);
            body.AddRange(text);
        }
        PutU16(body, 1); // object: name, flags, mesh 0, node 1, pad, offset, two i16
        PutU16(body, 0);
        PutU32(body, 0);
        PutU16(body, 1);
        PutU16(body, 0);
        for (var i = 0; i < 3; ++i)
            PutF32(body, 0);
        PutU32(body, 0);
        PutU32(body, 1); // detail: root node 1, size
        PutF32(body, 10.0f);
        PutU32(body, 0); // v >= 5, v >= 6 words
        PutU32(body, 0);
        // A box mesh with faces: the sphere test runs on the faces (docs/collision.md T4.1).
        var block = DtsBoxMesh(box);
        body.AddRange(block);
        PutU32(body, 0); // no materials
        return Pers("TS::Shape", 8, body);
    }

    // fixtures::DtsSpec: general TS::Shape v8 builder for the shape model tests (docs/interiors.md 8.2, 8.3).
    public sealed class DtsSpec
    {
        public readonly record struct NodeRec(short Name, short Parent, short NumSub, short FirstSub, short DefaultTransform);
        public readonly record struct Seq(int Name, int Cyclic, float Duration, int Priority);
        public readonly record struct Sub(short Sequence, short NumKeys, short FirstKey);
        public readonly record struct Key(float Position, ushort Index, ushort Flags);
        public readonly record struct Xform(short[] Q, float[] T);
        public readonly record struct Obj(short Name, ushort Flags, int Mesh, short NodeIndex, float[] Offset, short NumSub, short FirstSub);
        public readonly record struct Detail(int Root, float Size);

        public List<NodeRec> Nodes { get; set; } = [];
        public List<Seq> Sequences { get; set; } = [];
        public List<Sub> Subs { get; set; } = [];
        public List<Key> Keys { get; set; } = [];
        public List<Xform> Transforms { get; set; } = [];
        public List<string> Names { get; set; } = [];
        public List<Obj> Objects { get; set; } = [];
        public List<Detail> Details { get; set; } = [];
        public List<List<byte>> Meshes { get; set; } = []; // PERS TS::CelAnimMesh blocks
        public float Radius { get; set; } = 3.0f;
        public float[] Box { get; set; } = [-1, -1, -1, 1, 1, 1];
        public int AlwaysNode { get; set; } = -1;
    }

    // fixtures::dtsBoxMesh: CelAnimMesh v3, one frame, a box min..max. Vertices 0 and 1 are the frame box corners, 2..9
    // the eight corners (bit 0 x, bit 1 y, bit 2 z), 12 faces with outward normals for the engine's
    // N = (B - C) x (A - C).
    public static List<byte> DtsBoxMesh(ReadOnlySpan<float> box)
    {
        ReadOnlySpan<float> scale =
        [
            DivF(SubF(box[3], box[0]), 255.0f), DivF(SubF(box[4], box[1]), 255.0f), DivF(SubF(box[5], box[2]), 255.0f),
        ];
        return DtsBoxMeshFrame(scale, box);
    }

    // fixtures::dtsBoxMeshFrame: the same mesh with the frame scale and origin given: a packed vertex decodes to
    // f32(u8 * scale + origin) (docs/collision.md T2.7).
    public static List<byte> DtsBoxMeshFrame(ReadOnlySpan<float> scale, ReadOnlySpan<float> origin)
    {
        var mesh = new List<byte>();
        foreach (var v in new uint[] { 10, 10, 0, 12, 1, 0 })
            PutU32(mesh, v);
        PutF32(mesh, 3.0f);
        foreach (var v in new byte[] { 0, 0, 0, 0, 255, 255, 255, 0 })
            mesh.Add(v);
        for (var c = 0; c < 8; ++c)
            foreach (var v in new byte[] { (byte)((c & 1) != 0 ? 255 : 0), (byte)((c & 2) != 0 ? 255 : 0), (byte)((c & 4) != 0 ? 255 : 0), 0 })
                mesh.Add(v);
        // Quads as corner indices, counter-clockwise seen from outside.
        int[][] quads = [[0, 2, 6, 4], [1, 5, 7, 3], [0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6]];
        foreach (var q in quads)
            foreach (var tri in new[] { new[] { q[0], q[1], q[2] }, [q[0], q[2], q[3]] })
            {
                // (B - C) x (A - C) points outward for A, B, C in this order.
                foreach (var k in tri)
                {
                    PutU32(mesh, (uint)(k + 2));
                    PutU32(mesh, 0);
                }
                PutU32(mesh, 7); // material
            }
        PutU32(mesh, 0);
        for (var i = 0; i < 3; ++i)
            PutF32(mesh, scale[i]);
        for (var i = 0; i < 3; ++i)
            PutF32(mesh, origin[i]);
        return Pers("TS::CelAnimMesh", 3, mesh);
    }

    // fixtures::dtsModelFile.
    public static List<byte> DtsModelFile(DtsSpec s)
    {
        var body = new List<byte>();
        int[] counts = [ s.Nodes.Count, s.Sequences.Count, s.Subs.Count, s.Keys.Count, s.Transforms.Count,
                         s.Names.Count, s.Objects.Count, s.Details.Count, s.Meshes.Count, 0, 0 ];
        foreach (var c in counts)
            PutU32(body, (uint)c);
        PutF32(body, s.Radius);
        for (var i = 0; i < 3; ++i)
            PutF32(body, 0);
        foreach (var b in s.Box)
            PutF32(body, b);
        foreach (var n in s.Nodes)
            foreach (var v in new[] { n.Name, n.Parent, n.NumSub, n.FirstSub, n.DefaultTransform })
                PutU16(body, (ushort)v);
        foreach (var q in s.Sequences)
        {
            PutU32(body, (uint)q.Name);
            PutU32(body, (uint)q.Cyclic);
            PutF32(body, q.Duration);
            PutU32(body, (uint)q.Priority);
            body.AddRange(new byte[16]);
        }
        foreach (var q in s.Subs)
            foreach (var v in new[] { q.Sequence, q.NumKeys, q.FirstKey })
                PutU16(body, (ushort)v);
        foreach (var k in s.Keys)
        {
            PutF32(body, k.Position);
            PutU16(body, k.Index);
            PutU16(body, k.Flags);
        }
        foreach (var t in s.Transforms)
        {
            foreach (var v in t.Q)
                PutU16(body, (ushort)v);
            foreach (var v in t.T)
                PutF32(body, v);
        }
        foreach (var name in s.Names)
        {
            var text = new byte[24];
            var nb = ByteString.ToBytes(name);
            Array.Copy(nb, text, Math.Min(nb.Length, 23));
            body.AddRange(text);
        }
        foreach (var o in s.Objects)
        {
            PutU16(body, (ushort)o.Name);
            PutU16(body, o.Flags);
            PutU32(body, (uint)o.Mesh);
            PutU16(body, (ushort)o.NodeIndex);
            PutU16(body, 0);
            foreach (var v in o.Offset)
                PutF32(body, v);
            PutU16(body, (ushort)o.NumSub);
            PutU16(body, (ushort)o.FirstSub);
        }
        foreach (var d in s.Details)
        {
            PutU32(body, (uint)d.Root);
            PutF32(body, d.Size);
        }
        PutU32(body, 0);
        PutU32(body, (uint)s.AlwaysNode);
        foreach (var m in s.Meshes)
            body.AddRange(m);
        PutU32(body, 0);
        return Pers("TS::Shape", 8, body);
    }

    // fixtures::World: a server world like the dedicated start-up: interpreter, sim, game plugin, resources rooted at
    // `root` with search path "base". Mission classes are registered once.
    public sealed class World
    {
        public Interpreter In { get; } = new();
        public ResourceManager Res { get; }
        public ClassContext Ctx { get; } = new();
        public SimConsole Sim { get; }
        public GameState Game { get; } = new();
        private static bool _classes;
        public string Out { get; set; } = "";
        public int Lines { get; set; }

        public World(string root)
        {
            Res = new ResourceManager(root);
            Sim = new SimConsole(In, Ctx);
            Res.SetSearchPath("base");
            In.SetResources(Res);
            Builtins.RegisterBuiltins(In, Res, root);
            Ctx.Resources = Res;
            Sim.RegisterCommands();
            In.SetObjects(Sim);
            Game.Sim = Sim;
            Game.Console = In;
            Game.Resources = Res;
            Ctx.Game = Game;
            if (!_classes)
            {
                MissionClasses.Register();
                _classes = true;
            }
            GameCommands.Register(In, Game);
            In.SetOutput(l => Out += (Lines++ != 0 ? "\n" : "") + l);
            Run("newServer(); focusServer(); newObject(ConsoleScheduler, SimConsoleScheduler);");
        }

        public string Run(string s)
        {
            Out = "";
            Lines = 0;
            In.Evaluate(s);
            return Out;
        }

        public SimObject? Object(string name) => Sim.Server()!.Find(name);
    }

    // fixtures::Cases: runs (script, expected output) pairs and reports every mismatch.
    public sealed class Cases
    {
        public int Failures { get; set; }

        public void Check(World w, string src, string want)
        {
            var got = w.Run(src);
            if (got != want)
            {
                Console.Error.WriteLine("FAIL " + src + "\n  want: " + want + "\n  got:  " + got);
                ++Failures;
            }
        }
    }
}
