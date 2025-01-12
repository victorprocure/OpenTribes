// Port of src/script/numbers.hpp and src/script/numbers.cpp: number conversions of the script
// (docs/script.md 3.1-3.3), in the form of the portable truncating build (DECISIONS D25, docs/portable-float.md 1):
// double values; parsing and digit generation (%g, %f) follow the rounding mode of Fpu (toward zero in the script
// FPU mode, docs/script.md 3.2), computed on the exact values (Fpu.ParseChop, CFormat below).
using System.Globalization;
using System.Numerics;
using System.Text;
using OpenTribes.Core;

namespace OpenTribes.Script;

public static class Numbers
{
    // A quiet NaN with the sign bit clear (std::numeric_limits<double>::quiet_NaN, nanLD).
    public static readonly double NaN = BitConverter.Int64BitsToDouble(0x7ff8000000000000L);
    // The same NaN with the sign bit set.
    public static readonly double NegNaN = BitConverter.Int64BitsToDouble(unchecked((long)0xfff8000000000000UL));

    // src/script/numbers.cpp enterScriptFpuMode: puts the FPU in round-toward-zero, as the original's crt_ftol
    // (0x0048813c) leaves it.
    public static void EnterScriptFpuMode() => Fpu.EnterChop();

    // src/script/numbers.cpp isTrueString: "True", ASCII case-insensitive.
    public static bool IsTrueString(string s)
    {
        const string kTrue = "true";
        if (s.Length != 4)
            return false;
        for (int i = 0; i < 4; ++i)
            if ((s[i] | 0x20) != kTrue[i])
                return false;
        return true;
    }

    // src/script/numbers.cpp parseBool: T/t true, F/f false, else atof != 0 (Console_parseBool).
    public static bool ParseBool(string s)
    {
        if (s.Length != 0 && (s[0] == 'T' || s[0] == 't'))
            return true;
        if (s.Length != 0 && (s[0] == 'F' || s[0] == 'f'))
            return false;
        return BorlandAtof(s) != 0;
    }

    // src/script/numbers.cpp looksNumeric: non-empty, only 0-9 . e E + - (0x005f6280).
    public static bool LooksNumeric(string s)
    {
        if (s.Length == 0)
            return false;
        foreach (char c in s)
            if (!((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-'))
                return false;
        return true;
    }

    private static bool IsSpace(char c) => c == ' ' || (c >= '\t' && c <= '\r');
    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    // src/script/numbers.cpp borlandAtof: decimal only; strtod of the matched text (double in the portable build).
    public static double BorlandAtof(string s)
    {
        // [ws][sign]digits[.digits][(e|E)[sign]digits]; no hex, inf or nan forms.
        int i = 0;
        while (i < s.Length && IsSpace(s[i]))
            ++i;
        int start = i;
        // Borland RTL reads its own "+INF" / "-NAN" spellings (sign and upper case required).
        if (i + 3 < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            bool negative = s[i] == '-';
            if (string.CompareOrdinal(s, i + 1, "INF", 0, 3) == 0)
                return negative ? double.NegativeInfinity : double.PositiveInfinity;
            if (string.CompareOrdinal(s, i + 1, "NAN", 0, 3) == 0)
                return negative ? NegNaN : NaN;
        }
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            ++i;
        int digits = 0;
        while (i < s.Length && IsDigit(s[i]))
        {
            ++i;
            ++digits;
        }
        if (i < s.Length && s[i] == '.')
        {
            ++i;
            while (i < s.Length && IsDigit(s[i]))
            {
                ++i;
                ++digits;
            }
        }
        if (digits == 0)
            return 0.0;
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < s.Length && (s[j] == '+' || s[j] == '-'))
                ++j;
            if (j < s.Length && IsDigit(s[j]))
            {
                while (j < s.Length && IsDigit(s[j]))
                    ++j;
                i = j;
            }
        }
        // The result must fit a double: overflow gives infinity, underflow below denormals gives 0
        // (both are what the round-to-nearest parse returns).
        return Strtod(s.Substring(start, i - start));
    }

    // strtod of a text matched by one of the decimal grammars of this port, in the current rounding mode.
    internal static double Strtod(string number) => Fpu.Parse(number);

    // src/script/numbers.cpp borlandAtoi: wraps modulo 2^32.
    public static int BorlandAtoi(string s)
    {
        int i = 0;
        while (i < s.Length && IsSpace(s[i]))
            ++i;
        bool negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            negative = s[i++] == '-';
        uint v = 0;
        while (i < s.Length && IsDigit(s[i]))
            v = unchecked(v * 10 + (uint)(s[i++] - '0'));
        return unchecked((int)(negative ? 0u - v : v));
    }

    // src/script/numbers.cpp parseIntLiteral: INT token, decimal (atoi) or hex (strtol).
    public static int ParseIntLiteral(string text)
    {
        if (text.Length > 2 && (text[1] == 'x' || text[1] == 'X'))
        {
            // Hex literals wrap modulo 2^32 (0x100000000 is 0, 0x1FFFFFFFF is -1).
            uint v = 0;
            for (int i = 2; i < text.Length; ++i)
            {
                char c = text[i];
                v = unchecked(v * 16 + (uint)(c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10));
            }
            return unchecked((int)v);
        }
        return BorlandAtoi(text);
    }

    // src/script/numbers.cpp formatFloat: %g, Borland spellings for INF/NAN.
    public static string FormatFloat(double v)
    {
        if (double.IsInfinity(v))
            return v < 0 ? "-INF" : "+INF";
        if (double.IsNaN(v))
            return double.IsNegative(v) ? "-NAN" : "+NAN";
        return CFormat.G(v, 6); // digits truncate in round-toward-zero mode
    }

    // src/script/numbers.cpp formatFloat15: %.15g, binary float arithmetic in LastHope 1.30 (L5).
    public static string FormatFloat15(double v)
    {
        if (double.IsInfinity(v) || double.IsNaN(v))
            return FormatFloat(v);
        return CFormat.G(v, 15);
    }

    // src/script/numbers.cpp formatInt.
    public static string FormatInt(int v) => v.ToString(CultureInfo.InvariantCulture);

    // src/script/numbers.cpp floatToInt: fistp truncation, 0x80000000 when out of range.
    public static int FloatToInt(double v)
    {
        Fpu.EnterChop(); // crt_ftol leaves the FPU in round-toward-zero (0x0048813c)
        if (double.IsNaN(v) || v >= 2147483648.0 || v < -2147483648.0)
            return int.MinValue;
        return (int)v; // C conversion truncates, like fistp under round-toward-zero
    }
}

// Port convention (no C++ file): the printf conversions %g, %e and %f of the C++ code (snprintf), with the
// digits generated from the exact decimal value of the double and rounded in the current mode of Fpu: truncated
// (toward zero) in the script FPU mode, as the original prints them (docs/script.md 3.2), else nearest-even. Special values print as the C library does (inf, nan).
public static class CFormat
{
    // Exact decimal digits of a finite non-zero |v|: v = 0.D * 10^(exp10 + 1) = D[0].D[1..] * 10^exp10.
    private static (string digits, int exp10) Exact(double v)
    {
        long bits = BitConverter.DoubleToInt64Bits(v) & 0x7fffffffffffffffL;
        int be = (int)(bits >> 52);
        long frac = bits & 0xfffffffffffffL;
        long m = be == 0 ? frac : frac | (1L << 52);
        int e2 = (be == 0 ? 1 : be) - 1075;
        string d;
        int exp10;
        if (e2 >= 0)
        {
            d = (new BigInteger(m) << e2).ToString(CultureInfo.InvariantCulture);
            exp10 = d.Length - 1;
        }
        else
        {
            d = (new BigInteger(m) * BigInteger.Pow(5, -e2)).ToString(CultureInfo.InvariantCulture);
            exp10 = d.Length - 1 + e2;
        }
        return (d.TrimEnd('0'), exp10);
    }

    private static char DigitAt(string d, int i) => i >= 0 && i < d.Length ? d[i] : '0';

    // Keeps the first `keep` digits of d (value 0.d * 10^(exp10 + 1)): truncated toward zero, or rounded to nearest-even
    // outside the toward-zero mode (a carry may add a digit and raise exp10).
    private static (string, int) Round(string d, int exp10, int keep)
    {
        if (keep >= d.Length)
            return (d, exp10);
        if (keep < 0)
            return ("", exp10);
        string kept = d[..keep];
        if (Fpu.TowardZero)
            return (kept, exp10);
        string rest = d[keep..];
        int cmp = rest[0] > '5' ? 1 : rest[0] < '5' ? -1 : rest[1..].Trim('0').Length > 0 ? 1 : 0;
        bool up = cmp > 0 || (cmp == 0 && keep > 0 && (kept[^1] - '0') % 2 == 1);
        if (!up)
            return (kept, exp10);
        string nd = (kept.Length == 0 ? BigInteger.One : BigInteger.Parse(kept, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
        return (nd, nd.Length > kept.Length ? exp10 + 1 : exp10);
    }

    // Fixed notation of the exact digits truncated to prec decimals.
    private static string Fixed(string d, int exp10, int prec)
    {
        var sb = new StringBuilder();
        if (exp10 < 0)
            sb.Append('0');
        else
            for (int i = 0; i <= exp10; ++i)
                sb.Append(DigitAt(d, i));
        if (prec > 0)
        {
            sb.Append('.');
            for (int k = 1; k <= prec; ++k)
                sb.Append(DigitAt(d, exp10 + k));
        }
        return sb.ToString();
    }

    private static string Special(double v)
    {
        if (double.IsNaN(v))
            return double.IsNegative(v) ? "-nan" : "nan";
        return v < 0 ? "-inf" : "inf";
    }

    // %.{prec}f
    public static string F(double v, int prec)
    {
        if (!double.IsFinite(v))
            return Special(v);
        string sign = double.IsNegative(v) ? "-" : "";
        if (v == 0)
            return sign + Fixed("", 0, prec);
        var (d, e) = Exact(v);
        (d, e) = Round(d, e, e + 1 + prec);
        return sign + Fixed(d, e, prec);
    }

    // %.{prec}g
    public static string G(double v, int prec)
    {
        if (!double.IsFinite(v))
            return Special(v);
        if (prec == 0)
            prec = 1;
        string sign = double.IsNegative(v) ? "-" : "";
        string d = "";
        int x = 0;
        if (v != 0)
        {
            (d, x) = Exact(v);
            (d, x) = Round(d, x, prec); // P significant digits; rounding up may raise the exponent
        }
        string body;
        if (prec > x && x >= -4)
        {
            body = StripZeros(Fixed(d, x, prec - 1 - x));
        }
        else
        {
            var sb = new StringBuilder();
            sb.Append(DigitAt(d, 0));
            if (prec > 1)
            {
                sb.Append('.');
                for (int i = 1; i < prec; ++i)
                    sb.Append(DigitAt(d, i));
            }
            body = StripZeros(sb.ToString());
            int ax = Math.Abs(x);
            body += (x < 0 ? "e-" : "e+") + (ax < 10 ? "0" : "") + ax.ToString(CultureInfo.InvariantCulture);
        }
        return sign + body;
    }

    private static string StripZeros(string s)
    {
        if (s.IndexOf('.') < 0)
            return s;
        s = s.TrimEnd('0');
        return s.EndsWith('.') ? s[..^1] : s;
    }
}
