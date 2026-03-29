// What the client moves of the control player each frame: the ghost visual of that player is not updated by the ghost stream
// (the control state and the prediction move it), so its position, velocity and ground contact would stay at the first update. The
// stock client moves one object, so animation, footsteps, jet and image sounds follow the moving player [code 00414c38, 0041aea0].
using OpenTribes.Game;

namespace OpenTribes.Client.Scene;

public readonly record struct ControlSample(double X, double Y, double Z, double Vx, double Vy, double Vz, bool Contact, bool Jetting,
                                            bool Crouch, double Yaw)
{
    // The sample of the control state of the stream (no prediction): null for another ghost or a control state without position.
    public static ControlSample? FromControlState(ClientPsc psc, int ghost)
    {
        if (ghost != psc.ControlGhost || psc.ControlClass != "Player" || psc.ControlState.GetValueOrDefault("pos") is not double[] { Length: >= 3 } p)
            return null;
        double[] v = psc.ControlState.GetValueOrDefault("vel") is double[] { Length: >= 3 } vv ? vv : new double[3];
        bool Flag(string k) => psc.ControlState.GetValueOrDefault(k) is long l && l != 0;
        double yaw = psc.ControlState.GetValueOrDefault("rotZ") is double y ? y : 0;
        return new ControlSample(p[0], p[1], p[2], v[0], v[1], v[2], Flag("flag1f4"), Flag("flagfe4"), Flag("crouchfee"), yaw);
    }
}
