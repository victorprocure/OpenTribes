// The server browser's favorite, never ping and refresh behaviour (docs/gui.md 3.3; code 004c51f0, 004c52b4, 004c525c, 004c5054, 004c5320,
// 004c4e2c, 004c7820, 004c8278, delegate 004d7bb4). Synthetic data only: no window, no game data, no network beyond a queued loopback address.
namespace OpenTribes.Tests;

using OpenTribes.Client.App;
using OpenTribes.Gui;

public class ServerListFlagsTests
{
    private static GuiServerList MakeList()
    {
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var scroll = GuiFixture.Make("FearGui::FearGuiScrollCtrl", 44, 106, 552, 309, ("headerY", 23u), ("horizontalPolicy", 2u), ("verticalPolicy", 2u), ("style", 6u));
        var content = GuiFixture.Make("SimGui::ScrollContentCtrl", 6, 6, 540, 280);
        var list = GuiFixture.Make("FearGui::ServerListCtrl", 0, 0, 817, 159, ("active", (byte)1));
        root.AddChild(scroll);
        scroll.AddChild(content);
        content.AddChild(list);
        var fx = new GuiFixture();
        fx.Content(root);
        fx.Frame();
        return (GuiServerList)list;
    }

    private static GuiServerList.Server Row(int id, bool fav = false, bool never = false, int state = GuiServerList.StateUnknown) =>
        new(id, "S" + id, "IP:10.0.0." + id + ":28001", "1.11", 50, fav) { NeverPing = never, State = state };

    [Fact]
    public void ScriptRowShortFormTakesFavoriteAndNoNeverPing() // 004c5320, 0041e470
    {
        var r = GuiServerList.ParseScriptRow(3, ["addGameServer", "IP:1.2.3.4:5", "N", "1.11", "77", "TRUE"])!;
        Assert.Equal(("IP:1.2.3.4:5", "N", 77), (r.Address, r.Name, r.Ping));
        Assert.True(r.Favorite);
        Assert.False(r.NeverPing);
        Assert.Equal(GuiServerList.StateUnknown, r.State);
        Assert.False(GuiServerList.ParseScriptRow(3, ["x", "a", "b", "c", "1", "false"])!.Favorite);
        Assert.False(GuiServerList.ParseScriptRow(3, ["x", "a", "b", "c", "1", "False"])!.Favorite);
    }

    [Fact]
    public void ScriptRowSevenAndElevenArgumentForms() // 004c5320
    {
        var seven = GuiServerList.ParseScriptRow(1, ["x", "a", "b", "c", "1", "FALSE", "TRUE"])!;
        Assert.False(seven.Favorite);
        Assert.True(seven.NeverPing);
        var full = GuiServerList.ParseScriptRow(1, ["x", "a", "b", "c", "1", "TRUE", "base", "Broadside", "CTF", "12", "TRUE"])!;
        Assert.Equal(("base", "Broadside", "CTF", 12), (full.ModList, full.Mission, full.MissionType, full.Cpu));
        Assert.True(full.Favorite && full.NeverPing);
    }

    [Fact]
    public void ScriptRowNeedsAllFiveFields()
    {
        Assert.Null(GuiServerList.ParseScriptRow(1, ["x", "a", "b", "c", "1"]));
        Assert.Null(GuiServerList.ParseScriptRow(1, ["x", "a", "b", "", "1", "TRUE"]));
    }

    [Fact]
    public void SavedFileHasTheFlagsAndLoadsBack() // 004c4e2c
    {
        var l = MakeList();
        var a = Row(1, fav: true);
        a.ModList = "base";
        a.Mission = "Raindance";
        a.MissionType = "CTF";
        a.Cpu = 9;
        l.AddServer(a);
        l.AddServer(Row(2, never: true));
        var text = l.BuildServerFile();
        Assert.StartsWith("//-----------------------------------\n//\n// Database of GAME servers\n//\n//-----------------------------------\n\n", text);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(x => x.StartsWith("addGameServer", StringComparison.Ordinal)).ToArray();
        Assert.Equal("addGameServer(\"IP:10.0.0.1:28001\", \"S1\", \"1.11\", \"50\", \"TRUE\", \"base\", \"Raindance\", \"CTF\", \"9\", \"FALSE\");", lines[0]);
        Assert.EndsWith("\"FALSE\", \"\", \"\", \"\", \"0\", \"TRUE\");", lines[1]);
    }

    [Fact]
    public void FlagsSetByAddressIgnoringCase() // 004c51f0, 004c52b4
    {
        var l = MakeList();
        l.AddServer(Row(1));
        l.SetFavorite("ip:10.0.0.1:28001", true);
        l.SetNeverPing("IP:10.0.0.1:28001", true);
        Assert.True(l.Servers[0].Favorite && l.Servers[0].NeverPing);
        l.SetFavorite("ip:10.0.0.1:28001", false);
        Assert.False(l.Servers[0].Favorite);
        Assert.True(l.Servers[0].NeverPing);
    }

    [Fact]
    public void EndOfRefreshAllDropsUnansweredRowsButKeepsFlaggedOnes() // 004c7820
    {
        var l = MakeList();
        l.AddServer(Row(1));
        l.AddServer(Row(2, fav: true));
        l.AddServer(Row(3, never: true));
        l.AddServer(Row(4, state: GuiServerList.StateInfo));
        l.MarkAllUnknown();
        l.Servers[3].State = GuiServerList.StateInfo;
        var ask = l.EndSearch(false);
        Assert.Equal(["S2", "S3", "S4"], l.Servers.Select(s => s.Name));
        Assert.Equal(["S2"], ask.Select(s => s.Name));
        Assert.Equal(GuiServerList.StateQuerying, l.Servers[0].State);
        Assert.Equal(GuiServerList.StateUnknown, l.Servers[1].State);
        Assert.False(l.Requery);
    }

    [Fact]
    public void EndOfASecondSearchAsksNobodyAndDropsNothing() // 004c7820 with +0x3b2 cleared by the first
    {
        var l = MakeList();
        l.AddServer(Row(1, fav: true));
        l.MarkAllUnknown();
        l.EndSearch(false);
        Assert.Empty(l.EndSearch(false));
        Assert.Single(l.Servers);
        Assert.Equal(GuiServerList.StateUnknown, l.Servers[0].State);
    }

    [Fact]
    public void EndKeepsEveryRowAfterAMasterErrorAndAsksThem() // 004c7820, 004d7864
    {
        var l = MakeList();
        l.AddServer(Row(1));
        l.AddServer(Row(2));
        l.MarkAllUnknown();
        Assert.Equal(2, l.EndSearch(true).Count);
        Assert.Equal(2, l.Servers.Count);
    }

    [Fact]
    public void NoRowIsDroppedBeforeAFirstRefreshAll() // 004c702e clears +0x3b1
    {
        var l = MakeList();
        l.AddServer(Row(1));
        l.ResetSearchFlags();
        l.Servers[0].State = GuiServerList.StateQuerying;
        Assert.Empty(l.EndSearch(false));
        Assert.Single(l.Servers);
        Assert.Equal(GuiServerList.StateUnknown, l.Servers[0].State);
    }

    [Fact]
    public void ReplacingARowByAddressKeepsOneRow() // 004c5320
    {
        var l = MakeList();
        l.AddServer(Row(1));
        var n = Row(9, fav: true);
        n.Name = "New";
        n.Address = "ip:10.0.0.1:28001";
        l.ReplaceServer(l.Find(n.Address)!, n);
        Assert.Single(l.Servers);
        Assert.Equal("New", l.Servers[0].Name);
        Assert.True(l.Servers[0].Favorite);
    }
}

public sealed class ServerBrowserFlagsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-sb-" + Guid.NewGuid().ToString("N"));
    private ClientApp? _app;

    public ServerBrowserFlagsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "temp"));
    }

    public void Dispose()
    {
        _app?.Dispose();
        try
        { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    // A join screen of synthetic controls: the server list (140016) in the content, the delegate of the real client.
    private (ClientApp App, GuiServerList List) Boot()
    {
        File.WriteAllText(Path.Combine(_root, "console.cs"),
            "newClient(); focusClient();\n" +
            "newObject(MainWindow, SimGui::Canvas, \"Tribes\", 640, 480, True, \"512 384\", \"1024 768\");\n" +
            "function onConnection(%m) { $conn = %m; }\nfunction Game::EndFrame() {}\n" +
            "$ConsoleWorld::DefaultSearchPath = \"config;base;temp\";\n");
        var opt = ClientOptions.Parse(["--root", _root, "--headless"], out var err)!;
        Assert.Equal("", err);
        _app = new ClientApp(opt);
        Assert.True(_app.Start(out var error), error);
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var scroll = GuiFixture.Make("FearGui::FearGuiScrollCtrl", 44, 106, 552, 309, ("headerY", 23u), ("horizontalPolicy", 2u), ("verticalPolicy", 2u), ("style", 6u));
        var content = GuiFixture.Make("SimGui::ScrollContentCtrl", 6, 6, 540, 280);
        var list = GuiFixture.Make("FearGui::ServerListCtrl", 0, 0, 817, 159, ("active", (byte)1));
        list.Id = ClientDelegates.ServerList;
        root.AddChild(scroll);
        scroll.AddChild(content);
        content.AddChild(list);
        _app.Canvas.SetContent(root, _app.Canvas.Delegates["JoinGame"]);
        return (_app, (GuiServerList)list);
    }

    private static GuiControl Box(int id)
    {
        var b = GuiFixture.Make("FearGui::FGUniversalButton", 10, 10 + ((id - 140044) * 40), 20, 20, ("isSwitch", (byte)1), ("bodyVersion", 3u), ("messageTag", (uint)id));
        b.Id = (uint)id;
        return b;
    }

    private GuiControl PushInfoDialog()
    {
        var dlg = GuiFixture.Make("SimGui::Control", 100, 100, 300, 200);
        dlg.AddChild(Box(ServerBrowser.InfoFavoriteBoxId));
        dlg.AddChild(Box(ServerBrowser.InfoNeverPingBoxId));
        _app!.Canvas.PushDialog(dlg);
        return dlg;
    }

    private static void SelectInfoRow(GuiServerList l, string address, bool fav = false, bool never = false)
    {
        l.AddServer(new GuiServerList.Server(1, "S", address, "1.11", 5, fav) { NeverPing = never, State = GuiServerList.StateInfo });
        l.Selected = 0;
    }

    [Fact]
    public void FavoriteBoxWritesTheSelectedRow() // 004d862c -> 004c52b4
    {
        var (app, l) = Boot();
        SelectInfoRow(l, "IP:127.0.0.1:28001");
        PushInfoDialog();
        ((GuiUniversalButton)app.GuiRt.FindControl("140044")!).On = true;
        Assert.True(app.Delegates.Browser.Message(ServerBrowser.InfoFavoriteId));
        Assert.True(l.Servers[0].Favorite);
        Assert.False(l.Servers[0].NeverPing);
        ((GuiUniversalButton)app.GuiRt.FindControl("140044")!).On = false;
        app.Delegates.Browser.Message(ServerBrowser.InfoFavoriteId);
        Assert.False(l.Servers[0].Favorite);
    }

    [Fact]
    public void NeverPingBoxWritesTheSelectedRow() // 004d86bf -> 004c51f0
    {
        var (app, l) = Boot();
        SelectInfoRow(l, "IP:127.0.0.1:28001");
        PushInfoDialog();
        ((GuiUniversalButton)app.GuiRt.FindControl("140045")!).On = true;
        Assert.True(app.Delegates.Browser.Message(ServerBrowser.InfoNeverPingId));
        Assert.True(l.Servers[0].NeverPing);
        Assert.False(l.Servers[0].Favorite);
    }

    [Fact]
    public void ClickingACheckBoxInTheDialogReachesTheRow() // the button's message goes to the join delegate (004d7bb4)
    {
        var (app, l) = Boot();
        SelectInfoRow(l, "IP:127.0.0.1:28001");
        PushInfoDialog();
        app.Canvas.HandleInput(new RawInput { Device = RawInput.DevMouse, ObjType = RawInput.ObjXAxis, Value = 115 - (int)app.Canvas.CursorX });
        app.Canvas.HandleInput(new RawInput { Device = RawInput.DevMouse, ObjType = RawInput.ObjYAxis, Value = 115 - (int)app.Canvas.CursorY });
        app.Canvas.HandleInput(new RawInput { Device = RawInput.DevMouse, ObjType = RawInput.ObjButton, Index = 0, Action = 1 });
        app.Canvas.Frame();
        app.Canvas.HandleInput(new RawInput { Device = RawInput.DevMouse, ObjType = RawInput.ObjButton, Index = 0, Action = 0 });
        app.Canvas.Frame();
        Assert.True(l.Servers[0].Favorite);
    }

    [Fact]
    public void BoxesStartAsTheRowFlagsWhenTheDialogOpens() // 004d8532, 004d856f
    {
        var (app, l) = Boot();
        SelectInfoRow(l, "IP:127.0.0.1:28001", fav: false, never: true);
        PushInfoDialog();
        app.Delegates.Browser.ShowFlags(l.Servers[0]);
        Assert.False(((GuiUniversalButton)app.GuiRt.FindControl("140044")!).On);
        Assert.True(((GuiUniversalButton)app.GuiRt.FindControl("140045")!).On);
    }

    [Fact]
    public void FlagsNeedASelectedRowWithItsInfo() // 004d7bb4 state byte
    {
        var (app, l) = Boot();
        l.AddServer(new GuiServerList.Server(1, "S", "IP:127.0.0.1:28001", "1.11", 5, false));
        l.Selected = 0;
        PushInfoDialog();
        ((GuiUniversalButton)app.GuiRt.FindControl("140044")!).On = true;
        app.Delegates.Browser.Message(ServerBrowser.InfoFavoriteId);
        Assert.False(l.Servers[0].Favorite);
        app.Delegates.Browser.Message(ServerBrowser.InfoRefreshId);
        Assert.Equal(0, app.Delegates.Browser.PendingPings);
    }

    [Fact]
    public void DialogRefreshAsksTheSelectedServerAgain() // 004d8754 -> 0054fff0
    {
        var (app, l) = Boot();
        SelectInfoRow(l, "IP:127.0.0.1:28001");
        PushInfoDialog();
        Assert.True(app.Delegates.Browser.Message(ServerBrowser.InfoRefreshId));
        Assert.Equal(1, app.Delegates.Browser.PendingPings);
        Assert.Equal(GuiServerList.StateInfo, l.Servers[0].State);
    }

    [Fact]
    public void ARefreshAllLeavesNeverPingRowsOutOfTheQuery() // 004c525c
    {
        var (app, l) = Boot();
        l.AddServer(new GuiServerList.Server(1, "N", "IP:127.0.0.1:28001", "1.11", 5, false) { NeverPing = true });
        l.AddServer(new GuiServerList.Server(2, "F", "IP:127.0.0.1:28002", "1.11", 5, true));
        app.Interp.SetVariable("$pref::lanOnly", "TRUE");
        app.Delegates.Browser.QueryServers();
        Assert.Equal(["IP:127.0.0.1:28001"], app.Delegates.Browser.SeenAddresses);
    }

    [Fact]
    public void LeavingTheScreenWritesTheServerFile() // 004d7630, 004c4e2c
    {
        var (app, l) = Boot();
        app.Interp.Evaluate("addGameServer(\"IP:127.0.0.1:28001\", \"A\", \"1.11\", \"30\", \"TRUE\", \"base\", \"Raindance\", \"CTF\", \"7\", \"TRUE\");");
        Assert.Single(l.Servers);
        Assert.True(l.Servers[0].Favorite && l.Servers[0].NeverPing);
        app.Canvas.SetContent(new GuiControl { Name = "Other" });
        var file = Path.Combine(_root, "config", "GameServerList.cs");
        Assert.True(File.Exists(file));
        Assert.Contains("addGameServer(\"IP:127.0.0.1:28001\", \"A\", \"1.11\", \"30\", \"TRUE\", \"base\", \"Raindance\", \"CTF\", \"7\", \"TRUE\");\n", File.ReadAllText(file));
    }

    [Fact]
    public void AddGameServerAcceptsTheSavedFormAndReplacesByAddress() // 0041e470, 004c5320
    {
        var (app, l) = Boot();
        app.Interp.Evaluate("addGameServer(\"IP:127.0.0.1:28001\", \"A\", \"1.11\", \"30\", \"FALSE\");");
        app.Interp.Evaluate("addGameServer(\"ip:127.0.0.1:28001\", \"B\", \"1.11\", \"31\", \"TRUE\", \"TRUE\");");
        Assert.Single(l.Servers);
        Assert.Equal("B", l.Servers[0].Name);
        Assert.True(l.Servers[0].Favorite && l.Servers[0].NeverPing);
        Assert.Equal("TRUE", app.Interp.Evaluate("addGameServer(\"IP:127.0.0.1:1\", \"\", \"1\", \"1\", \"1\");"));
        Assert.Single(l.Servers);
    }
}

public class MasterMotdTests
{
    [Fact]
    public void DialogTextIsTheMasterNameThenTheMessage() // 004d7fbf
    {
        Assert.Equal("<f1>Message of the day from: Harness<f0>\n\nLocal test master", ServerBrowser.MotdText("Harness", "Local test master"));
        Assert.Equal("<f1>Message of the day from: <f0>\n\n", ServerBrowser.MotdText("", ""));
    }

    [Fact]
    public void DialogTextIsCutAt1023Characters() // 004d7fbf
    {
        var text = ServerBrowser.MotdText("M", new string('x', 5000));
        Assert.Equal(1023, text.Length);
        Assert.StartsWith("<f1>Message of the day from: M<f0>\n\nxxx", text);
    }
}
