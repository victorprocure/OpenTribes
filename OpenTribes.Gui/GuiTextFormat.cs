namespace OpenTribes.Gui;

using OpenTribes.Platform.Gfx;

/// <summary>The text layout of SimGui::TextFormat (docs/gui.md 3.3.1): text with a table of ten fonts and inline tags, laid out into positioned text pieces and bitmaps.</summary>
/// <remarks>
/// <para>The command objective page builds one layout per objective line (FGHCommandObjectivePage). The tags are:</para>
/// <list type="bullet">
/// <item><description><c>&lt;fN&gt;</c> selects font N (0 to 9, a font that did not load is font 0).</description></item>
/// <item><description><c>&lt;jl&gt;</c>, <c>&lt;jc&gt;</c>, <c>&lt;jr&gt;</c> set left, centred and right justification.</description></item>
/// <item><description><c>&lt;lN&gt;</c> and <c>&lt;rN&gt;</c> set the left and right margin to N space widths of font 0.</description></item>
/// <item><description><c>&lt;n&gt;</c> clears every float.</description></item>
/// <item><description><c>&lt;sN&gt;</c> sets the line spacing (at least 2). A new line drops by spacing / 2 font heights.</description></item>
/// <item><description><c>&lt;vNAME&gt;</c> inserts the text of a console variable.</description></item>
/// <item><description><c>&lt;Bx,y:name&gt;</c> places a bitmap, floating left, centred along the line (<c>&lt;jc&gt;</c>) or right (<c>&lt;jr&gt;</c>).</description></item>
/// <item><description><c>&lt;&lt;</c> is a literal <c>&lt;</c>. A tab moves to the next stop of three space widths.</description></item>
/// </list>
/// </remarks>
public sealed class GuiTextFormat
{
    /// <summary>The number of fonts in the table.</summary>
    public const int FontCount = 10;
    /// <summary>The maximum number of bitmaps in a layout.</summary>
    public const int MaxBitmaps = 8;

    /// <summary>The font file of each table entry, as the objective page loads them.</summary>
    public static readonly string[] ObjectiveFonts =
    [
        "sf_white_10b.pft", "sf_green_10b.pft", "sf_yellow_10b.pft", "sf_orange214_10.pft", "sf_red_10b.pft",
        "sf_green_12.pft", "sf_yellow_12.pft", "sf_orange_12.pft", "sf_green_14.pft", "sf_yellow_14.pft",
    ];

    /// <summary>A positioned run of text.</summary>
    public sealed class Piece
    {
        /// <summary>The font table index.</summary>
        public int Font { get; set; }
        /// <summary>The justification: 0 left, 1 centred, 2 right.</summary>
        public int Just { get; set; }
        /// <summary>The x position in the layout.</summary>
        public int X { get; set; }
        /// <summary>The y position in the layout.</summary>
        public int Y { get; set; }
        /// <summary>The room the justification works in.</summary>
        public int Avail { get; set; }
        /// <summary>The text of the run.</summary>
        public string Text { get; set; } = "";
    }

    /// <summary>A positioned bitmap.</summary>
    public sealed class Image
    {
        /// <summary>The bitmap.</summary>
        public PbmpBitmap Bitmap { get; set; } = null!;
        /// <summary>The x position in the layout.</summary>
        public int X { get; set; }
        /// <summary>The y position in the layout.</summary>
        public int Y { get; set; }
        /// <summary>The justification: 0 left float, 1 centred, 2 right float.</summary>
        public int Just { get; set; }
        /// <summary>The width the bitmap takes including its margins.</summary>
        public int Width { get; set; }
    }

    /// <summary>The text pieces in layout order.</summary>
    public List<Piece> Pieces { get; } = [];
    /// <summary>The bitmaps in layout order.</summary>
    public List<Image> Images { get; } = [];
    /// <summary>The height of the layout: the y of the end of the text plus 2.</summary>
    public int Height { get; set; }         // y of the end of the text + 2
    /// <summary>The width of the widest line.</summary>
    public int MaxWidth { get; set; }
    /// <summary>True when the layout completed.</summary>
    public bool Valid { get; set; }

    private readonly PftFont?[] _fonts;
    private readonly Func<string, PbmpBitmap?> _bitmaps;
    private readonly Func<string, string> _variable;
    private readonly int _width;
    private int _font, _just, _x, _y, _left, _right, _leftBase, _rightBase, _leftFloat = -1, _rightFloat = -1, _unit, _minWidth;

    private GuiTextFormat(PftFont?[] fonts, Func<string, PbmpBitmap?> bitmaps, Func<string, string> variable, int width)
    {
        _fonts = fonts;
        _bitmaps = bitmaps;
        _variable = variable;
        _width = width;
    }

    /// <summary>Lays a text out at a width.</summary>
    /// <param name="text">The text with its tags.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="fonts">The table of ten fonts. Entries can be null.</param>
    /// <param name="bitmaps">Looks a bitmap up by name.</param>
    /// <param name="variable">Looks a console variable up by name, or null for none.</param>
    /// <returns>The layout. One that cannot fit the widest glyph, or has an unterminated or over long tag, stops at that point and keeps what it has.</returns>
    public static GuiTextFormat Layout(string text, int width, PftFont?[] fonts, Func<string, PbmpBitmap?> bitmaps, Func<string, string>? variable = null)
    {
        var l = new GuiTextFormat(fonts, bitmaps, variable ?? (_ => ""), width);
        l.Run(text);
        return l;
    }

    private PftFont? Font => _fonts[_font];
    private static int Scaled(int v, int scale) => (int)(((long)v * scale) >> 16);
    private static int Advance(PftFont f, char c) => Scaled(f.CharWidth(c), f.ScaleX);
    private static int LineHeight(PftFont? f) => f == null ? 0 : Scaled(f.CellHeight, f.ScaleY);
    private int Measure(string s) => Font?.MeasureTextWidth(s) ?? 0;

    /// <summary>The line spacing (+0x224): 2 at the start, <c>&lt;sN&gt;</c> sets N but never below 2.</summary>
    /// <remarks>A new line drops by the font height times spacing * 0.5, cut to an integer (0053701c, constant 005370b4), so <c>&lt;s3&gt;</c> makes 19 pixels of a 13 pixel font.</remarks>
    private int _spacing = 2;

    /// <summary>Starts a new line: the cursor drops by the font height and returns to the left margin. A float that the line has passed releases its margin.</summary>
    private void NewLine()
    {
        _y += (int)(_spacing * 0.5 * LineHeight(Font));
        if (_leftFloat > 0 && _leftFloat <= _y)
        {
            _leftFloat = -1;
            _left = _leftBase;
        }
        if (_rightFloat > 0 && _rightFloat <= _y)
        {
            _rightFloat = -1;
            _right = _rightBase;
        }
        _x = _left;
    }

    private bool FloatsClear() => !(_leftFloat > 0 && _y < _leftFloat) && !(_rightFloat > 0 && _y < _rightFloat);

    private void Run(string text)
    {
        if (_fonts[0] == null)
            return;
        var widest = 0;
        foreach (var f in _fonts)
            if (f != null)
                widest = Math.Max(widest, Advance(f, 'W'));
        _minWidth = widest;
        if (widest > _width)
            return;
        _unit = Advance(_fonts[0]!, ' ');
        var tab = _unit * 3;
        _right = _rightBase = _width - 1;
        var i = 0;
        var complete = true;
        while (i < text.Length)
        {
            int start = i, j = i;
            var ended = false;
            while (!ended)
            {
                if (j >= text.Length)
                    ended = true;
                else if (text[j] == '\t')
                {
                    if (j == start)
                    {
                        var stop = _left;
                        while (stop <= _x)
                            stop += tab;
                        _x = stop;
                        ++start;
                        ++j;
                    }
                    else
                        ended = true;
                }
                else if (text[j] == '\n')
                {
                    if (j == start)
                    {
                        NewLine();
                        ++start;
                        ++j;
                    }
                    else
                        ended = true;
                }
                else if (text[j] == '<')
                {
                    var hasClose = text.IndexOf('>', j) >= 0;
                    if (j == start && hasClose)
                    {
                        if (j + 1 < text.Length && text[j + 1] == '<')
                        {
                            // `<<`: the second `<` starts a text run
                            start = j + 1;
                            j += 2;
                            ended = true;
                        }
                        else
                        {
                            var close = text.IndexOf('>', j + 1);
                            if (close < 0 || close - (j + 1) > 255)
                            {
                                complete = false;
                                goto done;
                            }
                            var tag = text.Substring(j + 1, close - j - 1);
                            j = start = close + 1;
                            if (!Tag(tag, ref text, ref j, ref start))
                            {
                                complete = false;
                                goto done;
                            }
                        }
                    }
                    else if (!hasClose)
                        ++j;
                    else
                        ended = true;
                }
                else
                    ++j;
            }
            if (j > start)
                Place(text[start..j]);
            i = j;
        }
        done:
        if (!complete)
            return;
        if (_left < _x)
            NewLine();
        var bottom = Math.Max(_leftFloat, _rightFloat);
        if (!FloatsClear())
            _y = bottom;
        Height = _y + 2;
        MaxWidth = Math.Max(MaxWidth, _x);
        Justify();
        Valid = true;
    }

    private bool Tag(string tag, ref string text, ref int j, ref int start)
    {
        if (tag.Length == 0)
            return true;
        var arg = tag[1..];
        switch (char.ToLowerInvariant(tag[0]))
        {
            case 'b':
                AddBitmap(arg);
                break;
            case 'f':
            {
                var n = Math.Clamp(Atoi(arg), 0, FontCount - 1);
                _font = _fonts[n] == null ? 0 : n;
                break;
            }
            case 'j':
                _just = arg.Length > 0 && char.ToLowerInvariant(arg[0]) == 'c' ? 1 : arg.Length > 0 && char.ToLowerInvariant(arg[0]) == 'r' ? 2 : 0;
                break;
            case 'l':
            {
                _leftBase = Math.Min(_unit * Math.Max(0, Atoi(arg)), _width - _minWidth - 1);
                _left = _leftBase;
                if (_x < _left)
                    _x = _left;
                ShrinkMargins();
                break;
            }
            case 'r':
            {
                _rightBase = Math.Max(_width - 1 - (_unit * Math.Max(0, Atoi(arg))), _minWidth + 1);
                _right = _rightBase;
                if (_right - Advance(Font!, 'W') < _x)
                    NewLine();
                ShrinkMargins();
                break;
            }
            case 'n':
                if (_left < _x)
                    NewLine();
                while (!FloatsClear())
                    NewLine();
                _x = _left;
                break;
            case 's':
                _spacing = Math.Max(2, Atoi(arg));
                break;
            case 'v':
            {
                // the variable's text replaces the tag in the text still to be laid out
                var value = arg.Length > 0 ? _variable(arg) : "";
                text = text[..(start - (tag.Length + 2))] + value + text[start..];
                j = start -= tag.Length + 2;
                break;
            }
            default:
                break;
        }
        return true;
    }

    // A margin pair closer than the widest glyph moves the left margin back.
    private void ShrinkMargins()
    {
        if (_right - _left < _minWidth)
        {
            _left = _right - _minWidth;
            if (_left < 0)
            {
                _left = 0;
                _right = _left + _minWidth;
            }
        }
    }

    private static int Atoi(string s)
    {
        int i = 0, sign = 1, v = 0;
        while (i < s.Length && s[i] == ' ')
            ++i;
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            sign = s[i++] == '-' ? -1 : 1;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
            v = (v * 10) + (s[i++] - '0');
        return sign * v;
    }

    private void AddBitmap(string arg)
    {
        if (Images.Count >= MaxBitmaps)
            return;
        int dx = 4, dy = 0;
        var name = arg;
        var colon = arg.IndexOf(':');
        if (colon >= 0)
        {
            var comma = arg.IndexOf(',');
            if (comma >= 0)
            {
                dx = Atoi(arg[..comma]);
                dy = Atoi(arg[(comma + 1)..]);
            }
            name = arg[(colon + 1)..];
        }
        var bmp = _bitmaps(name);
        if (bmp == null)
            return;
        var w = bmp.Width + (dx * 2);
        if (w > _width)
            return;
        while (_right - _x + 1 < w)
            NewLine();
        var im = new Image { Bitmap = bmp, Just = _just, Width = w };
        if (_just == 0)
        {
            im.X = _x + dx;
            im.Y = _y + dy;
            _x += w;
            MaxWidth = Math.Max(MaxWidth, _x);
            _leftFloat = Math.Max(_leftFloat, bmp.Height + _y + (dy * 2));
            _left = _x;
        }
        else if (_just == 1)
        {
            im.X = _left + dx;
            im.Y = _y + dy;
            _leftFloat = Math.Max(_leftFloat, bmp.Height + _y + (dy * 2));
            _left += w;
            MaxWidth = Math.Max(MaxWidth, w);
        }
        else
        {
            _rightFloat = Math.Max(_rightFloat, bmp.Height + _y + (dy * 2));
            _right -= w;
            im.X = _right + dx;
            im.Y = _y + dy;
        }
        Images.Add(im);
    }

    /// <summary>Puts one run of text into lines.</summary>
    /// <remarks>A line breaks at the last space that fits. A word wider than the space left drops to the next line, and one wider than a whole line breaks at the character that fits.</remarks>
    private void Place(string run)
    {
        var f = Font;
        if (f == null)
            return;
        if (_right - _x + 1 < Advance(f, 'W'))
            NewLine();
        var pos = 0;
        while (pos < run.Length)
        {
            var avail = _right - _x + 1;
            var rest = run[pos..];
            var fit = rest.Length;
            while (fit > 0 && Measure(rest[..fit]) > avail)
                --fit;
            if (fit == rest.Length)
            {
                AddPiece(rest);
                _x += Measure(rest);
                MaxWidth = Math.Max(MaxWidth, _x);
                return;
            }
            // break before the last space within the part that fits (or right at the fit when a space follows)
            var cut = fit;
            while (cut > 0 && rest[cut] != ' ')
                --cut;
            if (cut == 0)
            {
                if (_x > _left + Advance(f, 'W'))
                {
                    // other text is on this line: the word starts a new one
                    if (rest.Length > 0 && rest[0] == ' ')
                        ++pos;
                    NewLine();
                    continue;
                }
                cut = Math.Max(fit, 1);
            }
            var piece = rest[..cut];
            AddPiece(piece);
            _x += Measure(piece);
            MaxWidth = Math.Max(MaxWidth, _x);
            NewLine();
            pos += cut;
            while (pos < run.Length && run[pos] == ' ')
                ++pos;
        }
    }

    private void AddPiece(string text) =>
        // a right justified piece centres or aligns inside the room from the cursor, the others inside the margins
        Pieces.Add(new Piece { Font = _font, Just = _just, X = _x, Y = _y, Text = text, Avail = _just == 2 ? _right - _x : _right - _left });

    // Moves centred and right justified pieces and bitmaps by the room left on their line.
    private void Justify()
    {
        for (var i = 0; i < Pieces.Count;)
        {
            var p = Pieces[i];
            int k = i, total = 0;
            if (p.Just == 0)
            {
                ++i;
                continue;
            }
            while (k < Pieces.Count && Pieces[k].Y == p.Y && Pieces[k].Just == p.Just)
            {
                var f = _fonts[Pieces[k].Font];
                total += f == null ? 0 : f.MeasureTextWidth(Pieces[k].Text);
                ++k;
            }
            for (var m = i; m < k; ++m)
                Pieces[m].X += p.Just == 1 ? (Pieces[m].Avail - total) / 2 : Pieces[m].Avail - total;
            i = k;
        }
        for (var i = 0; i < Images.Count;)
        {
            var im = Images[i];
            if (im.Just != 1)
            {
                ++i;
                continue;
            }
            int k = i, total = 0;
            while (k < Images.Count && Images[k].Y == im.Y && Images[k].Just == 1)
                total += Images[k++].Width;
            for (var m = i; m < k; ++m)
                Images[m].X += (_width - total) / 2;
            i = k;
        }
    }

    /// <summary>Draws the pieces, then the bitmaps.</summary>
    /// <param name="p">The painter.</param>
    /// <param name="ox">The x of the layout origin.</param>
    /// <param name="oy">The y of the layout origin.</param>
    public void Draw(GuiPaint p, int ox, int oy)
    {
        foreach (var pc in Pieces)
            if (_fonts[pc.Font] is PftFont f && pc.Text.Length > 0)
                p.Text(f, ox + pc.X, oy + pc.Y, pc.Text);
        foreach (var im in Images)
            p.Bitmap(im.Bitmap, ox + im.X, oy + im.Y);
    }
}
