// Port of src/game/turret_sim.cpp: the Turret tick (docs/static-sim.md 4) and the Trigger (docs/static-sim.md 6). The
// turret activation of this file, the target search, aim, sustained fire and the shape bind (turret_fire.cpp,
// turret_control.cpp) are TurretFire.cs.
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

public partial class GameBase
{
    // ---- Turret ----

    // src/game/turret_sim.cpp GameBase::turretTick: 0x00456288 (docs/static-sim.md 4.3).
    public void TurretTick()
    {
        StaticTick(0.03f);
        using var chop = ChopScope();
        const float dt = 0.032f;
        if (!TurretData.Sustained)
        {
            if (ControlClient == 0 && DamageState == kEnabled && Active)
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
                    uint ms = unchecked((uint)(int)Mul(TurretData.ReloadDelay, 1000.0f));
                    if (TurretTickMs >= unchecked(ms + TurretStamp))
                    {
                        TurretStamp = TurretTickMs;
                        TurretBarrelState = 1;
                    }
                    break;
                }
                case 1: // 0x00457cc0: the common tick runs a second time, then the search
                    StaticTick(0.03f);
                    if (ControlClient == 0 && DamageState == kEnabled && Active)
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
                float e = Store(Sub(Energy, Mul(TurretData.EnergyRate, 0.032)));
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

    // src/game/turret_sim.cpp GameBase::typeBits: object type (+0x48).
    public uint TypeBits()
    {
        switch (mShapeClass)
        {
            case ShapeKind.StaticShape:
            case ShapeKind.Sensor:
            case ShapeKind.Turret:
                return ObjectType.kStaticType;
            case ShapeKind.Moveable:
                return ObjectType.kMoveableType;
            case ShapeKind.Item:
                return ObjectType.kItemType;
            case ShapeKind.Mine:
                return ObjectType.kMineType;
            case ShapeKind.Vehicle:
                return ObjectType.kVehicleType;
        }
        return mKind switch
        {
            Kind.Trigger => ObjectType.kTriggerType,
            Kind.Marker => ObjectType.kMarkerType,
            Kind.Interior => ObjectType.kInteriorType,
            _ => GameUtil.EqualsNoCase(ClassName(), "Player") ? ObjectType.kPlayerType : 0u,
        };
    }

    // src/game/turret_sim.cpp GameBase::triggerCollide: event 0x41f (0x004874b4): ignored when inactive or when the
    // source's type has no bit of the occupant mask 0x24400180.
    public void TriggerCollide(GameBase source)
    {
        if (!TriggerActive || Manager() == null || (source.TypeBits() & 0x24400180u) == 0)
            return;
        float now = Manager()!.Time();
        for (int i = 0; i < Occupants.Count; ++i)
            if (Occupants[i].Object == source.Id())
            {
                Occupant o = Occupants[i];
                o.Expiry = Store(Add(now, 0.25f));
                Occupants[i] = o;
                return;
            }
        using var chop = ChopScope();
        Occupants.Add(new Occupant { Object = source.Id(), Expiry = Store(Add(now, 0.25f)), NextContact = Store(Add(now, TriggerInterval)) });
        mCtx.Game?.ScriptCallback(this, "onEnter", new[] { Numbers.FormatInt(source.Id()) });
        if (Occupants.Count == 1 && mCtx.Game != null)
            mCtx.Game.JoinSet(this, 12); // SimTimerSet: event 0x40a every tick
    }

    // src/game/turret_sim.cpp GameBase::triggerTimer: 0x0048762c, per occupant in list order. An occupant that was
    // deleted leaves without a callback, and an emptied list leaves set 12 (0x004872e0).
    private void TriggerTimer()
    {
        if (Manager() == null || mCtx.Game == null)
            return;
        float now = Manager()!.Time();
        using var chop = ChopScope();
        for (int i = 0; i < Occupants.Count;)
        {
            Occupant o = Occupants[i];
            if (Manager()!.FindById(o.Object) == null)
            {
                Occupants.RemoveAt(i);
                if (Occupants.Count == 0)
                    mCtx.Game.LeaveSet(this, 12);
                continue;
            }
            if (now < o.Expiry)
            {
                if (o.NextContact <= now)
                {
                    o.NextContact = Store(Add(now, TriggerInterval));
                    Occupants[i] = o;
                    mCtx.Game.ScriptCallback(this, "onContact", new[] { Numbers.FormatInt(o.Object) });
                }
                ++i;
            }
            else
            {
                Occupants.RemoveAt(i);
                mCtx.Game.ScriptCallback(this, "onLeave", new[] { Numbers.FormatInt(o.Object) });
            }
        }
    }

    // src/game/turret_sim.cpp GameBase::triggerSetActive: 0x004872a4: on a change, turning off first sends onLeave for
    // every occupant from the last to the first and leaves set 12; then onActivate / onDeactivate. Always True.
    public bool TriggerSetActive(bool on)
    {
        if (on == TriggerActive)
            return true;
        TriggerActive = on;
        // An occupant that was deleted has left the list through its delete notify (0x004872e0).
        if (Manager() != null)
            Occupants.RemoveAll(o => Manager()!.FindById(o.Object) == null);
        if (!on && Occupants.Count != 0)
        {
            while (Occupants.Count != 0)
            {
                int obj = Occupants[^1].Object;
                Occupants.RemoveAt(Occupants.Count - 1);
                mCtx.Game?.ScriptCallback(this, "onLeave", new[] { Numbers.FormatInt(obj) });
            }
            mCtx.Game?.LeaveSet(this, 12);
        }
        mCtx.Game?.ScriptCallback(this, on ? "onActivate" : "onDeactivate", Array.Empty<string>());
        return true;
    }

    // src/game/turret_sim.cpp GameBase::onTimerEvent.
    public override void OnTimerEvent(float time, int timerId, float interval)
    {
        if (mKind == Kind.Trigger)
            TriggerTimer();
    }
}
