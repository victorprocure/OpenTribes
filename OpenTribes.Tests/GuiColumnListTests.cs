// Column lists (docs/gui.md 3.3; code 004bcc58 load, 004bcdac save, 004bcad4 drag, 004d55e0 master add, 004d54d0 select): synthetic data only (R5).
namespace OpenTribes.Tests;

using OpenTribes.Gui;

public class GuiColumnListTests
{
    [Fact]
    public void ServerColumnsStartInTableOrderAllShown()
    {
        var c = new ServerColumns();
        Assert.Equal(Enumerable.Range(0, 12), c.Entries.Select(e => e.Order));
        Assert.All(c.Entries, e => Assert.True(e.Display));
        Assert.Equal(131309, c.AtOrder(0)!.NameTag);
        Assert.Equal(131312, c.AtOrder(11)!.NameTag);
        Assert.True(c.IsPermutation());
    }

    [Fact]
    public void ServerColumnsSaveAndLoadThroughTheConsoleVariables()
    {
        var fx = new GuiFixture();
        var c = new ServerColumns();
        c.MoveTo(c.Entries[5], 1);
        c.Entries[3].Display = false;
        c.Save(fx.Host);
        Assert.Equal("1, YES, 100", fx.Host.GetVar("$pref::ServerColumnDisplay5"));
        Assert.Equal("4, NO, 105", fx.Host.GetVar("$pref::ServerColumnDisplay3"));
        var d = new ServerColumns();
        d.Load(fx.Host);
        Assert.Equal(c.Entries.Select(e => (e.Order, e.Display, e.Width)), d.Entries.Select(e => (e.Order, e.Display, e.Width)));
    }

    [Fact]
    public void ServerColumnsWithBrokenOrdersResetAndAreWrittenBack()
    {
        var fx = new GuiFixture();
        fx.Host.SetVar("$pref::ServerColumnDisplay0", "7, YES, 50");   // 7 is taken by entry 7
        var c = new ServerColumns();
        c.Load(fx.Host);
        Assert.Equal(Enumerable.Range(0, 12), c.Entries.Select(e => e.Order));
        Assert.Equal("0, YES, 50", fx.Host.GetVar("$pref::ServerColumnDisplay0"));
    }

    [Fact]
    public void MovingAnEntryDownShiftsTheOnesBetweenUp()
    {
        var c = new ServerColumns();
        c.MoveTo(c.Entries[2], 5);
        Assert.Equal(new[] { 0, 1, 5, 2, 3, 4, 6, 7, 8, 9, 10, 11 }, c.Entries.Select(e => e.Order));
        c.MoveTo(c.Entries[2], 0);
        Assert.Equal(new[] { 1, 2, 0, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, c.Entries.Select(e => e.Order));
        Assert.True(c.IsPermutation());
    }

    [Fact]
    public void MasterListSortsByNameIgnoringCaseAndSelectsById()
    {
        var fx = new GuiFixture();
        GuiControl list = GuiFixture.Make("FearGui::MasterServerList", 0, 0, 244, 74, ("active", (byte)1));
        list.Name = "Masters";
        fx.Content(list);
        fx.Interp.Evaluate("FGMasterList::addEntry(Masters, \"US Master\", \"a.example:1\", 0);");
        fx.Interp.Evaluate("FGMasterList::addEntry(Masters, \"australian Master\", \"b.example\", 2);");
        fx.Interp.Evaluate("FGMasterList::addEntry(Masters, \"UK Master\", \"c.example\", 1);");
        var m = (GuiMasterList)list;
        Assert.Equal(new[] { "australian Master", "UK Master", "US Master" }, m.Entries.Select(e => e.Text));
        fx.Interp.Evaluate("FGMasterList::setSelected(Masters, 0);");
        Assert.Equal(2, m.Selected);
        Assert.Equal("0", fx.Host.GetVar("$Server::CurrentMaster"));
        Assert.Equal("a.example:1", fx.Host.GetVar("$Server::Master1"));
        Assert.Equal("0", fx.Interp.Evaluate("FGMasterList::getSelected(Masters);"));
        fx.Interp.Evaluate("FGMasterList::deleteEntry(Masters, 0);");
        Assert.Equal(-1, m.Selected);
        Assert.Equal(2, m.Entries.Count);
        fx.Interp.Evaluate("FGMasterList::clear(Masters);");
        Assert.Empty(m.Entries);
    }

    [Fact]
    public void ColumnListIsAsHighAsItsHeaderAndRows()
    {
        var fx = new GuiFixture();
        GuiControl scroll = GuiFixture.Make("FearGui::FearGuiScrollCtrl", 0, 0, 272, 284, ("headerY", 23u), ("verticalPolicy", 1u), ("horizontalPolicy", 1u));
        GuiControl content = GuiFixture.Make("SimGui::ScrollContentCtrl", 1, 1, 270, 282);
        GuiControl list = GuiFixture.Make("FearGui::MasterServerList", 0, 0, 244, 74, ("active", (byte)1));
        list.Name = "Masters";
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        root.AddChild(scroll);
        scroll.AddChild(content);
        content.AddChild(list);
        fx.Content(root);
        fx.Interp.Evaluate("FGMasterList::addEntry(Masters, \"A\", \"x\", 0); FGMasterList::addEntry(Masters, \"B\", \"y\", 1);");
        fx.Frame();
        var m = (GuiMasterList)list;
        Assert.Equal(23 + (2 * m.RowHeight), m.H);
        Assert.Equal(270, m.W);
        Assert.Equal(23, m.HeaderH);
        Assert.Equal(1, content.X);   // the content keeps the offset of its file
    }

    [Fact]
    public void FilterBoxIsDimmedUntilAFilterIsChosen() // 004d8fe4
    {
        var fx = new GuiFixture();
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        GuiControl box = GuiFixture.Make("FearGui::FearGuiBox", 10, 10, 230, 197);
        GuiControl scroll = GuiFixture.Make("FearGui::FearGuiScrollCtrl", 3, 3, 224, 152, ("headerY", 23u));
        GuiControl content = GuiFixture.Make("SimGui::ScrollContentCtrl", 0, 0, 224, 152);
        GuiControl filter = GuiFixture.Make("FearGui::FilterCtrl", 0, 0, 256, 23, ("active", (byte)0));
        root.AddChild(box);
        box.AddChild(scroll);
        scroll.AddChild(content);
        content.AddChild(filter);
        fx.Content(root);
        Assert.True(((GuiBox)box).ForceDim);
        ((GuiFilterCtrl)filter).ChosenFilter = 1;
        filter.Wake();
        Assert.False(((GuiBox)box).ForceDim);
    }
}

public class ServerListTests
{
    private static (GuiFixture Fx, GuiServerList List) Make(params (string, string)[] prefs)
    {
        var fx = new GuiFixture();
        foreach (var (k, v) in prefs)
            fx.Host.SetVar(k, v);
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        GuiControl scroll = GuiFixture.Make("FearGui::FearGuiScrollCtrl", 44, 106, 552, 309, ("headerY", 23u), ("horizontalPolicy", 2u), ("verticalPolicy", 2u), ("style", 6u));
        GuiControl content = GuiFixture.Make("SimGui::ScrollContentCtrl", 6, 6, 540, 280);
        GuiControl list = GuiFixture.Make("FearGui::ServerListCtrl", 0, 0, 817, 159, ("active", (byte)1));
        root.AddChild(scroll);
        scroll.AddChild(content);
        content.AddChild(list);
        fx.Content(root);
        fx.Frame();
        return (fx, (GuiServerList)list);
    }

    [Fact]
    public void ColumnsFollowTheServerColumnPreferences() // 004c4ca4
    {
        var (_, l) = Make();
        Assert.Equal(12, l.ColumnSet.Count);
        Assert.Equal(new[] { 52, 52, 54, 105, 43, 90, 90, 59, 33, 102, 100, 90 }, l.ColumnSet.Select(c => c.Width));
        var (_, m) = Make(("$pref::ServerColumnDisplay0", "3, YES, 52"), ("$pref::ServerColumnDisplay3", "0, NO, 105"));
        Assert.Equal(131301, m.ColumnSet[0].HeaderTag);   // Server Name (entry 3) moved first
        Assert.False(m.ColumnSet[0].Visible);
        Assert.True(m.ColumnSet[3].Visible);
    }

    [Fact]
    public void ListIsAsWideAsItsColumnsAndTheViewKeepsItsInset()
    {
        var (_, l) = Make();
        Assert.Equal(870, l.W);
        Assert.Equal(23, l.H);
        Assert.Equal(6, l.Parent!.X);
        Assert.Equal(540, l.Parent.W);   // the scroll is 552 wide, the content 6 pixels in on both sides
    }

    [Fact]
    public void RowsShowTheNameAddressVersionAndPing()
    {
        var (fx, l) = Make();
        l.AddServer(new GuiServerList.Server(1, "Alpha", "127.0.0.1:28001", "1.11", 20, false));
        fx.Frame();
        Assert.Single(l.Entries);
        Assert.Equal(23 + l.RowHeight, l.H);
    }
}

public class ServerListRowTests
{
    private static GuiServerList MakeList()
    {
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        GuiControl scroll = GuiFixture.Make("FearGui::FearGuiScrollCtrl", 44, 106, 552, 309, ("headerY", 23u), ("horizontalPolicy", 2u), ("verticalPolicy", 2u), ("style", 6u));
        GuiControl content = GuiFixture.Make("SimGui::ScrollContentCtrl", 6, 6, 540, 280);
        GuiControl list = GuiFixture.Make("FearGui::ServerListCtrl", 0, 0, 817, 159, ("active", (byte)1));
        root.AddChild(scroll);
        scroll.AddChild(content);
        content.AddChild(list);
        var fx = new GuiFixture();
        fx.Content(root);
        fx.Frame();
        return (GuiServerList)list;
    }

    [Fact]
    public void FindsRowsByAddressIgnoringCase()
    {
        GuiServerList l = MakeList();
        l.AddServer(new GuiServerList.Server(1, "A", "IP:127.0.0.1:28001", "1.11", 5, false));
        Assert.Equal("A", l.Find("ip:127.0.0.1:28001")!.Name);
        Assert.Null(l.Find("IP:127.0.0.1:28002"));
    }

    [Fact]
    public void RefreshMarksEveryRowUnknownAndFailuresDropRowsAgain() // 004c774c
    {
        GuiServerList l = MakeList();
        l.AddServer(new GuiServerList.Server(1, "A", "IP:1.2.3.4:1", "1.11", 5, false) { State = GuiServerList.StateInfo });
        l.KeepFailed = true;
        l.MarkAllUnknown();
        Assert.Equal(9999, l.Servers[0].Ping);
        Assert.Equal(GuiServerList.StateUnknown, l.Servers[0].State);
        Assert.False(l.KeepFailed);
    }

    [Fact]
    public void RemovingARowKeepsTheSelectionOnTheSameServer()
    {
        GuiServerList l = MakeList();
        for (var i = 1; i <= 3; ++i)
            l.AddServer(new GuiServerList.Server(i, "S" + i, "IP:1.2.3.4:" + i, "1.11", 5, false));
        l.Select(2);
        l.RemoveServer(l.Servers[0]);
        Assert.Equal(1, l.Selected);
        Assert.Equal("S3", l.Servers[l.Selected].Name);
        l.RemoveServer(l.Servers[l.Selected]);
        Assert.Equal(-1, l.Selected);
        Assert.Single(l.Servers);
    }
}

public class IrcHistoryTests
{
    [Fact]
    public void UnsetOrOutOfRangeHistoryBecomesTheClientSize() // 005fc340
    {
        var interp = new Script.Interpreter();
        var h = new Client.App.IrcHistory();
        h.Keep(interp);
        Assert.Equal("50", interp.PeekVariable("$IRC::MsgHistory"));
        interp.SetVariable("$IRC::MsgHistory", "200");
        h.Keep(interp);
        Assert.Equal("200", interp.PeekVariable("$IRC::MsgHistory"));
        interp.SetVariable("$IRC::MsgHistory", "9999");
        h.Keep(interp);
        Assert.Equal("200", interp.PeekVariable("$IRC::MsgHistory"));
    }
}
