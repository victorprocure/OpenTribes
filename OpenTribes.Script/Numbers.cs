using System.Globalization;
using System.Numerics;
using System.Text;
using OpenTribes.Core;

namespace OpenTribes.Script;

/// <summary>The number conversions of the script (docs/script.md 3.1 to 3.3), in the form of the portable truncating build (DECISIONS D25, docs/portable-float.md 1).</summary>
/// <remarks>Values are doubles. Parsing and digit generation (%g, %f) follow the rounding mode of <c>Fpu</c>, toward zero in the script FPU mode (docs/script.md 3.2), and are computed on the exact values. Port of src/script/numbers.cpp.</remarks>
public static class Numbers
{
    /// <summary>A quiet NaN with the sign bit clear.</summary>
    public static readonly double NaN = BitConverter.Int64BitsToDouble(0x7ff8000000000000L);
    /// <summary>The same NaN with the sign bit set.</summary>
    public static readonly double NegNaN = BitConverter.Int64BitsToDouble(unchecked((long)0xfff8000000000000UL));

    /// <summary>Puts the FPU in round-toward-zero, as the original's crt_ftol leaves it (0048813c).</summary>
    public static void EnterScriptFpuMode() => Fpu.EnterChop();

    /// <summary>Tests for the string "True", ASCII case-insensitive.</summary>
    /// <param name="s">The string.</param>
    /// <returns>True when it matches.</returns>
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

    /// <summary>Parses a boolean (Console_parseBool): T or t is true, F or f is false, anything else is atof not equal to 0.</summary>
    /// <param name="s">The string.</param>
    /// <returns>The value.</returns>
    public static bool ParseBool(string s)
    {
        if (s.Length != 0 && (s[0] == 'T' || s[0] == 't'))
            return true;
        if (s.Length != 0 && (s[0] == 'F' || s[0] == 'f'))
            return false;
        return BorlandAtof(s) != 0;
    }

    /// <summary>Tests whether a string is non-empty and holds only 0-9 . e E + - (005f6280).</summary>
    /// <param name="s">The string.</param>
    /// <returns>True when it looks numeric.</returns>
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

    /// <summary>Parses a decimal number like the Borland atof: strtod of the matched text.</summary>
    /// <param name="s">The string.</param>
    /// <returns>The value, a double in the portable build.</returns>
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

    /// <summary>Parses an integer like the Borland atoi, wrapping modulo 2^32.</summary>
    /// <param name="s">The string.</param>
    /// <returns>The value.</returns>
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

    /// <summary>Parses an INT token, decimal (atoi) or hexadecimal (strtol).</summary>
    /// <param name="text">The token text.</param>
    /// <returns>The value.</returns>
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

    /// <summary>Formats a number as %g, with the Borland spellings for INF and NAN.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The text.</returns>
    public static string FormatFloat(double v)
    {
        if (double.IsInfinity(v))
            return v < 0 ? "-INF" : "+INF";
        if (double.IsNaN(v))
            return double.IsNegative(v) ? "-NAN" : "+NAN";
        return CFormat.G(v, 6); // digits truncate in round-toward-zero mode
    }

    /// <summary>Formats a number as %.15g, using binary float arithmetic as LastHope 1.30 does (docs/lasthope-1.30.md L5).</summary>
    /// <param name="v">The value.</param>
    /// <returns>The text.</returns>
    public static string FormatFloat15(double v)
    {
        if (double.IsInfinity(v) || double.IsNaN(v))
            return FormatFloat(v);
        return CFormat.G(v, 15);
    }

    /// <summary>Formats an integer in decimal.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The text.</returns>
    public static string FormatInt(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Converts a double to an int by fistp truncation.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The value, 0x80000000 when out of range.</returns>
    public static int FloatToInt(double v)
    {
        Fpu.EnterChop(); // crt_ftol leaves the FPU in round-toward-zero (0x0048813c)
        if (double.IsNaN(v) || v >= 2147483648.0 || v < -2147483648.0)
            return int.MinValue;
        return (int)v; // C conversion truncates, like fistp under round-toward-zero
    }
}

/// <summary>The printf conversions %g, %e and %f of the C++ code (snprintf).</summary>
/// <remarks>The digits are generated from the exact decimal value of the double and rounded in the current mode of <c>Fpu</c>: truncated in the script FPU mode, as the original prints them (docs/script.md 3.2), else to nearest even. Special values print as the C library does (inf, nan).</remarks>
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

    /// <summary>Formats a number as %.{prec}f.</summary>
    /// <param name="v">The value.</param>
    /// <param name="prec">The number of decimals.</param>
    /// <returns>The text.</returns>
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

    /// <summary>Formats a number as %.{prec}g.</summary>
    /// <param name="v">The value.</param>
    /// <param name="prec">The number of significant digits.</param>
    /// <returns>The text.</returns>
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
