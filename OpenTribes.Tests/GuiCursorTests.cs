// The cursor tag of the hovered control (docs/gui.md 2.6; code 0049f1cc, 004958bc, 004b65e0, 004cd838, 004c2c70, 004c6888): synthetic controls only (R5).
using OpenTribes.Gui;

namespace OpenTribes.Tests;

public class GuiCursorTests
{
    private static GuiControl Root(params GuiControl[] kids)
    {
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        foreach (GuiControl k in kids)
            root.AddChild(k);
        return root;
    }

    [Fact]
    public void AnActiveButtonShowsTheHandAnInactiveOneNothing()
    {
        GuiControl b = GuiFixture.Make("FearGui::FGUniversalButton", 10, 10, 60, 20, ("active", (byte)1));
        Assert.Equal(GuiControl.CursorHand, b.CursorTagAt(5, 5));
        ((GuiActive)b).Active = false;
        Assert.Equal(0u, b.CursorTagAt(5, 5));
    }

    [Theory]
    [InlineData("FearGui::FGMenuItem")]
    [InlineData("FearGui::FGTab")]
    [InlineData("FearGui::FGStandardComboBox")]
    public void MenuItemsTabsAndCombosAlwaysShowTheHand(string cls)
    {
        Assert.Equal(GuiControl.CursorHand, GuiFixture.Make(cls).CursorTagAt(1, 1));
    }

    [Fact]
    public void TextEditsOfTheIrcAndTheTestEditShowTheTextCursor()
    {
        Assert.Equal(GuiControl.CursorIBeam, GuiFixture.Make("FearGui::TestEdit").CursorTagAt(1, 1));
        Assert.Equal(GuiControl.CursorIBeam, GuiFixture.Make("FearGui::FGIRCTextEdit").CursorTagAt(1, 1));
        Assert.Equal(0u, GuiFixture.Make("SimGui::TextEdit").CursorTagAt(1, 1));
    }

    [Fact]
    public void ThePlainControlKeepsTheDefault()
    {
        Assert.Equal(0u, GuiFixture.Make("SimGui::Control").CursorTagAt(1, 1));
    }

    [Fact]
    public void TheCanvasTakesTheTagOfTheHoveredControlAndLetsGoOnLeaving()
    {
        var fx = new GuiFixture();
        GuiControl b = GuiFixture.Make("FearGui::FGUniversalButton", 100, 100, 60, 20, ("active", (byte)1));
        fx.Content(Root(b));
        fx.Move(110, 110);
        fx.Frame();
        Assert.Equal(GuiControl.CursorHand, fx.Canvas.CursorOverrideTag);
        fx.Move(300, 300);
        fx.Frame();
        Assert.Equal(0u, fx.Canvas.CursorOverrideTag);
    }

    [Fact]
    public void ARowOfTheServerListShowsTheHandAndAHeaderBorderTheAdjustCursor()
    {
        GuiControl scroll = GuiFixture.Make("FearGui::FearGuiScrollCtrl", 44, 106, 552, 309, ("headerY", 23u), ("horizontalPolicy", 2u), ("verticalPolicy", 2u), ("style", 6u));
        GuiControl content = GuiFixture.Make("SimGui::ScrollContentCtrl", 6, 6, 540, 280);
        GuiControl list = GuiFixture.Make("FearGui::ServerListCtrl", 0, 0, 817, 159, ("active", (byte)1));
        scroll.AddChild(content);
        content.AddChild(list);
        var fx = new GuiFixture();
        fx.Content(Root(scroll));
        // y of the list is its offset in the content: the header is the first 23 rows of the view
        Assert.Equal(GuiControl.CursorHand, list.CursorTagAt(10, 40));
        Assert.Equal(GuiControl.CursorHAdjust, list.CursorTagAt(52, 10));
        Assert.Equal(GuiControl.CursorHand, list.CursorTagAt(20, 10));
    }
}
