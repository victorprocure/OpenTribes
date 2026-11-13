// Render-level tests of the zoom branch of the crosshair (Crosshair 004ab500): the sniper cross and the zoom box text must reach the
// device, which drops what lies outside the clip like the real one. Synthetic data only (R5).
namespace OpenTribes.Tests;

using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;

public class GuiCrosshairZoomTests
{
    internal sealed class ClipDev : IGfxDevice
    {
        public List<(GfxRect Rect, int Color)> Rects { get; } = [];
        public List<(int X0, int Y0, int X1, int Y1)> Lines { get; } = [];
        public List<string> Texts { get; } = [];
        private GfxRect _clip = new(0, 0, 639, 479);
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
        public void SetClip(GfxRect? clip) => _clip = clip ?? new GfxRect(0, 0, 639, 479);
        private bool In(int x, int y) => x >= _clip.Left && x <= _clip.Right && y >= _clip.Top && y <= _clip.Bottom;
        public void DrawLine(int x0, int y0, int x1, int y1, int colorIndex)
        {
            if (Clipped(x0, y0, x1, y1))
                Lines.Add((Math.Clamp(x0, _clip.Left, _clip.Right), Math.Clamp(y0, _clip.Top, _clip.Bottom), Math.Clamp(x1, _clip.Left, _clip.Right), Math.Clamp(y1, _clip.Top, _clip.Bottom)));
        }
        // a line is kept when it crosses the clip (the real device clips it)
        private bool Clipped(int x0, int y0, int x1, int y1) =>
            Math.Max(x0, x1) >= _clip.Left && Math.Min(x0, x1) <= _clip.Right && Math.Max(y0, y1) >= _clip.Top && Math.Min(y0, y1) <= _clip.Bottom;
        public void DrawRectOutline(GfxRect rect, int colorIndex) { }
        public void FillRect(GfxRect rect, int colorIndex, float alpha = 1f) => Rects.Add((rect, colorIndex));
        public List<(int X, int Y, string Text)> TextAt { get; } = [];
        public int DrawText(PftFont font, int x, int y, string text)
        {
            TextAt.Add((x, y, text));
            if (In(x, y))
                Texts.Add(text);
            return y;
        }
        public void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitRegion(PbmpBitmap bitmap, GfxRect source, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitStretch(PbmpBitmap bitmap, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public void BlitRegionStretch(PbmpBitmap bitmap, GfxRect source, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public byte[] ReadPixels() => [];
        public IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options) => throw new NotSupportedException();
        public void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB) { }
        public void SetCamera3D(in Camera3D camera) { }
        public void Draw3D(ReadOnlySpan<Vertex3D> triangles, in DrawState3D state) { }
        public void EndScene3D() { }
    }

    private static ClipDev Render(HudZoom zoom, string? sniperPref = null)
    {
        var fx = new GuiFixture();
        fx.Host.SetFont("sf_white_7.pft", new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 6, CellHeight = 7 });
        if (sniperPref != null)
            fx.Interp.Evaluate("$pref::SniperCrosshair = \"" + sniperPref + "\";");
        fx.Host.Hud = new StaticHudSource { Zoom = zoom };
        var root = GuiFixture.Make("SimGui::TSControl", 0, 0, 640, 480);
        root.AddChild(GuiFixture.Make("FearGui::Crosshair", 313, 233, 13, 13));
        fx.Content(root);
        var dev = new ClipDev();
        fx.Canvas.Render(dev);
        return dev;
    }

    [Fact]
    public void ZoomedViewDrawsTheSniperCrossAcrossTheWholeView()
    {
        var d = Render(new HudZoom(22.5, 22.5, 22.5, 100, 110, 5000, 0));
        Assert.Contains((320, 0, 320, 479), d.Lines);   // vertical line through the centre, full height
        Assert.Contains((0, 240, 639, 240), d.Lines);   // horizontal line, full width
    }

    [Fact]
    public void SniperCrosshairFalseKeepsTheReticleOnly()
    {
        var d = Render(new HudZoom(22.5, 22.5, 22.5, 100, 110, 5000, 0), "FALSE");
        Assert.DoesNotContain(d.Lines, l => l.Y1 - l.Y0 > 100 || l.X1 - l.X0 > 100);
    }

    [Fact]
    public void ZoomStepWithTheZoomOffDrawsTheBoxAndItsTextForTwoSeconds()
    {
        // 90 degrees, zoom FOV 22.5: tan(11.25) x 640 x 0.5 = 64 pixels half width, so the box is 640 / 128 = 5x
        var d = Render(new HudZoom(90, 22.5, 90, 100, 100, 5000, 4000));
        Assert.Contains("ZOOM: 5x", d.Texts);
        Assert.DoesNotContain(d.Lines, l => l.Y1 - l.Y0 > 100 || l.X1 - l.X0 > 100);
        Assert.DoesNotContain("ZOOM: 5x", Render(new HudZoom(90, 22.5, 90, 100, 100, 6001, 4000)).Texts);
    }
}
