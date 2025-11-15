// SimGui::TextFormat layout (docs/gui.md 3.3.1): text with a table of ten fonts and inline tags, laid out into positioned text
// pieces and bitmaps. The command objective page builds one layout per objective line (`FGHCommandObjectivePage`).
//   <fN> font N (0..9, a font that did not load is font 0)   <jl> <jc> <jr> left, centred, right justification
//   <lN> <rN> left and right margin of N space widths of font 0   <n> clear every float   <sN> line spacing (no effect on the layout)
//   <vNAME> the text of a console variable   <Bx,y:name> bitmap, floating left, centred along the line (<jc>) or right (<jr>)
//   <<  a literal `<`   tab: next stop of three space widths   new line
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

public sealed class GuiTextFormat
{
    public const int FontCount = 10;
    public const int MaxBitmaps = 8;

    // The font file of each table entry, as the objective page loads them.
    public static readonly string[] ObjectiveFonts =
    {
        "sf_white_10b.pft", "sf_green_10b.pft", "sf_yellow_10b.pft", "sf_orange214_10.pft", "sf_red_10b.pft",
        "sf_green_12.pft", "sf_yellow_12.pft", "sf_orange_12.pft", "sf_green_14.pft", "sf_yellow_14.pft",
    };

    public sealed class Piece
    {
        public int Font, Just, X, Y, Avail;
        public string Text = "";
    }

    public sealed class Image
    {
        public PbmpBitmap Bitmap = null!;
        public int X, Y, Just, Width;
    }

    public readonly List<Piece> Pieces = new();
    public readonly List<Image> Images = new();
    public int Height;         // y of the end of the text + 2
    public int MaxWidth;
    public bool Valid;

    private readonly PftFont?[] mFonts;
    private readonly Func<string, PbmpBitmap?> mBitmaps;
    private readonly Func<string, string> mVariable;
    private readonly int mWidth;
    private int mFont, mJust, mX, mY, mLeft, mRight, mLeftBase, mRightBase, mLeftFloat = -1, mRightFloat = -1, mUnit, mMinWidth;

    private GuiTextFormat(PftFont?[] fonts, Func<string, PbmpBitmap?> bitmaps, Func<string, string> variable, int width)
    {
        mFonts = fonts;
        mBitmaps = bitmaps;
        mVariable = variable;
        mWidth = width;
    }

    // Lays `text` out at `width` pixels. A layout that cannot fit the widest glyph, or with an unterminated or over long tag,
    // stops at that point and keeps what it has.
    public static GuiTextFormat Layout(string text, int width, PftFont?[] fonts, Func<string, PbmpBitmap?> bitmaps, Func<string, string>? variable = null)
    {
        var l = new GuiTextFormat(fonts, bitmaps, variable ?? (_ => ""), width);
        l.Run(text);
        return l;
    }

    private PftFont? Font => mFonts[mFont];
    private static int Scaled(int v, int scale) => (int)(((long)v * scale) >> 16);
    private static int Advance(PftFont f, char c) => Scaled(f.CharWidth(c), f.ScaleX);
    private static int LineHeight(PftFont? f) => f == null ? 0 : Scaled(f.CellHeight, f.ScaleY);
    private int Measure(string s) => Font?.MeasureTextWidth(s) ?? 0;

    // Starts a new line: the cursor drops by the font height and returns to the left margin; a float that the line has passed
    // releases its margin.
    private void NewLine()
    {
        mY += LineHeight(Font);
        if (mLeftFloat > 0 && mLeftFloat <= mY)
        {
            mLeftFloat = -1;
            mLeft = mLeftBase;
        }
        if (mRightFloat > 0 && mRightFloat <= mY)
        {
            mRightFloat = -1;
            mRight = mRightBase;
        }
        mX = mLeft;
    }

    private bool FloatsClear() => !(mLeftFloat > 0 && mY < mLeftFloat) && !(mRightFloat > 0 && mY < mRightFloat);

    private void Run(string text)
    {
        if (mFonts[0] == null)
            return;
        int widest = 0;
        foreach (PftFont? f in mFonts)
            if (f != null)
                widest = Math.Max(widest, Advance(f, 'W'));
        mMinWidth = widest;
        if (widest > mWidth)
            return;
        mUnit = Advance(mFonts[0]!, ' ');
        int tab = mUnit * 3;
        mRight = mRightBase = mWidth - 1;
        int i = 0;
        bool complete = true;
        while (i < text.Length)
        {
            int start = i, j = i;
            bool ended = false;
            while (!ended)
            {
                if (j >= text.Length)
                    ended = true;
                else if (text[j] == '\t')
                {
                    if (j == start)
                    {
                        int stop = mLeft;
                        while (stop <= mX)
                            stop += tab;
                        mX = stop;
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
                    bool hasClose = text.IndexOf('>', j) >= 0;
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
                            int close = text.IndexOf('>', j + 1);
                            if (close < 0 || close - (j + 1) > 255)
                            {
                                complete = false;
                                goto done;
                            }
                            string tag = text.Substring(j + 1, close - j - 1);
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
                Place(text.Substring(start, j - start));
            i = j;
        }
    done:
        if (!complete)
            return;
        if (mLeft < mX)
            NewLine();
        int bottom = Math.Max(mLeftFloat, mRightFloat);
        if (!FloatsClear())
            mY = bottom;
        Height = mY + 2;
        MaxWidth = Math.Max(MaxWidth, mX);
        Justify();
        Valid = true;
    }

    private bool Tag(string tag, ref string text, ref int j, ref int start)
    {
        if (tag.Length == 0)
            return true;
        string arg = tag.Substring(1);
        switch (char.ToLowerInvariant(tag[0]))
        {
            case 'b':
                AddBitmap(arg);
                break;
            case 'f':
            {
                int n = Math.Clamp(Atoi(arg), 0, FontCount - 1);
                mFont = mFonts[n] == null ? 0 : n;
                break;
            }
            case 'j':
                mJust = arg.Length > 0 && char.ToLowerInvariant(arg[0]) == 'c' ? 1 : arg.Length > 0 && char.ToLowerInvariant(arg[0]) == 'r' ? 2 : 0;
                break;
            case 'l':
            {
                mLeftBase = Math.Min(mUnit * Math.Max(0, Atoi(arg)), mWidth - mMinWidth - 1);
                mLeft = mLeftBase;
                if (mX < mLeft)
                    mX = mLeft;
                ShrinkMargins();
                break;
            }
            case 'r':
            {
                mRightBase = Math.Max(mWidth - 1 - mUnit * Math.Max(0, Atoi(arg)), mMinWidth + 1);
                mRight = mRightBase;
                if (mRight - Advance(Font!, 'W') < mX)
                    NewLine();
                ShrinkMargins();
                break;
            }
            case 'n':
                if (mLeft < mX)
                    NewLine();
                while (!FloatsClear())
                    NewLine();
                mX = mLeft;
                break;
            case 's':
                break;
            case 'v':
            {
                // the variable's text replaces the tag in the text still to be laid out
                string value = arg.Length > 0 ? mVariable(arg) : "";
                text = text.Substring(0, start - (tag.Length + 2)) + value + text.Substring(start);
                j = start = start - (tag.Length + 2);
                break;
            }
        }
        return true;
    }

    // A margin pair closer than the widest glyph moves the left margin back.
    private void ShrinkMargins()
    {
        if (mRight - mLeft < mMinWidth)
        {
            mLeft = mRight - mMinWidth;
            if (mLeft < 0)
            {
                mLeft = 0;
                mRight = mLeft + mMinWidth;
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
            v = v * 10 + (s[i++] - '0');
        return sign * v;
    }

    private void AddBitmap(string arg)
    {
        if (Images.Count >= MaxBitmaps)
            return;
        int dx = 4, dy = 0;
        string name = arg;
        int colon = arg.IndexOf(':');
        if (colon >= 0)
        {
            int comma = arg.IndexOf(',');
            if (comma >= 0)
            {
                dx = Atoi(arg.Substring(0, comma));
                dy = Atoi(arg.Substring(comma + 1));
            }
            name = arg.Substring(colon + 1);
        }
        PbmpBitmap? bmp = mBitmaps(name);
        if (bmp == null)
            return;
        int w = bmp.Width + dx * 2;
        if (w > mWidth)
            return;
        while (mRight - mX + 1 < w)
            NewLine();
        var im = new Image { Bitmap = bmp, Just = mJust, Width = w };
        if (mJust == 0)
        {
            im.X = mX + dx;
            im.Y = mY + dy;
            mX += w;
            MaxWidth = Math.Max(MaxWidth, mX);
            mLeftFloat = Math.Max(mLeftFloat, bmp.Height + mY + dy * 2);
            mLeft = mX;
        }
        else if (mJust == 1)
        {
            im.X = mLeft + dx;
            im.Y = mY + dy;
            mLeftFloat = Math.Max(mLeftFloat, bmp.Height + mY + dy * 2);
            mLeft += w;
            MaxWidth = Math.Max(MaxWidth, w);
        }
        else
        {
            mRightFloat = Math.Max(mRightFloat, bmp.Height + mY + dy * 2);
            mRight -= w;
            im.X = mRight + dx;
            im.Y = mY + dy;
        }
        Images.Add(im);
    }

    // Puts one run of text into lines. A line breaks at the last space that fits; a word wider than the space left drops to the
    // next line, and one wider than a whole line breaks at the character that fits.
    private void Place(string run)
    {
        PftFont? f = Font;
        if (f == null)
            return;
        if (mRight - mX + 1 < Advance(f, 'W'))
            NewLine();
        int pos = 0;
        while (pos < run.Length)
        {
            int avail = mRight - mX + 1;
            string rest = run.Substring(pos);
            int fit = rest.Length;
            while (fit > 0 && Measure(rest.Substring(0, fit)) > avail)
                --fit;
            if (fit == rest.Length)
            {
                AddPiece(rest, avail);
                mX += Measure(rest);
                MaxWidth = Math.Max(MaxWidth, mX);
                return;
            }
            // break before the last space within the part that fits (or right at the fit when a space follows)
            int cut = fit;
            while (cut > 0 && rest[cut] != ' ')
                --cut;
            if (cut == 0)
            {
                if (mX > mLeft + Advance(f, 'W'))
                {
                    // other text is on this line: the word starts a new one
                    if (rest.Length > 0 && rest[0] == ' ')
                        ++pos;
                    NewLine();
                    continue;
                }
                cut = Math.Max(fit, 1);
            }
            string piece = rest.Substring(0, cut);
            AddPiece(piece, avail);
            mX += Measure(piece);
            MaxWidth = Math.Max(MaxWidth, mX);
            NewLine();
            pos += cut;
            while (pos < run.Length && run[pos] == ' ')
                ++pos;
        }
    }

    private void AddPiece(string text, int avail)
    {
        // a right justified piece centres or aligns inside the room from the cursor, the others inside the margins
        Pieces.Add(new Piece { Font = mFont, Just = mJust, X = mX, Y = mY, Text = text, Avail = mJust == 2 ? mRight - mX : mRight - mLeft });
    }

    // Moves centred and right justified pieces and bitmaps by the room left on their line.
    private void Justify()
    {
        for (int i = 0; i < Pieces.Count;)
        {
            Piece p = Pieces[i];
            int k = i, total = 0;
            if (p.Just == 0)
            {
                ++i;
                continue;
            }
            while (k < Pieces.Count && Pieces[k].Y == p.Y && Pieces[k].Just == p.Just)
            {
                PftFont? f = mFonts[Pieces[k].Font];
                total += f == null ? 0 : f.MeasureTextWidth(Pieces[k].Text);
                ++k;
            }
            for (int m = i; m < k; ++m)
                Pieces[m].X += p.Just == 1 ? (Pieces[m].Avail - total) / 2 : Pieces[m].Avail - total;
            i = k;
        }
        for (int i = 0; i < Images.Count;)
        {
            Image im = Images[i];
            if (im.Just != 1)
            {
                ++i;
                continue;
            }
            int k = i, total = 0;
            while (k < Images.Count && Images[k].Y == im.Y && Images[k].Just == 1)
                total += Images[k++].Width;
            for (int m = i; m < k; ++m)
                Images[m].X += (mWidth - total) / 2;
            i = k;
        }
    }

    // Draws the pieces, then the bitmaps, with the layout origin at (ox, oy).
    public void Draw(GuiPaint p, int ox, int oy)
    {
        foreach (Piece pc in Pieces)
            if (mFonts[pc.Font] is PftFont f && pc.Text.Length > 0)
                p.Text(f, ox + pc.X, oy + pc.Y, pc.Text);
        foreach (Image im in Images)
            p.Bitmap(im.Bitmap, ox + im.X, oy + im.Y);
    }
}
