using OpenTribes.Gui;
using Xunit;

namespace OpenTribes.Tests;

// PlayerManager_message 0040dbfc: the chat display is child 140113 of the object named `playGui`, not any control with that id.
public class PlayChatLookupTests
{
    [Fact]
    public void ChatDisplayOfAnotherObjectIsNotThePlayGuiChild()
    {
        var f = new GuiFixture();
        GuiControl command = GuiFixture.Make("SimGui::Control");
        command.Name = "commandGui";
        GuiControl cmdChat = GuiFixture.Make("SimGui::Control");
        cmdChat.Id = 140113;
        command.Children.Add(cmdChat);
        f.Runtime.Named["commandGui"] = command;
        Assert.Null(f.Runtime.FindChildOf("playGui", 140113));

        GuiControl play = GuiFixture.Make("SimGui::Control");
        play.Name = "playGui";
        GuiControl chat = GuiFixture.Make("SimGui::Control");
        chat.Id = 140113;
        play.Children.Add(chat);
        f.Content(play);
        Assert.Same(chat, f.Runtime.FindChildOf("playGui", 140113));

        f.Content(GuiFixture.Make("SimGui::Control"));
        Assert.Null(f.Runtime.FindChildOf("playGui", 140113));
    }

    // play.gui has flags 1 (not deleted when replaced): while CmdInventory.gui is the content the object still exists, so the
    // "Station Access Off" message that comes before the mode switch back reaches the chat display.
    [Fact]
    public void PlayGuiStaysFindableWhileAnotherContentIsUp()
    {
        var f = new GuiFixture();
        GuiControl play = GuiFixture.Make("SimGui::Control");
        play.Name = "playGui";
        play.Flags = 1;
        GuiControl chat = GuiFixture.Make("SimGui::Control");
        chat.Id = 140113;
        play.Children.Add(chat);
        f.Content(play);
        f.Content(GuiFixture.Make("SimGui::Control"));
        Assert.Same(chat, f.Runtime.FindChildOf("playGui", 140113));
    }
}

// PlayerManager_message 0040dbfc asks for playGui when the message is made: a line made before the play GUI exists is not a chat ring line,
// even if the play GUI is there when the frame pumps the chat (stock trace: PlayerTeamChangeEvent 67 ms before the PSC mode change).
public class ChatLookupAtArrivalTests
{
    [Fact]
    public void LookupRunsWhenTheMessageIsMade()
    {
        var pm = new OpenTribes.Game.ClientPlayerManager();
        bool play = false;
        pm.ChatDisplayExists = () => play;
        pm.Message("Rec joined team Blood Eagle.");
        play = true;
        pm.Message("Rec: hello", 3);
        Assert.Equal(new[] { false, true }, pm.ChatDisplayed);
        pm.Clear();
        Assert.Empty(pm.ChatDisplayed);
    }
}
