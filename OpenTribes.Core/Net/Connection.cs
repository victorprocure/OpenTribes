namespace OpenTribes.Net;

/// <summary>The callbacks a <see cref="Connection"/> raises. Port of src/net/connection.hpp ConnectionEvents.</summary>
public abstract class ConnectionEvents
{
    /// <summary>Called when a connect request arrives (status 0).</summary>
    /// <param name="c">The connection.</param>
    /// <param name="payload">The request payload.</param>
    public abstract void OnConnectRequest(Connection c, ReadOnlySpan<byte> payload);
    /// <summary>Called when the peer accepts our request (status 1).</summary>
    /// <param name="c">The connection.</param>
    /// <param name="payload">The accept payload.</param>
    public virtual void OnAccepted(Connection c, ReadOnlySpan<byte> payload) { }
    /// <summary>Called when the peer rejects our request (status 2).</summary>
    /// <param name="c">The connection.</param>
    /// <param name="payload">The reject reason bytes.</param>
    public virtual void OnRejected(Connection c, ReadOnlySpan<byte> payload) { }
    /// <summary>Called when the peer acknowledged our accept and the connection is up (status 3).</summary>
    /// <param name="c">The connection.</param>
    public virtual void OnEstablished(Connection c) { }
    /// <summary>Called when the connection ends (status 4 and 5).</summary>
    /// <param name="c">The connection.</param>
    /// <param name="reason">The disconnect reason bytes. The C++ passes a null reason on a timeout or replacement, which is an empty span here.</param>
    /// <param name="timedOut">True on a timeout or when a new connection from the same address replaced this one.</param>
    public abstract void OnClosed(Connection c, ReadOnlySpan<byte> reason, bool timedOut);
    /// <summary>Called when a data packet arrives.</summary>
    /// <param name="c">The connection.</param>
    /// <param name="payload">The packet payload.</param>
    public virtual void OnData(Connection c, ReadOnlySpan<byte> payload) { }
    /// <summary>Called when a packet sent with a cookie was acknowledged or lost.</summary>
    /// <param name="c">The connection.</param>
    /// <param name="cookie">The cookie given to <see cref="Connection.Send"/>.</param>
    /// <param name="delivered">True when the peer acknowledged the packet.</param>
    public virtual void OnNotify(Connection c, uint cookie, bool delivered) { }
    /// <summary>Called when a packet with more than 20 ack ranges was dropped (DECISIONS D11).</summary>
    /// <param name="c">The connection.</param>
    public virtual void OnAckRangeOverflow(Connection c) { }
}

/// <summary>The handshake state of a <see cref="Connection"/>. Port of src/net/connection.hpp Connection::State.</summary>
public enum ConnectionState : byte
{
    /// <summary>No handshake yet.</summary>
    Idle = 0,
    /// <summary>A connect request arrived and is being decided.</summary>
    Requested = 1,
    /// <summary>We sent a connect request.</summary>
    RequestSent = 2,
    /// <summary>The connection is up.</summary>
    Connected = 3,
    /// <summary>We sent an accept and wait for its acknowledgement.</summary>
    AcceptSent = 5,
}

/// <summary>Sends one datagram for a connection.</summary>
/// <param name="to">The destination.</param>
/// <param name="data">The datagram.</param>
public delegate void SendFn(Address to, ReadOnlySpan<byte> data);

/// <summary>The virtual connection protocol: sequenced packets with acknowledgement ranges, retransmission and a connect handshake.</summary>
/// <remarks>Port of src/net/connection.cpp, see docs/net-protocol.md section 3.</remarks>
public sealed class Connection
{
    /// <summary>Packet type: data.</summary>
    public const uint Data = 0;
    /// <summary>Packet type: disconnect.</summary>
    public const uint Disconnect = 2;
    /// <summary>Packet type: connect request.</summary>
    public const uint ConnectRequest = 3;
    /// <summary>Packet type: accept.</summary>
    public const uint Accept = 4;
    /// <summary>Packet type: reject.</summary>
    public const uint Reject = 5;
    /// <summary>Packet type: keep alive. It is unsequenced.</summary>
    public const uint KeepAlive = 7;
    /// <summary>Type flag: the receiver must acknowledge.</summary>
    public const uint FlagAckRequest = 0x08;
    /// <summary>Type flag: the packet is a pure acknowledgement.</summary>
    public const uint FlagAckReply = 0x10;

    /// <summary>The largest datagram in bytes.</summary>
    public const int MaxPacket = 1500;
    /// <summary>The number of silent retries before the connection times out.</summary>
    public const uint MaxRetries = 15;

    private const uint Free = 6;

    // src/net/connection.hpp Connection::Slot.
    private struct Slot
    {
        public uint SendTick { get; set; }
        public uint Type { get; set; }
        public bool Acked { get; set; }
        public uint Cookie { get; set; }
    }

    private Address _peer;
    private uint _id;
    private readonly ConnectionEvents _events;
    private readonly SendFn _send;

    private ConnectionState _state = ConnectionState.Idle;
    private bool _dead;
    private uint _rtt = 100;
    private float _loss;

    private readonly bool[] _received = new bool[32];
    private uint _lastRecvSeq;
    private uint _ackedByPeer;  // highest of my sequences the peer acked, mod 32
    private uint _lastSentSeq;
    private uint _oldestUnacked;
    private uint _lastRecvTick;
    private uint _retries;
    private readonly Slot[] _slots = new Slot[32];
    private byte[] _lastPayload = []; // resent by retransmits

    /// <summary>Caller data attached to the connection.</summary>
    public object? UserData { get; set; }

    // src/net/connection.cpp carriesId.
    private static bool CarriesId(uint type)
    {
        var b = type & ~FlagAckRequest;
        return b is >= Disconnect and <= Reject;
    }

    /// <summary>Creates a connection.</summary>
    /// <param name="peer">The peer address.</param>
    /// <param name="id">The connection id. Its low bit is carried in every packet.</param>
    /// <param name="events">Receives the callbacks.</param>
    /// <param name="send">Sends datagrams.</param>
    /// <param name="nowMs">The current time in milliseconds.</param>
    public Connection(Address peer, uint id, ConnectionEvents events, SendFn send, uint nowMs)
    {
        _peer = peer;
        _id = id;
        _events = events;
        _send = send;
        _lastRecvTick = nowMs;
        for (var i = 0; i < 32; ++i)
            _slots[i].Type = Free;
    }

    /// <summary>Sends a sequenced packet.</summary>
    /// <param name="type">The packet type.</param>
    /// <param name="payload">The payload.</param>
    /// <param name="nowMs">The current time in milliseconds.</param>
    /// <param name="cookie">A value returned by <see cref="ConnectionEvents.OnNotify"/>, 0 for none.</param>
    /// <returns>False when 31 packets are already in flight.</returns>
    public bool Send(uint type, ReadOnlySpan<byte> payload, uint nowMs, uint cookie = 0)
    {
        if (((_lastSentSeq + 1) & 31) == _oldestUnacked)
            return false;
        Transmit(type, payload, nowMs, cookie);
        return true;
    }

    /// <summary>Sends a reject or disconnect and marks the connection dead.</summary>
    /// <param name="type">The packet type, <see cref="Reject"/> or <see cref="Disconnect"/>.</param>
    /// <param name="reason">The reason as a byte string, sent as a C string. Null sends no payload (the deleted connection of an invalid packet).</param>
    /// <param name="nowMs">The current time in milliseconds.</param>
    public void Close(uint type, string? reason, uint nowMs)
    {
        // A null reason sends the packet with no payload (the deleted connection of an invalid packet).
        var payload = Array.Empty<byte>();
        if (reason != null)
        {
            var len = reason.IndexOf('\0'); // strlen
            var text = len < 0 ? reason : reason[..len];
            payload = new byte[text.Length + 1];
            for (var i = 0; i < text.Length; ++i)
                payload[i] = (byte)text[i];
        }
        Transmit(type, payload, nowMs, 0);
        _dead = true;
    }

    /// <summary>Removes the connection without sending anything, for a banned peer.</summary>
    public void Drop() => _dead = true;

    /// <summary>Builds and sends one packet. A packet with <see cref="FlagAckRequest"/> resends the newest slot. Keep alives and replies are unsequenced.</summary>
    private void Transmit(uint type, ReadOnlySpan<byte> payload, uint nowMs, uint cookie)
    {
        if (type == ConnectRequest && _state == ConnectionState.Idle)
            _state = ConnectionState.RequestSent;
        else if (type == Accept && _state == ConnectionState.Requested)
            _state = ConnectionState.AcceptSent;

        var buf = new byte[MaxPacket];
        var bs = new BitBuffer(buf, buf.Length);
        var wireType = type;
        var seq = _lastSentSeq;
        var withId = false;

        if ((type & FlagAckRequest) != 0)
        {
            // Retransmit the newest slot with its original type plus flags.
            ref var slot = ref _slots[_lastSentSeq & 31];
            slot.Type |= FlagAckRequest;
            wireType = slot.Type | type;
            withId = CarriesId(slot.Type);
            payload = _lastPayload;
        }
        else if (type is KeepAlive or FlagAckReply)
        {
            // Unsequenced. The original also resets the stored payload length here.
            _lastPayload = [];
            payload = [];
        }
        else
        {
            if (type == Accept)
                wireType = Accept | FlagAckRequest;
            seq = ++_lastSentSeq;
            _slots[seq & 31] = new Slot { SendTick = nowMs, Type = type, Acked = false, Cookie = cookie };
            withId = CarriesId(type);
            _lastPayload = payload.ToArray();
        }

        bs.WriteFlag(true);
        bs.WriteInt(_id & 1, 1);
        bs.WriteInt(seq, 9);
        bs.WriteInt(_ackedByPeer, 5);

        // One range per run of received sequences, scanning from lastRecv + 2 around the ring.
        var stop = (_lastRecvSeq + 1) & 31;
        var i = stop;
        while (true)
        {
            i = (i + 1) & 31;
            if (i == stop)
                break;
            if (!_received[i])
                continue;
            var start = i;
            uint count = 0;
            while (i != stop && _received[i])
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
            bs.WriteInt(_id, 32);

        var header = bs.BytePosition();
        if (header + payload.Length > buf.Length)
            return;
        payload.CopyTo(buf.AsSpan(header));
        _send(_peer, buf.AsSpan(0, header + payload.Length));
    }

    // src/net/connection.cpp Connection::flush.
    private void Flush()
    {
        for (var i = (_lastSentSeq + 1) & 31; i != (_lastSentSeq & 31); i = (i + 1) & 31)
        {
            if (_slots[i].Type == Free)
                continue;
            if (_slots[i].Cookie != 0)
                _events.OnNotify(this, _slots[i].Cookie, false);
            _slots[i].Type = Free;
        }
        _state = ConnectionState.Idle;
    }

    /// <summary>Handles one datagram from the peer: the sequence and ack ranges, the handshake and the payload.</summary>
    /// <param name="data">The datagram.</param>
    /// <param name="nowMs">The current time in milliseconds.</param>
    public void Receive(ReadOnlySpan<byte> data, uint nowMs)
    {
        var copy = data.ToArray();
        var size = copy.Length;
        var bs = new BitBuffer(copy, size);
        if (!bs.ReadFlag())
            return;
        var idBit = bs.ReadInt(1);
        var seq = bs.ReadInt(9);
        var peerAckAck = bs.ReadInt(5);

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
                _events.OnAckRangeOverflow(this); // the original's stack array holds 20 ranges
                return;
            }
            rangeStart[(int)rangeCount] = bs.ReadInt(5);
            rangeCountOf[(int)rangeCount] = cnt;
            ++rangeCount;
        }
        var rawType = bs.ReadInt(5);
        if (bs.Error())
            return;
        var needAck = (rawType == KeepAlive || (rawType & FlagAckRequest) != 0) && (rawType & FlagAckReply) == 0;
        var type = rawType & ~(FlagAckRequest | FlagAckReply);
        if (type == Accept)
            needAck = true;

        ReadOnlySpan<byte> payload = [];
        void TakePayload(ReadOnlySpan<byte> d, ref ReadOnlySpan<byte> p)
        {
            var at = bs.BytePosition();
            p = at < d.Length ? d[at..] : [];
        }

        if (type == ConnectRequest)
        {
            var id = bs.ReadInt(32);
            TakePayload(data, ref payload);
            if (_state != ConnectionState.Idle && id != _id)
            {
                // A new connection id from the same address replaces the old connection.
                Flush();
                _events.OnClosed(this, [], true);
                _dead = true;
                return;
            }
            if (_state == ConnectionState.Idle)
                _id = id;
        }
        if ((_id & 1) != idBit)
            return; // "packet ordering incorrect"

        // Extend the 9-bit sequence into the window [lastRecv, lastRecv + 30].
        var full = seq | (_lastRecvSeq & ~0x1ffu);
        var windowEnd = _lastRecvSeq + 30;
        if (full < _lastRecvSeq || full > windowEnd)
        {
            full = seq | (windowEnd & ~0x1ffu);
            if (full < _lastRecvSeq || full > windowEnd)
                return; // "bad seqNum"
        }

        for (var s = _lastRecvSeq + 1; s < full; ++s)
            _received[s & 31] = false;
        if (type != KeepAlive)
            _received[full & 31] = true;
        for (var i = (full + 1) & 31; i != ((peerAckAck + 1) & 31); i = (i + 1) & 31)
            _received[i] = false;
        var gap = (int)(full & 31) - (int)peerAckAck;
        if (gap < 0)
            gap += 32;
        if (gap > 11)
            needAck = true;

        if (rangeCount != 0)
        {
            uint end = 0;
            for (uint r = 0; r < rangeCount; ++r)
            {
                var s = rangeStart[(int)r];
                for (uint c = 0; c < rangeCountOf[(int)r]; ++c, s = (s + 1) & 31)
                    if (_slots[s].Type != Free)
                        _slots[s].Acked = true;
                end = s;
            }
            _oldestUnacked = rangeStart[0];
            var i = (_lastSentSeq + 1) & 31;
            do
            {
                ref var slot = ref _slots[i];
                if (slot.Type != Free)
                {
                    if (!slot.Acked)
                    {
                        _loss = 0.01f * ((_loss * 99.0f) + 1.0f);
                        if (slot.Cookie != 0)
                            _events.OnNotify(this, slot.Cookie, false);
                        slot.Type = Free;
                    }
                    else
                    {
                        _loss = 0.01f * _loss * 99.0f;
                        if (slot.Cookie != 0)
                            _events.OnNotify(this, slot.Cookie, true);
                        if ((slot.Type & ~FlagAckRequest) == Accept && _state == ConnectionState.AcceptSent)
                        {
                            _state = ConnectionState.Connected;
                            _events.OnEstablished(this);
                        }
                        slot.Type = Free;
                        _rtt = (nowMs - slot.SendTick + (_rtt * 31)) >> 5;
                    }
                }
                i = (i + 1) & 31;
            } while (i != end);
            _ackedByPeer = (end - 1) & 31;
        }

        _lastRecvTick = nowMs;
        _retries = 0;
        var isNew = _lastRecvSeq != full;
        _lastRecvSeq = full;

        if (needAck)
        {
            if (_slots[_lastSentSeq & 31].Type == Free)
                Transmit(FlagAckReply, [], nowMs, 0);
            else
                Transmit(FlagAckRequest | FlagAckReply, [], nowMs, 0);
        }
        if (!isNew)
            return;

        if (type == ConnectRequest && _state == ConnectionState.Idle)
        {
            _state = ConnectionState.Requested;
            _events.OnConnectRequest(this, payload);
        }
        else if ((type == Disconnect && _state == ConnectionState.Connected) ||
                 ((type == Accept || type == Reject) && _state == ConnectionState.RequestSent))
        {
            var id = bs.ReadInt(32);
            TakePayload(data, ref payload);
            if (id != _id)
                return;
            if (type == Accept)
            {
                _events.OnAccepted(this, payload);
                _state = ConnectionState.Connected;
                return;
            }
            Flush();
            if (type == Reject)
                _events.OnRejected(this, payload);
            else
                _events.OnClosed(this, payload, false);
            _dead = true;
        }
        else if (type == Data && _state == ConnectionState.Connected)
        {
            TakePayload(data, ref payload);
            _events.OnData(this, payload);
        }
    }

    /// <summary>Sends a keep alive or retransmit after silence. On timeout the connection is flushed, reported and marked dead.</summary>
    /// <param name="nowMs">The current time in milliseconds.</param>
    public void Tick(uint nowMs)
    {
        if (_dead || nowMs <= _lastRecvTick + (3000 * (_retries + 1)))
            return;
        if (_slots[_lastSentSeq & 31].Type == Free)
            Transmit(KeepAlive, [], nowMs, 0);
        else
            Transmit(FlagAckRequest, [], nowMs, 0);
        if (++_retries > MaxRetries)
        {
            Flush();
            _events.OnClosed(this, [], true);
            _dead = true;
        }
    }

    /// <summary>Gets the peer address.</summary>
    /// <returns>The address.</returns>
    public Address Peer() => _peer;
    /// <summary>Gets the handshake state.</summary>
    /// <returns>The state.</returns>
    public ConnectionState State() => _state;
    /// <summary>Tells whether the connection ended and can be removed.</summary>
    /// <returns>True when dead.</returns>
    public bool Dead() => _dead;
    /// <summary>Gets the connection id.</summary>
    /// <returns>The id.</returns>
    public uint Id() => _id;
    /// <summary>Gets the smoothed round trip time.</summary>
    /// <returns>The time in milliseconds.</returns>
    public uint RttMs() => _rtt;
    /// <summary>Gets the smoothed packet loss.</summary>
    /// <returns>The loss as a fraction.</returns>
    public float PacketLoss() => _loss;
}
