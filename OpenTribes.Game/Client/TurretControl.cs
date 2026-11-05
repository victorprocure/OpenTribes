// The client side of a Turret that is the control object of the player (a turret taken over from a command station: Client::takeControl
// in the stock scripts, Client::setControlObject with the turret).
// The stock client keeps no copy for it: the PSC block (Turret_readControlState 0045711c) writes the rotation (+0x8b4), the elevation
// (+0x8b8), the fire count, the state (+0x8cc) and the energy straight into the ghost, and Turret_clientUpdate 00456428 (vtable +0x154)
// replays the buffered moves of the client through Turret_updateAim 00456874 for the control turret, in the tick loop of the other
// control objects. A turret nobody controls gets updateAim with no move and the elapsed time (the ghost branch).
using OpenTribes.Net;

namespace OpenTribes.Game;

// The datablock and shape values the aim step reads: TurretData speed (+0x114), speedModifier (+0x118), isSustained (+0x150) and the
// elevation limits (+0x8bc max, +0x8c0 min) that Turret_onNewDataBlock 00452970 takes from the shape. The rotation limits stay
// 0 and 0 (no limit), as on the server (BindTurretShape).
public readonly record struct TurretAimParams(float Speed, float SpeedModifier, bool Sustained, float MinElevation, float MaxElevation);

public sealed class TurretControl
{
    private const float TwoPi = 6.2831855f;
    private const float Pi = 3.1415927f;

    public bool Active;
    public int Ghost = -1;
    public float Rotation, Elevation, Energy;
    public int Mode, FireCount;
    public bool Beam;
    public uint SimTime;                 // +0x218, the time of the first buffered move
    public long Steps;
    public TurretAimParams Params;
    // The datablock and shape values of the control ghost, null while they are not known (the aim then stays at the server state).
    public Func<ClientGhost, TurretAimParams?>? Resolve;

    public void Release()
    {
        Active = false;
        Ghost = -1;
    }

    // Turret_readControlState 0045711c after ClientPsc.ReadServerBlock, and the +0x218 of the PSC reader: the aim is the server's and
    // the moves still in the buffer run again (step 3 of client-net.md 4.4).
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
        SimTime = unchecked((uint)((int)((receiveTime - 1) & ~31u) - (psc.Moves.Count - 1) * 32));
        Active = true;
    }

    // Turret_clientUpdate 00456428 for the control turret: ticks from +0x218 to `now`; every tick that has a buffered move (00404c04)
    // runs updateAim with the elapsed milliseconds of the tick part.
    public int Advance(ClientPsc psc, uint now)
    {
        if (!Active)
            return 0;
        int steps = 0;
        while ((int)(SimTime - now) < 0)
        {
            uint next = Math.Min((SimTime + 32) & ~31u, now);
            if (ClientPrediction.MoveForTick(psc, SimTime, now, out Move bm, out _))
            {
                Aim(ClientPrediction.ServerMove(bm), (float)((next - SimTime) * 0.001));
                ++steps;
            }
            SimTime = next;
        }
        Steps += steps;
        return steps;
    }

    // The move part of Turret_updateAim 00456874 on a ghost (no fire, no jump script: both are guarded by the ghost bit +0x4c & 2). Only in
    // state 3 (+0x8cc), the move is dropped in any other state. Not sustained: limit = speed * speedModifier, scale = dt * 16; sustained:
    // limit = speed * dt, scale = dt * 32. The move turn (+0x18) drives the rotation, the pitch (+0x1c) the elevation.
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
        Elevation = p * scale + Elevation;
        Rotation = y * scale + Rotation;
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
        float r = -lim;
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

    private static float F(Dictionary<string, object?> s, string k) => s.TryGetValue(k, out object? v) && v is double d ? (float)d : 0.0f;
    private static long L(Dictionary<string, object?> s, string k) => s.TryGetValue(k, out object? v) && v is long l ? l : 0;
}
