// Port of tests/test_gamebase_ghosts.cpp: ghost layouts and server state of the GameBase family
// (docs/game-stream-ghosts.md 4, 6.4): StaticShape, Sensor, Turret, Item, Mine, Moveable, Marker, Trigger. Objects are
// created by mission script with synthetic shapes; every update is packed and read back field by field. 157 asserts.
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Net;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Tests.Fixtures;

public class GamebaseGhostsTests
{
    private readonly GhostManager _gm = new(2049);
    private byte[] _buf = new byte[4096];
    private uint _packedBits;
    private readonly Dictionary<SimObject, ClientGhost> _clients = new(ReferenceEqualityComparer.Instance);
    private ClientGhost? _client;

    private BitBuffer Pack(SimObject? obj, uint mask, uint tag, int client = 2049)
    {
        Assert.NotNull(obj);
        var cls = GhostManager.GhostClassOf(obj!);
        Assert.True(cls != null && cls.Tag == tag); // 2.3 tags
        _buf = new byte[4096];
        var w = new BitBuffer(_buf, _buf.Length);
        var ctx = new GhostContext(_gm, client, mask == 0xffffffffu);
        Assert.Equal(0u, cls!.Pack!(obj!, ctx, w, mask));
        Assert.False(w.Error());
        _packedBits = w.BitPosition();
        // Client round trip (docs/client-net.md 3): the unpack of the same class consumes exactly the packed bits.
        var isNew = mask == 0xffffffffu;
        _clients.TryGetValue(obj!, out var prev);
        _client = _clients[obj!] = ClientRoundTrip.Unpack(tag, _buf, _packedBits, isNew, ClientRoundTrip.WorldFor(tag, obj), isNew ? null : prev);
        return new BitBuffer(_buf, (int)((w.BitPosition() + 7) / 8));
    }

    private static float F32(BitBuffer r) => r.ReadF32();

    private static void Vec(BitBuffer r, float x, float y, float z)
    {
        float a = r.ReadF32(), b = r.ReadF32(), c = r.ReadF32();
        Assert.True(a == x && b == y && c == z);
    }

    // ---- synthetic TS::Shape v8 (docs/interiors.md 8.2) ----
    private sealed record SeqSpec(string Name, bool Cyclic, float Duration);
    private sealed record NodeSpec(string Name, int Parent, int DefaultTransform, int Seq = -1, int Key0 = 0, int Key1 = 0);
    private readonly record struct Q(float X, float Y, float Z, float W);

    private static List<byte> ShapeFile(SeqSpec[] seqs, NodeSpec[] nodes, Q[] quats)
    {
        var names = new List<string>();
        int NameOf(string n)
        {
            var i = names.IndexOf(n);
            if (i >= 0)
                return i;
            names.Add(n);
            return names.Count - 1;
        }
        List<byte> nodeBytes = [], seqBytes = [], subBytes = [], keyBytes = [], quatBytes = [];
        int subs = 0, keys = 0;
        foreach (var n in nodes)
        {
            PutU16(nodeBytes, (ushort)NameOf(n.Name));
            PutU16(nodeBytes, (ushort)n.Parent);
            PutU16(nodeBytes, (ushort)(n.Seq >= 0 ? 1 : 0));
            PutU16(nodeBytes, (ushort)subs);
            PutU16(nodeBytes, (ushort)n.DefaultTransform);
            if (n.Seq >= 0)
            {
                PutU16(subBytes, (ushort)n.Seq);
                PutU16(subBytes, 2);
                PutU16(subBytes, (ushort)keys);
                PutF32(keyBytes, 0.0f);
                PutU32(keyBytes, (uint)n.Key0);
                PutF32(keyBytes, 1.0f);
                PutU32(keyBytes, (uint)n.Key1 | 0x10000000u); // flag bits above the index
                ++subs;
                keys += 2;
            }
        }
        foreach (var s in seqs)
        {
            PutU32(seqBytes, (uint)NameOf(s.Name));
            PutU32(seqBytes, s.Cyclic ? 1u : 0u);
            PutF32(seqBytes, s.Duration);
            for (var i = 0; i < 5; ++i)
                PutU32(seqBytes, 0);
        }
        foreach (var q in quats)
        {
            foreach (var c in new[] { q.X, q.Y, q.Z, q.W })
                PutU16(quatBytes, (ushort)(short)Math.Round(c * 32767.0f, MidpointRounding.AwayFromZero));
            for (var i = 0; i < 3; ++i)
                PutF32(quatBytes, 0);
        }
        var body = new List<byte>();
        uint[] counts = [(uint)nodes.Length, (uint)seqs.Length, (uint)subs, (uint)keys, (uint)quats.Length, (uint)names.Count, 0, 0, 0, 0, 0];
        foreach (var c in counts)
            PutU32(body, c);
        PutF32(body, 1.0f); // radius
        for (var i = 0; i < 3; ++i)
            PutF32(body, 0); // center
        foreach (var v in new[] { -1.0f, -1.0f, -1.0f, 1.0f, 1.0f, 1.0f })
            PutF32(body, v); // box
        foreach (var part in new[] { nodeBytes, seqBytes, subBytes, keyBytes, quatBytes })
            body.AddRange(part);
        foreach (var n in names)
        {
            var rec = new byte[24];
            var nb = ByteString.ToBytes(n);
            Array.Copy(nb, rec, nb.Length);
            body.AddRange(rec);
        }
        return Pers("TS::Shape", 8, body);
    }

    // GameBase part 4.1 of a mission object on team `team`.
    private static void ReadGameBase(BitBuffer r, uint team)
    {
        Assert.True(r.ReadFlag());
        Assert.True(r.ReadInt(4) == team);
        Assert.True(!r.ReadFlag() && !r.ReadFlag()); // no control client, no owner client
    }

    [Fact]
    public void Main()
    {
        var root = Path.Combine(TestPaths.TmpDir(""), "gamebase_root");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        // Station shape: power is cyclic. Turret shape: the node chain of a stock turret, whose elevate sequence tilts
        // "dummy turret" from -40 to +60 degrees between z rotations.
        const float h = 0.707083f;
        Q[] quats = [ new(0, 0, 0, 1), new(0, 0, -h, h), new(0, 0, h, h), new(-0.241829f, -0.241829f, 0.664449f, 0.664449f),
                      new(0.353526f, 0.353526f, 0.612354f, 0.612354f) ];
        PutFile(Path.Combine(root, "base/station.dts"), ShapeFile([new("ambient", true, 1.0f), new("Power", true, 2.0f)],
                                                               [new("bounds", -1, 0)], quats));
        PutFile(Path.Combine(root, "base/gun.dts"),
                ShapeFile([new("turn", false, 0.1f), new("elevate", false, 0.3f), new("fire", false, 1.0f), new("power", false, 0.06f)],
                          [ new("bounds", -1, 0), new("guns", 0, 1), new("move", 1, 1), new("dummy turret", 2, 2, 1, 3, 4),
                                           new("dummy muzzle", 3, 2) ],
                          quats));
        PutFile(Path.Combine(root, "base/plain.dts"), ShapeFile([], [new("bounds", -1, 0)], quats));

        // 4.2 shape data: sequence lookup is by name, ASCII case-insensitive (0x005b9798).
        {
            var res = new ResourceManager(root);
            res.SetSearchPath("base");
            var gun = Shape.GetShapeAnim(res, "gun");
            Assert.True(gun != null && gun.Sequences.Count == 4);
            Assert.True(gun!.FindSequence("POWER") == 3 && gun.FindSequence("elevate") == 1 && gun.FindSequence("none") == -1);
            Assert.True(!gun.Sequences[3].Cyclic && gun.Sequences[3].Duration == 0.06f);
            float lo = 0, hi = 0;
            Assert.True(gun.NodePitchRange("elevate", "dummy muzzle", ref lo, ref hi));
            Assert.True(Math.Abs(lo + 0.698f) < 0.001f && Math.Abs(hi - 1.047f) < 0.001f); // -40 and +60 degrees
            Assert.False(gun.NodePitchRange("elevate", "no node", ref lo, ref hi));
            var st = Shape.GetShapeAnim(res, "station");
            Assert.True(st != null && st.FindSequence("power") == 1 && st.Sequences[1].Cyclic);
            Assert.Null(Shape.GetShapeAnim(res, "missing"));
        }

        var w = new Fixtures.World(root);
        GhostClasses.Register();
        var masks = new DefMap<int, uint>(); // object id -> bits set since the last clear (6.4)
        w.Game.MaskBits = (o, bits) => masks[o.Id()] |= bits;
        w.Run("$Server::teamName0 = \"A\"; resetPlayerManager();");
        w.Run("StaticShapeData Gen { className = \"Generator\"; shapeFile = \"station\"; maxDamage = 2; visibleToSensor = true; };" +
              "StaticShapeData Pad { className = \"Station\"; shapeFile = \"station\"; };" +
              "StaticShapeData Rock { shapeFile = \"plain\"; };" +
              "SensorData Radar { shapeFile = \"station\"; };" +
              "TurretData Gun { shapeFile = \"gun\"; }; TurretData Stub { shapeFile = \"plain\"; };" +
              "ItemData Flag { shapeFile = \"plain\"; }; MineData Boom { shapeFile = \"plain\"; };" +
              "MoveableData Lift { className = \"Elevator\"; shapeFile = \"plain\"; };" +
              "MarkerData PathMarker; TriggerData Zone;");
        w.Run("function Generator::onEnabled(%this) { GameBase::setActive(%this, true); }" +
              "function Generator::onActivate(%this) { GameBase::playSequence(%this, 0, \"power\");" +
              "  GameBase::generatePower(%this, true); }" +
              "function Generator::onDeactivate(%this) { GameBase::stopSequence(%this, 0);" +
              "  GameBase::generatePower(%this, false); }" +
              "function StaticShape::onPower(%this, %power, %gen) { $log = $log @ \" P\" @ %this.tag @ %power;" +
              "  if (%power) GameBase::playSequence(%this, 0, \"power\"); else GameBase::stopSequence(%this, 0); }" +
              "function StaticShape::onEnabled(%this) { $log = $log @ \" E\" @ %this.tag;" +
              "  if (GameBase::isPowered(%this)) GameBase::playSequence(%this, 0, \"power\"); }" +
              "function StaticShape::onDisabled(%this) { $log = $log @ \" D\" @ %this.tag; }" +
              "function StaticShape::onDestroyed(%this) { $log = $log @ \" X\" @ %this.tag; }" +
              "function Turret::onAdd(%this) { $turretAdds++; }" +
              "function Turret::onEnabled(%this) { $turretEnables++; if (GameBase::isPowered(%this)) GameBase::setActive(%this, true); }" +
              "function Turret::onActivate(%this) { GameBase::playSequence(%this, 0, power); }" +
              "function Turret::onEndSequence(%this, %thread) { $ended = $ended @ %thread; }" +
              "function Elevator::onNewPath(%this) { $newPath = Moveable::getWaypointCount(%this);" +
              "  Moveable::setWaypoint(%this, 0); }");
        w.Run("instant SimGroup \"MissionGroup\" {" +
              " instant StaticShape \"Far\" { dataBlock = \"Pad\"; tag = \"far\"; };" +
              " instant TeamGroup \"x\" {" +
              "  instant StaticShape \"S1\" { dataBlock = \"Pad\"; tag = \"s1\"; position = \"1 2 3\"; rotation = \"0 -0 1.5\"; };" +
              "  instant StaticShape \"G\" { dataBlock = \"Gen\"; tag = \"g\"; };" +
              "  instant SimGroup \"sub\" { instant StaticShape \"S2\" { dataBlock = \"Pad\"; tag = \"s2\"; }; };" +
              "  instant StaticShape \"R\" { dataBlock = \"Rock\"; tag = \"r\"; };" +
              "  instant Sensor \"Sen\" { dataBlock = \"Radar\"; position = \"4 5 6\"; };" +
              "  instant Turret \"T\" { dataBlock = \"Gun\"; position = \"7 8 9\"; };" +
              "  instant Turret \"T2\" { dataBlock = \"Stub\"; };" +
              "  instant Item \"F\" { dataBlock = \"Flag\"; position = \"10 11 12\"; rotation = \"0 -0 -2.34512\"; };" +
              "  instant Item \"Spin\" { dataBlock = \"Flag\"; rotates = \"True\"; collideable = \"True\"; };" +
              "  instant Mine \"Mn\" { dataBlock = \"Boom\"; rotation = \"0 0 1\"; };" +
              "  instant SimGroup \"lift\" {" +
              "   instant Moveable \"El\" { dataBlock = \"Lift\"; };" +
              "   instant SimPath \"Path\" { instant Marker \"m0\" { dataBlock = \"PathMarker\"; position = \"1 1 1\"; };" +
              "     instant Marker \"m1\" { dataBlock = \"PathMarker\"; position = \"1 1 5\"; rotation = \"0 0 0.5\"; }; }; };" +
              "  instant Moveable \"Door\" { dataBlock = \"Lift\"; };" +
              "  instant Trigger \"Tr\" { dataBlock = \"Zone\"; position = \"1 2 3\"; boundingBox = \"-1 -2 -3 1 2 3\"; };" +
              " };" +
              "};");
        SimObject? Obj(string path) => w.Object("MissionGroup/" + path);
        GameBase Gb(string path)
        {
            var o = Obj(path);
            Assert.True(o is GameBase);
            return (GameBase)o!;
        }
        var c = new Cases();

        // 4.11 power at load. Every StaticBase reports onEnabled after onAdd (0x0044fe8c). The
        // generator's onActivate powers its group and the sub-groups: S1 is registered and gets
        // onPower, the generator itself too, S2 is not registered yet and only counts; Far is
        // outside the group.
        c.Check(w, "echo($log);", " Efar Es1 Ps1true Pgtrue Es2 Er");
        c.Check(w, "echo(GameBase::isPowered(\"MissionGroup/team0/S1\"), GameBase::isPowered(\"MissionGroup/team0/sub/S2\")," +
                   " GameBase::isPowered(\"MissionGroup/Far\"), GameBase::getPowerCount(\"MissionGroup/team0/G\")," +
                   " GameBase::isPowerGenerator(\"MissionGroup/team0/G\"), GameBase::isPowerGenerator(\"MissionGroup/team0/S1\")," +
                   " GameBase::isActive(\"MissionGroup/team0/G\"), GameBase::isActive(\"MissionGroup/team0/S1\"));",
                "TrueTrueFalse1TrueFalseTrueFalse");
        Assert.True(Gb("team0/Tr").PowerCount == 1 && Gb("team0/lift/El").PowerCount == 1);  // every GameBase counts
        // Turret onNewDataBlock (0x00452970) runs the onAdd hook once more than GameBase::onAdd.
        c.Check(w, "echo($turretAdds, \" \", $turretEnables);", "4 4");

        // 4.2 threads set by the scripts: powered shapes play "power" forward.
        var s1 = Gb("team0/S1");
        Assert.True(s1.Threads[0].Sequence == 1 && s1.Threads[0].Flags == GameBase.ThreadForward);
        Assert.True(s1.Threads[1].Sequence == -1 && Gb("Far").Threads[0].Sequence == -1);
        Assert.True(Gb("team0/sub/S2").Threads[0].Sequence == 1 && Gb("team0/G").Threads[0].Sequence == 1);
        Assert.True(Gb("team0/R").Threads[0].Sequence == -1);  // no such sequence: playSequence fails
        c.Check(w, "echo(GameBase::playSequence(\"MissionGroup/team0/R\", 0, \"power\")," +
                   " GameBase::playSequence(\"MissionGroup/team0/S1\", 3, \"power\")," +
                   " GameBase::playSequence(\"MissionGroup/team0/S1\", 1)," +
                   " GameBase::playSequence(\"MissionGroup/team0/S1\", 0, \"power\"));",
                "FalseFalseFalseTrue");

        // 4.3 StaticShape creation update (mask 0xffffffff).
        {
            var r = Pack(s1, 0xffffffffu, 0x221);
            // client-net.md 3.2, 3.3 StaticShape: transform, damage, datablock 1.
            Assert.True(ClientRoundTrip.VecIs(_client!.State["pos"], 1, 2, 3) && ClientRoundTrip.VecIs(_client.State["rot"], 0, 0, 1.5));
            Assert.True((long)_client.State["team"]! == 0 && (long)_client.State["damageState"]! == 1 && (long)_client.State["datablock"]! == 1);
            Assert.Equal(1, ((List<object?>)_client.State["threads"]!).Count);
            ReadGameBase(r, 0);
            Assert.False(r.ReadFlag());  // 0x80 flash: never in a full update
            Assert.True(r.ReadFlag());   // 0x400 forced: a thread is set
            Assert.True(r.ReadFlag() && r.ReadInt(4) == 1 && r.ReadInt(2) == 0 && r.ReadFlag() && !r.ReadFlag());
            Assert.True(!r.ReadFlag() && !r.ReadFlag());  // threads 1 and 2
            Assert.False(r.ReadFlag());                   // fade
            Assert.True(r.ReadFlag());
            Vec(r, 1, 2, 3);
            Vec(r, 0, -0.0f, 1.5f);  // rotation as given, not re-derived from the matrix
            Assert.True(r.ReadFlag() && r.ReadFlag() && r.ReadInt(6) == 0);  // enabled, damage 0
            Assert.True(r.ReadFlag() && !r.ReadFlag());                      // isTarget
            Assert.True(r.ReadFlag() && r.ReadInt(3) == 1);                  // datablock 1 (width: below)
            Assert.False(r.ReadFlag());                                      // no sensor target
            Assert.True(r.BitPosition() == _packedBits);
        }
        // 2.3: the datablock index uses bitLength(count + 1) bits: 3 StaticShapeData give 3 bits.
        {
            var r = Pack(Gb("team0/G"), 0x02, 0x221);
            Assert.True((long)_client!.State["datablock"]! == 0 && (long)_client.State["target"]! == 0 && (long)_client.State["isTarget"]! == 0);
            Assert.True(!r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag());
            Assert.True(r.ReadFlag() && !r.ReadFlag());       // 0x02 isTarget
            Assert.True(r.ReadFlag() && r.ReadInt(3) == 0);   // datablock 0
            Assert.True(r.ReadFlag() && r.ReadInt(7) == 0);   // sensor target index (visibleToSensor)
            Assert.True(r.BitPosition() == _packedBits);
        }
        // 6.4 mask setters of the thread commands, and their effect on the update.
        {
            masks.Clear();
            c.Check(w, "echo(GameBase::pauseSequence(\"MissionGroup/team0/S1\", 0), GameBase::pauseSequence(\"MissionGroup/team0/S1\", 0)," +
                       " GameBase::pauseSequence(\"MissionGroup/team0/S1\", 1));",
                    "TrueFalseFalse");
            Assert.Equal(0xc00u, masks[s1.Id()]);  // 0x400 | 0x800 << 0
            var r = Pack(s1, 0xc00, 0x221);
            Assert.True(!r.ReadFlag() && !r.ReadFlag());  // GameBase, flash
            Assert.True(r.ReadFlag() && r.ReadFlag() && r.ReadInt(4) == 1 && r.ReadInt(2) == 2 && r.ReadFlag() && !r.ReadFlag());
            Assert.True(!r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag());  // threads 1, 2, fade
            Assert.True(!r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag());
            Assert.True(r.BitPosition() == _packedBits);
            // A thread whose slot bit is not in the mask is not sent.
            var q = Pack(s1, 0x400, 0x221);
            Assert.True(!q.ReadFlag() && !q.ReadFlag() && q.ReadFlag() && !q.ReadFlag() && !q.ReadFlag() && !q.ReadFlag());
            // Resume (playSequence without a name), direction, stop.
            c.Check(w, "echo(GameBase::playSequence(\"MissionGroup/team0/S1\", 0)," +
                       " GameBase::setSequenceDirection(\"MissionGroup/team0/S1\", 0, false)," +
                       " GameBase::setSequenceDirection(\"MissionGroup/team0/S1\", 1, false)," +
                       " GameBase::stopSequence(\"MissionGroup/team0/S1\", 0), GameBase::stopSequence(\"MissionGroup/team0/S1\", 0));",
                    "TrueTrueFalseTrueFalse");
            Assert.True(s1.Threads[0].Flags == GameBase.Stop);  // stopped, backward
        }
        // 4.2 flash (mask 0x80) and fade.
        {
            s1.FlashNormal[0] = 1.0f;
            s1.FlashNormal[1] = -1.0f;
            s1.FlashNormal[2] = -0.5f;
            s1.FlashAmount = 7.0f;  // clamped to 5: unit(8) of 1.0
            s1.FadeState = 2;
            var r = Pack(s1, 0x80, 0x221);
            Assert.True(ClientRoundTrip.Near(ClientRoundTrip.D(_client!.State["flashAmount"]), 5, 1e-9) && (long)_client.State["fade"]! == 2); // 3.2
            Assert.True(!r.ReadFlag() && r.ReadFlag());
            Assert.True(r.ReadInt(7) == 127 && r.ReadInt(7) == 0 && r.ReadFlag() && r.ReadInt(8) == 255);
            Assert.False(r.ReadFlag());                 // threads
            Assert.True(r.ReadFlag() && r.ReadFlag());  // fade state 2
            s1.FadeState = 0;
            s1.FlashAmount = 0;
        }
        // 4.2 damage states (0x0044f918): thresholds are damageLevel ratios of maxDamage; the
        // state change sets mask 0x100 and calls the script; the generator loses its power.
        {
            var g = Gb("team0/G");
            masks.Clear();
            w.Run("$log = \"\";");
            c.Check(w, "echo(GameBase::getDamageState(\"MissionGroup/team0/G\"), GameBase::setDamageLevel(\"MissionGroup/team0/G\", 1.25)," +
                       " GameBase::getDamageLevel(\"MissionGroup/team0/G\"), GameBase::getDamageState(\"MissionGroup/team0/G\"));",
                    "EnabledTrue1.25Disabled");
            Assert.True(g.DamageState == GameBase.Disabled && !g.Active && (masks[g.Id()] & 0x100) != 0);
            var r = Pack(g, 0x100, 0x221);
            for (var i = 0; i < 5; ++i)
                Assert.False(r.ReadFlag());
            Assert.True(r.ReadFlag() && !r.ReadFlag() && r.ReadFlag());  // not enabled, disabled
            Assert.Equal(39u, r.ReadInt(6));                             // 1.25 / 2 * 63, truncated
            w.Run("GameBase::setDamageLevel(\"MissionGroup/team0/G\", 1.5);");
            Assert.True(g.DamageState == GameBase.Destroyed && g.Damage == 2.0f);  // destroyed: damage = maxDamage
            var d = Pack(g, 0x100, 0x221);
            for (var i = 0; i < 5; ++i)
                Assert.False(d.ReadFlag());
            Assert.True(d.ReadFlag() && !d.ReadFlag() && !d.ReadFlag() && d.ReadInt(6) == 63);
            c.Check(w, "echo($log);", " Dg Xg");
            // The generator stays a generator until a script says otherwise (stock scripts do).
            c.Check(w, "GameBase::generatePower(\"MissionGroup/team0/G\", false); echo($log, \"|\"," +
                       " GameBase::isPowered(\"MissionGroup/team0/S1\"), GameBase::getPowerCount(\"MissionGroup/team0/sub/S2\"));",
                    " Dg Xg Ps1false Ps2false Prfalse|False0");
            Assert.True(Gb("team0/sub/S2").Threads[0].Flags == (GameBase.ThreadForward | GameBase.Stop));
        }
        // 4.3 setIsTarget (0x0045041c): flag +0x7d1, mask 0x02, set 725.
        {
            masks.Clear();
            c.Check(w, "echo(GameBase::setIsTarget(\"MissionGroup/team0/S1\", true));", "True");
            Assert.True(s1.IsTarget && masks[s1.Id()] == 0x02);
            var set725 = w.Sim.Server()!.FindById(725);
            if (set725?.AsSet() is SimSet s725)
            {
                var member = s725.Members().Any(m => m == s1);
                Assert.True(member);
            }
            var r = Pack(s1, 0x02, 0x221);
            for (var i = 0; i < 6; ++i)
                Assert.False(r.ReadFlag());
            Assert.True(r.ReadFlag() && r.ReadFlag());  // isTarget 1
        }
        // 6.4 setTeam (mask 0x04) and the transform setters. Both console commands arrive as event
        // 0x405, whose handler sets the rotation (0x10) and the position (0x08) ([live]: the update
        // after either command carries both fields).
        {
            masks.Clear();
            w.Run("GameBase::setTeam(\"MissionGroup/team0/S1\", 3); GameBase::setPosition(\"MissionGroup/team0/S1\", \"5 6 7\");");
            Assert.Equal(0x1cu, masks[s1.Id()]);
            masks.Clear();
            w.Run("GameBase::setRotation(\"MissionGroup/team0/S1\", \"0 0 2\");");
            Assert.Equal(0x18u, masks[s1.Id()]);
            var r = Pack(s1, 0x1c, 0x221);
            ReadGameBase(r, 3);
            Assert.True(!r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag());
            Assert.True(r.ReadFlag());
            Vec(r, 5, 6, 7);
            // The angles of the matrix round trip (0x0044c978): 2 comes back one unit lower.
            var tr0 = s1.Transform;
            float[] two = [0, 0, 2];
            var back = new float[3];
            MissionTransform.EulerToMatrix(two, ref tr0);
            MissionTransform.MatrixToEuler(tr0, back);
            Assert.True(back[2] is < 2.0f and > 1.999999f);
            Vec(r, 0, 0, back[2]);
        }
        // 4.3 Sensor: datablock under mask 0x01, no target field.
        {
            var r = Pack(Obj("team0/Sen"), 0xffffffffu, 0x229);
            Assert.True(ClientRoundTrip.VecIs(_client!.State["pos"], 4, 5, 6) && (long)_client.State["datablock"]! == 0); // 3.3 Sensor
            ReadGameBase(r, 0);
            Assert.True(!r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag());  // no script played a thread
            Assert.True(r.ReadFlag());
            Vec(r, 4, 5, 6);
            Vec(r, 0, 0, 0);
            Assert.True(r.ReadFlag() && r.ReadFlag() && r.ReadInt(6) == 0);
            Assert.True(r.ReadFlag() && !r.ReadFlag());
            Assert.True(r.ReadFlag() && r.ReadInt(2) == 0);  // 0x01 datablock; 1 SensorData: 2 bits
            Assert.True(r.BitPosition() == _packedBits);
        }
        // 4.5 Turret. The elevation limits come from the shape (0x00452970): Euler x of "dummy
        // muzzle" at both ends of "elevate", here -40 and +60 degrees, so elevation 0 is 0.4.
        var t = Gb("team0/T");
        {
            Assert.True(t.TurretRotation == 0.0f && t.TurretElevation == 0.0f && t.TurretMode == 0);
            Assert.True(t.TurretBarrelState == 1 && t.Active);
            var r = Pack(t, 0xffffffffu, 0x202);
            Assert.True(ClientRoundTrip.VecIs(_client!.State["pos"], 7, 8, 9) && _client.State.ContainsKey("rotation") && _client.State.ContainsKey("mode")); // 3.3 Turret
            Assert.False(r.ReadFlag());  // not owner-only
            ReadGameBase(r, 0);
            Assert.True(!r.ReadFlag() && r.ReadFlag());
            Assert.True(r.ReadFlag() && r.ReadInt(4) == 3 && r.ReadInt(2) == 0 && r.ReadFlag() && !r.ReadFlag());  // power playing
            Assert.True(!r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag());
            Assert.True(r.ReadFlag());
            Vec(r, 7, 8, 9);
            Vec(r, 0, 0, 0);
            Assert.True(r.ReadFlag() && r.ReadFlag() && r.ReadInt(6) == 0);
            Assert.True(r.ReadFlag() && !r.ReadFlag());
            Assert.True(r.ReadFlag() && r.ReadInt(2) == 0);    // datablock 0 of 2 TurretData
            Assert.True(r.ReadFlag() && r.ReadInt(8) == 0);    // 0x2000 rotation / 2 pi
            Assert.True(r.ReadFlag() && r.ReadInt(8) == 102);  // 0x4000 elevation between the limits
            Assert.True(r.ReadFlag() && r.ReadInt(2) == 0);    // 0x8000 fire state
            Assert.True(r.ReadFlag() && r.ReadInt(4) == 0);    // 0x10000 mode
            Assert.False(r.ReadFlag());                        // barrel state 2
            Assert.True(r.BitPosition() == _packedBits);
            // A shape without sequences keeps the constructor values: rotation pi, limits +-pi/4.
            var s = Pack(Gb("team0/T2"), 0x6000, 0x202);
            for (var i = 0; i < 8; ++i)
                Assert.False(s.ReadFlag());  // owner-only, GameBase, flash, threads, fade, 0x18, 0x100, 0x02
            Assert.False(s.ReadFlag());      // Sensor datablock
            Assert.True(s.ReadFlag() && s.ReadInt(8) == 127 && s.ReadFlag() && s.ReadInt(8) == 127);
            // The controlling client gets one flag unless the update has mask 0x01 or 0x04.
            t.ControlClient = 2049;
            Pack(t, 0x2000, 0x202);
            Assert.True(_packedBits == 1 && (_buf[0] & 1) != 0);
            Pack(t, 0x2004, 0x202);
            Assert.True(_packedBits > 1 && (_buf[0] & 1) == 0);
            Pack(t, 0x2000, 0x202, 2050);
            Assert.True(_packedBits > 1 && (_buf[0] & 1) == 0);
            t.ControlClient = 0;
        }
        // Server ticks (0x0044fd78): the non-cyclic power sequence (0.06 s) ends, which sets the
        // at-end flag without a mask bit and calls onEndSequence(id, thread). The Moveable tick
        // (0x0044388c) finds the SimPath beside it.
        var el = Gb("team0/lift/El");
        {
            var r = Pack(el, 0xffffffffu, 0x220);
            Assert.True((long)_client!.State["datablock"]! == 0 && !_client.State.ContainsKey("pos")); // 3.3 Moveable: no transform
            ReadGameBase(r, 0);
            Assert.True(!r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag());
            Assert.False(r.ReadFlag());  // no transform: the client derives it from the path
            Assert.True(r.ReadFlag() && r.ReadFlag() && r.ReadInt(6) == 0);
            Assert.True(r.ReadFlag() && !r.ReadFlag());
            Assert.Equal(0u, r.ReadInt(2));  // datablock on a new ghost, no flag
            Assert.True(!r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag());  // path 0xff: 0x2000, 0x08, 0x4000 dropped
            Assert.True(r.BitPosition() == _packedBits);
        }
        masks.Clear();
        w.Sim.AdvanceRealTime(1000);
        {
            Assert.True(t.Threads[0].Flags == (GameBase.ThreadForward | GameBase.ThreadAtEnd));
            Assert.Equal(0u, (masks[t.Id()] & 0xc00));
            c.Check(w, "echo($ended, \"|\", $newPath);", "0|2");  // the count is known inside onNewPath (2.3)
            var r = Pack(t, 0xc00, 0x202);
            Assert.True(!r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag() && r.ReadFlag());
            Assert.True(r.ReadFlag() && r.ReadInt(4) == 3 && r.ReadInt(2) == 0 && r.ReadFlag() && r.ReadFlag());
            // A cyclic sequence never ends.
            w.Run("GameBase::playSequence(\"MissionGroup/team0/S1\", 0, \"power\");");
            w.Sim.AdvanceRealTime(8000);
            Assert.True(s1.Threads[0].Flags == GameBase.ThreadForward);
        }
        // 4.6 Moveable with its path: id in SimPath order, stopped (state 3), target 15.
        {
            Assert.True(el.PathId == 0 && el.WaypointCount == 2 && el.MoveState == GameBase.MoveStopped);
            // 0x08 setWaypoint, 0x18 transform of waypoint 0, 0x4008 state, 0x2000 path
            Assert.Equal(0x6018u, masks[el.Id()]);
            Assert.True(el.Transform.P[0] == 1 && el.Transform.P[1] == 1 && el.Transform.P[2] == 1);
            var r = Pack(el, 0xffffffffu, 0x220);
            ReadGameBase(r, 0);
            for (var i = 0; i < 4; ++i)
                Assert.False(r.ReadFlag());
            Assert.True(r.ReadFlag() && r.ReadFlag() && r.ReadInt(6) == 0 && r.ReadFlag() && !r.ReadFlag());
            Assert.Equal(0u, r.ReadInt(2));
            Assert.True(r.ReadFlag() && r.ReadInt(8) == 0);                       // 0x2000 path id
            Assert.True(r.ReadFlag() && r.ReadInt(4) == 0 && F32(r) == 0.0f);     // 0x08 waypoint, time
            Assert.True(r.ReadFlag() && !r.ReadFlag() && r.ReadInt(4) == 15 && r.ReadInt(2) == 3);  // 0x4000
            Assert.True(r.BitPosition() == _packedBits);
            masks.Clear();
            c.Check(w, "echo(Moveable::moveToWaypoint(\"MissionGroup/team0/lift/El\", 5), Moveable::moveToWaypoint(\"MissionGroup/team0/lift/El\", 1)," +
                       " Moveable::getState(\"MissionGroup/team0/lift/El\"), Moveable::getWaypointCount(\"MissionGroup/team0/lift/El\")," +
                       " Moveable::getPosition(\"MissionGroup/team0/lift/El\"), Moveable::getState(\"MissionGroup/team0/Door\"));",
                    "FalseTrueMoveForward20Undefined");
            Assert.True(masks[el.Id()] == 0x4008 && el.MoveTarget == 1);
            var u = Pack(el, 0x4008, 0x220);
            for (var i = 0; i < 7; ++i)
                Assert.False(u.ReadFlag());  // GameBase, flash, threads, fade, 0x18, 0x100, 0x02
            Assert.False(u.ReadFlag());      // 0x2000
            Assert.True(u.ReadFlag() && u.ReadInt(4) == 0 && F32(u) == 0.0f);
            Assert.True(u.ReadFlag() && !u.ReadFlag() && u.ReadInt(4) == 1 && u.ReadInt(2) == 1);
            // A Moveable with no SimPath in its group keeps path 0xff.
            Assert.Equal(0xff, Gb("team0/Door").PathId);
        }
        // 4.4 Item: no transform through ShapeBase; rotation z as unit(8) of z / 2 pi (wraps for
        // negative angles); position; a placed item counts as at rest (no rest physics yet).
        var flag = Gb("team0/F");
        {
            var r = Pack(flag, 0xffffffffu, 0x22a);
            Assert.True((long)_client!.State["datablock"]! == 0 && ClientRoundTrip.Near(ClientRoundTrip.D(_client.State["rotZ"]), 161 / 255.0) &&
                        (long)_client.State["atRest"]! >= 0); // 3.3 Item: rotation z as a fraction of 2 pi
            ReadGameBase(r, 0);
            Assert.True(!r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag());
            Assert.False(r.ReadFlag());
            Assert.True(r.ReadFlag() && r.ReadFlag() && r.ReadInt(6) == 0 && r.ReadFlag() && !r.ReadFlag());
            Assert.True(r.ReadFlag() && r.ReadInt(2) == 0);  // datablock
            Assert.True(!r.ReadFlag() && !r.ReadFlag());     // sensor target, thrower ghost
            Assert.True(!r.ReadFlag() && !r.ReadFlag());     // rotates, collideable
            Assert.True(r.ReadFlag() && r.ReadInt(8) == 161);  // -2.34512 / 2 pi * 255 = -95 in 8 bits
            Assert.True(r.ReadFlag());
            Vec(r, 10, 11, 12);
            // 5.2: a server Item starts not at rest and nothing stops it outside a container (no
            // gravity, no contact), so the update carries the velocity.
            Assert.True(!r.ReadFlag() && r.ReadFlag());
            Vec(r, 0, 0, 0);
            Assert.True(r.BitPosition() == _packedBits);
            var s = Pack(Gb("team0/Spin"), 0x02, 0x22a);
            for (var i = 0; i < 6; ++i)
                Assert.False(s.ReadFlag());
            Assert.True(s.ReadFlag() && !s.ReadFlag());  // ShapeBase 0x02
            Assert.True(s.ReadFlag() && s.ReadInt(2) == 0 && !s.ReadFlag() && !s.ReadFlag());
            Assert.True(s.ReadFlag() && s.ReadFlag());            // rotates, collideable
            Assert.True(!s.ReadFlag() && !s.ReadFlag() && s.ReadFlag());  // 0x10, 0x08, at rest (rotating)
            // setPosition (0x0044c81c) clears the rest flag; then mask 0x20 sends the velocity. The
            // event handler sets the rotation first, so the mask is 0x18 ([live]: the Item update
            // after the command carries rotZ and the position).
            masks.Clear();
            w.Run("GameBase::setPosition(\"MissionGroup/team0/F\", \"1 1 1\");");
            Assert.True(masks[flag.Id()] == 0x18 && !flag.AtRest);
            c.Check(w, "echo(GameBase::isAtRest(\"MissionGroup/team0/F\"));", "False");
            flag.Velocity[2] = -3.0f;
            var v = Pack(flag, 0x28, 0x22a);
            for (var i = 0; i < 9; ++i)
                Assert.False(v.ReadFlag());  // through Item 0x02 and 0x10
            Assert.True(v.ReadFlag());
            Vec(v, 1, 1, 1);
            Assert.True(!v.ReadFlag() && v.ReadFlag());
            Vec(v, 0, 0, -3);
        }
        // Mine (0x004590a0): Item layout; the rotation is not sent while the mine is at rest.
        {
            var mine = Gb("team0/Mn");
            // Mine_ctor 0x004588f8 sets +0x889 (collideable) and +0x7e4; a mine of the live capture (Siege, newObject)
            // sends the collideable flag set (ROADMAP G12).
            Assert.True(mine.ItemCollideable && mine.DeleteOnDestroy);
            mine.AtRest = true;
            var r = Pack(mine, 0x10, 0x21e);
            for (var i = 0; i < 10; ++i)
                Assert.False(r.ReadFlag());  // ..., Item 0x02, 0x10 dropped, 0x08
            Assert.True(r.ReadFlag() && r.BitPosition() == _packedBits);
            mine.AtRest = false;
            var m = Pack(mine, 0x10, 0x21e);
            for (var i = 0; i < 8; ++i)
                Assert.False(m.ReadFlag());
            Assert.True(m.ReadFlag() && m.ReadInt(8) == 40);  // 1 / 2 pi * 255
        }
        // 4.9 Marker: team -1 (Marker::onNewDataBlock), datablock, the persist position and
        // rotation. Trigger: raw transform (flags, 3 x 3, translation) and bounding box.
        {
            var r = Pack(Obj("team0/lift/Path/m1"), 0xffffffffu, 0x21b);
            Assert.True((long)_client!.State["team"]! == 15 && ClientRoundTrip.VecIs(_client.State["pos"], 1, 1, 5) && ClientRoundTrip.VecIs(_client.State["rot"], 0, 0, 0.5)); // 3.3 Marker
            ReadGameBase(r, 15);
            Assert.True(r.ReadFlag() && r.ReadInt(2) == 0);
            Assert.True(r.ReadFlag());
            Vec(r, 1, 1, 5);
            Vec(r, 0, 0, 0.5f);
            Assert.True(r.BitPosition() == _packedBits);
            var n = Pack(Obj("team0/lift/Path/m1"), 0x02, 0x21b);
            Assert.True(!n.ReadFlag() && !n.ReadFlag() && !n.ReadFlag() && n.BitPosition() == _packedBits);

            var tr = Pack(Obj("team0/Tr"), 0xffffffffu, 0x21f);
            Assert.True(((string)_client!.State["transform"]!).Length == 104 && ((double[])_client.State["box"]!)[3] == 1); // 3.3 Trigger
            Assert.True(tr.ReadFlag());
            var flags = tr.ReadInt(32);
            Assert.Equal(2u, flags);  // translation only
            Vec(tr, 1, 0, 0);
            Vec(tr, 0, 1, 0);
            Vec(tr, 0, 0, 1);
            Vec(tr, 1, 2, 3);
            Assert.True(tr.ReadFlag());
            Vec(tr, -1, -2, -3);
            Vec(tr, 1, 2, 3);
            Assert.True(tr.BitPosition() == _packedBits);
            var none = Pack(Obj("team0/Tr"), 0x04, 0x21f);
            Assert.True(!none.ReadFlag() && !none.ReadFlag() && _packedBits == 2);
        }
        Assert.Equal(0, c.Failures);
    }
}
