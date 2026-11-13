// Port of tests/test_game_stream.cpp: GameConnection stream, event manager and event builders (docs/game-stream.md 1-3).
// 116 asserts.
namespace OpenTribes.Tests;

using OpenTribes.Game;
using OpenTribes.Net;

public class GameStreamTests
{
    // ---- server packets ----

    private sealed class Fake
    {
        public bool Connected { get; set; } = true;
        public List<(byte[] Data, uint Cookie)> Sent { get; } = [];

        public GameConnection.Transport Transport() => new()
        {
            Connected = () => Connected,
            Send = (d, n, _, cookie) =>
            {
                Sent.Add((d.AsSpan(0, n).ToArray(), cookie));
                return true;
            },
        };
    }

    private sealed class Ev
    {
        public bool Guaranteed { get; set; }
        public bool Ordered { get; set; }
        public bool Next { get; set; }
        public int Seq { get; set; } = -2;
        public uint HeaderBits { get; set; }
        public uint Tag { get; set; }
        public uint Body { get; set; }
    }

    private sealed class Packet
    {
        public bool RateSet { get; set; }
        public bool RateReq { get; set; }
        public bool HasEvents { get; set; }
        public bool HasPsc { get; set; }
        public bool Control { get; set; }
        public bool Ghosts { get; set; }
        public List<Ev> Events { get; } = [];
        public uint Mode { get; set; }
        public uint LastMove { get; set; }
        public uint BitsCount { get; set; }
        public bool Flag6a { get; set; }
        public int Bytes { get; set; }
        public uint SetBits { get; set; } // the 10 + 10 bits of the rate sections: interval | size << 10
        public uint ReqBits { get; set; } // the 10 + 10 bits of the rate sections: interval | size << 10
    }

    // Decodes a server packet whose events all carry an 8-bit body (2.1, 1.2, 6.3).
    private static Packet Decode(byte[] d)
    {
        var p = new Packet();
        var b = new Bits(d, (uint)(d.Length * 8));
        p.Bytes = d.Length;
        if (p.RateSet = b.Flag())
            p.SetBits = b.U(20);
        if (p.RateReq = b.Flag())
            p.ReqBits = b.U(20);
        if (p.HasEvents = b.Flag())
        {
            var prev = -4;
            while (b.Flag())
            {
                var e = new Ev();
                var start = b.Pos() - 1;
                e.Guaranteed = b.Flag();
                if (e.Guaranteed)
                {
                    e.Next = b.Flag();
                    if (e.Next)
                    {
                        e.Seq = (prev + 1) & 127;
                        e.Ordered = true;
                    }
                    else if (b.Flag())
                    {
                        e.Seq = (int)b.U(7);
                        e.Ordered = true;
                    }
                    prev = e.Seq;
                }
                e.Tag = b.U(7) + 0x400;
                e.HeaderBits = b.Pos() - start;
                e.Body = b.U(8);
                p.Events.Add(e);
            }
        }
        if (p.HasPsc = b.Flag())
        {
            p.Mode = b.U(3);
            p.Flag6a = b.Flag();
            p.LastMove = b.U(32);
            p.Control = b.Flag();
        }
        p.Ghosts = b.Flag();
        p.BitsCount = b.Pos();
        Assert.True(p.Bytes == (p.BitsCount + 7) / 8);
        return p;
    }

    private static OutEvent EvOf(uint i, bool guaranteed = true, bool ordered = true)
    {
        var w = new EventWriter();
        w.PutInt(i & 255, 8);
        return w.Finish(0x401, guaranteed, ordered);
    }

    // ---- client packets ----

    private sealed class ClientPacket
    {
        public enum Kind { Ordered, Unordered, Unguaranteed }

        private readonly byte[] _buf = new byte[1500];
        private readonly BitBuffer _bs;

        public ClientPacket(bool rates = false)
        {
            _bs = new BitBuffer(_buf, _buf.Length);
            _bs.WriteFlag(rates); // rateSet: 10 + 10 bits
            if (rates)
                _bs.WriteInt(0x12345, 20);
            _bs.WriteFlag(rates); // rateReq
            if (rates)
                _bs.WriteInt(0xabcde, 20);
            _bs.WriteFlag(true);
        }

        // Event header (2.1): entry flag, delivery class, sequence, class tag.
        public ClientPacket Header(uint tag, Kind kind, int seq = -1, bool next = false)
        {
            _bs.WriteFlag(true);
            _bs.WriteFlag(kind != Kind.Unguaranteed);
            if (kind == Kind.Ordered)
            {
                _bs.WriteFlag(next);
                if (!next)
                {
                    _bs.WriteFlag(true);
                    _bs.WriteInt((uint)seq, 7);
                }
            }
            else if (kind == Kind.Unordered)
            {
                _bs.WriteFlag(false);
                _bs.WriteFlag(false);
            }
            _bs.WriteInt(tag - 0x400, 7);
            return this;
        }

        public ClientPacket Console(string[] argv, Kind kind, int seq = -1, bool next = false, uint tag = 0x408)
        {
            Header(tag, kind, seq, next);
            _bs.WriteInt((uint)argv.Length, 5);
            foreach (var s in argv)
                _bs.WriteString(s);
            return this;
        }

        // Destination prefix of a Sim event (2.4): form 0 object id (32 bits), 1 server ghost, 2 client ghost (10 bits).
        public ClientPacket Dest(int form, uint value)
        {
            if (!_bs.WriteFlag(form == 1) && !_bs.WriteFlag(form == 2))
                _bs.WriteInt(value, 32);
            else
                _bs.WriteInt(value, 10);
            return this;
        }

        // SimActionEvent 0x40b (2.6): the destination prefix is the whole body.
        public ClientPacket Action(int form, uint value, Kind kind, int seq = -1, bool next = false) =>
            Header(0x40b, kind, seq, next).Dest(form, value);

        public ClientPacket BitsOf(uint value, uint count)
        {
            _bs.WriteInt(value, count);
            return this;
        }

        public ClientPacket String(string s)
        {
            _bs.WriteString(s);
            return this;
        }

        // The packet ends here: no end flag, no other members; the last byte is padded with 0 bits.
        public byte[] Cut() => _buf.AsSpan(0, _bs.BytePosition()).ToArray();

        // Ends the events, then the PSC (6.2) with `moves` moves, then no ghosts.
        public byte[] Finish(int moves = -1)
        {
            _bs.WriteFlag(false);
            _bs.WriteFlag(moves >= 0);
            if (moves >= 0)
            {
                _bs.WriteInt(169, 8);
                _bs.WriteInt(0, 32);
                for (var i = 0; i < moves; ++i)
                {
                    _bs.WriteFlag(true);
                    if (i > 0)
                    {
                        _bs.WriteFlag(true); // same as previous
                    }
                    else
                    {
                        _bs.WriteInt(0, 16);
                        _bs.WriteInt(0, 3);
                    }
                    _bs.WriteFlag(false); // fire
                    _bs.WriteFlag(false); // useItem
                    _bs.WriteFlag(false); // pitch
                    _bs.WriteFlag(false); // turn
                }
                _bs.WriteFlag(false);
            }
            _bs.WriteFlag(false);
            Assert.False(_bs.Error());
            return _buf.AsSpan(0, _bs.BytePosition()).ToArray();
        }
    }

    private sealed class Calls : List<(string Fn, List<string> Args)> { }

    private static GameConnection.Hooks Capture(Calls calls) => new() { RemoteCall = (fn, args) => calls.Add((fn, args)) };

    private static void Deliver(GameConnection c, byte[] d) => c.Receive(d);

    private static bool SameArgs(List<string> a, params string[] b) => a.SequenceEqual(b);

    // ---- tests ----

    [Fact]
    public void TestEventWriter()
    {
        // net-protocol.md 4: LSB first, same bits as BitStream.
        var w = new EventWriter();
        w.PutFlag(true);
        w.PutInt(5, 3);
        w.PutInt(0xff, 8);
        Assert.True(w.Bits() == 12 && w.Bytes().Count == 2 && w.Bytes()[0] == 0xfb && w.Bytes()[1] == 0x0f);
        // str (net-protocol.md 4.1): bit-identical to BitStream::writeString.
        var s = new EventWriter();
        s.PutInt(1, 3);
        s.PutString("Tribes 1.11");
        s.PutBits([0xa5], 5);
        Assert.True(s.Bits() == 3 + Bits.StrBits("Tribes 1.11") + 5);
        var e = s.Finish(0x401);
        var b = new Bits(e);
        Assert.True(b.U(3) == 1 && b.Str() == "Tribes 1.11" && b.U(5) == 0x05 && b.Done());
        // finish resets the writer; an unguaranteed event is never ordered (2.2).
        Assert.True(s.Bits() == 0 && s.Bytes().Count == 0);
        var u = s.Finish(0x461, false, true);
        Assert.True(!u.Guaranteed && !u.Ordered && u.BodyBits == 0 && u.Seq == -1);
    }

    [Fact]
    public void TestBuilders()
    {
        // 2.5 SimConsoleEvent: int(5) argc, argc str; guaranteed ordered (2.3).
        var re = GameEvents.MakeRemoteEval(["SVInfo", "1.11", "host"]);
        Assert.True(re.Tag == 0x408 && re.Guaranteed && re.Ordered);
        {
            var b = new Bits(re);
            Assert.True(b.U(5) == 3 && b.Str() == "SVInfo" && b.Str() == "1.11" && b.Str() == "host" && b.Done());
        }
        // 2.5 TeamAddEvent: u32 +0x24 (0), u32 team (signed), str name, str skin.
        var ta = GameEvents.MakeTeamAdd(-1, "unnamed", "base");
        Assert.True(ta.Tag == 0x44f && ta.Ordered);
        {
            var b = new Bits(ta);
            Assert.True(b.U(32) == 0 && b.S32() == -1 && b.Str() == "unnamed" && b.Str() == "base" && b.Done());
        }
        // 2.5 DeltaScoreEvent: flag isTeam; int(4) team + 1 or int(7) id - 2048; u32 sort; str text.
        var dt = GameEvents.MakeDeltaScore(true, -1, 0, "Team\tScore");
        Assert.True(dt.Tag == 0x44d && dt.Ordered);
        {
            var b = new Bits(dt);
            Assert.True(b.Flag() && b.U(4) == 0 && b.U(32) == 0 && b.Str() == "Team\tScore" && b.Done());
        }
        var dc = GameEvents.MakeDeltaScore(false, 2050, -5, "%n\t%t");
        {
            var b = new Bits(dc);
            Assert.True(!b.Flag() && b.U(7) == 2 && b.S32() == -5 && b.Str() == "%n\t%t" && b.Done());
        }
        var d7 = GameEvents.MakeDeltaScore(true, 7, 3, "");
        {
            var b = new Bits(d7);
            Assert.True(b.Flag() && b.U(4) == 8 && b.U(32) == 3 && b.Str().Length == 0 && b.Done());
        }
        // 2.5 TargetNameEvent: int(7) index, str name; ordered (observed).
        var tn = GameEvents.MakeTargetName(3, "Main Generator");
        Assert.True(tn.Tag == 0x45d && tn.Ordered);
        {
            var b = new Bits(tn);
            Assert.True(b.U(7) == 3 && b.Str() == "Main Generator" && b.Done());
        }
        // SimPathEvent without a path (a SimPath removed): destination object 42, index, flag 0.
        {
            var pr = GameEvents.MakePathRemoved(3);
            Assert.True(pr.Tag == 0x425 && !pr.Ordered);
            var b = new Bits(pr);
            Assert.True(!b.Flag() && !b.Flag() && b.U(32) == 42 && b.U(32) == 3 && !b.Flag() && b.Done());
        }
        // Unnamed targets send no event.
        {
            var evs = GameEvents.MakeTargetNameEvents(["A", "", "", "B"]);
            Assert.Equal(2, evs.Count);
            Bits b0 = new(evs[0]), b1 = new(evs[1]);
            Assert.True(b0.U(7) == 0 && b0.Str() == "A" && b1.U(7) == 3 && b1.Str() == "B");
        }
        // player-manager.md 11 PlayerAddEvent: u32 id, flag isAI, str name, skin, voice, int(1) male, u32 commander (-1
        // none), flag + int(3) team (flag 0 = -1).
        var pa = GameEvents.MakePlayerAdd(2050, false, "Pilot", "base", "male2", true, -1, -1);
        Assert.True(pa.Tag == 0x44b && pa.Ordered);
        {
            var b = new Bits(pa);
            Assert.True(b.U(32) == 2050 && !b.Flag() && b.Str() == "Pilot" && b.Str() == "base" && b.Str() == "male2");
            Assert.True(b.U(1) == 1 && b.S32() == -1 && !b.Flag() && b.Done());
        }
        var pb = GameEvents.MakePlayerAdd(2051, true, "Bot", "beagle", "female1", false, 2050, 5);
        {
            var b = new Bits(pb);
            Assert.True(b.U(32) == 2051 && b.Flag() && b.Str() == "Bot" && b.Str() == "beagle" && b.Str() == "female1");
            Assert.True(b.U(1) == 0 && b.S32() == 2050 && b.Flag() && b.U(3) == 5 && b.Done());
        }
    }

    [Fact]
    public void TestIdlePacket()
    {
        // 1.2: idle server packet = rate 0, rate 0, events 0, PSC (mode, flag, lastMove, control 0), ghosts 0: 42 bits, 6
        // bytes. 6.3: lastMove -1 before any move.
        var f = new Fake();
        var calls = new Calls();
        var c = new GameConnection(2049, f.Transport(), Capture(calls))
        {
            GuiMode = 5,
            Flag6a = true
        };
        c.Tick(1000);
        Assert.Single(f.Sent);
        var p = Decode(f.Sent[0].Data);
        Assert.True(!p.RateSet && !p.RateReq && !p.HasEvents && p.HasPsc && !p.Ghosts);
        Assert.True(p.Mode == 5 && p.Flag6a && p.LastMove == 0xffffffff && !p.Control);
        Assert.True(p.BitsCount == 42 && p.Bytes == 6);
    }

    [Fact]
    public void TestPacing()
    {
        // 1.3: send when connected, now - lastSend >= 100 ms, fewer than 10 unacknowledged.
        var f = new Fake();
        var calls = new Calls();
        var c = new GameConnection(2049, f.Transport(), Capture(calls));
        c.Tick(1000);
        c.Tick(1099);
        Assert.Single(f.Sent);
        c.Tick(1100);
        Assert.Equal(2, f.Sent.Count);
        f.Connected = false;
        c.Tick(1200);
        Assert.Equal(2, f.Sent.Count);
        f.Connected = true;
        uint now = 1200;
        for (; f.Sent.Count < 10; now += 100)
            c.Tick(now);
        c.Tick(now);
        Assert.Equal(10, f.Sent.Count); // 10 unacknowledged
        c.Notify(f.Sent[0].Cookie, true);
        c.Tick(now);
        Assert.Equal(11, f.Sent.Count);
        // Every packet has its own cookie (1.5).
        for (var i = 1; i < f.Sent.Count; ++i)
            Assert.True(f.Sent[i].Cookie != f.Sent[i - 1].Cookie);
    }

    // A client packet with only rate sections: rateSet (interval, size) and rateReq (interval, size), -1 absent.
    private static byte[] RateOnly(int setInterval, int setSize, int reqInterval, int reqSize)
    {
        var buf = new byte[16];
        var w = new BitBuffer(buf, buf.Length);
        w.WriteFlag(setInterval >= 0);
        if (setInterval >= 0)
        {
            w.WriteInt((uint)setInterval, 10);
            w.WriteInt((uint)setSize, 10);
        }
        w.WriteFlag(reqInterval >= 0);
        if (reqInterval >= 0)
        {
            w.WriteInt((uint)reqInterval, 10);
            w.WriteInt((uint)reqSize, 10);
        }
        w.WriteFlag(false);
        w.WriteFlag(false);
        w.WriteFlag(false);
        return buf.AsSpan(0, w.BytePosition()).ToArray();
    }

    [Fact]
    public void TestRateSections()
    {
        // ROADMAP G26 (docs/game-stream.md 1.3, 1.5).
        {
            // Changed prefs are sent once as rateReq with the server's own interval and size, and again after a lost
            // packet.
            var f = new Fake();
            var calls = new Calls();
            var c = new GameConnection(2049, f.Transport(), Capture(calls));
            c.SetOwnRate(33, 450);
            c.Tick(1000);
            var p = Decode(f.Sent[0].Data);
            Assert.True(!p.RateSet && p.RateReq && p.ReqBits == (33u | (450u << 10)));
            c.Tick(1100);
            Assert.False(Decode(f.Sent[1].Data).RateReq);
            c.Notify(f.Sent[0].Cookie, false);
            c.Tick(1200);
            p = Decode(f.Sent[2].Data);
            Assert.True(p.RateReq && p.ReqBits == (33u | (450u << 10)));
            c.Notify(f.Sent[2].Cookie, true);
            c.Tick(1300);
            Assert.False(Decode(f.Sent[3].Data).RateReq);
            c.SetOwnRate(33, 450); // no change: nothing to send
            c.Tick(1400);
            Assert.False(Decode(f.Sent[4].Data).RateReq);
        }
        {
            // A client rateReq is answered with rateSet when it changes the active rate; the size is not clamped (0 stays
            // 0); a lost rateSet is sent again.
            var f = new Fake();
            var calls = new Calls();
            var c = new GameConnection(2049, f.Transport(), Capture(calls));
            c.Receive(RateOnly(-1, 0, 33, 450)); // max(33, 100) = 100, min(450, 200) = 200: unchanged
            c.Tick(1000);
            Assert.False(Decode(f.Sent[0].Data).RateSet);
            c.Receive(RateOnly(-1, 0, 200, 0));
            c.Tick(1200);
            var p = Decode(f.Sent[1].Data);
            Assert.True(p.RateSet && p.SetBits == (200u | (0u << 10)));
            c.Notify(f.Sent[1].Cookie, false);
            c.Tick(1400);
            p = Decode(f.Sent[2].Data);
            Assert.True(p.RateSet && p.SetBits == 200u);
            c.Tick(1600);
            Assert.False(Decode(f.Sent[3].Data).RateSet);
        }
        {
            // A client rateSet becomes the active rate: the server then writes one packet per 300 ms, 120 bytes.
            var f = new Fake();
            var calls = new Calls();
            var c = new GameConnection(2049, f.Transport(), Capture(calls));
            c.Receive(RateOnly(300, 120, -1, 0));
            c.Tick(1000);
            c.Tick(1299);
            Assert.Single(f.Sent);
            c.Tick(1300);
            Assert.Equal(2, f.Sent.Count);
            // An interval of 0 (the original divides by zero when it writes the next packet) is read as 1.
            c.Receive(RateOnly(0, 100, -1, 0));
            c.Tick(1301);
            Assert.Equal(3, f.Sent.Count);
        }
        {
            // Pacing: with interval 200 ms a packet is held back when 1000 / 200 = 5 is below the packets written since
            // the last delivered one (the counter starts at 1, so five packets are sent before the sixth is held).
            var f = new Fake();
            var calls = new Calls();
            var c = new GameConnection(2049, f.Transport(), Capture(calls));
            c.Receive(RateOnly(200, 200, -1, 0));
            for (uint now = 1000; now < 1000 + (20 * 200); now += 200)
                c.Tick(now);
            Assert.Equal(5, f.Sent.Count);
            c.Notify(f.Sent[0].Cookie, true);
            c.Tick(6000);
            Assert.Equal(6, f.Sent.Count);
            // A lost packet does not move the watermark.
            c.Notify(f.Sent[1].Cookie, false);
            c.Tick(6200);
            Assert.Equal(6, f.Sent.Count);
        }
    }

    [Fact]
    public void TestEventEncoding()
    {
        // 2.1: the first ordered event of a packet carries 7 explicit bits, consecutive ones only the nextSeq flag.
        // Header costs: unguaranteed 9, unordered 11, ordered 18 or 10. An unordered event has internal sequence -2, so
        // the ordered event after it is explicit.
        var f = new Fake();
        var calls = new Calls();
        var c = new GameConnection(2049, f.Transport(), Capture(calls));
        c.Post(EvOf(10));
        c.Post(EvOf(11));
        c.Post(EvOf(12, false));
        c.Post(EvOf(13));
        c.Post(EvOf(14, true, false));
        c.Post(EvOf(15));
        c.Tick(1000);
        var p = Decode(f.Sent[^1].Data);
        Assert.True(p.HasEvents && p.Events.Count == 6 && c.Queued() == 0);
        var e = p.Events;
        Assert.True(e[0].Ordered && !e[0].Next && e[0].Seq == 0 && e[0].HeaderBits == 18 && e[0].Body == 10);
        Assert.True(e[1].Ordered && e[1].Next && e[1].Seq == 1 && e[1].HeaderBits == 10 && e[1].Body == 11);
        Assert.True(!e[2].Guaranteed && e[2].HeaderBits == 9 && e[2].Body == 12);
        Assert.True(e[3].Ordered && e[3].Next && e[3].Seq == 2 && e[3].HeaderBits == 10);
        Assert.True(e[4].Guaranteed && !e[4].Ordered && e[4].HeaderBits == 11 && e[4].Body == 14);
        Assert.True(e[5].Ordered && !e[5].Next && e[5].Seq == 3 && e[5].HeaderBits == 18 && e[5].Body == 15);
        foreach (var x in p.Events)
            Assert.Equal(0x401u, x.Tag);
        // A new packet starts again at -4: its first ordered event is explicit.
        c.Post(EvOf(16));
        c.Tick(1100);
        var q = Decode(f.Sent[^1].Data);
        Assert.True(q.Events.Count == 1 && !q.Events[0].Next && q.Events[0].Seq == 4);
    }

    [Fact]
    public void TestResend()
    {
        // 2.2, 1.5: a lost packet's guaranteed events go back to the head of the queue in their original order with
        // their original sequences; unguaranteed events are not resent.
        var f = new Fake();
        var calls = new Calls();
        var c = new GameConnection(2049, f.Transport(), Capture(calls));
        c.Post(EvOf(1));
        c.Post(EvOf(2));
        c.Post(EvOf(3, true, false));
        c.Post(EvOf(4, false));
        c.Tick(1000);
        var lost = f.Sent[^1].Cookie;
        c.Post(EvOf(5));
        c.Notify(lost, false);
        Assert.Equal(4, c.Queued());
        c.Tick(1100);
        var p = Decode(f.Sent[^1].Data);
        Assert.Equal(4, p.Events.Count);
        Assert.True(p.Events[0].Body == 1 && p.Events[0].Seq == 0 && !p.Events[0].Next);
        Assert.True(p.Events[1].Body == 2 && p.Events[1].Seq == 1 && p.Events[1].Next);
        Assert.True(p.Events[2].Body == 3 && p.Events[2].Guaranteed && !p.Events[2].Ordered);
        Assert.True(p.Events[3].Body == 5 && p.Events[3].Seq == 2 && !p.Events[3].Next);
        // Delivered: nothing more to send.
        c.Notify(f.Sent[^1].Cookie, true);
        c.Tick(1200);
        Assert.False(Decode(f.Sent[^1].Data).HasEvents);
    }

    [Fact]
    public void TestWindow()
    {
        // 2.2: before an ordered event the low mark advances over acknowledged sequences, up to the highest written one;
        // the writer stops when seq > low + 126 (0x00551c04), so at most 127 sequences are in flight.
        var f = new Fake();
        var calls = new Calls();
        var c = new GameConnection(2049, f.Transport(), Capture(calls));
        for (uint i = 0; i < 300; ++i)
            c.Post(EvOf(i));
        uint now = 1000;
        var cookies = new List<uint>();
        var counts = new List<int>();
        var written = 0;
        void Run()
        {
            while (true)
            {
                var before = f.Sent.Count;
                c.Tick(now += 100);
                Assert.True(f.Sent.Count == before + 1);
                var p = Decode(f.Sent[^1].Data);
                if (p.Events.Count == 0)
                    return;
                cookies.Add(f.Sent[^1].Cookie);
                counts.Add(p.Events.Count);
                for (var k = 0; k < p.Events.Count; ++k)
                    Assert.True(p.Events[k].Ordered && (uint)p.Events[k].Seq == ((written + k) & 127));
                written += p.Events.Count;
            }
        }
        Run();
        Assert.True(written == 127 && cookies.Count >= 2); // seq 0..126
        c.Notify(cookies[1], true);                         // seq 0 still in flight: low stays 0
        Run();
        Assert.Equal(127, written);
        c.Notify(cookies[0], true); // all of 0..126 acknowledged: low = 126 (capped at the highest written)
        for (var k = 2; k < cookies.Count; ++k)
            c.Notify(cookies[k], true);
        Run();
        Assert.Equal(254, written); // again 127 in flight: seq 127..253
    }

    [Fact]
    public void TestReceiveOrdered()
    {
        var f = new Fake();
        var calls = new Calls();
        var c = new GameConnection(2049, f.Transport(), Capture(calls));
        // 1.2 rate fields (10 + 10 bits each) are skipped; 3.1: "remote" + argv[0], client id first.
        Deliver(c, new ClientPacket(true).Console(["SetCLInfo", "base", "Pilot"], ClientPacket.Kind.Ordered, 0).Finish());
        Assert.True(calls.Count == 1 && calls[0].Fn == "remoteSetCLInfo");
        Assert.True(SameArgs(calls[0].Args, "2049", "base", "Pilot"));
        calls.Clear();

        // 2.2: out-of-order arrival waits for the expected sequence, then releases in order.
        Deliver(c, new ClientPacket().Console(["B"], ClientPacket.Kind.Ordered, 2).Console(["C"], ClientPacket.Kind.Ordered, -1, true).Finish());
        Assert.Empty(calls);
        Deliver(c, new ClientPacket().Console(["A"], ClientPacket.Kind.Ordered, 1).Finish());
        Assert.True(calls.Count == 3 && calls[0].Fn == "remoteA" && calls[1].Fn == "remoteB" && calls[2].Fn == "remoteC");
        calls.Clear();

        // 2.2: unordered and unguaranteed events are dispatched at once.
        Deliver(c, new ClientPacket()
                       .Console(["E"], ClientPacket.Kind.Ordered, 5)
                       .Console(["U"], ClientPacket.Kind.Unordered)
                       .Console(["G"], ClientPacket.Kind.Unguaranteed)
                       .Finish());
        Assert.True(calls.Count == 2 && calls[0].Fn == "remoteU" && calls[1].Fn == "remoteG");
        Deliver(c, new ClientPacket().Console(["D"], ClientPacket.Kind.Ordered, 4).Finish());
        Assert.True(calls.Count == 4 && calls[2].Fn == "remoteD" && calls[3].Fn == "remoteE");
        calls.Clear();

        // The sender keeps up to 127 sequences in flight, so an event 70 ahead waits (it is not a duplicate) until 6..75
        // arrive.
        Deliver(c, new ClientPacket().Console(["Far"], ClientPacket.Kind.Ordered, 76).Finish());
        Assert.Empty(calls);
        var run = new ClientPacket();
        for (var s = 6; s <= 75; ++s)
            run.Console(["N" + s], ClientPacket.Kind.Ordered, s, s != 6);
        Deliver(c, run.Finish());
        Assert.True(calls.Count == 71 && calls[0].Fn == "remoteN6" && calls[69].Fn == "remoteN75" && calls[70].Fn == "remoteFar");
        calls.Clear();

        // A repeated copy of a waiting event is dispatched once.
        Deliver(c, new ClientPacket().Console(["Y"], ClientPacket.Kind.Ordered, 78).Finish());
        Deliver(c, new ClientPacket().Console(["Y"], ClientPacket.Kind.Ordered, 78).Finish());
        Deliver(c, new ClientPacket().Console(["X"], ClientPacket.Kind.Ordered, 77).Finish());
        Assert.True(calls.Count == 2 && calls[0].Fn == "remoteX" && calls[1].Fn == "remoteY");
        calls.Clear();

        // 7-bit sequences wrap: 127 is followed by 0.
        var wrap = new ClientPacket();
        for (var s = 79; s <= 130; ++s)
            wrap.Console(["W"], ClientPacket.Kind.Ordered, s & 127, s != 79);
        Deliver(c, wrap.Finish());
        Assert.Equal(52, calls.Count);
    }

    [Fact]
    public void TestReceiveRemoteEval()
    {
        var f = new Fake();
        var calls = new Calls();
        var c = new GameConnection(2050, f.Transport(), Capture(calls));
        // 3.1: no arguments gives remoteXxx(clientId).
        Deliver(c, new ClientPacket().Console(["dataFinished"], ClientPacket.Kind.Ordered, 0).Finish());
        Assert.True(calls.Count == 1 && calls[0].Fn == "remotedataFinished" && SameArgs(calls[0].Args, "2050"));
        calls.Clear();
        // 3.1 (0x00510118): argv text with each NUL ("remoteX", "2050", args) stays within 0x1ff bytes; the argument
        // that crosses it is dropped.
        string a = new('a', 250);
        Deliver(c, new ClientPacket().Console(["X", a, new string('b', 246)], ClientPacket.Kind.Unguaranteed).Finish());
        Assert.True(calls.Count == 1 && calls[0].Args.Count == 3 && calls[0].Args[2].Length == 246); // 511
        Deliver(c, new ClientPacket().Console(["X", a, new string('b', 247)], ClientPacket.Kind.Unguaranteed).Finish());
        Assert.True(calls.Count == 2 && SameArgs(calls[1].Args, "2050", a)); // 512
    }

    [Fact]
    public void TestReceiveInvalid()
    {
        // 2.3: game events (0x44b-0x462) from a client are "Invalid packet"; 1.2: the reader stops, so later events and
        // the PSC of that packet are ignored, the owner is told once and the connection reads nothing more.
        var f = new Fake();
        var calls = new Calls();
        var invalid = 0;
        var h = Capture(calls);
        h.InvalidPacket = () => ++invalid;
        var c = new GameConnection(2049, f.Transport(), h);
        Deliver(c, new ClientPacket()
                       .Console(["Bad"], ClientPacket.Kind.Ordered, 0, false, 0x44b)
                       .Console(["After"], ClientPacket.Kind.Unguaranteed)
                       .Finish(2));
        Assert.True(calls.Count == 0 && c.MoveCount == 0 && c.Invalid() && invalid == 1);
        Deliver(c, new ClientPacket().Console(["Ok"], ClientPacket.Kind.Ordered, 0).Finish(3));
        Assert.True(calls.Count == 0 && c.MoveCount == 0 && invalid == 1);
        // A valid packet on a connection that has not seen one: moves count (6.2) and lastMove goes back in the server
        // PSC (6.3).
        var g = new GameConnection(2049, f.Transport(), Capture(calls));
        Deliver(g, new ClientPacket().Console(["Ok"], ClientPacket.Kind.Ordered, 0).Finish(3));
        Assert.True(calls.Count == 1 && g.MoveCount == 3 && !g.Invalid());
        g.Tick(1000);
        Assert.Equal(2u, Decode(f.Sent[^1].Data).LastMove);
    }

    private sealed class ActionLog
    {
        public List<EventDest> Dests { get; } = [];
        public Calls CallsOf { get; } = new();
        public int Invalid { get; set; }

        public GameConnection.Hooks Hooks()
        {
            var h = Capture(CallsOf);
            h.ActionEvent = Dests.Add;
            h.InvalidPacket = () => ++Invalid;
            return h;
        }
    }

    [Fact]
    public void TestReceiveAction()
    {
        // 2.6: a server reads SimActionEvent 0x40b from a client. The body is the destination prefix (2.4).
        var f = new Fake();
        var log = new ActionLog();
        var c = new GameConnection(2049, f.Transport(), log.Hooks());
        Deliver(c, new ClientPacket()
                       .Action(0, 0, ClientPacket.Kind.Unguaranteed)
                       .Action(0, 0xffffffffu, ClientPacket.Kind.Unordered)
                       .Action(0, 2049, ClientPacket.Kind.Ordered, 0)
                       .Action(1, 0, ClientPacket.Kind.Ordered, -1, true)
                       .Action(1, 1023, ClientPacket.Kind.Ordered, -1, true)
                       .Action(2, 1022, ClientPacket.Kind.Ordered, -1, true)
                       .Console(["After"], ClientPacket.Kind.Ordered, -1, true)
                       .Finish(2));
        Assert.True(!c.Invalid() && log.Invalid == 0 && c.MoveCount == 2); // the PlayerPSC after the events is read
        Assert.Equal(6, log.Dests.Count);
        Assert.True(log.Dests[0].Kind == EventDest.KindT.ObjectId && log.Dests[0].Value == 0);
        Assert.True(log.Dests[1].Kind == EventDest.KindT.ObjectId && log.Dests[1].Value == 0xffffffffu);
        Assert.True(log.Dests[2].Kind == EventDest.KindT.ObjectId && log.Dests[2].Value == 2049);
        Assert.True(log.Dests[3].Kind == EventDest.KindT.ServerGhost && log.Dests[3].Value == 0);
        Assert.True(log.Dests[4].Kind == EventDest.KindT.ServerGhost && log.Dests[4].Value == 1023);
        Assert.True(log.Dests[5].Kind == EventDest.KindT.ClientGhost && log.Dests[5].Value == 1022);
        // The console event behind them was read at the right bit and took sequence 4.
        Assert.True(log.CallsOf.Count == 1 && log.CallsOf[0].Fn == "remoteAfter");

        // 2.2: an ordered action event takes a sequence like a console event. 6 and 7 wait for 5.
        Deliver(c, new ClientPacket().Action(0, 7, ClientPacket.Kind.Ordered, 6).Console(["Late"], ClientPacket.Kind.Ordered, 7).Finish());
        Assert.True(log.Dests.Count == 6 && log.CallsOf.Count == 1);
        Deliver(c, new ClientPacket().Action(1, 5, ClientPacket.Kind.Ordered, 5).Finish());
        Assert.True(log.Dests.Count == 8 && log.Dests[6].Value == 5 && log.Dests[7].Value == 7);
        Assert.True(log.CallsOf.Count == 2 && log.CallsOf[1].Fn == "remoteLate" && !c.Invalid());

        // Without a handler (the server) the events are read and dropped.
        var calls = new Calls();
        var plain = new GameConnection(2050, f.Transport(), Capture(calls));
        Deliver(plain, new ClientPacket().Action(0, 2050, ClientPacket.Kind.Ordered, 0).Console(["Next"], ClientPacket.Kind.Ordered, 1).Finish(1));
        Assert.True(calls.Count == 1 && calls[0].Fn == "remoteNext" && plain.MoveCount == 1 && !plain.Invalid());
    }

    [Fact]
    public void TestReceiveRejected()
    {
        // 2.6: every registered event class but 0x408 and 0x40b rejects itself on a server (0x0055199c), and a tag
        // without a class fails the lookup: "Invalid packet". The events before it in the packet have run, the ones after
        // it and the PlayerPSC are not read.
        var f = new Fake();
        uint[] rejected =
        [
            0x401, 0x40f, 0x421, 0x423, 0x425, 0x44b, 0x44c, 0x44d, 0x44f, 0x450, 0x452, 0x453, 0x455, 0x456, 0x458, 0x459,
            0x45c, 0x45d, 0x45f, 0x460, 0x461, 0x462, 0x400, 0x402, 0x407, 0x409, 0x40a, 0x40c, 0x424, 0x44a, 0x463, 0x47f,
        ];
        foreach (var tag in rejected)
        {
            foreach (var kind in new[] { ClientPacket.Kind.Ordered, ClientPacket.Kind.Unordered, ClientPacket.Kind.Unguaranteed })
            {
                var log = new ActionLog();
                var c = new GameConnection(2049, f.Transport(), log.Hooks());
                Deliver(c, new ClientPacket()
                               .Console(["Before"], ClientPacket.Kind.Ordered, 0)
                               .Action(0, 1, ClientPacket.Kind.Unguaranteed)
                               .Header(tag, kind, 1)
                               .Dest(0, 0)
                               .Console(["After"], ClientPacket.Kind.Unguaranteed)
                               .Action(0, 2, ClientPacket.Kind.Unguaranteed)
                               .Finish(3));
                Assert.True(c.Invalid() && log.Invalid == 1 && c.MoveCount == 0);
                Assert.True(log.CallsOf.Count == 1 && log.CallsOf[0].Fn == "remoteBefore" && log.Dests.Count == 1);
            }
        }
    }

    [Fact]
    public void TestReceiveTruncated()
    {
        // 1.6, 2.6: a read past the end is no error inside the event reader. A failed read gives 0 and does not move the
        // position; the event is dispatched with what was read, then the packet is "Invalid packet.".
        var f = new Fake();
        {
            // say with argc 3 and only the function name on the wire: the two missing strings are empty.
            var log = new ActionLog();
            var c = new GameConnection(2049, f.Transport(), log.Hooks());
            Deliver(c, new ClientPacket().Header(0x408, ClientPacket.Kind.Ordered, 0).BitsOf(3, 5).String("say").Cut());
            Assert.True(log.CallsOf.Count == 1 && log.CallsOf[0].Fn == "remotesay" && SameArgs(log.CallsOf[0].Args, "2049", "", ""));
            Assert.True(c.Invalid() && log.Invalid == 1);
        }
        {
            // The same event ahead of its turn waits and never runs.
            var log = new ActionLog();
            var c = new GameConnection(2049, f.Transport(), log.Hooks());
            Deliver(c, new ClientPacket().Header(0x408, ClientPacket.Kind.Ordered, 5).BitsOf(3, 5).String("say").Cut());
            Assert.True(log.CallsOf.Count == 0 && c.Invalid() && log.Invalid == 1);
        }
        {
            // An action event without its destination: the two prefix flags are padding, the id reads 0.
            var log = new ActionLog();
            var c = new GameConnection(2049, f.Transport(), log.Hooks());
            Deliver(c, new ClientPacket().Header(0x40b, ClientPacket.Kind.Unordered).Cut());
            Assert.True(log.Dests.Count == 1 && log.Dests[0].Kind == EventDest.KindT.ObjectId && log.Dests[0].Value == 0);
            Assert.True(c.Invalid() && log.Invalid == 1);
        }
        {
            // A whole action event and no member flags after it.
            var log = new ActionLog();
            var c = new GameConnection(2049, f.Transport(), log.Hooks());
            Deliver(c, new ClientPacket().Action(1, 1, ClientPacket.Kind.Unguaranteed).Cut());
            Assert.True(log.Dests.Count == 1 && log.Dests[0].Kind == EventDest.KindT.ServerGhost && log.Dests[0].Value == 1);
            Assert.True(c.Invalid() && log.Invalid == 1);
        }
        {
            // A Huffman string cut in the middle is completed with the character of the all-zero code, 't' (0x00583644); a
            // raw string that does not fit reads as empty (D20).
            var log = new ActionLog();
            var c = new GameConnection(2049, f.Transport(), log.Hooks());
            string text = new('e', 40); // compresses: 'e' is a short code
            var p = new ClientPacket();
            p.Header(0x408, ClientPacket.Kind.Unguaranteed).BitsOf(1, 5).String(text);
            var whole = p.Cut();
            var part = whole.AsSpan(0, whole.Length - 4).ToArray();
            Deliver(c, part);
            Assert.True(log.CallsOf.Count == 1 && log.CallsOf[0].Fn.Length == 6 + 40);
            var got = log.CallsOf[0].Fn;
            var es = -1;
            for (var i = 6; i < got.Length; ++i)
                if (got[i] != 'e')
                {
                    es = i;
                    break;
                }
            var restT = true;
            for (var i = es + 1; es >= 0 && i < got.Length; ++i)
                if (got[i] != 't')
                    restT = false;
            Assert.True(es != -1 && es > 6 && restT);
            Assert.True(c.Invalid() && log.Invalid == 1);

            var raw = new ActionLog();
            var d = new GameConnection(2049, f.Transport(), raw.Hooks());
            var q = new ClientPacket();
            q.Header(0x408, ClientPacket.Kind.Unguaranteed).BitsOf(2, 5).String("f").BitsOf(0, 1).BitsOf(20, 8).BitsOf(0, 8);
            Deliver(d, q.Cut()); // a raw string of 20 bytes with one on the wire
            Assert.True(raw.CallsOf.Count == 1 && raw.CallsOf[0].Fn == "remotef" && SameArgs(raw.CallsOf[0].Args, "2049", ""));
            Assert.True(d.Invalid() && raw.Invalid == 1);
        }
    }
}
