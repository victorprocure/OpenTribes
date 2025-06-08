// `tribes --shape-census`: loads every `.dts` of the mounted volumes for rendering and steps each sequence (acceptance of
// ROADMAP M9 C5: every shape loads and animates). Reports counts and the first failures.
using OpenTribes.Core;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Shapes;

public static class ShapeCensus
{
    public sealed class Result
    {
        public int IflSequences, Triggers, TexKeys, MultiDetailLists, Files, Loaded, Failed, Sequences, Stepped, Exceptions, MeshesV3, MeshesOther, MissingTextures, ShapesWithMissing;
        public readonly SortedDictionary<uint, int> Flags = new();
        public readonly List<string> Failures = new();
        public readonly SortedSet<string> MissingNames = new(StringComparer.OrdinalIgnoreCase);
    }

    public static Result Run(string root)
    {
        ResourceManager res = GfxAssets.Mount(root, out _);
        var r = new Result();
        var names = new List<string>();
        for (ResourceManager.Entry? e = res.FindNext("*.dts", null, out string name); e != null; e = res.FindNext("*.dts", e, out name))
            names.Add(name);
        PaletteBank? pal = null;
        foreach (string n in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ++r.Files;
            try
            {
                if (!GfxAssets.Read(res, n, out byte[] data))
                {
                    r.Failures.Add(n + ": unreadable");
                    ++r.Failed;
                    continue;
                }
                RenderShape? s = RenderShape.Parse(data, n);
                if (s == null)
                {
                    r.Failures.Add(n + ": parse");
                    ++r.Failed;
                    continue;
                }
                ++r.Loaded;
                foreach (RsMesh m in s.Meshes)
                    if (m.Version == 3) ++r.MeshesV3; else ++r.MeshesOther;
                foreach (RsSequence q in s.Sequences)
                    if (q.NumIfl > 0) ++r.IflSequences;
                r.Triggers += s.Triggers.Length;
                foreach (RsKey k in s.Keys)
                    if ((k.Flags & 0x2000) != 0) ++r.TexKeys;
                if (s.MaterialDetails > 1) ++r.MultiDetailLists;
                foreach (RsMaterial m in s.Materials)
                    r.Flags[m.Flags] = r.Flags.GetValueOrDefault(m.Flags) + 1;
                bool miss = false;
                foreach (RsMaterial m in s.Materials)
                    if (m.Type == 3 && GfxAssets.LoadBitmap(res, m.Name) == null)
                    {
                        ++r.MissingTextures;
                        r.MissingNames.Add(m.Name);
                        miss = true;
                    }
                if (miss) ++r.ShapesWithMissing;
                var inst = new RenderInstance(s);
                for (int q = 0; q < s.Sequences.Length; ++q)
                {
                    ++r.Sequences;
                    var t = inst.AddThread(q, 0, 0);
                    var trig = new List<uint>();
                    for (int step = 0; step <= 20; ++step)
                    {
                        inst.Animate(Math.Min(step % Math.Max(s.Details.Length, 1), Math.Max(s.Details.Length - 1, 0)));
                        inst.Advance(t, Math.Max(s.Sequences[q].Duration, 0.05) / 10, trig);
                    }
                    inst.Threads.Remove(t);
                    ++r.Stepped;
                }
                _ = pal;
            }
            catch (Exception ex)
            {
                ++r.Exceptions;
                r.Failures.Add(n + ": " + ex.GetType().Name + " " + ex.Message);
            }
        }
        return r;
    }

    public static int Report(string root)
    {
        Result r = Run(root);
        Console.WriteLine($"shapes {r.Files} loaded {r.Loaded} failed {r.Failed} exceptions {r.Exceptions}");
        Console.WriteLine($"sequences {r.Sequences} stepped {r.Stepped}; meshes v3 {r.MeshesV3} other {r.MeshesOther}");
        Console.WriteLine($"type-3 materials with no bitmap {r.MissingTextures} in {r.ShapesWithMissing} shapes: {string.Join(" ", r.MissingNames)}");
        Console.WriteLine($"ifl sequences {r.IflSequences} triggers {r.Triggers} keys with texture frame {r.TexKeys} material lists with details>1 {r.MultiDetailLists}");
        Console.WriteLine("material flags " + string.Join(" ", r.Flags.Select(kv => $"{kv.Key:x}:{kv.Value}")));
        foreach (string f in r.Failures.Take(20))
            Console.WriteLine("  " + f);
        return r.Failed + r.Exceptions == 0 ? 0 : 1;
    }
}
