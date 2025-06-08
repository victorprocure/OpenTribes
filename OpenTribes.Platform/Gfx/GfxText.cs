namespace OpenTribes.Platform.Gfx;

// docs/renderer.md 4 "Text": drawTextAt and drawTextInRect on top of the blit calls of any device.
public static class GfxText
{
    // drawTextAt: pen starts at x; per character: glyph rect on its sheet, top-left
    // y = point.y + baseline + glyphBaseline - (rect.bottom - rect.top) + 1, blitRegion, advance by the
    // glyph width (proportional) or the cell width (fixed) plus spacing. Missing characters draw nothing and
    // advance by the blank cell width. Returns the bottom y of the lowest glyph drawn (y when none).
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

    // drawTextInRect: justification bits 1 left, 2 right, 4 center; 8 vertical center, 0x20 bottom, 0x10 top;
    // 0x40 fits the text to the rectangle by scaling (restored afterwards). No word wrap.
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
