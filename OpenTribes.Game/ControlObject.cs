// Port of src/game/control_object.hpp and src/game/control_object.cpp: moves and control objects (docs/game-stream.md
// 6.1, 6.5; docs/player-control.md): the move record, its wire form, and the ObserverCamera every human client owns.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>A move record: the control input of one 32 ms tick (32 bytes in the original, docs/game-stream.md 6.1).</summary>
public struct Move
{
    /// <summary>The forward input, 0 to 1 in 1/15 steps (+0x00).</summary>
    public float Forward;
    /// <summary>The backward input (+0x04).</summary>
    public float Backward;
    /// <summary>The left input (+0x08).</summary>
    public float Left;
    /// <summary>The right input (+0x0c).</summary>
    public float Right;
    /// <summary>True while the jet key is down (+0x10).</summary>
    public bool Jet;
    /// <summary>True while the crouch key is down (+0x11).</summary>
    public bool Crouch;
    /// <summary>True while the jump key is down (+0x12).</summary>
    public bool Jump;
    /// <summary>True while the fire key is down (+0x13).</summary>
    public bool Fire;
    /// <summary>The item to use, or -1 (+0x14).</summary>
    public int UseItem;
    /// <summary>The yaw delta (+0x18).</summary>
    public float Turn;
    /// <summary>The pitch delta (+0x1c).</summary>
    public float Pitch;

    /// <summary>Creates an empty move: no input, no item.</summary>
    public Move()
    {
        Forward = Backward = Left = Right = 0;
        Jet = Crouch = Jump = Fire = false;
        UseItem = -1;
        Turn = Pitch = 0;
    }
}

/// <summary>What a control state writer may ask about the receiving connection.</summary>
public sealed class ControlContext
{
    /// <summary>The ghost manager of the receiving connection, or null.</summary>
    public GhostManager? Ghosts;
}

/// <summary>BitStream extensions for the raw float writes of the game files (<c>bs.writeBytes(&amp;f, 4)</c>).</summary>
public static class BitStreamF
{
    /// <summary>Writes a float as its raw 32 bits.</summary>
    /// <param name="bs">The stream.</param>
    /// <param name="f">The value.</param>
    public static void WriteF32(this BitStream bs, float f)
    {
        Span<byte> b = stackalloc byte[4];
        BitConverter.TryWriteBytes(b, f);
        bs.WriteBytes(b);
    }

    /// <summary>Writes floats as their raw 32 bits.</summary>
    /// <param name="bs">The stream.</param>
    /// <param name="v">The values.</param>
    public static void WriteF32s(this BitStream bs, ReadOnlySpan<float> v)
    {
        foreach (var f in v)
            bs.WriteF32(f);
    }

    /// <summary>Reads a float from its raw 32 bits.</summary>
    /// <param name="bs">The stream.</param>
    /// <returns>The value.</returns>
    public static float ReadF32(this BitStream bs)
    {
        Span<byte> b = stackalloc byte[4];
        b.Clear();
        bs.ReadBytes(b);
        return BitConverter.ToSingle(b);
    }
}

/// <summary>The move wire form: clamp, pack and unpack.</summary>
public static class Moves
{
    /// <summary>The largest turn or pitch delta of one move (float at 0060f084).</summary>
    public const float kMoveMaxTurn = 1.0f;

    /// <summary>Clamps a move angle.</summary>
    /// <param name="v">The angle delta.</param>
    /// <returns>The clamped value. NaN becomes 0.</returns>
    /// <remarks>Follows 00404860.</remarks>
    public static float ClampMoveAngle(float v)
    {
        var bits = BitConverter.SingleToUInt32Bits(v);
        if (((bits >> 23) & 0xff) == 0xff)
            return 0; // NaN and infinity
        if (!(-kMoveMaxTurn < v))
            return -kMoveMaxTurn;
        return kMoveMaxTurn <= v ? kMoveMaxTurn : v;
    }

    /// <summary>Writes a move to a stream.</summary>
    /// <param name="bs">The stream.</param>
    /// <param name="m">The move.</param>
    /// <param name="full">True for the PlayerPSC form with fire, useItem, pitch and turn.</param>
    /// <param name="prev">The preceding move, which enables the same-as-previous bit, or null.</param>
    /// <remarks>Follows the move pack (004046a0).</remarks>
    public static void PackMove(BitStream bs, in Move m, bool full, Move? prev)
    {
        var body = true;
        if (prev is Move p)
        {
            var same = m.Forward == p.Forward && m.Backward == p.Backward && m.Left == p.Left && m.Right == p.Right &&
                        m.Jet == p.Jet && m.Jump == p.Jump && m.Crouch == p.Crouch;
            body = !bs.WriteFlag(same);
        }
        if (body)
        {
            foreach (var f in new[] { m.Forward, m.Backward, m.Left, m.Right })
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

    /// <summary>Reads a move from a stream.</summary>
    /// <param name="bs">The stream.</param>
    /// <param name="m">The move to fill. It holds the preceding move when <paramref name="havePrev"/> is true.</param>
    /// <param name="full">True for the PlayerPSC form.</param>
    /// <param name="havePrev">True when <paramref name="m"/> holds the preceding move.</param>
    /// <remarks>Follows the move unpack (004048b8).</remarks>
    public static void UnpackMove(BitStream bs, ref Move m, bool full, bool havePrev)
    {
        var body = true;
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

    /// <summary>Reads a move from a stream with a separate previous move.</summary>
    /// <param name="bs">The stream.</param>
    /// <param name="m">The move to fill.</param>
    /// <param name="full">True for the PlayerPSC form.</param>
    /// <param name="prev">The preceding move.</param>
    public static void UnpackMove(BitStream bs, ref Move m, bool full, in Move prev)
    {
        var body = !bs.ReadFlag();
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

/// <summary>The observer camera every human client owns. It is created at connect, registered in the server manager and in no container.</summary>
/// <remarks>Port of class ObserverCamera in src/game/control_object.hpp (constructor 004348a4, vtable 00617624).</remarks>
public sealed class ObserverCamera : GameBase
{
    /// <summary>The camera mode values: fixed, look, fly, orbit object and orbit point.</summary>
    public const int kFixed = 0, kLook = 1, kFly = 2, kOrbitObject = 3, kOrbitPoint = 4;

    /// <summary>What the camera needs from the game. Every member is optional.</summary>
    public sealed class CameraServices
    {
        /// <summary>Evaluates a console line (005f4280).</summary>
        public Action<string>? Evaluate;
        /// <summary>Reads the mission area as x, y, w, h (006a8478).</summary>
        public Func<float[], bool>? MissionArea;
        /// <summary>Container 7, used for the orbit collision.</summary>
        public Container? Container;
    }

    private static readonly ClassContext sNullContext = new();

    /// <summary>The last move (+0x240).</summary>
    public Move LastMove = new();
    /// <summary>True while the camera is inside the mission area (+0x260).</summary>
    public bool InMissionArea = true;
    /// <summary>The camera rotation (+0x264).</summary>
    public readonly float[] CamRotation = new float[3];
    /// <summary>The camera position (+0x270).</summary>
    public readonly float[] Position = new float[3];
    /// <summary>The object the camera orbits (+0x288).</summary>
    public GameBase? OrbitObject;
    /// <summary>The object whose damage the camera shows (+0x28c).</summary>
    public GameBase? DamageObject;
    /// <summary>The minimum orbit distance (+0x290).</summary>
    public float MinDistance;
    /// <summary>The maximum orbit distance (+0x294).</summary>
    public float MaxDistance;
    /// <summary>The current orbit distance (+0x298).</summary>
    public float Distance;
    /// <summary>The camera mode (+0x29c).</summary>
    public int Mode = kFly;
    /// <summary>The id of the owning client (+0x2a0).</summary>
    public int ClientId;

    private readonly CameraServices mServices;

    /// <summary>Creates a camera with no game services, for tests.</summary>
    /// <param name="clientId">The id of the owning client.</param>
    public ObserverCamera(int clientId = 0) : this(sNullContext, clientId, new CameraServices()) { }

    /// <summary>Creates a camera.</summary>
    /// <param name="ctx">The class context.</param>
    /// <param name="clientId">The id of the owning client.</param>
    /// <param name="services">What the camera needs from the game.</param>
    public ObserverCamera(ClassContext ctx, int clientId, CameraServices services)
        : base("ObserverCamera", Kind.Marker, -1, Array.Empty<PersistDef>(), ctx)
    {
        ClientId = clientId;
        mServices = services;
    }

    // src/game/control_object.cpp ObserverCamera::call.
    private void Call(string function, int arg) => mServices.Evaluate?.Invoke(function + "(" + Numbers.FormatInt(arg) + ");");

    // src/game/control_object.cpp ObserverCamera::setTransform.
    private void SetTransform(ReadOnlySpan<float> rot, ReadOnlySpan<float> pos)
    {
        MissionTransform.EulerToMatrix(rot, ref Transform);
        Transform.Flags |= 2;
        for (var i = 0; i < 3; ++i)
            Transform.P[i] = pos[i];
    }

    // src/game/control_object.cpp ObserverCamera::objectCentre: centre of the object's world box (+0x70..+0x84):
    // (max + min) stored, then halved.
    private bool ObjectCentre(GameBase obj, Span<float> output)
    {
        Container.Entry? e = mServices.Container?.Find(obj);
        for (var i = 0; i < 3; ++i)
        {
            if (e != null)
            {
                var sum = Store(Add(e.Box[i + 3], e.Box[i]));
                output[i] = Store(Mul(0.5f, sum));
            }
            else
            {
                output[i] = obj.Transform.P[i]; // no container entry: a degenerate box at the origin of the object
            }
        }
        return e != null;
    }

    /// <summary>Switches fly mode on or off.</summary>
    /// <param name="fly">True for fly mode, false for look mode.</param>
    /// <param name="on">True to turn the mode on.</param>
    /// <param name="rot">The rotation to set.</param>
    /// <param name="pos">The position to set.</param>
    /// <remarks>Follows Observer::setFlyMode (00434c70).</remarks>
    public void SetFlyMode(bool fly, bool on, ReadOnlySpan<float> rot, ReadOnlySpan<float> pos)
    {
        DamageObject = null;
        OrbitObject = null;
        for (var i = 0; i < 3; ++i)
        {
            Position[i] = pos[i];
            CamRotation[i] = rot[i];
        }
        SetTransform(CamRotation, Position);
        Mode = !on ? kFixed : fly ? kFly : kLook;
    }

    /// <summary>Starts orbiting an object or a point.</summary>
    /// <param name="obj">The object to orbit, or null to orbit the point.</param>
    /// <param name="point">The point to orbit.</param>
    /// <param name="cur">The current distance.</param>
    /// <param name="min">The minimum distance.</param>
    /// <param name="max">The maximum distance.</param>
    /// <param name="rot">The rotation to set.</param>
    /// <remarks>Follows setOrbitObject and setOrbitPoint (00434afc).</remarks>
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
            for (var i = 0; i < 3; ++i)
                Position[i] = point[i];
        }
        for (var i = 0; i < 3; ++i)
            CamRotation[i] = rot[i];
        MinDistance = min;
        MaxDistance = max;
        Distance = cur;
    }

    /// <summary>Sets the object whose damage the camera shows. The orbit object is ignored.</summary>
    /// <param name="obj">The object, or null.</param>
    /// <remarks>Follows setDamageObject (00434978).</remarks>
    public void SetDamageObject(GameBase? obj) => DamageObject = obj == OrbitObject ? null : obj;

    /// <summary>Drops references to an object that was deleted.</summary>
    /// <param name="obj">The deleted object.</param>
    /// <remarks>Follows the delete notification (00434a94).</remarks>
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

    /// <inheritdoc/>
    /// <remarks>Follows 00434a24. The original follows the chain of damage objects without a limit, so a camera that is its own damage object overflows its stack. Here such a chain reads 0 (D13).</remarks>
    public override float DamageFlash()
    {
        if (sFlashDepth >= 4)
            return 0.0f;
        ++sFlashDepth;
        var flash = DamageObject != null ? DamageObject.DamageFlash() : OrbitObject != null ? OrbitObject.DamageFlash() : 0.0f;
        --sFlashDepth;
        return flash;
    }

    // src/game/control_object.cpp ObserverCamera::orbitPlace: 00449604 (GameBase_placeBehind, also used by Player_getCameraTransform
    // 00412324): moves the transform back from its position along -forward (row 1) by dist, or to just in front of what a ray of
    // 2.5 * dist hits first (mask 0x20c00006).
    private void OrbitPlace(ref Transform t, float dist)
    {
        var p = new float[] { t.P[0], t.P[1], t.P[2] };
        PlaceBehind(mServices.Container, this, p, t.M[3], t.M[4], t.M[5], dist);
        for (var i = 0; i < 3; ++i)
            t.P[i] = p[i];
    }

    /// <summary>Moves a camera position back along the view, stopping at geometry.</summary>
    /// <param name="container">The container to cast the ray in.</param>
    /// <param name="exclude">The object the ray ignores (the object at +0x3c of the caller).</param>
    /// <param name="p">The position, moved in place.</param>
    /// <param name="f0">The x of the unit forward vector of the transform.</param>
    /// <param name="f1">The y of the forward vector.</param>
    /// <param name="f2">The z of the forward vector.</param>
    /// <param name="dist">The distance to move back.</param>
    /// <remarks>Follows GameBase_placeBehind (00449604). The ray goes from p to <c>p - 2.5 * dist * f</c>, all products stored as float. A hit whose <c>normal . f</c> is 0.01 or more puts the camera <c>(p - hit) . f - 0.025 / (normal . f)</c> back, clamped to [0, dist]. A smaller dot product keeps the full distance.</remarks>
    public static void PlaceBehind(Container? container, object? exclude, float[] p, float f0, float f1, float f2, float dist)
    {
        var reach = Store(Mul(-(double)dist, 2.5f));
        var start = new Vec3(p[0], p[1], p[2]);
        var end = new Vec3(Store(Add(p[0], Mul(reach, f0))), Store(Add(p[1], Mul(reach, f1))), Store(Add(p[2], Mul(reach, f2))));
        var back = dist;
        object? hitObject = null;
        Vec3 hit = default, normal = default;
        if (container != null && container.CastRay(start, end, 0x20c00006u, exclude, ref hitObject, ref hit, ref normal))
        {
            var d = Store(Add(Add(Mul(f0, normal.X), Mul(f1, normal.Y)), Mul(f2, normal.Z)));
            if (!(d < 0.01))
            {
                float v0 = Store(Sub(p[0], hit.X)), v1 = Store(Sub(p[1], hit.Y)), v2 = Store(Sub(p[2], hit.Z));
                var along = Add(Add(Mul(v0, f0), Mul(v1, f1)), Mul(v2, f2));
                back = Store(Sub(along, Mul(Div(1.0f, d), 0.025)));
                if (back > dist)
                    back = dist;
                if (back < 0.0f)
                    back = 0.0f;
            }
        }
        float[] fwd = [f0, f1, f2];
        for (var i = 0; i < 3; ++i)
        {
            var off = Store(Mul(back, fwd[i]));
            p[i] = Store(Sub(p[i], off));
        }
    }

    /// <summary>Runs one 32 ms step with a move.</summary>
    /// <param name="mv">The move.</param>
    /// <remarks>Follows the full tick (00435348). The engine runs with the control word that crt_ftol leaves, round toward zero (docs/script.md 4).</remarks>
    public void Step(in Move mv)
    {
        using var chop = ChopScope();
        // 32 * 0.001 (double) stored to a float under round toward zero: 0x3d03126e, one ulp below the nearest float to
        // 0.032.
        const float dt = 0.031999998f;
        var scale = Store(Mul(32.0, 0.03125f));
        var dist = Distance;
        float[] rot = [CamRotation[0], CamRotation[1], CamRotation[2]];
        float[] pos = [Position[0], Position[1], Position[2]];
        var m = new Transform();
        MissionTransform.EulerToMatrix(CamRotation, ref m);
        var speed = mv.Jet ? 30 : 10;
        var fwd = Store(Mul(Sub(mv.Forward, mv.Backward), speed));
        var side = Store(Mul(Sub(mv.Right, mv.Left), speed));

        if (Mode == kFly)
        {
            for (var i = 0; i < 3; ++i)
            {
                var v = Store(Add(Mul(side, m.M[i]), Mul(fwd, m.M[3 + i])));
                v = Store(Mul(dt, v));
                pos[i] = Store(Add(v, pos[i]));
            }
        }
        else if (Mode is kOrbitObject or kOrbitPoint)
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
        if (Mode is kOrbitObject or kOrbitPoint)
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
                for (var i = 0; i < 3; ++i)
                    t.P[i] = Position[i];
                OrbitPlace(ref t, dist);
                Transform = t;
            }
        }
        else
        {
            SetTransform(rot, pos);
        }
        for (var i = 0; i < 3; ++i)
            CamRotation[i] = rot[i];
        if (Mode is not kOrbitObject and not kOrbitPoint)
            for (var i = 0; i < 3; ++i)
                Position[i] = pos[i];
        Distance = dist;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 00435064.</remarks>
    public override void ApplyMoves(Move[] moves, int count)
    {
        for (var i = 0; i < count; ++i)
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
        float[] area = [0, 0, 0, 0];
        mServices.MissionArea?.Invoke(area);
        var inside = Position[0] >= area[0] && Add(area[0], area[2]) >= Position[0] && Position[1] >= area[1] &&
                      Add(area[1], area[3]) >= Position[1];
        if (InMissionArea && !inside)
            Call("Observer::leaveMissionArea", Id());
        else if (!InMissionArea && inside)
            Call("Observer::enterMissionArea", Id());
        InMissionArea = inside;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 00434f00.</remarks>
    public override void WriteControlState(BitStream bs, ControlContext ctx)
    {
        bs.WriteF32s(CamRotation);
        var m = (uint)Mode;
        uint index = 0x3ff;
        if (m == kOrbitObject)
        {
            var g = ctx.Ghosts != null && OrbitObject != null ? ctx.Ghosts.GhostIndex(OrbitObject) : -1;
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
