using OpenTribes.Sim;
using OpenTribes.World;

namespace OpenTribes.Game;

// The eye (vtable +0x110) and muzzle (vtable +0x10c) transforms of a Vehicle, Flier or Turret control object, as the aim marker routine
// FUN_004ada90 reads them (docs/gui.md, crosshair aim markers). Both are in world space: the node transform times the object transform.
public static class ControlAim
{
    private static Transform TimesObject(in Transform local, in Transform obj)
    {
        var a = new TMat3 { Flags = local.Flags, M = local.M, P = local.P };
        var b = new TMat3 { Flags = obj.Flags, M = obj.M, P = obj.P };
        Shape.TmatMul(a, b, out TMat3 w);
        return new Transform { Flags = w.Flags, M = w.M, P = w.P };
    }

    // Vehicle_getEyeTransform 00445eac (Vehicle and Flier slot 0x110): the animated `dummy eye` node entry times the object transform
    // (m_mul_TMat3F 005ed2b2). Without the node (index +0x870 = -1) the identity times the object transform, which is the object transform.
    // Unlike the camera 00445d88 nothing moves it back and nothing scales its angles.
    public static Transform VehicleEye(in Transform obj, in Transform? eyeNode) => eyeNode is { } n ? TimesObject(n, obj) : obj;

    // The node index Vehicle_getMuzzleTransform 00445cd8 uses: the entry `fireCount % count` of the `dummy muzzle%d` table (+0x874, count
    // +0x8a4) when the table is not empty, else the single `dummy muzzle` node (+0x86c). -1 when there is none.
    public static int VehicleMuzzleNode(int single, IReadOnlyList<int> table, int fireCount) =>
        table.Count != 0 ? table[(int)((uint)fireCount % (uint)table.Count)] : single;

    // Vehicle_getMuzzleTransform 00445cd8: the muzzle node entry times the object transform, true. Without a node the routine copies the
    // object transform and reports false (the marker routine then uses the identity).
    public static bool VehicleMuzzle(in Transform obj, in Transform? muzzleNode, out Transform output)
    {
        if (muzzleNode is not { } n)
        {
            output = obj;
            return false;
        }
        output = TimesObject(n, obj);
        return true;
    }

    // The node transform of Turret_getEyeTransform 004566ac and Turret_getMuzzleTransform 004565e0: Euler (elevation, 0, rotation) at the
    // translation of the node (flag 2), times the object transform. Without the node the object transform.
    public static Transform TurretNode(in Transform obj, in Transform? node, float rotation, float elevation)
    {
        if (node is not { } n)
            return obj;
        var local = new Transform();
        MissionTransform.EulerToMatrix(new[] { elevation, 0.0f, rotation }, ref local);
        local.Flags |= 2u;
        for (int i = 0; i < 3; ++i)
            local.P[i] = n.P[i];
        return TimesObject(local, obj);
    }
}
