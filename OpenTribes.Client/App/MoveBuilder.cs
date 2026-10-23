// docs/client-net.md 4.1 and 4.2: the actions of the play maps fill the accumulators of the move being built
// (PlayerPSC_onAction 004070b0) and every crossed 32 ms boundary appends a move (PlayerPSC_collectMoves 00404c3c).
using OpenTribes.Game;
using OpenTribes.Gui;

namespace OpenTribes.Client.App;

public sealed class MoveBuilder
{
    // Action ids: 220000 + the number in fear.strings.cs (client-scripts.md 3.10).
    public const int Fire1 = 220001, Pitch = 220003, Yaw = 220004, MoveLeft = 220007, MoveRight = 220008, MoveBack = 220009,
        MoveForward = 220010, MoveUp = 220011, View = 220017, Break1 = 220019, Run = 220104, Strafe = 220105, UseItem = 220106,
        Jet = 220107, Crouch = 220110, Stand = 220111, ToggleCrouch = 220112, SniperFov = 220113, IncSniperFov = 220114,
        ObserverToggle = 220214, LookUp = 220221, LookDown = 220222, TurnLeft = 220223, TurnRight = 220224, PitchSpeed = 220225,
        YawSpeed = 220226, CenterView = 220230;

    public const int MaxMoves = 30;

    // The move being built (psc +0x70..+0x8c) and the key rates (+0x3f0..+0x404).
    public float Forward, Backward, Left, Right;
    public bool JetOn, CrouchOn, JumpOn;
    public int UseItemType = -1;
    public float TurnAcc, PitchAcc;
    public float YawSpeedKey, PitchSpeedKey, TurnLeftKey, TurnRightKey, LookUpKey, LookDownKey;
    public uint FireCounter = 0, SampledFire = 0;
    public bool ThirdPerson;                 // not +0xa0 (first person): the VIEW toggle (0040706c.. PlayerPSC_onActionDefault)
    public double CurrentFov = 90;
    // +0x9c: the third person distance, 0 in first person and up to 4 m, eased at 15 m/s (4.2 step 1).
    public float ViewDist;
    public const float MaxViewDist = 4f;
    // The zoom (0060f0b0 on) and its FOV (0060f0a4, 22.5 at start: the PlayerFov callback 004040b0 stores 90 * 0.25 in 0060f078 and the PSC
    // constructor copies it). INC_SNIPER_FOV only changes this value and the view only follows it while the zoom is on.
    public bool ZoomOn;
    public double ZoomTarget = 22.5;
    // The FOV animation of the PSC [code 00406f47, 00406e06, 004077cd]: from +0x408 to +0x40c over the times +0x414 .. +0x418 (ms), the
    // length being $pref::PlayerZoomSpeed (0060f0a8, clamped 0..1) * 1000. CurrentFov (0060f0a0) follows it at every frame.
    public double FovFrom = 90, FovTo = 90;
    public uint FovStart, FovEnd;
    // 0060f0b4: the time of the last INC_SNIPER_FOV while the zoom was off (the crosshair shows the zoom box for 2 s after it).
    public uint ZoomStepStamp;
    // The client time (006a83a0) of the last frame.
    public uint LastNow;
    public Func<double> ZoomSpeed = () => 0;
    public bool FirstPersonNow => !ThirdPerson && ViewDist == 0;
    // True when the control object may look (vtable +0x11c) and when PITCH is reversed (flier with $pref::flipFlierY).
    public Func<bool> CanLook = () => true;
    public Func<bool> ReversePitch = () => false;
    public Func<double> PrefFov = () => 90;

    private static float Clamp(float v, float lo, float hi) => Math.Min(Math.Max(v, lo), hi);

    // scaled(v) = clamp(v * currentFov / 90, -1, 1)
    private float Scaled(float v) => Clamp((float)(v * CurrentFov / 90.0), -1f, 1f);

    // True when the action is one of the rows of 4.1 (it is consumed here).
    public bool OnAction(int id, float v)
    {
        switch (id)
        {
            case Fire1: FireCounter += (FireCounter & 1) == 0 ? 1u : 2u; return true;
            case Break1: FireCounter += (FireCounter & 1) == 1 ? 1u : 2u; return true;
            case MoveForward: Forward = Clamp(v, 0, 1); return true;
            case MoveBack: Backward = Clamp(v, 0, 1); return true;
            case MoveLeft: Left = Clamp(v, 0, 1); return true;
            case MoveRight: Right = Clamp(v, 0, 1); return true;
            case Run:
                if (v < -0.01f) { Backward = 1; Forward = 0; }
                else if (v > 0.01f) { Forward = 1; Backward = 0; }
                else { Forward = Backward = 0; }
                return true;
            case Strafe:
                if (v < -0.01f) { Right = 1; Left = 0; }
                else if (v > 0.01f) { Left = 1; Right = 0; }
                else { Left = Right = 0; }
                return true;
            case MoveUp: JumpOn = true; return true;
            case Jet: JetOn = v != 0; return true;
            case Crouch: CrouchOn = true; return true;
            case Stand: CrouchOn = false; return true;
            case ToggleCrouch: CrouchOn = !CrouchOn; return true;
            case UseItem: UseItemType = (int)v; return true;
            case Yaw: TurnAcc += Scaled(v) * (float)Math.PI; return true;
            case Pitch:
                if (CanLook())
                {
                    float d = Scaled(v) * (float)Math.PI;
                    PitchAcc += ReversePitch() ? -d : d;
                }
                return true;
            case LookUp: LookUpKey = Clamp(v, 0, 1); return true;
            case LookDown: LookDownKey = Clamp(v, 0, 1); return true;
            case TurnLeft: TurnLeftKey = Clamp(v, 0, 1); return true;
            case TurnRight: TurnRightKey = Clamp(v, 0, 1); return true;
            case PitchSpeed: PitchSpeedKey = Clamp(v, -1, 1) * (ReversePitch() ? -1 : 1); return true;
            case YawSpeed: YawSpeedKey = Clamp(v, -1, 1); return true;
            case View:
                // 00406dc5: only the FOV target +0x40c changes, the frame update snaps to it
                ThirdPerson = !ThirdPerson;
                if (ThirdPerson)
                    ZoomOn = false;
                FovTo = ThirdPerson ? 90 : PrefFov();
                return true;
            case SniperFov:
                // 00406f47: the zoom needs first person and a control object that accepts it (vtable +0x174, true for a Player, Vehicle
                // and Flier, so every weapon zooms); a refused press changes nothing
                if (v == 0)
                {
                    FovTo = FirstPersonNow ? PrefFov() : 90;
                    ZoomOn = false;
                }
                else if (FirstPersonNow)
                {
                    FovTo = ZoomTarget;
                    ZoomOn = true;
                }
                else
                    return true;
                StartFovAnimation();
                return true;
            case IncSniperFov:
                {
                    int cur = (int)ZoomTarget;
                    ZoomTarget = v > 0
                        ? cur switch { 5 => 45, 11 => 5.625, 22 => 11.25, 45 => 22.5, _ => 45 }
                        : cur switch { 5 => 11.25, 11 => 22.5, 22 => 45, 45 => 5.625, _ => 45 };
                    // 00406ecb: the zoom on restarts the animation toward the new value, off only stamps the time
                    if (ZoomOn)
                    {
                        FovTo = ZoomTarget;
                        StartFovAnimation();
                    }
                    else
                    {
                        if (FovEnd < LastNow)
                            FovStart = LastNow;
                        ZoomStepStamp = LastNow;
                    }
                    return true;
                }
            default: return false;
        }
    }

    private void StartFovAnimation()
    {
        FovFrom = CurrentFov;
        FovStart = LastNow;
        FovEnd = (uint)(long)(LastNow + (float)ZoomSpeed() * 1000f);
    }

    // The FOV update of the camera getter [code 004077cd]: inside the animation the value is interpolated linearly, after it the
    // value snaps to 90 (third person), the zoom FOV (zoom on) or $pref::PlayerFov.
    public void UpdateFov(uint now)
    {
        if (now < FovEnd)
            CurrentFov = (float)((double)(now - FovStart) / (double)(FovEnd - FovStart) * (FovTo - FovFrom) + FovFrom);
        else
        {
            CurrentFov = ThirdPerson ? 90 : ZoomOn ? ZoomTarget : PrefFov();
            FovTo = CurrentFov;
        }
    }

    // What the crosshair reads [code 00404578].
    public HudZoom HudZoomState() => new(CurrentFov, ZoomTarget, FovTo, FovStart, FovEnd, LastNow, ZoomStepStamp);

    // 4.2 step 1: the view distance moves toward 0 in first person and toward 4 otherwise, 15 * dt * 0.001 per call.
    public void UpdateViewDist(uint prev, uint now)
    {
        float step = 15f * (now - prev) * 0.001f;
        float target = ThirdPerson ? MaxViewDist : 0f;
        ViewDist = ViewDist < target ? Math.Min(ViewDist + step, target) : Math.Max(ViewDist - step, target);
    }

    private Move Current(bool fire, float turn, float pitch) => new()
    {
        Forward = Forward, Backward = Backward, Left = Left, Right = Right,
        Jet = JetOn, Crouch = CrouchOn, Jump = JumpOn, Fire = fire, UseItem = UseItemType,
        Turn = turn, Pitch = pitch,
    };

    // 4.2: called every frame with the previous and the new client time (ms). Appends the moves of the 32 ms
    // boundaries crossed to the PSC buffer (at most 30 wait for acknowledgement).
    public int Collect(ClientPsc psc, uint prev, uint now)
    {
        UpdateViewDist(prev, now);
        LastNow = now;
        UpdateFov(now);
        long n = (((long)now - 1) >> 5) - (((long)prev - 1) >> 5);
        if (n <= 0)
            return 0;
        float pitchKeys = PitchSpeedKey + LookUpKey - LookDownKey;
        float turnKeys = TurnLeftKey - YawSpeedKey - TurnRightKey;
        int made = 0;
        if (psc.Moves.Count < MaxMoves)
        {
            PitchAcc += pitchKeys;
            TurnAcc += turnKeys;
            bool fire = (FireCounter & 1) == 1 || ((SampledFire & 1) == 0 && SampledFire != FireCounter);
            Move m = ClientPrediction.Quantize(Current(fire, TurnAcc, PitchAcc));
            SampledFire = FireCounter;
            psc.AddMove(m);
            ++made;
        }
        bool held = (FireCounter & 1) == 1;
        JumpOn = false;
        UseItemType = -1;
        for (long k = 1; k < n && psc.Moves.Count < MaxMoves; ++k)
        {
            psc.AddMove(ClientPrediction.Quantize(Current(held, turnKeys, pitchKeys)));
            ++made;
        }
        TurnAcc = 0;
        PitchAcc = 0;
        return made;
    }

    public void Reset()
    {
        Forward = Backward = Left = Right = 0;
        JetOn = CrouchOn = JumpOn = false;
        UseItemType = -1;
        TurnAcc = PitchAcc = 0;
        YawSpeedKey = PitchSpeedKey = TurnLeftKey = TurnRightKey = LookUpKey = LookDownKey = 0;
        FireCounter = SampledFire = 0;
        ThirdPerson = false;
        ViewDist = 0;
        ZoomOn = false;
        ZoomTarget = 22.5;
        CurrentFov = FovFrom = FovTo = 90;
        FovStart = FovEnd = 0;
    }
}
