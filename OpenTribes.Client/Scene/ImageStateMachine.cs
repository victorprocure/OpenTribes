// Client side item image state machine of a Player ghost (docs/player-sim.md 7.5, docs/shapes-render.md 3.6, docs/audio.md 3.2).
// The server decides the ammo flag, the trigger and the fire count; the ghost carries them and this class runs the state timers,
// the image threads (state sequence, ambient, spin) and the per state sounds the way `Player_updateImageState` and
// `Player_setImageState` do on a ghost.
using OpenTribes.Client.Shapes;

namespace OpenTribes.Client.Scene;

public sealed class ImageStateData
{
    public int WeaponType = -1, AmmoType = -1;
    public double ActivateTime, FireTime, ReloadTime, SpinUpTime, SpinDownTime;
}

public sealed class ImageStateMachine
{
    public const int Reset = 0, Fire = 1, NoAmmo = 2, Idle = 3, Ready = 4, Reload = 5, SpinUp = 6, SpinDown = 7, Activate = 8;
    // The update step of a move (`updateMove` calls the state update with 0.032 s).
    public const double Step = 0.032;

    public int State;
    public double Timer;
    public bool Triggered, Ammo;
    public int FireCount;
    public readonly ImageStateData Data;
    public RenderThread? StateThread, Ambient, Spin;

    private readonly RenderInstance? mInst;
    private readonly int mSeqActivation = -1, mSeqFire = -1, mSeqReload = -1, mSeqNoAmmo = -1, mSeqReady = -1;
    private double mAccum;

    // The control object runs its state update with the moves of the client prediction (`Player_updateMove` is called for
    // every step of the prediction, replays after a server block included, and runs `Player_updateImageState` with 0.032 s),
    // not once per 0.032 s of time. When set, `Advance` only moves the image threads and `Step` runs the updates.
    public bool Stepped;

    // Called when the state is left (a looping state sound stops) and when a state plays its sound (field name of the datablock).
    public Action? StopLoopingSound;
    public Action<string>? PlaySound;
    // Called when a shot is fired (state Fire entered, or fired again while a spinning weapon keeps firing).
    public Action? Fired;

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

    // Advances the image threads (every frame) and runs the state update once per 0.032 s of time.
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

    // `count` prediction steps of the control object (see `Stepped`).
    public void StepMoves(int count)
    {
        for (int i = 0; i < count; ++i)
            Update(Step);
    }

    // `Player_setImage` on a registered object enters Activate.
    public void Mount() => SetState(Activate);

    // The ghost stream values of one slot (`Player_unpackImages`): trigger, ammo flag and fire count. A new ghost
    // only stores the count (and starts in Fire when the initial flag is set); a registered one enters Fire when the count changed.
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

    public void Update(double dt)
    {
        Timer -= dt;
        int wt = Data.WeaponType;
        bool expired = !(Timer > 0);
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
                    if (Spin != null)
                        Spin.TimeScale = 1 - Timer / Data.SpinUpTime;
                }
                else
                    SetState(Ready);
                break;
            case SpinDown:
                if (!Triggered || !Ammo)
                {
                    if (Timer > 0)
                    {
                        if (Spin != null)
                            Spin.TimeScale = Timer / Data.SpinDownTime;
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

    private void SpinScale(double v)
    {
        if (Spin != null)
            Spin.TimeScale = v;
    }

    public void SetState(int next)
    {
        int old = State;
        double oldTimer = Timer;
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
                Timer = old == SpinDown ? Data.SpinUpTime * (1 - oldTimer / Data.SpinDownTime) : Data.SpinUpTime;
                break;
            case SpinDown:
                PlaySound?.Invoke("sfxSpinDown");
                Timer = old == SpinUp ? Data.SpinDownTime * (1 - oldTimer / Data.SpinUpTime) : Data.SpinDownTime;
                break;
            case Activate:
                Timer = Data.ActivateTime;
                PlaySound?.Invoke("sfxActivate");
                StartState(mSeqActivation);
                break;
        }
    }
}
