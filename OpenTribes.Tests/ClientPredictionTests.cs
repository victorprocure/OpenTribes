// Client prediction and server correction of the control object (docs/client-net.md 4.2 step 4, 4.4; ROADMAP M9 C3b).
// Two worlds with the same synthetic flat terrain: the server world steps the server object, the client world holds the
// client copy. The server state travels through the real wire path (writeControlState, ClientPsc.ReadServerBlock).
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Net;
using OpenTribes.Script;
using OpenTribes.World;
using static OpenTribes.Tests.Fixtures;

namespace OpenTribes.Tests;

public class ClientPredictionTests
{
    private const float kFlat = 50.0f;
    private const int kGhost = 5;
    private const uint kT0 = 1000;   // first tick of the client clock

    internal sealed class Side
    {
        public readonly Fixtures.World W;

        public Side(string name)
        {
            string root = Path.Combine(TestPaths.TmpDir(""), name);
            if (Directory.Exists(root))
                Directory.Delete(root, true);
            var t = new TerrainSpec { Scale = 5, Width = 4, Height = 4 }; // 4 x 4 repeats of the 128 m block, flat
            t.Heights = Enumerable.Repeat(kFlat, 25).ToList();
            t.Squares = new List<byte>(new byte[32]);
            PutFile(Path.Combine(root, "base/m.dtf"), DtfFile(t, kFlat, kFlat));
            PutFile(Path.Combine(root, "base/m#0.dtb"), DtbFile(t, kFlat, kFlat));
            PutFile(Path.Combine(root, "base/t.dml"), "dml");
            float[] gen = { -1, -1.5f, 0, 1, 1.5f, 2 };
            PutFile(Path.Combine(root, "base/gen.dts"), DtsCollisionFile(gen));
            W = new Fixtures.World(root);
            Numbers.EnterScriptFpuMode();
            W.Run("$Server::teamName0 = \"A\"; resetPlayerManager();");
            W.Run("PlayerData tarmor { shapeFile = \"none\"; maxForwardSpeed = 11; maxBackwardSpeed = 10;" +
                  " maxSideSpeed = 10; mass = 9; drag = 1; groundForce = 360; groundTraction = 3; jumpImpulse = 75;" +
                  " jumpSurfaceMinDot = 0.25; jetForce = 236; jetEnergyDrain = 0.75; minJetEnergy = 1;" +
                  " maxJetSideForceFactor = 0.75; maxJetForwardVelocity = 22; maxEnergy = 60; maxDamage = 0.5;" +
                  " boxWidth = 0.5; boxDepth = 0.5; boxNormalHeight = 2.25; boxCrouchHeight = 1.75; canCrouch = true;" +
                  " minDamageSpeed = 25; damageScale = 0.0078125; };");
            W.Run("FlierData Scout { shapeFile = \"gen\"; className = \"Vehicle\"; mass = 9.0; drag = 1.0; density = 1.2;" +
                  " maxBank = 0.5; maxPitch = 0.5; maxSpeed = 50; minSpeed = -2; lift = 0.75; maxAlt = 25; maxVertical = 10;" +
                  " maxDamage = 0.6; damageLevel = {1.0, 1.0}; maxEnergy = 100; accel = 0.4; groundDamageScale = 1.0;" +
                  " reloadDelay = 2.0; ramDamage = 1.5; ramDamageType = -1; visibleDriver = true; driverPose = 22; };");
            W.Run("instant SimGroup \"MissionGroup\" {" +
                  " instant SimTerrain \"Terrain\" { tedFileName = \"m.dtf\"; position = \"-256 -256 0\";" +
                  "   contGravity = \"0 0 -20\"; }; };" +
                  "instant SimGroup \"MissionCleanup\";");
        }

        public Player Spawn(string pos)
        {
            string id = W.Run("echo(spawnPlayer(tarmor, \"" + pos + "\", \"0 0 0\"));");
            return Player.From(W.Object(id))!;
        }

        public Flier Make(string pos)
        {
            string id = W.Run("echo(newObject(\"\", flier, Scout, true));");
            var f = (Flier)W.Object(id)!;
            W.Run("GameBase::setPosition(" + id + ", \"" + pos + "\");");
            return f;
        }
    }

    // A server PSC block (game-stream.md 6.3, mode 1) with the control state of `obj`, read by the client branch.
    private static void Correct(ClientWorld cw, GameBase obj, int lastMove)
    {
        byte[] buf = new byte[256];
        var bs = new BitStream(buf, buf.Length);
        bs.WriteInt(1, 3);                       // GUI mode 1
        bs.WriteFlag(false);
        bs.WriteInt(unchecked((uint)lastMove), 32);
        bs.WriteFlag(true);                      // control object
        bs.WriteInt(0, 8);                       // damage flash
        bs.WriteInt(kGhost, 10);
        obj.WriteControlState(bs, new ControlContext());
        bs.WriteFlag(false);                     // inventory reset
        bs.WriteFlag(false);                     // no inventory delta
        bs.WriteFlag(false);                     // no waypoint
        bs.WriteFlag(false);                     // no visible-mask word
        var r = new ClientReader(buf.AsSpan(0, bs.BytePosition()).ToArray(), cw);
        cw.Psc.ReadServerBlock(r);
    }

    private static Move MoveOf(int k, bool moving, bool flier)
    {
        var m = new Move();
        if (!moving)
            return ClientPrediction.Quantize(m);
        m.Forward = k % 9 < 6 ? 1.0f : 0.4f;
        m.Right = k % 7 == 3 ? 0.6f : 0.0f;
        m.Turn = k % 5 == 0 ? 0.05f : (k % 5 == 2 ? -0.03f : 0.0f);
        m.Pitch = k % 4 == 1 ? 0.02f : 0.0f;
        m.Jet = k % 13 >= 8;
        m.Jump = !flier && k % 17 == 4;
        return ClientPrediction.Quantize(m);
    }

    private static uint U(float f) => BitConverter.SingleToUInt32Bits(f);

    private static void AssertSame(GameBase a, GameBase b)
    {
        for (int i = 0; i < 3; ++i)
        {
            Assert.Equal(U(a.Transform.P[i]), U(b.Transform.P[i]));
            Assert.Equal(U(a.Velocity[i]), U(b.Velocity[i]));
            Assert.Equal(U(a.Rotation[i]), U(b.Rotation[i]));
        }
        Assert.Equal(U(a.Energy), U(b.Energy));
        if (a is Player pa && b is Player pb)
        {
            Assert.Equal(U(pa.Pitch), U(pb.Pitch));
            Assert.Equal(pa.Jetting, pb.Jetting);
            Assert.Equal(pa.ContactFlag, pb.ContactFlag);
        }
        if (a is Flier fa && b is Flier fb)
        {
            Assert.Equal(U(fa.SpeedF), U(fb.SpeedF));
            Assert.Equal(U(fa.SpeedL), U(fb.SpeedL));
            Assert.Equal(U(fa.SpeedS), U(fb.SpeedS));
            Assert.Equal(fa.Landed, fb.Landed);
        }
    }

    // The server step of one move: Player applyMoves runs updateMove and updateAnimation (player-sim.md 2.1), a
    // Vehicle its update with dt 0.032 (vehicle-sim.md 3.1).
    private static void ServerStep(GameBase o, Move sent)
    {
        Move m = ClientPrediction.ServerMove(sent); // what GameConnection.ReadPsc unpacks
        if (o is Player p)
        {
            p.UpdateMove(m);
            p.UpdateAnimation(0.032f);
        }
        else if (o is Flier f)
        {
            Move mv = m;
            f.Update(ref mv, 0.032f);
        }
    }

    // A Flier's control state carries the speeds and the energy as unit(16) (vehicle-sim.md 6.2): the server object is
    // reset to the decoded state too, so both sides step on from the same values with the server code.
    private static void Requantize(Run run)
    {
        var sv = (Flier)run.Server;
        var c = new Vehicle.ClientControl
        {
            SpeedF16 = (uint)(long)run.Cw.Psc.ControlState["F16"]!, SpeedT16 = (uint)(long)run.Cw.Psc.ControlState["T16"]!,
            Energy16 = (uint)(long)run.Cw.Psc.ControlState["E16"]!, HasFlier = true, L = sv.SpeedL, S = sv.SpeedS, Landed = sv.Landed,
        };
        for (int i = 0; i < 3; ++i)
        {
            c.Rot[i] = sv.Rotation[i];
            c.TargetRot[i] = sv.TargetRot[i];
            c.Position[i] = sv.Transform.P[i];
        }
        sv.ApplyClientControlState(c);
    }

    private sealed class Run
    {
        public readonly ClientWorld Cw = new();
        public readonly ClientPrediction Pred = new();
        public readonly List<Move> Moves = new();
        public GameBase Server = null!, Client = null!;
        public uint Tick = kT0;  // the client frame time is 32 * Tick
    }

    private static Run Setup(string name, bool flier, bool moving)
    {
        var srv = new Side(name + "_srv");
        var cli = new Side(name + "_cli");
        var run = new Run();
        if (flier)
        {
            Flier s = srv.Make("100 100 70"), c = cli.Make("100 100 70");
            s.Piloted = true;
            run.Server = s;
            run.Client = c;
        }
        else
        {
            run.Server = srv.Spawn("100 100 50");
            run.Client = cli.Spawn("100 100 50");
        }
        run.Cw.Ghosts.Insert(new ClientGhost { Index = kGhost, Class = flier ? "Flier" : "Player" });
        GameBase copy = run.Client;
        run.Pred.CreateObject = (g, p) => copy;
        for (int k = 0; k < 120; ++k)
            run.Moves.Add(MoveOf(k, moving, flier));
        return run;
    }

    // One client frame at 32 * tick (client-net.md 2.1): the move of the tick crossed (4.2), then the replay (4.4).
    private static void Frame(Run run)
    {
        ++run.Tick;
        run.Cw.Psc.AddMove(run.Moves[(int)run.Cw.Psc.MovesCreated]);
        run.Pred.Advance(run.Cw.Psc, 32 * run.Tick);
    }

    // client-net.md 4.4: after a correction with N moves pending, the replay leaves the copy where N server steps of the
    // same moves leave the server object, bit for bit. The server has applied the moves before lastMove.
    private static void ReplayEqualsServer(string name, bool flier, bool moving)
    {
        foreach (int pending in new[] { 1, 4, 11 })
        {
            Run run = Setup(name + pending, flier, moving);
            const int lastMove = 15;
            for (int k = 0; k < lastMove + pending; ++k)
                run.Cw.Psc.AddMove(run.Moves[k]);
            for (int k = 0; k < lastMove; ++k)
                ServerStep(run.Server, run.Moves[k]);
            // Flier: the control state carries the speeds and the energy as unit(16), so the reference is a server object
            // reset to the decoded state (vehicle-sim.md 6.2), stepped by the server code.
            Correct(run.Cw, run.Server, lastMove);
            Assert.Equal(pending, run.Cw.Psc.Moves.Count);
            uint now = 32 * run.Tick;
            CorrectionRecord? rec = run.Pred.OnServerBlock(run.Cw, now);
            Assert.True(rec != null && rec.Predicted == null && rec.Pending == pending);
            if (flier)
                Requantize(run);
            else
                AssertSame(run.Server, run.Client); // the reset state is the server state
            Assert.Equal(pending, run.Pred.Advance(run.Cw.Psc, now));
            Assert.Equal(pending, rec.Replayed);
            for (int k = lastMove; k < lastMove + pending; ++k)
                ServerStep(run.Server, run.Moves[k]);
            AssertSame(run.Server, run.Client);
        }
    }

    // client-net.md 4.4: a correction whose state is the prediction for move lastMove - 1 changes nothing: the record
    // measures 0 and the replay ends on the state the copy had before the reset.
    private static void MatchingCorrection(string name, bool flier, bool moving)
    {
        Run run = Setup(name, flier, moving);
        // The first block: the server object at its start, no move applied yet.
        Frame(run);
        Correct(run.Cw, run.Server, 0);
        Assert.NotNull(run.Pred.OnServerBlock(run.Cw, 32 * run.Tick));
        if (flier)
            Requantize(run);
        run.Pred.Advance(run.Cw.Psc, 32 * run.Tick);
        int applied = 0;
        for (int round = 0; round < 6; ++round)
        {
            for (int f = 0; f < 3 + round; ++f)
                Frame(run);
            // The server applies all but the newest two moves (one packet of latency).
            int lastMove = (int)run.Cw.Psc.MovesCreated - 2;
            for (; applied < lastMove; ++applied)
                ServerStep(run.Server, run.Moves[applied]);
            PredictedState before = PredictedState.Of(run.Client, 0);
            Correct(run.Cw, run.Server, lastMove);
            CorrectionRecord rec = run.Pred.OnServerBlock(run.Cw, 32 * run.Tick)!;
            Assert.True(rec.Predicted != null && rec.Predicted.Move == lastMove - 1);
            Assert.True(rec.PosError == 0 && rec.VelError == 0 && rec.RotError == 0 && rec.PitchError == 0 && rec.EnergyError == 0,
                        "round " + round + ": " + rec.PosError + " " + rec.VelError + " " + rec.RotError + " " + rec.PitchError + " " + rec.EnergyError);
            if (flier)
                Requantize(run);
            Assert.Equal(2, run.Pred.Advance(run.Cw.Psc, 32 * run.Tick));
            PredictedState after = PredictedState.Of(run.Client, 0);
            if (flier)
                continue; // the reset re-quantises the speeds and the energy: the replay starts from other values
            for (int i = 0; i < 3; ++i)
            {
                Assert.Equal(U(before.Position[i]), U(after.Position[i]));
                Assert.Equal(U(before.Velocity[i]), U(after.Velocity[i]));
                Assert.Equal(U(before.Rotation[i]), U(after.Rotation[i]));
            }
            Assert.Equal(U(before.Energy), U(after.Energy));
            Assert.Equal(U(before.Pitch), U(after.Pitch));
        }
    }

    [Fact]
    public void PlayerReplayIdle() => ReplayEqualsServer("cp_pri", false, false);

    [Fact]
    public void PlayerReplayMoving() => ReplayEqualsServer("cp_prm", false, true);

    [Fact]
    public void FlierReplayIdle() => ReplayEqualsServer("cp_fri", true, false);

    [Fact]
    public void FlierReplayMoving() => ReplayEqualsServer("cp_frm", true, true);

    [Fact]
    public void PlayerMatchingCorrectionIdle() => MatchingCorrection("cp_pmi", false, false);

    [Fact]
    public void PlayerMatchingCorrectionMoving() => MatchingCorrection("cp_pmm", false, true);

    [Fact]
    public void FlierMatchingCorrectionIdle() => MatchingCorrection("cp_fmi", true, false);

    [Fact]
    public void FlierMatchingCorrectionMoving() => MatchingCorrection("cp_fmm", true, true);

    // 4.2 step 4: the buffered move is quantised (axes in 1/15, angles clamped, NaN to 0) and packs again to the same
    // bits for every packet (4.3); the prediction steps with the move as the server reads it (toward zero).
    [Fact]
    public void QuantizedMove()
    {
        Numbers.EnterScriptFpuMode();
        var m = new Move { Forward = 0.5f, Left = 1.0f, Turn = 3.0f, Pitch = float.NaN, Jet = true };
        Move q = ClientPrediction.Quantize(m);
        Assert.Equal(7.0f / 15.0f, q.Forward);
        Assert.Equal(1.0f, q.Left);
        Assert.Equal(1.0f, q.Turn);
        Assert.Equal(0.0f, q.Pitch);
        Assert.True(q.Jet && q.UseItem == -1);
        Move s = ClientPrediction.ServerMove(q);
        Assert.Equal(U(Fpu.F32Chop(7.0 / 15.0)), U(s.Forward));
        Assert.True(Fpu.TowardZero); // the mode of the caller is kept
        for (int k = 0; k <= 15; ++k)
        {
            Move a = ClientPrediction.Quantize(new Move { Forward = k / 15.0f, Right = k / 15.0f });
            byte[] buf = new byte[16];
            var bs = new BitStream(buf, buf.Length);
            Moves.PackMove(bs, a, true, null);
            Assert.Equal((uint)k, new BitStream(buf, buf.Length).ReadInt(4));
            Assert.Equal(U(Fpu.F32Chop(k / 15.0)), U(ClientPrediction.ServerMove(a).Right));
        }
    }

    // 4.4 (00404c04): the newest entry belongs to the tick that holds `now`; a full buffer of 30 gives no move.
    [Fact]
    public void MoveForTick()
    {
        var psc = new ClientPsc();
        for (int k = 0; k < 4; ++k)
            psc.AddMove(new Move { Forward = k / 15.0f });
        uint now = 32 * 100 + 5;            // inside tick 100
        Assert.True(ClientPrediction.MoveForTick(psc, 32 * 100, now, out Move m, out int idx) && idx == 3);
        Assert.True(ClientPrediction.MoveForTick(psc, 32 * 97, now, out m, out idx) && idx == 0 && m.Forward == 0.0f);
        Assert.False(ClientPrediction.MoveForTick(psc, 32 * 96, now, out _, out _));
        for (int k = 4; k < 30; ++k)
            psc.AddMove(new Move());
        Assert.Equal(30, psc.Moves.Count);
        Assert.False(ClientPrediction.MoveForTick(psc, 32 * 100, now, out _, out _));
    }

    // Player_updateMove on a ghost (+0x4c bit 0x02): S7 does not touch the crouch flag, the control state keeps it.
    [Fact]
    public void GhostKeepsCrouch()
    {
        var side = new Side("cp_crouch");
        Player p = side.Spawn("100 100 50");
        p.ClientSide = true;
        p.LocallyControlled = true;
        var m = ClientPrediction.Quantize(new Move { Crouch = true });
        p.UpdateMove(m);
        Assert.False(p.Crouching);
        p.ClientSide = false;
        p.UpdateMove(m);
        Assert.True(p.Crouching);
    }
}
