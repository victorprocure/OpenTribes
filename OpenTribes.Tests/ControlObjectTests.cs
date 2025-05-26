// Port of tests/test_control_object.cpp: moves and control objects (docs/game-stream.md 6.1-6.5,
// docs/player-manager.md 12.5): the move wire form, the ObserverCamera, the PlayerPSC blocks in both directions, and the
// ownership and control links with their script commands. 258 asserts.
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Net;
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;

namespace OpenTribes.Tests;

public class ControlObjectTests
{
    private static uint Bits32(float f) => BitConverter.SingleToUInt32Bits(f);
    private static float FromBits(uint v) => BitConverter.UInt32BitsToSingle(v);

    // C++ value-initialised Move arrays: every element runs the Move constructor (useItem -1).
    private static Move[] MoveArray(int n)
    {
        var m = new Move[n];
        for (int i = 0; i < n; ++i)
            m[i] = new Move();
        return m;
    }

    // ---- 6.1 move ----

    private static void MoveWire()
    {
        // Full form, no previous move: 4 x int(4), jet, jump, crouch, fire, useItem, pitch, turn.
        var m = new Move();
        m.Forward = 1.0f;
        m.Left = (float)(7.0 / 15.0);
        m.Jet = true;
        m.Crouch = true;
        m.Fire = true;
        m.UseItem = 9;
        m.Pitch = 0.25f;
        byte[] buf = new byte[64];
        var w = new BitStream(buf, buf.Length);
        Moves.PackMove(w, m, true, null);
        uint total = w.BitPosition();
        Assert.True(total == 16 + 3 + 1 + 1 + 8 + 1 + 32 + 1);
        var b = new Bits(buf, total);
        Assert.True(b.U(4) == 15 && b.U(4) == 0 && b.U(4) == 7 && b.U(4) == 0);
        Assert.True(b.Flag() && !b.Flag() && b.Flag()); // jet, jump, crouch in this order
        Assert.True(b.Flag());                          // fire
        Assert.True(b.Flag() && b.U(8) == 9);           // useItem
        Assert.True(b.Flag() && b.F32() == 0.25f);      // pitch comes before turn
        Assert.True(!b.Flag() && b.Done());

        // Same motion as the previous move: one bit instead of the 19 motion bits. Fire, useItem, pitch and turn are not
        // part of the comparison.
        Move n = m;
        n.Fire = false;
        n.UseItem = -1;
        n.Pitch = 0;
        n.Turn = -0.5f;
        var w2 = new BitStream(buf, buf.Length);
        Moves.PackMove(w2, n, true, m);
        Assert.True(w2.BitPosition() == 1 + 1 + 1 + 1 + 1 + 32);
        var r2 = new BitStream(buf, buf.Length);
        Move o = m;
        Moves.UnpackMove(r2, ref o, true, havePrev: true);
        Assert.True(o.Forward == 1.0f && o.Left == m.Left && o.Jet && o.Crouch && !o.Jump);
        Assert.True(!o.Fire && o.UseItem == -1 && o.Pitch == 0 && o.Turn == -0.5f);

        // A changed flag breaks "same"; the short form (ghost echo) carries the motion only.
        n.Jump = true;
        var w3 = new BitStream(buf, buf.Length);
        Moves.PackMove(w3, n, false, m);
        Assert.True(w3.BitPosition() == 1 + 19);
        var r3 = new BitStream(buf, buf.Length);
        var shortForm = new Move();
        shortForm.Fire = true;
        shortForm.UseItem = 3;
        shortForm.Turn = 1;
        Moves.UnpackMove(r3, ref shortForm, false, in m);
        Assert.True(shortForm.Jump && shortForm.Jet && !shortForm.Fire && shortForm.UseItem == -1 && shortForm.Turn == 0);

        // Read values are n / 15; turn and pitch are clamped to 1 radian, NaN and infinity read 0.
        Assert.True(Moves.ClampMoveAngle(0.5f) == 0.5f && Moves.ClampMoveAngle(3.0f) == 1.0f);
        Assert.True(Moves.ClampMoveAngle(-3.0f) == -1.0f && Moves.ClampMoveAngle(-1.0f) == -1.0f);
        Assert.True(Moves.ClampMoveAngle(FromBits(0x7fc00000)) == 0.0f);
        Assert.True(Moves.ClampMoveAngle(FromBits(0x7f800000)) == 0.0f);
        Assert.True(Moves.ClampMoveAngle(FromBits(0xff800000)) == 0.0f);
    }

    // ---- 6.5 ObserverCamera ----

    private sealed class Calls
    {
        public readonly List<string> Lines = new();
        public ObserverCamera.CameraServices Services() => new() { Evaluate = l => Lines.Add(l) };
    }

    private static ObserverCamera.CameraServices NoServices() => new();

    private static void CameraFly()
    {
        var ctx = new ClassContext();
        var calls = new Calls();
        var cam = new ObserverCamera(ctx, 2049, calls.Services());
        // Constructor state (004348a4): fly mode, in the mission area, no targets.
        Assert.True(cam.Mode == ObserverCamera.kFly && cam.InMissionArea && cam.OrbitObject == null && cam.DamageObject == null);
        Assert.True(cam.ClassName() == "ObserverCamera" && (cam as SimObject) is GameBase g && g == cam);
        Assert.True(cam.ControlClient == 0 && cam.OwnerClient == 0 && cam.Team == -1 && cam.ClientId == 2049);

        float[] rot = { 0, 0, 0 }, pos = { 100, 200, 50 };
        cam.SetFlyMode(true, true, rot, pos);
        Assert.True(cam.Transform.P[0] == 100 && cam.Transform.P[1] == 200 && cam.Transform.P[2] == 50);

        // The step of a move runs with the previous move: the first applied move does not move.
        var fwd = new Move();
        fwd.Forward = 1;
        cam.ApplyMoves(new[] { fwd }, 1);
        Assert.True(cam.Position[1] == 200.0f);
        // Second move: 10 units/s forward (row 1 of the rotation) for one 32 ms tick. The tick length is 32 * 0.001
        // stored under round toward zero (0x3d03126e).
        cam.ApplyMoves(new[] { fwd }, 1);
        float dt = FromBits(0x3d03126e), speed = 10.0f, start = 200.0f;
        float delta = Fpu.Store(Fpu.Mul(dt, speed));
        float want = Fpu.Store(Fpu.Add(start, delta));
        Assert.True(cam.Position[0] == 100.0f && cam.Position[1] == want && cam.Position[2] == 50.0f);
        Assert.True(cam.Transform.P[1] == want);
        // Jet triples the speed; backward and left subtract; the result follows the yaw.
        var back = new Move();
        back.Backward = 1;
        back.Jet = true;
        cam.LastMove = back;
        float before = cam.Position[1];
        cam.Step(cam.LastMove);
        Assert.True(cam.Position[1] < before && Math.Abs((before - cam.Position[1]) - 0.96f) < 1e-4f);
        float[] quarter = { 0, 0, 1.57079637f };
        cam.SetFlyMode(true, true, quarter, pos);
        var right = new Move();
        right.Right = 1;
        cam.Step(right); // right is +x of the camera; after a quarter turn that is +y of the world
        Assert.True(Math.Abs(cam.Position[1] - 200.32f) < 1e-3f && Math.Abs(cam.Position[0] - 100.0f) < 1e-3f);

        // Turn adds to z and wraps into 0..2pi; pitch adds to x and clamps at +-1.3962 (0x3fb2b6ae).
        cam.SetFlyMode(true, true, rot, pos);
        var turn = new Move();
        turn.Turn = -0.25f;
        turn.Pitch = 1.0f;
        cam.Step(turn);
        Assert.True(Math.Abs(cam.CamRotation[2] - (6.2831855f - 0.25f)) < 1e-6f && cam.CamRotation[0] == 1.0f);
        cam.Step(turn);
        Assert.True(Bits32(cam.CamRotation[0]) == 0x3fb2b6ae && cam.CamRotation[1] == 0);
        turn.Turn = 0.5f;
        turn.Pitch = -1.0f;
        cam.Step(turn);
        cam.Step(turn);
        Assert.True(Math.Abs(cam.CamRotation[2] - 0.5f) < 1e-5f); // 2pi - 0.5 + 1.0 wrapped past 2pi
        for (int i = 0; i < 4; ++i)
            cam.Step(turn);
        Assert.True(Bits32(cam.CamRotation[0]) == 0xbfb2b6ae);

        // Mode 1 (fly flag off): looks around but does not move. Mode 0: nothing changes.
        cam.SetFlyMode(false, true, rot, pos);
        Assert.True(cam.Mode == ObserverCamera.kLook);
        Move both = fwd;
        both.Turn = 0.5f;
        cam.Step(both);
        Assert.True(cam.Position[1] == 200.0f && cam.CamRotation[2] == 0.5f);
        cam.SetFlyMode(true, false, rot, pos);
        Assert.True(cam.Mode == ObserverCamera.kFixed);
        cam.Step(both);
        Assert.True(cam.Position[1] == 200.0f && cam.CamRotation[2] == 0.0f);
        cam.SetFlyMode(false, false, rot, pos);
        Assert.True(cam.Mode == ObserverCamera.kFixed);
    }

    private static void CameraCallbacks()
    {
        var ctx = new ClassContext();
        var calls = new Calls();
        ObserverCamera.CameraServices s = calls.Services();
        float[] area = { 0, 0, 100, 100 };
        s.MissionArea = o =>
        {
            area.CopyTo(o, 0);
            return true;
        };
        var cam = new ObserverCamera(ctx, 2051, s);
        float[] rot = { 0, 0, 0 }, pos = { 50, 99.9f, 0 };
        cam.SetFlyMode(true, true, rot, pos);

        // Fire edges against the previous move call triggerDown / triggerUp with the client id; a held trigger calls
        // nothing. Jump calls Observer::jump for every move that has it.
        Move[] m = MoveArray(5);
        m[1].Fire = m[2].Fire = true;
        m[3].Jump = m[4].Jump = true;
        cam.ApplyMoves(m, 5);
        Assert.True(calls.Lines.SequenceEqual(new[] { "Observer::triggerDown(2051);", "Observer::triggerUp(2051);",
                                                      "Observer::jump(2051);", "Observer::jump(2051);" }));
        calls.Lines.Clear();

        // Leaving and entering the mission area (x, y, w, h; bounds inclusive) is checked once per applyMoves call and
        // reported with the camera's object id (0 while unregistered).
        var fwd = new Move();
        fwd.Forward = 1;
        Move[] run = { fwd, fwd, fwd };
        cam.ApplyMoves(run, 3);
        Assert.True(cam.Position[1] > 100.0f && !cam.InMissionArea);
        Assert.True(calls.Lines.SequenceEqual(new[] { "Observer::leaveMissionArea(0);" }));
        calls.Lines.Clear();
        cam.ApplyMoves(run, 1);
        Assert.True(calls.Lines.Count == 0);
        var back = new Move();
        back.Backward = 1;
        Move[] ret = { back, back, back, back };
        cam.ApplyMoves(ret, 4);
        cam.ApplyMoves(ret, 4);
        Assert.True(cam.InMissionArea && calls.Lines.SequenceEqual(new[] { "Observer::enterMissionArea(0);" }));
    }

    private static void CameraOrbit()
    {
        var ctx = new ClassContext();
        var calls = new Calls();
        var cam = new ObserverCamera(ctx, 2049, calls.Services());
        ObserverCamera target = new(ctx, 0, NoServices()), other = new(ctx, 0, NoServices());
        target.Transform.P[0] = 10;
        target.Transform.P[1] = 20;
        target.Transform.P[2] = 30;

        // Orbit point (00434afc with no object): mode 4, the point, rotation and distances.
        float[] point = { 1, 2, 3 }, rot = { 0, 0, 0 };
        cam.SetDamageObject(other);
        cam.SetOrbit(null, point, 5, 2, 8, rot);
        Assert.True(cam.Mode == ObserverCamera.kOrbitPoint && cam.Position[0] == 1 && cam.Position[2] == 3);
        Assert.True(cam.Distance == 5 && cam.MinDistance == 2 && cam.MaxDistance == 8 && cam.DamageObject == null);
        // A step puts the transform `distance` behind the point along the view direction (+y).
        var idle = new Move();
        cam.Step(idle);
        Assert.True(cam.Transform.P[0] == 1 && cam.Transform.P[1] == -3 && cam.Transform.P[2] == 3);
        Assert.True(cam.Position[1] == 2); // the orbit centre is kept
        // Forward moves closer at 10 units/s (30 with jet), clamped to the limits.
        var mIn = new Move();
        mIn.Forward = 1;
        mIn.Jet = true;
        cam.Step(mIn);
        Assert.True(Math.Abs(cam.Distance - 4.04f) < 1e-5f);
        for (int i = 0; i < 5; ++i)
            cam.Step(mIn);
        Assert.True(cam.Distance == 2.0f && cam.Transform.P[1] == 0.0f);
        var mOut = new Move();
        mOut.Backward = 1;
        mOut.Jet = true;
        for (int i = 0; i < 10; ++i)
            cam.Step(mOut);
        Assert.True(cam.Distance == 8.0f);

        // Orbit object: mode 3, centre = the object's box centre (its position without a container entry); the centre
        // follows the object at every step.
        cam.SetOrbit(target, point, 4, 4, 4, rot);
        Assert.True(cam.Mode == ObserverCamera.kOrbitObject && cam.OrbitObject == target);
        Assert.True(cam.Position[0] == 10 && cam.Position[1] == 20 && cam.Position[2] == 30);
        target.Transform.P[0] = 11;
        cam.Step(idle);
        Assert.True(cam.Position[0] == 11 && cam.Transform.P[0] == 11 && cam.Transform.P[1] == 16);
        // Negative distance: the camera takes the object's transform (first person at -1).
        cam.SetOrbit(target, point, -1, -1, -1, rot);
        cam.Step(idle);
        Assert.True(cam.Transform.P[0] == 11 && cam.Transform.P[1] == 20 && cam.Transform.P[2] == 30);

        // setDamageObject: the orbit object itself is stored as none (00434978). The flash getter asks the damage
        // object, then the orbit object, else 0 (00434a24).
        cam.SetDamageObject(target);
        Assert.True(cam.DamageObject == null);
        cam.SetDamageObject(other);
        Assert.True(cam.DamageObject == other && cam.DamageFlash() == 0.0f);

        // D13: a camera chain that leads back to itself reads 0 instead of recursing forever.
        cam.SetDamageObject(cam);
        Assert.True(cam.DamageObject == cam && cam.DamageFlash() == 0.0f);
        cam.SetDamageObject(other);

        // Delete notification (00434a94): damage object cleared silently; the orbit object drops to orbit-point mode
        // and the script is told with the client id.
        cam.ObjectDeleted(other);
        Assert.True(cam.DamageObject == null && calls.Lines.Count == 0);
        cam.ObjectDeleted(target);
        Assert.True(cam.OrbitObject == null && cam.Mode == ObserverCamera.kOrbitPoint);
        Assert.True(calls.Lines.SequenceEqual(new[] { "Observer::orbitObjectDeleted(2049);" }));
        // setFlyMode drops both targets.
        cam.SetOrbit(target, point, 4, 4, 4, rot);
        cam.SetDamageObject(other);
        cam.SetFlyMode(true, true, rot, point);
        Assert.True(cam.OrbitObject == null && cam.DamageObject == null && cam.Mode == ObserverCamera.kFly);
    }

    private static void CameraControlState()
    {
        var ctx = new ClassContext();
        ObserverCamera cam = new(ctx, 2049, NoServices()), target = new(ctx, 0, NoServices());
        float[] rot = { 0.5f, 0, 2.0f }, pos = { 1, 2, 3 };
        var cc = new ControlContext();
        byte[] buf = new byte[64];

        // Modes 0-2: rotation, int(3) mode, position.
        cam.SetFlyMode(true, true, rot, pos);
        var w = new BitStream(buf, buf.Length);
        cam.WriteControlState(w, cc);
        Assert.True(w.BitPosition() == 96 + 3 + 96);
        {
            var b = new Bits(buf, w.BitPosition());
            Assert.True(b.F32() == 0.5f && b.F32() == 0 && b.F32() == 2.0f && b.U(3) == 2);
            Assert.True(b.F32() == 1 && b.F32() == 2 && b.F32() == 3 && b.Done());
        }
        // Mode 4: position, then min, max and current distance.
        cam.SetOrbit(null, pos, 5, 2, 8, rot);
        var w4 = new BitStream(buf, buf.Length);
        cam.WriteControlState(w4, cc);
        Assert.True(w4.BitPosition() == 96 + 3 + 96 + 96);
        {
            var b = new Bits(buf, w4.BitPosition());
            b.U(32); b.U(32); b.U(32);
            Assert.True(b.U(3) == 4 && b.F32() == 1 && b.F32() == 2 && b.F32() == 3);
            Assert.True(b.F32() == 2 && b.F32() == 8 && b.F32() == 5 && b.Done());
        }
        // Mode 3 with an object the client has no ghost of is sent as mode 4.
        cam.SetOrbit(target, pos, 5, 2, 8, rot);
        var w3 = new BitStream(buf, buf.Length);
        cam.WriteControlState(w3, cc);
        {
            var b = new Bits(buf, w3.BitPosition());
            b.U(32); b.U(32); b.U(32);
            Assert.True(b.U(3) == 4 && w3.BitPosition() == 96 + 3 + 96 + 96);
        }
        Assert.True(cam.Mode == ObserverCamera.kOrbitObject); // only the wire value changes
    }

    // ---- 6.2-6.4 PlayerPSC ----

    private sealed class Fake
    {
        public readonly List<(byte[] Data, uint Cookie)> Sent = new();

        public GameConnection.Transport Transport() => new()
        {
            Connected = () => true,
            Send = (d, n, _, cookie) =>
            {
                Sent.Add((d.AsSpan(0, n).ToArray(), cookie));
                return true;
            },
        };
    }

    // Control object that records what it is given.
    private sealed class Probe : GameBase
    {
        public Probe(ClassContext ctx) : base("Probe", Kind.Marker, -1, Array.Empty<PersistDef>(), ctx) { }
        public override void ApplyMoves(Move[] m, int n)
        {
            Calls.Add(n);
            for (int i = 0; i < n; ++i)
                All.Add(m[i]);
        }
        public override void WriteControlState(BitStream bs, ControlContext ctx) => bs.WriteInt(0x5a, 8);
        public override float DamageFlash() => Flash;
        public readonly List<int> Calls = new();
        public readonly List<Move> All = new();
        public float Flash;
    }

    // Client packet: no rates, no events, PSC with fov, first move index and moves, no ghosts.
    private static byte[] MovePacket(uint first, IReadOnlyList<Move> moves, uint fov = 169)
    {
        byte[] buf = new byte[1400];
        var bs = new BitStream(buf, buf.Length);
        bs.WriteFlag(false);
        bs.WriteFlag(false);
        bs.WriteFlag(false);
        bs.WriteFlag(true);
        bs.WriteInt(fov, 8);
        bs.WriteInt(first, 32);
        for (int i = 0; i < moves.Count; ++i)
        {
            bs.WriteFlag(true);
            Moves.PackMove(bs, moves[i], true, i != 0 ? moves[i - 1] : null);
        }
        bs.WriteFlag(false);
        bs.WriteFlag(false);
        Assert.True(!bs.Error());
        return buf.AsSpan(0, bs.BytePosition()).ToArray();
    }

    // Ghost scope origin across a control switch to the ObserverCamera (docs/game-stream-ghosts.md 6.9). Order of one
    // frame: client packets (the moves step the control object at once), ticks (the camera has none), then the packet
    // write, whose PlayerPSC block names the scope object and whose ghost block runs the scope query on the camera
    // transform. Observer::setOrbitObject places nothing, so a packet written after the switch and before the next
    // client move scopes from the transform the camera was left with.
    private sealed class ScopeThing : SimObject
    {
        public override string ClassName() => "StaticShape";
    }

    private static void CameraSwitchScope()
    {
        var ctx = new ClassContext();
        var world = new Container();
        ScopeThing terrain = new(), atDrop = new(), atPlayer = new();
        var te = new Container.Entry(terrain, ObjectType.kTerrainType, Float6.Of(-9000, -9000, 0, 9000, 9000, 9000), false, null);
        te.DatabaseKind = Container.Database.Terrain;
        te.ContainerMask = 0xffffffffu;
        te.VisibleDistance = 600;
        world.Add(te);
        // 4000 m apart: an observer camera sees 1500 m (it is in no container).
        world.Add(new Container.Entry(atDrop, ObjectType.kStaticType, Float6.Of(-1, -1, 49, 1, 1, 51), false, null));
        world.Add(new Container.Entry(atPlayer, ObjectType.kStaticType, Float6.Of(3999, -1, 49, 4001, 1, 51), false, null));

        var body = new Probe(ctx); // the player: its eye transform is its transform
        body.Transform.P[0] = 4000;
        body.Transform.P[2] = 50;
        var cam = new ObserverCamera(ctx, 2050, NoServices());
        float[] drop = { 0, 0, 50 }, rot = { 0, 0, 0 };
        cam.SetFlyMode(true, true, rot, drop); // where the camera was left before the client spawned

        GameBase control = body;
        var f = new Fake();
        var hooks = new GameConnection.Hooks();
        hooks.ControlObject = () => control;
        int calls = 0;
        hooks.RemoteCall = (fn, args) =>
        {
            Assert.True(fn == "remoteOtCamera" && args.Count == 1 && args[0] == "2050");
            ++calls;
            control = cam;
            cam.SetOrbit(body, null, 6, 6, 6, rot);
        };
        var c = new GameConnection(2050, f.Transport(), hooks);
        var sweeps = new List<(SimObject? Object, float[] Pos)>();
        var created = new List<SimObject>();
        c.Ghosts().ScopeQuery = g =>
        {
            GhostScope.ScopeGhosts(g, world);
            sweeps.Add((g.Camera.Object, new[] { g.Camera.Pos[0], g.Camera.Pos[1], g.Camera.Pos[2] }));
        };
        c.Ghosts().OnNewGhost = (_, obj) => created.Add(obj);
        c.Ghosts().StartGhosting();
        uint now = 0;
        int acked = 0;
        void Send()
        {
            now += 100;
            int before = f.Sent.Count;
            c.Tick(now);
            Assert.True(f.Sent.Count == before + 1);
            while (acked + 4 < f.Sent.Count)
                c.Notify(f.Sent[acked++].Cookie, true);
        }
        bool Has(SimObject o) => created.Contains(o);
        Send(); // always phase: nothing to send, no sweep
        Assert.True(sweeps.Count == 0);

        // The player in control: the sweep starts at the player.
        var idle = new Move();
        byte[] first = MovePacket(0, new[] { idle });
        c.Receive(first);
        Send();
        Assert.True(sweeps.Count == 1 && sweeps[^1].Object == body && sweeps[^1].Pos[0] == 4000);
        Assert.True(Has(atPlayer) && !Has(atDrop));

        // The console line: control to the camera, orbit around the player. Nothing is placed.
        control = cam;
        cam.SetOrbit(body, null, 6, 6, 6, rot);
        Assert.True(cam.Mode == ObserverCamera.kOrbitObject && cam.Position[0] == 4000);
        Assert.True(cam.Transform.P[0] == 0 && cam.Transform.P[1] == 0 && cam.Transform.P[2] == 50);

        // A packet without a move, then a packet written before the next client packet: the camera has not been
        // stepped and the sweep starts at the drop point.
        byte[] empty = MovePacket(1, Array.Empty<Move>());
        c.Receive(empty);
        Send();
        Assert.True(sweeps.Count == 2 && sweeps[^1].Object == cam);
        Assert.True(sweeps[^1].Pos[0] == 0 && sweeps[^1].Pos[1] == 0 && sweeps[^1].Pos[2] == 50);
        Assert.True(Has(atDrop));
        Send();
        Assert.True(sweeps.Count == 3 && sweeps[^1].Pos[0] == 0);

        // The first move steps the camera on receipt, before the packet of the same frame is written: the sweep starts
        // 6 m behind the player. The ghost at the player, deleted by the stale sweeps, is created again.
        int ghosts = created.Count;
        byte[] move = MovePacket(1, new[] { idle });
        c.Receive(move);
        Assert.True(cam.Transform.P[0] == 4000 && cam.Transform.P[1] == -6 && cam.Transform.P[2] == 50);
        Send();
        Assert.True(sweeps.Count == 4 && sweeps[^1].Object == cam);
        Assert.True(sweeps[^1].Pos[0] == 4000 && sweeps[^1].Pos[1] == -6 && sweeps[^1].Pos[2] == 50);
        Assert.True(created.Count == ghosts + 1 && created[^1] == atPlayer);

        // The switch as a remote eval in one client packet with a move: the event is dispatched before the PlayerPSC
        // block is read, so the move steps the camera and no packet sees the stale transform.
        cam.SetFlyMode(true, true, rot, drop);
        control = body;
        calls = 0;
        byte[] buf = new byte[256];
        var bs = new BitStream(buf, buf.Length);
        bs.WriteFlag(false); // rateSet
        bs.WriteFlag(false); // rateReq
        bs.WriteFlag(true);  // events
        bs.WriteFlag(true);
        bs.WriteFlag(false); // unguaranteed
        bs.WriteInt(0x08, 7);
        bs.WriteInt(1, 5);
        bs.WriteString("OtCamera");
        bs.WriteFlag(false);
        bs.WriteFlag(true);  // PlayerPSC
        bs.WriteInt(169, 8);
        bs.WriteInt(2, 32);
        bs.WriteFlag(true);
        Moves.PackMove(bs, idle, true, null);
        bs.WriteFlag(false);
        bs.WriteFlag(false); // no ghost block
        Assert.True(!bs.Error());
        c.Receive(buf.AsSpan(0, bs.BytePosition()));
        Assert.True(calls == 1 && control == cam && body.Calls.Count == 1); // the move went to the camera
        Assert.True(cam.Transform.P[0] == 4000 && cam.Transform.P[1] == -6);
        Send();
        Assert.True(sweeps[^1].Object == cam && sweeps[^1].Pos[0] == 4000 && sweeps[^1].Pos[1] == -6);
        Assert.True(created.Count == ghosts + 1);
    }

    private static void ClientPsc()
    {
        var ctx = new ClassContext();
        var probe = new Probe(ctx);
        GameBase? control = null;
        var f = new Fake();
        var hooks = new GameConnection.Hooks();
        hooks.ControlObject = () => control;
        var c = new GameConnection(2049, f.Transport(), hooks);

        // Without a control object moves are counted and dropped (6.2).
        Move[] three = MoveArray(3);
        three[1].Forward = 1;
        three[2].Turn = 0.5f;
        byte[] p = MovePacket(0, three);
        c.Receive(p);
        Assert.True(c.MoveCount == 3 && probe.Calls.Count == 0);

        // With one: new moves reach applyMoves in one call; the fov byte is unit(8) of fov / 135 degrees and is stored
        // in radians (stock 90 degrees sends 169).
        control = probe;
        p = MovePacket(3, three);
        c.Receive(p);
        Assert.True(c.MoveCount == 6 && probe.Calls.SequenceEqual(new[] { 3 }));
        Assert.True(probe.All[0].Forward == 0 && probe.All[1].Forward == 1 && probe.All[2].Turn == 0.5f);
        Assert.True(MathF.Abs(probe.Fov - 169.0f / 255.0f * 135.0f * 3.14159265f / 180.0f) < 1e-6f);

        // A resend that overlaps: the moves the server has are skipped, the rest applied.
        Move[] five = MoveArray(5);
        five[3].Jet = true;
        five[4].Jump = true;
        p = MovePacket(4, five);
        c.Receive(p);
        Assert.True(c.MoveCount == 9 && probe.Calls[^1] == 3 && probe.All.Count == 6);
        Assert.True(!probe.All[3].Jet && probe.All[4].Jet && probe.All[5].Jump);
        // Entirely old: nothing applied, no call.
        p = MovePacket(0, three);
        c.Receive(p);
        Assert.True(c.MoveCount == 9 && probe.Calls.Count == 2);
        // A gap (first index after the next expected move) is an invalid packet: the connection reports it and reads
        // nothing more (docs/game-stream.md 6.6).
        int invalidCalls = 0;
        var hooks2 = new GameConnection.Hooks { ControlObject = hooks.ControlObject, InvalidPacket = () => ++invalidCalls };
        var d = new GameConnection(2049, f.Transport(), hooks2);
        p = MovePacket(0, three);
        d.Receive(p);
        Assert.True(d.MoveCount == 3 && !d.Invalid());
        p = MovePacket(10, three);
        d.Receive(p);
        Assert.True(d.MoveCount == 3 && probe.Calls.Count == 3 && d.Invalid() && invalidCalls == 1);
        p = MovePacket(3, three);
        d.Receive(p);
        Assert.True(d.MoveCount == 3 && invalidCalls == 1); // reported once, then ignored
        // The index is compared with 32-bit wrap: a huge index is a gap, not an overflow.
        foreach (uint firstIndex in new[] { 0x7fffffffu, 0x80000000u })
        {
            var e = new GameConnection(2049, f.Transport(), hooks2);
            p = MovePacket(firstIndex, three);
            e.Receive(p);
            Assert.True(e.MoveCount == 0 && e.Invalid());
        }
        Assert.True(probe.Calls.Count == 3);

        // At most 30 new moves of one packet are taken (D13); the client resends the rest.
        Move[] many = MoveArray(45);
        p = MovePacket(9, many);
        c.Receive(p);
        Assert.True(c.MoveCount == 39 && probe.Calls[^1] == 30);

        // lastMove in the server block is moveCount - 1 (6.3).
        c.Tick(1000);
        var b = new Bits(f.Sent[^1].Data, (uint)(f.Sent[^1].Data.Length * 8));
        Assert.True(!b.Flag() && !b.Flag() && !b.Flag() && b.Flag());
        b.U(3);
        b.Flag();
        Assert.True(b.U(32) == 38);
    }

    private sealed class ServerPsc
    {
        public uint Mode, LastMove, Flash, Ghost, State;
        public bool Flag6a, Control, InvReset;
        public readonly List<(uint Index, uint Shop, uint Buy)> Items = new(); // mode 4 rows
        public readonly List<(uint, uint)> Visible = new();                    // mode 1 words
        public bool Waypoint;
        public uint WaypointX, WaypointY;
        public uint CmdFirst, CmdRows;
        public readonly List<uint> CmdSlots = new();
    }

    // Decodes the PSC of a server packet that has no events. hasState: the Probe's 8-bit state.
    private static ServerPsc DecodePsc(byte[] d)
    {
        var p = new ServerPsc();
        var b = new Bits(d, (uint)(d.Length * 8));
        Assert.True(!b.Flag() && !b.Flag() && !b.Flag() && b.Flag());
        p.Mode = b.U(3);
        p.Flag6a = b.Flag();
        p.LastMove = b.U(32);
        p.Control = b.Flag();
        if (p.Control)
        {
            p.Flash = b.U(8);
            p.Ghost = b.U(10);
            if (p.Ghost != 0x3ff)
                p.State = b.U(8);
            if (p.Mode == 4)
                while (b.Flag())
                {
                    uint i = b.U(4);
                    uint shop = b.Flag() ? b.U(32) : 0;
                    uint buy = b.Flag() ? b.U(32) : 0;
                    p.Items.Add((i, shop, buy));
                }
            p.InvReset = b.Flag();
            Assert.True(!b.Flag()); // no item count deltas without a Player
            if (p.Mode == 2)
            {
                p.CmdFirst = b.U(8);
                uint slot = p.CmdFirst;
                while (b.Flag())
                {
                    ++p.CmdRows;
                    for (uint k = 0; k < 8; ++k)
                    {
                        if (!b.Flag()) // one flag per slot (6.3)
                            continue;
                        p.CmdSlots.Add(slot + k);
                        Assert.True(b.U(11) == slot + k && b.U(11) == 5 && b.U(3) == 1 && b.U(2) == 2);
                        if (slot + k < 128)
                            Assert.True(b.U(3) == 3);
                        else
                            Assert.True(b.Flag() && !b.Flag());
                        Assert.True(b.U(6) == 4 && b.U(3) == 6);
                    }
                    slot = (slot + 8) & 0xff;
                }
            }
            else if (p.Mode == 1)
            {
                p.Waypoint = b.Flag();
                if (p.Waypoint)
                {
                    p.WaypointX = b.U(10);
                    p.WaypointY = b.U(10);
                }
                while (b.Flag())
                {
                    uint i = b.U(3);
                    p.Visible.Add((i, b.U(32)));
                }
            }
        }
        Assert.True(!b.Flag()); // no ghost block
        Assert.True((b.Pos() + 7) / 8 == d.Length);
        return p;
    }

    // C++ `in = input`: the hook copies the whole input struct.
    private static void CopyInput(PscInput from, PscInput to)
    {
        to.Team = from.Team;
        from.Shopping.CopyTo(to.Shopping, 0);
        from.Buying.CopyTo(to.Buying, 0);
        from.Visible.CopyTo(to.Visible, 0);
        to.LimitCommand = from.LimitCommand;
        to.WaypointChanged = from.WaypointChanged;
        to.WaypointX = from.WaypointX;
        to.WaypointY = from.WaypointY;
        to.CommandSlot = from.CommandSlot;
        to.ItemCounts = from.ItemCounts;
    }

    private static void ServerPscTest()
    {
        var ctx = new ClassContext();
        var probe = new Probe(ctx);
        GameBase? control = null;
        var input = new PscInput();
        var f = new Fake();
        var hooks = new GameConnection.Hooks();
        hooks.ControlObject = () => control;
        hooks.PscInputFn = i => CopyInput(input, i);
        var c = new GameConnection(2049, f.Transport(), hooks);
        uint now = 0;
        // Sends the next packet. Packets older than the last four are reported delivered so the connection never
        // reaches its limit of unacknowledged packets.
        int acked = 0;
        ServerPsc Send()
        {
            now += 100;
            int before = f.Sent.Count;
            c.Tick(now);
            Assert.True(f.Sent.Count == before + 1);
            while (acked + 4 < f.Sent.Count)
                c.Notify(f.Sent[acked++].Cookie, true);
            return DecodePsc(f.Sent[^1].Data);
        }

        // No control object: the block ends after the flag (6.3).
        c.GuiMode = 1;
        c.Flag6a = true;
        ServerPsc p = Send();
        Assert.True(p.Mode == 1 && p.Flag6a && p.LastMove == 0xffffffff && !p.Control);
        Assert.True(c.Ghosts().ScopeObject() == null);

        // Control object that is not ghosted: flash byte, index 0x3ff, no control state. The object becomes the ghost
        // scope object. Play mode: no waypoint, then the visible mask as XOR words against what the client holds (all
        // of it the first time).
        control = probe;
        probe.Flash = 0.5f;
        input.Visible[0] = 0x6;
        input.Visible[4] = 0xa800ffff;
        p = Send();
        Assert.True(p.Control && p.Flash == 127 && p.Ghost == 0x3ff && !p.InvReset && !p.Waypoint);
        Assert.True(c.Ghosts().ScopeObject() == probe);
        Assert.True(p.Visible.SequenceEqual(new[] { (0u, 0x6u), (4u, 0xa800ffffu) }));
        uint firstCookie = f.Sent[^1].Cookie;
        // Unchanged mask: nothing. A change: only the changed word, as XOR.
        p = Send();
        Assert.True(p.Visible.Count == 0);
        input.Visible[4] = 0xa800fff0;
        p = Send();
        Assert.True(p.Visible.SequenceEqual(new[] { (4u, 0xfu) }));
        uint changeCookie = f.Sent[^1].Cookie;
        // 6.4: a lost packet puts its XOR words back, so they are sent again; a delivered one changes nothing.
        c.Notify(firstCookie, true);
        p = Send();
        Assert.True(p.Visible.Count == 0);
        c.Notify(changeCookie, false);
        Assert.True(c.ClientVisible()[4] == 0xa800ffff);
        p = Send();
        Assert.True(p.Visible.SequenceEqual(new[] { (4u, 0xfu) }));

        // Inventory reset flag: sent once, re-armed when that packet is lost.
        c.InventoryReset = true;
        p = Send();
        Assert.True(p.InvReset);
        uint resetCookie = f.Sent[^1].Cookie;
        p = Send();
        Assert.True(!p.InvReset);
        c.Notify(resetCookie, false);
        p = Send();
        Assert.True(p.InvReset && !Send().InvReset);

        // Inventory mode: rows of changed item-mask words (shopping, buying), XOR against the client's copy; no
        // visible mask in this mode.
        c.GuiMode = 4;
        input.Shopping[0] = 0x3;
        input.Buying[0] = 0x1;
        input.Shopping[6] = 0x80;
        p = Send();
        Assert.True(p.Mode == 4 && p.Visible.Count == 0 && p.Items.Count == 2);
        Assert.True(p.Items[0].Index == 0 && p.Items[0].Shop == 0x3 && p.Items[0].Buy == 0x1);
        Assert.True(p.Items[1].Index == 6 && p.Items[1].Shop == 0x80 && p.Items[1].Buy == 0);
        uint itemCookie = f.Sent[^1].Cookie;
        Assert.True(Send().Items.Count == 0);
        input.Buying[0] = 0;
        p = Send();
        Assert.True(p.Items.Count == 1 && p.Items[0].Shop == 0 && p.Items[0].Buy == 0x1);
        c.Notify(itemCookie, false);
        p = Send(); // the lost words come back
        Assert.True(p.Items.Count == 2 && p.Items[0].Shop == 0x3 && p.Items[0].Buy == 0x1);

        // Play mode waypoint: flag, then int(10) x and y when the hook reports a moved waypoint.
        c.GuiMode = 1;
        input.WaypointChanged = true;
        input.WaypointX = 1023;
        input.WaypointY = 7;
        p = Send();
        Assert.True(p.Waypoint && p.WaypointX == 1023 && p.WaypointY == 7);
        input.WaypointChanged = false;
        Assert.True(!Send().Waypoint);

        // Command mode: int(8) first slot, rows of 8 slots while the cursor has not come round (32 rows cover all 256
        // slots). The cursor starts at -8, so the first block starts at slot 0 and each later block 8 further. A slot is
        // one flag: slot 255 exists but is out of range and goes out as 0, like an absent one. No waypoint or mask in
        // this mode.
        c.GuiMode = 2;
        uint present = 0; // 0: slots 9, 130 and (out of range) 255; 1: every slot
        input.CommandSlot = (slot, s) =>
        {
            if (present == 0 && slot != 9 && slot != 130 && slot != 255)
                return false;
            s.InRange = slot != 255 || present != 0;
            s.X = slot;
            s.Y = 5;
            s.Team = 1;
            s.Status = 2;
            s.CommandStatus = 3;
            s.Controllable = true;
            s.Active = false;
            s.DbGroup = 4;
            s.DbIndex = 6;
            s.DbIndexBits = 3;
            return true;
        };
        input.WaypointChanged = true; // computed in every mode, written in play mode only
        p = Send();
        Assert.True(p.Mode == 2 && p.CmdFirst == 0 && p.CmdRows == 32 && !p.Waypoint && p.Visible.Count == 0);
        Assert.True(p.CmdSlots.SequenceEqual(new uint[] { 9, 130 }));
        input.WaypointChanged = false;
        p = Send();
        Assert.True(p.CmdFirst == 8 && p.CmdRows == 32 && p.CmdSlots.SequenceEqual(new uint[] { 9, 130 }));
        p = Send();
        Assert.True(p.CmdFirst == 16 && p.CmdRows == 32 && p.CmdSlots.SequenceEqual(new uint[] { 130, 9 }));
        // Limited bandwidth (PSC +0x90): no further row once more than 400 bits are written since the first row. A full
        // client row is 1 + 8 * 40 = 321 bits, so two rows go out. The next block starts 8 after the row the cursor
        // stopped at: that row (40..47) is skipped, as on the original.
        input.LimitCommand = true;
        present = 1;
        p = Send();
        Assert.True(p.CmdFirst == 24 && p.CmdRows == 2 && p.CmdSlots.Count == 16 && p.CmdSlots[^1] == 39);
        p = Send();
        Assert.True(p.CmdFirst == 48 && p.CmdRows == 2 && p.CmdSlots[0] == 48);
        input.LimitCommand = false;
        present = 0;

        // A ghosted control object: its index and its control state follow.
        c.GuiMode = 0;
        GhostManager.RegisterGhostClass("Probe", new GhostClass { Tag = 0x3f0, Pack = (_, _, _, _) => 0u });
        c.Ghosts().StartGhosting();
        c.Ghosts().MarkInScope(probe, true);
        for (int i = 0; i < 6 && c.Ghosts().GhostIndex(probe) < 0; ++i)
        {
            now += 100;
            c.Tick(now);
            c.Notify(f.Sent[^1].Cookie, true);
        }
        int index = c.Ghosts().GhostIndex(probe);
        if (index >= 0)
        {
            now += 100;
            c.Tick(now);
            var b = new Bits(f.Sent[^1].Data, (uint)(f.Sent[^1].Data.Length * 8));
            Assert.True(!b.Flag() && !b.Flag() && !b.Flag() && b.Flag());
            b.U(3); b.Flag(); b.U(32);
            Assert.True(b.Flag() && b.U(8) == 127 && b.U(10) == (uint)index && b.U(8) == 0x5a);
        }
        c.Ghosts().ObjectRemoved(probe);
    }

    // ---- docs/player-manager.md 12.5: links and commands ----

    private static Fixtures.World FreshWorld(string name)
    {
        string root = TestPaths.TmpDir(name);
        Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "base"));
        return new Fixtures.World(root);
    }

    private static void Ownership()
    {
        Fixtures.World w = FreshWorld("control_object");
        PlayerManager pm = w.Game.Players;
        var maskBits = new List<(int, uint)>();
        pm.SetMaskBitsHook = (o, bits) => maskBits.Add((o.Id(), bits));
        w.Sim.Server()!.OnUnregister = o => pm.ObjectRemoved(o);
        w.Run("resetPlayerManager();");
        w.Game.AddTeam(0);
        w.Game.AddTeam(1);
        w.Run("instant StaticShape \"a\" { position = \"10 20 30\"; }; instant StaticShape \"b\" { position = \"1 2 3\"; };");
        GameBase? a = w.Object("a") as GameBase;
        GameBase? b = w.Object("b") as GameBase;
        Assert.True(a != null && b != null);
        string A = a!.Id().ToString(), B = b!.Id().ToString();

        ClientRecord? c1 = pm.AddClient(2049, "One", "male1", true);
        ClientRecord? c2 = pm.AddClient(2050, "Two", "male1", true);
        Assert.True(c1 != null && c2 != null && c1.Camera != null && c2.Camera != null && c1.Camera.ClientId == 2049);
        ObserverCamera cam1o = c1!.Camera!, cam2o = c2!.Camera!;
        string cam1 = cam1o.Id().ToString();
        // The connection's Net::EventManager is a registered object whose pool id comes right before the camera's
        // (live: EventManager n, ObserverCamera n + 1 per client).
        Assert.True(w.Run("echo(getObjectType(" + (cam1o.Id() - 1) + "));") == "Net::EventManager");
        Assert.True(cam2o.Id() == cam1o.Id() + 2);
        // The camera is a registered GameBase: GameBase commands work on it; no links yet.
        Assert.True(w.Run("echo(Client::getObserverCamera(2049));") == cam1);
        Assert.True(w.Run("echo(GameBase::getControlClient(" + cam1 + "), GameBase::getOwnerClient(" + cam1 + "));") == "-1-1");
        Assert.True(w.Run("echo(Observer::setFlyMode(2049, \"5 6 7\", \"0 0 1\", true, true));") == "True");
        Assert.True(w.Run("echo(GameBase::getPosition(" + cam1 + "));") == "5 6 7");

        // Client::setControlObject (0041d4b8): the object's control client (+0x230) and the record's control object
        // are kept one to one; mask bit 4 marks the GameBase part.
        Assert.True(w.Run("echo(Client::setControlObject(2049, " + cam1 + "));") == "true");
        Assert.True(c1.ControlObject == cam1o.Id() && cam1o.ControlClient == 2049);
        Assert.True(w.Run("echo(Client::getControlObject(2049), GameBase::getControlClient(" + cam1 + "));") == cam1 + "2049");
        Assert.True(maskBits.SequenceEqual(new[] { (cam1o.Id(), 4u) }));
        Assert.True(pm.ControlObjectOf(2049) == cam1o && pm.ControlObjectOf(2050) == null);
        // Same client again: no change, no mask bits.
        maskBits.Clear();
        w.Run("Client::setControlObject(2049, " + cam1 + ");");
        Assert.True(maskBits.Count == 0);
        // Another object: the previous one is released first.
        Assert.True(w.Run("echo(Client::setControlObject(2049, a));") == "true");
        Assert.True(c1.ControlObject == a.Id() && a.ControlClient == 2049 && cam1o.ControlClient == 0);
        Assert.True(maskBits.SequenceEqual(new[] { (cam1o.Id(), 4u), (a.Id(), 4u) }));
        // Another client takes the object: the first client loses it.
        Assert.True(w.Run("echo(Client::setControlObject(2050, " + A + "));") == "true");
        Assert.True(c1.ControlObject == 0 && c2.ControlObject == a.Id() && a.ControlClient == 2050);
        // Not an object (-1): the client's current control object is released.
        Assert.True(w.Run("echo(Client::setControlObject(2050, -1));") == "true");
        Assert.True(c2.ControlObject == 0 && a.ControlClient == 0);
        Assert.True(w.Run("echo(Client::getControlObject(2050), GameBase::getControlClient(a));") == "-1-1");
        Assert.True(w.Run("echo(Client::setControlObject(2060, a));") == "false");

        // Client::setOwnedObject (0041d3b4): owner (+0x238) and the last non-zero owner (+0x234).
        maskBits.Clear();
        Assert.True(w.Run("echo(Client::setOwnedObject(2049, a));") == "true");
        Assert.True(c1.OwnedObject == a.Id() && a.OwnerClient == 2049 && a.LastOwnerClient == 2049);
        Assert.True(w.Run("echo(Client::getOwnedObject(2049), GameBase::getOwnerClient(a), Player::getClient(a));") ==
                    A + "20492049");
        Assert.True(maskBits.SequenceEqual(new[] { (a.Id(), 4u) }));
        // A client id as an object argument names that client's owned object (00422d18).
        Assert.True(pm.FindServerObject("2049") == a && pm.FindServerObject("2050") == null);
        Assert.True(pm.FindServerObject(A) == a && pm.FindServerObject("nothing") == null);
        Assert.True(w.Run("echo(GameBase::getPosition(2049));") == "10 20 30");
        Assert.True(w.Run("echo(GameBase::getOwnerClient(2049));") == "2049");
        // GameBase::getTeam resolves a client id as the client, not as its object (12.3). The client follows the team
        // of its owned object (GB6, 0x004483cd): the answer is the client's team, 1 after the object changed to 1.
        Assert.True(w.Run("echo(GameBase::getTeam(2049));") == "-1");
        a.SetTeam(1);
        Assert.True(w.Run("echo(GameBase::getTeam(2049));") == "1" && c1.Team == 1);
        // A team change of the client moves its owned object (5 step 7).
        pm.SetTeam(2049, 0);
        Assert.True(a.Team == 0);
        // Owning a second object releases the first; the last owner stays.
        Assert.True(w.Run("echo(Client::setOwnedObject(2049, b));") == "true");
        Assert.True(a.OwnerClient == 0 && a.LastOwnerClient == 2049 && b.OwnerClient == 2049 && c1.OwnedObject == b.Id());
        Assert.True(w.Run("echo(Client::setOwnedObject(2049, -1));") == "true");
        Assert.True(c1.OwnedObject == 0 && b.OwnerClient == 0 && b.LastOwnerClient == 2049);
        Assert.True(w.Run("echo(Client::getOwnedObject(2049), GameBase::getOwnerClient(b));") == "-1-1");

        // Error texts and argument counts.
        Assert.True(w.Run("echo(GameBase::getOwnerClient());") == "invalid call to GameBase::getOwnerClient(object)\n-1");
        Assert.True(w.Run("echo(Player::getClient());") == "invalid call to Player::getClient(object)\n-1");
        Assert.True(w.Run("echo(GameBase::getControlClient(1, 2));") == "invalid call to GameBase::getControlClient(object)\n-1");
        Assert.True(w.Run("echo(GameBase::getControlClient(nothing));") == "-1");

        // Observer commands: argument counts, return strings (0043a2fc, 0043a404, 0043a518, 0043a670).
        Assert.True(w.Run("echo(Observer::setOrbitObject(2049, a, 5, 5));") == "false");
        Assert.True(w.Run("echo(Observer::setOrbitObject(2060, a, 5, 5, 5));") == "false");
        Assert.True(w.Run("echo(Observer::setOrbitObject(2049, nothing, 5, 5, 5));") == "false");
        Assert.True(w.Run("echo(Observer::setOrbitObject(2049, 2050, 5, 5, 5));") == "false"); // client without an object
        Assert.True(w.Run("echo(Observer::setOrbitObject(2049, a, 5, 2, 9, \"0.1 0 0.2\"));") == "True");
        ObserverCamera cam = cam1o;
        Assert.True(cam.Mode == ObserverCamera.kOrbitObject && cam.OrbitObject == a);
        Assert.True(cam.Distance == 5 && cam.MinDistance == 2 && cam.MaxDistance == 9);
        Assert.True(MathF.Abs(cam.CamRotation[0] - 0.1f) < 1e-6f && MathF.Abs(cam.CamRotation[2] - 0.2f) < 1e-6f);
        Assert.True(cam.Position[0] == 10 && cam.Position[1] == 20 && cam.Position[2] == 30);
        Assert.True(w.Run("echo(Observer::setDamageObject(2049, b), Observer::setDamageObject(2049, b, 1));") == "truefalse");
        Assert.True(cam.DamageObject == b);
        Assert.True(w.Run("echo(Observer::setDamageObject(2049, nothing), Observer::setDamageObject(2060, b));") == "falsefalse");
        Assert.True(w.Run("echo(Observer::setOrbitPoint(2049, \"1 2 3\", -3, -3, -3));") == "True");
        Assert.True(cam.Mode == ObserverCamera.kOrbitPoint && cam.OrbitObject == null && cam.DamageObject == null);
        Assert.True(cam.Position[0] == 1 && cam.Distance == -3 && cam.CamRotation[0] == 0);
        Assert.True(w.Run("echo(Observer::setOrbitPoint(2049, \"1 2 3\", 1, 1));") == "false");
        Assert.True(w.Run("echo(Client::toggleCMDObserver(true), Client::toggleCMDObserver());") == "FalseFalse");
        Assert.True(w.Run("echo(Client::limitCommandBandwidth(2049, true), Client::limitCommandBandwidth(2060, true), " +
                          "Client::limitCommandBandwidth(2049));") == "truefalsefalse");
        Assert.True(c1.LimitCommand);

        // Deleting an object (GameBase::onRemove 00448180) clears the records that point at it and tells cameras that
        // orbit it.
        w.Run("Observer::setOrbitObject(2050, b, 5, 5, 5); Client::setControlObject(2049, b); Client::setOwnedObject(2050, b);");
        Assert.True(c1.ControlObject == b.Id() && c2.OwnedObject == b.Id() && cam2o.OrbitObject == b);
        int bid = b.Id();
        w.Sim.Server()!.DeleteObject(b);
        Assert.True(c1.ControlObject == 0 && c2.OwnedObject == 0 && bid != 0);
        Assert.True(cam2o.OrbitObject == null && cam2o.Mode == ObserverCamera.kOrbitPoint);

        // Disconnect: the owned object is deleted with the client, the camera too (4.2).
        w.Run("Client::setOwnedObject(2050, a);");
        int camId = cam2o.Id();
        pm.RemoveClient(2050);
        Assert.True(w.Sim.Server()!.FindById(camId) == null && w.Object("a") == null);
    }

    private static void Visibility()
    {
        Fixtures.World w = FreshWorld("control_object_vis");
        PlayerManager pm = w.Game.Players;
        w.Sim.Server()!.OnUnregister = o => pm.ObjectRemoved(o);
        w.Run("resetPlayerManager();");
        w.Game.AddTeam(0);
        w.Game.AddTeam(1);
        w.Run("instant StaticShape \"t0\" { position = \"0 0 0\"; }; instant StaticShape \"t1\" { position = \"1 0 0\"; };" +
              "instant StaticShape \"n\" { position = \"2 0 0\"; }; instant Marker \"mk\" { position = \"3 0 0\"; };");
        var t0 = (GameBase)w.Object("t0")!;
        var t1 = (GameBase)w.Object("t1")!;
        var n = (GameBase)w.Object("n")!;
        var mk = (GameBase)w.Object("mk")!;
        t0.SetTeam(0);
        t1.SetTeam(1);
        mk.SetTeam(-1);
        // The sweep (static-sim.md 3.4) sets the own-team bit of the objects it refreshes.
        var sensors = (SensorManager)w.Sim.Server()!.FindById(720)!;
        foreach (GameBase g in new[] { t0, t1, n, mk })
            sensors.Update(g);
        // An object is visible to its own team (00439d70); a Marker of team -1 to every team (00486154); nothing to
        // team -1, whose test reads bits 7 and 31 (004495b4).
        Assert.True(pm.VisibleToTeam(t0, 0) && !pm.VisibleToTeam(t0, 1) && !pm.VisibleToTeam(t0, -1));
        Assert.True(pm.VisibleToTeam(t1, 1) && !pm.VisibleToTeam(t1, 0));
        Assert.True(!pm.VisibleToTeam(n, 0) && !pm.VisibleToTeam(n, -1));
        Assert.True(pm.VisibleToTeam(mk, 0) && pm.VisibleToTeam(mk, 1) && !pm.VisibleToTeam(mk, -1));
        mk.SetTeam(1);
        Assert.True(!pm.VisibleToTeam(mk, 0) && pm.VisibleToTeam(mk, 1));
        mk.SetTeam(-1);

        // Mask: words 0-3 clients by owned object, words 4-7 sensor targets by index.
        w.Game.Targets.Clear();
        w.Game.Targets.AddRange(new GameBase?[] { t0, t1, null, mk });
        ClientRecord c = pm.AddClient(2051, "P", "male1", true)!;
        pm.SetOwnerClient(t1, 2051);
        uint[] mask = new uint[8];
        pm.VisibleMask(0, mask);
        Assert.True(mask[0] == 0 && mask[4] == 0x9 && mask[5] == 0);
        pm.VisibleMask(1, mask);
        Assert.True(mask[0] == (1u << 3) && mask[4] == 0xa);
        pm.VisibleMask(-1, mask);
        foreach (uint word in mask)
            Assert.True(word == 0);

        // Mission area and command map slots. Without a MissionCenterPos the area is zero.
        float[] area = { 9, 9, 9, 9 };
        Assert.True(!pm.MissionArea(area) && area[0] == 0 && area[3] == 0);
        w.Run("instant MissionCenterPos \"MissionCenter\" { x = \"-100\"; y = \"-200\"; w = \"400\"; h = \"800\"; };");
        Assert.True(pm.MissionArea(area) && area[0] == -100 && area[1] == -200 && area[2] == 400 && area[3] == 800);
        c.Team = 1;
        c.CommandStatus = 2;
        t1.Transform.P[0] = 100;  // the middle of the area in x
        t1.Transform.P[1] = -200; // the lower edge in y
        var s = new CommandMapSlot();
        // Client slot: the owned object; coordinates ((p - origin) + extent / 2) * 1023 / extent.
        Assert.True(pm.CommandSlot(c, 3, s));
        Assert.True(s.InRange && s.X == 1023 && s.Y == 511 && s.Team == 1 && s.CommandStatus == 3);
        Assert.True(!pm.CommandSlot(c, 4, s)); // no such client
        // Target slots start at 128; other teams' targets are absent, the Marker is present.
        var t = new CommandMapSlot();
        Assert.True(pm.CommandSlot(c, 129, t) && t.Team == 1);
        Assert.True(!pm.CommandSlot(c, 128, t) && !pm.CommandSlot(c, 130, t) && pm.CommandSlot(c, 131, t));
        Assert.True(!pm.CommandSlot(c, 140, t));
        // Far outside the area: present but not in range (11-bit coordinates).
        t1.Transform.P[0] = -5000;
        Assert.True(pm.CommandSlot(c, 129, t) && !t.InRange);
        t1.Transform.P[0] = 100;
        // Status (6.3): StaticBase sends damage state - 1 in two bits and its active byte; only a Turret can be
        // controllable; Marker (GameBase) sends 0 and no flags.
        Assert.True(pm.CommandSlot(c, 129, t) && t.Status == 0 && !t.Active && !t.Controllable);
        t1.Active = true;
        t1.DamageState = GameBase.kDestroyed;
        Assert.True(pm.CommandSlot(c, 129, t) && t.Status == 2 && t.Active && !t.Controllable);
        t1.DamageState = GameBase.kNone;
        Assert.True(pm.CommandSlot(c, 129, t) && t.Status == 3);
        t1.DamageState = GameBase.kEnabled;
        t1.Active = false;
        Assert.True(pm.CommandSlot(c, 131, t) && t.Status == 0 && !t.Active && !t.Controllable);
        // An ObserverCamera as owned object: status 2 (00434a5c).
        pm.SetOwnerClient(t1, 0);
        pm.SetOwnerClient(c.Camera!, 2051);
        c.Camera!.SensorWord = 1u << 1; // visible to team 1
        Assert.True(pm.CommandSlot(c, 3, s) && s.Status == 2 && s.CommandStatus == 3);
        pm.SetOwnerClient(c.Camera!, 0);
        pm.SetOwnerClient(t1, 2051);

        // GB6 (mission.md, 0x004483cd): GameBase::setTeam of an object with an owning client moves that client, so its
        // PlayerTeamChangeEvent (0x452) goes to every human; an unchanged team sends nothing (G14 leftover).
        {
            var evs = new List<(int, uint)>();
            Action<int, OutEvent> oldSend = pm.Send;
            pm.Send = (id, e) => evs.Add((id, e.Tag));
            t1.SetTeam(0);
            Assert.True(c.Team == 0 && evs.SequenceEqual(new[] { (2051, 0x452u) }));
            t1.SetTeam(1);
            Assert.True(c.Team == 1 && evs.Count == 2);
            evs.Clear();
            t1.SetTeam(1);
            Assert.True(evs.Count == 0);
            w.Run("GameBase::setTeam(" + t1.Id() + ", 0);");
            Assert.True(c.Team == 0 && evs.Count == 1);
            t1.SetTeam(1);
            pm.Send = oldSend;
            c.Team = 1;
        }

        // Waypoint of a targeted command: (p - origin) * 1023 / extent of the target while it is visible to the
        // client's team; reported once per change and stored in the record.
        uint wx, wy;
        c.CommandTarget = 129;
        Assert.True(!pm.UpdateWaypoint(c, out wx, out wy)); // no command (icon -1)
        c.CommandIcon = 5;
        Assert.True(pm.UpdateWaypoint(c, out wx, out wy) && wx == 511 && wy == 0);
        Assert.True(c.WaypointX == 511 && c.WaypointY == 0 && !pm.UpdateWaypoint(c, out wx, out wy));
        t1.Transform.P[1] = 600; // the upper edge: 1023
        Assert.True(pm.UpdateWaypoint(c, out wx, out wy) && wx == 511 && wy == 1023);
        t1.Transform.P[0] = 301; // 1025: outside 10 bits, nothing sent, record kept
        Assert.True(!pm.UpdateWaypoint(c, out wx, out wy) && c.WaypointX == 511);
        t1.Transform.P[0] = 0;
        c.CommandTarget = 128; // team 0's target: not visible to team 1
        Assert.True(!pm.UpdateWaypoint(c, out wx, out wy));
        c.CommandTarget = 3; // client 2051: its owned object t1
        Assert.True(pm.UpdateWaypoint(c, out wx, out wy) && wx == 255 && wy == 1023);
        c.CommandTarget = -1; // waypoint command: no target to follow
        t1.Transform.P[0] = 50;
        Assert.True(!pm.UpdateWaypoint(c, out wx, out wy));
        c.CommandIcon = -1;
        t1.Transform.P[0] = 100;
        t1.Transform.P[1] = -200;
        // A viewer always has its own slot, visible or not.
        c.Team = 0;
        Assert.True(pm.CommandSlot(c, 3, s));
        // A Player takes no sensor target slot, whatever its datablock says: SensorManager_addTarget (00439c70) refuses
        // datablock group 3, so it shows only in its client slot.
        w.Run("PlayerData varmor { shapeFile = \"none\"; visibleToSensor = true; };");
        SimObject? po = w.Object(w.Run("echo(spawnPlayer(varmor, \"0 0 0\", \"0 0 0\"));"));
        Assert.True(po is GameBase pgb && pgb.TargetIndex == -1);
        Assert.True(w.Game.Targets.Count == 4);
        foreach (GameBase? g in w.Game.Targets)
            Assert.True(g != po);
        w.Sim.Server()!.DeleteObject(po!); // no MissionCleanup group here to own it
        // Removed targets leave their slot empty.
        w.Sim.Server()!.DeleteObject(t0);
        Assert.True(w.Game.Targets[0] == null);
    }

    // docs/game-stream.md 1.1: the connection's members as server manager objects.

    // The connection object a client id names (Net::PacketStream, a group with the client id).
    private sealed class TestConnection : SimGroup
    {
        private readonly int mId;
        public TestConnection(int id) => mId = id;
        public override string ClassName() => "Net::PacketStream";
        public override int FixedId() => mId;
    }

    private static void ConnectionObjects()
    {
        Fixtures.World w = FreshWorld("connection_objects");
        PlayerManager pm = w.Game.Players;
        SimManager m = w.Sim.Server()!;
        w.Run("resetPlayerManager();");
        w.Game.AddTeam(0);
        Assert.True(w.Run("echo(isObject(37), isObject(715));") == "FalseFalse");

        (TestConnection, ClientRecord) Connect(int id, string name)
        {
            var conn = new TestConnection(id);
            m.AddToRoot(conn);
            ClientRecord? r = pm.AddClient(id, name, "male1", true);
            Assert.True(r != null && r.Camera != null && r.EventManager != null && r.PlayerPsc != null && r.GhostManager != null);
            return (conn, r!);
        }
        var (conn1, c1) = Connect(2049, "One");
        // Ids fixed by the constructors: GhostManager 37, PlayerPSC 715; the EventManager takes the pool id before the
        // camera's. All three are members of the connection, the two fixed ones named.
        Assert.True(c1.GhostManager!.Id() == 37 && c1.PlayerPsc!.Id() == 715);
        Assert.True(c1.EventManager!.Id() + 1 == c1.Camera!.Id());
        string em1 = c1.EventManager.Id().ToString();
        Assert.True(w.Run("echo(isObject(37), isObject(715));") == "TrueTrue");
        Assert.True(w.Run("echo(getObjectType(37), \"/\", getObjectType(715));") == "Net::GhostManager/PlayerPSC");
        Assert.True(w.Run("echo(getGroup(37), getGroup(715), getGroup(" + em1 + "));") == "204920492049");
        Assert.True(w.Run("echo(isObject(\"2049/PlayerPSC\"), isObject(\"2049/GhostManager\"));") == "TrueTrue");

        // A second connection registers the same ids: its objects shadow the first one's.
        var (conn2, c2) = Connect(2050, "Two");
        Assert.True(c2.GhostManager!.Id() == 37 && c2.PlayerPsc!.Id() == 715);
        Assert.True(c2.EventManager!.Id() == c1.Camera.Id() + 1);
        Assert.True(w.Run("echo(getGroup(37), getGroup(715));") == "20502050");

        // The second one leaves: the first connection's objects are found again.
        pm.RemoveClient(2050);
        m.DeleteObject(conn2);
        Assert.True(w.Run("echo(getGroup(37), getGroup(715), getObjectType(37));") == "20492049Net::GhostManager");
        pm.RemoveClient(2049);
        m.DeleteObject(conn1);
        Assert.True(w.Run("echo(isObject(37), isObject(715), isObject(" + em1 + "));") == "FalseFalseFalse");
    }

    [Fact]
    public void Main()
    {
        Numbers.EnterScriptFpuMode(); // the engine's rounding mode (docs/script.md 4)
        MoveWire();
        CameraFly();
        CameraCallbacks();
        CameraOrbit();
        CameraControlState();
        ClientPsc();
        ServerPscTest();
        CameraSwitchScope();
        Ownership();
        Visibility();
        ConnectionObjects();
    }
}
