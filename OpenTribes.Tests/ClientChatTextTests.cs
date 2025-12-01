// The ~w voice tag in shown chat text and the type of a chat line (PlayerManager_message, ROADMAP M9).
using OpenTribes.Game;

namespace OpenTribes.Tests;

public sealed class ClientChatTextTests
{
    [Fact]
    public void VoiceTagIsStrippedFromShownText()
    {
        var pm = new ClientPlayerManager();
        Assert.Equal("Objective Completed", pm.StripVoice("Objective Completed~wobjcomp"));
        Assert.Equal("plain", pm.StripVoice("plain"));
        pm.PlayVoices = () => false;
        Assert.Equal("Objective Completed~wobjcomp", pm.StripVoice("Objective Completed~wobjcomp"));
    }

    [Fact]
    public void MessagesKeepTheirType()
    {
        var pm = new ClientPlayerManager();
        pm.Message("a");
        pm.Message("b", 3);
        Assert.Equal(new[] { 0, 3 }, pm.ChatTypes);
        pm.Clear();
        Assert.Empty(pm.ChatTypes);
    }
}
