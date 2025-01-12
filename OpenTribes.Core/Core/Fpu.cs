// Port of x87::f32 and the Chop-guarded float stores of the portable truncating build (src/core/ldouble.hpp with
// OT_PORTABLE_TRUNCATE, docs/portable-float.md 1): a double stored to a single float rounds toward zero.
namespace OpenTribes.Core;

public static class Fpu
{
    // The float nearest to d, stepped one ulp toward zero when its magnitude exceeds d's. A finite d beyond the float
    // range gives +-float.MaxValue; NaN, infinities, zeros and exact values are unchanged.
    public static float F32Chop(double d)
    {
        float f = (float)d;
        if (double.IsNaN(d) || double.IsInfinity(d))
            return f;
        if (float.IsInfinity(f))
            return d > 0 ? float.MaxValue : -float.MaxValue;
        if (Math.Abs((double)f) > Math.Abs(d))
            f = d > 0 ? MathF.BitDecrement(f) : MathF.BitIncrement(f);
        return f;
    }
}
