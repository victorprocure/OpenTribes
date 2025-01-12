// Port of the transform part of src/game/mission_objects.hpp and src/game/mission_objects.cpp (namespace ot::sim): the
// Transform struct and the Euler conversions of the original (docs/mission.md 5.1). The rest of mission_objects.cpp
// (persist fields, GameBase, TeamGroup, registerMissionClasses) is stage P4. Portable form: asinl and atan2l are
// Math.Asin and Math.Atan2, the float differences and products follow the mode of Fpu, float stores are Fpu.Store.
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Sim;

// src/game/mission_objects.hpp struct Transform.
public struct Transform
{
    public uint Flags; // bit 0 rotation, bit 1 translation
    public Float9 M;
    public Float3 P;

    public Transform()
    {
        Flags = 0;
        M = Float9.Identity();
        P = default;
    }
}

public static class MissionTransform
{
    // src/game/mission_objects.cpp eulerToMatrix: m_mat3_setEuler (0x005ed42e), x87 fsincos and the product grouping of
    // the original (world::eulerMatrix).
    public static void EulerToMatrix(ReadOnlySpan<float> e, ref Transform t)
    {
        var m = new TMat3();
        m.Flags = t.Flags;
        Shape.EulerMatrix(e, ref m);
        t.Flags = m.Flags;
        t.M = m.M;
    }

    // src/game/mission_objects.cpp matrixToEuler.
    public static void MatrixToEuler(in Transform t, Span<float> e)
    {
        if ((t.Flags & 1) == 0)
        {
            e[0] = e[1] = e[2] = 0;
            return;
        }
        ReadOnlySpan<float> m = t.M;
        e[0] = Store(Math.Asin(m[5]));
        float a = SubF(m[5], 1.0f), b = AddF(m[5], 1.0f);
        if (MulF(a, a) < 1e-12f || MulF(b, b) < 1e-12f)
        {
            e[1] = 0;
            e[2] = Store(Math.Atan2(m[1], m[0]));
        }
        else
        {
            e[1] = Store(Math.Atan2(-m[2], m[8]));
            e[2] = Store(Math.Atan2(-m[3], m[4]));
        }
    }
}
