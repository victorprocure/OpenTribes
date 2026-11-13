// Port of tests/test_fuzz.cpp: robustness of everything that parses untrusted bytes (D11: malformed input is rejected,
// never trusted): mutated and random inputs must not crash, hang or read out of bounds. Deterministic (fixed seed). 24
// asserts, one to one.
namespace OpenTribes.Tests;

using System.Globalization;
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Net;
using OpenTribes.Script;
using OpenTribes.Server;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Tests.Fixtures;
using Node = OpenTribes.Tests.Fixtures.Node;
using Player = OpenTribes.Game.Player;

public class FuzzTests
{
    private sealed class Rng
    {
        public uint S { get; set; }

        public Rng(uint s) => S = s;

        public uint Next()
        {
            S ^= S << 13;
            S ^= S >> 17;
            S ^= S << 5;
            return S;
        }

        public uint Below(uint n) => n != 0 ? Next() % n : 0;
    }

    private static readonly uint[] Edges =
    [
        0, 1, 0x7f, 0x80, 0xff, 0x100, 0x7fff, 0xffff, 0x10000, 0x7fffffff, 0x80000000u, 0xffffffffu, 0xfffffff0u,
    ];

    // One mutation step: bit flips, byte overwrites with boundary values, truncation, growth, 32-bit overwrites with
    // boundary values (counts and offsets).
    private static List<byte> Mutate(List<byte> seed, Rng r)
    {
        var b = new List<byte>(seed);
        var steps = 1 + r.Below(4);
        for (uint i = 0; i < steps; ++i)
        {
            switch (r.Below(6))
            {
                case 0:
                    if (b.Count != 0)
                    {
                        var at = (int)r.Below((uint)b.Count);
                        b[at] ^= (byte)(1u << (int)r.Below(8));
                    }
                    break;
                case 1:
                    if (b.Count != 0)
                    {
                        var at = (int)r.Below((uint)b.Count);
                        b[at] = (byte)Edges[r.Below(5)];
                    }
                    break;
                case 2:
                {
                    var n = (int)r.Below((uint)b.Count + 1);
                    if (n < b.Count)
                        b.RemoveRange(n, b.Count - n);
                    break;
                }
                case 3:
                    for (var n = r.Below(40); n > 0; --n)
                        b.Add((byte)r.Next());
                    break;
                case 4:
                    if (b.Count >= 4)
                    {
                        var v = Edges[r.Below((uint)Edges.Length)];
                        var at = (int)r.Below((uint)b.Count - 3);
                        for (var k = 0; k < 4; ++k)
                            b[at + k] = (byte)(v >> (8 * k));
                    }
                    break;
                default:
                    if (b.Count != 0)
                    {
                        var at = (int)r.Below((uint)b.Count);
                        var len = (int)r.Below((uint)(b.Count - at + 1));
                        b.RemoveRange(at, len);
                    }
                    break;
            }
        }
        return b;
    }

    private static List<byte> RandomBytes(Rng r, uint maxLen)
    {
        var b = new List<byte>(new byte[r.Below(maxLen + 1)]);
        for (var i = 0; i < b.Count; ++i)
            b[i] = (byte)r.Next();
        return b;
    }

    private static void Fuzz(IReadOnlyList<List<byte>> seeds, uint rounds, uint seed, Action<byte[]> run)
    {
        var r = new Rng(seed);
        foreach (var s in seeds)
            run([.. s]);
        for (uint i = 0; i < rounds; ++i)
            run([.. r.Below(8) == 0 ? RandomBytes(r, 300) : Mutate(seeds[(int)r.Below((uint)seeds.Count)], r)]);
    }

    private static string Literal(IEnumerable<byte> b)
    {
        var t = new System.Text.StringBuilder("\"");
        foreach (var c in b)
            t.Append(c is (byte)'"' or (byte)'\\' or (byte)'\n' or (byte)'\r' or 0 or (byte)'#' ? 'x' : (char)c);
        return t.Append('"').ToString();
    }

    private static string Expand(string form, string a, string c)
    {
        var line = new System.Text.StringBuilder();
        for (var i = 0; i < form.Length; ++i)
        {
            if (form[i] == '%' && i + 1 < form.Length && (form[i + 1] == 'a' || form[i + 1] == 'c'))
                line.Append(form[++i] == 'a' ? a : c);
            else
                line.Append(form[i]);
        }
        return line.ToString();
    }

    private static void FuzzFiles()
    {
        var t = new TerrainSpec
        {
            Heights = [.. Enumerable.Repeat(3.0f, 25)],
            Squares = [.. new byte[32]]
        };
        float[] box = [-8, -8, -8, 8, 8, 8];
        var dig = DigFile(0x1234, box, [new Node(0, -44, -45)], 1, 45, [[1, 0, 0, 0]]);
        var dis = DisFile("hut-00.dig", "hut-000.dil");
        var dtb = DtbFile(t, 0, 16);

        Fuzz([dig], 20000, 1, b =>
        {
            var g = InteriorGeometry.Parse(b);
            // Surface lookup of a ray hit (docs/collision.md 5.2 part 4.1) on whatever was read.
            if (g != null)
                for (var leaf = 43; leaf < 46; ++leaf)
                    for (var plane = 0; plane < 3; ++plane)
                        g.SurfaceMaterial(g.SurfaceAt(leaf, plane, new Vec3(0.5f, 0.25f, 0.125f)));
        });
        Fuzz([dis], 20000, 2, b =>
        {
            var f = new InteriorShapeFile();
            InteriorShapeFile.Parse(b, f);
        });
        Fuzz([dtb], 20000, 3, b =>
        {
            var g = new GridBlock();
            GridBlock.Parse(b, g);
        });
        Fuzz([dig, dis, dtb], 20000, 4, b =>
        {
            var output = new float[6];
            Shape.ReadShapeBox(b, output);
        });
        // LZH: any input decodes into exactly the requested size without reading past the input.
        Fuzz([new List<byte> { 0x00, 0x11, 0x22, 0x33 }], 5000, 5, b =>
        {
            var output = new byte[512];
            Lzh.LzhDecodeCounted(b, output);
        });
        // RLE (volume compression 1): any input fills the requested size or fails, never reading past the input.
        Fuzz([new List<byte> { 0x05, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0x8a, (byte)'z', 0x03, (byte)'a', (byte)'b', (byte)'c', 0x80, 0x00, 0xff, (byte)'q' }],
             5000, 8, b =>
             {
                 var output = new byte[700];
                 VolArchive.RleDecode(b, output);
                 VolArchive.RleDecode(b, output.AsSpan(0, 7));
             });

        // Volumes are opened from disk.
        var dir = Path.Combine(TestPaths.TmpDir(""), "fuzz_vol");
        Directory.CreateDirectory(dir);
        string rleLit = "\x05hello\x8az\x03abc", rleRuns = "\xff" + "a\x81" + "b\x80" + "c\x00";
        PutVol(Path.Combine(dir, "seed.vol"), [("a.cs", "echo(1);"), ("sub\\b.txt", new string('x', 300))],
               [
                   new PackedEntry { Name = "r1.dat", Packed = rleLit, Size = 5 + 10 + 3 },
                   new PackedEntry { Name = "r2.dat", Packed = rleRuns, Size = 127 + 1 },
                   new PackedEntry { Name = "r3.dat", Packed = "\x7f", Size = 64 },
               ]);
        List<byte> vol;
        {
            var path = Path.Combine(dir, "seed.vol");
            Assert.True(File.Exists(path));
            var data = File.ReadAllBytes(path);
            Assert.True(data.Length == new FileInfo(path).Length);
            vol = [.. data];
        }
        Fuzz([vol], 1500, 6, b =>
        {
            File.WriteAllBytes(Path.Combine(dir, "m.vol"), b);
            using var a = new VolArchive();
            if (!a.Open(Path.Combine(dir, "m.vol")))
                return;
            foreach (var e in a.Entries())
                a.Read(e, out _);
        });
    }

    private static void FuzzScript()
    {
        var seed = "function f(%a) { for (%i = 0; %i < 3; %i++) $x[%i, \"k\"] = %a @ \"s\\n\" $+ 0x1F; " +
                      "if (%a == 1) return f(%a - 1); else echo(%a.b, a::b(1, 2)); }\n" +
                      "instant SimGroup \"g\" { x = \"1\"; instant Marker m { p[0] = 1; }; };\n" +
                      "#tag = 5, \"text\"; while (true) break;";
        Fuzz([new List<byte>(ByteString.ToBytes(seed))], 20000, 7, b => ScriptParser.Parse(ByteString.FromBytes(b)));
    }

    // The text-parsing commands of docs/script.md 6.7 on mutated and random strings.
    private static void FuzzMathCommands()
    {
        Numbers.EnterScriptFpuMode();
        var interp = new Interpreter();
        MathCommands.RegisterMathCommands(interp, null);
        interp.SetOutput(_ => { });
        var seedText = "100% %1 %2%9 %0 %a 1e 1e+5 -.5e-3 +INF -NAN 0x10 1,2,3 3.4028237e38 1e-45 9e999 abc";
        string[] forms =
        [
            "sprintf(%a, %c, %a);", "Vector::add(%a, %c);", "Vector::dot(%c, %a);", "Vector::normalize(%a);",
            "Vector::getRotation(%c);", "Vector::getFromRot(%a, %c);", "Vector::getDistance(%a, %a);", "Vector::neg(%c);",
        ];
        Fuzz([new List<byte>(ByteString.ToBytes(seedText))], 6000, 11, b =>
        {
            string a = Literal(b), c = Literal(Enumerable.Reverse(b));
            foreach (var form in forms)
                interp.Evaluate(Expand(form, a, c));
            // The pow command itself would stop in the original's math error handler; the function reports those cases
            // instead.
            MathCommands.BorlandPow(Numbers.BorlandAtof(ByteString.FromBytes(b)), Numbers.BorlandAtof(ByteString.FromBytes(Enumerable.Reverse(b).ToArray())));
        });
    }

    // The backlog commands (docs/script.md 6.8) on mutated strings.
    private static void FuzzBacklogCommands()
    {
        Numbers.EnterScriptFpuMode();
        var root = Path.Combine(TestPaths.TmpDir(""), "ot_fuzz_backlog");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var w = new Fixtures.World(root);
        w.Game.Players.Send = (_, _) => { };
        w.In.SetVariable("$Server::teamName0", "Blood Eagle");
        w.In.SetVariable("$Server::teamSkin0", "beagle");
        w.Run("resetPlayerManager();");
        w.Game.AddTeam(0);
        w.Game.Players.AddClient(2049, "A", "male1", true);
        w.Game.Players.AddClient(2050, "B", "male1", true);
        w.Run("Client::setDataFinished(2049); Client::setDataFinished(2050);");
        w.Run("Client::setInitialTeam(2049, 0); Client::setInitialTeam(2050, 0); Client::setGuiMode(2049, 2);");
        w.Run("ItemData FuzzItem { showInventory = true; };");
        w.In.SetOutput(_ => { });
        var seedText = "2049 2050 -1 255 256 1e30 -5.5 0 0 0 abc FuzzItem SimGame.sae 1.5 99999999999";
        string[] forms =
        [
            "WaypointToWorld(%a);", "issueCommand(2049, 2050, %a, %c, %a, %c);",
            "issueCommandI(2049, 2050, %a, %c, %a, %c);", "issueTargCommand(2049, 2050, %a, %c, %a);",
            "issueCommandI(%a, %c, 3, \"t\", 1, 2);", "setCommandStatus(%a, %c, %a);",
            "remoteSelectCommander(%a, %c);", "playSound(%a, %c);", "playVoice(%a, %c);",
            "GameBase::playSound(%a, %c, %a);", "GameBase::getLOSInfo(%a, %c, %a);",
            "GameBase::getMuzzleTransform(%a);", "Interior::setLightTime(%a, %c, %a);",
            "Client::setItemShopping(2049, %a, %c);", "Client::setItemBuying(%a, %c);",
            "Client::clearItemShopping(%a, %c);", "Client::clearItemBuying(2049, %a);",
            "Client::isItemShoppingOn(%a, %c);", "Client::isItemBuyingOn(2049, %a);",
            "Client::getMouseSensitivity(%a);", "Client::getMouseXAxisFlip(%c);",
            "setTerrainVisibility(%a, %c, %a);", "setTerrainDetail(%a, %c, %a);",
            "setTerrainContainer(%a, %c, %a, %c);", "lightTerrain(%a, %c, %a);", "saveTerrain(%a, %c);",
            "loadObject(%a, %c);", "storeObject(%a, %c, %a);",
        ];
        Fuzz([new List<byte>(ByteString.ToBytes(seedText))], 3000, 13, b =>
        {
            string a = Literal(b), c = Literal(Enumerable.Reverse(b));
            foreach (var form in forms)
                w.In.Evaluate(Expand(form, a, c));
        });
        Directory.Delete(root, true);
    }

    private static void FuzzServer()
    {
        var cfg = new ServerConfig();
        var server = new NetServer(cfg);
        var sent = 0;
        server.SetSendFunction((_, _) => ++sent);
        server.SetLog(_ => { });

        // Seeds: the queries and a connect request (docs/net-protocol.md 5, 6).
        var buf = new byte[600];
        var bs = new BitBuffer(buf, buf.Length);
        bs.WriteBytes([1, 0]);
        foreach (var s in new[] { "Tribes", "1.11", "", "Fuzz", "Male", "male1", "base" })
            bs.WriteString(s);
        var p = new VcPacket { Id = 0x2222, IdBit = 0, Seq = 1, Type = 3, Payload = buf.AsSpan(0, bs.BytePosition()).ToArray() };
        var seeds = new List<List<byte>>
        {
            new() { 0x60, 0x12, 0x34 }, new() { 0x62, 0x12, 0x34 }, new() { 0x10, 3, 0xff, 0, 0x34, 0x12, 0, 0 },
            new() { 0x10, 7, 0xff, 0, 0x34, 0x12, 0, 0 }, new(VcPacket.Build(p)),
        };
        foreach (var type in new uint[] { 0, 2, 4, 5, 7 })
        {
            var q = new VcPacket { Id = p.Id, IdBit = p.IdBit, Seq = 2, AckAck = p.AckAck, Type = type, Payload = p.Payload };
            seeds.Add([.. VcPacket.Build(q)]);
        }
        var r = new Rng(8);
        uint now = 0;
        for (uint i = 0; i < 60000; ++i)
        {
            var b = (r.Below(8) == 0 ? RandomBytes(r, 200) : Mutate(seeds[(int)r.Below((uint)seeds.Count)], r)).ToArray();
            // A few source addresses, so mutated packets hit connections in every state.
            var from = new Address(0x7f000001u, (ushort)(3000 + r.Below(6)));
            server.Receive(from, b, now);
            now += r.Below(50);
        }
        _ = sent;
    }

    private static void FuzzGameStream()
    {
        // Seed: rate fields, one ordered console event, PSC with four moves in every form of 6.1.
        var buf = new byte[300];
        var bs = new BitBuffer(buf, buf.Length);
        bs.WriteFlag(true);
        bs.WriteInt(10, 20);
        bs.WriteFlag(false);
        bs.WriteFlag(true); // events
        bs.WriteFlag(true); // event present
        bs.WriteFlag(true); // guaranteed
        bs.WriteFlag(false);
        bs.WriteFlag(true);
        bs.WriteInt(0, 7);
        bs.WriteInt(0x408 - 0x400, 7);
        bs.WriteInt(3, 5);
        foreach (var s in new[] { "say", "0", "hello there" })
            bs.WriteString(s);
        bs.WriteFlag(false); // end of events
        bs.WriteFlag(true);  // PSC
        bs.WriteInt(169, 8);
        var firstMovePos = bs.BitPosition();
        bs.WriteInt(0, 32);
        for (var i = 0; i < 4; ++i)
        {
            bs.WriteFlag(true);
            if (i != 0 && bs.WriteFlag(i == 2))
            {
                // same as previous: no motion fields
            }
            else
            {
                bs.WriteInt(0x1234 + (uint)i, 16);
                bs.WriteInt((uint)(i == 3 ? 2 : 5), 3); // jet, jump, crouch
            }
            bs.WriteFlag(i != 1); // fire, with both edges
            if (bs.WriteFlag(i == 0))
                bs.WriteInt(7, 8);
            bs.WriteFlag(true);
            bs.WriteInt(i == 3 ? 0x7fc00000u : 0x3f000000u, 32); // pitch: 0.5, NaN
            if (bs.WriteFlag(i != 0))
                bs.WriteInt(0xc1200000u, 32); // turn: -10, clamped
        }
        bs.WriteFlag(false);
        var seed = new List<byte>(buf.AsSpan(0, bs.BytePosition()).ToArray());

        var calls = 0;
        var hooks = new GameConnection.Hooks
        {
            RemoteCall = (_, _) => ++calls
        };
        var services = new ObserverCamera.CameraServices
        {
            Evaluate = _ => ++calls
        };
        var classes = new ClassContext();
        var target = new ObserverCamera(classes, 0, new ObserverCamera.CameraServices());
        var camera = new ObserverCamera(classes, 2049, services);
        var controlled = false;
        hooks.ControlObject = () => controlled ? camera : null;
        var world = new Rng(77);
        hooks.PscInputFn = inp =>
        {
            inp.Team = (int)world.Below(10) - 1;
            for (var k = 0; k < inp.Visible.Length; ++k)
                inp.Visible[k] = world.Next();
            for (var k = 0; k < 7; ++k)
            {
                inp.Shopping[k] = world.Next();
                inp.Buying[k] = world.Next();
            }
            inp.LimitCommand = world.Below(2) != 0;
            inp.CommandSlot = (slot, output) =>
            {
                Assert.True(slot < 256);
                output.InRange = world.Below(2) != 0;
                output.X = world.Next();
                output.Y = world.Next();
                output.Team = world.Next();
                output.DbIndexBits = world.Below(9);
                return world.Below(3) != 0;
            };
        };
        var tr = new GameConnection.Transport { Connected = () => true, Send = (_, _, _, _) => true };
        // A packet the reader rejects ends the connection: the owner is told once, nothing more is read, and the next
        // packet goes to a new connection.
        int invalidReports = 0, opened = 0;
        hooks.InvalidPacket = () => ++invalidReports;
        GameConnection connPtr = null!;
        void Open()
        {
            connPtr = new GameConnection(2049, tr, hooks);
            ++opened;
        }
        Open();
        var r = new Rng(9);
        uint now = 0;
        var seedBytes = seed.ToArray();
        for (uint i = 0; i < 60000; ++i)
        {
            var conn = connPtr;
            if (r.Below(2) == 0)
            {
                var patch = new BitBuffer(seedBytes, seedBytes.Length);
                patch.SetBitPosition(firstMovePos);
                patch.WriteInt((uint)conn.MoveCount, 32);
                seed = [.. seedBytes];
            }
            var b = (r.Below(8) == 0 ? RandomBytes(r, 200) : Mutate(seed, r)).ToArray();
            var before = conn.MoveCount;
            var reportsBefore = invalidReports;
            conn.Receive(b);
            // At most 30 new moves per packet are taken (D13).
            Assert.True((uint)conn.MoveCount - (uint)before <= 30);
            // The owner hears of an invalid packet once; a rejected connection takes nothing more.
            Assert.True(invalidReports - reportsBefore <= 1 && (invalidReports == reportsBefore || conn.Invalid()));
            if (conn.Invalid())
            {
                var frozen = conn.MoveCount;
                conn.Receive(b);
                Assert.True(conn.MoveCount == frozen && invalidReports == reportsBefore + 1);
                Open();
                continue;
            }
            if (i % 16 == 0)
            {
                now += 100;
                controlled = r.Below(8) != 0;
                conn.GuiMode = r.Below(7);
                conn.InventoryReset = r.Below(16) == 0;
                float[] v = [r.Below(2000) - 1000.0f, r.Below(2000) - 1000.0f, r.Below(500)];
                float[] rot = [0.1f, 0, r.Below(628) / 100.0f];
                switch (r.Below(6))
                {
                    case 0:
                        camera.SetFlyMode(r.Below(2) != 0, r.Below(2) != 0, rot, v);
                        break;
                    case 1:
                        camera.SetOrbit(target, v, r.Below(20) - 5.0f, 1, 12, rot);
                        break;
                    case 2:
                        camera.SetOrbit(null, v, 5, 2, 9, rot);
                        break;
                    case 3:
                        camera.ObjectDeleted(target);
                        break;
                    default:
                        break;
                }
                connPtr.Tick(now);
                connPtr.Notify((i / 16) + 1, r.Below(4) != 0); // random delivery reports, some for unknown cookies
            }
        }
        _ = calls;
        Assert.True(invalidReports > 100 && opened == invalidReports + 1); // the mutations reach every rejection
    }

    private static readonly uint[] Tags =
    [
        0x408, 0x40b, 0x408, 0x40b, 0x408, 0x40b, 0x401, 0x40f, 0x421, 0x423, 0x425, 0x45c, 0x44b, 0x453, 0x462, 0x400, 0x402,
        0x40a, 0x40c, 0x47f,
    ];
    private static readonly uint[] Ids = [0, 1, 8, 2048, 2049, 2050, 0x7fffffff, 0x80000000u, 0xffffffffu];
    private static readonly uint[] Index = [0, 1, 511, 1022, 1023];

    // Client events (docs/game-stream.md 2.6): random event lists with random class tags, delivery classes and bodies.
    private static void FuzzClientEvents()
    {
        var r = new Rng(4242);
        var tr = new GameConnection.Transport { Connected = () => true, Send = (_, _, _, _) => true };
        int accepted = 0, rejected = 0, cutRuns = 0;
        for (uint round = 0; round < 20000; ++round)
        {
            int calls = 0, actions = 0, invalid = 0;
            var hooks = new GameConnection.Hooks
            {
                RemoteCall = (fn, args) =>
                {
                    Assert.True(fn.StartsWith("remote", StringComparison.Ordinal) && args.Count != 0 && args[0] == "2049");
                    ++calls;
                },
                ActionEvent = d =>
                    {
                        Assert.True(d.Kind <= EventDest.KindT.ClientGhost);
                        Assert.True(d.Kind == EventDest.KindT.ObjectId || d.Value < 1024);
                        ++actions;
                    },
                InvalidPacket = () => ++invalid
            };
            var conn = new GameConnection(2049, tr, hooks);

            var buf = new byte[1500];
            var bs = new BitBuffer(buf, buf.Length);
            bs.WriteFlag(false);
            bs.WriteFlag(false);
            bs.WriteFlag(true);
            int wantCalls = 0, wantActions = 0;
            int nextSeq = 0, expect = 0;
            var bad = false;
            var count = 1 + r.Below(12);
            var gap = r.Below(6) == 0;
            for (uint i = 0; i < count; ++i)
            {
                var tag = r.Below(3) == 0 && i + 1 == count ? Tags[r.Below((uint)Tags.Length)] : Tags[r.Below(6)];
                var kind = r.Below(3); // 0 ordered, 1 unordered, 2 unguaranteed
                bs.WriteFlag(true);
                bs.WriteFlag(kind != 2);
                var due = true;
                if (kind == 0)
                {
                    var seq = nextSeq++;
                    if (gap && seq >= 2)
                        seq += 3;
                    bs.WriteFlag(false);
                    bs.WriteFlag(true);
                    bs.WriteInt((uint)seq & 127, 7);
                    due = seq == expect;
                }
                else if (kind == 1)
                {
                    bs.WriteFlag(false);
                    bs.WriteFlag(false);
                }
                bs.WriteInt(tag - 0x400, 7);
                if (tag is not 0x408 and not 0x40b)
                {
                    bad = true;
                    for (var n = r.Below(64); n > 0; --n)
                        bs.WriteFlag(r.Below(2) != 0);
                    break;
                }
                if (due && kind == 0)
                    ++expect;
                if (tag == 0x408)
                {
                    var argc = 1 + r.Below(4);
                    bs.WriteInt(argc, 5);
                    for (uint k = 0; k < argc; ++k)
                    {
                        var len = r.Below(12);
                        var ch = (char)('a' + r.Below(26));
                        bs.WriteString(new string(ch, (int)len));
                    }
                    wantCalls += due ? 1 : 0;
                }
                else
                {
                    var form = r.Below(3);
                    if (bs.WriteFlag(form == 1) || bs.WriteFlag(form == 2))
                        bs.WriteInt(Index[r.Below(5)], 10);
                    else
                        bs.WriteInt(Ids[r.Below(9)], 32);
                    wantActions += due ? 1 : 0;
                }
            }
            bs.WriteFlag(false);
            bs.WriteFlag(false); // no PlayerPSC
            bs.WriteFlag(false); // no ghosts
            Assert.False(bs.Error());
            var whole = new List<byte>(buf.AsSpan(0, bs.BytePosition()).ToArray());

            if (r.Below(4) == 0)
            {
                // Cut or mutated: no outcome is claimed beyond one report at most and no dispatch after it.
                var b = r.Below(2) != 0 ? whole.GetRange(0, (int)r.Below((uint)whole.Count + 1)) : Mutate(whole, r);
                conn.Receive(b.ToArray());
                Assert.True(invalid <= 1 && invalid == 1 == conn.Invalid());
                int c0 = calls, a0 = actions;
                conn.Receive(whole.ToArray());
                Assert.True(invalid <= 1 && (!conn.Invalid() || invalid == 1));
                if (invalid == 1 && c0 + a0 == calls + actions)
                    ++cutRuns;
                continue;
            }
            conn.Receive(whole.ToArray());
            Assert.True(calls == wantCalls && actions == wantActions);
            Assert.True(conn.Invalid() == bad && invalid == (bad ? 1 : 0));
            if (bad)
                ++rejected;
            else
                ++accepted;
            // A rejected connection reads nothing more; an accepted one takes the next packet.
            conn.Receive(whole.ToArray());
            Assert.True(invalid == (bad ? 1 : 0));
            if (bad)
                Assert.True(calls == wantCalls && actions == wantActions);
        }
        Assert.True(accepted > 3000 && rejected > 2000 && cutRuns > 500);
    }

    // Remote console sessions (docs/server-ops.md 3): chunks split anywhere, long lines, NULs, wrong passwords, and the
    // ban list matcher on arbitrary addresses.
    private static void FuzzTelnet()
    {
        int evaluated = 0, sent = 0;
        var host = new TelnetSession.Host
        {
            Password = () => "pw",
            Prompt = () => "% ",
            Evaluate = line =>
                {
                    Assert.True(line.Length <= TelnetSession.LineMax);
                    ++evaluated;
                },
            Send = bytes => sent += bytes.Length
        };
        var text = "pw\r\necho(1);\r\n" + new string('x', 600) + "\nbad\0zz\r\n\b\b\x7fquit();\n";
        var seed = new List<byte>(ByteString.ToBytes(text));
        var r = new Rng(10);
        for (uint i = 0; i < 4000; ++i)
        {
            var session = new TelnetSession();
            var b = (r.Below(8) == 0 ? RandomBytes(r, 1500) : Mutate(seed, r)).ToArray();
            var at = 0;
            while (at < b.Length)
            {
                var n = 1 + (int)r.Below(64);
                if (n > b.Length - at)
                    n = b.Length - at;
                if (!session.Receive(b.AsSpan(at, n), host))
                    break;
                at += n;
            }
        }
        var bans = new BanList(() => 1000);
        for (uint i = 0; i < 20000; ++i)
        {
            byte[] a = [.. RandomBytes(r, 200)], q = [.. RandomBytes(r, 200)];
            string addr = ByteString.FromBytes(a), query = ByteString.FromBytes(q);
            if (r.Below(3) == 0)
            {
                var first = r.Below(300).ToString(CultureInfo.InvariantCulture);
                addr = "IP:" + first + "." + (r.Below(2) != 0 ? "*" : r.Below(300).ToString(CultureInfo.InvariantCulture));
            }
            switch (r.Below(4))
            {
                case 0:
                    bans.Add(addr, (int)r.Next());
                    break;
                case 1:
                    bans.AddAbsolute(addr, (int)r.Next());
                    break;
                case 2:
                    bans.Remove(query);
                    break;
                default:
                    bans.IsBanned(query);
                    break;
            }
            foreach (var e in bans.Entries())
                Assert.True(e.Address.Length <= BanList.MaxAddress);
            if (bans.Entries().Count > 2000)
                bans = new BanList(() => 1000);
            bans.ExportLines();
        }
        _ = evaluated;
        _ = sent;
    }

    // Moves of 40 slots from random bits, as the fuzz rounds of the Player and the Vehicle decode them.
    private static int RandomMoves(Rng r, Move[] moves, bool pitchToo)
    {
        var raw = new byte[64];
        for (var k = 0; k < raw.Length; ++k)
            raw[k] = (byte)r.Next();
        var bs = new BitBuffer(raw, raw.Length);
        var n = (int)r.Below(40);
        for (var i = 0; i < n; ++i)
        {
            if (bs.Error())
                bs = new BitBuffer(raw, raw.Length);
            moves[i] = new Move();
            if (i != 0)
                Moves.UnpackMove(bs, ref moves[i], true, in moves[i - 1]);
            else
                Moves.UnpackMove(bs, ref moves[i], true, false);
            moves[i].UseItem = -1; // remoteUseItem is script, not under test here
            if (r.Below(50) == 0)
            {
                moves[i].Forward = BitConverter.UInt32BitsToSingle(r.Next()); // raw floats past the reader: any bit pattern
                moves[i].Turn = BitConverter.UInt32BitsToSingle(r.Next());
                if (pitchToo)
                    moves[i].Pitch = BitConverter.UInt32BitsToSingle(r.Next());
            }
        }
        return n;
    }

    // Player as a control object (ROADMAP 7.2).
    private static void FuzzPlayer()
    {
        var root = Path.Combine(TestPaths.TmpDir(""), "fuzz_player");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        var t = new TerrainSpec
        {
            Scale = 5,
            Height = 2,
            Heights = [.. Enumerable.Repeat(50.0f, 25)],
            Squares = [.. new byte[32]]
        };
        PutFile(Path.Combine(root, "base/m.dtf"), DtfFile(t, 50, 50));
        PutFile(Path.Combine(root, "base/m#0.dtb"), DtbFile(t, 50, 50));
        PutFile(Path.Combine(root, "base/t.dml"), "dml");
        float[] box = [-8, -8, -8, 8, 8, 8];
        var dig = DigFile(0x1234, box, [new Node(0, -44, -45)], 1, 45, [[1, 0, 0, 0]]);
        PutFile(Path.Combine(root, "base/hut.dis"), DisFile("hut-00.dig", "hut-000.dil"));
        PutFile(Path.Combine(root, "base/hut-00.dig"), dig);
        PutFile(Path.Combine(root, "base/hut-000.dil"), DilFile(0x1234, [], [], ""));
        var w = new Fixtures.World(root);
        Numbers.EnterScriptFpuMode();
        w.Run("resetPlayerManager();" +
              "PlayerData tarmor { maxForwardSpeed = 11; maxBackwardSpeed = 10; maxSideSpeed = 10; mass = 9;" +
              " groundForce = 360; groundTraction = 3; jumpImpulse = 75; jetForce = 236; jetEnergyDrain = 0.75;" +
              " maxEnergy = 60; boxWidth = 0.5; boxDepth = 0.5; boxNormalHeight = 2.25; boxCrouchHeight = 1.75;" +
              " canCrouch = true; };" +
              "ItemData Kit { className = \"Tool\"; };" +
              "instant SimGroup \"MissionGroup\" { instant SimTerrain \"Terrain\" { tedFileName = \"m.dtf\";" +
              " contGravity = \"0 0 -20\"; };" +
              " instant InteriorShape \"Hut\" { fileName = \"hut.dis\"; position = \"100 100 52\"; }; };" +
              "instant SimGroup \"MissionCleanup\";");
        var id = w.Run("echo(spawnPlayer(tarmor, \"100 80 51\", \"0 0 0\"));");
        var p = Player.From(w.Object(id));
        Assert.NotNull(p);
        var r = new Rng(11);
        string[] hostile = ["1e30", "-1e30", "nan", "inf", "-inf", "1e-40", "999999999999", "abc", ""];
        string Pick() => hostile[r.Below(9)];
        var moves = new Move[40];
        for (uint round = 0; round < 4000; ++round)
        {
            var n = RandomMoves(r, moves, false);
            p!.ApplyMoves(moves, n);
            for (var k = r.Below(8); k > 0; --k)
                p.ServerTick(round * 32);
            switch (r.Below(40))
            {
                case 0:
                    w.Run("GameBase::setPosition(" + id + ", \"" + Pick() + " " + Pick() + " " + Pick() + "\");");
                    break;
                case 1:
                    w.Run("Player::applyImpulse(" + id + ", \"" + Pick() + " " + Pick() + " " + Pick() + "\");");
                    break;
                case 2:
                    w.Run("Player::setItemCount(" + id + ", " + Pick() + ", " + Pick() + "); Player::mountItem(" + id +
                          ", Kit, " + Pick() + "); Player::setAnimation(" + id + ", " + Pick() + ");" +
                          "Player::getMountedItem(" + id + ", " + Pick() + "); GameBase::setEnergy(" + id + ", " + Pick() + ");");
                    break;
                case 3:
                    // Back to a sane state near the interior.
                    w.Run("GameBase::setPosition(" + id + ", \"100 " + (85 + (int)r.Below(20)).ToString(CultureInfo.InvariantCulture) + " 52\");");
                    p.Velocity[0] = p.Velocity[1] = p.Velocity[2] = 0;
                    break;
                default:
                    break;
            }
            Assert.True(p.Credit <= 6 && p.TicksSinceJumpContact >= 0 && p.Forces().Count <= 2);
        }
        // Mutated shapes under the collision detail reader (docs/collision.md 6 T2.1).
        float[] shapeBox = [-1, -1, 0, 1, 1, 2];
        Fuzz([DtsCollisionFile(shapeBox)], 20000, 14, b =>
        {
            var boxes = new List<Float6>();
            Shape.ReadShapeCollisionBoxes(b, boxes);
            var output = new float[6];
            Shape.ReadShapeBox(b, output);
        });
        // Mutated shapes under the full model reader, the instance and the line test (docs/collision.md 6 T7, T2.5a).
        {
            var spec = new DtsSpec
            {
                Names = ["bounds", "root", "arm", "swing", "body"],
                Nodes = [new(0, -1, 0, 0, 0), new(1, 0, 0, 0, 0), new(2, 1, 1, 0, 1)],
                Sequences = [new(3, 1, 1.0f, 2)],
                Subs = [new(0, 2, 0), new(0, 2, 2)],
                Keys = [new(0.0f, 0, 0x8000), new(1.0f, 1, 0x8000), new(0.0f, 0, 0xd000), new(0.6f, 0, 0x4000)],
                Transforms =
                [
                    new([0, 0, 0, 32767], [0, 0, 0]), new([0, 0, 23170, 23170], [1, 0, 0]),
                ]
            };
            float[] fb = [-1, -1, -1, 1, 1, 1];
            spec.Objects = [new(4, 0, 0, 1, [0, 0, 0], 1, 1), new(4, 0, 0, 2, [0, 0, 1], 0, 0)];
            spec.Details = [new(1, 10.0f)];
            spec.Meshes = [DtsBoxMesh(fb)];
            var sr = new Rng(15);
            Fuzz([DtsModelFile(spec), DtsCollisionFile(shapeBox)], 20000, 15, b =>
            {
                var model = new ShapeModel();
                if (!ShapeModel.Parse(b, model))
                    return;
                var inst = new ShapeInstance();
                inst.Build(model);
                inst.SetOverride("ar", "root");
                inst.Threads =
                [
                    new((int)sr.Below(3) - 1, (sr.Below(1200) / 1000.0f) - 0.1f, (int)sr.Below(5) - 1),
                    new((int)sr.Below(2), sr.Below(1000) / 1000.0f, -1),
                ];
                inst.ViewMode = (byte)sr.Below(3);
                inst.Animate();
                float[] s = [-3, 0.2f, 0.1f], e = [3, 0.1f, sr.Below(4)];
                var sf = new ShapeSurface();
                inst.CastLine(s, e, sr.Below(2) != 0, null, ref sf);
                var obj = new TMat3();
                float[] pp = new float[3], nn = new float[3];
                Shape.ShapeLine(inst, obj, s, e, true, pp, nn);
                // Sphere test (T4) on any detail index, both modes.
                var contacts = new List<ShapeContact>();
                inst.CollideSphere((int)sr.Below(4) - 1, e, sr.Below(2) != 0, obj, sr.Below(30) / 10.0f, contacts);
                model.CollisionDetail();
            });
        }
        // Mutated geometry under the Player box test (docs/collision.md 5.2): bounded recursion and surface lists.
        float[] identity = [1, 0, 0, 0, 1, 0, 0, 0, 1], origin = [0, 0, 0];
        float[] bmin = [-0.5f, -0.5f, 0], bmax = [0.5f, 0.5f, 2.25f];
        var qr = new Rng(12);
        Fuzz([dig], 20000, 13, b =>
        {
            var geo = InteriorGeometry.Parse(b);
            if (geo == null)
                return;
            float[] at = [(int)qr.Below(20) - 10, (int)qr.Below(20) - 10, (int)qr.Below(20) - 10];
            var hit = new CollisionHit();
            Collision.CollideInteriorBox(geo, identity, origin, at, bmin, bmax, hit);
            geo.FindSurface((int)qr.Below(100), (int)qr.Below(8));
        });
    }

    // Vehicle as a control object (ROADMAP 7.5).
    private static void FuzzVehicle()
    {
        var root = Path.Combine(TestPaths.TmpDir(""), "fuzz_vehicle");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        var t = new TerrainSpec
        {
            Scale = 5,
            Width = 4,
            Height = 4,
            Heights = [.. Enumerable.Repeat(50.0f, 25)],
            Squares = [.. new byte[32]]
        };
        PutFile(Path.Combine(root, "base/m.dtf"), DtfFile(t, 50, 50));
        PutFile(Path.Combine(root, "base/m#0.dtb"), DtbFile(t, 50, 50));
        PutFile(Path.Combine(root, "base/t.dml"), "dml");
        float[] gen = [-1, -1.5f, 0, 1, 1.5f, 2];
        PutFile(Path.Combine(root, "base/gen.dts"), DtsCollisionFile(gen));
        var w = new Fixtures.World(root);
        Numbers.EnterScriptFpuMode();
        w.Run("resetPlayerManager();" +
              "PlayerData tarmor { maxForwardSpeed = 11; maxBackwardSpeed = 10; maxSideSpeed = 10; mass = 9;" +
              " groundForce = 360; groundTraction = 3; jumpImpulse = 75; jetForce = 236; jetEnergyDrain = 0.75;" +
              " maxEnergy = 60; boxWidth = 0.5; boxDepth = 0.5; boxNormalHeight = 2.25; boxCrouchHeight = 1.75;" +
              " canCrouch = true; };" +
              "FlierData Scout { shapeFile = \"gen\"; className = \"Vehicle\"; mass = 9.0; drag = 1.0; density = 1.2;" +
              " maxBank = 0.5; maxPitch = 0.5; maxSpeed = 50; minSpeed = -2; lift = 0.75; maxAlt = 25; maxVertical = 10;" +
              " maxDamage = 0.6; damageLevel = {1.0, 1.0}; maxEnergy = 100; accel = 0.4; groundDamageScale = 1.0;" +
              " reloadDelay = 2.0; ramDamage = 1.5; ramDamageType = -1; visibleDriver = true; driverPose = 22; };" +
              "instant SimGroup \"MissionGroup\" { instant SimTerrain \"Terrain\" { tedFileName = \"m.dtf\";" +
              " position = \"-256 -256 0\"; contGravity = \"0 0 -20\"; }; };" +
              "instant SimGroup \"MissionCleanup\";");
        var vid = w.Run("echo(newObject(\"\", flier, Scout, true));");
        var pid = w.Run("echo(spawnPlayer(tarmor, \"0 0 51\", \"0 0 0\"));");
        var v = Vehicle.From(w.Object(vid));
        var p = Player.From(w.Object(pid));
        Assert.True(v != null && p != null);
        var r = new Rng(21);
        string[] hostile = ["1e30", "-1e30", "nan", "inf", "-inf", "1e-40", "999999999999", "abc", "", "-1", "7", "2147483647"];
        string Pick() => hostile[r.Below(12)];
        w.Run("GameBase::setPosition(" + vid + ", \"0 0 60\");");
        v!.ControlClient = 2049;
        var moves = new Move[40];
        for (uint round = 0; round < 3000; ++round)
        {
            var n = RandomMoves(r, moves, true);
            v.ApplyMoves(moves, n);
            for (var k = r.Below(6); k > 0; --k)
            {
                v.ServerTick(round * 32);
                p!.ServerTick(round * 32);
            }
            var buf = new byte[512];
            var output = new BitBuffer(buf, buf.Length);
            var client = r.Below(2) != 0 ? 2049 : 2050;
            var mask = r.Next() | (r.Below(4) == 0 ? 0xffffffffu : 0);
            v.PackUpdate(output, client, mask, r.Below(4) == 0);
            var cs = new BitBuffer(buf, buf.Length);
            var ctx = new ControlContext();
            v.WriteControlState(cs, ctx);
            switch (r.Below(30))
            {
                case 0:
                    w.Run("GameBase::setPosition(" + vid + ", \"" + Pick() + " " + Pick() + " " + Pick() + "\");");
                    break;
                case 1:
                    w.Run("GameBase::setRotation(" + vid + ", \"" + Pick() + " " + Pick() + " " + Pick() + "\");");
                    break;
                case 2:
                    w.Run("Player::setMountObject(" + pid + ", " + vid + ", " + Pick() + "); Player::getMountObject(" + pid + ");" +
                          "Vehicle::getMountPoint(" + vid + ", " + Pick() + "); Vehicle::canMount(" + vid + ", " + pid + ");" +
                          "Vehicle::incPassengers(" + vid + "); Vehicle::decPassengers(" + vid + ");");
                    break;
                case 3:
                    w.Run("GameBase::applyDamage(" + vid + ", -1, " + Pick() + ", \"" + Pick() + " 0 0\", \"0 0 1\", \"0 0 0\", 8);");
                    break;
                case 4:
                    w.Run("Player::setMountObject(" + pid + ", -1, 0);");
                    break;
                case 5:
                    // Back to a sane state: the vehicle in the air above the terrain, undamaged.
                    w.Run("GameBase::setDamageLevel(" + vid + ", 0); GameBase::setPosition(" + vid + ", \"0 0 60\");");
                    v.Velocity[0] = v.Velocity[1] = v.Velocity[2] = 0;
                    v.SpeedF = v.SpeedT = v.SpeedL = v.SpeedS = 0;
                    break;
                default:
                    break;
            }
            Assert.True(v.Credit <= 6);
        }
    }

    [Fact]
    public void Main()
    {
        FuzzPlayer();
        FuzzVehicle();
        FuzzFiles();
        FuzzScript();
        FuzzMathCommands();
        FuzzBacklogCommands();
        FuzzServer();
        FuzzGameStream();
        FuzzClientEvents();
        FuzzTelnet();
    }
}
