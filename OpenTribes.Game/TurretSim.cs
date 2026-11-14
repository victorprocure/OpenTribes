// Port of src/game/turret_sim.cpp: the Turret tick (docs/static-sim.md 4) and the Trigger (docs/static-sim.md 6). The
// turret activation of this file, the target search, aim, sustained fire and the shape bind (turret_fire.cpp,
// turret_control.cpp) are TurretFire.cs.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

public partial class GameBase
{
    // ---- Turret ----

    /// <summary>Runs one turret tick.</summary>
    /// <remarks>Follows GameBase::turretTick (0x00456288, docs/static-sim.md 4.3).</remarks>
    public void TurretTick()
    {
        StaticTick(0.03f);
        using var chop = ChopScope();
        const float dt = 0.032f;
        if (!TurretData.Sustained)
        {
            if (ControlClient == 0 && DamageState == Enabled && Active)
                TurretSearchStep();
            if (ControlClient == 0)
                TurretUpdateAim(null, dt);
            else
                ++TurretCredit;
        }
        else
        {
            switch (TurretBarrelState)
            {
                case 0:
                {
                    // 0x00457db0: ready again after reloadDelay.
                    var ms = unchecked((uint)(int)Mul(TurretData.ReloadDelay, 1000.0f));
                    if (TurretTickMs >= unchecked(ms + TurretStamp))
                    {
                        TurretStamp = TurretTickMs;
                        TurretBarrelState = 1;
                    }
                    break;
                }
                case 1: // 0x00457cc0: the common tick runs a second time, then the search
                    StaticTick(0.03f);
                    if (ControlClient == 0 && DamageState == Enabled && Active)
                        TurretSearchStep();
                    if (ControlClient == 0)
                        TurretUpdateAim(null, dt);
                    else
                        ++TurretCredit;
                    break;
                case 2:
                    TurretSustainedFireTick();
                    break;
                default:
                    break;
            }
            if (TurretBarrelState == 2)
            {
                // The beam drains energyRate * 0.032 (f64 constant) per tick.
                var e = Store(Sub(Energy, Mul(TurretData.EnergyRate, 0.032)));
                if (e < 0.0f)
                {
                    TurretStopBeam();
                    e = 0.0f;
                }
                SetEnergy(e);
            }
        }
    }

    // ---- Trigger ----

    /// <summary>Returns the object type bits (+0x48).</summary>
    /// <returns>The type bits.</returns>
    public uint TypeBits() => _shapeClass switch
    {
        ShapeKind.StaticShape or ShapeKind.Sensor or ShapeKind.Turret => ObjectType.StaticType,
        ShapeKind.Moveable => ObjectType.MoveableType,
        ShapeKind.Item => ObjectType.ItemType,
        ShapeKind.Mine => ObjectType.MineType,
        ShapeKind.Vehicle => ObjectType.VehicleType,
        ShapeKind.NoShape or _ => _kind switch
        {
            Kind.Trigger => ObjectType.TriggerType,
            Kind.Marker => ObjectType.MarkerType,
            Kind.Interior => ObjectType.InteriorType,
            Kind.ShapeBase or _ => GameUtil.EqualsNoCase(ClassName(), "Player") ? ObjectType.PlayerType : 0u,
        },
    };

    /// <summary>Handles a trigger collide event from an object.</summary>
    /// <param name="source">The colliding object.</param>
    /// <remarks>Follows event 0x41f (0x004874b4). Ignored when the trigger is inactive or when the type of <paramref name="source"/> has no bit of the occupant mask 0x24400180.</remarks>
    public void TriggerCollide(GameBase source)
    {
        if (!TriggerActive || Manager() == null || (source.TypeBits() & 0x24400180u) == 0)
            return;
        var now = Manager()!.Time();
        for (var i = 0; i < Occupants.Count; ++i)
            if (Occupants[i].ObjectId == source.Id())
            {
                var o = Occupants[i];
                o.Expiry = Store(Add(now, 0.25f));
                Occupants[i] = o;
                return;
            }
        using var chop = ChopScope();
        Occupants.Add(new Occupant { ObjectId = source.Id(), Expiry = Store(Add(now, 0.25f)), NextContact = Store(Add(now, TriggerInterval)) });
        _ctx.Game?.ScriptCallback(this, "onEnter", [Numbers.FormatInt(source.Id())]);
        if (Occupants.Count == 1 && _ctx.Game != null)
            _ctx.Game.JoinSet(this, 12); // SimTimerSet: event 0x40a every tick
    }

    // src/game/turret_sim.cpp GameBase::triggerTimer: 0x0048762c, per occupant in list order. An occupant that was
    // deleted leaves without a callback, and an emptied list leaves set 12 (0x004872e0).
    private void TriggerTimer()
    {
        if (Manager() == null || _ctx.Game == null)
            return;
        var now = Manager()!.Time();
        using var chop = ChopScope();
        for (var i = 0; i < Occupants.Count;)
        {
            var o = Occupants[i];
            if (Manager()!.FindById(o.ObjectId) == null)
            {
                Occupants.RemoveAt(i);
                if (Occupants.Count == 0)
                    _ctx.Game.LeaveSet(this, 12);
                continue;
            }
            if (now < o.Expiry)
            {
                if (o.NextContact <= now)
                {
                    o.NextContact = Store(Add(now, TriggerInterval));
                    Occupants[i] = o;
                    _ctx.Game.ScriptCallback(this, "onContact", [Numbers.FormatInt(o.ObjectId)]);
                }
                ++i;
            }
            else
            {
                Occupants.RemoveAt(i);
                _ctx.Game.ScriptCallback(this, "onLeave", [Numbers.FormatInt(o.ObjectId)]);
            }
        }
    }

    /// <summary>Switches a trigger on or off.</summary>
    /// <param name="on">The new active state.</param>
    /// <returns>Always true.</returns>
    /// <remarks>Follows 0x004872a4. On a change, turning off first sends onLeave for every occupant from the last to the first and leaves set 12. Then onActivate or onDeactivate runs.</remarks>
    public bool TriggerSetActive(bool on)
    {
        if (on == TriggerActive)
            return true;
        TriggerActive = on;
        // An occupant that was deleted has left the list through its delete notify (0x004872e0).
        if (Manager() != null)
            Occupants.RemoveAll(o => Manager()!.FindById(o.ObjectId) == null);
        if (!on && Occupants.Count != 0)
        {
            while (Occupants.Count != 0)
            {
                var obj = Occupants[^1].ObjectId;
                Occupants.RemoveAt(Occupants.Count - 1);
                _ctx.Game?.ScriptCallback(this, "onLeave", [Numbers.FormatInt(obj)]);
            }
            _ctx.Game?.LeaveSet(this, 12);
        }
        _ctx.Game?.ScriptCallback(this, on ? "onActivate" : "onDeactivate", []);
        return true;
    }

    /// <inheritdoc/>
    public override void OnTimerEvent(float time, int timerId, float interval)
    {
        if (_kind == Kind.Trigger)
            TriggerTimer();
    }
}
