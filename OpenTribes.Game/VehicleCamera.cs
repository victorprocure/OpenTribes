using OpenTribes.Core;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

// src/game/vehicle_camera.cpp: Vehicle_getCameraTransform 00445d88 (the Vehicle and Flier slot 0x170; docs/client-net.md 4.2).
public static class VehicleCamera
{
    // float 0x00445e94: the view distance factor (the camera argument is the PSC +0x9c timer, 0..4).
    public const float DistanceFactor = 2.0f;
    // long double 0x00445e9c: scale of the pitch word (Euler x) of the third person transform.
    public const double PitchScale = 0.8;
    // float 0x00445ea8: scale of the roll word (Euler y).
    public const float RollScale = 0.5f;

    // `obj` is the object transform (+0x94), `eye` the transform of the `dummy eye` node in object space (+0x328 -> +0x30 table, 0x34 bytes
    // per node), null when the shape has no such node (index -1: the camera is the object transform and the view distance is ignored).
    // With an eye node: eye x obj (m_mul_TMat3F 005ed2b2), then GameBase_placeBehind 00449604 by 2.0 * arg, then, when arg is not 0,
    // the Euler angles of the result with x * 0.8 and y * 0.5 (the position is kept) and flag 2.
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
