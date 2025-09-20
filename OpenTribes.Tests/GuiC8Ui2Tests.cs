// Tests of the second C8 GUI fixes: the loading progress text, the join mode of +connect, the inventory lists, the score list
// and the server menu of the Tab key. Synthetic data only (R5).
using OpenTribes.Client.App;
using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Script;

namespace OpenTribes.Tests;

public sealed class GuiC8Ui2Tests : IDisposable
{
    private readonly string mRoot = Path.Combine(Path.GetTempPath(), "ot-c8ui2-" + Guid.NewGuid().ToString("N"));
    private ClientApp? mApp;

    public GuiC8Ui2Tests()
    {
        Directory.CreateDirectory(Path.Combine(mRoot, "config"));
        Directory.CreateDirectory(Path.Combine(mRoot, "temp"));
    }

    public void Dispose()
    {
        mApp?.Dispose();
        try { Directory.Delete(mRoot, true); } catch (IOException) { }
    }

    private ClientApp Boot(string consoleCs)
    {
        File.WriteAllText(Path.Combine(mRoot, "console.cs"),
            "newClient(); focusClient();\n" +
            "newObject(MainWindow, SimGui::Canvas, \"Tribes\", 640, 480, True, \"512 384\", \"1024 768\");\n" +
            "function Game::EndFrame() {}\n$ConsoleWorld::DefaultSearchPath = \"config;base;temp\";\n" + consoleCs);
        ClientOptions opt = ClientOptions.Parse(new[] { "--root", mRoot, "--headless" }, out string err)!;
        Assert.Equal("", err);
        mApp = new ClientApp(opt);
        Assert.True(mApp.Start(out string error), error);
        return mApp;
    }

    // ---- loading screen ----

    [Fact]
    public void FormattedTextWithNoHeightGrowsToItsText() // Loading.gui: ProgressText is stored 273 x 0 and a script fills it
    {
        var fx = new GuiFixture();
        fx.Host.SetFont("sf_white_10.pft", new OpenTribes.Platform.Gfx.PftFont { Flags = OpenTribes.Platform.Gfx.PftFont.FlagFixedWidth, GlyphCellWidth = 5, CellHeight = 10 });
        GuiControl text = GuiFixture.Make("FearGuiFormattedText", 1, 0, 273, 0);
        GuiControl root = GuiFixture.Make("SimGui::TSControl", 0, 0, 640, 480);
        root.AddChild(text);
        fx.Content(root);
        text.SetValue("<jc><f1>Establishing uplink with satellite network...");
        Assert.True(text.H >= 10);
    }

    [Fact]
    public void ConnectFromTheCommandLineIsAJoin() // client-net.md 1.1: onConnection loads Loading.gui only in JOIN mode, which +connect never set
    {
        Boot("$connectAddress = \"127.0.0.1:28001\";");
        Assert.Equal("", mApp!.Interp.PeekVariable("$pref::PlayGameMode"));
        mApp.Interp.Evaluate("connect(\"127.0.0.1:28001\");");
        Assert.Equal("JOIN", mApp.Interp.PeekVariable("$pref::PlayGameMode"));
    }

    [Fact]
    public void ConnectKeepsTheModeOfTheMenu()
    {
        Boot("$connectAddress = \"127.0.0.1:28001\"; $pref::PlayGameMode = \"HOST\";");
        mApp!.Interp.Evaluate("connect(\"127.0.0.1:28001\");");
        Assert.Equal("HOST", mApp.Interp.PeekVariable("$pref::PlayGameMode"));
    }

    // ---- inventory lists ----

    private static InventoryEntry Item(int i, string heading, string name, int count = 0, int price = 0, bool shop = false, bool buy = false) =>
        new(i, heading, name, price, count, shop, buy);

    [Fact]
    public void InventoryListShowsCarriedItemsUnderTheirHeadingsInHeadingOrder() // gui.md 3.3 FearGuiInventory [code 0048afcc, 0048a700]
    {
        var items = new[]
        {
            Item(1, "cAmmo", "Bullet", count: 100), Item(2, "bWeapons", "Blaster", count: 1),
            Item(3, "bWeapons", "Chaingun", count: 1), Item(4, "bWeapons", "Mortar"), Item(5, "aArmor", "Light Armor", count: 1),
        };
        List<GuiInventoryList.Row> rows = GuiInventoryList.Build(items, purchase: false);
        Assert.Equal(new[] { "aArmor", "Light Armor", "bWeapons", "Blaster", "Chaingun", "cAmmo", "Bullet" }, rows.Select(r => r.IsHeader ? r.Heading : r.Text));
        Assert.Equal(100, rows.Last().Value);
        Assert.All(rows.Where(r => !r.IsHeader), r => Assert.True(r.Enabled));
    }

    [Fact]
    public void PurchaseListShowsShoppingItemsWithPricesAndGraysTheOnesNotBuying() // gui.md 3.3 FearGuiPurchase [code 0048b05c]
    {
        var items = new[]
        {
            Item(2, "bWeapons", "Blaster", price: 85, shop: true, buy: true), Item(3, "bWeapons", "Chaingun", price: 125, shop: true),
            Item(4, "bWeapons", "Mortar", price: 200),
        };
        List<GuiInventoryList.Row> rows = GuiInventoryList.Build(items, purchase: true);
        Assert.Equal(3, rows.Count);
        Assert.True(rows[0].IsHeader);
        Assert.Equal(85, rows[1].Value);
        Assert.True(rows[1].Enabled);
        Assert.False(rows[2].Enabled);
    }

    [Fact]
    public void ClientHudSourceBuildsInventoryEntriesFromTheDatablocksAndTheMasks()
    {
        var world = new ClientWorld();
        world.DataBlocks.Counts[5] = 3;
        for (int i = 0; i < 3; ++i)
            world.DataBlocks.Blocks[(5, i)] = new ClientDataBlock
            {
                Group = 5, Index = i, Class = "item", Fields = new List<DecodedField>
                {
                    new("description", "Item" + i), new("heading", "bWeapons"), new("price", (long)(10 * (i + 1))), new("showInventory", i == 2 ? 0L : 1L),
                },
            };
        world.Psc.ItemCounts[1] = 4;
        world.Psc.Shopping[0] = 1u << 1;
        world.Psc.Buying[0] = 1u << 1;
        IReadOnlyList<InventoryEntry> list = new ClientHudSource(world).InventoryItems;
        Assert.Equal(2, list.Count);
        Assert.Equal(new InventoryEntry(1, "bWeapons", "Item1", 20, 4, true, true), list[1]);
        Assert.False(list[0].Shopping);
    }

    [Fact]
    public void InventoryListAnswersTheSelectedItemIndexAndCallsTheScript()
    {
        var fx = new GuiFixture();
        fx.Interp.Evaluate("function InventoryList::onSelect(%i) { $sel = %i; }");
        fx.Host.SetFont("sf_white_10.pft", new OpenTribes.Platform.Gfx.PftFont { Flags = OpenTribes.Platform.Gfx.PftFont.FlagFixedWidth, GlyphCellWidth = 5, CellHeight = 10 });
        var src = new StaticHudSource();
        src.Inventory.Add(Item(7, "bWeapons", "Blaster", count: 1));
        fx.Host.Hud = src;
        var list = (GuiInventoryList)GuiFixture.Make("FearGuiInventory", 0, 0, 300, 100);
        list.Name = "InventoryList";
        GuiControl root = GuiFixture.Make("SimGui::TSControl", 0, 0, 640, 480);
        root.AddChild(list);
        fx.Content(root);
        list.PreRender();
        Assert.Equal("-1", list.GetValue());
        list.SelectRow(0, false);          // the heading
        Assert.Equal("-1", list.GetValue());
        list.SelectRow(1, false);
        Assert.Equal("7", list.GetValue());
        Assert.Equal("7", fx.Interp.PeekVariable("$sel"));
    }

    // ---- score list ----

    [Fact]
    public void ScoreHeadingDefinesTheColumnPositions() // Score text: "Player Name\t\x78Team\t\xC8Score"
    {
        string heading = "Player Name\t\x78Team\t\xC8Score";
        Assert.Equal(new[] { 0, 0x78, 0xC8 }, GuiScoreList.Columns(heading));
        Assert.Equal("Player Name\tTeam\tScore", GuiScoreList.HeadingText(heading));
    }

    [Fact]
    public void ScoreLinesListTeamsThenPlayersAndMarkTheLocalPlayer()
    {
        var board = new ScoreBoard("Team Name\t\xD6Score", "Player Name\t\x6FTeam",
            new[] { new ScoreRow(0, "Blood Eagle\t 3", 3), new ScoreRow(1, "Diamond Sword\t 1", 1) },
            new[] { new ScoreRow(2049, "Ann\tBlood Eagle", 3), new ScoreRow(2050, "Bob\tDiamond Sword", 1) }, 2050);
        List<GuiScoreList.Line> lines = GuiScoreList.Build(board);
        Assert.Equal(6, lines.Count);
        Assert.True(lines[0].Heading);
        Assert.True(lines[3].Heading);
        Assert.False(lines[4].Local);
        Assert.True(lines[5].Local);
        Assert.Equal(new[] { 0, 0x6F }, lines[5].Columns);
    }

    [Fact]
    public void ScorePlaceholdersAreReplacedWithNameTeamPingAndLoss() // score strings of dm.cs and ctf.cs: %n %t %p %l
    {
        var rec = new ClientPlayerRecord { Name = "Ann", PingMs = 60, Loss = 0.25 };
        Assert.Equal("Ann\tBlood Eagle\t 3\t60\t25", ClientHudSource.Expand("%n\t%t\t 3\t%p\t%l", rec, "Blood Eagle"));
        Assert.Equal("Blood Eagle\t 0", ClientHudSource.Expand("%t\t 0", null, "Blood Eagle"));
    }

    [Fact]
    public void ScoreBoardOfTheClientWorldSortsByScoreDescending()
    {
        var world = new ClientWorld { PlayerId = 2049 };
        world.Players.ClientHeading = "Player Name\t\x78Score";
        world.Players.Clients[2049] = new ClientPlayerRecord { Id = 2049, Name = "Ann", ScoreText = "%n\t1", SortValue = 1 };
        world.Players.Clients[2050] = new ClientPlayerRecord { Id = 2050, Name = "Bob", ScoreText = "%n\t5", SortValue = 5 };
        ScoreBoard board = new ClientHudSource(world).Scores!;
        Assert.Equal(new[] { "Bob\t5", "Ann\t1" }, board.Clients.Select(c => c.Text));
        Assert.Equal(2049, board.LocalClient);
    }

    // ---- server menu ----

    [Fact]
    public void ChatMenuOnDisplayFollowsSetCMModeAndDeletion() // remoteNewMenu / remoteCancelMenu of menu.cs
    {
        Boot("");
        var game = (GameState)typeof(ClientApp).GetField("mGameState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(mApp)!;
        Eval("newObject(CurServerMenu, ChatMenu, \"Options\"); addCMCommand(CurServerMenu, \"1Change Teams\", clientMenuSelect, \"changeteams\"); setCMMode(CurServerMenu, 1);");
        ServerMenuView? view = mApp!.CurrentMenu();
        Assert.NotNull(view);
        Assert.Equal("Options", view!.Title);
        Assert.Equal(('1', "Change Teams"), view.Items[0]);
        Eval("deleteObject(CurServerMenu);");
        Assert.Null(mApp.CurrentMenu());
        Assert.NotNull(game);
    }

    private string Eval(string text) => mApp!.Interp.Evaluate(text);

    // ---- scroll policy ----

    [Fact]
    public void GameScrollFramesAreNotTabStops() // FearGuiScrollCtrl tabStop 004b8bc4: the score list must not keep the Tab key
    {
        GuiControl frame = GuiFixture.Make("FearGui::FearGuiScrollCtrl", 0, 0, 100, 100);
        Assert.False(frame.WantsTab);
        Assert.False(frame.CanTake());
        Assert.True(GuiFixture.Make("SimGui::ScrollCtrl", 0, 0, 100, 100).WantsTab);
    }
}
