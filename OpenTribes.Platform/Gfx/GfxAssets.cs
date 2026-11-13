using OpenTribes.Core;

namespace OpenTribes.Platform.Gfx;

/// <summary>Loads graphics assets through the resource manager (docs/renderer.md 2.7).</summary>
/// <remarks>Names are looked up through ResourceManager::find. The readers do not look at the extension, except that <c>.pal</c> selects the RIFF reader and anything else the PL98 reader for palettes.</remarks>
public static class GfxAssets
{
    /// <summary>Mounts the game directory like the server's <c>--root</c> start-up.</summary>
    /// <param name="root">The game directory.</param>
    /// <param name="volumes">Receives the number of volumes added.</param>
    /// <param name="searchPath">The search path.</param>
    /// <returns>The resource manager. The base directory is scanned and every <c>.vol</c> found there is added, so stock resources resolve by name.</returns>
    /// <remarks>The stock script start-up that mounts mod, voice and skin volumes is C8.</remarks>
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

    /// <summary>Reads a resource. A name without a directory also matches "dir\name" inside a volume, since stock volumes store "gui\bg.bmp".</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="data">Receives the bytes.</param>
    /// <returns>False when the resource is missing.</returns>
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

    /// <summary>Loads a bitmap.</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="includePalette">Whether to keep the palette.</param>
    /// <returns>The bitmap, or null.</returns>
    public static PbmpBitmap? LoadBitmap(ResourceManager res, string name, bool includePalette = false) =>
        Read(res, name, out byte[] data) && PbmpBitmap.TryParse(data, includePalette, out PbmpBitmap? b, out _) ? b : null;

    /// <summary>Loads a bitmap array.</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="includePalette">Whether to keep the palette.</param>
    /// <returns>The array, or null.</returns>
    public static PbmaArray? LoadBitmapArray(ResourceManager res, string name, bool includePalette = false) =>
        Read(res, name, out byte[] data) && PbmaArray.TryParse(data, includePalette, out PbmaArray? a, out _) ? a : null;

    /// <summary>Loads a palette.</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="name">The resource name.</param>
    /// <returns>The palette bank, or null.</returns>
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

    /// <summary>Loads a font.</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="name">The resource name.</param>
    /// <returns>The font, or null.</returns>
    public static PftFont? LoadFont(ResourceManager res, string name) =>
        Read(res, name, out byte[] data) && PftFont.TryParse(data, out PftFont? f, out _) ? f : null;
}
