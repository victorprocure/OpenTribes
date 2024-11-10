// Port of src/net/connection.hpp and src/net/connection.cpp: virtual connection protocol.
// See docs/net-protocol.md section 3.
namespace OpenTribes.Net;

// src/net/connection.hpp struct ConnectionEvents.
public abstract class ConnectionEvents
{
    public abstract void OnConnectRequest(Connection c, ReadOnlySpan<byte> payload);       // status 0
    public virtual void OnAccepted(Connection c, ReadOnlySpan<byte> payload) { }           // status 1
    public virtual void OnRejected(Connection c, ReadOnlySpan<byte> payload) { }           // status 2
    public virtual void OnEstablished(Connection c) { }                                    // status 3
    // Status 4, 5. The C++ passes a null reason on a timeout or replacement; that is an empty span here.
    public abstract void OnClosed(Connection c, ReadOnlySpan<byte> reason, bool timedOut);
    public virtual void OnData(Connection c, ReadOnlySpan<byte> payload) { }
    public virtual void OnNotify(Connection c, uint cookie, bool delivered) { }
    // A packet with more than 20 ack ranges was dropped (DECISIONS D11).
    public virtual void OnAckRangeOverflow(Connection c) { }
}

// src/net/connection.hpp Connection::State.
public enum ConnectionState : byte
{
    Idle = 0,
    Requested = 1,
    RequestSent = 2,
    Connected = 3,
    AcceptSent = 5,
}

// src/net/connection.hpp Connection::SendFn.
public delegate void SendFn(Address to, ReadOnlySpan<byte> data);

public sealed class Connection
{
    // src/net/connection.hpp Connection::Type.
    public const uint Data = 0;
    public const uint Disconnect = 2;
    public const uint ConnectRequest = 3;
    public const uint Accept = 4;
    public const uint Reject = 5;
    public const uint KeepAlive = 7;
    public const uint FlagAckRequest = 0x08;
    public const uint FlagAckReply = 0x10;

    public const int kMaxPacket = 1500;
    public const uint kMaxRetries = 15;

    private const uint kFree = 6;

    // src/net/connection.hpp Connection::Slot.
    private struct Slot
    {
        public uint SendTick;
        public uint Type;
        public bool Acked;
        public uint Cookie;
    }

    private Address mPeer;
    private uint mId;
    private readonly ConnectionEvents mEvents;
    private readonly SendFn mSend;

    private ConnectionState mState = ConnectionState.Idle;
    private bool mDead;
    private uint mRtt = 100;
    private float mLoss = 0.0f;

    private readonly bool[] mReceived = new bool[32];
    private uint mLastRecvSeq;
    private uint mAckedByPeer;  // highest of my sequences the peer acked, mod 32
    private uint mLastSentSeq;
    private uint mOldestUnacked;
    private uint mLastRecvTick;
    private uint mRetries;
    private readonly Slot[] mSlots = new Slot[32];
    private byte[] mLastPayload = Array.Empty<byte>(); // resent by retransmits

    public object? UserData;

    // src/net/connection.cpp carriesId.
    private static bool CarriesId(uint type)
    {
        uint b = type & ~FlagAckRequest;
        return b >= Disconnect && b <= Reject;
    }

    // src/net/connection.cpp Connection::Connection.
    public Connection(Address peer, uint id, ConnectionEvents events, SendFn send, uint nowMs)
    {
        mPeer = peer;
        mId = id;
        mEvents = events;
        mSend = send;
        mLastRecvTick = nowMs;
        for (int i = 0; i < 32; ++i)
            mSlots[i].Type = kFree;
    }

    // src/net/connection.cpp Connection::send: sends a sequenced packet. False when 31 packets are already in flight.
    public bool Send(uint type, ReadOnlySpan<byte> payload, uint nowMs, uint cookie = 0)
    {
        if (((mLastSentSeq + 1) & 31) == mOldestUnacked)
            return false;
        Transmit(type, payload, nowMs, cookie);
        return true;
    }

    // src/net/connection.cpp Connection::close: sends a reject or disconnect with a C string reason
    // (null: no payload) and marks the connection dead. The reason is a byte string.
    public void Close(uint type, string? reason, uint nowMs)
    {
        // A null reason sends the packet with no payload (the deleted connection of an invalid packet).
        byte[] payload = Array.Empty<byte>();
        if (reason != null)
        {
            int len = reason.IndexOf('\0'); // strlen
            string text = len < 0 ? reason : reason[..len];
            payload = new byte[text.Length + 1];
            for (int i = 0; i < text.Length; ++i)
                payload[i] = (byte)text[i];
        }
        Transmit(type, payload, nowMs, 0);
        mDead = true;
    }

    // src/net/connection.hpp Connection::drop: removes the connection without sending anything (banned peer).
    public void Drop() => mDead = true;

    // src/net/connection.cpp Connection::transmit.
    private void Transmit(uint type, ReadOnlySpan<byte> payload, uint nowMs, uint cookie)
    {
        if (type == ConnectRequest && mState == ConnectionState.Idle)
            mState = ConnectionState.RequestSent;
        else if (type == Accept && mState == ConnectionState.Requested)
            mState = ConnectionState.AcceptSent;

        byte[] buf = new byte[kMaxPacket];
        var bs = new BitStream(buf, buf.Length);
        uint wireType = type;
        uint seq = mLastSentSeq;
        bool withId = false;

        if ((type & FlagAckRequest) != 0)
        {
            // Retransmit the newest slot with its original type plus flags.
            ref Slot slot = ref mSlots[mLastSentSeq & 31];
            slot.Type |= FlagAckRequest;
            wireType = slot.Type | type;
            withId = CarriesId(slot.Type);
            payload = mLastPayload;
        }
        else if (type == KeepAlive || type == FlagAckReply)
        {
            // Unsequenced. The original also resets the stored payload length here.
            mLastPayload = Array.Empty<byte>();
            payload = ReadOnlySpan<byte>.Empty;
        }
        else
        {
            if (type == Accept)
                wireType = Accept | FlagAckRequest;
            seq = ++mLastSentSeq;
            mSlots[seq & 31] = new Slot { SendTick = nowMs, Type = type, Acked = false, Cookie = cookie };
            withId = CarriesId(type);
            mLastPayload = payload.ToArray();
        }

        bs.WriteFlag(true);
        bs.WriteInt(mId & 1, 1);
        bs.WriteInt(seq, 9);
        bs.WriteInt(mAckedByPeer, 5);

        // One range per run of received sequences, scanning from lastRecv + 2 around the ring.
        uint stop = (mLastRecvSeq + 1) & 31;
        uint i = stop;
        while (true)
        {
            i = (i + 1) & 31;
            if (i == stop)
                break;
            if (!mReceived[i])
                continue;
            uint start = i;
            uint count = 0;
            while (i != stop && mReceived[i])
            {
                ++count;
                i = (i + 1) & 31;
            }
            while (count > 7)
            {
                bs.WriteInt(7, 3);
                bs.WriteInt(start, 5);
                start = (start + 7) & 31;
                count -= 7;
            }
            bs.WriteInt(count, 3);
            bs.WriteInt(start, 5);
            if (i == stop)
                break;
        }
        bs.WriteInt(0, 3);
        bs.WriteInt(wireType, 5);
        if (withId)
            bs.WriteInt(mId, 32);

        int header = bs.BytePosition();
        if (header + payload.Length > buf.Length)
            return;
        payload.CopyTo(buf.AsSpan(header));
        mSend(mPeer, buf.AsSpan(0, header + payload.Length));
    }

    // src/net/connection.cpp Connection::flush.
    private void Flush()
    {
        for (uint i = (mLastSentSeq + 1) & 31; i != (mLastSentSeq & 31); i = (i + 1) & 31)
        {
            if (mSlots[i].Type == kFree)
                continue;
            if (mSlots[i].Cookie != 0)
                mEvents.OnNotify(this, mSlots[i].Cookie, false);
            mSlots[i].Type = kFree;
        }
        mState = ConnectionState.Idle;
    }

    // src/net/connection.cpp Connection::receive.
    public void Receive(ReadOnlySpan<byte> data, uint nowMs)
    {
        byte[] copy = data.ToArray();
        int size = copy.Length;
        var bs = new BitStream(copy, size);
        if (!bs.ReadFlag())
            return;
        uint idBit = bs.ReadInt(1);
        uint seq = bs.ReadInt(9);
        uint peerAckAck = bs.ReadInt(5);

        Span<uint> rangeStart = stackalloc uint[20];
        Span<uint> rangeCountOf = stackalloc uint[20];
        uint rangeCount = 0;
        uint cnt;
        while ((cnt = bs.ReadInt(3)) != 0)
        {
            if (bs.Error())
                return;
            if (rangeCount == 20)
            {
                mEvents.OnAckRangeOverflow(this); // the original's stack array holds 20 ranges
                return;
            }
            rangeStart[(int)rangeCount] = bs.ReadInt(5);
            rangeCountOf[(int)rangeCount] = cnt;
            ++rangeCount;
        }
        uint rawType = bs.ReadInt(5);
        if (bs.Error())
            return;
        bool needAck = (rawType == KeepAlive || (rawType & FlagAckRequest) != 0) && (rawType & FlagAckReply) == 0;
        uint type = rawType & ~(FlagAckRequest | FlagAckReply);
        if (type == Accept)
            needAck = true;

        ReadOnlySpan<byte> payload = ReadOnlySpan<byte>.Empty;
        void TakePayload(ReadOnlySpan<byte> d, ref ReadOnlySpan<byte> p)
        {
            int at = bs.BytePosition();
            p = at < d.Length ? d[at..] : ReadOnlySpan<byte>.Empty;
        }

        if (type == ConnectRequest)
        {
            uint id = bs.ReadInt(32);
            TakePayload(data, ref payload);
            if (mState != ConnectionState.Idle && id != mId)
            {
                // A new connection id from the same address replaces the old connection.
                Flush();
                mEvents.OnClosed(this, ReadOnlySpan<byte>.Empty, true);
                mDead = true;
                return;
            }
            if (mState == ConnectionState.Idle)
                mId = id;
        }
        if ((mId & 1) != idBit)
            return; // "packet ordering incorrect"

        // Extend the 9-bit sequence into the window [lastRecv, lastRecv + 30].
        uint full = seq | (mLastRecvSeq & ~0x1ffu);
        uint windowEnd = mLastRecvSeq + 30;
        if (full < mLastRecvSeq || full > windowEnd)
        {
            full = seq | (windowEnd & ~0x1ffu);
            if (full < mLastRecvSeq || full > windowEnd)
                return; // "bad seqNum"
        }

        for (uint s = mLastRecvSeq + 1; s < full; ++s)
            mReceived[s & 31] = false;
        if (type != KeepAlive)
            mReceived[full & 31] = true;
        for (uint i = (full + 1) & 31; i != ((peerAckAck + 1) & 31); i = (i + 1) & 31)
            mReceived[i] = false;
        int gap = (int)(full & 31) - (int)peerAckAck;
        if (gap < 0)
            gap += 32;
        if (gap > 11)
            needAck = true;

        if (rangeCount != 0)
        {
            uint end = 0;
            for (uint r = 0; r < rangeCount; ++r)
            {
                uint s = rangeStart[(int)r];
                for (uint c = 0; c < rangeCountOf[(int)r]; ++c, s = (s + 1) & 31)
                    if (mSlots[s].Type != kFree)
                        mSlots[s].Acked = true;
                end = s;
            }
            mOldestUnacked = rangeStart[0];
            uint i = (mLastSentSeq + 1) & 31;
            do
            {
                ref Slot slot = ref mSlots[i];
                if (slot.Type != kFree)
                {
                    if (!slot.Acked)
                    {
                        mLoss = 0.01f * (mLoss * 99.0f + 1.0f);
                        if (slot.Cookie != 0)
                            mEvents.OnNotify(this, slot.Cookie, false);
                        slot.Type = kFree;
                    }
                    else
                    {
                        mLoss = 0.01f * mLoss * 99.0f;
                        if (slot.Cookie != 0)
                            mEvents.OnNotify(this, slot.Cookie, true);
                        if ((slot.Type & ~FlagAckRequest) == Accept && mState == ConnectionState.AcceptSent)
                        {
                            mState = ConnectionState.Connected;
                            mEvents.OnEstablished(this);
                        }
                        slot.Type = kFree;
                        mRtt = ((nowMs - slot.SendTick) + mRtt * 31) >> 5;
                    }
                }
                i = (i + 1) & 31;
            } while (i != end);
            mAckedByPeer = (end - 1) & 31;
        }

        mLastRecvTick = nowMs;
        mRetries = 0;
        bool isNew = mLastRecvSeq != full;
        mLastRecvSeq = full;

        if (needAck)
        {
            if (mSlots[mLastSentSeq & 31].Type == kFree)
                Transmit(FlagAckReply, ReadOnlySpan<byte>.Empty, nowMs, 0);
            else
                Transmit(FlagAckRequest | FlagAckReply, ReadOnlySpan<byte>.Empty, nowMs, 0);
        }
        if (!isNew)
            return;

        if (type == ConnectRequest && mState == ConnectionState.Idle)
        {
            mState = ConnectionState.Requested;
            mEvents.OnConnectRequest(this, payload);
        }
        else if ((type == Disconnect && mState == ConnectionState.Connected) ||
                 ((type == Accept || type == Reject) && mState == ConnectionState.RequestSent))
        {
            uint id = bs.ReadInt(32);
            TakePayload(data, ref payload);
            if (id != mId)
                return;
            if (type == Accept)
            {
                mEvents.OnAccepted(this, payload);
                mState = ConnectionState.Connected;
                return;
            }
            Flush();
            if (type == Reject)
                mEvents.OnRejected(this, payload);
            else
                mEvents.OnClosed(this, payload, false);
            mDead = true;
        }
        else if (type == Data && mState == ConnectionState.Connected)
        {
            TakePayload(data, ref payload);
            mEvents.OnData(this, payload);
        }
    }

    // src/net/connection.cpp Connection::tick: keepalive/retransmit on silence. On timeout the
    // connection is flushed, reported and marked dead.
    public void Tick(uint nowMs)
    {
        if (mDead || nowMs <= mLastRecvTick + 3000 * (mRetries + 1))
            return;
        if (mSlots[mLastSentSeq & 31].Type == kFree)
            Transmit(KeepAlive, ReadOnlySpan<byte>.Empty, nowMs, 0);
        else
            Transmit(FlagAckRequest, ReadOnlySpan<byte>.Empty, nowMs, 0);
        if (++mRetries > kMaxRetries)
        {
            Flush();
            mEvents.OnClosed(this, ReadOnlySpan<byte>.Empty, true);
            mDead = true;
        }
    }

    // src/net/connection.hpp accessors.
    public Address Peer() => mPeer;
    public ConnectionState State() => mState;
    public bool Dead() => mDead;
    public uint Id() => mId;
    public uint RttMs() => mRtt;
    public float PacketLoss() => mLoss;
}
