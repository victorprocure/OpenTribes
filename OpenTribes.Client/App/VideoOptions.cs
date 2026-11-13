namespace OpenTribes.Client.App;

using OpenTribes.Gui;
using OpenTribes.Platform.Gl;

/// <summary>The Video options lists: the driver combos get one entry per display device, the resolution combo one per mode of the chosen full screen driver. Each combo then selects the saved preference or, without one, the device in use.</summary>
/// <remarks>Follows 004d9228 (devices) and 004d946c (modes). The devices are our own platform's: the software renderer and OpenGL (named with its renderer string). Glide is not reproduced (D31). The stock client lists the devices Windows reports, so the OpenGL entry shows another renderer text there.</remarks>
public static class VideoOptions
{
    /// <summary>The control id of the windowed driver combo.</summary>
    public const int WindowedCombo = 140400;
    /// <summary>The control id of the full screen driver combo.</summary>
    public const int FullscreenCombo = 140401;
    /// <summary>The control id of the resolution combo.</summary>
    public const int ResolutionCombo = 140402;
    /// <summary>The control id of the windowed radio button.</summary>
    public const int WindowedRadio = 140405;
    /// <summary>The control id of the full screen radio button.</summary>
    public const int FullscreenRadio = 140406;
    /// <summary>The control id of the fullscreen windowed check box.</summary>
    public const int FullscreenWindowedCheck = 140410;

    /// <summary>A display device.</summary>
    /// <param name="Name">The device name.</param>
    /// <param name="Info">The info string, or null.</param>
    /// <param name="Id">The device id.</param>
    /// <param name="Windowed">True when the device can run windowed.</param>
    /// <param name="Fullscreen">True when the device can run full screen.</param>
    public sealed record Device(string Name, string? Info, int Id, bool Windowed, bool Fullscreen)
    {
        /// <summary>The list text: the name, and " [info]" when the device has an info string (004d9228).</summary>
        public string Text => Info is { Length: > 0 } ? $"{Name} [{Info}]" : Name;
    }

    /// <summary>Lists the devices of the platform.</summary>
    /// <param name="renderer">The renderer string of OpenGL, or null.</param>
    /// <returns>The devices.</returns>
    public static List<Device> Devices(string? renderer) =>
    [
        new Device("Software", null, 0, true, true),
        new Device("OpenGL", renderer, 1, true, true),
    ];

    /// <summary>Lists the mode names as "WxH" (004d946c). The id of an entry is the mode index.</summary>
    /// <param name="modes">The modes.</param>
    /// <returns>The names.</returns>
    public static List<string> ModeNames(IEnumerable<(int W, int H)> modes) =>
        [.. modes.Select(m => $"{m.W}x{m.H}")];

    /// <summary>Fills the combos of the Video page.</summary>
    /// <param name="root">The root of the options screen.</param>
    /// <param name="host">The GUI host.</param>
    /// <param name="renderer">The renderer string of OpenGL, or null.</param>
    /// <param name="modes">The display modes.</param>
    /// <param name="windowedDefault">The device used windowed.</param>
    /// <param name="fullscreenDefault">The device used full screen.</param>
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
        for (var i = 0; i < names.Count; ++i)
            res.Entries.Add((names[i], i));
        var r = host.GetVar("$pref::VideoFullScreenRes") is { Length: > 0 } pref ? res.Entries.FindIndex(e => string.Equals(e.Text, pref, StringComparison.OrdinalIgnoreCase)) : -1;
        res.SetSelectedIndex(r < 0 ? 0 : r);
    }

    /// <summary>Adds the "Fullscreen windowed" check box to the Video page (Options.gui; the stock files stay as they are).</summary>
    /// <param name="root">The root of the options screen.</param>
    /// <param name="host">The GUI host.</param>
    /// <param name="pref">The preference variable that the check box mirrors.</param>
    /// <remarks>The box picks the borderless desktop full screen over the display mode change. It is the stock check box button (FGUniversalButton, BTN_CheckBox bitmaps, a switch mirroring the pref as TRUE or FALSE) with a label in the stock label font, on its own row under the Gamma slider. It is active only while the full screen radio (140406) is on.</remarks>
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
        foreach (var k in new[] { "fontTag", "disabledFontTag", "highlightFontTag" })
            label.F[k] = 0u;
        parent.AddChild(box);
        parent.AddChild(label);
        SyncFullscreenWindowed(root);
    }

    /// <summary>Makes the check box active only while the full screen radio is on.</summary>
    /// <param name="root">The root of the options screen.</param>
    public static void SyncFullscreenWindowed(GuiControl root)
    {
        if (root.FindById(FullscreenWindowedCheck) is GuiUniversalButton box && root.FindById(FullscreenRadio) is GuiUniversalButton fs && box.Active != fs.On)
            box.Active = fs.On;
    }

    // The entry of a saved driver name, else of the device in use, else the first.
    private static int Pick(GuiCombo combo, string pref, string current)
    {
        var i = pref.Length > 0 ? combo.FindEntryByName(pref) : -1;
        if (i < 0)
            i = combo.FindEntryByName(current);
        return i < 0 ? 0 : i;
    }
}

/// <summary>The label of "Fullscreen windowed": the stock label look (FGSimpleText, orange with a black shadow) and the dimmed grey look when its check box is inactive.</summary>
/// <remarks>Follows the FGSimpleText::draw fonts (004c9c70).</remarks>
public sealed class FullscreenWindowedLabel : GuiSimpleText
{
    /// <summary>The check box this label belongs to.</summary>
    public GuiUniversalButton? Box;
    /// <inheritdoc/>
    public override bool WantsTab => false;
    /// <inheritdoc/>
    public override bool CanTake() => false;

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        if (Host == null || Canvas == null)
            return;
        bool small = Canvas.Width < 512 || Canvas.Height < 384, on = Box?.Active ?? true;
        var main = small ? (on ? "sf_yellow_6.pft" : "sf_grey_6.pft") : on ? "sf_orange255_10.pft" : "sf_grey100_10b.pft";
        var shadow = small ? "sf_black_6.pft" : "sf_black_10.pft";
        int gx = ox + X, gy = oy + Y;
        p.Text(Host.FontByName(shadow), gx, gy + 1, Text);
        p.Text(Host.FontByName(main), gx + 1, gy, Text);
    }
}
