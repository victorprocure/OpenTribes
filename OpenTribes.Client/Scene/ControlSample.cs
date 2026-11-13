namespace OpenTribes.Client.Scene;

using OpenTribes.Game;

/// <summary>What the client moves of the control player each frame: position, velocity, ground contact and facing.</summary>
/// <param name="X">The x position.</param>
/// <param name="Y">The y position.</param>
/// <param name="Z">The z position.</param>
/// <param name="Vx">The x velocity.</param>
/// <param name="Vy">The y velocity.</param>
/// <param name="Vz">The z velocity.</param>
/// <param name="Contact">True when the player stands on the ground.</param>
/// <param name="Jetting">True while the jets fire.</param>
/// <param name="Crouch">True while the player crouches.</param>
/// <param name="Yaw">The heading, rotZ.</param>
/// <remarks>The ghost stream does not update the visual of the control player (the control state and the prediction move it), so the client feeds it this sample. The stock client moves one object, so animation, footsteps, jet and image sounds follow the moving player (00414c38, 0041aea0).</remarks>
public readonly record struct ControlSample(double X, double Y, double Z, double Vx, double Vy, double Vz, bool Contact, bool Jetting,
                                            bool Crouch, double Yaw)
{
    /// <summary>Reads the sample from the control state of the stream (no prediction).</summary>
    /// <param name="psc">The client packet state.</param>
    /// <param name="ghost">The ghost index to sample.</param>
    /// <returns>The sample, or null for another ghost or a control state without position.</returns>
    public static ControlSample? FromControlState(ClientPsc psc, int ghost)
    {
        if (ghost != psc.ControlGhost || psc.ControlClass != "Player" || psc.ControlState.GetValueOrDefault("pos") is not double[] { Length: >= 3 } p)
            return null;
        var v = psc.ControlState.GetValueOrDefault("vel") is double[] { Length: >= 3 } vv ? vv : new double[3];
        bool Flag(string k) => psc.ControlState.GetValueOrDefault(k) is long l && l != 0;
        var yaw = psc.ControlState.GetValueOrDefault("rotZ") is double y ? y : 0;
        return new ControlSample(p[0], p[1], p[2], v[0], v[1], v[2], Flag("flag1f4"), Flag("flagfe4"), Flag("crouchfee"), yaw);
    }
}
