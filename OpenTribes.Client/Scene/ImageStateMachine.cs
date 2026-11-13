namespace OpenTribes.Client.Scene;

using OpenTribes.Client.Shapes;

/// <summary>The weapon data the state machine of an image needs.</summary>
public sealed class ImageStateData
{
    /// <summary>The weapon type of the image, -1 for none.</summary>
    public int WeaponType = -1;
    /// <summary>The ammo type of the image, -1 for none.</summary>
    public int AmmoType = -1;
    /// <summary>The activate state time in seconds.</summary>
    public double ActivateTime;
    /// <summary>The fire state time in seconds.</summary>
    public double FireTime;
    /// <summary>The reload state time in seconds.</summary>
    public double ReloadTime;
    /// <summary>The spin up state time in seconds.</summary>
    public double SpinUpTime;
    /// <summary>The spin down state time in seconds.</summary>
    public double SpinDownTime;
}

/// <summary>The client side item image state machine of a Player ghost.</summary>
/// <remarks>docs/player-sim.md 7.5, docs/shapes-render.md 3.6 and docs/audio.md 3.2. The server decides the ammo flag, the trigger and the fire count. The ghost carries them and this class runs the state timers, the image threads (state sequence, ambient, spin) and the per state sounds the way <c>Player_updateImageState</c> and <c>Player_setImageState</c> do on a ghost.</remarks>
public sealed class ImageStateMachine
{
    /// <summary>The Reset state.</summary>
    public const int Reset = 0;
    /// <summary>The Fire state.</summary>
    public const int Fire = 1;
    /// <summary>The NoAmmo state.</summary>
    public const int NoAmmo = 2;
    /// <summary>The Idle state.</summary>
    public const int Idle = 3;
    /// <summary>The Ready state.</summary>
    public const int Ready = 4;
    /// <summary>The Reload state.</summary>
    public const int Reload = 5;
    /// <summary>The SpinUp state.</summary>
    public const int SpinUp = 6;
    /// <summary>The SpinDown state.</summary>
    public const int SpinDown = 7;
    /// <summary>The Activate state.</summary>
    public const int Activate = 8;
    /// <summary>The length in seconds of one state update of a move: <c>updateMove</c> calls the state update with 0.032 s.</summary>
    public const double Step = 0.032;

    /// <summary>The current state.</summary>
    public int State;
    /// <summary>The time left in the current state, in seconds.</summary>
    public double Timer;
    /// <summary>True while the trigger is held.</summary>
    public bool Triggered;
    /// <summary>True when the weapon has ammo.</summary>
    public bool Ammo;
    /// <summary>The fire count of the ghost stream.</summary>
    public int FireCount;
    /// <summary>The weapon data.</summary>
    public readonly ImageStateData Data;
    /// <summary>The thread of the state sequence, or null when the shape has no activation sequence.</summary>
    public RenderThread? StateThread;
    /// <summary>The ambient thread, or null.</summary>
    public RenderThread? Ambient;
    /// <summary>The spin thread, or null.</summary>
    public RenderThread? Spin;

    private readonly RenderInstance? mInst;
    private readonly int mSeqActivation = -1, mSeqFire = -1, mSeqReload = -1, mSeqNoAmmo = -1, mSeqReady = -1;
    private double mAccum;

    /// <summary>True when the state updates run per prediction step instead of per 0.032 s of time.</summary>
    /// <remarks>The control object runs its state update with the moves of the client prediction (<c>Player_updateMove</c> is called for every step of the prediction, replays after a server block included, and runs <c>Player_updateImageState</c> with 0.032 s), not once per 0.032 s of time. When set, <see cref="Advance"/> only moves the image threads and <see cref="StepMoves"/> runs the updates.</remarks>
    public bool Stepped;

    /// <summary>Called when the state is left, so a looping state sound stops.</summary>
    public Action? StopLoopingSound;
    /// <summary>Called when a state plays its sound, with the field name of the datablock.</summary>
    public Action<string>? PlaySound;
    /// <summary>Called when a shot is fired: state Fire entered, or fired again while a spinning weapon keeps firing.</summary>
    public Action? Fired;

    /// <summary>Creates the state machine and its threads.</summary>
    /// <param name="data">The weapon data.</param>
    /// <param name="inst">The shape instance of the image, or null.</param>
    public ImageStateMachine(ImageStateData data, RenderInstance? inst)
    {
        Data = data;
        mInst = inst;
        if (inst == null)
            return;
        RenderShape sh = inst.Shape;
        mSeqActivation = sh.FindSequence("activation");
        mSeqFire = sh.FindSequence("fire");
        mSeqReload = sh.FindSequence("reload");
        mSeqNoAmmo = sh.FindSequence("noammo");
        mSeqReady = sh.FindSequence("ready");
        int amb = sh.FindSequence("ambient"), spin = sh.FindSequence("spin");
        // `Player_setImage`: the state thread exists only when the shape has an activation sequence.
        if (mSeqActivation >= 0)
            StateThread = Make(mSeqActivation, 1);
        if (amb >= 0)
            Ambient = Make(amb, 1);
        if (spin >= 0)
            Spin = Make(spin, 0);
    }

    private RenderThread Make(int seq, double scale)
    {
        RenderThread t = mInst!.AddThread(seq, 0, mInst.Shape.Sequences[seq].Priority);
        t.TimeScale = scale;
        return t;
    }

    /// <summary>Advances the image threads every frame and runs the state update once per 0.032 s of time.</summary>
    /// <param name="dt">The frame time in seconds.</param>
    public void Advance(double dt)
    {
        if (mInst != null)
        {
            if (Ambient != null)
                mInst.Advance(Ambient, dt);
            if (StateThread != null)
                mInst.Advance(StateThread, dt);
            if (Spin != null)
                mInst.Advance(Spin, dt);
        }
        if (Stepped)
            return;
        mAccum += dt;
        while (mAccum >= Step)
        {
            mAccum -= Step;
            Update(Step);
        }
    }

    /// <summary>Runs the state update for prediction steps of the control object (see <see cref="Stepped"/>).</summary>
    /// <param name="count">The number of steps.</param>
    public void StepMoves(int count)
    {
        for (var i = 0; i < count; ++i)
            Update(Step);
    }

    /// <summary>Enters Activate, as <c>Player_setImage</c> does on a registered object.</summary>
    public void Mount() => SetState(Activate);

    /// <summary>Applies the values of one slot of the ghost stream (<c>Player_unpackImages</c>): trigger, ammo flag and fire count.</summary>
    /// <param name="triggered">The trigger flag.</param>
    /// <param name="ammo">The ammo flag.</param>
    /// <param name="fireCount">The fire count.</param>
    /// <param name="registered">True for a registered ghost, which enters Fire when the count changed. A new ghost only stores the count.</param>
    /// <param name="initialFire">True when a new ghost starts in Fire.</param>
    public void Unpack(bool triggered, bool ammo, int fireCount, bool registered, bool initialFire)
    {
        Triggered = triggered;
        Ammo = ammo;
        if (!registered)
        {
            FireCount = fireCount;
            if (initialFire)
                State = Fire;
            return;
        }
        if (fireCount != FireCount)
        {
            FireCount = fireCount;
            SetState(Fire);
        }
        Update(0);
    }

    /// <summary>Runs one state update.</summary>
    /// <param name="dt">The time step in seconds.</param>
    public void Update(double dt)
    {
        Timer -= dt;
        var wt = Data.WeaponType;
        var expired = !(Timer > 0);
        switch (State)
        {
            case Reset:
            case Activate:
                if (expired)
                    SetState(wt == 1 ? Idle : !Ammo ? NoAmmo : wt == 3 ? Reload : Ready);
                break;
            case Fire:
                if (expired)
                {
                    if (wt == 1)
                    {
                        if (!Triggered || !Ammo)
                            SetState(SpinDown);
                        else
                        {
                            Timer = Data.FireTime;
                            Fired?.Invoke();
                        }
                    }
                    else if (wt == 2)
                    {
                        if (!Triggered || !Ammo)
                            SetState(Reload);
                    }
                    else
                        SetState(Reload);
                }
                break;
            case NoAmmo:
                if (Ammo)
                    SetState(wt == 1 ? (Triggered ? SpinUp : Idle) : Reload);
                break;
            case Idle:
                if (!Ammo)
                    SetState(NoAmmo);
                else if (Triggered)
                    SetState(wt == 1 ? SpinUp : Ready);
                break;
            case Ready:
                if (wt == 1 && (!Triggered || !Ammo))
                    SetState(SpinDown);
                break;
            case Reload:
                if (expired)
                    SetState(wt == 3 ? SpinUp : Ready);
                break;
            case SpinUp:
                if (wt == 3)
                {
                    if (!Ammo)
                    {
                        SetState(NoAmmo);
                        return;
                    }
                }
                else if (!Triggered || !Ammo)
                {
                    SetState(SpinDown);
                    return;
                }
                if (Timer > 0)
                {
                    Spin?.TimeScale = 1 - (Timer / Data.SpinUpTime);
                }
                else
                    SetState(Ready);
                break;
            case SpinDown:
                if (!Triggered || !Ammo)
                {
                    if (Timer > 0)
                    {
                        Spin?.TimeScale = Timer / Data.SpinDownTime;
                    }
                    else
                        SetState(Idle);
                }
                else
                    SetState(SpinUp);
                break;
        }
    }

    private void StartState(int seq)
    {
        if (StateThread != null && seq >= 0)
        {
            StateThread.TimeScale = 1;
            StateThread.Sequence = seq;
            StateThread.Position = 0;
            StateThread.LastPosition = 0;
            StateThread.Priority = mInst!.Shape.Sequences[seq].Priority;
        }
    }

    private void SpinScale(double v) => Spin?.TimeScale = v;

    /// <summary>Enters a state: stops the looping sound, sets the timer and plays the sound of the state.</summary>
    /// <param name="next">The state.</param>
    public void SetState(int next)
    {
        var old = State;
        var oldTimer = Timer;
        State = next;
        StopLoopingSound?.Invoke();
        // A cyclic state sequence is rewound and held when its state ends; a one shot keeps its end pose.
        if (StateThread != null && mInst != null && StateThread.Sequence >= 0 && mInst.Shape.Sequences[StateThread.Sequence].Cyclic)
        {
            StateThread.Position = 0;
            StateThread.TimeScale = 0;
        }
        switch (next)
        {
            case Fire:
                Timer = Data.FireTime;
                Fired?.Invoke();
                PlaySound?.Invoke("sfxFire");
                StartState(mSeqFire);
                SpinScale(1);
                break;
            case NoAmmo:
                SpinScale(0);
                StartState(mSeqNoAmmo);
                break;
            case Idle:
                SpinScale(0);
                break;
            case Ready:
                PlaySound?.Invoke("sfxReady");
                SpinScale(1);
                StartState(mSeqReady);
                break;
            case Reload:
                if (Data.AmmoType == -1 || Ammo)
                {
                    PlaySound?.Invoke("sfxReload");
                    Timer = Data.ReloadTime;
                    SpinScale(0);
                    StartState(mSeqReload);
                    if (Data.ReloadTime == 0.0)
                        SetState(Ready);
                }
                else
                    SetState(NoAmmo);
                break;
            case SpinUp:
                PlaySound?.Invoke("sfxSpinUp");
                Timer = old == SpinDown ? Data.SpinUpTime * (1 - (oldTimer / Data.SpinDownTime)) : Data.SpinUpTime;
                break;
            case SpinDown:
                PlaySound?.Invoke("sfxSpinDown");
                Timer = old == SpinUp ? Data.SpinDownTime * (1 - (oldTimer / Data.SpinUpTime)) : Data.SpinDownTime;
                break;
            case Activate:
                Timer = Data.ActivateTime;
                PlaySound?.Invoke("sfxActivate");
                StartState(mSeqActivation);
                break;
        }
    }
}
