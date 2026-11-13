using OpenTribes.Sim;
using OpenTribes.World;

namespace OpenTribes.Game;

/// <summary>The eye and muzzle transforms of a vehicle, flier or turret control object, in world space.</summary>
/// <remarks>Both are the node transform times the object transform, as the aim marker routine FUN_004ada90 reads them through vtable +0x110 (eye) and +0x10c (muzzle); see docs/gui.md, crosshair aim markers.</remarks>
public static class ControlAim
{
    private static Transform TimesObject(in Transform local, in Transform obj)
    {
        var a = new TMat3 { Flags = local.Flags, M = local.M, P = local.P };
        var b = new TMat3 { Flags = obj.Flags, M = obj.M, P = obj.P };
        Shape.TmatMul(a, b, out TMat3 w);
        return new Transform { Flags = w.Flags, M = w.M, P = w.P };
    }

    /// <summary>Returns the eye transform of a vehicle or flier.</summary>
    /// <param name="obj">The object transform.</param>
    /// <param name="eyeNode">The animated <c>dummy eye</c> node transform, or null when the shape has none.</param>
    /// <returns>The node times the object transform, or the object transform without a node.</returns>
    /// <remarks>Follows Vehicle_getEyeTransform (00445eac, slot 0x110; node index +0x870). Unlike the camera (00445d88) nothing moves it back or scales its angles.</remarks>
    public static Transform VehicleEye(in Transform obj, in Transform? eyeNode) => eyeNode is { } n ? TimesObject(n, obj) : obj;

    /// <summary>Picks the node index the vehicle muzzle uses.</summary>
    /// <param name="single">The index of the single <c>dummy muzzle</c> node (+0x86c), or -1.</param>
    /// <param name="table">The indices of the <c>dummy muzzle%d</c> nodes (+0x874, count +0x8a4).</param>
    /// <param name="fireCount">The fire counter.</param>
    /// <returns>The table entry <c>fireCount % count</c> when the table is not empty, else <paramref name="single"/>.</returns>
    /// <remarks>Follows Vehicle_getMuzzleTransform (00445cd8).</remarks>
    public static int VehicleMuzzleNode(int single, IReadOnlyList<int> table, int fireCount) =>
        table.Count != 0 ? table[(int)((uint)fireCount % (uint)table.Count)] : single;

    /// <summary>Computes the muzzle transform of a vehicle.</summary>
    /// <param name="obj">The object transform.</param>
    /// <param name="muzzleNode">The muzzle node transform, or null.</param>
    /// <param name="output">Receives the muzzle transform, or a copy of the object transform without a node.</param>
    /// <returns>True when a node was used. False makes the marker routine use the identity.</returns>
    /// <remarks>Follows Vehicle_getMuzzleTransform (00445cd8).</remarks>
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

    /// <summary>Computes the eye or muzzle node transform of a turret.</summary>
    /// <param name="obj">The object transform.</param>
    /// <param name="node">The node translation, or null when the shape has no such node.</param>
    /// <param name="rotation">The turret rotation.</param>
    /// <param name="elevation">The turret elevation.</param>
    /// <returns>Euler (elevation, 0, rotation) at the node translation (flag 2) times the object transform, or the object transform without a node.</returns>
    /// <remarks>Follows Turret_getEyeTransform (004566ac) and Turret_getMuzzleTransform (004565e0).</remarks>
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
