namespace OpenTribes.Client.Gui;

using OpenTribes.Core;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;

/// <summary>The GUI census tool: loads every <c>.gui</c> of the volumes (docs/gui.md 1.6) and lists the class census.</summary>
/// <remarks>Started with <c>tribes --root DIR --gui-census</c>.</remarks>
public static class GuiCensus
{
    /// <summary>Lists the names of all <c>.gui</c> resources, sorted.</summary>
    /// <param name="res">The mounted resources.</param>
    /// <returns>The resource names.</returns>
    public static List<string> GuiNames(ResourceManager res)
    {
        var names = new List<string>();
        for (var e = res.FindNext("gui\\*.gui", null, out var name); e != null; e = res.FindNext("gui\\*.gui", e, out name))
            names.Add(name);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>Prints the resource names that match a pattern, for example <c>*hud*</c> (<c>--gui-list PATTERN</c>).</summary>
    /// <param name="root">The game root directory.</param>
    /// <param name="pattern">The wildcard pattern.</param>
    /// <returns>The process exit code.</returns>
    public static int List(string root, string pattern)
    {
        var res = GfxAssets.Mount(root, out _);
        for (var e = res.FindNext(pattern, null, out var name); e != null; e = res.FindNext(pattern, e, out name))
            Console.WriteLine(name);
        return 0;
    }

    /// <summary>Prints the control tree of one screen with the persistent fields (<c>--gui-tree NAME</c>).</summary>
    /// <param name="root">The game root directory.</param>
    /// <param name="name">The screen name.</param>
    /// <returns>The process exit code.</returns>
    public static int Tree(string root, string name)
    {
        var res = GfxAssets.Mount(root, out _);
        if (!GfxAssets.Read(res, name.Contains('.') ? name : name + ".gui", out var data))
            return 1;
        var c = GuiLoader.Load(data, out var error);
        if (c == null)
        {
            Console.WriteLine(error);
            return 1;
        }
        static void Dump(GuiControl k, int depth)
        {
            var f = string.Join(" ", k.F.Select(kv => $"{kv.Key}={kv.Value}"));
            Console.WriteLine($"{new string(' ', depth * 2)}{k.ClassName} {k.Name} id={k.Id} ({k.X},{k.Y} {k.W}x{k.H}) fl={k.Flags:x} h/v={k.HSizing}/{k.VSizing} cmd='{k.Command}' var='{k.ConsoleVar}' {f}");
            foreach (var d in k.Children)
                Dump(d, depth + 1);
        }
        Dump(c, 0);
        return 0;
    }

    /// <summary>Loads every screen and prints the class census.</summary>
    /// <param name="root">The game root directory.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string root)
    {
        var res = GfxAssets.Mount(root, out _);
        var ok = new List<string>();
        var failed = new List<string>();
        var objects = 0;
        var classes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in GuiNames(res))
        {
            if (!res.Read(name, out var data))
            {
                failed.Add(name + " (unreadable)");
                continue;
            }
            var c = GuiLoader.Load(data, out var error);
            if (c == null)
            {
                failed.Add($"{name}: {error}");
                continue;
            }
            ok.Add(name);
            foreach (var d in c.Descendants())
            {
                ++objects;
                classes[d.ClassName] = classes.GetValueOrDefault(d.ClassName) + 1;
            }
        }
        Console.WriteLine($"loaded {ok.Count} files, {objects} objects, {classes.Count} classes");
        Console.WriteLine("loaded: " + string.Join(" ", ok.Select(n => Path.GetFileNameWithoutExtension(n))));
        Console.WriteLine($"failed {failed.Count}:");
        foreach (var f in failed)
            Console.WriteLine("  " + f);
        Console.WriteLine("classes: " + string.Join(", ", classes.Select(kv => $"{kv.Key} {kv.Value}")));
        return 0;
    }
}
