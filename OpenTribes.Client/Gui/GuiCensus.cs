// `tribes --root DIR --gui-census`: loads every `.gui` of the volumes (docs/gui.md 1.6) and lists the class census.
using OpenTribes.Core;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Gui;

public static class GuiCensus
{
    public static List<string> GuiNames(ResourceManager res)
    {
        var names = new List<string>();
        for (ResourceManager.Entry? e = res.FindNext("gui\\*.gui", null, out string name); e != null; e = res.FindNext("gui\\*.gui", e, out name))
            names.Add(name);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    // `--gui-list PATTERN`: lists the resource names that match (for example `*hud*`).
    public static int List(string root, string pattern)
    {
        ResourceManager res = GfxAssets.Mount(root, out _);
        for (ResourceManager.Entry? e = res.FindNext(pattern, null, out string name); e != null; e = res.FindNext(pattern, e, out name))
            Console.WriteLine(name);
        return 0;
    }

    // `--gui-tree NAME`: prints the control tree with the persistent fields.
    public static int Tree(string root, string name)
    {
        ResourceManager res = GfxAssets.Mount(root, out _);
        if (!GfxAssets.Read(res, name.Contains('.') ? name : name + ".gui", out byte[] data))
            return 1;
        GuiControl c = GuiLoader.Load(data, out string error);
        if (c == null)
        {
            Console.WriteLine(error);
            return 1;
        }
        void Dump(GuiControl k, int depth)
        {
            string f = string.Join(" ", k.F.Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"{new string(' ', depth * 2)}{k.ClassName} {k.Name} id={k.Id} ({k.X},{k.Y} {k.W}x{k.H}) fl={k.Flags:x} h/v={k.HSizing}/{k.VSizing} cmd='{k.Command}' var='{k.ConsoleVar}' {f}");
            foreach (GuiControl d in k.Children)
                Dump(d, depth + 1);
        }
        Dump(c, 0);
        return 0;
    }

    public static int Run(string root)
    {
        ResourceManager res = GfxAssets.Mount(root, out _);
        var ok = new List<string>();
        var failed = new List<string>();
        int objects = 0;
        var classes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string name in GuiNames(res))
        {
            if (!res.Read(name, out byte[] data))
            {
                failed.Add(name + " (unreadable)");
                continue;
            }
            GuiControl c = GuiLoader.Load(data, out string error);
            if (c == null)
            {
                failed.Add($"{name}: {error}");
                continue;
            }
            ok.Add(name);
            foreach (GuiControl d in c.Descendants())
            {
                ++objects;
                classes[d.ClassName] = classes.GetValueOrDefault(d.ClassName) + 1;
            }
        }
        Console.WriteLine($"loaded {ok.Count} files, {objects} objects, {classes.Count} classes");
        Console.WriteLine("loaded: " + string.Join(" ", ok.Select(n => Path.GetFileNameWithoutExtension(n))));
        Console.WriteLine($"failed {failed.Count}:");
        foreach (string f in failed)
            Console.WriteLine("  " + f);
        Console.WriteLine("classes: " + string.Join(", ", classes.Select(kv => $"{kv.Key} {kv.Value}")));
        return 0;
    }
}
