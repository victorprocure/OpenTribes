// Unit tests of the graphics file loaders (docs/renderer.md section 2, 3.3, 4 text) on synthetic bytes (R5, R9).
using System.Text;
using OpenTribes.Core;
using OpenTribes.Platform.Gfx;
using static OpenTribes.Tests.GfxFixtures;

namespace OpenTribes.Tests;

public class GfxFormatTests
{
    // ---- 2.2 PBMP ----

    [Fact]
    public void PbmpStockLayoutStridePaddingAndMips() // 2.2.1, 2.2.2, 2.2.3
    {
        // 6 x 4 pixels: stride 8 (4-byte aligned rows). DETL 3: level 1 is 3 x 2, level 2 is 1 x 1.
        var data = new List<byte>();
        for (int y = 0; y < 4; ++y)
            for (int x = 0; x < 8; ++x)
                data.Add((byte)(y * 10 + x)); // columns 6 and 7 are padding
        data.AddRange(new byte[] { 90, 91, 92, 93, 94, 95 }); // level 1
        data.Add(99);                                         // level 2
        while (data.Count % 4 != 0)
            data.Add(0);
        byte[] file = Pbmp(6, 4, data.ToArray(), attrs: 1, pidx: 0x10c6, detl: 3);

        PbmpBitmap b = Bitmap(file);
        Assert.Equal((6, 4, 8), (b.Width, b.Height, b.Depth));
        Assert.Equal(8, b.Stride);
        Assert.Equal(32, b.Pixels.Length);
        Assert.Equal(32, b.Pixels[3 * 8 + 2]); // row 3 col 2 = 3 * 10 + 2; rows are top-down
        Assert.Equal(3, b.MipCount);
        Assert.Equal(2, b.MipLevels.Count);
        Assert.Equal(new byte[] { 90, 91, 92, 93, 94, 95 }, b.MipLevels[0]);
        Assert.Equal(new byte[] { 99 }, b.MipLevels[1]);
        Assert.Equal(0x10c6u, b.PaletteId);
        // 2.2.1: the loader ORs bit3 (owns memory) into the stored attributes.
        Assert.Equal(BitmapAttributes.Transparent | BitmapAttributes.OwnsMemory, b.Attributes);
        Assert.Equal(BlendMode.Transparent, b.Blend);
    }

    [Fact]
    public void PbmpMipLevelSizesOf128x128() // 2.2.2: 21845 bytes of levels for DETL 8
    {
        int n = 128 * 128 + 64 * 64 + 32 * 32 + 16 * 16 + 8 * 8 + 4 * 4 + 2 * 2 + 1;
        Assert.Equal(21845, n);
        byte[] data = Pad4(new byte[n]);
        Assert.Equal(21848, data.Length); // the chunk is padded to a multiple of 4
        PbmpBitmap b = Bitmap(Pbmp(128, 128, data, attrs: 8, detl: 8));
        Assert.Equal(7, b.MipLevels.Count);
        Assert.Single(b.MipLevels[6]);
    }

    [Fact]
    public void PbmpPidxAbsentSelectsFirstSubpalette() // 2.2.1 optional PiDX, 2.2.3
    {
        PbmpBitmap b = Bitmap(Pbmp(2, 2, new byte[8]));
        Assert.Equal(0xFFFFFFFFu, b.PaletteId);
        Assert.Equal(1, b.MipCount); // DETL absent
        Assert.Empty(b.MipLevels);
    }

    [Fact]
    public void PbmpCountFieldDecidesHowManyChunksAreRead() // 2.2.1 "count"
    {
        // count = 1 (the data chunk only): the PiDX chunk behind it is not read, whatever the PBMP size says.
        byte[] file = Pbmp(2, 2, new byte[8], pidx: 7, count: 1, topSize: 3);
        PbmpBitmap b = Bitmap(file);
        Assert.Equal(1u, b.ChunkCount);
        Assert.Equal(0xFFFFFFFFu, b.PaletteId);
        // The top 8 bits of the count dword are not part of the count (low 24 bits).
        file = Pbmp(2, 2, new byte[8], pidx: 7, count: 0xAB000000 | 2);
        b = Bitmap(file);
        Assert.Equal(2u, b.ChunkCount);
        Assert.Equal(7u, b.PaletteId);
    }

    [Fact]
    public void PbmpUnknownChunksAreSkippedBySizeWithoutPadding() // 2.2.1, intro: no padding to even sizes
    {
        var extra = new List<(string, byte[])> { ("ZZZZ", new byte[] { 1, 2, 3 }) };
        byte[] file = Pbmp(2, 2, new byte[8], pidx: 5, extra: extra);
        // chunk order: data, PiDX, ZZZZ(3 bytes), and the file ends right after the odd-sized payload
        PbmpBitmap b = Bitmap(file);
        Assert.Equal(5u, b.PaletteId);
        // unknown chunk before the known one
        var l = new List<byte>();
        Tag(l, "PBMP");
        U32(l, 0);
        Chunk(l, "head", Dwords(3, 2, 2, 8, 0));
        Chunk(l, "ZZZZ", new byte[] { 1, 2, 3 });
        Chunk(l, "data", new byte[8]);
        Chunk(l, "PiDX", Dwords(9));
        b = Bitmap(l.ToArray());
        Assert.Equal(9u, b.PaletteId);
    }

    [Fact]
    public void PbmpWithoutPbmpTagAndBadFirstChunk() // 2.2.1
    {
        Assert.Equal(4, Bitmap(Pbmp(2, 2, new byte[8], tag: false)).Stride);
        var l = new List<byte>();
        Tag(l, "PBMP");
        U32(l, 0);
        Chunk(l, "data", new byte[8]);
        Assert.False(PbmpBitmap.TryParse(l.ToArray(), false, out _, out string err));
        Assert.Contains("head", err);
    }

    [Fact]
    public void PbmpTruncatedAndShortDataFail() // robustness of the reader
    {
        byte[] file = Pbmp(4, 4, new byte[16], pidx: 1);
        for (int cut = 0; cut < file.Length - 1; cut += 5)
            Assert.False(PbmpBitmap.TryParse(file.AsSpan(0, cut), false, out _, out _));
        Assert.False(PbmpBitmap.TryParse(Pbmp(4, 4, new byte[8]), false, out _, out string err));
        Assert.Contains("shorter", err);
    }

    [Fact]
    public void PbmpEmbeddedRiffPaletteOnlyWithTheFlag() // 2.2.1 RIFF chunk
    {
        var extra = new List<(string, byte[])> { ("RIFF", RiffPal(256, false).AsSpan(8).ToArray()) };
        byte[] file = Pbmp(2, 2, new byte[8], extra: extra);
        Assert.Null(Bitmap(file, palette: false).Palette);
        PbmpBitmap b = Bitmap(file, palette: true);
        Assert.NotNull(b.Palette);
        Assert.Equal(new byte[] { 5, 10, 15 }, b.Palette!.SubPalettes[0].Colors.AsSpan(20, 3).ToArray());
    }

    [Fact]
    public void BlendModeFromAttributes() // 2.2.1 flags, 4 "Bitmap blits": attributes & 0x37
    {
        Assert.Equal(BlendMode.Opaque, BitmapAttributes.OwnsMemory.Blend());
        Assert.Equal(BlendMode.Transparent, (BitmapAttributes.Transparent | BitmapAttributes.OwnsMemory).Blend());
        Assert.Equal(BlendMode.Translucent, BitmapAttributes.Translucent.Blend());
        Assert.Equal(BlendMode.Translucent, (BitmapAttributes.Translucent | BitmapAttributes.Transparent).Blend());
        Assert.Equal(BlendMode.Additive, BitmapAttributes.Additive.Blend());
        Assert.Equal(BlendMode.Subtractive, BitmapAttributes.Subtractive.Blend());
        Assert.Equal(BlendMode.Opaque, BitmapAttributes.Fuzzy.Blend()); // not handled by the 2D path
        Assert.Equal(BlendMode.Opaque, BitmapAttributes.Alpha88.Blend()); // bit 6 is outside 0x37
    }

    // ---- 2.2.4 Windows BMP ----

    [Fact]
    public void WindowsBmp8BitBottomUpPaletteAndIndex() // 2.2.4
    {
        // 3 x 2, stride 4: bottom-up file; the reserved words carry the palette index (0xF5F7, index).
        byte[] rows = { 1, 2, 3, 0, 4, 5, 6, 0 }; // top row first
        var bgra = new byte[1024];
        bgra[4 * 5] = 30;      // B of entry 5
        bgra[4 * 5 + 1] = 20;  // G
        bgra[4 * 5 + 2] = 10;  // R
        byte[] file = WindowsBmp(3, 2, 8, rows, bottomUp: true, bgra: bgra, res1: 0xF5F7, res2: 0x1234);
        PbmpBitmap b = Bitmap(file, palette: true);
        Assert.Equal(4, b.Stride);
        Assert.Equal(rows, b.Pixels); // flipped back to top-down
        Assert.Equal(0x1234u, b.PaletteId);
        Assert.Equal(new byte[] { 10, 20, 30, 4 }, b.Palette!.SubPalettes[0].Colors.AsSpan(20, 4).ToArray()); // R, G, B, 4
        Assert.Null(Bitmap(file, palette: false).Palette);
        // reserved1 != 0xF5F7, or reserved2 == 0xFFFF: no index
        Assert.Equal(0xFFFFFFFFu, Bitmap(WindowsBmp(3, 2, 8, rows, true, bgra, 0, 0x1234)).PaletteId);
        Assert.Equal(0xFFFFFFFFu, Bitmap(WindowsBmp(3, 2, 8, rows, true, bgra, 0xF5F7, 0xFFFF)).PaletteId);
    }

    [Fact]
    public void WindowsBmp24BitAndUnsupportedDepth() // 2.2.4: 8 or 24 bit only
    {
        byte[] rows = new byte[12]; // 1 x 2, stride 4
        rows[0] = 1; rows[1] = 2; rows[2] = 3;
        rows[4] = 4; rows[5] = 5; rows[6] = 6;
        PbmpBitmap b = Bitmap(WindowsBmp(1, 2, 24, rows.AsSpan(0, 8).ToArray(), bottomUp: false));
        Assert.Equal((1, 2, 24, 4), (b.Width, b.Height, b.Depth, b.Stride));
        Assert.Equal(rows.AsSpan(0, 8).ToArray(), b.Pixels);
        byte[] bad = WindowsBmp(1, 2, 8, new byte[8], false);
        bad[28] = 4; // bit count 4
        Assert.False(PbmpBitmap.TryParse(bad, false, out _, out string err));
        Assert.Contains("8 and 24", err);
    }

    // ---- 2.3 PBMA ----

    [Fact]
    public void PbmaArrayChunksCountAndSlotLookup() // 2.3
    {
        var bmps = new List<byte[]> { Pbmp(2, 2, new byte[8], pidx: 1), Pbmp(3, 1, new byte[4], pidx: 2) };
        PbmaArray.TryParse(Pbma(bmps), false, out PbmaArray? a, out string err);
        Assert.True(a != null, err);
        Assert.Equal(2u, a!.ChunkCount); // count = chunks after head = bitmapCount
        Assert.Equal(2u, a.BitmapCount);
        Assert.Equal(2, a.Bitmaps.Count);
        Assert.Equal(2u, a.Get(1)!.PaletteId); // no rmap: the id is the slot
        Assert.Null(a.Get(2));
    }

    [Fact]
    public void PbmaUserAndRmapChunks() // 2.3: sparse ids by binary search, user size = n / bitmapCount
    {
        var bmps = new List<byte[]> { Pbmp(1, 1, new byte[4], pidx: 10), Pbmp(1, 1, new byte[4], pidx: 20), Pbmp(1, 1, new byte[4], pidx: 30) };
        byte[] file = Pbma(bmps, user: new byte[6], rmap: new uint[] { 5, 90, 1000 });
        PbmaArray.TryParse(file, false, out PbmaArray? a, out string err);
        Assert.True(a != null, err);
        Assert.Equal(5u, a!.ChunkCount);
        Assert.Equal(2, a.UserSize);
        Assert.Equal(20u, a.Get(90)!.PaletteId);
        Assert.Equal(30u, a.Get(1000)!.PaletteId);
        Assert.Null(a.Get(6));
        Assert.Null(a.Get(2000));
    }

    [Fact]
    public void PbmaUnknownChunkSkippedAndCountHonoured() // 2.3
    {
        var body = new List<byte>();
        Chunk(body, "zzzz", new byte[3]);
        body.AddRange(Pbmp(1, 1, new byte[4]));
        body.AddRange(Pbmp(1, 1, new byte[4], pidx: 3)); // beyond count: not read
        var b = new List<byte>();
        Tag(b, "PBMA");
        U32(b, 0);
        Chunk(b, "head", Dwords(2, 1));
        b.AddRange(body);
        PbmaArray.TryParse(b.ToArray(), false, out PbmaArray? a, out string err);
        Assert.True(a != null, err);
        Assert.Single(a!.Bitmaps);
    }

    // ---- 2.4 PL98 and 2.5 RIFF ----

    [Fact]
    public void PplMultiPaletteSelectionById() // 2.4 palette selection by id
    {
        byte[] file = Ppl(new (uint, uint)[] { (0xFFFFFFFF, 1), (0xFFFFFFFE, 2), (0x7b7, 6), (0x470, 1) });
        Assert.True(PaletteBank.TryParsePpl(file, out PaletteBank? p, out string err), err);
        Assert.Equal(4, p!.SubPalettes.Count);
        Assert.Equal((1u, 2u, 254u), (p.ShadeBits, p.HazeSteps, p.HazeColor));
        Assert.Equal(2u, p.ShadeSteps);
        Assert.Equal(2, p.FindSubPalette(0x7b7).Colors[3]);          // subpalette k stores k in the second and fourth color byte
        Assert.Equal(2, p.FindSubPalette(0x7b7).Colors[1]);
        Assert.Equal(3, p.FindSubPalette(0x470).Colors[1]);
        Assert.Same(p.SubPalettes[0], p.FindSubPalette(0xFFFFFFFF)); // none: first
        Assert.Same(p.SubPalettes[0], p.FindSubPalette(0x9999));     // not present: first
        Assert.True(p.IsAllowed(0) && !p.IsAllowed(32));             // allowed colors: bit i = word i/32, mask 0x80000000 >> (i & 31)
    }

    [Fact]
    public void PplMapBlobTablesAndFloatMaps() // 2.4 map blob
    {
        // kind 1: shadeSteps * 256 * (hazeSteps + 1) bytes; kind 2: 65536; kind 0: none and no 0x1100 block.
        byte[] file = Ppl(new (uint, uint)[] { (1, 1), (2, 2), (3, 0), (4, 8) }, shadeBits: 2, hazeSteps: 3, weights: true);
        Assert.True(PaletteBank.TryParsePpl(file, out PaletteBank? p, out string err), err);
        Assert.Equal(4 * 256 * 4, p!.SubPalettes[0].ShadeHazeTable!.Length);
        Assert.Equal(65536, p.SubPalettes[1].BlendTable!.Length);
        Assert.Null(p.SubPalettes[2].ShadeHazeTable);
        Assert.Null(p.SubPalettes[2].IdentityMap);
        Assert.Equal(65536, p.SubPalettes[3].BlendTable!.Length);
        Assert.Equal(7, p.SubPalettes[0].ShadeHazeTable![1]);                    // byte pattern (i * 7)
        Assert.Equal(200, p.SubPalettes[1].IdentityMap![200]);
        Assert.Equal(0.04f + 5 / 1000f, p.SubPalettes[1].IndexToRed![5], 5);     // k = 1, map 0
        Assert.Equal(0.07f + 5 / 1000f, p.SubPalettes[1].IndexToAlpha![5], 5);   // k = 1, map 3
        Assert.Equal(0.12f, p.SubPalettes[3].IndexToRed![0], 5);                 // k = 3 (the 0x1100 blocks skip kind 0)
        // color weights trailer: flag 1, 256 floats, usedStart, usedEnd
        Assert.Equal(100f, p.ColorWeights![200]);
        Assert.Equal((12u, 34u), (p.UsedStart, p.UsedEnd));
        // kind 0 palettes still get float maps from their colors
        Assert.Equal(5 / 255f, p.SubPalettes[2].IndexToRed![5]);
    }

    [Fact]
    public void PplRejectsBadCountsAndTruncation() // 2.4: subpaletteCount 1..16
    {
        byte[] file = Ppl(new (uint, uint)[] { (1, 1) });
        byte[] zero = (byte[])file.Clone();
        zero[4] = 0;
        Assert.False(PaletteBank.TryParsePpl(zero, out _, out string err));
        Assert.Contains("count", err);
        zero[4] = 17;
        Assert.False(PaletteBank.TryParsePpl(zero, out _, out _));
        for (int cut = 0; cut < file.Length - 6; cut += 997)
            Assert.False(PaletteBank.TryParsePpl(file.AsSpan(0, cut), out _, out _));
    }

    [Fact]
    public void PplNearestColorPrefersIndexZeroWhenCloser() // 2.4 getNearestColor
    {
        var rgba = new byte[1024];
        rgba[4 * 1] = 200; rgba[4 * 2] = 100; rgba[4 * 3] = 10;
        PaletteBank p = PaletteBank.FromColors(rgba, 256);
        for (int i = 0; i < 8; ++i)
            p.AllowedColors[i] = 0;
        p.AllowedColors[0] = 0x80000000u >> 1 | 0x80000000u >> 2 | 0x80000000u >> 3; // indices 1, 2, 3 allowed
        Assert.Equal(1, p.NearestColor(201, 0, 0));
        Assert.Equal(2, p.NearestColor(100, 1, 1));
        Assert.Equal(0, p.NearestColor(0, 0, 0));   // index 0 (black) is closer than any allowed color
    }

    [Fact]
    public void RiffPalFromRiffOrPalAndExtraChunks() // 2.5
    {
        byte[] file = RiffPal(256, true, flags: 7);
        Assert.True(PaletteBank.TryParsePal(file, out PaletteBank? p, out string err), err);
        SubPalette s = p!.SubPalettes[0];
        Assert.Equal((0u, SubPalette.NoId), (s.Kind, s.Id));
        Assert.Equal(new byte[] { 9, 18, 27, 7 }, s.Colors.AsSpan(36, 4).ToArray());
        Assert.Equal(9 / 255f, s.IndexToRed![9]);
        Assert.Equal(0f, s.IndexToAlpha![0]); // alpha 0 for index 0, 1 for the rest
        Assert.Equal(1f, s.IndexToAlpha[1]);
        // the stream may start at "PAL " instead of "RIFF"
        Assert.True(PaletteBank.TryParsePal(file.AsSpan(8), out PaletteBank? q, out err), err);
        Assert.Equal(s.Colors, q!.SubPalettes[0].Colors);
        // count = chunk size / 4 - 1
        Assert.True(PaletteBank.TryParsePal(RiffPal(16, false), out PaletteBank? r, out err), err);
        Assert.Equal(45, r!.SubPalettes[0].Colors[15 * 4 + 2]);
        Assert.Equal(0, r.SubPalettes[0].Colors[16 * 4]);
        Assert.False(PaletteBank.TryParsePal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, out _, out _));
    }

    // ---- 2.6 PFON ----

    private static byte[] TestFont(uint flags = PftFont.FlagProportional | PftFont.FlagTransparent, int spacing = 1,
                                   uint justification = 0, int scale = 65536)
    {
        // Sheet 0: 16 x 8. Characters 'A' (code 65) and 'B' (66) are glyphs 0 and 1; 'C' (67) is -1 (missing).
        byte[] sheet = Pbmp(16, 8, new byte[128], attrs: 9, pidx: 0xFFFFFFFF).ToArray();
        var glyphs = new (byte, byte, byte, byte, byte, sbyte)[]
        {
            (0, 0, 0, 3, 4, 0),   // 'A' 3 x 4
            (0, 4, 1, 5, 6, -1),  // 'B' 5 x 6, baseline -1
        };
        return Pft(flags, cellH: 7, cellW: 6, baseline: 6, spacing, lookupOffset: 65, new short[] { 0, 1, -1 }, glyphs,
                   Pbma(new List<byte[]> { sheet }), justification, scale);
    }

    [Fact]
    public void PftLookupAndGlyphRects() // 2.6 lookupGlyph, char info
    {
        Assert.True(PftFont.TryParse(TestFont(), out PftFont? f, out string err), err);
        Assert.Equal((2u, 7, 6, 6, 1), (f!.Version, f.CellHeight, f.GlyphCellWidth, f.Baseline, f.Spacing));
        Assert.Equal(65, f.LookupOffset);
        Assert.Equal(2, f.Glyphs.Length);
        Assert.Single(f.Sheets.Bitmaps);
        Assert.True(f.TryGetGlyph('A', out GlyphEntry a));
        Assert.Equal(new GfxRect(0, 0, 2, 3), a.SheetRect);    // inclusive: (x, y) to (x + w - 1, y + h - 1)
        Assert.True(f.TryGetGlyph('B', out GlyphEntry b));
        Assert.Equal(new GfxRect(4, 1, 8, 6), b.SheetRect);
        Assert.Equal(-1, b.Baseline);
        Assert.False(f.TryGetGlyph('C', out _));   // lookup[2] == -1
        Assert.False(f.TryGetGlyph('D', out _));   // index == lookupSize: present in the original, absent here
        Assert.False(f.TryGetGlyph('@', out _));   // below lookupOffset
    }

    [Fact]
    public void PftMetricsProportionalFixedMissingAndEmpty() // 2.6 metrics
    {
        PftFont.TryParse(TestFont(), out PftFont? f, out _);
        // proportional: sum(charWidth + spacing) - spacing; 'C' is missing: a blank cell of max(glyphCellW, 5) = 6
        Assert.Equal((3 + 1) + (5 + 1) - 1, f!.MeasureTextWidth("AB"));
        Assert.Equal(3 + 1 + 6 + 1 - 1, f.MeasureTextWidth("AC"));
        Assert.Equal(-1, f.MeasureTextWidth(""));                // (0 - spacing) * scaleX >> 16
        Assert.Equal(6, f.MeasureTextHeight("AB"));              // max charHeight
        Assert.Equal(7, f.MeasureTextHeight("AC"));              // the missing cell is cellHeight high
        Assert.Equal(6, f.CharWidth('C'));
        PftFont.TryParse(TestFont(flags: PftFont.FlagFixedWidth | PftFont.FlagTransparent), out PftFont? g, out _);
        Assert.Equal((6 + 1) * 2 - 1, g!.MeasureTextWidth("AB")); // fixed: glyphCellW + spacing each
        PftFont.TryParse(TestFont(scale: 2 << 16), out PftFont? h, out _);
        Assert.Equal(2 * (3 + 1 + 5 + 1 - 1), h!.MeasureTextWidth("AB")); // * scaleX >> 16
    }

    [Fact]
    public void PftLowerCapsUppercasesBeforeLookup() // 2.6 flag bit10
    {
        PftFont.TryParse(TestFont(flags: PftFont.FlagProportional | PftFont.FlagLowerCaps), out PftFont? f, out _);
        Assert.True(f!.TryGetGlyph('b', out GlyphEntry g));
        Assert.Equal(5, g.Width);
        PftFont.TryParse(TestFont(), out PftFont? plain, out _);
        Assert.False(plain!.TryGetGlyph('b', out _));
    }

    [Fact]
    public void PftRejectsWrongTagAndTruncation() // 2.1
    {
        byte[] file = TestFont();
        byte[] bad = (byte[])file.Clone();
        bad[0] = (byte)'X';
        Assert.False(PftFont.TryParse(bad, out _, out string err));
        Assert.Contains("PFON", err);
        for (int cut = 0; cut < 300; cut += 17)
            Assert.False(PftFont.TryParse(file.AsSpan(0, cut), out _, out _));
    }

    // ---- 4 text placement, on a recording device ----

    private sealed class Recorder : IGfxDevice
    {
        public readonly List<string> Calls = new();
        public int Width => 100;
        public int Height => 100;
        public void SetPalette(PaletteBank bank, float gamma = 1f) { }
        public void FlushTextureCache() { }
        public void FlushTexture(PbmpBitmap bitmap) { }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void Clear(int colorIndex) { }
        public void ClearRgb(byte r, byte g, byte b) { }
        public void SetClip(GfxRect? clip) { }
        public void DrawLine(int x0, int y0, int x1, int y1, int colorIndex) { }
        public void DrawRectOutline(GfxRect rect, int colorIndex) { }
        public void FillRect(GfxRect rect, int colorIndex, float alpha = 1f) { }
        public void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitRegion(PbmpBitmap bitmap, GfxRect s, int x, int y, BlitFlip flip = BlitFlip.None) =>
            Calls.Add($"R {s.Left},{s.Top},{s.Right},{s.Bottom} @{x},{y}");
        public void BlitStretch(PbmpBitmap bitmap, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public void BlitRegionStretch(PbmpBitmap bitmap, GfxRect s, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) =>
            Calls.Add($"S {s.Left},{s.Top},{s.Right},{s.Bottom} @{x},{y} {width}x{height}");
        public byte[] ReadPixels() => Array.Empty<byte>();
        public IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options) => throw new NotSupportedException();
        public void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB) { }
        public void SetCamera3D(in Camera3D camera) { }
        public void Draw3D(ReadOnlySpan<Vertex3D> triangles, in DrawState3D state) { }
        public void EndScene3D() { }
        public void Dispose() { }
    }

    [Fact]
    public void TextAtPlacesGlyphsByTheBaselineFormula() // 4 drawTextAt
    {
        PftFont.TryParse(TestFont(), out PftFont? f, out _);
        var d = new Recorder();
        int bottom = GfxText.DrawAt(d, f!, 10, 20, "ACB");
        // 'A': y = 20 + baseline 6 + glyph baseline 0 - (rect.bottom - rect.top = 3) + 1 = 24; pen 10
        // 'C' is missing: nothing drawn, pen advances by the blank cell width 6 + spacing 1
        // 'B': y = 20 + 6 - 1 - 5 + 1 = 21; pen = 10 + (3 + 1) + (6 + 1) = 21
        Assert.Equal(new[] { "R 0,0,2,3 @10,24", "R 4,1,8,6 @21,21" }, d.Calls);
        Assert.Equal(27, bottom); // lowest glyph row: 24 + 4 - 1 for 'A', 21 + 6 - 1 = 26 for 'B'
    }

    [Fact]
    public void TextAtScaledGoesThroughStretch() // 4: scale in 16.16 multiplies rect, y offset and advance
    {
        PftFont.TryParse(TestFont(scale: 2 << 16), out PftFont? f, out _);
        var d = new Recorder();
        GfxText.DrawAt(d, f!, 0, 0, "AB");
        // 'A': yOff = 6 + 0 - 3 = 3 -> 3 * 2 + 1 = 7, size 6 x 8; advance (3 + 1) * 2 = 8
        Assert.Equal(new[] { "S 0,0,2,3 @0,7 6x8", "S 4,1,8,6 @8,1 10x12" }, d.Calls);
    }

    [Fact]
    public void TextInRectJustification() // 4 drawTextInRect
    {
        var rect = new GfxRect(10, 20, 49, 59); // 40 x 40
        PftFont.TryParse(TestFont(justification: PftFont.JustRight), out PftFont? f, out _);
        var d = new Recorder();
        GfxText.DrawInRect(d, f!, rect, "A");
        Assert.Equal("R 0,0,2,3 @47,24", d.Calls[0]);   // pen = right - extent + 1 = 49 - 3 + 1; y = top 20
        PftFont.TryParse(TestFont(justification: PftFont.JustCenter | PftFont.JustVCenter), out f, out _);
        d = new Recorder();
        GfxText.DrawInRect(d, f!, rect, "A");
        // x = 10 + (40 - 3) / 2 = 28; y top = 20 + (40 - 6) / 2 = 37, glyph y = 37 + 6 - 3 + 1 = 41
        Assert.Equal("R 0,0,2,3 @28,41", d.Calls[0]);
        PftFont.TryParse(TestFont(justification: PftFont.JustBottom), out f, out _);
        d = new Recorder();
        GfxText.DrawInRect(d, f!, rect, "A");
        Assert.Equal("R 0,0,2,3 @10,58", d.Calls[0]);   // top = 59 - 6 + 1 = 54, glyph y = 54 + 6 - 3 + 1
        PftFont.TryParse(TestFont(justification: PftFont.JustFit), out f, out _);
        d = new Recorder();
        GfxText.DrawInRect(d, f!, rect, "A");
        // scale x = 40 / 3 in 16.16, y = 40 / 7: the glyph is stretched through the scaled path
        Assert.StartsWith("S 0,0,2,3 @10,", d.Calls[0]);
        Assert.EndsWith(" 39x22", d.Calls[0]);
    }

    // ---- 3.3 gamma and 3.4 conversion ----

    [Fact]
    public void StockDefaultGammaTableIsOneBelowFromOneTo254()
    {
        byte[] t = PaletteTables.GammaTable(1f);
        Assert.Equal(0, t[0]);
        Assert.Equal(255, t[255]);
        for (int i = 1; i < 255; ++i)
            Assert.Equal(i - 1, t[i]);
        // gamma 0.5 is the integer exponent 2 (005d4880, pow 606d90 square and multiply): trunc(255 * x * x) with x a truncated i / 255
        byte[] sq = PaletteTables.GammaTable(0.5f);
        Assert.Equal(0, sq[0]);
        Assert.Equal(255, sq[255]);
        for (int i = 1; i < 255; ++i)
            Assert.InRange(sq[i], (int)(255.0 * i * i / 65025.0) - 1, (int)(255.0 * i * i / 65025.0));
    }

    [Fact]
    public void GammaTableAndRgbaConversion() // 3.3 items 1 and 2, 3.4
    {
        byte[] id = new byte[256];
        for (int i = 0; i < 256; ++i)
            id[i] = (byte)i;
        byte[] g2 = PaletteTables.GammaTable(2f); // e = 1 / gamma: trunc(255 * sqrt(i / 255))
        Assert.Equal(0, g2[0]);
        Assert.Equal(255, g2[255]);
        Assert.Equal((int)(255 * Math.Sqrt(64 / 255.0)), g2[64]);
        // range 0.05 .. 20 clamps
        Assert.Equal(PaletteTables.GammaTable(20f), PaletteTables.GammaTable(100f));

        // 4 x 1 bitmap of indices 0, 1, 2, 3 with palette flags (the alpha of kind 2 palettes)
        var rgba = new byte[1024];
        for (int i = 0; i < 4; ++i)
        {
            rgba[i * 4] = (byte)(10 * i + 1);
            rgba[i * 4 + 1] = (byte)(20 * i + 2);
            rgba[i * 4 + 2] = (byte)(30 * i + 3);
            rgba[i * 4 + 3] = (byte)(40 * i);
        }
        PaletteBank bank = PaletteBank.FromColors(rgba, 256);
        SubPalette sp = bank.SubPalettes[0];
        byte[] px = { 0, 1, 2, 3 };
        PbmpBitmap opaque = Bitmap(Pbmp(4, 1, px, attrs: 0));
        byte[] o = PaletteTables.ToRgba(opaque, sp, id);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 11, 22, 33, 255 }, o.AsSpan(0, 8).ToArray()); // index 0 is opaque
        PbmpBitmap keyed = Bitmap(Pbmp(4, 1, px, attrs: 1));
        byte[] k = PaletteTables.ToRgba(keyed, sp, id);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 11, 22, 33, 255 }, k.AsSpan(0, 8).ToArray());
        PbmpBitmap trans = Bitmap(Pbmp(4, 1, px, attrs: 4));
        sp.Kind = 2; // translucent subpalette: the flags byte is the alpha
        byte[] t = PaletteTables.ToRgba(trans, sp, id);
        Assert.Equal(new byte[] { 11, 22, 33, 40 }, t.AsSpan(4, 4).ToArray());
        sp.Kind = 0;
        Assert.Equal(255, PaletteTables.ToRgba(trans, sp, id)[7]); // other palettes: opaque texture, constant alpha at draw
    }

    // ---- 2.7 resource lookup through the port's ResourceManager ----

    [Fact]
    public void ResolvesNamesThroughVolumes() // 2.7
    {
        string root = TestPaths.TmpDir("gfx-res");
        string dir = root + "/base";
        Directory.CreateDirectory(dir);
        byte[] bmp = Pbmp(2, 2, new byte[8], pidx: 0x5bb);
        byte[] pal = RiffPal(256, false);
        byte[] ppl = Ppl(new (uint, uint)[] { (1, 1) });
        byte[] pft = TestFont();
        byte[] pba = Pbma(new List<byte[]> { Pbmp(1, 1, new byte[4]) });
        string S(byte[] b) => ByteString.FromBytes(b);
        Fixtures.PutVol(dir + "/gfx.vol", new (string, string)[]
        {
            ("gui\\logo.bmp", S(bmp)), ("gui\\feargui.pal", S(pal)), ("Shell.ppl", S(ppl)),
            ("console.pft", S(pft)), ("Opening.pba", S(pba)),
        });
        ResourceManager res = GfxAssets.Mount(root, out int vols);
        Assert.Equal(1, vols);
        Assert.Equal(0x5bbu, GfxAssets.LoadBitmap(res, "gui\\logo.bmp")!.PaletteId);
        Assert.NotNull(GfxAssets.LoadBitmap(res, "LOGO.bmp"));            // a name without its directory also resolves
        Assert.NotNull(GfxAssets.LoadPalette(res, "feargui.pal"));         // .pal: RIFF reader
        Assert.Single(GfxAssets.LoadPalette(res, "Shell.ppl")!.SubPalettes); // anything else: PL98 reader
        Assert.NotNull(GfxAssets.LoadFont(res, "console.pft"));
        Assert.Single(GfxAssets.LoadBitmapArray(res, "Opening.pba")!.Bitmaps);
        Assert.Null(GfxAssets.LoadBitmap(res, "missing.bmp"));
        Assert.Null(GfxAssets.LoadBitmap(res, "console.pft"));             // wrong format: null, no exception
    }
}
