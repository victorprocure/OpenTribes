namespace OpenTribes.World;

using System.Runtime.CompilerServices;

/// <summary>A float[3] stored inline, so a struct copy copies the elements like the C++ array assignment.</summary>
[InlineArray(3)]
public struct Float3
{
    private float mElement;

    /// <summary>Creates a vector from three components.</summary>
    /// <param name="x">The first component.</param>
    /// <param name="y">The second component.</param>
    /// <param name="z">The third component.</param>
    /// <returns>The new array.</returns>
    public static Float3 Of(float x, float y, float z)
    {
        Float3 r = default;
        r[0] = x;
        r[1] = y;
        r[2] = z;
        return r;
    }

    /// <summary>Creates a vector from the first three elements of a span.</summary>
    /// <param name="v">The source values, at least three.</param>
    /// <returns>The new array.</returns>
    public static Float3 Of(ReadOnlySpan<float> v)
    {
        Float3 r = default;
        v[..3].CopyTo(r);
        return r;
    }
}
/// <summary>A float[4] stored inline.</summary>

[InlineArray(4)]
public struct Float4
{
    private float mElement;
}
/// <summary>A float[6] stored inline, used for boxes (min xyz then max xyz).</summary>

[InlineArray(6)]
public struct Float6
{
    private float mElement;
    /// <summary>Creates a box from the first six elements of a span.</summary>
    /// <param name="v">The source values, at least six.</param>
    /// <returns>The new array.</returns>

    public static Float6 Of(ReadOnlySpan<float> v)
    {
        Float6 r = default;
        v[..6].CopyTo(r);
        return r;
    }
    /// <summary>Creates a box from its two corners.</summary>
    /// <param name="x0">The minimum x.</param>
    /// <param name="y0">The minimum y.</param>
    /// <param name="z0">The minimum z.</param>
    /// <param name="x1">The maximum x.</param>
    /// <param name="y1">The maximum y.</param>
    /// <param name="z1">The maximum z.</param>
    /// <returns>The new array.</returns>

    public static Float6 Of(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        Float6 r = default;
        r[0] = x0;
        r[1] = y0;
        r[2] = z0;
        r[3] = x1;
        r[4] = y1;
        r[5] = z1;
        return r;
    }
}
/// <summary>A float[9] stored inline, used for 3x3 matrices.</summary>

[InlineArray(9)]
public struct Float9
{
    private float mElement;

    /// <summary>Creates the identity matrix.</summary>
    /// <returns>The new matrix.</returns>
    public static Float9 Identity()
    {
        Float9 r = default;
        r[0] = r[4] = r[8] = 1;
        return r;
    }

    /// <summary>Creates a matrix from the first nine elements of a span.</summary>
    /// <param name="v">The source values, at least nine.</param>
    /// <returns>The new matrix.</returns>
    public static Float9 Of(ReadOnlySpan<float> v)
    {
        Float9 r = default;
        v[..9].CopyTo(r);
        return r;
    }
}
