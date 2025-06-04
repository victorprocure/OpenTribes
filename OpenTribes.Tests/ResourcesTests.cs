// Port of tests/test_resources.cpp.
using OpenTribes.Core;

namespace OpenTribes.Tests;

public class ResourcesTests
{
    private static void Put(string p, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, ByteString.ToBytes(text));
    }

    private static void PutU32(List<byte> b, uint v)
    {
        for (int i = 0; i < 4; ++i)
            b.Add((byte)(v >> (i * 8)));
    }

    private static void PutStr(List<byte> b, string s) => b.AddRange(ByteString.ToBytes(s));

    // Minimal PVOL writer, same layout as tools/mkvol.py.
    private static void PutVol(string p, (string name, string data)[] entries)
    {
        var b = new List<byte>();
        PutStr(b, "PVOL");
        PutU32(b, 0);
        var offsets = new List<uint>();
        foreach (var (_, data) in entries)
        {
            offsets.Add((uint)b.Count);
            PutStr(b, "VBLK");
            PutU32(b, (uint)data.Length | 0x80000000);
            PutStr(b, data);
            while (b.Count % 4 != 0)
                b.Add(0);
        }
        uint dir = (uint)b.Count;
        for (int i = 0; i < 4; ++i)
            b[4 + i] = (byte)(dir >> (i * 8));
        string names = "";
        var nameOffsets = new List<uint>();
        foreach (var e in entries)
        {
            nameOffsets.Add((uint)names.Length);
            names += e.name + '\0';
        }
        PutStr(b, "vols");
        PutU32(b, (uint)names.Length);
        PutStr(b, names);
        if (names.Length % 2 != 0)
            b.Add(0);
        PutStr(b, "voli");
        PutU32(b, (uint)(17 * entries.Length));
        for (int i = 0; i < entries.Length; ++i)
        {
            PutU32(b, 0);
            PutU32(b, nameOffsets[i]);
            PutU32(b, offsets[i]);
            PutU32(b, (uint)entries[i].data.Length);
            b.Add(0);
        }
        Put(p, ByteString.FromBytes(b.ToArray()));
    }

    private static string Text(ResourceManager res, string name)
    {
        if (!res.Read(name, out byte[] buf))
            return "<missing>";
        return ByteString.FromBytes(buf);
    }

    private static List<string> List(ResourceManager res, string pattern)
    {
        var output = new List<string>();
        for (var e = res.FindNext(pattern, null, out string name); e != null; e = res.FindNext(pattern, e, out name))
            output.Add(name);
        return output;
    }

    private const string kPath = "config;base;base\\missions;temp";

    [Fact]
    public void TestResources()
    {
        string root = Path.Combine(TestPaths.TmpDir("resources"), "res_root");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        Put(root + "/config/dup.cs", "config");
        Put(root + "/base/dup.cs", "base");
        Put(root + "/base/Missions/dup.cs", "missions"); // directory case differs from the search path
        Put(root + "/base/Loose.cs", "loose");
        Put(root + "/base/sub/deep.cs", "deep");
        Put(root + "/base/b.cs", "b");
        Put(root + "/base/A.cs", "A");
        PutVol(root + "/base/one.vol", new[] { ("loose.cs", "vol-one"), ("vonly.cs", "one"), ("dir\\pathed.cs", "pathed") });
        PutVol(root + "/base/two.vol", new[] { ("VONLY.cs", "two") });

        var strings = new StringTable();
        var res = new ResourceManager(root, strings);
        res.SetSearchPath(kPath);

        // RL2: earlier search directories win.
        Assert.Equal("config", Text(res, "dup.cs"));
        // RL1: case-insensitive lookup.
        Assert.Equal("loose", Text(res, "LOOSE.CS"));
        // RS4/RL6: recursive scan; subdirectory files need their relative path.
        Assert.Null(res.Find("deep.cs"));
        Assert.Equal("deep", Text(res, "sub\\deep.cs"));
        // Path-qualified with the search directory itself does not match.
        Assert.Null(res.Find("base\\dup.cs"));

        // RL3: loose beats volume. RL4: newest volume wins, unmount exposes the older one.
        Assert.True(res.AddVolume("one.vol") && res.AddVolume("two.vol"));
        Assert.Equal("loose", Text(res, "loose.cs"));
        Assert.Equal("two", Text(res, "vonly.cs"));
        res.RemoveVolume("two.vol");
        Assert.Equal("one", Text(res, "vonly.cs"));
        // RL5: pathed volume names match only with their path.
        Assert.Null(res.Find("pathed.cs"));
        Assert.Equal("pathed", Text(res, "dir\\pathed.cs"));

        // RS1: files added after a scan are invisible until the next setSearchPath.
        Put(root + "/base/later.cs", "later");
        Assert.Null(res.Find("later.cs"));
        // RS3: a new volume file cannot be mounted before a rescan.
        PutVol(root + "/base/three.vol", new[] { ("t.cs", "t") });
        Assert.False(res.AddVolume("three.vol"));
        res.SetSearchPath(kPath);
        Assert.Equal("later", Text(res, "later.cs"));
        Assert.True(res.AddVolume("three.vol"));
        // RL3 still holds after rescans: loose entries go back in front of volume entries.
        Assert.Equal("loose", Text(res, "loose.cs"));

        // OF2-OF4: newest entries first; a rescan prepends only new files.
        List<string> cs = List(res, "*.cs");
        Assert.Equal("t.cs", cs[0]); // newest: mounted last
        List<string> mission = List(res, "dup.cs");
        // base\missions scanned after base: the bare missions entry is newer than "missions\dup.cs".
        List<string> all = List(res, "*dup.cs");
        Assert.Equal(4, all.Count);
        Assert.True(all[0] == "later.cs" || all[0] == "dup.cs");
        bool sawPrefixed = false;
        foreach (string n in all)
            sawPrefixed |= n == "Missions\\dup.cs";
        Assert.True(sawPrefixed);

        // OF3: loose files in reverse case-insensitive order within a directory.
        List<string> ab = List(res, "?.cs");
        Assert.Equal(new List<string> { "t.cs", "b.cs", "A.cs" }, ab);

        // OF5/RQ2: the first spelling interned is displayed for later case variants.
        Assert.Same(strings.Lookup("VONLY.cs"), strings.Lookup("vonly.cs"));

        // Wildcard quirk: "*" needs at least one remaining character to match a trailing star.
        Assert.True(ResourceManager.WildcardMatch("*", "abc"));
        Assert.True(ResourceManager.WildcardMatch("A*.CS", "abc.cs"));
        Assert.False(ResourceManager.WildcardMatch("abc*", "abc"));
        Assert.True(ResourceManager.WildcardMatch("a?c", "abc"));

        // RS2: deleted files disappear on the next rescan.
        File.Delete(root + "/base/later.cs");
        Assert.NotNull(res.Find("later.cs"));
        res.SetSearchPath(kPath);
        Assert.Null(res.Find("later.cs"));

        // 8.7 (G42): an unmounted volume keeps the entries that hold an instance until purge.
        PutVol(root + "/base/mis.vol", new[] { ("kept.dis", "k"), ("held.dig", "h"), ("never.bmp", "n"), ("early.dil", "e") });
        PutVol(root + "/base/other.vol", new[] { ("o.cs", "o") });
        res.SetSearchPath(kPath);
        Assert.True(res.AddVolume("mis.vol") && res.AddVolume("other.vol"));
        Assert.Null(res.Load("missing.dis"));
        ResourceManager.Entry? early = res.Load("early.dil");
        Assert.True(early != null && early.Locks == 1 && early.HasInstance);
        ResourceManager.Unlock(early!);
        res.Purge(); // mounted: the unlocked instance goes, the entry stays
        Assert.True(res.Find("early.dil") == early && !early!.HasInstance);
        ResourceManager.Entry kept = res.Load("kept.dis")!;
        ResourceManager.Unlock(kept); // its object is gone, the instance waits for purge
        {
            var holder = new ResourceLocks(); // an object that still holds its resource
            Assert.True(holder.Load(res, "held.dig") && !holder.Load(res, "missing.dig"));
            res.RemoveVolume("mis.vol");
            Assert.Equal(new List<string> { "held.dig", "kept.dis" }, List(res, "*.d??"));
            Assert.True(res.Find("never.bmp") == null && res.Find("early.dil") == null);
            Assert.True(res.Find("kept.dis")!.Flags == ResourceManager.Stale && Text(res, "kept.dis") == "<missing>");
            // A load of a stale entry returns its instance.
            ResourceManager.Entry? again = res.Load("kept.dis");
            Assert.True(again == kept && kept.Locks == 1);
            ResourceManager.Unlock(again!);
            // Remounting reuses the kept entries in place; the others are created in front.
            Assert.True(res.AddVolume("mis.vol"));
            List<string> order = List(res, "*");
            int At(string n) => order.IndexOf(n);
            Assert.True(At("early.dil") < At("never.bmp") && At("never.bmp") < At("o.cs") && At("o.cs") < At("held.dig") &&
                        At("held.dig") < At("kept.dis") && At("early.dil") >= 0);
            Assert.True(Text(res, "kept.dis") == "k" && res.Find("kept.dis") == kept);
            res.RemoveVolume("mis.vol");
            res.Purge(); // frees the unlocked stale entry, keeps the locked one
            Assert.True(res.Find("kept.dis") == null && res.Find("held.dig") != null);
            holder.Release(); // the C++ holder's destructor at the end of the block
        }
        res.Purge(); // the holder is gone
        Assert.Null(res.Find("held.dig"));
        // Generated entries are locked by their creator and survive purge.
        Assert.True(res.AddGenerated("gen.sae"));
        res.Purge();
        Assert.NotNull(res.Find("gen.sae"));
        // A loaded loose file deleted from disk keeps its entry after a rescan until purge.
        Put(root + "/base/gone.cs", "gone");
        res.SetSearchPath(kPath);
        ResourceManager.Unlock(res.Load("gone.cs")!);
        File.Delete(root + "/base/gone.cs");
        res.SetSearchPath(kPath);
        Assert.True(res.Find("gone.cs") != null && res.Find("gone.cs")!.Flags == ResourceManager.Stale);
        res.Purge();
        Assert.Null(res.Find("gone.cs"));
        res.RemoveVolume("other.vol");

        // Unsafe search path elements are ignored.
        var unsafePaths = new ResourceManager(root, strings);
        unsafePaths.SetSearchPath("..\\x;base\\.;c:\\x;\\base;base");
        Assert.Equal("base", Text(unsafePaths, "dup.cs"));

        _ = mission;
    }
}
