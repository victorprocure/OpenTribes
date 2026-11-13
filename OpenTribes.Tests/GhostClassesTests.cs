// Port of tests/test_ghost_classes.cpp: ghost layouts of the mission environment classes (docs/game-stream-ghosts.md
// 2.3, 3) on objects created by mission script, packed and read back field by field. 110 asserts.
namespace OpenTribes.Tests;

using OpenTribes.Game;
using OpenTribes.Net;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Tests.Fixtures;

public class GhostClassesTests
{
    private readonly GhostManager _gm = new(2049);

    // Packs obj with mask and returns a reader positioned at the start.
    private BitBuffer Pack(SimObject? obj, uint mask, uint tag)
    {
        Assert.NotNull(obj);
        var cls = GhostManager.GhostClassOf(obj!);
        Assert.True(cls != null && cls.Tag == tag); // 2.3 tags
        var buf = new byte[4096];
        var w = new BitBuffer(buf, buf.Length);
        var ctx = new GhostContext(_gm, 2049, mask == 0xffffffffu);
        Assert.Equal(0u, cls!.Pack!(obj!, ctx, w, mask));
        Assert.False(w.Error());
        // Client round trip (docs/client-net.md 3): the unpack of the same class consumes exactly the packed bits.
        var isNew = mask == 0xffffffffu;
        _clients.TryGetValue(obj!, out var prev);
        _client = _clients[obj!] = ClientRoundTrip.Unpack(tag, buf, w.BitPosition(), isNew, ClientRoundTrip.WorldFor(tag, obj), isNew ? null : prev);
        return new BitBuffer(buf, (int)((w.BitPosition() + 7) / 8));
    }

    private readonly Dictionary<SimObject, ClientGhost> _clients = new(ReferenceEqualityComparer.Instance);
    private ClientGhost? _client;

    private static uint U32(BitBuffer r) => r.ReadInt(32);
    private static float F32(BitBuffer r) => r.ReadF32();

    private static void Vec(BitBuffer r, float x, float y, float z)
    {
        float a = r.ReadF32(), b = r.ReadF32(), c = r.ReadF32();
        Assert.True(a == x && b == y && c == z);
    }

    [Fact]
    public void Main()
    {
        var root = Path.Combine(TestPaths.TmpDir(""), "ghost_root");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        PutVol(Path.Combine(root, "base/data.vol"), [("inside.txt", "x")]);
        PutFile(Path.Combine(root, "base/lush.day.ppl"), "palette");
        // Terrain: one 2 x 2 block (detail 2, 8 m squares) with its material list.
        var t = new TerrainSpec
        {
            N = 2,
            Detail = 2,
            Width = 1,
            Height = 1,
            Heights = [.. Enumerable.Repeat(1.0f, 9)],
            Squares = [.. new byte[8]]
        };
        List<byte> dtf = DtfFile(t, 1, 1), dtb = DtbFile(t, 1, 1);
        PutFile(Path.Combine(root, "base/m.dtf"), dtf);
        PutFile(Path.Combine(root, "base/m#0.dtb"), dtb);
        PutFile(Path.Combine(root, "base/t.dml"), "dml");
        // Interior: shape, geometry (box 10 x 2 x 2: isContainer 1) and base lighting.
        float[] big = [0, -1, -1, 10, 1, 1], small = [0, 0, 0, 5, 5, 5];
        PutFile(Path.Combine(root, "base/hut.dis"), DisFile("hut-00.dig", "hut-000-1.dil")); // mission suffix cut (3.4)
        PutFile(Path.Combine(root, "base/hut-00.dig"), DigFile(0x1234, big, [new Node(0, -44, -45)], 1, 45, [[1, 0, 0, 0]]));
        PutFile(Path.Combine(root, "base/hut-000.dil"), DilFile(0x1234, [new LightRec(0.7f, 1), new LightRec(1.0f, 0x10)], [1, 3], "a\0"));
        PutFile(Path.Combine(root, "base/box.dis"), DisFile("box-00.dig", "box-000.dil"));
        PutFile(Path.Combine(root, "base/box-00.dig"), DigFile(0x55, small, [new Node(0, -44, -45)], 1, 45, [[1, 0, 0, 0]]));
        PutFile(Path.Combine(root, "base/box-000.dil"), DilFile(0x55, [], [], ""));

        var w = new Fixtures.World(root);
        GhostClasses.Register();
        w.Run("$Server::teamName0 = \"A\"; resetPlayerManager();");
        var output = w.Run(
            "instant SimGroup \"MissionGroup\" {" +
            " instant SimVolume \"vol\" { fileName = \"DATA.VOL\"; };" +
            " instant SimPalette \"pal\" { fileName = \"lush.day.ppl\"; };" +
            " instant SimPalette \"nopal\" { fileName = \"none.ppl\"; };" +
            " instant MissionCenterPos \"MC\" { x = \"0.1\"; h = -700; };" +
            " instant SimTerrain \"Terrain\" { tedFileName = \"m.dtf\"; hazeDistance = 1500; position = \"-8 -8 0\";" +
            "   contGravity = \"0 0 -20\"; };" +
            " instant SimLight \"L\" { type = 5; isStatic = true; range = 12.5; color = \"1 0.5 0.25\"; aim = \"0 0 -1\"; };" +
            " instant Snowfall \"Snow\" { intensity = 0.5; wind = \"3 4 5\"; rain = true; };" +
            " instant Sky \"Sky\" { dmlName = \"sky.dml\"; skyColor = \"0.25 0.5 1\"; size = 300; textures[3] = 7; };" +
            " instant Planet \"Planet1\" { fileName = \"sun\"; azimuth = 400; incidence = 120; castShadows = true; ambient = \"0.5 0.5 0.5\"; };" +
            " instant Planet \"Planet2\" { incidence = -100; };" +
            " instant StarField \"Stars\" { colors[1] = \"0.75 0.75 0.75\"; inFrontOfSky = false; };" +
            " instant TeamGroup \"x\" {" +
            "   instant InteriorShape \"Hut\" { filename = \"hut.dis\"; position = \"1 2 3\"; rotation = \"0 0 0\"; };" +
            "   instant InteriorShape \"Box\" { filename = \"box.dis\"; };" +
            "   instant InteriorShape \"None\" { filename = \"missing.dis\"; }; };" +
            "};");
        // 3.1: a missing .ppl fails onAdd (mission.md 3.1).
        Assert.True(output.Contains("Failed to registerObject of type: SimPalette (nopal)", StringComparison.Ordinal));
        SimObject? Obj(string path) => w.Object("MissionGroup/" + path);

        // 3.1 SimVolume: file name with the first spelling interned (the volume scan's).
        {
            var r = Pack(Obj("vol"), 0xffffffffu, 0x001);
            Assert.Equal("data.vol", (string)_client!.State["file"]!); // client-net.md 3.3 SimVolume
            Assert.Equal("data.vol", r.ReadString());
        }
        // 3.1 SimPalette.
        {
            var r = Pack(Obj("pal"), 0xffffffffu, 0x003);
            Assert.Equal("lush.day.ppl", r.ReadString());
        }
        // 3.1 MissionCenterPos: x y w h, ctor 1230 1230 1400 1400; floats parsed toward zero.
        {
            var r = Pack(Obj("MC"), 0xffffffffu, 0x2cc);
            var rect = (double[])_client!.State["rect"]!;
            Assert.True(rect.Length == 4 && rect[1] == 1230 && rect[2] == 1400 && rect[3] == -700); // 3.1 MissionCenterPos
            Assert.Equal(0x3dccccccu, U32(r)); // "0.1" truncated (nearest would be 0x3dcccccd)
            Assert.Equal(1230, F32(r));
            Assert.Equal(1400, F32(r));
            Assert.Equal(-700, F32(r));
            Assert.True(w.Sim.Server()!.FindById(716) == Obj("MC"));
        }
        // 3.2 SimTerrain: one flag per mask bit, the unconditional flag, then the CRC.
        {
            var terrain = Obj("Terrain") as TerrainObject;
            Assert.True(terrain != null && w.Sim.Server()!.FindById(8) == terrain); // fixed id 8 (mission.md 3.1)
            var r = Pack(terrain, 0xffffffffu, 0x002);
            Assert.True((string)_client!.State["ted"]! == "m.dtf" && ClientRoundTrip.VecIs(_client.State["position"], -8, -8, 0)); // 3.2
            Assert.True(ClientRoundTrip.VecIs(_client.State["contGravity"], 0, 0, -20) && _client.State.ContainsKey("crc"));
            Assert.Equal(1500, ((double[])_client.State["visible/haze/hazeVMin/hazeVMax"]!)[1]);
            Assert.True(r.ReadFlag() && r.ReadString() == "m.dtf");
            Assert.True(r.ReadFlag() && F32(r) == 3000 && F32(r) == 1500 && F32(r) == 1000 && F32(r) == 2000);
            Assert.True(r.ReadFlag() && F32(r) == 500 && F32(r) == 120);
            Assert.True(r.ReadFlag());
            Vec(r, -8, -8, 0);
            Vec(r, 0, 0, 0);
            Assert.True(r.ReadFlag());
            Vec(r, 0, 0, -20);
            Assert.Equal(0, F32(r));
            Assert.Equal(10000, F32(r));
            Assert.False(r.ReadFlag()); // +0x6a4 rendering suppressed
            Assert.True(r.ReadFlag() && U32(r) == terrain!.Crc());
            byte[] dml = [(byte)'d', (byte)'m', (byte)'l'];
            var crc = 0xffffffffu;
            foreach (var b in new IReadOnlyList<byte>[] { dtf, dtb, dml })
                foreach (var c in b)
                {
                    crc ^= c;
                    for (var k = 0; k < 8; ++k)
                        crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1)));
                }
            Assert.True(terrain.Crc() == crc);
            var r2 = Pack(terrain, 0x40, 0x002);
            for (var i = 0; i < 6; ++i)
                Assert.False(r2.ReadFlag());
            Assert.True(r2.ReadFlag() && U32(r2) == crc);
        }
        // 3.3 SimLight: isStatic first (0x40), then 0x01..0x20; unset fields 0 (D12).
        {
            var r = Pack(Obj("L"), 0xffffffffu, 0x006);
            Assert.True((long)_client!.State["type"]! == 5 && (long)_client.State["isStatic"]! == 1 && (double)_client.State["range"]! == 12.5); // 3.3
            Assert.True(ClientRoundTrip.VecIs(_client.State["color"], 1, 0.5, 0.25));
            Assert.True(r.ReadFlag() && r.ReadFlag());
            Assert.True(r.ReadFlag() && r.ReadInt(3) == 5);
            Assert.True(r.ReadFlag() && F32(r) == 12.5f);
            Assert.True(r.ReadFlag());
            Vec(r, 1, 0.5f, 0.25f);
            Assert.True(r.ReadFlag());
            Vec(r, 0, 0, 0);
            Assert.True(r.ReadFlag());
            Vec(r, 0, 0, 0);
            Assert.True(r.ReadFlag());
            Vec(r, 0, 0, -1);
            var r2 = Pack(Obj("L"), 0x02, 0x006);
            Assert.True(!r2.ReadFlag() && !r2.ReadFlag() && r2.ReadFlag() && F32(r2) == 12.5f);
            for (var i = 0; i < 4; ++i)
                Assert.False(r2.ReadFlag());
        }
        // 3.5 Snowfall: global wind (x, y, -75 for rain), suspendRendering ctor 1, rain.
        {
            Assert.True(w.Sim.Server()!.FindById(714) == Obj("Snow"));
            var r = Pack(Obj("Snow"), 0xffffffffu, 0x216);
            Assert.True((double)_client!.State["intensity"]! == 0.5 && ClientRoundTrip.VecIs(_client.State["wind"], 3, 4, -75) && (long)_client.State["flagf1"]! == 1); // 3.5
            Assert.True(r.ReadFlag() && F32(r) == 0.5f);
            Assert.True(r.ReadFlag());
            Vec(r, 3, 4, -75);
            Assert.True(r.ReadFlag() && r.ReadFlag());
        }
        // 3.6 Sky: ctor size 600 (set 300 here), textures i % 2 per element unless set.
        {
            Assert.True(w.Sim.Server()!.FindById(729) == Obj("Sky"));
            var r = Pack(Obj("Sky"), 0xffffffffu, 0x230);
            Assert.True((string)_client!.State["dml"]! == "sky.dml" && ClientRoundTrip.VecIs(_client.State["vecB8"], 0.25, 0.5, 1)); // 3.6
            Assert.True(((List<object?>)_client.State["textures"]!)[3] is long t3 && t3 == 7);
            Assert.Equal("sky.dml", r.ReadString());
            Vec(r, 0.25f, 0.5f, 1);
            Vec(r, 0, 0, 0); // hazeColor unset (D12)
            Assert.Equal(0, F32(r));
            Assert.Equal(300, F32(r));
            for (uint i = 0; i < 16; ++i)
                Assert.True(r.ReadInt(4) == (i == 3 ? 7 : i % 2));
        }
        // 3.6 Planet: incidence clamped to [-89, 89], azimuth as given, ctor size and distance.
        {
            var r = Pack(Obj("Planet1"), 0xffffffffu, 0x22f);
            Assert.True((string)_client!.State["file"]! == "sun" && (double)_client.State["f6c"]! == 400 && (double)_client.State["f70"]! == 89); // 3.6
            Assert.Equal("sun", r.ReadString());
            Assert.Equal(400, F32(r));
            Assert.Equal(89, F32(r));
            Assert.Equal(1u, r.ReadInt(8));
            Assert.Equal(0u, r.ReadInt(8));
            Vec(r, 0, 0, 0);
            Vec(r, 0.5f, 0.5f, 0.5f);
            Assert.Equal(2000, F32(r));
            Assert.Equal(11000, F32(r));
            var m = Pack(Obj("Planet2"), 0xffffffffu, 0x22f);
            Assert.True(m.ReadString() == "" && F32(m) == 0 && F32(m) == -89);
        }
        // 3.6 StarField: ctor flags 1 and 0, colors (1, 0.5, 0.25 grey) per element, inFrontOfSky.
        {
            var r = Pack(Obj("Stars"), 0xffffffffu, 0x232);
            Assert.True((long)_client!.State["flagAF"]! == 1 && (long)_client.State["flagB0"]! == 0 && ((double[])_client.State["data7c"]!)[3] == 0.75); // 3.6
            Assert.True(r.ReadFlag() && !r.ReadFlag());
            Vec(r, 1, 1, 1);
            Vec(r, 0.75f, 0.75f, 0.75f);
            Vec(r, 0.25f, 0.25f, 0.25f);
            Assert.False(r.ReadFlag());
        }
        // 3.4 InteriorShape: GameBase part (4.1), initial record, then the update form.
        {
            var hut = Obj("x/Hut");
            Assert.Null(hut); // the TeamGroup was renamed team0 (mission.md TM6)
            hut = Obj("team0/Hut");
            var r = Pack(hut, 0xffffffffu, 0x20a);
            Assert.True((string)_client!.State["file"]! == "hut.dis" && (long)_client.State["isContainer"]! == 1 && ClientRoundTrip.VecIs(_client.State["pos"], 1, 2, 3)); // 3.4
            Assert.True((long)_client.State["lightCount"]! == 2 && ((List<object?>)_client.State["states"]!).Count == 2);
            Assert.True(r.ReadFlag() && r.ReadInt(4) == 0 && !r.ReadFlag() && !r.ReadFlag()); // team 0
            Assert.True(r.ReadFlag()); // initial
            Assert.Equal("hut.dis", r.ReadString());
            Assert.Equal(1, r.ReadSignedInt(2)); // box extent 10 >= 6
            Vec(r, 1, 2, 3);
            Vec(r, 0, 0, 0);
            Assert.Equal(2u, U32(r));
            Assert.Equal(0x1234u, U32(r));     // .dig buildId
            Assert.Equal(0x000100c2u, U32(r)); // fold(map 01 03) << 16 | fold(name "a\0")
            Assert.Equal(2u, U32(r));
            var rec = new byte[16];
            r.ReadBytes(rec);
            var inten = BitConverter.ToSingle(rec, 0);
            Assert.True(inten == 0.7f && rec[4] == 1 && rec[5] == 0 && rec[8] == 0 && rec[12] == 0);
            r.ReadBytes(rec);
            inten = BitConverter.ToSingle(rec, 0);
            Assert.True(inten == 1.0f && rec[4] == 0); // flags 0x10: bit 0 clear
            Assert.False(r.ReadFlag());                // +0x41c
            // Update: position/rotation, one flag per light, then +0x418.
            var u = Pack(hut, 0x07, 0x20a);
            Assert.True(ClientRoundTrip.Has(_client!, "pos") && ClientRoundTrip.Has(_client!, "lightOn") && ((List<object?>)ClientRoundTrip.Field(_client!, "lightOn")!).Count == 2);
            Assert.True(u.ReadFlag() && u.ReadInt(4) == 0 && !u.ReadFlag() && !u.ReadFlag());
            Assert.False(u.ReadFlag());
            Assert.True(u.ReadFlag());
            Vec(u, 1, 2, 3);
            Vec(u, 0, 0, 0);
            Assert.True(u.ReadFlag() && u.ReadFlag() && !u.ReadFlag());
            Assert.True(u.ReadFlag() && !u.ReadFlag());
            // InteriorShape::setTransform (GameBase::setPosition on an interior): mask 0x01 with the new position.
            {
                float[] np = [7, 8, 9];
                ((GameBase)hut!).SetInteriorPosition(np);
                var tt = Pack(hut, 0x01, 0x20a);
                Assert.False(tt.ReadFlag()); // no GameBase part
                Assert.False(tt.ReadFlag()); // not initial
                Assert.True(tt.ReadFlag());
                Vec(tt, 7, 8, 9);
                Vec(tt, 0, 0, 0);
                Assert.True(!tt.ReadFlag() && !tt.ReadFlag());
            }
            // G38: the cast reads the current transform (InteriorShape_getCollisionImages 0x0047f814).
            {
                var c = w.Game.GetContainer()!;
                object? o = null;
                Vec3 hit = default, nrm = default;
                bool Ray(float x0, float x1) =>
                    c.CastRay(new Vec3(x0, 8, 9), new Vec3(x1, 8, 9), ObjectType.InteriorType, null, ref o, ref hit, ref nrm);
                float[] at = [1, 8, 9], to = [3, 8, 9];
                ((GameBase)hut!).SetInteriorPosition(at);
                Assert.True(Ray(2, 2.5f) && o == hut);
                ((GameBase)hut).SetInteriorPosition(to);
                Assert.True(!Ray(2, 2.5f) && Ray(4, 5) && o == hut);
            }
            // isContainer: box extents below 6 give 0; no shape keeps the persist value.
            var b = Pack(Obj("team0/Box"), 0xffffffffu, 0x20a);
            b.ReadFlag();
            b.ReadInt(4);
            b.ReadFlag();
            b.ReadFlag();
            Assert.True(b.ReadFlag() && b.ReadString() == "box.dis" && b.ReadSignedInt(2) == 0);
            var n = Pack(Obj("team0/None"), 0xffffffffu, 0x20a);
            n.ReadFlag();
            n.ReadInt(4);
            n.ReadFlag();
            n.ReadFlag();
            Assert.True(n.ReadFlag() && n.ReadString() == "missing.dis" && n.ReadSignedInt(2) == -1);
            Vec(n, 0, 0, 0);
            Vec(n, 0, 0, 0);
            Assert.True(U32(n) == 0 && U32(n) == 0 && !n.ReadFlag());
        }
        // 3.3 SimLight setters (ROADMAP G30): event 0x420 of activateGroup (0x0052d290) sets the colour of every member
        // but the argument object and sends mask 0x04; processArguments (0x0052cac0) of newObject.
        {
            var masks = new List<(SimObject, uint)>();
            w.Game.MaskBits = (o, bb) => masks.Add((o, bb));
            w.Run("instant SimGroup \"LG\" { instant SimLight \"LA\" { color = \"1 1 1\"; }; instant SimLight \"LB\" { color = \"1 1 1\"; }; };");
            var la = w.Object("LG/LA");
            var lb = w.Object("LG/LB");
            Assert.True(la != null && lb != null);
            masks.Clear();
            Assert.Equal("True", w.Run("echo(activateGroup(LG, \"0.5\"));"));
            Assert.True(masks.Count == 2 && masks[0].Item1 == la && masks[0].Item2 == 0x04 && masks[1].Item1 == lb);
            var c = Pack(la, 0x04, 0x006);
            Assert.True(!c.ReadFlag() && !c.ReadFlag() && !c.ReadFlag() && c.ReadFlag());
            Vec(c, 0.5f, 0.5f, 0.5f);
            masks.Clear();
            Assert.Equal("True", w.Run("echo(activateGroup(" + la!.Id() + ", \"0.25\"));")); // an object: its group, without itself
            Assert.True(masks.Count == 1 && masks[0].Item1 == lb);
            var d = Pack(lb, 0x04, 0x006);
            d.ReadFlag();
            d.ReadFlag();
            d.ReadFlag();
            Assert.True(d.ReadFlag());
            Vec(d, 0.25f, 0.25f, 0.25f);
            Assert.Equal("True", w.Run("echo(activateGroup(" + la.Id() + "));")); // no value: 0
            var z = Pack(lb, 0x04, 0x006);
            z.ReadFlag();
            z.ReadFlag();
            z.ReadFlag();
            Assert.True(z.ReadFlag());
            Vec(z, 0, 0, 0);
            Assert.Equal("False", w.Run("echo(activateGroup(nosuch, 1));"));
            Assert.Equal("wrong argcount to activateGroup(212652310bject1,<value>)\nFalse", w.Run("echo(activateGroup());"));

            // Directional: colour, direction normalized twice, optional ambient (type 1; DirectionalWrap 6).
            static bool Near(float a, double bb) => Math.Abs(a - bb) < 1e-5;
            Assert.NotEqual("0", w.Run("echo(newObject(N1, SimLight, Directional, 1, 0.5, 0.25, 3, 4, 5));"));
            var n = Pack(w.Object("N1"), 0xffffffffu, 0x006);
            Assert.True(n.ReadFlag() && !n.ReadFlag()); // mask 0x40: isStatic false
            Assert.True(n.ReadFlag() && n.ReadInt(3) == 1);
            Assert.True(n.ReadFlag());
            F32(n); // range: unset
            Assert.True(n.ReadFlag());
            Vec(n, 1, 0.5f, 0.25f);
            Assert.True(n.ReadFlag());
            Vec(n, 0, 0, 0); // ambient absent: 0
            Assert.True(n.ReadFlag());
            F32(n);
            F32(n);
            F32(n); // position: unset
            Assert.True(n.ReadFlag());
            float ax = F32(n), ay = F32(n), az = F32(n);
            Assert.True(Near(ax, 3 / Math.Sqrt(50.0)) && Near(ay, 4 / Math.Sqrt(50.0)) && Near(az, 5 / Math.Sqrt(50.0)));
            Assert.NotEqual("0", w.Run("echo(newObject(N2, SimLight, DirectionalWrap, 0.3, 0.6, 0.9, 0, 0, 2, 0.2, 0.3, 0.4));"));
            var wr = Pack(w.Object("N2"), 0x01, 0x006);
            Assert.True(!wr.ReadFlag() && wr.ReadFlag() && wr.ReadInt(3) == 6);
            var wa = Pack(w.Object("N2"), 0x08, 0x006);
            for (var i = 0; i < 4; ++i)
                Assert.False(wa.ReadFlag());
            Assert.True(wa.ReadFlag());
            float a0 = F32(wa), a1 = F32(wa), a2 = F32(wa);
            Assert.True(Near(a0, 0.2) && Near(a1, 0.3) && Near(a2, 0.4));
            // Point: type 2, range first; ambient only with 11 arguments.
            Assert.NotEqual("0", w.Run("echo(newObject(N3, SimLight, Point, 50, 1, 1, 0.5, 10, 20, 30));"));
            var pt = Pack(w.Object("N3"), 0xffffffffu, 0x006);
            Assert.True(pt.ReadFlag() && !pt.ReadFlag());
            Assert.True(pt.ReadFlag() && pt.ReadInt(3) == 2);
            Assert.True(pt.ReadFlag() && F32(pt) == 50.0f);
            Assert.True(pt.ReadFlag());
            Vec(pt, 1, 1, 0.5f);
            Assert.True(pt.ReadFlag());
            Vec(pt, 0, 0, 0);
            Assert.True(pt.ReadFlag());
            Vec(pt, 10, 20, 30);
            // Wrong forms print the usage line and the generic line, and the object is gone.
            Assert.True(w.Run("echo(newObject(N4, SimLight, Directional, 1, 2, 3));") ==
                        "SimLight: Directional intR intG intB dirX dirY dirz [ambR ambG ambB]\n" +
                        "SimLight: type(Directional/Point) ...\n" +
                        "newObject for: SimLight failed arg list: Directional, 1, 2, 3\n0");
            Assert.Equal(0, w.Run("echo(newObject(N5, SimLight, Point, 1, 2));").IndexOf("SimLight: Point range intR", StringComparison.Ordinal));
            Assert.Equal(0, w.Run("echo(newObject(N6, SimLight, Spot, 1));").IndexOf("SimLight: type(Directional/Point) ...\n", StringComparison.Ordinal));
            Assert.Equal(0, w.Run("echo(newObject(N7, SimLight));").IndexOf("SimLight: type(Directional/Point) ...\n", StringComparison.Ordinal));
            Assert.True(w.Object("N4") == null && w.Object("N7") == null);
            w.Game.MaskBits = null;
        }
    }
}
