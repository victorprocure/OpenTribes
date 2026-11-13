// Selecting players and giving orders on the command map (docs/gui.md 3.3 "The command map"): TSCommander [004d1bf0 left down, 004d2754 drag,
// 004d2b1c up, 004d2a98, 004d291c, 004d2404, 004d25ec, 004d2614, 004d23bc, 004d3018, 004d3220, 004d32c4], CommandTeamCtrl [004a8b00, 004a850c, 004a8a6c]
// and the chat menu it drives [00401fe0, 00402068]. Synthetic data only.
namespace OpenTribes.Tests;

using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;

public class CommandOrdersTests
{
    internal sealed class RectDev : IGfxDevice
    {
        public readonly List<string> Calls = [];
        public int Width => 640;
        public int Height => 480;
        public void Dispose() { }
        public void SetPalette(PaletteBank bank, float gamma = 1f) { }
        public void FlushTextureCache() { }
        public void FlushTexture(PbmpBitmap bitmap) { }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void Clear(int colorIndex) { }
        public void ClearRgb(byte r, byte g, byte b) { }
        public void SetClip(GfxRect? clip) { }
        public void DrawLine(int x0, int y0, int x1, int y1, int colorIndex) { }
        public void DrawRectOutline(GfxRect r, int colorIndex) => Calls.Add($"outline {r.Left},{r.Top},{r.Right},{r.Bottom} c{colorIndex:x}");
        public void FillRect(GfxRect r, int colorIndex, float alpha = 1f) => Calls.Add($"fill {r.Left},{r.Top},{r.Right},{r.Bottom} c{colorIndex:x}");
        public int DrawText(PftFont font, int x, int y, string text) { Calls.Add($"text {x},{y} '{text}'"); return y; }
        public void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None) => Calls.Add($"blit w{bitmap.Width} {x},{y}");
        public void BlitRegion(PbmpBitmap bitmap, GfxRect source, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitStretch(PbmpBitmap bitmap, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public void BlitRegionStretch(PbmpBitmap bitmap, GfxRect source, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public byte[] ReadPixels() => [];
        public IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options) => throw new NotSupportedException();
        public void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB) { }
        public void SetCamera3D(in Camera3D camera) { }
        public void Draw3D(ReadOnlySpan<Vertex3D> triangles, in DrawState3D state) { }
        public void EndScene3D() { }
    }

    private static ClientDataBlock Block(int group, int index, params (string, object)[] fields) =>
        new() { Group = group, Index = index, Fields = [.. fields.Select(f => new DecodedField(f.Item1, f.Item2))] };

    // The map is 408 x 408 at (31, 36) on the canvas. A slot at (x, y) in 1/1024 stands at (31 + x * 408 >> 10, 36 + 408 - (y * 408 >> 10)).
    internal sealed class Rig
    {
        public readonly GuiFixture Fx = new();
        public readonly ClientWorld World = new() { PlayerId = 2049 };
        public readonly GuiCommandMap Map;
        public readonly GuiControl Current;
        public readonly List<string> Remote = [], Menu = [];
        public bool MenuExists = true;

        public Rig(bool withTeamList = false)
        {
            // players: 2049 Rec (me, team 0), 2050 Dan and 2051 Eve (team 0), 2052 Foe (team 1); a team 0 turret (slot 129) and a team 1 one (slot 130)
            foreach (var (id, name, team) in new[] { (2049, "Rec", 0), (2050, "Dan", 0), (2051, "Eve", 0), (2052, "Foe", 1) })
                World.Players.Clients[id] = new ClientPlayerRecord { Id = id, Name = name, Team = team };
            World.DataBlocks.Blocks[(3, 0)] = Block(3, 0, ("mapFilter", 15L), ("mapIcon", "M_player"), ("description", "Armor"));
            World.DataBlocks.Blocks[(19, 0)] = Block(19, 0, ("mapFilter", 15L), ("mapIcon", "M_turret"), ("description", "Plasma Turret"), ("range", 100L));
            World.DataBlocks.Blocks[(4, 0)] = Block(4, 0, ("mapFilter", 15L), ("mapIcon", "M_stat"), ("description", "Generator"));
            Slot(1, 512, 512, 0, 3);
            Slot(2, 256, 768, 0, 3);
            Slot(3, 300, 300, 0, 3);
            Slot(4, 700, 700, 1, 3);
            Slot(129, 768, 256, 0, 19);
            Slot(130, 900, 100, 1, 19);
            Slot(131, 100, 100, 0, 4);
            World.Players.TargetNames[1] = "Balcony Turret";
            Fx.Host.Hud = new ClientHudSource(World);
            Fx.Host.ChatMenuExists = n => MenuExists && n == "CommandChatMenu";
            Fx.Host.SetChatMenuMode = (n, m) => Menu.Add($"{n} {m}");
            Fx.Interp.AddCommand("remoteEval", (_, a) => { Remote.Add(string.Join("|", a.Skip(1))); return "True"; });
            Fx.Interp.SetVariable("$pref::mapFilter", "15");
            var root = Root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
            var parent = GuiFixture.Make("SimGui::Control", 31, 0, 409, 480);
            Map = (GuiCommandMap)GuiFixture.Make("FearGui::TSCommander", 0, 36, 408, 408);
            Current = GuiFixture.Make("FearGui::FGCurrentCommand", 5, 5, 100, 20);
            root.AddChild(parent);
            parent.AddChild(Map);
            root.AddChild(Current);
            Current.Id = GuiCommandMap.CurrentCommandId;
            if (withTeamList)
            {
                var team = GuiFixture.Make("FearGui::CommandTeamCtrl", 450, 100, 640, 15, ("active", (byte)1));
                root.AddChild(team);
                Team = (GuiCommandTeam)team;
            }
            Map.Id = GuiCommandMap.MapId;
            Fx.Content(root);
        }

        public GuiCommandTeam? Team;
        public GuiControl Root = null!;

        public void Slot(int i, int x, int y, int team, int group) =>
            World.Psc.CommandSlots[i] = new ClientCommandSlot { Present = true, X = x, Y = y, Team = team, Group = group, Index = 0, Active = true };

        public (int X, int Y) At(int slot) => (31 + ((World.Psc.CommandSlots[slot].X * 408) >> 10), 36 + 408 - ((World.Psc.CommandSlots[slot].Y * 408) >> 10));

        public void Down(int x, int y, byte mod = 0) => Map.MsDown(new GuiEvent { X = x, Y = y, Mod = mod });
        public void Drag(int x, int y) => Map.MsDrag(new GuiEvent { X = x, Y = y });
        public void Up(int x, int y) => Map.MsUp(new GuiEvent { X = x, Y = y });

        public void Click(int slot, byte mod = 0)
        {
            var (x, y) = At(slot);
            Down(x, y, mod);
            Up(x, y);
        }

        public int[] Picked() => [.. Enumerable.Range(0, 256).Where(i => Map.Selected[i])];
    }

    // ---- picking ----

    [Fact]
    public void AClickWithin3PixelsSelectsThePlayerAndTurnsTheMenuOn() // [004d1bf0, 004d2a98]
    {
        var r = new Rig();
        var (x, y) = r.At(2);
        r.Down(x + 3, y - 3);
        Assert.Equal([2], r.Picked());
        Assert.True(r.World.Players.Clients[2050].Selected);
        Assert.Equal("CommandChatMenu 1", r.Menu[^1]);
        r.Up(x + 3, y - 3);
        Assert.Equal([2], r.Picked());
    }

    [Fact]
    public void AClickFourPixelsAwayPicksNothing()
    {
        var r = new Rig();
        var (x, y) = r.At(2);
        r.Down(x + 4, y);
        Assert.Empty(r.Picked());
    }

    [Fact]
    public void APlainClickReplacesTheSelectionAndShiftAddsOrFlips() // [004d1bf0]
    {
        var r = new Rig();
        r.Click(2);
        r.Click(3);
        Assert.Equal([3], r.Picked());
        Assert.False(r.World.Players.Clients[2050].Selected);
        r.Click(2, mod: 1);
        Assert.Equal([2, 3], r.Picked());
        r.Menu.Clear();
        r.Click(2, mod: 1);
        Assert.Equal([3], r.Picked());
        // deselecting switches the command menu off [004d2a98]
        Assert.Contains("CommandChatMenu 0", r.Menu);
    }

    [Fact]
    public void OtherTeamsAndSlotsOfOtherKindsAreNotPicked()
    {
        var r = new Rig();
        r.Click(4); // team 1 player
        r.Click(129); // a turret while the map shows players
        Assert.Empty(r.Picked());
    }

    [Fact]
    public void TheMapFilterHidesSlotsFromPicking() // the datablock's mapFilter against $pref::mapFilter
    {
        var r = new Rig();
        r.Fx.Interp.SetVariable("$pref::mapFilter", "16");
        r.Click(2);
        Assert.Empty(r.Picked());
    }

    [Fact]
    public void TurretsAreSingleSelectAndTheTabSwitchesTheKind() // [004d1bf0 local_64, local_63]
    {
        var r = new Rig();
        r.Slot(132, 600, 600, 0, 19);
        r.Map.Group = 0x13;
        r.Click(129);
        Assert.Equal([129], r.Picked());
        r.Click(132, mod: 1); // shift: one turret is held, no second
        Assert.Equal([129], r.Picked());
        r.Click(132);
        Assert.Equal([132], r.Picked());
        Assert.Equal(132 - 128, r.Map.SelectedTurretTarget());
        // the kind in view changes what a click can reach
        r.Map.Group = 3;
        r.Click(2);
        Assert.Equal([2], r.Picked()); // the turret is dropped when a player is picked
    }

    [Fact]
    public void StaticsListAlsoPicksSensors()
    {
        var r = new Rig();
        r.World.DataBlocks.Blocks[(8, 0)] = Block(8, 0, ("mapFilter", 15L), ("mapIcon", "M_sensor"));
        r.Slot(133, 800, 800, 0, 8);
        r.Map.Group = 4;
        r.Click(133);
        Assert.Equal([133], r.Picked());
        Assert.Equal(133 - 128, r.Map.SelectedStaticTarget());
    }

    // ---- the drag rectangle ----

    [Fact]
    public void ADragRectangleSelectsThePlayersInsideAndAPlainDragClearsTheOldSelection() // [004d2b1c]
    {
        var r = new Rig();
        r.Click(1); // old selection: me
        var (x2, y2) = r.At(2);
        var (x3, y3) = r.At(3);
        // an empty spot, then a drag across slots 2 and 3 (slot 4 is the other team and outside)
        r.Down(x2 - 20, y2 - 20);
        r.Drag(x3 + 20, y3 + 20);
        r.Up(x3 + 20, y3 + 20);
        Assert.Equal([2, 3], r.Picked());
        Assert.False(r.World.Players.Clients[2049].Selected);
    }

    [Fact]
    public void AShiftDragKeepsTheOldSelection()
    {
        var r = new Rig();
        r.Click(1);
        var (x2, y2) = r.At(2);
        r.Down(x2 - 20, y2 - 20, mod: 1);
        r.Drag(x2 + 20, y2 + 20);
        r.Up(x2 + 20, y2 + 20);
        Assert.Equal([1, 2], r.Picked());
    }

    [Fact]
    public void TheRectangleEdgesAreLeftIncludedRightExcluded()
    {
        var r = new Rig();
        var (x2, y2) = r.At(2);
        r.Down(x2, y2 - 30);
        r.Drag(x2 + 30, y2 + 30);
        r.Up(x2 + 30, y2 + 30); // the slot sits on the left edge
        Assert.Equal([2], r.Picked());
        var s = new Rig();
        s.Down(x2 - 30, y2 - 30);
        s.Drag(x2, y2 + 30);
        s.Up(x2, y2 + 30); // on the right edge
        Assert.Empty(s.Picked());
    }

    [Fact]
    public void TheDragRectangleAndTheSelectionOutlinesAreDrawn() // [004d04e8, 004cf94c]
    {
        var r = new Rig();
        r.Fx.Host.SetFont("if_small.pft", new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 5, CellHeight = 8 });
        r.Fx.Host.Hud = new ClientHudSource(r.World) { CommandMapSource = () => new CommandMapPicture(-827, -717, 1000, 1000, Client.World.CommandMapBake.Tiles(new byte[Client.World.CommandMapBake.Size * Client.World.CommandMapBake.Size])) };
        r.Click(2);
        var (x2, y2) = r.At(2);
        r.Down(x2 - 50, y2 + 10);
        r.Drag(x2 - 10, y2 + 40);
        var dev = new RectDev();
        r.Fx.Canvas.Render(dev);
        Assert.Contains($"outline {x2 - 6},{y2 - 6},{x2 + 6},{y2 + 6} cff", dev.Calls); // 6 pixels around the selected player
        Assert.Contains($"outline {x2 - 50},{y2 + 10},{x2 - 10},{y2 + 40} cff", dev.Calls); // the drag rectangle
        // the local client is never outlined; an unselected one neither
        var (x1, y1) = r.At(1);
        Assert.DoesNotContain($"outline {x1 - 6},{y1 - 6},{x1 + 6},{y1 + 6} cff", dev.Calls);
    }

    [Fact]
    public void ASelectedObjectGetsALabelAndOutline()
    {
        var r = new Rig();
        r.Fx.Host.SetFont("if_small.pft", new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 5, CellHeight = 8 });
        r.Fx.Host.Hud = new ClientHudSource(r.World) { CommandMapSource = () => new CommandMapPicture(-827, -717, 1000, 1000, Client.World.CommandMapBake.Tiles(new byte[Client.World.CommandMapBake.Size * Client.World.CommandMapBake.Size])) };
        r.Map.Group = 0x13;
        r.Click(129);
        var (x, y) = r.At(129);
        var dev = new RectDev();
        r.Fx.Canvas.Render(dev);
        Assert.Contains($"outline {x - 6},{y - 6},{x + 6},{y + 6} cff", dev.Calls);
        Assert.Contains($"text {x + 4},{y - 4} 'Balcony Turret'", dev.Calls);
    }

    // ---- the order ----

    [Fact]
    public void SetIssueCommandNeedsSelectedPlayersThenWaitsForAClick() // [004d2614, 004d23bc]
    {
        var r = new Rig();
        r.Map.SetIssueCommand(1, "Attack enemy forces~wattway");
        Assert.Equal(0, r.Map.OrderMode); // nobody selected
        r.Click(2);
        r.Map.SetIssueCommand(1, "Attack enemy forces~wattway");
        Assert.Equal(1, r.Map.OrderMode);
        // the current command line shows the text up to the voice tag
        Assert.Equal("Attack enemy forces", ((GuiSimpleText)r.Current).Text);
        r.Map.SetIssueCommand(-1, "");
        Assert.Equal(0, r.Map.OrderMode);
    }

    [Fact]
    public void AWaypointClickSendsIssueCommandToTheSelectedPlayers() // [004d1bf0 order branch, 004d2404]
    {
        var r = new Rig();
        r.Click(2);
        r.Click(3, mod: 1);
        r.Map.SetIssueCommand(1, "Attack enemy forces~wattway");
        // an empty place: local (200, 100) of the 408 pixel map
        r.Down(31 + 200, 36 + 100);
        // x = 200 * 1023 / 408, y = (408 - 100) * 1023 / 408
        Assert.Equal(new[] { $"2048|IssueCommand|1|Attack enemy forces~wattway|{200 * 1023 / 408}|{(408 - 100) * 1023 / 408}|2050|2051" }, r.Remote);
        Assert.Equal("waypoint", r.Fx.Interp.PeekVariable("$CommandTarget"));
        // the order stays until the button comes up
        Assert.Equal(1, r.Map.OrderMode);
        r.Up(31 + 200, 36 + 100);
        Assert.Equal(0, r.Map.OrderMode);
    }

    [Fact]
    public void AClickOnAnEnemyPlayerSendsIssueTargCommandWithItsSlot() // [004d25ec]
    {
        var r = new Rig();
        r.Click(2);
        r.Map.SetIssueCommand(1, "Attack");
        var (x, y) = r.At(4);
        r.Down(x + 2, y + 1);
        Assert.Equal(new[] { "2048|IssueTargCommand|1|Attack|4|2050" }, r.Remote);
        Assert.Equal("enemy player", r.Fx.Interp.PeekVariable("$CommandTarget"));
        Assert.Equal("Foe", r.Fx.Interp.PeekVariable("$CommandTargetName"));
    }

    [Fact]
    public void AClickOnAFriendlyTurretNamesItAndAFriendlyPlayerIsCovered()
    {
        var r = new Rig();
        r.Click(2);
        r.Map.SetIssueCommand(2, "Defend");
        var (x, y) = r.At(129);
        r.Down(x, y);
        Assert.Equal("2048|IssueTargCommand|2|Defend|129|2050", r.Remote[^1]);
        Assert.Equal("friendly turret", r.Fx.Interp.PeekVariable("$CommandTarget"));
        Assert.Equal("Balcony Turret", r.Fx.Interp.PeekVariable("$CommandTargetName"));
    }

    [Fact]
    public void ASlotOfAnotherKindIsAWaypointAtItsPlace() // the default case of the target switch
    {
        var r = new Rig();
        r.World.DataBlocks.Blocks[(10, 0)] = Block(10, 0, ("mapFilter", 15L));
        r.Slot(134, 400, 500, 0, 11);
        r.Click(2);
        r.Map.SetIssueCommand(1, "Go");
        var (x, y) = r.At(134);
        r.Down(x, y);
        Assert.Equal("2048|IssueCommand|1|Go|400|500|2050", r.Remote[^1]);
        Assert.Equal("waypoint", r.Fx.Interp.PeekVariable("$CommandTarget"));
    }

    [Fact]
    public void TheNearestSlotUnder4PixelsWinsAnyKind()
    {
        var r = new Rig();
        r.Click(2);
        r.Map.SetIssueCommand(1, "Go");
        var (x, y) = r.At(131); // a team 0 static, not the shown kind
        r.Down(x + 3, y);
        Assert.Equal("2048|IssueTargCommand|1|Go|131|2050", r.Remote[^1]);
        Assert.Equal("friendly static", r.Fx.Interp.PeekVariable("$CommandTarget"));
    }

    [Fact]
    public void AStarTextIsResolvedByTheScriptThenTheChoiceIsRestored() // [004d2404 with Commander::StarCommand]
    {
        var r = new Rig();
        r.Fx.Interp.AddCommand("setIssueCommand", (_, a) => { r.Map.SetIssueCommand(int.Parse(a[1]), a[2]); return "True"; });
        r.Fx.Interp.Evaluate("function Commander::StarCommand(%type) { setIssueCommand(1, \"Attack enemy \" @ $CommandTargetName @ \"~wattway\"); }");
        r.Click(2);
        r.Map.SetIssueCommand(1, "*Attack");
        Assert.Equal(1, r.Map.OrderMode);
        var (x, y) = r.At(4);
        r.Down(x, y);
        Assert.Equal("2048|IssueTargCommand|1|Attack enemy Foe~wattway|4|2050", r.Remote[^1]);
        Assert.Equal("*Attack", r.Map.OrderText);
    }

    [Fact]
    public void TurningTheCommandMenuOffEndsTheOrderCursor() // [00401fe0]
    {
        var r = new Rig();
        r.Click(2);
        r.Map.SetIssueCommand(1, "Go");
        r.Map.SetOrderMode(0);
        Assert.Equal(0, r.Map.OrderMode);
        // deselecting the last player switches the menu off through setCMMode and the host resets the cursor; here the hook is the host's
        r.Fx.Host.SetChatMenuMode = (_, mode) => { if (mode == 0) r.Map.SetOrderMode(0); };
        r.Map.SetIssueCommand(1, "Go");
        Assert.Equal(1, r.Map.OrderMode);
        r.Click(2, mod: 1); // flips it off
        Assert.Equal(0, r.Map.OrderMode);
    }

    [Fact]
    public void EscapeInTheCommandMenuCancelsTheOrderAndIsTakenAtTheTopLevel() // [00402068]
    {
        var tree = new ChatMenuTree { Mode = 1 };
        tree.Add("aAttack", new[] { "setIssueCommand", "1", "*Attack" });
        Assert.False(tree.OnKey(1, 1, '\0', 0, _ => { }, () => false));
        Assert.True(tree.OnKey(1, 1, '\0', 0, _ => { }, () => true));
        // without the order cursor the item still runs
        var ran = new List<string>();
        Assert.True(tree.OnKey(0x1e, 1, 'a', 0, ran.AddRange));
        Assert.Equal(new[] { "setIssueCommand", "1", "*Attack" }, ran);
    }

    [Fact]
    public void ModeCursorsFollowTheOrderAndZoomState() // [004d1b54]
    {
        var r = new Rig();
        Assert.Equal(0u, r.Map.CursorTagAt(10, 10));
        r.Click(2);
        r.Map.SetIssueCommand(1, "Go");
        Assert.Equal(160556u, r.Map.CursorTagAt(10, 10)); // CUR_WayPoint
        r.Map.SetOrderMode(0);
        r.Map.ZoomMode = true;
        Assert.Equal(160557u, r.Map.CursorTagAt(10, 10)); // CUR_Zoom
    }

    // ---- centring on the selection ----

    [Fact]
    public void CentreModeMovesTheMapToTheSelectedSlots() // [004d3018, 004d3220, 004d32c4]
    {
        var r = new Rig();
        r.Click(2);
        r.Map.CenterMode = true;
        r.Fx.Clock += 1000; // past $pref::CommanderZoomTime
        r.Map.PreRender();
        // parent 409 x 480, map 408: x = 409/2 - (408 * 256 >> 10), y = 480/2 - (408 - (408 * 768 >> 10))
        Assert.Equal((204 - 102, 240 - (408 - 306)), (r.Map.X, r.Map.Y));
        // a click anywhere ends the centring
        r.Down(31, 36);
        Assert.False(r.Map.CenterMode);
    }

    // ---- the team list ----

    private static Rig TeamRig()
    {
        var r = new Rig(withTeamList: true);
        r.World.Players.Clients[2050].Commander = 2049;
        r.World.Players.Clients[2051].Commander = 2050;
        r.Fx.Host.SetArray("CmdListCtrl.pba", Pieces());
        return r;
    }

    private static PbmaArray Pieces()
    {
        var a = new PbmaArray { BitmapCount = 12 };
        for (var i = 0; i < 12; ++i)
            a.Bitmaps.Add(new PbmpBitmap { Width = 8, Height = 8 });
        return a;
    }

    private static void ListDown(Rig r, int row, byte mod = 0, int clicks = 1, int dx = 60)
    {
        var team = r.Team!;
        team.PreRender();
        var (gx, gy) = team.GlobalOrigin();
        team.MsDown(new GuiEvent { X = gx + dx, Y = gy + (row * team.RowHeight) + 2, Mod = mod, ClickCount = clicks });
    }

    [Fact]
    public void ListRowsSelectThePlayerOnTheMap() // [004a8b00]
    {
        var r = TeamRig();
        r.Team!.PreRender();
        Assert.Equal(new[] { 2049, 2050, 2051 }, r.Team.Rows.Select(x => x.Id));
        ListDown(r, 1);
        Assert.Equal([2], r.Picked());
        Assert.True(r.World.Players.Clients[2050].Selected);
        ListDown(r, 2);
        Assert.Equal([3], r.Picked());
        // ctrl flips
        ListDown(r, 1, mod: 0x04);
        Assert.Equal([2, 3], r.Picked());
        ListDown(r, 1, mod: 0x04);
        Assert.Equal([3], r.Picked());
    }

    [Fact]
    public void AShiftClickSelectsTheRowsFromTheLastPlainClick()
    {
        var r = TeamRig();
        ListDown(r, 0);
        ListDown(r, 2, mod: 1);
        Assert.Equal([1, 2, 3], r.Picked());
    }

    [Fact]
    public void ADoubleClickSelectsTheNodeAndItsFirstSubordinatesDown() // [004a850c]
    {
        var r = TeamRig();
        ListDown(r, 0);
        ListDown(r, 0, clicks: 2);
        // Rec (1), then the first record under Rec (Dan, 2), then the first under Dan (Eve, 3)
        Assert.Equal([1, 2, 3], r.Picked());
    }

    [Fact]
    public void TheTreeGlyphOpensAndClosesANode()
    {
        var r = TeamRig();
        r.Team!.PreRender();
        var before = r.Team.Rows.Count;
        // the glyph of a row at depth 0 starts at 4 + the two picture widths (none loaded) and is step (8 + 2) wide
        ListDown(r, 0, dx: 6);
        Assert.False(r.World.Players.Clients[2049].Expanded);
        r.Team.PreRender();
        Assert.Single(r.Team.Rows);
        ListDown(r, 0, dx: 6);
        r.Team.PreRender();
        Assert.Equal(before, r.Team.Rows.Count);
    }

    [Fact]
    public void ARowClickWhileTheOrderWaitsGivesTheOrderToThatPlayer() // [004a8b00 order branch]
    {
        var r = TeamRig();
        r.Click(2);
        r.Map.SetIssueCommand(3, "Repair");
        ListDown(r, 2);
        Assert.Equal("2048|IssueTargCommand|3|Repair|3|2050", r.Remote[^1]);
        Assert.Equal("friendly player", r.Fx.Interp.PeekVariable("$CommandTarget"));
        Assert.Equal("Eve", r.Fx.Interp.PeekVariable("$CommandTargetName"));
        Assert.Equal(0, r.Map.OrderMode);
        Assert.Equal(160558u, new Func<uint>(() => { r.Map.SetOrderMode(1); return r.Team!.CursorTagAt(1, 1); })());
    }

    [Fact]
    public void ReportPicksTheFirstSelectedPlayerNotAlreadyAboveMe() // [004a8a6c, 0040cad4]
    {
        var r = TeamRig();
        r.World.Players.Clients[2050].Commander = -1;
        r.World.Players.Clients[2051].Commander = 2049; // Eve is under me
        r.Map.SelectClient(2049 - 0, true); // myself is skipped
        r.Map.SelectClient(2051, true); // Eve has me above her
        Assert.Equal(-1, r.Team!.FirstReportable());
        r.Map.SelectClient(2050, true);
        Assert.Equal(2050, r.Team.FirstReportable());
    }
}

// The client wiring: the console function, the command chat menu and the buttons of the command screen [00421e14, 00401fe0, 00402068, 004dd280].
public sealed class CommandOrdersClientTests : IDisposable
{
    private readonly string mRoot = Path.Combine(Path.GetTempPath(), "ot-cmdord-" + Guid.NewGuid().ToString("N"));
    private Client.App.ClientApp? mApp;

    public CommandOrdersClientTests()
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

    private Client.App.ClientApp Boot()
    {
        File.WriteAllText(Path.Combine(mRoot, "console.cs"),
            "newClient(); focusClient();\n" +
            "newObject(MainWindow, SimGui::Canvas, \"Tribes\", 640, 480, True, \"512 384\", \"1024 768\");\n" +
            "function Game::EndFrame() {}\n$ConsoleWorld::DefaultSearchPath = \"config;base;temp\";\n" +
            "newObject(CommandChatMenu, ChatMenu, \"Command Menu\");\n" +
            "addCMCommand(CommandChatMenu, \"aAttack\", setIssueCommand, 1, \"Attack enemy forces~wattway\");\n");
        var opt = Client.App.ClientOptions.Parse(["--root", mRoot, "--headless"], out var err)!;
        Assert.Equal("", err);
        mApp = new Client.App.ClientApp(opt);
        Assert.True(mApp.Start(out var error), error);
        return mApp;
    }

    private static (GuiCommandMap Map, ClientWorld World, List<string> Remote) Screen(Client.App.ClientApp app)
    {
        var world = new ClientWorld { PlayerId = 2049 };
        world.Players.Clients[2049] = new ClientPlayerRecord { Id = 2049, Name = "Rec", Team = 0 };
        world.Players.Clients[2050] = new ClientPlayerRecord { Id = 2050, Name = "Dan", Team = 0 };
        world.DataBlocks.Blocks[(3, 0)] = Block(3, 0, ("mapFilter", 15L), ("mapIcon", "M_player"));
        world.Psc.CommandSlots[2] = new ClientCommandSlot { Present = true, X = 256, Y = 768, Team = 0, Group = 3, Index = 0 };
        app.Host.Hud = new ClientHudSource(world);
        app.Interp.SetVariable("$pref::mapFilter", "15");
        var remote = new List<string>();
        app.Interp.AddCommand("remoteEval", (_, a) => { remote.Add(string.Join("|", a.Skip(1))); return "True"; });
        var root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        var map = (GuiCommandMap)GuiFixture.Make("FearGui::TSCommander", 0, 36, 408, 408);
        map.Id = GuiCommandMap.MapId;
        var current = GuiFixture.Make("FearGui::FGCurrentCommand", 5, 5, 100, 20);
        current.Id = GuiCommandMap.CurrentCommandId;
        root.AddChild(map);
        root.AddChild(current);
        app.Canvas.SetContent(root);
        return (map, world, remote);
    }

    [Fact]
    public void SetIssueCommandIsFalseWithoutAMapAndTrueWithOne()
    {
        var app = Boot();
        Assert.Equal("False", app.Interp.Evaluate("setIssueCommand(1, \"Go\");"));
        var (map, _, _) = Screen(app);
        Assert.Equal("True", app.Interp.Evaluate("setIssueCommand(1, \"Go\");"));
        Assert.Equal(0, map.OrderMode); // nobody is selected
        Assert.Equal("False", app.Interp.Evaluate("setIssueCommand(1);")); // argument count
    }

    [Fact]
    public void SelectingAPlayerShowsTheCommandMenuAndItsKeyStartsTheOrderUntilEscape()
    {
        var app = Boot();
        var (map, world, _) = Screen(app);
        var game = (GameState)typeof(Client.App.ClientApp).GetField("mGameState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(app)!;
        map.SelectSlot(2, true);
        Assert.True(world.Players.Clients[2050].Selected);
        Assert.NotNull(game.ChatMenus.Showing(game, "CommandChatMenu"));
        // the item key runs `setIssueCommand 1 text`: the order cursor is on
        Assert.True(game.ChatMenus.OnKey(game, 0x1e, 1, 'a', 0, w => app.Interp.CallFunction(w[0], w.Skip(1).ToArray())));
        Assert.Equal(1, map.OrderMode);
        Assert.Equal("Attack enemy forces~wattway", map.OrderText);
        // Escape ends it and is taken even at the top level of the menu
        Assert.True(game.ChatMenus.OnKey(game, 1, 1, '\0', 0, _ => { }));
        Assert.Equal(0, map.OrderMode);
        // a second Escape has nothing to cancel
        game.ChatMenus.OnKey(game, 1, 2, '\0', 0, _ => { });
        Assert.False(game.ChatMenus.OnKey(game, 1, 1, '\0', 0, _ => { }));
        // deselecting the player switches the menu off
        map.SelectSlot(2, false);
        Assert.Null(game.ChatMenus.Showing(game, "CommandChatMenu"));
    }

    [Fact]
    public void SwitchingTheCommandMenuOffThroughTheConsoleEndsTheOrder() // 00401fe0
    {
        var app = Boot();
        var (map, _, _) = Screen(app);
        map.SelectSlot(2, true);
        map.SetIssueCommand(1, "Go");
        Assert.Equal(1, map.OrderMode);
        app.Interp.Evaluate("setCMMode(CommandChatMenu, 0);");
        Assert.Equal(0, map.OrderMode);
    }

    [Fact]
    public void TheCommandScreenMessagesReachTheMapAndTheServer() // 004dd280
    {
        var app = Boot();
        var (map, _, remote) = Screen(app);
        Assert.True(app.Play.Message(null, 140116));
        Assert.Equal(0x13, map.Group);
        Assert.True(app.Play.Message(null, 140117));
        Assert.Equal(4, map.Group);
        Assert.True(app.Play.Message(null, 140115));
        Assert.Equal(3, map.Group);
        var zoom = (GuiUniversalButton)GuiFixture.Make("FearGui::FGUniversalButton", 0, 0, 10, 10);
        zoom.On = true;
        Assert.True(app.Play.Message(zoom, 140211));
        Assert.True(map.ZoomMode);
        zoom.On = false;
        app.Play.Message(zoom, 140211);
        Assert.False(map.ZoomMode);
        // unreport: remoteEval(2048, SelectCommander, -1)
        Assert.True(app.Play.Message(null, 140214));
        Assert.Equal("2048|SelectCommander|-1", remote[^1]);
        // take control mounts the selected turret
        map.Group = 0x13;
        map.Selected[130] = true;
        app.Host.Hud!.CommandSlotTable![130] = new ClientCommandSlot { Present = true, Group = 0x13 };
        Assert.True(app.Play.Message(null, 160243));
        Assert.Equal("2048|CmdrMountObject|2", remote[^1]);
    }

    private static ClientDataBlock Block(int group, int index, params (string, object)[] fields) =>
        new() { Group = group, Index = index, Fields = [.. fields.Select(f => new DecodedField(f.Item1, f.Item2))] };
}

