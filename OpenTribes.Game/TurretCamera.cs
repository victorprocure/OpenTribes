using OpenTribes.Core;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

// Turret_getCameraTransform 004564fc (Turret slot 0x170).
public static class TurretCamera
{
    // double 0x004565d8: the factor of the view distance (the camera argument is the PSC +0x9c timer).
    public const double DistanceFactor = 1.3;

    // `obj` is the object transform (+0x94), `eyeNode` the translation of the `dummy eye` node of the animated shape (the +0x30 table, 0x34
    // bytes per node, position at +0x28), null when the shape has no such node (index +0x88c = -1: the camera is the object transform and
    // the distance is ignored). With a node: the rotation is Euler (elevation, 0, rotation) at the node position (flag 2), multiplied
    // by the object transform (m_mul_TMat3F 005ed2b2), then GameBase_placeBehind 00449604 by arg * 1.3. No pitch or roll scaling,
    // unlike the Vehicle camera.
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
