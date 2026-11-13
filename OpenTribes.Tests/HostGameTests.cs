// Hosting a game from the client (GitHub issue 11, client-app.md 1.7): the client process keeps a server world next to the
// client world, with one console. The stock console functions newServer / focusServer / focusClient / deleteServer (cases
// 0x7c, 0x7a, 0x79, 0x7d of FearGame_consoleCallback), the focus per frame phase (FearWorld_advance 004eba40,
// FearWorld_clientAdvance 004eb7ac) and the join over the loopback transport (0058d7d4) of the same process. Synthetic
// game directory: no game data.
using OpenTribes.Client.App;

namespace OpenTribes.Tests;

public sealed class HostGameTests : IDisposable
{
    private readonly string mRoot = Path.Combine(Path.GetTempPath(), "ot-host-" + Guid.NewGuid().ToString("N"));
    private ClientApp? mApp;

    public HostGameTests()
    {
        Directory.CreateDirectory(Path.Combine(mRoot, "config"));
        Directory.CreateDirectory(Path.Combine(mRoot, "temp"));
    }

    public void Dispose()
    {
        mApp?.Dispose();
        try { Directory.Delete(mRoot, true); } catch (IOException) { }
    }

    private ClientApp Boot(string consoleCs = "")
    {
        File.WriteAllText(Path.Combine(mRoot, "console.cs"),
            "newClient(); focusClient();\n" +
            "newObject(MainWindow, SimGui::Canvas, \"Tribes\", 640, 480, True, \"512 384\", \"1024 768\");\n" +
            "function onConnection(%m) { $conn = %m; }\nfunction Game::EndFrame() {}\n" +
            "$ConsoleWorld::DefaultSearchPath = \"config;base;temp\";\n" + consoleCs);
        ClientOptions opt = ClientOptions.Parse(new[] { "--root", mRoot, "--headless" }, out string err)!;
        Assert.Equal("", err);
        mApp = new ClientApp(opt);
        Assert.True(mApp.Start(out string error), error);
        return mApp;
    }

    private string Eval(string text) => mApp!.Interp.Evaluate(text);

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
        Assert.False(mApp!.Hosted.Exists);
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
        Assert.Equal("7", mApp!.Interp.PeekVariable("$marked"));
    }

    [Fact]
    public void TheServerWorldHasItsOwnCommandsWhileFocused() // the server commands answer only under the server focus
    {
        Boot();
        Assert.False(mApp!.Interp.HasFunction("Client::getTransportAddress"));
        Eval("newServer(); focusServer();");
        Assert.True(mApp.Interp.HasFunction("Client::getTransportAddress"));
        Eval("focusClient();");
        Assert.False(mApp.Interp.HasFunction("Client::getTransportAddress"));
    }

    [Fact]
    public void TheFrameLeavesTheClientFocus()
    {
        Boot();
        Eval("newServer(); focusServer();");
        mApp!.Step();
        Assert.Equal("False", Eval("isObject(NoSuchObject);"));
        Eval("newObject(InClient, SimSet);");
        Assert.Null(mApp.Interp.FocusedWorld());
    }

    [Fact]
    public void TheClientJoinsItsOwnServerOverLoopback() // client-app.md 1.7 steps 3 and 5
    {
        Boot();
        Eval("newServer(); focusServer(); newObject(serverDelegate, FearCSDelegate, true, \"LOOPBACK\", 0);");
        Assert.True(mApp!.Hosted.Net!.IsOpen());
        ushort port = mApp.Hosted.Net.Port();
        Eval("focusClient();");
        Assert.True(mApp.Connect("LOOPBACK:" + port));
        for (int i = 0; i < 400 && mApp.Interp.PeekVariable("$conn") != "Accepted"; ++i)
        {
            mApp.Step();
            Thread.Sleep(1);
        }
        Assert.True(mApp.Interp.PeekVariable("$conn") == "Accepted", string.Join("\n", mApp.LogTail.Take(12)));
        Assert.Single(mApp.Hosted.Net.Players());
    }

    [Fact]
    public void ALoopbackOnlyGameDoesNotListenOnOtherAddresses() // the $SinglePlayer form opens no outside socket
    {
        Boot();
        Eval("newServer(); focusServer(); newObject(serverDelegate, FearCSDelegate, true, \"LOOPBACK\", 0);");
        Assert.True(mApp!.Hosted.Net!.IsOpen());
        Assert.True(mApp.Hosted.Net.LoopbackOnly);
        // Stock passes the packets in memory: a real UDP socket reaches nothing of this game.
        using var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        probe.Client.ReceiveTimeout = 50;
        probe.Send(new byte[] { 0x10, 0x05 }, 2, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, mApp.Hosted.Net.Port()));
        Assert.Equal(0, probe.Available);
    }

    [Fact]
    public void AnIpBindThatFailsPrintsTheSessionErrorAndFailsTheDelegate() // SimNet_activate 0x0054e86c, UDPTransport_activate 0058bfac
    {
        using var taken = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0));
        int port = ((System.Net.IPEndPoint)taken.Client.LocalEndPoint!).Port;
        Boot("$Console::PrintLevel = 2;\n");
        Eval("newServer(); focusServer();");
        // The result of the failed newObject is false and no other port is tried.
        Assert.NotEqual("True", Eval("newObject(serverDelegate, FearCSDelegate, true, \"IP\", " + port + ");"));
        Assert.Contains("SimNET: Session (server): transport IP on port " + port + ": ERROR", mApp!.LogTail);
        Assert.False(mApp.Hosted.Net!.IsOpen());
    }

    [Fact]
    public void AStartedTransportPrintsOkPerPair() // the word is Ok once any transport of the list has activated (bl, 0x0054e8d0)
    {
        Boot("$Console::PrintLevel = 2;\n");
        Eval("newServer(); focusServer(); newObject(serverDelegate, FearCSDelegate, true, \"LOOPBACK\", 0, \"IP\", 0);");
        Assert.Contains("SimNET: Session (server): transport LOOPBACK on port 0: Ok", mApp!.LogTail);
        Assert.Contains("SimNET: Session (server): transport IP on port 0: Ok", mApp.LogTail);
    }

    [Fact]
    public void AFailedIpBindStillServesWhenLoopbackStarted() // any started transport keeps the delegate; loopback is the only one left
    {
        using var taken = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0));
        int port = ((System.Net.IPEndPoint)taken.Client.LocalEndPoint!).Port;
        Boot("$Console::PrintLevel = 2;\n");
        Eval("newServer(); focusServer(); newObject(serverDelegate, FearCSDelegate, true, \"LOOPBACK\", 0, \"IP\", " + port + ");");
        Assert.True(mApp!.Hosted.Net!.IsOpen());
        Assert.True(mApp.Hosted.Net.LoopbackOnly);
        Assert.Contains("SimNET: Session (server): transport IP on port " + port + ": Ok", mApp.LogTail);
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
        for (int i = 0; i < waitFrames; ++i)
        {
            mApp!.Step();
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
        using var master = FakeMaster(out int port);
        HostWithMaster(port, "false", "true", "\"IP\", 0, \"LOOPBACK\", 0");
        // The heartbeat leaves at network start, from the game port.
        byte[]? beat = Receive(master, out var from, 50);
        Assert.NotNull(beat);
        Assert.Equal(new byte[] { 0x10, 0x05, 0xff, 0, 0, 0, 0, 0 }, beat);
        Assert.Equal(mApp!.Hosted.Net!.Port(), from.Port);
        // The master answers an unknown server with a challenge; the server returns its ping reply with the same key.
        master.Send(new byte[] { 0x10, 0x03, 0xff, 0x00, 0x34, 0x12 }, 6, from);
        byte[]? reply = Receive(master, out _, 200);
        Assert.NotNull(reply);
        Assert.Equal(40, reply!.Length);
        Assert.Equal(new byte[] { 0x10, 0x04, 0xff, 0xf0, 0x34, 0x12 }, reply[..6]);
    }

    [Fact]
    public void AnUnsetHostPublicGameCountsAsPublicForAHostedGame() // 0x0054f478: an empty $Server::HostPublicGame becomes TRUE
    {
        using var master = FakeMaster(out int port);
        HostWithMaster(port, "false", "\"\"", "\"IP\", 0");
        Assert.NotNull(Receive(master, out _, 50));
        Assert.Equal("TRUE", mApp!.Interp.PeekVariable("$Server::HostPublicGame"));
    }

    [Theory]
    [InlineData("true", "true", "\"IP\", 0")]               // $SinglePlayer: no announcement
    [InlineData("false", "false", "\"IP\", 0")]             // not public
    [InlineData("false", "true", "\"LOOPBACK\", 0")]        // no IP transport
    [InlineData("false", "true", "\"ip\", 0")]              // the transport name is compared exactly (0x0063ead5)
    public void AHostedGameThatIsNotPublicSendsNoHeartbeat(string single, string pub, string transports)
    {
        using var master = FakeMaster(out int port);
        HostWithMaster(port, single, pub, transports);
        Assert.Null(Receive(master, out _, 40));
    }
}

// The stock scripts end to end (createServer of server.cs, client-app.md 1.7): runs only when a stock game directory exists
// (OT_GAME_ROOT or the default install path), like the intro movie tests.
public sealed class HostGameStockTests : IDisposable
{
    private readonly string mRoot = Path.Combine(Path.GetTempPath(), "ot-hoststock-" + Guid.NewGuid().ToString("N"));
    private ClientApp? mApp;

    public void Dispose()
    {
        mApp?.Dispose();
        try { Directory.Delete(mRoot, true); } catch (IOException) { }
    }

    private static string? GameRoot()
    {
        string root = Environment.GetEnvironmentVariable("OT_GAME_ROOT") ?? "/mnt/software/Games/Tribes/Tribes";
        return File.Exists(Path.Combine(root, "console.cs")) && File.Exists(Path.Combine(root, "base", "scripts.vol")) ? root : null;
    }

    private void BootStock(string mission, bool predict = false)
    {
        string game = GameRoot()!;
        Directory.CreateDirectory(mRoot);
        Directory.CreateDirectory(Path.Combine(mRoot, "config"));
        Directory.CreateDirectory(Path.Combine(mRoot, "temp"));
        Directory.CreateSymbolicLink(Path.Combine(mRoot, "base"), Path.Combine(game, "base"));
        File.Copy(Path.Combine(game, "console.cs"), Path.Combine(mRoot, "console.cs"));
        ClientOptions opt = ClientOptions.Parse(predict ? new[] { "--root", mRoot, "--headless" } : new[] { "--root", mRoot, "--headless", "--no-predict" }, out string err)!;
        Assert.Equal("", err);
        mApp = new ClientApp(opt);
        Assert.True(mApp.Start(out string error), error);
        mApp.Interp.SetVariable("$pref::lastMission", mission);
        using var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        mApp.Interp.SetVariable("$Server::Port", ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port.ToString());
    }

    private bool RunUntilConnected()
    {
        for (int i = 0; i < 4000 && mApp!.Interp.PeekVariable("$ConnectedToServer") != "True"; ++i)
        {
            mApp.Step();
            Thread.Sleep(1);
        }
        return mApp!.Interp.PeekVariable("$ConnectedToServer") == "True";
    }

    [Fact]
    public void TheHostGameButtonStartsAMissionAndTheClientJoinsIt()
    {
        if (GameRoot() == null)
            return;
        BootStock("5_CTF");
        mApp!.Interp.Evaluate("createServer();"); // the CREATE SERVER button of gui\\CreateServer.gui: command `createServer();`
        Assert.True(RunUntilConnected(), string.Join("\n", mApp.LogTail.TakeLast(60)));
        Assert.True(mApp.Hosted.Exists);
        Assert.NotNull(mApp.Hosted.Sim!.Server()!.Find("MissionGroup"));
        Assert.Single(mApp.Hosted.Net!.Players());
        Assert.True(mApp.Hosted.Net.LoopbackOnly == false); // IP transport: a non-single-player game listens on all interfaces
        // End of game (GUI.CS EndGame): disconnect, then deleteServer ends the server world and closes its socket.
        mApp.Interp.Evaluate("disconnect(); deleteServer();");
        Assert.False(mApp.Hosted.Exists);
    }

    [Fact]
    public void TheClientPredictionWorldDoesNotDisturbTheHostedServer() // the process-wide hooks stay with the hosted world
    {
        if (GameRoot() == null)
            return;
        BootStock("5_CTF", predict: true);
        mApp!.Interp.Evaluate("createServer();");
        Assert.True(RunUntilConnected(), string.Join("\n", mApp.LogTail.TakeLast(60)));
        for (int i = 0; i < 600; ++i)
            mApp.Step();
        Assert.NotNull(mApp.Hosted.Sim!.Server()!.Find("MissionGroup"));
        Assert.True(mApp.Game!.Joined);
    }

    [Fact]
    public void ASinglePlayerHostOpensNoOutsideSocket() // createServer with $SinglePlayer: LOOPBACK only (server.cs)
    {
        if (GameRoot() == null)
            return;
        BootStock("1_Welcome");
        mApp!.Interp.SetVariable("$SinglePlayer", "true");
        mApp.Interp.Evaluate("createServer();");
        Assert.True(RunUntilConnected(), string.Join("\n", mApp.LogTail.TakeLast(60)));
        Assert.True(mApp.Hosted.Net!.LoopbackOnly);
    }

    [Fact]
    public void TheHostScreenListsTheMissionsOfTheStockScripts() // gui\\CreateServer.gui and GUI.CS CreateServerGui::onOpen
    {
        if (GameRoot() == null)
            return;
        BootStock("5_CTF");
        mApp!.Interp.Evaluate("GuiLoadContentCtrl(MainWindow, \"gui\\\\CreateServer.gui\");");
        Assert.NotNull(mApp.GuiRt.FindControl("MissionTextList"));
        mApp.Step();
        var list = (OpenTribes.Gui.GuiList)mApp.GuiRt.FindControl("MissionTextList")!;
        Assert.True(list.Entries.Count > 0, string.Join("\n", mApp.LogTail.TakeLast(40)));
    }
}
