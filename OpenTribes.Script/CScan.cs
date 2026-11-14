namespace OpenTribes.Script;

using OpenTribes.Core;

/// <summary>The C library sscanf conversions the game layer uses: "%f %f %f", "%f %f %f %f %f %f" and "%d" (std::sscanf of src/game).</summary>
/// <remarks>White space is skipped, then a decimal, hexadecimal, infinity or NaN number is read and stored to float in the current mode. The double parse runs in the mode of <c>Fpu</c> and the store follows, which together truncate like the single rounding of strtof.</remarks>
public static class CScan
{
    private static bool IsSpace(char c) => c is ' ' or (>= '\t' and <= '\r');
    private static bool Hex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    /// <summary>Reads one %f at a position, skipping white space first.</summary>
    /// <param name="s">The text.</param>
    /// <param name="pos">The position, advanced past the number. Unchanged when no number starts there.</param>
    /// <param name="f">Receives the number.</param>
    /// <returns>False when no number starts at the position.</returns>
    public static bool ScanFloat(string s, ref int pos, out float f)
    {
        f = 0;
        var i = pos;
        while (i < s.Length && IsSpace(s[i]))
            ++i;
        var start = i;
        var negative = false;
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
            var scale = 0;
            var point = false;
            for (; i < s.Length; ++i)
            {
                var c = s[i];
                if (c == '.' && !point)
                {
                    point = true;
                    continue;
                }
                if (!Hex(c))
                    break;
                m = (m * 16) + (c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10);
                if (point)
                    scale -= 4;
            }
            if (i < s.Length && (s[i] == 'p' || s[i] == 'P'))
            {
                var j = i + 1;
                var eneg = false;
                if (j < s.Length && (s[j] == '+' || s[j] == '-'))
                    eneg = s[j++] == '-';
                if (j < s.Length && s[j] >= '0' && s[j] <= '9')
                {
                    var e = 0;
                    while (j < s.Length && s[j] >= '0' && s[j] <= '9')
                        e = Math.Min((e * 10) + (s[j++] - '0'), 100000);
                    scale += eneg ? -e : e;
                    i = j;
                }
            }
            f = Fpu.Store(Math.ScaleB(negative ? -m : m, scale));
            pos = i;
            return true;
        }
        var digits = 0;
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
            var j = i + 1;
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
        var s = CString.CStr(text);
        var pos = 0;
        var n = 0;
        for (; n < output.Length; ++n)
        {
            while (pos < s.Length && IsSpace(s[pos]))
                ++pos;
            if (pos >= s.Length)
                return n == 0 ? -1 : n;
            if (!ScanFloat(s, ref pos, out var f))
                return n;
            output[n] = f;
        }
        return n;
    }

    /// <summary>Reads one %d: white space, a sign and decimal digits (strtol, the value cut to 32 bits).</summary>
    /// <param name="text">The text.</param>
    /// <param name="value">Receives the number.</param>
    /// <returns>The number stored, -1 at the end of the input.</returns>
    public static int ScanInt(string text, ref int value)
    {
        var s = CString.CStr(text);
        var i = 0;
        while (i < s.Length && IsSpace(s[i]))
            ++i;
        if (i >= s.Length)
            return -1;
        var negative = false;
        var start = i;
        if (s[i] is '+' or '-')
            negative = s[i++] == '-';
        if (i >= s.Length || s[i] < '0' || s[i] > '9')
            return 0;
        long v = 0;
        var overflow = false;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            var d = s[i++] - '0';
            if (v > (long.MaxValue - d) / 10)
                overflow = true;
            else
                v = (v * 10) + d;
        }
        _ = start;
        var r = overflow ? (negative ? long.MinValue : long.MaxValue) : (negative ? -v : v);
        value = unchecked((int)r);
        return 1;
    }
}
