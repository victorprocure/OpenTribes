namespace OpenTribes.Sim;

using OpenTribes.World;
using static OpenTribes.Core.Fpu;

/// <summary>A mission object transform: a flag word, a 3x3 matrix and a position.</summary>
/// <remarks>Port of the transform part of src/game/mission_objects.cpp (namespace ot::sim), docs/mission.md 5.1. The rest of that file (persist fields, GameBase, TeamGroup, registerMissionClasses) is in <c>OpenTribes.Game/MissionObjects.cs</c>.</remarks>
public struct Transform
{
    /// <summary>The flags: bit 0 means rotation is set, bit 1 means translation is set.</summary>
    public uint Flags;
    /// <summary>The rotation matrix.</summary>
    public Float9 M;
    /// <summary>The position.</summary>
    public Float3 P;

    /// <summary>Creates an identity transform with no flags set.</summary>
    public Transform()
    {
        Flags = 0;
        M = Float9.Identity();
        P = default;
    }
}

/// <summary>The Euler angle conversions of the original.</summary>
/// <remarks>Portable form: asinl and atan2l are <see cref="Math.Asin"/> and <see cref="Math.Atan2"/>. Float differences and products follow the mode of <see cref="Core.Fpu"/> and float stores use <c>Fpu.Store</c>.</remarks>
public static class MissionTransform
{
    /// <summary>Sets the matrix of a transform from three Euler angles.</summary>
    /// <param name="e">The Euler angles in radians.</param>
    /// <param name="t">The transform to update.</param>
    /// <remarks>Follows m_mat3_setEuler (005ed42e), with the x87 fsincos and the product grouping of the original (world eulerMatrix).</remarks>
    public static void EulerToMatrix(ReadOnlySpan<float> e, ref Transform t)
    {
        var m = new TMat3
        {
            Flags = t.Flags
        };
        Shape.EulerMatrix(e, ref m);
        t.Flags = m.Flags;
        t.M = m.M;
    }

    /// <summary>Reads three Euler angles from the matrix of a transform. All zero when the rotation flag is clear.</summary>
    /// <param name="t">The transform.</param>
    /// <param name="e">Receives the Euler angles in radians.</param>
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
