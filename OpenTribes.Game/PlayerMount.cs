// Port of src/game/player_mount.cpp: Player mounting (docs/vehicle-sim.md 2.1 MT1, MT3, MT4; docs/player-sim.md 2.7).
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

public sealed partial class Player
{
    /// <summary>Returns the object the player is mounted on.</summary>
    /// <returns>The mount object, or null.</returns>
    public GameBase? MountObject()
    {
        var m = Manager();
        var o = m != null && MountId != 0 ? m.FindById(MountId) : null;
        return o as GameBase;
    }

    /// <summary>Mounts the player on an object at a mount point, or unmounts.</summary>
    /// <param name="obj">The object to mount, or null to unmount.</param>
    /// <param name="point">The mount point index.</param>
    /// <remarks>Follows Player::setMountObject (0x0041268c).</remarks>
    public void SetMountObject(GameBase? obj, int point)
    {
        using var chop = X87.Chop();
        SetMaskBits(0x8000);
        MountId = obj != null ? obj.Id() : 0;
        MountPoint = point;
        if (obj == null)
            return;
        obj.GetMountTransform(point, out var t);
        float[] e = [0, 0, 0];
        MissionTransform.MatrixToEuler(t, e);
        var yaw = e[2];
        if (point == 0)
        {
            // Only the translation, with the player's own rotation.
            MissionTransform.EulerToMatrix(Rotation, ref Transform);
            for (var i = 0; i < 3; ++i)
                Transform.P[i] = t.P[i];
            Transform.Flags |= 3;
        }
        else
        {
            Transform = t;
            Transform.Flags |= 3;
        }
        if (point <= 0)
        {
            Rotation[0] = Rotation[1] = 0;
            Rotation[2] = yaw;
        }
        else if (point == 1)
        {
            Rotation[0] = Rotation[1] = Rotation[2] = 0;
        }
        else
        {
            Rotation[2] = X87.F32(Sub(Rotation[2], yaw));
        }
        TriggerImage(0, false);
        Velocity[0] = Velocity[1] = Velocity[2] = 0;
        UpdateContainer();
    }

    /// <summary>Places the player on its mount point, with the rotation (0, 0, yaw).</summary>
    /// <returns>False when there is no mount object.</returns>
    /// <remarks>The mounted branch of Player_applyControlState (0x0041405c) and of the client ghost interpolation FUN_00414a00. The transform is set raw, with no collision, and the container entry follows. Same product as the mounted step.</remarks>
    public bool PlaceOnMount()
    {
        var m = MountObject();
        if (m == null)
            return false;
        using var chop = X87.Chop();
        m.GetMountTransform(MountPoint, out var mt);
        var r = new TMat3();
        var mm = new TMat3();
        float[] yawOnly = [0, 0, Rotation[2]];
        r.Flags = 0;
        Shape.EulerMatrix(yawOnly, ref r);
        mm.Flags = mt.Flags;
        mm.M = mt.M;
        mm.P = mt.P;
        Shape.TmatMul(r, mm, out var output);
        Transform.M = output.M;
        Transform.P = output.P;
        Transform.Flags = (output.Flags & 7u) | 3u;
        AtRest = false;
        RefileBox();
        return true;
    }

    /// <summary>Runs one tick of a mounted player: takes the velocity and transform of the mount point without collision.</summary>
    /// <param name="mv">The move of the tick.</param>
    /// <remarks>Follows Player::mountedStep (0x00415d26).</remarks>
    public void MountedStep(Move mv)
    {
        using var chop = X87.Chop();
        var m = MountObject();
        if (m == null)
        {
            // The mount object is gone (the original clears the mount from its delete notification).
            SetMountObject(null, 0);
            return;
        }
        Velocity[0] = m.Velocity[0];
        Velocity[1] = m.Velocity[1];
        Velocity[2] = m.Velocity[2];
        m.GetMountTransform(MountPoint, out var mt);
        var r = new TMat3();
        var mm = new TMat3();
        float[] yawOnly = [0, 0, Rotation[2]];
        r.Flags = 0;
        Shape.EulerMatrix(yawOnly, ref r);
        mm.Flags = mt.Flags;
        mm.M = mt.M;
        mm.P = mt.P;
        Shape.TmatMul(r, mm, out var output);
        Transform.M = output.M;
        Transform.P = output.P;
        Transform.Flags = (output.Flags & 7u) | 3u;
        ContactFlag = false;
        UpdateContainer();
        SetMaskBits(0x2000);
        if (mv.Jump && !Dead && !ClientSide)
        {
            // MT4 (server only, 0x00415304): the vehicle's passengerJump(vehicle, player, "up row"); the map name takes part in the lookup.
            if (Vehicle.From(m) is Vehicle v)
            {
                var fn = Vehicle.FindVehicleMethod(mGame, v, "passengerJump", true);
                if (fn.Length != 0 && mGame.Console != null)
                {
                    static string G(float f) => Numbers.FormatFloat(f);
                    mGame.Console.CallFunction(fn, new[]
                    {
                        Numbers.FormatInt(v.Id()), Numbers.FormatInt(Id()),
                        G(Transform.M[6]) + " " + G(Transform.M[7]) + " " + G(Transform.M[8]),
                    });
                }
            }
        }
    }
}
