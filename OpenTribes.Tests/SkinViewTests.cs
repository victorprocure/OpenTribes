// The player setup model view (docs/gui.md model views; code 0049ae84 drag, 004998e0 and 0049a9bc camera): synthetic shape bounds only (R5).
namespace OpenTribes.Tests;

using OpenTribes.Gui;

public class SkinViewTests
{
    [Fact]
    public void ADragTurnsTheModelByAHundredthOfARadianPerPixelAndWraps()
    {
        var v = (GuiShapeView)GuiFixture.Make("FearGui::SkinView", 0, 0, 256, 282);
        Assert.Equal(Math.PI, v.Yaw, 9);
        v.MsDown(new GuiEvent { X = 100, Y = 50 });
        v.MsDrag(new GuiEvent { X = 150, Y = 50 });
        Assert.Equal(Math.PI + 0.5, v.Yaw, 9);
        v.MsDrag(new GuiEvent { X = 150 + 400, Y = 50 });
        Assert.Equal(Math.PI + 0.5 + 4 - (2 * Math.PI), v.Yaw, 6);
        v.MsDrag(new GuiEvent { X = 150, Y = 50 });
        Assert.InRange(v.Yaw, 0.0, 2 * Math.PI);
    }

    [Fact]
    public void TheCommandsOfTheScriptChooseSkinGenderAndArmor()
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var v = GuiFixture.Make("FearGui::SkinView", 10, 10, 256, 282);
        v.Id = 7;
        root.AddChild(v);
        fx.Content(root);
        fx.Interp.Evaluate("FGSkin::set(7, \"base\", \"FEMALE\");");
        var view = (GuiShapeView)v;
        Assert.Equal("base", view.SkinBase);
        Assert.False(view.Male);
        Assert.Equal(0, view.Armor);
        fx.Interp.Evaluate("FGSkin::cycleArmor(7);");
        fx.Interp.Evaluate("FGSkin::cycleArmor(7);");
        Assert.Equal(2, view.Armor);
        fx.Interp.Evaluate("FGSkin::cycleArmor(7);");
        Assert.Equal(0, view.Armor);
    }

    [Fact]
    public void TheCameraStandsOnTheFarSideLookingBackAtTheModelFromAbove()
    {
        // yaw pi (the start): behind the model's +Y, looking along -Y, 14 degrees down
        var c = Client.App.SkinViewRenderer.CameraFor(0, 0, 1.0, true, Math.PI, 256, 282);
        var (fx, fy, fz) = c.Forward;
        Assert.True(fy < -0.9);
        Assert.True(fz < 0);
        Assert.True(c.Y > 3);
        Assert.Equal(2 * 0.3, c.HorizontalFov, 9);
        // a view as wide as tall is fitted by the wider extent
        var sq = Client.App.SkinViewRenderer.CameraFor(0, 0, 1.0, true, Math.PI, 200, 200);
        Assert.True(sq.Y > c.Y);
    }
}
