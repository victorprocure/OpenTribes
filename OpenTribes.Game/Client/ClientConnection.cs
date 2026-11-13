// Client GameConnection (docs/client-net.md 2.2, 4.3; docs/game-stream.md 1): the receive path of one packet payload
// (rate sections, EventManager, PlayerPSC, GhostManager) and the packet writer (rate sections, remote evals, the
// PSC block with the moves, the empty ghost block).
namespace OpenTribes.Game;

/// <summary>What one received payload decoded to, with the bit accounting of tools/stream_decode.py game_packet.</summary>
public sealed class DecodedPacket
{
    /// <summary>The direction of the packet, "s2c" or "c2s".</summary>
    public string Direction = "s2c";
    /// <summary>The packet length in bytes.</summary>
    public int Bytes;
    /// <summary>The bits attributed to known fields.</summary>
    public int BitsRead;
    /// <summary>The bits that no layout explains. They are counted, never skipped silently.</summary>
    public int UnknownBits;
    /// <summary>The 0 to 7 padding bits after the last member.</summary>
    public int PaddingBits;
    /// <summary>The reason for the unknown bits, or null.</summary>
    public string? UnknownReason;
    /// <summary>True when the reader went past the end ("Invalid packet").</summary>
    public bool Overrun;                  // read past the end: "Invalid packet"
    /// <summary>The Net_invalidPacket text of a member reader. The session drops the connection.</summary>
    public string? Error;
    /// <summary>The rate set section: interval and size, or null.</summary>
    public (int Interval, int Size)? RateSet;
    /// <summary>The rate request section: interval and size, or null.</summary>
    public (int Interval, int Size)? RateReq;
    /// <summary>The decoded events.</summary>
    public readonly List<DecodedEvent> Events = [];
    /// <summary>The decoded PlayerPSC block, or null.</summary>
    public DecodedPsc? Psc;
    /// <summary>The decoded ghost block, or null.</summary>
    public DecodedGhostBlock? Ghosts;
}

/// <summary>The client side GameConnection: the receive path of one packet payload and the packet writer.</summary>
/// <remarks>Covers the rate sections, the EventManager, the PlayerPSC and the GhostManager on receive, and the rate sections, remote evals, the PSC block with the moves and the empty ghost block on send (docs/client-net.md 2.2, 4.3; docs/game-stream.md 1).</remarks>
public sealed class ClientGameConnection
{

    /// <summary>The client world.</summary>
    public readonly ClientWorld World;
    /// <summary>The event manager.</summary>
    public readonly ClientEvents Events;

    /// <summary>The active packet interval in milliseconds (+0x9bc).</summary>
    public uint IntervalMs = 100;
    /// <summary>The active packet size in bytes (+0x9c0).</summary>
    public uint PacketBytes = 200;
    /// <summary>The own requested packet interval in milliseconds (+0x9c8).</summary>
    public uint OwnIntervalMs = 100;
    /// <summary>The own requested packet size in bytes (+0x9cc).</summary>
    public uint OwnPacketBytes = 200;
    private bool mRateSetDirty, mRateReqDirty;

    // Send side.
    private readonly LinkedList<OutEvent> mQueue = new();
    private readonly Dictionary<uint, List<OutEvent>> mInFlight = [];
    private readonly Dictionary<uint, (uint Number, bool Set, bool Req)> mMarks = [];
    private readonly bool[] mSeqInFlight = new bool[128];
    private int mNextSeq, mLowSeq, mMaxSeq;
    private uint mNextCookie = 1, mPacketCount = 1, mDeliveredNo, mLastSend;
    private bool mSentAny;

    /// <summary>Loads the own rate request from the packet prefs.</summary>
    /// <param name="rateText">The value of <c>$pref::PacketRate</c>, 1 to 30, default 10. The interval is 1000 / rate.</param>
    /// <param name="sizeText">The value of <c>$pref::PacketSize</c>, 100 to 450, default 200.</param>
    /// <remarks>As in the 1.11 exe. A value that differs from the held request marks the rateReq section dirty (GameConnection_loadPacketPrefs 0x00550c68). An unset pref reads as the default through the empty text.</remarks>
    public void LoadPacketPrefs(string rateText, string sizeText)
    {
        var rate = rateText.Length == 0 ? 10u : unchecked((uint)Numbers.BorlandAtoi(rateText));
        var size = sizeText.Length == 0 ? 200 : Numbers.BorlandAtoi(sizeText);
        rate = rate > 30 ? 30u : (rate < 1 ? 1u : rate);
        size = size > 450 ? 450 : (size < 100 ? 100 : size);
        var interval = 1000u / rate;
        if (interval != OwnIntervalMs || (uint)size != OwnPacketBytes)
        {
            OwnIntervalMs = interval;
            OwnPacketBytes = (uint)size;
            mRateReqDirty = true;
        }
    }

    /// <summary>Creates a connection for a client world.</summary>
    /// <param name="world">The client world.</param>
    public ClientGameConnection(ClientWorld world)
    {
        World = world;
        Events = new ClientEvents(world);
    }

    // ---- receive (2.2) ----

    /// <summary>Reads one received packet payload.</summary>
    /// <param name="payload">The bytes after the VC header.</param>
    /// <param name="direction">The direction, "s2c" or "c2s".</param>
    /// <returns>What the payload decoded to.</returns>
    /// <remarks>Follows GameConnection_receivePacket (005509a4) on a client.</remarks>
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
            var pad = r.Left;
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
        catch (PacketErrorException e)
        {
            pkt.Error = e.Message;
            pkt.BitsRead = r.Pos;
            ++World.Errors;
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

    /// <summary>Gets whether events wait for the next packet, such as the CGADone remote eval after the ghost-always phase.</summary>
    public bool OutgoingPending => mQueue.Count != 0;

    /// <summary>Queues an event.</summary>
    /// <param name="e">The event.</param>
    public void Post(OutEvent e)
    {
        if (e.Ordered)
            e.Seq = mNextSeq++;
        mQueue.AddLast(e);
    }

    /// <summary>Queues a remote eval.</summary>
    /// <param name="argv">The command arguments.</param>
    public void PostRemoteEval(params string[] argv) => Post(GameEvents.MakeRemoteEval(argv));

    private bool WriteEvents(BitStream bs, List<OutEvent> sent)
    {
        var any = false;
        var prev = -4;
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

    /// <summary>The <c>$pref::PacketFrame</c> value (DAT_0063efb4, 32 to 96 in 1.30), 0 when not set.</summary>
    /// <remarks>A running client has a net time, and GameConnection_writePacket (00550d2c) then paces by it: a packet is due when net time is at least last send + PacketFrame, and 1000 / PacketFrame is not below the packets written since the last delivered one. The negotiated interval only paces a connection without a net time.</remarks>
    public uint PacketFrame;

    /// <summary>Tells whether a packet is due (docs/game-stream.md 1.3 Pacing).</summary>
    /// <param name="nowMs">The current time in milliseconds.</param>
    /// <returns>True when the interval (or the packet frame) has passed and 1000 / interval is not below the packets written since the last delivered one.</returns>
    public bool Due(uint nowMs)
    {
        var interval = PacketFrame != 0 ? PacketFrame : Math.Max(IntervalMs, 1u);
        return !mSentAny || (unchecked(nowMs - mLastSend) >= interval && 1000u / interval >= unchecked(mPacketCount - mDeliveredNo));
    }

    /// <summary>Builds the payload of the next packet.</summary>
    /// <param name="nowMs">The current time in milliseconds.</param>
    /// <param name="send">Hands the payload to the VC with its length, the time and the notify cookie. Returns whether the window had room.</param>
    /// <returns>True when the packet was sent. On false the contents are put back.</returns>
    public bool WritePacket(uint nowMs, Func<byte[], int, uint, bool> send)
    {
        var buf = new byte[Connection.kMaxPacket - 32];
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
        var flagPos = bs.BitPosition();
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
        var cookie = mNextCookie++;
        if (!send(buf, bs.BytePosition(), cookie))
        {
            for (var i = sent.Count - 1; i >= 0; --i)
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

    /// <summary>Reports whether a packet was delivered (docs/game-stream.md 1.5).</summary>
    /// <param name="cookie">The notify cookie of the packet.</param>
    /// <param name="delivered">True when it was delivered.</param>
    /// <remarks>A lost packet re-arms its rate sections and requeues its guaranteed events.</remarks>
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
            for (var i = events.Count - 1; i >= 0; --i)
                mQueue.AddFirst(events[i]);
        }
        mInFlight.Remove(cookie);
    }

    /// <summary>Gets the number of queued events.</summary>
    public int Queued => mQueue.Count;
}
