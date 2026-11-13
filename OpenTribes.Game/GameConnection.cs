// Port of src/game/game_connection.hpp and src/game/game_connection.cpp: the GameConnection (docs/game-stream.md): the
// type-0 data stream of one client. Members in stream order: event manager, PlayerPSC, ghost manager. Also the event
// builders (docs/game-stream.md 2.5); inventoryCounts of src/game/player.cpp is Player.InventoryCounts.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>One event, packed once. The body is copied into every packet that carries it.</summary>
public sealed class OutEvent
{
    /// <summary>The class tag (0x400 to 0x47f).</summary>
    public ushort Tag;
    /// <summary>True when the event is delivered guaranteed.</summary>
    public bool Guaranteed = true;
    /// <summary>True when the event is delivered in order.</summary>
    public bool Ordered = true;
    /// <summary>The packed body bytes.</summary>
    public byte[] Body = [];
    /// <summary>The length of the body in bits.</summary>
    public uint BodyBits;
    /// <summary>The ordered sequence number, assigned when the event is first written.</summary>
    public int Seq = -1;
}

/// <summary>Packs an event body bit by bit.</summary>
public sealed class EventWriter
{
    private readonly List<byte> mBuf = [];
    private uint mBits;

    /// <summary>Returns the packed bytes.</summary>
    /// <returns>The buffer, not a copy.</returns>
    public List<byte> Bytes() => mBuf;
    /// <summary>Returns the number of bits written.</summary>
    /// <returns>The bit count.</returns>
    public uint Bits() => mBits;

    /// <summary>Writes a one-bit flag.</summary>
    /// <param name="v">The flag.</param>
    public void PutFlag(bool v) => PutInt(v ? 1u : 0u, 1);

    /// <summary>Writes an unsigned value.</summary>
    /// <param name="v">The value.</param>
    /// <param name="bits">The number of bits.</param>
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

    /// <summary>Writes a string.</summary>
    /// <param name="s">The string.</param>
    public void PutString(string s)
    {
        var tmp = new byte[1024];
        var bs = new BitStream(tmp, tmp.Length);
        bs.WriteString(s);
        PutBits(tmp, bs.BitPosition());
    }

    /// <summary>Writes bits from a byte buffer.</summary>
    /// <param name="data">The source bytes.</param>
    /// <param name="bits">The number of bits to copy.</param>
    public void PutBits(IReadOnlyList<byte> data, uint bits)
    {
        for (uint i = 0; i < bits; ++i)
            PutInt((uint)(data[(int)(i >> 3)] >> (int)(i & 7)) & 1, 1);
    }

    /// <summary>Ends the body and builds the event.</summary>
    /// <param name="tag">The class tag.</param>
    /// <param name="guaranteed">True for guaranteed delivery.</param>
    /// <param name="ordered">True for ordered delivery.</param>
    /// <returns>The event.</returns>
    public OutEvent Finish(ushort tag, bool guaranteed = true, bool ordered = true)
    {
        var e = new OutEvent { Tag = tag, Guaranteed = guaranteed, Ordered = ordered && guaranteed, Body = [.. mBuf], BodyBits = mBits };
        mBuf.Clear();
        mBits = 0;
        return e;
    }
}

/// <summary>One slot of the PlayerPSC command map block (docs/game-stream.md 6.3 mode 2).</summary>
public sealed class CommandMapSlot
{
    /// <summary>True when both coordinates fit in 11 bits. Otherwise the slot is sent as 0.</summary>
    public bool InRange;
    /// <summary>The x position, in 1/1023 of the mission area, offset by half the area.</summary>
    public uint X;
    /// <summary>The y position, in 1/1023 of the mission area, offset by half the area.</summary>
    public uint Y;
    /// <summary>The object team, 3 bits.</summary>
    public uint Team;
    /// <summary>The first status value of vtable +0x128, 2 bits.</summary>
    public uint Status;
    /// <summary>For clients: the record command status plus 1.</summary>
    public uint CommandStatus;
    /// <summary>For targets: whether the target can be taken over, as a Turret (vtable +0x148).</summary>
    public bool Controllable;
    /// <summary>For targets: the second status value of vtable +0x128.</summary>
    public bool Active;
    /// <summary>The datablock group of the target.</summary>
    public uint DbGroup;
    /// <summary>The datablock index of the target.</summary>
    public uint DbIndex;
    /// <summary>The number of bits of the datablock index.</summary>
    public uint DbIndexBits;
}

/// <summary>What the server PlayerPSC block (docs/game-stream.md 6.3) reads from the game at each write.</summary>
public sealed class PscInput
{
    /// <summary>The team of the client (client record +0x14).</summary>
    public int Team = -1;
    /// <summary>The shopping item mask (PSC +0x420).</summary>
    public readonly uint[] Shopping = new uint[7];
    /// <summary>The buying item mask (PSC +0x43c).</summary>
    public readonly uint[] Buying = new uint[7];
    /// <summary>The visibility mask: clients 0 to 127, then targets 0 to 127.</summary>
    public readonly uint[] Visible = new uint[8];
    /// <summary>The command limit flag (PSC +0x90).</summary>
    public bool LimitCommand;
    /// <summary>True when the waypoint changed.</summary>
    public bool WaypointChanged;
    /// <summary>The waypoint x.</summary>
    public uint WaypointX;
    /// <summary>The waypoint y.</summary>
    public uint WaypointY;
    /// <summary>Fills a command map slot by index. Returning false sends the slot as 0.</summary>
    public Func<uint, CommandMapSlot, bool>? CommandSlot;
    /// <summary>The 200 item counts of the inventory object (PSC +0xa8), or null without one.</summary>
    public int[]? ItemCounts;
}

/// <summary>The destination prefix of a Sim event.</summary>
/// <remarks>Follows SimEvent_unpackDest (0x004fdd48).</remarks>
public struct EventDest
{
    /// <summary>The kinds of event destination.</summary>
    public enum KindT : byte
    {
        /// <summary>A 32-bit object id.</summary>
        ObjectId,
        /// <summary>The server object of a ghost index of this connection (event flag 0x20).</summary>
        ServerGhost,
        /// <summary>The receiver's ghost object of an index (event flag 0x10). A server has none.</summary>
        ClientGhost,
    }
    /// <summary>The destination kind.</summary>
    public KindT Kind;
    /// <summary>The object id or ghost index.</summary>
    public uint Value;
}

/// <summary>The server side of one client connection: the event queue, the ghost block and the PlayerPSC block of each packet.</summary>
/// <remarks>Port of class GameConnection in src/game/game_connection.hpp.</remarks>
public sealed class GameConnection
{
    /// <summary>The callbacks into the game that a connection needs.</summary>
    public sealed class Hooks
    {
        /// <summary>Runs a remote eval from the client. The name is "remote" plus argv[0], and the arguments start with the client id.</summary>
        public Action<string, List<string>>? RemoteCall;
        /// <summary>Returns the client's control object (client record +0x94, PSC +0xa4), or null.</summary>
        public Func<GameBase?>? ControlObject;
        /// <summary>Fills the inputs of the server PSC block. Called only with a control object.</summary>
        public Action<PscInput>? PscInputFn;
        /// <summary>Handles a SimActionEvent (0x40b) from the client at its dispatch (docs/game-stream.md 2.6).</summary>
        public Action<EventDest>? ActionEvent;
        /// <summary>Called when a member block of a client packet was invalid ("Invalid packet", Net_invalidPacket 0x00553630).</summary>
        public Action? InvalidPacket;
    }

    /// <summary>The calls the stream makes on the connection: state and send. Tests supply fakes.</summary>
    /// <remarks>Mirrors Connection::state and Connection::send.</remarks>
    public sealed class Transport
    {
        /// <summary>Returns whether the connection is up.</summary>
        public Func<bool> Connected = () => false;
        /// <summary>Sends a packet: the buffer, its length in bytes, the time in milliseconds and the cookie. Returns false when it was not sent.</summary>
        public Func<byte[], int, uint, uint, bool> Send = (_, _, _, _) => false;
    }

    // Deltas one packet carried (PSC record, 6.4).
    private sealed class PscRecord
    {
        public bool InventoryReset;
        public readonly uint[] Shopping = new uint[7], Buying = new uint[7], Visible = new uint[8];
        public readonly List<(byte, short)> Items = []; // inventory deltas {type, delta}
    }

    private sealed class InEvent
    {
        public bool Action;
        public EventDest Dest;
        public List<string> Args = [];
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
    private readonly Dictionary<uint, List<OutEvent>> mInFlight = []; // packet cookie -> guaranteed events
    private readonly Dictionary<uint, uint> mGhostTokens = [];        // packet cookie -> ghost manager packet
    private readonly Dictionary<uint, PscRecord> mPscRecords = [];    // packet cookie -> PSC deltas
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
    private readonly SortedDictionary<int, InEvent> mPendingRecv = [];
    private uint mLastSend;
    private uint mIntervalMs = 100, mPacketBytes = 200; // active rate (+0x9bc, +0x9c0)
    private bool mRateSetDirty;    // +0x9c4
    private bool mRateReqDirty;    // +0x9d0
    private uint mPacketCount = 1, mDeliveredNo; // +0x9b4, +0x9b8
    private readonly Dictionary<uint, PacketMark> mMarks = [];
    private bool mInvalid;

    /// <summary>The GUI mode of the PlayerPSC state (docs/game-stream.md 6.3).</summary>
    public uint GuiMode;
    /// <summary>The PlayerPSC flag at +0x6a.</summary>
    public bool Flag6a;
    /// <summary>The number of moves received. The last move is <c>MoveCount - 1</c>.</summary>
    public int MoveCount;
    /// <summary>True when the inventory is sent again (+0x3dc). It is sent once and re-armed when the packet is lost (docs/game-stream.md 6.4).</summary>
    public bool InventoryReset;
    /// <summary>The server's own packet interval in milliseconds (1000 / <c>$pref::PacketRate</c>, +0x9c8).</summary>
    public uint OwnIntervalMs = 100;
    /// <summary>The server's own packet size in bytes (<c>$pref::PacketSize</c>, +0x9cc).</summary>
    public uint OwnPacketBytes = 200;

    /// <summary>Creates a connection over a stream connection.</summary>
    /// <param name="clientId">The client id.</param>
    /// <param name="vc">The stream connection.</param>
    /// <param name="hooks">The callbacks into the game.</param>
    public GameConnection(int clientId, Connection vc, Hooks hooks)
        : this(clientId, new Transport
        {
            Connected = () => vc.State() == ConnectionState.Connected,
            Send = (d, n, now, cookie) => vc.Send(Connection.Data, d.AsSpan(0, n), now, cookie),
        }, hooks)
    {
    }

    /// <summary>Creates a connection over a transport.</summary>
    /// <param name="clientId">The client id.</param>
    /// <param name="vc">The transport.</param>
    /// <param name="hooks">The callbacks into the game.</param>
    public GameConnection(int clientId, Transport vc, Hooks hooks)
    {
        mClientId = clientId;
        mVc = vc;
        mHooks = hooks;
        mGhosts = new GhostManager(clientId);
    }

    /// <summary>Returns the client id.</summary>
    /// <returns>The client id.</returns>
    public int ClientId() => mClientId;
    /// <summary>Returns the number of queued events.</summary>
    /// <returns>The queue length.</returns>
    public int Queued() => mQueue.Count;
    /// <summary>Tells whether the connection received an invalid packet.</summary>
    /// <returns>True after an invalid packet.</returns>
    public bool Invalid() => mInvalid;
    /// <summary>Returns the ghost manager of the connection.</summary>
    /// <returns>The ghost manager.</returns>
    public GhostManager Ghosts() => mGhosts;
    /// <summary>Returns the visibility mask the client holds (+0x490).</summary>
    /// <returns>The mask words.</returns>
    public uint[] ClientVisible() => mHasVisible;

    /// <summary>Sets the server's own packet rate. The rateReq section is marked dirty when the values change.</summary>
    /// <param name="intervalMs">The packet interval in milliseconds.</param>
    /// <param name="bytes">The packet size in bytes.</param>
    /// <remarks>Follows GameConnection_loadPacketPrefs (0x00550c68).</remarks>
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

    /// <summary>Queues an event. Ordered events take their sequence here.</summary>
    /// <param name="e">The event.</param>
    /// <remarks>Follows 00551b7c.</remarks>
    public void Post(OutEvent e)
    {
        if (e.Ordered)
            e.Seq = mNextSeq++;
        mQueue.AddLast(e);
    }

    // src/game/game_connection.cpp GameConnection::writeEvents.
    private bool WriteEvents(BitStream bs, List<OutEvent> sent)
    {
        var any = false;
        var prev = -4; // first ordered event of a packet carries its sequence
        while (mQueue.Count != 0 && bs.BitPosition() <= mPacketBytes * 8)
        {
            var e = mQueue.First!.Value;
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
                    var next = e.Seq == prev + 1;
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
        var control = mHooks.ControlObject?.Invoke();
        bs.WriteInt(GuiMode, 3);
        bs.WriteFlag(Flag6a);
        bs.WriteInt(unchecked((uint)MoveCount - 1u), 32);
        if (!bs.WriteFlag(control != null))
            return false;
        var input = new PscInput();
        mHooks.PscInputFn?.Invoke(input);
        // The control object becomes the ghost scope object (00553048).
        mGhosts.SetScopeObject(control);
        var flash = Mul(control!.DamageFlash(), 255.0);
        bs.WriteInt(flash is >= 0 and < 4294967296.0 ? (uint)flash : 0u, 8);
        var ghost = mGhosts.GhostIndex(control);
        var index = ghost < 0 ? 0x3ffu : (uint)ghost;
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
                var delta = (long)input.ItemCounts[t] - mHasItems[t];
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
            var first = (mCommandCursor + 8) & 0xff;
            mCommandCursor = first;
            bs.WriteInt(first, 8);
            var start = bs.BitPosition();
            do
            {
                bs.WriteFlag(true);
                for (uint k = 0; k < 8; ++k)
                {
                    var slot = mCommandCursor + k; // the cursor is a multiple of 8: no wrap inside a row
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
        for (var i = 0; i < 7; ++i)
        {
            mHasShopping[i] ^= rec.Shopping[i];
            mHasBuying[i] ^= rec.Buying[i];
        }
        for (var i = 0; i < 8; ++i)
            mHasVisible[i] ^= rec.Visible[i];
        foreach ((var type, var delta) in rec.Items)
            mHasItems[type] -= delta;
    }

    /// <summary>Writes a packet when the send interval has passed.</summary>
    /// <param name="nowMs">The current time in milliseconds.</param>
    public void Tick(uint nowMs)
    {
        // Pacing (0x00550d2c): the interval since the last packet, and 1000 / interval below the packets written since
        // the last delivered one. An interval of 0 (a client rateSet) divides by zero in the original; it is read as 1.
        if (!mVc.Connected() || unchecked(nowMs - mLastSend) < mIntervalMs ||
            1000u / Math.Max(mIntervalMs, 1u) < unchecked(mPacketCount - mDeliveredNo))
            return;
        var buf = new byte[Connection.kMaxPacket - 32];
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
        var flagPos = bs.BitPosition();
        bs.WriteFlag(true);
        if (!WriteEvents(bs, sent))
        {
            // An absent member costs exactly one 0 bit: rewind over the end marker and clear the flag.
            bs.SetBitPosition(flagPos);
            bs.WriteFlag(false);
        }
        bs.WriteFlag(true);
        var pscRecord = new PscRecord();
        var hasPscRecord = WritePsc(bs, pscRecord);
        flagPos = bs.BitPosition();
        bs.WriteFlag(true);
        if (!mGhosts.WritePacket(bs, mPacketBytes * 8, out var ghostToken))
        {
            bs.SetBitPosition(flagPos);
            bs.WriteFlag(false);
        }
        var cookie = mNextCookie++;
        if (!mVc.Send(buf, bs.BytePosition(), nowMs, cookie))
        {
            for (var i = sent.Count - 1; i >= 0; --i)
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

    /// <summary>Reports whether a packet was delivered.</summary>
    /// <param name="cookie">The cookie of the packet.</param>
    /// <param name="delivered">True when it was delivered.</param>
    /// <remarks>Follows 0x005508f0. A lost packet re-arms the rate sections it carried. A delivered one moves the watermark.</remarks>
    // carried; a delivered one moves the watermark.
    public void Notify(uint cookie, bool delivered)
    {
        if (mMarks.TryGetValue(cookie, out var mk))
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
        if (mGhostTokens.TryGetValue(cookie, out var g))
        {
            mGhosts.Notify(g, delivered);
            mGhostTokens.Remove(cookie);
        }
        if (mPscRecords.TryGetValue(cookie, out var p))
        {
            if (!delivered)
                PscDropped(p);
            mPscRecords.Remove(cookie);
        }
        if (!mInFlight.TryGetValue(cookie, out var events))
            return;
        if (delivered)
        {
            foreach (var e in events)
                if (e.Ordered)
                    mSeqInFlight[e.Seq & 127] = false;
        }
        else
        {
            // Lost: requeue at the head in original order with original sequences.
            for (var i = events.Count - 1; i >= 0; --i)
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
        var argc = (int)bs.ReadInt(5);
        var args = new List<string> { "remote" + bs.ReadString(), mClientId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var used = args[0].Length + args[1].Length + 2;
        for (var i = 0; i < argc - 1; ++i)
        {
            var s = bs.ReadString();
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
        var prev = -4;
        while (bs.ReadFlag())
        {
            var guaranteed = bs.ReadFlag();
            var seq = -2; // dispatched at once: unguaranteed or guaranteed unordered
            if (guaranteed)
            {
                if (bs.ReadFlag())
                    seq = (prev + 1) & 127;
                else if (bs.ReadFlag())
                    seq = (int)bs.ReadInt(7);
                prev = seq;
            }
            var tag = bs.ReadInt(7) + 0x400;
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
            while (mPendingRecv.TryGetValue(mExpectedSeq, out var next))
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
        var control = mHooks.ControlObject?.Invoke();
        var fov = bs.ReadInt(8);
        control?.Fov = Store(Mul(Mul(Div(Div(fov, 255.0), 180.0), 3.141592653589793), 135.0));
        // The index is client data: 32-bit wrap like the original's subtraction, never overflow.
        var skip = unchecked((int)((uint)MoveCount - bs.ReadInt(32)));
        if (skip < 0)
            return false; // "Invalid packet.": the packet starts after the next expected move
        var moves = new List<Move>();
        var m = new Move();
        var havePrev = false;
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
            control.ApplyMoves([.. moves], moves.Count);
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
            foreach (var b in data)
                sb.Append(' ').Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            Console.Error.WriteLine(sb.ToString());
        }
        mInvalid = true;
        mHooks.InvalidPacket?.Invoke();
    }

    /// <summary>Reads a client packet.</summary>
    /// <param name="data">The packet data.</param>
    public void Receive(ReadOnlySpan<byte> data)
    {
        if (mInvalid)
            return;
        var copy = data.ToArray();
        var bs = new BitStream(copy, copy.Length);
        if (bs.ReadFlag())
        {
            // rateSet (0x005509a4): the peer's interval and size become the active ones.
            var interval = bs.ReadInt(10);
            var bytes = bs.ReadInt(10);
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
            var interval = Math.Max(bs.ReadInt(10), OwnIntervalMs);
            var bytes = Math.Min(bs.ReadInt(10), OwnPacketBytes);
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

/// <summary>The builders of the game events (docs/game-stream.md 2.5).</summary>
public static class GameEvents
{
    /// <summary>Builds a remote eval event.</summary>
    /// <param name="argv">The command arguments.</param>
    /// <returns>The event.</returns>
    public static OutEvent MakeRemoteEval(IReadOnlyList<string> argv)
    {
        var w = new EventWriter();
        w.PutInt((uint)argv.Count, 5);
        foreach (var s in argv)
            w.PutString(s);
        return w.Finish(0x408);
    }

    /// <summary>Builds a team add event.</summary>
    /// <param name="team">The team id.</param>
    /// <param name="name">The team name.</param>
    /// <param name="skin">The team skin.</param>
    /// <returns>The event.</returns>
    public static OutEvent MakeTeamAdd(int team, string name, string skin)
    {
        var w = new EventWriter();
        w.PutInt(0, 32); // +0x24: team record +4, never written on the server
        w.PutInt((uint)team, 32);
        w.PutString(name);
        w.PutString(skin);
        return w.Finish(0x44f);
    }

    /// <summary>Builds a PlayerAddEvent, a copy of the client record (docs/player-manager.md 11).</summary>
    /// <param name="clientId">The client id.</param>
    /// <param name="isAI">True for an AI player.</param>
    /// <param name="name">The player name.</param>
    /// <param name="skin">The skin.</param>
    /// <param name="voice">The voice set.</param>
    /// <param name="male">True for a male player.</param>
    /// <param name="commander">The commander id.</param>
    /// <param name="team">The team id.</param>
    /// <returns>The event.</returns>
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

    /// <summary>Builds a delta score event.</summary>
    /// <param name="isTeam">True for a team score, false for a client score.</param>
    /// <param name="who">The team or client id.</param>
    /// <param name="sortValue">The sort value.</param>
    /// <param name="text">The score text.</param>
    /// <returns>The event.</returns>
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

    /// <summary>Builds a target name event.</summary>
    /// <param name="index">The target index.</param>
    /// <param name="name">The target name.</param>
    /// <returns>The event.</returns>
    public static OutEvent MakeTargetName(uint index, string name)
    {
        var w = new EventWriter();
        w.PutInt(index, 7);
        w.PutString(name);
        return w.Finish(0x45d);
    }

    /// <summary>Builds a SimPathEvent without a path, which makes the SimPathManager (object 42) drop a path.</summary>
    /// <param name="index">The index of the dropped path.</param>
    /// <returns>The event.</returns>
    /// <remarks>Follows 0052b8cc, flag 0.</remarks>
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

    /// <summary>Builds one target name event per named sensor target, in index order.</summary>
    /// <param name="names">The target names. An unnamed target sends none.</param>
    /// <returns>The events.</returns>
    public static List<OutEvent> MakeTargetNameEvents(IReadOnlyList<string> names)
    {
        var output = new List<OutEvent>();
        for (var i = 0; i < names.Count; ++i)
            if (names[i].Length != 0)
                output.Add(MakeTargetName((uint)i, names[i]));
        return output;
    }

    /// <summary>Returns the inventory object counts of a control object.</summary>
    /// <param name="control">The control object.</param>
    /// <returns>The 200 item counts, or null. The inventory object is the object if it is a Player, else the Player an ObserverCamera orbits (0x004052c8).</returns>
    public static int[]? InventoryCounts(GameBase? control) => Player.InventoryCounts(control);
}
