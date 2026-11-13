// The tribes-netclient pieces: the capture decoder (the format tools/stream_decode.py reads), the move script and the
// dump (ROADMAP M9 C3a, docs/client-net.md 5.6).
namespace OpenTribes.Tests;

using OpenTribes.ClientNet;
using OpenTribes.Net;

public class ClientNetToolTests
{
    // A VC packet header (net-protocol.md 3.2) of type 0 followed by the payload.
    private static byte[] Vc(int seq, byte[] payload)
    {
        var buf = new byte[payload.Length + 16];
        var bs = new BitStream(buf, buf.Length);
        bs.WriteFlag(true);
        bs.WriteInt(0, 1);
        bs.WriteInt((uint)seq, 9);
        bs.WriteInt(0, 5);
        bs.WriteInt(0, 3);
        bs.WriteInt(0, 5);
        var at = bs.BytePosition();
        payload.CopyTo(buf, at);
        return buf.AsSpan(0, at + payload.Length).ToArray();
    }

    [Fact]
    public void CaptureDecoderReadsTheStreamDecodeFormat()
    {
        // Server packet: no rate, no events, PSC mode 1 without control, no ghosts. The second line repeats sequence 5 (a
        // retransmission the VC layer does not deliver) and the third is an annotation without a hex field.
        byte[] payload = [0x20, 0x00, 0x00, 0x00, 0x00, 0x00];
        // bit 2: PSC flag; bits 3..5: mode 1 (LSB first starting at bit 3); then flag, 32 bits lastMove, flag.
        var buf = new byte[16];
        var w = new BitStream(buf, buf.Length);
        w.WriteFlag(false);
        w.WriteFlag(false);
        w.WriteFlag(false);
        w.WriteFlag(true);
        w.WriteInt(1, 3);
        w.WriteFlag(false);
        w.WriteInt(0xffffffffu, 32);
        w.WriteFlag(false);
        w.WriteFlag(false);
        payload = buf.AsSpan(0, w.BytePosition()).ToArray();
        var hex = Convert.ToHexString(Vc(5, payload)).ToLowerInvariant();
        var path = Path.Combine(TestPaths.TmpDir(""), "clientnet_capture.jsonl");
        File.WriteAllLines(path,
        [
            "{\"t\": 0.1, \"dir\": \"s2c\", \"client\": \"c1\", \"hex\": \"" + hex + "\"}",
            "{\"t\": 0.2, \"dir\": \"s2c\", \"client\": \"c1\", \"hex\": \"" + hex + "\"}",
            /*lang=json,strict*/
                                 "{\"t\": 0.3, \"dir\": \"s2c\", \"client\": \"c1\", \"note\": \"drop\", \"seq\": 6}",
            "{\"t\": 0.4, \"dir\": \"s2c\", \"client\": \"c1\", \"hex\": \"" + Convert.ToHexString(Vc(6, payload)).ToLowerInvariant() + "\"}",
        ]);
        var dumpPath = Path.Combine(TestPaths.TmpDir(""), "clientnet_capture.out.jsonl");
        CaptureTotals t;
        using (var dump = new PacketDump(dumpPath))
            t = CaptureDecoder.Decode(path, dump, null, null);
        Assert.True(t.Packets == 2 && t.Duplicates == 1 && t.Unknown == 0 && t.Overruns == 0);
        var lines = File.ReadAllLines(dumpPath);
        Assert.True(lines.Length == 2 && lines[0].Contains("\"psc\"", StringComparison.Ordinal) && lines[0].Contains("\"seq\":5", StringComparison.Ordinal));
        var h = CaptureDecoder.ParseVc(Vc(300, payload));
        Assert.True(h != null && h.Seq == 300 && h.Type == 0 && h.Payload.SequenceEqual(payload));
    }

    [Fact]
    public void MoveScriptSetsHeldKeysFromItsTimeOn()
    {
        var s = MoveScript.Parse("# comment\n0 fwd=15\n2.5 jump=1 fire=1  # inline\n\n10 spawn=1 turn=0.05\n");
        Assert.True(s.Count == 3 && s[0].At == 0 && s[0].Keys["fwd"] == 15 && s[1].At == 2.5 && s[1].Keys["fire"] == 1);
        Assert.True(s[2].Keys["spawn"] == 1 && s[2].Keys["turn"] == 0.05);
        Assert.Throws<FormatException>(() => MoveScript.Parse("1 fwd"));
    }
}
