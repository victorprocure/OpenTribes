// Tests of the second C8 GUI fixes: the loading progress text, the join mode of +connect, the inventory lists, the score list
// and the server menu of the Tab key. Synthetic data only (R5).
namespace OpenTribes.Tests;

using OpenTribes.Client.App;
using OpenTribes.Game;
using OpenTribes.Gui;

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
        try
        { Directory.Delete(mRoot, true); }
        catch (IOException) { }
    }

    private ClientApp Boot(string consoleCs)
    {
        File.WriteAllText(Path.Combine(mRoot, "console.cs"),
            "newClient(); focusClient();\n" +
            "newObject(MainWindow, SimGui::Canvas, \"Tribes\", 640, 480, True, \"512 384\", \"1024 768\");\n" +
            "function Game::EndFrame() {}\n$ConsoleWorld::DefaultSearchPath = \"config;base;temp\";\n" + consoleCs);
        var opt = ClientOptions.Parse(["--root", mRoot, "--headless"], out var err)!;
        Assert.Equal("", err);
        mApp = new ClientApp(opt);
        Assert.True(mApp.Start(out var error), error);
        return mApp;
    }

    // ---- loading screen ----

    [Fact]
    public void FormattedTextWithNoHeightGrowsToItsText() // Loading.gui: ProgressText is stored 273 x 0 and a script fills it
    {
        var fx = new GuiFixture();
        fx.Host.SetFont("sf_white_10.pft", new Platform.Gfx.PftFont { Flags = Platform.Gfx.PftFont.FlagFixedWidth, GlyphCellWidth = 5, CellHeight = 10 });
        var text = GuiFixture.Make("FearGuiFormattedText", 1, 0, 273, 0);
        var root = GuiFixture.Make("SimGui::TSControl", 0, 0, 640, 480);
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
        var rows = GuiInventoryList.Build(items, purchase: false);
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
        var rows = GuiInventoryList.Build(items, purchase: true);
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
        for (var i = 0; i < 3; ++i)
            world.DataBlocks.Blocks[(5, i)] = new ClientDataBlock
            {
                Group = 5,
                Index = i,
                Class = "item",
                Fields =
                [
                    new("description", "Item" + i), new("heading", "bWeapons"), new("price", (long)(10 * (i + 1))), new("showInventory", i == 2 ? 0L : 1L),
                ],
            };
        world.Psc.ItemCounts[1] = 4;
        world.Psc.Shopping[0] = 1u << 1;
        world.Psc.Buying[0] = 1u << 1;
        var list = new ClientHudSource(world).InventoryItems;
        Assert.Equal(2, list.Count);
        Assert.Equal(new InventoryEntry(1, "bWeapons", "Item1", 20, 4, true, true), list[1]);
        Assert.False(list[0].Shopping);
    }

    // FGHSensorPing 004ea720: drawn for the own player and for an object flagged 0x20000000 (Vehicle, Flier), nothing for a controlled Turret.
    [Fact]
    public void SensorHudShowsForPlayerAndVehiclesButNotForATurret()
    {
        var world = new ClientWorld();
        var src = new ClientHudSource(world);
        Assert.False(src.SensorHudShown);
        world.Psc.ControlGhost = 5;
        foreach ((var cls, var shown) in new[] { ("Player", true), ("Vehicle", true), ("Flier", true), ("Turret", false), ("ObserverCamera", false) })
        {
            world.Psc.ControlClass = cls;
            Assert.Equal(shown, src.SensorHudShown);
        }
    }

    [Fact]
    public void PlayerViewIsOffInTheObserverCamera()
    {
        var world = new ClientWorld();
        var src = new ClientHudSource(world);
        Assert.False(src.InPlayerView);
        world.Psc.ControlGhost = 5;
        world.Psc.ControlClass = "Player";
        Assert.True(src.InPlayerView);
        world.Psc.ControlClass = "ObserverCamera";
        Assert.True(src.HasPlayerState);
        Assert.False(src.InPlayerView);
    }

    // CompassHud 004e7e5c reads vtable +0x178 of the control object (`00413850`, the Euler z of its transform; the same function for
    // Player, Vehicle and Flier). A piloted Flier is the control object, so its rotation drives the dial, not a "rotZ" it does not have.
    [Fact]
    public void CompassYawOfAPilotedFlierIsItsRotationZ()
    {
        var world = new ClientWorld();
        var src = new ClientHudSource(world);
        world.Psc.ControlGhost = 5;
        world.Psc.ControlClass = "Flier";
        world.Psc.ControlState = new Dictionary<string, object?> { ["rot"] = new[] { 0.4, 0.0, -0.3 } };
        Assert.Equal(-0.3, src.Yaw);
        src.ObjectYaw = ghost => ghost == 5 ? -0.25 : null;
        Assert.Equal(-0.25, src.Yaw);
    }

    // ObserverCamera vtable +0x178 is 00413850 as well: the Euler z of the camera transform built from its rotation (+0x264).
    [Fact]
    public void CompassYawOfAnObserverCameraIsItsRotationZ()
    {
        var world = new ClientWorld();
        var src = new ClientHudSource(world);
        world.Psc.ControlGhost = 2;
        world.Psc.ControlClass = "ObserverCamera";
        world.Psc.ControlState = new Dictionary<string, object?> { ["rot"] = new[] { 0.2, 0.0, 1.25 }, ["camMode"] = 2L };
        Assert.Equal(1.25, src.Yaw);
        var body = new ClientGhost { Index = 7, Class = "Player" };
        body.State["rotZ"] = 2.5;
        world.Ghosts.Insert(body);
        world.Psc.ControlState = new Dictionary<string, object?>
        {
            ["rot"] = new[] { 0.2, 0.0, 1.25 },
            ["camMode"] = 3L,
            ["orbitGhost"] = 7L,
            ["orbit"] = new[] { 0.0, 10.0, -1.0 },
        };
        Assert.Equal(2.5, src.Yaw);
        world.Psc.ControlState["orbit"] = new[] { 0.0, 10.0, 5.0 };
        Assert.Equal(1.25, src.Yaw);
    }

    [Fact]
    public void CompassYawOfAPlayerIsRotZUnlessTheHostGivesTheMountHeading()
    {
        var world = new ClientWorld();
        var src = new ClientHudSource(world);
        world.Psc.ControlGhost = 5;
        world.Psc.ControlClass = "Player";
        world.Psc.ControlState = new Dictionary<string, object?> { ["rotZ"] = 1.5 };
        Assert.Equal(1.5, src.Yaw);
        src.ObjectYaw = _ => 2.25;
        Assert.Equal(2.25, src.Yaw);
        src.ObjectYaw = _ => null;
        Assert.Equal(1.5, src.Yaw);
    }

    // 004349e4: the ObserverCamera forwards vtable +0xf8 to its orbit object, 0.0 without one; Player 00413828 is 1.0 once dead.
    [Fact]
    public void DamageLevelOfAnObserverCameraIsThatOfItsOrbitObject()
    {
        var world = new ClientWorld();
        var src = new ClientHudSource(world);
        var cam = new ClientGhost { Index = 2, Class = "ObserverCamera" };
        var body = new ClientGhost { Index = 7, Class = "Player" };
        world.Ghosts.Insert(cam);
        world.Ghosts.Insert(body);
        world.Psc.ControlGhost = 2;
        world.Psc.ControlClass = "ObserverCamera";
        world.Psc.ControlState = new Dictionary<string, object?> { ["camMode"] = 3L, ["orbitGhost"] = 7L };
        Assert.Equal(0, src.DamageLevel);
        body.State["damage"] = 0.25;
        Assert.Equal(0.25, src.DamageLevel);
        body.State["dead"] = 1L;
        Assert.Equal(1, src.DamageLevel);
        world.Psc.ControlState = new Dictionary<string, object?> { ["camMode"] = 4L };
        Assert.Equal(0, src.DamageLevel);
    }

    [Fact]
    public void InventoryListAnswersTheSelectedItemIndexAndCallsTheScript()
    {
        var fx = new GuiFixture();
        fx.Interp.Evaluate("function InventoryList::onSelect(%i) { $sel = %i; }");
        fx.Host.SetFont("sf_white_10.pft", new Platform.Gfx.PftFont { Flags = Platform.Gfx.PftFont.FlagFixedWidth, GlyphCellWidth = 5, CellHeight = 10 });
        var src = new StaticHudSource();
        src.Inventory.Add(Item(7, "bWeapons", "Blaster", count: 1));
        fx.Host.Hud = src;
        var list = (GuiInventoryList)GuiFixture.Make("FearGuiInventory", 0, 0, 300, 100);
        list.Name = "InventoryList";
        var root = GuiFixture.Make("SimGui::TSControl", 0, 0, 640, 480);
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
        var heading = "Player Name\t\x78Team\t\xC8Score";
        Assert.Equal([0, 0x78, 0xC8], GuiScoreList.Columns(heading));
        Assert.Equal("Player Name\tTeam\tScore", GuiScoreList.HeadingText(heading));
    }

    [Fact]
    public void ScoreLinesListTeamsThenPlayersAndMarkTheSelectedPlayer()
    {
        var board = new ScoreBoard("Team Name\t\xD6Score", "Player Name\t\x6FTeam",
            new[] { new ScoreRow(0, "Blood Eagle\t 3", 3), new ScoreRow(1, "Diamond Sword\t 1", 1) },
            new[] { new ScoreRow(2049, "Ann\tBlood Eagle", 3), new ScoreRow(2050, "Bob\tDiamond Sword", 1) }, 2050);
        var lines = GuiScoreList.Build(board);
        Assert.Equal(6, lines.Count);
        Assert.True(lines[0].Heading);
        Assert.True(lines[3].Heading);
        Assert.False(lines[4].Local);
        Assert.False(lines[5].Local);
        Assert.Equal([0, 0x6F], lines[5].Columns);
        Assert.True(GuiScoreList.Build(board, 2050)[5].Local);
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
        var board = new ClientHudSource(world).Scores!;
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
        var view = mApp!.CurrentMenu();
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
        var frame = GuiFixture.Make("FearGui::FearGuiScrollCtrl", 0, 0, 100, 100);
        Assert.False(frame.WantsTab);
        Assert.False(frame.CanTake());
        Assert.True(GuiFixture.Make("SimGui::ScrollCtrl", 0, 0, 100, 100).WantsTab);
    }

    // CommandTeamCtrl focusIn 004a93d0, CommandItemCtrl 004aa0fc, CommandTurretCtrl 004aafb4: the commander screen never holds the
    // keyboard, so the chat menu and command keys reach the action maps there. Other lists still take focus.
    [Theory]
    [InlineData("FearGui::CommandTeamCtrl")]
    [InlineData("FearGui::CommandItemCtrl")]
    [InlineData("FearGui::CommandTurretCtrl")]
    [InlineData("FearGui::FGTextList")]
    [InlineData("FearGuiFormattedText")]
    [InlineData("FearGui::FGIRCView")]
    [InlineData("FearGui::FGIRCNicknames")]
    [InlineData("FearGui::FGIRCChannelCtrl")]
    [InlineData("FearGui::FGIRCTopicCtrl")]
    [InlineData("FearGui::FGIRCScrollChannel")]
    [InlineData("FearGui::FGMenuCtrl")]
    [InlineData("FearGui::FGHInventory")]
    public void StubFocusClassesRefuseFocus(string cls) => Assert.False(GuiFixture.Make(cls, 0, 0, 100, 100).CanTake());

    [Fact]
    public void OtherListsTakeFocus() => Assert.True(GuiFixture.Make("FearGui::ServerListCtrl", 0, 0, 100, 100).CanTake());
}

public class CompassWaypointTests
{
    // 004e7e5c: "%d" of the truncated 2D distance (at most 999), "000" without a waypoint or within 2 metres on both axes.
    [Fact]
    public void ReadoutShowsTheDistanceOrZeros()
    {
        Assert.Equal("000", GuiCompassHud.Readout(null));
        Assert.Equal("000", GuiCompassHud.Readout(new HudWaypoint(10, 10, 12.9, 8.1)));
        Assert.Equal("3", GuiCompassHud.Readout(new HudWaypoint(10, 10, 13.5, 10)));
        Assert.Equal("5", GuiCompassHud.Readout(new HudWaypoint(0, 0, 3, 4)));
        Assert.Equal("141", GuiCompassHud.Readout(new HudWaypoint(0, 0, 100, 100)));
        Assert.Equal("999", GuiCompassHud.Readout(new HudWaypoint(0, 0, 5000, 0)));
    }

    // The tip (0, -24) turned by pi/2 - (bearing - yaw): a waypoint straight ahead (bearing = yaw + pi/2) points up, one to
    // the right of the view (bearing = yaw) points right.
    [Fact]
    public void ArrowPointsAtTheWaypointRelativeToTheView()
    {
        var ahead = GuiCompassHud.Arrow(0, new HudWaypoint(0, 50, 0, 0), false, 100, 100);
        Assert.Equal(100, ahead[0].X, 3);
        Assert.Equal(76, ahead[0].Y, 3);
        Assert.Equal(106, ahead[1].X, 3);
        Assert.Equal(84, ahead[1].Y, 3);
        Assert.Equal(94, ahead[2].X, 3);
        var right = GuiCompassHud.Arrow(0, new HudWaypoint(50, 0, 0, 0), false, 100, 100);
        Assert.Equal(124, right[0].X, 3);
        Assert.Equal(100, right[0].Y, 3);
        var low = GuiCompassHud.Arrow(0, new HudWaypoint(0, 50, 0, 0), true, 100, 100);
        Assert.Equal(85.6, low[0].Y, 3);
    }
}
