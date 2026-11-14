// Client side pose of a Turret ghost: the power / fire thread (+0x8a4) and the rotation as the stock client drives them
// from the ghost mode (+0x8cc). Sources: Turret_unpackUpdate 0x0045750c (mode, fire count), Turret_updateAim 0x00456874
// (ghost branch: the thread advances in mode 3, mode 2 runs activate, mode 1 runs deactivate), Turret_activate 0x00455b34
// and Turret_deactivate 0x00455cc4 (ghost branches), Turret_setPowerSequence 0x00452e1c, Turret_setFireSequence 0x00452dd0.
// The server mirror of the same thread is GameBase.TurretActivate / TurretDeactivate in TurretFire.cs.
namespace OpenTribes.Game;

/// <summary>The client side pose of a Turret ghost: the power and fire thread (+0x8a4) and the rotation, as the stock client drives them from the ghost mode (+0x8cc).</summary>
/// <remarks>Sources: Turret_unpackUpdate (0x0045750c, mode and fire count), Turret_updateAim (0x00456874, ghost branch: the thread advances in mode 3, mode 2 runs activate, mode 1 runs deactivate), Turret_activate (0x00455b34) and Turret_deactivate (0x00455cc4) ghost branches, Turret_setPowerSequence (0x00452e1c) and Turret_setFireSequence (0x00452dd0). The server mirror is <c>GameBase.TurretActivate</c> and <c>TurretDeactivate</c> in TurretFire.cs.</remarks>
public sealed class TurretGhostPose
{
    /// <summary>The value of two pi.</summary>
    public const double TwoPi = 6.283185307179586;

    /// <summary>Gets the thread position, 0 to 1, in the current sequence, which is <c>fire</c> (priority 0) or <c>power</c> (priority -1).</summary>
    public double Position { get; private set; }
    /// <summary>Gets whether the thread runs <c>fire</c>.</summary>
    public bool OnFire { get; private set; }
    /// <summary>Gets the power sound the last Tick asked for (the datablock sounds at +0x13c and +0x140): 1 activation, -1 deactivation, 0 none.</summary>
    public int PowerSound { get; private set; }
    /// <summary>Gets the turret mode.</summary>
    public int Mode { get; private set; }
    /// <summary>Gets or sets the rotation in radians (+0x8b4). The turn thread sits at the rotation divided by two pi.</summary>
    public double Rotation { get; set; }

    /// <summary>Gets whether the shape has the <c>power</c> and <c>fire</c> sequences. Without them there is no thread, and activate and deactivate end at once.</summary>
    public bool HasThread { get; }
    private readonly double _powerDuration, _fireDuration;
    private readonly bool _powerCyclic, _fireCyclic;
    // Datablock rotation speed (+0x114) in radians per second.
    private readonly double _speed;

    /// <summary>Creates the pose.</summary>
    /// <param name="hasThread">True when the shape has the <c>power</c> and <c>fire</c> sequences.</param>
    /// <param name="powerDuration">The duration of the <c>power</c> sequence.</param>
    /// <param name="powerCyclic">True when the <c>power</c> sequence is cyclic.</param>
    /// <param name="fireDuration">The duration of the <c>fire</c> sequence.</param>
    /// <param name="fireCyclic">True when the <c>fire</c> sequence is cyclic.</param>
    /// <param name="speed">The turret speed.</param>
    public TurretGhostPose(bool hasThread, double powerDuration, bool powerCyclic, double fireDuration, bool fireCyclic, double speed)
    {
        HasThread = hasThread;
        _powerDuration = powerDuration > 1e-6 ? powerDuration : 1e-6;
        _fireDuration = fireDuration > 1e-6 ? fireDuration : 1e-6;
        _powerCyclic = powerCyclic;
        _fireCyclic = fireCyclic;
        _speed = speed;
        // Turret_onNewDataBlock: the thread starts on `power` at position 0.
    }

    private void Set(double pos)
    {
        var cyclic = OnFire ? _fireCyclic : _powerCyclic;
        Position = Normalise(pos, cyclic);
    }

    private static double Normalise(double p, bool cyclic)
    {
        if (p is < 0 or > 1)
            return cyclic ? p - Math.Floor(p) : Math.Clamp(p, 0, 1);
        return p;
    }

    private void Advance(double dt)
    {
        var dur = OnFire ? _fireDuration : _powerDuration;
        Set(Position + (dt / dur));
    }

    // Turret_setPowerSequence: the thread takes `power` and keeps its position.
    private void UsePower() => OnFire = false;

    // Turret_setFireSequence: the thread takes `fire` at position 1.
    private void UseFire()
    {
        OnFire = true;
        Set(1.0);
    }

    /// <summary>Handles the fire count block of the unpack: the thread restarts <c>fire</c> at 0, the shot animation.</summary>
    public void OnFireCount()
    {
        if (!HasThread)
            return;
        UseFire();
        Set(0.0);
    }

    /// <summary>Handles the mode block of the unpack.</summary>
    /// <param name="mode">The new mode.</param>
    /// <remarks>The thread takes <c>power</c>. A drop from on to deactivating jumps to the end of the sequence, a rise from off to activating to the start. The stored mode ignores a 0 while it is 1, so the deactivation finishes in the local tick.</remarks>
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

    /// <summary>Runs Turret_updateAim on a ghost for one step.</summary>
    /// <param name="dt">The step length in seconds.</param>
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
            var local = Rotation;
            while (local > Math.PI)
                local -= TwoPi;
            var step = _speed * dt;
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
