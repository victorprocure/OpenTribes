// Decodes a capture file in the format tools/stream_decode.py reads: JSON lines {t, dir: c2s|s2c, client, hex}. The
// VC header is parsed, type 0 payloads are delivered once per 9 bit sequence (duplicates are reported and skipped, as
// the VC layer does) and every payload goes through the same receive path as a live session.
using System.Text.Json;
using OpenTribes.Game;

namespace OpenTribes.ClientNet;

public sealed class CaptureTotals
{
    public long Packets, Bits, Known, Unknown, Pad, Duplicates, Overruns;
    public readonly SortedDictionary<string, long> Events = new(), Ghosts = new();
}

public static class CaptureDecoder
{
    public sealed class VcHeader
    {
        public int Seq, Type;
        public byte[] Payload = Array.Empty<byte>();
    }

    // Parses the VC header (net-protocol.md 3.2): the payload starts at the next byte.
    public static VcHeader? ParseVc(byte[] data)
    {
        var bs = new OpenTribes.Net.BitStream(data, data.Length);
        bs.ReadInt(1);
        bs.ReadInt(1);
        var h = new VcHeader { Seq = (int)bs.ReadInt(9) };
        bs.ReadInt(5);
        while (bs.ReadInt(3) != 0)
        {
            bs.ReadInt(5);
            if (bs.Error())
                return null;
        }
        h.Type = (int)bs.ReadInt(5);
        if ((h.Type & ~0x18) is >= 2 and <= 5)
            bs.ReadInt(32);
        if (bs.Error())
            return null;
        int at = bs.BytePosition();
        h.Payload = at < data.Length ? data[at..] : Array.Empty<byte>();
        return h;
    }

    private sealed class ClientCtx
    {
        public readonly ClientWorld World = new();
        public readonly ClientGameConnection S2c;
        public readonly ClientGameConnection C2s;
        public readonly List<int> SeenS2c = new(), SeenC2s = new();

        public ClientCtx()
        {
            S2c = new ClientGameConnection(World);
            C2s = new ClientGameConnection(new ClientWorld());
        }
    }

    public static CaptureTotals Decode(string path, PacketDump dump, string? onlyClient, TextWriter? log)
    {
        var tot = new CaptureTotals();
        var ctxs = new Dictionary<string, ClientCtx>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0)
                continue;
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("hex", out JsonElement hexEl))
                continue;
            string client = root.GetProperty("client").GetString() ?? "";
            if (onlyClient != null && client != onlyClient)
                continue;
            string dir = root.GetProperty("dir").GetString() ?? "s2c";
            double t = root.GetProperty("t").GetDouble();
            byte[] data = Convert.FromHexString(hexEl.GetString() ?? "");
            if (data.Length == 0 || (data[0] & 1) == 0)
                continue;
            VcHeader? h = ParseVc(data);
            if (h == null)
                continue;
            int ty = h.Type;
            if ((ty & ~0x18) != 0 || h.Payload.Length == 0 || ((ty & 0x10) != 0 && (ty & 0x08) == 0))
                continue;
            if (!ctxs.TryGetValue(client, out ClientCtx? ctx))
                ctxs[client] = ctx = new ClientCtx();
            List<int> seen = dir == "c2s" ? ctx.SeenC2s : ctx.SeenS2c;
            if (seen.Contains(h.Seq))
            {
                ++tot.Duplicates;
                log?.WriteLine(t.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " " + dir + " seq=" + h.Seq + " duplicate, not delivered");
                continue;
            }
            seen.Add(h.Seq);
            if (seen.Count > 32)
                seen.RemoveAt(0);
            DecodedPacket p = (dir == "c2s" ? ctx.C2s : ctx.S2c).Receive(h.Payload, dir);
            ++tot.Packets;
            tot.Bits += h.Payload.Length * 8L;
            tot.Known += p.BitsRead;
            tot.Unknown += p.UnknownBits;
            tot.Pad += p.PaddingBits;
            if (p.Overrun)
                ++tot.Overruns;
            foreach (DecodedEvent e in p.Events)
                tot.Events[e.Name] = tot.Events.GetValueOrDefault(e.Name) + 1;
            if (p.Ghosts != null)
                foreach (DecodedGhost g in p.Ghosts.Updates)
                    if (g.Op != "delete")
                        tot.Ghosts[g.Class] = tot.Ghosts.GetValueOrDefault(g.Class) + 1;
            dump.Write(p, t, client, h.Seq);
            if (p.UnknownReason != null)
                log?.WriteLine(t.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " " + dir + " seq=" + h.Seq + " UNKNOWN " + p.UnknownReason);
        }
        return tot;
    }
}
