// Port of tests/test_net_server.cpp: NetServer unit tests, connectionless queries and connect request checks
// (docs/net-protocol.md 5 and 6, docs/lasthope-1.30.md 2.1 and 2.2). No socket is opened: datagrams go in through
// NetServer.Receive and out through the send seam.
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Net;
using OpenTribes.Server;

public class NetServerTests
{
    private sealed class Harness
    {
        public NetServer Server { get; }
        public List<(Address to, byte[] data)> Sent { get; } = [];
        public List<string> Log { get; } = [];
        public uint NextId { get; set; } = 0x1000; // client connection ids; low bit varies

        public Harness(ServerConfig cfg)
        {
            Server = new NetServer(cfg);
            Server.SetSendFunction((to, d) => Sent.Add((to, d.ToArray())));
            Server.SetLog(Log.Add);
        }

        public byte[] Query(Address from, byte[] req)
        {
            Sent.Clear();
            Server.Receive(from, req, 0);
            Assert.True(Sent.Count <= 1);
            return Sent.Count == 0 ? [] : Sent[0].data;
        }
    }

    private sealed class Request
    {
        public ushort PacketVersion { get; set; } = 1;
        public string Game { get; set; } = "Tribes";
        public string Version { get; set; } = "1.11";
        public string Password { get; set; } = "";
        public string Name { get; set; } = "Player";
        public string Gender { get; set; } = "Male";
        public string Voice { get; set; } = "male1";
        public string Skin { get; set; } = "base";
    }

    private static byte[] RequestPayload(Request r)
    {
        var buf = new byte[1500];
        var bs = new BitBuffer(buf, buf.Length);
        bs.WriteBytes([(byte)r.PacketVersion, (byte)(r.PacketVersion >> 8)]);
        foreach (var s in new[] { r.Game, r.Version, r.Password, r.Name, r.Gender, r.Voice, r.Skin })
            bs.WriteString(s);
        return buf.AsSpan(0, bs.BytePosition()).ToArray();
    }

    private sealed class Reply
    {
        public bool Accepted { get; set; }
        public string Reason { get; set; } = ""; // reject text without the NUL
        public uint PlayerId { get; set; }
        public uint ManagerId { get; set; }
        public uint ConnectionId { get; set; }
    }

    private static Address Client(uint n) => new(0x0a000000u + n, (ushort)(2000 + n));

    // Sends a type 3 VC packet (seq 1) carrying the request and decodes the server's answer.
    private static Reply Connect(Harness h, Address from, Request r, uint now = 0)
    {
        var p = new VcPacket { Id = h.NextId++ };
        p.IdBit = p.Id & 1;
        p.Seq = 1;
        p.Type = 3;
        p.Payload = RequestPayload(r);
        var d = VcPacket.Build(p);
        h.Sent.Clear();
        h.Server.Receive(from, d, now);
        Assert.Single(h.Sent);
        var output = VcPacket.Parse(h.Sent[0].data);
        Assert.True(output.HasId && output.Id == p.Id && output.Seq == 1);
        var rep = new Reply { ConnectionId = p.Id };
        if (output.Type == 0x0c)
        {
            rep.Accepted = true;
            Assert.Equal(8, output.Payload.Length);
            var input = new BitBuffer(output.Payload, 8);
            rep.PlayerId = input.ReadInt(32);
            rep.ManagerId = input.ReadInt(32);
        }
        else
        {
            Assert.True(output.Type == 5 && output.Payload.Length != 0 && output.Payload[^1] == 0);
            rep.Reason = ByteString.FromBytes(output.Payload.AsSpan(0, output.Payload.Length - 1));
        }
        return rep;
    }

    // Acks the accept (3.5), then sends a disconnect (5.5) so the player is dropped.
    private static void AckAccept(Harness h, Address from, uint id)
    {
        var p = new VcPacket { Id = id, IdBit = id & 1, Seq = 1, Ranges = { (1u, 1u) }, Type = 0x10 };
        h.Server.Receive(from, VcPacket.Build(p), 0);
    }

    private static void Disconnect(Harness h, Address from, uint id)
    {
        AckAccept(h, from, id);
        var p = new VcPacket { Id = id, IdBit = id & 1, Seq = 2, Type = 2, Payload = VcPacket.Bytes("bye", true) };
        h.Server.Receive(from, VcPacket.Build(p), 0);
    }

    private static List<byte> Str(string s)
    {
        var v = new List<byte> { (byte)s.Length };
        v.AddRange(ByteString.ToBytes(s));
        return v;
    }

    private const string StockUpgrade =
        "You must upgrade your executable to play on this server (requires version 1.11 or higher).  " +
        "Check www.tribesplayers.com for the most recent patches.";
    private const string LastHopeUpgrade =
        "You must upgrade your executable to play on this server (requires version 1.30). " +
        "Check www.lasthope.us for the most recent patches.";

    [Fact]
    public void TestNetServer()
    {
        var cases = 0;
        var cfg = new ServerConfig { Hostname = "Host", MaxPlayers = "8" };

        // 6.1: 0x60 ping reply, byte stream with length-prefixed strings (4.2).
        {
            var h = new Harness(cfg);
            var want = new List<byte> { 0x61, 0xaa, 0xbb, 0x62 };
            want.AddRange(Str("Tribes"));
            want.AddRange(Str("1.11"));
            want.AddRange(Str("Host"));
            want.AddRange([1, 0, 0, 8]);
            Assert.Equal([.. want], h.Query(Client(1), [0x60, 0xaa, 0xbb]));
            // Any length is answered.
            Assert.Equal(0x61, h.Query(Client(1), [0x60, 0xaa, 0xbb, 0x00, 0x01])[0]);
            ++cases;
        }

        // 6.1: hostname clipped to 32, password flag, LastHope version string (lasthope L1).
        {
            var c = cfg.Clone();
            c.Hostname = new string('h', 40);
            c.Password = "pw";
            c.LastHope = true;
            var h = new Harness(c);
            var want = new List<byte> { 0x61, 1, 2, 0x62 };
            want.AddRange(Str("Tribes"));
            want.AddRange(Str("1.30"));
            want.AddRange(Str(new string('h', 32)));
            want.AddRange([1, 1, 0, 8]);
            Assert.Equal([.. want], h.Query(Client(1), [0x60, 1, 2]));
            ++cases;
        }

        // 6.1: 0x62 info only when exactly 3 bytes; the 0x63 reply carries the game fields,
        // teams and per-player rows (ping/4, loss*100, team, name).
        {
            var h = new Harness(cfg);
            Assert.Empty(h.Query(Client(1), [0x62, 1, 2, 3]));
            Assert.Empty(h.Query(Client(1), [0x62, 1]));
            var r = Connect(h, Client(5), new Request());
            Assert.True(r.Accepted);
            h.Server.SetGameInfo(() => new GameInfo
            {
                Hostname = "Live",
                MaxPlayers = "16",
                Dedicated = false,
                CpuSpeed = 0x1234,
                ModList = "base",
                MissionType = "Capture the Flag", // clipped to 8
                MissionName = "Raindance",
                Info = "info",
                TeamCount = 2,
                TeamHeading = "TH",
                ClientHeading = "CH",
                Teams = { new("Blood Eagle", "0"), new("Diamond Sword", "1") },
            });
            var want = new List<byte> { 0x63, 7, 9, 0x62 };
            want.AddRange(Str("Tribes"));
            want.AddRange(Str("1.11"));
            want.AddRange(Str("Live"));
            want.AddRange([0, 0, 1, 16, 0x34, 0x12]);
            want.AddRange(Str("base"));
            want.AddRange(Str("Capture "));
            want.AddRange(Str("Raindance"));
            want.AddRange(Str("info"));
            want.AddRange([2]);
            want.AddRange(Str("TH"));
            want.AddRange(Str("CH"));
            want.AddRange(Str("Blood Eagle"));
            want.AddRange(Str("0"));
            want.AddRange(Str("Diamond Sword"));
            want.AddRange(Str("1"));
            want.AddRange([0, 0, 0xff]); // no client record yet: ping 0, loss 0, no team
            want.AddRange(Str("Player"));
            want.AddRange(Str(""));
            Assert.Equal([.. want], h.Query(Client(1), [0x62, 7, 9]));
            ++cases;
        }

        // 6.2: 0x10 type 3 ping, 40-byte reply with the packet version (1 for "1.11" and "1.30").
        foreach (var lastHope in new[] { false, true })
        {
            var c = cfg.Clone();
            c.LastHope = lastHope;
            c.Hostname = new string('x', 35);
            var h = new Harness(c);
            var reply = h.Query(Client(1), [0x10, 0x03, 0xff, 0x00, 0x34, 0x12, 0x00, 0x00]);
            var want = new List<byte> { 0x10, 0x04, 0xff, 0xf0, 0x34, 0x12, 0x20, 0x00, 0x01, 0x00 };
            want.AddRange(Enumerable.Repeat((byte)'x', 29));
            want.Add(0);
            Assert.Equal([.. want], reply);
            // A request cut before the key is answered with key 0 (docs/server-ops.md 2.2).
            want[4] = want[5] = 0;
            Assert.Equal([.. want], h.Query(Client(1), [0x10, 0x03, 0xff, 0x00]));
            ++cases;
        }

        // 5.2 steps 3-4: packet version, then the case-sensitive game type.
        {
            var h = new Harness(cfg);
            var r = new Request { PacketVersion = 2 };
            Assert.Equal("Wrong packet version.", Connect(h, Client(1), r).Reason);
            r = new Request { Game = "tribes" };
            Assert.Equal("Wrong game type.", Connect(h, Client(2), r).Reason);
            // Order: a bad packet version wins over a bad game type.
            r.PacketVersion = 0x100;
            Assert.Equal("Wrong packet version.", Connect(h, Client(3), r).Reason);
            Assert.Empty(h.Server.Players());
            ++cases;
        }

        // 5.2 step 5: minor = atoi of at most two digits after the client's first '.', threshold 11.
        // The major compare is a zero-length strncmp for "1.11", so "Wrong game version." never fires.
        {
            var h = new Harness(cfg);
            Reply Version(string v, uint n) => Connect(h, Client(n), new Request { Version = v });
            Assert.Equal(StockUpgrade, Version("1.10", 1).Reason);
            Assert.Equal(StockUpgrade, Version("1.9", 2).Reason);
            Assert.Equal(StockUpgrade, Version("111", 3).Reason); // no dot: minor 0
            Assert.True(Version("1.11", 4).Accepted);
            Assert.True(Version("1.119", 5).Accepted); // "11" only
            Assert.True(Version("9.30", 6).Accepted);  // major ignored
            Assert.True(Version("1.30", 7).Accepted);  // a 1.30 client passes a 1.11 server (lasthope L1)
            ++cases;
        }

        // lasthope 2.1 L1: threshold 30 and the 1.30 upgrade text.
        {
            var c = cfg.Clone();
            c.LastHope = true;
            var h = new Harness(c);
            var r = new Request();
            Assert.Equal(LastHopeUpgrade, Connect(h, Client(1), r).Reason);
            r.Version = "1.29";
            Assert.Equal(LastHopeUpgrade, Connect(h, Client(2), r).Reason);
            r.Version = "1.30";
            Assert.True(Connect(h, Client(3), r).Accepted);
            ++cases;
        }

        // 5.2 step 6: a non-empty server password must match exactly.
        {
            var c = cfg.Clone();
            c.Password = "pw";
            var h = new Harness(c);
            var r = new Request();
            Assert.Equal("Wrong password.", Connect(h, Client(1), r).Reason);
            r.Password = "PW";
            Assert.Equal("Wrong password.", Connect(h, Client(2), r).Reason);
            r.Password = "pw";
            Assert.True(Connect(h, Client(3), r).Accepted);
            var open = new Harness(cfg);
            r.Password = "anything";
            Assert.True(Connect(open, Client(4), r).Accepted);
            ++cases;
        }

        // 5.2 step 7: max = min($Server::MaxPlayers, 128), 32 when empty; full when count >= max.
        {
            int Capacity(string max)
            {
                var c = cfg.Clone();
                c.MaxPlayers = max;
                var h = new Harness(c);
                for (uint n = 1; n <= 200; ++n)
                {
                    var r = Connect(h, Client(n), new Request());
                    if (!r.Accepted)
                    {
                        Assert.Equal("Server is full.", r.Reason);
                        return h.Server.Players().Count;
                    }
                }
                return -1;
            }
            Assert.Equal(2, Capacity("2"));
            Assert.Equal(32, Capacity(""));
            Assert.Equal(128, Capacity("200"));
            Assert.Equal(0, Capacity("0"));
            ++cases;
        }

        // 5.2 step 8: first 16 characters kept; bytes < 0x20, > 0x7f, '\', '"', '~' become '*';
        // duplicates get "%s.%d" from 1 (case-sensitive: the name is interned case-sensitively,
        // StringTable_insert flag 1 at 0x0042afef).
        {
            var h = new Harness(cfg);
            string Join(string name, uint n)
            {
                var rep = Connect(h, Client(n), new Request { Name = name });
                Assert.True(rep.Accepted);
                return h.Server.PlayerById(rep.PlayerId)!.Name;
            }
            Assert.Equal("A*B*C*D*E\x7f" + "F*", Join("A\x01" + "B\"C~D\\E\x7f" + "F\x80", 1));
            Assert.Equal("ABCDEFGHIJKLMNOP", Join("ABCDEFGHIJKLMNOPQRST", 2));
            Assert.Equal("ABCDEFGHIJKLMNOP.1", Join("ABCDEFGHIJKLMNOPXYZ", 3));
            Assert.Equal("Bob", Join("Bob", 4));
            Assert.Equal("Bob.1", Join("Bob", 5));
            Assert.Equal("Bob.2", Join("Bob", 6));
            Assert.Equal("bob", Join("bob", 7));
            Assert.Equal("Bob.1.1", Join("Bob.1", 8));
            ++cases;
        }

        // 5.1: gender is stricmp(.., "male") == 0, voice is kept; 5.4: accept payload is the
        // player id and server manager id 2048.
        {
            var h = new Harness(cfg);
            var r = new Request { Gender = "MALE", Voice = "Male3" };
            var a = Connect(h, Client(1), r);
            r.Gender = "Female";
            var b = Connect(h, Client(2), r);
            Assert.True(a.Accepted && b.Accepted && a.ManagerId == 2048 && b.ManagerId == 2048);
            Assert.True(h.Server.PlayerById(a.PlayerId)!.Male && !h.Server.PlayerById(b.PlayerId)!.Male);
            Assert.Equal("Male3", h.Server.PlayerById(a.PlayerId)!.Voice);
            ++cases;
        }

        // 8 (live verification): player ids from 2049, freed ids reused last-freed-first.
        {
            var h = new Harness(cfg);
            var a = Connect(h, Client(1), new Request());
            var b = Connect(h, Client(2), new Request());
            var c = Connect(h, Client(3), new Request());
            Assert.True(a.PlayerId == 2049 && b.PlayerId == 2050 && c.PlayerId == 2051);
            Disconnect(h, Client(2), b.ConnectionId);
            Disconnect(h, Client(1), a.ConnectionId);
            Assert.Single(h.Server.Players());
            h.Server.Poll(0); // removes the dead VCs (2.5)
            Assert.Equal(2049u, Connect(h, Client(4), new Request()).PlayerId);
            Assert.Equal(2050u, Connect(h, Client(5), new Request()).PlayerId);
            Assert.Equal(2052u, Connect(h, Client(6), new Request()).PlayerId);
            ++cases;
        }

        // 3.5 / 5.6: the accept ack establishes the stream; data reaches the hooks.
        {
            var h = new Harness(cfg);
            var accepted = 0;
            var data = "";
            var hooks = new NetHooks
            {
                OnAccepted = (p, c) => ++accepted,
                OnData = (p, d) => data = ByteString.FromBytes(d),
            };
            h.Server.SetHooks(hooks);
            var a = Connect(h, Client(1), new Request());
            Assert.Equal(0, accepted);
            AckAccept(h, Client(1), a.ConnectionId);
            Assert.Equal(1, accepted);
            var p = new VcPacket { Id = a.ConnectionId, IdBit = a.ConnectionId & 1, Seq = 2, Type = 0, Payload = VcPacket.Bytes("game") };
            h.Server.Receive(Client(1), VcPacket.Build(p), 0);
            Assert.Equal("game", data);
            ++cases;
        }

        // DECISIONS D11 / lasthope 2.2 L2: more than 20 ack ranges is dropped; in LastHope mode the
        // drop logs "Crash Attempt: <address>".
        foreach (var lastHope in new[] { false, true })
        {
            var c = cfg.Clone();
            c.LastHope = lastHope;
            var h = new Harness(c);
            var p = new VcPacket { Id = 0x2001, IdBit = 1, Seq = 1, Type = 3 };
            for (var i = 0; i < 21; ++i)
                p.Ranges.Add((1, 3));
            var r = new Request { Version = lastHope ? "1.30" : "1.11" };
            p.Payload = RequestPayload(r);
            var from = new Address(0x0a000102, 28001);
            h.Server.Receive(from, VcPacket.Build(p), 0);
            Assert.True(h.Sent.Count == 0 && h.Server.Players().Count == 0);
            if (lastHope)
                Assert.True(h.Log.Count == 1 && h.Log[0] == "Crash Attempt: IP:10.0.1.2:28001");
            else
                Assert.Empty(h.Log);
            ++cases;
        }

        // Frame steps (docs/sim.md 8.3 step 3): the game tick hook runs in sendPackets only.
        {
            var h = new Harness(cfg);
            var ticks = 0;
            uint last = 0;
            var hooks = new NetHooks
            {
                OnTick = now =>
                {
                    ++ticks;
                    last = now;
                },
            };
            h.Server.SetHooks(hooks);
            h.Server.ReceivePackets(40);
            Assert.Equal(0, ticks);
            h.Server.SendPackets(41);
            Assert.True(ticks == 1 && last == 41);
            h.Server.Poll(50);
            Assert.True(ticks == 2 && last == 50);
            ++cases;
        }

        _ = cases;
    }
}
