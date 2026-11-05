// The commander map (docs/gui.md 3.3 "The command map", docs/world-render.md 2.11): TSCommander [004ceb18, 004cef14, 004d04e8] and its picture.
using OpenTribes.Client.World;
using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;
using OpenTribes.World;

namespace OpenTribes.Tests;

public class CommandMapTests
{
    private sealed class StretchDev : IGfxDevice
    {
        public readonly List<string> Calls = new();
        public readonly List<GfxRect?> Clips = new();
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
        public void SetClip(GfxRect? clip) => Clips.Add(clip);
        public void DrawLine(int x0, int y0, int x1, int y1, int colorIndex) => Calls.Add($"line {x0},{y0},{x1},{y1} c{colorIndex:x}");
        public void DrawRectOutline(GfxRect rect, int colorIndex) { }
        public void FillRect(GfxRect rect, int colorIndex, float alpha = 1f) { }
        public int DrawText(PftFont font, int x, int y, string text) { Calls.Add($"text {x},{y} '{text}'"); return y; }
        public void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None) => Calls.Add($"blit {bitmap.Width}x{bitmap.Height} {x},{y} f{(int)flip}");
        public void BlitRegion(PbmpBitmap bitmap, GfxRect source, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitStretch(PbmpBitmap bitmap, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) =>
            Calls.Add($"stretch {bitmap.Width}x{bitmap.Height} {x},{y} {width}x{height}");
        public void BlitRegionStretch(PbmpBitmap bitmap, GfxRect source, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public byte[] ReadPixels() => Array.Empty<byte>();
        public IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options) => throw new NotSupportedException();
        public void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB) { }
        public void SetCamera3D(in Camera3D camera) { }
        public void Draw3D(ReadOnlySpan<Vertex3D> triangles, in DrawState3D state) { }
        public void EndScene3D() { }
    }

    private static CommandMapPicture Picture() => new(-827, -717, 1000, 1000, CommandMapBake.Tiles(new byte[CommandMapBake.Size * CommandMapBake.Size]));

    [Fact]
    public void FitScalesByTheSmallerParentSideAndCentresTheOtherAxis() // [code 004cf3ed .. 004cf4f1], command.gui: parent 409 x 480, map 408 x 408 at (0, 36)
    {
        // the float scale 409 / 408 is a little under, so 408 * scale truncates to 408: the control keeps its size and the centring gives its file place
        Assert.Equal((0, 36, 408, 408), GuiCommandMap.Fit(0, 36, 408, 408, 409, 480));
        // a parent wider than high: scaled to the height, centred across
        Assert.Equal((50, 0, 100, 100), GuiCommandMap.Fit(0, 0, 200, 200, 200, 100));
        // a control that already fits is only moved when the parent is larger
        Assert.Equal((0, 0, 400, 400), GuiCommandMap.Fit(0, 0, 400, 400, 400, 400));
    }

    [Fact]
    public void TileSpanIsAQuarterRoundedUpWhenFourFallShort() // [code 004d04e8]
    {
        Assert.Equal(102, GuiCommandMap.TileSpan(408));
        Assert.Equal(103, GuiCommandMap.TileSpan(409));
        Assert.Equal(100, GuiCommandMap.TileSpan(400));
        Assert.Equal(1, GuiCommandMap.TileSpan(3));
    }

    [Fact]
    public void ClassMapsToTheMapControl() => Assert.IsType<GuiCommandMap>(GuiFixture.Make("FearGui::TSCommander"));

    [Fact]
    public void DrawFitsTheControlThenStretchesSixteenTilesFromItsCorner()
    {
        var fx = new GuiFixture();
        fx.Host.Hud = new StaticHudSource { CommandMapValue = Picture() };
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        GuiControl parent = GuiFixture.Make("SimGui::Control", 31, 0, 409, 480);
        GuiControl map = GuiFixture.Make("FearGui::TSCommander", 0, 36, 408, 408);
        root.AddChild(parent);
        parent.AddChild(map);
        fx.Content(root);
        var dev = new StretchDev();
        fx.Canvas.Render(dev);
        Assert.Equal(16, dev.Calls.Count);
        Assert.Equal("stretch 256x256 31,36 102x102", dev.Calls[0]);
        Assert.Equal("stretch 256x256 133,36 102x102", dev.Calls[1]);
        Assert.Equal("stretch 256x256 31,138 102x102", dev.Calls[4]);
        Assert.Equal("stretch 256x256 337,342 102x102", dev.Calls[15]);
        Assert.Equal((0, 36, 408, 408), (map.X, map.Y, map.W, map.H));
        // the clip is the parent's rectangle while the tiles draw
        Assert.Contains(new GfxRect(31, 0, 439, 479), dev.Clips);
    }

    [Fact]
    public void NoPictureDrawsNothing()
    {
        var fx = new GuiFixture();
        fx.Host.Hud = new StaticHudSource();
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        root.AddChild(GuiFixture.Make("FearGui::TSCommander", 0, 36, 408, 408));
        fx.Content(root);
        var dev = new StretchDev();
        fx.Canvas.Render(dev);
        Assert.Empty(dev.Calls);
    }

    // ---- the picture ----

    private static GridBlock Block(int n, Func<int, int, (byte flags, byte mat)> sq)
    {
        var b = new GridBlock { Size = n, Heights = new float[(n + 1) * (n + 1)], Squares = new byte[n * n * 2] };
        for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
            {
                var (f, m) = sq(x, y);
                b.Squares[(y * n + x) * 2] = f;
                b.Squares[(y * n + x) * 2 + 1] = m;
            }
        return b;
    }

    // Material 0 is red, 1 is blue; a material is a 4 x 4 picture whose left half is dark (so orientation shows).
    private static byte[]? Material(int m)
    {
        var rgba = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; ++y)
            for (int x = 0; x < 4; ++x)
            {
                int o = (y * 4 + x) * 4;
                byte v = (byte)(x < 2 ? 100 : 200);
                rgba[o] = m == 0 ? v : (byte)0;
                rgba[o + 2] = m == 1 ? v : (byte)0;
                rgba[o + 3] = 255;
            }
        return rgba;
    }

    // The nearest "palette": index = the dominant channel (1 red, 2 blue) times 10 plus the level / 100.
    private static int Nearest(int r, int g, int b) => (r > b ? 10 : 20) + (Math.Max(r, b) >= 150 ? 2 : 1);

    private static byte[] Bake(TerrainData t, double ox, double oy, double ax, double ay, double aw, double ah) =>
        CommandMapBake.Render(t, ox, oy, ax, ay, aw, ah, Material, _ => (4, 4), Nearest);

    [Fact]
    public void PictureHasTheNorthEdgeOnTopAndOnePixelPerAreaOver1024()
    {
        // two materials in rows: cell row y < 128 red, else blue; the area is the lower left 2048 / 2 square of the terrain at origin 0
        var t = new TerrainData(Block(256, (x, y) => (0, (byte)(y < 128 ? 0 : 1))), 2, Array.Empty<ushort>());
        // the whole 2048 x 2048 terrain: the picture's top rows are the north (y near 2048, blue), the bottom rows the south (red)
        byte[] pic = Bake(t, 0, 0, 0, 0, 2048, 2048);
        Assert.Equal(CommandMapBake.Size * CommandMapBake.Size, pic.Length);
        Assert.True(pic[10 * 1024 + 500] >= 20);
        Assert.True(pic[1000 * 1024 + 500] is >= 10 and < 20);
        // the halfway row is the cell row 128 (y = 1024): rows 0 to 511 are blue, 512 to 1023 red
        Assert.True(pic[511 * 1024] >= 20);
        Assert.True(pic[512 * 1024] is >= 10 and < 20);
    }

    [Fact]
    public void PictureFollowsTheAreaOrigin()
    {
        // 1024 x 1024 world units at (-512, -512) over a terrain at origin (-1024, -1024): cell x 64..192 of the 256
        var t = new TerrainData(Block(256, (x, y) => (0, (byte)(x < 128 ? 0 : 1))), 2, Array.Empty<ushort>());
        byte[] pic = Bake(t, -1024, -1024, -512, -512, 1024, 1024);
        // world x 0 (cell 128) is the middle column: left of it red, right of it blue
        Assert.True(pic[100 * 1024 + 100] is >= 10 and < 20);
        Assert.True(pic[100 * 1024 + 900] >= 20);
    }

    [Fact]
    public void FlagsOrientTheCellTextureAndEmptySquaresStayZero()
    {
        // one terrain of material 0 with flag 0 (mirror v) and flag 4 (e = 0, no change): the dark half of the picture is the left half in x
        // for both, since neither flag swaps u and v or mirrors u (the element bits 2 and 0)
        var t = new TerrainData(Block(256, (x, y) => (x == 0 ? (byte)(1 << 3) : (byte)0, (byte)0)), 2, Array.Empty<ushort>());
        byte[] pic = Bake(t, 0, 0, 0, 0, 2048, 2048); // 2 world units a pixel, 4 pixels a cell
        Assert.Equal(0, pic[500 * 1024 + 1]);             // cell column 0 is an empty square
        Assert.Equal(11, pic[500 * 1024 + 4]);            // dark half of the next cell: the material's left half is 100
        Assert.Equal(12, pic[500 * 1024 + 6]);            // light half: 200
        // flag 1 (rotate: element bits 3, swap and mirror u): the dark half moves to the top of the cell
        var r = new TerrainData(Block(256, (x, y) => ((byte)1, (byte)0)), 2, Array.Empty<ushort>());
        byte[] rp = Bake(r, 0, 0, 0, 0, 2048, 2048);
        // the row 500 is in cell row 127 (y 1000 .. 1008 is pixel rows 496 .. 500 from the top 2048 down): rows are not all one value
        Assert.NotEqual(rp[496 * 1024 + 8], rp[499 * 1024 + 8]);
    }

    [Fact]
    public void LightMapScalesTheColour()
    {
        // light words: 0xf000 (mono nibble only) is black for the colour nibbles, 0x0888 is half
        var light = new ushort[257 * 257];
        for (int i = 0; i < light.Length; ++i)
            light[i] = 0x0fff;
        var t = new TerrainData(Block(256, (x, y) => (0, (byte)0)), 2, light);
        byte[] bright = Bake(t, 0, 0, 0, 0, 2048, 2048);
        Assert.Equal(11, bright[300 * 1024 + 300]); // red, dark half of the material (100) at full light
        for (int i = 0; i < light.Length; ++i)
            light[i] = 0x0000;
        var dark = new TerrainData(Block(256, (x, y) => (0, (byte)0)), 2, light);
        byte[] black = Bake(dark, 0, 0, 0, 0, 2048, 2048);
        // r = g = b = 0 maps through the nearest function to the blue family (r > b is false)
        Assert.Equal(21, black[300 * 1024 + 300]);
    }

    [Fact]
    public void TilesAreTheSixteenQuartersInRowMajorOrder()
    {
        var pic = new byte[CommandMapBake.Size * CommandMapBake.Size];
        for (int y = 0; y < CommandMapBake.Size; ++y)
            for (int x = 0; x < CommandMapBake.Size; ++x)
                pic[y * CommandMapBake.Size + x] = (byte)((y / 256) * 4 + x / 256 + 1);
        PbmpBitmap[] tiles = CommandMapBake.Tiles(pic);
        Assert.Equal(16, tiles.Length);
        for (int i = 0; i < 16; ++i)
        {
            Assert.Equal(256, tiles[i].Width);
            Assert.Equal(i + 1, tiles[i].Pixels[0]);
            Assert.Equal(i + 1, tiles[i].Pixels[255 * tiles[i].Stride + 255]);
        }
    }

    // ---- the marks over the picture ----

    private static ClientDataBlock Block(int group, int index, params (string, object)[] fields) =>
        new() { Group = group, Index = index, Fields = fields.Select(f => new DecodedField(f.Item1, f.Item2)).ToList() };

    private static ClientWorld MarkWorld()
    {
        var w = new ClientWorld { PlayerId = 2049 };
        w.Players.Clients[2049] = new ClientPlayerRecord { Id = 2049, Name = "Rec", Team = 0 };
        w.Players.Clients[2050] = new ClientPlayerRecord { Id = 2050, Name = "Dan", Team = 0, Commander = 3, CmdIssuer = 2049, CmdTarget = 129, CommandStatus = 1 };
        w.DataBlocks.Blocks[(3, 0)] = Block(3, 0, ("mapFilter", 15L), ("mapIcon", "M_player"), ("description", "Armor"));
        w.DataBlocks.Blocks[(19, 0)] = Block(19, 0, ("mapFilter", 15L), ("mapIcon", "M_turret"), ("description", "Plasma Turret"), ("range", 100L), ("supression", 0L));
        w.Players.TargetNames[1] = "Balcony Turret";
        w.Psc.CommandSlots[1] = new ClientCommandSlot { Present = true, X = 512, Y = 512, Team = 0, Group = 3, Index = 0 };
        w.Psc.CommandSlots[2] = new ClientCommandSlot { Present = true, X = 256, Y = 768, Team = 0, Group = 3, Index = 0 };
        w.Psc.CommandSlots[129] = new ClientCommandSlot { Present = true, X = 768, Y = 256, Team = 0, Group = 19, Index = 0, Active = true };
        return w;
    }

    [Fact]
    public void MarksCarryTheSlotsTheirDatablocksAndTheRecords()
    {
        CommandMapMarks marks = new ClientHudSource(MarkWorld()).CommandMapMarks!;
        Assert.Equal((2049, 0), (marks.LocalId, marks.LocalTeam));
        Assert.Equal(new[] { 1, 2, 129 }, marks.Slots.Select(m => m.Slot));
        CommandMapSlotMark dan = marks.Slots[1];
        Assert.Equal(("Dan", 2050, 3, 129, 1), (dan.ClientName, dan.ClientId, dan.OrderIcon, dan.OrderTarget, dan.OrderStatus));
        CommandMapSlotMark turret = marks.Slots[2];
        Assert.Equal(("M_turret", "Balcony Turret", 100, true), (turret.Icon, turret.TargetName, turret.Range, turret.Active));
        // a record that never got an order named an issuer has no order, even when the shared field holds a commander id
        Assert.Equal(-1, marks.Slots[0].OrderIcon);
        Assert.Null(new ClientHudSource(new ClientWorld { PlayerId = 9 }).CommandMapMarks);
    }

    [Fact]
    public void SlotsAreDroppedWithoutADatablock()
    {
        ClientWorld w = MarkWorld();
        w.DataBlocks.Blocks.Remove((19, 0));
        Assert.Equal(new[] { 1, 2 }, new ClientHudSource(w).CommandMapMarks!.Slots.Select(m => m.Slot));
    }

    [Fact]
    public void DrawPutsTheCircleOrderLineIconsAndNamesOverTheTiles()
    {
        var fx = new GuiFixture();
        ClientWorld w = MarkWorld();
        var hud = new ClientHudSource(w) { CommandMapSource = Picture };
        fx.Host.Hud = hud;
        fx.Interp.SetVariable("$pref::mapFilter", "15");
        fx.Interp.SetVariable("$pref::mapSensorRange", "1");
        fx.Interp.SetVariable("$pref::mapNames", "1");
        fx.Host.SetFont("if_small.pft", new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 5, CellHeight = 8 });
        fx.Host.SetBitmap("M_turret_green.bmp", new PbmpBitmap { Width = 8, Height = 6 });
        var order = new PbmaArray { BitmapCount = 8 };
        for (int i = 0; i < 8; ++i)
            order.Bitmaps.Add(new PbmpBitmap { Width = 10 + i, Height = 10 });
        fx.Host.SetArray("CmdIconsCtrl.pba", order);
        var here = new PbmaArray { BitmapCount = 8 };
        for (int i = 0; i < 8; ++i)
            here.Bitmaps.Add(new PbmpBitmap { Width = 9, Height = 9 });
        fx.Host.SetArray("CmdWhereAmI.pba", here);
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        GuiControl parent = GuiFixture.Make("SimGui::Control", 31, 0, 409, 480);
        root.AddChild(parent);
        parent.AddChild(GuiFixture.Make("FearGui::TSCommander", 0, 36, 408, 408));
        fx.Content(root);
        var dev = new StretchDev();
        fx.Canvas.Render(dev);
        // the control is at (31, 36) 408 x 408: slot 1 at 512, 512 is its middle (235, 240); slot 129 at 768, 256 is (337, 342)
        // the turret circle: range 100 over the 1000 unit area at 408 pixels is 40 pixels, colour 0xfb (no suppression), the first point at angle 0
        Assert.Contains("line 377,342,376,349 cfb", dev.Calls);
        // the green turret icon, centred: 8 x 6 around (337, 342)
        Assert.Contains("blit 8x6 333,339 f0", dev.Calls);
        // Dan (slot 2 at 256, 768: (133, 138)) has an order on slot 129: a white... status 1 is 0xf2 line to the turret and the icon 3 piece 2 (12 x 10) centred on it
        Assert.Contains("line 133,138,337,342 cf2", dev.Calls);
        Assert.Contains("blit 12x10 331,337 f0", dev.Calls);
        // the names: players after pass 3, the turret by its target name, all at (x + 4, y - 4)
        Assert.Contains("text 137,134 'Dan'", dev.Calls);
        Assert.Contains("text 239,236 'Rec'", dev.Calls);
        Assert.Contains("text 341,338 'Balcony Turret'", dev.Calls);
        // the where-am-I mark of the local client: heading 256 wraps to 0 .. 12: piece 0, no flip, centred 9 x 9
        Assert.Contains("blit 9x9 231,236 f0", dev.Calls);
    }

    [Fact]
    public void FilterAndSensorRangePrefsGateTheMarks()
    {
        var fx = new GuiFixture();
        fx.Host.Hud = new ClientHudSource(MarkWorld()) { CommandMapSource = Picture };
        fx.Interp.SetVariable("$pref::mapFilter", "0");
        fx.Interp.SetVariable("$pref::mapSensorRange", "0");
        fx.Host.SetFont("if_small.pft", new PftFont { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = 5, CellHeight = 8 });
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        root.AddChild(GuiFixture.Make("FearGui::TSCommander", 0, 36, 408, 408));
        fx.Content(root);
        var dev = new StretchDev();
        fx.Canvas.Render(dev);
        Assert.Equal(16, dev.Calls.Count); // the tiles only
    }

    // ---- zoom and pan ----

    private static (GuiFixture Fx, GuiCommandMap Map) ZoomSetup()
    {
        var fx = new GuiFixture();
        fx.Host.Hud = new StaticHudSource { CommandMapValue = Picture() };
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        GuiControl parent = GuiFixture.Make("SimGui::Control", 31, 0, 409, 480);
        var map = (GuiCommandMap)GuiFixture.Make("FearGui::TSCommander", 0, 36, 408, 408);
        root.AddChild(parent);
        parent.AddChild(map);
        fx.Content(root);
        fx.Canvas.Render(new StretchDev()); // the fit
        return (fx, map);
    }

    [Fact]
    public void ZoomInDoublesTheControlAroundTheCursorAndAnimatesOverTheZoomTime() // [code 004d3424, 004cf784]
    {
        var (fx, map) = ZoomSetup();
        fx.Interp.SetVariable("$pref::CommanderZoomTime", "250");
        // the cursor at parent point (200, 240) is canvas (231, 240) over the control: it stays over the same map point
        map.ZoomIn(231, 240, true);
        Assert.Equal(1, map.ZoomSteps);
        Assert.True(map.Animating);
        Assert.Equal((0, 36, 408, 408), (map.X, map.Y, map.W, map.H)); // nothing moves before the first frame step
        fx.Frame(125);
        Assert.Equal((-100, -66, 612, 612), (map.X, map.Y, map.W, map.H)); // half way: 0.5 of the way to (-200, -168, 816, 816)
        fx.Frame(200);
        Assert.False(map.Animating);
        Assert.Equal((-200, -168, 816, 816), (map.X, map.Y, map.W, map.H));
    }

    [Fact]
    public void ZoomStopsAtFourStepsAndOutAtNoZoomRefits()
    {
        var (fx, map) = ZoomSetup();
        for (int i = 0; i < 6; ++i)
            map.ZoomIn(231, 240, true);
        Assert.Equal(GuiCommandMap.MaxZoomSteps, map.ZoomSteps);
        fx.Frame(500);
        Assert.Equal(408 * 16, map.W);
        for (int i = 0; i < 4; ++i)
            map.ZoomOut(231, 240, true);
        Assert.Equal(0, map.ZoomSteps);
        fx.Frame(500);
        Assert.Equal(408, map.W);
        // one more out with no zoom: fitted at once, no animation
        map.ZoomOut(231, 240, true);
        Assert.False(map.Animating);
        Assert.Equal((0, 36, 408, 408), (map.X, map.Y, map.W, map.H));
    }

    [Fact]
    public void ZoomOutHalvesAroundTheCursorAndTheClampKeepsThePictureOverTheParent()
    {
        var (fx, map) = ZoomSetup();
        map.ZoomIn(31, 0, true);       // the parent's corner: (0, 0) - (0 - 36) * 2 = 72 would leave a gap, the clamp holds the top at +240
        fx.Frame(300);
        Assert.Equal((0, 72, 816, 816), (map.X, map.Y, map.W, map.H));
        // the parent is 409 x 480: x is at most half the parent wide (204), y at most 240, and at least -(half) - |parent - size|
        map.ZoomIn(31 + 800, 479, true);
        fx.Frame(300);
        Assert.True(map.X >= -(409 / 2) - Math.Abs(409 - map.W) && map.X <= 409 >> 1);
        Assert.True(map.Y >= -(480 / 2) - Math.Abs(480 - map.H) && map.Y <= 480 >> 1);
    }

    [Fact]
    public void ZoomModeClicksZoomAndTheRightButtonPansOtherwise() // [code 004d1bf0, 004d2d78, 004d2e5c, 004d2fe0]
    {
        var (fx, map) = ZoomSetup();
        // not in zoom mode: the right button drags the picture by the mouse movement (the clamp does not bind here)
        map.ZoomMode = false;
        map.MsRightDown(new GuiEvent { X = 100, Y = 100 });
        map.MsRightDrag(new GuiEvent { X = 90, Y = 130 });
        Assert.Equal((-10, 66), (map.X, map.Y));
        map.MsRightUp(new GuiEvent());
        // zoom mode: the left button zooms in, the right button out
        map.ZoomMode = true;
        map.MsDown(new GuiEvent { X = 131, Y = 140 });
        Assert.Equal(1, map.ZoomSteps);
        map.MsRightDown(new GuiEvent { X = 131, Y = 140 });
        Assert.Equal(0, map.ZoomSteps);
    }
}
