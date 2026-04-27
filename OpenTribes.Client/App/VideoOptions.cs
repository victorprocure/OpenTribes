// The Video options lists [code 004d9228 devices, 004d946c modes]: the driver combos get one entry per display device, the resolution
// combo one per mode of the chosen full screen driver; each combo then selects the saved preference or, without one, the device in
// use. The devices are our own platform's: the software renderer and OpenGL (named with its renderer string); Glide is not
// reproduced (D31). The stock client lists the devices Windows reports, so the OpenGL entry shows another renderer text there.
using System.Globalization;
using OpenTribes.Gui;

namespace OpenTribes.Client.App;

public static class VideoOptions
{
    public const int WindowedCombo = 140400, FullscreenCombo = 140401, ResolutionCombo = 140402;

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

    // The entry of a saved driver name, else of the device in use, else the first.
    private static int Pick(GuiCombo combo, string pref, string current)
    {
        int i = pref.Length > 0 ? combo.FindEntryByName(pref) : -1;
        if (i < 0)
            i = combo.FindEntryByName(current);
        return i < 0 ? 0 : i;
    }
}
