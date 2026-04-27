// The Video options lists (docs/gui.md 3.3, code 004d9228, 004d946c): synthetic modes and renderer text only (R5).
using OpenTribes.Client.App;
using OpenTribes.Gui;

namespace OpenTribes.Tests;

public class VideoOptionsTests
{
    private static (GuiFixture Fx, GuiControl Root) Screen()
    {
        var fx = new GuiFixture();
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        foreach (int id in new[] { VideoOptions.WindowedCombo, VideoOptions.FullscreenCombo, VideoOptions.ResolutionCombo })
        {
            GuiControl c = GuiFixture.Make("FearGui::FGStandardComboBox", 0, 0, 200, 19);
            c.Id = (uint)id;
            root.AddChild(c);
        }
        fx.Content(root);
        return (fx, root);
    }

    private static GuiCombo Combo(GuiControl root, int id) => (GuiCombo)root.FindById((uint)id)!;

    [Fact]
    public void ListsTheDevicesAndModesAndSelectsTheSavedOnes()
    {
        var (fx, root) = Screen();
        fx.Host.SetVar("$pref::VideoFullScreenDriver", "OpenGL");
        fx.Host.SetVar("$pref::VideoWindowedDriver", "Software");
        fx.Host.SetVar("$pref::VideoFullScreenRes", "800x600");
        VideoOptions.Fill(root, fx.Host, "Test GPU", new[] { (640, 480), (800, 600) }, "Software", "OpenGL");
        GuiCombo fs = Combo(root, VideoOptions.FullscreenCombo);
        Assert.Equal(new[] { "Software", "OpenGL [Test GPU]" }, fs.Entries.Select(e => e.Text));
        Assert.Equal("OpenGL [Test GPU]", fs.GetValue());
        Assert.Equal("Software", Combo(root, VideoOptions.WindowedCombo).GetValue());
        GuiCombo res = Combo(root, VideoOptions.ResolutionCombo);
        Assert.Equal(new[] { "640x480", "800x600" }, res.Entries.Select(e => e.Text));
        Assert.Equal("800x600", res.GetValue());
    }

    [Fact]
    public void WithoutSavedChoicesTheDeviceInUseAndTheFirstModeShow()
    {
        var (fx, root) = Screen();
        VideoOptions.Fill(root, fx.Host, null, new[] { (640, 480), (1024, 768) }, "Software", "OpenGL");
        Assert.Equal("OpenGL", Combo(root, VideoOptions.FullscreenCombo).GetValue());
        Assert.Equal("640x480", Combo(root, VideoOptions.ResolutionCombo).GetValue());
    }

    [Fact]
    public void FindEntryTakesTheFirstWordOfAnEntry()
    {
        var (fx, root) = Screen();
        VideoOptions.Fill(root, fx.Host, "Test GPU", new[] { (640, 480) }, "Software", "OpenGL");
        GuiCombo fs = Combo(root, VideoOptions.FullscreenCombo);
        Assert.Equal(1, fs.FindEntryByName("opengl"));
        Assert.Equal(-1, fs.FindEntryByName("Glide"));
    }
}
