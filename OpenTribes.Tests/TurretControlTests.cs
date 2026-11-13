// A Turret as the control object: Turret_readControlState 0045711c (PSC control state), Turret_clientUpdate 00456428 with
// Turret_updateAim 00456874 (the replay of the buffered moves), Turret_getCameraTransform 004564fc.
namespace OpenTribes.Tests;

using OpenTribes.Game;
using OpenTribes.Net;
using OpenTribes.Script;
using OpenTribes.Sim;

public sealed class TurretControlTests
{
    private const int kGhost = 5;

    // A server PSC block (game-stream.md 6.3, mode 1) whose control state is Turret_writeControlState 004572f8.
    private static void Block(ClientWorld cw, float rot, float elev, uint fire, bool beam, uint state, float energy)
    {
        var buf = new byte[256];
        var bs = new BitStream(buf, buf.Length);
        bs.WriteInt(1, 3);
        bs.WriteFlag(false);
        bs.WriteInt(0, 32);
        bs.WriteFlag(true);
        bs.WriteInt(0, 8);
        bs.WriteInt(kGhost, 10);
        bs.WriteF32(rot);
        bs.WriteF32(elev);
        bs.WriteInt(fire, 2);
        bs.WriteFlag(beam);
        bs.WriteInt(state, 4);
        bs.WriteF32(energy);
        bs.WriteFlag(false);
        bs.WriteFlag(false);
        bs.WriteFlag(false);
        bs.WriteFlag(false);
        var r = new ClientReader(buf.AsSpan(0, bs.BytePosition()).ToArray(), cw);
        cw.Psc.ReadServerBlock(r);
    }

    private static ClientWorld World()
    {
        var cw = new ClientWorld();
        cw.Ghosts.Insert(new ClientGhost { Index = kGhost, Class = "Turret" });
        return cw;
    }

    [Fact]
    public void ControlStateOfATurretIsRead()
    {
        Numbers.EnterScriptFpuMode();
        ClientWorld cw = World();
        Block(cw, 1.5f, -0.25f, 2, true, 3, 77.5f);
        ClientPsc psc = cw.Psc;
        Assert.Equal("Turret", psc.ControlClass);
        Assert.Equal(kGhost, psc.ControlGhost);
        Assert.Equal(1.5, Convert.ToDouble(psc.ControlState["aimRot"]));
        Assert.Equal(-0.25, Convert.ToDouble(psc.ControlState["aimElev"]));
        Assert.Equal(2L, psc.ControlState["fire2"]);
        Assert.Equal(1L, psc.ControlState["beam"]);
        Assert.Equal(3L, psc.ControlState["state4"]);
        Assert.Equal(77.5, Convert.ToDouble(psc.ControlState["energy"]));
    }

    private static ClientPrediction Controlled(ClientWorld cw, float rot, float elev, uint state, TurretAimParams p, int moves, uint receive, Func<int, Move> move)
    {
        var pred = new ClientPrediction();
        pred.Turret.Resolve = _ => p;
        Block(cw, rot, elev, 0, false, state, 10);
        for (var i = 0; i < moves; ++i)
            cw.Psc.AddMove(move(i));
        pred.OnTurretBlock(cw, receive);
        return pred;
    }

    [Fact]
    public void BufferedMovesReplayThroughTheAimStep()
    {
        Numbers.EnterScriptFpuMode();
        ClientWorld cw = World();
        var p = new TurretAimParams(2.0f, 1.0f, false, -0.5f, 0.5f);
        // Three moves: the first tick is at ((receive - 1) & ~31) - 2 * 32.
        var pred = Controlled(cw, 1.0f, 0.0f, 3, p, 3, 3200, _ => ClientPrediction.Quantize(new Move { Turn = 0.25f, Pitch = 0.125f }));
        TurretControl t = pred.Turret;
        Assert.True(t.Active);
        Assert.Equal(3104u, t.SimTime);
        Assert.Equal(3, pred.AdvanceTurret(cw.Psc, 3200));
        Assert.Equal(3200u, t.SimTime);
        // limit 2 * 1, scale 0.032 * 16: 0.25 * 0.512 per tick on the rotation, 0.125 * 0.512 on the elevation
        Assert.Equal(1.0f + (3 * 0.25f * 0.512f), t.Rotation, 4);
        Assert.Equal(3 * 0.125f * 0.512f, t.Elevation, 4);
        // nothing left for the same time
        Assert.Equal(0, pred.AdvanceTurret(cw.Psc, 3200));
    }

    [Fact]
    public void OnlyStateThreeFollowsTheMoves()
    {
        Numbers.EnterScriptFpuMode();
        ClientWorld cw = World();
        var p = new TurretAimParams(2.0f, 1.0f, false, -0.5f, 0.5f);
        var pred = Controlled(cw, 1.0f, 0.1f, 2, p, 2, 3200, _ => ClientPrediction.Quantize(new Move { Turn = 1.0f, Pitch = 1.0f }));
        pred.AdvanceTurret(cw.Psc, 3200);
        Assert.Equal(1.0f, pred.Turret.Rotation);
        Assert.Equal(0.1f, pred.Turret.Elevation);
    }

    [Fact]
    public void ElevationStaysInTheLimitsAndRotationWraps()
    {
        Numbers.EnterScriptFpuMode();
        ClientWorld cw = World();
        var p = new TurretAimParams(40.0f, 1.0f, false, -0.5f, 0.5f);
        var pred = Controlled(cw, 6.2f, 0.45f, 3, p, 1, 3200, _ => ClientPrediction.Quantize(new Move { Turn = 1.0f, Pitch = 1.0f }));
        pred.AdvanceTurret(cw.Psc, 3200);
        Assert.Equal(0.5f, pred.Turret.Elevation);
        Assert.InRange(pred.Turret.Rotation, 0.0f, 6.2831855f);
        Assert.True(pred.Turret.Rotation < 1.0f);    // 6.2 + 0.512 wrapped by 2 pi
    }

    [Fact]
    public void SustainedTurretUsesTheOtherLimitAndScale()
    {
        Numbers.EnterScriptFpuMode();
        var t = new TurretControl { Mode = 3, Params = new TurretAimParams(2.0f, 100.0f, true, 0, 0) };
        // sustained: limit = speed * dt = 0.064, scale = dt * 32 = 1.024; the move 1.0 is clamped to the limit
        t.Aim(new Move { Turn = 1.0f, Pitch = -1.0f }, 0.032f);
        Assert.Equal(0.064f * 1.024f, t.Rotation, 5);
        Assert.Equal(-0.064f * 1.024f, t.Elevation, 5);
    }

    [Fact]
    public void ReleaseOnAnotherControlObject()
    {
        Numbers.EnterScriptFpuMode();
        ClientWorld cw = World();
        var p = new TurretAimParams(2.0f, 1.0f, false, -0.5f, 0.5f);
        var pred = Controlled(cw, 1.0f, 0.0f, 3, p, 1, 3200, _ => new Move());
        Assert.True(pred.Turret.Active);
        cw.Psc.ControlGhost = -1;
        pred.OnTurretBlock(cw, 3232);
        Assert.False(pred.Turret.Active);
        Assert.Equal(0, pred.AdvanceTurret(cw.Psc, 3300));
    }

    private static Transform Make(float rx, float ry, float rz, float x, float y, float z)
    {
        var t = new Transform();
        MissionTransform.EulerToMatrix([rx, ry, rz], ref t);
        t.Flags |= 3u;
        t.P[0] = x;
        t.P[1] = y;
        t.P[2] = z;
        return t;
    }

    [Fact]
    public void CameraWithoutEyeNodeIsTheObjectTransform()
    {
        Numbers.EnterScriptFpuMode();
        Transform obj = Make(0, 0, 0.7f, 5, 6, 7);
        Transform t = TurretCamera.Compute(obj, null, 1.0f, 0.3f, 3f, null, null);
        for (var i = 0; i < 9; ++i)
            Assert.Equal(obj.M[i], t.M[i]);
        Assert.Equal(7f, t.P[2]);
    }

    [Fact]
    public void CameraIsTheAimEulerAtTheEyeNodeTimesTheObject()
    {
        Numbers.EnterScriptFpuMode();
        Transform obj = Make(0, 0, 0, 10, 20, 30);
        Transform node = Make(0.9f, 0.9f, 0.9f, 0, 0, 2);          // only the position of the node is used
        float rot = (float)(Math.PI / 2), elev = 0.4f;
        Transform t = TurretCamera.Compute(obj, node, rot, elev, 0f, null, null);
        Assert.Equal(10f, t.P[0], 4);
        Assert.Equal(20f, t.P[1], 4);
        Assert.Equal(32f, t.P[2], 4);
        var e = new float[3];
        MissionTransform.MatrixToEuler(t, e);
        Assert.Equal(elev, e[0], 4);
        Assert.Equal(0f, e[1], 4);
        Assert.Equal(rot, e[2], 4);
    }

    [Fact]
    public void CameraMovesBackByOnePointThreeTimesTheDistance()
    {
        Numbers.EnterScriptFpuMode();
        Transform obj = Make(0, 0, 0, 0, 0, 10);
        Transform t = TurretCamera.Compute(obj, Make(0, 0, 0, 0, 0, 0), 0f, 0f, 2f, null, null);
        // forward is +y: back by 2.6 along the forward row, no pitch or roll scaling
        Assert.Equal(-2.6f, t.P[1], 3);
        Assert.Equal(10f, t.P[2], 3);
        var e = new float[3];
        Transform tilted = TurretCamera.Compute(obj, Make(0, 0, 0, 0, 0, 0), 0f, 0.5f, 2f, null, null);
        MissionTransform.MatrixToEuler(tilted, e);
        Assert.Equal(0.5f, e[0], 4);
    }
}
