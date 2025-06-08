// Port of the rounding-mode parts of the portable truncating build (src/core/ldouble.hpp with OT_PORTABLE_TRUNCATE,
// the Chop guard and x87::f32 of src/world/collision.cpp, docs/portable-float.md 1): kChopMode is FE_TOWARDZERO, the
// Chop guards, enterScriptFpuMode and floatToInt set it, and every double operation, strtod, float store and printf
// digit generation follows the current mode. C# has no rounding mode, so the mode is the flag TowardZero and the
// toward-zero results are computed in software from the nearest result and its exact error.
using System.Globalization;
using System.Numerics;

namespace OpenTribes.Core;

public static class Fpu
{
    // The current rounding mode of the C++ process: true for FE_TOWARDZERO, false for FE_TONEAREST (the start-up mode).
    public static bool TowardZero;

    // fesetround(kChopMode): enterScriptFpuMode, floatToInt.
    public static void EnterChop() => TowardZero = true;

    // x87::Chop: toward zero for a scope, the previous mode restored at its end.
    public readonly struct Chop : IDisposable
    {
        private readonly bool mOld;
        public Chop(bool old)
        {
            mOld = old;
            TowardZero = true;
        }
        public void Dispose() => TowardZero = mOld;
    }

    public static Chop ChopScope() => new(TowardZero);

    private static double TowardZeroStep(double r) => r > 0 ? Math.BitDecrement(r) : Math.BitIncrement(r);

    // Finite operands whose nearest result overflowed: toward zero gives the largest finite double.
    private static double Overflow(double r) => r > 0 ? double.MaxValue : -double.MaxValue;

    // a + b rounded toward zero (TwoSum gives the exact error of the nearest sum).
    public static double AddChop(double a, double b)
    {
        double r = a + b;
        if (!double.IsFinite(r))
            return double.IsInfinity(r) && double.IsFinite(a) && double.IsFinite(b) ? Overflow(r) : r;
        double bb = r - a;
        double err = (a - (r - bb)) + (b - bb);
        if ((r > 0 && err < 0) || (r < 0 && err > 0))
            r = TowardZeroStep(r);
        return r;
    }

    // a - b rounded toward zero.
    public static double SubChop(double a, double b) => AddChop(a, -b);

    // a * b rounded toward zero (fma gives the exact error).
    public static double MulChop(double a, double b)
    {
        double r = a * b;
        if (!double.IsFinite(r))
            return double.IsInfinity(r) && double.IsFinite(a) && double.IsFinite(b) ? Overflow(r) : r;
        double err = Math.FusedMultiplyAdd(a, b, -r);
        if ((r > 0 && err < 0) || (r < 0 && err > 0))
            r = TowardZeroStep(r);
        return r;
    }

    // a / b rounded toward zero: the remainder a - r b has the sign of (exact - r) times the sign of b.
    public static double DivChop(double a, double b)
    {
        double r = a / b;
        if (!double.IsFinite(r))
            return double.IsInfinity(r) && double.IsFinite(a) && double.IsFinite(b) && b != 0 ? Overflow(r) : r;
        if (r == 0)
            return r;
        double rem = Math.FusedMultiplyAdd(-r, b, a);
        double diff = b > 0 ? rem : -rem; // sign of exact - r
        if ((r > 0 && diff < 0) || (r < 0 && diff > 0))
            r = TowardZeroStep(r);
        return r;
    }

    // sqrt(a) rounded toward zero: a - r^2 < 0 means r is above the exact root.
    public static double SqrtChop(double a)
    {
        double r = Math.Sqrt(a);
        if (!double.IsFinite(r) || r == 0)
            return r;
        if (Math.FusedMultiplyAdd(-r, r, a) < 0)
            r = Math.BitDecrement(r);
        return r;
    }

    // strtod of a decimal text ([sign]digits[.digits][(e|E)[sign]digits]) rounded toward zero: the nearest double,
    // stepped toward zero when it is larger in magnitude than the exact decimal value (exact integer comparison).
    // A text beyond the double range stays +-infinity: the original parses into the 80-bit unit, which holds 1e400, and
    // the store overflows (the C++ truncating build clamps to DBL_MAX there, docs/portable-float.md 5 item 1; the port
    // follows the original).
    public static double ParseChop(string text)
    {
        double r = ParseNearest(text);
        if (double.IsInfinity(r))
            return r;
        if (r == 0 || double.IsNaN(r))
            return r;
        // Exact decimal value: mantissa digits m times 10^e10.
        int i = 0;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
            ++i;
        var digits = new System.Text.StringBuilder("0");
        long e10 = 0;
        bool point = false;
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
            string exp = text[(i + 1)..];
            e10 += long.TryParse(exp, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long ex) ? ex : 0;
        }
        BigInteger m = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
        // |r| = m2 * 2^e2.
        long bits = BitConverter.DoubleToInt64Bits(r) & 0x7fffffffffffffffL;
        int be = (int)(bits >> 52);
        long frac = bits & 0xfffffffffffffL;
        BigInteger m2 = be == 0 ? frac : frac | (1L << 52);
        int e2 = (be == 0 ? 1 : be) - 1075;
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

    // strtod in the current mode.
    public static double Parse(string text) => TowardZero ? ParseChop(text) : ParseNearest(text);

    // The basic double operations in the current mode.
    public static double Add(double a, double b) => TowardZero ? AddChop(a, b) : a + b;
    public static double Sub(double a, double b) => TowardZero ? SubChop(a, b) : a - b;
    public static double Mul(double a, double b) => TowardZero ? MulChop(a, b) : a * b;
    public static double Div(double a, double b) => TowardZero ? DivChop(a, b) : a / b;
    public static double Sqrt(double a) => TowardZero ? SqrtChop(a) : Math.Sqrt(a);

    // float(x) in the current mode: the plain store of the C++ (math_commands.cpp f32, script float conversions).
    public static float Store(double d) => TowardZero ? F32Chop(d) : (float)d;

    // Single-precision float + - * / of the C++ (SSE single operations, which follow the same mode): the double result
    // rounded toward zero, then stored toward zero, is the toward-zero float result (both roundings truncate).
    public static float AddF(float a, float b) => TowardZero ? F32Chop(AddChop(a, b)) : a + b;
    public static float SubF(float a, float b) => TowardZero ? F32Chop(SubChop(a, b)) : a - b;
    public static float MulF(float a, float b) => TowardZero ? F32Chop(MulChop(a, b)) : a * b;
    public static float DivF(float a, float b) => TowardZero ? F32Chop(DivChop(a, b)) : a / b;
    // sqrtf (SSE sqrtss under the mode).
    public static float SqrtF(float a) => TowardZero ? F32Chop(SqrtChop(a)) : MathF.Sqrt(a);

    // The float nearest to d, stepped one ulp toward zero when its magnitude exceeds d's: float(x) under FE_TOWARDZERO.
    // A finite d beyond the float range gives +-float.MaxValue; NaN, infinities, zeros and exact values are unchanged.
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

    // Stable text round trip (ROADMAP M9 C8 fix, client-scripts.md E7): true when `text` is `[-]digits.digits` or `[-]digits`
    // with `decimals` digits after the point (a printed `%f`), or, with decimals < 0, at most six significant digits in
    // plain form (a printed `%g` without exponent). Such text is what the engine itself printed.
    public static bool IsPrintedDecimal(string text, int decimals)
    {
        int i = text.Length > 0 && text[0] == '-' ? 1 : 0;
        int dot = text.IndexOf('.');
        int intDigits = (dot < 0 ? text.Length : dot) - i;
        if (intDigits <= 0)
            return false;
        for (int k = i; k < text.Length; ++k)
            if (k != dot && (text[k] < '0' || text[k] > '9'))
                return false;
        int frac = dot < 0 ? 0 : text.Length - dot - 1;
        if (decimals >= 0)
            return dot >= 0 && frac == decimals;
        int sig = 0;
        bool started = false;
        for (int k = i; k < text.Length; ++k)
        {
            if (k == dot)
                continue;
            if (text[k] != '0')
                started = true;
            if (started)
                ++sig;
        }
        return sig == 6 || sig == 0;
    }

    // `chopped` is the toward-zero parse of the plain decimal `text`. A printed digit string truncates, so the value it parses to
    // must not lie below the decimal or the next print loses a unit of the last digit. Steps up to two floats (or doubles) away
    // from zero until the magnitude is at least the decimal's; an exact value stays.
    public static double AtLeastDecimal(double chopped, string text, bool single)
    {
        if (double.IsNaN(chopped) || double.IsInfinity(chopped) || chopped == 0)
            return chopped;
        bool neg = text.StartsWith('-');
        string digits = neg ? text[1..] : text;
        int dot = digits.IndexOf('.');
        int k = dot < 0 ? 0 : digits.Length - dot - 1;
        var num = BigInteger.Parse(digits.Replace(".", ""), CultureInfo.InvariantCulture);
        double c = chopped;
        for (int step = 0; step < 3 && CompareMagnitude(c, num, k) < 0; ++step)
            c = single ? (c > 0 ? MathF.BitIncrement((float)c) : MathF.BitDecrement((float)c)) : (c > 0 ? Math.BitIncrement(c) : Math.BitDecrement(c));
        return c;
    }

    // |f| compared with num / 10^k, exactly.
    private static int CompareMagnitude(double f, BigInteger num, int k)
    {
        long bits = BitConverter.DoubleToInt64Bits(Math.Abs(f));
        int exp = (int)((bits >> 52) & 0x7ff);
        long mant = bits & 0xfffffffffffffL;
        if (exp == 0)
            exp = 1;
        else
            mant |= 1L << 52;
        exp -= 1075;
        BigInteger lhs = mant * BigInteger.Pow(10, k);
        BigInteger rhs = num;
        if (exp >= 0)
            lhs <<= exp;
        else
            rhs <<= -exp;
        return lhs.CompareTo(rhs);
    }
}
