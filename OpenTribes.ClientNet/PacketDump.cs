// JSON lines dump of decoded packets: one object per packet with the events, the PSC block and the ghost updates, each
// with its field list (name, value) in wire order.
using System.Text;
using System.Text.Json;
using OpenTribes.Game;

namespace OpenTribes.ClientNet;

public sealed class PacketDump : IDisposable
{
    private readonly StreamWriter? mOut;
    private long mIndex;

    public PacketDump(string? path)
    {
        if (path != null)
            mOut = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = false };
    }

    public void Dispose() => mOut?.Dispose();

    public static void Value(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case long l: w.WriteNumberValue(l); break;
            case int i: w.WriteNumberValue(i); break;
            case uint u: w.WriteNumberValue(u); break;
            case bool b: w.WriteBooleanValue(b); break;
            case double d:
                if (double.IsFinite(d))
                    w.WriteNumberValue(d);
                else
                    w.WriteStringValue(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case float f: Value(w, (double)f); break;
            case string s: w.WriteStringValue(s); break;
            case System.Collections.IEnumerable e:
                w.WriteStartArray();
                foreach (object? x in e)
                    Value(w, x);
                w.WriteEndArray();
                break;
            default: w.WriteStringValue(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)); break;
        }
    }

    private static void Fields(Utf8JsonWriter w, string name, IEnumerable<DecodedField> fields)
    {
        w.WriteStartArray(name);
        foreach (DecodedField f in fields)
        {
            w.WriteStartArray();
            w.WriteStringValue(f.Name);
            Value(w, f.Value);
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    public void Write(DecodedPacket p, double t, string? client = null, long? seq = null)
    {
        if (mOut == null)
            return;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("n", mIndex++);
            w.WriteNumber("t", Math.Round(t, 4));
            w.WriteString("dir", p.Direction);
            if (client != null)
                w.WriteString("client", client);
            if (seq != null)
                w.WriteNumber("seq", seq.Value);
            w.WriteNumber("bytes", p.Bytes);
            w.WriteNumber("bits", p.BitsRead);
            w.WriteNumber("unknown", p.UnknownBits);
            w.WriteNumber("pad", p.PaddingBits);
            if (p.UnknownReason != null)
                w.WriteString("reason", p.UnknownReason);
            if (p.Overrun)
                w.WriteBoolean("overrun", true);
            if (p.RateSet is var (si, ss))
                w.WriteString("rateSet", si + "ms/" + ss);
            if (p.RateReq is var (ri, rs))
                w.WriteString("rateReq", ri + "ms/" + rs);
            w.WriteStartArray("events");
            foreach (DecodedEvent e in p.Events)
            {
                w.WriteStartObject();
                w.WriteNumber("tag", e.Tag);
                w.WriteString("name", e.Name);
                w.WriteString("mode", e.Mode);
                w.WriteNumber("bits", e.Bits);
                w.WriteString("text", e.Text);
                Fields(w, "fields", e.Fields);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (p.Psc != null)
            {
                w.WriteStartObject("psc");
                w.WriteNumber("bits", p.Psc.Bits);
                w.WriteString("text", p.Psc.Text);
                Fields(w, "fields", p.Psc.Fields);
                w.WriteEndObject();
            }
            if (p.Ghosts != null)
            {
                w.WriteStartObject("ghosts");
                w.WriteBoolean("always", p.Ghosts.Always);
                w.WriteNumber("idbits", p.Ghosts.IdBits);
                w.WriteBoolean("done", p.Ghosts.Done);
                w.WriteNumber("bits", p.Ghosts.Bits);
                w.WriteStartArray("updates");
                foreach (DecodedGhost g in p.Ghosts.Updates)
                {
                    w.WriteStartObject();
                    w.WriteNumber("i", g.Index);
                    w.WriteString("op", g.Op);
                    w.WriteString("class", g.Class);
                    if (g.ObjectValue >= 0)
                        w.WriteNumber("obj", g.ObjectValue);
                    w.WriteNumber("bits", g.Bits);
                    Fields(w, "fields", g.Fields);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        mOut.Write(Encoding.UTF8.GetString(ms.ToArray()));
        mOut.Write('\n');
    }
}
