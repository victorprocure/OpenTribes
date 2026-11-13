// Client side pose of a Turret ghost: the power / fire thread (+0x8a4) and the rotation as the stock client drives them
// from the ghost mode (+0x8cc). Sources: Turret_unpackUpdate 0x0045750c (mode, fire count), Turret_updateAim 0x00456874
// (ghost branch: the thread advances in mode 3, mode 2 runs activate, mode 1 runs deactivate), Turret_activate 0x00455b34
// and Turret_deactivate 0x00455cc4 (ghost branches), Turret_setPowerSequence 0x00452e1c, Turret_setFireSequence 0x00452dd0.
// The server mirror of the same thread is GameBase.TurretActivate / TurretDeactivate in TurretFire.cs.
namespace OpenTribes.Game;

public sealed class TurretGhostPose
{
    public const double TwoPi = 6.283185307179586;

    // Thread state: position 0..1 of the current sequence, which is `fire` (priority 0) or `power` (priority -1).
    public double Position { get; private set; }
    public bool OnFire { get; private set; }
    // Power sound the last Tick asked for (the datablock sounds at +0x13c and +0x140): 1 activation, -1 deactivation, 0 none.
    public int PowerSound { get; private set; }
    public int Mode { get; private set; }
    // Rotation in radians (+0x8b4); the turn thread sits at Rotation / 2 pi.
    public double Rotation { get; set; }

    // False when the shape lacks `power` or `fire`: no thread, so activate and deactivate end at once.
    public bool HasThread { get; }
    private readonly double mPowerDuration, mFireDuration;
    private readonly bool mPowerCyclic, mFireCyclic;
    // Datablock rotation speed (+0x114) in radians per second.
    private readonly double mSpeed;

    public TurretGhostPose(bool hasThread, double powerDuration, bool powerCyclic, double fireDuration, bool fireCyclic, double speed)
    {
        HasThread = hasThread;
        mPowerDuration = powerDuration > 1e-6 ? powerDuration : 1e-6;
        mFireDuration = fireDuration > 1e-6 ? fireDuration : 1e-6;
        mPowerCyclic = powerCyclic;
        mFireCyclic = fireCyclic;
        mSpeed = speed;
        // Turret_onNewDataBlock: the thread starts on `power` at position 0.
    }

    private void Set(double pos)
    {
        bool cyclic = OnFire ? mFireCyclic : mPowerCyclic;
        Position = Normalise(pos, cyclic);
    }

    private static double Normalise(double p, bool cyclic)
    {
        if (p < 0 || p > 1)
            return cyclic ? p - Math.Floor(p) : Math.Clamp(p, 0, 1);
        return p;
    }

    private void Advance(double dt)
    {
        double dur = OnFire ? mFireDuration : mPowerDuration;
        Set(Position + dt / dur);
    }

    // Turret_setPowerSequence: the thread takes `power` and keeps its position.
    private void UsePower() => OnFire = false;

    // Turret_setFireSequence: the thread takes `fire` at position 1.
    private void UseFire()
    {
        OnFire = true;
        Set(1.0);
    }

    // The fire count block of the unpack: the thread restarts `fire` at 0 (the shot animation).
    public void OnFireCount()
    {
        if (!HasThread)
            return;
        UseFire();
        Set(0.0);
    }

    // The mode block of the unpack. The thread takes `power`; a drop from on to deactivating jumps to the end of the sequence,
    // a rise from off to activating to the start. The stored mode ignores a 0 while it is 1, so the deactivation finishes in
    // the local tick.
    public void OnMode(int mode)
    {
        if (HasThread)
        {
            UsePower();
            if (mode == 1 && Mode == 3)
                Set(1.0);
            else if (mode == 2 && Mode == 0)
                Set(0.0);
        }
        if (Mode != 1 || mode != 0)
            Mode = mode;
    }

    // Turret_updateAim on a ghost, one step of dt seconds.
    public void Tick(double dt)
    {
        PowerSound = 0;
        if (HasThread && Mode == 3)
            Advance(dt);
        if (HasThread)
        {
            if (Mode == 2)
                Activate(dt);
            else if (Mode == 1)
                Deactivate(dt);
        }
    }

    // Turret_activate, ghost branch: power forward; at 0.99 the thread switches to `fire` and waits for the server to send mode 3.
    private void Activate(double dt)
    {
        UsePower();
        // Turret_activate 0x00455b34: the activation sound starts when the thread is still at position 0.
        if (Position == 0.0)
            PowerSound = 1;
        if (Position >= 0.99)
        {
            UseFire();
            return;
        }
        Mode = 2;
        Advance(dt);
    }

    // Turret_deactivate, ghost branch: the rotation turns home the short way first, then the thread runs backwards; at 0 the mode is 0.
    private void Deactivate(double dt)
    {
        UsePower();
        Mode = 1;
        // Turret_deactivate 0x00455cc4: with the rotation home, the deactivation sound starts while the thread is at position 1.
        if (Rotation == 0 && Position == 1.0)
            PowerSound = -1;
        if (Rotation != 0)
        {
            double local = Rotation;
            while (local > Math.PI)
                local -= TwoPi;
            double step = mSpeed * dt;
            if (local > 0)
            {
                Rotation -= step;
                if (local - step < 0)
                    Rotation = 0;
            }
            else
            {
                Rotation += step;
                if (local + step > 0)
                    Rotation = 0;
            }
            return;
        }
        Advance(-dt);
        if (!(Position > 0))
            Mode = 0;
    }
}
