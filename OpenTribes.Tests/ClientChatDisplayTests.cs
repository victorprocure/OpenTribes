// The chat display size action and the font of a message type (ROADMAP M9, FearGuiChatDisplay).
using OpenTribes.Gui;

namespace OpenTribes.Tests;

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
}
