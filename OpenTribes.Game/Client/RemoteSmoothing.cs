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

/// <summary>The smoothing window arithmetic of 1.11.</summary>
public static class SmoothWindow
{
    /// <summary>The tick length in ms.</summary>
    public const uint Tick = 32;
    /// <summary>The default interpolation time in ms (<c>$net::interpolateTime</c>).</summary>
    public const int DefaultInterpolate = 64;
    /// <summary>The default predict forward time in ms (<c>$net::predictForwardTime</c>).</summary>
    public const int DefaultPredict = 0;
    /// <summary>The most ticks a copy steps. The step loops are unsigned, so a negative predict time would loop 2^27 times.</summary>
    public const int MaxTicks = 4096;

    /// <summary>Returns the tick count of the step loops.</summary>
    /// <param name="predict">The predict forward time in ms.</param>
    /// <returns>The tick count, as the loops at 0x004140f7 and 0x0044600d run: <c>edi = predict + 32, esi = 0, while (esi &lt; edi) { step; esi += 32 }</c>.</returns>
    public static int Ticks(int predict)
    {
        uint limit = unchecked((uint)predict + 32u);
        ulong n = ((ulong)limit + 31u) / 32u;
        return (int)Math.Min(n, (ulong)MaxTicks);
    }

    /// <summary>Tells whether a Player update takes the snap path.</summary>
    /// <param name="interpolate">The interpolation time in ms.</param>
    /// <param name="snapFlag">The snap flag of the update (flag10000).</param>
    /// <returns>True for a zero interpolation time or the snap flag.</returns>
    /// <remarks>Follows Player 0x00414133.</remarks>
    public static bool PlayerSnap(int interpolate, bool snapFlag) => interpolate == 0 || snapFlag;

    /// <summary>Tells whether a Vehicle update takes the snap path.</summary>
    /// <param name="interpolate">The interpolation time in ms.</param>
    /// <returns>True for a zero interpolation time.</returns>
    /// <remarks>Follows Vehicle 0x0044604f.</remarks>
    public static bool VehicleSnap(int interpolate) => interpolate == 0;

    /// <summary>Returns the end of the smoothing window: <c>(now + interpolate + 31) &amp; ~31</c>.</summary>
    /// <param name="now">The current time in ms.</param>
    /// <param name="interpolate">The interpolation time in ms.</param>
    /// <returns>The end time.</returns>
    /// <remarks>Follows 0x0041414b and 0x00446071.</remarks>
    public static uint End(uint now, int interpolate) => unchecked(now + (uint)interpolate + 31u) & ~31u;

    /// <summary>Returns the time of the snap path, <c>now &amp; ~31</c>.</summary>
    /// <param name="now">The current time in ms.</param>
    /// <returns>The time rounded down to a tick.</returns>
    public static uint Floor(uint now) => now & ~31u;
}

/// <summary>A remote Player ghost: the simulated copy and its smoothing state.</summary>
public sealed class RemotePlayer
{
    private const double Pi = 3.141592653589793, TwoPi = 6.283185307179586;

    /// <summary>The simulated Player copy.</summary>
    public readonly Player P;
    /// <summary>The time the copy is simulated up to (+0x218).</summary>
    public uint SimTime;
    /// <summary>The end of the smoothing window (+0x1020).</summary>
    public uint End;
    /// <summary>The time of the last frame (+0x21c).</summary>
    public uint LastTime;
    /// <summary>The position rate (+0x788): per ms in a window, per tick after a step.</summary>
    public readonly float[] PosRate = new float[3];
    /// <summary>The yaw rate (+0x794).</summary>
    public float YawRate;
    /// <summary>The pitch rate (+0x798).</summary>
    public float PitchRate;
    /// <summary>The number of steps run.</summary>
    public long Steps;
    /// <summary>The ghost index of the mount object (+0x1028), -1 for none.</summary>
    public int MountGhost = -1;
    /// <summary>The mount point (+0x102c).</summary>
    public int MountPoint;

    /// <summary>Creates the smoothing state of a Player copy.</summary>
    /// <param name="p">The Player copy.</param>
    public RemotePlayer(Player p)
    {
        P = p;
        P.ClientSide = true;
        P.LocallyControlled = false;
    }

    /// <summary>Registers the copy after the first unpack of a new ghost.</summary>
    /// <param name="now">The client time in ms.</param>
    /// <remarks>Follows GameBase_onAdd (0x00448108): +0x218 and +0x21c become the client time (<c>mov edx,[0x6a8494]; mov eax,[edx+4]</c>, 0x6a8494 = &amp;0x6a839c, +4 = 0x6a83a0), not rounded down to a tick. It replaces the <c>now &amp; ~31</c> of the unregistered paths in 0x0041405c and 0x00445f68.</remarks>
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

    /// <summary>Sets the state directly for a ghost that is not registered yet (Player_unpackUpdate, object +0x44 clear).</summary>
    /// <param name="now">The current time in ms.</param>
    /// <param name="pos">The position.</param>
    /// <param name="vel">The velocity.</param>
    /// <param name="yaw">The yaw.</param>
    /// <param name="pitch">The pitch.</param>
    /// <param name="contact">True on contact.</param>
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

    /// <summary>Applies the control state of an update: steps the copy forward and sets the smoothing window.</summary>
    /// <param name="now">The current time in ms.</param>
    /// <param name="move">The echoed move.</param>
    /// <param name="pos">The position.</param>
    /// <param name="vel">The velocity.</param>
    /// <param name="contact">True on contact.</param>
    /// <param name="pitch">The pitch.</param>
    /// <param name="yaw">The yaw.</param>
    /// <param name="ticksWithoutMove">The ticks without a move.</param>
    /// <param name="snapFlag">The snap flag of the update.</param>
    /// <param name="interpolate">The interpolation time in ms.</param>
    /// <param name="predict">The predict forward time in ms.</param>
    /// <remarks>Follows Player_applyControlState (0x0041405c).</remarks>
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

    /// <summary>Advances a Player that is not controlled and not dead by one frame.</summary>
    /// <param name="now">The current time in ms.</param>
    /// <remarks>Follows Player_clientUpdate (0x00414c38).</remarks>
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

/// <summary>A remote Vehicle or Flier ghost: the simulated copy and its smoothing state.</summary>
public sealed class RemoteVehicle
{
    private const double Pi = 3.141592653589793, TwoPi = 6.283185307179586;

    /// <summary>The simulated Vehicle copy.</summary>
    public readonly Vehicle V;
    /// <summary>The time the copy is simulated up to (+0x218).</summary>
    public uint SimTime;
    /// <summary>The end of the smoothing window (+0x8e0).</summary>
    public uint End;
    /// <summary>The position rate per ms (+0x914).</summary>
    public readonly float[] PosRate = new float[3];
    /// <summary>The rotation rate per ms (+0x908).</summary>
    public readonly float[] RotRate = new float[3];
    /// <summary>The number of steps run.</summary>
    public long Steps;

    /// <summary>Creates the smoothing state of a Vehicle copy.</summary>
    /// <param name="v">The Vehicle copy.</param>
    public RemoteVehicle(Vehicle v)
    {
        V = v;
        V.ClientSide = true;
    }

    /// <summary>Registers the copy: +0x218 becomes the client time (see <see cref="RemotePlayer.Register"/>).</summary>
    /// <param name="now">The client time in ms.</param>
    /// <remarks>Follows GameBase_onAdd (0x00448108).</remarks>
    public void Register(uint now) => SimTime = now;

    private void ZeroRates()
    {
        Array.Clear(PosRate);
        Array.Clear(RotRate);
    }

    /// <summary>Applies an update without the move block: the state is set directly and the rates are cleared. The window is not touched.</summary>
    /// <param name="rot">The rotation.</param>
    /// <param name="pos">The position.</param>
    public void Place(ReadOnlySpan<float> rot, ReadOnlySpan<float> pos)
    {
        ZeroRates();
        V.SetRotation(rot);
        V.SetPosition(pos);
    }

    /// <summary>Applies the state of an update.</summary>
    /// <param name="now">The current time in ms.</param>
    /// <param name="move">The echoed move.</param>
    /// <param name="pos">The position.</param>
    /// <param name="rot">The rotation.</param>
    /// <param name="ticksWithoutMove">The ticks without a move.</param>
    /// <param name="speedL">The lift speed, stored at +0x898 for the forward steps only.</param>
    /// <param name="speedF">The forward speed, stored at +0x940 for the forward steps only.</param>
    /// <param name="interpolate">The interpolation time in ms.</param>
    /// <param name="predict">The predict forward time in ms.</param>
    /// <remarks>Follows Vehicle_clientSetState (0x00445f68).</remarks>
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

    /// <summary>Advances a Vehicle that is not the control object by one frame.</summary>
    /// <param name="now">The current time in ms.</param>
    /// <remarks>Follows Vehicle_clientUpdate (0x0044549c).</remarks>
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
