// Port of tests/test_bitstream.cpp.
namespace OpenTribes.Tests;

using OpenTribes.Net;

public class BitStreamTests
{
    [Fact]
    public void TestBitStream()
    {
        // LSB-first packing: 1 then 0b101 (3 bits) then 0xff (8 bits) -> 0xfb 0x0f.
        var buf = new byte[512];
        var w = new BitStream(buf, buf.Length);
        w.WriteFlag(true);
        w.WriteInt(5, 3);
        w.WriteInt(0xff, 8);
        Assert.True(buf[0] == 0xfb && buf[1] == 0x0f);

        // Prefix-free codes: every symbol decodes back to itself.
        uint maxLen = 0;
        for (var c = 0; c < 256; ++c)
        {
            HuffmanCode code = Huffman.HuffmanCode((byte)c);
            Assert.True(code.Length > 0);
            maxLen = code.Length > maxLen ? code.Length : maxLen;
            var bit = 0;
            var sym = Huffman.HuffmanDecode(() => ((code.Bits >> bit++) & 1) != 0);
            Assert.True(sym == c && bit == code.Length);
        }
        Assert.True(maxLen <= 32); // the original stores codes in 32 bits

        Array.Clear(buf);
        var output = new BitStream(buf, buf.Length);
        string[] strings = ["Tribes", "1.11", "", "\u0001\u0080ÿ binary", new string('z', 300)];
        output.WriteInt(1, 16);
        foreach (var s in strings)
            output.WriteString(s);
        output.WriteSignedInt(-1234, 16);
        Assert.False(output.Error());

        var input = new BitStream(buf, buf.Length);
        Assert.Equal(1u, input.ReadInt(16));
        foreach (var s in strings)
            Assert.Equal(s[..Math.Min(s.Length, 255)], input.ReadString());
        Assert.Equal(-1234, input.ReadSignedInt(16));
        Assert.False(input.Error());

        // Common text compresses; "Tribes" is 6 chars, so compressed means < 48 bits.
        Array.Clear(buf);
        var t = new BitStream(buf, buf.Length);
        t.WriteString("Tribes");
        Assert.True(t.BitPosition() < 1 + 8 + 48 && (buf[0] & 1) != 0);

        // Reads past the end set the error flag.
        var small = new BitStream(buf, 1);
        small.ReadInt(16);
        Assert.True(small.Error());
    }
}
