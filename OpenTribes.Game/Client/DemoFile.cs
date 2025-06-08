// `.rec` demo reader (docs/client-net.md 5.2, 5.4): header, records, the 0xff packet records fed to the same receive
// path as the network, the PSC member records (moves, actions) and the end record.
namespace OpenTribes.Game;

public enum DemoRecordKind { Packet, Move, Action, Other }

public sealed class DemoRecord
{
    public uint Time;                  // ms since base
    public DemoRecordKind Kind;
    public byte KindByte;
    public byte[] Packet = Array.Empty<byte>();
    public Move Move = new();
    public float ActionValue;
    public uint ActionId;
}

public sealed class DemoFile
{
    public uint ClientId, ServerId;
    public readonly List<DemoRecord> Records = new();
    public bool Complete;              // ended with the 0xffffffff end record
    public int Packets, Moves, Actions;

    public static DemoFile Parse(byte[] data)
    {
        var d = new DemoFile();
        int p = 0;
        uint U32() { uint v = BitConverter.ToUInt32(data, p); p += 4; return v; }
        if (data.Length < 8)
            throw new InvalidDataException("demo shorter than its header");
        d.ClientId = U32();
        d.ServerId = U32();
        while (p + 4 <= data.Length)
        {
            uint time = U32();
            if (time == 0xffffffffu)
            {
                d.Complete = true;
                break;
            }
            if (p >= data.Length)
                break;
            byte kind = data[p++];
            var rec = new DemoRecord { Time = time, KindByte = kind };
            if (kind == 0xff)
            {
                int len = BitConverter.ToUInt16(data, p);
                p += 2;
                if (p + len > data.Length)
                    throw new InvalidDataException("packet record past the end of the file");
                rec.Kind = DemoRecordKind.Packet;
                rec.Packet = data.AsSpan(p, len).ToArray();
                p += len;
                ++d.Packets;
            }
            else if (kind == 0x01)
            {
                byte sub = data[p++];
                if (sub == 1)
                {
                    // 32 byte raw Move: 4 x f32 axes, jet/crouch/jump/fire bytes, i32 useItem, f32 turn, f32 pitch.
                    rec.Kind = DemoRecordKind.Move;
                    rec.Move = new Move
                    {
                        Forward = BitConverter.ToSingle(data, p),
                        Backward = BitConverter.ToSingle(data, p + 4),
                        Left = BitConverter.ToSingle(data, p + 8),
                        Right = BitConverter.ToSingle(data, p + 12),
                        Jet = data[p + 16] != 0,
                        Crouch = data[p + 17] != 0,
                        Jump = data[p + 18] != 0,
                        Fire = data[p + 19] != 0,
                        UseItem = BitConverter.ToInt32(data, p + 20),
                        Turn = BitConverter.ToSingle(data, p + 24),
                        Pitch = BitConverter.ToSingle(data, p + 28),
                    };
                    p += 32;
                    ++d.Moves;
                }
                else if (sub == 0)
                {
                    rec.Kind = DemoRecordKind.Action;
                    rec.ActionValue = BitConverter.ToSingle(data, p);
                    rec.ActionId = BitConverter.ToUInt32(data, p + 4);
                    p += 8;
                    ++d.Actions;
                }
                else
                    throw new InvalidDataException("unknown PSC sub-kind " + sub);
            }
            else
                throw new InvalidDataException("unknown record kind 0x" + kind.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            d.Records.Add(rec);
        }
        return d;
    }

    // Playback (5.4): the header ids go to the world as the accept path would, the packet records run through the same
    // receive path as the network, moves append to the PSC buffer (no cap, 5.4), actions are returned to the caller.
    public void Play(ClientGameConnection conn, Action<DemoRecord, DecodedPacket?>? each = null)
    {
        ClientWorld w = conn.World;
        w.PlayerId = (int)ClientId;
        w.ServerId = (int)ServerId;
        foreach (DemoRecord rec in Records)
        {
            DecodedPacket? pkt = null;
            switch (rec.Kind)
            {
                case DemoRecordKind.Packet:
                    w.NetTimeMs = rec.Time;
                    pkt = conn.Receive(rec.Packet);
                    break;
                case DemoRecordKind.Move:
                    w.Psc.Moves.Add(rec.Move);
                    break;
            }
            each?.Invoke(rec, pkt);
        }
    }
}
