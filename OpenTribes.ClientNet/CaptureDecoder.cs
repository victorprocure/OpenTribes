namespace OpenTribes.ClientNet;

using System.Text.Json;
using OpenTribes.Game;

/// <summary>The totals of a capture decode.</summary>
public sealed class CaptureTotals
{
    /// <summary>The packets decoded.</summary>
    public long Packets;
    /// <summary>The payload bits seen.</summary>
    public long Bits;
    /// <summary>The bits the decoder understood.</summary>
    public long Known;
    /// <summary>The bits the decoder could not interpret.</summary>
    public long Unknown;
    /// <summary>The padding bits.</summary>
    public long Pad;
    /// <summary>The duplicate packets skipped.</summary>
    public long Duplicates;
    /// <summary>The packets that overran their payload.</summary>
    public long Overruns;
    /// <summary>The event counts by name.</summary>
    public readonly SortedDictionary<string, long> Events = [];
    /// <summary>The ghost update counts by class, deletes excluded.</summary>
    public readonly SortedDictionary<string, long> Ghosts = [];
}

/// <summary>Decodes a capture file in the format <c>tools/stream_decode.py</c> reads: JSON lines <c>{t, dir: c2s|s2c, client, hex}</c>.</summary>
/// <remarks>The VC header is parsed. Type 0 payloads are delivered once per 9 bit sequence, where duplicates are reported and skipped as the VC layer does. Every payload goes through the same receive path as a live session.</remarks>
public static class CaptureDecoder
{
    /// <summary>The parsed VC header of a packet.</summary>
    public sealed class VcHeader
    {
        /// <summary>The 9 bit sequence number.</summary>
        public int Seq;
        /// <summary>The packet type with its flags.</summary>
        public int Type;
        /// <summary>The bytes after the header.</summary>
        public byte[] Payload = [];
    }

    /// <summary>Parses the VC header (net-protocol.md 3.2). The payload starts at the next byte.</summary>
    /// <param name="data">The datagram.</param>
    /// <returns>The header, or null when it is malformed.</returns>
    public static VcHeader? ParseVc(byte[] data)
    {
        var bs = new Net.BitStream(data, data.Length);
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
        var at = bs.BytePosition();
        h.Payload = at < data.Length ? data[at..] : [];
        return h;
    }

    private sealed class ClientCtx
    {
        public readonly ClientWorld World = new();
        public readonly ClientGameConnection S2c;
        public readonly ClientGameConnection C2s;
        public readonly List<int> SeenS2c = [], SeenC2s = [];

        public ClientCtx()
        {
            S2c = new ClientGameConnection(World);
            C2s = new ClientGameConnection(new ClientWorld());
        }
    }

    /// <summary>Decodes a capture file.</summary>
    /// <param name="path">The capture file.</param>
    /// <param name="dump">Receives each decoded packet.</param>
    /// <param name="onlyClient">The client to decode, or null for all.</param>
    /// <param name="log">Receives notes about duplicates and unknown data, or null.</param>
    /// <returns>The totals.</returns>
    public static CaptureTotals Decode(string path, PacketDump dump, string? onlyClient, TextWriter? log)
    {
        var tot = new CaptureTotals();
        var ctxs = new Dictionary<string, ClientCtx>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0)
                continue;
            using JsonDocument doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("hex", out var hexEl))
                continue;
            var client = root.GetProperty("client").GetString() ?? "";
            if (onlyClient != null && client != onlyClient)
                continue;
            var dir = root.GetProperty("dir").GetString() ?? "s2c";
            var t = root.GetProperty("t").GetDouble();
            var data = Convert.FromHexString(hexEl.GetString() ?? "");
            if (data.Length == 0 || (data[0] & 1) == 0)
                continue;
            var h = ParseVc(data);
            if (h == null)
                continue;
            var ty = h.Type;
            if ((ty & ~0x18) != 0 || h.Payload.Length == 0 || ((ty & 0x10) != 0 && (ty & 0x08) == 0))
                continue;
            if (!ctxs.TryGetValue(client, out var ctx))
                ctxs[client] = ctx = new ClientCtx();
            var seen = dir == "c2s" ? ctx.SeenC2s : ctx.SeenS2c;
            if (seen.Contains(h.Seq))
            {
                ++tot.Duplicates;
                log?.WriteLine(t.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " " + dir + " seq=" + h.Seq + " duplicate, not delivered");
                continue;
            }
            seen.Add(h.Seq);
            if (seen.Count > 32)
                seen.RemoveAt(0);
            var p = (dir == "c2s" ? ctx.C2s : ctx.S2c).Receive(h.Payload, dir);
            ++tot.Packets;
            tot.Bits += h.Payload.Length * 8L;
            tot.Known += p.BitsRead;
            tot.Unknown += p.UnknownBits;
            tot.Pad += p.PaddingBits;
            if (p.Overrun)
                ++tot.Overruns;
            foreach (var e in p.Events)
                tot.Events[e.Name] = tot.Events.GetValueOrDefault(e.Name) + 1;
            if (p.Ghosts != null)
                foreach (var g in p.Ghosts.Updates)
                    if (g.Op != "delete")
                        tot.Ghosts[g.Class] = tot.Ghosts.GetValueOrDefault(g.Class) + 1;
            dump.Write(p, t, client, h.Seq);
            if (p.UnknownReason != null)
                log?.WriteLine(t.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " " + dir + " seq=" + h.Seq + " UNKNOWN " + p.UnknownReason);
        }
        return tot;
    }
}
