// Port of src/game/player_mount.cpp: Player mounting (docs/vehicle-sim.md 2.1 MT1, MT3, MT4; docs/player-sim.md 2.7).
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

public sealed partial class Player
{
    // src/game/player_mount.cpp Player::mountObject: the mount object, or null.
    public GameBase? MountObject()
    {
        SimManager? m = Manager();
        SimObject? o = m != null && MountId != 0 ? m.FindById(MountId) : null;
        return o as GameBase;
    }

    // src/game/player_mount.cpp Player::setMountObject: 0x0041268c.
    public void SetMountObject(GameBase? obj, int point)
    {
        using var chop = X87.Chop();
        SetMaskBits(0x8000);
        MountId = obj != null ? obj.Id() : 0;
        MountPoint = point;
        if (obj == null)
            return;
        obj.GetMountTransform(point, out Transform t);
        float[] e = { 0, 0, 0 };
        MissionTransform.MatrixToEuler(t, e);
        float yaw = e[2];
        if (point == 0)
        {
            // Only the translation, with the player's own rotation.
            MissionTransform.EulerToMatrix(Rotation, ref Transform);
            for (int i = 0; i < 3; ++i)
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

    // The mounted branch of Player_applyControlState 0x0041405c and of the interpolation FUN_00414a00 (client ghost): the transform
    // is the mount point's transform times the rotation (0, 0, yaw), set raw (no collision, the container entry follows). Same product
    // as the mounted step. False when there is no mount object.
    public bool PlaceOnMount()
    {
        GameBase? m = MountObject();
        if (m == null)
            return false;
        using var chop = X87.Chop();
        m.GetMountTransform(MountPoint, out Transform mt);
        var r = new TMat3();
        var mm = new TMat3();
        float[] yawOnly = { 0, 0, Rotation[2] };
        r.Flags = 0;
        Shape.EulerMatrix(yawOnly, ref r);
        mm.Flags = mt.Flags;
        mm.M = mt.M;
        mm.P = mt.P;
        Shape.TmatMul(r, mm, out TMat3 output);
        Transform.M = output.M;
        Transform.P = output.P;
        Transform.Flags = (output.Flags & 7u) | 3u;
        AtRest = false;
        RefileBox();
        return true;
    }

    // src/game/player_mount.cpp Player::mountedStep: 0x00415d26: the velocity and transform of the mount point, without
    // collision.
    public void MountedStep(Move mv)
    {
        using var chop = X87.Chop();
        GameBase? m = MountObject();
        if (m == null)
        {
            // The mount object is gone (the original clears the mount from its delete notification).
            SetMountObject(null, 0);
            return;
        }
        Velocity[0] = m.Velocity[0];
        Velocity[1] = m.Velocity[1];
        Velocity[2] = m.Velocity[2];
        m.GetMountTransform(MountPoint, out Transform mt);
        var r = new TMat3();
        var mm = new TMat3();
        float[] yawOnly = { 0, 0, Rotation[2] };
        r.Flags = 0;
        Shape.EulerMatrix(yawOnly, ref r);
        mm.Flags = mt.Flags;
        mm.M = mt.M;
        mm.P = mt.P;
        Shape.TmatMul(r, mm, out TMat3 output);
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
                string fn = Vehicle.FindVehicleMethod(mGame, v, "passengerJump", true);
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
