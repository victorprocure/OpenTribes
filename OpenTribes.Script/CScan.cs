using OpenTribes.Core;

namespace OpenTribes.Script;

/// <summary>The C library sscanf conversions the game layer uses: "%f %f %f", "%f %f %f %f %f %f" and "%d" (std::sscanf of src/game).</summary>
/// <remarks>White space is skipped, then a decimal, hexadecimal, infinity or NaN number is read and stored to float in the current mode. The double parse runs in the mode of <c>Fpu</c> and the store follows, which together truncate like the single rounding of strtof.</remarks>
public static class CScan
{
    private static bool IsSpace(char c) => c == ' ' || (c >= '\t' && c <= '\r');
    private static bool Hex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>Reads one %f at a position, skipping white space first.</summary>
    /// <param name="s">The text.</param>
    /// <param name="pos">The position, advanced past the number. Unchanged when no number starts there.</param>
    /// <param name="f">Receives the number.</param>
    /// <returns>False when no number starts at the position.</returns>
    public static bool Float(string s, ref int pos, out float f)
    {
        f = 0;
        int i = pos;
        while (i < s.Length && IsSpace(s[i]))
            ++i;
        int start = i;
        bool negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            negative = s[i++] == '-';
        bool Word(string w) => i + w.Length <= s.Length && string.Compare(s, i, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0;
        if (Word("inf"))
        {
            i += Word("infinity") ? 8 : 3;
            f = negative ? float.NegativeInfinity : float.PositiveInfinity;
            pos = i;
            return true;
        }
        if (Word("nan"))
        {
            i += 3;
            f = (float)(negative ? Numbers.NegNaN : Numbers.NaN);
            pos = i;
            return true;
        }
        if (i + 2 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X') &&
            (Hex(s[i + 2]) || (s[i + 2] == '.' && i + 3 < s.Length && Hex(s[i + 3]))))
        {
            i += 2;
            double m = 0;
            int scale = 0;
            bool point = false;
            for (; i < s.Length; ++i)
            {
                char c = s[i];
                if (c == '.' && !point)
                {
                    point = true;
                    continue;
                }
                if (!Hex(c))
                    break;
                m = m * 16 + (c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10);
                if (point)
                    scale -= 4;
            }
            if (i < s.Length && (s[i] == 'p' || s[i] == 'P'))
            {
                int j = i + 1;
                bool eneg = false;
                if (j < s.Length && (s[j] == '+' || s[j] == '-'))
                    eneg = s[j++] == '-';
                if (j < s.Length && s[j] >= '0' && s[j] <= '9')
                {
                    int e = 0;
                    while (j < s.Length && s[j] >= '0' && s[j] <= '9')
                        e = Math.Min(e * 10 + (s[j++] - '0'), 100000);
                    scale += eneg ? -e : e;
                    i = j;
                }
            }
            f = Fpu.Store(Math.ScaleB(negative ? -m : m, scale));
            pos = i;
            return true;
        }
        int digits = 0;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            ++i;
            ++digits;
        }
        if (i < s.Length && s[i] == '.')
        {
            ++i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9')
            {
                ++i;
                ++digits;
            }
        }
        if (digits == 0)
            return false;
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < s.Length && (s[j] == '+' || s[j] == '-'))
                ++j;
            if (j < s.Length && s[j] >= '0' && s[j] <= '9')
            {
                while (j < s.Length && s[j] >= '0' && s[j] <= '9')
                    ++j;
                i = j;
            }
        }
        f = Fpu.Store(Numbers.Strtod(s[start..i]));
        pos = i;
        return true;
    }

    /// <summary>Reads several %f conversions like sscanf with one conversion per output element.</summary>
    /// <param name="text">The text.</param>
    /// <param name="output">Receives the numbers. Elements that are not read keep their value.</param>
    /// <returns>The number stored, or -1 (EOF) when the input ends before the first conversion.</returns>
    public static int Floats(string text, Span<float> output)
    {
        string s = CString.CStr(text);
        int pos = 0;
        int n = 0;
        for (; n < output.Length; ++n)
        {
            while (pos < s.Length && IsSpace(s[pos]))
                ++pos;
            if (pos >= s.Length)
                return n == 0 ? -1 : n;
            if (!Float(s, ref pos, out float f))
                return n;
            output[n] = f;
        }
        return n;
    }

    /// <summary>Reads one %d: white space, a sign and decimal digits (strtol, the value cut to 32 bits).</summary>
    /// <param name="text">The text.</param>
    /// <param name="value">Receives the number.</param>
    /// <returns>The number stored, -1 at the end of the input.</returns>
    public static int Int(string text, ref int value)
    {
        string s = CString.CStr(text);
        int i = 0;
        while (i < s.Length && IsSpace(s[i]))
            ++i;
        if (i >= s.Length)
            return -1;
        bool negative = false;
        int start = i;
        if (s[i] == '+' || s[i] == '-')
            negative = s[i++] == '-';
        if (i >= s.Length || s[i] < '0' || s[i] > '9')
            return 0;
        long v = 0;
        bool overflow = false;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            int d = s[i++] - '0';
            if (v > (long.MaxValue - d) / 10)
                overflow = true;
            else
                v = v * 10 + d;
        }
        _ = start;
        long r = overflow ? (negative ? long.MinValue : long.MaxValue) : (negative ? -v : v);
        value = unchecked((int)r);
        return 1;
    }
}
