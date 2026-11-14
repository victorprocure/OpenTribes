// The right button of the skin view (docs/gui.md 3.3; code 0049ae00): synthetic control only (R5).
namespace OpenTribes.Tests;

using OpenTribes.Gui;

public class SkinViewZoomTests
{
    [Fact]
    public void ARightDragMovesTheDistanceByTenOverTheHeightPerPixelAndKeepsItInRange()
    {
        var v = (GuiShapeView)GuiFixture.Make("FearGui::SkinView", 0, 0, 100, 200);
        v.Zoom = v.ZoomMin = 4.0;
        v.ZoomMax = 12.0;
        v.MsRightDown(new GuiEvent { X = 5, Y = 100 });
        v.MsRightDrag(new GuiEvent { X = 5, Y = 80 });
        Assert.Equal(5.0, v.Zoom, 9);
        v.MsRightDrag(new GuiEvent { X = 5, Y = 120 });
        Assert.Equal(4.0, v.Zoom, 9);
        v.MsRightDrag(new GuiEvent { X = 5, Y = 200 });
        Assert.Equal(4.0, v.Zoom, 9);
        v.MsRightDrag(new GuiEvent { X = 5, Y = -2000 });
        Assert.Equal(12.0, v.Zoom, 9);
    }
}
