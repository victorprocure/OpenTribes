namespace OpenTribes.Tests;
// PlayerManager_message 0040dbfc: the chat display is child 140113 of the object named `playGui`, not any control with that id.
public class PlayChatLookupTests
{
    [Fact]
    public void ChatDisplayOfAnotherObjectIsNotThePlayGuiChild()
    {
        var f = new GuiFixture();
        var command = GuiFixture.Make("SimGui::Control");
        command.Name = "commandGui";
        var cmdChat = GuiFixture.Make("SimGui::Control");
        cmdChat.Id = 140113;
        command.Children.Add(cmdChat);
        f.Runtime.Named["commandGui"] = command;
        Assert.Null(f.Runtime.FindChildOf("playGui", 140113));

        var play = GuiFixture.Make("SimGui::Control");
        play.Name = "playGui";
        var chat = GuiFixture.Make("SimGui::Control");
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
        var play = GuiFixture.Make("SimGui::Control");
        play.Name = "playGui";
        play.Flags = 1;
        var chat = GuiFixture.Make("SimGui::Control");
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
    private static readonly bool[] Values1 = [false, true];
    [Fact]
    public void LookupRunsWhenTheMessageIsMade()
    {
        var pm = new Game.ClientPlayerManager();
        var play = false;
        pm.ChatDisplayExists = () => play;
        pm.Message("Rec joined team Blood Eagle.");
        play = true;
        pm.Message("Rec: hello", 3);
        Assert.Equal(Values1, pm.ChatDisplayed);
        pm.Clear();
        Assert.Empty(pm.ChatDisplayed);
    }
}

// The client answers the ghost-always phase (CGADone) before it builds the scene, so the server sees the reply about 170 ms after
// the phase like the stock client (docs/gui.md 4.2): LiveGame waits for the queue to empty.
public class CgaReplyTests
{
    [Fact]
    public void QueuedRemoteEvalIsPendingUntilWritten()
    {
        var conn = new Game.ClientGameConnection(new Game.ClientWorld());
        Assert.False(conn.OutgoingPending);
        conn.PostRemoteEval("CGADone");
        Assert.True(conn.OutgoingPending);
    }
}
