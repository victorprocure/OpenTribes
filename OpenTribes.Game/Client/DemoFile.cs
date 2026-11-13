// `.rec` demo reader (docs/client-net.md 5.2, 5.4): header, records, the 0xff packet records fed to the same receive
// path as the network, the PSC member records (moves, actions) and the end record.
namespace OpenTribes.Game;

/// <summary>The kind of a demo record.</summary>
public enum DemoRecordKind
{
    /// <summary>A network packet record.</summary>
    Packet,
    /// <summary>A PSC move record.</summary>
    Move,
    /// <summary>A PSC action record.</summary>
    Action,
    /// <summary>Any other record.</summary>
    Other,
}

/// <summary>One record of a demo file.</summary>
public sealed class DemoRecord
{
    /// <summary>The time in ms since the base.</summary>
    public uint Time { get; set; }
    /// <summary>The record kind.</summary>
    public DemoRecordKind Kind { get; set; }
    /// <summary>The raw kind byte.</summary>
    public byte KindByte { get; set; }
    /// <summary>The packet bytes of a packet record.</summary>
    public byte[] Packet { get; set; } = [];
    /// <summary>The move of a move record.</summary>
    public ref Move Move => ref _move;
    private Move _move = new();
    /// <summary>The value of an action record.</summary>
    public float ActionValue { get; set; }
    /// <summary>The action id of an action record.</summary>
    public uint ActionId { get; set; }
}

/// <summary>A <c>.rec</c> demo file reader (docs/client-net.md 5.2, 5.4).</summary>
/// <remarks>The records are the header, the 0xff packet records fed to the same receive path as the network, the PSC member records (moves, actions) and the end record.</remarks>
public sealed class DemoFile
{
    /// <summary>The client id of the header.</summary>
    public uint ClientId { get; set; }
    /// <summary>The server id of the header.</summary>
    public uint ServerId { get; set; }
    /// <summary>The records in file order.</summary>
    public List<DemoRecord> Records { get; } = [];
    /// <summary>True when the file ended with the 0xffffffff end record.</summary>
    public bool Complete { get; set; }
    /// <summary>The number of packet records.</summary>
    public int Packets { get; set; }
    /// <summary>The number of move records.</summary>
    public int Moves { get; set; }
    /// <summary>The number of action records.</summary>
    public int Actions { get; set; }

    /// <summary>Parses a demo file.</summary>
    /// <param name="data">The file bytes.</param>
    /// <returns>The parsed file.</returns>
    public static DemoFile Parse(byte[] data)
    {
        var d = new DemoFile();
        var p = 0;
        uint U32()
        { var v = BitConverter.ToUInt32(data, p); p += 4; return v; }
        if (data.Length < 8)
            throw new InvalidDataException("demo shorter than its header");
        d.ClientId = U32();
        d.ServerId = U32();
        while (p + 4 <= data.Length)
        {
            var time = U32();
            if (time == 0xffffffffu)
            {
                d.Complete = true;
                break;
            }
            if (p >= data.Length)
                break;
            var kind = data[p++];
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
                var sub = data[p++];
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

    /// <summary>Plays the records back (docs/client-net.md 5.4).</summary>
    /// <param name="conn">The client connection.</param>
    /// <param name="each">Called for every record with the decoded packet of a packet record.</param>
    /// <remarks>The header ids go to the world as the accept path would. The packet records run through the same receive path as the network. Moves append to the PSC buffer without a cap. Actions are returned to the caller.</remarks>
    public void Play(ClientGameConnection conn, Action<DemoRecord, DecodedPacket?>? each = null)
    {
        var w = conn.World;
        w.PlayerId = (int)ClientId;
        w.ServerId = (int)ServerId;
        foreach (var rec in Records)
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
                case DemoRecordKind.Action:
                case DemoRecordKind.Other:
                default:
                    break;
            }
            each?.Invoke(rec, pkt);
        }
    }
}
