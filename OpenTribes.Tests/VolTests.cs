// Port of tests/test_vol.cpp.
namespace OpenTribes.Tests;

using OpenTribes.Core;

public class VolTests
{
    private static readonly string sTmp = TestPaths.TmpDir("vol");

    private static void PutU32(List<byte> b, uint v)
    {
        for (var i = 0; i < 4; ++i)
            b.Add((byte)(v >> (i * 8)));
    }

    private static void PutTag(List<byte> b, string tag) => b.AddRange(ByteString.ToBytes(tag));

    private sealed class Spec
    {
        public string? Name;     // null writes name offset -1
        public string Data = ""; // byte string
        public byte Compression;
        public uint BlockSize;   // VBLK size field: the uncompressed length of a compressed entry (0: data.size())

        public Spec(string? name, string data, byte compression, uint blockSize = 0)
        {
            Name = name;
            Data = data;
            Compression = compression;
            BlockSize = blockSize;
        }
    }

    // Builds a PVOL the way the original writer lays it out.
    private static byte[] BuildVol(List<Spec> specs, bool align4Strings)
    {
        var b = new List<byte>();
        PutTag(b, "PVOL");
        PutU32(b, 0);
        var offsets = new List<uint>();
        foreach (var s in specs)
        {
            offsets.Add((uint)b.Count);
            PutTag(b, "VBLK");
            PutU32(b, (s.BlockSize != 0 ? s.BlockSize : (uint)s.Data.Length) | 0x80000000);
            b.AddRange(ByteString.ToBytes(s.Data));
            while (b.Count % 4 != 0)
                b.Add(0);
        }
        var dir = (uint)b.Count;
        for (var i = 0; i < 4; ++i)
            b[4 + i] = (byte)(dir >> (i * 8));

        var strings = "";
        var nameOffsets = new List<uint>();
        foreach (var s in specs)
        {
            nameOffsets.Add(s.Name != null ? (uint)strings.Length : 0xffffffff);
            if (s.Name != null)
                strings += s.Name + "\0";
        }
        PutTag(b, "vols");
        PutU32(b, (uint)strings.Length | (align4Strings ? 0x80000000 : 0));
        b.AddRange(ByteString.ToBytes(strings));
        while (b.Count % (align4Strings ? 4 : 2) != 0)
            b.Add(0);

        PutTag(b, "voli");
        PutU32(b, (uint)(specs.Count * 17));
        for (var i = 0; i < specs.Count; ++i)
        {
            PutU32(b, 0);
            PutU32(b, nameOffsets[i]);
            PutU32(b, offsets[i]);
            PutU32(b, (uint)specs[i].Data.Length);
            b.Add(specs[i].Compression);
        }
        return [.. b];
    }

    private static string WriteTemp(byte[] bytes, string name)
    {
        var path = Path.Combine(sTmp, name);
        bool ok;
        try
        {
            File.WriteAllBytes(path, bytes);
            ok = true;
        }
        catch (IOException)
        {
            ok = false;
        }
        Assert.True(ok);
        return path;
    }

    private static string Str(byte[] b) => ByteString.FromBytes(b);

    private static void TestRoundTrip(bool align4)
    {
        var specs = new List<Spec>
        {
            new("Zeta.cs", "echo(\"z\");", 0),
            new("gui\\Abort.gui", "abc", 0),
            new("odd", "12345", 0),
            new(null, "unnamed", 0),
            new("packed.dat", "xx", 1),
            new("weird.dat", "xx", 2),
        };
        var path = WriteTemp(BuildVol(specs, align4), align4 ? "a4.vol" : "a2.vol");

        using var vol = new VolArchive();
        Assert.True(vol.Open(path));
        Assert.Equal(specs.Count, vol.Entries().Count);
        Assert.Empty(vol.Entries()[3].Name);

        var buf = Array.Empty<byte>();
        var e = vol.Find("zeta.CS");
        Assert.True(e != null && vol.Read(e, out buf));
        Assert.Equal("echo(\"z\");", Str(buf));

        e = vol.Find("GUI\\abort.GUI");
        Assert.True(e != null && vol.Read(e, out buf) && Str(buf) == "abc");

        e = vol.Find("odd");
        Assert.True(e != null && vol.Read(e, out buf) && Str(buf) == "12345");

        Assert.Null(vol.Find("missing"));
        Assert.Null(vol.Find("unnamed"));

        e = vol.Find("packed.dat"); // "xx" is a literal header of 120 bytes with one byte after it
        Assert.True(e != null && !vol.Read(e, out buf));
        e = vol.Find("weird.dat"); // compression type 2 does not exist
        Assert.True(e != null && !vol.Read(e, out buf));
    }

    // Encoder for the RLE stream format (docs/resources.md 7): header bit 7 = run of (header & 0x7f)
    // copies of the next byte, otherwise (header) literal bytes follow.
    private static string RleEncode(string input)
    {
        var output = new System.Text.StringBuilder();
        var i = 0;
        while (i < input.Length)
        {
            var run = 1;
            while (i + run < input.Length && input[i + run] == input[i] && run < 127)
                ++run;
            if (run >= 3)
            {
                output.Append((char)(0x80 | run));
                output.Append(input[i]);
                i += run;
                continue;
            }
            var lit = 0;
            while (i + lit < input.Length && lit < 127)
            {
                var r = 1;
                while (i + lit + r < input.Length && input[i + lit + r] == input[i + lit] && r < 3)
                    ++r;
                if (r >= 3)
                    break;
                ++lit;
            }
            output.Append((char)lit);
            output.Append(input, i, lit);
            i += lit;
        }
        return output.ToString();
    }

    private static void TestRle()
    {
        var pattern = "";
        for (var i = 0; i < 700; ++i)
            pattern += (char)('a' + (i * 7 % 5)); // no runs: literal blocks of 127
        var runs = new string('z', 300);       // 127 + 127 + 46
        runs += "ab";
        runs += new string('q', 3);
        var mixed = pattern + runs + pattern[..10] + new string('r', 127) + new string('s', 128);
        var specs = new List<Spec>();
        var plain = new List<string> { mixed, runs, new('\0', 1000) };
        for (var i = 0; i < plain.Count; ++i)
            specs.Add(new Spec(null, RleEncode(plain[i]), 1, (uint)plain[i].Length));
        // Hand-made streams: zero-length headers are skipped, a run may end the output early.
        const string skip = "\u0080A\u0000\u0000\u008a" + "k\u0002" + "hi"; // zero run (value A), two zero literals, 10 x k, "hi"
        specs.Add(new Spec(null, skip, 1, 12));
        specs.Add(new Spec(null, "\u0085y", 1, 2));             // run of 5 cut to 2
        specs.Add(new Spec(null, "\u0082x\u0003" + "ab", 1, 5)); // literal block 3 but one byte left: fails
        specs.Add(new Spec(null, "\u0082x", 1, 4));              // input ends before the output is full: fails
        specs.Add(new Spec(null, "\u0082x", 1, 2000));           // output far larger than 64x the input: fails
        specs.Add(new Spec(null, "", 1, 0));                     // empty entry
        // Literal-only data grows by one header per 127 bytes and does not fit the block window (the VBLK size
        // is the uncompressed length): the original's memory stream runs dry and the read fails.
        specs.Add(new Spec(null, RleEncode(pattern), 1, (uint)pattern.Length));
        specs.Add(new Spec(null, RleEncode("A"), 1, 1));
        string[] names = [ "m.rle", "r.rle", "z.rle", "skip.rle", "cut.rle", "short.rle", "end.rle", "big.rle", "empty.rle",
                           "pattern.rle", "one.rle" ];
        for (var i = 0; i < specs.Count; ++i)
            specs[i].Name = names[i];
        var path = WriteTemp(BuildVol(specs, false), "rle.vol");
        using var vol = new VolArchive();
        Assert.True(vol.Open(path));
        var buf = Array.Empty<byte>();
        for (var i = 0; i < plain.Count; ++i)
        {
            var pe = vol.Find(names[i]);
            Assert.True(pe != null && vol.Read(pe, out buf));
            Assert.Equal(plain[i], Str(buf));
        }
        var e = vol.Find("skip.rle");
        Assert.True(e != null && vol.Read(e, out buf) && Str(buf) == "kkkkkkkkkkhi");
        e = vol.Find("cut.rle");
        Assert.True(e != null && vol.Read(e, out buf) && Str(buf) == "yy");
        foreach (var bad in new[] { "short.rle", "end.rle", "big.rle", "pattern.rle", "one.rle" })
        {
            e = vol.Find(bad);
            Assert.True(e != null && !vol.Read(e, out buf));
        }
        e = vol.Find("empty.rle");
        Assert.True(e != null && vol.Read(e, out buf) && buf.Length == 0);

        // The decoder on its own: every prefix of a valid stream fails or yields a prefix of the text.
        var enc = ByteString.ToBytes(RleEncode(mixed));
        var output = new byte[mixed.Length];
        Assert.True(VolArchive.RleDecode(enc, output));
        for (var cut = 0; cut < enc.Length; ++cut)
            Assert.False(VolArchive.RleDecode(enc.AsSpan(0, cut), output));
    }

    private static void TestRejectsGarbage()
    {
        using var vol = new VolArchive();
        byte[] junk = [(byte)'P', (byte)'V', (byte)'O', (byte)'L', 0xe4, 0x01, 0x20, 0x20, (byte)'V', (byte)'B', (byte)'L', (byte)'K'];
        Assert.False(vol.Open(WriteTemp(junk, "junk.vol")));
        junk[0] = (byte)'X';
        Assert.False(vol.Open(WriteTemp(junk, "junk2.vol")));
    }

    [Fact]
    public void TestVol()
    {
        Assert.Equal(0, VolArchive.VolNameCompare("abc", "ABC"));
        Assert.Equal(0, VolArchive.VolNameCompare("a_", "A_"));
        Assert.NotEqual(0, VolArchive.VolNameCompare("[", "{"));
        TestRoundTrip(true);
        TestRoundTrip(false);
        TestRejectsGarbage();
        TestRle();
    }
}
