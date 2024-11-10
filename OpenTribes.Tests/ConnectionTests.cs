// Port of tests/test_connection.cpp: VC protocol unit tests (docs/net-protocol.md section 3, DECISIONS D11).
using OpenTribes.Core;
using OpenTribes.Net;

namespace OpenTribes.Tests;

public class ConnectionTests
{
    private sealed class Recorder : ConnectionEvents
    {
        public readonly List<string> Requests = new(), Accepted = new(), Rejected = new(), Data = new();
        public readonly List<(uint cookie, bool delivered)> Notifies = new();
        public int Established, Closed, Overflows;
        public bool ClosedTimedOut;
        public string CloseReason = "";

        private static string Str(ReadOnlySpan<byte> p) => ByteString.FromBytes(p);
        public override void OnConnectRequest(Connection c, ReadOnlySpan<byte> p) => Requests.Add(Str(p));
        public override void OnAccepted(Connection c, ReadOnlySpan<byte> p) => Accepted.Add(Str(p));
        public override void OnRejected(Connection c, ReadOnlySpan<byte> p) => Rejected.Add(Str(p));
        public override void OnEstablished(Connection c) => ++Established;
        public override void OnClosed(Connection c, ReadOnlySpan<byte> p, bool timedOut)
        {
            ++Closed;
            ClosedTimedOut = timedOut;
            CloseReason = Str(p);
        }
        public override void OnData(Connection c, ReadOnlySpan<byte> p) => Data.Add(Str(p));
        public override void OnNotify(Connection c, uint cookie, bool delivered) => Notifies.Add((cookie, delivered));
        public override void OnAckRangeOverflow(Connection c) => ++Overflows;
    }

    private sealed class Sink
    {
        public readonly List<byte[]> Sent = new();
        public SendFn Fn() => (Address _, ReadOnlySpan<byte> d) => Sent.Add(d.ToArray());
        public VcPacket Last() => VcPacket.Parse(Sent[^1]);
    }

    private static readonly Address kPeer = new(0x0a000001, 1234);
    private const uint kClientId = 0x12345679; // odd: header id bit 1

    private static byte[] B(string s) => ByteString.ToBytes(s);

    private static void Deliver(Connection c, VcPacket p, uint now)
    {
        byte[] d = VcPacket.Build(p);
        c.Receive(d, now);
    }

    private static VcPacket Pkt(uint seq, uint type, List<(uint, uint)>? ranges = null, string payload = "", uint ackAck = 0)
    {
        return new VcPacket
        {
            IdBit = kClientId & 1,
            Seq = seq & 0x1ff,
            AckAck = ackAck,
            Ranges = ranges ?? new List<(uint, uint)>(),
            Type = type,
            Id = kClientId,
            Payload = VcPacket.Bytes(payload),
        };
    }

    private static List<(uint, uint)> R(params (uint, uint)[] r) => r.ToList();

    // Server-side connection taken through 3.5 with raw packets: request seq 1, accept seq 1, ack.
    private static void Establish(Connection c, Recorder ev, Sink sink, uint now)
    {
        Deliver(c, Pkt(1, Connection.ConnectRequest, null, "req"), now);
        Assert.True(c.State() == ConnectionState.Requested && ev.Requests.Count == 1);
        byte[] accept = { 1, 2, 3, 4, 5, 6, 7, 8 };
        Assert.True(c.Send(Connection.Accept, accept, now));
        Assert.Equal(ConnectionState.AcceptSent, c.State());
        Deliver(c, Pkt(1, Connection.FlagAckReply, R((1, 1))), now);
        Assert.True(c.State() == ConnectionState.Connected && ev.Established == 1);
        sink.Sent.Clear();
    }

    [Fact]
    public void TestConnection()
    {
        int cases = 0;

        // 3.2: header field order and sizes; data carries no id, payload follows the 3-byte header.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, kClientId, ev, sink.Fn(), 0);
            byte[] payload = { (byte)'a', (byte)'b', (byte)'c' };
            Assert.True(c.Send(Connection.Data, payload, 0));
            byte[] want = { 0x07, 0x00, 0x00, (byte)'a', (byte)'b', (byte)'c' };
            Assert.True(sink.Sent.Count == 1 && sink.Sent[0].SequenceEqual(want));
            ++cases;
        }

        // 3.3: connect request consumes a sequence, carries the 32-bit id, client state 0 -> 2.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, kClientId, ev, sink.Fn(), 0);
            byte[] payload = { 9, 9 };
            c.Send(Connection.ConnectRequest, payload, 0);
            VcPacket p = sink.Last();
            Assert.True(p.Type == 3 && p.HasId && p.Id == kClientId && p.Seq == 1 && p.IdBit == 1);
            Assert.True(p.HeaderBytes == 7 && p.Payload.Length == 2);
            Assert.Equal(ConnectionState.RequestSent, c.State());
            ++cases;
        }

        // 3.5: full handshake between two connections, then data both ways (3.4 dispatch table).
        {
            var cev = new Recorder();
            var sev = new Recorder();
            var wire = new Queue<(bool toServer, byte[] bytes)>();
            var client = new Connection(kPeer, kClientId, cev, (Address _, ReadOnlySpan<byte> d) => wire.Enqueue((true, d.ToArray())), 0);
            var server = new Connection(kPeer, 77, sev, (Address _, ReadOnlySpan<byte> d) => wire.Enqueue((false, d.ToArray())), 0);
            var seen = new List<VcPacket>();
            void Pump(uint now)
            {
                while (wire.Count != 0)
                {
                    var (toServer, bytes) = wire.Dequeue();
                    seen.Add(VcPacket.Parse(bytes));
                    (toServer ? server : client).Receive(bytes, now);
                }
            }
            client.Send(Connection.ConnectRequest, B("hello"), 0);
            Pump(10);
            Assert.True(sev.Requests.Count == 1 && sev.Requests[0] == "hello");
            Assert.True(server.State() == ConnectionState.Requested && server.Id() == kClientId);

            byte[] accept = { 1, 0, 0, 0, 2, 0, 0, 0 };
            server.Send(Connection.Accept, accept, 20);
            Assert.Equal(ConnectionState.AcceptSent, server.State());
            seen.Clear();
            Pump(30);
            Assert.Equal(2, seen.Count);
            Assert.True(seen[0].Type == 0x0c && seen[0].HasId && seen[0].Seq == 1); // accept sent as 4 + 0x08
            Assert.True(seen[1].Type == 0x10 && !seen[1].HasId);                   // pure ack from the client
            Assert.True(cev.Accepted.Count == 1 && cev.Accepted[0] == ByteString.FromBytes(accept));
            Assert.Equal(ConnectionState.Connected, client.State());
            Assert.True(server.State() == ConnectionState.Connected && sev.Established == 1);

            server.Send(Connection.Data, B("s2c"), 40, 5);
            Pump(45);
            Assert.Empty(sev.Notifies);
            client.Send(Connection.Data, B("c2s"), 40);
            Pump(50);
            Assert.True(cev.Data.Count == 1 && cev.Data[0] == "s2c");
            Assert.True(sev.Data.Count == 1 && sev.Data[0] == "c2s");
            // The client's data packet acks the server's seq 2: cookie 5 delivered.
            Assert.True(sev.Notifies.Count == 1 && sev.Notifies[0] == (5u, true));
            ++cases;
        }

        // 3.4 step 6 / 3.1: unacked slots before the last range are lost, acked ones delivered;
        // loss average and RTT update.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, 0, ev, sink.Fn(), 1000);
            Establish(c, ev, sink, 1000);
            float loss0 = c.PacketLoss();
            uint rtt0 = c.RttMs();
            c.Send(Connection.Data, ReadOnlySpan<byte>.Empty, 1000, 21); // seq 2
            c.Send(Connection.Data, ReadOnlySpan<byte>.Empty, 1000, 22); // seq 3
            Deliver(c, Pkt(2, Connection.Data, R((1, 3))), 1320);
            Assert.Equal(2, ev.Notifies.Count);
            Assert.True(ev.Notifies[0] == (21u, false) && ev.Notifies[1] == (22u, true));
            float lost = 0.01f * (loss0 * 99.0f + 1.0f);
            float want = 0.01f * lost * 99.0f;
            Assert.True(MathF.Abs(c.PacketLoss() - want) < 1e-6f);
            Assert.Equal((320 + rtt0 * 31) >> 5, c.RttMs());
            ++cases;
        }

        // 3.3: at most 31 sequenced packets in flight.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, 0, ev, sink.Fn(), 0);
            for (int i = 0; i < 31; ++i)
                Assert.True(c.Send(Connection.Data, ReadOnlySpan<byte>.Empty, 0));
            Assert.False(c.Send(Connection.Data, ReadOnlySpan<byte>.Empty, 0));
            Assert.Equal(31, sink.Sent.Count);
            ++cases;
        }

        // 3.4 step 4: 9-bit sequences extend across the 512 wrap; duplicates and sequences
        // outside [lastRecv, lastRecv + 30] are not delivered.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, 0, ev, sink.Fn(), 0);
            Establish(c, ev, sink, 0);
            uint seq = 1;
            for (int i = 0; i < 520; ++i)
            {
                ++seq;
                Deliver(c, Pkt(seq, Connection.Data, null, "d", (seq - 1) & 31), 0);
            }
            Assert.Equal(520, ev.Data.Count); // seq 512 arrived as 9-bit 0 and was accepted
            Deliver(c, Pkt(seq, Connection.Data, null, "dup", (seq - 1) & 31), 0);
            Assert.Equal(520, ev.Data.Count);
            Deliver(c, Pkt(seq + 31, Connection.Data, null, "far", 0), 0);
            Assert.Equal(520, ev.Data.Count);
            Deliver(c, Pkt(seq - 1, Connection.Data, null, "old", 0), 0);
            Assert.Equal(520, ev.Data.Count);
            Deliver(c, Pkt(seq + 30, Connection.Data, null, "edge", (seq + 29) & 31), 0);
            Assert.True(ev.Data.Count == 521 && ev.Data[^1] == "edge");
            ++cases;
        }

        // 3.4 step 5: a gap of 12 or more between the sequence and the ack-of-ack forces an ack;
        // step 8: a pure ack (0x10) when the newest slot is free.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, 0, ev, sink.Fn(), 0);
            Establish(c, ev, sink, 0);
            Deliver(c, Pkt(13, Connection.Data, null, "", (13 - 11) & 31), 0);
            Assert.Empty(sink.Sent);
            Deliver(c, Pkt(14, Connection.Data, null, "", (14 - 12) & 31), 0);
            Assert.True(sink.Sent.Count == 1 && sink.Last().Type == 0x10 && sink.Last().Seq == 1);
            ++cases;
        }

        // 3.4 steps 1 and 8: a keepalive requests an ack; with the newest slot pending the answer
        // is that packet retransmitted as type | 0x18 with the same sequence and payload.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, 0, ev, sink.Fn(), 0);
            Establish(c, ev, sink, 0);
            c.Send(Connection.Data, B("zz"), 0);
            Deliver(c, Pkt(1, Connection.KeepAlive), 0);
            VcPacket p = sink.Last();
            Assert.True(sink.Sent.Count == 2 && p.Type == 0x18 && p.Seq == 2 && VcPacket.Bytes("zz").SequenceEqual(p.Payload));
            // A packet already flagged 0x10 never triggers an answer.
            Deliver(c, Pkt(1, Connection.FlagAckRequest | Connection.FlagAckReply), 0);
            Assert.Equal(2, sink.Sent.Count);
            ++cases;
        }

        // 3.2: ack ranges from lastRecv + 2 around the ring, runs split at 7; keepalives are not
        // marked received.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, 0, ev, sink.Fn(), 0);
            Establish(c, ev, sink, 0);
            for (uint s = 2; s <= 11; ++s)
                Deliver(c, Pkt(s, Connection.Data), 0);
            Deliver(c, Pkt(12, Connection.KeepAlive), 0); // answered with an ack; 12 not marked
            sink.Sent.Clear();
            c.Send(Connection.Data, ReadOnlySpan<byte>.Empty, 0);
            VcPacket p = sink.Last();
            Assert.Equal(2, p.Ranges.Count);
            Assert.True(p.Ranges[0] == (7u, 1u) && p.Ranges[1] == (4u, 8u));
            ++cases;
        }

        // D11: more than 20 ack ranges drops the packet (reported); 20 are accepted.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, 0, ev, sink.Fn(), 0);
            Establish(c, ev, sink, 0);
            var ranges = Enumerable.Repeat((1u, 20u), 21).ToList();
            Deliver(c, Pkt(2, Connection.Data, ranges, "x"), 0);
            Assert.True(ev.Data.Count == 0 && ev.Overflows == 1);
            ranges.RemoveRange(20, ranges.Count - 20);
            Deliver(c, Pkt(2, Connection.Data, ranges, "x"), 0);
            Assert.True(ev.Data.Count == 1 && ev.Overflows == 1);
            ++cases;
        }

        // 3.4 step 3: a header id bit that differs from the stored id's low bit is dropped.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, 0, ev, sink.Fn(), 0);
            Establish(c, ev, sink, 0);
            VcPacket p = Pkt(2, Connection.Data, null, "x");
            p.IdBit ^= 1;
            Deliver(c, p, 0);
            Assert.Empty(ev.Data);
            ++cases;
        }

        // 3.4 step 2: a connect request with a new id on a non-idle VC drops the old connection
        // (status 4) without processing the packet. 3.6: the flush reports pending packets lost,
        // except the newest slot (the original's loop stops before lastSent).
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, 0, ev, sink.Fn(), 0);
            Establish(c, ev, sink, 0);
            c.Send(Connection.Data, ReadOnlySpan<byte>.Empty, 0, 33);
            c.Send(Connection.Data, ReadOnlySpan<byte>.Empty, 0, 34);
            VcPacket p = Pkt(1, Connection.ConnectRequest, null, "again");
            p.Id = kClientId + 2; // same low bit
            Deliver(c, p, 0);
            Assert.True(ev.Closed == 1 && ev.ClosedTimedOut && c.Dead());
            Assert.Single(ev.Requests);
            Assert.True(ev.Notifies.Count == 1 && ev.Notifies[0] == (33u, false)); // flushed
            ++cases;
        }

        // 3.4 dispatch: type 0 is delivered only in state 3.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, kClientId, ev, sink.Fn(), 0);
            Deliver(c, Pkt(1, Connection.Data, null, "early"), 0);
            Assert.Empty(ev.Data);
            ++cases;
        }

        // 3.4 dispatch / 5.3: a reject (id must match) in state 2 raises status 2 and kills the VC.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, kClientId, ev, sink.Fn(), 0);
            c.Send(Connection.ConnectRequest, ReadOnlySpan<byte>.Empty, 0);
            VcPacket bad = Pkt(1, Connection.Reject, R((1, 1)), "no\0");
            bad.Id = kClientId + 2;
            Deliver(c, bad, 0);
            Assert.True(ev.Rejected.Count == 0 && !c.Dead());
            var c2 = new Connection(kPeer, kClientId, ev, sink.Fn(), 0);
            c2.Send(Connection.ConnectRequest, ReadOnlySpan<byte>.Empty, 0);
            Deliver(c2, Pkt(1, Connection.Reject, R((1, 1)), "Wrong password.\0"), 0);
            Assert.True(ev.Rejected.Count == 1 && ev.Rejected[0] == "Wrong password.\0");
            Assert.True(c2.Dead());
            ++cases;
        }

        // 3.4 dispatch / 5.5: a disconnect in state 3 raises status 5 (timedOut = 0) with the reason.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, 0, ev, sink.Fn(), 0);
            Establish(c, ev, sink, 0);
            Deliver(c, Pkt(2, Connection.Disconnect, null, "bye\0"), 0);
            Assert.True(ev.Closed == 1 && !ev.ClosedTimedOut && ev.CloseReason == "bye\0" && c.Dead());
            ++cases;
        }

        // 5.3: close() sends the reason as raw bytes with the trailing NUL, then the VC is dead.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, 0, ev, sink.Fn(), 0);
            Deliver(c, Pkt(1, Connection.ConnectRequest), 0);
            c.Close(Connection.Reject, "Server is full.", 0);
            VcPacket p = sink.Last();
            Assert.True(p.Type == 5 && p.HasId && p.Id == kClientId && p.Payload.SequenceEqual(VcPacket.Bytes("Server is full.", true)));
            Assert.True(c.Dead());
            ++cases;
        }

        // 3.6: keepalive after 3000 ms of silence, then every 3000 ms; the 16th unanswered send
        // times the VC out (status 4) and flushes pending packets as lost.
        {
            var ev = new Recorder();
            var sink = new Sink();
            const uint t0 = 5000;
            var c = new Connection(kPeer, 0, ev, sink.Fn(), t0);
            Establish(c, ev, sink, t0);
            c.Tick(t0 + 3000);
            Assert.Empty(sink.Sent);
            c.Tick(t0 + 3001);
            Assert.True(sink.Sent.Count == 1 && sink.Last().Type == 7 && sink.Last().Seq == 1);
            c.Tick(t0 + 6000);
            Assert.Single(sink.Sent);
            // A valid packet resets the retry count.
            Deliver(c, Pkt(1, Connection.FlagAckReply), t0 + 6000);
            c.Tick(t0 + 9000);
            Assert.Single(sink.Sent);
            uint @base = t0 + 6000;
            for (uint n = 1; n <= 15; ++n)
            {
                c.Tick(@base + 3000 * n + 1);
                Assert.True(sink.Sent.Count == 1 + n && ev.Closed == 0);
            }
            c.Tick(@base + 3000 * 16 + 1);
            Assert.True(sink.Sent.Count == 17 && ev.Closed == 1 && ev.ClosedTimedOut && c.Dead());
            ++cases;
        }

        // 3.6 / 3.5: with the newest slot pending, the timeout retransmits it with flag 0x08
        // (a connect request goes out as 0x0b with the same sequence, payload and id). The
        // newest slot is not reported by the timeout flush.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, kClientId, ev, sink.Fn(), 0);
            c.Send(Connection.ConnectRequest, B("req"), 0, 44);
            c.Tick(3001);
            VcPacket p = sink.Last();
            Assert.True(p.Type == 0x0b && p.Seq == 1 && p.HasId && p.Id == kClientId && p.Payload.SequenceEqual(VcPacket.Bytes("req")));
            for (uint n = 2; n <= 16; ++n)
                c.Tick(3000 * n + 1);
            Assert.True(ev.Closed == 1 && ev.ClosedTimedOut && ev.Notifies.Count == 0);
            ++cases;
        }

        // ROADMAP G31, net-protocol.md U8: the retransmit repeats the payload of the newest packet after an
        // intervening ack of older packets and after a second keepalive; once the packet is acked a keepalive is
        // answered with an empty pure ack that reuses the newest sequence number; the next packet's payload is the
        // one a later retransmit repeats.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, 0, ev, sink.Fn(), 0);
            Establish(c, ev, sink, 0);
            c.Send(Connection.Data, B("d1"), 10); // seq 2
            Deliver(c, Pkt(1, Connection.KeepAlive), 11);
            Assert.True(sink.Sent.Count == 2 && sink.Last().Type == 0x18 && sink.Last().Seq == 2);
            Assert.Equal(VcPacket.Bytes("d1"), sink.Last().Payload);
            Deliver(c, Pkt(2, Connection.Data, R((1, 1)), "m"), 12); // acks only the old accept slot
            Deliver(c, Pkt(2, Connection.KeepAlive), 13);
            Assert.True(sink.Last().Type == 0x18 && sink.Last().Seq == 2 && sink.Last().Payload.SequenceEqual(VcPacket.Bytes("d1")));
            Deliver(c, Pkt(2, Connection.Data | Connection.FlagAckRequest, R((2, 1)), "m"), 14); // acks seq 2
            VcPacket pure = sink.Last();
            Assert.True(pure.Type == 0x10 && pure.Seq == 2 && pure.Payload.Length == 0 && pure.HeaderBytes == sink.Sent[^1].Length);
            Deliver(c, Pkt(2, Connection.KeepAlive), 15);
            Assert.True(sink.Last().Type == 0x10 && sink.Last().Seq == 2 && sink.Last().Payload.Length == 0);
            c.Send(Connection.Data, B("d2"), 16); // the pure acks took no sequence
            Assert.Equal(3u, sink.Last().Seq);
            Deliver(c, Pkt(2, Connection.KeepAlive), 17);
            Assert.True(sink.Last().Type == 0x18 && sink.Last().Seq == 3 && sink.Last().Payload.SequenceEqual(VcPacket.Bytes("d2")));
            ++cases;
        }

        // ROADMAP G31: a sequence below the newest one received, one more than 30 ahead, and a packet with the
        // other id bit are dropped without a delivery and without an answer, even with the ack request flag. A
        // repeat of the newest sequence is not delivered again but is answered.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, 0, ev, sink.Fn(), 0);
            Establish(c, ev, sink, 0);
            const uint req = Connection.Data | Connection.FlagAckRequest;
            Deliver(c, Pkt(4, req, null, "a"), 1); // 2 and 3 skipped
            Assert.True(ev.Data.Count == 1 && sink.Sent.Count == 1 && sink.Last().Type == 0x10);
            Deliver(c, Pkt(3, req, null, "late"), 2);
            Deliver(c, Pkt(2, req, null, "late"), 2);
            Deliver(c, Pkt(4 + 31, req, null, "far"), 2);
            VcPacket other = Pkt(5, req, null, "bit");
            other.IdBit ^= 1;
            Deliver(c, other, 2);
            Assert.True(ev.Data.Count == 1 && sink.Sent.Count == 1);
            Deliver(c, Pkt(4, req, null, "a"), 3); // repeat
            Assert.True(ev.Data.Count == 1 && sink.Sent.Count == 2 && sink.Last().Type == 0x10);
            Deliver(c, Pkt(4 + 30, req, null, "edge"), 4);
            Assert.True(ev.Data.Count == 2 && ev.Data[^1] == "edge" && sink.Sent.Count == 3);
            ++cases;
        }

        // net-protocol.md U7: a pure ack (0x10) with a new sequence number reaches the type 0 path and delivers an
        // empty payload; a keepalive with a new sequence number delivers nothing and takes the number.
        {
            var ev = new Recorder();
            var sink = new Sink();
            var c = new Connection(kPeer, 0, ev, sink.Fn(), 0);
            Establish(c, ev, sink, 0);
            Deliver(c, Pkt(2, Connection.FlagAckReply), 1);
            Assert.True(ev.Data.Count == 1 && ev.Data[0].Length == 0 && sink.Sent.Count == 0);
            Deliver(c, Pkt(3, Connection.KeepAlive), 2);
            Assert.True(ev.Data.Count == 1 && sink.Sent.Count == 1 && sink.Last().Type == 0x10);
            Deliver(c, Pkt(3, Connection.Data, null, "dup of the keepalive number"), 3);
            Assert.Single(ev.Data); // the number is taken
            Deliver(c, Pkt(4, Connection.Data, null, "next"), 4);
            Assert.True(ev.Data.Count == 2 && ev.Data[^1] == "next");
            ++cases;
        }
    }
}
