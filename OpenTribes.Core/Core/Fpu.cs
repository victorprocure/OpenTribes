namespace OpenTribes.Core;

using System.Globalization;
using System.Numerics;

/// <summary>Emulates the original's floating point rounding mode. C# has no rounding mode, so the mode is the flag <see cref="TowardZero"/> and toward-zero results are computed in software from the nearest result and its exact error.</summary>
/// <remarks>Port of the rounding-mode parts of the portable truncating build (src/core/ldouble.hpp with OT_PORTABLE_TRUNCATE, the Chop guard and x87::f32 of src/world/collision.cpp, docs/portable-float.md 1). The Chop guards, <see cref="EnterChop"/> and the float-to-int conversion set the mode, and every double operation, strtod, float store and printf digit generation follows it.</remarks>
public static class Fpu
{
    /// <summary>The current rounding mode of the C++ process: true for FE_TOWARDZERO, false for FE_TONEAREST (the start-up mode).</summary>
    public static bool TowardZero { get; set; }

    /// <summary>Switches to toward-zero rounding, as <c>fesetround(kChopMode)</c> does in the script start-up and float-to-int paths.</summary>
    public static void EnterChop() => TowardZero = true;

    /// <summary>Scope that rounds toward zero and restores the previous mode when disposed. Port of <c>x87::Chop</c>.</summary>
    public readonly struct Chop : IDisposable
    {
        private readonly bool _old;
        /// <summary>Starts the scope in toward-zero mode.</summary>
        /// <param name="old">The mode to restore on dispose.</param>
        public Chop(bool old)
        {
            _old = old;
            TowardZero = true;
        }
        /// <summary>Restores the mode saved at construction.</summary>
        public void Dispose() => TowardZero = _old;
    }

    /// <summary>Starts a toward-zero scope that restores the current mode when disposed.</summary>
    /// <returns>The scope.</returns>
    public static Chop ChopScope() => new(TowardZero);

    private static double TowardZeroStep(double r) => r > 0 ? Math.BitDecrement(r) : Math.BitIncrement(r);

    // Finite operands whose nearest result overflowed: toward zero gives the largest finite double.
    private static double Overflow(double r) => r > 0 ? double.MaxValue : -double.MaxValue;

    /// <summary>Adds two doubles and rounds toward zero. TwoSum gives the exact error of the nearest sum.</summary>
    /// <param name="a">The first operand.</param>
    /// <param name="b">The second operand.</param>
    /// <returns>The sum rounded toward zero.</returns>
    public static double AddChop(double a, double b)
    {
        var r = a + b;
        if (!double.IsFinite(r))
            return double.IsInfinity(r) && double.IsFinite(a) && double.IsFinite(b) ? Overflow(r) : r;
        var bb = r - a;
        var err = a - (r - bb) + (b - bb);
        if ((r > 0 && err < 0) || (r < 0 && err > 0))
            r = TowardZeroStep(r);
        return r;
    }

    /// <summary>Subtracts two doubles and rounds toward zero.</summary>
    /// <param name="a">The minuend.</param>
    /// <param name="b">The subtrahend.</param>
    /// <returns>The difference rounded toward zero.</returns>
    public static double SubChop(double a, double b) => AddChop(a, -b);

    /// <summary>Multiplies two doubles and rounds toward zero. A fused multiply-add gives the exact error.</summary>
    /// <param name="a">The first factor.</param>
    /// <param name="b">The second factor.</param>
    /// <returns>The product rounded toward zero.</returns>
    public static double MulChop(double a, double b)
    {
        var r = a * b;
        if (!double.IsFinite(r))
            return double.IsInfinity(r) && double.IsFinite(a) && double.IsFinite(b) ? Overflow(r) : r;
        var err = Math.FusedMultiplyAdd(a, b, -r);
        if ((r > 0 && err < 0) || (r < 0 && err > 0))
            r = TowardZeroStep(r);
        return r;
    }

    /// <summary>Divides two doubles and rounds toward zero. The remainder <c>a - r*b</c> has the sign of (exact - r) times the sign of <paramref name="b"/>.</summary>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor.</param>
    /// <returns>The quotient rounded toward zero.</returns>
    public static double DivChop(double a, double b)
    {
        var r = a / b;
        if (!double.IsFinite(r))
            return double.IsInfinity(r) && double.IsFinite(a) && double.IsFinite(b) && b != 0 ? Overflow(r) : r;
        if (r == 0)
            return r;
        var rem = Math.FusedMultiplyAdd(-r, b, a);
        var diff = b > 0 ? rem : -rem; // sign of exact - r
        if ((r > 0 && diff < 0) || (r < 0 && diff > 0))
            r = TowardZeroStep(r);
        return r;
    }

    /// <summary>Takes the square root and rounds toward zero. A negative <c>a - r^2</c> means the nearest root is above the exact one.</summary>
    /// <param name="a">The operand.</param>
    /// <returns>The root rounded toward zero.</returns>
    public static double SqrtChop(double a)
    {
        var r = Math.Sqrt(a);
        if (!double.IsFinite(r) || r == 0)
            return r;
        if (Math.FusedMultiplyAdd(-r, r, a) < 0)
            r = Math.BitDecrement(r);
        return r;
    }

    /// <summary>Parses decimal text (<c>[sign]digits[.digits][(e|E)[sign]digits]</c>) and rounds toward zero. The nearest double is stepped toward zero when its magnitude exceeds the exact decimal value (exact integer comparison).</summary>
    /// <param name="text">The decimal text.</param>
    /// <returns>The parsed value. Text beyond the double range stays infinite.</returns>
    /// <remarks>The original parses into the 80-bit unit, which holds 1e400, and the store overflows. The C++ truncating build clamps to DBL_MAX there (docs/portable-float.md 5 item 1). The port follows the original.</remarks>
    public static double ParseChop(string text)
    {
        var r = ParseNearest(text);
        if (double.IsInfinity(r))
            return r;
        if (r == 0 || double.IsNaN(r))
            return r;
        // Exact decimal value: mantissa digits m times 10^e10.
        var i = 0;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
            ++i;
        var digits = new System.Text.StringBuilder("0");
        long e10 = 0;
        var point = false;
        for (; i < text.Length && text[i] != 'e' && text[i] != 'E'; ++i)
        {
            if (text[i] == '.')
            {
                point = true;
                continue;
            }
            digits.Append(text[i]);
            if (point)
                --e10;
        }
        if (i < text.Length)
        {
            // r is finite and non-zero, so the exponent is within a few hundred of the digit count.
            var exp = text[(i + 1)..];
            e10 += long.TryParse(exp, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ex) ? ex : 0;
        }
        BigInteger m = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
        // |r| = m2 * 2^e2.
        var bits = BitConverter.DoubleToInt64Bits(r) & 0x7fffffffffffffffL;
        var be = (int)(bits >> 52);
        var frac = bits & 0xfffffffffffffL;
        BigInteger m2 = be == 0 ? frac : frac | (1L << 52);
        var e2 = (be == 0 ? 1 : be) - 1075;
        BigInteger lhs = m2, rhs = m;
        if (e2 > 0)
            lhs <<= e2;
        else
            rhs <<= -e2;
        if (e10 > 0)
            rhs *= BigInteger.Pow(10, (int)e10);
        else
            lhs *= BigInteger.Pow(10, (int)-e10);
        return lhs > rhs ? TowardZeroStep(r) : r;
    }

    private static double ParseNearest(string text) =>
        double.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                     CultureInfo.InvariantCulture);

    /// <summary>Parses decimal text in the current rounding mode.</summary>
    /// <param name="text">The decimal text.</param>
    /// <returns>The parsed value.</returns>
    public static double Parse(string text) => TowardZero ? ParseChop(text) : ParseNearest(text);

    /// <summary>Adds two doubles in the current rounding mode.</summary>
    /// <param name="a">The first operand.</param>
    /// <param name="b">The second operand.</param>
    /// <returns>The sum.</returns>
    public static double Add(double a, double b) => TowardZero ? AddChop(a, b) : a + b;
    /// <summary>Subtracts two doubles in the current rounding mode.</summary>
    /// <param name="a">The minuend.</param>
    /// <param name="b">The subtrahend.</param>
    /// <returns>The difference.</returns>
    public static double Sub(double a, double b) => TowardZero ? SubChop(a, b) : a - b;
    /// <summary>Multiplies two doubles in the current rounding mode.</summary>
    /// <param name="a">The first factor.</param>
    /// <param name="b">The second factor.</param>
    /// <returns>The product.</returns>
    public static double Mul(double a, double b) => TowardZero ? MulChop(a, b) : a * b;
    /// <summary>Divides two doubles in the current rounding mode.</summary>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor.</param>
    /// <returns>The quotient.</returns>
    public static double Div(double a, double b) => TowardZero ? DivChop(a, b) : a / b;
    /// <summary>Takes a square root in the current rounding mode.</summary>
    /// <param name="a">The operand.</param>
    /// <returns>The root.</returns>
    public static double Sqrt(double a) => TowardZero ? SqrtChop(a) : Math.Sqrt(a);

    /// <summary>Converts a double to float in the current rounding mode, like the plain float store in the C++ (math_commands.cpp f32, script float conversions).</summary>
    /// <param name="d">The value to store.</param>
    /// <returns>The float value.</returns>
    public static float Store(double d) => TowardZero ? F32Chop(d) : (float)d;

    /// <summary>Adds two floats like the C++ SSE single operation, which follows the same rounding mode.</summary>
    /// <param name="a">The first operand.</param>
    /// <param name="b">The second operand.</param>
    /// <returns>The sum.</returns>
    /// <remarks>All four single-precision operations compute the double result rounded toward zero and then store it toward zero. Both roundings truncate, so the result is the toward-zero float result.</remarks>
    public static float AddF(float a, float b) => TowardZero ? F32Chop(AddChop(a, b)) : a + b;
    /// <summary>Subtracts two floats like the C++ SSE single operation. See <see cref="AddF"/>.</summary>
    /// <param name="a">The minuend.</param>
    /// <param name="b">The subtrahend.</param>
    /// <returns>The difference.</returns>
    public static float SubF(float a, float b) => TowardZero ? F32Chop(SubChop(a, b)) : a - b;
    /// <summary>Multiplies two floats like the C++ SSE single operation. See <see cref="AddF"/>.</summary>
    /// <param name="a">The first factor.</param>
    /// <param name="b">The second factor.</param>
    /// <returns>The product.</returns>
    public static float MulF(float a, float b) => TowardZero ? F32Chop(MulChop(a, b)) : a * b;
    /// <summary>Divides two floats like the C++ SSE single operation. See <see cref="AddF"/>.</summary>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor.</param>
    /// <returns>The quotient.</returns>
    public static float DivF(float a, float b) => TowardZero ? F32Chop(DivChop(a, b)) : a / b;
    /// <summary>Takes a single-precision square root like <c>sqrtf</c> (SSE <c>sqrtss</c> under the current mode).</summary>
    /// <param name="a">The operand.</param>
    /// <returns>The root.</returns>
    public static float SqrtF(float a) => TowardZero ? F32Chop(SqrtChop(a)) : MathF.Sqrt(a);

    /// <summary>Converts a double to the float nearest to it, stepped one ulp toward zero when its magnitude exceeds that of <paramref name="d"/>. This is <c>float(x)</c> under FE_TOWARDZERO.</summary>
    /// <param name="d">The value to convert.</param>
    /// <returns>The float. A finite value beyond the float range gives plus or minus <see cref="float.MaxValue"/>. NaN, infinities, zeros and exact values are unchanged.</returns>
    public static float F32Chop(double d)
    {
        var f = (float)d;
        if (double.IsNaN(d) || double.IsInfinity(d))
            return f;
        if (float.IsInfinity(f))
            return d > 0 ? float.MaxValue : -float.MaxValue;
        if (Math.Abs((double)f) > Math.Abs(d))
            f = d > 0 ? MathF.BitDecrement(f) : MathF.BitIncrement(f);
        return f;
    }

    /// <summary>Tells whether <paramref name="text"/> is a number the engine itself printed, so a parse and print round trip is stable.</summary>
    /// <param name="text">The text, <c>[-]digits.digits</c> or <c>[-]digits</c>.</param>
    /// <param name="decimals">The digits after the point of a printed <c>%f</c>. A negative value means a printed <c>%g</c> without exponent, which has at most six significant digits.</param>
    /// <returns>True when the text has that shape.</returns>
    /// <remarks>Used by the script number conversions (ROADMAP M9 C8, client-scripts.md E7).</remarks>
    public static bool IsPrintedDecimal(string text, int decimals)
    {
        var i = text.Length > 0 && text[0] == '-' ? 1 : 0;
        var dot = text.IndexOf('.');
        var intDigits = (dot < 0 ? text.Length : dot) - i;
        if (intDigits <= 0)
            return false;
        for (var k = i; k < text.Length; ++k)
            if (k != dot && (text[k] < '0' || text[k] > '9'))
                return false;
        var frac = dot < 0 ? 0 : text.Length - dot - 1;
        if (decimals >= 0)
            return dot >= 0 && frac == decimals;
        var sig = 0;
        var started = false;
        for (var k = i; k < text.Length; ++k)
        {
            if (k == dot)
                continue;
            if (text[k] != '0')
                started = true;
            if (started)
                ++sig;
        }
        return sig is 6 or 0;
    }

    /// <summary>Steps a toward-zero parse away from zero until its magnitude is at least the decimal's. A printed digit string truncates, so a value that parses below the decimal would lose a unit of the last digit on the next print.</summary>
    /// <param name="chopped">The toward-zero parse of <paramref name="text"/>.</param>
    /// <param name="text">The plain decimal text.</param>
    /// <param name="single">True to step in floats, false to step in doubles.</param>
    /// <returns>The adjusted value. Steps at most three times. An exact value stays.</returns>
    public static double AtLeastDecimal(double chopped, string text, bool single)
    {
        if (double.IsNaN(chopped) || double.IsInfinity(chopped) || chopped == 0)
            return chopped;
        var neg = text.StartsWith('-');
        var digits = neg ? text[1..] : text;
        var dot = digits.IndexOf('.');
        var k = dot < 0 ? 0 : digits.Length - dot - 1;
        var num = BigInteger.Parse(digits.Replace(".", ""), CultureInfo.InvariantCulture);
        var c = chopped;
        for (var step = 0; step < 3 && CompareMagnitude(c, num, k) < 0; ++step)
            c = single ? (c > 0 ? MathF.BitIncrement((float)c) : MathF.BitDecrement((float)c)) : (c > 0 ? Math.BitIncrement(c) : Math.BitDecrement(c));
        return c;
    }

    // |f| compared with num / 10^k, exactly.
    private static int CompareMagnitude(double f, BigInteger num, int k)
    {
        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(f));
        var exp = (int)((bits >> 52) & 0x7ff);
        var mant = bits & 0xfffffffffffffL;
        if (exp == 0)
            exp = 1;
        else
            mant |= 1L << 52;
        exp -= 1075;
        var lhs = mant * BigInteger.Pow(10, k);
        var rhs = num;
        if (exp >= 0)
            lhs <<= exp;
        else
            rhs <<= -exp;
        return lhs.CompareTo(rhs);
    }
}
