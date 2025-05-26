// Port of tests/test_master.cpp: master heartbeat and DNet master commands (docs/net-protocol.md 6.2 type 5; ROADMAP
// 8.3, rule R6). [live]: the reference sends `10 05 ff 00 00 00 00 00` from its game port to $inet::Master1 at network
// start. No socket is opened: datagrams go out through the send seam.
using OpenTribes.Net;
using OpenTribes.Script;
using OpenTribes.Server;

namespace OpenTribes.Tests;

public class MasterTests
{
    private sealed class World
    {
        public readonly Interpreter In = new();
        public readonly ServerConfig Cfg = new();
        public readonly NetServer Net;
        public readonly List<(Address to, byte[] data)> Sent = new();
        public string Out = "";
        public readonly List<(float delay, Action action)> Scheduled = new();

        public World()
        {
            Net = new NetServer(Cfg);
            Net.SetSendFunction((to, d) => Sent.Add((to, d.ToArray())));
            In.SetOutput(l => Out += l + "\n");
        }

        public MasterHeartbeat.Schedule Schedule() => (delay, action) => Scheduled.Add((delay, action));

        public string Eval(string line)
        {
            Out = "";
            In.Evaluate(line);
            return Out;
        }
    }

    private static readonly byte[] kBeat = { 0x10, 0x05, 0xff, 0, 0, 0, 0, 0 };

    [Fact]
    public void Master()
    {
        // Address text: "IP:a.b.c.d:port", prefix optional and case-insensitive.
        Address a;
        Assert.True(MasterHeartbeat.ParseMasterAddress("IP:127.0.0.1:29000", out a, false) && a.Ip == 0x7f000001 && a.Port == 29000);
        Assert.True(MasterHeartbeat.ParseMasterAddress("ip:10.1.2.3:28000", out a, false) && a.ToString() == "IP:10.1.2.3:28000");
        Assert.True(MasterHeartbeat.ParseMasterAddress("192.168.0.9:1", out a, false) && a.Port == 1);
        foreach (string bad in new[] { "", "IP:", "IP:127.0.0.1", "IP:127.0.0.1:", "IP:127.0.0.1:0", "IP:127.0.0.1:70000",
                                       "IP:127.0.0.1:28x", ":28000", "IP:t1m1.masters.example:28000" })
            Assert.False(MasterHeartbeat.ParseMasterAddress(bad, out a, false)); // names are not looked up without the opt-in

        {
            // Unset $Server::HostPublicGame becomes TRUE; the round reschedules itself at 120 s,
            // calls checkMasterTranslation() and walks $inet::Master1.. to the first unset entry.
            var w = new World();
            var hb = new MasterHeartbeat(w.In, w.Net, false);
            hb.RegisterCommands();
            w.Eval("function checkMasterTranslation() { $calls++; $inet::Master1 = \"IP:127.0.0.1:29000\"; " +
                   "$inet::Master2 = \"IP:127.9.9.9:29001\"; $inet::Master4 = \"IP:127.0.0.1:29004\"; }");
            hb.Start(w.Schedule());
            Assert.Equal("TRUE", w.In.GetVariable("$Server::HostPublicGame"));
            Assert.Equal("1", w.In.GetVariable("$calls"));
            Assert.True(w.Sent.Count == 2 && w.Sent[0].data.SequenceEqual(kBeat) && w.Sent[1].data.SequenceEqual(kBeat));
            Assert.True(w.Sent[0].to.ToString() == "IP:127.0.0.1:29000" && w.Sent[1].to.ToString() == "IP:127.9.9.9:29001");
            Assert.True(w.Scheduled.Count == 1 && w.Scheduled[0].delay == 120.0f);
            w.Scheduled[0].action(); // the next round
            Assert.True(w.Sent.Count == 4 && w.Scheduled.Count == 2 && w.In.GetVariable("$calls") == "2");
        }
        {
            // A private server keeps the timer but sends nothing and does not call the script.
            var w = new World();
            var hb = new MasterHeartbeat(w.In, w.Net, false);
            w.Eval("$Server::HostPublicGame = false; $inet::Master1 = \"IP:127.0.0.1:29000\";" +
                   "function checkMasterTranslation() { $calls++; }");
            hb.Start(w.Schedule());
            Assert.True(w.Sent.Count == 0 && w.Scheduled.Count == 1 && w.In.GetVariable("$calls").Length == 0);
            Assert.Equal("false", w.In.GetVariable("$Server::HostPublicGame"));
        }
        {
            // R6: without the opt-in only loopback masters are contacted; with it, any address.
            // The send seam captures every datagram: nothing reaches a socket.
            foreach (bool allow in new[] { false, true })
            {
                var w = new World();
                var hb = new MasterHeartbeat(w.In, w.Net, allow);
                w.Eval("$Server::HostPublicGame = true; $inet::Master1 = \"IP:10.255.255.1:28000\"; " +
                       "$inet::Master2 = \"IP:127.0.0.1:29000\"; $inet::Master3 = \"garbage\"; function checkMasterTranslation() {}");
                hb.Start(w.Schedule());
                Assert.Equal(allow ? 2 : 1, w.Sent.Count);
                Assert.Equal("IP:127.0.0.1:29000", w.Sent[^1].to.ToString());
            }
        }
        {
            // DNet::resolveMasters counts names up to the first empty one (at most 10);
            // DNet::getResolvedMaster gives "IP:a.b.c.d:port" for index 0..9, else "".
            var w = new World();
            var hb = new MasterHeartbeat(w.In, w.Net, false);
            hb.RegisterCommands();
            Assert.Equal("|\n", w.Eval("echo(DNet::getResolvedMaster(0), \"|\");")); // nothing resolved yet
            Assert.Equal("2\n", w.Eval("echo(DNet::resolveMasters(\"IP:127.0.0.1:28000\", \"10.0.0.2:5\", \"\", \"IP:1.2.3.4:5\"));"));
            Assert.Equal("IP:127.0.0.1:28000|IP:10.0.0.2:5|||\n",
                         w.Eval("echo(DNet::getResolvedMaster(0), \"|\", DNet::getResolvedMaster(1), \"|\", " +
                                "DNet::getResolvedMaster(2), \"|\", DNet::getResolvedMaster(-1), \"|\", DNet::getResolvedMaster());"));
            Assert.Equal("0|\n", w.Eval("echo(DNet::resolveMasters(\"\"), DNet::getResolvedMaster(0), \"|\");"));
            Assert.Equal("False|\n", w.Eval("echo(DNet::resolveMasters(), \"|\");")); // null result
            // A host name stays unresolved without the opt-in (no lookup leaves the machine).
            Assert.Equal("1|\n", w.Eval("echo(DNet::resolveMasters(\"t1m1.masters.example:28000\"), DNet::getResolvedMaster(0), \"|\");"));
            string eleven = "echo(DNet::resolveMasters(";
            for (int i = 0; i < 12; ++i)
                eleven += (i != 0 ? ", " : "") + "\"IP:127.0.0.1:" + (100 + i) + "\"";
            Assert.Equal("10\n", w.Eval(eleven + "));"));
        }
    }
}
