namespace OpenTribes.Client.App;

using OpenTribes.Game;
using OpenTribes.Gui;

/// <summary>Builds the moves of the control player from the actions of the play maps.</summary>
/// <remarks>docs/client-net.md 4.1 and 4.2. The actions fill the accumulators of the move being built (PlayerPSC_onAction 004070b0) and every crossed 32 ms boundary appends a move (PlayerPSC_collectMoves 00404c3c). The action ids are 220000 plus the number in fear.strings.cs (client-scripts.md 3.10).</remarks>
public sealed class MoveBuilder
{
    /// <summary>The action id for the fire button (FIRE1).</summary>
    public const int Fire1 = 220001;
    /// <summary>The action id for the pitch axis.</summary>
    public const int Pitch = 220003;
    /// <summary>The action id for the yaw axis.</summary>
    public const int Yaw = 220004;
    /// <summary>The action id for move left.</summary>
    public const int MoveLeft = 220007;
    /// <summary>The action id for move right.</summary>
    public const int MoveRight = 220008;
    /// <summary>The action id for move back.</summary>
    public const int MoveBack = 220009;
    /// <summary>The action id for move forward.</summary>
    public const int MoveForward = 220010;
    /// <summary>The action id for move up (jump).</summary>
    public const int MoveUp = 220011;
    /// <summary>The action id for the view toggle (first and third person).</summary>
    public const int View = 220017;
    /// <summary>The action id for the brake.</summary>
    public const int Break1 = 220019;
    /// <summary>The action id for run.</summary>
    public const int Run = 220104;
    /// <summary>The action id for strafe.</summary>
    public const int Strafe = 220105;
    /// <summary>The action id for use an item.</summary>
    public const int UseItem = 220106;
    /// <summary>The action id for jet.</summary>
    public const int Jet = 220107;
    /// <summary>The action id for crouch.</summary>
    public const int Crouch = 220110;
    /// <summary>The action id for stand.</summary>
    public const int Stand = 220111;
    /// <summary>The action id for toggle crouch.</summary>
    public const int ToggleCrouch = 220112;
    /// <summary>The action id for the sniper zoom.</summary>
    public const int SniperFov = 220113;
    /// <summary>The action id for increase the sniper field of view.</summary>
    public const int IncSniperFov = 220114;
    /// <summary>The action id for the observer toggle.</summary>
    public const int ObserverToggle = 220214;
    /// <summary>The action id for look up.</summary>
    public const int LookUp = 220221;
    /// <summary>The action id for look down.</summary>
    public const int LookDown = 220222;
    /// <summary>The action id for turn left.</summary>
    public const int TurnLeft = 220223;
    /// <summary>The action id for turn right.</summary>
    public const int TurnRight = 220224;
    /// <summary>The action id for the pitch speed.</summary>
    public const int PitchSpeed = 220225;
    /// <summary>The action id for the yaw speed.</summary>
    public const int YawSpeed = 220226;
    /// <summary>The action id for center the view.</summary>
    public const int CenterView = 220230;

    /// <summary>The most moves that wait for acknowledgement.</summary>
    public const int MaxMoves = 30;

    /// <summary>The forward key rate (+0x3f0 to +0x404).</summary>
    public float Forward { get; set; }
    /// <summary>The backward key rate.</summary>
    public float Backward { get; set; }
    /// <summary>The left key rate.</summary>
    public float Left { get; set; }
    /// <summary>The right key rate.</summary>
    public float Right { get; set; }
    /// <summary>True while the jet key is held.</summary>
    public bool JetOn { get; set; }
    /// <summary>True while the crouch key is held.</summary>
    public bool CrouchOn { get; set; }
    /// <summary>True while the jump key is held.</summary>
    public bool JumpOn { get; set; }
    /// <summary>The item type of the use item action, -1 for none.</summary>
    public int UseItemType { get; set; } = -1;
    /// <summary>The accumulated turn of the move being built.</summary>
    public float TurnAcc { get; set; }
    /// <summary>The accumulated pitch of the move being built.</summary>
    public float PitchAcc { get; set; }
    /// <summary>The yaw speed key rate.</summary>
    public float YawSpeedKey { get; set; }
    /// <summary>The pitch speed key rate.</summary>
    public float PitchSpeedKey { get; set; }
    /// <summary>The turn left key rate.</summary>
    public float TurnLeftKey { get; set; }
    /// <summary>The turn right key rate.</summary>
    public float TurnRightKey { get; set; }
    /// <summary>The look up key rate.</summary>
    public float LookUpKey { get; set; }
    /// <summary>The look down key rate.</summary>
    public float LookDownKey { get; set; }
    /// <summary>The fire counter.</summary>
    public uint FireCounter { get; set; }
    /// <summary>The fire counter of the last sampled move.</summary>
    public uint SampledFire { get; set; }
    /// <summary>True for the third person view: not +0xa0 (first person), the VIEW toggle (0040706c, PlayerPSC_onActionDefault).</summary>
    public bool ThirdPerson { get; set; }
    /// <summary>The current field of view.</summary>
    public double CurrentFov { get; set; } = 90;
    /// <summary>The third person distance (+0x9c): 0 in first person and up to 4 m, eased at 15 m/s (4.2 step 1).</summary>
    public float ViewDist { get; set; }
    /// <summary>The largest third person distance.</summary>
    public const float MaxViewDist = 4f;
    /// <summary>True while the zoom is on (0060f0b0).</summary>
    public bool ZoomOn { get; set; }
    /// <summary>The field of view of the zoom (0060f0a4). It is 22.5 at start: the PlayerFov callback (004040b0) stores 90 * 0.25 in 0060f078 and the PSC constructor copies it. INC_SNIPER_FOV only changes this value and the view only follows it while the zoom is on.</summary>
    public double ZoomTarget { get; set; } = 22.5;
    /// <summary>The field of view the animation starts from (+0x408).</summary>
    public double FovFrom { get; set; } = 90;
    /// <summary>The field of view the animation ends at (+0x40c).</summary>
    public double FovTo { get; set; } = 90;
    /// <summary>The start time of the FOV animation in milliseconds (+0x414).</summary>
    public uint FovStart { get; set; }
    /// <summary>The end time of the FOV animation in milliseconds (+0x418).</summary>
    public uint FovEnd { get; set; }
    /// <summary>The time of the last INC_SNIPER_FOV while the zoom was off (0060f0b4). The crosshair shows the zoom box for 2 s after it.</summary>
    public uint ZoomStepStamp { get; set; }
    /// <summary>The client time (006a83a0) of the last frame.</summary>
    public uint LastNow { get; set; }
    /// <summary>Reads <c>$pref::PlayerZoomSpeed</c> (0060f0a8, clamped 0 to 1). The FOV animation lasts that value times 1000 ms (00406f47, 00406e06, 004077cd), and <see cref="CurrentFov"/> (0060f0a0) follows it at every frame.</summary>
    public Func<double> ZoomSpeed { get; set; } = () => 0;
    /// <summary>True for the first person view.</summary>
    public bool FirstPersonNow => !ThirdPerson && ViewDist == 0;
    /// <summary>Tells whether the control object may look (vtable +0x11c).</summary>
    public Func<bool> CanLook { get; set; } = () => true;
    /// <summary>Tells whether PITCH is reversed (flier with <c>$pref::flipFlierY</c>).</summary>
    public Func<bool> ReversePitch { get; set; } = () => false;
    /// <summary>Takes the pitch of the owned control Player (+0xfc4) and sets it to 0, for CENTERVIEW (PlayerPSC_onActionDefault 00406ce8, action 0x35c46). Returns null when there is no owned object.</summary>
    public Func<float?> TakeControlPitch { get; set; } = () => null;
    /// <summary>Reads <c>$pref::PlayerFov</c>.</summary>
    public Func<double> PrefFov { get; set; } = () => 90;

    private static float Clamp(float v, float lo, float hi) => Math.Min(Math.Max(v, lo), hi);

    // scaled(v) = clamp(v * currentFov / 90, -1, 1)
    private float Scaled(float v) => Clamp((float)(v * CurrentFov / 90.0), -1f, 1f);

    /// <summary>Applies an action of the play maps.</summary>
    /// <param name="id">The action id.</param>
    /// <param name="v">The action value.</param>
    /// <returns>True when the action is one of the rows of 4.1 and was consumed here.</returns>
    public bool OnAction(int id, float v)
    {
        switch (id)
        {
            case Fire1:
                FireCounter += (FireCounter & 1) == 0 ? 1u : 2u;
                return true;
            case Break1:
                FireCounter += (FireCounter & 1) == 1 ? 1u : 2u;
                return true;
            case MoveForward:
                Forward = Clamp(v, 0, 1);
                return true;
            case MoveBack:
                Backward = Clamp(v, 0, 1);
                return true;
            case MoveLeft:
                Left = Clamp(v, 0, 1);
                return true;
            case MoveRight:
                Right = Clamp(v, 0, 1);
                return true;
            case Run:
                if (v < -0.01f)
                { Backward = 1; Forward = 0; }
                else if (v > 0.01f)
                { Forward = 1; Backward = 0; }
                else
                { Forward = Backward = 0; }
                return true;
            case Strafe:
                if (v < -0.01f)
                { Right = 1; Left = 0; }
                else if (v > 0.01f)
                { Left = 1; Right = 0; }
                else
                { Left = Right = 0; }
                return true;
            case MoveUp:
                JumpOn = true;
                return true;
            case Jet:
                JetOn = v != 0;
                return true;
            case Crouch:
                CrouchOn = true;
                return true;
            case Stand:
                CrouchOn = false;
                return true;
            case ToggleCrouch:
                CrouchOn = !CrouchOn;
                return true;
            case UseItem:
                UseItemType = (int)v;
                return true;
            case Yaw:
                TurnAcc += Scaled(v) * (float)Math.PI;
                return true;
            case Pitch:
                if (CanLook())
                {
                    var d = Scaled(v) * (float)Math.PI;
                    PitchAcc += ReversePitch() ? -d : d;
                }
                return true;
            case LookUp:
                LookUpKey = Clamp(v, 0, 1);
                return true;
            case LookDown:
                LookDownKey = Clamp(v, 0, 1);
                return true;
            case TurnLeft:
                TurnLeftKey = Clamp(v, 0, 1);
                return true;
            case TurnRight:
                TurnRightKey = Clamp(v, 0, 1);
                return true;
            case PitchSpeed:
                PitchSpeedKey = Clamp(v, -1, 1) * (ReversePitch() ? -1 : 1);
                return true;
            case YawSpeed:
                YawSpeedKey = Clamp(v, -1, 1);
                return true;
            case CenterView:
                // The pending move pitch (+0x8c) drops by the player's pitch (when the object may look) and the player's pitch is 0 at
                // once; the move then brings the server's pitch to 0 as well.
                if (TakeControlPitch() is float held && CanLook())
                    PitchAcc -= held;
                return true;
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
                var cur = (int)ZoomTarget;
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
            default:
                return false;
        }
    }

    private void StartFovAnimation()
    {
        FovFrom = CurrentFov;
        FovStart = LastNow;
        FovEnd = (uint)(long)(LastNow + ((float)ZoomSpeed() * 1000f));
    }

    /// <summary>Updates the field of view of the camera getter (004077cd): inside the animation the value is interpolated linearly. After it the value snaps to 90 (third person), the zoom FOV (zoom on) or <c>$pref::PlayerFov</c>.</summary>
    /// <param name="now">The client time in milliseconds.</param>
    public void UpdateFov(uint now)
    {
        if (now < FovEnd)
            CurrentFov = (float)(((now - FovStart) / (double)(FovEnd - FovStart) * (FovTo - FovFrom)) + FovFrom);
        else
        {
            CurrentFov = ThirdPerson ? 90 : ZoomOn ? ZoomTarget : PrefFov();
            FovTo = CurrentFov;
        }
    }

    /// <summary>Reads the zoom state for the crosshair (00404578).</summary>
    /// <returns>The zoom state.</returns>
    public HudZoom HudZoomState() => new(CurrentFov, ZoomTarget, FovTo, FovStart, FovEnd, LastNow, ZoomStepStamp);

    /// <summary>Moves the view distance toward 0 in first person and toward 4 otherwise, 15 * dt * 0.001 per call (4.2 step 1).</summary>
    /// <param name="prev">The previous client time in milliseconds.</param>
    /// <param name="now">The new client time in milliseconds.</param>
    public void UpdateViewDist(uint prev, uint now)
    {
        var step = 15f * (now - prev) * 0.001f;
        var target = ThirdPerson ? MaxViewDist : 0f;
        ViewDist = ViewDist < target ? Math.Min(ViewDist + step, target) : Math.Max(ViewDist - step, target);
    }

    private Move Current(bool fire, float turn, float pitch) => new()
    {
        Forward = Forward,
        Backward = Backward,
        Left = Left,
        Right = Right,
        Jet = JetOn,
        Crouch = CrouchOn,
        Jump = JumpOn,
        Fire = fire,
        UseItem = UseItemType,
        Turn = turn,
        Pitch = pitch,
    };

    /// <summary>Appends the moves of the 32 ms boundaries crossed since the last frame to the PSC buffer (4.2). At most 30 wait for acknowledgement.</summary>
    /// <param name="psc">The client packet state.</param>
    /// <param name="prev">The previous client time in milliseconds.</param>
    /// <param name="now">The new client time in milliseconds.</param>
    /// <returns>The number of moves appended.</returns>
    public int Collect(ClientPsc psc, uint prev, uint now)
    {
        UpdateViewDist(prev, now);
        LastNow = now;
        UpdateFov(now);
        var n = (((long)now - 1) >> 5) - (((long)prev - 1) >> 5);
        if (n <= 0)
            return 0;
        var pitchKeys = PitchSpeedKey + LookUpKey - LookDownKey;
        var turnKeys = TurnLeftKey - YawSpeedKey - TurnRightKey;
        var made = 0;
        if (psc.Moves.Count < MaxMoves)
        {
            PitchAcc += pitchKeys;
            TurnAcc += turnKeys;
            var fire = (FireCounter & 1) == 1 || ((SampledFire & 1) == 0 && SampledFire != FireCounter);
            var m = ClientPrediction.Quantize(Current(fire, TurnAcc, PitchAcc));
            SampledFire = FireCounter;
            psc.AddMove(m);
            ++made;
        }
        var held = (FireCounter & 1) == 1;
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

    /// <summary>Resets the builder to its start state.</summary>
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
