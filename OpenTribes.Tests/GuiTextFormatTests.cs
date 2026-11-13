// docs/gui.md 3.3.1: the TextFormat layout of the objective page (fonts 0..9, bitmaps, justification, margins, wrapping).
namespace OpenTribes.Tests;

using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;

public sealed class GuiTextFormatTests
{
    // Fixed width fonts: font n is (6 + n) pixels per character and 10 + n high, spacing 0.
    private static PftFont?[] Fonts()
    {
        var f = new PftFont?[GuiTextFormat.FontCount];
        for (var i = 0; i < f.Length; ++i)
            f[i] = new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 6 + i, CellHeight = 10 + i };
        return f;
    }

    private static PbmpBitmap Bmp(int w, int h) => new() { Width = w, Height = h };

    private static GuiTextFormat Layout(string text, int width, Func<string, PbmpBitmap?>? bmps = null, Func<string, string>? vars = null) =>
        GuiTextFormat.Layout(text, width, Fonts(), bmps ?? (_ => null), vars);

    [Fact]
    public void FontTagSelectsTheTenFontTableAndClampsIt()
    {
        var l = Layout("<f5>ab<f1>cd<f99>ef<f-3>gh", 400);
        Assert.Equal([5, 1, 9, 0], l.Pieces.Select(p => p.Font).ToArray());
        Assert.Equal([0, 22, 22 + (2 * 7), 22 + (2 * 7) + (2 * 15)], l.Pieces.Select(p => p.X).ToArray());
    }

    [Fact]
    public void ALineEndsWithItsFontHeightPlusTwo()
    {
        Assert.Equal(10 + 2, Layout("hello", 400).Height);
        Assert.Equal(10 + 5 + 2, Layout("<f5>hello", 400).Height);
        Assert.Equal(2, Layout("", 400).Height);
        Assert.Equal((10 * 2) + 2, Layout("a\nb", 400).Height);
    }

    [Fact]
    public void LineSpacingScalesTheDropOfANewLine() // 0053701c: height * spacing * 0.5 cut to an integer, spacing 2 at the start and never below 2
    {
        Assert.Equal([0, 15], Layout("<s3>a\nb", 400).Pieces.Select(p => p.Y).ToArray());     // 10 * 3 / 2
        Assert.Equal([0, 10], Layout("<s1>a\nb", 400).Pieces.Select(p => p.Y).ToArray());     // clamped to 2
        Assert.Equal([0, 25], Layout("<s5>a\nb", 400).Pieces.Select(p => p.Y).ToArray());
    }

    [Fact]
    public void WordsWrapAtTheLastSpaceThatFits()
    {
        var l = Layout("aaaa bbbb cccc", 60);      // 10 characters of 6 px fit
        Assert.Equal(new[] { "aaaa bbbb", "cccc" }, l.Pieces.Select(p => p.Text).ToArray());
        Assert.Equal([0, 10], l.Pieces.Select(p => p.Y).ToArray());
        Assert.Equal((10 * 2) + 2, l.Height);
    }

    [Fact]
    public void CentreAndRightJustificationMoveEachLineByItsRoom()
    {
        var c = Layout("<jc>abcd", 100);
        Assert.Equal((100 - 1 - 24) / 2, c.Pieces[0].X);
        var r = Layout("<jr>abcd", 100);
        Assert.Equal(100 - 1 - 24, r.Pieces[0].X);
    }

    [Fact]
    public void LeftMarginIsCountedInSpaceWidthsOfFontZero()
    {
        var l = Layout("<l3>x", 200);
        Assert.Equal(18, l.Pieces[0].X);
    }

    [Fact]
    public void TabAdvancesToTheNextStopOfThreeSpaces()
    {
        var l = Layout("a\tb", 200);
        Assert.Equal([0, 18], l.Pieces.Select(p => p.X).ToArray());
    }

    [Fact]
    public void TwoCentredBitmapsSitSideBySideCentredOnTheLine()
    {
        var bmps = new Dictionary<string, PbmpBitmap> { ["a.bmp"] = Bmp(100, 40), ["b.bmp"] = Bmp(60, 40) };
        var l = Layout("<jc><B0,0:a.bmp><B0,0:b.bmp>", 400, n => bmps.GetValueOrDefault(n));
        Assert.Equal(2, l.Images.Count);
        Assert.Equal((400 - 160) / 2, l.Images[0].X);
        Assert.Equal(((400 - 160) / 2) + 100, l.Images[1].X);
        Assert.Equal(40 + 2, l.Height);
    }

    [Fact]
    public void ABitmapTagOffsetsAreInTheWidthTwice()
    {
        var bmps = new Dictionary<string, PbmpBitmap> { ["a.bmp"] = Bmp(20, 30) };
        var l = Layout("<B5,2:a.bmp>text", 400, n => bmps.GetValueOrDefault(n));
        Assert.Equal(5, l.Images[0].X);
        Assert.Equal(2, l.Images[0].Y);
        Assert.Equal(30 + 4 + 2, l.Height);
        Assert.Equal(30, l.Pieces[0].X);                       // the text follows the 20 + 2 * 5 pixels of the floating bitmap
    }

    [Fact]
    public void ALiteralLessThanAndUnknownTagsAreHandled()
    {
        var l = Layout("a<<b<q>c", 200);
        Assert.Equal("a<bc", string.Concat(l.Pieces.Select(p => p.Text)));
    }

    [Fact]
    public void VariableTagInsertsTheConsoleText()
    {
        var l = Layout("x<vname>y", 200, vars: n => n == "name" ? "NAME" : "");
        Assert.Equal("xNAMEy", string.Concat(l.Pieces.Select(p => p.Text)));
    }

    [Fact]
    public void WidthBelowTheWidestGlyphLeavesTheLayoutEmpty()
    {
        var l = Layout("abc", 5);
        Assert.False(l.Valid);
        Assert.Empty(l.Pieces);
    }
}
