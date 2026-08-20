// Client GameConnection (docs/client-net.md 2.2, 4.3; docs/game-stream.md 1): the receive path of one packet payload
// (rate sections, EventManager, PlayerPSC, GhostManager) and the packet writer (rate sections, remote evals, the
// PSC block with the moves, the empty ghost block).
using System.Globalization;
using OpenTribes.Script;

namespace OpenTribes.Game;

// What one received payload decoded to, with the bit accounting of tools/stream_decode.py game_packet.
public sealed class DecodedPacket
{
    public string Direction = "s2c";
    public int Bytes;
    public int BitsRead;                  // bits attributed to fields (known)
    public int UnknownBits;               // bits that no layout explains: counted, never skipped silently
    public int PaddingBits;               // 0..7 bits after the last member
    public string? UnknownReason;
    public bool Overrun;                  // read past the end: "Invalid packet"
    public (int Interval, int Size)? RateSet, RateReq;
    public readonly List<DecodedEvent> Events = new();
    public DecodedPsc? Psc;
    public DecodedGhostBlock? Ghosts;
}

public sealed class ClientGameConnection
{
    private const int kMaxMoves = 30;

    public readonly ClientWorld World;
    public readonly ClientEvents Events;

    // Active rate (+0x9bc, +0x9c0), own requested rate (+0x9c8, +0x9cc), dirty flags.
    public uint IntervalMs = 100, PacketBytes = 200;
    public uint OwnIntervalMs = 100, OwnPacketBytes = 200;
    private bool mRateSetDirty, mRateReqDirty;

    // Send side.
    private readonly LinkedList<OutEvent> mQueue = new();
    private readonly Dictionary<uint, List<OutEvent>> mInFlight = new();
    private readonly Dictionary<uint, (uint Number, bool Set, bool Req)> mMarks = new();
    private readonly bool[] mSeqInFlight = new bool[128];
    private int mNextSeq, mLowSeq, mMaxSeq;
    private uint mNextCookie = 1, mPacketCount = 1, mDeliveredNo, mLastSend;
    private bool mSentAny;

    // The own rate request from $pref::PacketRate (10..30, default 10) and $pref::PacketSize (200..450, default 200): the
    // interval is 1000 / rate. A value that differs from the held request marks the rateReq section dirty
    // (GameConnection_loadPacketPrefs 0x00550c68). An unset pref reads as the default through the empty text.
    public void LoadPacketPrefs(string rateText, string sizeText)
    {
        uint rate = rateText.Length == 0 ? 10u : unchecked((uint)Numbers.BorlandAtoi(rateText));
        int size = sizeText.Length == 0 ? 200 : Numbers.BorlandAtoi(sizeText);
        rate = rate > 30 ? 30u : (rate < 10 ? 10u : rate);
        size = size > 450 ? 450 : (size < 200 ? 200 : size);
        uint interval = 1000u / rate;
        if (interval != OwnIntervalMs || (uint)size != OwnPacketBytes)
        {
            OwnIntervalMs = interval;
            OwnPacketBytes = (uint)size;
            mRateReqDirty = true;
        }
    }

    public ClientGameConnection(ClientWorld world)
    {
        World = world;
        Events = new ClientEvents(world);
    }

    // ---- receive (2.2) ----

    // GameConnection_receivePacket 005509a4 on a client: `payload` is the byte string after the VC header.
    public DecodedPacket Receive(byte[] payload, string direction = "s2c")
    {
        var pkt = new DecodedPacket { Direction = direction, Bytes = payload.Length };
        var r = new ClientReader(payload, World);
        ++World.Packets;
        try
        {
            if (r.Flag())
            {
                int interval = (int)r.Int(10), size = (int)r.Int(10);
                pkt.RateSet = (interval, size);
                IntervalMs = (uint)interval;       // stored unclamped (2.2 step 2)
                PacketBytes = (uint)size;
            }
            if (r.Flag())
            {
                int interval = (int)r.Int(10), size = (int)r.Int(10);
                pkt.RateReq = (interval, size);
                uint i2 = Math.Max((uint)interval, OwnIntervalMs), s2 = Math.Min((uint)size, OwnPacketBytes);
                if (i2 != IntervalMs || s2 != PacketBytes)
                {
                    IntervalMs = i2;
                    PacketBytes = s2;
                    mRateSetDirty = true;
                }
            }
            if (r.Flag())
                Events.Read(r, pkt.Events);
            if (r.Flag())
                pkt.Psc = direction == "c2s" ? ClientPsc.ReadClientBlock(r) : World.Psc.ReadServerBlock(r);
            if (r.Flag())
                pkt.Ghosts = World.Ghosts.Read(r);
            int pad = r.Left;
            if (pad >= 8)
            {
                pkt.UnknownBits = pad;
                pkt.UnknownReason = pad + " trailing bits after the last member";
                pkt.BitsRead = r.Pos;
            }
            else
            {
                pkt.BitsRead = r.Pos;
                pkt.PaddingBits = pad;      // stale buffer bits when the last write was a flag (game-stream.md 1.2)
            }
        }
        catch (UnsupportedException e)
        {
            pkt.UnknownBits = r.Left;
            pkt.BitsRead = r.Pos;
            pkt.UnknownReason = "from bit " + r.Pos + ": " + e.Message;
            // The partial member results are lost with the exception; the state the earlier members set stays.
        }
        catch (OverrunException)
        {
            pkt.Overrun = true;
            pkt.BitsRead = r.SizeBits;
            ++World.Errors;
        }
        World.UnknownBits += pkt.UnknownBits;
        World.PaddingBits += pkt.PaddingBits;
        if (pkt.UnknownReason != null)
            World.UnknownReasons.Add(pkt.UnknownReason);
        return pkt;
    }

    // ---- send (4.3, game-stream.md 1.3) ----

    public void Post(OutEvent e)
    {
        if (e.Ordered)
            e.Seq = mNextSeq++;
        mQueue.AddLast(e);
    }

    public void PostRemoteEval(params string[] argv) => Post(GameEvents.MakeRemoteEval(argv));

    private bool WriteEvents(BitStream bs, List<OutEvent> sent)
    {
        bool any = false;
        int prev = -4;
        while (mQueue.Count != 0 && bs.BitPosition() <= PacketBytes * 8)
        {
            OutEvent e = mQueue.First!.Value;
            while (!mSeqInFlight[mLowSeq & 127] && mLowSeq < mMaxSeq)
                ++mLowSeq;
            if (e.Ordered && e.Seq > mLowSeq + 126)
                break;
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
                    prev = -2;
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

    // $pref::PacketFrame (DAT_0063efb4, 32..96 in 1.30), 0 = not set. A running client has a net time, and then
    // GameConnection_writePacket (00550d2c) paces by it: a packet is due when net time >= last send + PacketFrame, and
    // 1000 / PacketFrame is not below the packets written since the last delivered one. The negotiated interval only
    // paces a connection without a net time.
    public uint PacketFrame;

    // Whether a packet is due (1.3 Pacing): the interval (or the packet frame) has passed and 1000 / interval is not below
    // the packets written since the last delivered one.
    public bool Due(uint nowMs)
    {
        uint interval = PacketFrame != 0 ? PacketFrame : Math.Max(IntervalMs, 1u);
        return !mSentAny || (unchecked(nowMs - mLastSend) >= interval && 1000u / interval >= unchecked(mPacketCount - mDeliveredNo));
    }

    // Builds the payload of the next packet. `send` hands it to the VC with the notify cookie and returns whether the
    // window had room; on false the contents are put back.
    public bool WritePacket(uint nowMs, Func<byte[], int, uint, bool> send)
    {
        byte[] buf = new byte[Connection.kMaxPacket - 32];
        var bs = new BitStream(buf, buf.Length);
        bool setWas = mRateSetDirty, reqWas = mRateReqDirty;
        if (bs.WriteFlag(mRateSetDirty))
        {
            bs.WriteInt(IntervalMs, 10);
            bs.WriteInt(PacketBytes, 10);
            mRateSetDirty = false;
        }
        if (bs.WriteFlag(mRateReqDirty))
        {
            bs.WriteInt(OwnIntervalMs, 10);
            bs.WriteInt(OwnPacketBytes, 10);
            mRateReqDirty = false;
        }
        var sent = new List<OutEvent>();
        uint flagPos = bs.BitPosition();
        bs.WriteFlag(true);
        if (!WriteEvents(bs, sent))
        {
            bs.SetBitPosition(flagPos);
            bs.WriteFlag(false);
        }
        bs.WriteFlag(true);
        World.Psc.WriteClient(bs);
        // The client's own ghost manager writes `always 0, idbits 4, end` every packet (game-stream-ghosts.md 2.1).
        bs.WriteFlag(true);
        bs.WriteFlag(false);
        bs.WriteInt(1, 3);
        bs.WriteFlag(false);
        uint cookie = mNextCookie++;
        if (!send(buf, bs.BytePosition(), cookie))
        {
            for (int i = sent.Count - 1; i >= 0; --i)
                mQueue.AddFirst(sent[i]);
            mRateSetDirty |= setWas;
            mRateReqDirty |= reqWas;
            return false;
        }
        mMarks[cookie] = (mPacketCount, setWas, reqWas);
        ++mPacketCount;
        if (sent.Count != 0)
            mInFlight[cookie] = sent;
        mLastSend = nowMs;
        mSentAny = true;
        return true;
    }

    // Packet notify (game-stream.md 1.5): a lost packet re-arms its rate sections and requeues its guaranteed events.
    public void Notify(uint cookie, bool delivered)
    {
        if (mMarks.TryGetValue(cookie, out var mk))
        {
            if (delivered)
                mDeliveredNo = mk.Number;
            else
            {
                mRateSetDirty |= mk.Set;
                mRateReqDirty |= mk.Req;
            }
            mMarks.Remove(cookie);
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
            for (int i = events.Count - 1; i >= 0; --i)
                mQueue.AddFirst(events[i]);
        }
        mInFlight.Remove(cookie);
    }

    public int Queued => mQueue.Count;
}
