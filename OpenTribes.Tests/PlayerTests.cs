// Port of tests/test_player.cpp: the Player as a simulated control object (ROADMAP 7.2): creation and commands
// (docs/player-sim.md 1, 5.6, 6, 7.6; docs/player-control.md 6), the move step against vectors of the exact model
// (docs/player-sim.md 2-4, 6.2), collision (docs/collision.md 2-5), the control state (8.1) and the ghost layout
// (docs/game-stream-ghosts.md 4.7). 367 asserts, one to one.
using System.Globalization;
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Net;
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;
using static OpenTribes.Tests.Fixtures;

namespace OpenTribes.Tests;

public class PlayerTests
{
    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);
    private static string Itoa(int v) => v.ToString(CultureInfo.InvariantCulture);

    // PERS ITRGeometry of one solid box (docs/collision.md 5.2 part 1): a chain of six BSP nodes, six surfaces (one per
    // plane) listed by the single solid leaf 43.
    private static List<byte> BoxDig(float[] b)
    {
        var body = new List<byte>();
        PutU32(body, 0x77);
        PutF32(body, 1.0f);
        for (int i = 0; i < 6; ++i)
            PutF32(body, b[i]);
        uint[] counts = { 6, 6, 1, 1, 1, 0, 0, 0, 6 };
        foreach (uint c in counts)
            PutU32(body, c);
        for (int i = 0; i < 6; ++i)
        {
            // surfaces: planeFront, material 0, plane i
            var s = new byte[20];
            s[0] = 0x80;
            s[6] = (byte)i;
            body.AddRange(s);
        }
        for (int i = 0; i < 6; ++i)
        {
            // node i: front = empty leaf 0, back = next node or solid leaf 43
            PutU16(body, (ushort)i);
            PutU16(body, unchecked((ushort)(short)-1));
            PutU16(body, unchecked((ushort)(short)(i == 5 ? -44 : i + 1)));
            PutU16(body, 0);
        }
        var leaf = new byte[12]; // bitList offset 0, byte count 1
        leaf[8] = 1;
        body.AddRange(leaf);
        body.AddRange(new byte[44]);
        body.Add(0xfc); // surfaces 0..5
        // Planes n.p + d = 0, front outside the box.
        float[][] planes =
        {
            new[] { 1f, 0, 0, -b[3] }, new[] { -1f, 0, 0, b[0] }, new[] { 0, 1f, 0, -b[4] },
            new[] { 0, -1f, 0, b[1] }, new[] { 0, 0, 1f, -b[5] }, new[] { 0, 0, -1f, b[2] },
        };
        foreach (float[] p in planes)
            foreach (float f in p)
                PutF32(body, f);
        PutU32(body, 0);
        PutU32(body, 0);
        return Pers("ITRGeometry", 7, body);
    }

    // Run-time x87 arithmetic with truncating stores.
    private static float Inverse(float x) => X87.F32(Div(1.0, x));
    private static float Scaled(float a, float b) => X87.F32(Mul(a, b));
    private static float Sum(float a, double b) => X87.F32(Add(a, b));

    private const float kFlat = 50.0f;

    private sealed class Fixture
    {
        public readonly string Root;
        public readonly Fixtures.World W;

        public Fixture(string name)
        {
            Root = Path.Combine(TestPaths.TmpDir(""), name);
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
            var t = new TerrainSpec(); // 2 x 2 blocks of 4 x 4 squares of 32 m: 256 m x 256 m, flat
            t.Scale = 5;
            t.Height = 2;
            t.Heights = Enumerable.Repeat(kFlat, 25).ToList();
            t.Squares = new List<byte>(new byte[32]);
            PutFile(Path.Combine(Root, "base/m.dtf"), DtfFile(t, kFlat, kFlat));
            PutFile(Path.Combine(Root, "base/m#0.dtb"), DtbFile(t, kFlat, kFlat));
            PutFile(Path.Combine(Root, "base/t.dml"), "dml");
            float[] low = { -2, -1, 0, 2, 1, 0.4f }, high = { -2, -1, 0, 2, 1, 0.8f };
            PutFile(Path.Combine(Root, "base/low.dis"), DisFile("low-00.dig", "low-000.dil"));
            PutFile(Path.Combine(Root, "base/low-00.dig"), BoxDig(low));
            PutFile(Path.Combine(Root, "base/low-000.dil"), DilFile(0x77, new List<LightRec>(), new List<byte>(), ""));
            PutFile(Path.Combine(Root, "base/high.dis"), DisFile("high-00.dig", "high-000.dil"));
            PutFile(Path.Combine(Root, "base/high-00.dig"), BoxDig(high));
            PutFile(Path.Combine(Root, "base/high-000.dil"), DilFile(0x77, new List<LightRec>(), new List<byte>(), ""));
            float[] gen = { -1, -1.5f, 0, 1, 1.5f, 2 };
            PutFile(Path.Combine(Root, "base/gen.dts"), DtsCollisionFile(gen));
            float[] pan = { -1.202879f, -2.5f, -2.5f, 1.202879f, 2.5f, 2.5f };
            PutFile(Path.Combine(Root, "base/pan.dts"), DtsCollisionFile(pan));
            W = new Fixtures.World(Root);
            Numbers.EnterScriptFpuMode();
            W.Run("$Server::teamName0 = \"A\"; resetPlayerManager();");
            W.Run("PlayerData tarmor { shapeFile = \"none\"; maxForwardSpeed = 11; maxBackwardSpeed = 10;" +
                  " maxSideSpeed = 10; mass = 9; drag = 1; groundForce = 360; groundTraction = 3; jumpImpulse = 75;" +
                  " jumpSurfaceMinDot = 0.25; jetForce = 236; jetEnergyDrain = 0.75; minJetEnergy = 1;" +
                  " maxJetSideForceFactor = 0.75; maxJetForwardVelocity = 22; maxEnergy = 60; maxDamage = 0.5;" +
                  " boxWidth = 0.5; boxDepth = 0.5; boxNormalHeight = 2.25; boxCrouchHeight = 1.75; canCrouch = true;" +
                  " minDamageSpeed = 25; damageScale = 0.0078125; };" +
                  "ItemImageData GunImage { shapeFile = \"gen\"; mass = 3; weaponType = 0; };" +
                  "ItemData Gun { className = \"Weapon\"; imageType = GunImage; };" +
                  "ItemData Kit { className = \"Tool\"; };");
            W.Run("instant SimGroup \"MissionGroup\" {" +
                  " instant SimTerrain \"Terrain\" { tedFileName = \"m.dtf\"; position = \"0 0 0\";" +
                  "   contGravity = \"0 0 -20\"; }; };" +
                  "instant SimGroup \"MissionCleanup\";");
        }

        public Player Spawn(string pos, string rot = "0 0 0")
        {
            string id = W.Run("echo(spawnPlayer(tarmor, \"" + pos + "\", \"" + rot + "\"));");
            Player? p = Player.From(W.Object(id));
            Assert.True(p != null);
            return p!;
        }
    }

    // makeMove: a vector move.
    private static Move MakeMove(string[] v)
    {
        var m = new Move();
        m.Forward = Store(Div(int.Parse(v[0], CultureInfo.InvariantCulture), 15.0));
        m.Backward = Store(Div(int.Parse(v[1], CultureInfo.InvariantCulture), 15.0));
        m.Left = Store(Div(int.Parse(v[2], CultureInfo.InvariantCulture), 15.0));
        m.Right = Store(Div(int.Parse(v[3], CultureInfo.InvariantCulture), 15.0));
        m.Jet = v[4] != "0";
        m.Jump = v[5] != "0";
        m.Crouch = v[6] != "0";
        m.Turn = TestPaths.IncF32(v[7]);
        m.Pitch = TestPaths.IncF32(v[8]);
        return m;
    }

    // The step against the exact model: every field of every step is bit-identical.
    private static void RunVectors(Fixture f, string name, string prefix)
    {
        const string inc = "tests/player_vectors.inc";
        string[] start = TestPaths.IncTokens(inc, "k" + prefix + "Start")[0];
        List<string[]> moves = TestPaths.IncTokens(inc, "k" + prefix + "Moves"), states = TestPaths.IncTokens(inc, "k" + prefix + "States");
        string pos = Numbers.FormatFloat(TestPaths.IncF32(start[0])) + " " + Numbers.FormatFloat(TestPaths.IncF32(start[1])) + " " +
                     Numbers.FormatFloat(TestPaths.IncF32(start[2]));
        Player p = f.Spawn(pos);
        p.RechargeRate = 8.0f;
        for (int i = 0; i < moves.Count; ++i)
        {
            p.UpdateMove(MakeMove(moves[i]));
            uint[] s = states[i].Take(9).Select(TestPaths.IncU32).ToArray();
            int[] flags = states[i].Skip(9).Select(t => int.Parse(t, CultureInfo.InvariantCulture)).ToArray();
            bool ok = true;
            for (int k = 0; k < 3; ++k)
                ok = ok && Bits(p.Transform.P[k]) == s[k] && Bits(p.Velocity[k]) == s[3 + k];
            ok = ok && Bits(p.Rotation[2]) == s[6] && Bits(p.Pitch) == s[7] && Bits(p.Energy) == s[8];
            ok = ok && (p.ContactFlag ? 1 : 0) == flags[0] && (p.Jetting ? 1 : 0) == flags[1] && (p.Crouching ? 1 : 0) == flags[2];
            ok = ok && (int)MulF(p.Traction, 127.0f) == flags[3] && Math.Min(p.TicksSinceJumpContact, 15) == flags[4];
            if (!ok)
            {
                Console.Error.WriteLine($"{name} step {i}: pos {Bits(p.Transform.P[0]):x8} {Bits(p.Transform.P[1]):x8} " +
                                        $"{Bits(p.Transform.P[2]):x8} want {s[0]:x8} {s[1]:x8} {s[2]:x8}, vel {Bits(p.Velocity[0]):x8} " +
                                        $"{Bits(p.Velocity[1]):x8} {Bits(p.Velocity[2]):x8} want {s[3]:x8} {s[4]:x8} {s[5]:x8}, " +
                                        $"yaw {Bits(p.Rotation[2]):x8}/{s[6]:x8} pitch {Bits(p.Pitch):x8}/{s[7]:x8} " +
                                        $"energy {Bits(p.Energy):x8}/{s[8]:x8}");
                Assert.True(false);
            }
        }
        f.W.Run("deleteObject(" + Itoa(p.Id()) + ");");
    }

    private static void TestStepVectors()
    {
        var f = new Fixture("player_vectors");
        RunVectors(f, "fall", "Fall");
        RunVectors(f, "air", "Air");
        RunVectors(f, "ground", "Ground");
    }

    // Line against the Player image (collision.md 9, 0x0043599c, 0x005ebe80).
    private static void TestPlayerLine()
    {
        var f = new Fixture("player_line");
        Fixtures.World w = f.W;
        var c = new Cases();
        (string rot, string s, string e, string want)[] rays =
        {
            ("0 0 0", "50 30 901", "50 70 901", "50 49.4999 901|0 0 1"),
            ("0 0 0", "50.123 30 900.7", "50.2 70 901.1", "50.1605 49.4999 900.894|0 0 1"),
            ("0 0 0.7", "50.123 30 900.7", "50.2 70 901.1", "50.1604 49.4814 900.894|0 0 1"),
            ("0 0 2.5", "30 47.3 900.3", "70 51.9 901.9", "49.74 49.57 901.089|0 0 1"),
            ("0 0 1", "50.1 50.05 901", "55 50 901", "50.0999 50.0499 901|0 0 1"),
            ("0 0 1.9", "47.77 61.3 902.2", "50.3 49.9 900.4", "50.1492 50.5793 900.507|0 0 1"),
            ("0 0 3.0", "58.77 41.3 900.2", "49.3 50.2 901.4", "50.0591 49.4865 901.303|0 0 1"),
            ("0 0 0.01", "50.61 20 900.2", "50.2 60 902.1", "50.3075 49.503 901.601|0 0 1"),
        };
        foreach (var r in rays)
        {
            Player pr = f.Spawn("50 50 900", r.rot);
            string id = Itoa(pr.Id());
            c.Check(w, "if (getLOSInfo(\"" + r.s + "\", \"" + r.e + "\", 16)) echo($los::object == " + id +
                       ", \"|\", $los::position, \"|\", $los::normal); else echo(\"miss\");",
                    "1|" + r.want);
            w.Run("deleteObject(" + id + ");");
        }
        // A segment that passes the turned box misses.
        f.Spawn("50 50 900", "0 0 -1.1");
        c.Check(w, "echo(getLOSInfo(\"53 52 910\", \"49.9 49.8 895\", 16));", "False");
        Assert.True(c.Failures == 0);
    }

    // ---- creation (player-sim 1.1 - 1.7) ----

    private static void TestSpawn()
    {
        var f = new Fixture("player_spawn");
        Fixtures.World w = f.W;
        var c = new Cases();
        // 1.1: argc != 4 and an unknown armor return -1 without a message.
        c.Check(w, "echo(spawnPlayer());", "-1");
        c.Check(w, "echo(spawnPlayer(tarmor));", "-1");
        c.Check(w, "echo(spawnPlayer(nosuch, \"1 2 3\", \"0 0 0\"));", "-1");
        // 1.3: onAdd runs before the player joins MissionCleanup; energy = maxEnergy (1.4).
        w.Run("function Player::onAdd(%this) { $added = %this; $inCleanup = Group::objectCount(MissionCleanup);" +
              " GameBase::setRechargeRate(%this, 8); }");
        Player p = f.Spawn("16 16 80", "0 0 1.5");
        string id = Itoa(p.Id());
        c.Check(w, "echo($added == " + id + ", \" \", $inCleanup, \" \", Group::objectCount(MissionCleanup));", "1 0 1");
        Assert.True(p.Energy == 60.0f && p.RechargeRate == 8.0f && p.Team == -1);
        Assert.True(p.Transform.P[0] == 16 && p.Transform.P[2] == 80 && p.Rotation[2] == 1.5f);
        // 1.2 / 2.4 start values; 3.1 geometry: box +-0.5, 0..2.25, radius = height / 2.
        Assert.True(p.TicksSinceJumpContact == 100 && p.JumpNormal[2] == 1 && p.PendingAnim == 0 && p.CurrentAnim == -1);
        Assert.True(p.BoxMin[0] == -0.5f && p.BoxMax[2] == 2.25f && p.Radius == 1.125f && p.Mass == 9.0f);
        // 1.6: set 719 and container 7 inside the terrain container (gravity entry first, FC5).
        bool inSet = false;
        foreach (SimObject m in w.Sim.Server()!.FindById(719)!.AsSet()!.Members())
            inSet = inSet || m == p;
        Assert.True(inSet && w.Game.World.Find(p) != null && w.Game.World.TerrainOf(p) != null);
        Assert.True(p.Forces().Count == 1 && p.Forces()[0].Type == 1 && p.Forces()[0].V[2] == -20.0f);
        // 1.5 setArmor / getArmor with the exact messages; a client id names its owned object.
        c.Check(w, "echo(Player::getArmor(" + id + "));", "tarmor");
        c.Check(w, "echo(Player::getArmor());", "wrong argcount to Player::getArmor(player)\nFalse");
        c.Check(w, "echo(Player::setArmor(" + id + "));", "wrong argcount to Player::setArmor(player,armor)\nFalse");
        c.Check(w, "echo(Player::getArmor(8));", "False");
        p.Energy = 10;
        c.Check(w, "echo(Player::setArmor(" + id + ", tarmor), \" \", GameBase::getEnergy(" + id + "));", "True 60");
        c.Check(w, "echo(Player::setArmor(" + id + ", nosuch), \" \", Player::getArmor(" + id + "));", "True tarmor");
        // 6.2 energy commands.
        c.Check(w, "echo(GameBase::setEnergy(" + id + ", 100), GameBase::getEnergy(" + id + "));", "True60");
        c.Check(w, "echo(GameBase::setEnergy(" + id + ", -3), GameBase::getEnergy(" + id + "));", "True0");
        c.Check(w, "echo(GameBase::getEnergy());", "wrong argcount to getEnergy(object)\n0");
        // 1.7 onRemove: the script callback, then the container entry goes.
        w.Run("function Player::onRemove(%this) { $removed = %this; }");
        w.Run("deleteObject(" + id + ");");
        c.Check(w, "echo($removed == " + id + ");", "1");
        Assert.True(w.Game.World.Contents(null).Count == 1); // only the terrain is left in container 7
        Assert.True(c.Failures == 0);
    }

    // ---- moves: credit, latency, null moves (player-sim 2.1) ----

    private static void TestMoveCredit()
    {
        var f = new Fixture("player_credit");
        Player p = f.Spawn("16 16 500");
        var jet = new Move();
        jet.Jet = true;
        // TK6: the first applied move runs the stored (zero) move; the jet drains from the second.
        p.ApplyMoves(new[] { jet }, 1);
        Assert.True(p.Energy == 60.0f && !p.Jetting && p.Credit == 1 && p.LastMove.Jet);
        p.ApplyMoves(new[] { jet }, 1);
        Assert.True(p.Jetting && p.Energy == 59.25f && p.Credit == 2);
        // TK3: a packet runs moves until the credit exceeds 5; the rest is dropped.
        var many = new Move[8];
        for (int i = 0; i < 8; ++i)
        {
            many[i] = new Move();
            many[i].Jet = true;
        }
        p.ApplyMoves(many, 8);
        Assert.True(p.Credit == 6 && p.Energy == 59.25f - 4 * 0.75f);
        float z = p.Transform.P[2];
        // TK3/TK4: with a control client, ticks only pay the credit back until it is below -25.
        p.ControlClient = 2049;
        for (int i = 0; i < 32; ++i)
            p.ServerTick((uint)i * 32);
        Assert.True(p.Credit == -26 && p.Transform.P[2] == z && p.TicksWithoutMove == 32);
        p.ServerTick(1024);
        Assert.True(p.Credit == -26 && p.Transform.P[2] != z && p.TicksWithoutMove == 0 && !p.LastMove.Jet);
        // TK4: without a control client every tick is a null move.
        Player q = f.Spawn("20 16 500");
        q.ServerTick(0);
        Assert.True(q.Velocity[2] == -0.64f && q.Credit == 0);
        // TK7: a dead player ignores moves.
        q.Dead = true;
        float vz = q.Velocity[2];
        q.ApplyMoves(new[] { jet }, 1);
        Assert.True(q.Velocity[2] == vz && q.Credit == 0);
    }

    // ---- forces and containers (player-sim 2.6), rotation (4) ----

    private static void TestForcesAndAim()
    {
        var f = new Fixture("player_forces");
        Player p = f.Spawn("16 16 500");
        var jet = new Move();
        jet.Jet = true;
        p.UpdateMove(jet);
        // FC5: gravity (container entry at onAdd) first, the jet entry second; the jet force is stored times invMass (FC1).
        Assert.True(p.Forces().Count == 2 && p.Forces()[0].Type == 1 && p.Forces()[1].Type == 0x100);
        Assert.True(p.Forces()[1].V[2] == Scaled(236.0f, Inverse(9.0f)));
        // FC4: outside the terrain container there is no gravity.
        f.W.Run("GameBase::setPosition(" + Itoa(p.Id()) + ", \"1000 1000 500\");");
        // SimObject::setTransform (0x005054a4) updates the container entry at once; the force list changes only in the
        // step (FC5).
        {
            Container.Entry? e = f.W.Game.World.Find(p);
            Assert.True(e != null && e.Box[0] < 1000.0f && e.Box[3] > 1000.0f && e.Box[2] <= 500.0f && e.Box[5] > 500.0f);
            Assert.True(p.Forces().Count == 2);
        }
        p.Velocity[0] = p.Velocity[1] = p.Velocity[2] = 0;
        p.UpdateMove(new Move());
        Assert.True(p.Forces().Count == 1 && p.Forces()[0].Type == 0x100 && p.Velocity[2] == 0.0f);
        // Back inside: gravity is appended behind the jet entry.
        f.W.Run("GameBase::setPosition(" + Itoa(p.Id()) + ", \"16 16 500\");");
        p.UpdateMove(new Move());
        Assert.True(p.Forces().Count == 2 && p.Forces()[0].Type == 0x100 && p.Forces()[1].Type == 1);
        Assert.True(p.Velocity[2] == -0.64f);
        p.RemoveForce(0x100);
        Assert.True(p.Forces().Count == 1 && p.Forces()[0].Type == 1);

        // RA2: one wrap of the yaw by the double 2 pi; RA4: pitch clamp to 0x3fc49809.
        Player q = f.Spawn("20 16 500");
        var turn = new Move();
        turn.Turn = -0.5f;
        q.UpdateMove(turn);
        Assert.True(q.Rotation[2] == Sum(-0.5f, 6.283185307179586));
        turn.Turn = 1.0f;
        q.UpdateMove(turn);
        Assert.True(q.Rotation[2] < 1.0f);
        var look = new Move();
        look.Pitch = 1.0f;
        q.UpdateMove(look);
        q.UpdateMove(look);
        Assert.True(Bits(q.Pitch) == 0x3fc49809u);
        look.Pitch = -1.0f;
        for (int i = 0; i < 4; ++i)
            q.UpdateMove(look);
        Assert.True(Bits(q.Pitch) == 0xbfc49809u);
        // 6.2: recharge clamps at maxEnergy and at 0 for a negative rate.
        q.RechargeRate = -4000.0f;
        q.UpdateMove(new Move());
        Assert.True(q.Energy == 0.0f);
    }

    // MV4 container drag and horzVelClamp (docs/player-sim.md 9.5 `drag`, `clamp`).
    private static void TestDragAndClamp()
    {
        var f = new Fixture("player_drag");
        Player p = f.Spawn("16 16 500");
        p.UpdateMove(new Move());
        Assert.True(p.Velocity[2] == -0.64f);
        f.W.Run("setTerrainContainer(\"MissionGroup\\\\Terrain\", \"0 0 -20\", 0.5, 10000);");
        // Already inside: neither the drag nor a new gravity reaches it.
        p.Velocity[2] = 0;
        p.UpdateMove(new Move());
        Assert.True(p.Velocity[2] == -0.64f);
        // A player that enters afterwards: v = f32(v - v * f32((drag * contDrag) * dt)).
        static float Dragged(float v)
        {
            float c = Scaled(Scaled(1.0f, 0.5f), 0.032f);
            return X87.F32(Sub(v, Mul(v, c)));
        }
        Player q = f.Spawn("20 16 500");
        q.UpdateMove(new Move());
        Assert.True(Bits(q.Velocity[2]) == Bits(Dragged(-0.64f)) && q.Velocity[2] > -0.64f);
        // Leaving the container removes gravity and keeps the drag (0x0051963c).
        f.W.Run("GameBase::setPosition(" + Itoa(q.Id()) + ", \"1000 1000 500\");");
        q.Velocity[0] = q.Velocity[1] = 0;
        q.Velocity[2] = -1.0f;
        q.UpdateMove(new Move());
        Assert.True(q.Forces().Count == 1 && q.Forces()[0].Type == 0x100 && Bits(q.Velocity[2]) == Bits(Dragged(-1.0f)));
        // The first player re-enters and takes the drag.
        f.W.Run("GameBase::setPosition(" + Itoa(p.Id()) + ", \"1000 1000 500\");");
        p.UpdateMove(new Move());
        f.W.Run("GameBase::setPosition(" + Itoa(p.Id()) + ", \"16 16 500\");");
        p.Velocity[2] = 0;
        p.UpdateMove(new Move());
        Assert.True(Bits(p.Velocity[2]) == Bits(Dragged(-0.64f)));

        // horzVelClamp: not above 3.0, no clamp; a redeclaration reaches a bound player.
        var g = new Fixture("player_clamp");
        Player cl = g.Spawn("16 16 500");
        cl.Velocity[0] = 8.0f;
        cl.Velocity[1] = 6.0f;
        cl.UpdateMove(new Move());
        Assert.True(cl.Velocity[0] == 8.0f && cl.Velocity[1] == 6.0f);
        g.W.Run("PlayerData tarmor { horzVelClamp = 3; };");
        cl.UpdateMove(new Move());
        Assert.True(cl.Velocity[0] == 8.0f && cl.Velocity[1] == 6.0f);
        g.W.Run("PlayerData tarmor { horzVelClamp = 5; maxForwardSpeed = 4; };");
        cl.UpdateMove(new Move());
        {
            float ten = 10.0f;
            float k = X87.F32(Div(5.0, ten));
            Assert.True(cl.Velocity[0] == Scaled(k, 8.0f) && cl.Velocity[1] == Scaled(k, 6.0f));
            Assert.True(cl.Velocity[0] < 4.01f && cl.Velocity[0] > 3.99f);
        }
        Assert.True(cl.PData.MaxForwardSpeed == 4.0f && cl.PData.Mass == 9.0f);
        // Mass, drag and the box are copies made at bind: a redeclaration does not change them.
        g.W.Run("PlayerData tarmor { mass = 20; drag = 3; boxNormalHeight = 4; };");
        cl.UpdateMove(new Move());
        Assert.True(cl.PData.Mass == 9.0f && cl.PData.Drag == 1.0f && cl.PData.BoxNormalHeight == 2.25f);
    }

    // Console transforms (docs/player-sim.md 9.4, docs/mission.md 4.1).
    private static void TestConsoleTransform()
    {
        var f = new Fixture("player_console_transform");
        Player p = f.Spawn("16 16 500");
        string id = Itoa(p.Id());
        // 3.14159 parses to 0x40490fcf. [live]: the reference control state after this pair of commands carries the yaw
        // 0x40490fcd, one unit lost per round trip.
        f.W.Run("GameBase::setRotation(" + id + ", \"0 0 3.14159\");");
        Assert.True(Bits(p.Rotation[2]) == 0x40490fceu);
        Assert.True(p.Transform.P[0] == 16.0f && p.Transform.P[2] == 500.0f);
        f.W.Run("GameBase::setPosition(" + id + ", \"20 16 500\");");
        Assert.True(Bits(p.Rotation[2]) == 0x40490fcdu);
        Assert.True(p.Transform.P[0] == 20.0f);
        // The matrix is rebuilt from the stored angle.
        {
            Transform t = p.Transform;
            float[] e = { 0, 0, p.Rotation[2] };
            MissionTransform.EulerToMatrix(e, ref t);
            bool same = true;
            for (int i = 0; i < 9; ++i)
                same = same && Bits(t.M[i]) == Bits(p.Transform.M[i]);
            Assert.True(same);
        }
        // A zero rotation stays +0 through both commands ([live]: every Broadside scenario).
        f.W.Run("GameBase::setRotation(" + id + ", \"0 0 0\"); GameBase::setPosition(" + id + ", \"16 16 500\");");
        Assert.True(Bits(p.Rotation[2]) == 0u && Bits(p.Rotation[0]) == 0u && Bits(p.Rotation[1]) == 0u);
        // A yaw reached by moves is re-read from the matrix by setPosition as well.
        var turn = new Move();
        turn.Turn = 0.7f;
        p.UpdateMove(turn);
        Assert.True(Bits(p.Rotation[2]) == Bits(0.7f));
        float[] back = new float[3];
        MissionTransform.MatrixToEuler(p.Transform, back);
        f.W.Run("GameBase::setPosition(" + id + ", \"16 16 500\");");
        Assert.True(Bits(p.Rotation[2]) == Bits(back[2]));
    }

    // ---- narrow phase (collision 5.1 - 5.3) ----

    private static void TestNarrowPhase()
    {
        var f = new Fixture("player_narrow");
        using var chop = X87.Chop();
        Container.Entry? te = null;
        foreach (object? o in f.W.Game.World.Contents(null))
            if (f.W.Game.World.Find(o)!.TerrainData != null)
                te = f.W.Game.World.Find(o);
        Assert.True(te != null && te.Gravity[2] == -20.0f);
        // FC3 (0x005193cc, docs/static-sim.md TD8): the z range of the terrain box is the terrain position +-contHeight
        // (default 10000), hmin and hmax are not used. A contHeight of 0 gives +-hmax. [live] staticdiff ch_item_hmax.
        Assert.True(te!.Box[2] == -10000.0f && te.Box[5] == 10000.0f);
        f.W.Run("instant SimTerrain \"Terrain2\" { tedFileName = \"m.dtf\"; position = \"1000 1000 5\"; contHeight = 40; };" +
                "instant SimTerrain \"Terrain3\" { tedFileName = \"m.dtf\"; position = \"3000 3000 5\"; contHeight = 0; };");
        int extra = 0;
        foreach (object? o in f.W.Game.World.Contents(null))
        {
            Container.Entry e = f.W.Game.World.Find(o)!;
            if (e.TerrainData == null || e.Box[0] == te.Box[0])
                continue;
            ++extra;
            if (e.Box[0] == 1000.0f)
                Assert.True(e.Box[2] == -35.0f && e.Box[5] == 45.0f);
            else
                Assert.True(e.Box[0] == 3000.0f && e.Box[2] == 5.0f - kFlat && e.Box[5] == 5.0f + kFlat);
        }
        Assert.True(extra == 2);
        // 5.1: the sphere centre (0, 0, r) against the plane under it.
        var hit = new CollisionHit();
        const float r = 1.125f;
        float[] above = { 10, 10, kFlat - 0.5f };
        Assert.True(Collision.CollideTerrain(te.TerrainData!, above, r, hit));
        Assert.True(hit.Surfaces.Count == 1 && hit.Surfaces[0].Distance == 0.625f && hit.Surfaces[0].Normal[2] == 1.0f);
        Assert.True(hit.StepHeight == -1.0f);
        float[] below = { 10, 10, kFlat - 1.5f }; // centre 0.375 under the plane: d < 0 still collides
        Assert.True(Collision.CollideTerrain(te.TerrainData!, below, r, hit) && hit.Surfaces[0].Distance == -0.375f);
        float[] high = { 10, 10, kFlat + 0.01f };
        Assert.True(!Collision.CollideTerrain(te.TerrainData!, high, r, hit));
        float[] off = { -10, 10, kFlat };
        Assert.True(!Collision.CollideTerrain(te.TerrainData!, off, r, hit));

        // 5.2: box against a solid box interior (x -2..2, y -1..1, z 0..0.8) at the origin.
        ResourceManager res = f.W.Res;
        InteriorGeometry? geo = Interior.LoadInterior(res, "high.dis");
        Assert.True(geo != null && geo.FindSurface(43, 1) == 1 && geo.FindSurface(43, 9) == -1 && geo.FindSurface(0, 1) == -1);
        float[] identity = { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, origin = { 0, 0, 0 };
        float[] bmin = { -0.5f, -0.5f, 0 }, bmax = { 0.5f, 0.5f, 2.25f };
        // The box reaches 0.1 into the -x face, feet 0.25 above the floor of the solid.
        float[] into = { -2.4f, 0.25f, 0.25f };
        Assert.True(Collision.CollideInteriorBox(geo!, identity, origin, into, bmin, bmax, hit));
        bool wall = false, top = false;
        foreach (CollisionSurface s in hit.Surfaces)
        {
            wall = wall || (s.Normal[0] == -1 && s.Normal[1] == 0 && s.Normal[2] == 0);
            top = top || s.Normal[2] == 1;
            Assert.True(s.Distance == 1.0f && s.HasMaterial); // interior contacts: distance 1.0
        }
        // Normals point out of the solid; the step height is the top of the intersection above the feet (0.8 - 0.25).
        Assert.True(wall && top && Math.Abs(hit.StepHeight - 0.55f) < 1e-6f);
        float[] clear = { -2.6f, 0.25f, 0.25f };
        Assert.True(!Collision.CollideInteriorBox(geo!, identity, origin, clear, bmin, bmax, hit));
        // B2-B5: an interior turned by 90 degrees about z at (10, 0, 0).
        float[] quarter = { 0, 1, 0, -1, 0, 0, 0, 0, 1 }, at = { 10, 0, 0 };
        float[] turned = { 10.25f, -2.4f, 0.25f };
        Assert.True(Collision.CollideInteriorBox(geo!, quarter, at, turned, bmin, bmax, hit));
        bool rotated = false;
        foreach (CollisionSurface s in hit.Surfaces)
        {
            float[] n = new float[3];
            Float3 sn = s.Normal;
            X87.MulVector(sn, hit.Rot, n);
            rotated = rotated || (n[0] == 0 && n[1] == -1 && n[2] == 0);
        }
        Assert.True(rotated);
        // 2.2: the world box of an interior is padded by 3.0.
        float[] padded = new float[6];
        Collision.InteriorWorldBox(geo!.Box, identity, at, padded);
        Assert.True(padded[0] == 5 && padded[3] == 15 && padded[2] == -3 && padded[5] == 3.8f);

        // 5.3: spheres collide when r1 + r2 > lenf(c1 - c2); the normal is (c1 - c2) / lenf.
        float[] c1 = { 0, 0, 1 }, c2 = { 2, 0, 1 }, c3 = { 2.5f, 0, 1 };
        Assert.True(Collision.CollideSpheres(c1, r, c2, r, identity, hit));
        Assert.True(hit.Surfaces[0].Normal[0] == -1.0f && hit.Surfaces[0].Distance == 2.0f && !hit.Surfaces[0].HasMaterial);
        Assert.True(!Collision.CollideSpheres(c1, r, c3, r, identity, hit));
        // lenf (0x005ed031): exact for 4^k, 1.5 for s = 2.
        float[] d2 = { 1, 1, 0 };
        Assert.True(X87.Lenf(d2) == 1.5f);
        // 5.4 approximation: a sphere 0.5 above the top face of a box rests on that face.
        float[] box = { -1, -1, 0, 1, 1, 2 }, centre = { 0.25f, 0, 2.5f };
        Assert.True(Collision.CollideSphereBox(box, identity, origin, centre, r, hit));
        Assert.True(hit.Surfaces[0].Normal[2] == 1.0f && hit.Surfaces[0].Distance == 0.5f);
    }

    // ---- stepped move, step-up, wall (collision 3, 4) ----

    private static void TestInteriorMoves()
    {
        var f = new Fixture("player_interior");
        Fixtures.World w = f.W;
        w.Run("instant InteriorShape \"Low\" { fileName = \"low.dis\"; position = \"20 10 50\"; };" +
              "instant InteriorShape \"High\" { fileName = \"high.dis\"; position = \"20 24 50\"; };");
        var none = new Move();
        var right = new Move();
        right.Right = 1.0f;
        // 4.2: a 0.4 m ledge is climbed in one step.
        Player p = f.Spawn("15 10 50.2");
        for (int i = 0; i < 40; ++i)
            p.UpdateMove(none);
        Assert.True(p.ContactFlag && p.Velocity[2] == 0.0f);
        float top = 0;
        for (int i = 0; i < 60 && p.Transform.P[0] < 19.5f; ++i)
        {
            p.UpdateMove(right);
            top = Math.Max(top, p.Transform.P[2]);
        }
        Assert.True(p.Transform.P[0] >= 19.5f && Math.Abs(top - 50.401f) < 2e-3f && p.ContactFlag);
        Assert.True(Math.Abs(p.Velocity[0] - 10.0f) < 1e-3f);
        // The same block 0.8 m high blocks (H > 0.6 r).
        Player q = f.Spawn("15 24 50.2");
        for (int i = 0; i < 40; ++i)
            q.UpdateMove(none);
        for (int i = 0; i < 60; ++i)
            q.UpdateMove(right);
        Assert.True(q.Transform.P[0] < 17.5f && q.Transform.P[0] > 17.4f && q.Transform.P[2] < 50.1f);
        Assert.True(q.Velocity[0] < 0.0f && q.Velocity[0] > -2e-4f && q.ContactFlag);
        // Ledge: walking off the low block leaves no contact (free fall).
        bool airborne = false;
        for (int i = 0; i < 60 && !airborne; ++i)
        {
            p.UpdateMove(right);
            airborne = !p.ContactFlag && p.Transform.P[0] > 22.0f;
        }
        Assert.True(airborne);
    }

    // ---- shapes with a collision detail, player against player (collision 5.3, 5.4, 6) ----

    private static void TestShapesAndPlayers()
    {
        var f = new Fixture("player_shapes");
        Fixtures.World w = f.W;
        // 6 T2.1: the boxes of the detail rooted at the node named "collision" (any case).
        float[] box = { -1, -1.5f, 0, 1, 1.5f, 2 };
        List<byte> dts = DtsCollisionFile(box);
        var boxes = new List<Float6>();
        Assert.True(Shape.ReadShapeCollisionBoxes(dts.ToArray(), boxes) && boxes.Count == 1);
        for (int i = 0; i < 6; ++i)
            Assert.True(Math.Abs(boxes[0][i] - box[i]) < 1e-5f);
        byte[] cut = dts.Take(dts.Count / 2).ToArray();
        Assert.True(!Shape.ReadShapeCollisionBoxes(cut, boxes) && boxes.Count == 0);
        w.Run("StaticShapeData Gen { shapeFile = \"gen\"; };" +
              "instant StaticShape \"G\" { dataBlock = \"Gen\"; position = \"40 40 50\"; };");
        Assert.True(w.Game.World.Find(w.Object("G")) != null);
        // A player dropped on the shape rests on the top face of its collision box (z 52).
        Player p = f.Spawn("40 40 53");
        for (int i = 0; i < 80; ++i)
            p.UpdateMove(new Move());
        Assert.True(p.ContactFlag && p.Velocity[2] == 0.0f && p.Transform.P[2] >= 52.0f && p.Transform.P[2] < 52.03f);
        // Walking into its side.
        Player q = f.Spawn("35 40 50.2");
        var right = new Move();
        right.Right = 1.0f;
        for (int i = 0; i < 40; ++i)
            q.UpdateMove(new Move());
        for (int i = 0; i < 60; ++i)
            q.UpdateMove(right);
        // The player creeps while the gate lets it (box edge short of 39) and is stopped there.
        Assert.True(q.Transform.P[0] > 38.2f && q.Transform.P[0] < 38.5f && Math.Abs(q.Velocity[0]) < 1e-3f);

        // 5.3 and the failed-move fallback (collision 3 notes, 0x00505814).
        Player a = f.Spawn("100 100 50.2");
        Player b = f.Spawn("104 100 50.2");
        for (int i = 0; i < 40; ++i)
        {
            a.UpdateMove(new Move());
            b.UpdateMove(new Move());
        }
        float bx = b.Transform.P[0];
        bool failed = false;
        for (int i = 0; i < 40 && !failed; ++i)
        {
            float before = a.Transform.P[0];
            a.UpdateMove(right);
            failed = a.Velocity[0] == 0.0f && a.Velocity[2] == 0.0f && a.Transform.P[0] == before && i > 5;
        }
        Assert.True(failed && !a.ContactFlag && a.AtRest);
        Assert.True(a.Transform.P[0] < bx - 1.0f && b.Transform.P[0] == bx); // gated by the boxes, not the spheres
        for (int i = 0; i < 30; ++i)
            a.UpdateMove(right);
        Assert.True(a.Transform.P[0] < bx && b.Transform.P[0] == bx);
    }

    // ---- Items against interiors and shapes (static-sim 5.3, collision T3, E5) ----

    private static void TestItemCollision()
    {
        var f = new Fixture("player_items");
        Fixtures.World w = f.W;
        w.Run("ItemData Brick { shapeFile = \"gen\"; mass = 1; friction = 1; elasticity = 0.2; };" +
              "StaticShapeData Gen { shapeFile = \"gen\"; };" +
              "instant InteriorShape \"High\" { fileName = \"high.dis\"; position = \"60 60 60\"; };" +
              "instant StaticShape \"G\" { dataBlock = \"Gen\"; position = \"90 60 80\"; };" +
              "instant Item \"OnInterior\" { dataBlock = \"Brick\"; position = \"60 60 64\"; };" +
              "instant Item \"OnShape\" { dataBlock = \"Brick\"; position = \"90 60 86\"; };" +
              "instant Item \"Turned\" { dataBlock = \"Brick\"; position = \"60 60 64\"; rotation = \"0 0 1.5707964\"; };");
        GameBase Settle(string name)
        {
            var it = w.Object(name) as GameBase;
            Assert.True(it != null && it.ShapeClass() == GameBase.ShapeKind.Item);
            for (int i = 0; i < 400 && !it!.AtRest; ++i)
                it.ItemPhysics(0.032f);
            return it!;
        }
        GameBase a = Settle("OnInterior");
        Assert.True(a.AtRest && a.Transform.P[2] > 60.7f && a.Transform.P[2] < 61.2f);
        Assert.True(Math.Abs(a.Transform.P[0] - 60.0f) < 0.05f && Math.Abs(a.Transform.P[1] - 60.0f) < 0.05f);
        GameBase b = Settle("OnShape");
        Assert.True(b.AtRest && b.Transform.P[2] > 82.0f && b.Transform.P[2] < 82.6f);
        GameBase t = Settle("Turned");
        Assert.True(t.AtRest && t.Transform.P[2] > 60.7f && t.Transform.P[2] < 61.2f);
        InteriorGeometry? geo = Interior.LoadInterior(w.Res, "high.dis");
        Assert.True(geo != null);
        float[] identity = { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, quarter = { 0, 1, 0, -1, 0, 0, 0, 0, 1 };
        float[] origin = { 0, 0, 0 }, at = { -2.6f, 0, 0.3f };
        float[] bmin = { -0.1f, -1.0f, 0 }, bmax = { 0.1f, 1.0f, 0.5f };
        float[] wide = { -1.0f, -0.1f, 0 }, wideMax = { 1.0f, 0.1f, 0.5f };
        var hit = new CollisionHit();
        Assert.True(!Collision.CollideInteriorBox(geo!, identity, origin, at, bmin, bmax, hit, identity));
        Assert.True(Collision.CollideInteriorBox(geo!, identity, origin, at, bmin, bmax, hit, quarter));
        Assert.True(Collision.CollideInteriorBox(geo!, identity, origin, at, wide, wideMax, hit, identity));
        Assert.True(!Collision.CollideInteriorBox(geo!, identity, origin, at, wide, wideMax, hit, quarter));
    }

    private static DtsSpec.Xform X(short[] q, float[] t) => new(q, t);
    private static readonly short[] kQ0 = { 0, 0, 0, 32767 };

    // ---- requested animations on the server (docs/player-sim.md 5.5) ----

    private static void TestAnimationWrap()
    {
        var f = new Fixture("player_anim");
        Fixtures.World w = f.W;
        var body = new DtsSpec();
        body.Names = new List<string> { "bounds", "root", "torso", "run", "wave" };
        body.Nodes = new() { new(0, -1, 0, 0, 0), new(1, 0, 0, 0, 0) };
        body.Transforms = new() { X(kQ0, new float[] { 0, 0, 0 }) };
        body.Objects = new() { new(2, 0, 0, 1, new float[] { 0, 0, 0 }, 0, 0) };
        body.Details = new() { new(1, 10.0f) };
        body.Sequences = new() { new(1, 1, 0.03125f, 0), new(3, 1, 0.5f, 0), new(4, 0, 0.5f, 0) };
        float[] torso = { -0.3f, -0.3f, 0, 0.3f, 0.3f, 2 };
        body.Meshes = new() { DtsBoxMesh(torso) };
        PutFile(Path.Combine(f.Root, "base/animbody.dts"), DtsModelFile(body));
        w.Res.SetSearchPath("base");
        w.Run("PlayerData aarmor { shapeFile = \"animbody\"; maxForwardSpeed = 11; maxSideSpeed = 10; mass = 9; drag = 1;" +
              " groundForce = 360; groundTraction = 3; maxEnergy = 60; maxDamage = 0.5; boxWidth = 0.5; boxDepth = 0.5;" +
              " boxNormalHeight = 2.25; boxCrouchHeight = 1.75;" +
              " animData[0] = { root, none, 1, true, true, true, false, 0 };" +
              " animData[1] = { run, none, 1, true, false, true, false, 3 };" +
              " animData[2] = { wave, none, 1, true, false, true, false, 3 };" +
              " animData[3] = { run, none, -1, true, false, true, false, 3 };" +
              " animData[4] = { wave, none, 1, true, false, true, false, 4 }; };");
        Player? p = Player.From(w.Object(w.Run("echo(spawnPlayer(aarmor, \"40 40 50.1\", \"0 0 0\"));")));
        Assert.True(p != null && p.PData.AnimCyclic[1] && !p.PData.AnimCyclic[2] && p.PData.AnimDuration[1] == 0.5f);
        string id = Itoa(p!.Id());
        void Step(int n)
        {
            for (int i = 0; i < n; ++i)
                p.UpdateAnimation(0.032f);
        }
        Step(2);
        Assert.True(p.CurrentAnim == 0 && p.PendingAnim == -1);
        // One-shot: 0.5 s at 0.064 per step stops at 1.0 on the 16th step and is finished on the 17th.
        w.Run("Player::setAnimation(" + id + ", 2);");
        Step(15);
        Assert.True(p.CurrentAnim == 2 && p.PendingAnim == 2 && p.AnimPosition > 0.95f && p.AnimPosition < 0.99f);
        Step(1);
        Assert.True(p.AnimPosition == 1.0f && p.PendingAnim == 2);
        Step(1);
        Assert.True(p.PendingAnim == -1 && p.CurrentAnim == 0 && p.AnimPosition == 0.0f);
        // Cyclic (0x005b8d94).
        w.Run("Player::setAnimation(" + id + ", 1);");
        Step(15);
        Assert.True(p.AnimPosition > 0.95f && p.AnimPosition < 0.97f);
        Step(1);
        Assert.True(p.CurrentAnim == 1 && p.PendingAnim == 1 && p.AnimPosition > 0.02f && p.AnimPosition < 0.03f);
        w.Run("Player::setAnimation(" + id + ", 2);");
        Assert.True(p.PendingAnim == 1);
        for (int i = 0; i < 16; ++i)
        {
            Step(1);
            Assert.True(p.AnimPosition >= 0.0f && p.AnimPosition <= 1.0f);
        }
        Assert.True(p.PendingAnim == 1 && p.CurrentAnim == 1);
        w.Run("Player::setAnimation(" + id + ", 4);");
        Assert.True(p.PendingAnim == 4 && p.CurrentAnim == 4 && p.AnimPosition == 0.0f);
        Step(18);
        Assert.True(p.PendingAnim == -1 && p.CurrentAnim == 0);
        // Reversed entry (direction <= 0): starts at 0.99 and still advances forward.
        w.Run("Player::setAnimation(" + id + ", 3);");
        Assert.True(p.CurrentAnim == 3 && Bits(p.AnimPosition) == 0x3f7d70a4u);
        Step(1);
        Assert.True(p.PendingAnim == 3 && p.AnimPosition > 0.05f && p.AnimPosition < 0.06f);
    }

    // ---- Triggers against the Player model (docs/collision.md T6, E2, 11.8; docs/static-sim.md 6.2) ----

    private static void TestTriggers()
    {
        // m_Box3F_collideOrientedBox (0x005ec754).
        {
            using var chop = X87.Chop();
            float[] one = { 1, 1, 1 }, idm = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            float[] touch = { 2, 0, 0 }, apart = { 2.001f, 0, 0 }, corner = { 2, 2, 2 }, off = { 2, 2, 2.001f };
            Assert.True(Shape.OrientedBoxesOverlap(one, one, idm, touch) && !Shape.OrientedBoxesOverlap(one, one, idm, apart));
            Assert.True(Shape.OrientedBoxesOverlap(one, one, idm, corner) && !Shape.OrientedBoxesOverlap(one, one, idm, off));
            const float c = 0.70710678f;
            float[] turned = { c, c, 0, -c, c, 0, 0, 0, 1 };
            float[] inn = { 2.41f, 0, 0 }, outt = { 2.42f, 0, 0 };
            Assert.True(Shape.OrientedBoxesOverlap(one, one, turned, inn) && !Shape.OrientedBoxesOverlap(one, one, turned, outt));
            float[] rod = { 2, 0.05f, 0.05f }, cross = { 0, 1, 0, -1, 0, 0, 0, 0, 1 };
            float[] apartZ = { 0, 0, 0.2f }, touchZ = { 0, 0, 0.1f };
            Assert.True(!Shape.OrientedBoxesOverlap(rod, rod, cross, apartZ) && Shape.OrientedBoxesOverlap(rod, rod, cross, touchZ));
            float[] diagRod = { 0.05f, 2, 0.05f }, yz = { 1, 0, 0, 0, c, c, 0, -c, c };
            float[] beside = { 0, -0.2f, 0.2f }, crossing = { 0, -0.05f, 0.05f };
            Assert.True(!Shape.OrientedBoxesOverlap(rod, diagRod, yz, beside) && Shape.OrientedBoxesOverlap(rod, diagRod, yz, crossing));
        }

        var f = new Fixture("player_trigger");
        Fixtures.World w = f.W;
        var body = new DtsSpec();
        body.Names = new List<string> { "bounds", "root", "torso" };
        body.Nodes = new() { new(0, -1, 0, 0, 0), new(1, 0, 0, 0, 1) };
        body.Transforms = new() { X(kQ0, new float[] { 0.25f, 0, 0 }), X(kQ0, new float[] { 0, 0, 0 }) };
        body.Objects = new() { new(2, 0, 0, 1, new float[] { 0, 0, 0 }, 0, 0) };
        body.Details = new() { new(1, 10.0f) };
        float[] torso = { -0.3f, -0.3f, 0, 0.3f, 0.3f, 2 };
        body.Meshes = new() { DtsBoxMesh(torso) };
        PutFile(Path.Combine(f.Root, "base/trigbody.dts"), DtsModelFile(body));
        w.Res.SetSearchPath("base");
        w.Run("PlayerData zarmor { shapeFile = \"trigbody\"; maxForwardSpeed = 11; maxSideSpeed = 10; mass = 9; drag = 1;" +
              " groundForce = 360; groundTraction = 3; jumpSurfaceMinDot = 0.25; maxEnergy = 60; maxDamage = 0.5;" +
              " boxWidth = 0.5; boxDepth = 0.5; boxNormalHeight = 2.25; boxCrouchHeight = 1.75; minDamageSpeed = 25;" +
              " damageScale = 0.0078125; };" +
              "TriggerData Zone;" +
              "function Zone::onEnter(%t, %o) { $zl = $zl @ \"E\" @ $zi[%t] @ $zp[%o]; }" +
              "function Zone::onContact(%t, %o) { $zl = $zl @ \"C\" @ $zi[%t] @ $zp[%o]; }" +
              "function Zone::onLeave(%t, %o) { $zl = $zl @ \"L\" @ $zi[%t] @ $zp[%o]; }");
        GameBase Trigger(string tag, string pos, string rot, string box, string sphere)
        {
            w.Run("instant Trigger \"Z" + tag + "\" { dataBlock = \"Zone\"; position = \"" + pos +
                  "\"; rotation = \"" + rot + "\"; boundingBox = \"" + box + "\"; isSphere = \"" + sphere + "\"; };" +
                  "$zi[nameToId(\"Z" + tag + "\")] = \"" + tag + "\";");
            var t = w.Object("Z" + tag) as GameBase;
            Assert.True(t != null);
            return t!;
        }
        Player Spawn(string tag, string pos)
        {
            Player? p = Player.From(w.Object(w.Run("echo(spawnPlayer(zarmor, \"" + pos + "\", \"0 0 0\"));")));
            Assert.True(p != null);
            w.Run("$zp[" + Itoa(p!.Id()) + "] = \"" + tag + "\";");
            return p;
        }
        void Ticks(int n)
        {
            for (int i = 0; i < n; ++i)
                w.Sim.AdvanceRealTime(32);
        }
        string Log() => w.Run("echo($zl);");
        Ticks(4);
        GameBase a = Trigger("a", "40.75 40 50.5", "0 0 0", "-0.25 -0.25 -0.25 0.25 0.25 0.25", "False");
        GameBase b = Trigger("b", "60.9 40 50.5", "0 0 0", "-0.2 -0.2 -0.2 0.2 0.2 0.2", "False");
        GameBase s = Trigger("s", "80 40.5 51", "0 0 0", "-0.3 -0.2 -0.4 0.3 0.2 0.4", "True");
        Assert.True(!a.TriggerSphere && a.TriggerBox[0] == -0.25f && a.TriggerBox[5] == 0.25f);
        Assert.True(s.TriggerSphere);
        {
            var images = new List<CollisionImage>();
            float[] qa = { 40, 39.5f, 50, 41, 40.5f, 52 }, qs = { 79.5f, 40, 50, 80.5f, 41, 52 };
            w.Game.CollisionCandidates(a, 0x10, qa, images);
            Assert.True(images.Count == 0); // the mover itself is excluded
            w.Game.CollisionCandidates(b, 0x10, qa, images);
            Assert.True(images.Count == 1 && images[0].ImageKind == CollisionImage.Kind.Trigger && images[0].NonBlocking);
            Assert.True(images[0].Box[0] == a.TriggerBox[0] && images[0].Box[3] == a.TriggerBox[3] && images[0].Object == a);
            images.Clear();
            w.Game.CollisionCandidates(a, 0x10, qs, images);
            Assert.True(images.Count == 1 && images[0].ImageKind == CollisionImage.Kind.TriggerSphere);
            Assert.True(Math.Abs(images[0].Radius - 0.4f) < 1e-6f && images[0].Centre[0] == 0 && images[0].Centre[2] == 0);
            images.Clear();
            float[] edge = { 39.5f, 39.5f, 50, 40.5f, 40.5f, 52 }, low = { 41.0f, 39.5f, 50, 41.5f, 40.5f, 52 };
            w.Game.CollisionCandidates(b, 0x10, edge, images);
            Assert.True(images.Count == 0);
            w.Game.CollisionCandidates(b, 0x10, low, images);
            Assert.True(images.Count == 1);
        }
        Spawn("1", "40 40 50.1");
        Player pb = Spawn("2", "60.45 40 50.1");
        Ticks(3);
        Assert.True(Log() == "Eb2" && b.Occupants.Count == 1 && a.Occupants.Count == 0);
        Ticks(9);
        Assert.True(Log() == "Eb2Cb2");
        w.Run("GameBase::setPosition(" + Itoa(pb.Id()) + ", \"60.45 44 50.1\");");
        Ticks(12);
        Assert.True(Log() == "Eb2Cb2Cb2Lb2" && b.Occupants.Count == 0);
        w.Run("$zl = \"\";");
        Spawn("3", "60 40 50.1");
        GameBase flat = Trigger("f", "100.59 40 50.5", "0 0 0", "-0.2 -0.2 -0.2 0.2 0.2 0.2", "False");
        GameBase turnedT = Trigger("t", "120.57 40 50.5", "0 0 0.785398", "-0.2 -0.2 -0.2 0.2 0.2 0.2", "False");
        Spawn("4", "100 40 50.1");
        Player pt = Spawn("5", "120 40 50.1");
        Ticks(3);
        Assert.True(flat.Occupants.Count == 0 && turnedT.Occupants.Count == 1 && Log() == "Et5");
        w.Run("$zl = \"\";");
        Player ps = Spawn("6", "80 40 50.1");
        Ticks(3);
        Assert.True(Log() == "Es6" && s.Occupants.Count == 1);
        w.Run("$zl = \"\"; Player::kill(" + Itoa(ps.Id()) + ");");
        Ticks(12);
        Assert.True(Log() == "Ct5Cs6Ls6" && s.Occupants.Count == 0);
        int freed = pt.Id();
        w.Run("$zl = \"\"; deleteObject(" + Itoa(freed) + ");");
        Assert.True(turnedT.Occupants.Count == 0);
        Player reborn = Spawn("7", "140 40 50.1");
        Assert.True(reborn.Id() == freed);
        Ticks(12);
        w.Run("GameBase::setActive(\"Zt\", false);");
        Assert.True(Log() == "" && turnedT.Occupants.Count == 0);
    }

    // ---- shape images in both directions, trigger sphere, Item events (collision 6 T3, T4, 8 E1) ----

    private static void TestMeshContacts()
    {
        var f = new Fixture("player_mesh");
        Fixtures.World w = f.W;
        var body = new DtsSpec();
        body.Names = new List<string> { "bounds", "root", "torso" };
        body.Nodes = new() { new(0, -1, 0, 0, 0), new(1, 0, 0, 0, 0) };
        body.Transforms = new() { X(kQ0, new float[] { 0, 0, 0 }) };
        body.Objects = new() { new(2, 0, 0, 1, new float[] { 0, 0, 0 }, 0, 0) };
        body.Details = new() { new(1, 10.0f) };
        float[] torso = { -0.3f, -0.3f, 0, 0.3f, 0.3f, 2 };
        body.Meshes = new() { DtsBoxMesh(torso) };
        var pole = new DtsSpec();
        pole.Names = new List<string> { "bounds", "root", "foot", "top" };
        pole.Nodes = new() { new(0, -1, 0, 0, 0), new(1, 0, 0, 0, 0) };
        pole.Transforms = new() { X(kQ0, new float[] { 0, 0, 0 }) };
        pole.Objects = new() { new(2, 0, 0, 1, new float[] { 0, 0, 0 }, 0, 0), new(3, 0, 1, 1, new float[] { 0, 0, 0 }, 0, 0) };
        pole.Details = new() { new(1, 10.0f) };
        pole.Radius = 1.0f;
        float[] foot = { -0.2f, -0.2f, 0, 0.2f, 0.2f, 0.2f }, top = { -0.2f, -0.2f, 1.8f, 0.2f, 0.2f, 2 };
        pole.Box = new float[] { -0.2f, -0.2f, 0, 0.2f, 0.2f, 2 };
        pole.Meshes = new() { DtsBoxMesh(foot), DtsBoxMesh(top) };
        PutFile(Path.Combine(f.Root, "base/meshbody.dts"), DtsModelFile(body));
        PutFile(Path.Combine(f.Root, "base/meshpole.dts"), DtsModelFile(pole));
        w.Res.SetSearchPath("base");
        w.Run("PlayerData marmor { shapeFile = \"meshbody\"; maxForwardSpeed = 11; maxSideSpeed = 10; mass = 9; drag = 1;" +
              " groundForce = 360; groundTraction = 3; jumpSurfaceMinDot = 0.25; maxEnergy = 60; maxDamage = 0.5;" +
              " boxWidth = 0.5; boxDepth = 0.5; boxNormalHeight = 2.25; boxCrouchHeight = 1.75; minDamageSpeed = 25;" +
              " damageScale = 0.0078125; };" +
              "StaticShapeData Pad { shapeFile = \"gen\"; triggerRadius = 3; };" +
              "ItemData Pole { shapeFile = \"meshpole\"; mass = 1; friction = 1; elasticity = 0.2; };" +
              "instant StaticShape \"S\" { dataBlock = \"Pad\"; position = \"40 40 50\"; };" +
              "function Pad::onCollision(%t, %o) { $touch = $touch @ %o @ \";\"; }" +
              "function Pole::onCollision(%t, %o) { $pick = $pick @ %t @ \">\" @ %o @ \";\"; }");
        Player Spawn(string pos)
        {
            Player? p = Player.From(w.Object(w.Run("echo(spawnPlayer(marmor, \"" + pos + "\", \"0 0 0\"));")));
            Assert.True(p != null);
            return p!;
        }
        void Ticks(int n)
        {
            for (int i = 0; i < n; ++i)
                w.Sim.AdvanceRealTime(32);
        }
        Ticks(4);
        Player near = Spawn("40 37 50.1");
        Player far = Spawn("40 32 50.1");
        Ticks(12);
        string nearId = Itoa(near.Id());
        Assert.True(w.Run("echo($touch);") == nearId + ";");
        Assert.True(Math.Abs(near.Transform.P[1] - 37.0f) < 1e-3f && near.ContactFlag && far.ContactFlag);
        Ticks(40); // 1.28 s later: the second callback, still none for the far Player
        Assert.True(w.Run("echo($touch);") == nearId + ";" + nearId + ";");
        {
            var images = new List<CollisionImage>();
            float[] query = { 39, 36, 50, 41, 38, 52 };
            w.Game.CollisionCandidates(near, 0x800000u, query, images);
            Assert.True(images.Count == 2 && images[0].ImageKind == CollisionImage.Kind.Shape && !images[0].NonBlocking);
            Assert.True(images[0].Instance != null && images[0].MeshMode && images[0].Detail == 0);
            Assert.True(images[1].ImageKind == CollisionImage.Kind.TriggerSphere && images[1].NonBlocking);
            Assert.True(images[1].Radius == 3.0f && images[1].Object == images[0].Object);
        }

        Player target = Spawn("90 90 50.1");
        Ticks(12);
        w.Run("instant Item \"Thrown\" { dataBlock = \"Pole\"; position = \"90 89.2 50.5\"; };");
        var thrown = w.Object("Thrown") as GameBase;
        Assert.True(thrown != null && thrown.ShapeClass() == GameBase.ShapeKind.Item);
        {
            var image = new CollisionImage();
            Assert.True(w.Game.ShapeImage(thrown!, image) && !image.MeshMode && image.NonBlocking && image.Instance != null);
        }
        thrown!.Velocity[1] = 6.0f;
        w.Run("$pick = \"\";");
        thrown.ItemPhysics(0.032f);
        Assert.True(w.Run("echo($pick);").Length == 0);
        Ticks(1); // the event is handled at the start of the next tick
        Assert.True(w.Run("echo($pick);") == Itoa(thrown.Id()) + ">" + Itoa(target.Id()) + ";");

        Player host = Spawn("140 140 50.1");
        Ticks(12);
        w.Run("instant Item \"Dropped\" { dataBlock = \"Pole\"; position = \"140 140 54\"; };");
        var dropped = (GameBase)w.Object("Dropped")!;
        bool caught = false;
        for (int i = 0; i < 120 && !caught; ++i)
        {
            dropped.ItemPhysics(0.032f);
            caught = dropped.Velocity[2] == 0.0f && dropped.Transform.P[2] < 52.5f;
        }
        Assert.True(caught && !dropped.AtRest && dropped.Velocity[0] == 0.0f && dropped.Velocity[1] == 0.0f);
        Assert.True(dropped.Transform.P[2] > host.Transform.P[2] + 0.1f && dropped.Transform.P[2] < host.Transform.P[2] + 1.2f);
    }

    // ---- touch order of a corpse and a flag after a teleport (docs/collision.md 11.13, ROADMAP G20) ----

    private static void TestTouchOrder()
    {
        var f = new Fixture("player_touch_order");
        Fixtures.World w = f.W;
        w.Run("ItemData Flag2 { shapeFile = \"gen\"; mass = 1; friction = 1; elasticity = 0.2; };" +
              "function Flag2::onCollision(%t, %o) { $log = $log @ \"I\" @ %o @ \";\"; }" +
              "function Player::onCollision(%t, %o) { $log = $log @ \"P\" @ %t @ \"<\" @ %o @ \";\"; }");
        void Ticks(int n)
        {
            for (int i = 0; i < n; ++i)
                w.Sim.AdvanceRealTime(32);
        }
        Player corpse = f.Spawn("100 100 50.1");
        Ticks(12);
        w.Run("Player::kill(" + Itoa(corpse.Id()) + ");");
        w.Run("instant Item \"Flag\" { dataBlock = \"Flag2\"; position = \"100 100 50.1\"; };");
        Player alpha = f.Spawn("130 100 50.1");
        alpha.ControlClient = 1; // moves come from a client, the tick steps nothing
        Ticks(12);
        w.Run("$log = \"\";");
        w.Run("GameBase::setPosition(" + Itoa(alpha.Id()) + ", \"100.5 100 50.1\");");
        alpha.ApplyMoves(new[] { new Move() }, 1);
        Ticks(2);
        string a = Itoa(alpha.Id()), c = Itoa(corpse.Id());
        string want = "P" + c + "<" + a + ";I" + a + ";";
        string log = w.Run("echo($log);");
        Assert.StartsWith(want, log, StringComparison.Ordinal);
    }

    // ---- Moveable riders and doors (collision E3, static-sim 2.5) ----

    private static void TestRiders()
    {
        var f = new Fixture("player_riders");
        Fixtures.World w = f.W;
        w.Run("MarkerData PathMarker;" +
              "MoveableData PLift { shapeFile = \"gen\"; speed = 10; };" +
              "MoveableData PDoor { shapeFile = \"gen\"; speed = 10; displace = false; blockForward = true; };" +
              "instant SimGroup \"lift\" {" +
              " instant Moveable \"PL\" { dataBlock = \"PLift\"; };" +
              " instant SimPath \"Path\" {" +
              "  instant Marker \"m0\" { dataBlock = \"PathMarker\"; position = \"70 70 60\"; };" +
              "  instant Marker \"m1\" { dataBlock = \"PathMarker\"; position = \"70 70 65\"; }; }; };");
        var lift = w.Object("lift/PL") as GameBase;
        Assert.True(lift != null && lift.ShapeClass() == GameBase.ShapeKind.Moveable);
        Player p = f.Spawn("70 70 62.5");
        void Step(int n)
        {
            for (int i = 0; i < n; ++i)
            {
                w.Sim.AdvanceRealTime(32);
                p.UpdateMove(new Move());
            }
        }
        Step(40);
        Assert.True(p.ContactFlag && Math.Abs(p.Transform.P[2] - 62.0f) < 0.1f);
        Assert.Contains(p.Id(), lift!.Carried);
        float before = p.Transform.P[2];
        w.Run("Moveable::moveToWaypoint(\"lift/PL\", 1);");
        Step(60);
        Assert.True(Math.Abs(lift.Transform.P[2] - 65.0f) < 0.01f);
        Assert.True(Math.Abs((p.Transform.P[2] - before) - 5.0f) < 0.05f && p.ContactFlag);

        w.Run("instant SimGroup \"gate\" {" +
              " instant Moveable \"Dr\" { dataBlock = \"PDoor\"; };" +
              " instant SimPath \"Path\" {" +
              "  instant Marker \"d0\" { dataBlock = \"PathMarker\"; position = \"100 100 60\"; };" +
              "  instant Marker \"d1\" { dataBlock = \"PathMarker\"; position = \"100 100 49\"; }; }; };");
        var door = w.Object("gate/Dr") as GameBase;
        Assert.True(door != null && door.ShapeClass() == GameBase.ShapeKind.Moveable);
        Player q = f.Spawn("100 100 50.2");
        for (int i = 0; i < 40; ++i)
        {
            w.Sim.AdvanceRealTime(32);
            q.UpdateMove(new Move());
        }
        float standing = q.Transform.P[2];
        w.Run("Moveable::moveToWaypoint(\"gate/Dr\", 1);");
        for (int i = 0; i < 100; ++i)
        {
            w.Sim.AdvanceRealTime(32);
            q.UpdateMove(new Move());
        }
        Assert.True(door!.MoveBlocked && door.Transform.P[2] > 51.0f && door.Transform.P[2] < 59.9f);
        Assert.True(q.Transform.P[2] == standing && q.Transform.P[0] == 100.0f);
        Assert.Contains(p.Id(), lift.Carried);
        int riderId = p.Id();
        w.Run("deleteObject(" + Itoa(riderId) + ");");
        Assert.True(lift.Carried.Count == 0);
    }

    // ---- repair tick, throw direction, fade (player-sim 6.3, static-sim 5.4, 5.6) ----

    // G45: 0x00415108 zeroes the pitch when $pref::freeLook goes from TRUE to FALSE, once.
    private static void TestFreeLookOff()
    {
        var f = new Fixture("player_freelook");
        Player p = f.Spawn("30 30 50.2");
        p.Pitch = 0.5f;
        p.ServerTick(0);
        Assert.True(p.Pitch == 0.5f && p.FreeLookCopy);
        f.W.Run("$pref::freeLook = false;");
        p.ServerTick(0);
        Assert.True(p.Pitch == 0 && !p.FreeLookCopy);
        p.Pitch = 0.5f;
        p.ServerTick(0);
        Assert.True(p.Pitch == 0.5f);
        f.W.Run("$pref::freeLook = true;");
        p.ServerTick(0);
        Assert.True(p.FreeLookCopy);
    }

    private static void TestRepairThrowFade()
    {
        var f = new Fixture("player_repair");
        Fixtures.World w = f.W;
        Player p = f.Spawn("30 30 50.2");
        string id = Itoa(p.Id());
        w.Run("GameBase::setDamageLevel(" + id + ", 0.3);");
        float start = p.Damage;
        w.Run("GameBase::repairDamage(" + id + ", 0.1);");
        p.ServerTick(0);
        Assert.True(Math.Abs((start - p.Damage) - 0.0032f) < 1e-6f && Math.Abs(p.RepairPending - (0.1f - 0.0032f)) < 1e-6f);
        for (int i = 0; i < 40; ++i)
            p.ServerTick(0);
        Assert.True(p.RepairPending == 0.0f && Math.Abs((start - p.Damage) - 0.1f) < 1e-5f);
        w.Run("GameBase::setAutoRepairRate(" + id + ", 0.02);");
        float before = p.Damage;
        p.ServerTick(0);
        Assert.True(Math.Abs((before - p.Damage) - 0.00064f) < 1e-6f);
        Assert.True(!p.Dead); // the repair tick never goes through the Player damage override
        float[] dir = new float[3];
        Assert.True(p.ThrowDirection(dir));
        Assert.True(Math.Abs(dir[0]) < 1e-6f && Math.Abs(dir[1] - MathF.Cos(0.5f)) < 1e-6f && Math.Abs(dir[2] - MathF.Sin(0.5f)) < 1e-6f);
        p.StartFade(2);
        Assert.True(p.FadeState == 2);
        w.Sim.AdvanceRealTime(2496);
        p.UpdateFade();
        Assert.True(p.FadeState == 2);
        w.Sim.AdvanceRealTime(32);
        p.UpdateFade();
        Assert.True(p.FadeState == 0);
        p.StartFade(1); // a fade out stays until the object goes away
        w.Sim.AdvanceRealTime(5000);
        p.UpdateFade();
        Assert.True(p.FadeState == 1);
    }

    // ---- contact response: landing damage, item callback (collision 4.3, 8 E1.4) ----

    private static void TestCallbacks()
    {
        var f = new Fixture("player_callbacks");
        Fixtures.World w = f.W;
        var c = new Cases();
        w.Run("function Player::onDamage(%this,%type,%value,%pos,%vec,%mom,%vertPos,%quadrant,%object) {" +
              " $dmg = %type @ \"|\" @ %value @ \"|\" @ %pos @ \"|\" @ %vertPos @ \"|\" @ %quadrant @ \"|\" @ %object; }");
        Player p = f.Spawn("16 16 85");
        string id = Itoa(p.Id());
        float impact = 0;
        for (int i = 0; i < 80 && !p.ContactFlag; ++i)
        {
            impact = p.Velocity[2];
            p.UpdateMove(new Move());
        }
        Assert.True(p.ContactFlag && p.LastDamage > 0.0f);
        float vn = SubF(impact, 0.64f);
        Assert.True(Math.Abs(p.LastDamage - MulF(0.0078125f, SubF(-vn, 25.0f))) < 1e-6f);
        string dmg = w.Run("echo($dmg);");
        Assert.True(dmg.StartsWith("0|", StringComparison.Ordinal) && dmg.Contains("|0 0 0|legs|", StringComparison.Ordinal) && dmg[^1] == '0');
        w.Run("function Player::onKilled(%this) { $killed = %this; }");
        c.Check(w, "echo(Player::isDead(" + id + "));", "False");
        c.Check(w, "echo(GameBase::setDamageLevel(" + id + ", 9), Player::isDead(" + id + "), $killed == " + id + ");", "TrueTrue1");
        Assert.True(p.Damage == 0.5f && p.ObjectTypeBits == Player.kCorpseType && p.CollisionMask == Player.kDeadMask);
        c.Check(w, "echo(GameBase::setDamageLevel(" + id + ", 0.1), Player::isDead(" + id + "));", "TrueFalse");
        Assert.True(p.ObjectTypeBits == 0x80 && p.CollisionMask == Player.kLiveMask);
        c.Check(w, "echo(Player::kill(" + id + "), Player::kill(" + id + "), Player::isDead(" + id + "));", "TrueTrueTrue");
        c.Check(w, "echo(Player::kill());", "wrong argcount to kill(player)\nFalse");
        c.Check(w, "echo(Player::isDead(8), Player::blowUp(8), Player::isCrouching(8), Player::getLastContactCount(8));",
                "FalseTrue-1-1");
        w.Run("ItemData Flg { className = \"Flag\"; };" +
              "instant Item \"F1\" { dataBlock = \"Flg\"; position = \"16 16 50\"; };" +
              "function Item::onCollision(%this, %obj) { $hits = $hits @ %this @ \">\" @ %obj @ \";\"; }");
        Player q = f.Spawn("16 16 51");
        var item = (GameBase)w.Object("F1")!;
        w.Sim.AdvanceRealTime(64);
        w.Game.DispatchCollisionEvent(item, q);
        w.Game.DispatchCollisionEvent(item, q); // throttled: expiry = now + 1.0
        string want = Itoa(item.Id()) + ">" + Itoa(q.Id()) + ";";
        c.Check(w, "echo($hits);", want);
        w.Run("function Player::onCollision(%this, %obj) { $phits = $phits @ %obj @ \";\"; }");
        w.Game.DispatchCollisionEvent(q, item);
        c.Check(w, "echo($phits);", Itoa(item.Id()) + ";");
        Assert.True(c.Failures == 0);
    }

    // ---- inventory and image commands (player-sim 7.1, 7.2, 7.6) ----

    private static void TestInventory()
    {
        var f = new Fixture("player_inventory");
        Fixtures.World w = f.W;
        var c = new Cases();
        w.Run("function Weapon::onMount(%p, %i) { $log = $log @ \"M\" @ %i; }" +
              "function Weapon::onUnmount(%p, %i) { $log = $log @ \"U\" @ %i; }" +
              "function Gun::onUse(%p, %i) { $log = $log @ \"use\" @ %i; }" +
              "function item::onDrop(%p, %i) { $log = $log @ \"drop\" @ %i; }");
        Player p = f.Spawn("16 16 60");
        string id = Itoa(p.Id());
        c.Check(w, "echo(getNumItems());", "2");
        c.Check(w, "echo(Player::incItemCount(" + id + ", Gun), Player::incItemCount(" + id + ", Gun, 4)," +
                   " Player::decItemCount(" + id + ", Gun, 2), Player::getItemCount(" + id + ", 0));", "1533");
        c.Check(w, "echo(Player::setItemCount(" + id + ", Kit, -5), Player::getItemClassCount(" + id + ", Weapon)," +
                   " Player::getItemClassCount(" + id + ", Tool));", "010");
        c.Check(w, "echo(Player::getItemCount(8, Gun));", "Player::getItemCount: could not find player\n0");
        c.Check(w, "echo(Player::getItemCount(" + id + ", Nope));", "Player::getItemCount: incorrect item type: Nope\n0");
        c.Check(w, "echo(Player::getItemCount(" + id + ", -1));", "0");
        c.Check(w, "echo(Player::getItemCount(" + id + "));", "wrong argcount to getItemCount(player,type)\n0");
        c.Check(w, "echo(Player::setItemCount(" + id + ", Gun));", "wrong argcount to setItemCount(player,type,count)\n0");
        c.Check(w, "echo(Player::mountItem(" + id + ", Gun, 0), Player::getMountedItem(" + id + ", 0)," +
                   " Player::getMountedItem(" + id + ", 1), $log);", "TrueGun-1MGun");
        Assert.True(p.Mass == 12.0f && p.Images[0].Item == 0);
        c.Check(w, "echo(Player::mountItem(" + id + ", Kit, 1));", "False"); // no image
        c.Check(w, "echo(Player::mountItem(" + id + ", Gun, 8));", "mountItem: invalid imageSlot\nFalse");
        c.Check(w, "echo(Player::getMountedItem(" + id + ", 9));", "Player::getMountedItem: invalid imageSlot\n-1");
        c.Check(w, "echo(Player::isTriggered(" + id + ", 0), Player::trigger(" + id + ", 0), Player::isTriggered(" + id +
                   ", 0), Player::trigger(" + id + ", 0, false), Player::isTriggered(" + id + ", 0));",
                "FalseTrueTrueTrueFalse");
        c.Check(w, "$log = \"\"; echo(Player::useItem(" + id + ", Gun), Player::dropItem(" + id + ", Gun), $log);",
                "TrueTrueuseGundropGun");
        c.Check(w, "$log = \"\"; echo(Player::setItemCount(" + id + ", Gun, 0), Player::getMountedItem(" + id + ", 0), $log);",
                "0-1UGun");
        Assert.True(p.Mass == 9.0f);
        c.Check(w, "echo(Player::useItem(" + id + ", Gun), $log);", "TrueUGun"); // count 0: no callback
        Assert.True(p.SetItemCount(200, 5) == 0 && p.SetItemCount(-1, 5) == 0 && p.ItemCountOf(5000) == 0);
        p.SetItemCount(1, 7);
        int[]? counts = Player.InventoryCounts(p);
        Assert.True(counts != null && counts[1] == 7 && Player.InventoryCounts(null) == null);
        c.Check(w, "echo(Player::setAnimation(" + id + ", 500), Player::setAnimation(" + id + ", -3));", "TrueTrue");
        Assert.True(p.PendingAnim == 0);
        c.Check(w, "echo(Player::setAnimation(" + id + "));", "wrong argcount to Player::setAnimation(player,anim)\nFalse");
        c.Check(w, "echo(Player::setDamageFlash(" + id + ", 3), Player::getDamageFlash(" + id + "));", "True1");
        c.Check(w, "echo(Player::applyImpulse(" + id + ", \"9 0 0\"));", "False");
        Assert.True(p.Velocity[0] == Scaled(9.0f, Inverse(9.0f)));
        Assert.True(c.Failures == 0);
    }

    // ---- item image state machine (player-sim 7.5) ----

    private static void TestImageStateMachine()
    {
        var f = new Fixture("player_images");
        Fixtures.World w = f.W;
        var c = new Cases();
        w.Run("ItemData Bullets { className = \"Ammo\"; };" +
              "ItemImageData RifleImage { shapeFile = \"gen\"; weaponType = 0; ammoType = Bullets; activateTime = 0.1;" +
              " fireTime = 0.064; reloadTime = 0.064; };" +
              "ItemData Rifle { className = \"Weapon\"; imageType = RifleImage; };" +
              "ItemImageData BeamImage { shapeFile = \"gen\"; weaponType = 2; activateTime = 0; fireTime = 0.064;" +
              " reloadTime = 0; minEnergy = 5; maxEnergy = 10; };" +
              "ItemData Beam { className = \"Weapon\"; imageType = BeamImage; };" +
              "ItemImageData SpinImage { shapeFile = \"gen\"; weaponType = 1; ammoType = Bullets; activateTime = 0.032;" +
              " fireTime = 0.064; spinUpTime = 0.064; spinDownTime = 0.064; };" +
              "ItemData Spin { className = \"Weapon\"; imageType = SpinImage; };");
        w.Run("$log = \"\";" +
              "function ItemImage::onFire(%p, %s) { $log = $log @ \"F\"; }" +
              "function ItemImage::onActivate(%p, %s) { $log = $log @ \"A\"; }" +
              "function ItemImage::onUpdateFire(%p, %s) { $log = $log @ \"u\"; }" +
              "function ItemImage::onDeactivate(%p, %s) { $log = $log @ \"D\"; }" +
              "function Player::onNoAmmo(%p, %s, %i) { $log = $log @ \"N\" @ %i; }");
        Player p = f.Spawn("16 16 60");
        p.RechargeRate = 0;
        string id = Itoa(p.Id());
        string State(int slot) => w.Run("echo(Player::getItemState(" + id + ", " + Itoa(slot) + "));");
        void Ticks(int n)
        {
            for (int i = 0; i < n; ++i)
                p.UpdateMove(new Move());
        }
        w.Run("Player::mountItem(" + id + ", Rifle, 0);");
        Assert.True(State(0) == "Activate" && Math.Abs(p.Images[0].Timer - 0.1f) < 1e-5f);
        Ticks(3);
        Assert.True(State(0) == "Activate" && p.Images[0].Ammo == false);
        Ticks(1);
        Assert.True(State(0) == "NoAmmo" && w.Run("echo($log);") == "NRifle");
        w.Run("Player::setItemCount(" + id + ", Bullets, 5);");
        Ticks(1);
        Assert.True(State(0) == "Reload" && p.Images[0].Ammo);
        Ticks(1);
        Assert.True(State(0) == "Reload");
        Ticks(1);
        Assert.True(State(0) == "Ready");
        w.Run("$log = \"\"; Player::trigger(" + id + ", 0, true);");
        Assert.True(State(0) == "Fire" && p.Images[0].FireCount == 1 && w.Run("echo($log);") == "F");
        Assert.True(p.ItemCountOf(2) == 5); // no consumption without a projectile type
        Ticks(1);
        Assert.True(State(0) == "Fire");
        Ticks(1);
        Assert.True(State(0) == "Reload");
        w.Run("Player::trigger(" + id + ", 0, false);");
        Ticks(4);
        Assert.True(State(0) == "Ready");
        w.Run("Player::trigger(" + id + ", 0, true);");
        w.Run("Player::mountItem(" + id + ", Spin, 0);");
        Assert.True(State(0) == "Fire" && p.Images[0].NextItem != -2);
        Ticks(3);
        Assert.True(State(0) == "Activate" && p.Images[0].Item != -1); // swapped when Fire was left
        c.Check(w, "echo(Player::unmountItem(" + id + ", 0), Player::mountItem(" + id + ", Beam, 1));", "TrueTrue");
        Assert.True(State(1) == "Activate");
        Ticks(1);
        Assert.True(State(1) == "Ready" && p.Images[1].Ammo); // energy 60 > minEnergy 5
        w.Run("$log = \"\"; Player::trigger(" + id + ", 1, true);");
        Assert.True(State(1) == "Fire" && w.Run("echo($log);") == "A");
        float e0 = p.Energy;
        Ticks(1);
        Assert.True(w.Run("echo($log);") == "Au");
        Assert.True(Math.Abs((e0 - p.Energy) - 0.32f) < 1e-4f); // 10 * 0.032
        w.Run("Player::trigger(" + id + ", 1, false);");
        Ticks(2);
        Assert.True(State(1) == "Reload" || State(1) == "Ready");
        Assert.True(w.Run("echo($log);").Contains('D'));
        w.Run("Player::unmountItem(" + id + ", 1);");
        Assert.True(State(1) == "Reset" && p.Images[1].Image == -1);
        Assert.True(c.Failures == 0);
    }

    // ---- radius damage (projectile-sim 3.2, table V8) ----

    private static void TestRadiusDamage()
    {
        var f = new Fixture("player_radius");
        Fixtures.World w = f.W;
        var c = new Cases();
        w.Run("StaticShapeData Pan { shapeFile = \"pan\"; };" +
              "function Pan::onDamage(%this, %type, %value, %pos, %vec, %mom, %src)" +
              " { $d = %type @ \"|\" @ %value @ \"|\" @ getWord(%vec, 0) @ \"|\" @ getWord(%mom, 0) @ \"|\" @ %src; }" +
              "instant StaticShape \"pp\" { dataBlock = \"Pan\"; position = \"100 100 100\"; };");
        (int d, string want)[] rows =
        {
            (1, "4|0.449998|-1|-134.999|2049"), (3, "4|0.449998|-3|-404.999|2049"),
            (4, "4|0.420287|-2.50903|-316.355|2049"), (7, "4|0.270287|-1.52364|-123.547|2049"),
            (10, "4|0.120287|-1.31678|-47.5181|2049"), (11, "4|0.0702879|-1.27992|-26.989|2049"),
        };
        foreach (var r in rows)
        {
            w.Run("$d = \"\"; GameBase::applyRadiusDamage(4, \"" + Itoa(100 + r.d) + " 100 100\", 10, 0.5, 150, 2049);");
            string got = w.Run("echo($d);"), want = r.want;
            static List<double> Fields(string t) =>
                t.Split('|').Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0).ToList();
            List<double> a = Fields(got), b = Fields(want);
            bool ok = a.Count == b.Count;
            for (int i = 0; ok && i < a.Count; ++i)
                ok = Math.Abs(a[i] - b[i]) <= 3e-5 * Math.Max(1.0, Math.Abs(b[i]));
            if (!ok)
            {
                Console.Error.WriteLine($"radius D={r.d}: got {got} want {r.want}");
                ++c.Failures;
            }
        }
        c.Check(w, "$d = \"\"; echo(GameBase::applyRadiusDamage(4, \"130 100 100\", 10, 0.5, 150, 2049), $d);", "true");
        c.Check(w, "echo(GameBase::applyRadiusDamage(4));",
                "wrong argcount to GameBase::applyRadiusDamage(type,pos,radius,damageValue,force,srcObjectId)\nfalse");
        c.Check(w, "$d = \"\"; echo(GameBase::applyDamage(pp, 3, 0.25, \"1 2 3\", \"4 5 6\", \"7 8 9\", 5), $d);",
                "True3|0.25|4|7|5");
        w.Run("function Pan::onDamage(%this, %type, %value, %pos, %vec, %mom, %src)" +
              " { $d = %type @ \"|\" @ %value @ \"|\" @ %pos @ \"|\" @ %vec @ \"|\" @ %mom @ \"|\" @ %src; }");
        c.Check(w, "GameBase::applyDamage(pp, 3, 0.45, \"0.45 2 3\", \"4 5 6\", \"7 8 -0.3\", 5); echo($d);",
                "3|0.449997|0.449999 2 3|4 5 6|7 8 -0.299999|5");
        c.Check(w, "GameBase::applyDamage(pp, 3, -0.45, \"1 2 3\", \"4 5 6\", \"7 8 9\", 5); echo($d);",
                "3|-0.449997|1 2 3|4 5 6|7 8 9|5");
        c.Check(w, "GameBase::applyDamage(pp, 3, 2, \"1 2 3\", \"4 5 6\", \"7 8 9\", 5); echo($d);", "3|2|1 2 3|4 5 6|7 8 9|5");
        c.Check(w, "GameBase::applyDamage(pp, 3, 0.00001, \"1 2 3\", \"4 5 6\", \"7 8 9\", 5); echo($d);",
                "3|9.99997e-06|1 2 3|4 5 6|7 8 9|5");
        c.Check(w, "GameBase::applyDamage(pp, 3, 1234567, \"1 2 3\", \"4 5 6\", \"7 8 9\", 5); echo($d);",
                "3|1.23456e+06|1 2 3|4 5 6|7 8 9|5");
        c.Check(w, "GameBase::applyDamage(pp, 3, -3, \"1 2 3\", \"4 5 6\", \"7 8 9\", 5); echo($d);", "3|-3|1 2 3|4 5 6|7 8 9|5");
        Assert.True(c.Failures == 0);
    }

    // castWorld (collision.md 9, interiors.md 6.3 step 4): the terrain hit behind a Player shortens the segment, and the
    // Player's line test computes its hit point from the shortened segment.
    private static void TestCastWorldShortening()
    {
        var f = new Fixture("cast_shorten");
        Fixtures.World w = f.W;
        Player p = f.Spawn("50 50 50", "0 0 0.3");
        var T = new TMat3();
        T.Flags = p.Transform.Flags;
        T.M = p.Transform.M;
        T.P = p.Transform.P;
        Shape.TmatInverse(T, out TMat3 inv);
        float[] mn = { p.BoxMin[0], p.BoxMin[1], p.BoxMin[2] }, mx = { p.BoxMax[0], p.BoxMax[1], p.BoxHeight };
        void LineHit(float[] S, ReadOnlySpan<float> E, float[] output)
        {
            float[] ls = new float[3], le = new float[3];
            X87.MulPoint(S, inv.M, inv.P, ls);
            X87.MulPoint(E, inv.M, inv.P, le);
            float t = 0;
            bool found = false;
            for (int a = 0; a < 3 && !found; ++a)
            {
                int b = (a + 1) % 3, c = (a + 2) % 3;
                float num, den;
                if (ls[a] < mn[a])
                {
                    if (le[a] < mn[a])
                        continue;
                    num = X87.F32(Sub(mn[a], ls[a]));
                    den = X87.F32(Sub(le[a], ls[a]));
                }
                else if (ls[a] > mx[a])
                {
                    if (le[a] > mx[a])
                        continue;
                    num = X87.F32(Sub(ls[a], mx[a]));
                    den = X87.F32(Sub(ls[a], le[a]));
                }
                else
                {
                    continue;
                }
                if (Mul(Sub(le[b], ls[b]), num) < Mul(Sub(mx[b], ls[b]), den) && Mul(Sub(mn[b], ls[b]), den) < Mul(Sub(le[b], ls[b]), num) &&
                    Mul(Sub(le[c], ls[c]), num) < Mul(Sub(mx[c], ls[c]), den) && Mul(Sub(mn[c], ls[c]), den) < Mul(Sub(le[c], ls[c]), num))
                {
                    t = X87.F32(Div(num, den));
                    found = true;
                }
            }
            Assert.True(found);
            for (int i = 0; i < 3; ++i)
                output[i] = X87.F32(Add(Mul(Sub(E[i], S[i]), t), S[i]));
        }
        static bool Same(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            for (int i = 0; i < 3; ++i)
                if (Bits(a[i]) != Bits(b[i]))
                    return false;
            return true;
        }
        int differing = 0;
        for (int k = 0; k < 12; ++k)
        {
            float[] S = { AddF(30.0f, MulF(0.05f, k)), AddF(35.0f, MulF(0.01f, k)), AddF(52.4f, MulF(0.013f, k)) }, E = { 70.0f, 65.0f, 48.4f };
            var terrain = new RayHit();
            var hit = new RayHit();
            Assert.True(Projectile.CastWorld(w.Game, S, E, 0xffffffffu, p, ref terrain) && terrain.Terrain);
            Assert.True(Projectile.CastWorld(w.Game, S, E, 0xffffffffu, null, ref hit) && hit.Object == p);
            float[] shortened = new float[3], full = new float[3];
            LineHit(S, terrain.Point, shortened);
            LineHit(S, E, full);
            Assert.True(Same(hit.Point, shortened));
            if (!Same(shortened, full))
                ++differing;
        }
        Assert.True(differing > 0); // the shortening changes the f32 hit point of some of these rays
        for (int k = 0; k < 400; ++k)
        {
            float[] S = { AddF(30.0f, MulF(0.0123f, k)), AddF(35.0f, MulF(0.0071f, k)), AddF(51.6f, MulF(0.0013f, k)) }, E = { 70.0f, 65.0f, 51.2f };
            var hit = new RayHit();
            Assert.True(Projectile.CastWorld(w.Game, S, E, 0xffffffffu, null, ref hit) && hit.Object == p);
            float[] full = new float[3];
            LineHit(S, E, full);
            Assert.True(Same(hit.Point, full));
        }
    }

    // Items and projectiles take the terrain container's gravity and drag on entry (FC2, 0x005195b4), as the Player does.
    private static void TestEntryForces()
    {
        var f = new Fixture("player_entry_forces");
        Fixtures.World w = f.W;
        w.Run("ItemData Brick { shapeFile = \"gen\"; mass = 1; drag = 1; friction = 1; elasticity = 0.2; };" +
              "GrenadeData Gr { maxLevelFlightDist = 150; elasticity = 0.45; liveTime = 1.0; totalTime = 30; };" +
              "instant Item \"Inside\" { dataBlock = \"Brick\"; position = \"16 16 200\"; };");
        GameBase? Item(string name) => w.Object(name) as GameBase;
        void Tick(int n)
        {
            for (int i = 0; i < n; ++i)
            {
                uint t = w.Sim.TickMs();
                while (w.Sim.TickMs() == t)
                    w.Sim.AdvanceRealTime(1);
            }
        }
        GameBase? a = Item("Inside");
        Assert.True(a != null);
        a!.ItemPhysics(0.032f);
        Assert.True(a.Velocity[2] == -0.64f && a.ItemGravity == 20.0f);
        string shell = w.Run("echo(Projectile::spawnProjectile(Gr, \"1 0 0 0 0 -1 0 1 0 16 16 200\", -1, \"0 0 0\"));");
        var g = (GameBase)w.Object(shell)!;
        Tick(2);
        float g0 = g.Velocity[2];
        w.Run("setTerrainContainer(\"MissionGroup\\\\Terrain\", \"0 0 -5\", 0.5, 10000);");
        a.Velocity[2] = 0;
        a.ItemPhysics(0.032f);
        Assert.True(a.Velocity[2] == -0.64f && a.ItemGravity == 20.0f);
        Tick(1);
        Assert.True(Math.Abs(g.Velocity[2] - (g0 - 20.0f / 30.0f)) < 1e-4f);
        w.Run("instant Item \"Later\" { dataBlock = \"Brick\"; position = \"20 16 200\"; };");
        GameBase b = Item("Later")!;
        b.ItemPhysics(0.032f);
        float c = Scaled(Scaled(1.0f, 0.5f), 0.032f);
        float v1 = X87.F32(Sub(-0.16f, Mul(-0.16f, c)));
        Assert.True(Bits(b.Velocity[2]) == Bits(v1) && b.ItemGravity == 5.0f);
        string shell2 = w.Run("echo(Projectile::spawnProjectile(Gr, \"1 0 0 0 0 -1 0 1 0 20 16 200\", -1, \"0 0 0\"));");
        var h = (GameBase)w.Object(shell2)!;
        float h0 = h.Velocity[2];
        Tick(2); // one step: gravity, then the drag of a Grenade (movement flag 0x10)
        {
            float dt = 0.0333333351f;
            float v = X87.F32(Add(Mul(-5.0f, dt), h0));
            float k = Scaled(Scaled(1.0f, 0.5f), dt);
            Assert.True(Bits(h.Velocity[2]) == Bits(X87.F32(Sub(v, Mul(v, k)))));
            Assert.True(h.Velocity[2] > h0);
        }
        w.Run("Item::hide(Inside, true); Item::hide(Inside, false);");
        a.Velocity[2] = 0;
        a.ItemPhysics(0.032f);
        Assert.True(Bits(a.Velocity[2]) == Bits(v1) && a.ItemGravity == 5.0f);
    }

    // The armor and image shapes of the muzzle tests: eye at (0, 0.25, 2), hand at (0.5, 0, 1.25) keyed by `looks`.
    private static (DtsSpec body, DtsSpec gun) MuzzleShapes()
    {
        var body = new DtsSpec();
        body.Names = new List<string> { "bounds", "root", "dummy eye10", "dummy hand10", "looks" };
        body.Nodes = new() { new(0, -1, 0, 0, 0), new(1, 0, 0, 0, 0), new(2, 1, 0, 0, 1), new(3, 1, 1, 0, 2) };
        body.Sequences = new() { new(4, 0, 0.333333f, 2) };
        body.Subs = new() { new(0, 2, 0) };
        body.Keys = new() { new(0.0f, 2, 0x8000), new(1.0f, 3, 0x8000) };
        body.Transforms = new()
        {
            X(kQ0, new float[] { 0, 0, 0 }), X(kQ0, new float[] { 0, 0.25f, 2.0f }), X(kQ0, new float[] { 0.5f, 0, 1.25f }),
            X(kQ0, new float[] { 0.5f, 0, 1.75f }),
        };
        body.Details = new() { new(1, 10.0f) };
        var gun = new DtsSpec();
        gun.Names = new List<string> { "bounds", "always", "dummyalways muzzle", "root" };
        gun.Nodes = new() { new(0, -1, 0, 0, 0), new(1, 0, 0, 0, 0), new(2, 1, 0, 0, 1), new(3, 0, 0, 0, 0) };
        gun.Transforms = new() { X(kQ0, new float[] { 0, 0, 0 }), X(kQ0, new float[] { 0, 0.75f, 0.0625f }) };
        gun.Details = new() { new(3, 10.0f) };
        gun.AlwaysNode = 1;
        return (body, gun);
    }

    // ---- muzzle, mount and eye transforms (projectile-sim 1.5) ----

    private static void TestMuzzleTransforms()
    {
        var f = new Fixture("player_muzzle");
        Fixtures.World w = f.W;
        (DtsSpec body, DtsSpec gun) = MuzzleShapes();
        PutFile(Path.Combine(f.Root, "base/body.dts"), DtsModelFile(body));
        PutFile(Path.Combine(f.Root, "base/gunm.dts"), DtsModelFile(gun));
        w.Res.SetSearchPath("base"); // rescan: the files were added after the world was built
        w.Run("PlayerData marmor { shapeFile = \"body\"; mass = 9; maxEnergy = 60; maxDamage = 0.5; boxWidth = 0.5;" +
              " boxDepth = 0.5; boxNormalHeight = 2.25; boxCrouchHeight = 1.75; };" +
              "ItemImageData AimImage { shapeFile = \"gunm\"; mountPoint = 0; mountOffset = \"0.25 0.5 0.125\";" +
              " weaponType = 0; };" +
              "ItemData AimGun { className = \"Weapon\"; imageType = AimImage; };" +
              "ItemImageData BackImage { shapeFile = \"gunm\"; mountPoint = 1; weaponType = 0; };" +
              "ItemData BackGun { className = \"Weapon\"; imageType = BackImage; };");
        string id = w.Run("echo(spawnPlayer(marmor, \"8 16 200\", \"0 0 0\"));");
        Player? p = Player.From(w.Object(id));
        Assert.True(p != null);
        void Tick(int n)
        {
            for (int i = 0; i < n; ++i)
            {
                uint t = w.Sim.TickMs();
                while (w.Sim.TickMs() == t)
                    w.Sim.AdvanceRealTime(1);
            }
        }
        Tick(3);
        void Offset(float[] output)
        {
            string s = w.Run("echo(GameBase::getMuzzleTransform(" + id + "), \" \", GameBase::getPosition(" + id + "));");
            float[] v = new float[15];
            int n = CScan.Floats(s, v);
            Assert.True(n == 15);
            for (int i = 0; i < 9; ++i)
                output[i] = v[i];
            for (int i = 0; i < 3; ++i)
                output[9 + i] = SubF(v[9 + i], v[12 + i]);
        }
        static bool Near3(ReadOnlySpan<float> v, float x, float y, float z) =>
            Math.Abs(v[0] - x) < 2e-3f && Math.Abs(v[1] - y) < 2e-3f && Math.Abs(v[2] - z) < 2e-3f; // %g of the position
        float[] m = new float[12];
        Offset(m);
        Assert.True(m[0] == 1 && m[4] == 1 && m[8] == 1 && Near3(m.AsSpan(9), 0, 0.25f, 2.0f));
        w.Run("Player::mountItem(" + id + ", AimGun, 0);");
        Tick(2);
        Offset(m);
        Assert.True(!p!.Pose.FirstPerson && Near3(m.AsSpan(9), 0.75f, 1.25f, 1.4375f));
        Assert.True(p.ImageMountTransform(0, out TMat3 mount) && Near3(mount.P, 0.75f, 0.5f, 1.375f));
        p.Pitch = 0.5f;
        p.UpdateAnimation(0.032f);
        float look = 0.5f - 0.5f * 0.5f * 0.651088f;
        Assert.True(Math.Abs(p.Pose.LookPosition - look) < 1e-5f);
        Assert.True(p.ImageMountTransform(0, out mount) && mount.M[4] == 1 && Near3(mount.P, 0.75f, 0.5f, 1.375f));
        p.AiControlled = true;
        Assert.True(p.ImageMountTransform(0, out mount));
        float c = MathF.Cos(0.5f), s = MathF.Sin(0.5f);
        Assert.True(Near3(mount.P, 0.75f, 0.5f * c - 0.125f * s, 1.25f + 0.5f * s + 0.125f * c));
        Assert.True(Math.Abs(mount.M[4] - c) < 1e-6f && Math.Abs(mount.M[5] - s) < 1e-6f && Math.Abs(mount.M[7] + s) < 1e-6f);
        p.AiControlled = false;
        Transform eye = p.EyeTransform();
        Assert.True(Math.Abs(eye.M[4] - c) < 1e-6f && Math.Abs(eye.P[1] - (p.Transform.P[1] + 0.25f)) < 1e-4f);
        p.Pitch = 0;
        p.UpdateAnimation(0.032f);
        w.Run("Player::unmountItem(" + id + ", 0); Player::mountItem(" + id + ", BackGun, 0);");
        Assert.True(!p.ImageMountTransform(0, out mount) && mount.P[0] == 0 && mount.M[0] == 1);
        Assert.True(p.MuzzleObjectTransform(0, out TMat3 mz) && Near3(mz.P, 0, 0.75f, 0.0625f));
        Assert.True(p.Pullback == 0.0f);
        Assert.True(!p.RaySkip);
        p.Kill();
        Assert.True(p.RaySkip);
    }

    // ---- fire edge cases (ROADMAP G21, docs/projectile-sim.md 12.2) ----

    // Projectile_applyDeflection (0x00459fa4) against the reference: the direction of the first 120 Bullets of a freshly
    // started server (tests/deflection_vectors.inc).
    private static void TestDeflectionVectors()
    {
        uint[][] kRef = TestPaths.IncTokens("tests/deflection_vectors.inc", null).Select(r => r.Select(TestPaths.IncU32).ToArray()).ToArray();
        var f = new Fixture("player_deflect");
        Fixtures.World w = f.W;
        ProjectileGhost.Register();
        w.Run("BulletData DBl { aimDeflection = 0.25; muzzleVelocity = 425.0; totalTime = 1.5; inheritedVelocityScale = 1.0; };");
        Projectile.ResetProjectileRandoms();
        string[] kAxes = { "0 1 0", "0.6 0.64 0.48", "-0.48 0.6 0.64", "0.64 -0.48 0.6" };
        var gm = new GhostManager(2049);
        var gctx = new GhostContext(gm, 2049, true);
        int n = kRef.Length;
        Assert.True(n == 120);
        for (int i = 0; i < n; ++i)
        {
            string id = w.Run("echo(Projectile::spawnProjectile(DBl, \"1 0 0 " + kAxes[i % 4] + " 0 0 1 16 16 93\", -1, \"0 0 0\"));");
            SimObject? o = w.Object(id);
            Assert.True(o != null);
            GhostClass? cls = GhostManager.GhostClassOf(o!);
            Assert.True(cls != null && cls.Tag == 0x258);
            byte[] buf = new byte[64];
            var g = new BitStream(buf, buf.Length);
            cls!.Pack!(o!, gctx, g, 0xffffffffu);
            ClientGhost cb = ClientRoundTrip.Unpack(0x258, buf, g.BitPosition(), true, ClientRoundTrip.WorldFor(0x258, o));
            Assert.True(ClientRoundTrip.VecIs(cb.State["start"], 16, 16, 93) && (long)cb.State["speed16"]! == 6798 && (long)cb.State["elapsedMs"]! == 0);
            var r = new BitStream(buf, buf.Length);
            Assert.True(r.ReadFlag() && r.ReadInt(4) == 15 && !r.ReadFlag() && !r.ReadFlag()); // GameBase part
            Assert.True(r.ReadFlag() && r.ReadFlag());                                         // initial, twice
            Assert.True(r.ReadInt(2) == 0 && !r.ReadFlag());                                   // block 0 of 1, no shooter ghost
            float s0 = r.ReadF32(), s1 = r.ReadF32(), s2 = r.ReadF32();
            Assert.True(s0 == 16 && s1 == 16 && s2 == 93);
            Assert.True(r.ReadInt(15) == 0); // no flight time yet
            uint x = r.ReadInt(20), y = r.ReadInt(20), neg = r.ReadFlag() ? 1u : 0u;
            if (x != kRef[i][0] || y != kRef[i][1] || neg != kRef[i][2])
            {
                Console.WriteLine($"deflection shot {i + 1}: {x} {y} {neg}, reference {kRef[i][0]} {kRef[i][1]} {kRef[i][2]}");
                Assert.True(false);
            }
            Assert.True(r.ReadInt(14) == 6798); // ftol(425 x 16), the reference speed field
            w.Run("deleteObject(" + id + ");");
        }
    }

    // LaserProjectile_packInitial (0x00463c54) and the ghost-always rule of NetObject_onAdd (0x0050a420).
    private static void TestLaserGhost()
    {
        var f = new Fixture("player_laserghost");
        Fixtures.World w = f.W;
        PlayerGhost.Register();
        ProjectileGhost.Register();
        w.Run("LaserData SL { damageConversion = 0.007; baseDamageType = 1; beamTime = 0.5; detachFromShooter = false; };");
        Player p = f.Spawn("16 16 60");
        string pid = Itoa(p.Id());
        var gm = new GhostManager(2049);
        var gctx = new GhostContext(gm, 2049, true);
        gm.MarkInScope(p, true);
        gm.StartGhosting();
        byte[] pkt = new byte[512];
        var output = new BitStream(pkt, pkt.Length);
        Assert.True(gm.WritePacket(output, 3000, out uint token));
        gm.Notify(token, true);
        int shooterGhost = gm.GhostIndex(p);
        Assert.True(shooterGhost != -1);
        int seen = 0;
        SimObject? last = null;
        w.Sim.Server()!.OnRegister = o =>
        {
            ++seen;
            last = o;
        };
        string id = w.Run("echo(Projectile::spawnProjectile(SL, \"1 0 0 0 1 0 0 0 1 16 16 80\", " + pid + ", \"0 0 0\"));");
        SimObject? obj = w.Object(id);
        Assert.True(obj != null && seen == 1 && last == obj);
        Assert.Contains("-1", w.Run("echo(Projectile::spawnProjectile(Nope, \"1 0 0 0 1 0 0 0 1 16 16 80\", -1, \"0 0 0\"));"),
                        StringComparison.Ordinal);
        Assert.True(seen == 1);
        w.Sim.Server()!.OnRegister = null;
        Assert.True((GhostScope.NetFlags(obj!) & 0x140u) == 0x140u);
        GhostClass? cls = GhostManager.GhostClassOf(obj!);
        Assert.True(cls != null && cls.Tag == 0x25b);
        uint Pack(GhostContext ctx, byte[] buf)
        {
            Array.Clear(buf);
            var g = new BitStream(buf, buf.Length);
            cls!.Pack!(obj!, ctx, g, 0xffffffffu);
            return g.BitPosition();
        }
        byte[] buf = new byte[64];
        Assert.True(Pack(gctx, buf) == 124);
        ClientGhost cl = ClientRoundTrip.Unpack(0x25b, buf, 124, true, ClientRoundTrip.WorldFor(0x25b, obj)); // client-net.md 3.6
        Assert.True((long)cl.State["shooterGhost"]! == shooterGhost && (long)cl.State["hit"]! == 0 && !cl.State.ContainsKey("start"));
        Assert.True(ClientRoundTrip.VecIs(cl.State["end"], 16, 3016, 80) && (long)cl.State["energy5"]! == 0);
        var r = new BitStream(buf, buf.Length);
        Assert.True(r.ReadFlag() && r.ReadInt(4) == 15 && !r.ReadFlag() && !r.ReadFlag());
        Assert.True(r.ReadFlag() && r.ReadFlag() && r.ReadInt(2) == 0);
        Assert.True(r.ReadFlag() && (int)r.ReadInt(10) == shooterGhost);
        Assert.True(!r.ReadFlag()); // no hit: nothing along the beam
        float e0 = r.ReadF32(), e1 = r.ReadF32(), e2 = r.ReadF32();
        Assert.True(e0 == 16 && e1 == 3016 && e2 == 80); // the terrain's visible distance, 3000 m
        Assert.True(r.ReadInt(5) == 0);                  // a console spawn has no energy
        var other = new GhostManager(2050);
        var octx = new GhostContext(other, 2050, true);
        Assert.True(Pack(octx, buf) == 124 - 10 + 96);
        ClientGhost cl2 = ClientRoundTrip.Unpack(0x25b, buf, 124 - 10 + 96, true, ClientRoundTrip.WorldFor(0x25b, obj));
        Assert.True(!cl2.State.ContainsKey("shooterGhost") && ClientRoundTrip.VecIs(cl2.State["start"], 16, 16, 80)); // no shooter ghost: start follows
        var r2 = new BitStream(buf, buf.Length);
        r2.ReadInt(7);
        Assert.True(r2.ReadFlag() && r2.ReadFlag() && r2.ReadInt(2) == 0 && !r2.ReadFlag());
        float st0 = r2.ReadF32(), st1 = r2.ReadF32(), st2 = r2.ReadF32();
        Assert.True(st0 == 16 && st1 == 16 && st2 == 80 && !r2.ReadFlag());
        r2.ReadF32();
        e1 = r2.ReadF32();
        r2.ReadF32();
        Assert.True(e1 == 3016 && r2.ReadInt(5) == 0);
    }

    // Aimed muzzle (0x0044c148), the dead rule of Player_fireImage and of the mount (0x00419ba0), and the weapon pull-back
    // (0x004198d4) on the synthetic armor of testMuzzleTransforms.
    private static void TestFireEdgeTransforms()
    {
        var f = new Fixture("player_fireedge");
        Fixtures.World w = f.W;
        (DtsSpec body, DtsSpec gun) = MuzzleShapes();
        PutFile(Path.Combine(f.Root, "base/body.dts"), DtsModelFile(body));
        PutFile(Path.Combine(f.Root, "base/gunm.dts"), DtsModelFile(gun));
        w.Res.SetSearchPath("base");
        w.Run("PlayerData marmor { shapeFile = \"body\"; mass = 9; maxEnergy = 60; maxDamage = 0.5; boxWidth = 0.5;" +
              " boxDepth = 0.5; boxNormalHeight = 2.25; boxCrouchHeight = 1.75; };" +
              "RocketData Rk { muzzleVelocity = 65; terminalVelocity = 65; totalTime = 2.0; liveTime = 1.5; };" +
              "ItemImageData AimImage { shapeFile = \"gunm\"; mountPoint = 0; mountOffset = \"0.25 0.5 0.125\";" +
              " weaponType = 0; projectileType = Rk; accuFire = true; activateTime = 0; fireTime = 0.032; reloadTime = 0.032; };" +
              "ItemData AimGun { className = \"Weapon\"; imageType = AimImage; };");
        Player Make(string pos)
        {
            Player? pl = Player.From(w.Object(w.Run("echo(spawnPlayer(marmor, \"" + pos + "\", \"0 0 0\"));")));
            Assert.True(pl != null);
            return pl!;
        }
        static void Steps(Player pl, int n)
        {
            for (int i = 0; i < n; ++i)
                pl.UpdateMove(new Move());
        }
        void Shoot(Player pl, float[] v)
        {
            string id = Itoa(pl.Id());
            string before = w.Run("echo(Group::objectCount(MissionCleanup));");
            w.Run("Player::trigger(" + id + ", 0, true);");
            Steps(pl, 1);
            w.Run("Player::trigger(" + id + ", 0, false);");
            string after = w.Run("echo(Group::objectCount(MissionCleanup));");
            Assert.True(int.Parse(after, CultureInfo.InvariantCulture) == int.Parse(before, CultureInfo.InvariantCulture) + 1);
            SimObject? o = w.Object(w.Run("echo(Group::getObject(MissionCleanup, " + before + "));"));
            Assert.True(o is GameBase);
            var gb = (GameBase)o!;
            v[0] = gb.Velocity[0];
            v[1] = gb.Velocity[1];
            v[2] = gb.Velocity[2];
            w.Run("deleteObject(" + Itoa(o!.Id()) + ");");
            Steps(pl, 4); // Fire, Reload, Ready
        }
        static bool Near3(ReadOnlySpan<float> v, float x, float y, float z, float tol) =>
            Math.Abs(v[0] - x) < tol && Math.Abs(v[1] - y) < tol && Math.Abs(v[2] - z) < tol;
        Player p = Make("8 16 50");
        string pidS = Itoa(p.Id());
        w.Run("Player::mountItem(" + pidS + ", AimGun, 0);");
        Steps(p, 4);
        Assert.True(Math.Abs(p.Transform.P[2] - 50.0f) < 1e-3f && p.Pullback == 0.0f);
        float[] v = new float[3];
        Shoot(p, v);
        Assert.True(Near3(v, 0, 65, 0, 1e-4f));
        Player far = Make("8 26 50");
        Steps(far, 4);
        Shoot(p, v);
        float len = MathF.Sqrt(0.75f * 0.75f + 8.25f * 8.25f + 0.5625f * 0.5625f);
        Assert.True(Near3(v, -0.75f / len * 65, 8.25f / len * 65, 0.5625f / len * 65, 2e-3f));
        w.Run("GameBase::setPosition(" + Itoa(far.Id()) + ", \"8 18.4 50\");");
        Steps(far, 2);
        Shoot(p, v);
        Assert.True(Near3(v, 0, 65, 0, 1e-4f));
        w.Run("GameBase::setPosition(" + Itoa(far.Id()) + ", \"8 26 50\");");
        Steps(far, 2);
        Player wall = Make("8.75 17.2 50");
        Steps(wall, 2);
        Steps(p, 1);
        Assert.True(Math.Abs(p.Pullback + 0.75f) < 1e-4f);
        Assert.True(p.ImageMountTransform(0, out TMat3 mount) && Near3(mount.P, 0.75f, -0.25f, 1.375f, 1e-4f));
        w.Run("deleteObject(" + Itoa(wall.Id()) + ");");
        Steps(p, 1);
        Assert.True(p.Pullback == 0.0f);
        p.Kill();
        Shoot(p, v);
        Assert.True(Near3(v, 0, 65, 0, 1e-4f));
        Player q = Make("40 16 50");
        w.Run("Player::mountItem(" + Itoa(q.Id()) + ", AimGun, 0);");
        Steps(q, 2);
        q.AiControlled = true;
        q.Pitch = 0.5f;
        Assert.True(q.ImageMountTransform(0, out mount) && Math.Abs(mount.M[4] - MathF.Cos(0.5f)) < 1e-6f);
        q.Kill();
        Assert.True(q.ImageMountTransform(0, out mount) && mount.M[4] == 1.0f && Near3(mount.P, 0.75f, 0.5f, 1.375f, 1e-4f));
    }

    // ---- projectiles (projectile-sim 2.1 - 2.5, 3.5, 1.3) ----

    private static void TestProjectiles()
    {
        var f = new Fixture("player_projectiles");
        Fixtures.World w = f.W;
        var c = new Cases();
        w.Run("RocketData Rk { muzzleVelocity = 65; terminalVelocity = 65; totalTime = 2.0; liveTime = 1.5; };" +
              "GrenadeData Gr { maxLevelFlightDist = 150; elasticity = 0.45; liveTime = 1.0; totalTime = 30; };" +
              "BulletData Bl { muzzleVelocity = 200; totalTime = 2.0; liveTime = 1.125; damageValue = 0.125;" +
              " damageType = 8; };");
        void Tick(int n)
        {
            for (int i = 0; i < n; ++i)
            {
                uint t = w.Sim.TickMs();
                while (w.Sim.TickMs() == t)
                    w.Sim.AdvanceRealTime(1);
            }
        }
        string Pos(string id) => w.Run("echo(GameBase::getPosition(" + id + "));");
        c.Check(w, "echo(Projectile::spawnProjectile(Rk));", "-1");
        c.Check(w, "echo(Projectile::spawnProjectile(Nope, \"1 0 0 0 1 0 0 0 1 0 0 0\", -1, \"0 0 0\"));",
                "Projectile data block \"Nope\" is undefined\n-1");
        string id = w.Run("echo(Projectile::spawnProjectile(Rk, \"1 0 0 0 1 0 0 0 1 16 16 80\", -1, \"0 0 0\"));");
        Assert.True(id != "-1");
        Assert.True(Pos(id) == "16 16 80");
        Tick(1);
        Assert.True(Pos(id) == "16 16 80");
        Tick(1);
        Assert.True(Pos(id) == "16 18.1667 80" || Pos(id) == "16 18.1666 80");
        Tick(31); // tick 33
        string before = Pos(id);
        Tick(1); // tick 34 is skipped
        Assert.True(Pos(id) == before);
        Tick(1);
        Assert.True(Pos(id) != before);
        var gb = w.Object(id) as GameBase;
        Assert.True(gb != null && gb.Velocity[0] == 0 && gb.Velocity[1] == 65.0f && gb.Velocity[2] == 0);
        Tick(43); // tick 78
        Assert.True(w.Object(id) != null);
        Tick(1); // tick 79: expired in tick 63 (2016 ms > 2000 ms), deleted 16 ticks later
        Assert.True(w.Object(id) == null);
        id = w.Run("echo(Projectile::spawnProjectile(Gr, \"1 0 0 0 0 -1 0 1 0 16 16 200\", -1, \"0 0 0\"));");
        gb = (GameBase)w.Object(id)!;
        Assert.True(Math.Abs(gb.Velocity[2] + 54.7722f) < 1e-3f);
        Tick(2);
        Assert.True(Math.Abs(gb.Velocity[2] + 55.4389f) < 1e-3f);
        Tick(1);
        Assert.True(Math.Abs(gb.Velocity[2] + 56.1055f) < 1e-3f);
        id = w.Run("echo(Projectile::spawnProjectile(Bl, \"1 0 0 0 1 0 0 0 1 16 16 120\", -1, \"0 0 0\"));");
        gb = (GameBase)w.Object(id)!;
        Assert.True(Math.Abs(gb.Velocity[0] + 0.000190735f) < 1e-8f && Math.Abs(gb.Velocity[1] - 199.999f) < 1e-3f);
        Assert.True(Math.Abs(gb.Velocity[2] - 0.0690531f) < 1e-6f);
        Tick(1);
        Assert.True(Math.Abs(gb.Transform.P[1] - (16.0f + 6.4f)) < 1e-3f);
        Tick(1);
        Assert.True(Math.Abs(gb.Transform.P[1] - (16.0f + 12.8f)) < 1e-3f);
        Assert.True(c.Failures == 0);
    }

    // ---- SeekingMissile, LaserProjectile, TargetLaser, Lightning, RepairEffect (projectile-sim 2.6-2.10) ----

    private static void TestBeamAndSeekerProjectiles()
    {
        var f = new Fixture("player_beams");
        Fixtures.World w = f.W;
        float[] solar = { -1.28014f, -1.32877f, 0, 1.28014f, 1.32877f, 2.40577f };
        PutFile(Path.Combine(f.Root, "base/panel.dts"), DtsCollisionFile(solar));
        w.Run("SeekingMissileData TM { collisionRadius = 0.0; mass = 2.0; damageClass = 1; damageValue = 0.5;" +
              " damageType = 5; explosionRadius = 9.5; kickBackStrength = 175.0; muzzleVelocity = 72.0; totalTime = 10;" +
              " liveTime = 10; seekingTurningRadius = 9; nonSeekingTurningRadius = 75.0; proximityDist = 1.5;" +
              " inheritedVelocityScale = 0.5; };" +
              "LaserData SL { damageConversion = 0.007; baseDamageType = 1; beamTime = 0.5; detachFromShooter = false; };" +
              "TargetLaserData TL { damageConversion = 0.0; baseDamageType = 0; detachFromShooter = false; };" +
              "LightningData LC { damageType = 12; boltLength = 40.0; coneAngle = 35.0; damagePerSec = 0.06;" +
              " energyDrainPerSec = 60.0; segmentDivisions = 4; numSegments = 5; beamWidth = 0.125; updateTime = 120;" +
              " skipPercent = 0.5; displaceBias = 0.15; };" +
              "RepairEffectData RB { boltLength = 5.0; segmentDivisions = 4; beamWidth = 0.125; updateTime = 450;" +
              " skipPercent = 0.6; displaceBias = 0.15; };" +
              "StaticShapeData Panel { shapeFile = \"panel\"; };");
        void Tick(int n)
        {
            for (int i = 0; i < n; ++i)
            {
                uint t = w.Sim.TickMs();
                while (w.Sim.TickMs() == t)
                    w.Sim.AdvanceRealTime(1);
            }
        }
        string Pos(string id) => w.Run("echo(GameBase::getPosition(" + id + "));");
        string Vel(string id) => w.Run("echo(Item::getVelocity(" + id + "));");
        string id = w.Run("echo(Projectile::spawnProjectile(TM, \"0 -1 0 1 0 0 0 0 1 0 -100 900\", -1, \"0 0 0\"));");
        Assert.True(id != "-1" && Pos(id) == "0 -100 900" && Vel(id) == "72 0 0");
        Tick(1);
        Assert.True(Pos(id) == "2.30399 -100 900" && Vel(id) == "71.9999 0 0");
        Tick(1);
        Assert.True(Pos(id) == "4.60799 -100 900");
        Tick(37);
        Assert.True(Pos(id) == "89.8556 -100 900" && Vel(id) == "71.9996 0 0");
        Tick(40);
        Assert.True(Pos(id) == "182.014 -100 900" && Vel(id) == "71.9993 0 0");
        Tick(40);
        Assert.True(Pos(id) == "274.173 -100 900" && Vel(id) == "71.9989 0 0");
        w.Run("deleteObject(" + id + ");");

        w.Run("$PPpanel = newObject(\"pp1\", StaticShape, Panel, true); GameBase::setPosition(pp1, \"0 0 800\");" +
              "$hit = 0; function Panel::onDamage(%this, %type, %value, %pos, %vec, %mom, %src) { $hit++; }");
        id = w.Run("echo(Projectile::spawnProjectile(SL, \"1 0 0 0 1 0 0 0 1 0 -20 801.2\", -1, \"0 0 0\"));");
        Assert.True(id != "-1" && Pos(id) == "0 -20 801.199");
        Tick(15);
        Assert.True(w.Object(id) != null && Pos(id) == "0 -20 801.199");
        Tick(1);
        Assert.True(w.Object(id) == null && w.Run("echo($hit);") == "0");

        Player shooter = f.Spawn("0 -40 800");
        id = w.Run("echo(Projectile::spawnProjectile(TL, \"1 0 0 0 1 0 0 0 1 0 0 800\", " + Itoa(shooter.Id()) + ", \"0 0 0\"));");
        Assert.True(id != "-1");
        Tick(11);
        Assert.True(w.Object(id) != null && Pos(id) == "0 0 800");
        w.Run("deleteObject(" + id + ");");

        Player healer = f.Spawn("0 -3 799");
        w.Run("$log = \"\"; $k = 0;" +
              "function RepairBolt::onAcquire(%a, %p, %t) { $log = $log @ \"A\" @ $k @ \" \"; }" +
              "function RepairBolt::onRelease(%a, %p) { $log = $log @ \"R\" @ $k @ \" \"; }" +
              "function RepairBolt::checkDone(%a, %p) { $log = $log @ \"C\" @ $k @ \" \"; }");
        w.Run("RepairEffectData RepairBolt { boltLength = 5.0; segmentDivisions = 4; beamWidth = 0.125; updateTime = 450;" +
              " skipPercent = 0.6; displaceBias = 0.15; };");
        id = w.Run("echo(Projectile::spawnProjectile(RepairBolt, \"1 0 0 0 1 0 0 0 1 0 -3 800.8\", " + Itoa(healer.Id()) + ", \"0 0 0\"));");
        Assert.True(id != "-1" && Pos(id) == "0 -3 800.799");
        for (int k = 1; k <= 24; ++k)
        {
            w.Run("$k = " + Itoa(k) + ";");
            Tick(1);
        }
        string log = w.Run("echo($log);");
        Assert.True(log.StartsWith("A0 C4 ", StringComparison.Ordinal) && !log.Contains('R'));
        w.Run("deleteObject(" + id + ");");

        w.Run("deleteObject(" + Itoa(healer.Id()) + "); deleteObject(" + Itoa(shooter.Id()) + ");");
        Player victim = f.Spawn("0 0 800");
        w.Run("$log = \"\"; $n = 0;" +
              "function Lightning::damageTarget(%t, %ts, %dps, %eps, %pos, %vec, %mom, %sh) { $n++;" +
              " if($n == 1) $log = %t @ \"|\" @ %ts @ \"|\" @ %dps @ \"|\" @ %eps @ \"|\" @ %vec @ \"|\" @ %mom @ \"|\" @ %sh; }");
        id = w.Run("echo(Projectile::spawnProjectile(LC, \"1 0 0 0 1 0 0 0 1 0 -20 800.2\", -1, \"0 0 0\"));");
        Assert.True(id != "-1");
        Tick(1);
        Assert.True(w.Run("echo($n);") == "1" &&
                    w.Run("echo($log);") == Itoa(victim.Id()) + "|0.0319999|0.0599997|60|0 0.999999 0|0 0 0|0");
        Tick(29);
        Assert.True(w.Run("echo($n);") == "30");
    }

    // SeekingMissile steering rotation (projectile-sim 2.6, 0x0046680f).
    private static void TestSeekerRotation()
    {
        Numbers.EnterScriptFpuMode();
        using var chop = X87.Chop();
        float[] vn = { 1, 0, 0 }, an = { 0, 1, 0 };
        float ang = X87.F32(Mul(Mul(32, 0.001), X87.F32(Div(72.0, Mul(6.283185307179586, 75.0)))));
        float[] output = new float[3];
        Seeker.SeekerRotate(vn, an, ang, 72.0f, output);
        Assert.True(Numbers.FormatFloat(output[0]) == "71.9991" && Numbers.FormatFloat(output[1]) == "0.352023" && output[2] == 0.0f);
        float[] up = { 0, 0, -1 };
        Seeker.SeekerRotate(vn, up, ang, 72.0f, output);
        Assert.True(output[2] < 0 && output[1] == 0.0f);
    }

    // SeekingMissile aim and steering against the exact model (projectile-sim 2.6, tests/seeker_vectors.inc).
    private static void TestSeekerVectors()
    {
        Numbers.EnterScriptFpuMode();
        using var chop = X87.Chop();
        static float F(uint b) => BitConverter.UInt32BitsToSingle(b);
        static uint B(float v)
        {
            uint u = BitConverter.SingleToUInt32Bits(v);
            return u == 0x80000000u ? 0u : u; // the model has no signed zero
        }
        int[] kinds = { 0, 0, 0 };
        uint[][] rows = TestPaths.IncTokens("tests/seeker_vectors.inc", "kSeekerVectors").Select(r => r.Select(TestPaths.IncU32).ToArray()).ToArray();
        for (int ri = 0; ri < rows.Length; ++ri)
        {
            uint[] r = rows[ri];
            float[] p = { F(r[0]), F(r[1]), F(r[2]) }, v = { F(r[3]), F(r[4]), F(r[5]) };
            float[] tp = { F(r[6]), F(r[7]), F(r[8]) }, tv = { F(r[9]), F(r[10]), F(r[11]) };
            float[] d = new float[3], aim = new float[3], output = new float[3];
            Seeker.SeekerLead(p, tp, tv, 72.0f, d, aim);
            for (int i = 0; i < 3; ++i)
                Assert.True(B(aim[i]) == r[13 + i]);
            int kind = Seeker.SeekerSteer(v, aim, 32, F(r[12]), 72.0f, true, output);
            Assert.True(kind == (int)r[16]);
            for (int i = 0; i < 3; ++i)
                Assert.True(B(output[i]) == r[17 + i]);
            {
                float gap = 0;
                for (int i = 0; i < 3; ++i)
                    gap = Math.Max(gap, Math.Max(Math.Abs(aim[i] - F(r[13 + i])), Math.Abs(output[i] - F(r[17 + i]))));
                if (gap != 0 || kind != (int)r[16])
                    Console.Error.WriteLine($"seeker vector {ri} differs (kind {kind}, want {r[16]}, largest difference {gap})");
            }
            ++kinds[kind];
        }
        Assert.True(kinds[0] > 0 && kinds[1] > 50 && kinds[2] > 50);
        float[] vv = { 72, 0, 0 }, aa = { 1, 1, 0 };
        float[] o2 = new float[3];
        Assert.True(Seeker.SeekerSteer(vv, aa, 32, 1.0f, 72.0f, false, o2) == 0 && o2[0] == 72.0f && o2[1] == 0.0f);
        float[] p0 = { 1, 2, 3 }, t0 = { 4, 6, 3 };
        float[] dd = new float[3], lead = new float[3];
        Seeker.SeekerLead(p0, t0, null, 72.0f, dd, lead);
        Assert.True(dd[0] == 3.0f && dd[1] == 4.0f && dd[2] == 0.0f && dd.SequenceEqual(lead));
    }

    // A Mine entering Destroyed explodes (static-sim 5.7, 0x00458be4).
    private static void TestMineDestruction()
    {
        var f = new Fixture("player_mine");
        Fixtures.World w = f.W;
        w.Run("MineData Mn { mass = 1.0; explosionRadius = 10.0; damageValue = 0.65; damageType = 13;" +
              " kickBackStrength = 150.0; maxDamage = 0.5; triggerRadius = 0.5; };");
        f.Spawn("0 0 800");
        w.Run("$log = \"\"; function Player::onDamage(%this, %type, %value, %pos, %vec, %mom, %vp, %q, %o) {" +
              " $log = %type @ \"|\" @ %value @ \"|\" @ %mom; }");
        GameBase MineAt(string name, float y)
        {
            w.Run("$" + name + " = newObject(\"\", Mine, Mn); GameBase::setPosition($" + name + ", \"0 " +
                  y.ToString("F6", CultureInfo.InvariantCulture) + " 800\");");
            var gb = w.Object(w.Run("echo($" + name + ");")) as GameBase;
            Assert.True(gb != null && gb.ShapeClass() == GameBase.ShapeKind.Mine);
            var e = new Container.Entry(gb, ObjectType.kMineType,
                                        Float6.Of(-2.5f, SubF(y, 2.5f), 797.5f, 2.5f, AddF(y, 2.5f), 802.5f), false, null);
            w.Game.World.Add(e);
            return gb!;
        }
        MineAt("far", -40.0f);
        w.Run("GameBase::setDamageLevel($far, 0.5);");
        Assert.True(w.Run("echo($log);").Length == 0);
        MineAt("m", -4.0f);
        w.Run("GameBase::setDamageLevel($m, 0.5);");
        string log = w.Run("echo($log);");
        float[] vals = new float[4];
        bool parsed = log.StartsWith("13|", StringComparison.Ordinal);
        if (parsed)
        {
            string[] parts = log[3..].Split('|');
            parsed = parts.Length == 2 && CScan.Floats(parts[0], vals.AsSpan(0, 1)) == 1 && CScan.Floats(parts[1], vals.AsSpan(1, 3)) == 3;
        }
        Assert.True(log.StartsWith("13|", StringComparison.Ordinal) && parsed);
        float value = vals[0], mx = vals[1], my = vals[2], mz = vals[3];
        Assert.True(value > 0.3f && value < 0.65f && my > 50.0f && mz > 0.0f && Math.Abs(mx) < 1.0f);
    }

    // ---- wire: control state (player-sim 8.1) and ghost (game-stream-ghosts 4.7) ----

    private static void TestWire()
    {
        var f = new Fixture("player_wire");
        PlayerGhost.Register();
        Player p = f.Spawn("16 16 500", "0 0 0.5");
        var jet = new Move();
        jet.Jet = true;
        jet.Pitch = 0.25f;
        p.UpdateMove(jet);
        byte[] buf = new byte[128];
        var output = new BitStream(buf, buf.Length);
        var ctx = new ControlContext();
        p.WriteControlState(output, ctx);
        Assert.True(output.BitPosition() == 305); // on foot
        var inb = new BitStream(buf, buf.Length);
        float[] v = new float[3];
        Assert.True(inb.ReadInt(2) == 0);
        float yaw = inb.ReadF32();
        Assert.True(inb.ReadFlag());
        for (int i = 0; i < 3; ++i)
            v[i] = inb.ReadF32();
        Assert.True(yaw == 0.5f && v[0] == p.Transform.P[0] && v[2] == p.Transform.P[2]);
        for (int i = 0; i < 3; ++i)
            v[i] = inb.ReadF32();
        Assert.True(v[2] == p.Velocity[2]);
        float pitch = inb.ReadF32();
        float energy = inb.ReadF32();
        Assert.True(pitch == 0.25f && energy == p.Energy);
        Assert.True(!inb.ReadFlag() && inb.ReadFlag()); // contact, jetting
        Assert.True(inb.ReadInt(7) == 0 && !inb.ReadFlag() && inb.ReadInt(4) == 15);

        var gm = new GhostManager(2049);
        var gctx = new GhostContext(gm, 2049, true);
        GhostClass? cls = GhostManager.GhostClassOf(p);
        Assert.True(cls != null && cls.Tag == 0x218);
        p.RechargeRate = 8.0f;
        Array.Clear(buf);
        var g = new BitStream(buf, buf.Length);
        Assert.True(cls!.Pack!(p, gctx, g, 0xffffffffu) == 0);
        var r = new BitStream(buf, buf.Length);
        Assert.True(r.ReadFlag() && r.ReadInt(4) == 15 && !r.ReadFlag() && !r.ReadFlag()); // team -1, no clients
        Assert.True(!r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag());                      // flash, threads, fade
        Assert.True(r.ReadFlag() && r.ReadInt(2) == 0 && !r.ReadFlag());                   // datablock 0 of 1, not AI
        Assert.True(r.ReadFlag() && !r.ReadFlag());                                        // mask 0x8000: dismounted
        Assert.True(r.ReadFlag() && !r.ReadFlag() && r.ReadInt(6) == 0);                   // alive, damage 0
        Assert.True(r.ReadFlag());                                                         // state block
        r.ReadInt(5);
        for (int i = 0; i < 3; ++i)
            v[i] = r.ReadF32();
        Assert.True(v[2] == p.Transform.P[2]);
        Assert.True(r.ReadFlag()); // moving
        r.ReadInt(17);
        r.ReadInt(21);
        Assert.True(!r.ReadFlag()); // contact
        Assert.True(r.ReadInt(9) == (uint)(0.5 * 511 / 6.283185307179586));
        Assert.True(!r.ReadFlag()); // animation: never on a new ghost
        Assert.True(r.ReadFlag());
        float rate = r.ReadF32();
        Assert.True(rate == 8.0f);
        Assert.True(!r.ReadFlag() && !r.ReadFlag() && r.ReadFlag()); // pda, crouch, images (mask 0x20000)
        for (int i = 0; i < 8; ++i)
        {
            Assert.True(r.ReadFlag() && r.ReadInt(8) == 0 && !r.ReadFlag() && !r.ReadFlag() && !r.ReadFlag());
            Assert.True(r.ReadInt(3) == 0 && !r.ReadFlag());
        }
        Assert.True(r.BitPosition() == g.BitPosition());
        // Client round trip (client-net.md 3.4): the creating update, then the 0x2000 state update of a non-owner.
        ClientWorld cw = ClientRoundTrip.WorldFor(0x218, p);
        ClientGhost cg = ClientRoundTrip.Unpack(0x218, buf, g.BitPosition(), true, cw);
        Assert.True((long)cg.State["team"]! == 15 && (long)cg.State["datablock"]! == 0 && cg.State["mount"] as string == "none");
        Assert.True(ClientRoundTrip.VecIs(cg.State["pos"], p.Transform.P[0], p.Transform.P[1], p.Transform.P[2]));
        Assert.True(ClientRoundTrip.Near(ClientRoundTrip.D(cg.State["rotZ"]), 0.5, 0.0125) && ClientRoundTrip.D(cg.State["f728"]) == 8.0);
        Assert.True(((List<object?>)cg.State["images"]!).Count == 8);
        p.LastMove = jet;
        Array.Clear(buf);
        var g2 = new BitStream(buf, buf.Length);
        cls.Pack!(p, gctx, g2, 0x2000);
        uint other = g2.BitPosition();
        cg = ClientRoundTrip.Unpack(0x218, buf, other, false, cw, cg);
        Assert.True(ClientRoundTrip.Has(cg, "move") && ClientRoundTrip.Has(cg, "energy") && ClientRoundTrip.Has(cg, "f8c"));
        p.ControlClient = 2049;
        Array.Clear(buf);
        var g3 = new BitStream(buf, buf.Length);
        cls.Pack!(p, gctx, g3, 0x2000);
        Assert.True(g3.BitPosition() == 13 && other > 13 + 5 + 96);
        cg = ClientRoundTrip.Unpack(0x218, buf, g3.BitPosition(), false, cw, cg);
        Assert.True(!ClientRoundTrip.Has(cg, "pos")); // the controlling client gets no state block (4.7)
    }

    // ---- PSC inventory deltas (player-sim 7.1) and rate negotiation (game-stream 1.1) ----

    private sealed class Psc
    {
        public List<(uint, int)> Items = new();
        public bool RateSet;
        public uint Interval, Bytes;
    }

    private static void TestPscInventory()
    {
        var f = new Fixture("player_psc");
        Player p = f.Spawn("16 16 60");
        var sent = new List<(byte[] Data, uint Cookie)>();
        var transport = new GameConnection.Transport
        {
            Connected = () => true,
            Send = (d, n, _, cookie) =>
            {
                sent.Add((d.AsSpan(0, n).ToArray(), cookie));
                return true;
            },
        };
        var hooks = new GameConnection.Hooks();
        hooks.ControlObject = () => p;
        hooks.PscInputFn = inp => inp.ItemCounts = Player.InventoryCounts(p);
        var c = new GameConnection(2049, transport, hooks);
        uint now = 1000;
        Psc Send()
        {
            int before = sent.Count;
            c.Tick(now);
            now += 1000;
            Assert.True(sent.Count == before + 1);
            byte[] data = sent[^1].Data;
            var b = new BitStream(data, data.Length);
            var output = new Psc();
            output.RateSet = b.ReadFlag();
            if (output.RateSet)
            {
                output.Interval = b.ReadInt(10);
                output.Bytes = b.ReadInt(10);
            }
            Assert.True(!b.ReadFlag() && !b.ReadFlag() && b.ReadFlag()); // no rateReq, no events, PSC
            b.ReadInt(4);  // GUI mode, menu score flag
            b.ReadInt(32); // last move
            Assert.True(b.ReadFlag()); // control object
            b.ReadInt(8);              // damage flash
            Assert.True(b.ReadInt(10) == 0x3ff); // no ghost yet: no control state
            b.ReadFlag();              // inventory reset
            while (b.ReadFlag())
            {
                uint type = b.ReadInt(8);
                output.Items.Add((type, b.ReadSignedInt(10)));
            }
            return output;
        }
        static bool Same(List<(uint, int)> a, params (uint, int)[] b) => a.SequenceEqual(b);
        Assert.True(Send().Items.Count == 0);
        p.SetItemCount(3, 5);
        p.SetItemCount(7, 700);
        Psc a = Send();
        Assert.True(Same(a.Items, (3u, 5), (7u, 511)));
        uint cookie = sent[^1].Cookie;
        Assert.True(Same(Send().Items, (7u, 189)));
        Assert.True(Send().Items.Count == 0);
        c.Notify(cookie, false);
        Assert.True(Same(Send().Items, (3u, 5), (7u, 511)));
        p.SetItemCount(3, 2);
        Assert.True(Same(Send().Items, (3u, -3)));

        c.OwnIntervalMs = 33;
        c.OwnPacketBytes = 450;
        byte[] req = new byte[8];
        var w = new BitStream(req, req.Length);
        w.WriteFlag(false);
        w.WriteFlag(true);
        w.WriteInt(20, 10);
        w.WriteInt(1000, 10);
        w.WriteFlag(false);
        w.WriteFlag(false);
        c.Receive(req);
        Psc r = Send();
        Assert.True(r.RateSet && r.Interval == 33 && r.Bytes == 450 && !Send().RateSet);
        int count = sent.Count;
        c.Tick(now);      // the previous send was at now - 1000
        c.Tick(now + 20); // 20 ms later: inside the 33 ms interval
        c.Tick(now + 34);
        Assert.True(sent.Count == count + 2);
    }

    [Fact]
    public void Main()
    {
        TestStepVectors();
        TestSpawn();
        TestPlayerLine();
        TestCastWorldShortening();
        TestMoveCredit();
        TestForcesAndAim();
        TestConsoleTransform();
        TestDragAndClamp();
        TestEntryForces();
        TestNarrowPhase();
        TestInteriorMoves();
        TestShapesAndPlayers();
        TestItemCollision();
        TestMeshContacts();
        TestTriggers();
        TestAnimationWrap();
        TestTouchOrder();
        TestRiders();
        TestFreeLookOff();
        TestRepairThrowFade();
        TestCallbacks();
        TestInventory();
        TestImageStateMachine();
        TestRadiusDamage();
        TestMuzzleTransforms();
        TestDeflectionVectors();
        TestLaserGhost();
        TestFireEdgeTransforms();
        TestProjectiles();
        TestBeamAndSeekerProjectiles();
        TestSeekerRotation();
        TestSeekerVectors();
        TestMineDestruction();
        TestWire();
        TestPscInventory();
    }
}
