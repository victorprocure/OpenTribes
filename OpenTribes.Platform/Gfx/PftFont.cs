namespace OpenTribes.Platform.Gfx;

/// <summary>The char info of a glyph, 8 bytes (docs/renderer.md 2.6). The glyph rectangle on its sheet is (X, Y) to (X + Width - 1, Y + Height - 1), inclusive.</summary>
/// <param name="Sheet">The sheet index.</param>
/// <param name="X">The left of the glyph on the sheet.</param>
/// <param name="Y">The top of the glyph on the sheet.</param>
/// <param name="Width">The glyph width.</param>
/// <param name="Height">The glyph height.</param>
/// <param name="Baseline">The glyph baseline.</param>
public readonly record struct GlyphEntry(byte Sheet, byte X, byte Y, byte Width, byte Height, sbyte Baseline)
{
    /// <summary>Gets the glyph rectangle on its sheet, inclusive.</summary>
    public GfxRect SheetRect => new(X, Y, X + Width - 1, Y + Height - 1);
}

/// <summary>A <c>.pft</c> PFON font (docs/renderer.md 2.6).</summary>
public sealed class PftFont
{
    /// <summary>The font flag bits: proportional, fixed width, mono, bitmap, true type, rasterized, transparent, translucent, underlined, unicode and lower caps.</summary>
    public const uint FlagProportional = 1, FlagFixedWidth = 2, FlagMono = 4, FlagBitmap = 8, FlagTrueType = 0x10,
        FlagRasterized = 0x20, FlagTransparent = 0x40, FlagTranslucent = 0x80, FlagUnderlined = 0x100,
        FlagUnicode = 0x200, FlagLowerCaps = 0x400;

    /// <summary>The justification bits (docs/renderer.md 2.6): 1 left, 2 right, 4 center, 8 center vertically, 0x10 top, 0x20 bottom, 0x40 fit.</summary>
    public const uint JustLeft = 1, JustRight = 2, JustCenter = 4, JustVCenter = 8, JustTop = 0x10,
        JustBottom = 0x20, JustFit = 0x40;

    /// <summary>The file version.</summary>
    public uint Version;
    /// <summary>The flag bits, see <see cref="FlagProportional"/>.</summary>
    public uint Flags;
    /// <summary>The default justification bits.</summary>
    public uint Justification;
    /// <summary>The number of glyphs.</summary>
    public uint GlyphCount;
    /// <summary>The cell height.</summary>
    public int CellHeight;
    /// <summary>The glyph cell width.</summary>
    public int GlyphCellWidth;
    /// <summary>The ink colour.</summary>
    public uint InkColor;
    /// <summary>The background colour.</summary>
    public uint BgColor;
    /// <summary>The baseline.</summary>
    public int Baseline;
    /// <summary>The horizontal scale in 16.16 fixed point.</summary>
    public int ScaleX = 65536;
    /// <summary>The vertical scale in 16.16 fixed point.</summary>
    public int ScaleY = 65536;
    /// <summary>The spacing between characters.</summary>
    public int Spacing;
    /// <summary>The offset of the first character in the lookup table.</summary>
    public ushort LookupOffset;
    /// <summary>The lookup table from character to glyph index.</summary>
    public short[] Lookup = [];
    /// <summary>The glyphs.</summary>
    public GlyphEntry[] Glyphs = [];
    /// <summary>The glyph sheets.</summary>
    public PbmaArray Sheets = new();
    /// <summary>The attached palette, or null. No stock font has one.</summary>
    public PaletteBank? Palette;

    /// <summary>Parses a <c>.pft</c> file.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="font">Receives the font.</param>
    /// <param name="error">Receives the failure reason.</param>
    /// <returns>True on success.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, out PftFont? font, out string error)
    {
        font = null;
        error = "";
        try
        {
            var c = new Cursor(data);
            font = Read(ref c);
            return true;
        }
        catch (GfxFormatException e)
        {
            error = e.Message;
            return false;
        }
    }

    private static PftFont Read(ref Cursor c)
    {
        // 2.1: tag(4) size(4) version(4); the size is not used.
        if (c.Tag() != "PFON")
            throw new GfxFormatException("not a PFON font");
        c.U32();
        var f = new PftFont
        {
            Version = c.U32(),
            Flags = c.U32(),
            Justification = c.U32(),
            GlyphCount = c.U32(),
            CellHeight = (int)c.U32(),
            GlyphCellWidth = (int)c.U32(),
            InkColor = c.U32(),
            BgColor = c.U32(),
            Baseline = (int)c.U32(),
            ScaleX = (int)c.U32(),
            ScaleY = (int)c.U32(),
            Spacing = (int)c.U32()
        };
        if (f.GlyphCount > 65536)
            throw new GfxFormatException("bad glyph count");
        // lookupSize is read first, lookupOffset second.
        var lookupSize = c.U16();
        f.LookupOffset = c.U16();
        f.Lookup = new short[lookupSize];
        for (var i = 0; i < lookupSize; ++i)
            f.Lookup[i] = (short)c.U16();
        f.Glyphs = new GlyphEntry[f.GlyphCount];
        for (var i = 0; i < f.GlyphCount; ++i)
        {
            var b = c.Bytes(8);
            f.Glyphs[i] = new GlyphEntry(b[0], b[1], b[2], b[3], b[4], (sbyte)b[5]);
        }
        f.Sheets = PbmaArray.Read(ref c, false);
        // paletteAttached: 0 in all stock fonts; nonzero is followed by a PL98 palette (2.4).
        if (c.Remaining >= 4 && c.U32() != 0)
            f.Palette = PaletteBank.ReadPpl(ref c);
        return f;
    }

    /// <summary>Looks up a glyph (docs/renderer.md 2.6 lookupGlyph).</summary>
    /// <param name="ch">The character.</param>
    /// <param name="glyph">Receives the glyph.</param>
    /// <returns>True when present. Only 0 &lt;= index &lt; lookupSize counts as present, although the original also accepts index == lookupSize.</returns>
    public bool TryGetGlyph(int ch, out GlyphEntry glyph)
    {
        glyph = default;
        if ((Flags & FlagLowerCaps) != 0 && ch >= 'a' && ch <= 'z')
            ch -= 32;
        var i = ch - LookupOffset;
        if (i < 0 || i >= Lookup.Length)
            return false;
        int g = Lookup[i];
        if (g < 0 || g >= Glyphs.Length)
            return false;
        glyph = Glyphs[g];
        return true;
    }

    /// <summary>Gets the width of the blank cell for a missing character: max(glyphCellW, 5) (docs/renderer.md 2.6).</summary>
    public int MissingWidth => Math.Max(GlyphCellWidth, 5);
    /// <summary>Gets the height of the blank cell for a missing character: max(cellHeight, 1) (docs/renderer.md 2.6).</summary>
    public int MissingHeight => Math.Max(CellHeight, 1);

    /// <summary>Gets the width of a character.</summary>
    /// <param name="ch">The character.</param>
    /// <returns>The glyph width, or <see cref="MissingWidth"/>.</returns>
    public int CharWidth(int ch)
    {
        if ((Flags & FlagFixedWidth) != 0)
            return GlyphCellWidth;
        return TryGetGlyph(ch, out GlyphEntry g) ? g.Width : MissingWidth;
    }

    /// <summary>Gets the height of a character.</summary>
    /// <param name="ch">The character.</param>
    /// <returns>The glyph height, or <see cref="MissingHeight"/>.</returns>
    public int CharHeight(int ch) => TryGetGlyph(ch, out GlyphEntry g) ? g.Height : MissingHeight;

    /// <summary>Measures the width of a text (measureTextWidth).</summary>
    /// <param name="text">The text.</param>
    /// <returns>(sum(charWidth + spacing) - spacing) * scaleX &gt;&gt; 16. For an empty string it is (0 - spacing) * scaleX &gt;&gt; 16.</returns>
    public int MeasureTextWidth(string text)
    {
        long sum = 0;
        foreach (var ch in text)
            sum += CharWidth(ch) + Spacing;
        return (int)(((sum - Spacing) * ScaleX) >> 16);
    }

    /// <summary>Measures the height of a text (measureTextHeight).</summary>
    /// <param name="text">The text.</param>
    /// <returns>(max charHeight) * scaleY &gt;&gt; 16.</returns>
    public int MeasureTextHeight(string text)
    {
        var max = 0;
        foreach (var ch in text)
            max = Math.Max(max, CharHeight(ch));
        return (int)(((long)max * ScaleY) >> 16);
    }
}
