// docs/gui.md 4.2 Crosshair: the aim triangles and distance labels of FUN_004ada90 [code 004ada90].
namespace OpenTribes.Tests;

using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;

public class HudMarkersTests
{
    [Fact]
    public void LabelIsOneDecimalAndMeters()
    {
        Assert.Equal("6.8 m", HudMarkers.Label(6.8));
        Assert.Equal("6.8 m", HudMarkers.Label(6.87576));   // stock trace: len 6.87576 printed as "6.8 m"
        Assert.Equal("123.4 m", HudMarkers.Label(123.46));
        Assert.Equal("0.0 m", HudMarkers.Label(0));
    }

    [Fact]
    public void TriangleTipIsAtTheTargetAndPointsDown()
    {
        Assert.Equal([(100, 50), (96, 42), (104, 42)], HudMarkers.Triangle(100, 50, false));
        Assert.Equal([(100, 50), (97, 44), (103, 44)], HudMarkers.Triangle(100, 50, true));
    }

    [Fact]
    public void LabelIsCentredUnderTheTip()
    {
        Assert.Equal((100 - 10, 54), HudMarkers.LabelOrigin(100, 50, 21, false));   // width 21 >> 1 = 10
        Assert.Equal((90, 53), HudMarkers.LabelOrigin(100, 50, 20, true));
    }

    [Fact]
    public void PixelRoundsToNearestForPositiveCoordinates()
    {
        Assert.Equal(320, HudMarkers.Pixel(319.5));
        Assert.Equal(319, HudMarkers.Pixel(319.49));
    }

    [Fact]
    public void DistanceIsTheEuclideanLength() => Assert.Equal(5.0, HudMarkers.Distance((0, 0, 0), (3, 0, 4)), 5);

    private static GuiCrosshairZoomTests.ClipDev Render(params HudTargetMarker[] marks)
    {
        var fx = new GuiFixture();
        fx.Host.SetFont("sf_white_7.pft", new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 6, CellHeight = 7 });
        var hud = new StaticHudSource();
        hud.Markers.AddRange(marks);
        fx.Host.Hud = hud;
        var root = GuiFixture.Make("SimGui::TSControl", 0, 0, 640, 480);
        root.AddChild(GuiFixture.Make("FearGui::Crosshair", 313, 233, 13, 13));
        fx.Content(root);
        var dev = new GuiCrosshairZoomTests.ClipDev();
        fx.Canvas.Render(dev);
        return dev;
    }

    [Fact]
    public void MarkerDrawsTheTriangleAndTheLabelUnderIt()
    {
        var d = Render(new HudTargetMarker(new HudMarkPoint(200, 150, true), null, "6.8 m"));
        Assert.Contains(d.Rects, r => r.Color == 0xfb && r.Rect.Top == 142 + 0 && r.Rect.Left <= 200 && r.Rect.Right >= 196);
        Assert.Contains(d.Rects, r => r.Color == 0xfb && r.Rect.Top == 150 && r.Rect.Left == 200 && r.Rect.Right == 200);   // the tip row
        Assert.Contains(d.TextAt, t => t.Text == "6.8 m" && t.Y == 154 && t.X == 200 - (5 * 6 / 2));
    }

    [Fact]
    public void ShortSolutionUsesTheOtherColourAndTheHighArcGetsItsOwnTriangle()
    {
        var d = Render(new HudTargetMarker(new HudMarkPoint(100, 100, false), new HudMarkPoint(300, 200, true), "10.0 m"));
        Assert.Contains(d.Rects, r => r.Color == 0xf1);
        Assert.Contains(d.Rects, r => r.Color == 0xfb);
        Assert.Equal(2, d.TextAt.Count(t => t.Text == "10.0 m"));
    }

    [Fact]
    public void NoMarkersDrawNothing()
    {
        var d = Render();
        Assert.DoesNotContain(d.Rects, r => r.Color is 0xfb or 0xf1);
    }
}
