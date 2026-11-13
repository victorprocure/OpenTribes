// FGShellBorder / MMShellBorder frame (docs/gui.md 3.3, draw 0049bd0c): synthetic pieces only (R5).
namespace OpenTribes.Tests;

using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;

public class GuiShellBorderTests
{
    private sealed class BlitDev : IGfxDevice
    {
        public readonly List<string> Calls = [];
        public int Width => 640;
        public int Height => 480;
        public void Dispose() { }
        public void SetPalette(PaletteBank bank, float gamma = 1f) { }
        public void FlushTextureCache() { }
        public void FlushTexture(PbmpBitmap bitmap) { }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void Clear(int colorIndex) { }
        public void ClearRgb(byte r, byte g, byte b) { }
        public void SetClip(GfxRect? clip) { }
        public void DrawLine(int x0, int y0, int x1, int y1, int colorIndex) => Calls.Add($"line {x0},{y0},{x1},{y1} c{colorIndex:x}");
        public void DrawRectOutline(GfxRect rect, int colorIndex) { }
        public void FillRect(GfxRect rect, int colorIndex, float alpha = 1f) => Calls.Add($"fill {rect.Left},{rect.Top} c{colorIndex:x}");
        public int DrawText(PftFont font, int x, int y, string text) { Calls.Add($"text w{font.GlyphCellWidth} {x},{y} '{text}'"); return y; }
        public void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None) => Calls.Add($"blit {bitmap.Width}x{bitmap.Height} {x},{y}");
        public void BlitRegion(PbmpBitmap bitmap, GfxRect source, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitStretch(PbmpBitmap bitmap, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) => Calls.Add($"stretch {bitmap.Width}x{bitmap.Height} {x},{y} {width}x{height}");
        public void BlitRegionStretch(PbmpBitmap bitmap, GfxRect source, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public byte[] ReadPixels() => [];
        public IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options) => throw new NotSupportedException();
        public void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB) { }
        public void SetCamera3D(in Camera3D camera) { }
        public void Draw3D(ReadOnlySpan<Vertex3D> triangles, in DrawState3D state) { }
        public void EndScene3D() { }
    }

    private static PbmaArray Frame()
    {
        // piece i is (10 + i) wide and (20 + i) high
        var a = new PbmaArray { BitmapCount = 15 };
        for (var i = 0; i < 15; i++)
            a.Bitmaps.Add(new PbmpBitmap { Width = 10 + i, Height = 20 + i });
        return a;
    }

    private static List<string> Draw(int w, int h)
    {
        var fx = new GuiFixture(w, h);
        fx.Host.SetArray("ShellBorderCtrl.pba", Frame());
        var root = GuiFixture.Make("FearGui::MMShellBorder", 0, 0, w, h);
        fx.Content(root);
        var dev = new BlitDev();
        fx.Canvas.Render(dev);
        return dev.Calls;
    }

    [Fact]
    public void FrameDrawsFifteenPiecesWhenTheExtentIsAtLeast512By384()
    {
        var c = Draw(640, 480);
        // title bar: 9 at x 224, stretch of 8 takes 640 - (224 + 19) - 20 - 44 - 23 = 310, 10 at 224 + 19 + 310, 13 after 44 pixels
        Assert.Contains("blit 19x29 224,0", c);
        Assert.Contains("stretch 18x28 243,0 310x28", c);
        Assert.Contains("blit 20x30 553,0", c);
        Assert.Contains("blit 23x33 617,0", c);
        // right column: 14 below 13, stretch of 7 up to piece 3 at the bottom right
        Assert.Contains("blit 24x34 616,33", c);
        Assert.Contains("stretch 17x27 623,67 17x390", c);
        Assert.Contains("blit 13x23 627,457", c);
        // left column: 11, 12, stretch of 6, 0 at the bottom
        Assert.Contains("blit 21x31 0,0", c);
        Assert.Contains("blit 22x32 0,31", c);
        Assert.Contains("stretch 16x26 0,63 16x" + (480 - 63 - 20), c);
        Assert.Contains("blit 10x20 0,460", c);
        // bottom strip: 1 after 0 at the height of its bottom edge, stretch of 5, 4 before the corner piece 3
        Assert.Contains("blit 11x21 10,459", c);
        Assert.Contains("stretch 15x25 21,459 " + (640 - 21 - 14 - 13) + "x25", c);
        Assert.Contains("blit 14x24 " + (640 - 14 - 13) + ",456", c);
    }

    [Fact]
    public void NoFrameBelow512By384()
    {
        var c = Draw(400, 300);
        Assert.DoesNotContain(c, s => s.StartsWith("blit ") || s.StartsWith("stretch "));
    }

    private static PftFont Font(int cell) => new() { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = cell, CellHeight = 10 };

    [Fact]
    public void MenuItemDrawsShadowedTextAfterThePictureWidthAndIndentsTheChildren() // docs/gui.md 3.3 FGMenuItem, draw 00495d44 / 00495bec
    {
        var fx = new GuiFixture(640, 480);
        fx.Host.SetFont("sf_orange214_10.pft", Font(5));
        fx.Host.SetFont("sf_orange255_10.pft", Font(6));
        fx.Host.SetFont("sf_white_10.pft", Font(7));
        fx.Host.SetFont("sf_black_10.pft", Font(8));
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var parent = GuiFixture.Make("FearGui::FGMenuItem", 10, 20, 129, 38, ("text", "Top"), ("expanded", (byte)1));
        var child = GuiFixture.Make("FearGui::FGMenuItem", 0, 19, 129, 19, ("text", "Sub"), ("tabSet", (byte)1));
        parent.AddChild(child);
        root.AddChild(parent);
        fx.Content(root);
        var dev = new BlitDev();
        fx.Canvas.Render(dev);
        // no MNU_Expand_DF.BMP in the fixture: the picture is taken as 19 x 19; the font is 10 high, so the text sits (19 - 10) / 2 = 4 below the top, 2 up
        Assert.Contains("text w8 28,23 'Top'", dev.Calls);      // shadow: one left, one down
        Assert.Contains("text w6 29,22 'Top'", dev.Calls);      // an item with children uses sf_orange255_10
        Assert.Contains("text w8 47,42 'Sub'", dev.Calls);      // child at x + 19, y + 19, its text 19 further right
        Assert.Contains("text w7 48,41 'Sub'", dev.Calls);      // the shown tab is white
    }

    [Fact]
    public void TestEditDrawsTwoUnderlineRulesWithMarkedEnds() // docs/gui.md 3.3 TestEdit, draw 004cd974
    {
        var fx = new GuiFixture(640, 480);
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        root.AddChild(GuiFixture.Make("FearGui::TestEdit", 10, 20, 100, 19, ("active", (byte)1)));
        fx.Content(root);
        var dev = new BlitDev();
        fx.Canvas.Render(dev);
        Assert.Contains("line 13,37,106,37 cf0", dev.Calls);   // x + 3 .. x + W - 4 at y + H - 2
        Assert.Contains("line 14,38,105,38 cef", dev.Calls);   // one row lower and one pixel shorter
        Assert.Contains("fill 13,37 cee", dev.Calls);
        Assert.Contains("fill 106,37 cee", dev.Calls);
    }

    [Fact]
    public void SliderDrawsCapsBarAndAThirtyPixelThumbAfterTheLeftCap() // docs/gui.md 3.3 FGSlider, draw 004ca7b8, thumb width 004ca1ee
    {
        var fx = new GuiFixture(640, 480);
        var a = new PbmaArray { BitmapCount = 15 };
        for (var i = 0; i < 15; i++)
            a.Bitmaps.Add(new PbmpBitmap { Width = 4 + i, Height = 20 + i });   // piece i is (4 + i) x (20 + i)
        fx.Host.SetArray("ShellSliderCtrl.pba", a);
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        root.AddChild(GuiFixture.Make("FearGui::FGSlider", 10, 20, 140, 17, ("active", (byte)1), ("maxValue", 0x3f800000u)));
        fx.Content(root);
        var dev = new BlitDev();
        fx.Canvas.Render(dev);
        Assert.Contains("blit 6x22 10,20", dev.Calls);              // left cap (piece 2)
        Assert.Contains("stretch 15x31 16,20 125x31", dev.Calls);   // bar (piece 11) over 140 - 6 - 9
        Assert.Contains("blit 9x25 141,20", dev.Calls);             // right cap (piece 5)
        Assert.Contains("blit 11x27 16,20", dev.Calls);             // thumb start (piece 7) right after the left cap
        Assert.Contains("stretch 10x26 27,20 7x26", dev.Calls);     // thumb middle (piece 6): 30 - 11 - 12
        Assert.Contains("blit 12x28 34,20", dev.Calls);             // thumb end (piece 8)
    }

    [Fact]
    public void MenuItemTextIsGreyBehindADialog() // gui.md 2.5 dimmed rule, FGMenuItem font 00495bec
    {
        var fx = new GuiFixture(640, 480);
        fx.Host.SetFont("sf_orange214_10.pft", Font(5));
        fx.Host.SetFont("sf_grey100_10b.pft", Font(9));
        fx.Host.SetFont("sf_black_10.pft", Font(8));
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        root.AddChild(GuiFixture.Make("FearGui::FGMenuItem", 10, 20, 129, 19, ("text", "Item")));
        fx.Content(root);
        var dlg = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var box = GuiFixture.Make("FearGui::FGDlgBox", 100, 100, 300, 200, ("active", (byte)1));
        box.Id = 141000;
        dlg.AddChild(box);
        fx.Canvas.PushDialog(dlg);
        var dev = new BlitDev();
        fx.Canvas.Render(dev);
        Assert.Contains(dev.Calls, c => c.StartsWith("text w9 ") && c.EndsWith("'Item'"));
        Assert.DoesNotContain(dev.Calls, c => c.StartsWith("text w5 ") && c.EndsWith("'Item'"));
    }
}
