// The Video options lists (docs/gui.md 3.3, code 004d9228, 004d946c): synthetic modes and renderer text only (R5).
namespace OpenTribes.Tests;

using OpenTribes.Client.App;
using OpenTribes.Gui;

public class VideoOptionsTests
{
    private static (GuiFixture Fx, GuiControl Root) Screen()
    {
        var fx = new GuiFixture();
        GuiControl root = GuiFixture.Make("SimGui::Control", 0, 0, 640, 480);
        foreach (var id in new[] { VideoOptions.WindowedCombo, VideoOptions.FullscreenCombo, VideoOptions.ResolutionCombo })
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

    [Fact]
    public void FullscreenWindowedCheckFollowsTheFullscreenRadioAndWritesThePref()
    {
        var (fx, root) = Screen();
        foreach (var id in new[] { VideoOptions.WindowedRadio, VideoOptions.FullscreenRadio })
        {
            GuiControl r = GuiFixture.Make("FearGui::FGUniversalButton", 0, 0, 18, 15);
            r.Id = (uint)id;
            root.AddChild(r);
        }
        var fs = (GuiUniversalButton)root.FindById(VideoOptions.FullscreenRadio)!;
        fs.On = false;
        const string pref = "$pref::OpenTribes::FullscreenWindowed";
        fx.Host.SetVar(pref, "TRUE");
        VideoOptions.AddFullscreenWindowed(root, fx.Host, pref);
        var box = (GuiUniversalButton)root.FindById(VideoOptions.FullscreenWindowedCheck)!;
        Assert.True(box.On);
        Assert.False(box.Active);
        fs.On = true;
        VideoOptions.SyncFullscreenWindowed(root);
        Assert.True(box.Active);
        box.Activate();
        Assert.False(box.On);
        Assert.Equal("FALSE", fx.Host.GetVar(pref));
        box.Activate();
        Assert.Equal("TRUE", fx.Host.GetVar(pref));
        fs.On = false;
        VideoOptions.SyncFullscreenWindowed(root);
        Assert.False(box.Active);
    }
}
