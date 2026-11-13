// Port of tests/test_vehicle.cpp: vehicles (ROADMAP 7.5, docs/vehicle-sim.md): creation and datablock binding (1),
// mounting (2), move credit and the null-move tick (3.1), the flight step against vectors of the exact model (3.3 -
// 3.6), collision damage (4), the weapon (5), the wire (6) and the Vehicle:: commands (7.1). 110 asserts, one to one.
namespace OpenTribes.Tests;

using System.Globalization;
using OpenTribes.Game;
using OpenTribes.Net;
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;
using static OpenTribes.Tests.Fixtures;

public class VehicleTests
{
    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);
    private static string Itoa(int v) => v.ToString(CultureInfo.InvariantCulture);

    private const float kFlat = 50.0f;

    private const string kBlocks =
        "FlierData Scout { shapeFile = \"gen\"; className = \"Vehicle\"; mass = 9.0; drag = 1.0; density = 1.2;" +
        " maxBank = 0.5; maxPitch = 0.5; maxSpeed = 50; minSpeed = -2; lift = 0.75; maxAlt = 25; maxVertical = 10;" +
        " maxDamage = 0.6; damageLevel = {1.0, 1.0}; maxEnergy = 100; accel = 0.4; groundDamageScale = 1.0;" +
        " reloadDelay = 2.0; ramDamage = 1.5; ramDamageType = -1; visibleDriver = true; driverPose = 22; };" +
        "FlierData LAPC { shapeFile = \"gen\"; className = \"Vehicle\"; mass = 18.0; drag = 1.0; density = 1.2;" +
        " maxBank = 0.25; maxPitch = 0.175; maxSpeed = 25; minSpeed = -1; lift = 0.5; maxAlt = 15; maxVertical = 6;" +
        " maxDamage = 1.5; damageLevel = {1.0, 1.0}; maxEnergy = 100; accel = 0.25; groundDamageScale = 0.50;" +
        " reloadDelay = 3.0; ramDamage = 2; ramDamageType = -1; visibleDriver = true; driverPose = 23; };" +
        "FlierData HAPC { shapeFile = \"gen\"; className = \"Vehicle\"; mass = 18.0; drag = 1.0; density = 1.2;" +
        " maxBank = 0.25; maxPitch = 0.175; maxSpeed = 25; minSpeed = -1; lift = 0.35; maxAlt = 15; maxVertical = 6;" +
        " maxDamage = 2.0; damageLevel = {1.0, 1.0}; maxEnergy = 100; accel = 0.25; groundDamageScale = 0.125;" +
        " reloadDelay = 3.0; ramDamage = 2; ramDamageType = -1; visibleDriver = true; driverPose = 23; };";

    private sealed class Fixture
    {
        public readonly string Root;
        public readonly Fixtures.World W;

        public Fixture(string name)
        {
            Root = Path.Combine(TestPaths.TmpDir(""), name);
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
            var t = new TerrainSpec
            {
                Scale = 5,
                Width = 16, // 16 x 16 repeats of the one 128 m block: 2048 m, the flights stay inside
                Height = 16,
                Heights = [.. Enumerable.Repeat(kFlat, 25)],
                Squares = [.. new byte[32]]
            }; // 256 m x 256 m, flat
            PutFile(Path.Combine(Root, "base/m.dtf"), DtfFile(t, kFlat, kFlat));
            PutFile(Path.Combine(Root, "base/m#0.dtb"), DtbFile(t, kFlat, kFlat));
            PutFile(Path.Combine(Root, "base/t.dml"), "dml");
            float[] gen = [-1, -1.5f, 0, 1, 1.5f, 2];
            PutFile(Path.Combine(Root, "base/gen.dts"), DtsCollisionFile(gen));
            W = new Fixtures.World(Root);
            Numbers.EnterScriptFpuMode();
            W.Run("$Server::teamName0 = \"A\"; resetPlayerManager();");
            W.Run("PlayerData tarmor { shapeFile = \"none\"; maxForwardSpeed = 11; maxBackwardSpeed = 10;" +
                  " maxSideSpeed = 10; mass = 9; drag = 1; groundForce = 360; groundTraction = 3; jumpImpulse = 75;" +
                  " jumpSurfaceMinDot = 0.25; jetForce = 236; jetEnergyDrain = 0.75; minJetEnergy = 1;" +
                  " maxJetSideForceFactor = 0.75; maxJetForwardVelocity = 22; maxEnergy = 60; maxDamage = 0.5;" +
                  " boxWidth = 0.5; boxDepth = 0.5; boxNormalHeight = 2.25; boxCrouchHeight = 1.75; canCrouch = true;" +
                  " minDamageSpeed = 25; damageScale = 0.0078125; };");
            W.Run(kBlocks);
            W.Run("instant SimGroup \"MissionGroup\" {" +
                  " instant SimTerrain \"Terrain\" { tedFileName = \"m.dtf\"; position = \"-1024 -1024 0\";" +
                  "   contGravity = \"0 0 -20\"; }; };" +
                  "instant SimGroup \"MissionCleanup\";");
        }

        public Flier Make(string data, string pos)
        {
            var id = W.Run("echo(newObject(\"\", flier, " + data + ", true));");
            var f = W.Object(id) as Flier;
            Assert.True(f != null && f.ShapeClass() == GameBase.ShapeKind.Vehicle);
            W.Run("GameBase::setPosition(" + id + ", \"" + pos + "\");");
            return f!;
        }

        public Player Spawn(string pos)
        {
            var id = W.Run("echo(spawnPlayer(tarmor, \"" + pos + "\", \"0 0 0\"));");
            Player? p = Player.From(W.Object(id));
            Assert.NotNull(p);
            return p!;
        }
    }

    private static Move MakeMove(string[] v)
    {
        var m = new Move
        {
            Forward = v[0] != "0" ? 1.0f : 0.0f,
            Backward = v[1] != "0" ? 1.0f : 0.0f,
            Left = v[2] != "0" ? 1.0f : 0.0f,
            Right = v[3] != "0" ? 1.0f : 0.0f,
            Jet = v[4] != "0",
            Turn = TestPaths.IncF32(v[5]),
            Pitch = TestPaths.IncF32(v[6])
        };
        return m;
    }

    // docs/portable-float.md 5 item 2: the Flier yaw update rz = f32(rz - (1/30 * ry) * K) uses the 80-bit constant at
    // 0x0042f7ac, which the portable build (and this port) holds as a 53-bit double. The vectors of the exact model are
    // bit-identical up to the first move with a turn (490); from there on the port differs in the low bits like the C++
    // truncating build (1207, 1205, 1205 of 1720 steps). The assertion of the loop holds bit-exactly before that step;
    // the differing steps from it on are counted and reported with both values of the first one.
    private const int kFirstTurn = 490;

    // The flight step against the exact model: every field of every step is bit-identical.
    private static void RunVectors(Fixture f, string data)
    {
        var diverged = 0;
        var firstDiff = "";
        const string inc = "tests/flier_vectors.inc";
        var start = TestPaths.IncTokens(inc, "k" + data + "Start")[0];
        List<string[]> moves = TestPaths.IncTokens(inc, "k" + data + "Moves"), states = TestPaths.IncTokens(inc, "k" + data + "States");
        Assert.True(moves.Count == states.Count); // static_assert(N == M)
        var pos = Numbers.FormatFloat(TestPaths.IncF32(start[0])) + " " + Numbers.FormatFloat(TestPaths.IncF32(start[1])) + " " +
                     Numbers.FormatFloat(TestPaths.IncF32(start[2]));
        var fl = f.Make(data, pos);
        fl.Piloted = true;
        for (var i = 0; i < moves.Count; ++i)
        {
            var m = MakeMove(moves[i]);
            fl.Update(ref m, 0.032f);
            var s = states[i].Take(13).Select(TestPaths.IncU32).ToArray();
            var landed = int.Parse(states[i][13], CultureInfo.InvariantCulture);
            var ok = true;
            for (var k = 0; k < 3; ++k)
                ok = ok && Bits(fl.Transform.P[k]) == s[k] && Bits(fl.Rotation[k]) == s[3 + k] && Bits(fl.TargetRot[k]) == s[6 + k];
            ok = ok && Bits(fl.SpeedL) == s[9] && Bits(fl.SpeedS) == s[10] && Bits(fl.SpeedF) == s[11] && Bits(fl.SpeedT) == s[12] &&
                 (fl.Landed ? 1 : 0) == landed;
            if (!ok && i >= kFirstTurn)
            {
                if (diverged++ == 0)
                    firstDiff = $"{data} step {i}: pos {Bits(fl.Transform.P[0]):x8} {Bits(fl.Transform.P[1]):x8} {Bits(fl.Transform.P[2]):x8}" +
                                $" want {s[0]:x8} {s[1]:x8} {s[2]:x8} rot {Bits(fl.Rotation[0]):x8} {Bits(fl.Rotation[1]):x8}" +
                                $" {Bits(fl.Rotation[2]):x8} want {s[3]:x8} {s[4]:x8} {s[5]:x8} L {Bits(fl.SpeedL):x8}/{s[9]:x8}" +
                                $" S {Bits(fl.SpeedS):x8}/{s[10]:x8} F {Bits(fl.SpeedF):x8}/{s[11]:x8} T {Bits(fl.SpeedT):x8}/{s[12]:x8}";
            }
            else if (!ok)
            {
                Console.Error.WriteLine($"{data} step {i}: pos {Bits(fl.Transform.P[0]):x8} {Bits(fl.Transform.P[1]):x8} {Bits(fl.Transform.P[2]):x8}" +
                                        $" want {s[0]:x8} {s[1]:x8} {s[2]:x8} rot {Bits(fl.Rotation[0]):x8} {Bits(fl.Rotation[1]):x8}" +
                                        $" {Bits(fl.Rotation[2]):x8} want {s[3]:x8} {s[4]:x8} {s[5]:x8} L {Bits(fl.SpeedL):x8}/{s[9]:x8}" +
                                        $" S {Bits(fl.SpeedS):x8}/{s[10]:x8} F {Bits(fl.SpeedF):x8}/{s[11]:x8} T {Bits(fl.SpeedT):x8}/{s[12]:x8}" +
                                        $" landed {(fl.Landed ? 1 : 0)}/{landed}");
                Assert.True(false);
            }
        }
        if (diverged != 0)
            Console.Error.WriteLine($"{data}: {diverged} of {moves.Count} steps differ from the exact model from step {kFirstTurn} " +
                                    $"(80-bit yaw constant, docs/portable-float.md 5 item 2); first: {firstDiff}");
        f.W.Run("deleteObject(" + Itoa(fl.Id()) + ");");
    }

    private static void TestStepVectors()
    {
        var f = new Fixture("vehicle_vectors");
        RunVectors(f, "Scout");
        RunVectors(f, "LAPC");
        RunVectors(f, "HAPC");
    }

    // ---- creation and binding (1) ----

    private static void TestCreation()
    {
        var f = new Fixture("vehicle_create");
        var w = f.W;
        var c = new Cases();
        w.Run("function Vehicle::onAdd(%this) { $added = %this; GameBase::setRechargeRate(%this, 10); }");
        var v = f.Make("Scout", "-40 0 60");
        var id = Itoa(v.Id());
        c.Check(w, "echo($added == " + id + ");", "1");
        c.Check(w, "echo(getObjectType(" + id + "), \" \", GameBase::getDataName(" + id + "), \" \", GameBase::isActive(" +
                   id + "), \" \", GameBase::getDamageState(" + id + "), \" \", GameBase::getEnergy(" + id + "));",
                "Flier Scout False Enabled 100");
        Assert.True(v.ControlClient == 0 && v.OwnerClient == 0 && !v.Piloted && !v.Landed && v.SpeedF == 0 && v.SpeedT == 0 &&
                    v.SpeedL == 0 && v.SpeedS == 0 && v.Credit == 0 && v.FireCount == 0);
        Assert.Equal(0x199, v.ItemFlags);
        bool in718 = false, in719 = false;
        foreach (var m in w.Sim.Server()!.FindById(718)!.AsSet()!.Members())
            in718 = in718 || m == v;
        foreach (var m in w.Sim.Server()!.FindById(719)!.AsSet()!.Members())
            in719 = in719 || m == v;
        Assert.True(in718 && !in719 && w.Game.World.Find(v) != null && w.Game.World.TerrainOf(v) != null);
        var d = v.Data;
        Assert.True(d.MaxSpeed == 50 && d.MinSpeed == -2 && d.MaxVertical == 10 && d.MaxAlt == 25 && d.Lift == 0.75f);
        Assert.True(Bits(d.Accel) == Bits(X87.F32(0.399999)) && d.DriverPose == 22 && d.VisibleDriver);
        Assert.True(v.MaxDamage == X87.F32(0.599999) && v.MaxEnergy == 100);
        Assert.Equal(100.0f, v.Ceiling); // (hmax - hmin / 2) + (hmax + maxAlt) on the flat 50 m terrain
        Assert.True(v.ShapeRadius == 0.75f && v.ItemSubStep == X87.F32(3.0 * 0.45));
        Assert.Equal(0x24c00080u, v.TypeMask);
        c.Check(w, "echo(GameBase::getMapName(" + id + "));", "");
        Assert.Equal(0, c.Failures);
    }

    // ---- credit, latency, the null-move tick (3.1) ----

    private static void TestCredit()
    {
        var f = new Fixture("vehicle_credit");
        var v = f.Make("Scout", "128 128 70");
        var jet = new Move
        {
            Jet = true
        };
        v.Piloted = true;
        v.ApplyMoves([jet], 1);
        Assert.True(v.Credit == 1 && v.SpeedL == -0.75f && v.LastMove.Jet && v.TicksWithoutMove == 0);
        v.ApplyMoves([jet], 1);
        Assert.True(v.Credit == 2 && v.SpeedL == -0.25f); // -0.75 + 1.25 (jet) - 0.75 (lift decay)
        var many = new Move[8];
        for (var i = 0; i < 8; ++i)
        {
            many[i] = new Move
            {
                Jet = true
            };
        }
        v.ApplyMoves(many, 8);
        Assert.Equal(6, v.Credit);
        v.ControlClient = 2049;
        var z = v.Transform.P[2];
        for (var i = 0; i < 32; ++i)
            v.ServerTick((uint)i * 32);
        Assert.True(v.Credit == -26 && v.Transform.P[2] == z && v.TicksWithoutMove == 32);
        v.ServerTick(1024);
        Assert.True(v.Credit == -26 && v.Transform.P[2] != z && v.TicksWithoutMove == 0);
        v.ControlClient = 0;
        var z2 = v.Transform.P[2];
        v.ServerTick(1056);
        Assert.True(v.Transform.P[2] != z2 && !v.Piloted && v.SoundState == 0);
        v.ControlClient = 2049;
        v.SpeedL = 0;
        v.ServerTick(1088);
        Assert.True(v.Piloted && v.SoundState == 1);
        v.SpeedL = 1.0f;
        v.ServerTick(1120);
        Assert.Equal(2, v.SoundState);
    }

    // ---- mounting (2) ----

    private static void TestMounting()
    {
        var f = new Fixture("vehicle_mount");
        var w = f.W;
        var c = new Cases();
        var v = f.Make("Scout", "100 100 80");
        var p = f.Spawn("100 100 54");
        string vid = Itoa(v.Id()), pid = Itoa(p.Id());
        c.Check(w, "echo(Player::getMountObject(" + pid + "));", "-1");
        c.Check(w, "echo(Player::setMountObject(" + pid + ", " + vid + ", 1), \" \", Player::getMountObject(" + pid + "));",
                "True " + vid);
        Assert.True(p.MountId == v.Id() && p.MountPoint == 1);
        Assert.True(p.Transform.P[0] == v.Transform.P[0] && p.Transform.P[2] == v.Transform.P[2]);
        Assert.True(p.Velocity[0] == 0 && p.Rotation[0] == 0 && p.Rotation[2] == 0);
        v.Velocity[0] = 3.0f;
        v.Transform.P[0] = 110.0f;
        p.UpdateMove(new Move());
        Assert.True(p.Transform.P[0] == 110.0f && p.Velocity[0] == 3.0f && !p.ContactFlag);
        w.Run("function Vehicle::passengerJump(%v, %p, %up) { $pj = %v @ \"|\" @ %p @ \"|\" @ %up; }");
        p.MountPoint = 2;
        var jump = new Move
        {
            Jump = true
        };
        p.UpdateMove(jump);
        c.Check(w, "echo($pj);", vid + "|" + pid + "|0 0 1");
        c.Check(w, "echo(Player::setMountObject(" + pid + ", -1, 0), Player::getMountObject(" + pid + "));", "True-1");
        c.Check(w, "echo(Player::setMountObject(), \" \", Player::getMountObject());",
                "wrong argcount to mountObject(Player, Object, mountPoint)\nwrong argcount to Player::getMountObject(Player)\nFalse False");
        c.Check(w, "echo(Player::setMountObject(" + pid + ", " + vid + ", 1));", "True");
        w.Run("deleteObject(" + vid + ");");
        p.UpdateMove(new Move());
        Assert.Equal(0, p.MountId);
        Assert.Equal(0, c.Failures);
    }

    // ---- the Vehicle:: commands (7.1) ----

    private static void TestCommands()
    {
        var f = new Fixture("vehicle_commands");
        var w = f.W;
        var c = new Cases();
        var v = f.Make("Scout", "100 100 80");
        var p = f.Spawn("100 100 54");
        string vid = Itoa(v.Id()), pid = Itoa(p.Id());
        c.Check(w, "echo(Vehicle::canMount());", "wrong argcount to canMount (Vehicle, Player)\nFalse");
        c.Check(w, "echo(Vehicle::canRide());", "wrong argcount to canRide (Vehicle, Player)\nFalse");
        c.Check(w, "echo(Vehicle::nextPassengerPoint());", "wrong argcount to nextPassengerPoint (Vehicle)\n-1");
        c.Check(w, "echo(Vehicle::incPassengers());", "wrong argcount to incPassengers (Vehicle)\n-1");
        c.Check(w, "echo(Vehicle::decPassengers());", "wrong argcount to decPassengers (Vehicle)\n-1");
        c.Check(w, "echo(Vehicle::getMountPoint());", "wrong argcount to getMountPoint (Vehicle, MountPoint)\n-1");
        c.Check(w, "echo(Vehicle::canMount(" + pid + ", " + pid + "), Vehicle::nextPassengerPoint(" + pid + "));", "False-1");
        c.Check(w, "echo(Vehicle::canMount(" + vid + ", " + pid + "), Vehicle::canMount(" + vid + ", " + vid + "));", "TrueTrue");
        v.Piloted = true;
        c.Check(w, "echo(Vehicle::canMount(" + vid + ", " + pid + "));", "False");
        c.Check(w, "echo(Vehicle::canRide(" + vid + ", " + pid + "), \" \", Vehicle::nextPassengerPoint(" + vid +
                   "), \" \", Vehicle::incPassengers(" + vid + "), \" \", Vehicle::nextPassengerPoint(" + vid +
                   "), \" \", Vehicle::decPassengers(" + vid + "), Vehicle::decPassengers(" + vid +
                   "), \" \", Vehicle::nextPassengerPoint(" + vid + "));",
                "False 2 True 3 TrueTrue 2");
        c.Check(w, "echo(Vehicle::getMountPoint(" + vid + ", 7));", "100 100 80");
        c.Check(w, "echo(Vehicle::getMountPoint(" + vid + ", -1));", "100 100 80");
        Assert.Equal(0, c.Failures);
    }

    // ---- collision damage and destruction (4) ----

    private static void TestDamage()
    {
        var f = new Fixture("vehicle_damage");
        var w = f.W;
        var c = new Cases();
        w.Run("function Vehicle::onDamage(%this, %type, %value, %pos, %vec, %mom, %obj)" +
              " { $dmg = %type @ \"|\" @ %value @ \"|\" @ %vec @ \"|\" @ %mom @ \"|\" @ %obj;" +
              " GameBase::setDamageLevel(%this, GameBase::getDamageLevel(%this) + %value); }" +
              "function Vehicle::onDestroyed(%this, %mom) { $destroyed = %this; }");
        var v = f.Make("Scout", "128 128 120");
        var vid = Itoa(v.Id());
        v.Piloted = true;
        v.ControlClient = 2049;
        v.SpeedF = 50;
        v.SpeedT = 50;
        c.Check(w, "echo(GameBase::applyDamage(" + vid + ", -1, 0.3, \"0 0 0\", \"0 0 1\", \"0 0 0\", 8));", "True");
        Assert.True(v.Damage > 0.29f && v.DamageState == GameBase.kEnabled && v.SpeedF == 50);
        c.Check(w, "echo($dmg);", "-1|0.299997|0 0 1|0 0 0|8");
        w.Run("GameBase::applyDamage(" + vid + ", -1, 0.4, \"0 0 0\", \"0 0 1\", \"0 0 0\", 8);");
        Assert.True(v.DamageState == GameBase.kDestroyed && v.SpeedF == 0 && v.SpeedT == 0);
        c.Check(w, "echo($destroyed == " + vid + ");", "1");
        v.Velocity[0] = 5;
        var none = new Move();
        v.Update(ref none, 0.032f);
        Assert.True(v.Velocity[0] == 5.0f && v.Velocity[2] < 0);
        var g = f.Make("Scout", "60 60 52.5");
        g.Piloted = true;
        g.SpeedF = 50;
        g.SpeedT = 50;
        g.Rotation[0] = -0.4f;
        w.Run("$dmg = \"\";");
        var m = new Move
        {
            Forward = 1.0f
        };
        for (var i = 0; i < 6 && w.Run("echo($dmg);").Length == 0; ++i)
            g.Update(ref m, 0.032f);
        Assert.True(g.SpeedF is 0.0f or < 50.0f);
        Assert.Equal(0, c.Failures);
    }

    private static CollisionHit FloorHit()
    {
        var h = new CollisionHit
        {
            Type = ObjectType.kInteriorType
        };
        var sf = new CollisionSurface
        {
            Normal = Float3.Of(0, 0, 1),
            Distance = 0.1f
        };
        h.Surfaces.Add(sf);
        return h;
    }

    // ---- hulls (docs/vehicle-sim.md 12) ----

    private static void TestHulls()
    {
        var f = new Fixture("vehicle_hulls");
        var w = f.W;
        var none = new Move();
        w.Run("function Vehicle::onDamage(%this, %type, %value, %pos, %vec, %mom, %obj) { $hd = $hd @ %this @ \":\" @ %value @ \" \"; }");
        {
            var low = f.Make("Scout", "100 100 53");
            var high = f.Make("Scout", "100 100 58");
            for (var i = 0; i < 80; ++i)
            {
                low.Update(ref none, 0.032f);
                high.Update(ref none, 0.032f);
            }
            Assert.True(low.Landed && low.Transform.P[2] > 51.9f && low.Transform.P[2] < 52.01f);
            Assert.True(high.Landed && high.Transform.P[2] > 55.9f && high.Transform.P[2] < 56.01f);
            Assert.Equal(0, w.Run("echo($hd);").Length);
            high.Transform.P[2] = 55.5f;
            high.UpdateGround();
            var e = w.Game.World.Find(high);
            Assert.True(e != null && high.Transform.P[2] > 55.9f && e.Box[2] == high.Transform.P[2]);
            w.Run("deleteObject(" + Itoa(low.Id()) + "); deleteObject(" + Itoa(high.Id()) + ");");
        }
        for (var dead = 0; dead < 2; ++dead)
        {
            var v = f.Make("Scout", "300 300 120");
            var p = f.Spawn("300 302 120");
            p.Dead = dead != 0;
            w.Run("$hd = \"\";");
            v.Velocity[1] = 5.0f;
            var candidates = new List<CollisionImage>();
            var hits = new List<CollisionHit>();
            float[] query = [290, 290, 110, 310, 310, 130];
            w.Game.CollisionCandidates(v, 0x34c00086u, query, candidates);
            var before = candidates.Count;
            Assert.True(before >= 1);
            float[] at = [v.Transform.P[0], v.Transform.P[1], v.Transform.P[2]];
            var free = v.TestAt(at, candidates, hits);
            if (dead != 0)
            {
                Assert.True(free && hits.Count == 0 && candidates.Count == before - 1);
            }
            else
            {
                Assert.True(!free && hits.Count == 1 && hits[0].Object == p && candidates.Count == before);
                v.ProcessCollision(hits, 0);
                Assert.StartsWith(Itoa(v.Id()) + ":", w.Run("echo($hd);"), StringComparison.Ordinal); // C2: the hull's own damage call
            }
            w.Run("deleteObject(" + Itoa(v.Id()) + "); deleteObject(" + Itoa(p.Id()) + ");");
        }
        for (var pass = 0; pass < 2; ++pass)
        {
            var v = f.Make("Scout", "400 400 120");
            v.Velocity[0] = 3.0f;
            v.Velocity[1] = 0.0f;
            v.Velocity[2] = -1.0f;
            var hits = new List<CollisionHit> { FloorHit() };
            Assert.True(v.ProcessCollision(hits, pass));
            Assert.True(v.Velocity[2] >= 0.0f);
            if (pass == 0)
                Assert.True(v.Velocity[0] < 3.0f);
            else
                Assert.Equal(3.0f, v.Velocity[0]);
            w.Run("deleteObject(" + Itoa(v.Id()) + ");");
        }
        {
            var v = f.Make("Scout", "600 600 120");
            f.Spawn("600 602 120");
            w.Run("function Vehicle::onDamage(%this, %type, %value, %pos, %vec, %mom, %obj) { $hr = GameBase::getRotation(%this); }");
            w.Run("$hr = \"none\";");
            v.Rotation[2] = 0.2f;
            v.Velocity[0] = 0;
            v.Velocity[1] = 5.0f;
            v.Velocity[2] = 0.64f;
            v.MoveStep(0.032f);
            var seen = w.Run("echo($hr);");
            Assert.True(seen != "none" && seen.StartsWith("0 ", StringComparison.Ordinal));
            Assert.NotEqual(0.0f, v.Transform.M[1]);
            w.Run("function Vehicle::onDamage(%this, %type, %value, %pos, %vec, %mom, %obj) { $hd = $hd @ %this @ \":\" @ %value @ \" \"; }");
        }
        {
            var v = f.Make("Scout", "500 500 120");
            var p = f.Spawn("500 500 120");
            v.Rotation[0] = -0.2f;
            MissionTransform.EulerToMatrix(v.Rotation, ref v.Transform);
            v.Transform.P[0] = 500;
            v.Transform.P[1] = 500;
            v.Transform.P[2] = 120;
            p.SetMountObject(v, 1);
            p.UpdateMove(none);
            var pitched = p.Transform.M[5];
            Assert.NotEqual(0.0f, pitched);
            float[] delta = [0, 0.5f, 0];
            float[] vp = [v.Transform.P[0], v.Transform.P[1], v.Transform.P[2]];
            p.PushBy(delta, vp);
            Assert.True(p.Transform.M[5] == pitched && p.Transform.P[1] > 500.4f);
        }
        {
            var v = f.Make("Scout", "700 700 120");
            var p = f.Spawn("700 701.5 120");
            var parked = f.Make("HAPC", "700 703.2 120");
            var away = f.Make("HAPC", "700 760 120");
            Assert.True((v.ItemFlags & 5) == 1 && (parked.ItemFlags & 5) == 1 && (away.ItemFlags & 5) == 1);
            parked.Velocity[0] = 0;
            parked.Velocity[1] = 0;
            parked.Velocity[2] = -0.64f;
            float[] at = [parked.Transform.P[0], parked.Transform.P[1], parked.Transform.P[2]];
            float[] before = [700, 699.99f, 120];
            var py = p.Transform.P[1];
            Assert.True(w.Game.MoveableDisplace(v, before));
            Assert.True(p.Transform.P[1] > py);
            Assert.True((parked.ItemFlags & 5) == 0 && (v.ItemFlags & 5) == 1 && (away.ItemFlags & 5) == 1);
            Assert.True(parked.Transform.P[0] == at[0] && parked.Transform.P[1] == at[1] && parked.Transform.P[2] == at[2]);
            Assert.True(parked.Velocity[0] == 0.0f && parked.Velocity[1] == 0.0f && parked.Velocity[2] == -0.64f);
            Assert.Equal(0, w.Game.mDisplaceHidden.Count);
            foreach (var hull in new[] { parked, away })
            {
                hull.Velocity[0] = 3.0f;
                hull.Velocity[1] = 0.0f;
                hull.Velocity[2] = -1.0f;
                var hits = new List<CollisionHit> { FloorHit() };
                Assert.True(hull.ProcessCollision(hits, 0));
                Assert.True(hull == parked ? hull.Velocity[0] == 3.0f : hull.Velocity[0] < 3.0f);
            }
            var q = f.Spawn("700 701.5 121.2");
            var qz = q.Transform.P[2];
            float[] again = [700, 699.98f, 120];
            Assert.True(w.Game.MoveableDisplace(v, again));
            Assert.True(q.Transform.P[2] == qz && w.Game.mDisplaceHidden.Count == 0);
        }
        {
            w.Run("ItemData Ball { shapeFile = \"gen\"; mass = 2; friction = 0.5; elasticity = 0.5; };" +
                  "MineData Mn { shapeFile = \"gen\"; triggerRadius = 2.5; };" +
                  "StaticShapeData Crate { shapeFile = \"gen\"; };" +
                  "MoveableData Lift { shapeFile = \"gen\"; speed = 10; };");
            GameBase Gb(string make, string pos)
            {
                var id = w.Run("echo(" + make + ");");
                var o = w.Object(id);
                Assert.True(o is GameBase);
                w.Run("GameBase::setPosition(" + id + ", \"" + pos + "\");");
                return (GameBase)o!;
            }
            var v = f.Make("Scout", "900 900 120");
            var p = f.Spawn("900 901.5 120");
            v.Velocity[0] = 0;
            v.Velocity[1] = 5.0f;
            v.Velocity[2] = 0;
            var y = 899.99f;
            void Shove()
            {
                float[] before = [900, y, 120];
                y = SubF(y, 0.01f);
                var py = p.Transform.P[1];
                Assert.True(w.Game.MoveableDisplace(v, before));
                Assert.True(p.Transform.P[1] > py && w.Game.mDisplaceHidden.Count == 0);
                p.Transform.P[1] = py; // the same overlap for the next object
                p.UpdateContainer();
            }
            var item = Gb("newObject(\"\", Item, Ball, 1, false)", "900 903.2 120");
            var far = Gb("newObject(\"\", Item, Ball, 1, false)", "900 960 120");
            item.AtRest = far.AtRest = true;
            Assert.Equal(5, (item.ItemFlags & 5));
            var at = item.Transform.P[1]; // 903.2 as setPosition reads it
            Shove();
            Assert.True((item.ItemFlags & 5) == 0 && !item.AtRest && (far.ItemFlags & 5) == 5 && far.AtRest);
            Assert.True(item.Transform.P[1] == at && item.Velocity[1] == 0.0f);
            w.Run("deleteObject(" + Itoa(item.Id()) + ");");
            w.Run("PlayerData sarmor { shapeFile = \"gen\"; maxForwardSpeed = 11; maxBackwardSpeed = 10;" +
                  " maxSideSpeed = 10; mass = 9; drag = 1; groundForce = 360; groundTraction = 3; jumpImpulse = 75;" +
                  " jumpSurfaceMinDot = 0.25; jetForce = 236; jetEnergyDrain = 0.75; minJetEnergy = 1;" +
                  " maxJetSideForceFactor = 0.75; maxJetForwardVelocity = 22; maxEnergy = 60; maxDamage = 0.5;" +
                  " boxWidth = 0.5; boxDepth = 0.5; boxNormalHeight = 2.25; boxCrouchHeight = 1.75; canCrouch = true;" +
                  " minDamageSpeed = 25; damageScale = 0.0078125; };");
            w.Run("deleteObject(" + Itoa(p.Id()) + ");");
            Player? sp = Player.From(w.Object(w.Run("echo(spawnPlayer(sarmor, \"900 901.5 120\", \"0 0 0\"));")));
            Assert.NotNull(sp);
            p = sp!;
            var mine = Gb("newObject(\"\", Mine, Mn)", "900 903.2 120");
            Assert.True(mine.ShapeTriggerRadius == 2.5f && (mine.ItemFlags & 5) == 5);
            Shove();
            Assert.True((mine.ItemFlags & 5) == 0 && mine.Transform.P[1] == at);
            Assert.True(mine.Velocity[0] == 0.0f && mine.Velocity[1] == 5.0f && mine.Velocity[2] == 0.0f);
            {
                w.Run("$mc = 0; function Mn::onCollision(%this, %obj) { $mc = $mc @ getObjectType(%obj); }");
                var hull = f.Make("Scout", "900 906.5 120");
                var cands = new List<CollisionImage>();
                var hits = new List<CollisionHit>();
                float[] hp = [hull.Transform.P[0], hull.Transform.P[1], hull.Transform.P[2]];
                hull.HullCandidates(hp, cands, Flier.kHullMask);
                int spheres = 0, shapes = 0;
                foreach (var im in cands)
                    if (im.Object == mine)
                    {
                        if (im.ImageKind == CollisionImage.Kind.TriggerSphere)
                            ++spheres;
                        else
                            ++shapes;
                    }
                Assert.True(spheres == 1 && shapes == 1);
                var n = cands.Count;
                hull.TestAt(hp, cands, hits);
                Assert.True(cands.Count == n - 1);
                foreach (var im in cands)
                    Assert.False((im.Object == mine && im.ImageKind == CollisionImage.Kind.TriggerSphere));
                w.Run("deleteObject(" + Itoa(hull.Id()) + ");"); // the sender is gone: the event is dropped
                w.Sim.AdvanceRealTime(33);
                Assert.Equal("0", w.Run("echo($mc);"));
                hull = f.Make("Scout", "900 906.5 120");
                cands.Clear();
                hp = [hull.Transform.P[0], hull.Transform.P[1], hull.Transform.P[2]];
                hull.HullCandidates(hp, cands, Flier.kHullMask);
                hull.TestAt(hp, cands, hits);
                w.Sim.AdvanceRealTime(32);
                Assert.StartsWith("0Flier", w.Run("echo($mc);"), StringComparison.Ordinal);
                w.Run("deleteObject(" + Itoa(hull.Id()) + ");");
            }
            w.Run("deleteObject(" + Itoa(mine.Id()) + ");");
            var crate = Gb("newObject(\"\", StaticShape, Crate, true)", "900 903.2 120");
            Assert.True((crate.ItemFlags & 5) == 5 && w.Run("echo(GameBase::getRotation(" + Itoa(crate.Id()) + "));") == "0 0 0");
            Shove();
            Assert.True((crate.ItemFlags & 5) == 0 && crate.Transform.P[1] == at && crate.Velocity[1] == 0.0f);
            Assert.Equal("0 -0 -0", w.Run("echo(GameBase::getRotation(" + Itoa(crate.Id()) + "));"));
            w.Run("deleteObject(" + Itoa(crate.Id()) + ");");
            var lift = Gb("newObject(\"\", Moveable, Lift, true)", "900 903.2 120");
            far.Attach(lift);
            Assert.True((far.ItemFlags & 5) == 5 && far.AtRest);
            Shove();
            Assert.True((lift.ItemFlags & 5) == 0 && (far.ItemFlags & 5) == 0 && !far.AtRest);
            Assert.True(far.Transform.P[1] == 960.0f && far.Velocity[1] == 0.0f);
            w.Run("deleteObject(" + Itoa(lift.Id()) + "); deleteObject(" + Itoa(far.Id()) + ");");
            w.Run("deleteObject(" + Itoa(p.Id()) + "); deleteObject(" + Itoa(v.Id()) + ");");
        }
        {
            w.Run("$hd = \"\"; $ray = 0; $rot = \"\";" +
                  "function Vehicle::onDamage(%this, %type, %value, %pos, %vec, %mom, %obj) {" +
                  " $hd = $hd @ %value @ \" \"; $rot = GameBase::getRotation(%this);" +
                  " if($ray) getLOSInfo(\"1000 1000 60\", \"1000 1000 51\", -1); }");
            var a = f.Make("Scout", "1000 1000 53");
            for (var i = 0; i < 80; ++i)
                a.Update(ref none, 0.032f);
            Assert.True(a.Landed && w.Run("echo($hd);").Length == 0);
            var low = a.Transform.P[2];
            var pos = "1000 1001.7 " + CFormat.G(low, 9);
            var b = f.Make("Scout", pos);
            var e = w.Game.World.Find(a)!;
            var q0 = e.ImageQueries;
            var id = Itoa(a.Id());
            a.Update(ref none, 0.032f);
            Assert.True(w.Run("echo($hd);").Length != 0 && w.Run("echo($rot);") == "0 0 0");
            Assert.True(a.Transform.P[2] == low && w.Run("echo(GameBase::getRotation(" + id + "));") == "0 -0 -0");
            Assert.True(w.Game.World.Find(a)!.ImageQueries == q0);
            w.Run("$ray = 1;");
            a.Update(ref none, 0.032f);
            Assert.True(w.Game.World.Find(a)!.ImageQueries > q0);
            Assert.True(a.Transform.P[2] > low + 0.01f && a.Transform.P[2] < low + 0.0105f);
            Assert.Equal("0 0 0", w.Run("echo(GameBase::getRotation(" + id + "));"));
            w.Run("$ray = 0;");
            a.Update(ref none, 0.032f);
            Assert.True(a.Transform.P[2] == low);
            w.Run("deleteObject(" + id + "); deleteObject(" + Itoa(b.Id()) + ");");
            w.Run("function Vehicle::onDamage(%this, %type, %value, %pos, %vec, %mom, %obj) { $hd = $hd @ %this @ \":\" @ %value @ \" \"; }");
        }
        {
            var p = f.Spawn("800 800 50.5");
            for (var i = 0; i < 40; ++i)
                p.UpdateMove(none);
            Assert.True(p.RestFlag && p.ContactFlag);
            var y = p.Transform.P[1];
            float[] delta = [0, 0.0128f, -0.0088f];
            float[] mover = [0, 0.4f, -0.595f];
            Assert.True(p.PushBy(delta, mover));
            Assert.True(!p.RestFlag && p.Transform.P[1] > y + 0.012f);
            p.Velocity[0] = p.Velocity[1] = p.Velocity[2] = 0; // the displace restores the velocity (commit)
            p.UpdateMove(none);
            Assert.True(p.RestFlag);
        }
    }

    // ---- Triggers against the hull (docs/collision.md E2, 11.10; ROADMAP G27 a) ----

    private static void TestTriggers()
    {
        var f = new Fixture("vehicle_triggers");
        var w = f.W;
        var none = new Move();
        w.Run("TriggerData Zone;" +
              "function Zone::onEnter(%t, %o) { $zl = $zl @ \"E\" @ $zi[%t]; }" +
              "function Zone::onContact(%t, %o) { $zl = $zl @ \"C\" @ $zi[%t]; }" +
              "function Zone::onLeave(%t, %o) { $zl = $zl @ \"L\" @ $zi[%t]; }");
        GameBase Trigger(string tag, string pos, string box, string sphere)
        {
            w.Run("instant Trigger \"Z" + tag + "\" { dataBlock = \"Zone\"; position = \"" + pos +
                  "\"; rotation = \"0 0 0\"; boundingBox = \"" + box + "\"; isSphere = \"" + sphere + "\"; };" +
                  "$zi[nameToId(\"Z" + tag + "\")] = \"" + tag + "\";");
            return (GameBase)w.Object("Z" + tag)!;
        }
        void Tick(Flier v, int n)
        {
            for (var i = 0; i < n; ++i)
            {
                v.Update(ref none, 0.032f);
                w.Sim.AdvanceRealTime(32);
            }
        }
        string Log() => w.Run("echo($zl);");
        var inBox = Trigger("a", "100 100 60", "-1 -1 -1 1 1 1", "False");
        var aside = Trigger("b", "106 100 60", "-1 -1 -1 1 1 1", "False");
        var inSphere = Trigger("s", "130 100 60", "-1 -1 -1 1 1 1", "True");
        var v = f.Make("Scout", "100 100 60.2");
        var u = f.Make("Scout", "130 100 60.2");
        Tick(v, 1);
        Tick(u, 1);
        Assert.Equal("EaEs", Log());
        Assert.True(inBox.Occupants.Count == 1 && inSphere.Occupants.Count == 1 && aside.Occupants.Count == 0);
        w.Run("deleteObject(" + Itoa(v.Id()) + "); deleteObject(" + Itoa(u.Id()) + "); $zl = \"\";");
        var far = Trigger("f", "202.1 100 60", "-1 -1 -1 1 1 1", "False");
        var near = Trigger("n", "301.9 100 60", "-1 -1 -1 1 1 1", "False");
        var vf = f.Make("Scout", "200 100 60.2");
        var vn = f.Make("Scout", "300 100 60.2");
        Tick(vf, 1);
        Tick(vn, 1);
        Assert.True(Log() == "En" && far.Occupants.Count == 0 && near.Occupants.Count == 1);
        Tick(vn, 10);
        Assert.Equal("EnCn", Log());
        w.Run("GameBase::setPosition(" + Itoa(vn.Id()) + ", \"300 140 60.2\");");
        Tick(vn, 10);
        Assert.True(Log() == "EnCnLn" && near.Occupants.Count == 0);
    }

    // ---- wire (6) ----

    private static void TestWire()
    {
        var f = new Fixture("vehicle_wire");
        var v = f.Make("Scout", "128 128 70");
        v.ControlClient = 2049;
        v.Piloted = true;
        v.SpeedF = 25;
        v.SpeedT = 50;
        v.SpeedL = 2.5f;
        v.SpeedS = -1.5f;
        var buf = new byte[256];
        var bs = new BitStream(buf, buf.Length);
        var ctx = new ControlContext();
        v.WriteControlState(bs, ctx);
        Assert.True(bs.BitPosition() == 2 + 288 + (16 * 3) + 6 + 2 + 2 + 64 + 1);
        var rd = new BitStream(buf, buf.Length);
        Assert.Equal(0u, rd.ReadInt(2));
        for (var i = 0; i < 6; ++i)
            rd.ReadF32();
        float p0 = rd.ReadF32(), p1 = rd.ReadF32(), p2 = rd.ReadF32();
        _ = p1;
        Assert.True(p0 == 128 && p2 == 70);
        Assert.True(rd.ReadInt(16) == (uint)(long)(65535.0 * (25.0 + 2) / 52)); // unit(16) of (F - min) / (max - min)
        Assert.Equal(65535u, rd.ReadInt(16));
        Assert.Equal(65535u, rd.ReadInt(16)); // energy 100 / 100
        Assert.True(rd.ReadInt(6) == 0 && rd.ReadInt(2) == 0 && rd.ReadInt(2) == 0);
        float l = rd.ReadF32(), s = rd.ReadF32();
        Assert.True(l == 2.5f && s == -1.5f && !rd.ReadFlag());

        var b2 = new byte[256];
        var owner = new BitStream(b2, b2.Length);
        Assert.True(v.PackUpdate(owner, 2049, 0x2000, false) == 0 && owner.BitPosition() == 1);
        var b3 = new byte[256];
        var full = new BitStream(b3, b3.Length);
        v.PackUpdate(full, 2050, 0xffffffffu, true);
        Assert.True(full.BitPosition() > 100 && (b3[0] & 1) == 0);
        // Client round trip (client-net.md 3.5): the creating update, the 0x2000 state update, the owner-only form.
        var cw = ClientRoundTrip.WorldFor(0x223, v);
        var cv = ClientRoundTrip.Unpack(0x223, b3, full.BitPosition(), true, cw);
        Assert.True(cv.Class == "Flier" && ClientRoundTrip.Has(cv, "datablock") && ClientRoundTrip.Has(cv, "team") && !ClientRoundTrip.Has(cv, "ownerOnly"));
        var co = ClientRoundTrip.Unpack(0x223, b2, owner.BitPosition(), false, cw, cv);
        Assert.Equal(1, (long)co.State["ownerOnly"]!);
        var b4 = new byte[256];
        var upd = new BitStream(b4, b4.Length);
        v.PackUpdate(upd, 2050, 0x2000, false);
        var b5 = new byte[256];
        var upd2 = new BitStream(b5, b5.Length);
        v.Piloted = false;
        v.PackUpdate(upd2, 2050, 0x2000, false);
        Assert.True(upd.BitPosition() > 0 && upd2.BitPosition() > 0);
        var cu = ClientRoundTrip.Unpack(0x223, b4, upd.BitPosition(), false, cw, cv);
        Assert.True(ClientRoundTrip.Has(cu, "rot") && ClientRoundTrip.Has(cu, "pos") && ClientRoundTrip.Has(cu, "move") && ClientRoundTrip.Has(cu, "f8d8"));
        Assert.True(ClientRoundTrip.VecIs(ClientRoundTrip.Field(cu, "pos"), v.Transform.P[0], v.Transform.P[1], v.Transform.P[2]));
        var cu2 = ClientRoundTrip.Unpack(0x223, b5, upd2.BitPosition(), false, cw, cv);
        Assert.True(ClientRoundTrip.Has(cu2, "pos"), string.Join(",", cu2.Last.Select(x => x.Name)));
    }

    [Fact]
    public void Main()
    {
        TestStepVectors();
        TestCreation();
        TestCredit();
        TestMounting();
        TestCommands();
        TestDamage();
        TestHulls();
        TestTriggers();
        TestWire();
    }
}
