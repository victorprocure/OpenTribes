// Tests of the C8 follow-up fixes (ROADMAP M9 C8): the chat entry draw, the HUD formatted-text markup, the weapon list layout, the
// undefined `Game::EndFrame` callback and the prefs round trip. Synthetic data only (R5).
using OpenTribes.Client.App;
using OpenTribes.Core;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Gl;
using OpenTribes.Script;

namespace OpenTribes.Tests;

public class GuiC8FixTests
{
    // A device that records fills, lines and text with the width of the font it was drawn in.
    private sealed class TextDev : IGfxDevice
    {
        public readonly List<string> Calls = new();
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
        public void FillRect(GfxRect rect, int colorIndex, float alpha = 1f) => Calls.Add($"fill c{colorIndex:x}");
        public int DrawText(PftFont font, int x, int y, string text) { Calls.Add($"text w{font.GlyphCellWidth} {x},{y} '{text}'"); return y; }
        public void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitRegion(PbmpBitmap bitmap, GfxRect source, int x, int y, BlitFlip flip = BlitFlip.None) { }
        public void BlitStretch(PbmpBitmap bitmap, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public void BlitRegionStretch(PbmpBitmap bitmap, GfxRect source, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) { }
        public byte[] ReadPixels() => Array.Empty<byte>();
        public IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options) => throw new NotSupportedException();
        public void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB) { }
        public void SetCamera3D(in Camera3D camera) { }
        public void Draw3D(ReadOnlySpan<Vertex3D> triangles, in DrawState3D state) { }
        public void EndScene3D() { }
    }

    private static PftFont Font(int cell) => new() { Flags = PftFont.FlagFixedWidth, GlyphCellWidth = cell, CellHeight = 10 };

    private static GuiFixture Fixture(out GuiControl root)
    {
        var fx = new GuiFixture();
        fx.Interp.Evaluate("IDFNT_HL = 150007, \"hl.pft\"; IDFNT_FONTIS_3 = 150303, \"f0.pft\"; IDFNT_FONTIS_2 = 150302, \"f1.pft\";");
        fx.Host.SetFont("hl.pft", Font(7));
        fx.Host.SetFont("f0.pft", Font(5));
        fx.Host.SetFont("f1.pft", Font(6));
        fx.Host.SetFont("sf_white_10.pft", Font(8));
        root = GuiFixture.Make("SimGui::TSControl", 0, 0, 640, 480);
        return fx;
    }

    private static List<string> Draw(GuiFixture fx)
    {
        var dev = new TextDev();
        fx.Canvas.Render(dev);
        return dev.Calls;
    }

    // ---- 1. chat entry ----

    [Fact]
    public void ChatEntryDrawsItsTextCursorAndNoWhiteFill() // gui.md 3.3 TextEdit draw: class text draw (text, cursor); play.gui chat has font tag 0
    {
        GuiFixture fx = Fixture(out GuiControl root);
        var chat = (GuiChat)GuiFixture.Make("FearGui::FearGuiChat", 135, 414, 370, 36,
            ("active", (byte)1), ("maxLength", (uint)80), ("fontTag", 0u), ("highlightFontTag", 150007u));
        chat.SetVisible(false);
        root.AddChild(chat);
        fx.Content(root);
        chat.Open();
        fx.Type("hi");
        List<string> calls = Draw(fx);
        Assert.Contains(calls, c => c.StartsWith("text w7 ") && c.EndsWith("'hi'"));       // the highlight font, not nothing
        Assert.DoesNotContain("fill cff", calls);                                           // not the opaque white of the plain TextEdit
        Assert.Contains(calls, c => c.StartsWith("line ") && c.EndsWith(" cfe"));           // the cursor
    }

    [Fact]
    public void ChatEntryFillFollowsItsProfile() // gui.md 2.5 base render: the opaque flag and fill colour of the Control block
    {
        GuiFixture fx = Fixture(out GuiControl root);
        var chat = (GuiChat)GuiFixture.Make("FearGui::FearGuiChat", 0, 0, 100, 20, ("active", (byte)1), ("highlightFontTag", 150007u));
        chat.Opaque = 1;
        chat.FillColor = 0x12;
        root.AddChild(chat);
        fx.Content(root);
        Assert.Contains("fill c12", Draw(fx));
    }

    // ---- 2. formatted text ----

    [Fact]
    public void MarkupSelectsFontsAndDropsUnspecifiedTags() // gui.md 3.3 TextFormat: `<fN>` selects a font; script.md 1: `<jc>` is text the GUI interprets
    {
        List<GuiMarkup.Run> runs = GuiMarkup.Parse("<jc><f1>Hello <f0>world<l7>!");
        Assert.Equal(new[] { new GuiMarkup.Run(1, "Hello "), new GuiMarkup.Run(0, "world!") }, runs);
        Assert.Equal("a < b <3 c", GuiMarkup.Strip("a < b <3 c"));            // not a tag
        Assert.Equal("x", GuiMarkup.Strip("<f9>x"));                          // outside f0..f6: dropped, no font change
    }

    [Fact]
    public void CenterPrintAndChatDisplayDrawNoMarkup() // gui.md 3.3 CenterPrint (formatted-text layout), 4.2 chat display
    {
        GuiFixture fx = Fixture(out GuiControl root);
        root.AddChild(GuiFixture.Make("CenterPrint", 25, 419, 590, 36));
        root.AddChild(GuiFixture.Make("FearGui::FearGuiChatDisplay", 132, 9, 370, 45, ("lines", 3u), ("hudX", 0u), ("hudY", 0u)));
        fx.Content(root);
        fx.Host.CenterPrint("<jc><f1>Flag <f0>taken", 5);
        fx.Host.AddChat("<f1>Tester: <f0>hello<jl>");
        List<string> calls = Draw(fx);
        Assert.DoesNotContain(calls, c => c.Contains('<'));
        Assert.Contains(calls, c => c.StartsWith("text w6 ") && c.EndsWith("'Flag '"));
        Assert.Contains(calls, c => c.StartsWith("text w5 ") && c.EndsWith("'taken'"));
        Assert.Contains(calls, c => c.StartsWith("text w6 ") && c.EndsWith("'Tester: '"));
        Assert.Contains(calls, c => c.StartsWith("text w5 ") && c.EndsWith("'hello'"));
    }

    [Fact]
    public void FormattedTextControlWrapsMarkedUpText() // gui.md 3.3 FearGuiFormattedText: markup, wrapping
    {
        GuiFixture fx = Fixture(out GuiControl root);
        root.AddChild(GuiFixture.Make("FearGuiFormattedText", 0, 0, 120, 100, ("fontTag", 150007u), ("lineSpacing", 0u)));
        fx.Content(root);
        ((GuiTextWrap)root.Children[0]).Text = "<jc><f1>aaaa bbbb cccc dddd eeee";
        List<string> calls = Draw(fx);
        Assert.DoesNotContain(calls, c => c.Contains('<'));
        Assert.True(calls.Count(c => c.StartsWith("text w6")) >= 2);          // 6 px glyphs, 120 px wide: wrapped at 20 characters
    }

    // ---- 3. weapon list ----

    [Fact]
    public void WeaponListUsesTheSpecWidth() // gui.md 4.2 CurrentWeaponHud: ext (62,76); the draw is icons, the names are our text
    {
        var fx = new GuiFixture();
        var root = GuiFixture.Make("SimGui::TSControl", 0, 0, 640, 480);
        var hud = (GuiHud)GuiFixture.Make("FearGui::CurrentWeaponHud", 0, 213, 62, 76, ("hudX", 0u), ("hudY", 0u));
        root.AddChild(hud);
        fx.Content(root);
        Assert.Equal((62, 76), (hud.W, hud.H));
    }

    // ---- 4. Game::EndFrame ----

    [Fact]
    public void UndefinedEndFrameCallbackPrintsUnknownCommand() // sim.md TK9 [live]: the original prints it every frame, so it is kept
    {
        var interp = new Interpreter();
        var lines = new List<string>();
        interp.SetOutput(lines.Add);
        interp.CallFunction("Game::EndFrame", Array.Empty<string>());
        interp.CallFunction("Game::EndFrame", Array.Empty<string>());
        Assert.Equal(2, lines.Count(l => l.Contains("Game::EndFrame: Unknown command.")));
    }

    // ---- 5. prefs round trip ----

    [Theory]
    [InlineData(5, "0.999998")]
    [InlineData(5, "0.899999")]
    [InlineData(5, "0.000991")]
    [InlineData(3, "0.0999999")]
    [InlineData(3, "123.457")]
    [InlineData(4, "0.299999")]
    public void PrefTextSurvivesTenSaveLoadCycles(int type, string text) // script.md 3.2 (truncating print) with D27 (toward-zero parse)
    {
        Numbers.EnterScriptFpuMode();
        string cur = EngineVariables.Normalize(type, text);
        Assert.Equal(text, cur);                       // the printed digits are kept
        for (int i = 0; i < 10; ++i)
            cur = EngineVariables.Normalize(type, cur);
        Assert.Equal(text, cur);
    }

    [Theory]
    [InlineData(5, "0.9", "0.899999")]                 // a hand-typed value prints the original's digits (script.md 3.2: 0.1 is 0.0999999)
    [InlineData(3, "0.1", "0.0999999")]
    [InlineData(5, "1", "1.000000")]
    [InlineData(3, "1500", "1500")]
    public void TypedPrefTextKeepsTheOriginalDigits(int type, string typed, string printed) // script.md 3.2
    {
        Numbers.EnterScriptFpuMode();
        Assert.Equal(printed, EngineVariables.Normalize(type, typed));
    }

    [Fact]
    public void ActionMapScaleSurvivesSaveAndLoad() // client-scripts.md 3.9 saveActionMap, E7: `%f` of the scale
    {
        Numbers.EnterScriptFpuMode();
        Assert.True(ClientCommands.ScanFloat("0.000991", out float f));
        string once = CFormat.F(f, 6);
        Assert.Equal("0.000991", once);
        for (int i = 0; i < 10; ++i)
        {
            Assert.True(ClientCommands.ScanFloat(once, out f));
            once = CFormat.F(f, 6);
        }
        Assert.Equal("0.000991", once);
    }
}
