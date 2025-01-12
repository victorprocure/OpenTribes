// Port convention (no C++ file): the fixed float arrays of the C++ structs (float m[9], float p[3], float box[6]) as
// inline arrays, so that a struct copy copies them as the C++ assignment does.
using System.Runtime.CompilerServices;

namespace OpenTribes.World;

[InlineArray(3)]
public struct Float3
{
    private float mElement;

    public static Float3 Of(float x, float y, float z)
    {
        Float3 r = default;
        r[0] = x;
        r[1] = y;
        r[2] = z;
        return r;
    }

    public static Float3 Of(ReadOnlySpan<float> v)
    {
        Float3 r = default;
        v[..3].CopyTo(r);
        return r;
    }
}

[InlineArray(4)]
public struct Float4
{
    private float mElement;
}

[InlineArray(6)]
public struct Float6
{
    private float mElement;

    public static Float6 Of(ReadOnlySpan<float> v)
    {
        Float6 r = default;
        v[..6].CopyTo(r);
        return r;
    }

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

[InlineArray(9)]
public struct Float9
{
    private float mElement;

    public static Float9 Identity()
    {
        Float9 r = default;
        r[0] = r[4] = r[8] = 1;
        return r;
    }

    public static Float9 Of(ReadOnlySpan<float> v)
    {
        Float9 r = default;
        v[..9].CopyTo(r);
        return r;
    }
}
