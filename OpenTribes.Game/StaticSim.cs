// Port of src/game/static_sim.cpp: engine behavior of the non-player mission objects (docs/static-sim.md): the
// StaticBase tick, collision callback records, energy, repair, fade, the delayed delete, Moveable binding and stepping,
// SimPath. The Chop class of the file is Fpu.ChopScope; every float(...) inside it is Fpu.Store.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>A SimPath: a group of waypoint markers, with the path id given by the registration order (SimPathManager, object 42).</summary>
/// <remarks>Port of class SimPathObject in src/game/mission_objects.hpp (docs/static-sim.md 2.2). OnAdd registers the children and then records one point per child that has a transform.</remarks>
public sealed class SimPathObject : SimGroup
{
    /// <summary>One point of the path.</summary>
    public sealed class Point
    {
        /// <summary>The point position.</summary>
        public readonly float[] Pos = new float[3];
        /// <summary>The point rotation as Euler angles.</summary>
        public readonly float[] Rot = new float[3];
    }

    private readonly PersistFields mFields = new(new PersistDef[] { new("isLooping", 4, 1), new("isCompressed", 4, 1) });
    private readonly List<Point> mPoints = [];

    /// <inheritdoc/>
    public override string ClassName() => "SimPath";
    /// <summary>Tells whether the path loops (<c>isLooping</c>).</summary>
    /// <returns>True for a looping path.</returns>
    public bool Looping() => mFields.Boolean("isLooping", false);
    /// <summary>Returns the points of the path.</summary>
    /// <returns>The points in order.</returns>
    public IReadOnlyList<Point> Points() => mPoints;

    /// <inheritdoc/>
    public override bool SetPersistField(string field, IReadOnlyList<string> values) => mFields.Set(field, values);

    /// <inheritdoc/>
    public override IReadOnlyList<string>? PersistValue(string field) => mFields.Get(field);

    /// <inheritdoc/>
    /// <remarks>Follows 0x0052beec: the children register first (SimSet::onAdd), then one record per child that answers query 0x401 (a transform): translation and the Euler angles of the matrix.</remarks>
    public override bool OnAdd()
    {
        if (!base.OnAdd())
            return false;
        mPoints.Clear();
        if (mFields.Boolean("isCompressed", false))
            return true; // the stored list would be used; none exists in a mission
        foreach (SimObject m in Members())
        {
            if (m is not GameBase gb)
                continue;
            var p = new Point();
            for (var i = 0; i < 3; ++i)
                p.Pos[i] = gb.Transform.P[i];
            MissionTransform.MatrixToEuler(gb.Transform, p.Rot);
            mPoints.Add(p);
        }
        return true;
    }

    /// <summary>Reads a point of the path with the deltas to the next point.</summary>
    /// <param name="i">The point index. A looping path takes it modulo the count.</param>
    /// <param name="rec">Receives point <paramref name="i"/>.</param>
    /// <param name="dpos">Receives the position delta to the next point, the last one to point 0.</param>
    /// <param name="drot">Receives the rotation delta to the next point, the last one to point 0.</param>
    /// <returns>The number of points, 0 when empty.</returns>
    /// <remarks>Follows the SimPathManager getPoint (0x0052b2d4).</remarks>
    public int GetPoint(int i, out Point rec, Span<float> dpos, Span<float> drot)
    {
        rec = new Point();
        var n = mPoints.Count;
        if (n == 0)
            return 0;
        if (Looping())
            i %= n; // C remainder: a negative index stays negative
        if (i < 0 || i >= n)
            return n;
        using var chop = ChopScope();
        rec = mPoints[i];
        Point next = mPoints[i + 1 < n ? i + 1 : 0];
        for (var k = 0; k < 3; ++k)
        {
            dpos[k] = Store(Sub(next.Pos[k], rec.Pos[k]));
            drot[k] = Store(Sub(next.Rot[k], rec.Rot[k]));
        }
        return n;
    }
}

public partial class GameBase
{
    // src/game/static_sim.cpp len3: len(a) = sqrt((x*x + y*y) + z*z), unrounded (0x005ed014).
    internal static double Len3(ReadOnlySpan<float> v) => Sqrt(Add(Add(Mul(v[0], v[0]), Mul(v[1], v[1])), Mul(v[2], v[2])));

    /// <inheritdoc/>
    public override void ServerTick(uint tickMs)
    {
        switch (mShapeClass)
        {
            case ShapeKind.Moveable:
                MoveableTick();
                break;
            case ShapeKind.Item:
            case ShapeKind.Mine:
                ItemTick();
                break;
            case ShapeKind.Turret:
                TurretTickMs = tickMs;
                TurretTick();
                break;
            default:
                StaticTick(0.03f);
                break;
        }
    }

    /// <summary>Runs one tick of a StaticBase object: collision records, threads, recharge, repair and damage state.</summary>
    /// <param name="dt">The step length in seconds.</param>
    /// <remarks>Follows StaticBase::serverTick (0x0044fd78). Every step takes dt 0.03 (0x3cf5c28f) although a tick is 0.032 s.</remarks>
    public void StaticTick(float dt)
    {
        if (!IsStaticBase() || !ShapeBound)
            return;
        using var chop = ChopScope();
        ExpireCollisions();
        // 0x0044c710: threads in slot order. A non-cyclic sequence that sits at its end while not flagged ends first (the
        // script is told on the server), then every thread advances.
        for (var slot = 0; slot < 3; ++slot)
        {
            Thread t = Threads[slot];
            if (!t.Created)
                continue;
            ShapeSequence? seq = Anim != null && t.Sequence >= 0 && t.Sequence < Anim.Sequences.Count ? Anim.Sequences[t.Sequence] : null;
            if (seq != null && !seq.Cyclic && (t.Flags & kThreadAtEnd) == 0)
            {
                var done = (t.Flags & kThreadForward) != 0 ? t.Position >= 1.0f : t.Position <= 0.0f;
                if (done)
                {
                    t.Flags |= kThreadAtEnd;
                    ThreadChanged(slot);
                    mCtx.Game?.ScriptCallback(this, "onEndSequence", new[] { Numbers.FormatInt(slot) });
                }
            }
            if (seq != null && t.Speed != 0 && seq.Duration > 0)
            {
                var step = Store(Div(dt, seq.Duration));
                var delta = Store(Mul(t.Speed, step));
                t.Position = Store(Add(delta, t.Position));
                if (!seq.Cyclic)
                {
                    if (t.Position > 1.0f)
                        t.Position = 1.0f;
                    else if (t.Position < 0.0f)
                        t.Position = 0.0f;
                }
                else
                {
                    while (t.Position >= 1.0f)
                        t.Position = SubF(t.Position, 1.0f);
                    while (t.Position < 0.0f)
                        t.Position = AddF(t.Position, 1.0f);
                }
            }
        }
        RechargeStep(dt);
        RepairStep(dt);
        UpdateDamageState();
    }

    // ---- collision callback records (docs/static-sim.md 1.8, docs/collision.md E1.4) ----

    /// <summary>Drops the collision records once every expiry has passed.</summary>
    /// <remarks>Follows 0x00448248.</remarks>
    public void ExpireCollisions()
    {
        if (CollisionRecords.Count == 0 || Manager() == null)
            return;
        if (Manager()!.Time() > CollisionLatest)
            CollisionRecords.Clear();
    }

    /// <summary>Records a collision with another object.</summary>
    /// <param name="other">The other object.</param>
    /// <param name="services">The game services, or null.</param>
    /// <remarks>Follows vtable +0x140 (0x00448840). The record of <paramref name="other"/> is found from index 0. A record that expired earlier in the list is taken over with expiry 0, else one is appended.</remarks>
    public void OnCollision(GameBase other, GameServices? services = null)
    {
        GameServices? game = services ?? mCtx.Game;
        if (game == null)
            return;
        var now = Manager() != null ? Manager()!.Time() : 0.0f;
        var rec = -1;
        for (var i = 0; i < CollisionRecords.Count; ++i)
        {
            CollisionRecord r = CollisionRecords[i];
            if (r.Other == other.Id())
            {
                rec = i;
                break;
            }
            if (r.Expiry < now)
            {
                CollisionRecords[i] = new CollisionRecord { Other = other.Id(), Expiry = 0 };
                rec = i;
                break;
            }
        }
        if (rec < 0)
        {
            CollisionRecords.Add(new CollisionRecord { Other = other.Id(), Expiry = 0 });
            rec = CollisionRecords.Count - 1;
        }
        CollisionRecord c = CollisionRecords[rec];
        if (now <= c.Expiry)
            return;
        c.Expiry = Store(Add(now, 1.0f));
        CollisionRecords[rec] = c;
        if (c.Expiry > CollisionLatest)
            CollisionLatest = c.Expiry;
        game.ScriptCallback(this, "onCollision", new[] { Numbers.FormatInt(other.Id()) });
    }

    /// <summary>Notifies the object of a collision. Server only.</summary>
    /// <param name="other">The other object.</param>
    /// <remarks>Follows vtable +0xe8. The accepted types are the per-class masks of docs/collision.md E1.4. The type bits come from the container classes.</remarks>
    public void OnCollisionNotify(GameBase other)
    {
        var type = other.ShapeClass() switch
        {
            ShapeKind.StaticShape or ShapeKind.Sensor or ShapeKind.Turret => ObjectType.kStaticType,
            ShapeKind.Moveable => ObjectType.kMoveableType,
            ShapeKind.Item => ObjectType.kItemType,
            ShapeKind.Mine => ObjectType.kMineType,
            ShapeKind.Vehicle => ObjectType.kVehicleType,
            _ => GameUtil.EqualsNoCase(other.ClassName(), "Player") ? ObjectType.kPlayerType : 0u,
        };
        uint mask;
        switch (mShapeClass)
        {
            case ShapeKind.StaticShape:
            case ShapeKind.Sensor:
            case ShapeKind.Turret:
            case ShapeKind.Moveable:
                mask = 0x24200080; // Player, Item, Mine, Vehicle
                break;
            case ShapeKind.Item:
                if (ItemThrower == other)
                    return;
                mask = 0x24800080; // Player, StaticBase, Mine, Vehicle
                break;
            case ShapeKind.Mine:
                if (DamageState == kDestroyed || ItemThrower == other)
                    return;
                mask = 0x24800080;
                break;
            default:
                return;
        }
        if ((type & mask) != 0)
            OnCollision(other);
    }

    // ---- energy, repair, shield, fade ----

    /// <summary>Sets the energy, clamped to [0, maxEnergy].</summary>
    /// <param name="v">The new energy.</param>
    /// <remarks>Follows 0x0044bd78.</remarks>
    public void SetEnergy(float v)
    {
        Energy = v;
        if (Energy > MaxEnergy)
            Energy = MaxEnergy;
        else if (Energy < 0)
            Energy = 0;
    }

    /// <summary>Recharges the energy for one step.</summary>
    /// <param name="dt">The step length in seconds.</param>
    /// <remarks>Follows 0x0044b5b0.</remarks>
    public void RechargeStep(float dt)
    {
        using var chop = ChopScope();
        Energy = Store(Add(Mul(RechargeRate, dt), Energy));
        if (Energy > MaxEnergy)
            Energy = MaxEnergy;
        else if (Energy < 0)
            Energy = 0;
    }

    /// <summary>Repairs damage. Only an object with damage takes a positive amount.</summary>
    /// <param name="amount">The repair amount.</param>
    /// <remarks>Follows 0x0044bc40.</remarks>
    public void RepairDamage(float amount)
    {
        if (Damage != 0 && amount > 0)
            RepairPending = Store(Add(RepairPending, amount));
    }

    /// <summary>Applies the pending repair at repairRate per second.</summary>
    /// <param name="dt">The step length in seconds.</param>
    /// <remarks>Follows 0x0044b610.</remarks>
    public void RepairStep(float dt)
    {
        using var chop = ChopScope();
        if (RepairPending != 0)
        {
            var step = Store(Mul(RepairRate, dt));
            if (RepairPending >= step)
            {
                Damage = Store(Sub(Damage, step));
                RepairPending = Store(Sub(RepairPending, step));
            }
            else
            {
                Damage = Store(Sub(Damage, RepairPending));
                RepairPending = 0;
            }
            if (Damage < 0)
                Damage = 0;
            if (RepairPending > Damage)
                RepairPending = Damage;
            else if (RepairPending < 0)
                RepairPending = 0;
            SetMaskBits(0x100);
        }
        if (Damage > 0 && AutoRepairRate > 0)
        {
            // The 0.032 is an f64 constant (0x0044b744).
            Damage = Store(Sub(Damage, Mul(AutoRepairRate, 0.032)));
            if (Damage < 0)
                Damage = 0;
            SetMaskBits(0x100);
        }
    }

    /// <summary>Activates the shield effect.</summary>
    /// <param name="vec">The hit vector. The stored direction is <c>-vec</c> normalised, without a zero-length guard.</param>
    /// <param name="zOffset">The second stored value, the z offset.</param>
    /// <remarks>Follows 0x0044bb58. Sets mask 0x80.</remarks>
    public void ActivateShield(ReadOnlySpan<float> vec, float zOffset)
    {
        using var chop = ChopScope();
        var l = Len3(vec);
        for (var i = 0; i < 3; ++i)
            FlashNormal[i] = Store(Div(-(double)vec[i], l));
        FlashAmount = zOffset;
        SetMaskBits(0x80);
    }

    /// <summary>Starts a fade on a server object.</summary>
    /// <param name="state">The fade state.</param>
    /// <remarks>Follows 0x0044eed4 and 0x0044ef7c.</remarks>
    public void StartFade(int state)
    {
        FadeState = state;
        FadeStart = SimTimeMs();
        SetMaskBits(0x200);
    }

    /// <summary>Returns the tick time in milliseconds (the global at 0x006a8494 + 4).</summary>
    /// <returns>The time in milliseconds, 0 without a manager.</returns>
    public uint SimTimeMs() =>
        Manager() != null ? (uint)(long)Math.Round(Mul(Manager()!.Time(), 1000.0), MidpointRounding.AwayFromZero) : 0u;

    /// <summary>Ends a fade in once 0x9c4 ms have passed since the start.</summary>
    /// <remarks>Follows 0x0044cff8, end of the pack.</remarks>
    public void UpdateFade()
    {
        if (FadeState == 2 && FadeStart + 2500u <= SimTimeMs())
            FadeState = 0;
    }

    /// <summary>Posts the delayed delete of a destroyed object.</summary>
    /// <remarks>Follows 0x0044f9a0: a server object with deleteOnDestroy posts event 0x401 to itself at manager time + 0.5. The handler (0x0044ffb4) deletes it. Leaving Destroyed does not cancel it.</remarks>
    public void PostDelayedDelete()
    {
        if (!DeleteOnDestroy || Manager() == null)
            return;
        SimManager m = Manager()!;
        m.PostEvent(this, Store(Add(m.Time(), 0.5f)), () => m.DeleteObject(this));
    }

    // ---- Moveable (docs/static-sim.md 2) ----

    // src/game/static_sim.cpp GameBase::loadSegment: 0x004440c0: run when the waypoint differs from the cached one.
    private bool LoadSegment()
    {
        if (Waypoint == CachedSegment)
            return true;
        CachedSegment = Waypoint;
        SimPathObject? path = mCtx.Game?.PathById(PathId) as SimPathObject;
        SimPathObject.Point rec = new();
        Span<float> dpos = stackalloc float[3], drot = stackalloc float[3];
        dpos.Clear();
        drot.Clear();
        WaypointCount = path != null ? path.GetPoint(Waypoint, out rec, dpos, drot) : 0;
        var valid = path != null && WaypointCount != 0 && Waypoint >= 0 && Waypoint < WaypointCount && Waypoint < path.Points().Count;
        if (!valid)
        {
            CachedSegment = -1;
            Waypoint = 0;
            WaypointTime = 0;
            return false;
        }
        using var chop = ChopScope();
        for (var i = 0; i < 3; ++i)
        {
            SegPos[i] = rec.Pos[i];
            SegRot[i] = rec.Rot[i];
            SegDPos[i] = dpos[i];
            SegDRot[i] = drot[i];
        }
        SegmentTime = Store(Div(Len3(SegDPos), MoveData.Speed));
        if (SegmentTime == 0)
            SegmentTime = 1.0f;
        if (WaypointTime > SegmentTime)
            WaypointTime = SegmentTime;
        return true;
    }

    // src/game/static_sim.cpp GameBase::applyMoveable: 0x0044421c. Applied only when (waypoint, time) differs from the
    // last applied pair.
    private bool ApplyMoveable(bool forced)
    {
        if (Waypoint == AppliedWaypoint && WaypointTime == AppliedTime)
            return true;
        using var chop = ChopScope();
        float[] rot = new float[3], pos = new float[3];
        if (SegmentTime != 0)
        {
            var f = Store(Div(WaypointTime, SegmentTime));
            for (var i = 0; i < 3; ++i)
            {
                pos[i] = Store(Add(Mul(SegDPos[i], f), SegPos[i]));
                rot[i] = Store(Add(Mul(SegDRot[i], f), SegRot[i]));
            }
        }
        else
        {
            for (var i = 0; i < 3; ++i)
            {
                pos[i] = SegPos[i];
                rot[i] = SegRot[i];
            }
        }
        var l = Len3(SegDPos);
        var lf = Store(l);
        if (lf == 0 || MoveState == kMoveStopped)
        {
            Velocity[0] = Velocity[1] = Velocity[2] = 0;
        }
        else
        {
            var k = Store(Div(MoveData.Speed, lf));
            for (var i = 0; i < 3; ++i)
                Velocity[i] = Store(Mul(k, SegDPos[i]));
            if (MoveState == kMoveBackward)
                for (var i = 0; i < 3; ++i)
                    Velocity[i] = -Velocity[i];
        }
        float[] oldRot = [Rotation[0], Rotation[1], Rotation[2]];
        float[] oldPos = [Transform.P[0], Transform.P[1], Transform.P[2]];
        void Place(float[] r, float[] p)
        {
            SetRotation(r);
            SetPosition(p);
            mCtx.Game?.ObjectMoved(this);
        }
        var directionOpen = (MoveState == kMoveForward && !MoveData.BlockForward) ||
                             (MoveState == kMoveBackward && !MoveData.BlockBackward);
        if (forced)
        {
            Place(rot, pos);
        }
        else if (MoveData.Displace)
        {
            Place(rot, pos);
            // Displace the objects the new transform overlaps; the move stands when that succeeded or this direction
            // cannot block.
            var ok = MoveableDisplaceOverride != null ? MoveableDisplaceOverride(this)
                                                       : (mCtx.Game == null || mCtx.Game.MoveableDisplace(this, oldPos));
            if (!ok && !directionOpen)
            {
                if (!MoveBlocked)
                    SetMaskBits(0x4000);
                MoveBlocked = true;
                Place(oldRot, oldPos);
                return false;
            }
            if (MoveBlocked)
                SetMaskBits(0x4000);
            MoveBlocked = false;
        }
        else
        {
            // Doors: test the new transform first unless this direction cannot block.
            var free = directionOpen ||
                        (MoveableTestOverride != null ? MoveableTestOverride(this)
                                                      : (mCtx.Game == null || mCtx.Game.MoveableTest(this, rot, pos)));
            if (!free)
            {
                if (!MoveBlocked)
                    SetMaskBits(0x4000);
                MoveBlocked = true;
                return false;
            }
            Place(rot, pos);
            if (MoveBlocked)
                SetMaskBits(0x4000);
            MoveBlocked = false;
        }
        AppliedWaypoint = Waypoint;
        AppliedTime = WaypointTime;
        return true;
    }

    // src/game/static_sim.cpp GameBase::findPath: 0x004439f0: the first member of the Moveable's group that answers
    // query 0x40c (a SimPath) gives the path id; none: the search repeats next tick.
    private void FindPath()
    {
        if (Group() == null || mCtx.Game == null)
            return;
        foreach (SimObject m in Group()!.Members())
        {
            if (!GameUtil.EqualsNoCase(m.ClassName(), "SimPath"))
                continue;
            var id = mCtx.Game.PathIndex(m);
            if (id < 0)
                continue;
            SetPath(id, 0);
            return;
        }
    }

    /// <summary>Sets the path of a Moveable.</summary>
    /// <param name="id">The path id.</param>
    /// <param name="points">The number of points of the path.</param>
    /// <returns>True when the path was set.</returns>
    /// <remarks>Follows 0x00443974: stores the id, reads the point count, calls the script onNewPath, loads the segment of the current waypoint and snaps to it without the collision test. The Moveable is stopped unless a state was requested earlier. Sets mask 0x2000.</remarks>
    public bool SetPath(int id, int points)
    {
        _ = points;
        PathId = id;
        Span<float> dpos = stackalloc float[3], drot = stackalloc float[3];
        WaypointCount = mCtx.Game?.PathById(PathId) is SimPathObject path ? path.GetPoint(Waypoint, out _, dpos, drot) : 0;
        mCtx.Game?.ScriptCallback(this, "onNewPath", Array.Empty<string>());
        LoadSegment();
        ApplyMoveable(true);
        if (MoveState == kMoveNone)
            SetMoveState(kMoveStopped);
        SetMaskBits(0x2000);
        return true;
    }

    // src/game/static_sim.cpp GameBase::moveableTick: 0x0044388c.
    private void MoveableTick()
    {
        StaticTick(0.03f);
        if (PathId == 0xff)
        {
            FindPath();
            if (PathId == 0xff)
                return;
            SetMoveState(MoveState); // the pending state, now that a path exists
        }
        using var chop = ChopScope();
        MoveableStep(0.032f);
    }

    // src/game/static_sim.cpp GameBase::moveableStep: 0x00443d5c. Every time slice is the f32 0.032 (0x3d03126f).
    private void MoveableStep(float dt)
    {
        if (PathId == 0xff || dt == 0)
            return;
        void Callback(string name, bool withIndex)
        {
            if (mCtx.Game == null)
                return;
            var extra = withIndex ? new[] { Numbers.FormatInt(Waypoint) } : [];
            mCtx.Game.ScriptCallback(this, name, extra);
        }
        if (MoveState == kMoveStopped)
        {
            MoveBlocked = false;
            return;
        }
        if (MoveState == kMoveForward)
        {
            var wp0 = Waypoint;
            var t0 = WaypointTime;
            LoadSegment();
            while (dt > 0 && MoveState == kMoveForward)
            {
                WaypointTime = Store(Add(dt, WaypointTime));
                if (WaypointTime > SegmentTime)
                {
                    dt = Store(Sub(WaypointTime, SegmentTime));
                    WaypointTime = 0;
                    ++Waypoint;
                    LoadSegment();
                }
                else
                {
                    dt = 0;
                }
                if (!ApplyMoveable(false))
                {
                    Waypoint = wp0;
                    WaypointTime = t0;
                    return;
                }
                SetMaskBits(0x08);
                if (WaypointCount - 1 == Waypoint)
                {
                    Callback("onWaypoint", true);
                    Callback("onLast", false);
                    if (MoveState == kMoveForward)
                        SetMoveState(kMoveStopped);
                }
                else if (WaypointTime == 0)
                {
                    if (Waypoint == MoveTarget)
                        SetMoveState(kMoveStopped);
                    Callback("onWaypoint", true);
                }
            }
        }
        else if (MoveState == kMoveBackward)
        {
            var wp0 = Waypoint;
            var t0 = WaypointTime;
            LoadSegment();
            while (dt > 0 && MoveState == kMoveBackward)
            {
                WaypointTime = Store(Sub(WaypointTime, dt));
                if (WaypointTime > 0)
                {
                    dt = 0;
                }
                else
                {
                    dt = -WaypointTime;
                    if (Waypoint == 0)
                    {
                        WaypointTime = 0;
                    }
                    else
                    {
                        --Waypoint;
                        LoadSegment();
                        WaypointTime = SegmentTime;
                    }
                }
                if (!ApplyMoveable(false))
                {
                    Waypoint = wp0;
                    WaypointTime = t0;
                    return;
                }
                SetMaskBits(0x08);
                if (WaypointTime == 0)
                {
                    Callback("onWaypoint", true);
                    Callback("onFirst", false);
                    if (MoveState == kMoveBackward)
                        SetMoveState(kMoveStopped);
                }
                else if (WaypointTime == SegmentTime)
                {
                    if (Waypoint + 1 == MoveTarget)
                        SetMoveState(kMoveStopped);
                    Callback("onWaypoint", true);
                }
            }
        }
    }

    /// <summary>Sets the move state of a Moveable. With no path only the state is stored.</summary>
    /// <param name="state">The move state.</param>
    /// <remarks>Follows 0x00443a40, server part.</remarks>
    public void SetMoveState(int state)
    {
        if (PathId == 0xff)
        {
            MoveState = state;
            return;
        }
        LoadSegment();
        var last = WaypointCount - 1;
        if (Waypoint > last)
        {
            Waypoint = last;
            WaypointTime = 0;
        }
        if (last == Waypoint && WaypointTime > 0)
            WaypointTime = 0;
        if (state == kMoveForward)
        {
            if (last == Waypoint)
                state = kMoveStopped;
        }
        else if (state == kMoveBackward && Waypoint == 0 && WaypointTime == 0)
        {
            state = kMoveStopped;
        }
        if (state != MoveState)
        {
            MoveState = state;
            SetMaskBits(0x4008);
        }
    }

    /// <summary>Sets the current waypoint without a range check or a transform. The next segment load repairs the index.</summary>
    /// <param name="index">The waypoint index.</param>
    /// <remarks>Follows 0x004447ac.</remarks>
    public void SetWaypoint(int index)
    {
        Waypoint = index;
        WaypointTime = 0;
        SetMaskBits(0x08);
    }

    /// <summary>Moves a Moveable to a waypoint.</summary>
    /// <param name="index">The waypoint index.</param>
    /// <returns>True when the move started.</returns>
    /// <remarks>Follows 0x004447dc.</remarks>
    public bool MoveToWaypoint(int index)
    {
        if (index < 0 || index > WaypointCount - 1)
            return false;
        MoveTarget = index;
        if (Waypoint < index)
            SetMoveState(kMoveForward);
        else if (index < Waypoint || WaypointTime > 0)
            SetMoveState(kMoveBackward);
        else
            SetMoveState(kMoveStopped);
        return true;
    }

    /// <summary>Runs the moveForward, moveBackward and stop commands.</summary>
    /// <param name="state">The requested move state.</param>
    /// <remarks>Follows 0x0044484c. Ignored unless the object is active and Enabled.</remarks>
    public void MoveForward(int state)
    {
        if (!Active || DamageState != kEnabled)
            return;
        MoveTarget = 15;
        SetMoveState(state);
    }

    /// <summary>Returns the position along the path, <c>wp + t / T</c> (Moveable::getPosition).</summary>
    /// <returns>The position.</returns>
    public float MoveablePosition()
    {
        using var chop = ChopScope();
        return Store(Add(Waypoint, (double)Store(Div(WaypointTime, SegmentTime))));
    }
}
