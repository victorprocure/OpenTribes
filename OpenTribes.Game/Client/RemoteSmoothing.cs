// Smoothing and look-ahead of remote Player and Vehicle ghosts, as Tribes 1.11 does it (docs/client-net.md 4.4.2).
// 1.11 reads $net::interpolateTime (0x6a8410, default 64) and $net::predictForwardTime (0x6a840c, default 0) in
//   Player_applyControlState 0x0041405c (reads at 0x004140f7 and 0x00414133) and
//   Vehicle_clientSetState   0x00445f68 (reads at 0x0044600d, 0x0044604f and 0x00446071).
// The stream state is applied to a simulated copy of the ghost (the object of the prediction world), stepped forward
// (predict + 32) / 32 ticks with the echoed move, and the drawn state then moves from the old to the new state over
// the window ending at (now + interpolate + 31) & ~31. Player_clientUpdate (0x00414c38) and Vehicle_clientUpdate
// (0x0044549c) advance the window each frame and then step the copy with the echoed move for the time left.
using OpenTribes.Core;
using OpenTribes.Sim;

namespace OpenTribes.Game;

// The window arithmetic of 1.11.
public static class SmoothWindow
{
    public const uint Tick = 32;
    public const int DefaultInterpolate = 64, DefaultPredict = 0;
    // The step loops are unsigned: a negative predict time would loop 2^27 times. The copy stops at this many ticks.
    public const int MaxTicks = 4096;

    // Tick count of the loops at 0x004140f7 and 0x0044600d: edi = predict + 32, esi = 0, while (esi < edi) { step; esi += 32 }.
    public static int Ticks(int predict)
    {
        uint limit = unchecked((uint)predict + 32u);
        ulong n = ((ulong)limit + 31u) / 32u;
        return (int)Math.Min(n, (ulong)MaxTicks);
    }

    // Player 0x00414133: a zero interpolation time or the snap flag of the update (flag10000) takes the snap path.
    public static bool PlayerSnap(int interpolate, bool snapFlag) => interpolate == 0 || snapFlag;

    // Vehicle 0x0044604f: only a zero interpolation time snaps.
    public static bool VehicleSnap(int interpolate) => interpolate == 0;

    // 0x0041414b and 0x00446071: (now + interpolate + 31) & ~31.
    public static uint End(uint now, int interpolate) => unchecked(now + (uint)interpolate + 31u) & ~31u;

    // The snap path: now & ~31.
    public static uint Floor(uint now) => now & ~31u;
}

// A remote Player ghost: the simulated copy and its smoothing state.
public sealed class RemotePlayer
{
    private const double Pi = 3.141592653589793, TwoPi = 6.283185307179586;

    public readonly Player P;
    public uint SimTime;                                   // +0x218
    public uint End;                                       // +0x1020
    public uint LastTime;                                  // +0x21c: the time of the last frame
    public readonly float[] PosRate = new float[3];        // +0x788: per ms in a window, per tick after a step
    public float YawRate, PitchRate;                       // +0x794, +0x798
    public long Steps;
    public int MountGhost = -1;                            // the ghost index of the mount object (+0x1028), -1 none
    public int MountPoint;                                 // +0x102c

    public RemotePlayer(Player p)
    {
        P = p;
        P.ClientSide = true;
        P.LocallyControlled = false;
    }

    // GameBase_onAdd 0x00448108 (registration, after the first unpack of a new ghost): +0x218 and +0x21c = the client time
    // (`mov edx,[0x6a8494]; mov eax,[edx+4]`, 0x6a8494 = &0x6a839c, +4 = 0x6a83a0), not rounded down to a tick. It replaces the
    // `now & ~31` of the unregistered paths in 0x0041405c and 0x00445f68.
    public void Register(uint now)
    {
        SimTime = now;
        LastTime = now;
    }

    private void ZeroRates()
    {
        Array.Clear(PosRate);
        YawRate = 0;
        PitchRate = 0;
    }

    // A ghost that is not registered yet (Player_unpackUpdate, object +0x44 clear): the state is set directly.
    public void Place(uint now, ReadOnlySpan<float> pos, ReadOnlySpan<float> vel, float yaw, float pitch, bool contact)
    {
        SimTime = SmoothWindow.Floor(now);
        End = 0;
        P.SetPosition(pos);
        for (int i = 0; i < 3; ++i)
            P.Velocity[i] = vel[i];
        P.AtRest = false;
        float[] rot = { P.Rotation[0], P.Rotation[1], yaw };
        P.SetRotation(rot);
        P.Pitch = pitch;
        P.ContactFlag = contact;
        ZeroRates();
    }

    // Player_applyControlState 0x0041405c.
    public void Update(uint now, in Move move, ReadOnlySpan<float> pos, ReadOnlySpan<float> vel, bool contact, float pitch, float yaw,
                       int ticksWithoutMove, bool snapFlag, int interpolate, int predict)
    {
        P.LastMove = move;                                 // +0xff0
        float oldYaw = P.Rotation[2], oldPitch = P.Pitch;
        float[] oldPos = { P.Transform.P[0], P.Transform.P[1], P.Transform.P[2] };
        for (int i = 0; i < 3; ++i)
            P.Velocity[i] = vel[i];                        // SimMovement_setLinearVelocity
        P.AtRest = false;
        P.SetPosition(pos);                                // vtable +0x19c
        P.ContactFlag = contact;
        P.Pitch = pitch;
        P.SetRotation(new[] { P.Rotation[0], P.Rotation[1], yaw });   // vtable +0x1a0
        End = 0;
        P.TicksWithoutMove = 0;
        int ticks = SmoothWindow.Ticks(predict);
        for (int k = 0; k < ticks; ++k)
        {
            P.UpdateMove(P.LastMove);
            ++Steps;
        }
        P.TicksWithoutMove = ticksWithoutMove;
        if (SmoothWindow.PlayerSnap(interpolate, snapFlag))
        {
            End = 0;
            SimTime = SmoothWindow.Floor(now);
            ZeroRates();
            return;
        }
        End = SmoothWindow.End(now, interpolate);
        float span = unchecked((uint)(End - SimTime));
        PitchRate = (P.Pitch - oldPitch) / span;
        P.Pitch = oldPitch;
        float dyaw = P.Rotation[2] - oldYaw;
        if (dyaw < -Pi)
            dyaw += (float)TwoPi;
        if (dyaw > Pi)
            dyaw -= (float)TwoPi;
        YawRate = dyaw / span;
        P.SetRotation(new[] { P.Rotation[0], P.Rotation[1], oldYaw });
        if (P.MountId != 0)
        {
            // Mounted: no position rates (+0x1e2.. keep their old values); the drawn transform is the mount point's with the old yaw.
            P.PlaceOnMount();
            return;
        }
        for (int i = 0; i < 3; ++i)
            PosRate[i] = (P.Transform.P[i] - oldPos[i]) / span;
        P.SetPosition(oldPos);
    }

    // FUN_00414a00: adds `f` times the rates to the drawn state (f in ms inside a window, in ticks after a step).
    private void Interpolate(float f)
    {
        float yaw = YawRate * f + P.Rotation[2];
        if (yaw < 0.0)
            yaw += (float)TwoPi;
        if (yaw > TwoPi)
            yaw -= (float)TwoPi;
        P.SetRotation(new[] { P.Rotation[0], P.Rotation[1], yaw });
        P.Pitch = PitchRate * f + P.Pitch;
        if (P.MountId != 0)
        {
            P.PlaceOnMount();                              // follows the mount node as it is now
            return;
        }
        Span<float> pos = stackalloc float[3];
        for (int i = 0; i < 3; ++i)
            pos[i] = PosRate[i] * f + P.Transform.P[i];
        P.PlaceRaw(pos);
    }

    // The step of the loop in Player_clientUpdate for a remote Player: the echoed move, deltas written by the step.
    private void Step()
    {
        Move m = P.LastMove;
        float[] before = { P.Transform.P[0], P.Transform.P[1], P.Transform.P[2] };
        float pitchBefore = P.Pitch;
        P.UpdateMove(m);
        ++Steps;
        for (int i = 0; i < 3; ++i)
            PosRate[i] = P.Transform.P[i] - before[i];
        YawRate = m.Turn;
        PitchRate = P.Pitch - pitchBefore;
    }

    // Player_clientUpdate 0x00414c38 for a Player that is not controlled and not dead.
    public void Advance(uint now)
    {
        if (SimTime == now)
            return;                                        // the caller skips objects whose +0x218 equals now (004eb9xx loop)
        if (P.Dead)
            P.ClientCorpseStep((float)((now - LastTime) * 0.001));    // 0x0041447c, before the window
        if (End != 0)
        {
            uint t = Math.Min(End, now);
            if (SimTime < t)
            {
                Interpolate(t - SimTime);
                SimTime = t;
            }
            else
                End = 0;
        }
        while (SimTime < now)
        {
            uint next = Math.Min((SimTime + 32) & ~31u, now);
            uint covered = ((next - 1) & 31) + 1;          // puVar10
            if ((SimTime & 31) == 0)
            {
                Step();
                if (covered != 32)
                    Interpolate(((int)covered - 32) * 0.03125f);
            }
            else
                Interpolate((int)(covered - (SimTime & 31)) * 0.03125f);
            SimTime = next;
        }
        LastTime = now;
    }
}

// A remote Vehicle or Flier ghost: the simulated copy and its smoothing state.
public sealed class RemoteVehicle
{
    private const double Pi = 3.141592653589793, TwoPi = 6.283185307179586;

    public readonly Vehicle V;
    public uint SimTime;                                   // +0x218
    public uint End;                                       // +0x8e0
    public readonly float[] PosRate = new float[3];        // +0x914, per ms
    public readonly float[] RotRate = new float[3];        // +0x908, per ms
    public long Steps;

    public RemoteVehicle(Vehicle v)
    {
        V = v;
        V.ClientSide = true;
    }

    // GameBase_onAdd 0x00448108: +0x218 = the client time (see RemotePlayer.Register).
    public void Register(uint now) => SimTime = now;

    private void ZeroRates()
    {
        Array.Clear(PosRate);
        Array.Clear(RotRate);
    }

    // The update without the move block: the state is set directly and the rates are cleared; the window is not touched.
    public void Place(ReadOnlySpan<float> rot, ReadOnlySpan<float> pos)
    {
        ZeroRates();
        V.SetRotation(rot);
        V.SetPosition(pos);
    }

    // Vehicle_clientSetState 0x00445f68. `speedF` goes to +0x940 and `speedL` to +0x898 for the forward steps only.
    public void SetState(uint now, in Move move, ReadOnlySpan<float> pos, ReadOnlySpan<float> rot, int ticksWithoutMove,
                         float speedL, float speedF, int interpolate, int predict)
    {
        V.LastMove = move;                                 // +0x8e4
        float oldF = V.SpeedF, oldL = V.SpeedL;
        V.SpeedF = speedF;
        V.SpeedL = speedL;
        float[] oldRot = { V.Rotation[0], V.Rotation[1], V.Rotation[2] };
        float[] oldPos = { V.Transform.P[0], V.Transform.P[1], V.Transform.P[2] };
        V.SetPosition(pos);
        V.SetRotation(rot);
        End = 0;
        V.TicksWithoutMove = 0;
        int ticks = SmoothWindow.Ticks(predict);
        for (int k = 0; k < ticks; ++k)
        {
            Move m = V.LastMove;
            V.Update(ref m, 0.032f);
            ++Steps;
        }
        V.TicksWithoutMove = ticksWithoutMove;
        if (SmoothWindow.VehicleSnap(interpolate))
        {
            End = 0;
            SimTime = SmoothWindow.Floor(now);
            ZeroRates();
        }
        else
        {
            SimTime = SmoothWindow.Floor(now);
            End = SmoothWindow.End(now, interpolate);
            float span = End - SimTime;
            for (int i = 0; i < 3; ++i)
                PosRate[i] = (V.Transform.P[i] - oldPos[i]) / span;
            V.SetPosition(oldPos);
            float[] cur = { V.Rotation[0], V.Rotation[1], V.Rotation[2] };
            for (int i = 0; i < 3; ++i)
            {
                float d = cur[i] - oldRot[i];
                if (d < -Pi)
                    d += (float)TwoPi;
                if (d > Pi)
                    d -= (float)TwoPi;
                RotRate[i] = d / span;
            }
            V.SetRotation(oldRot);
        }
        V.SpeedF = oldF;
        V.SpeedL = oldL;
    }

    // Vehicle_clientUpdate 0x0044549c for a Vehicle that is not the control object.
    public void Advance(uint now)
    {
        if (SimTime == now)
            return;                                        // skipped by the caller (+0x218 equals now)
        if (End != 0)
        {
            uint t = Math.Min(End, now);
            if (SimTime < t)
            {
                float dt = t - SimTime;
                V.SetRotation(new[] { RotRate[0] * dt + V.Rotation[0], RotRate[1] * dt + V.Rotation[1], RotRate[2] * dt + V.Rotation[2] });
                V.SetPosition(new[] { PosRate[0] * dt + V.Transform.P[0], PosRate[1] * dt + V.Transform.P[1], PosRate[2] * dt + V.Transform.P[2] });
                SimTime = t;
            }
            else
                End = 0;
        }
        while (SimTime < now)
        {
            uint next = Math.Min((SimTime + 32) & ~31u, now);
            uint ms = ((next - 1) & 31) + 1 - (SimTime & 31);
            Move m = V.LastMove;
            V.Update(ref m, (float)(ms * 0.001));
            SimTime = next;
        }
    }
}
