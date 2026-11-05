// The Objects and Controllables lists of the command screen and the observer panel (docs/gui.md 3.3 "The command screen panels"):
// CommandItemCtrl [004a94d4, 004a98b8, 004a9a08, 004a973c, 004a97b0, 004a9714], CommandTurretCtrl [004aa21c, 004aa6b8, 004aa814, 004aa644, 004aa4b4, 004aa48c],
// the slot helpers [004d27d0, 004d28a8, 004d2798, 004d2a44, 004d29fc], CMDObserve [004b4818, 004b48bc] and the console functions [0043a670, 0043a6e8]. Synthetic data only.
using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;
using Rig = OpenTribes.Tests.CommandOrdersTests.Rig;

namespace OpenTribes.Tests;

public class CommandListsTests
{
    private static PbmpBitmap Pic(int w) => new() { Width = w, Height = 8 };

    // A rig with both lists in the tree. Widths tell the pictures apart: damage low 5, medium 6, high 7, item active 3, inactive 4.
    private static (Rig Rig, GuiCommandItems Items, GuiCommandTurrets Turrets) ListRig()
    {
        var r = new Rig();
        var items = (GuiCommandItems)GuiFixture.Make("FearGui::CommandItemCtrl", 450, 100, 640, 15, ("active", (byte)1));
        var turrets = (GuiCommandTurrets)GuiFixture.Make("FearGui::CommandTurretCtrl", 450, 100, 640, 15, ("active", (byte)1));
        r.Root.AddChild(items);
        r.Root.AddChild(turrets);
        var host = r.Fx.Host;
        host.SetFont("if_w_10b.pft", new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 5, CellHeight = 10 });
        host.SetFont("if_gr_10b.pft", new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 5, CellHeight = 10 });
        host.SetBitmap("CMD_DamageLow.bmp", Pic(5));
        host.SetBitmap("CMD_DamageMed.bmp", Pic(6));
        host.SetBitmap("CMD_DamageHigh.bmp", Pic(7));
        host.SetBitmap("CMD_ItemActive.bmp", Pic(3));
        host.SetBitmap("CMD_ItemInactive.bmp", Pic(4));
        r.Slot(132, 600, 600, 0, 19);
        r.Slot(133, 200, 200, 0, 8);
        r.Slot(134, 300, 700, 1, 4);
        r.World.DataBlocks.Blocks[(8, 0)] = new ClientDataBlock { Group = 8, Index = 0, Fields = { new DecodedField("description", "Sensor") } };
        return (r, items, turrets);
    }

    private static void Down(GuiControl list, int row, int clicks = 1, int dx = 60)
    {
        var (gx, gy) = list.GlobalOrigin();
        list.MsDown(new GuiEvent { X = gx + dx, Y = gy + row * ((GuiList)list).RowHeight + 2, ClickCount = clicks });
    }

    // ---- rows ----

    [Fact]
    public void TheObjectsListHoldsTheStaticsAndSensorsOfMyTeamInSlotOrder() // [004a98b8]
    {
        var (_, items, _) = ListRig();
        items.PreRender();
        Assert.Equal(new[] { (131, "Generator"), (133, "Sensor") }, items.Rows.Select(x => (x.Slot, x.Name)));
        Assert.Equal(new[] { "Generator", "Sensor" }, items.Entries.Select(e => e.Text));
    }

    [Fact]
    public void TheControllablesListHoldsTurretsAndVehiclesAndTakesTheSlotLabelFirst() // [004aa6b8]
    {
        var (r, _, turrets) = ListRig();
        r.World.DataBlocks.Blocks[(10, 0)] = new ClientDataBlock { Group = 10, Index = 0, Fields = { new DecodedField("description", "Scout") } };
        r.World.DataBlocks.Blocks[(11, 0)] = new ClientDataBlock { Group = 11, Index = 0 }; // no description: left out
        r.Slot(135, 10, 10, 0, 10);
        r.Slot(136, 20, 20, 0, 11);
        turrets.PreRender();
        Assert.Equal(new[] { (129, "Balcony Turret"), (132, "Plasma Turret"), (135, "Scout") }, turrets.Rows.Select(x => (x.Slot, x.Name)));
    }

    [Fact]
    public void ASlotWithoutADatablockOrAnotherTeamIsLeftOut()
    {
        var (r, items, _) = ListRig();
        r.World.Psc.CommandSlots[131].Present = false;
        items.PreRender();
        Assert.Equal(new[] { 133 }, items.Rows.Select(x => x.Slot));
        r.World.Players.Clients[2049].Team = 1;
        items.PreRender();
        Assert.Equal(new[] { 134 }, items.Rows.Select(x => x.Slot)); // team 1 slot 134 has no datablock (4, 0) description? it does: Generator
        Assert.Equal("Generator", items.Rows[0].Name);
    }

    // ---- clicks ----

    [Fact]
    public void ARowOfTheObjectsListSelectsThatStaticAndDropsPlayersAndTurrets() // [004a973c, 004d28a8]
    {
        var (r, items, _) = ListRig();
        r.Map.Group = 3;
        r.Click(2);
        r.Map.Group = 4;
        items.PreRender();
        Down(items, 1);
        Assert.Equal(new[] { 133 }, r.Picked());
        Assert.False(r.World.Players.Clients[2050].Selected);
        Down(items, 0);
        Assert.Equal(new[] { 131 }, r.Picked());
        Assert.Equal(131 - 128, r.Map.SelectedStaticTarget());
        Assert.Equal(0, items.Selected);
    }

    [Fact]
    public void ARowOfTheControllablesListSelectsThatTurretAndDropsStatics() // [004aa644, 004d27d0]
    {
        var (r, items, turrets) = ListRig();
        r.Map.Group = 4;
        items.PreRender();
        Down(items, 0);
        r.Map.Group = 0x13;
        turrets.PreRender();
        Down(turrets, 1);
        Assert.Equal(new[] { 132 }, r.Picked());
        Assert.Equal(132 - 128, r.Map.SelectedTurretTarget());
    }

    [Fact]
    public void AListSelectionNeedsTheMatchingTab()
    {
        // the map selects only the kind in view [004d28a8: the kind byte +0x28f must be 4]
        var (r, items, _) = ListRig();
        r.Map.Group = 3;
        items.PreRender();
        Down(items, 0);
        Assert.Empty(r.Picked());
    }

    [Fact]
    public void ADoubleClickOnTheSameControllableMountsIt() // [004aa4b4, 004d2798]
    {
        var (r, _, turrets) = ListRig();
        r.Map.Group = 0x13;
        turrets.PreRender();
        Down(turrets, 0);
        Assert.DoesNotContain(r.Remote, x => x.Contains("CmdrMountObject"));
        Down(turrets, 0, clicks: 2);
        Assert.Equal("2048|CmdrMountObject|1", r.Remote[^1]);
        // a double click that lands on another row than the one before only selects
        r.Remote.Clear();
        Down(turrets, 1, clicks: 2);
        Assert.Empty(r.Remote);
        Assert.Equal(new[] { 132 }, r.Picked());
    }

    [Fact]
    public void TheObjectsListNeverMounts()
    {
        var (r, items, _) = ListRig();
        r.Map.Group = 4;
        items.PreRender();
        Down(items, 0);
        Down(items, 0, clicks: 2);
        Assert.Empty(r.Remote);
    }

    // ---- the order ----

    [Fact]
    public void ARowClickWhileTheOrderWaitsGivesTheOrderToThatSlot() // [004a97b0, 004aa4b4, 004d25ec]
    {
        var (r, _, turrets) = ListRig();
        r.Click(2);
        r.Map.SetIssueCommand(3, "Repair");
        Assert.Equal(1, r.Map.OrderMode);
        r.Map.Group = 0x13;
        turrets.PreRender();
        Down(turrets, 1);
        Assert.Equal("2048|IssueTargCommand|3|Repair|132|2050", r.Remote[^1]);
        Assert.Equal("Plasma Turret", r.Fx.Interp.PeekVariable("$CommandTargetName"));
        Assert.NotEqual("friendly player", r.Fx.Interp.PeekVariable("$CommandTarget")); // only the team list sets that
        Assert.Equal(0, r.Map.OrderMode);
        Assert.DoesNotContain(r.Picked(), i => i >= 128);
    }

    [Fact]
    public void AnObjectRowGivesTheOrderToo()
    {
        var (r, items, _) = ListRig();
        r.Click(2);
        r.Map.SetIssueCommand(3, "Repair");
        r.Map.Group = 4;
        items.PreRender();
        Down(items, 0);
        Assert.Equal("2048|IssueTargCommand|3|Repair|131|2050", r.Remote[^1]);
        Assert.Equal("Generator", r.Fx.Interp.PeekVariable("$CommandTargetName"));
    }

    [Fact]
    public void TheOrderCursorShowsTheWaypointArrowOverTheLists() // [004a9714, 004aa48c]
    {
        var (r, items, turrets) = ListRig();
        Assert.NotEqual(160558u, items.CursorTagAt(1, 1));
        r.Map.SetOrderMode(1);
        Assert.Equal(160558u, items.CursorTagAt(1, 1));
        Assert.Equal(160558u, turrets.CursorTagAt(1, 1));
    }

    // ---- draw ----

    [Fact]
    public void ARowDrawsThePicturesAndTheNameAndASelectedOneIsFilled() // [004a9a08]
    {
        var (r, items, _) = ListRig();
        r.World.Psc.CommandSlots[131].Status = 2;
        r.World.Psc.CommandSlots[131].Active = false;
        r.World.Psc.CommandSlots[133].Status = 1;
        r.Map.Group = 4;
        items.PreRender();
        Down(items, 0);
        var dev = new CommandOrdersTests.RectDev();
        r.Fx.Canvas.Render(dev);
        // row 0 at (450, 100): rows are 12 high (font 10 + 2)
        Assert.Contains("fill 463,100,1089,111 cfc", dev.Calls); // 450 + high 7 + active 3 + 3, to the cell's right edge
        Assert.Contains("blit w7 452,101", dev.Calls); // high damage at (x + 2, y + 1)
        Assert.Contains("blit w4 460,101", dev.Calls); // inactive after it: 452 + 7 + 1
        Assert.Contains("text 466,98 'Generator'", dev.Calls); // 460 + 4 + 2, two above the row
        Assert.Contains("blit w6 452,113", dev.Calls); // row 1: medium damage
        Assert.Contains("blit w3 459,113", dev.Calls); // active
        Assert.DoesNotContain(dev.Calls, c => c.StartsWith("fill ") && c.Contains(",112,") && c.EndsWith("cfc"));
    }

    [Fact]
    public void AControllableThatCannotBeTakenIsGreyedByItsFont() // [004aa814]
    {
        var (r, _, turrets) = ListRig();
        var white = new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 5, CellHeight = 10 };
        var grey = new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 7, CellHeight = 10 };
        r.Fx.Host.SetFont("if_w_10b.pft", white);
        r.Fx.Host.SetFont("if_gr_10b.pft", grey);
        r.World.Psc.CommandSlots[129].Controllable = true;
        r.Map.Group = 0x13;
        turrets.PreRender();
        var dev = new CommandOrdersTests.RectDev();
        r.Fx.Canvas.Render(dev);
        Assert.Contains(dev.Calls, c => c.Contains("'Balcony Turret'"));
        Assert.Contains(dev.Calls, c => c.Contains("'Plasma Turret'"));
    }

    // ---- the damage of the team list ----

    [Fact]
    public void TheTeamListTakesTheDamageFromThePlayersSlot() // [004d29fc, 004a8638]
    {
        var r = new Rig(withTeamList: true);
        r.World.Psc.CommandSlots[2].Status = 2;
        r.World.Psc.CommandSlots[3].Status = 1;
        r.Team!.PreRender();
        Assert.Equal(new[] { 0, 2, 1 }, r.Team.Rows.Select(x => x.Damage));
    }

    // ---- the observer ----

    private static string Say(Rig r, string expr)
    {
        r.Fx.Output.Clear();
        r.Fx.Interp.Evaluate("echo(" + expr + ");");
        return r.Fx.Output[^1].Trim();
    }

    private static (Rig Rig, GuiCmdObserve Observe) ObserveRig(bool view = true)
    {
        var r = new Rig();
        var obs = (GuiCmdObserve)GuiFixture.Make("FearGui::CMDObserve", 0, 0, 100, 100);
        obs.Visible = false;
        if (view)
        {
            var tv = GuiFixture.Make("SimGui::TSControl", 0, 0, 10, 10);
            tv.Id = GuiCmdObserve.ViewId;
            obs.AddChild(tv);
        }
        r.Root.AddChild(obs);
        return (r, obs);
    }

    [Fact]
    public void ObservingNeedsTheShownPanelAndSendsTheServerTheNewPlayer() // [004b4818, 0043a6e8]
    {
        var (r, obs) = ObserveRig();
        Assert.Equal("False", Say(r, "Client::cmdObservePlayer(2050)"));
        Assert.Empty(r.Remote);
        obs.Visible = true;
        Assert.Equal("True", Say(r, "Client::cmdObservePlayer(2050)"));
        Assert.Equal("2048|SCOM|2050", r.Remote[^1]);
        Assert.Equal(2050, obs.Observed);
        // the same player again, and a call with another argument count, do nothing
        Assert.Equal("False", Say(r, "Client::cmdObservePlayer(2050)"));
        Assert.Equal("False", Say(r, "Client::cmdObservePlayer()"));
        Assert.Single(r.Remote);
        // the start value is 0 [004b4718]
        var (r2, obs2) = ObserveRig();
        obs2.Visible = true;
        Assert.False(obs2.Observe(0));
    }

    [Fact]
    public void SelectingAPlayerOnTheMapPointsTheObserverAtIt() // [004d291c]
    {
        var (r, obs) = ObserveRig();
        obs.Visible = true;
        r.Map.SelectClient(2050, true);
        Assert.Equal("2048|SCOM|2050", r.Remote[^1]);
        r.Map.SelectClient(2051, true);
        Assert.Equal("2048|SCOM|2051", r.Remote[^1]);
        // a deselect sends nothing
        r.Remote.Clear();
        r.Map.SelectClient(2051, false);
        Assert.Empty(r.Remote);
    }

    [Fact]
    public void TheToggleFollowsTheFirstSelectedRecordAndStopsWithMinusOne() // [004b48bc, 0043a670]
    {
        var (r, obs) = ObserveRig();
        // nothing selected: the panel stays off
        Assert.Equal("True", Say(r, "Client::toggleCMDObserver(true)"));
        Assert.False(obs.Visible);
        Assert.Empty(r.Remote);
        r.World.Players.Clients[2051].Selected = true;
        r.World.Players.Clients[2050].Selected = true;
        r.Fx.Interp.Evaluate("Client::toggleCmdObserver(true);");
        Assert.True(obs.Visible);
        Assert.Equal("2048|SCOM|2050", r.Remote[^1]);
        Assert.Equal(2050, obs.Observed);
        // on again changes nothing
        r.Fx.Interp.Evaluate("Client::toggleCMDObserver(1);");
        Assert.Single(r.Remote);
        r.Fx.Interp.Evaluate("Client::toggleCMDObserver(false);");
        Assert.False(obs.Visible);
        Assert.Equal("2048|SCOM|-1", r.Remote[^1]);
        Assert.Equal("False", Say(r, "Client::toggleCMDObserver()"));
    }

    [Fact]
    public void WithoutTheViewTheToggleOnlyShowsThePanel() // [004b48bc]
    {
        var (r, obs) = ObserveRig(view: false);
        r.World.Players.Clients[2050].Selected = true;
        obs.Toggle(true);
        Assert.True(obs.Visible);
        Assert.Empty(r.Remote);
        Assert.Equal(0, obs.Observed);
    }
}
