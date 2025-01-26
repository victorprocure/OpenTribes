// Port of src/game/control_object.hpp and src/game/control_object.cpp: moves and control objects (docs/game-stream.md
// 6.1, 6.5; docs/player-control.md): the move record, its wire form, and the ObserverCamera every human client owns.
using OpenTribes.Core;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

// src/game/control_object.hpp struct Move: move record (32 bytes in the original, 6.1).
public struct Move
{
    public float Forward, Backward, Left, Right; // +0x00..+0x0c, 0..1 in 1/15 steps
    public bool Jet;     // +0x10
    public bool Crouch;  // +0x11
    public bool Jump;    // +0x12
    public bool Fire;    // +0x13
    public int UseItem;  // +0x14
    public float Turn;   // +0x18 yaw delta
    public float Pitch;  // +0x1c pitch delta

    public Move()
    {
        Forward = Backward = Left = Right = 0;
        Jet = Crouch = Jump = Fire = false;
        UseItem = -1;
        Turn = Pitch = 0;
    }
}

// src/game/control_object.hpp struct ControlContext: what a control state writer may ask about the receiving connection.
public sealed class ControlContext
{
    public GhostManager? Ghosts;
}

// BitStream helpers of the game files: the raw float writes (bs.writeBytes(&f, 4)).
public static class BitStreamF
{
    public static void WriteF32(this BitStream bs, float f)
    {
        Span<byte> b = stackalloc byte[4];
        BitConverter.TryWriteBytes(b, f);
        bs.WriteBytes(b);
    }

    public static void WriteF32s(this BitStream bs, ReadOnlySpan<float> v)
    {
        foreach (float f in v)
            bs.WriteF32(f);
    }

    public static float ReadF32(this BitStream bs)
    {
        Span<byte> b = stackalloc byte[4];
        b.Clear();
        bs.ReadBytes(b);
        return BitConverter.ToSingle(b);
    }
}

public static class Moves
{
    // Largest turn or pitch delta of one move (float at 0060f084).
    public const float kMoveMaxTurn = 1.0f;

    // src/game/control_object.cpp clampMoveAngle: NaN becomes 0 (00404860).
    public static float ClampMoveAngle(float v)
    {
        uint bits = BitConverter.SingleToUInt32Bits(v);
        if (((bits >> 23) & 0xff) == 0xff)
            return 0; // NaN and infinity
        if (!(-kMoveMaxTurn < v))
            return -kMoveMaxTurn;
        return kMoveMaxTurn <= v ? kMoveMaxTurn : v;
    }

    // src/game/control_object.cpp packMove: move pack 004046a0. full: the PlayerPSC form with fire, useItem, pitch and
    // turn. prev: the preceding move, enabling the same-as-previous bit.
    public static void PackMove(BitStream bs, in Move m, bool full, Move? prev)
    {
        bool body = true;
        if (prev is Move p)
        {
            bool same = m.Forward == p.Forward && m.Backward == p.Backward && m.Left == p.Left && m.Right == p.Right &&
                        m.Jet == p.Jet && m.Jump == p.Jump && m.Crouch == p.Crouch;
            body = !bs.WriteFlag(same);
        }
        if (body)
        {
            foreach (float f in new[] { m.Forward, m.Backward, m.Left, m.Right })
                bs.WriteInt(GameUtil.Ftol(Mul(f, 15.0)), 4);
            bs.WriteFlag(m.Jet);
            bs.WriteFlag(m.Jump);
            bs.WriteFlag(m.Crouch);
        }
        if (!full)
            return;
        bs.WriteFlag(m.Fire);
        if (bs.WriteFlag(m.UseItem != -1))
            bs.WriteInt((uint)m.UseItem, 8);
        if (bs.WriteFlag(m.Pitch != 0.0f))
            bs.WriteF32(m.Pitch);
        if (bs.WriteFlag(m.Turn != 0.0f))
            bs.WriteF32(m.Turn);
    }

    // src/game/control_object.cpp unpackMove: move unpack 004048b8. `havePrev`: m holds the preceding move.
    public static void UnpackMove(BitStream bs, ref Move m, bool full, bool havePrev)
    {
        bool body = true;
        if (havePrev)
            body = !bs.ReadFlag();
        if (body)
        {
            m.Forward = Store(Div(bs.ReadInt(4), 15.0)); // 80-bit constant 1/15 at 00404bf8
            m.Backward = Store(Div(bs.ReadInt(4), 15.0));
            m.Left = Store(Div(bs.ReadInt(4), 15.0));
            m.Right = Store(Div(bs.ReadInt(4), 15.0));
            m.Jet = bs.ReadFlag();
            m.Jump = bs.ReadFlag();
            m.Crouch = bs.ReadFlag();
        }
        if (!full)
        {
            m.Fire = false;
            m.UseItem = -1;
            m.Pitch = m.Turn = 0;
            return;
        }
        m.Fire = bs.ReadFlag();
        m.UseItem = bs.ReadFlag() ? (int)bs.ReadInt(8) : -1;
        m.Pitch = bs.ReadFlag() ? ClampMoveAngle(bs.ReadF32()) : 0.0f;
        m.Turn = bs.ReadFlag() ? ClampMoveAngle(bs.ReadF32()) : 0.0f;
    }

    // src/game/control_object.cpp unpackMove with a separate previous move (the prev form of the C++).
    public static void UnpackMove(BitStream bs, ref Move m, bool full, in Move prev)
    {
        bool body = !bs.ReadFlag();
        if (body)
        {
            m.Forward = Store(Div(bs.ReadInt(4), 15.0));
            m.Backward = Store(Div(bs.ReadInt(4), 15.0));
            m.Left = Store(Div(bs.ReadInt(4), 15.0));
            m.Right = Store(Div(bs.ReadInt(4), 15.0));
            m.Jet = bs.ReadFlag();
            m.Jump = bs.ReadFlag();
            m.Crouch = bs.ReadFlag();
        }
        else
        {
            m.Forward = prev.Forward;
            m.Backward = prev.Backward;
            m.Left = prev.Left;
            m.Right = prev.Right;
            m.Jet = prev.Jet;
            m.Jump = prev.Jump;
            m.Crouch = prev.Crouch;
        }
        if (!full)
        {
            m.Fire = false;
            m.UseItem = -1;
            m.Pitch = m.Turn = 0;
            return;
        }
        m.Fire = bs.ReadFlag();
        m.UseItem = bs.ReadFlag() ? (int)bs.ReadInt(8) : -1;
        m.Pitch = bs.ReadFlag() ? ClampMoveAngle(bs.ReadF32()) : 0.0f;
        m.Turn = bs.ReadFlag() ? ClampMoveAngle(bs.ReadF32()) : 0.0f;
    }
}

// src/game/control_object.hpp class ObserverCamera: observer camera (class ObserverCamera, ctor 004348a4, vtable
// 00617624): created for every human client at connect, registered in the server manager, in no container.
public sealed class ObserverCamera : GameBase
{
    public const int kFixed = 0, kLook = 1, kFly = 2, kOrbitObject = 3, kOrbitPoint = 4;

    // src/game/control_object.hpp ObserverCamera::Services (CameraServices here): what the camera needs from the game (all optional).
    public sealed class CameraServices
    {
        public Action<string>? Evaluate;           // console line (005f4280)
        public Func<float[], bool>? MissionArea;   // x, y, w, h (006a8478..)
        public Container? Container;               // container 7, orbit collision
    }

    private static readonly ClassContext sNullContext = new();

    public Move LastMove = new();                    // +0x240
    public bool InMissionArea = true;               // +0x260
    public readonly float[] CamRotation = new float[3]; // +0x264
    public readonly float[] Position = new float[3];    // +0x270
    public GameBase? OrbitObject;                   // +0x288
    public GameBase? DamageObject;                  // +0x28c
    public float MinDistance;                       // +0x290
    public float MaxDistance;                       // +0x294
    public float Distance;                          // +0x298
    public int Mode = kFly;                         // +0x29c
    public int ClientId;                            // +0x2a0

    private readonly CameraServices mServices;

    // src/game/control_object.cpp ObserverCamera::ObserverCamera.
    public ObserverCamera(int clientId = 0) : this(sNullContext, clientId, new CameraServices()) { }

    public ObserverCamera(ClassContext ctx, int clientId, CameraServices services)
        : base("ObserverCamera", Kind.Marker, -1, Array.Empty<PersistDef>(), ctx)
    {
        ClientId = clientId;
        mServices = services;
    }

    // src/game/control_object.cpp ObserverCamera::call.
    private void Call(string function, int arg) => mServices.Evaluate?.Invoke(function + "(" + Script.Numbers.FormatInt(arg) + ");");

    // src/game/control_object.cpp ObserverCamera::setTransform.
    private void SetTransform(ReadOnlySpan<float> rot, ReadOnlySpan<float> pos)
    {
        MissionTransform.EulerToMatrix(rot, ref Transform);
        Transform.Flags |= 2;
        for (int i = 0; i < 3; ++i)
            Transform.P[i] = pos[i];
    }

    // src/game/control_object.cpp ObserverCamera::objectCentre: centre of the object's world box (+0x70..+0x84):
    // (max + min) stored, then halved.
    private bool ObjectCentre(GameBase obj, Span<float> output)
    {
        Container.Entry? e = mServices.Container?.Find(obj);
        for (int i = 0; i < 3; ++i)
        {
            if (e != null)
            {
                float sum = Store(Add(e.Box[i + 3], e.Box[i]));
                output[i] = Store(Mul(0.5f, sum));
            }
            else
            {
                output[i] = obj.Transform.P[i]; // no container entry: a degenerate box at the origin of the object
            }
        }
        return e != null;
    }

    // src/game/control_object.cpp ObserverCamera::setFlyMode: Observer::setFlyMode (00434c70).
    public void SetFlyMode(bool fly, bool on, ReadOnlySpan<float> rot, ReadOnlySpan<float> pos)
    {
        DamageObject = null;
        OrbitObject = null;
        for (int i = 0; i < 3; ++i)
        {
            Position[i] = pos[i];
            CamRotation[i] = rot[i];
        }
        SetTransform(CamRotation, Position);
        Mode = !on ? kFixed : fly ? kFly : kLook;
    }

    // src/game/control_object.cpp ObserverCamera::setOrbit: setOrbitObject / setOrbitPoint (00434afc).
    public void SetOrbit(GameBase? obj, ReadOnlySpan<float> point, float cur, float min, float max, ReadOnlySpan<float> rot)
    {
        DamageObject = null;
        OrbitObject = obj;
        if (obj != null)
        {
            ObjectCentre(obj, Position);
            Mode = kOrbitObject;
        }
        else
        {
            Mode = kOrbitPoint;
            for (int i = 0; i < 3; ++i)
                Position[i] = point[i];
        }
        for (int i = 0; i < 3; ++i)
            CamRotation[i] = rot[i];
        MinDistance = min;
        MaxDistance = max;
        Distance = cur;
    }

    // src/game/control_object.cpp ObserverCamera::setDamageObject: setDamageObject (00434978).
    public void SetDamageObject(GameBase? obj) => DamageObject = obj == OrbitObject ? null : obj;

    // src/game/control_object.cpp ObserverCamera::objectDeleted: the delete notification (00434a94).
    public void ObjectDeleted(SimObject? obj)
    {
        if (obj == null)
            return;
        if (obj == DamageObject)
            DamageObject = null;
        if (obj == OrbitObject)
        {
            OrbitObject = null;
            Mode = kOrbitPoint;
            Call("Observer::orbitObjectDeleted", ClientId);
        }
    }

    private static int sFlashDepth;

    // src/game/control_object.cpp ObserverCamera::damageFlash: 00434a24. The original follows the chain without a limit:
    // a camera that is its own damage object overflows its stack. Here such a chain reads 0 (D13).
    public override float DamageFlash()
    {
        if (sFlashDepth >= 4)
            return 0.0f;
        ++sFlashDepth;
        float flash = DamageObject != null ? DamageObject.DamageFlash() : OrbitObject != null ? OrbitObject.DamageFlash() : 0.0f;
        --sFlashDepth;
        return flash;
    }

    // src/game/control_object.cpp ObserverCamera::orbitPlace: 00449604: moves the transform back from its position along
    // -forward (row 1) by dist, or to just in front of what a ray of 2.5 * dist hits first (mask 0x20c00006).
    private void OrbitPlace(ref Transform t, float dist)
    {
        float f0 = t.M[3], f1 = t.M[4], f2 = t.M[5];
        float reach = Store(Mul(-(double)dist, 2.5f));
        var start = new Vec3(t.P[0], t.P[1], t.P[2]);
        var end = new Vec3(Store(Add(t.P[0], Mul(reach, f0))), Store(Add(t.P[1], Mul(reach, f1))), Store(Add(t.P[2], Mul(reach, f2))));
        float back = dist;
        object? hitObject = null;
        Vec3 hit = default, normal = default;
        if (mServices.Container != null && mServices.Container.CastRay(start, end, 0x20c00006u, this, ref hitObject, ref hit, ref normal))
        {
            float d = Store(Add(Add(Mul(f0, normal.X), Mul(f1, normal.Y)), Mul(f2, normal.Z)));
            if (!(d < 0.01))
            {
                float v0 = Store(Sub(t.P[0], hit.X)), v1 = Store(Sub(t.P[1], hit.Y)), v2 = Store(Sub(t.P[2], hit.Z));
                double along = Add(Add(Mul(v0, f0), Mul(v1, f1)), Mul(v2, f2));
                back = Store(Sub(along, Mul(Div(1.0f, d), 0.025)));
                if (back > dist)
                    back = dist;
                if (back < 0.0f)
                    back = 0.0f;
            }
        }
        float[] fwd = { f0, f1, f2 };
        for (int i = 0; i < 3; ++i)
        {
            float off = Store(Mul(back, fwd[i]));
            t.P[i] = Store(Sub(t.P[i], off));
        }
    }

    // src/game/control_object.cpp ObserverCamera::step: one 32 ms step with a move (00435348, full tick). The engine runs
    // with the control word crt_ftol leaves: round toward zero (docs/script.md 4).
    public void Step(in Move mv)
    {
        using var chop = ChopScope();
        // 32 * 0.001 (double) stored to a float under round toward zero: 0x3d03126e, one ulp below the nearest float to
        // 0.032.
        const float dt = 0.031999998f;
        float scale = Store(Mul(32.0, 0.03125f));
        float dist = Distance;
        float[] rot = { CamRotation[0], CamRotation[1], CamRotation[2] };
        float[] pos = { Position[0], Position[1], Position[2] };
        var m = new Transform();
        MissionTransform.EulerToMatrix(CamRotation, ref m);
        int speed = mv.Jet ? 30 : 10;
        float fwd = Store(Mul(Sub(mv.Forward, mv.Backward), speed));
        float side = Store(Mul(Sub(mv.Right, mv.Left), speed));

        if (Mode == kFly)
        {
            for (int i = 0; i < 3; ++i)
            {
                float v = Store(Add(Mul(side, m.M[i]), Mul(fwd, m.M[3 + i])));
                v = Store(Mul(dt, v));
                pos[i] = Store(Add(v, pos[i]));
            }
        }
        else if (Mode == kOrbitObject || Mode == kOrbitPoint)
        {
            dist = Store(Sub(dist, Mul(fwd, dt)));
            if (dist < MinDistance)
                dist = MinDistance;
            else if (dist > MaxDistance)
                dist = MaxDistance;
        }
        if (Mode != kFixed)
        {
            const double twoPi = 6.283185307179586;
            rot[2] = Store(Add(Mul(mv.Turn, scale), rot[2]));
            rot[0] = Store(Add(Mul(mv.Pitch, scale), rot[0]));
            if (rot[2] < 0.0f)
                rot[2] = Store(Add(rot[2], twoPi));
            else if (rot[2] > twoPi)
                rot[2] = Store(Sub(rot[2], twoPi));
            if (rot[0] > 1.3962)
                rot[0] = 1.3962f; // 0x3fb2b6ae
            else if (rot[0] < -1.3962)
                rot[0] = -1.3962f;
        }
        if (Mode == kOrbitObject || Mode == kOrbitPoint)
        {
            if (Mode == kOrbitObject && OrbitObject == null)
                Mode = kOrbitPoint; // not reachable through setOrbit / objectDeleted
            if (Mode == kOrbitObject && Distance < 0.0f)
            {
                // -1: the object's own transform (first person). Other negative values ask the object for a transform at
                // that distance (vtable +0x170); GameBase objects without one answer with their transform.
                Transform = OrbitObject!.Transform;
            }
            else
            {
                if (Mode == kOrbitObject)
                    ObjectCentre(OrbitObject!, Position);
                var t = new Transform();
                MissionTransform.EulerToMatrix(rot, ref t);
                t.Flags |= 2;
                for (int i = 0; i < 3; ++i)
                    t.P[i] = Position[i];
                OrbitPlace(ref t, dist);
                Transform = t;
            }
        }
        else
        {
            SetTransform(rot, pos);
        }
        for (int i = 0; i < 3; ++i)
            CamRotation[i] = rot[i];
        if (Mode != kOrbitObject && Mode != kOrbitPoint)
            for (int i = 0; i < 3; ++i)
                Position[i] = pos[i];
        Distance = dist;
    }

    // src/game/control_object.cpp ObserverCamera::applyMoves: 00435064.
    public override void ApplyMoves(Move[] moves, int count)
    {
        for (int i = 0; i < count; ++i)
        {
            Move mv = moves[i];
            if (mv.Fire && !LastMove.Fire)
                Call("Observer::triggerDown", ClientId);
            else if (!mv.Fire && LastMove.Fire)
                Call("Observer::triggerUp", ClientId);
            if (mv.Jump)
                Call("Observer::jump", ClientId);
            Step(LastMove); // the step runs with the previous move
            LastMove = mv;
        }
        float[] area = { 0, 0, 0, 0 };
        mServices.MissionArea?.Invoke(area);
        bool inside = Position[0] >= area[0] && Add(area[0], area[2]) >= Position[0] && Position[1] >= area[1] &&
                      Add(area[1], area[3]) >= Position[1];
        if (InMissionArea && !inside)
            Call("Observer::leaveMissionArea", Id());
        else if (!InMissionArea && inside)
            Call("Observer::enterMissionArea", Id());
        InMissionArea = inside;
    }

    // src/game/control_object.cpp ObserverCamera::writeControlState: 00434f00.
    public override void WriteControlState(BitStream bs, ControlContext ctx)
    {
        bs.WriteF32s(CamRotation);
        uint m = (uint)Mode;
        uint index = 0x3ff;
        if (m == kOrbitObject)
        {
            int g = ctx.Ghosts != null && OrbitObject != null ? ctx.Ghosts.GhostIndex(OrbitObject) : -1;
            if (g >= 0)
                index = (uint)g;
            if (index == 0x3ff)
                m = kOrbitPoint;
        }
        bs.WriteInt(m, 3);
        if (m < 3)
        {
            bs.WriteF32s(Position);
        }
        else if (m == kOrbitObject)
        {
            bs.WriteInt(index, 10);
            bs.WriteF32(MinDistance);
            bs.WriteF32(MaxDistance);
            bs.WriteF32(Distance);
        }
        else if (m == kOrbitPoint)
        {
            bs.WriteF32s(Position);
            bs.WriteF32(MinDistance);
            bs.WriteF32(MaxDistance);
            bs.WriteF32(Distance);
        }
    }
}
