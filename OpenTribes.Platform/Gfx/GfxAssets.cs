using OpenTribes.Core;

namespace OpenTribes.Platform.Gfx;

// docs/renderer.md 2.7: names are looked up through ResourceManager::find; the readers here do not look at
// the extension, except that `.pal` selects the RIFF reader and anything else the PL98 reader for palettes.
public static class GfxAssets
{
    // Mounts the game directory like the server's `--root` start-up: the base directory is scanned and every
    // `.vol` found there is added, so stock resources resolve by name. (The stock script start-up that mounts
    // mod, voice and skin volumes is C8.) Returns the number of volumes added.
    public static ResourceManager Mount(string root, out int volumes, string searchPath = "config;base")
    {
        var res = new ResourceManager(root);
        res.SetSearchPath(searchPath);
        volumes = 0;
        var names = new List<string>();
        for (ResourceManager.Entry? e = res.FindNext("*.vol", null, out string name); e != null;
             e = res.FindNext("*.vol", e, out name))
            names.Add(name);
        // FindNext walks the list from the back; add in the order found.
        foreach (string n in names)
            if (res.AddVolume(n))
                ++volumes;
        return res;
    }

    // A name without a directory also matches "dir\name" inside a volume (stock volumes store "gui\bg.bmp").
    public static bool Read(ResourceManager res, string name, out byte[] data)
    {
        if (res.Read(name, out data))
            return true;
        if (name.Contains('\\'))
            return false;
        ResourceManager.Entry? e = res.FindNext("*\\" + name, null, out _);
        data = Array.Empty<byte>();
        return e != null && res.Read(e, out data);
    }

    public static PbmpBitmap? LoadBitmap(ResourceManager res, string name, bool includePalette = false) =>
        Read(res, name, out byte[] data) && PbmpBitmap.TryParse(data, includePalette, out PbmpBitmap? b, out _) ? b : null;

    public static PbmaArray? LoadBitmapArray(ResourceManager res, string name, bool includePalette = false) =>
        Read(res, name, out byte[] data) && PbmaArray.TryParse(data, includePalette, out PbmaArray? a, out _) ? a : null;

    public static PaletteBank? LoadPalette(ResourceManager res, string name)
    {
        if (!Read(res, name, out byte[] data))
            return null;
        bool riff = name.EndsWith(".pal", StringComparison.OrdinalIgnoreCase);
        PaletteBank? bank;
        return (riff ? PaletteBank.TryParsePal(data, out bank, out _) : PaletteBank.TryParsePpl(data, out bank, out _))
            ? bank
            : null;
    }

    public static PftFont? LoadFont(ResourceManager res, string name) =>
        Read(res, name, out byte[] data) && PftFont.TryParse(data, out PftFont? f, out _) ? f : null;
}
