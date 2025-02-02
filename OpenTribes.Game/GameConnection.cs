// Port of src/game/game_connection.hpp and src/game/game_connection.cpp: the GameConnection (docs/game-stream.md): the
// type-0 data stream of one client. Members in stream order: event manager, PlayerPSC, ghost manager. Also the event
// builders (docs/game-stream.md 2.5) and inventoryCounts of src/game/player.cpp (its Player branches are stage P4b).
using OpenTribes.Core;
using OpenTribes.Net;
using OpenTribes.Sim;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

// src/game/game_connection.hpp struct OutEvent: one event, packed once; the body is copied into every packet that
// carries it.
public sealed class OutEvent
{
    public ushort Tag;            // class tag (0x400..0x47f)
    public bool Guaranteed = true;
    public bool Ordered = true;
    public byte[] Body = Array.Empty<byte>();
    public uint BodyBits;
    public int Seq = -1;          // ordered sequence, assigned when first written
}

// src/game/game_connection.hpp class EventWriter.
public sealed class EventWriter
{
    private readonly List<byte> mBuf = new();
    private uint mBits;

    public List<byte> Bytes() => mBuf;
    public uint Bits() => mBits;

    // src/game/game_connection.cpp EventWriter::putFlag.
    public void PutFlag(bool v) => PutInt(v ? 1u : 0u, 1);

    // src/game/game_connection.cpp EventWriter::putInt.
    public void PutInt(uint v, uint bits)
    {
        for (uint i = 0; i < bits; ++i, ++mBits)
        {
            if ((mBits >> 3) >= mBuf.Count)
                mBuf.Add(0);
            if (((v >> (int)i) & 1) != 0)
                mBuf[(int)(mBits >> 3)] |= (byte)(1u << (int)(mBits & 7));
        }
    }

    // src/game/game_connection.cpp EventWriter::putString.
    public void PutString(string s)
    {
        byte[] tmp = new byte[1024];
        var bs = new BitStream(tmp, tmp.Length);
        bs.WriteString(s);
        PutBits(tmp, bs.BitPosition());
    }

    // src/game/game_connection.cpp EventWriter::putBits.
    public void PutBits(IReadOnlyList<byte> data, uint bits)
    {
        for (uint i = 0; i < bits; ++i)
            PutInt((uint)(data[(int)(i >> 3)] >> (int)(i & 7)) & 1, 1);
    }

    // src/game/game_connection.cpp EventWriter::finish.
    public OutEvent Finish(ushort tag, bool guaranteed = true, bool ordered = true)
    {
        var e = new OutEvent { Tag = tag, Guaranteed = guaranteed, Ordered = ordered && guaranteed, Body = mBuf.ToArray(), BodyBits = mBits };
        mBuf.Clear();
        mBits = 0;
        return e;
    }
}

// src/game/game_connection.hpp struct CommandMapSlot: one slot of the PlayerPSC command map block (6.3 mode 2).
public sealed class CommandMapSlot
{
    public bool InRange;          // both coordinates fit in 11 bits; else the slot is sent as 0
    public uint X, Y;             // 1/1023 of the mission area, offset by half the area
    public uint Team;             // object team, 3 bits
    public uint Status;           // vtable +0x128 first value, 2 bits
    public uint CommandStatus;    // clients: record command status + 1
    public bool Controllable;     // targets: vtable +0x148 (a Turret that can be taken over)
    public bool Active;           // targets: vtable +0x128 second value
    public uint DbGroup, DbIndex, DbIndexBits;
}

// src/game/game_connection.hpp struct PscInput: what the server PlayerPSC block (6.3) reads from the game at each write.
public sealed class PscInput
{
    public int Team = -1;                                         // client record +0x14
    public readonly uint[] Shopping = new uint[7], Buying = new uint[7]; // item masks (PSC +0x420, +0x43c)
    public readonly uint[] Visible = new uint[8];                 // clients 0..127, then targets 0..127
    public bool LimitCommand;                                     // PSC +0x90
    public bool WaypointChanged;
    public uint WaypointX, WaypointY;
    // False: the slot is sent as 0.
    public Func<uint, CommandMapSlot, bool>? CommandSlot;
    // 200 item counts of the inventory object (PSC +0xa8), or null without one.
    public int[]? ItemCounts;
}

// src/game/game_connection.hpp struct EventDest: destination prefix of a Sim event (SimEvent_unpackDest 0x004fdd48).
public struct EventDest
{
    public enum KindT : byte
    {
        ObjectId,    // 32-bit object id
        ServerGhost, // event flag 0x20: the server object of a ghost index of this connection
        ClientGhost, // event flag 0x10: the receiver's ghost object of an index; a server has none
    }
    public KindT Kind;
    public uint Value;
}

// src/game/game_connection.hpp class GameConnection.
public sealed class GameConnection
{
    // src/game/game_connection.hpp GameConnection::Hooks.
    public sealed class Hooks
    {
        // Remote eval from the client: name is "remote" + argv[0], args start with the client id.
        public Action<string, List<string>>? RemoteCall;
        // The client's control object (client record +0x94, PSC +0xa4), or null.
        public Func<GameBase?>? ControlObject;
        // Fills the inputs of the server PSC block; called only with a control object.
        public Action<PscInput>? PscInputFn;
        // SimActionEvent (0x40b) from the client, at its dispatch (docs/game-stream.md 2.6).
        public Action<EventDest>? ActionEvent;
        // A member block of a client packet was invalid ("Invalid packet", Net_invalidPacket 0x00553630).
        public Action? InvalidPacket;
    }

    // src/game/game_connection.hpp GameConnection::Transport: the VC calls the stream makes (Connection::state,
    // Connection::send); tests supply fakes.
    public sealed class Transport
    {
        public Func<bool> Connected = () => false;
        public Func<byte[], int, uint, uint, bool> Send = (_, _, _, _) => false;
    }

    // Deltas one packet carried (PSC record, 6.4).
    private sealed class PscRecord
    {
        public bool InventoryReset;
        public readonly uint[] Shopping = new uint[7], Buying = new uint[7], Visible = new uint[8];
        public readonly List<(byte, short)> Items = new(); // inventory deltas {type, delta}
    }

    private sealed class InEvent
    {
        public bool Action;
        public EventDest Dest;
        public List<string> Args = new();
    }

    private sealed class PacketMark
    {
        public uint Number;
        public bool RateSet, RateReq;
    }

    private const int kMaxMovesPerPacket = 30; // client move buffer (00404c3c)
    private const uint kItemTypes = 200;

    private readonly int mClientId;
    private readonly Transport mVc;
    private readonly Hooks mHooks;
    private readonly LinkedList<OutEvent> mQueue = new();
    private readonly Dictionary<uint, List<OutEvent>> mInFlight = new(); // packet cookie -> guaranteed events
    private readonly Dictionary<uint, uint> mGhostTokens = new();        // packet cookie -> ghost manager packet
    private readonly Dictionary<uint, PscRecord> mPscRecords = new();    // packet cookie -> PSC deltas
    private readonly uint[] mHasShopping = new uint[7], mHasBuying = new uint[7]; // what the client holds (+0x458, +0x474)
    private readonly uint[] mHasVisible = new uint[8];                   // +0x490
    private readonly int[] mHasItems = new int[kItemTypes];              // +0xac: item counts the client holds
    private uint mCommandCursor = 0xf8;                                  // +0x3ec: -8 in the ctor
    private readonly GhostManager mGhosts;
    private uint mNextCookie = 1;
    private int mNextSeq;          // ordered send counter (+0x60)
    private readonly bool[] mSeqInFlight = new bool[128];
    private int mLowSeq;           // window low mark (+0x68)
    private int mMaxSeq;           // highest sequence written (+0x6c)
    private int mExpectedSeq;      // ordered receive (+0x64)
    private readonly SortedDictionary<int, InEvent> mPendingRecv = new();
    private uint mLastSend;
    private uint mIntervalMs = 100, mPacketBytes = 200; // active rate (+0x9bc, +0x9c0)
    private bool mRateSetDirty;    // +0x9c4
    private bool mRateReqDirty;    // +0x9d0
    private uint mPacketCount = 1, mDeliveredNo; // +0x9b4, +0x9b8
    private readonly Dictionary<uint, PacketMark> mMarks = new();
    private bool mInvalid;

    // PlayerPSC server state (6.3)
    public uint GuiMode;
    public bool Flag6a;
    public int MoveCount;          // moves received; lastMove = moveCount - 1
    public bool InventoryReset;    // +0x3dc: sent once, re-armed when the packet is lost (6.4)
    // The server's own rate prefs: 1000 / $pref::PacketRate and $pref::PacketSize (+0x9c8, +0x9cc).
    public uint OwnIntervalMs = 100, OwnPacketBytes = 200;

    // src/game/game_connection.cpp GameConnection::GameConnection (Connection form).
    public GameConnection(int clientId, Connection vc, Hooks hooks)
        : this(clientId, new Transport
        {
            Connected = () => vc.State() == ConnectionState.Connected,
            Send = (d, n, now, cookie) => vc.Send(Connection.Data, d.AsSpan(0, n), now, cookie),
        }, hooks)
    {
    }

    // src/game/game_connection.cpp GameConnection::GameConnection.
    public GameConnection(int clientId, Transport vc, Hooks hooks)
    {
        mClientId = clientId;
        mVc = vc;
        mHooks = hooks;
        mGhosts = new GhostManager(clientId);
    }

    public int ClientId() => mClientId;
    public int Queued() => mQueue.Count;
    public bool Invalid() => mInvalid;
    public GhostManager Ghosts() => mGhosts;
    public uint[] ClientVisible() => mHasVisible; // mask the client holds (+0x490)

    // src/game/game_connection.hpp GameConnection::setOwnRate: setOwnRate marks the rateReq section dirty when they
    // change (GameConnection_loadPacketPrefs 0x00550c68).
    public void SetOwnRate(uint intervalMs, uint bytes)
    {
        if (intervalMs != OwnIntervalMs || bytes != OwnPacketBytes)
        {
            OwnIntervalMs = intervalMs;
            OwnPacketBytes = bytes;
            mRateReqDirty = true;
        }
    }

    // src/game/game_connection.cpp GameConnection::post: ordered events take their sequence here (00551b7c).
    public void Post(OutEvent e)
    {
        if (e.Ordered)
            e.Seq = mNextSeq++;
        mQueue.AddLast(e);
    }

    // src/game/game_connection.cpp GameConnection::writeEvents.
    private bool WriteEvents(BitStream bs, List<OutEvent> sent)
    {
        bool any = false;
        int prev = -4; // first ordered event of a packet carries its sequence
        while (mQueue.Count != 0 && bs.BitPosition() <= mPacketBytes * 8)
        {
            OutEvent e = mQueue.First!.Value;
            // Low mark: first sequence still in flight, at most the highest written (0x00551c04).
            while (!mSeqInFlight[mLowSeq & 127] && mLowSeq < mMaxSeq)
                ++mLowSeq;
            if (e.Ordered && e.Seq > mLowSeq + 126)
                break; // window full: 127 sequences in flight
            bs.WriteFlag(true);
            bs.WriteFlag(e.Guaranteed);
            if (e.Guaranteed)
            {
                if (e.Ordered)
                {
                    bool next = e.Seq == prev + 1;
                    bs.WriteFlag(next);
                    if (!next)
                    {
                        bs.WriteFlag(true);
                        bs.WriteInt((uint)(e.Seq & 127), 7);
                    }
                    prev = e.Seq;
                    mSeqInFlight[e.Seq & 127] = true;
                    if (e.Seq > mMaxSeq)
                        mMaxSeq = e.Seq;
                }
                else
                {
                    bs.WriteFlag(false);
                    bs.WriteFlag(false);
                    prev = -2; // unordered: internal sequence -2, the next ordered one is explicit
                }
            }
            bs.WriteInt((uint)(e.Tag - 0x400), 7);
            bs.WriteBits(e.BodyBits, e.Body);
            any = true;
            if (e.Guaranteed)
                sent.Add(e);
            mQueue.RemoveFirst();
        }
        bs.WriteFlag(false);
        return any;
    }

    // src/game/game_connection.cpp GameConnection::writePsc: server PlayerPSC block (0040507c server branch). True when a
    // record was filled, which is whenever a control object exists.
    private bool WritePsc(BitStream bs, PscRecord rec)
    {
        GameBase? control = mHooks.ControlObject?.Invoke();
        bs.WriteInt(GuiMode, 3);
        bs.WriteFlag(Flag6a);
        bs.WriteInt(unchecked((uint)MoveCount - 1u), 32);
        if (!bs.WriteFlag(control != null))
            return false;
        var input = new PscInput();
        mHooks.PscInputFn?.Invoke(input);
        // The control object becomes the ghost scope object (00553048).
        mGhosts.SetScopeObject(control);
        double flash = Mul(control!.DamageFlash(), 255.0);
        bs.WriteInt(flash >= 0 && flash < 4294967296.0 ? (uint)flash : 0u, 8);
        int ghost = mGhosts.GhostIndex(control);
        uint index = ghost < 0 ? 0x3ffu : (uint)ghost;
        bs.WriteInt(index, 10);
        if (index != 0x3ff)
            control.WriteControlState(bs, new ControlContext { Ghosts = mGhosts });
        rec.InventoryReset = InventoryReset;
        if (GuiMode == 4)
        {
            // Inventory mode: XOR of the item masks against what the client holds.
            for (uint i = 0; i < 7; ++i)
            {
                rec.Shopping[i] = input.Shopping[i] ^ mHasShopping[i];
                rec.Buying[i] = input.Buying[i] ^ mHasBuying[i];
                mHasShopping[i] = input.Shopping[i];
                mHasBuying[i] = input.Buying[i];
                if (rec.Shopping[i] == 0 && rec.Buying[i] == 0)
                    continue;
                bs.WriteFlag(true);
                bs.WriteInt(i, 4);
                if (bs.WriteFlag(rec.Shopping[i] != 0))
                    bs.WriteInt(rec.Shopping[i], 32);
                if (bs.WriteFlag(rec.Buying[i] != 0))
                    bs.WriteInt(rec.Buying[i], 32);
            }
            bs.WriteFlag(false);
        }
        if (bs.WriteFlag(InventoryReset))
        {
            InventoryReset = false;
            Array.Clear(mHasItems);
        }
        // Item count deltas of the inventory object (PSC +0xa8) against what the client holds (player-sim 7.1).
        if (input.ItemCounts != null)
            for (uint t = 0; t < kItemTypes; ++t)
            {
                long delta = (long)input.ItemCounts[t] - mHasItems[t];
                if (delta == 0)
                    continue;
                // sint(10): a larger change goes out over several packets.
                delta = Math.Max(-512, Math.Min(511, delta));
                mHasItems[t] += (int)delta;
                rec.Items.Add(((byte)t, (short)delta));
                bs.WriteFlag(true);
                bs.WriteInt(t, 8);
                bs.WriteSignedInt((int)delta, 10);
            }
        bs.WriteFlag(false);
        if (GuiMode == 2)
        {
            // Command map: rows of 8 slots from a cursor that advances over all 256 slots.
            uint first = (mCommandCursor + 8) & 0xff;
            mCommandCursor = first;
            bs.WriteInt(first, 8);
            uint start = bs.BitPosition();
            do
            {
                bs.WriteFlag(true);
                for (uint k = 0; k < 8; ++k)
                {
                    uint slot = mCommandCursor + k; // the cursor is a multiple of 8: no wrap inside a row
                    var s = new CommandMapSlot();
                    // One flag: no object, not visible and out of range are all 0.
                    if (!bs.WriteFlag(input.CommandSlot != null && input.CommandSlot(slot, s) && s.InRange))
                        continue;
                    bs.WriteInt(s.X, 11);
                    bs.WriteInt(s.Y, 11);
                    bs.WriteInt(s.Team, 3);
                    bs.WriteInt(s.Status, 2);
                    if (slot < 0x80)
                    {
                        bs.WriteInt(s.CommandStatus, 3);
                    }
                    else
                    {
                        bs.WriteFlag(s.Controllable);
                        bs.WriteFlag(s.Active);
                    }
                    bs.WriteInt(s.DbGroup, 6);
                    bs.WriteInt(s.DbIndex, s.DbIndexBits);
                }
                mCommandCursor = (mCommandCursor + 8) & 0xff;
            } while ((!input.LimitCommand || bs.BitPosition() <= start + 400) && mCommandCursor != first &&
                     bs.BitPosition() <= mPacketBytes * 8);
            bs.WriteFlag(false);
        }
        else if (GuiMode == 1)
        {
            // Play: the waypoint of the client's targeted command when it moved, then the XOR of the visible-target mask
            // against what the client holds.
            if (bs.WriteFlag(input.WaypointChanged))
            {
                bs.WriteInt(input.WaypointX, 10);
                bs.WriteInt(input.WaypointY, 10);
            }
            for (uint i = 0; i < 8; ++i)
            {
                rec.Visible[i] = input.Visible[i] ^ mHasVisible[i];
                mHasVisible[i] ^= rec.Visible[i];
                if (rec.Visible[i] == 0)
                    continue;
                bs.WriteFlag(true);
                bs.WriteInt(i, 3);
                bs.WriteInt(rec.Visible[i], 32);
            }
            bs.WriteFlag(false);
        }
        return true;
    }

    // src/game/game_connection.cpp GameConnection::pscDropped: PlayerPSC packet dropped (004045e4): the deltas go back
    // into the pending state.
    private void PscDropped(PscRecord rec)
    {
        if (rec.InventoryReset)
            InventoryReset = true;
        for (int i = 0; i < 7; ++i)
        {
            mHasShopping[i] ^= rec.Shopping[i];
            mHasBuying[i] ^= rec.Buying[i];
        }
        for (int i = 0; i < 8; ++i)
            mHasVisible[i] ^= rec.Visible[i];
        foreach ((byte type, short delta) in rec.Items)
            mHasItems[type] -= delta;
    }

    // src/game/game_connection.cpp GameConnection::tick.
    public void Tick(uint nowMs)
    {
        // Pacing (0x00550d2c): the interval since the last packet, and 1000 / interval below the packets written since
        // the last delivered one. An interval of 0 (a client rateSet) divides by zero in the original; it is read as 1.
        if (!mVc.Connected() || unchecked(nowMs - mLastSend) < mIntervalMs ||
            1000u / Math.Max(mIntervalMs, 1u) < unchecked(mPacketCount - mDeliveredNo))
            return;
        byte[] buf = new byte[Connection.kMaxPacket - 32];
        var bs = new BitStream(buf, buf.Length);
        bool rateSetWas = mRateSetDirty, rateReqWas = mRateReqDirty;
        if (bs.WriteFlag(mRateSetDirty))
        {
            // rateSet: the negotiated interval and size (1.1)
            bs.WriteInt(mIntervalMs, 10);
            bs.WriteInt(mPacketBytes, 10);
            mRateSetDirty = false;
        }
        var mark = new PacketMark { Number = mPacketCount, RateSet = rateSetWas, RateReq = rateReqWas };
        if (bs.WriteFlag(mRateReqDirty))
        {
            // rateReq: the server's own interval and size (+0x9c8, +0x9cc)
            bs.WriteInt(OwnIntervalMs, 10);
            bs.WriteInt(OwnPacketBytes, 10);
            mRateReqDirty = false;
        }
        var sent = new List<OutEvent>();
        uint flagPos = bs.BitPosition();
        bs.WriteFlag(true);
        if (!WriteEvents(bs, sent))
        {
            // An absent member costs exactly one 0 bit: rewind over the end marker and clear the flag.
            bs.SetBitPosition(flagPos);
            bs.WriteFlag(false);
        }
        bs.WriteFlag(true);
        var pscRecord = new PscRecord();
        bool hasPscRecord = WritePsc(bs, pscRecord);
        flagPos = bs.BitPosition();
        bs.WriteFlag(true);
        if (!mGhosts.WritePacket(bs, mPacketBytes * 8, out uint ghostToken))
        {
            bs.SetBitPosition(flagPos);
            bs.WriteFlag(false);
        }
        uint cookie = mNextCookie++;
        if (!mVc.Send(buf, bs.BytePosition(), nowMs, cookie))
        {
            for (int i = sent.Count - 1; i >= 0; --i)
                mQueue.AddFirst(sent[i]);
            if (ghostToken != 0)
                mGhosts.Notify(ghostToken, false);
            if (hasPscRecord)
                PscDropped(pscRecord);
            mRateSetDirty |= rateSetWas;
            mRateReqDirty |= rateReqWas;
            return;
        }
        ++mPacketCount;
        mMarks[cookie] = mark;
        if (hasPscRecord)
            mPscRecords[cookie] = pscRecord;
        if (sent.Count != 0)
            mInFlight[cookie] = sent;
        if (ghostToken != 0)
            mGhostTokens[cookie] = ghostToken;
        mLastSend = nowMs;
    }

    // src/game/game_connection.cpp GameConnection::notify: 0x005508f0: a lost packet re-arms the rate sections it
    // carried; a delivered one moves the watermark.
    public void Notify(uint cookie, bool delivered)
    {
        if (mMarks.TryGetValue(cookie, out PacketMark? mk))
        {
            if (delivered)
            {
                mDeliveredNo = mk.Number;
            }
            else
            {
                mRateSetDirty |= mk.RateSet;
                mRateReqDirty |= mk.RateReq;
            }
            mMarks.Remove(cookie);
        }
        if (mGhostTokens.TryGetValue(cookie, out uint g))
        {
            mGhosts.Notify(g, delivered);
            mGhostTokens.Remove(cookie);
        }
        if (mPscRecords.TryGetValue(cookie, out PscRecord? p))
        {
            if (!delivered)
                PscDropped(p);
            mPscRecords.Remove(cookie);
        }
        if (!mInFlight.TryGetValue(cookie, out List<OutEvent>? events))
            return;
        if (delivered)
        {
            foreach (OutEvent e in events)
                if (e.Ordered)
                    mSeqInFlight[e.Seq & 127] = false;
        }
        else
        {
            // Lost: requeue at the head in original order with original sequences.
            for (int i = events.Count - 1; i >= 0; --i)
                mQueue.AddFirst(events[i]);
        }
        mInFlight.Remove(cookie);
    }

    // ---- receive ----

    // src/game/game_connection.cpp GameConnection::dispatch.
    private void Dispatch(InEvent e)
    {
        if (e.Action)
        {
            mHooks.ActionEvent?.Invoke(e.Dest);
            return;
        }
        mHooks.RemoteCall?.Invoke(e.Args[0], e.Args.GetRange(1, e.Args.Count - 1));
    }

    // src/game/game_connection.cpp GameConnection::readConsoleEvent: SimConsoleEvent unpack (0x00510118): argv[0] =
    // "remote" + string 0 (read even when argc is 0), argv[1] = the source id, then strings 1..argc-1 while the argv
    // text, each string with its NUL, stays within 0x1ff bytes. The string that crosses the limit is dropped and the rest
    // are not read.
    private List<string> ReadConsoleEvent(BitStream bs)
    {
        int argc = (int)bs.ReadInt(5);
        var args = new List<string> { "remote" + bs.ReadString(), mClientId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        int used = args[0].Length + args[1].Length + 2;
        for (int i = 0; i < argc - 1; ++i)
        {
            string s = bs.ReadString();
            used += s.Length + 1;
            if (used > 0x1ff)
                break;
            args.Add(s);
        }
        return args;
    }

    // src/game/game_connection.cpp GameConnection::readEvents: event manager read (0x00551ef8). False on "Invalid
    // packet": the rest of the packet is ignored. A server reads two classes (docs/game-stream.md 2.6): SimConsoleEvent
    // 0x408 and SimActionEvent 0x40b.
    private bool ReadEvents(BitStream bs)
    {
        int prev = -4;
        while (bs.ReadFlag())
        {
            bool guaranteed = bs.ReadFlag();
            int seq = -2; // dispatched at once: unguaranteed or guaranteed unordered
            if (guaranteed)
            {
                if (bs.ReadFlag())
                    seq = (prev + 1) & 127;
                else if (bs.ReadFlag())
                    seq = (int)bs.ReadInt(7);
                prev = seq;
            }
            uint tag = bs.ReadInt(7) + 0x400;
            var e = new InEvent();
            if (tag == 0x408)
            {
                e.Args = ReadConsoleEvent(bs);
            }
            else if (tag == 0x40b)
            {
                e.Action = true;
                if (bs.ReadFlag())
                {
                    e.Dest.Kind = EventDest.KindT.ServerGhost;
                    e.Dest.Value = bs.ReadInt(10);
                }
                else if (bs.ReadFlag())
                {
                    e.Dest.Kind = EventDest.KindT.ClientGhost;
                    e.Dest.Value = bs.ReadInt(10);
                }
                else
                {
                    e.Dest.Value = bs.ReadInt(32);
                }
            }
            else
            {
                return false;
            }
            if (seq == -2)
            {
                Dispatch(e);
                continue;
            }
            // Ordered: dispatch in sequence; others wait, keyed by sequence, until their turn.
            mPendingRecv[seq] = e;
            while (mPendingRecv.TryGetValue(mExpectedSeq, out InEvent? next))
            {
                mPendingRecv.Remove(mExpectedSeq);
                mExpectedSeq = (mExpectedSeq + 1) & 127;
                Dispatch(next);
            }
        }
        return true;
    }

    // src/game/game_connection.cpp GameConnection::readPsc: client PlayerPSC block (00405ec8 server branch): fov, first
    // move index, moves. Moves the server already has are skipped; the rest go to the control object in one call.
    private bool ReadPsc(BitStream bs)
    {
        GameBase? control = mHooks.ControlObject?.Invoke();
        uint fov = bs.ReadInt(8);
        if (control != null) // unit(8) of fov / 135 degrees, stored in radians (+0x22c)
            control.Fov = Store(Mul(Mul(Div(Div(fov, 255.0), 180.0), 3.141592653589793), 135.0));
        // The index is client data: 32-bit wrap like the original's subtraction, never overflow.
        int skip = unchecked((int)((uint)MoveCount - bs.ReadInt(32)));
        if (skip < 0)
            return false; // "Invalid packet.": the packet starts after the next expected move
        var moves = new List<Move>();
        var m = new Move();
        bool havePrev = false;
        while (!bs.Error() && bs.ReadFlag())
        {
            Moves.UnpackMove(bs, ref m, true, havePrev);
            havePrev = true;
            if (bs.Error())
                break;
            if (skip > 0)
            {
                --skip;
                continue;
            }
            // A client buffers 30 moves (6.2); more new moves in one packet are read and dropped uncounted, so the
            // client resends them (D13).
            if (moves.Count >= kMaxMovesPerPacket)
                continue;
            moves.Add(m);
            MoveCount = unchecked((int)((uint)MoveCount + 1u));
        }
        if (control != null && moves.Count != 0)
            control.ApplyMoves(moves.ToArray(), moves.Count);
        return true;
    }

    // src/game/game_connection.cpp GameConnection::readGhostBlock: GhostManager read on the server (0x00552cc8): the
    // always flag and the id width are read, then any entry is "Invalid packet" because a server accepts no ghosts.
    private static bool ReadGhostBlock(BitStream bs)
    {
        bs.ReadFlag();
        bs.ReadInt(3);
        return !bs.ReadFlag();
    }

    // src/game/game_connection.cpp GameConnection::reportInvalid: diagnostic for live client sessions (OT_LOG_INVALID=1).
    private void ReportInvalid(string stage, ReadOnlySpan<byte> data)
    {
        if (Environment.GetEnvironmentVariable("OT_LOG_INVALID") != null)
        {
            var sb = new System.Text.StringBuilder("invalid packet (" + stage + "), " + data.Length + " bytes:");
            foreach (byte b in data)
                sb.Append(' ').Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            System.Console.Error.WriteLine(sb.ToString());
        }
        mInvalid = true;
        mHooks.InvalidPacket?.Invoke();
    }

    // src/game/game_connection.cpp GameConnection::receive.
    public void Receive(ReadOnlySpan<byte> data)
    {
        if (mInvalid)
            return;
        byte[] copy = data.ToArray();
        var bs = new BitStream(copy, copy.Length);
        if (bs.ReadFlag())
        {
            // rateSet (0x005509a4): the peer's interval and size become the active ones.
            uint interval = bs.ReadInt(10);
            uint bytes = bs.ReadInt(10);
            if (!bs.Error())
            {
                mIntervalMs = interval;
                mPacketBytes = bytes;
            }
        }
        if (bs.ReadFlag())
        {
            // rateReq (docs/game-stream.md 1.3): interval = max(request, own), size = min(request, own); a change is
            // answered with rateSet in the next packet.
            uint interval = Math.Max(bs.ReadInt(10), OwnIntervalMs);
            uint bytes = Math.Min(bs.ReadInt(10), OwnPacketBytes);
            if (!bs.Error() && (interval != mIntervalMs || bytes != mPacketBytes))
            {
                mIntervalMs = interval;
                mPacketBytes = bytes;
                mRateSetDirty = true;
            }
        }
        // The members in the order of the original's list (0x005509a4): after each one an error text stops the loop, and
        // a read past the end without a text is "Invalid packet." (docs/game-stream.md 6.6).
        if (bs.ReadFlag() && !ReadEvents(bs))
        {
            ReportInvalid("events", data);
            return;
        }
        if (bs.Error())
        {
            ReportInvalid("events end", data);
            return;
        }
        if (bs.ReadFlag() && !ReadPsc(bs))
        {
            ReportInvalid("psc", data);
            return;
        }
        if (bs.Error())
        {
            ReportInvalid("psc end", data);
            return;
        }
        if (bs.ReadFlag() && !ReadGhostBlock(bs))
        {
            ReportInvalid("ghosts", data);
            return;
        }
        if (bs.Error())
            ReportInvalid("ghosts end", data);
    }
}

// The event builders of src/game/game_connection.cpp (docs/game-stream.md 2.5) and inventoryCounts of src/game/player.cpp.
public static class GameEvents
{
    // src/game/game_connection.cpp makeRemoteEval.
    public static OutEvent MakeRemoteEval(IReadOnlyList<string> argv)
    {
        var w = new EventWriter();
        w.PutInt((uint)argv.Count, 5);
        foreach (string s in argv)
            w.PutString(s);
        return w.Finish(0x408);
    }

    // src/game/game_connection.cpp makeTeamAdd.
    public static OutEvent MakeTeamAdd(int team, string name, string skin)
    {
        var w = new EventWriter();
        w.PutInt(0, 32); // +0x24: team record +4, never written on the server
        w.PutInt((uint)team, 32);
        w.PutString(name);
        w.PutString(skin);
        return w.Finish(0x44f);
    }

    // src/game/game_connection.cpp makePlayerAdd: PlayerAddEvent: a copy of the client record (docs/player-manager.md 11).
    public static OutEvent MakePlayerAdd(int clientId, bool isAI, string name, string skin, string voice, bool male, int commander, int team)
    {
        var w = new EventWriter();
        w.PutInt((uint)clientId, 32);
        w.PutFlag(isAI);
        w.PutString(name);
        w.PutString(skin);
        w.PutString(voice);
        w.PutInt(male ? 1u : 0u, 1);
        w.PutInt((uint)commander, 32);
        w.PutFlag(team != -1);
        if (team != -1)
            w.PutInt((uint)team, 3);
        return w.Finish(0x44b);
    }

    // src/game/game_connection.cpp makeDeltaScore.
    public static OutEvent MakeDeltaScore(bool isTeam, int who, int sortValue, string text)
    {
        var w = new EventWriter();
        w.PutFlag(isTeam);
        if (isTeam)
            w.PutInt((uint)(who + 1), 4);
        else
            w.PutInt((uint)(who - 2048), 7);
        w.PutInt((uint)sortValue, 32);
        w.PutString(text);
        return w.Finish(0x44d);
    }

    // src/game/game_connection.cpp makeTargetName.
    public static OutEvent MakeTargetName(uint index, string name)
    {
        var w = new EventWriter();
        w.PutInt(index, 7);
        w.PutString(name);
        return w.Finish(0x45d);
    }

    // src/game/game_connection.cpp makePathRemoved: SimPathEvent without a path: the SimPathManager (object 42) drops
    // path `index` (0052b8cc, flag 0).
    public static OutEvent MakePathRemoved(uint index)
    {
        var w = new EventWriter();
        w.PutFlag(false); // destination: object id
        w.PutFlag(false);
        w.PutInt(42, 32);
        w.PutInt(index, 32);
        w.PutFlag(false); // no path
        return w.Finish(0x425, false, false);
    }

    // src/game/game_connection.cpp makeTargetNameEvents: one TargetNameEvent per named sensor target (an unnamed target
    // sends none), in index order.
    public static List<OutEvent> MakeTargetNameEvents(IReadOnlyList<string> names)
    {
        var output = new List<OutEvent>();
        for (int i = 0; i < names.Count; ++i)
            if (names[i].Length != 0)
                output.Add(MakeTargetName((uint)i, names[i]));
        return output;
    }

    // src/game/player.cpp inventoryCounts: inventory object of a control object (0x004052c8): the object if it is a
    // Player, else the Player an ObserverCamera orbits. Its 200 item counts or null.
    public static int[]? InventoryCounts(GameBase? control)
    {
        // P4b: Player: a Player control object, or the Player an ObserverCamera orbits, answers its item counts.
        _ = control;
        return null;
    }
}
