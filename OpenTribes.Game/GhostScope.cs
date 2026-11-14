// Port of src/game/ghost_scope.hpp and src/game/ghost_scope.cpp: normal-phase ghost scoping and update priority
// (docs/game-stream-ghosts.md 6.1-6.3): object net flags, the scope object's camera query, container traversal marks and
// the GameBase priority with the $GameBase::up* weights.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>The weights of the update priority, bound to the console variables <c>$GameBase::upFov</c> to <c>upInterest</c> (004246f0).</summary>
public sealed class PriorityWeights
{
    /// <summary>The weight of the field of view term.</summary>
    public float Fov { get; set; } = 1.0f;
    /// <summary>The weight of the distance term.</summary>
    public float Distance { get; set; } = 0.4f;
    /// <summary>The weight of the velocity term.</summary>
    public float Velocity { get; set; } = 0.4f;
    /// <summary>The weight of the skip count term.</summary>
    public float Skips { get; set; } = 0.2f;
    /// <summary>The weight of the ownership term.</summary>
    public float Ownership { get; set; } = 0.2f;
    /// <summary>The weight of the interest term.</summary>
    public float Interest { get; set; } = 0.2f;
}

/// <summary>The inputs of the GameBase priority (004488e8) that live on the object.</summary>
public sealed class PriorityInputs
{
    /// <summary>The world box (+0x70 to +0x84).</summary>
    public float[] Box { get; } = new float[6];
    /// <summary>The velocity (+0x194).</summary>
    public float[] Velocity { get; } = new float[3];
    /// <summary>The id of the shooter or thrower (+0x210).</summary>
    public int OwnerId { get; set; }
    /// <summary>The object type bits (+0x48).</summary>
    public uint Type { get; set; }
    /// <summary>True for a LaserProjectile.</summary>
    public bool Laser { get; set; }
}

/// <summary>The scope query and update priority of ghosted objects.</summary>
public static class GhostScope
{
    /// <summary>The net flag of a ghostable object (+0x4c, set by the class constructors; docs/game-stream-ghosts.md 6.1, 1.1).</summary>
    public const uint NetGhostable = 0x100;
    /// <summary>The net flag of an object that is always in scope.</summary>
    public const uint NetScopeAlways = 0x40;
    /// <summary>The net flag of an object ghosted only to client 2049.</summary>
    public const uint NetLocalOnly = 0x80;

    private sealed record ClassFlags(string Cls, uint Flags, uint Tag, bool GameBase);

    // src/game/ghost_scope.cpp kClasses: constructors (docs/game-stream-ghosts.md 6.1).
    private static readonly ClassFlags[] Classes =
    [
        new("SimVolume", 0x140, 0x001, false), new("SimTerrain", 0x140, 0x002, false),
        new("SimPalette", 0x140, 0x003, false), new("SimLight", 0x140, 0x006, false),
        new("Snowfall", 0x140, 0x216, false), new("Sky", 0x140, 0x230, false),
        new("Planet", 0x140, 0x22f, false), new("StarField", 0x140, 0x232, false),
        new("MissionCenterPos", 0x140, 0x2cc, false), new("InteriorShape", 0x140, 0x20a, true),
        new("LaserProjectile", 0x140, 0x25b, true), new("TargetLaser", 0x140, 0x25c, true),
        new("Player", 0x100, 0x218, true), new("StaticShape", 0x100, 0x221, true),
        new("Sensor", 0x100, 0x229, true), new("Turret", 0x100, 0x202, true),
        new("Moveable", 0x100, 0x220, true), new("Item", 0x100, 0x22a, true),
        new("Mine", 0x100, 0x21e, true), new("Vehicle", 0x100, 0x22c, true),
        new("Flier", 0x100, 0x223, true), new("Bullet", 0x100, 0x258, true),
        new("Grenade", 0x100, 0x259, true), new("RocketDumb", 0x100, 0x25a, true),
        new("SeekingMissile", 0x100, 0x25d, true), new("Lightning", 0x100, 0x25e, true),
        new("RepairEffect", 0x100, 0x25f, true), new("Marker", 0x180, 0x21b, true),
        new("Trigger", 0, 0x21f, true), new("ObserverCamera", 0, 0x231, true),
    ];

    // src/game/ghost_scope.cpp classOf.
    private static ClassFlags? ClassOf(SimObject obj)
    {
        foreach (var c in Classes)
            if (GameUtil.EqualsNoCase(c.Cls, obj.ClassName()))
                return c;
        return null;
    }

    private static Container? _container;
    private static readonly PriorityWeights WeightSet = new();

    // src/game/ghost_scope.cpp lenApprox: m_Point3F_lenf (005ed031): squared length stored as f32, then the bit-level
    // square root approximation (bits >> 1) + 0x1fc00000.
    private static float LenApprox(ReadOnlySpan<float> v)
    {
        var sq = Store(Add(Add(Mul(v[0], v[0]), Mul(v[1], v[1])), Mul(v[2], v[2])));
        var bits = BitConverter.SingleToUInt32Bits(sq);
        bits = (bits >> 1) + 0x1fc00000u;
        return BitConverter.UInt32BitsToSingle(bits);
    }

    // src/game/ghost_scope.cpp dot.
    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => Add(Add(Mul(a[0], b[0]), Mul(a[1], b[1])), Mul(a[2], b[2]));

    // src/game/ghost_scope.cpp netObjectPriority: 0050a594.
    private static float NetObjectPriority(SimObject obj, GhostManager gm, uint mask, uint skipCount) => 0.0f;

    // src/game/ghost_scope.cpp gameBaseClassPriority.
    private static float GameBaseClassPriority(SimObject obj, GhostManager gm, uint mask, uint skipCount)
    {
        var input = new PriorityInputs();
        if (_container?.Find(obj) is Container.Entry e)
        {
            for (var i = 0; i < 6; ++i)
                input.Box[i] = e.Box[i];
            input.Type = e.Type;
        }
        input.Laser = GameUtil.EqualsNoCase(obj.ClassName(), "LaserProjectile");
        if (obj is GameBase gb)
            for (var i = 0; i < 3; ++i)
                input.Velocity[i] = gb.Velocity[i]; // +0x194
        var recObj = gm.Camera.CameraObject;
        return GameBasePriority(input, gm.Camera, recObj != null ? recObj.Id() : 0, recObj == obj, mask, skipCount, WeightSet);
    }

    // src/game/ghost_scope.cpp packObserverCamera: GameBase part (4.1) alone: the ObserverCamera layout (00448d80).
    private static uint PackObserverCamera(SimObject obj, GhostContext ctx, BitBuffer bs, uint mask)
    {
        var cam = (ObserverCamera)obj;
        if (bs.WriteFlag((mask & 0x04) != 0))
        {
            bs.WriteInt((uint)cam.Team, 4);
            if (bs.WriteFlag(cam.ControlClient != 0)) // +0x230
                bs.WriteInt((uint)(cam.ControlClient - 0x800), 7);
            if (bs.WriteFlag(cam.OwnerClient != 0)) // +0x238
                bs.WriteInt((uint)(cam.OwnerClient - 0x800), 7);
        }
        return 0;
    }

    /// <summary>Returns the net flags (+0x4c) of an object.</summary>
    /// <param name="obj">The object.</param>
    /// <returns>The flags, or 0 for classes that are not ghosted by scope.</returns>
    public static uint NetFlags(SimObject obj)
    {
        var c = ClassOf(obj);
        return obj.NetFlags(c != null ? c.Flags : 0);
    }

    /// <summary>Returns the shared priority weights.</summary>
    /// <returns>The weights.</returns>
    public static PriorityWeights Weights() => WeightSet;

    /// <summary>Sets the priority weight defaults and binds the six console variables.</summary>
    /// <param name="interp">The script interpreter.</param>
    public static void RegisterPriorityVariables(Interpreter interp)
    {
        PriorityWeights d = new();
        WeightSet.Fov = d.Fov;
        WeightSet.Distance = d.Distance;
        WeightSet.Velocity = d.Velocity;
        WeightSet.Skips = d.Skips;
        WeightSet.Ownership = d.Ownership;
        WeightSet.Interest = d.Interest;
        (string, Func<float>, Action<float>)[] vars =
        [
            ("$GameBase::upFov", () => WeightSet.Fov, v => WeightSet.Fov = v),
            ("$GameBase::upDistance", () => WeightSet.Distance, v => WeightSet.Distance = v),
            ("$GameBase::upVelocity", () => WeightSet.Velocity, v => WeightSet.Velocity = v),
            ("$GameBase::upSkips", () => WeightSet.Skips, v => WeightSet.Skips = v),
            ("$GameBase::upOwnership", () => WeightSet.Ownership, v => WeightSet.Ownership = v),
            ("$GameBase::upInterest", () => WeightSet.Interest, v => WeightSet.Interest = v),
        ];
        foreach ((var name, var get, var set) in vars)
        {
            interp.SetVariable(name, Numbers.FormatFloat(get()));
            interp.BindVariable(name, s => set(Store(Numbers.BorlandAtof(s))));
        }
    }

    /// <summary>Computes the GameBase update priority.</summary>
    /// <param name="obj">The priority inputs of the object.</param>
    /// <param name="rec">The camera record.</param>
    /// <param name="recordObjectId">The id of the camera record object.</param>
    /// <param name="isRecordObject">True when the object is the camera record object.</param>
    /// <param name="mask">The dirty mask.</param>
    /// <param name="skipCount">How many times the object was skipped.</param>
    /// <param name="w">The priority weights.</param>
    /// <returns>The priority.</returns>
    /// <remarks>Follows 004488e8. Every stored intermediate is an f32 store.</remarks>
    public static float GameBasePriority(PriorityInputs obj, CameraRecord rec, int recordObjectId, bool isRecordObject, uint mask,
                                         uint skipCount, PriorityWeights w)
    {
        if (isRecordObject)
            return 10.0f;
        if (obj.Laser && (mask & 0x08000000u) != 0)
            return 9.89999962f;
        Span<float> v = stackalloc float[3];
        for (var i = 0; i < 3; ++i)
        {
            var s = Store(Add(obj.Box[i + 3], obj.Box[i]));
            var c = Store(Mul(0.5f, s));
            v[i] = Store(Sub(c, rec.Pos[i]));
        }
        var d = Store(Sqrt(Dot(v, v)));
        if (d == 0.0f)
            d = 0.001f;
        var r = Store(Div(1.0, d));
        Span<float> u = stackalloc float[3];
        for (var i = 0; i < 3; ++i)
            u[i] = Store(Mul(r, v[i]));
        var dist = d < rec.VisibleDistance ? Store(Sub(1.0, Div(d, rec.VisibleDistance))) : 0.0f;
        var cosine = Store(Dot(u, rec.Dir));
        var inFov = cosine > rec.CosHalfFov;
        var fovTerm = inFov ? 1.0f : 0.0f;
        var vel = 0.0f;
        if (inFov)
        {
            var a = rec.Dir;
            var b = obj.Velocity;
            Span<float> cr =
            [
                Store(Sub(Mul(a[1], b[2]), Mul(a[2], b[1]))), Store(Sub(Mul(a[2], b[0]), Mul(a[0], b[2]))),
                Store(Sub(Mul(a[0], b[1]), Mul(a[1], b[0]))),
            ];
            vel = Store(Div(Mul(LenApprox(cr), rec.Fov), Mul(rec.Fov, rec.VisibleDistance)));
            if (vel > 1.0f)
                vel = 1.0f;
        }
        var interest = 0.0f;
        if ((obj.Type & 0x80) != 0)
        {
            interest = 0.75f;
        }
        else if ((obj.Type & 0x100) != 0)
        {
            interest = 0.3f;
            var away = Store(-Dot(u, obj.Velocity));
            if (away > 0.0f)
                interest = Store(Add(Mul(0.2, away), interest));
        }
        else if ((obj.Type & 0x200000) != 0)
        {
            interest = 0.25f;
        }
        var skips = Store(Mul(skipCount, 0.5f));
        var own = recordObjectId == obj.OwnerId ? 1.0f : 0.0f;
        var sum = Mul(fovTerm, w.Fov);
        sum = Add(sum, Mul(dist, w.Distance));
        sum = Add(sum, Mul(vel, w.Velocity));
        sum = Add(sum, Mul(skips, w.Skips));
        sum = Add(sum, Mul(own, w.Ownership));
        sum = Add(sum, Mul(interest, w.Interest));
        return Store(sum);
    }

    /// <summary>Fills the camera record of an ObserverCamera.</summary>
    /// <param name="position">The camera position (+0x188).</param>
    /// <param name="rotation">The camera rotation matrix. The forward direction is its row 1.</param>
    /// <param name="rec">The record to fill.</param>
    /// <remarks>Follows 004351d8: field of view 45.0 (degrees used as radians), sin and cos of 22.5. The visible distance is left to the caller.</remarks>
    public static void ObserverCameraRecord(ReadOnlySpan<float> position, ReadOnlySpan<float> rotation, CameraRecord rec)
    {
        var t = new Transform();
        MissionTransform.EulerToMatrix(rotation, ref t);
        for (var i = 0; i < 3; ++i)
        {
            rec.Pos[i] = position[i];
            rec.Dir[i] = t.M[3 + i];
        }
        rec.Fov = 45.0f;
        rec.SinHalfFov = Store(Math.Sin(22.5));
        rec.CosHalfFov = Store(Math.Cos(22.5));
    }

    /// <summary>Runs the scope query of the manager's scope object over container 7.</summary>
    /// <param name="gm">The ghost manager.</param>
    /// <param name="container">The container to query.</param>
    /// <param name="cameraOf">Returns the observer camera of a human client (client record +0x9c). It is marked right after a GameBase scope object controlled by that client.</param>
    /// <remarks>Follows vtable +0x6c of the scope object.</remarks>
    public static void ScopeGhosts(GhostManager gm, Container container, Func<int, SimObject?>? cameraOf = null)
    {
        var so = gm.ScopeObject();
        if (so == null)
            return;
        var rec = gm.Camera;
        // markInScope (005532a8): net flag 0x80 objects only for connection 2049.
        void Mark(SimObject obj)
        {
            var f = NetFlags(obj);
            if ((f & NetLocalOnly) != 0 && gm.ClientId() != 2049)
                return;
            gm.MarkInScope(obj, (f & NetScopeAlways) != 0);
        }
        var vis = 1500.0f; // no container (004351d8, 004498ec)
        float inContainer = 0;
        if (container.VisibleDistance(so, ref inContainer))
            vis = inContainer;
        if (GameUtil.EqualsNoCase(so.ClassName(), "ObserverCamera"))
        {
            var cam = (ObserverCamera)so;
            ObserverCameraRecord(cam.Position, cam.CamRotation, rec);
            if (cam.Mode is ObserverCamera.OrbitObjectMode or ObserverCamera.OrbitPoint)
                for (var i = 0; i < 3; ++i)
                {
                    // Orbiting: the transform (+0x188, row 1) differs from the orbit centre.
                    rec.Pos[i] = cam.Transform.P[i];
                    rec.Dir[i] = cam.Transform.M[3 + i];
                }
            rec.CameraObject = so;
            rec.VisibleDistance = vis;
            Mark(so);
            if (cam.OrbitObject != null) // +0x288
                Mark(cam.OrbitObject);
        }
        else if (so is GameBase gb)
        {
            // 004498ec: translation and row 1 of the eye transform (vtable +0x110), half of fov +0x22c.
            rec.CameraObject = so;
            var eye = gb.EyeTransform();
            for (var i = 0; i < 3; ++i)
            {
                rec.Pos[i] = eye.P[i];
                rec.Dir[i] = eye.M[3 + i];
            }
            var half = Store(Mul(gb.Fov, 0.5f));
            rec.Fov = half;
            rec.SinHalfFov = Store(Math.Sin(half));
            rec.CosHalfFov = Store(Math.Cos(half));
            rec.VisibleDistance = vis;
            Mark(so);
            // The observer camera (client record +0x9c) of the human client controlling the object (+0x230).
            if (cameraOf != null && gb.ControlClient != 0 && cameraOf(gb.ControlClient) is SimObject camera)
                Mark(camera);
        }
        else
        {
            return;
        }
        var q = new Container.ScopeQuery
        {
            Point = new Vec3(rec.Pos[0], rec.Pos[1], rec.Pos[2]),
            VisibleDistance = rec.VisibleDistance,
            Markable = o =>
            {
                var f = NetFlags((SimObject)o!);
                return (f & NetGhostable) != 0 && (f & NetScopeAlways) == 0; // rule M
            },
            Mark = o => Mark((SimObject)o!),
        };
        container.Scope(q);
    }

    /// <summary>Sets the container the priority functions query.</summary>
    /// <param name="container">The container.</param>
    public static void SetPriorityContainer(Container? container) => _container = container;

    /// <summary>Dispatches the priority for <see cref="GhostManager.DefaultPriority"/>.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="gm">The ghost manager.</param>
    /// <param name="mask">The dirty mask.</param>
    /// <param name="skipCount">How many times the object was skipped.</param>
    /// <returns>The GameBase priority (004488e8) for GameBase classes, 0.0 for other NetObjects (0050a594).</returns>
    public static float GhostPriority(SimObject obj, GhostManager gm, uint mask, uint skipCount)
    {
        var c = ClassOf(obj);
        if (c != null && c.GameBase)
            return GameBaseClassPriority(obj, gm, mask, skipCount);
        return NetObjectPriority(obj, gm, mask, skipCount);
    }

    /// <summary>Fills the priority of every ghost class and the ObserverCamera GameBase-only packer.</summary>
    /// <remarks>GameBase classes are registered by tag when no packer exists yet (docs/game-stream-ghosts.md 4.1).</remarks>
    public static void RegisterGhostPriorities()
    {
        foreach (var c in Classes)
        {
            GhostClass.PriorityFn prio = c.GameBase ? GameBaseClassPriority : NetObjectPriority;
            if (GhostManager.FindGhostClass(c.Cls) is GhostClass g)
            {
                g.Priority ??= prio;
            }
            else if (c.GameBase)
            {
                var camera = GameUtil.EqualsNoCase(c.Cls, "ObserverCamera");
                GhostManager.RegisterGhostClass(c.Cls, new GhostClass { Tag = c.Tag, Pack = camera ? PackObserverCamera : null, Priority = prio });
            }
        }
    }
}
