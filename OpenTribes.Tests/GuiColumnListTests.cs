// Column lists (docs/gui.md 3.3; code 004bcc58 load, 004bcdac save, 004bcad4 drag, 004d55e0 master add, 004d54d0 select): synthetic data only (R5).
using OpenTribes.Gui;

namespace OpenTribes.Tests;

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
        Assert.Equal(23 + 2 * m.RowHeight, m.H);
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

public class IrcHistoryTests
{
    [Fact]
    public void UnsetOrOutOfRangeHistoryBecomesTheClientSize() // 005fc340
    {
        var interp = new OpenTribes.Script.Interpreter();
        var h = new OpenTribes.Client.App.IrcHistory();
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
