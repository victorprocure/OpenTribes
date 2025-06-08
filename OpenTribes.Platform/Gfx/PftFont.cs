using System.Buffers.Binary;

namespace OpenTribes.Platform.Gfx;

// docs/renderer.md 2.6: char info, 8 bytes. The glyph rectangle on its sheet is
// (X, Y) to (X + Width - 1, Y + Height - 1), inclusive.
public readonly record struct GlyphEntry(byte Sheet, byte X, byte Y, byte Width, byte Height, sbyte Baseline)
{
    public GfxRect SheetRect => new(X, Y, X + Width - 1, Y + Height - 1);
}

// docs/renderer.md 2.6: `.pft` PFON font.
public sealed class PftFont
{
    public const uint FlagProportional = 1, FlagFixedWidth = 2, FlagMono = 4, FlagBitmap = 8, FlagTrueType = 0x10,
        FlagRasterized = 0x20, FlagTransparent = 0x40, FlagTranslucent = 0x80, FlagUnderlined = 0x100,
        FlagUnicode = 0x200, FlagLowerCaps = 0x400;

    // Justification bits (2.6): 1 left, 2 right, 4 center, 8 center vertically, 0x10 top, 0x20 bottom, 0x40 fit.
    public const uint JustLeft = 1, JustRight = 2, JustCenter = 4, JustVCenter = 8, JustTop = 0x10,
        JustBottom = 0x20, JustFit = 0x40;

    public uint Version;
    public uint Flags;
    public uint Justification;
    public uint GlyphCount;
    public int CellHeight;
    public int GlyphCellWidth;
    public uint InkColor;
    public uint BgColor;
    public int Baseline;
    public int ScaleX = 65536;   // 16.16
    public int ScaleY = 65536;
    public int Spacing;
    public ushort LookupOffset;
    public short[] Lookup = Array.Empty<short>();
    public GlyphEntry[] Glyphs = Array.Empty<GlyphEntry>();
    public PbmaArray Sheets = new();
    public PaletteBank? Palette;

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
        var f = new PftFont { Version = c.U32() };
        f.Flags = c.U32();
        f.Justification = c.U32();
        f.GlyphCount = c.U32();
        f.CellHeight = (int)c.U32();
        f.GlyphCellWidth = (int)c.U32();
        f.InkColor = c.U32();
        f.BgColor = c.U32();
        f.Baseline = (int)c.U32();
        f.ScaleX = (int)c.U32();
        f.ScaleY = (int)c.U32();
        f.Spacing = (int)c.U32();
        if (f.GlyphCount > 65536)
            throw new GfxFormatException("bad glyph count");
        // lookupSize is read first, lookupOffset second.
        ushort lookupSize = c.U16();
        f.LookupOffset = c.U16();
        f.Lookup = new short[lookupSize];
        for (int i = 0; i < lookupSize; ++i)
            f.Lookup[i] = (short)c.U16();
        f.Glyphs = new GlyphEntry[f.GlyphCount];
        for (int i = 0; i < f.GlyphCount; ++i)
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

    // 2.6 lookupGlyph. Only 0 <= index < lookupSize counts as present, although the original also
    // accepts index == lookupSize.
    public bool TryGetGlyph(int ch, out GlyphEntry glyph)
    {
        glyph = default;
        if ((Flags & FlagLowerCaps) != 0 && ch >= 'a' && ch <= 'z')
            ch -= 32;
        int i = ch - LookupOffset;
        if (i < 0 || i >= Lookup.Length)
            return false;
        int g = Lookup[i];
        if (g < 0 || g >= Glyphs.Length)
            return false;
        glyph = Glyphs[g];
        return true;
    }

    // Missing character: a blank cell of width max(glyphCellW, 5) and height max(cellHeight, 1) (2.6).
    public int MissingWidth => Math.Max(GlyphCellWidth, 5);
    public int MissingHeight => Math.Max(CellHeight, 1);

    public int CharWidth(int ch)
    {
        if ((Flags & FlagFixedWidth) != 0)
            return GlyphCellWidth;
        return TryGetGlyph(ch, out GlyphEntry g) ? g.Width : MissingWidth;
    }

    public int CharHeight(int ch) => TryGetGlyph(ch, out GlyphEntry g) ? g.Height : MissingHeight;

    // measureTextWidth: (sum(charWidth + spacing) - spacing) * scaleX >> 16. Empty string: (0 - spacing) * scaleX >> 16.
    public int MeasureTextWidth(string text)
    {
        long sum = 0;
        foreach (char ch in text)
            sum += CharWidth(ch) + Spacing;
        return (int)(((sum - Spacing) * ScaleX) >> 16);
    }

    // measureTextHeight: (max charHeight) * scaleY >> 16.
    public int MeasureTextHeight(string text)
    {
        int max = 0;
        foreach (char ch in text)
            max = Math.Max(max, CharHeight(ch));
        return (int)(((long)max * ScaleY) >> 16);
    }
}
