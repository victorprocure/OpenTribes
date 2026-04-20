// FGShellBorder / MMShellBorder frame (docs/gui.md 3.3, draw 0049bd0c): synthetic pieces only (R5).
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Gl;

namespace OpenTribes.Tests;

public class GuiShellBorderTests
{
    private sealed class BlitDev : IGfxDevice
    {
        public readonly List<string> Calls = new();
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
        public void FillRect(GfxRect rect, int colorIndex, float alpha = 1f) => Calls.Add($"fill c{colorIndex:x}");
        public int DrawText(PftFont font, int x, int y, string text) { Calls.Add($"text w{font.GlyphCellWidth} {x},{y} '{text}'"); return y; }
        public void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None) => Calls.Add($"blit {bitmap.Width}x{bitmap.Height} {x},{y}");
        public void BlitRegion(PbmpBitmap bitmap, GfxRect source, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitStretch(PbmpBitmap bitmap, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) => Calls.Add($"stretch {bitmap.Width}x{bitmap.Height} {x},{y} {width}x{height}");
        public void BlitRegionStretch(PbmpBitmap bitmap, GfxRect source, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public byte[] ReadPixels() => Array.Empty<byte>();
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
        for (int i = 0; i < 15; i++)
            a.Bitmaps.Add(new PbmpBitmap { Width = 10 + i, Height = 20 + i });
        return a;
    }

    private static List<string> Draw(int w, int h)
    {
        var fx = new GuiFixture(w, h);
        fx.Host.SetArray("ShellBorderCtrl.pba", Frame());
        GuiControl root = GuiFixture.Make("FearGui::MMShellBorder", 0, 0, w, h);
        fx.Content(root);
        var dev = new BlitDev();
        fx.Canvas.Render(dev);
        return dev.Calls;
    }

    [Fact]
    public void FrameDrawsFifteenPiecesWhenTheExtentIsAtLeast512By384()
    {
        List<string> c = Draw(640, 480);
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
        List<string> c = Draw(400, 300);
        Assert.DoesNotContain(c, s => s.StartsWith("blit ") || s.StartsWith("stretch "));
    }
}
