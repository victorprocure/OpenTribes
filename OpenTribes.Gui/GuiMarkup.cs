// Formatted text of the HUD text controls (docs/gui.md 3.3 `FearGuiFormattedText`, `CenterPrint`, the chat display):
// `<fN>` selects one of the seven fonts of esf.strings.cs (f0 normal green, f1 pale yellow, f2 bright yellow, f3 bright green,
// f4 gray, f5 red, f6 italic). Every other tag of the form `<letters and digits>` (`<jc>`, `<jl>`, `<lN>`, `<t>` ...) is not
// specified by the spec; it is dropped from the drawn text so that markup never shows (open item, client-scripts.md 9).
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

public static class GuiMarkup
{
    // A piece of text in one font; Font -1 is the control's own font.
    public readonly record struct Run(int Font, string Text);

    // The tag names of the seven fonts (the comments of esf.strings.cs).
    private static readonly string[] kFontTags =
        { "IDFNT_FONTIS_3", "IDFNT_FONTIS_2", "IDFNT_FONTIS_1", "IDFNT_FONTIS_4", "IDFNT_FONTIS_5", "IDFNT_FONTIS_6", "IDFNT_FONTIS_3I" };

    public static string FontTagName(int n) => n >= 0 && n < kFontTags.Length ? kFontTags[n] : "";

    // Splits `text` into runs. `<fN>` with N in 0..6 switches the font for the text after it; other tags are removed.
    public static List<Run> Parse(string text, int startFont = -1)
    {
        var runs = new List<Run>();
        var cur = new System.Text.StringBuilder();
        int font = startFont;
        void Flush()
        {
            if (cur.Length > 0)
                runs.Add(new Run(font, cur.ToString()));
            cur.Clear();
        }
        for (int i = 0; i < text.Length; ++i)
        {
            if (text[i] == '<' && TagAt(text, i, out int len))
            {
                string name = text.Substring(i + 1, len - 2);
                if (name.Length == 2 && name[0] == 'f' && name[1] >= '0' && name[1] <= '6')
                {
                    Flush();
                    font = name[1] - '0';
                }
                i += len - 1;
                continue;
            }
            cur.Append(text[i]);
        }
        Flush();
        return runs;
    }

    // `<name>` with a name of letters and digits that starts with a letter.
    private static bool TagAt(string s, int i, out int length)
    {
        length = 0;
        int j = i + 1;
        if (j >= s.Length || !char.IsAsciiLetter(s[j]))
            return false;
        while (j < s.Length && char.IsAsciiLetterOrDigit(s[j]))
            ++j;
        if (j >= s.Length || s[j] != '>')
            return false;
        length = j - i + 1;
        return true;
    }

    // `<vNAME>` [code 005370b8]: the console variable's text replaces the tag in the text still to lay out, so an expansion that holds
    // tags is read as markup. `<<` is a literal `<` and is left alone. The count of expansions is bounded so that a variable that names
    // itself cannot loop.
    public static string ExpandVariables(string text, Func<string, string> variable)
    {
        if (text.IndexOf("<v", StringComparison.OrdinalIgnoreCase) < 0)
            return text;
        int budget = 64;
        for (int i = 0; i < text.Length; ++i)
        {
            if (text[i] != '<')
                continue;
            if (i + 1 < text.Length && text[i + 1] == '<')
            {
                ++i;
                continue;
            }
            int j = text.IndexOf('>', i + 1);
            if (j < 0)
                break;
            if (j - i - 1 > 255)
                break;
            if (j - i - 1 >= 2 && (text[i + 1] == 'v' || text[i + 1] == 'V') && budget-- > 0)
            {
                text = text.Substring(0, i) + variable(text.Substring(i + 2, j - i - 2)) + text.Substring(j + 1);
                --i;
            }
        }
        return text;
    }

    // The justification a text asks for with `<jl>`, `<jc>` or `<jr>` (the last one counts): 0 left, 1 centred, 2 right.
    public static int Justification(string text)
    {
        int best = -1, jl = text.LastIndexOf("<jl>", StringComparison.Ordinal), jc = text.LastIndexOf("<jc>", StringComparison.Ordinal),
            jr = text.LastIndexOf("<jr>", StringComparison.Ordinal);
        int pos = Math.Max(jl, Math.Max(jc, jr));
        if (pos >= 0)
            best = pos == jc ? 1 : pos == jr ? 2 : 0;
        return Math.Max(best, 0);
    }

    public static string Strip(string text) => string.Concat(Parse(text).Select(r => r.Text));

    // Draws the runs left to right; returns the width. `def` is the font of runs with no `<fN>` (and of fonts that do not load).
    public static int Draw(GuiPaint p, GuiHost host, PftFont? def, int x, int y, IEnumerable<Run> runs, Func<int, PftFont?>? fontOf = null)
    {
        int x0 = x;
        foreach (Run r in runs)
        {
            PftFont? f = (fontOf != null ? fontOf(r.Font) : null) ?? host.MarkupFont(r.Font) ?? def;
            if (f == null)
                continue;
            p.Text(f, x, y, r.Text);
            x += GuiPaint.TextWidth(f, r.Text) + f.Spacing;
        }
        return x - x0;
    }

    public static int Width(GuiHost host, PftFont? def, IEnumerable<Run> runs, Func<int, PftFont?>? fontOf = null)
    {
        int w = 0;
        foreach (Run r in runs)
        {
            PftFont? f = (fontOf != null ? fontOf(r.Font) : null) ?? host.MarkupFont(r.Font) ?? def;
            if (f != null)
                w += GuiPaint.TextWidth(f, r.Text) + f.Spacing;
        }
        return w;
    }

    // Greedy word wrap of runs to `width`; a line holds runs (a word never splits unless it is longer than the line).
    public static List<List<Run>> Wrap(GuiHost host, PftFont? def, List<Run> runs, int width, Func<int, PftFont?>? fontOf = null)
    {
        var lines = new List<List<Run>>();
        var line = new List<Run>();
        int lineW = 0;
        foreach (Run r in runs)
        {
            foreach (string piece in SplitKeepSpaces(r.Text))
            {
                var one = new Run(r.Font, piece);
                int w = Width(host, def, new[] { one }, fontOf);
                if (lineW + w > width && lineW > 0 && piece.Trim().Length > 0)
                {
                    lines.Add(line);
                    line = new List<Run>();
                    lineW = 0;
                }
                if (lineW == 0 && piece.Trim().Length == 0)
                    continue;
                if (line.Count > 0 && line[^1].Font == one.Font)
                    line[^1] = new Run(one.Font, line[^1].Text + one.Text);
                else
                    line.Add(one);
                lineW += w;
            }
        }
        if (line.Count > 0)
            lines.Add(line);
        return lines;
    }

    // Wrap of marked-up text: each line of the text is a paragraph, the font carries over from one to the next.
    public static List<List<Run>> WrapText(GuiHost host, PftFont? def, string text, int width, Func<int, PftFont?>? fontOf = null)
    {
        var lines = new List<List<Run>>();
        int font = -1;
        foreach (string para in text.Replace("\r", "").Split('\n'))
        {
            List<Run> runs = Parse(para, font);
            if (runs.Count == 0)
                lines.Add(new List<Run>());
            else
            {
                lines.AddRange(Wrap(host, def, runs, width, fontOf));
                font = runs[^1].Font;
            }
        }
        return lines;
    }

    private static IEnumerable<string> SplitKeepSpaces(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            int j = i;
            bool sp = s[i] == ' ';
            while (j < s.Length && (s[j] == ' ') == sp)
                ++j;
            yield return s[i..j];
            i = j;
        }
    }
}
