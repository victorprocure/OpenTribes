// The chat display size action and the font of a message type (ROADMAP M9, FearGuiChatDisplay).
namespace OpenTribes.Tests;

using OpenTribes.Gui;

public sealed class ClientChatDisplayTests
{
    [Theory]
    [InlineData(3, -1, 5)]
    [InlineData(5, -1, 20)]
    [InlineData(20, -1, 3)]
    [InlineData(7, -1, 3)]
    [InlineData(3, 12, 12)]
    public void ChatDisplaySizeCycles(int current, int value, int expected) =>
        Assert.Equal(expected, GuiChatDisplay.NextLines(current, value));

    [Theory]
    [InlineData(0u, false, "if_w_10b.pft")]
    [InlineData(3u, false, "if_g_10b.pft")]
    [InlineData(4u, false, "sf_red_10b.pft")]
    [InlineData(1u, false, "sf_red_10b.pft")]
    [InlineData(2u, false, "sf_yellow_10b.pft")]
    [InlineData(3u, true, "sf_green_6.pft")]
    [InlineData(0u, true, "sf_white_6.pft")]
    public void ChatFontFollowsTheMessageType(uint type, bool low, string expected) =>
        Assert.Equal(expected, GuiChatDisplay.FontName(type, low));

    [Theory]
    [InlineData("", false)]
    [InlineData("You have left the mission area.", true)]
    [InlineData(" ", true)]
    public void ALineWithoutTextIsNotAddedToTheRing(string text, bool shown) =>
        Assert.Equal(shown, GuiHost.ChatLineShown(text));

    private static GuiChatDisplay Display(uint lines) => (GuiChatDisplay)GuiFixture.Make("FearGui::FearGuiChatDisplay", 132, 9, 370, 45, ("lines", lines));

    [Fact]
    public void SizeActionResizesTheControl()
    {
        var d = Display(3);
        d.SetLines(-1);
        Assert.Equal(5, d.Lines);
        Assert.Equal((5 * 13) + GuiChatDisplay.HeightPad, d.H);
    }

    // The stock content lookup keeps the display object, so the size set with `u` comes back after another screen.
    [Fact]
    public void SizeMemoryAppliesToAReloadedDisplay()
    {
        var memory = new ChatSizeMemory();
        var first = Display(3);
        first.SetLines(-1);
        first.SetLines(-1);
        memory.Remember(140114, first);
        var reloaded = Display(3);
        Assert.False(memory.Apply(140200, reloaded));
        Assert.Equal(3, reloaded.Lines);
        Assert.True(memory.Apply(140114, reloaded));
        Assert.Equal(20, reloaded.Lines);
    }

    // FearGuiChatDisplay parent resize [code 004a56e8]: no fractions, width 240 below 512 x 384 and 370 again above it.
    [Fact]
    public void ParentResizeSwitchesTheWidthAndKeepsThePosition()
    {
        var d = Display(3);
        d.ParentResized(640, 480, 400, 300);
        Assert.Equal((132, 9, 240, 45), (d.X, d.Y, d.W, d.H));
        d.ParentResized(400, 300, 640, 480);
        Assert.Equal((132, 9, 370, 45), (d.X, d.Y, d.W, d.H));
    }

    [Fact]
    public void ParentResizePullsTheWindowInsideASmallSurface()
    {
        var d = Display(3);
        d.ParentResized(640, 480, 200, 100);
        Assert.Equal((1, 9, 199, 45), (d.X, d.Y, d.W, d.H));
        d.ParentResized(200, 100, 200, 30);
        // 29 high: two lines of 13 and the pad of 6 are 32, so one line fits
        Assert.Equal((1, 9, 199, 19), (d.X, d.Y, d.W, d.H));
    }
}
