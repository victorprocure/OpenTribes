// The cursor tag of the hovered control (docs/gui.md 2.6; code 0049f1cc, 004958bc, 004b65e0, 004cd838, 004c2c70, 004c6888): synthetic controls only (R5).
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;

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

public class GuiHelpTests
{
    private static (GuiFixture Fx, GuiControl Button) Make(bool popups = true)
    {
        var fx = new GuiFixture();
        fx.Interp.Evaluate("IDHELP_T = 00220700, \"-1,Hello <f1>world<f0>\";");
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        GuiControl b = GuiFixture.Make("FearGui::FGUniversalButton", 100, 100, 60, 20, ("active", (byte)1));
        b.HelpTag = 220700;
        root.AddChild(b);
        fx.Content(root, new GuiDelegate { Name = "T", HelpPopups = popups });
        return (fx, b);
    }

    [Fact]
    public void ThePopUpShowsAfterTheCursorRestedOneSecondAndASixtyNinth()
    {
        var (fx, _) = Make();
        fx.Move(110, 110);
        fx.Frame(1000);
        Assert.False(fx.Canvas.HelpVisible);
        fx.Frame(68);
        Assert.False(fx.Canvas.HelpVisible);
        fx.Frame(2);
        Assert.True(fx.Canvas.HelpVisible);
        Assert.Equal(220700u, fx.Canvas.HelpShownTag);
    }

    [Fact]
    public void ThePopUpBoxIsTheTextFormatLayoutPlusSix() // 004af248: layout at width - 6 with slots 0 and 1 (tags 150104, 150114); box min(width, widest + 6) x min(0xc0, height + 6)
    {
        var (fy, b) = Make();
        fy.Interp.Evaluate("IDHELP_T2 = 00220701, \"100,<f1>ab<n>cd\"; IDFNT_HA = 150104, \"ha.pft\"; IDFNT_HB = 150114, \"hb.pft\";");
        fy.Host.SetFont("ha.pft", new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 6, CellHeight = 10 });
        fy.Host.SetFont("hb.pft", new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 8, CellHeight = 12 });
        b.HelpTag = 220701;
        fy.Move(110, 110);
        fy.Frame(1200);
        Assert.True(fy.Canvas.HelpVisible);
        Assert.Equal((16 + 6, 24 + 2 + 6), (fy.Canvas.HelpBox.W, fy.Canvas.HelpBox.H));
    }

    [Fact]
    public void APressKeepsItAwayUntilTheHoverChanges()
    {
        var (fx, _) = Make();
        fx.Move(110, 110);
        fx.Frame(1200);
        Assert.True(fx.Canvas.HelpVisible);
        fx.Button(true);
        fx.Frame();
        Assert.False(fx.Canvas.HelpVisible);
        fx.Button(false);
        fx.Frame(2000);
        Assert.False(fx.Canvas.HelpVisible);
        fx.Move(300, 300);
        fx.Frame(100);
        fx.Move(112, 112);
        fx.Frame(1200);
        Assert.True(fx.Canvas.HelpVisible);
    }

    [Fact]
    public void ADelegateWithoutPopUpsAndThePreferenceFalseShowNothing()
    {
        var (fx, _) = Make(popups: false);
        fx.Move(110, 110);
        fx.Frame(2000);
        Assert.False(fx.Canvas.HelpVisible);
        var (fy, _) = Make();
        fy.Interp.SetVariable("$pref::HelpPopups", "FALSE");
        fy.Move(110, 110);
        fy.Frame(2000);
        Assert.False(fy.Canvas.HelpVisible);
    }

    [Fact]
    public void TheBoxIsPushedInsideTheWindow()
    {
        var (fx, b) = Make();
        b.X = 620;
        b.Y = 460;
        b.W = 20;
        b.H = 20;
        fx.Move(630, 470);
        fx.Frame(1200);
        Assert.True(fx.Canvas.HelpVisible);
        var (x, y, w, h) = fx.Canvas.HelpBox;
        Assert.True(x + w <= 640 - 3);
        Assert.True(y + h <= 480 - 3);
    }
}
