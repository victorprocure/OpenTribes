// Hosting a game from the client (GitHub issue 11, client-app.md 1.7): the client process keeps a server world next to the
// client world, with one console. The stock console functions newServer / focusServer / focusClient / deleteServer (cases
// 0x7c, 0x7a, 0x79, 0x7d of FearGame_consoleCallback), the focus per frame phase (FearWorld_advance 004eba40,
// FearWorld_clientAdvance 004eb7ac) and the join over the loopback transport (0058d7d4) of the same process. Synthetic
// game directory: no game data.
namespace OpenTribes.Tests;

using OpenTribes.Client.App;

public sealed class HostGameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-host-" + Guid.NewGuid().ToString("N"));
    private ClientApp? _app;

    public HostGameTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "temp"));
    }

    public void Dispose()
    {
        _app?.Dispose();
        try
        { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private ClientApp Boot(string consoleCs = "")
    {
        File.WriteAllText(Path.Combine(_root, "console.cs"),
            "newClient(); focusClient();\n" +
            "newObject(MainWindow, SimGui::Canvas, \"Tribes\", 640, 480, True, \"512 384\", \"1024 768\");\n" +
            "function onConnection(%m) { $conn = %m; }\nfunction Game::EndFrame() {}\n" +
            "$ConsoleWorld::DefaultSearchPath = \"config;base;temp\";\n" + consoleCs);
        var opt = ClientOptions.Parse(["--root", _root, "--headless"], out var err)!;
        Assert.Equal("", err);
        _app = new ClientApp(opt);
        Assert.True(_app.Start(out var error), error);
        return _app;
    }

    private string Eval(string text) => _app!.Interp.Evaluate(text);

    [Fact]
    public void FocusServerIsFalseUntilANewServerExists() // 0x7a: only when slot 1 is filled
    {
        Boot();
        Assert.Equal("False", Eval("focusServer();"));
        Assert.Equal("True", Eval("newServer();"));
        Assert.Equal("True", Eval("focusServer();"));
        Assert.Equal("True", Eval("deleteServer();"));
        Assert.Equal("False", Eval("focusServer();"));
    }

    [Fact]
    public void ObjectsLiveInTheWorldThatHasTheFocus() // MG3: sim commands resolve in the focus manager only
    {
        Boot();
        Eval("newServer(); focusServer(); newObject(HostedSet, SimSet);");
        Assert.Equal("True", Eval("isObject(HostedSet);"));
        Eval("focusClient();");
        Assert.Equal("False", Eval("isObject(HostedSet);"));
        Eval("newObject(ClientOnlySet, SimSet); focusServer();");
        Assert.Equal("False", Eval("isObject(ClientOnlySet);"));
        Assert.Equal("True", Eval("isObject(HostedSet);"));
    }

    [Fact]
    public void DeleteServerKeepsTheClientWorld() // client-app.md 1.7: deleteServer removes the server world only
    {
        Boot();
        Eval("newObject(ClientKeeps, SimSet); newServer(); focusServer(); newObject(HostedSet, SimSet); focusClient();");
        Eval("deleteServer();");
        Assert.Equal("True", Eval("isObject(ClientKeeps);"));
        Assert.False(_app!.Hosted.Exists);
    }

    [Fact]
    public void ANewServerReplacesTheOldOne() // MG1: filling the slot destroys the world in it
    {
        Boot();
        Eval("newServer(); focusServer(); newObject(HostedSet, SimSet); newServer(); focusServer();");
        Assert.Equal("False", Eval("isObject(HostedSet);"));
    }

    [Fact]
    public void ScriptFunctionsAndVariablesAreOneConsole() // the stock exe has one console for both worlds
    {
        Boot("function Hosted::mark(%v) { $marked = %v; return %v; }\n");
        Eval("newServer(); focusServer();");
        Eval("Hosted::mark(7);");
        Eval("focusClient();");
        Assert.Equal("7", _app!.Interp.PeekVariable("$marked"));
    }

    [Fact]
    public void TheServerWorldHasItsOwnCommandsWhileFocused() // the server commands answer only under the server focus
    {
        Boot();
        Assert.False(_app!.Interp.HasFunction("Client::getTransportAddress"));
        Eval("newServer(); focusServer();");
        Assert.True(_app.Interp.HasFunction("Client::getTransportAddress"));
        Eval("focusClient();");
        Assert.False(_app.Interp.HasFunction("Client::getTransportAddress"));
    }

    [Fact]
    public void TheFrameLeavesTheClientFocus()
    {
        Boot();
        Eval("newServer(); focusServer();");
        _app!.Step();
        Assert.Equal("False", Eval("isObject(NoSuchObject);"));
        Eval("newObject(InClient, SimSet);");
        Assert.Null(_app.Interp.FocusedWorld());
    }

    [Fact]
    public void TheClientJoinsItsOwnServerOverLoopback() // client-app.md 1.7 steps 3 and 5
    {
        Boot();
        Eval("newServer(); focusServer(); newObject(serverDelegate, FearCSDelegate, true, \"LOOPBACK\", 0);");
        Assert.True(_app!.Hosted.Net!.IsOpen());
        var port = _app.Hosted.Net.Port();
        Eval("focusClient();");
        Assert.True(_app.Connect("LOOPBACK:" + port));
        for (var i = 0; i < 400 && _app.Interp.PeekVariable("$conn") != "Accepted"; ++i)
        {
            _app.Step();
            Thread.Sleep(1);
        }
        Assert.True(_app.Interp.PeekVariable("$conn") == "Accepted", string.Join("\n", _app.LogTail.Take(12)));
        Assert.Single(_app.Hosted.Net.Players());
    }

    [Fact]
    public void ALoopbackOnlyGameDoesNotListenOnOtherAddresses() // the $SinglePlayer form opens no outside socket
    {
        Boot();
        Eval("newServer(); focusServer(); newObject(serverDelegate, FearCSDelegate, true, \"LOOPBACK\", 0);");
        Assert.True(_app!.Hosted.Net!.IsOpen());
        Assert.True(_app.Hosted.Net.LoopbackOnly);
        // Stock passes the packets in memory: a real UDP socket reaches nothing of this game.
        using var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        probe.Client.ReceiveTimeout = 50;
        probe.Send([0x10, 0x05], 2, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, _app.Hosted.Net.Port()));
        Assert.Equal(0, probe.Available);
    }

    [Fact]
    public void AnIpBindThatFailsPrintsTheSessionErrorAndFailsTheDelegate() // SimNet_activate 0x0054e86c, UDPTransport_activate 0058bfac
    {
        using var taken = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0));
        var port = ((System.Net.IPEndPoint)taken.Client.LocalEndPoint!).Port;
        Boot("$Console::PrintLevel = 2;\n");
        Eval("newServer(); focusServer();");
        // The result of the failed newObject is false and no other port is tried.
        Assert.NotEqual("True", Eval("newObject(serverDelegate, FearCSDelegate, true, \"IP\", " + port + ");"));
        Assert.Contains("SimNET: Session (server): transport IP on port " + port + ": ERROR", _app!.LogTail);
        Assert.False(_app.Hosted.Net!.IsOpen());
    }

    [Fact]
    public void AStartedTransportPrintsOkPerPair() // the word is Ok once any transport of the list has activated (bl, 0x0054e8d0)
    {
        Boot("$Console::PrintLevel = 2;\n");
        Eval("newServer(); focusServer(); newObject(serverDelegate, FearCSDelegate, true, \"LOOPBACK\", 0, \"IP\", 0);");
        Assert.Contains("SimNET: Session (server): transport LOOPBACK on port 0: Ok", _app!.LogTail);
        Assert.Contains("SimNET: Session (server): transport IP on port 0: Ok", _app.LogTail);
    }

    [Fact]
    public void AFailedIpBindStillServesWhenLoopbackStarted() // any started transport keeps the delegate; loopback is the only one left
    {
        using var taken = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0));
        var port = ((System.Net.IPEndPoint)taken.Client.LocalEndPoint!).Port;
        Boot("$Console::PrintLevel = 2;\n");
        Eval("newServer(); focusServer(); newObject(serverDelegate, FearCSDelegate, true, \"LOOPBACK\", 0, \"IP\", " + port + ");");
        Assert.True(_app!.Hosted.Net!.IsOpen());
        Assert.True(_app.Hosted.Net.LoopbackOnly);
        Assert.Contains("SimNET: Session (server): transport IP on port " + port + ": Ok", _app.LogTail);
    }

    // A tiny master: a loopback socket that stands in for the t1master style exchange (heartbeat, challenge, ping reply).
    private static System.Net.Sockets.UdpClient FakeMaster(out int port)
    {
        var m = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        m.Client.ReceiveTimeout = 300;
        port = ((System.Net.IPEndPoint)m.Client.LocalEndPoint!).Port;
        return m;
    }

    private byte[]? Receive(System.Net.Sockets.UdpClient m, out System.Net.IPEndPoint from, int waitFrames)
    {
        from = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
        for (var i = 0; i < waitFrames; ++i)
        {
            _app!.Step();
            if (m.Available > 0)
                return m.Receive(ref from);
            Thread.Sleep(2);
        }
        return null;
    }

    private void HostWithMaster(int masterPort, string single, string pub, string transports)
    {
        Boot("function checkMasterTranslation() { $inet::Master1 = \"IP:127.0.0.1:" + masterPort + "\"; }\n");
        Eval("$SinglePlayer = " + single + "; $Server::HostPublicGame = " + pub + ";");
        Eval("newServer(); focusServer(); newObject(serverDelegate, FearCSDelegate, true, " + transports + ");");
    }

    [Fact]
    public void APublicHostedGameAnnouncesItselfAndAnswersTheMasterChallenge() // SimNet_activate 0x0054e86c tail, 0x0054f478, 0x0054f608
    {
        using var master = FakeMaster(out var port);
        HostWithMaster(port, "false", "true", "\"IP\", 0, \"LOOPBACK\", 0");
        // The heartbeat leaves at network start, from the game port.
        var beat = Receive(master, out var from, 50);
        Assert.NotNull(beat);
        Assert.Equal(new byte[] { 0x10, 0x05, 0xff, 0, 0, 0, 0, 0 }, beat);
        Assert.Equal(_app!.Hosted.Net!.Port(), from.Port);
        // The master answers an unknown server with a challenge; the server returns its ping reply with the same key.
        master.Send([0x10, 0x03, 0xff, 0x00, 0x34, 0x12], 6, from);
        var reply = Receive(master, out _, 200);
        Assert.NotNull(reply);
        Assert.Equal(40, reply!.Length);
        Assert.Equal(new byte[] { 0x10, 0x04, 0xff, 0xf0, 0x34, 0x12 }, reply[..6]);
    }

    [Fact]
    public void AnUnsetHostPublicGameCountsAsPublicForAHostedGame() // 0x0054f478: an empty $Server::HostPublicGame becomes TRUE
    {
        using var master = FakeMaster(out var port);
        HostWithMaster(port, "false", "\"\"", "\"IP\", 0");
        Assert.NotNull(Receive(master, out _, 50));
        Assert.Equal("TRUE", _app!.Interp.PeekVariable("$Server::HostPublicGame"));
    }

    [Theory]
    [InlineData("true", "true", "\"IP\", 0")]               // $SinglePlayer: no announcement
    [InlineData("false", "false", "\"IP\", 0")]             // not public
    [InlineData("false", "true", "\"LOOPBACK\", 0")]        // no IP transport
    [InlineData("false", "true", "\"ip\", 0")]              // the transport name is compared exactly (0x0063ead5)
    public void AHostedGameThatIsNotPublicSendsNoHeartbeat(string singlePlayer, string pub, string transports)
    {
        using var master = FakeMaster(out var port);
        HostWithMaster(port, singlePlayer, pub, transports);
        Assert.Null(Receive(master, out _, 40));
    }
}

// The stock scripts end to end (createServer of server.cs, client-app.md 1.7): runs only when a stock game directory exists
// (OT_GAME_ROOT or the default install path), like the intro movie tests.
public sealed class HostGameStockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-hoststock-" + Guid.NewGuid().ToString("N"));
    private ClientApp? _app;

    public void Dispose()
    {
        _app?.Dispose();
        try
        { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private static string? GameRoot()
    {
        var root = Environment.GetEnvironmentVariable("OT_GAME_ROOT") ?? "/mnt/software/Games/Tribes/Tribes";
        return File.Exists(Path.Combine(root, "console.cs")) && File.Exists(Path.Combine(root, "base", "scripts.vol")) ? root : null;
    }

    private void BootStock(string mission, bool predict = false)
    {
        var game = GameRoot()!;
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "temp"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "base"), Path.Combine(game, "base"));
        File.Copy(Path.Combine(game, "console.cs"), Path.Combine(_root, "console.cs"));
        var opt = ClientOptions.Parse(predict ? ["--root", _root, "--headless"] : ["--root", _root, "--headless", "--no-predict"], out var err)!;
        Assert.Equal("", err);
        _app = new ClientApp(opt);
        Assert.True(_app.Start(out var error), error);
        _app.Interp.SetVariable("$pref::lastMission", mission);
        using var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        _app.Interp.SetVariable("$Server::Port", ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port.ToString());
    }

    private bool RunUntilConnected()
    {
        for (var i = 0; i < 4000 && _app!.Interp.PeekVariable("$ConnectedToServer") != "True"; ++i)
        {
            _app.Step();
            Thread.Sleep(1);
        }
        return _app!.Interp.PeekVariable("$ConnectedToServer") == "True";
    }

    [Fact]
    public void TheHostGameButtonStartsAMissionAndTheClientJoinsIt()
    {
        if (GameRoot() == null)
            return;
        BootStock("5_CTF");
        _app!.Interp.Evaluate("createServer();"); // the CREATE SERVER button of gui\\CreateServer.gui: command `createServer();`
        Assert.True(RunUntilConnected(), string.Join("\n", _app.LogTail.TakeLast(60)));
        Assert.True(_app.Hosted.Exists);
        Assert.NotNull(_app.Hosted.Sim!.Server()!.Find("MissionGroup"));
        Assert.Single(_app.Hosted.Net!.Players());
        Assert.False(_app.Hosted.Net.LoopbackOnly); // IP transport: a non-single-player game listens on all interfaces
        // End of game (GUI.CS EndGame): disconnect, then deleteServer ends the server world and closes its socket.
        _app.Interp.Evaluate("disconnect(); deleteServer();");
        Assert.False(_app.Hosted.Exists);
    }

    [Fact]
    public void TheClientPredictionWorldDoesNotDisturbTheHostedServer() // the process-wide hooks stay with the hosted world
    {
        if (GameRoot() == null)
            return;
        BootStock("5_CTF", predict: true);
        _app!.Interp.Evaluate("createServer();");
        Assert.True(RunUntilConnected(), string.Join("\n", _app.LogTail.TakeLast(60)));
        for (var i = 0; i < 600; ++i)
            _app.Step();
        Assert.NotNull(_app.Hosted.Sim!.Server()!.Find("MissionGroup"));
        Assert.True(_app.Game!.Joined);
    }

    [Fact]
    public void ASinglePlayerHostOpensNoOutsideSocket() // createServer with $SinglePlayer: LOOPBACK only (server.cs)
    {
        if (GameRoot() == null)
            return;
        BootStock("1_Welcome");
        _app!.Interp.SetVariable("$SinglePlayer", "true");
        _app.Interp.Evaluate("createServer();");
        Assert.True(RunUntilConnected(), string.Join("\n", _app.LogTail.TakeLast(60)));
        Assert.True(_app.Hosted.Net!.LoopbackOnly);
    }

    [Fact]
    public void TheHostScreenListsTheMissionsOfTheStockScripts() // gui\\CreateServer.gui and GUI.CS CreateServerGui::onOpen
    {
        if (GameRoot() == null)
            return;
        BootStock("5_CTF");
        _app!.Interp.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\CreateServer.gui\");");
        Assert.NotNull(_app.GuiRt.FindControl("MissionTextList"));
        _app.Step();
        var list = (Gui.GuiList)_app.GuiRt.FindControl("MissionTextList")!;
        Assert.True(list.Entries.Count > 0, string.Join("\n", _app.LogTail.TakeLast(40)));
    }
}
