using OpenTribes.Core;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Shapes;

/// <summary>The shape census: loads every <c>.dts</c> of the mounted volumes for rendering and steps each sequence, then reports counts and the first failures.</summary>
/// <remarks>Started with <c>tribes --shape-census</c>.</remarks>
public static class ShapeCensus
{
    /// <summary>The counts of one census run.</summary>
    public sealed class Result
    {
        /// <summary>The sequences that use an IFL.</summary>
        public int IflSequences;
        /// <summary>The triggers of all shapes.</summary>
        public int Triggers;
        /// <summary>The keys that carry a texture frame (flag 0x2000).</summary>
        public int TexKeys;
        /// <summary>The shapes with more than one material detail.</summary>
        public int MultiDetailLists;
        /// <summary>The files found.</summary>
        public int Files;
        /// <summary>The shapes that loaded.</summary>
        public int Loaded;
        /// <summary>The files that did not load.</summary>
        public int Failed;
        /// <summary>The sequences found.</summary>
        public int Sequences;
        /// <summary>The sequences stepped without an exception.</summary>
        public int Stepped;
        /// <summary>The exceptions thrown.</summary>
        public int Exceptions;
        /// <summary>The meshes of version 3.</summary>
        public int MeshesV3;
        /// <summary>The meshes of another version.</summary>
        public int MeshesOther;
        /// <summary>The type 3 materials whose bitmap is missing.</summary>
        public int MissingTextures;
        /// <summary>The shapes with a missing bitmap.</summary>
        public int ShapesWithMissing;
        /// <summary>The count of materials by flag value.</summary>
        public readonly SortedDictionary<uint, int> Flags = new();
        /// <summary>The failure messages, one per file.</summary>
        public readonly List<string> Failures = new();
        /// <summary>The names of the missing bitmaps.</summary>
        public readonly SortedSet<string> MissingNames = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Loads every shape and steps every sequence.</summary>
    /// <param name="root">The game root directory.</param>
    /// <returns>The counts.</returns>
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

    /// <summary>Runs the census and prints the counts and the first 20 failures.</summary>
    /// <param name="root">The game root directory.</param>
    /// <returns>0 when everything loaded and stepped, else 1.</returns>
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

/// <summary>The shape info tool.</summary>
public static class ShapeInfo
{
    /// <summary>Prints a text summary of one shape: details, objects with frame 0 bounds, sequences and materials (<c>tribes --shape-info NAME</c>).</summary>
    /// <param name="root">The game root directory.</param>
    /// <param name="name">The shape file name.</param>
    /// <returns>The process exit code.</returns>
    public static int Report(string root, string name)
    {
        ResourceManager res = GfxAssets.Mount(root, out _);
        if (!GfxAssets.Read(res, name.EndsWith(".dts", StringComparison.OrdinalIgnoreCase) ? name : name + ".dts", out byte[] data) ||
            RenderShape.Parse(data, name) is not RenderShape s)
        {
            Console.Error.WriteLine("shape not found: " + name);
            return 1;
        }
        Console.WriteLine($"{name} v{s.Version} radius {s.Radius:F2} center {s.Cx:F2},{s.Cy:F2},{s.Cz:F2} remap {s.RemapSize} always {s.AlwaysNode}");
        Console.WriteLine("details: " + string.Join(" ", s.Details.Select(d => $"{s.Names[s.Nodes[d.RootNode].Name]}@{d.Size}")));
        for (int i = 0; i < s.Nodes.Length; ++i)
            Console.WriteLine($"node {i} {s.Names[s.Nodes[i].Name]} parent {s.Nodes[i].Parent}");
        for (int i = 0; i < s.Objects.Length; ++i)
        {
            RsObject o = s.Objects[i];
            string bb = "";
            if (o.Mesh >= 0 && o.Mesh < s.Meshes.Length && s.Meshes[o.Mesh].Frames.Length > 0)
            {
                RsMesh m = s.Meshes[o.Mesh];
                RsFrame f = m.Frames[0];
                double[] lo = { 1e9, 1e9, 1e9 }, hi = { -1e9, -1e9, -1e9 };
                for (int k = 0; k < Math.Max(m.VertsPerFrame, 1) && (f.FirstVert + k) * 4 + 2 < m.Verts.Length; ++k)
                {
                    int p = (f.FirstVert + k) * 4;
                    double[] v = { m.Verts[p] * f.Sx + f.Ox, m.Verts[p + 1] * f.Sy + f.Oy, m.Verts[p + 2] * f.Sz + f.Oz };
                    for (int c = 0; c < 3; ++c) { lo[c] = Math.Min(lo[c], v[c]); hi[c] = Math.Max(hi[c], v[c]); }
                }
                bb = $"verts {m.VertsPerFrame} faces {m.Faces.Length} frames {m.Frames.Length} tvpf {m.TexVertsPerFrame} bbox {lo[0]:F2},{lo[1]:F2},{lo[2]:F2} .. {hi[0]:F2},{hi[1]:F2},{hi[2]:F2}";
            }
            Console.WriteLine($"object {i} {s.Names[o.Name]} node {o.Node} flags {o.Flags:x} mesh {o.Mesh} off {o.Ox:F2},{o.Oy:F2},{o.Oz:F2} {bb}");
        }
        for (int i = 0; i < s.Sequences.Length; ++i)
        {
            RsSequence q = s.Sequences[i];
            Console.WriteLine($"seq {i} {s.SequenceName(i)} cyclic {q.Cyclic} dur {q.Duration:F3} prio {q.Priority} trig {q.NumTriggers} ifl {q.NumIfl}");
        }
        for (int i = 0; i < s.Materials.Length; ++i)
            Console.WriteLine($"mat {i} flags {s.Materials[i].Flags:x} pal {s.Materials[i].PaletteIndex} rgb {s.Materials[i].R},{s.Materials[i].G},{s.Materials[i].B} {s.Materials[i].Name}");
        return 0;
    }
}
