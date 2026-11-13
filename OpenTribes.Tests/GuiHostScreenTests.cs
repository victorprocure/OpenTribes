// Tests of the host game screen fixes (issue 36): console variable names without `$`, FGTextList rows and selection, the Connect screen's
// game mode radio. Synthetic data only (R5).
using OpenTribes.Client.App;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Tests;

public sealed class GuiHostScreenTests : IDisposable
{
    private readonly string mRoot = Path.Combine(Path.GetTempPath(), "ot-hostscreen-" + Guid.NewGuid().ToString("N"));
    private ClientApp? mApp;

    public GuiHostScreenTests()
    {
        Directory.CreateDirectory(Path.Combine(mRoot, "config"));
        Directory.CreateDirectory(Path.Combine(mRoot, "temp"));
    }

    public void Dispose()
    {
        mApp?.Dispose();
        try { Directory.Delete(mRoot, true); } catch (IOException) { }
    }

    [Fact]
    public void EditBoundToAVariableWithoutDollarReadsAndWritesTheGlobal() // 005f3324 puts the `$` in front: CreateServer.gui binds Server::HostName
    {
        var fx = new GuiFixture();
        fx.Interp.SetVariable("$Server::HostName", "TRIBES Server");
        GuiControl edit = GuiFixture.Make("FearGui::TestEdit", 0, 0, 100, 19, ("active", (byte)1), ("maxLength", 80u));
        edit.ConsoleVar = "Server::HostName";
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        root.AddChild(edit);
        fx.Content(root);
        fx.Frame();
        Assert.Equal("TRIBES Server", edit.GetValue());
        edit.SetValue("Other");
        Assert.Equal("Other", fx.Interp.PeekVariable("$Server::HostName"));
    }

    private static (GuiFixture Fx, GuiControl Root, GuiScroll Scroll, GuiList List) MissionList(int rows)
    {
        var fx = new GuiFixture();
        fx.Host.SetFont("sf_orange214_10.pft", new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 5, CellHeight = 13 });
        fx.Host.SetFont("sf_white_10.pft", new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 5, CellHeight = 13 });
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        GuiControl scroll = GuiFixture.Make("FearGui::FearGuiScrollCtrl", 0, 0, 100, 60, ("verticalPolicy", 2u), ("horizontalPolicy", 1u));
        GuiControl content = GuiFixture.Make("SimGui::ScrollContentCtrl", 0, 0, 100, 60);
        GuiControl list = GuiFixture.Make("FearGui::FGTextList", 0, 0, 640, 60, ("active", (byte)1));
        list.Name = "MissionList";
        list.ConsoleVar = "$pref::LastMission";
        root.AddChild(scroll);
        scroll.AddChild(content);
        content.AddChild(list);
        fx.Content(root);
        var l = (GuiList)list;
        for (int i = 0; i < rows; ++i)
            l.AddEntry("Mission" + i, i);
        fx.Frame();
        return (fx, root, (GuiScroll)scroll, l);
    }

    [Fact]
    public void FgTextListRowsAreTheOrangeFontHeightPlusFour() // 00489d00: cell height = sf_orange214_10 height + 4
    {
        var (_, _, _, list) = MissionList(3);
        Assert.Equal(17, list.RowHeight);
    }

    [Fact]
    public void TextListValueSetsByTextAndTheBoundVariableDoesNotSelect() // 0054bf54 (set by text), 00539dcc (variable changed is a return)
    {
        var (fx, _, _, list) = MissionList(5);
        list.SetValue("Mission3");
        Assert.Equal(3, list.Selected);
        Assert.Equal("Mission3", list.GetValue());
        fx.Interp.SetVariable("$pref::LastMission", "Mission1");
        fx.Frame();
        Assert.Equal(3, list.Selected);
        list.SetValue("mission2");   // the compare is exact
        Assert.Equal(3, list.Selected);
    }

    [Fact]
    public void SelectingARowBelowTheViewScrollsItIntoView() // 0053caa0: the list moves by the missing distance
    {
        var (fx, _, scroll, list) = MissionList(10);
        list.SetValue("Mission3");   // row 3 spans 51..68 in a 60 high view
        Assert.Equal(8, scroll.ScrollY);
        list.SetValue("Mission0");
        Assert.Equal(0, scroll.ScrollY);
    }

    [Fact]
    public void ConnectScreenActivatesTheRadioOfTheGameMode() // 004dbea8: HOST 100105, TRAIN 100998, anything else 100103
    {
        File.WriteAllText(Path.Combine(mRoot, "console.cs"),
            "newClient(); focusClient();\n" +
            "newObject(MainWindow, SimGui::Canvas, \"Tribes\", 640, 480, True, \"512 384\", \"1024 768\");\n" +
            "function Game::EndFrame() {}\n$ConsoleWorld::DefaultSearchPath = \"config;base;temp\";\n$pref::PlayGameMode = \"HOST\";\n");
        ClientOptions opt = ClientOptions.Parse(new[] { "--root", mRoot, "--headless" }, out string err)!;
        Assert.Equal("", err);
        mApp = new ClientApp(opt);
        Assert.True(mApp.Start(out string error), error);
        foreach (var (mode, id) in new[] { ("HOST", 100105u), ("TRAIN", 100998u), ("JOIN", 100103u), ("", 100103u) })
        {
            mApp.Interp.SetVariable("$pref::PlayGameMode", mode);
            GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
            var radios = new Dictionary<uint, GuiUniversalButton>();
            foreach (var (rid, name) in new[] { (100103u, "JOIN"), (100105u, "HOST"), (100998u, "TRAIN") })
            {
                var b = (GuiUniversalButton)GuiFixture.Make("FearGui::FGUniversalButton", 0, 0, 50, 20,
                    ("radioId", 140600u), ("isSwitch", 1u), ("active", (byte)1), ("on", rid == 100103u ? 1u : 0u));
                b.Command = "$pref::PlayGameMode = \"" + name + "\";";
                b.Id = rid;
                root.AddChild(b);
                radios[rid] = b;
            }
            mApp.Canvas.SetContent(root, mApp.Canvas.Delegates["Connect"]);
            Assert.True(radios[id].On);
            Assert.Single(radios.Values, r => r.On);
        }
    }
}
