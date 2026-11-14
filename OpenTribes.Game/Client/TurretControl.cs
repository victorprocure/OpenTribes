// The client side of a Turret that is the control object of the player (a turret taken over from a command station: Client::takeControl
// in the stock scripts, Client::setControlObject with the turret).
// The stock client keeps no copy for it: the PSC block (Turret_readControlState 0045711c) writes the rotation (+0x8b4), the elevation
// (+0x8b8), the fire count, the state (+0x8cc) and the energy straight into the ghost, and Turret_clientUpdate 00456428 (vtable +0x154)
// replays the buffered moves of the client through Turret_updateAim 00456874 for the control turret, in the tick loop of the other
// control objects. A turret nobody controls gets updateAim with no move and the elapsed time (the ghost branch).
namespace OpenTribes.Game;

/// <summary>The datablock and shape values the aim step reads.</summary>
/// <param name="Speed">The TurretData speed (+0x114).</param>
/// <param name="SpeedModifier">The speed modifier (+0x118).</param>
/// <param name="Sustained">True for a sustained turret (isSustained, +0x150).</param>
/// <param name="MinElevation">The minimum elevation (+0x8c0).</param>
/// <param name="MaxElevation">The maximum elevation (+0x8bc).</param>
/// <remarks>Turret_onNewDataBlock (00452970) takes the elevation limits from the shape. The rotation limits stay 0 and 0 (no limit), as on the server (BindTurretShape).</remarks>
public readonly record struct TurretAimParams(float Speed, float SpeedModifier, bool Sustained, float MinElevation, float MaxElevation);

/// <summary>The client side of a Turret that is the control object of the player, such as a turret taken over from a command station.</summary>
/// <remarks>The stock client keeps no copy for it. The PSC block (Turret_readControlState 0045711c) writes the rotation (+0x8b4), the elevation (+0x8b8), the fire count, the state (+0x8cc) and the energy straight into the ghost. Turret_clientUpdate (00456428, vtable +0x154) replays the buffered moves through Turret_updateAim (00456874) for the control turret. A turret nobody controls gets updateAim with no move and the elapsed time (the ghost branch).</remarks>
public sealed class TurretControl
{
    private const float TwoPi = 6.2831855f;
    private const float Pi = 3.1415927f;

    /// <summary>True while a turret is the control object.</summary>
    public bool Active { get; set; }
    /// <summary>The ghost index of the turret, -1 for none.</summary>
    public int Ghost { get; set; } = -1;
    /// <summary>The rotation.</summary>
    public ref float Rotation => ref _rotation;
    private float _rotation;
    /// <summary>The elevation.</summary>
    public ref float Elevation => ref _elevation;
    private float _elevation;
    /// <summary>The energy.</summary>
    public float Energy { get; set; }
    /// <summary>The turret mode.</summary>
    public int Mode { get; set; }
    /// <summary>The fire count.</summary>
    public int FireCount { get; set; }
    /// <summary>True while the turret fires a beam.</summary>
    public bool Beam { get; set; }
    /// <summary>The time of the first buffered move (+0x218).</summary>
    public uint SimTime { get; set; }
    /// <summary>The number of steps run.</summary>
    public long Steps { get; set; }
    /// <summary>The datablock and shape values of the control ghost.</summary>
    public TurretAimParams Params { get; set; }
    /// <summary>Resolves the datablock and shape values of a control ghost. Returns null while they are not known, and the aim then stays at the server state.</summary>
    public Func<ClientGhost, TurretAimParams?>? Resolve { get; set; }

    /// <summary>Releases the control turret.</summary>
    public void Release()
    {
        Active = false;
        Ghost = -1;
    }

    /// <summary>Applies a server block: the aim is the server's and the moves still in the buffer run again.</summary>
    /// <param name="psc">The client PSC.</param>
    /// <param name="g">The control ghost.</param>
    /// <param name="receiveTime">The receive time in ms.</param>
    /// <remarks>Follows Turret_readControlState (0045711c) after <c>ClientPsc.ReadServerBlock</c>, and the +0x218 of the PSC reader (docs/client-net.md 4.4 step 3).</remarks>
    public void OnServerBlock(ClientPsc psc, ClientGhost g, uint receiveTime)
    {
        Ghost = g.Index;
        var s = psc.ControlState;
        Rotation = F(s, "aimRot");
        Elevation = F(s, "aimElev");
        FireCount = (int)L(s, "fire2");
        Beam = L(s, "beam") != 0;
        Mode = (int)L(s, "state4");
        Energy = F(s, "energy");
        if (Resolve?.Invoke(g) is { } p)
            Params = p;
        SimTime = unchecked((uint)((int)((receiveTime - 1) & ~31u) - ((psc.Moves.Count - 1) * 32)));
        Active = true;
    }

    /// <summary>Replays the buffered moves of the control turret for a frame.</summary>
    /// <param name="psc">The client PSC.</param>
    /// <param name="now">The current time in ms.</param>
    /// <returns>The ticks run.</returns>
    /// <remarks>Follows Turret_clientUpdate (00456428): ticks from +0x218 to <paramref name="now"/>. Every tick that has a buffered move (00404c04) runs updateAim with the elapsed milliseconds of the tick part.</remarks>
    public int Advance(ClientPsc psc, uint now)
    {
        if (!Active)
            return 0;
        var steps = 0;
        while ((int)(SimTime - now) < 0)
        {
            var next = Math.Min((SimTime + 32) & ~31u, now);
            if (ClientPrediction.MoveForTick(psc, SimTime, now, out var bm, out _))
            {
                Aim(ClientPrediction.ServerMove(bm), (float)((next - SimTime) * 0.001));
                ++steps;
            }
            SimTime = next;
        }
        Steps += steps;
        return steps;
    }

    /// <summary>Runs the move part of Turret_updateAim on a ghost.</summary>
    /// <param name="move">The move.</param>
    /// <param name="dt">The elapsed time in seconds.</param>
    /// <remarks>Follows Turret_updateAim (00456874). There is no fire and no jump script, as both are guarded by the ghost bit <c>+0x4c &amp; 2</c>. Only state 3 (+0x8cc) takes the move, and any other state drops it. Not sustained: <c>limit = speed * speedModifier</c>, <c>scale = dt * 16</c>. Sustained: <c>limit = speed * dt</c>, <c>scale = dt * 32</c>. The move turn (+0x18) drives the rotation and the pitch (+0x1c) the elevation.</remarks>
    public void Aim(in Move move, float dt)
    {
        if (Mode != 3)
            return;
        float lim, scale;
        if (!Params.Sustained)
        {
            lim = Params.Speed * Params.SpeedModifier;
            scale = dt * 16.0f;
        }
        else
        {
            lim = Params.Speed * dt;
            scale = dt * 32.0f;
        }
        float p = Clamp(move.Pitch, lim), y = Clamp(move.Turn, lim);
        Elevation = (p * scale) + Elevation;
        Rotation = (y * scale) + Rotation;
        WrapElevation(ref Elevation);                        // Turret_wrapElevation 0045317c
        if (Params.MaxElevation != Params.MinElevation)
        {
            if (Elevation > Params.MaxElevation)
                Elevation = Params.MaxElevation;
            if (Elevation < Params.MinElevation)
                Elevation = Params.MinElevation;
        }
        WrapRotation(ref Rotation);                          // Turret_wrapRotation 00453120
    }

    // The clamp of updateAim: the lower bound wins when the limit is negative, NaN falls to -limit.
    private static float Clamp(float v, float lim)
    {
        var r = -lim;
        if (r <= v)
        {
            r = v;
            if (lim < v)
                r = lim;
        }
        return r;
    }

    private static void WrapRotation(ref float r)
    {
        while (r > TwoPi)
            r -= TwoPi;
        while (r < 0.0f)
            r += TwoPi;
    }

    private static void WrapElevation(ref float e)
    {
        while (e < -Pi)
            e += TwoPi;
        while (e > Pi)
            e -= TwoPi;
    }

    private static float F(Dictionary<string, object?> s, string k) => s.TryGetValue(k, out var v) && v is double d ? (float)d : 0.0f;
    private static long L(Dictionary<string, object?> s, string k) => s.TryGetValue(k, out var v) && v is long l ? l : 0;
}
