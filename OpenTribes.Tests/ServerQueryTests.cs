// The client's server query (docs/net-protocol.md 6.2; code 0054ee8c queue, 0054f608 replies, 0042c4cc info): synthetic packets only (R5, R6).
namespace OpenTribes.Tests;

using OpenTribes.Net;

public class ServerQueryTests
{
    private sealed class FakeNet : IQueryTransport
    {
        public List<(Address To, byte[] Data)> Sent { get; } = [];
        public Queue<(Address From, byte[] Data)> Inbox { get; } = new();

        public bool Send(Address destination, byte[] data)
        {
            Sent.Add((destination, data));
            return true;
        }

        public bool Receive(out Address from, out byte[] data)
        {
            if (Inbox.Count == 0)
            {
                from = default;
                data = [];
                return false;
            }
            (from, data) = Inbox.Dequeue();
            return true;
        }
    }

    private static readonly Address Server = new(0x7f000001, 28001);

    private static byte[] PingReply(ushort key, byte players, ushort version, string name)
    {
        var d = new byte[40];
        new byte[] { 0x10, 4, 0xff, 0xf0, (byte)key, (byte)(key >> 8), 0x20, players, (byte)version, (byte)(version >> 8) }.CopyTo(d, 0);
        for (var i = 0; i < name.Length && i < 29; ++i)
            d[10 + i] = (byte)name[i];
        return d;
    }

    private static byte[] InfoReply(ushort key, ushort version, string game = "Tribes")
    {
        var body = new byte[0x400];
        var bs = new BitBuffer(body, body.Length);
        bs.WriteBytes([(byte)version, (byte)(version >> 8)]);
        bs.WriteString(game);
        bs.WriteString("1.11");
        bs.WriteString("Test Host");
        bs.WriteBytes([3]);
        bs.WriteBytes([8]);
        bs.WriteString("Broadside");
        bs.WriteBytes([1]);
        bs.WriteBytes([0]);
        bs.WriteBytes([0x10, 0x0e, 0, 0]);
        bs.WriteString("base");
        bs.WriteString("CTF");
        bs.WriteString("info text");
        bs.WriteString("Team");
        bs.WriteString("Player");
        bs.WriteString("00Red");
        var d = new byte[8 + bs.BytePosition()];
        new byte[] { 0x10, 8, 0xff, 0, (byte)key, (byte)(key >> 8), 0, 0 }.CopyTo(d, 0);
        Array.Copy(body, 0, d, 8, bs.BytePosition());
        return d;
    }

    private static (ServerQuery Q, FakeNet Net, Func<long> Clock, Action<long> Advance) Make()
    {
        long t = 1000;
        var net = new FakeNet();
        var q = new ServerQuery(net, () => t) { BroadcastIp = 0x7fffffff };
        return (q, net, () => t, d => t += d);
    }

    [Fact]
    public void PingAndInfoRequestBytes()
    {
        Assert.Equal(new byte[] { 0x10, 3, 0xff, 0, 0x34, 0x12, 0, 0 }, QueryPackets.BuildPing(0x1234, 0));
        Assert.Equal(new byte[] { 0x10, 3, 5, 0, 1, 0, 0, 0 }, QueryPackets.BuildPing(1, 5));
        Assert.Equal(new byte[] { 0x10, 7, 0xff, 0, 0x34, 0x12, 0, 0 }, QueryPackets.BuildInfoRequest(0x1234));
    }

    [Fact]
    public void ParsesAPingReply()
    {
        Assert.True(QueryPackets.TryParsePingReply(PingReply(7, 3, 1, "Hello"), out var r));
        Assert.Equal((ushort)7, r.Key);
        Assert.Equal((byte)3, r.Players);
        Assert.Equal((ushort)1, r.PacketVersion);
        Assert.Equal("Hello", r.Hostname);
        Assert.False(QueryPackets.TryParsePingReply(new byte[8], out _));
    }

    [Fact]
    public void ParsesAnInfoReply()
    {
        Assert.True(QueryPackets.TryParseInfoReply(InfoReply(9, 1), out var r));
        Assert.Equal((ushort)9, r.Key);
        Assert.Equal("Tribes", r.GameName);
        Assert.Equal("1.11", r.Version);
        Assert.Equal("Test Host", r.Hostname);
        Assert.Equal((byte)3, r.Players);
        Assert.Equal((byte)8, r.MaxPlayers);
        Assert.Equal("Broadside", r.Mission);
        Assert.True(r.Dedicated);
        Assert.False(r.Password);
        Assert.Equal(0x0e10, r.Cpu);
        Assert.Equal("base", r.ModList);
        Assert.Equal("CTF", r.MissionType);
        Assert.Equal("info text", r.Info);
        Assert.Equal("Team", r.TeamHeading);
        Assert.Equal("Player", r.ClientHeading);
        Assert.Equal(new[] { "00Red" }, r.Rows);
    }

    [Theory]
    [InlineData("IP:127.0.0.1:28001", "IP:127.0.0.1:28001", false)]
    [InlineData("LOOPBACK:28001", "IP:127.0.0.1:28001", false)]
    [InlineData("IP:BROADCAST:28001", "IP:Broadcast:28001", true)]
    [InlineData("ip:broadcast:28001", "IP:Broadcast:28001", true)]
    public void ResolvesAddressText(string text, string canonical, bool broadcast)
    {
        Assert.True(QueryAddress.TryResolve(text, 0xffffffff, out var c, out var t, out var b, out _));
        Assert.Equal(canonical, c);
        Assert.Equal(broadcast, b);
        Assert.Equal((ushort)28001, t.Port);
    }

    [Fact]
    public void AMasterListPartQueuesItsServersAndWaitsForTheOtherParts()
    {
        var (q, net, _, _) = Make();
        string master = null!, motd = null!;
        q.OnMaster = (_, n, first, m) => { master = n; motd = first ? m : "none"; };
        q.Queue("IP:127.0.0.1:28001");
        var d = new List<byte> { 0x10, 6, 1, 2, 0, 0, 0, 0, 3, (byte)'M', (byte)'s', (byte)'t', 2, (byte)'h', (byte)'i', 0, 1 };
        d.AddRange(new byte[] { 6, 127, 0, 0, 1, 0x71, 0x6d });
        q.OnDatagram(Server, [.. d], 1000);
        Assert.Equal("Mst", master);
        Assert.Equal("hi", motd);
        // The request entry is gone, part 2 is awaited and the listed server (127.0.0.1:28017) is pinged.
        Assert.Equal(2, q.PendingPings);
        Assert.Equal(new[] { "IP:127.0.0.1:28001", "IP:127.0.0.1:28017" }, q.PendingPingAddresses.OrderBy(x => x).ToArray());
        Assert.Equal(2, net.Sent.Count);
    }

    [Fact]
    public void ThePingIsTheArrivalTimeMinusTheSendTime()
    {
        var (q, _, _, adv) = Make();
        var pings = new List<int>();
        q.OnPing = (_, _, p, _) => pings.Add(p);
        q.Queue("IP:127.0.0.1:28001");
        adv(40);
        q.OnDatagram(new Address(0x7f000001, 28001), PingReply(0, 0, 1, "Srv"), 1004);
        Assert.Equal(new[] { 4 }, pings);
    }

    [Fact]
    public void ABroadcastIsSentOnceAndLeavesTheQueue()
    {
        var (q, net, _, adv) = Make();
        q.Queue("IP:BROADCAST:28001");
        Assert.Single(net.Sent);
        Assert.Equal(new Address(0x7fffffff, 28001), net.Sent[0].To);
        Assert.Equal(QueryPackets.BuildPing(0, 0), net.Sent[0].Data);
        Assert.Equal(0, q.PendingPings);
        adv(5000);
        q.Tick();
        Assert.Single(net.Sent);
    }

    [Fact]
    public void AnIpxBroadcastTakesAKeyAndSendsNothing()
    {
        var (q, net, _, _) = Make();
        q.Queue("IPX:BROADCAST:28001");
        q.Queue("IP:127.0.0.1:28001");
        Assert.Single(net.Sent);
        Assert.Equal(QueryPackets.BuildPing(1, 0), net.Sent[0].Data);
    }

    [Fact]
    public void ALanOnlyReplyIsDroppedBecauseNothingIsQueued()
    {
        var (q, net, _, _) = Make();
        var pings = new List<string>();
        q.OnPing = (a, _, _, _) => pings.Add(a);
        q.Queue("IP:BROADCAST:28001");
        net.Inbox.Enqueue((Server, PingReply(0, 0, 1, "Lan")));
        q.Tick();
        Assert.Empty(pings);
        Assert.Single(net.Sent);
    }

    [Fact]
    public void ABroadcastReplyWithAQueuedMasterIsPingedByItsOwnAddress()
    {
        var (q, net, _, adv) = Make();
        var pings = new List<(string A, string N, int P, ushort V)>();
        q.OnPing = (a, n, p, v) => pings.Add((a, n, p, v));
        q.Queue("IP:BROADCAST:28001");
        q.Queue("IP:127.0.0.1:29999");
        net.Inbox.Enqueue((Server, PingReply(0, 0, 1, "Lan")));
        q.Tick();
        // the reply from an unknown address queues a ping of its own (key 2, after the broadcast 0 and the master 1)
        Assert.Equal(3, net.Sent.Count);
        Assert.Equal(Server, net.Sent[2].To);
        Assert.Equal(QueryPackets.BuildPing(2, 0), net.Sent[2].Data);
        adv(7);
        net.Inbox.Enqueue((Server, PingReply(2, 1, 1, "Lan")));
        q.Tick();
        Assert.Equal(("IP:127.0.0.1:28001", "Lan", 7, (ushort)1), pings.Single());
        // the same address answering again is not queried again
        net.Inbox.Enqueue((Server, PingReply(0, 0, 1, "Lan")));
        q.Tick();
        Assert.Single(pings);
    }

    [Fact]
    public void APingRetriesThenFails()
    {
        var (q, net, _, adv) = Make();
        var failed = new List<string>();
        q.OnFail = failed.Add;
        q.Prefs.PingTimeoutMs = 900;
        q.Prefs.PingRetries = 2;
        q.Queue("IP:127.0.0.1:28001");
        Assert.Single(net.Sent);
        adv(901);
        q.Tick();
        Assert.Equal(2, net.Sent.Count);
        Assert.Equal(QueryPackets.BuildPing(1, 0), net.Sent[1].Data);
        adv(901);
        q.Tick();
        Assert.Equal(3, net.Sent.Count);
        Assert.Empty(failed);
        adv(901);
        q.Tick();
        Assert.Equal(3, net.Sent.Count);
        Assert.Equal(new[] { "IP:127.0.0.1:28001" }, failed);
        Assert.Equal(0, q.PendingPings);
    }

    [Fact]
    public void AnInfoRequestWaitsForThePingsAndIsAnswered()
    {
        var (q, net, _, _) = Make();
        ServerInfoRecord? got = null;
        q.OnPing = (a, _, p, v) => q.Request(a, p);
        q.OnInfo = r => got = r;
        q.Queue("IP:127.0.0.1:28001");
        net.Inbox.Enqueue((Server, PingReply(0, 0, 1, "Lan")));
        q.Tick();
        Assert.Equal(1, q.PendingRequests);
        Assert.Equal(QueryPackets.BuildInfoRequest(1), net.Sent[^1].Data);
        net.Inbox.Enqueue((Server, InfoReply(1, 1)));
        q.Tick();
        Assert.Equal(0, q.PendingRequests);
        Assert.NotNull(got);
        Assert.Equal("Test Host", got!.Hostname);
        Assert.Equal("IP:127.0.0.1:28001", got.Address);
    }

    [Fact]
    public void AnInfoReplyOfAnotherVersionIsAFailure()
    {
        var (q, net, _, _) = Make();
        var failed = new List<string>();
        q.OnFail = failed.Add;
        ServerInfoRecord? got = null;
        q.OnInfo = r => got = r;
        q.Request("IP:127.0.0.1:28001", 5);
        net.Inbox.Enqueue((Server, InfoReply(0, 2)));
        q.Tick();
        Assert.Null(got);
        Assert.Equal(new[] { "IP:127.0.0.1:28001" }, failed);
    }

    [Fact]
    public void TargetsOutsideTheLoopbackAreNotContactedWhenTheGateSaysNo()
    {
        var (q, net, _, _) = Make();
        q.MayContact = a => (a.Ip >> 24) == 127;
        q.Queue("IP:10.1.2.3:28001");
        q.Queue("IP:127.0.0.1:28001");
        Assert.Single(net.Sent);
        Assert.Equal(Server, net.Sent[0].To);
    }
}
