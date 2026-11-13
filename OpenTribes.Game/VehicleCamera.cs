using OpenTribes.Core;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

/// <summary>The third person camera of a vehicle or flier.</summary>
/// <remarks>Follows Vehicle_getCameraTransform (00445d88, slot 0x170; docs/client-net.md 4.2).</remarks>
public static class VehicleCamera
{
    /// <summary>The view distance factor (float at 0x00445e94). The camera argument is the PSC +0x9c timer, 0 to 4.</summary>
    public const float DistanceFactor = 2.0f;
    /// <summary>The scale of the pitch word (Euler x) of the third person transform (long double at 0x00445e9c).</summary>
    public const double PitchScale = 0.8;
    /// <summary>The scale of the roll word (Euler y) of the third person transform (float at 0x00445ea8).</summary>
    public const float RollScale = 0.5f;

    /// <summary>Computes the camera transform of a vehicle or flier.</summary>
    /// <param name="obj">The object transform (+0x94).</param>
    /// <param name="eye">The transform of the <c>dummy eye</c> node in object space, or null when the shape has none. Without it the camera is the object transform and the view distance is ignored.</param>
    /// <param name="arg">The camera argument (view distance timer).</param>
    /// <param name="container">The container used to keep the camera out of geometry.</param>
    /// <param name="exclude">The object ignored by that test.</param>
    /// <returns>The camera transform.</returns>
    /// <remarks>With an eye node: eye times object transform (m_mul_TMat3F 005ed2b2), then GameBase_placeBehind (00449604) by <c>2.0 * arg</c>. When <c>arg</c> is not 0 the Euler angles of the result are rebuilt with x * 0.8 and y * 0.5, keeping the position, with flag 2.</remarks>
    public static Transform Compute(in Transform obj, in Transform? eye, float arg, Container? container, object? exclude)
    {
        if (eye is not { } n)
            return obj;
        var a = new TMat3 { Flags = n.Flags, M = n.M, P = n.P };
        var b = new TMat3 { Flags = obj.Flags, M = obj.M, P = obj.P };
        Shape.TmatMul(a, b, out TMat3 w);
        var p = new float[] { w.P[0], w.P[1], w.P[2] };
        ObserverCamera.PlaceBehind(container, exclude, p, w.M[3], w.M[4], w.M[5], Store(Mul(arg, DistanceFactor)));
        var t = new Transform { Flags = w.Flags, M = w.M };
        for (int i = 0; i < 3; ++i)
            t.P[i] = p[i];
        if (arg != 0.0f)
        {
            var e = new float[3];
            MissionTransform.MatrixToEuler(t, e);
            e[0] = Store(Mul(PitchScale, e[0]));
            e[1] = Store(Mul(RollScale, e[1]));
            MissionTransform.EulerToMatrix(e, ref t);
            t.Flags |= 2u;
        }
        return t;
    }
}
