using OpenTribes.Core;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

/// <summary>The third person camera of a turret.</summary>
/// <remarks>Follows Turret_getCameraTransform (004564fc, Turret slot 0x170).</remarks>
public static class TurretCamera
{
    /// <summary>The factor of the view distance (double at 0x004565d8). The camera argument is the PSC +0x9c timer.</summary>
    public const double DistanceFactor = 1.3;

    /// <summary>Computes the camera transform of a turret.</summary>
    /// <param name="obj">The object transform (+0x94).</param>
    /// <param name="eyeNode">The translation of the <c>dummy eye</c> node of the animated shape, or null when the shape has none. Without it the camera is the object transform and the distance is ignored.</param>
    /// <param name="rotation">The turret rotation.</param>
    /// <param name="elevation">The turret elevation.</param>
    /// <param name="arg">The camera argument (view distance timer).</param>
    /// <param name="container">The container used to keep the camera out of geometry.</param>
    /// <param name="exclude">The object ignored by that test.</param>
    /// <returns>The camera transform.</returns>
    /// <remarks>With a node: Euler (elevation, 0, rotation) at the node position (flag 2), multiplied by the object transform (m_mul_TMat3F 005ed2b2), then GameBase_placeBehind (00449604) by <c>arg * 1.3</c>. Unlike the vehicle camera there is no pitch or roll scaling.</remarks>
    public static Transform Compute(in Transform obj, in Transform? eyeNode, float rotation, float elevation, float arg, Container? container, object? exclude)
    {
        if (eyeNode is not { } node)
            return obj;
        var local = new Transform();
        MissionTransform.EulerToMatrix(new[] { elevation, 0.0f, rotation }, ref local);
        local.Flags |= 2u;
        for (int i = 0; i < 3; ++i)
            local.P[i] = node.P[i];
        var a = new TMat3 { Flags = local.Flags, M = local.M, P = local.P };
        var b = new TMat3 { Flags = obj.Flags, M = obj.M, P = obj.P };
        Shape.TmatMul(a, b, out TMat3 w);
        var p = new float[] { w.P[0], w.P[1], w.P[2] };
        ObserverCamera.PlaceBehind(container, exclude, p, w.M[3], w.M[4], w.M[5], Store(Mul(arg, DistanceFactor)));
        var t = new Transform { Flags = w.Flags, M = w.M };
        for (int i = 0; i < 3; ++i)
            t.P[i] = p[i];
        return t;
    }
}
