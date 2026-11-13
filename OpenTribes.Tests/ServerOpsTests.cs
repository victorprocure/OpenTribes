// Port of tests/test_server_ops.cpp: server operations unit tests (docs/server-ops.md): 0x10 game info (2), remote
// console protocol (3), ban list (4), kick (5), console log (6), command surface (7). Synthetic input only: no socket
// is opened; datagrams go through NetServer.Receive and the send seam, console bytes through TelnetSession.Receive.
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Net;
using OpenTribes.Script;
using OpenTribes.Server;

public class ServerOpsTests
{
    private static readonly string Tmp = TestPaths.TmpDir("server_ops");

    private sealed class Harness
    {
        public NetServer Server { get; }
        public List<(Address to, byte[] data)> Sent { get; } = [];
        public List<(int level, string line)> Log { get; } = [];
        public uint NextId { get; set; } = 0x2000;

        public Harness(ServerConfig? cfg = null)
        {
            Server = new NetServer(cfg ?? new ServerConfig());
            Server.SetSendFunction((to, d) => Sent.Add((to, d.ToArray())));
            Server.SetLeveledLog((level, line) => Log.Add((level, line)));
        }

        public byte[] Query(Address from, byte[] req)
        {
            Sent.Clear();
            Server.Receive(from, req, 0);
            Assert.True(Sent.Count <= 1);
            return Sent.Count == 0 ? [] : Sent[0].data;
        }

        // Connect request from `from`; returns the player id (0 when nothing was accepted).
        public uint Join(Address from, string name) => Join(from, name, out _);

        public uint Join(Address from, string name, out uint connId)
        {
            var buf = new byte[1500];
            var bs = new BitBuffer(buf, buf.Length);
            bs.WriteBytes([1, 0]);
            foreach (var s in new[] { "Tribes", "1.11", "", name, "Male", "male1", "base" })
                bs.WriteString(s);
            var p = new VcPacket { Id = NextId++ };
            p.IdBit = p.Id & 1;
            p.Seq = 1;
            p.Type = 3;
            p.Payload = buf.AsSpan(0, bs.BytePosition()).ToArray();
            connId = p.Id;
            var d = VcPacket.Build(p);
            Sent.Clear();
            Server.Receive(from, d, 0);
            if (Sent.Count == 0)
                return 0;
            VcPacket output = VcPacket.Parse(Sent[0].data);
            if (output.Type != 0x0c)
                return 0;
            var input = new BitBuffer(output.Payload, 8);
            return input.ReadInt(32);
        }
    }

    private static Address Addr(uint n) => new(0x0a000000u + n, (ushort)(3000 + n));

    private sealed class GameInfoReply
    {
        public ushort PacketVersion { get; set; }
        public string Game { get; set; } = "";
        public string Version { get; set; } = "";
        public string Hostname { get; set; } = "";
        public string Mission { get; set; } = "";
        public string ModList { get; set; } = "";
        public string MissionType { get; set; } = "";
        public string Info { get; set; } = "";
        public string TeamHeading { get; set; } = "";
        public string ClientHeading { get; set; } = "";
        public byte Players { get; set; }
        public byte MaxPlayers { get; set; }
        public byte Dedicated { get; set; }
        public byte Password { get; set; }
        public uint Cpu { get; set; }
        public List<string> Rows { get; set; } = [];
    }

    private static GameInfoReply DecodeGameInfo(byte[] d)
    {
        Assert.True(d.Length >= 8);
        var r = new GameInfoReply();
        var body = d.AsSpan(8).ToArray();
        var bs = new BitBuffer(body, body.Length);
        var b = new byte[4];
        bs.ReadBytes(b.AsSpan(0, 2));
        r.PacketVersion = (ushort)(b[0] | (b[1] << 8));
        r.Game = bs.ReadString();
        r.Version = bs.ReadString();
        r.Hostname = bs.ReadString();
        var one = new byte[1];
        bs.ReadBytes(one);
        r.Players = one[0];
        bs.ReadBytes(one);
        r.MaxPlayers = one[0];
        r.Mission = bs.ReadString();
        bs.ReadBytes(one);
        r.Dedicated = one[0];
        bs.ReadBytes(one);
        r.Password = one[0];
        bs.ReadBytes(b);
        r.Cpu = b[0] | ((uint)b[1] << 8) | ((uint)b[2] << 16) | ((uint)b[3] << 24);
        r.ModList = bs.ReadString();
        r.MissionType = bs.ReadString();
        r.Info = bs.ReadString();
        r.TeamHeading = bs.ReadString();
        r.ClientHeading = bs.ReadString();
        while (bs.BitPosition() + 9 <= (d.Length - 8) * 8)
        {
            var row = bs.ReadString();
            if (bs.Error())
                break;
            r.Rows.Add(row);
        }
        while (r.Rows.Count != 0 && r.Rows[^1].Length == 0)
            r.Rows.RemoveAt(r.Rows.Count - 1);
        return r;
    }

    private static string S(byte[] d) => ByteString.FromBytes(d);

    // ---- 2: 0x10 queries ----
    private static int TestGameInfo()
    {
        var cases = 0;
        byte[] request = [0x10, 7, 0xff, 0, 0x34, 0x12, 0, 0];

        // 2.3 header and fixed fields from the configuration; no team heading: only the client
        // heading row, preceded by the "00" separator.
        {
            var cfg = new ServerConfig { Hostname = "Host", MaxPlayers = "12" };
            var h = new Harness(cfg);
            var d = h.Query(Addr(1), request);
            byte[] header = [0x10, 8, 0xff, 0, 0x34, 0x12, 0, 0];
            Assert.True(d.Length > 8 && d.AsSpan(0, 8).SequenceEqual(header));
            var r = DecodeGameInfo(d);
            Assert.True(r.PacketVersion == 1 && r.Game == "Tribes" && r.Version == "1.11" && r.Hostname == "Host");
            Assert.True(r.Players == 0 && r.MaxPlayers == 12 && r.Mission == "N/A" && r.Dedicated == 1 && r.Password == 0);
            Assert.True(r.Cpu == 0 && r.ModList == "base" && r.TeamHeading.Length == 0 && r.ClientHeading.Length == 0);
            Assert.Equal(["00", "11"], r.Rows);
            Assert.True(h.Log.Count == 1 && h.Log[0].level == 3 &&
                        h.Log[0].line == "SimNET: Got server info request from IP:10.0.0.1:3001.");
            ++cases;
        }

        // 2.2 request forms: header bytes 2, 3, 6, 7 and trailing data are ignored, the key is
        // echoed, a request cut before the key gets key 0; other types get no answer.
        {
            var h = new Harness();
            var d = h.Query(Addr(1), [0x10, 7, 1, 2, 0x56, 0x78, 9, 9, 1, 2, 3]);
            Assert.True(d.Length > 8 && d[2] == 0xff && d[3] == 0 && d[4] == 0x56 && d[5] == 0x78 && d[6] == 0 && d[7] == 0);
            d = h.Query(Addr(1), [0x10, 7]);
            Assert.True(d.Length > 8 && d[4] == 0 && d[5] == 0);
            d = h.Query(Addr(1), [0x10, 3]);
            Assert.True(d.Length == 40 && d[1] == 4 && d[4] == 0 && d[5] == 0);
            Assert.True(h.Log[^1].level == 3 && h.Log[^1].line == "SimNET: Got ping request from IP:10.0.0.1:3001");
            foreach (var type in new byte[] { 0, 1, 2, 4, 5, 6, 8, 9, 255 })
                Assert.Empty(h.Query(Addr(1), [0x10, type, 0xff, 0, 0, 0, 0, 0]));
            Assert.Empty(h.Query(Addr(1), [0x10]));
            ++cases;
        }

        // 2.3 empty hostname and mission send "N/A"; password flag; empty CPU text sends -1;
        // max players is a byte-wide decimal parse without a digit check ("300" -> 44).
        {
            var h = new Harness();
            h.Server.SetGameInfo(() => new GameInfo
            {
                Password = "pw",
                MaxPlayers = "300",
                CpuText = "",
                Dedicated = false,
                MissionName = "",
            });
            var r = DecodeGameInfo(h.Query(Addr(1), request));
            Assert.True(r.Hostname == "N/A" && r.Mission == "N/A" && r.Password == 1 && r.Dedicated == 0);
            Assert.True(r.Cpu == 0xffffffffu && r.MaxPlayers == unchecked((byte)300));
            ++cases;
        }

        // 2.4 rows: team heading, teams by descending sort value with %t expanded, separator,
        // client heading, players by descending sort value with %n %t %p %l expanded.
        {
            var h = new Harness();
            var a = h.Join(Addr(2), "Alpha");
            var b = h.Join(Addr(3), "Bravo");
            var c = h.Join(Addr(4), "Carol");
            Assert.True(a == 2049 && b == 2050 && c == 2051);
            h.Server.SetGameInfo(() =>
            {
                var g = new GameInfo
                {
                    Hostname = "H",
                    MaxPlayers = "8",
                    MissionName = "M",
                    TeamCount = 2,
                    TeamHeading = "TH",
                    ClientHeading = "CH",
                    Teams = { new("Red", "%t=1 (%t)", 1), new("Blue", "%t=5", 5) },
                };
                var pa = new GameInfo.PlayerRow { Id = a, Team = 0, Score = "%n/%t/%p/%l/%x%", Sort = 3, Ping = 42, Loss = 0.257f };
                var pb = new GameInfo.PlayerRow { Id = b, Team = 1, Score = "%n-%t", Sort = 9 };
                var pc = new GameInfo.PlayerRow { Id = c, Team = -1, Score = "[%t]%n", Sort = -4 };
                g.Players = [pa, pb, pc];
                return g;
            });
            var r = DecodeGameInfo(h.Query(Addr(1), request));
            Assert.True(r.Players == 3 && r.TeamHeading == "TH" && r.ClientHeading == "CH");
            var want = new List<string> { "00", "01TH", "00Blue=5", "00Red=1 (Red)", "00", "11CH",
                                          "10Bravo-Blue", "10Alpha/Red/42/25/%x%", "10[]Carol" };
            Assert.Equal(want, r.Rows);

            // The 0x63 reply lists the same players newest first with raw score text, ping/4,
            // loss*100 and the team byte (net-protocol.md 6.1).
            var text = S(h.Query(Addr(1), [0x62, 1, 2]));
            int carol = text.IndexOf("Carol", StringComparison.Ordinal), bravo = text.IndexOf("Bravo", StringComparison.Ordinal),
                alpha = text.IndexOf("Alpha", StringComparison.Ordinal);
            Assert.True(carol < bravo && bravo < alpha);
            Assert.True((byte)text[alpha - 4] == 42 / 4 && (byte)text[alpha - 3] == 25 && (byte)text[alpha - 2] == 0);
            Assert.Equal(0xff, (byte)text[carol - 2]);
            Assert.Contains("%n/%t/%p/%l/%x%", text);
            ++cases;
        }

        // 2.4 team count -1 (before the first mission) with a team heading: heading row only.
        {
            var h = new Harness();
            h.Server.SetGameInfo(() => new GameInfo { TeamCount = -1, TeamHeading = "TH", ClientHeading = "CH" });
            var r = DecodeGameInfo(h.Query(Addr(1), request));
            Assert.Equal(["00", "01TH"], r.Rows);
            ++cases;
        }

        // 2.5 bounds (D11): oversized texts neither overflow the 0x5d4 payload nor the row
        // buffer; rows stop when the budget is used up.
        {
            var h = new Harness();
            for (uint i = 0; i < 100; ++i)
                Assert.True(h.Join(Addr(10 + i), "P" + i) != 0 || i >= 8);
            h.Server.SetGameInfo(() =>
            {
                var g = new GameInfo { MaxPlayers = "128" };
                g.Hostname = g.MissionName = g.ModList = g.MissionType = g.Info = new string('x', 300);
                g.TeamCount = 8;
                g.TeamHeading = new string('h', 300);
                g.ClientHeading = new string('c', 300);
                for (var t = 0; t < 8; ++t)
                    g.Teams.Add(new(new string('n', 200), new string('%', 120) + "t%t%t%t%t%t%t%t", t));
                return g;
            });
            var d = h.Query(Addr(1), request);
            Assert.True(d.Length is >= 8 and <= (8 + 0x5d4));
            d = h.Query(Addr(1), [0x62, 0, 0]);
            Assert.True(d.Length is not 0 and <= 0x578);
            ++cases;
        }
        return cases;
    }

    // ---- 3: remote console ----
    private sealed class TelnetHarness
    {
        public TelnetSession Session { get; } = new();
        public string Out { get; set; } = "";
        public string Password { get; set; } = "pw";
        public string Prompt { get; set; } = "% ";
        public List<string> Evaluated { get; } = [];
        public TelnetSession.Host Host { get; }

        public TelnetHarness() => Host = new TelnetSession.Host
        {
            Password = () => Password,
            Prompt = () => Prompt,
            Evaluate = line =>
            {
                Evaluated.Add(line);
                Out += "<" + line + ">";
            },
            Send = d => Out += S(d),
        };

        // Feeds one chunk and returns what the client received for it.
        public string Feed(string data) => Feed(data, out _);

        public string Feed(string data, out bool open)
        {
            Out = "";
            open = Session.Receive(ByteString.ToBytes(data), Host);
            return Out;
        }
    }

    private static int TestTelnet()
    {
        var cases = 0;
        const string retry = "Nope... try agian.\r\nEnter Password:";

        // 3.1 banner text.
        Assert.Equal("SimEngine Telnet Provider\r\n\r\nEnter Password:", TelnetSession.Banner());
        ++cases;

        // 3.2 password dialogue: nothing is echoed before login; a wrong line gets the retry
        // text followed by the queued line end; the right one gets the line end and the prompt.
        {
            var t = new TelnetHarness();
            Assert.Empty(t.Feed("abc"));
            Assert.Equal(retry + "\r\n", t.Feed("\r\n"));
            Assert.Equal(retry + "\r\n", t.Feed("\r\n")); // empty line counts as a try
            Assert.True(t.Feed("bad\r\nworse\r\n", out var open) == retry + "Too many tries... cya." && !open);
            ++cases;
        }
        {
            var t = new TelnetHarness();
            Assert.Equal(retry + retry + "\r\n\r\n", t.Feed("bad1\r\nbad2\r\n"));
            Assert.True(t.Feed("pwx\b\r\n", out var open) == "\r\n% " && open && t.Session.Authenticated());
            Assert.Empty(t.Evaluated);
            ++cases;
        }

        // 3.2 lock-out: the fourth wrong line sends the goodbye and closes; nothing after it in
        // the chunk is processed.
        {
            var t = new TelnetHarness();
            t.Feed("1\n2\n3\n");
            Assert.True(t.Feed("4\npw\necho(1);\n", out var open) == "Too many tries... cya." && !open);
            Assert.True(!t.Session.Authenticated() && t.Evaluated.Count == 0);
            ++cases;
        }
        // An empty $TelnetPassword accepts the empty line; the compare stops at a NUL.
        {
            var t = new TelnetHarness { Password = "" };
            Assert.True(t.Feed("\n") == "\r\n% " && t.Session.Authenticated());
            var u = new TelnetHarness();
            Assert.Equal("\r\n% ", u.Feed("pw\0junk\n"));
            ++cases;
        }

        // 3.3 line editing after login: echo of every stored character, CR dropped, LF ends the
        // line (echoed as CR LF), evaluation output, then the prompt.
        {
            var t = new TelnetHarness();
            t.Feed("pw\n");
            Assert.Equal("echo(1);\r\n<echo(1);>% ", t.Feed("echo(1);\r\n"));
            Assert.Equal("a;\r\n<a;>% b;\r\n<b;>% ", t.Feed("a;\nb;\r\n")); // several lines per chunk
            Assert.Equal("ech", t.Feed("ech"));
            Assert.Equal("o;\r\n<echo;>% ", t.Feed("o;\n")); // split line
            Assert.True(t.Feed("\r") == "" && t.Feed("\n") == "\r\n<>% "); // empty line is evaluated
            Assert.Equal("abX\b \bc\r\n<abc>% ", t.Feed("abX\bc\n")); // backspace
            Assert.Equal("z\r\n<z>% ", t.Feed("\b\bz\n")); // backspace on an empty line: nothing
            Assert.Equal("n1\0n2\r\n<n1>% ", t.Feed("n1\0n2\n")); // NUL ends the text
            t.Prompt = "";
            Assert.Equal("q\r\n<q>", t.Feed("q\n"));
            ++cases;
        }

        // 3.3 line limit: 511 characters are kept and echoed; the rest is dropped silently.
        {
            var t = new TelnetHarness();
            t.Feed("pw\n");
            string longLine = new('x', 2000);
            string kept = new('x', TelnetSession.LineMax);
            Assert.Equal(kept, t.Feed(longLine));
            Assert.Equal("\r\n<" + kept + ">% ", t.Feed("yyy\n"));
            Assert.Equal("ok\r\n<ok>% ", t.Feed("ok\n"));
            // Before login the limit holds as well.
            var u = new TelnetHarness { Password = new string('p', 600) };
            Assert.Equal(retry + "\r\n", u.Feed(new string('p', 600) + "\n"));
            ++cases;
        }

        // 3.5 TelnetConsole without a port never opens a socket and ignores output.
        {
            var port = 0;
            var host = new TelnetConsole.Host
            {
                Port = () => port,
                Password = () => "pw",
                Prompt = () => "% ",
                Evaluate = _ => { },
                Print = _ => { },
            };
            using var console = new TelnetConsole(host);
            console.Poll();
            console.ConsoleLine("x");
            Assert.True(console.ClientCount() == 0 && console.ListeningPort() == 0);
            port = -1;
            console.Poll();
            Assert.Equal(-1, console.ListeningPort());
            ++cases;
        }
        return cases;
    }

    // ---- 4: ban list ----
    private static int TestBanList()
    {
        var cases = 0;
        var now = 1000;
        int clock() => now;

        // 4.1 add: relative expiry, -1 permanent; the port of an IP address becomes '*'.
        {
            var b = new BanList(clock);
            b.Add("IP:1.2.3.4:5", 100);
            b.Add("ip:8.8.8.8:1234", 0);
            b.Add("IP:9.9.9.9", -1);
            b.Add("garbage", -1);
            var want = new List<string> { "BanList::addAbsolute(\"IP:1.2.3.4:*\", 1100);",
                                          "BanList::addAbsolute(\"ip:8.8.8.8:*\", 1000);",
                                          "BanList::addAbsolute(\"IP:9.9.9.9\", 0);",
                                          "BanList::addAbsolute(\"garbage\", 0);" };
            Assert.Equal(want, b.ExportLines());
            // The duplicate test compares the address as given: a second ban of the same
            // client adds a second entry, the stored form updates the expiry in place.
            b.Add("IP:1.2.3.4:77", 50);
            Assert.True(b.Entries().Count == 5 && b.Entries()[4].Address == "IP:1.2.3.4:*" && b.Entries()[4].Expiry == 1050);
            b.AddAbsolute("ip:1.2.3.4:*", 2000);
            Assert.True(b.Entries().Count == 5 && b.Entries()[0].Expiry == 2000);
            // addAbsolute in the past is ignored; expiry == now is kept.
            b.AddAbsolute("IP:7.7.7.7:1", 999);
            Assert.Equal(5, b.Entries().Count);
            b.AddAbsolute("IP:7.7.7.7:1", 1000);
            Assert.Equal(6, b.Entries().Count);
            ++cases;
        }

        // 4.2 matching: '*' matches the rest, a pattern that ends at a ':' of the address
        // matches, comparison ignores case.
        {
            Assert.True(BanList.Matches("IP:1.2.3.4:*", "IP:1.2.3.4:28001"));
            Assert.True(BanList.Matches("ip:1.2.3.4:*", "IP:1.2.3.4:1"));
            Assert.False(BanList.Matches("IP:1.2.3.4:*", "IP:1.2.3.40:1"));
            Assert.True(BanList.Matches("IP:1.2.3.4", "IP:1.2.3.4:28001"));
            Assert.False(BanList.Matches("IP:1.2.3.4", "IP:1.2.3.44:28001"));
            Assert.True(BanList.Matches("IP:1.2.*", "IP:1.2.200.4:5"));
            Assert.True(BanList.Matches("IP:1.2.3.4:5", "IP:1.2.3.4:5"));
            Assert.False(BanList.Matches("IP:1.2.3.4:5", "IP:1.2.3.4:55"));
            Assert.False(BanList.Matches("IP:1.2.3.4:55", "IP:1.2.3.4:5"));
            Assert.True(BanList.Matches("*", "anything") && BanList.Matches("", "") && !BanList.Matches("", "x"));
            Assert.True(BanList.Matches("", ":x"));
            ++cases;
        }

        // 4.3 expiry: an entry bans while expiry >= now; expired entries are erased by the next
        // check; remove deletes the first equal entry only.
        {
            var b = new BanList(clock);
            b.Add("IP:1.1.1.1:9", 10);
            b.Add("IP:2.2.2.2:9", -1);
            Assert.True(b.IsBanned("IP:1.1.1.1:4000") && b.IsBanned("IP:2.2.2.2:1") && !b.IsBanned("IP:3.3.3.3:1"));
            now = 1010;
            Assert.True(b.IsBanned("IP:1.1.1.1:4000"));
            now = 1011;
            Assert.True(!b.IsBanned("IP:1.1.1.1:4000") && b.Entries().Count == 1);
            b.Add("IP:2.2.2.2:8", -1);
            Assert.Equal(2, b.Entries().Count);
            b.Remove("ip:2.2.2.2:*");
            Assert.True(b.Entries().Count == 1 && b.IsBanned("IP:2.2.2.2:1"));
            b.Remove("IP:2.2.2.2:*");
            b.Remove("IP:2.2.2.2:*");
            Assert.True(b.Entries().Count == 0 && !b.IsBanned("IP:2.2.2.2:1"));
            ++cases;
        }

        // D11: the original copies the address into 128 bytes unchecked; here it is clipped.
        {
            var b = new BanList(clock);
            b.Add(new string('a', 5000), -1);
            b.Add("IP:" + new string('1', 200) + ":5", -1);
            b.Add("IP:" + new string('1', 123) + ":5", -1);
            foreach (var e in b.Entries())
                Assert.True(e.Address.Length <= BanList.MaxAddress);
            Assert.Equal(':', b.Entries()[2].Address[^1]); // no room left for the '*'
            ++cases;
        }

        // 4.4 server side: a banned address gets no reply to any query or connect request, and
        // the console gets the level-2 lines.
        {
            var h = new Harness();
            h.Server.Bans().Add("IP:10.0.0.7:1", -1);
            var banned = new Address(0x0a000007u, 4242);
            var other = new Address(0x0a000008u, 4242);
            foreach (var q in new[] { new byte[] { 0x60, 1, 2 }, [0x62, 1, 2], [0x10, 3, 0xff, 0, 1, 2, 0, 0],
                                         [0x10, 7, 0xff, 0, 1, 2, 0, 0] })
            {
                h.Log.Clear();
                Assert.Empty(h.Query(banned, q));
                Assert.True(h.Log.Count == 1 && h.Log[0].level == 2 &&
                            h.Log[0].line == "SimNET: ignoring GI from banned address IP:10.0.0.7:4242");
                Assert.NotEmpty(h.Query(other, q));
            }
            h.Log.Clear();
            Assert.True(h.Join(banned, "Banned") == 0 && h.Sent.Count == 0 && h.Server.Players().Count == 0);
            Assert.True(h.Log.Count == 1 && h.Log[0].level == 2 &&
                        h.Log[0].line == "SimNET: ignoring connect from banned address IP:10.0.0.7:4242");
            h.Server.Poll(0); // the VC is gone: a later request is handled from scratch
            h.Server.Bans().Remove("IP:10.0.0.7:*");
            Assert.Equal(2049u, h.Join(banned, "Banned"));
            ++cases;
        }
        return cases;
    }

    // ---- 5: kick ----
    private static int TestKick()
    {
        var cases = 0;
        var h = new Harness();
        var closed = new List<uint>();
        h.Server.SetHooks(new NetHooks { OnClosed = p => closed.Add(p.Id) });

        var a = h.Join(Addr(1), "A", out var connA);
        var b = h.Join(Addr(2), "B", out var connB);
        Assert.True(a == 2049 && b == 2050);

        // Reason text on the wire: type 2 with the connection id, payload = text + NUL.
        h.Sent.Clear();
        Assert.True(h.Server.Kick(a, "Out you go"));
        Assert.True(h.Sent.Count == 1 && h.Sent[0].to == Addr(1));
        VcPacket p = VcPacket.Parse(h.Sent[0].data);
        Assert.True(p.Type == 2 && p.HasId && p.Id == connA && p.Payload.SequenceEqual(VcPacket.Bytes("Out you go", true)));
        Assert.True(closed.SequenceEqual(new[] { a }) && h.Server.Players().Count == 1);
        ++cases;

        // No reason: the default text. An empty reason sends just the NUL.
        h.Sent.Clear();
        Assert.True(h.Server.Kick(b, null));
        p = VcPacket.Parse(h.Sent[0].data);
        Assert.True(p.Type == 2 && p.Id == connB && p.Payload.SequenceEqual(VcPacket.Bytes("You were kicked from the game.", true)));
        h.Server.Poll(0);
        var c = h.Join(Addr(3), "C", out _);
        Assert.Equal(2050u, c); // ids are reused last-freed-first
        h.Sent.Clear();
        Assert.True(h.Server.Kick(c, ""));
        p = VcPacket.Parse(h.Sent[0].data);
        Assert.True(p.Type == 2 && p.Payload.SequenceEqual(new byte[] { 0 }));
        ++cases;

        // Unknown id: nothing happens.
        h.Sent.Clear();
        Assert.True(!h.Server.Kick(2099, "x") && h.Sent.Count == 0 && closed.Count == 3);
        ++cases;

        // dropInvalid (the SimNet error delegate after an invalid game packet, docs/game-stream.md 1.6): the game side
        // closes and the disconnect has no payload, not even the NUL of a reason text.
        h.Server.Poll(0);
        var inv = h.Join(Addr(6), "I", out var connI);
        h.Sent.Clear();
        closed.Clear();
        Assert.True(h.Server.DropInvalid(inv));
        Assert.True(closed.SequenceEqual(new[] { inv }) && h.Sent.Count == 1 && h.Sent[0].to == Addr(6));
        p = VcPacket.Parse(h.Sent[0].data);
        Assert.True(p.Type == 2 && p.HasId && p.Id == connI && p.Payload.Length == 0);
        Assert.True(!h.Server.DropInvalid(inv) && h.Server.Players().Count == 0);
        ++cases;

        // listPlayers lines: count, then newest first with address, name and score text.
        h.Server.Poll(0);
        h.Join(Addr(4), "D");
        h.Join(Addr(5), "E");
        var want = new List<string> { "Server: 2 clients", "2049 (IP:10.0.0.5:3005): E - ", "2050 (IP:10.0.0.4:3004): D - " };
        Assert.Equal(want, h.Server.PlayerList());
        ++cases;
        return cases;
    }

    // ---- 6: console log ----
    private static string Slurp(string path)
    {
        try
        {
            return ByteString.FromBytes(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static int TestConsoleLog()
    {
        var cases = 0;
        var path = Path.Combine(Tmp, "test_server_ops_console.log");
        File.Delete(path);
        static DateTime When() => new(2026, 10, 9, 7, 5, 3);
        const string header = "-------------------------- 10/9/2026 -- 07:05:03 -----\r\n";
        using (var log = new ConsoleLog(path, When))
        {
            log.Write("before"); // mode 0: nothing
            log.SetMode(1);
            log.Write("one");
            log.Write("two");
            Assert.Equal(header + "one\r\ntwo\r\n", Slurp(path));
            log.SetMode(7); // on, but neither file mode: nothing is written, no new header later
            log.Write("lost");
            log.SetMode(2);
            log.Write("three");
            Assert.Equal(header + "one\r\ntwo\r\nthree\r\n", Slurp(path));
            log.SetMode(0);
            log.Write("off");
            log.SetMode(2); // switched on again: a new header
            log.Write("four");
            log.SetMode(1);
            log.Write("five");
        }
        Assert.Equal(header + "one\r\ntwo\r\nthree\r\n" + header + "four\r\nfive\r\n", Slurp(path));
        ++cases;
        // The file is appended to across runs, with a header at the start of each run.
        using (var log = new ConsoleLog(path, When))
        {
            log.SetMode(1);
            log.Write("next run");
        }
        Assert.Equal(header + "one\r\ntwo\r\nthree\r\n" + header + "four\r\nfive\r\n" + header + "next run\r\n", Slurp(path));
        // An unwritable path is ignored.
        using (var log = new ConsoleLog(Path.Combine(Tmp, "no-such-dir", "x", "console.log"), When))
        {
            log.SetMode(1);
            log.Write("x");
            log.SetMode(2);
            log.Write("y");
        }
        File.Delete(path);
        ++cases;
        return cases;
    }

    // ---- 7: commands ----
    private sealed class ConsoleFixture
    {
        public Harness Net { get; } = new();
        public Interpreter In { get; } = new();
        public List<string> Out { get; } = [];
        public bool Quit { get; set; }

        public ConsoleFixture()
        {
            In.SetOutput(Out.Add);
            var ctx = new ServerCommandContext
            {
                Net = Net.Server,
                RootDir = Tmp,
                IsChatMenu = name => name == "Menu",
                Quit = () => Quit = true,
            };
            ServerCommands.RegisterServerCommands(In, ctx);
        }

        // Evaluates `echo("r=", <expr>);` and returns the printed lines.
        public List<string> Run(string expr)
        {
            Out.Clear();
            In.Evaluate("echo(\"r=\", " + expr + ");");
            return [.. Out];
        }
    }

    private static List<string> Lines(params string[] l) => [.. l];

    private static int TestCommands()
    {
        var cases = 0;
        var c = new ConsoleFixture();

        // 7.1 ban list commands: lower-case true/false by argument count.
        Assert.Equal(Lines("r=false"), c.Run("BanList::add()"));
        Assert.Equal(Lines("r=false"), c.Run("BanList::add(\"IP:1.2.3.4:5\", 100, 3)"));
        Assert.Equal(Lines("r=false"), c.Run("BanList::addAbsolute(\"x\")"));
        Assert.Equal(Lines("r=false"), c.Run("BanList::remove()"));
        Assert.Equal(Lines("r=false"), c.Run("BanList::export()"));
        Assert.Equal(Lines("r=true"), c.Run("BanList::addAbsolute(\"IP:10.9.8.7:99\", 2000000000)"));
        Assert.Equal(Lines("r=true"), c.Run("BanList::add(\"IP:10.9.8.6\", -1)"));
        Assert.Equal(2, c.Net.Server.Bans().Entries().Count);
        var file = Path.Combine(Tmp, "test_server_ops_ban.cs");
        File.Delete(file);
        Assert.Equal(Lines("r=true"), c.Run("BanList::export(\"test_server_ops_ban.cs\")"));
        Assert.Equal("BanList::addAbsolute(\"IP:10.9.8.7:*\", 2000000000);\r\n" +
                     "BanList::addAbsolute(\"IP:10.9.8.6\", 0);\r\n", Slurp(file));
        // The export is a script that rebuilds the list (banlist.cs is loaded with exec).
        Assert.Equal(Lines("r=true"), c.Run("BanList::remove(\"IP:10.9.8.7:*\")"));
        Assert.Equal(Lines("r=true"), c.Run("BanList::remove(\"ip:10.9.8.6\")"));
        Assert.Empty(c.Net.Server.Bans().Entries());
        c.In.Evaluate(Slurp(file));
        Assert.True(c.Net.Server.Bans().Entries().Count == 2 && c.Net.Server.Bans().Entries()[0].Expiry == 2000000000);
        File.Delete(file);
        // D13: paths outside the game directory are not written; a missing directory is ignored (D11).
        Assert.Equal(Lines("r=true"), c.Run("BanList::export(\"../test_server_ops_escape.cs\")"));
        Assert.Equal(Lines("r=true"), c.Run("BanList::export(\"/tmp/test_server_ops_escape.cs\")"));
        Assert.Equal(Lines("r=true"), c.Run("BanList::export(\"c:\\\\x.cs\")"));
        Assert.Equal(Lines("r=true"), c.Run("BanList::export(\"nodir\\\\x.cs\")"));
        Assert.Empty(Slurp(Tmp + "/../test_server_ops_escape.cs"));
        Assert.Empty(Slurp("/tmp/test_server_ops_escape.cs"));
        ++cases;

        // 7.2 Net::kick: usage for a wrong count, False always; the kick itself is covered above.
        Assert.Equal(Lines("kick(playerId, <reason>);", "r=False"), c.Run("Net::kick()"));
        Assert.Equal(Lines("kick(playerId, <reason>);", "r=False"), c.Run("Net::kick(1, \"a\", \"b\")"));
        Assert.Equal(Lines("r=False"), c.Run("Net::kick(2049)"));
        Assert.Equal(2049u, c.Net.Join(Addr(1), "K", out _));
        c.Net.Sent.Clear();
        Assert.Equal(Lines("r=False"), c.Run("Net::kick(2049, \"bye\")"));
        Assert.True(c.Net.Sent.Count == 1 && VcPacket.Parse(c.Net.Sent[0].data).Payload.SequenceEqual(VcPacket.Bytes("bye", true)));
        Assert.Equal(Lines("Server: 0 clients", "r=False"), c.Run("listPlayers()"));
        ++cases;

        // 7.3 master server surface with no resolver (ROADMAP 8.3 off).
        // DNet::resolveMasters and DNet::getResolvedMaster: tests/test_master.cpp.
        Assert.Equal(Lines("r=False"), c.Run("DNet::TranslateAddress(\"IP:127.0.0.1:28001\")"));
        Assert.Equal(Lines("addGameServer(address, name, version, ping, favorite <TRUE OR FALSE>);", "r=FALSE"),
                     c.Run("addGameServer()"));
        Assert.Equal(Lines("Unable to locate the GAME server list control", "r=FALSE"), c.Run("addGameServer(1, 2, 3, 4, 5)"));
        Assert.Equal(Lines("r="), c.Run("rebuildServerList()"));
        Assert.Equal(Lines("r=False"), c.Run("netStats()"));
        Assert.Equal(Lines("Send stats: 0 packets", "Receive stats: 0 packets", "r=False"), c.Run("logPacketStats()"));
        Assert.Equal(Lines("Connect: <net_address>", "r=False"), c.Run("connect(\"IP:127.0.0.1:1\")"));
        Assert.Equal(Lines("disconnect: not connected.", "r=False"), c.Run("disconnect()"));
        ++cases;

        // 7.4 GUI and input commands without a canvas: tests/test_client_commands.cpp.

        // 7.5 quit asks the main loop to stop; engine console variables and dbecho.
        Assert.False(c.Quit);
        c.In.Evaluate("quit();");
        Assert.True(c.Quit);
        Assert.Equal(Lines("r=1"), c.Run("$Console::printLevel"));
        c.In.Evaluate("$Console::logBufferEnabled = 5; $Console::updateMetrics = xyz;");
        Assert.Equal(Lines("r=TrueFalse"), c.Run("$Console::logBufferEnabled @ $Console::updateMetrics"));
        Assert.Equal(Lines("r=True"), c.Run("dbecho()"));
        Assert.Equal(Lines("", "r=True"), c.Run("dbecho(1)"));
        Assert.Equal(Lines("r=True"), c.Run("dbecho(2, \"x\")"));
        Assert.True(c.In.PeekVariable("$NoSuchVariable").Length == 0 && c.Out.Count == 1);
        ++cases;
        return cases;
    }

    [Fact]
    public void TestServerOps()
    {
        var cases = TestGameInfo() + TestTelnet() + TestBanList() + TestKick() + TestConsoleLog() + TestCommands();
        _ = cases;
    }
}
