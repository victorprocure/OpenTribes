// Headless render test (ROADMAP M9 C2): draws synthetic bitmaps in all four blend modes plus text, filled
// rectangles, a line, a clip rectangle and a stretch into an offscreen OpenGL 3.3 context and compares the
// read-back pixels with values computed here (docs/renderer.md 3.3, 4, 5.4). When no GL context can be made
// (no SDL3, no GL driver) the tests print "SKIPPED: reason" and pass without checking anything.
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Gl;
using Xunit.Abstractions;
using static OpenTribes.Tests.GfxFixtures;

namespace OpenTribes.Tests;

public class HeadlessRenderTests
{
    private const int W = 64, H = 48;
    private static readonly byte[] sBackground = { 40, 80, 120 };

    private readonly ITestOutputHelper mOut;

    public HeadlessRenderTests(ITestOutputHelper output) => mOut = output;

    // Palette: index i has color (r, g, b) from the table below; everything else is black.
    private static PaletteBank TestPalette()
    {
        var rgba = new byte[1024];
        void Set(int i, byte r, byte g, byte b)
        {
            rgba[i * 4] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
        }
        Set(0, 9, 9, 9);
        Set(1, 200, 100, 50);
        Set(2, 10, 20, 30);
        Set(3, 255, 255, 255);
        Set(4, 90, 40, 20);
        return PaletteBank.FromColors(rgba, 256);
    }

    // 4 x 4 bitmap: row 0 index 0, row 1 index 1, row 2 index 2, row 3 index 3.
    private static PbmpBitmap Rows(uint attrs)
    {
        var px = new byte[16];
        for (int i = 0; i < 16; ++i)
            px[i] = (byte)(i / 4);
        return Bitmap(Pbmp(4, 4, px, attrs));
    }

    private GlWindow? OpenWindow()
    {
        var failures = new List<string>();
        // First choice: the SDL offscreen video driver (no display needed); then whatever SDL picks.
        foreach (string? driver in new string?[] { "offscreen", null })
        {
            GlWindow? w = GlWindow.TryCreate("test", W, H, hidden: true, driver, out string err);
            if (w != null)
            {
                mOut.WriteLine($"RAN: video driver {w.VideoDriver}, OpenGL {w.GlVersion}");
                return w;
            }
            failures.Add($"{driver ?? "default"}: {err}");
        }
        mOut.WriteLine("SKIPPED: no OpenGL 3.3 context could be made (" + string.Join("; ", failures) + ")");
        return null;
    }

    private static (int r, int g, int b) Px(byte[] rgba, int x, int y)
    {
        int o = (y * W + x) * 4;
        return (rgba[o], rgba[o + 1], rgba[o + 2]);
    }

    private static void Near((int r, int g, int b) got, (int r, int g, int b) want, int tol, string what)
    {
        bool ok = Math.Abs(got.r - want.r) <= tol && Math.Abs(got.g - want.g) <= tol && Math.Abs(got.b - want.b) <= tol;
        Assert.True(ok, $"{what}: got {got}, expected {want} +-{tol}");
    }

    private static (int, int, int) Exact(byte r, byte g, byte b) => (r, g, b);

    [Fact]
    public void BlendModesAreExactOrWithinTolerance() // 2.2.1, 4 blits, 5.4 blend functions
    {
        using GlWindow? win = OpenWindow();
        if (win == null)
            return;
        using var gd = new GlGfxDevice(win);
        IGfxDevice dev = gd;
        dev.SetPalette(TestPalette(), 1f);
        dev.BeginFrame();
        Assert.Equal((W, H), (dev.Width, dev.Height));
        dev.ClearRgb(sBackground[0], sBackground[1], sBackground[2]);
        // Five 4 x 4 bitmaps side by side at y = 2: opaque, transparent, translucent, additive, subtractive.
        uint[] attrs = { 0, 1, 4, 0x10, 0x20 };
        for (int i = 0; i < attrs.Length; ++i)
            dev.Blit(Rows(attrs[i]), 2 + i * 6, 2);
        dev.EndFrame();
        Assert.Equal(0u, gd.GetError());
        byte[] px = dev.ReadPixels();
        Assert.Equal(W * H * 4, px.Length);

        // Outside every bitmap: the background, exactly. Row 0 of the image is the top row of the read-back.
        Assert.Equal(Exact(40, 80, 120), Px(px, 60, 40));
        Assert.Equal(Exact(40, 80, 120), Px(px, 1, 1));
        Assert.Equal(Exact(40, 80, 120), Px(px, 2, 6)); // just below the first bitmap

        // Opaque (attribute 0): every index shows its palette color, index 0 included. Exact.
        // Bitmap texels are packed like the stock upload (005d3ecc, 005d7148; kind 1 subpalette gives 5551): c -> ((c >> 3) << 3) | (c >> 5).
        // 9 -> 8, 10,20,30 -> 8,16,24, 200,100,50 -> 206,99,49, 255 stays. The background is a flat clear and stays exact.
        Assert.Equal(Exact(8, 8, 8), Px(px, 3, 2));
        Assert.Equal(Exact(198, 99, 49), Px(px, 3, 3));
        Assert.Equal(Exact(8, 16, 24), Px(px, 5, 4));
        Assert.Equal(Exact(255, 255, 255), Px(px, 2, 5));
        Assert.Equal(Exact(198, 99, 49), Px(px, 5, 3)); // last column of the bitmap
        Assert.Equal(Exact(40, 80, 120), Px(px, 6, 3));  // the column behind it

        // Transparent (attribute 1): index 0 shows the background, the others their color. Exact.
        int x1 = 2 + 6;
        Assert.Equal(Exact(40, 80, 120), Px(px, x1 + 1, 2));
        Assert.Equal(Exact(198, 99, 49), Px(px, x1 + 1, 3));
        Assert.Equal(Exact(255, 255, 255), Px(px, x1 + 3, 5));

        // Translucent (attribute 4): 50% blend [I]: 0.5 * src + 0.5 * dst, tolerance 2 for fixed-point rounding.
        int x2 = 2 + 12;
        Near(Px(px, x2 + 1, 3), ((198 + 40) / 2, (99 + 80) / 2, (49 + 120) / 2), 2, "translucent index 1");
        Near(Px(px, x2 + 1, 4), ((8 + 40) / 2, (16 + 80) / 2, (24 + 120) / 2), 2, "translucent index 2");

        // Additive (attribute 0x10): dst + src, clamped, tolerance 1.
        int x3 = 2 + 18;
        Near(Px(px, x3 + 1, 3), (Math.Min(255, 198 + 40), Math.Min(255, 99 + 80), Math.Min(255, 49 + 120)), 1, "additive index 1");
        Near(Px(px, x3 + 1, 5), (255, 255, 255), 1, "additive index 3");
        Near(Px(px, x3 + 1, 2), (40 + 8, 80 + 8, 120 + 8), 1, "additive index 0 (not keyed: adds its color)");

        // Subtractive (attribute 0x20): dst * (1 - src), tolerance 2.
        int x4 = 2 + 24;
        Near(Px(px, x4 + 1, 3), (40 * (255 - 206) / 255, 80 * (255 - 99) / 255, 120 * (255 - 49) / 255), 2, "subtractive index 1");
        Near(Px(px, x4 + 1, 5), (0, 0, 0), 2, "subtractive index 3");
    }

    [Fact]
    public void FlatFillsLineClipFlipAndStretch() // 1.7 clip, 4 rects, lines, flips, stretch
    {
        using GlWindow? win = OpenWindow();
        if (win == null)
            return;
        using var gd = new GlGfxDevice(win);
        IGfxDevice dev = gd;
        dev.SetPalette(TestPalette(), 1f);
        dev.BeginFrame();
        dev.Clear(2); // palette entry 2 (10, 20, 30), no gamma
        // Filled rectangle, inclusive: (4,4)-(9,6) covers 6 x 3 pixels.
        dev.FillRect(new GfxRect(4, 4, 9, 6), 1);
        // Outline (20,4)-(25,9): the four edges only.
        dev.DrawRectOutline(new GfxRect(20, 4, 25, 9), 3);
        // Horizontal line from (30,4) to (37,4), both ends included; a diagonal from (30,10) to (35,15).
        dev.DrawLine(30, 4, 37, 4, 4);
        dev.DrawLine(30, 10, 35, 15, 4);
        // Clip: a fill across the clip rectangle changes only the pixels inside it.
        dev.SetClip(new GfxRect(4, 20, 7, 22));
        dev.FillRect(new GfxRect(0, 18, 20, 30), 3);
        dev.SetClip(null);
        // Flip: a 2 x 2 bitmap with indices 1 2 / 3 4 drawn with flip X and flip Y.
        PbmpBitmap quad = Bitmap(Pbmp(2, 2, new byte[] { 1, 2, 0, 0, 3, 4, 0, 0 }, attrs: 0));
        dev.Blit(quad, 40, 4, BlitFlip.X);
        dev.Blit(quad, 44, 4, BlitFlip.Y);
        dev.Blit(quad, 48, 4, BlitFlip.X | BlitFlip.Y);
        // Stretch: a solid 2 x 2 bitmap to 6 x 6 (any filter gives the same color for a solid bitmap).
        dev.BlitStretch(Bitmap(Pbmp(2, 2, new byte[] { 1, 1, 0, 0, 1, 1, 0, 0 })), 40, 20, 6, 6);
        dev.EndFrame();
        byte[] px = dev.ReadPixels();

        var bg = (10, 20, 30);
        Assert.Equal(Exact(10, 20, 30), Px(px, 0, 0)); // clear
        for (int y = 4; y <= 6; ++y)
            for (int x = 4; x <= 9; ++x)
                Assert.Equal(Exact(200, 100, 50), Px(px, x, y));
        Assert.Equal(bg, Px(px, 10, 5));  // right + 1 is outside
        Assert.Equal(bg, Px(px, 4, 7));   // bottom + 1 is outside
        Assert.Equal(bg, Px(px, 3, 4));

        Assert.Equal(Exact(255, 255, 255), Px(px, 20, 4));  // outline corners and edges
        Assert.Equal(Exact(255, 255, 255), Px(px, 25, 9));
        Assert.Equal(Exact(255, 255, 255), Px(px, 22, 4));
        Assert.Equal(Exact(255, 255, 255), Px(px, 20, 7));
        Assert.Equal(Exact(255, 255, 255), Px(px, 25, 6));
        Assert.Equal(bg, Px(px, 22, 6)); // interior untouched

        for (int x = 30; x <= 37; ++x)
            Assert.Equal(Exact(90, 40, 20), Px(px, x, 4));
        Assert.Equal(bg, Px(px, 38, 4));
        for (int i = 0; i <= 5; ++i)
            Assert.Equal(Exact(90, 40, 20), Px(px, 30 + i, 10 + i)); // both end points included

        for (int y = 18; y <= 30; ++y)
            for (int x = 0; x <= 20; ++x)
            {
                bool inside = x >= 4 && x <= 7 && y >= 20 && y <= 22;
                Assert.Equal(inside ? Exact(255, 255, 255) : Exact(10, 20, 30), Px(px, x, y));
            }

        // flip X: row 0 reads 2 1, row 1 reads 4 3; flip Y: row 0 reads 3 4; both: 4 3 / 2 1
        // Bitmap texels go through the stock gamma table (i - 1 for 1..254) and the 5551 packing (kind 1 subpalette): c -> ((g >> 3) << 3) | (g >> 5), so 10,20,30 -> 8,8,24; 200,100,50 -> 198,99,49; 90,40,20 -> 90,33,16; 255 stays.
        Assert.Equal(Exact(8, 16, 24), Px(px, 40, 4));  // index 2
        Assert.Equal(Exact(198, 99, 49), Px(px, 41, 4)); // index 1
        Assert.Equal(Exact(90, 33, 16), Px(px, 40, 5));  // index 4
        Assert.Equal(Exact(255, 255, 255), Px(px, 44, 4)); // flip Y: index 3
        Assert.Equal(Exact(90, 33, 16), Px(px, 45, 4));    // index 4
        Assert.Equal(Exact(198, 99, 49), Px(px, 44, 5));  // index 1
        Assert.Equal(Exact(90, 33, 16), Px(px, 48, 4));    // both: index 4
        Assert.Equal(Exact(255, 255, 255), Px(px, 49, 4)); // index 3
        Assert.Equal(Exact(198, 99, 49), Px(px, 49, 5));  // second row reads 2 1

        for (int y = 20; y < 26; ++y)
            for (int x = 40; x < 46; ++x)
                Near(Px(px, x, y), (198, 99, 49), 1, $"stretch {x},{y}");
        Assert.Equal(bg, Px(px, 46, 20));
        Assert.Equal(bg, Px(px, 40, 26));
    }

    [Fact]
    public void TextThroughASyntheticFontAndGamma() // 4 text, 3.3 gamma table
    {
        using GlWindow? win = OpenWindow();
        if (win == null)
            return;
        using var gd = new GlGfxDevice(win);
        IGfxDevice dev = gd;
        dev.SetPalette(TestPalette(), 1f);

        // Sheet 16 x 8, transparent (attribute 9): 'A' is a 3 x 4 block of index 3 at (0,0), 'B' a 5 x 6 block of index 1 at (4,1).
        var sheet = new byte[16 * 8];
        for (int y = 0; y < 4; ++y)
            for (int x = 0; x < 3; ++x)
                sheet[y * 16 + x] = 3;
        for (int y = 1; y < 7; ++y)
            for (int x = 4; x < 9; ++x)
                sheet[y * 16 + x] = 1;
        byte[] file = Pft(PftFont.FlagProportional | PftFont.FlagTransparent, cellH: 7, cellW: 6, baseline: 6, spacing: 1,
                          lookupOffset: 65, new short[] { 0, 1 },
                          new (byte, byte, byte, byte, byte, sbyte)[] { (0, 0, 0, 3, 4, 0), (0, 4, 1, 5, 6, -1) },
                          Pbma(new List<byte[]> { Pbmp(16, 8, sheet, attrs: 9, pidx: 0xFFFFFFFF) }));
        Assert.True(PftFont.TryParse(file, out PftFont? font, out string err), err);

        dev.BeginFrame();
        dev.ClearRgb(0, 0, 0);
        int bottom = dev.DrawText(font!, 10, 20, "AB");
        dev.EndFrame();
        byte[] px = dev.ReadPixels();

        // drawTextAt: 'A' at x 10, y = 20 + 6 + 0 - 3 + 1 = 24 (3 x 4 block); 'B' at x = 10 + 3 + 1 = 14,
        // y = 20 + 6 - 1 - 5 + 1 = 21 (5 x 6 block). Everything else stays black (index 0 of the sheet is transparent).
        for (int y = 0; y < H; ++y)
            for (int x = 0; x < W; ++x)
            {
                (int, int, int) want = (0, 0, 0);
                if (x >= 10 && x <= 12 && y >= 24 && y <= 27)
                    want = (255, 255, 255);
                if (x >= 14 && x <= 18 && y >= 21 && y <= 26)
                    want = (198, 99, 49); // 5551 packing of (200, 100, 50) through the stock gamma table (199, 99, 49), see BlendModesAreExactOrWithinTolerance
                Assert.Equal(want, Px(px, x, y));
            }
        Assert.Equal(27, bottom);

        // Gamma (3.3): the texture path applies the 256-byte table to every palette color: exact.
        dev.SetPalette(TestPalette(), 2f);
        byte[] table = PaletteTables.GammaTable(2f);
        dev.BeginFrame();
        dev.ClearRgb(0, 0, 0);
        dev.DrawText(font!, 10, 20, "AB");
        dev.EndFrame();
        px = dev.ReadPixels();
        // The gamma table output is then packed to 5551 like the stock upload: c -> ((c >> 3) << 3) | (c >> 5).
        static byte Q(byte c) => (byte)(((c >> 3) << 3) | (c >> 5));
        Assert.Equal(Exact(Q(table[200]), Q(table[100]), Q(table[50])), Px(px, 16, 24));
        Assert.Equal(Exact(Q(table[255]), Q(table[255]), Q(table[255])), Px(px, 11, 25));
        Assert.Equal(0u, gd.GetError());
    }

    [Fact]
    public void TextureCacheIsKeyedPerBitmapAndFlushedByPalette() // 3.4
    {
        using GlWindow? win = OpenWindow();
        if (win == null)
            return;
        using var gd = new GlGfxDevice(win);
        IGfxDevice dev = gd;
        dev.SetPalette(TestPalette());
        PbmpBitmap a = Rows(0), b = Rows(1);
        dev.BeginFrame();
        dev.Blit(a, 0, 0);
        dev.Blit(a, 8, 0);
        dev.Blit(b, 16, 0);
        dev.EndFrame();
        Assert.Equal(2, gd.CachedTextureCount); // one texture per bitmap, not per draw
        dev.FlushTexture(a);
        Assert.Equal(1, gd.CachedTextureCount);
        dev.SetPalette(TestPalette(), 1.5f);     // a new palette or gamma flushes every texture
        Assert.Equal(0, gd.CachedTextureCount);
    }
}
