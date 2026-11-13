namespace OpenTribes.Platform.Gfx;

/// <summary>Draws text on top of the blit calls of any device (docs/renderer.md 4 "Text").</summary>
public static class GfxText
{
    /// <summary>Draws text at a point (drawTextAt).</summary>
    /// <param name="dev">The device.</param>
    /// <param name="font">The font.</param>
    /// <param name="x">The pen start x.</param>
    /// <param name="y">The point y.</param>
    /// <param name="text">The text.</param>
    /// <returns>The bottom y of the lowest glyph drawn, y when none.</returns>
    /// <remarks>Per character the glyph rectangle on its sheet is blitted with its top left at y = point.y + baseline + glyphBaseline - (rect.bottom - rect.top) + 1. The pen advances by the glyph width (proportional) or the cell width (fixed) plus spacing. Missing characters draw nothing and advance by the blank cell width.</remarks>
    public static int DrawAt(IGfxDevice dev, PftFont font, int x, int y, string text) =>
        Draw(dev, font, x, y, text, font.ScaleX, font.ScaleY);

    private static int Draw(IGfxDevice dev, PftFont font, int x, int y, string text, int scaleX, int scaleY)
    {
        bool scaled = scaleX != 65536 || scaleY != 65536;
        int pen = x, bottom = y;
        foreach (char ch in text)
        {
            int advance = font.CharWidth(ch);
            if (font.TryGetGlyph(ch, out GlyphEntry g) && font.Sheets.Get(g.Sheet) is PbmpBitmap sheet)
            {
                GfxRect r = g.SheetRect;
                int yOff = font.Baseline + g.Baseline - (r.Bottom - r.Top);
                if (!scaled)
                {
                    int gy = y + yOff + 1;
                    dev.BlitRegion(sheet, r, pen, gy);
                    bottom = Math.Max(bottom, gy + r.Height - 1);
                }
                else
                {
                    int gy = y + (int)(((long)yOff * scaleY) >> 16) + 1;
                    int w = Math.Max(1, (int)(((long)r.Width * scaleX) >> 16));
                    int h = Math.Max(1, (int)(((long)r.Height * scaleY) >> 16));
                    dev.BlitRegionStretch(sheet, r, pen, gy, w, h);
                    bottom = Math.Max(bottom, gy + h - 1);
                }
            }
            pen += (int)(((long)(advance + font.Spacing) * scaleX) >> 16);
        }
        return bottom;
    }

    /// <summary>Draws justified text in a rectangle (drawTextInRect).</summary>
    /// <param name="dev">The device.</param>
    /// <param name="font">The font.</param>
    /// <param name="rect">The rectangle.</param>
    /// <param name="text">The text.</param>
    /// <returns>The bottom y of the lowest glyph drawn.</returns>
    /// <remarks>The justification bits are 1 left, 2 right, 4 center, 8 vertical center, 0x20 bottom and 0x10 top. 0x40 fits the text to the rectangle by scaling, restored afterwards. There is no word wrap.</remarks>
    public static int DrawInRect(IGfxDevice dev, PftFont font, GfxRect rect, string text)
    {
        uint j = font.Justification;
        int extent = font.MeasureTextWidth(text);
        int x = rect.Left;
        if ((j & PftFont.JustRight) != 0)
            x = rect.Right - extent + 1;
        else if ((j & PftFont.JustCenter) != 0)
            x = rect.Left + (rect.Width - extent) / 2;

        int y = rect.Top;
        if ((j & PftFont.JustVCenter) != 0)
            y = rect.Top + (rect.Height - font.Baseline) / 2;
        else if ((j & PftFont.JustBottom) != 0)
            y = rect.Bottom - font.Baseline + 1;

        int sx = font.ScaleX, sy = font.ScaleY;
        if ((j & PftFont.JustFit) != 0 && extent > 0 && font.CellHeight > 0)
        {
            sx = (int)(((long)rect.Width << 16) / extent);
            sy = (int)(((long)rect.Height << 16) / font.CellHeight);
        }
        return Draw(dev, font, x, y, text, sx, sy);
    }
}
