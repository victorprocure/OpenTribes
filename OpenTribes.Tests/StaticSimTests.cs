// Port of tests/test_static_sim.cpp: engine behavior of the non-player mission objects (docs/static-sim.md): R250, the
// StaticBase tick, energy, repair, damage, power, Moveable motion, Item physics on a terrain, the SensorManager sweep,
// Trigger and Turret idle. Synthetic shapes, terrain and scripts. 97 asserts.
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Tests.Fixtures;

// std::map with operator[] (missing keys read the default and are inserted).
public sealed class DefMap<TK, TV> : Dictionary<TK, TV> where TK : notnull
{
    private readonly Func<TV> mMake;
    public DefMap(Func<TV>? make = null) => mMake = make ?? (() => default!);
    public new TV this[TK key]
    {
        get
        {
            if (!TryGetValue(key, out TV? v))
            {
                v = mMake();
                base[key] = v;
            }
            return v;
        }
        set => base[key] = value;
    }
}

public class StaticSimTests
{
    private sealed record SeqSpec(string Name, bool Cyclic, float Duration);

    // Synthetic TS::Shape v8 (docs/interiors.md 8.2): sequences, nodes, bounding sphere radius 1 at the origin and
    // box +-1.
    private static List<byte> ShapeFile(SeqSpec[] seqs)
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
        var nodeBytes = new List<byte>();
        var seqBytes = new List<byte>();
        var quatBytes = new List<byte>();
        PutU16(nodeBytes, (ushort)NameOf("bounds"));
        PutU16(nodeBytes, 0xffff);
        PutU16(nodeBytes, 0);
        PutU16(nodeBytes, 0);
        PutU16(nodeBytes, 0);
        foreach (SeqSpec s in seqs)
        {
            PutU32(seqBytes, (uint)NameOf(s.Name));
            PutU32(seqBytes, s.Cyclic ? 1u : 0u);
            PutF32(seqBytes, s.Duration);
            for (var i = 0; i < 5; ++i)
                PutU32(seqBytes, 0);
        }
        foreach (var c in new[] { 0.0f, 0.0f, 0.0f, 1.0f })
            PutU16(quatBytes, (ushort)(short)Math.Round(c * 32767.0f, MidpointRounding.AwayFromZero));
        for (var i = 0; i < 3; ++i)
            PutF32(quatBytes, 0);
        var body = new List<byte>();
        uint[] counts = [1, (uint)seqs.Length, 0, 0, 1, (uint)names.Count, 0, 0, 0, 0, 0];
        foreach (var c in counts)
            PutU32(body, c);
        PutF32(body, 1.0f);
        for (var i = 0; i < 3; ++i)
            PutF32(body, 0);
        foreach (var v in new[] { -1.0f, -1.0f, -1.0f, 1.0f, 1.0f, 1.0f })
            PutF32(body, v);
        body.AddRange(nodeBytes);
        body.AddRange(seqBytes);
        body.AddRange(quatBytes);
        foreach (var n in names)
        {
            var rec = new byte[24];
            var nb = ByteString.ToBytes(n);
            Array.Copy(nb, rec, nb.Length);
            body.AddRange(rec);
        }
        return Pers("TS::Shape", 8, body);
    }

    // PERS TS::MaterialList v4 with one material of type `type` using the table defaults.
    private static List<byte> MaterialListFile(int type)
    {
        var body = new List<byte>();
        PutU32(body, 1); // details
        PutU32(body, 1); // materials
        var entry = new List<byte>(new byte[0x40]);
        SetU32(entry, 0x30, (uint)type);
        SetU32(entry, 0x3c, 1); // useDefaults
        body.AddRange(entry);
        return Pers("TS::MaterialList", 4, body);
    }

    private Fixtures.World? gw;
    private readonly DefMap<int, uint> masks = [];
    private readonly DefMap<int, int> maskCount08 = [];
    private readonly DefMap<int, List<float>> maskTimes08 = new(() => []);
    private bool mStarted;

    // One 32 ms tick per call. The virtual clock starts 1 ms ahead so that the floating point clock stays clear of a tick
    // boundary (an exact boundary may fall a tick late).
    private void Tick(int n = 1)
    {
        for (var i = 0; i < n; ++i)
        {
            gw!.Sim.AdvanceRealTime(mStarted ? 32u : 33u);
            mStarted = true;
        }
    }

    private GameBase Gb(string path)
    {
        SimObject? o = gw!.Object(path);
        Assert.True(o is GameBase);
        return (GameBase)o!;
    }

    // Runs a script statement of the form `$v = newObject(...)` and returns the object in $v.
    private GameBase Obj(string statement)
    {
        gw!.Run(statement);
        var id = gw.Run("echo(" + statement[..statement.IndexOf(' ')] + ");");
        SimObject? o = gw.Sim.Server()!.FindById(Numbers.BorlandAtoi(id));
        Assert.True(o is GameBase);
        return (GameBase)o!;
    }

    private static bool Near(float a, float b, float tol = 1e-4f) => Math.Abs(a - b) <= tol;

    // ---- R250 (docs/static-sim.md 1.7) ----
    private static void TestR250()
    {
        var a = new R250(); // seed 0x8377
        var tab = a.Table();
        // Seed fill (0x005eb5d4): 32 words, 7 apart from word 3, form a staircase.
        Assert.NotEqual(0u, (tab[3] & 0x80000000u));
        Assert.True((tab[10] & 0x80000000u) == 0 && (tab[10] & 0x40000000u) != 0);
        Assert.Equal(1u, tab[220]);
        // Draw (0x005eb660): table[i] ^= table[(i + 103) mod 250], i advancing and wrapping at 250.
        var model = (uint[])tab.Clone();
        var b = new R250();
        for (var k = 0; k < 600; ++k)
        {
            var i = k % 250;
            model[i] ^= model[(i + 103) % 250];
            Assert.True(b.Next() == model[i]);
        }
        Assert.True(b.Index() == 600 % 250);
        // The stream repeats after a reseed with the same seed; another seed differs.
        var c = new R250();
        var d = new R250();
        d.Reseed(1);
        Assert.True(c.Next() == new R250().Next() && d.Next() != new R250().Next());
        // The unit draw is the draw times 2^-32.
        var e = new R250();
        Assert.True(e.Unit() == Fpu.Mul(new R250().Next(), 2.3283064365386963e-10));
    }

    [Fact]
    public void Main()
    {
        TestR250();
        var root = Path.Combine(TestPaths.TmpDir(""), "static_sim_root");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        PutFile(Path.Combine(root, "base/station.dts"), ShapeFile([new("ambient", true, 1.0f), new("power", true, 2.0f), new("act", false, 1.0f)]));
        PutFile(Path.Combine(root, "base/plain.dts"), ShapeFile([]));
        PutFile(Path.Combine(root, "base/gun.dts"), ShapeFile([new("fire", false, 1.0f), new("power", false, 0.06f)]));
        var t = new TerrainSpec
        {
            Heights = [.. Enumerable.Repeat(10.0f, 25)]
        };
        t.Squares = [.. new byte[t.N * t.N * 2]];
        PutFile(Path.Combine(root, "base/t.dtf"), DtfFile(t, 0, 2000));
        PutFile(Path.Combine(root, "base/t#0.dtb"), DtbFile(t, 0, 2000));
        PutFile(Path.Combine(root, "base/t.dml"), MaterialListFile(0));

        var w = new Fixtures.World(root);
        gw = w;
        StaticRandom.Reset();
        w.Game.MaskBits = (o, bits) =>
        {
            masks[o.Id()] |= bits;
            if ((bits & 0x08) != 0)
            {
                ++maskCount08[o.Id()];
                maskTimes08[o.Id()].Add(w.Sim.Server()!.Time());
            }
        };
        var c = new Cases();
        w.Run("$Server::teamName0 = \"A\"; $Server::teamName1 = \"B\"; resetPlayerManager();");
        w.Game.AddTeam(0);
        w.Game.AddTeam(1);
        w.Run("StaticShapeData Pad { shapeFile = \"station\"; maxEnergy = 10; maxDamage = 1.5; };" +
              "StaticShapeData Tgt { shapeFile = \"plain\"; visibleToSensor = true; };" +
              "SensorData Radar { shapeFile = \"plain\"; range = 50; };" +
              "SensorData NoSup { shapeFile = \"plain\"; range = 50; supressable = false; };" +
              "SensorData Quiet { shapeFile = \"plain\"; range = 50; pinger = false; };" +
              "SensorData Jammer { shapeFile = \"plain\"; range = 80; supression = true; };" +
              "MoveableData Lift { className = \"Elevator\"; shapeFile = \"plain\"; speed = 10; };" +
              "MoveableData Lift4 { shapeFile = \"plain\"; speed = 10; };" +
              "MoveableData Door { shapeFile = \"plain\"; speed = 10; displace = false; };" +
              "ItemData Ball { shapeFile = \"plain\"; mass = 2; friction = 0.5; elasticity = 0.5; };" +
              "MineData Mn { shapeFile = \"plain\"; };" +
              "TurretData Gun { shapeFile = \"plain\"; };" +
              "TurretData Gun2 { shapeFile = \"gun\"; };" +
              "MarkerData PathMarker; TriggerData Zone;");
        w.Run("function Pad::onCollision(%this, %o) { $col++; }" +
              "function Pad::onEndSequence(%this, %t) { $ended = $ended @ %t; $endTick = $ticks; }" +
              "function Pad::onDestroyed(%this) { $destroyed++; }" +
              "function Pad::onDisabled(%this) { $disabled++; }" +
              "function Pad::onEnabled(%this) { $enabled++; }" +
              "function Elevator::onWaypoint(%this, %i) { $log = $log @ \"W\" @ %i @ Moveable::getState(%this) @ \" \"; }" +
              "function Elevator::onLast(%this) { $log = $log @ \"L \"; }" +
              "function Elevator::onFirst(%this) { $log = $log @ \"F \"; }" +
              "function Elevator::onNewPath(%this) { $count = Moveable::getWaypointCount(%this); $newPath++; }" +
              "function Door::onNewPath(%this) { Moveable::setWaypoint(%this, 0); }" +
              "function Lift4::onWaypoint(%this, %i, %a, %b) { $k1++; }" +
              "function Zone::onActivate(%this) { $zlog = $zlog @ \"A\"; }" +
              "function Zone::onDeactivate(%this) { $zlog = $zlog @ \"D\"; }" +
              "function Zone::onEnter(%this, %o) { $zlog = $zlog @ \"E\"; }" +
              "function Zone::onContact(%this, %o) { $zlog = $zlog @ \"C\"; }" +
              "function Zone::onLeave(%this, %o) { $zlog = $zlog @ \"L\"; }" +
              "function Radar::onAdd(%this) { $radarAdds++; }" +
              "function Ball::onAdd(%this) { $ballCount = Item::getCount(%this); }");
        w.Run("instant SimTerrain \"Terrain\" { tedFileName = \"t.dtf\"; position = \"0 0 0\"; contGravity = \"0 0 -20\"; };" +
              "instant SimGroup \"MissionGroup\" {" +
              " instant StaticShape \"P\" { dataBlock = \"Pad\"; position = \"100 0 100\"; };" +
              " instant SimGroup \"lift\" {" +
              "  instant Moveable \"El\" { dataBlock = \"Lift\"; };" +
              "  instant SimPath \"Path\" { instant Marker \"m0\" { dataBlock = \"PathMarker\"; position = \"0 0 0\"; };" +
              "    instant Marker \"m1\" { dataBlock = \"PathMarker\"; position = \"5 0 0\"; };" +
              "    instant Marker \"m2\" { dataBlock = \"PathMarker\"; position = \"5 5 2\"; rotation = \"0 0 1\"; }; };" +
              " };" +
              " instant SimGroup \"gate\" {" +
              "  instant Moveable \"Dr\" { dataBlock = \"Door\"; };" +
              "  instant SimPath \"Path2\" { instant Marker \"d0\" { dataBlock = \"PathMarker\"; position = \"0 20 0\"; };" +
              "    instant Marker \"d1\" { dataBlock = \"PathMarker\"; position = \"0 20 4\"; }; };" +
              " };" +
              " instant SimGroup \"kgrp\" {" +
              "  instant Moveable \"K\" { dataBlock = \"Lift4\"; };" +
              "  instant SimPath \"Path3\" { instant Marker \"k0\" { dataBlock = \"PathMarker\"; position = \"0 40 0\"; };" +
              "    instant Marker \"k1\" { dataBlock = \"PathMarker\"; position = \"1 40 0\"; }; };" +
              " };" +
              " instant Moveable \"Lonely\" { dataBlock = \"Lift\"; };" +
              " instant Trigger \"Zn\" { dataBlock = \"Zone\"; position = \"500 500 500\"; boundingBox = \"-1 -1 -1 1 1 1\"; };" +
              " instant Turret \"Tu\" { dataBlock = \"Gun\"; position = \"300 300 300\"; };" +
              " instant Turret \"Tu2\" { dataBlock = \"Gun2\"; position = \"310 300 300\"; };" +
              "};");


        GameBase p = Gb("MissionGroup/P");
        const string P = "\"MissionGroup/P\"";
        static string Q(string src) => "echo(" + src + ");";
        c.Check(w, Q("GameBase::getEnergy(" + P + ")"), "10");  // energy = maxEnergy at the bind
        c.Check(w, "echo(GameBase::setEnergy(\"MissionGroup/P\", 50), GameBase::getEnergy(\"MissionGroup/P\")," +
                   " GameBase::setEnergy(\"MissionGroup/P\", -3), GameBase::getEnergy(\"MissionGroup/P\"));",
                "True10True0");  // clamped to [0, maxEnergy]
        masks.Clear();
        w.Run("GameBase::setRechargeRate(\"MissionGroup/P\", 2);");
        Assert.Equal(0x02u, masks[p.Id()]);
        Tick();
        Assert.True(Near(p.Energy, 0.06f));  // rate x 0.03 per tick, not 0.032
        Tick(1000);
        Assert.Equal(10.0f, p.Energy);
        w.Run("GameBase::setRechargeRate(\"MissionGroup/P\", 0);");
        // Repair: 0.1 per second x 0.03 per tick = 0.003; the rest in a last short step.
        w.Run("GameBase::setDamageLevel(\"MissionGroup/P\", 0.6); GameBase::repairDamage(\"MissionGroup/P\", 0.02);");
        Tick();
        Assert.True(Near(p.Damage, 0.597f, 5e-6f));
        Tick(5);
        Assert.True(Near(p.Damage, 0.582f, 5e-6f));
        Tick();
        Assert.True(Near(p.Damage, 0.58f, 5e-6f) && p.RepairPending == 0);
        Tick(10);
        Assert.True(Near(p.Damage, 0.58f, 5e-6f));
        c.Check(w, "echo(GameBase::repairDamage(\"MissionGroup/P\", -1), GameBase::repairDamage(\"MissionGroup/P\", abc));", "TrueTrue");
        Tick();
        Assert.True(Near(p.Damage, 0.58f, 5e-6f));  // nothing pending
        // Reference vector [live, SolarPanel]: level 0.125 with a repair of 0.0625 ends at
        // 0.0624999 (0x3d7ffff3). The unset repairRate is the constructor constant 0x3dcccccd
        // (0x00449ffc), not the default text parsed toward zero, which ends at 0.0624998.
        w.Run("GameBase::setDamageLevel(\"MissionGroup/P\", 0.125); GameBase::repairDamage(\"MissionGroup/P\", 0.0625);");
        Assert.Equal(0.1f, p.RepairRate);
        Tick(30);
        Assert.Equal(0, p.RepairPending);
        c.Check(w, "echo(GameBase::getDamageLevel(\"MissionGroup/P\"));", "0.0624999");
        w.Run("GameBase::setDamageLevel(\"MissionGroup/P\", 0.58);");
        // Auto repair takes rate x 0.032 per tick (an f64 constant).
        w.Run("GameBase::setAutoRepairRate(\"MissionGroup/P\", 0.5);");
        Tick();
        Assert.True(Near(p.Damage, 0.564f, 5e-6f));
        c.Check(w, Q("GameBase::getAutoRepairRate(" + P + ")"), "0.5");
        w.Run("GameBase::setAutoRepairRate(\"MissionGroup/P\", 0); GameBase::setDamageLevel(\"MissionGroup/P\", 0);");
        // State evaluation: the disabled threshold is maxDamage x damageLevel[0] = 0.75.
        w.Run("$disabled = 0; $enabled = 0; $destroyed = 0;");
        c.Check(w, "echo(GameBase::getDisabledDamage(\"MissionGroup/P\"), \" \", GameBase::setDamageLevel(\"MissionGroup/P\", 0.74)," +
                   " GameBase::getDamageState(\"MissionGroup/P\"), GameBase::setDamageLevel(\"MissionGroup/P\", 0.75)," +
                   " GameBase::getDamageState(\"MissionGroup/P\"));",
                "0.75 TrueEnabledTrueDisabled");
        c.Check(w, "echo($disabled, \"|\", $enabled);", "1|0");
        // setActive returns the active byte after the call: a Disabled object stays inactive.
        c.Check(w, "echo(GameBase::setActive(\"MissionGroup/P\", true), GameBase::isActive(\"MissionGroup/P\"));", "FalseFalse");
        w.Run("GameBase::setDamageLevel(\"MissionGroup/P\", 0);");
        c.Check(w, "echo(GameBase::setActive(\"MissionGroup/P\", true), GameBase::setActive(\"MissionGroup/P\", false)," +
                   " GameBase::isActive(\"MissionGroup/P\"));",
                "TrueFalseFalse");
        c.Check(w, "echo(GameBase::setDamageLevel(\"MissionGroup/P\", 5), GameBase::getDamageLevel(\"MissionGroup/P\")," +
                   " GameBase::getDamageState(\"MissionGroup/P\"), $destroyed);",
                "True1.5Destroyed1");  // destroyed: the level is clamped to maxDamage, one onDestroyed
        w.Run("GameBase::setDamageLevel(\"MissionGroup/P\", 0);");
        // Command texts (docs/static-sim.md 8.1).
        c.Check(w, "echo(GameBase::generatePower(1));", "wrong argcount to generatePower(object,bool)\nFalse");
        c.Check(w, "echo(GameBase::setActive(1));", "wrong argcount to GameBase::setActive(sensor,bool)\nFalse");
        c.Check(w, "echo(GameBase::getDamageLevel());", "wrong argcount to GameBase::getDamageLevel(object)\n0");
        c.Check(w, "echo(GameBase::setEnergy(nosuch, 1), GameBase::getEnergy(nosuch), GameBase::isPowered(nosuch));", "False0False");
        c.Check(w, "echo(GameBase::applyDamage(\"MissionGroup/P\", 0, 0.1, \"1 2\", \"0 0 0\", \"0 0 0\", 0));", "False");
        // 1.2 deleteOnDestroy: the object is deleted 0.5 s after it is destroyed.
        GameBase dd = Obj("$d = newObject(\"\", StaticShape, Pad, true);");
        var ddId = dd.Id();
        w.Run("GameBase::setDamageLevel($d, 5);");
        Tick(15);
        Assert.NotNull(w.Sim.Server()!.FindById(ddId));
        Tick(1);
        Assert.Null(w.Sim.Server()!.FindById(ddId));
        // 1.8 collision records: at most one onCollision per second per pair.
        GameBase ball = Obj("$i = newObject(\"\", Item, Ball, 3, false);");
        Assert.Equal(3, ball.ItemCount);
        c.Check(w, "echo($ballCount);", "0");  // Item::onAdd sees count 0: the count follows the datablock
        w.Run("$col = 0;");
        p.OnCollisionNotify(ball);
        p.OnCollisionNotify(ball);
        c.Check(w, "echo($col);", "1");
        Tick(31);
        p.OnCollisionNotify(ball);
        c.Check(w, "echo($col);", "1");
        Tick(1);
        p.OnCollisionNotify(ball);
        c.Check(w, "echo($col);", "2");
        // 1.7 threads: a non-cyclic sequence ends when its position has reached 1 at the start of
        // a tick; every tick advances by 0.03 x speed / duration. A fixed speed is set here.
        w.Run("$ended = \"\";");
        c.Check(w, "echo(GameBase::playSequence(\"MissionGroup/P\", 1, \"act\"));", "True");
        p.Threads[1].Speed = 1.0f;
        var ticks = 0;
        while (w.Run("echo($ended);").Length == 0 && ticks < 100)
        {
            Tick();
            ++ticks;
        }
        Assert.Equal(35, ticks);  // 34 ticks reach position 1.02 (clamped to 1), the 35th reports
        c.Check(w, "echo($ended);", "1");
        Assert.NotEqual(0, (p.Threads[1].Flags & GameBase.kThreadAtEnd));
        // Each apply draws once and sets a speed in [0.9, 1.1).
        var speeds = new DefMap<float, int>();
        for (var i = 0; i < 20; ++i)
        {
            w.Run(i % 2 != 0 ? "GameBase::playSequence(\"MissionGroup/P\", 1, \"power\");" : "GameBase::playSequence(\"MissionGroup/P\", 1, \"ambient\");");
            Assert.True(p.Threads[1].Speed is >= 0.9f and < 1.1f);
            ++speeds[p.Threads[1].Speed];
        }
        Assert.True(speeds.Count > 10);  // a new draw every time
        c.Check(w, "echo(GameBase::playSequence(\"MissionGroup/P\", 1, \"nosuch\"), GameBase::playSequence(\"MissionGroup/P\", 3, \"act\"));",
                "FalseFalse");

        // ---- 2 Moveable ----
        GameBase el = Gb("MissionGroup/lift/El");
        c.Check(w, "echo($newPath, \" \", $count);", "1 3");  // the point count is known inside onNewPath
        Assert.True(el.PathId == 0 && el.WaypointCount == 3 && el.MoveState == GameBase.kMoveStopped);
        Assert.Equal(0x6018u, masks[el.Id()]);  // 0x08 setWaypoint, 0x10 and 0x08 snap, 0x4008 state, 0x2000 path
        Assert.True(Gb("MissionGroup/gate/Dr").PathId == 1 && Gb("MissionGroup/kgrp/K").PathId == 2);  // 2.2 path ids
        GameBase lonely = Gb("MissionGroup/Lonely");
        Assert.Equal(0xff, lonely.PathId);  // no SimPath in its group: unbound
        c.Check(w, "echo(Moveable::getWaypointCount(\"MissionGroup/Lonely\"), Moveable::getPosition(\"MissionGroup/Lonely\")," +
                   " Moveable::getState(\"MissionGroup/Lonely\"), Moveable::moveToWaypoint(\"MissionGroup/Lonely\", 0)," +
                   " Moveable::moveForward(\"MissionGroup/Lonely\"), Moveable::getState(\"MissionGroup/Lonely\"));",
                "00UndefinedFalseTrueUndefined");  // moveForward needs an active object: ignored
        c.Check(w, "echo(Moveable::getState(\"MissionGroup/P\"), Moveable::stop(\"MissionGroup/P\"));", "FalseFalse");
        // The Moveable is not active: moveForward is ignored, moveToWaypoint is not.
        c.Check(w, "echo(Moveable::moveForward(\"MissionGroup/lift/El\"), Moveable::getState(\"MissionGroup/lift/El\"));", "TrueStop");
        masks.Clear();
        c.Check(w, "echo(Moveable::moveToWaypoint(\"MissionGroup/lift/El\", 5), Moveable::moveToWaypoint(\"MissionGroup/lift/El\", -1)," +
                   " Moveable::moveToWaypoint(\"MissionGroup/lift/El\", \"2abc\"), Moveable::getState(\"MissionGroup/lift/El\"));",
                "FalseFalseTrueMoveForward");
        Assert.True(masks[el.Id()] == 0x4008 && el.MoveTarget == 2);
        w.Run("$log = \"\";");
        Tick(8);  // t = 8 x 0.032 s on a 0.5 s segment (length 5, speed 10)
        c.Check(w, Q("Moveable::getPosition(\"MissionGroup/lift/El\")"), "0.511999");  // 8 x 0.032 / 0.5, printed truncated
        Assert.True(Near(el.Transform.P[0], 2.56f) && el.Transform.P[1] == 0);
        Tick(7);
        c.Check(w, "echo($log);", "");
        Tick(1);  // tick 16: t = 0.512 > 0.5: onWaypoint(1), the leftover 0.012 s runs on the next segment
        c.Check(w, "echo($log);", "W1MoveForward ");
        Assert.True(el.Waypoint == 1 && Near(el.WaypointTime, 0.012f, 1e-5f));
        // Segment 2 is sqrt(25 + 4) = 5.385 long: T = 0.5385 s, crossed when 0.012 + 0.032 m > T, m = 17.
        Tick(16);
        c.Check(w, "echo($log);", "W1MoveForward ");
        Tick(1);  // tick 33: waypoint 2 is the last: onWaypoint, onLast, then Stop
        c.Check(w, "echo($log, Moveable::getState(\"MissionGroup/lift/El\"));", "W1MoveForward W2MoveForward L Stop");
        Assert.True(el.Transform.P[0] == 5 && el.Transform.P[1] == 5 && el.Transform.P[2] == 2 && el.Rotation[2] == 1.0f);
        // Backward: onWaypoint fires in the first tick when leaving, onFirst on arrival.
        w.Run("$log = \"\";");
        c.Check(w, "echo(Moveable::moveToWaypoint(\"MissionGroup/lift/El\", 0), Moveable::getState(\"MissionGroup/lift/El\"));", "TrueMoveBackward");
        Tick();
        c.Check(w, "echo($log);", "W1MoveBackward ");
        Tick(33);
        c.Check(w, "echo($log, Moveable::getState(\"MissionGroup/lift/El\"), Moveable::getPosition(\"MissionGroup/lift/El\"));",
                "W1MoveBackward W0MoveBackward W0MoveBackward F Stop0");
        Assert.True(el.Transform.P[0] == 0 && el.Transform.P[2] == 0);
        // Stopping mid-segment keeps the position; a stop drops the leftover time (2.4).
        w.Run("Moveable::moveToWaypoint(\"MissionGroup/lift/El\", 2);");
        Tick(4);
        w.Run("GameBase::setActive(\"MissionGroup/lift/El\", true); Moveable::stop(\"MissionGroup/lift/El\");");
        c.Check(w, Q("Moveable::getState(\"MissionGroup/lift/El\")"), "Stop");
        var stopX = el.Transform.P[0];
        Tick(3);
        Assert.True(el.Transform.P[0] == stopX);
        // A blocked move retries every tick and flags 0x4000 on each change (2.5).
        GameBase dr = Gb("MissionGroup/gate/Dr");
        var free = false;
        dr.MoveableTestOverride = _ => free;
        masks.Clear();
        w.Run("Moveable::moveToWaypoint(\"MissionGroup/gate/Dr\", 1);");
        masks.Clear();
        Tick(3);
        Assert.True(dr.MoveBlocked && dr.Waypoint == 0 && dr.WaypointTime == 0 && dr.MoveState == GameBase.kMoveForward);
        Assert.True(masks[dr.Id()] == 0x4000 && dr.Transform.P[2] == 0);
        free = true;
        Tick();
        Assert.True(!dr.MoveBlocked && (masks[dr.Id()] & 0x4008) == 0x4008 && dr.Transform.P[2] > 0);
        // D11 / K1: an onWaypoint method with four parameters is not called.
        GameBase kk = Gb("MissionGroup/kgrp/K");
        w.Run("Moveable::moveToWaypoint(\"MissionGroup/kgrp/K\", 1);");
        Tick(10);
        c.Check(w, "echo($k1, \"|\", Moveable::getState(\"MissionGroup/kgrp/K\"));", "|Stop");
        Assert.Equal(1, kk.Waypoint);

        // ---- 5 Item on the terrain (flat at z = 10, gravity -20, sphere radius 1) ----
        GameBase b1 = Obj("$b = newObject(\"\", Item, Ball, 1, false);");
        w.Run("GameBase::setPosition($b, \"16 16 20\");");
        Tick(3);
        Assert.True(Near(b1.Transform.P[2], 19.90784f, 2e-4f) && Near(b1.Velocity[2], -1.92f, 1e-4f) && !b1.AtRest);
        // A moving Item resends its position once per second (mask 0x08).
        w.Run("GameBase::setPosition($b, \"16 16 1000\"); Item::setVelocity($b, \"0 0 0\");");
        maskCount08.Clear();
        maskTimes08.Clear();
        Tick(100);
        {
            List<float> times = maskTimes08[b1.Id()];
            Assert.True(times.Count is >= 3 and <= 4);
            for (var i = 1; i < times.Count; ++i)
                Assert.True(times[i] - times[i - 1] is > 1.0f and < 1.07f);  // 33 ticks of 0.032 s
        }
        // The response of a surface (0x00506f44) on a Default material (table 1.0, 1.0):
        // normal (0,0,1), v = (3,0,-6), friction 0.5, elasticity 0.5.
        {
            var def = new Material();
            b1.Velocity[0] = 3;
            b1.Velocity[1] = 0;
            b1.Velocity[2] = -6;
            float[] n = [0, 0, 1];
            Assert.True(b1.SurfaceResponse(n, def, 0x19d));
            // vn = -6, k = 6.0001, v.z = 0.0001; friction f = 6 x 0.5 = 3 against s = |v| = 3: the
            // sliding part is 3 x f/s, x ends at ~0; bounce n k 0.5 x 1 = 3.00005.
            Assert.True(Near(b1.Velocity[0], 0.0f, 1e-3f) && Near(b1.Velocity[2], 3.00005f, 1e-4f) && b1.ItemContact);
            b1.Velocity[2] = 1;  // moving away: ignored
            Assert.True(!b1.SurfaceResponse(n, def, 0x19d) && b1.Velocity[2] == 1);
        }
        // First tick of a placed Item (5.3, P13): the sphere overlaps the plane by 0.4 m. Gravity
        // gives v.z = -0.64, the response (friction 0.5, elasticity 0.5) turns it into
        // 0.5 x 0.6401 = 0.32005, and the item moves 0.32005 x 0.032 up and rests.
        GameBase b2 = Obj("$b2 = newObject(\"\", Item, Ball, 1, false);");
        w.Run("GameBase::setPosition($b2, \"24 24 10.6\"); Item::setVelocity($b2, \"0 0 0\");");
        masks.Clear();
        maskCount08.Clear();
        Tick();
        Assert.True(Near(b2.Transform.P[2], 10.61024f, 1e-4f) && b2.AtRest && Near(b2.Velocity[2], 0.32005f, 1e-4f));
        Assert.Equal(1, maskCount08[b2.Id()]);  // the periodic position resend
        Tick();
        Assert.Equal(2, maskCount08[b2.Id()]);  // once more when the at-rest flag changes
        Tick(10);
        Assert.True(maskCount08[b2.Id()] == 2 && b2.AtRest);
        // The rest velocity is kept (5.3): isAtRest is True with a velocity.
        c.Check(w, "echo(GameBase::isAtRest($b2), \" \", Item::getVelocity($b2));", "True 0 0 0.320049");
        // The matrix is rebuilt every step: a zero rotation reads back as 0 -0 -0 (X1/Q4).
        c.Check(w, Q("GameBase::getRotation($b2)"), "0 -0 -0");
        // testPosition (8.4): above the floor True, overlapping it False.
        c.Check(w, "echo(GameBase::testPosition($b2, \"24 24 20\"), GameBase::testPosition($b2, \"24 24 10.5\"));", "TrueFalse");
        // Item::hide leaves the container; showing a moving item keeps it moving (5.6).
        w.Run("Item::hide($b2, true);");
        Assert.True(w.Game.World.Find(b2) == null && !b2.AtRest && !b2.ItemVisible);
        w.Run("Item::hide($b2, false);");
        Assert.True(w.Game.World.Find(b2) != null && !b2.AtRest && b2.ItemVisible);
        w.Run("Item::setVelocity($b2, \"0 0 0\"); Item::hide($b2, true); Item::hide($b2, false);");
        Assert.True(b2.AtRest);  // zero velocity: at rest again
        c.Check(w, "echo(Item::getCount($b2), Item::isRotating($b2), Item::hide(nosuch, true));",
                "Item::hide: could not find item (nosuch)\n1FalseFalse");
        // A hidden Item has no container (no gravity, no box update) but still collides with the world
        // [live: a hidden flag thrown down bounces on the terrain, game types 4]. It must not fall
        // through the floor.
        w.Run("GameBase::setPosition($b2, \"24 24 14\"); Item::setVelocity($b2, \"0 0 -4\"); Item::hide($b2, true);");
        Tick(60);
        Assert.True(b2.Transform.P[2] > 9.5f && !b2.ItemVisible);
        w.Run("Item::hide($b2, false); Item::setVelocity($b2, \"0 0 0\");");
        // Throw (5.4): velocity = thrower velocity + direction x speed / mass, rotation z from the
        // generator, position at the thrower's box centre, masks 0x38.
        masks.Clear();
        c.Check(w, "echo(GameBase::throw($b2, \"MissionGroup/P\", 10, false));", "True");
        Assert.True(b2.Velocity[0] == 0 && b2.Velocity[1] == 40 && b2.Velocity[2] == 0);  // (0,8,0) x 10 / 2
        Assert.True(b2.Transform.P[0] == 100 && b2.Transform.P[2] == 100 && !b2.AtRest && b2.ItemThrower == p);
        Assert.True((masks[b2.Id()] & 0x38) == 0x38 && b2.Rotation[2] >= 0 && b2.Rotation[2] < 6.2832f);
        c.Check(w, "echo(GameBase::throw($b2, \"MissionGroup/P\", 10, true));", "True");
        Assert.True(b2.Velocity[0] >= -3.67f && b2.Velocity[0] <= 3.67f && b2.Velocity[2] >= 2.5f && b2.Velocity[2] <= 5.0f);
        c.Check(w, "echo(GameBase::throw(nosuch, $b2, 1, false));", "GameBase::throw: could not find item to throw\nFalse");
        c.Check(w, "echo(GameBase::throw($b2, 1, 2));", "wrong argcount to throwObject(item,object,vel,bool random)\nFalse");
        // The thrower is cleared 1 s after the throw.
        Tick(34);
        Assert.Null(b2.ItemThrower);
        // A rotating Item never moves or rotates (5.3).
        GameBase sp = Obj("$sp = newObject(\"\", Item, Ball, 1, false, true);");
        w.Run("GameBase::setPosition($sp, \"20 20 30\");");
        Tick(5);
        Assert.True(sp.ItemRotates && sp.Transform.P[2] == 30 && sp.Velocity[2] == 0);
        // newObject usage lines (5.2, 1.1): printed, then the failure line, and 0.
        c.Check(w, "echo(newObject(\"\", Item, Ball, 1));",
                "Item: <dataFile> <count> <bool pop> [bool rotate] [bool collide]\nnewObject for: Item failed arg list: Ball, 1\n0");
        c.Check(w, "echo(newObject(\"\", Mine));", "Mine: <dataFile>\nnewObject for: Mine failed arg list: \n0");
        c.Check(w, "echo(newObject(\"\", StaticShape, Pad, true, 5));",
                "StaticBase: <dataFile> [deleteOnDestroy]\nnewObject for: StaticShape failed arg list: Pad, true, 5\n0");
        c.Check(w, "echo(newObject(\"\", StaticShape, NoSuchBlock));", "newObject for: StaticShape failed arg list: NoSuchBlock\n0");
        GameBase mine = Obj("$mn = newObject(\"\", Mine, Mn);");
        Assert.True(mine.DeleteOnDestroy);  // forced by the Mine constructor

        // ---- 3 Sensors ----
        var sensors = (SensorManager)w.Sim.Server()!.FindById(720)!;
        GameBase s0 = Obj("$s0 = newObject(\"\", Sensor, Radar);");
        c.Check(w, "echo($radarAdds);", "1");  // Sensor::onAdd runs once for newObject (3.2)
        GameBase t1 = Obj("$t1 = newObject(\"\", StaticShape, Tgt);");
        GameBase t2 = Obj("$t2 = newObject(\"\", StaticShape, Tgt);");
        Assert.True(t1.TargetIndex == 0 && t2.TargetIndex == 1);  // lowest free slot (3.3)
        s0.SetTeam(0);
        t1.SetTeam(1);
        t2.SetTeam(1);
        w.Run("GameBase::setPosition($s0, \"0 0 50\"); GameBase::setPosition($t1, \"30 0 50\"); GameBase::setPosition($t2, \"60 0 50\");" +
              "GameBase::setActive($s0, true);");
        Tick(4);  // one target per tick: both swept twice
        // detected bit 0 by team 0's sensor, own bit 1; state 1 (pinged) in bits 24..
        Assert.True((t1.SensorWord & 0x3) == 0x3 && (t1.SensorWord >> 24) == 1);
        Assert.True((t2.SensorWord & 0x3) == 0x2 && (t2.SensorWord >> 24) == 0);  // 60 m: out of range 50
        // An inactive sensor detects nothing.
        w.Run("GameBase::setActive($s0, false);");
        Tick(4);
        Assert.Equal(0x2u, (t1.SensorWord & 0x3));
        w.Run("GameBase::setActive($s0, true);");
        // A jammer (supression) that detects the target puts it in state 2 and hides it from
        // supressable sensors (3.5 step 4, 5).
        GameBase jam = Obj("$jam = newObject(\"\", Sensor, Jammer);");
        jam.SetTeam(1);
        w.Run("GameBase::setPosition($jam, \"60 0 50\"); GameBase::setActive($jam, true);");
        Tick(4);
        Assert.True((t1.SensorWord >> 24) == 2 && (t1.SensorWord & 1) == 0);
        // A not supressable sensor is walked first and still sees it.
        GameBase ns = Obj("$ns = newObject(\"\", Sensor, NoSup);");
        ns.SetTeam(0);
        w.Run("GameBase::setPosition($ns, \"0 0 50\"); GameBase::setActive($ns, true);");
        Tick(4);
        // detected; its pinger overwrites the jammed state with 1 (the last writer wins, 3.5)
        Assert.True((t1.SensorWord & 1) != 0 && (t1.SensorWord >> 24) == 1);
        // List order within a team: not supressable first, then pingers before quiet ones.
        GameBase qt = Obj("$qt = newObject(\"\", Sensor, Quiet);");
        qt.SetTeam(0);
        Assert.True(sensors.TeamList(0).Count == 3 && sensors.TeamList(0)[0] == ns && sensors.TeamList(0)[1] == s0 &&
               sensors.TeamList(0)[2] == qt);
        Assert.True(sensors.Suppressors().Count == 1 && sensors.Suppressors()[0] == jam);
        // $SensorNetworkEnabled false clears the detected bits of every team (3.5 step 2).
        w.Run("$SensorNetworkEnabled = false;");
        Tick(4);
        Assert.True((t1.SensorWord & 0xff) == 0 && (t2.SensorWord & 0xff) == 0);
        w.Run("$SensorNetworkEnabled = true;");
        // Target slots: deleting frees the slot, the next target takes it.
        var freed = t1.TargetIndex;
        w.Run("deleteObject($t1);");
        Assert.Null(w.Game.Targets[freed]);
        GameBase t3 = Obj("$t3 = newObject(\"\", StaticShape, Tgt);");
        Assert.True(t3.TargetIndex == freed);
        c.Check(w, Q("getObjectByTargetIndex(" + Numbers.FormatInt(freed) + ")"), Numbers.FormatInt(t3.Id()));
        c.Check(w, "echo(getObjectByTargetIndex(127), getObjectByTargetIndex(-1), getObjectByTargetIndex(128), getObjectByTargetIndex());",
                "-1-1-1-1");
        // A Marker is visible to its own team without any sweep (Marker_setTeam).
        GameBase mk = Gb("MissionGroup/lift/Path/m0");
        Assert.True(PlayerManager.VisibleToTeam(mk, 0) && PlayerManager.VisibleToTeam(mk, 1));

        // ---- 6 Trigger ----
        GameBase zn = Gb("MissionGroup/Zn");
        c.Check(w, "echo($zlog);", "A");  // onActivate once at registration
        Assert.True(zn.TriggerActive);
        c.Check(w, "echo(GameBase::isActive(\"MissionGroup/Zn\"), GameBase::isPowered(\"MissionGroup/Zn\")," +
                   " GameBase::getDamageState(\"MissionGroup/Zn\"), GameBase::getDamageLevel(\"MissionGroup/Zn\"));",
                "TrueFalse00");
        w.Run("$zlog = \"\";");
        // An object whose type has no bit of 0x24400180 (an Item) is ignored.
        zn.TriggerCollide(b2);
        Assert.Equal(0, zn.Occupants.Count);
        // Enter, contact every 0.25 s while collisions keep arriving, leave 0.25 s after the last.
        // The Moveable El (type 0x400000) is the occupant.
        for (var k = 1; k <= 20; ++k)
        {
            Tick();
            zn.TriggerCollide(el);
            if (k == 1)
                Assert.Equal("E", w.Run("echo($zlog);"));
        }
        {
            // The timer runs from SimTimerSet 12, whose events are 0.03 s apart: the time a
            // handler sees is the event time, a little behind the tick (docs/static-sim.md 6.2).
            // Contacts come every 0.25 s while collisions arrive, onLeave 0.25 s after the last.
            var log = w.Run("echo($zlog);");
            Assert.True(log == "ECC" && zn.Occupants.Count == 1);
            var extra = 0;
            while (log[^1] != 'L' && extra < 15)
            {
                Tick();
                ++extra;
                log = w.Run("echo($zlog);");
            }
            Assert.True(extra is >= 7 and <= 9);
            Assert.True(log[0] == 'E' && log.Count(ch => ch == 'C') >= 2 && log[^1] == 'L');
        }
        Assert.Equal(0, zn.Occupants.Count);
        // setActive: returns True, a change with occupants sends onLeave (last first), onDeactivate.
        w.Run("$zlog = \"\";");
        zn.TriggerCollide(el);
        c.Check(w, "echo(GameBase::setActive(\"MissionGroup/Zn\", false), GameBase::setActive(\"MissionGroup/Zn\", false)," +
                   " $zlog, GameBase::isActive(\"MissionGroup/Zn\"), GameBase::setActive(\"MissionGroup/Zn\", true), $zlog);",
                "TrueTrueELDFalseTrueELDA");
        // A Trigger created by newObject runs no onActivate (the datablock arrives after the add).
        w.Run("$zlog = \"\"; $tz = newObject(\"\", Trigger, Zone);");
        c.Check(w, "echo($zlog);", "");



        // ---- 4 Turret idle ----
        GameBase tu = Gb("MissionGroup/Tu");
        w.Run("GameBase::setActive(\"MissionGroup/Tu\", true);"); // the search step needs an active, enabled turret
        masks.Clear();
        Tick(5);
        Assert.True(tu.TurretMode == 0 && (masks[tu.Id()] & 0x1e000) == 0); // nothing to do without a target
        tu.TurretMode = 3;
        tu.TurretSleep = 0;
        Tick(); // sleep time passed, state != off: deactivate; a shape without power sequences is off at once
        Assert.True(tu.TurretMode == 0 && (masks[tu.Id()] & 0x10000) != 0);
        // Activation with the power/fire sequences takes the power sequence length (0.06 s at dt 0.032: two steps), then
        // state 3; deactivation runs the thread back (4.8).
        GameBase tu2 = Gb("MissionGroup/Tu2");
        Assert.True(tu2.TurretHasPower && Near(tu2.TurretPowerDuration, 0.06f));
        masks.Clear();
        tu2.TurretActivate(0.032f);
        Assert.True(tu2.TurretMode == 2 && (masks[tu2.Id()] & 0x10000) != 0);
        tu2.TurretActivate(0.032f);
        Assert.Equal(2, tu2.TurretMode);
        tu2.TurretActivate(0.032f);
        Assert.True(tu2.TurretMode == 3 && tu2.TurretPowerPos == 1.0f);
        masks.Clear();
        tu2.TurretDeactivate(0.032f); // 3 -> 1: thread from 1 backwards
        Assert.True(tu2.TurretMode == 1 && (masks[tu2.Id()] & 0x12000) == 0x12000);
        tu2.TurretDeactivate(0.032f);
        Assert.True(tu2.TurretMode == 0 && tu2.TurretPowerPos == 0);
        // Deactivation first rotates home by speed x dt along the short way, snapping at 0.
        tu2.TurretMode = 1;
        tu2.TurretRotation = 1.0f;
        tu2.TurretDeactivate(0.032f);
        Assert.True(Near(tu2.TurretRotation, 0.968f, 1e-5f) && tu2.TurretMode == 1);
        tu2.TurretRotation = 0.02f;
        tu2.TurretDeactivate(0.032f);
        Assert.Equal(0, tu2.TurretRotation);

        // ---- materials (docs/collision.md 4.2, E6): the table written by setMaterialProperty ----
        {
            var list = new List<Material>();
            var v3 = new List<byte>();
            PutU32(v3, 1);
            PutU32(v3, 2);
            for (var i = 0; i < 2; ++i)
            {
                var e = new List<byte>(new byte[0x3c]);
                var type = i != 0 ? 4 : 13; // Glass, SoftEarth
                SetU32(e, 0x30, (uint)type);
                SetU32(e, 0x34, BitConverter.SingleToUInt32Bits(0.25f));
                SetU32(e, 0x38, BitConverter.SingleToUInt32Bits(0.75f));
                v3.AddRange(e);
            }
            Assert.True(MaterialList.ParseMaterialList([.. Pers("TS::MaterialList", 3, v3)], list) && list.Count == 2);
            Assert.True(list[0].Type == 13 && list[1].Type == 4 && list[0].UseDefaults); // v3: useDefaultProps is 1
            List<byte> bad = Pers("TS::MaterialList", 5, v3);
            Assert.False(MaterialList.ParseMaterialList([.. bad], list));
            c.Check(w, "echo(setMaterialProperty(Glass, 0.5, 1.6), setMaterialProperty(glass, 2, 3), setMaterialProperty(Lava, 1, 1)," +
                       " setMaterialProperty(Glass, 1));",
                    "TrueTrueFalseFalse");
            // The second call wins (type matched ASCII case-insensitively); defaults elsewhere.
            Assert.True(w.Game.Materials.FrictionOf(list[1]) == 2.0f && w.Game.Materials.ElasticityOf(list[1]) == 3.0f);
            Assert.Equal(1.0f, w.Game.Materials.FrictionOf(list[0]));
            list[0].UseDefaults = false; // own values: the table is bypassed
            Assert.True(w.Game.Materials.FrictionOf(list[0]) == 0.75f && w.Game.Materials.ElasticityOf(list[0]) == 0.25f);
        }
        Assert.Equal(0, c.Failures);

    }
}
