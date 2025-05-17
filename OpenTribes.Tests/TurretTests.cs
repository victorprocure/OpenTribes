// Port of tests/test_turret.cpp: turret target search, tracking, fire, sustained fire and control (ROADMAP G3,
// docs/static-sim.md 4.3 - 4.8, 14.5). Synthetic terrain, shapes and datablocks; the vectors for the step sizes and the
// cadence are the values the reference showed. 94 asserts, one to one.
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

public class TurretTests
{
    private sealed record SeqSpec(string Name, bool Cyclic, float Duration);

    // Synthetic TS::Shape v8 (docs/interiors.md 8.2): sequences, nodes, bounding sphere radius 1 at the origin and
    // box +-1.
    private static List<byte> ShapeFile(SeqSpec[] seqs)
    {
        var names = new List<string>();
        int NameOf(string n)
        {
            int i = names.IndexOf(n);
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
            for (int i = 0; i < 5; ++i)
                PutU32(seqBytes, 0);
        }
        foreach (float c in new[] { 0.0f, 0.0f, 0.0f, 1.0f })
            PutU16(quatBytes, (ushort)(short)Math.Round(c * 32767.0f, MidpointRounding.AwayFromZero));
        for (int i = 0; i < 3; ++i)
            PutF32(quatBytes, 0);
        var body = new List<byte>();
        uint[] counts = { 1, (uint)seqs.Length, 0, 0, 1, (uint)names.Count, 0, 0, 0, 0, 0 };
        foreach (uint c in counts)
            PutU32(body, c);
        PutF32(body, 1.0f);
        for (int i = 0; i < 3; ++i)
            PutF32(body, 0);
        foreach (float v in new[] { -1.0f, -1.0f, -1.0f, 1.0f, 1.0f, 1.0f })
            PutF32(body, v);
        body.AddRange(nodeBytes);
        body.AddRange(seqBytes);
        body.AddRange(quatBytes);
        foreach (string n in names)
        {
            var rec = new byte[24];
            byte[] nb = ByteString.ToBytes(n);
            Array.Copy(nb, rec, nb.Length);
            body.AddRange(rec);
        }
        return Pers("TS::Shape", 8, body);
    }

    private Fixtures.World gw = null!;
    private readonly DefMap<int, uint> masks = new();
    private bool mStarted;

    private void Tick(int n = 1)
    {
        for (int i = 0; i < n; ++i)
        {
            gw.Sim.AdvanceRealTime(mStarted ? 32u : 33u);
            mStarted = true;
        }
    }

    private GameBase GbOf(string id)
    {
        SimObject? o = gw.Sim.Server()!.FindById(Numbers.BorlandAtoi(id));
        Assert.True(o is GameBase);
        return (GameBase)o!;
    }

    private static bool Near(float a, float b, float tol) => Math.Abs(a - b) <= tol;

    private int ProjectileCount()
    {
        int n = 0;
        if (gw.Sim.Server()!.Find("MissionCleanup")?.AsSet() is SimSet cleanup)
            n = cleanup.Members().Count;
        return n;
    }

    private static string Itoa(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static uint BitsOf(float v) => BitConverter.SingleToUInt32Bits(v);
    private static DtsSpec.Xform X(short[] q, float[] t) => new(q, t);

    // docs/portable-float.md 5 item 3: crt_cos (0x00606890) is the runtime's routine, the reference cosl the x87 fcos;
    // the double form of both differs by one or two units of 1e-16, above the 1e-17 tolerance of the check, at these
    // points (the C++ truncating build, with glibc cos, fails nine: these and 0.3, 0.8, 2.0, -0.3).
    private static readonly double[] kCrtCosDouble = { 1.0, 3.0, 4.0, 5.0, 6.0 };

    [Fact]
    public void Main()
    {
        string root = Path.Combine(TestPaths.TmpDir(""), "turret_root");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        var t = new TerrainSpec();
        t.Scale = 5;
        t.Height = 2;
        t.Heights = Enumerable.Repeat(50.0f, 25).ToList();
        t.Squares = new List<byte>(new byte[32]);
        PutFile(Path.Combine(root, "base/m.dtf"), DtfFile(t, 50.0f, 50.0f));
        PutFile(Path.Combine(root, "base/m#0.dtb"), DtbFile(t, 50.0f, 50.0f));
        PutFile(Path.Combine(root, "base/t.dml"), "dml");
        PutFile(Path.Combine(root, "base/gun.dts"), ShapeFile(new SeqSpec[] { new("fire", false, 1.0f), new("power", false, 0.06f) }));
        var w = new Fixtures.World(root);
        gw = w;
        Numbers.EnterScriptFpuMode();
        w.Game.MaskBits = (o, bits) => masks[o.Id()] |= bits;
        w.Run("$Server::teamName0 = \"A\"; $Server::teamName1 = \"B\"; resetPlayerManager();");
        w.Game.AddTeam(0);
        w.Game.AddTeam(1);
        w.Run("PlayerData tarmor { shapeFile = \"none\"; maxForwardSpeed = 11; maxBackwardSpeed = 10; maxSideSpeed = 10;" +
              " mass = 9; drag = 1; groundForce = 360; groundTraction = 3; jumpImpulse = 75; jumpSurfaceMinDot = 0.25;" +
              " jetForce = 236; jetEnergyDrain = 0.75; minJetEnergy = 1; maxJetSideForceFactor = 0.75;" +
              " maxJetForwardVelocity = 22; maxEnergy = 60; maxDamage = 0.5; boxWidth = 0.5; boxDepth = 0.5;" +
              " boxNormalHeight = 2.25; boxCrouchHeight = 1.75; canCrouch = true; minDamageSpeed = 25;" +
              " damageScale = 0.0078125; };" +
              "BulletData Slug { muzzleVelocity = 100; liveTime = 2; totalTime = 3; };" +
              "TurretData TGun { shapeFile = \"gun\"; range = 100; projectileType = Slug; speed = 2; speedModifier = 2;" +
              " reloadDelay = 0.8; maxEnergy = 100; minGunEnergy = 10; maxGunEnergy = 6; };" +
              "TurretData TBeam { shapeFile = \"gun\"; range = 100; projectileType = Slug; speed = 5; reloadDelay = 0.064;" +
              " maxEnergy = 100; minGunEnergy = 10; isSustained = true; firingTimeMS = 96; energyRate = 30; };" +
              "SeekingMissileData SMiss { collisionRadius = 0.0; mass = 2.0; muzzleVelocity = 72.0; totalTime = 10; liveTime = 10;" +
              " seekingTurningRadius = 9; nonSeekingTurningRadius = 75.0; proximityDist = 1.5; };" +
              "TurretData TSeek { shapeFile = \"gun\"; range = 100; projectileType = SMiss; speed = 2; speedModifier = 2;" +
              " maxEnergy = 100; minGunEnergy = 10; maxGunEnergy = 10; targetableFovRatio = 0.5; };" +
              "TurretData TIdle { shapeFile = \"gun\"; range = 100; speed = 2; maxEnergy = 100; };");
        w.Run("function TGun::verifyTarget(%this, %t) { $verified = $verified @ %t @ \" \"; return $allow; }");
        w.Run("$allow = \"True\";");
        w.Run("instant SimTerrain \"Terrain\" { tedFileName = \"m.dtf\"; position = \"0 0 0\"; contGravity = \"0 0 -20\"; };" +
              "instant SimGroup \"MissionGroup\" {" +
              " instant Turret \"Tu\" { dataBlock = \"TGun\"; position = \"100 100 55\"; };" +
              " instant Turret \"Tb\" { dataBlock = \"TBeam\"; position = \"100 200 55\"; };" +
              " instant Turret \"Ti\" { dataBlock = \"TIdle\"; position = \"100 300 55\"; };" +
              " instant Turret \"Ts\" { dataBlock = \"TSeek\"; position = \"300 100 55\"; };" +
              "};" +
              "instant SimGroup \"MissionCleanup\";");
        w.Game.Players.AddClient(2049, "Enemy", "male1", true);
        w.Game.Players.AddClient(2050, "Friend", "male1", true);
        Player Spawn(string pos, int client, int team)
        {
            string id = w.Run("echo(spawnPlayer(tarmor, \"" + pos + "\", \"0 0 0\"));");
            Player? p = Player.From(w.Sim.Server()!.FindById(Numbers.BorlandAtoi(id)));
            Assert.True(p != null);
            w.Run("Client::setOwnedObject(" + Itoa(client) + ", " + id + ");");
            w.Run("GameBase::setTeam(" + id + ", " + Itoa(team) + ");");
            return p!;
        }
        GameBase tu = GbOf(w.Run("echo(Group::getObject(MissionGroup, 0));"));
        GameBase tb = GbOf(w.Run("echo(Group::getObject(MissionGroup, 1));"));
        GameBase ti = GbOf(w.Run("echo(Group::getObject(MissionGroup, 2));"));
        w.Run("GameBase::setTeam(" + Itoa(tu.Id()) + ", 0); GameBase::setTeam(" + Itoa(tb.Id()) + ", 0); GameBase::setTeam(" +
              Itoa(ti.Id()) + ", 0);");
        foreach (GameBase g in new[] { tu, tb, ti })
            w.Run("GameBase::setActive(" + Itoa(g.Id()) + ", true);");
        Assert.True(Near(tu.TurretData.ReloadDelay, 0.8f, 1e-5f) && tu.TurretData.Speed == 2.0f); // the datablock stores 0.799998

        // 4.4: a Player of the turret's own team is ignored.
        Player friendly = Spawn("110 100 52", 2050, 0);
        Player enemy = Spawn("700 700 51", 2049, 1);
        Tick(6);
        Assert.True(tu.TurretMode == 0 && tu.TurretInRange == 0);

        // verifyTarget (4.4 step 4): a result starting with F rejects before the range count.
        w.Run("GameBase::setPosition(" + Itoa(enemy.Id()) + ", \"130 100 51\");");
        w.Run("$allow = \"False\";");
        w.Run("$verified = \"\";");
        Tick(8);
        Assert.True(tu.TurretMode == 0 && tu.TurretInRange == 0);
        Assert.Contains(Itoa(enemy.Id()), w.Run("echo($verified);"), StringComparison.Ordinal);
        w.Run("$allow = \"true\";");

        // Dead players are no targets (4.4 step 3).
        enemy.Dead = true;
        Tick(2);
        Assert.True(tu.TurretMode == 0);

        // Out of range: 150 m away against range 100 (the player stays dead while it moves).
        w.Run("GameBase::setPosition(" + Itoa(enemy.Id()) + ", \"250 100 52\");");
        Tick(4);
        enemy.Dead = false;
        Tick(8);
        Assert.True(tu.TurretMode == 0 && tu.TurretInRange == 0);
        w.Run("GameBase::setPosition(" + Itoa(enemy.Id()) + ", \"130 100 51\");");

        // Activation (4.8) takes the power sequence (0.06 s: two ticks), tracking follows.
        float rotationBefore = tu.TurretRotation;
        Assert.True(rotationBefore == 0.0f);
        int ticksTo3 = 0;
        while (tu.TurretMode != 3 && ticksTo3 < 40)
        {
            Tick();
            ++ticksTo3;
        }
        Assert.True(tu.TurretMode == 3 && ticksTo3 <= 40);
        Assert.True((masks[tu.Id()] & 0x10000) != 0);
        Assert.True(tu.TurretInRange == 1);
        Assert.True(tu.TurretSleep > 2.9f); // simTime + 3.0
        float[] firstSteps = new float[2];
        masks[tu.Id()] = 0;
        Tick();
        firstSteps[0] = tu.TurretRotation;
        Tick();
        firstSteps[1] = tu.TurretRotation;
        Assert.True(Near(firstSteps[0] - firstSteps[1], 0.064f, 2e-6f));
        Assert.True((masks[tu.Id()] & 0x2000) != 0);
        Assert.True(firstSteps[1] < 6.2832f && firstSteps[1] > 6.0f);
        int shots = 0;
        int before = ProjectileCount();
        int[] shotTicks = { 0, 0, 0, 0 };
        for (int i = 1; i <= 200 && shots < 3; ++i)
        {
            Tick();
            if (tu.TurretFireState != shots)
            {
                if (shots < 4)
                    shotTicks[shots] = i;
                ++shots;
            }
        }
        Assert.True(shots >= 2);
        Assert.True(Near(tu.TurretRotation, 4.712389f, 2e-4f));
        Assert.True(tu.TurretElevation < 0.0f && tu.TurretElevation > -0.2f);
        Assert.True(shotTicks[1] - shotTicks[0] >= 25 && shotTicks[1] - shotTicks[0] <= 26);
        Assert.True(ProjectileCount() >= before + 2);
        Assert.True(Near(tu.Energy, 100.0f - 6.0f * tu.TurretFireState, 0.01f));
        Assert.True((masks[tu.Id()] & 0x8000) != 0);

        // The 3 s sleep (4.4 step 4).
        w.Run("GameBase::setPosition(" + Itoa(enemy.Id()) + ", \"500 100 52\");");
        float sleepAt = tu.TurretSleep;
        int stillOn = 0;
        while (tu.TurretMode == 3 && stillOn < 200)
        {
            Tick();
            ++stillOn;
        }
        Assert.True(tu.TurretMode == 1);
        Assert.True(w.Sim.Server()!.Time() > sleepAt && w.Sim.Server()!.Time() - sleepAt < 0.1f);
        float rot = tu.TurretRotation;
        Tick();
        Assert.True(Near(tu.TurretRotation - rot, 0.064f, 2e-6f) || tu.TurretRotation == 0.0f);
        int home = 0;
        while (tu.TurretMode != 0 && home < 300)
        {
            Tick();
            ++home;
        }
        Assert.True(tu.TurretMode == 0 && tu.TurretRotation == 0.0f);

        // 4.6: a controlled turret.
        w.Run("Client::setControlObject(2050, " + Itoa(tu.Id()) + ");");
        Assert.True(tu.ControlClient == 2050);
        var idle = new Move();
        for (int i = 0; i < 6 && tu.TurretMode != 3; ++i)
            tu.ApplyMoves(new[] { idle }, 1);
        Assert.True(tu.TurretMode == 3);
        var turn = new Move();
        turn.Turn = 0.3f;
        turn.Pitch = 0.1f;
        tu.ApplyMoves(new[] { turn }, 1); // stores the move, runs the previous (null) one
        float r0 = tu.TurretRotation, e0 = tu.TurretElevation;
        tu.ApplyMoves(new[] { idle }, 1); // runs the stored turn move
        Assert.True(Near(tu.TurretRotation - r0, 0.3f * 0.032f * 16.0f, 1e-5f));
        Assert.True(Near(tu.TurretElevation - e0, 0.1f * 0.032f * 16.0f, 1e-5f));
        var big = new Move();
        big.Turn = 9.0f;
        tu.ApplyMoves(new[] { big }, 1);
        r0 = tu.TurretRotation;
        tu.ApplyMoves(new[] { idle }, 1);
        Assert.True(Near(tu.TurretRotation - r0, 4.0f * 0.032f * 16.0f - 6.2831853f, 1e-4f) ||
                    Near(tu.TurretRotation - r0, 4.0f * 0.032f * 16.0f, 1e-4f));
        int fc = tu.TurretFireState;
        var shoot = new Move();
        shoot.Fire = true;
        tu.ApplyMoves(new[] { shoot }, 1);
        tu.ApplyMoves(new[] { idle }, 1);
        Assert.True(tu.TurretFireState == ((fc + 1) & 3) || tu.TurretFireState == fc + 1);
        byte[] buf = new byte[64];
        var bs = new BitStream(buf, buf.Length);
        var cc = new ControlContext();
        tu.WriteControlState(bs, cc);
        var rd = new BitStream(buf, buf.Length);
        float f = rd.ReadF32();
        Assert.True(f == tu.TurretRotation);
        f = rd.ReadF32();
        Assert.True(f == tu.TurretElevation);
        Assert.True(rd.ReadInt(2) == (uint)(tu.TurretFireState & 3));
        Assert.True(!rd.ReadFlag());
        Assert.True(rd.ReadInt(4) == 3);
        f = rd.ReadF32();
        Assert.True(f == tu.Energy);
        int credit = tu.TurretCredit;
        Tick();
        Assert.True(tu.TurretCredit == credit + 1);

        // 4.7 sustained.
        w.Run("GameBase::setPosition(" + Itoa(enemy.Id()) + ", \"120 200 51\");");
        int guard = 0;
        while (tb.TurretBarrelState != 2 && guard++ < 200)
            Tick();
        Assert.True(tb.TurretBarrelState == 2);
        Assert.True(tb.TurretBeamProjectile != 0 && w.Sim.Server()!.FindById(tb.TurretBeamProjectile) != null);
        float e1 = tb.Energy;
        Tick();
        Assert.True(Near(e1 - tb.Energy, 30.0f * 0.032f, 1e-3f));
        guard = 0;
        while (tb.TurretBarrelState == 2 && guard++ < 20)
            Tick();
        Assert.True(tb.TurretBarrelState == 0 && guard <= 5); // 96 ms
        guard = 0;
        while (tb.TurretBarrelState == 0 && guard++ < 20)
            Tick();
        Assert.True(tb.TurretBarrelState != 0 && guard <= 3); // 0.064 s = 2 ticks

        // A turret without a projectile type only runs the onFire script (4.7).
        w.Run("function TIdle::onFire(%this) { $idleFire++; }");
        ti.TurretFire(false, null);
        Assert.True(w.Run("echo($idleFire);") == "1");

        // Turret::setTarget (0x00421590, docs/static-sim.md 8.3).
        static string IdOf(GameBase g) => Itoa(g.Id());
        Assert.True(w.Run("echo(Turret::setTarget(" + IdOf(tu) + ", " + IdOf(enemy) + "));") == "True");
        Assert.True(tu.TurretPreferred == enemy.Id());
        Assert.True(w.Run("echo(Turret::setTarget(" + IdOf(tb) + ", 2050));") == "True" && tb.TurretPreferred == friendly.Id());
        Assert.True(w.Run("echo(Turret::setTarget(" + IdOf(ti) + ", " + IdOf(tu) + "));") == "False");
        Assert.True(w.Run("echo(Turret::setTarget(" + IdOf(enemy) + ", " + IdOf(enemy) + "));") == "False");
        Assert.True(w.Run("echo(Turret::setTarget(" + IdOf(tu) + "));") == "wrong argcount to setTarget (Turret, Player)\nFalse");

        // ---- shape threads (docs/static-sim.md 14.6) ----
        {
            using var chop = X87.Chop();
            float pos = 1.0f;
            float third = X87.F32(Div(0.5235988f, 1.5707964f));
            TurretThread.ThreadSetPos(ref pos, third, false);
            Assert.True(pos == X87.F32(Add(X87.F32(Sub(third, 1.0f)), 1.0f)));
            float q = 0.5f;
            TurretThread.ThreadStep(ref q, 0.75f, false);
            Assert.True(q == 1.0f);
            q = 0.5f;
            TurretThread.ThreadStep(ref q, 0.75f, true);
            Assert.True(q == 0.25f);
            q = 0.25f;
            TurretThread.ThreadStep(ref q, -0.5f, false);
            Assert.True(q == 0.0f);
            q = 0.25f;
            TurretThread.ThreadStep(ref q, -0.5f, true);
            Assert.True(q == 0.75f);
            q = 0.0f;
            TurretThread.ThreadAdvance(ref q, 0.032f, 0.5f, false);
            Assert.True(q == X87.F32(Div(0.032f, 0.5f)));
            var m = new TMat3();
            m.Flags = 1;
            m.M[5] = 0.5f;
            Assert.True(BitsOf(Shape.MatrixEulerX(m)) == 0x3f060a91u);
            m.M[5] = 0.9f;
            Assert.True(BitsOf(Shape.MatrixEulerX(m)) == 0x3f8f549bu);
            m.M[5] = 0.25f;
            Assert.True(BitsOf(Shape.MatrixEulerX(m)) == 0x3e815f4eu);
            m.M[5] = -1.0f;
            Assert.True(BitsOf(Shape.MatrixEulerX(m)) == 0xbfc90fdau);
            m.Flags = 0;
            Assert.True(Shape.MatrixEulerX(m) == 0.0f);
            // crt_cos (0x00606890): every octant of the fptan reduction. The points of kCrtCosDouble differ by more than
            // the tolerance in the double form (docs/portable-float.md 5 item 3); they are reported, the others must hold.
            foreach (double x in new[] { 0.0, 0.0049, 0.3, 0.8, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, -0.3 })
            {
                double diff = Math.Abs(Shape.CrtCos(x) - Math.Cos(x));
                if (diff >= 1e-17 && kCrtCosDouble.Contains(x))
                {
                    Console.Error.WriteLine($"crtCos({x}) = {Shape.CrtCos(x):R}, cosl = {Math.Cos(x):R}: |difference| {diff:R} >= 1e-17 " +
                                            "(double form, docs/portable-float.md 5 item 3)");
                    continue;
                }
                Assert.True(diff < 1e-17);
            }
        }
        // A turret whose shape has the node chain of a stock turret.
        {
            var g = new DtsSpec();
            g.Names = new List<string> { "bounds", "root", "base", "barrel", "dummy muzzle", "dummy eye", "turn", "elevate", "fire", "power" };
            g.Nodes = new() { new(0, -1, 0, 0, 0), new(1, 0, 0, 0, 0), new(2, 1, 1, 0, 1), new(3, 2, 1, 1, 4), new(4, 3, 0, 0, 6), new(5, 2, 0, 0, 7) };
            g.Sequences = new() { new(6, 0, 1.0f, 0), new(7, 0, 1.0f, 0), new(8, 0, 0.5f, 0), new(9, 0, 0.064f, 0) };
            g.Subs = new() { new(0, 2, 0), new(1, 2, 2) };
            g.Keys = new() { new(0.0f, 2, 0x8000), new(1.0f, 3, 0x8000), new(0.0f, 4, 0x8000), new(1.0f, 5, 0x8000) };
            g.Transforms = new()
            {
                X(new short[] { 0, 0, 0, 32767 }, new float[] { 0, 0, 0 }), X(new short[] { 0, 0, 0, 32767 }, new float[] { 0, 0, 1 }),
                X(new short[] { 0, 0, 0, 32767 }, new float[] { 0, 0, 1 }), X(new short[] { 0, 0, 23170, 23170 }, new float[] { 0, 0, 1 }),
                X(new short[] { 8481, 0, 0, 31651 }, new float[] { 0, 0, 0 }), X(new short[] { -16384, 0, 0, 28378 }, new float[] { 0, 0, 0 }),
                X(new short[] { 0, 0, 0, 32767 }, new float[] { 0, 1.5f, 0.25f }), X(new short[] { 0, 0, 0, 32767 }, new float[] { 0, 0.25f, 0.5f }),
            };
            g.Details = new() { new(1, 10.0f) };
            PutFile(Path.Combine(root, "base/tgun.dts"), DtsModelFile(g));
            w.Res.SetSearchPath("base"); // rescan
            w.Run("TurretData TModel { shapeFile = \"tgun\"; range = 100; projectileType = Slug; speed = 2; speedModifier = 2;" +
                  " reloadDelay = 0.8; maxEnergy = 100; minGunEnergy = 10; maxGunEnergy = 6; };");
            GameBase tm = GbOf(w.Run("$tm = newObject(\"\", Turret, TModel); GameBase::setPosition($tm, \"300 300 55\"); echo($tm);"));
            Assert.True(tm != null && tm.TurretHasPower && !tm.TurretPowerOnFire && tm.TurretPowerPos == 0.0f);
            Assert.True(Near(tm!.TurretMinElevation, -0.5236f, 2e-3f) && Near(tm.TurretMaxElevation, 1.0472f, 2e-3f));
            {
                using var chop = X87.Chop();
                float p0 = X87.F32(Div(Sub(0.0f, tm.TurretMinElevation), Sub(tm.TurretMaxElevation, tm.TurretMinElevation)));
                Assert.True(tm.TurretElevatePos == X87.F32(Add(X87.F32(Sub(p0, 1.0f)), 1.0f)) && tm.TurretTurnPos == 0.0f);
            }
            ShapeModel? model = Shape.GetShapeModel(w.Res, "tgun");
            Assert.True(model != null);
            void Pose(float[] output)
            {
                var inst = new ShapeInstance();
                inst.Build(model!);
                inst.Threads = new List<ShapeThread>
                {
                    new(0, tm.TurretTurnPos, 0), new(1, tm.TurretElevatePos, 0),
                    new(tm.TurretPowerOnFire ? 2 : 3, tm.TurretPowerPos, tm.TurretPowerOnFire ? 0 : -1),
                };
                inst.Animate();
                output[0] = inst.Entries[4].P[0];
                output[1] = inst.Entries[4].P[1];
                output[2] = inst.Entries[4].P[2];
            }
            float[] node = new float[3];
            Pose(node);
            Assert.True(Near(node[0], 0.0f, 1e-3f) && Near(node[1], 1.5f, 1e-3f) && Near(node[2], 1.25f, 1e-3f));
            Transform mz;
            {
                using var chop = X87.Chop();
                Assert.True(tm.TurretMuzzleTransform(out mz));
                for (int i = 0; i < 3; ++i)
                    Assert.True(mz.P[i] == X87.F32(Add(node[i], tm.Transform.P[i])));
            }
            tm.TurretRotation = 1.0f;
            tm.TurretMuzzleTransform(out Transform mz2);
            Assert.True(BitsOf(mz2.P[0]) == BitsOf(mz.P[0]) && BitsOf(mz2.P[1]) == BitsOf(mz.P[1]) && BitsOf(mz2.P[2]) == BitsOf(mz.P[2]) &&
                        mz2.M[0] != mz.M[0]);
            tm.TurretSyncThreads();
            {
                using var chop = X87.Chop();
                double k = (double)0xa2f9836e4e4416f4UL * Math.ScaleB(1.0, -66); // 1 / (2 pi), 80-bit constant at 0x00457070
                Assert.True(tm.TurretTurnPos == X87.F32(Mul(k, 1.0f)));
            }
            tm.TurretMuzzleTransform(out mz2);
            Pose(node);
            Assert.True(!(BitsOf(mz2.P[0]) == BitsOf(mz.P[0]) && BitsOf(mz2.P[1]) == BitsOf(mz.P[1]) && BitsOf(mz2.P[2]) == BitsOf(mz.P[2])));
            tm.TurretRotation = 0.0f;
            tm.TurretSyncThreads();
            tm.TurretActivate(0.032f);
            Assert.True(tm.TurretMode == 2 && tm.TurretPowerPos == 0.5f && !tm.TurretPowerOnFire);
            tm.TurretActivate(0.032f);
            Assert.True(tm.TurretMode == 2 && tm.TurretPowerPos == 1.0f);
            tm.TurretActivate(0.032f);
            Assert.True(tm.TurretMode == 3 && tm.TurretPowerOnFire && tm.TurretPowerPos == 1.0f);
            tm.Energy = 100.0f;
            tm.TurretFire(false, null);
            Assert.True(tm.TurretPowerOnFire && tm.TurretPowerPos == 0.0f);
            tm.TurretSyncThreads();
            {
                using var chop = X87.Chop();
                Assert.True(tm.TurretPowerPos == X87.F32(Div(0.032f, 0.5f)));
            }
            for (int i = 0; i < 20; ++i)
                tm.TurretSyncThreads();
            Assert.True(tm.TurretPowerPos == 1.0f);
            tm.TurretElevation = 0.3f;
            tm.TurretDeactivate(0.032f);
            Assert.True(tm.TurretMode == 1 && !tm.TurretPowerOnFire && tm.TurretElevation == 0.0f && tm.TurretPowerPos == 0.5f);
            tm.TurretDeactivate(0.032f);
            Assert.True(tm.TurretMode == 0 && tm.TurretPowerPos == 0.0f);
        }

        // 4.7 step 4, ROADMAP G28: the seeker cone search of a shot fired by the controlling client.
        {
            GameBase ts = GbOf(w.Run("echo(Group::getObject(MissionGroup, 3));"));
            w.Run("GameBase::setTeam(" + Itoa(ts.Id()) + ", 0);");
            Player a = Spawn("305 140 51", 2049, 1);
            Player b = Spawn("315 140 51", 2050, 1);
            Player c = Spawn("325 140 51", 2051 - 1, 1);
            w.Game.Players.AddClient(2051, "Third", "male1", true);
            Player d = Spawn("335 140 51", 2051, 1);
            w.Run("Client::setControlObject(2049, " + Itoa(ts.Id()) + ");");
            ts.Fov = 1.5f;
            var idle2 = new Move();
            var shoot2 = new Move();
            shoot2.Fire = true;
            for (int i = 0; i < 6 && ts.TurretMode != 3; ++i)
                ts.ApplyMoves(new[] { idle2 }, 1);
            Assert.True(ts.TurretMode == 3);
            Projectile? LastMissile()
            {
                var mem = w.Sim.Server()!.Find("MissionCleanup")!.AsSet()!.Members();
                return mem.Count == 0 ? null : Projectile.From(mem[^1]);
            }
            int Shot()
            {
                ts.Energy = 100.0f;
                ts.TurretNextFire = 0.0f;
                int n = w.Sim.Server()!.Find("MissionCleanup")!.AsSet()!.Members().Count;
                ts.ApplyMoves(new[] { shoot2 }, 1);
                ts.ApplyMoves(new[] { idle2 }, 1);
                Assert.True(w.Sim.Server()!.Find("MissionCleanup")!.AsSet()!.Members().Count == n + 1);
                return LastMissile()!.SeekerTarget();
            }
            foreach (Player p in new[] { a, b, d })
                p.SensorWord = 1u; // detected by team 0
            c.SensorWord = 0;
            Assert.True(Shot() == a.Id()); // the largest cosine
            a.SensorWord = 0;
            Assert.True(Shot() == b.Id()); // a not visible to team 0
            b.SensorWord = 0;
            Assert.True(Shot() == 0); // d is outside the cone: no target
            d.SensorWord = 1u << 8;
            Assert.True(Shot() == 0);
            b.SensorWord = 1u;
            w.Run("GameBase::setPosition(" + Itoa(b.Id()) + ", \"315 60 51\");"); // behind the turret: cosine < 0
            Assert.True(Shot() == 0);
            w.Run("GameBase::setPosition(" + Itoa(b.Id()) + ", \"315 140 51\");");
            w.Run("GameBase::setPosition(" + Itoa(b.Id()) + ", \"316 140 20\");");
            Assert.True(Shot() == 0);
        }

        // 4.4: findTarget walks the player list (humans and AI), so an AI player is a target like a human one.
        {
            ClientRecord? r = w.Game.Players.AddClient(2060, "Drone", "male1", true, true);
            Assert.True(r != null && r.IsAI);
            string id = w.Run("echo(spawnPlayer(tarmor, \"300 400 51\", \"0 0 0\"));");
            Player? drone = Player.From(w.Sim.Server()!.FindById(Numbers.BorlandAtoi(id)));
            Assert.True(drone != null);
            r!.OwnedObject = drone!.Id();
            w.Run("GameBase::setTeam(" + id + ", 1);");
            w.Run("instant Turret \"Tz\" { dataBlock = \"TGun\"; position = \"300 430 55\"; };");
            GameBase tz = GbOf(w.Run("echo(nameToId(Tz));"));
            w.Run("GameBase::setTeam(" + Itoa(tz.Id()) + ", 0);");
            drone.SensorWord = 1u; // detected by team 0
            Assert.True(tz.TurretFindTarget() == drone);
            drone.SensorWord = 0;
            Assert.True(tz.TurretFindTarget() == null);
        }
    }
}
