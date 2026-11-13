// The command screen panels (docs/gui.md 3.6): FGCommandPanel [004a159c], FGChatMenu [004a7934], CommandTeamCtrl [004a8638]. Synthetic data only.
namespace OpenTribes.Tests;

using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;

public class GuiCommandPanelTests
{
    private sealed class CallDev : IGfxDevice
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
        public void DrawLine(int x0, int y0, int x1, int y1, int colorIndex) => Calls.Add($"line {x0},{y0},{x1},{y1} c{colorIndex:x}");
        public void DrawRectOutline(GfxRect rect, int colorIndex) { }
        public void FillRect(GfxRect rect, int colorIndex, float alpha = 1f) => Calls.Add($"fill {rect.Left},{rect.Top},{rect.Right},{rect.Bottom} c{colorIndex:x}");
        public int DrawText(PftFont font, int x, int y, string text) { Calls.Add($"text w{font.GlyphCellWidth} {x},{y} '{text}'"); return y; }
        public void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None) => Calls.Add($"blit {bitmap.Width}x{bitmap.Height} {x},{y}");
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

    private static GuiControl Root(GuiControl child)
    {
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        root.AddChild(child);
        return root;
    }

    private static PftFont Font(int cell, int h = 10) => new() { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = cell, CellHeight = h };

    [Fact]
    public void ClassesMapToTheirControls()
    {
        Assert.IsType<GuiCommandPanel>(GuiFixture.Make("FearGui::FGCommandPanel"));
        Assert.IsType<GuiChatMenu>(GuiFixture.Make("FearGui::FGChatMenu"));
        Assert.IsType<GuiCommandTeam>(GuiFixture.Make("FearGui::CommandTeamCtrl"));
    }

    [Fact]
    public void PanelFillsF7AndCutsTheBottomLeftCorner()
    {
        var fx = new GuiFixture();
        fx.Content(Root(GuiFixture.Make("FearGui::FGCommandPanel", 10, 20, 30, 40)));
        var dev = new CallDev();
        fx.Canvas.Render(dev);
        // The top row is whole, the bottom row starts 9 pixels in, the outline is f8.
        Assert.Contains("fill 10,20,39,20 cf7", dev.Calls);
        Assert.Contains("fill 19,59,39,59 cf7", dev.Calls);
        Assert.Contains("line 10,20,39,20 cf8", dev.Calls);
        Assert.Contains("line 19,59,10,50 cf8", dev.Calls);
    }

    [Fact]
    public void ChatMenuShowsInactiveAndTheItems()
    {
        var fx = new GuiFixture();
        fx.Host.SetFont("sf_Orange255_10.pft", Font(6));
        fx.Host.SetFont("sf_white_10.pft", Font(5));
        ChatMenuState? state = new(false, "", Array.Empty<string>());
        fx.Host.Hud = new ClientHudSource(new ClientWorld()) { NamedMenuSource = n => n == "CommandChatMenu" ? state : null };
        fx.Content(Root(GuiFixture.Make("FearGui::FGChatMenu", 7, 9, 100, 60, ("menuName", "CommandChatMenu"))));
        var dev = new CallDev();
        fx.Canvas.Render(dev);
        Assert.Equal(new[] { "text w6 10,10 'Menu inactive'" }, dev.Calls);

        state = new ChatMenuState(true, "", new[] { "a: Attack", "d: Defend" });
        dev = new CallDev();
        fx.Canvas.Render(dev);
        Assert.Equal(new[] { "text w6 10,10 'Root Menu:'", "text w6 10,20 'a: Attack'", "text w6 10,30 'd: Defend'" }, dev.Calls);

        state = new ChatMenuState(true, "Orders", new[] { "a: Attack" });
        dev = new CallDev();
        fx.Canvas.Render(dev);
        Assert.Equal("text w6 10,10 'Orders'", dev.Calls[0]);

        state = null; // not a chat menu: nothing is drawn
        dev = new CallDev();
        fx.Canvas.Render(dev);
        Assert.Empty(dev.Calls);
    }

    private static ClientWorld Team()
    {
        var w = new ClientWorld { PlayerId = 2049 };
        void Add(int id, string name, int team, int cmdr, int status = 0) =>
            w.Players.Clients[id] = new ClientPlayerRecord { Id = id, Name = name, Team = team, Commander = cmdr, CommandStatus = status };
        Add(2048, "Boss", 0, -1);
        Add(2049, "Rec", 0, 2048, 1);
        Add(2050, "Dan", 0, 2048);
        Add(2051, "Eve", 0, 2050);
        Add(2052, "Solo", 0, -1);
        Add(2053, "Foe", 1, -1);
        return w;
    }

    [Fact]
    public void TeamRowsFollowTheCommandTree()
    {
        IReadOnlyList<CommandTeamRow> rows = new ClientHudSource(Team()).CommandTeam;
        Assert.Equal(new[] { "Boss", "Rec", "Dan", "Eve", "Solo" }, rows.Select(r => r.Name));
        Assert.Equal(new[] { 0, 1, 1, 2, 0 }, rows.Select(r => r.Depth));
        Assert.Equal(new[] { true, false, true, false, false }, rows.Select(r => r.HasChildren));
        Assert.Equal(new[] { true, true, false, false, false }, rows.Select(r => r.HasNext));
        Assert.Equal(new[] { 1, 2, 1, 1, 1 }, rows.Select(r => r.Order)); // the record's command status + 1
        Assert.Equal(new[] { (1, false), (0, true) }, rows[3].Ancestors);
        Assert.Empty(new ClientHudSource(new ClientWorld { PlayerId = 5 }).CommandTeam);
    }

    [Fact]
    public void TeamRowDrawsDamageOrderTreePieceAndName()
    {
        var fx = new GuiFixture();
        var pieces = new PbmaArray { BitmapCount = 12 };
        for (var i = 0; i < 12; i++)
            pieces.Bitmaps.Add(new PbmpBitmap { Width = 4 + i, Height = 13 });
        fx.Host.SetArray("CmdListCtrl.pba", pieces);
        foreach (var (n, w) in new[] { ("CMD_DamageLow.bmp", 11), ("CMD_DamageMed.bmp", 11), ("CMD_DamageHigh.bmp", 11), ("CMD_OrderPending.bmp", 9), ("CMD_OrderAck.bmp", 9) })
            fx.Host.SetBitmap(n, new PbmpBitmap { Width = w, Height = 9 });
        fx.Host.SetFont("if_w_10b.pft", Font(7, 13));
        fx.Host.Hud = new ClientHudSource(Team());
        fx.Content(Root(GuiFixture.Make("FearGui::CommandTeamCtrl", 0, 0, 200, 100)));
        var dev = new CallDev();
        fx.Canvas.Render(dev);
        // Row 0 (Boss: children, a next sibling, no order): damage at (2, 1), tree piece 3 + 1 = 4 at x 2 + 11 + 1 + 9 + 2 = 25, the name one step
        // (the width of piece 11, 15) further, 2 above the row. Row 1 (Rec, order 2 = pending) has the order picture at x 14, one row (13 + 2) down.
        Assert.Contains("blit 11x9 2,1", dev.Calls);
        Assert.Contains("blit 9x9 14,16", dev.Calls);
        Assert.Contains("blit 8x13 25,0", dev.Calls);
        Assert.Contains("text w7 40,-2 'Boss'", dev.Calls);
        // Row 2 (Dan, depth 1): two steps in.
        Assert.Contains("text w7 55,28 'Dan'", dev.Calls);
    }
}
