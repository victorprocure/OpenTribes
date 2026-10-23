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
}
