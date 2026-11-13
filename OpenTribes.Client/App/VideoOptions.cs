// The Video options lists [code 004d9228 devices, 004d946c modes]: the driver combos get one entry per display device, the resolution
// combo one per mode of the chosen full screen driver; each combo then selects the saved preference or, without one, the device in
// use. The devices are our own platform's: the software renderer and OpenGL (named with its renderer string); Glide is not
// reproduced (D31). The stock client lists the devices Windows reports, so the OpenGL entry shows another renderer text there.
using System.Globalization;
using OpenTribes.Gui;
using OpenTribes.Platform.Gl;

namespace OpenTribes.Client.App;

public static class VideoOptions
{
    public const int WindowedCombo = 140400, FullscreenCombo = 140401, ResolutionCombo = 140402,
        WindowedRadio = 140405, FullscreenRadio = 140406, FullscreenWindowedCheck = 140410;

    public sealed record Device(string Name, string? Info, int Id, bool Windowed, bool Fullscreen)
    {
        // 004d9228: the name, and " [info]" when the device has an info string.
        public string Text => Info is { Length: > 0 } ? $"{Name} [{Info}]" : Name;
    }

    public static List<Device> Devices(string? renderer) => new()
    {
        new Device("Software", null, 0, true, true),
        new Device("OpenGL", renderer, 1, true, true),
    };

    // 004d946c: "WxH" per mode, the id is the mode index.
    public static List<string> ModeNames(IEnumerable<(int W, int H)> modes) =>
        modes.Select(m => $"{m.W}x{m.H}").ToList();

    public static void Fill(GuiControl root, GuiHost host, string? renderer, IReadOnlyList<(int W, int H)> modes, string windowedDefault, string fullscreenDefault)
    {
        if (root.FindById(WindowedCombo) is not GuiCombo win || root.FindById(FullscreenCombo) is not GuiCombo fs || root.FindById(ResolutionCombo) is not GuiCombo res)
            return;
        win.Entries.Clear();
        fs.Entries.Clear();
        win.SetSelectedIndex(-1);
        fs.SetSelectedIndex(-1);
        foreach (Device d in Devices(renderer))
        {
            if (d.Fullscreen)
                fs.Entries.Add((d.Text, d.Id));
            if (d.Windowed)
                win.Entries.Add((d.Text, d.Id));
        }
        fs.SetSelectedIndex(Pick(fs, host.GetVar("$pref::VideoFullScreenDriver"), fullscreenDefault));
        win.SetSelectedIndex(Pick(win, host.GetVar("$pref::VideoWindowedDriver"), windowedDefault));
        res.Entries.Clear();
        res.SetSelectedIndex(-1);
        List<string> names = ModeNames(modes);
        for (int i = 0; i < names.Count; ++i)
            res.Entries.Add((names[i], i));
        int r = host.GetVar("$pref::VideoFullScreenRes") is { Length: > 0 } pref ? res.Entries.FindIndex(e => string.Equals(e.Text, pref, StringComparison.OrdinalIgnoreCase)) : -1;
        res.SetSelectedIndex(r < 0 ? 0 : r);
    }

    // Our own controls on the stock Video page (Options.gui; the stock files stay as they are): "Fullscreen windowed" picks the
    // borderless desktop full screen over the display mode change. It is the stock check box button (FGUniversalButton, BTN_CheckBox
    // bitmaps, a switch mirroring the pref as TRUE or FALSE) with a label in the stock label font, on its own row under the Gamma
    // slider. It is active only while the full screen radio (140406) is on.
    public static void AddFullscreenWindowed(GuiControl root, GuiHost host, string pref)
    {
        if (root.FindById(FullscreenWindowedCheck) != null || root.FindById(FullscreenRadio) is not GuiUniversalButton radio || radio.Parent is not GuiControl parent)
            return;
        // Left aligned with the Gamma label (the slider, 140403, is on its row) and one row below it.
        GuiControl? gamma = parent.Children.FirstOrDefault(c => c is GuiFGSimpleText t && t.Text == "Gamma");
        int x = gamma?.X ?? radio.X, y = gamma != null ? gamma.Y + gamma.H + 10 : radio.Y + radio.H + 24;
        var box = new GuiUniversalButton { ClassName = "FearGui::FGUniversalButton", Id = FullscreenWindowedCheck, X = x, Y = y, W = radio.W, H = radio.H, ConsoleVar = pref };
        foreach (var (k, v) in new (string, object)[] { ("bodyVersion", (byte)3), ("bitmapRoot", "BTN_CheckBox"), ("isSwitch", 1u), ("mirrorVar", 1u), ("radioId", 0u), ("active", (byte)1), ("messageTag", 0u), ("lowResX", (uint)x), ("lowResY", (uint)y) })
            box.F[k] = v;
        box.On = FullscreenPlan.UseDesktopMode(false, host.GetVar(pref));
        box.F["on"] = box.On ? 1u : 0u;
        var label = new FullscreenWindowedLabel { ClassName = "FearGui::FGSimpleText", X = x + 20, Y = y - 2, W = 150, H = 17, Text = "Fullscreen Windowed", Box = box };
        foreach (string k in new[] { "fontTag", "disabledFontTag", "highlightFontTag" })
            label.F[k] = 0u;
        parent.AddChild(box);
        parent.AddChild(label);
        SyncFullscreenWindowed(root);
    }

    // Active follows the full screen radio.
    public static void SyncFullscreenWindowed(GuiControl root)
    {
        if (root.FindById(FullscreenWindowedCheck) is GuiUniversalButton box && root.FindById(FullscreenRadio) is GuiUniversalButton fs && box.Active != fs.On)
            box.Active = fs.On;
    }

    // The entry of a saved driver name, else of the device in use, else the first.
    private static int Pick(GuiCombo combo, string pref, string current)
    {
        int i = pref.Length > 0 ? combo.FindEntryByName(pref) : -1;
        if (i < 0)
            i = combo.FindEntryByName(current);
        return i < 0 ? 0 : i;
    }
}

// The label of "Fullscreen windowed": the stock label look (FGSimpleText, orange with a black shadow) and the dimmed grey look
// when its check box is inactive [FGSimpleText::draw 004c9c70 fonts].
public sealed class FullscreenWindowedLabel : GuiSimpleText
{
    public GuiUniversalButton? Box;
    public override bool WantsTab => false;
    public override bool CanTake() => false;

    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Host == null || Canvas == null)
            return;
        bool small = Canvas.Width < 512 || Canvas.Height < 384, on = Box?.Active ?? true;
        string main = small ? (on ? "sf_yellow_6.pft" : "sf_grey_6.pft") : on ? "sf_orange255_10.pft" : "sf_grey100_10b.pft";
        string shadow = small ? "sf_black_6.pft" : "sf_black_10.pft";
        int gx = ox + X, gy = oy + Y;
        p.Text(Host.FontByName(shadow), gx, gy + 1, Text);
        p.Text(Host.FontByName(main), gx + 1, gy, Text);
    }
}
