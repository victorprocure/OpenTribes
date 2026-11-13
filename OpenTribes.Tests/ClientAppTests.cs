// Tests of the `tribes` client (ROADMAP M9 C8): the command table with real effects (docs/client-scripts.md 2.2 to 2.15), the
// engine variables and prefs files (5.1, 5.4), the play delegate and the move builder (3.8, client-net.md 4.1, 4.2), the timed
// script and the command line. The client runs headless on a synthetic game directory (R5): no window, no game data.
namespace OpenTribes.Tests;

using OpenTribes.Client.App;
using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Script;

public sealed class ClientAppTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-c8-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _lines = [];
    private ClientApp? _app;

    public ClientAppTests()
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

    private ClientApp Boot(string consoleCs = "", params string[] args)
    {
        File.WriteAllText(Path.Combine(_root, "console.cs"),
            "newClient(); focusClient();\n" +
            "newObject(MainWindow, SimGui::Canvas, \"Tribes\", 640, 480, True, \"512 384\", \"1024 768\");\n" +
            "function onConnection(%m) { $conn = %m; }\nfunction Game::EndFrame() {}\n" +
            "$ConsoleWorld::DefaultSearchPath = \"config;base;temp\";\n" + consoleCs);
        var all = new List<string> { "--root", _root, "--headless" };
        all.AddRange(args);
        var opt = ClientOptions.Parse([.. all], out var err)!;
        Assert.Equal("", err);
        _app = new ClientApp(opt);
        Assert.True(_app.Start(out var error), error);
        _app.Interp.SetOutput(_lines.Add);
        return _app;
    }

    private string Eval(string text) => _app!.Interp.Evaluate(text);

    // ---- quit ----

    [Fact]
    public void LoadingTheQuitScreenEndsTheProcess() // client-app.md 6.1: the exit dialog loads quit.gui, whose delegate quits when installed
    {
        Boot();
        Assert.False(_app!.QuitRequested);
        _app.Canvas.SetContent(new GuiControl { Name = "BlankQuitGui" }, _app.Canvas.Delegates["quit"]);
        Assert.True(_app.QuitRequested);
    }

    [Fact]
    public void OtherDelegatesDoNotQuit()
    {
        Boot();
        _app!.Canvas.SetContent(new GuiControl { Name = "x" }, _app.Canvas.Delegates["MainMenu"]);
        Assert.False(_app.QuitRequested);
    }

    // ---- HUD energy ----

    [Fact]
    public void EnergyLevelIsEnergyOverTheDatablockMaximum() // gui.md 4.2 JetPackHud: PSC energy is absolute, maxEnergy is in the datablock
    {
        var world = new ClientWorld();
        world.Psc.ControlGhost = 5;
        var ghost = new ClientGhost { Index = 5, Class = "Player" };
        ghost.State["datablock"] = 2L;
        world.Ghosts.Insert(ghost);
        world.DataBlocks.Blocks[(3, 2)] = new ClientDataBlock
        {
            Group = 3,
            Index = 2,
            Class = "Armor",
            Fields = [new("maxEnergy", 60.0)],
        };
        world.Psc.ControlState["energy"] = 15.0;
        var src = new ClientHudSource(world);
        Assert.Equal(0.25, src.EnergyLevel, 6);
        world.Psc.ControlState["energy"] = 90.0;
        Assert.Equal(1.0, src.EnergyLevel, 6);
    }

    // ---- 2.3 display and canvas ----

    [Fact]
    public void GammaIsClampedAndStored()
    {
        // client-scripts.md 2.3 setGamma.
        Boot();
        Eval("setGamma(MainWindow, 50);");
        Assert.Contains("Out of bounds gamma, must be between 0.05 and 20, value clamped", _lines);
        Assert.Equal("20", _app!.Interp.PeekVariable("$pref::Display::gammaValue"));
        Assert.Equal(20f, _app.Gamma);
        _lines.Clear();
        Eval("setGamma(MainWindow, 1.5);");
        Assert.Empty(_lines);
        Assert.Equal("1.5", _app.Interp.PeekVariable("$pref::Display::gammaValue"));
    }

    [Fact]
    public void WrongCanvasNamePrintsAndFails()
    {
        // 2.3: a wrong canvas name prints `Could not find simCanvas: <name>` and returns False.
        Boot();
        Assert.Equal("False", Eval("setGamma(NoWindow, 1);"));
        Assert.Contains("Could not find simCanvas: NoWindow", _lines);
    }

    [Fact]
    public void DevicesAreTheOneGlDeviceWithTheStockNames()
    {
        // D31: OpenGL is the device; Software and Glide are accepted names; D36 listDevices reports the one device.
        Boot();
        Assert.Equal("True", Eval("setFullscreenDevice(MainWindow, \"Software\");"));
        Assert.Equal("True", Eval("setWindowedDevice(MainWindow, \"Glide\");"));
        Assert.Equal("False", Eval("setFullscreenDevice(MainWindow, \"Direct3D\");"));
        Assert.Contains("Failed to set Fullscreen Device to Direct3D", _lines);
        Assert.Equal("", Eval("testDevice(OpenGL);"));
        Assert.Equal("False", Eval("testDevice(Voodoo);"));
        Assert.Equal("true", Eval("isGfxDriver(MainWindow, \"OpenGL\");"));
        Assert.Equal("False", Eval("isGfxDriver(MainWindow, \"Glide\");"));
        _lines.Clear();
        Eval("listDevices();");
        Assert.Equal("Number of devices: 1", _lines[0]);
        Assert.StartsWith("OpenGL", _lines[1]);
        Assert.Equal("false", Eval("isVirtualFS(MainWindow);"));
    }

    [Fact]
    public void FullscreenQueriesAnswerInTheStockSpelling()
    {
        // 2.3: isFullscreenMode answers `true` or `FALSE` verbatim; the headless client has no window, so it is windowed.
        Boot();
        Assert.Equal("FALSE", Eval("isFullscreenMode(MainWindow);"));
        Eval("setFullscreenMode(MainWindow, \"true\");");
        Assert.Equal("FALSE", Eval("isFullscreenMode(MainWindow);"));
    }

    [Fact]
    public void NextResolutionStepsTheModeLadderAndPrintsIt()
    {
        // 2.3 nextRes / prevRes print `Mode: W x H`.
        Boot();
        Eval("nextRes(MainWindow);");
        Assert.Equal("Mode: 800 x 600", _lines[^1]);
        Eval("prevRes(MainWindow);");
        Assert.Equal("Mode: 640 x 480", _lines[^1]);
        Eval("prevRes(MainWindow);");
        Assert.Equal("Mode: 1600 x 1200", _lines[^1]);
    }

    [Fact]
    public void ScreenShotNeedsAWriteablePath()
    {
        // 2.3 screenShot and the writeable path rule (client-app.md 5.2).
        Boot();
        Assert.Equal("False", Eval("screenShot(MainWindow, \"base\\\\x.bmp\");"));
        Assert.Contains("Error creating file 'base\\x.bmp'", _lines);
        Assert.Equal("", Eval("screenShot(MainWindow, \"temp\\\\x.bmp\");"));
    }

    // ---- 2.2 shell, 2.5 redbook ----

    [Fact]
    public void ShellCommandsCheckTheirArgumentsAndOpenNothingWithoutTheOption()
    {
        // 2.2: prefix check; R6: the system opens nothing unless the user passed --allow-browser.
        Boot();
        Assert.Equal("False", Eval("HTMLOpen(\"www.example.com\");"));
        Assert.Contains("HTMLOpen: can only open properly prefixed HTML URLs.", _lines);
        Assert.Equal("True", Eval("HTMLOpen(\"http://127.0.0.1/\");"));
        Assert.Equal("False", Eval("Notepad(\"a.doc\");"));
        Assert.Contains("Notepad: can only open .txt files", _lines);
    }

    [Fact]
    public void RedbookIsAbsentLikeAMachineWithoutADrive()
    {
        // 2.5 and D36: newRedbook fails, every later call finds no object (the install's console.log line).
        Boot();
        Assert.Equal("False", Eval("newRedbook(CD, MainWindow);"));
        _lines.Clear();
        Eval("rbSetVolume(CD, 0.5);");
        Eval("rbPlay(CD, 2);");
        Assert.Equal(new[] { "unable to find Redbook Obj CD", "unable to find Redbook Obj CD" }, _lines);
    }

    // ---- 2.4 net and R6 ----

    [Fact]
    public void ConnectToARemoteAddressFailsLocally()
    {
        // R6: nothing but 127.0.0.0/8 is contacted; the join screen sees onConnection("Rejected") and the error text.
        Boot();
        Eval("connect(\"IP:203.0.113.9:28001\");");
        Assert.Equal("Rejected", _app!.Interp.PeekVariable("$conn"));
        Assert.Contains("127.0.0.0/8", _app.Interp.PeekVariable("$errorString"));
        Assert.Null(_app.Game);
    }

    [Fact]
    public void MasterListStaysLocal()
    {
        // R6, client-scripts.md 4.1: DNet::resolveMasters resolves nothing that is not a loopback address.
        Boot();
        // 00422ad8: the count is the number of names given (up to the first empty one, at most 10), resolved or not
        Assert.Equal("2", Eval("DNet::resolveMasters(\"IP:master1.example.org:28000\", \"IP:198.51.100.7:28000\");"));
        Assert.Equal("", Eval("DNet::getResolvedMaster(0);"));
        Assert.Equal("", Eval("DNet::getResolvedMaster(1);"));
        Assert.Equal("2", Eval("DNet::resolveMasters(\"IP:master1.example.org:28000\", \"IP:127.0.0.1:28000\", \"\", \"x\");"));
        // 00422a74: a resolved master reads back as the address text in its own slot, the others as nothing
        Assert.Equal("", Eval("DNet::getResolvedMaster(0);"));
        Assert.Equal("IP:127.0.0.1:28000", Eval("DNet::getResolvedMaster(1);"));
        Assert.Equal("0", Eval("DNet::resolveMasters();"));
        Assert.Equal("10", Eval("DNet::resolveMasters(\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\",\"i\",\"j\",\"k\");"));
    }

    [Theory]
    [InlineData("127.0.0.1:28001", true)]
    [InlineData("IP:127.5.5.5:28001", true)]
    [InlineData("LOOPBACK:28001", true)]
    [InlineData("localhost:28001", true)]
    [InlineData("10.0.0.5:28001", false)]
    [InlineData("IP:example.org:28001", false)]
    public void LocalAddressRule(string address, bool local)
    {
        Boot();
        Assert.Equal(local, ClientApp.IsLocalAddress(address));
    }

    [Fact]
    public void DisconnectWithoutAGameSaysSo()
    {
        Boot();
        Eval("disconnect();");
        Assert.Contains("Disconnect: no CSDelegate.", _lines);
    }

    // ---- 2.6 and 2.7 input ----

    [Fact]
    public void DevicesActivateAndDeactivateByName()
    {
        // 2.6: inputActivate(keyboard0), an unknown device prints; a deactivated device delivers nothing.
        Boot("newActionMap(\"t.sae\"); bindCommand(mouse0, make, button0, TO, \"$Click = 1;\"); pushActionMap(\"t.sae\");");
        Assert.Equal("False", Eval("inputActivate(gamepad9);"));
        Assert.Contains("inputActivate: unknown device gamepad9", _lines);
        Eval("cursorOff(MainWindow);");   // the GUI takes the mouse while the cursor is on (client-app.md 3.8)
        Eval("inputDeactivate(mouse0);");
        _app!.InjectButton(0, true);
        Assert.Equal("", _app.Interp.PeekVariable("$Click"));
        _app.InjectButton(0, false);
        Eval("inputActivate(mouse0);");
        _app.InjectButton(0, true);
        Assert.Equal("1", _app.Interp.PeekVariable("$Click"));
    }

    [Fact]
    public void JoystickStartsInactiveAndEventsReachBindingsOnceActivated()
    {
        // Stock: the device's active flag starts clear (0050e9e8) and only inputActivate sets it (00520cea, 0050e220); with no
        // device attached the name is unknown.
        Boot("newActionMap(\"t.sae\"); bindCommand(joystick0, make, button2, TO, \"$Fire = $Fire @ 1;\"); pushActionMap(\"t.sae\");");
        Assert.Equal("False", Eval("inputActivate(joystick0);"));
        Assert.Contains("inputActivate: unknown device joystick0", _lines);
        _app!.Joysticks = Platform.Sdl.JoystickSet.Simulated("Test stick");
        _app.InjectJoyButton(0, 2, true);
        Assert.Equal("", _app.Interp.PeekVariable("$Fire"));
        _app.InjectJoyButton(0, 2, false);
        Assert.Equal("True", Eval("inputActivate(joystick0);"));
        _app.InjectJoyButton(0, 2, true);
        Assert.Equal("1", _app.Interp.PeekVariable("$Fire"));
        _app.InjectJoyButton(0, 2, false);
        Eval("inputDeactivate(joystick0);");
        _app.InjectJoyButton(0, 2, true);
        Assert.Equal("1", _app.Interp.PeekVariable("$Fire"));
        // inputActivate(all) activates every device (0050e220 with 0xff, 0xff).
        Eval("inputActivate(all);");
        _app.InjectJoyButton(0, 2, false);
        _app.InjectJoyButton(0, 2, true);
        Assert.Equal("11", _app.Interp.PeekVariable("$Fire"));
    }

    [Fact]
    public void PushAndPopActionMapCommandsAreRealAndNeedAMap()
    {
        // 2.6 and 3.8: pushActionMap puts the map on the stack, popActionMap takes it off; a map binding a command works.
        Boot("newActionMap(\"t.sae\"); bindCommand(keyboard, make, q, TO, \"$Q = $Q @ 1;\");");
        _app!.InjectKey(0x10, true);
        _app.InjectKey(0x10, false);
        Assert.Equal("", _app.Interp.PeekVariable("$Q"));
        Assert.Equal("True", Eval("pushActionMap(\"t.sae\");"));
        Assert.Equal(1, _app.Input.StackDepth);
        _app.InjectKey(0x10, true);
        _app.InjectKey(0x10, false);
        Assert.Equal("1", _app.Interp.PeekVariable("$Q"));
        Eval("popActionMap(\"t.sae\");");
        Assert.Equal(0, _app.Input.StackDepth);
    }

    // ---- 5 variables and prefs ----

    [Theory]
    [InlineData(1, "yes", "False")]
    [InlineData(1, "TRUE", "True")]
    [InlineData(1, "1", "True")]
    [InlineData(2, "12abc", "12")]
    [InlineData(2, "-3.9", "-3")]
    [InlineData(5, "7", "1.000000")]
    [InlineData(5, "-1", "0.000000")]
    [InlineData(5, "0.25", "0.250000")]
    [InlineData(3, "90", "90")]
    [InlineData(0, "anything", "anything")]
    public void TypedVariablesReadBackInTheirFormat(int type, string input, string expected) =>
        // client-scripts.md 5.1 types 0 string, 1 bool, 2 int, 3 float, 5 float clamped to 0..1.
        Assert.Equal(expected, EngineVariables.Normalize(type, input));

    [Fact]
    public void EngineVariablesStartAtTheirImageDefaultAndNormalise()
    {
        Boot();
        Assert.Equal("50", _app!.Interp.PeekVariable("$pref::NumDecals"));
        Assert.Equal("1.000000", _app.Interp.PeekVariable("$pref::TerrainDetail"));
        Assert.Equal("False", _app.Interp.PeekVariable("$pref::IgnoreTargets"));
        Eval("$pref::TerrainDetail = 3; $pref::NumDecals = \"12abc\"; $pref::IgnoreTargets = true;");
        Assert.Equal("1.000000", _app.Interp.PeekVariable("$pref::TerrainDetail"));
        Assert.Equal("12", _app.Interp.PeekVariable("$pref::NumDecals"));
        Assert.Equal("True", _app.Interp.PeekVariable("$pref::IgnoreTargets"));
    }

    [Fact]
    public void NetTimingVariablesHaveTheirStock111Defaults()
    {
        // 004eaec0 and 004eaec8; LastHope 1.30 ignores them (L3) and re-points interpolateTime into a string.
        Boot();
        Assert.Equal("0", _app!.Interp.PeekVariable("$net::predictForwardTime"));
        Assert.Equal("64", _app.Interp.PeekVariable("$net::interpolateTime"));
        Eval("$net::interpolateTime = 0; $net::predictForwardTime = \"12abc\";");
        Assert.Equal("0", _app.Interp.PeekVariable("$net::interpolateTime"));
        Assert.Equal("12", _app.Interp.PeekVariable("$net::predictForwardTime"));
    }

    [Fact]
    public void TerrainRendererVariablesAreRegisteredAsIn111()
    {
        // 1.11 registers $screenError, $screenSize, $minSquareSize, $farDistance, $renderSimTerrain and the T2 statistics when the
        // terrain renderer is created (0x005768e4); LastHope 1.30 stubbed that out (lasthope-1.30.md L7).
        Boot();
        Assert.Equal("0", _app!.Interp.PeekVariable("$screenError"));
        EngineVariables.RegisterTerrain(_app.Interp);
        Assert.Equal("9", _app.Interp.PeekVariable("$screenError"));
        Assert.Equal("45", _app.Interp.PeekVariable("$screenSize"));
        Assert.Equal("4", _app.Interp.PeekVariable("$minSquareSize"));
        Assert.Equal("500", _app.Interp.PeekVariable("$farDistance"));
        Assert.Equal("True", _app.Interp.PeekVariable("$renderSimTerrain"));
        Assert.Equal("0", _app.Interp.PeekVariable("$T2::staticTSU"));
        Eval("$screenError = 3.5;");
        Assert.Equal("3.5", _app.Interp.PeekVariable("$screenError"));
    }

    [Fact]
    public void PrefsSaveToConfigAndLoadBack()
    {
        // 5.4: export writes sorted `$name = "value";` lines to config\ClientPrefs.cs; exec of that file restores them.
        Boot();
        Eval("$pref::zeta = \"last\"; $pref::alpha = \"first\"; $pref::TerrainDetail = 0.5; export(\"pref::*\", \"config\\\\ClientPrefs.cs\", False);");
        var text = File.ReadAllText(Path.Combine(_root, "config", "ClientPrefs.cs"));
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("$pref::alpha = \"first\";", lines);
        Assert.Contains("$pref::TerrainDetail = \"0.500000\";", lines);
        Assert.True(Array.IndexOf(lines, "$pref::alpha = \"first\";") < Array.IndexOf(lines, "$pref::zeta = \"last\";"));
        // A second client run reads the same file (client-scripts.md 6.1 step 11).
        _app!.Dispose();
        _app = null;
        Boot("exec(\"clientPrefs.cs\");");
        Assert.Equal("first", _app!.Interp.PeekVariable("$pref::alpha"));
        Assert.Equal("0.500000", _app.Interp.PeekVariable("$pref::TerrainDetail"));
    }

    [Fact]
    public void PrefsOutsideTheWriteablePathsAreRefused()
    {
        // client-app.md 5.2: only recordings, temp, config, base\missions, base\recordings and base\gui accept writes.
        Boot();
        Eval("export(\"pref::*\", \"scripts\\\\x.cs\", False);");
        Assert.False(File.Exists(Path.Combine(_root, "scripts", "x.cs")));
        Eval("export(\"pref::*\", \"temp\\\\x.cs\", False);");
        Assert.True(File.Exists(Path.Combine(_root, "temp", "x.cs")));
    }

    [Fact]
    public void ActionMapsSaveAndReloadWithTheirFlags()
    {
        // client-scripts.md 3.11 and 6.1: config\config.cs is NewActionMap plus bindAction lines, executed at start-up.
        Boot("newActionMap(\"playMap.sae\");" +
             "IDACTION_YAW = 00220004; IDACTION_MOVEFORWARD = 00220010;" +
             "bindAction(mouse0, xaxis0, TO, IDACTION_YAW, Flip, Scale, 0.000991);" +
             "bindAction(keyboard0, make, \"w\", TO, IDACTION_MOVEFORWARD, 1.0);" +
             "bindCommand(keyboard0, make, control, \"p\", TO, \"drop(BackPack);\");" +
             "saveActionMap(\"config\\\\config.cs\", \"playMap.sae\");");
        var text = File.ReadAllText(Path.Combine(_root, "config", "config.cs"));
        Assert.StartsWith("NewActionMap(\"playMap.sae\");\n", text);
        Assert.Contains("bindAction(mouse0, xaxis0, TO, IDACTION_YAW, Flip, Scale, 0.00099", text);
        Assert.Contains("bindAction(keyboard0, make, \"w\", TO, IDACTION_MOVEFORWARD, 1.000000);", text);
        _app!.Dispose();
        _app = null;
        Boot("IDACTION_YAW = 00220004; IDACTION_MOVEFORWARD = 00220010; exec(\"config.cs\");");
        var m = _app!.Maps.FindMap("playMap.sae")!;
        Assert.Equal(3, m.Bindings.Count);
        Assert.InRange(m.Bindings[0].ScaleF, 0.00098f, 0.00100f);
        Assert.Equal("drop(BackPack);", m.Bindings[2].Command);
        // The sensitivity the options screen reads back (3.7).
        Assert.InRange(double.Parse(Eval("Client::getMouseSensitivity(\"playMap.sae\");"), System.Globalization.CultureInfo.InvariantCulture), 0.00098, 0.00100);
        Assert.Equal("FALSE", Eval("Client::getMouseXAxisFlip(\"playMap.sae\");"));
    }

    // ---- 3.8 play delegate and 4.1 / 4.2 moves ----

    private void EnterPlay()
    {
        _app!.Game = new LiveGame(_app);
        var play = new GuiControl { Id = PlayScreenHandler.PlayGuiId };
        _app.Canvas.SetContent(play);
        _app.Play.UpdateContent(play.Id);
    }

    [Fact]
    public void PlayScreenPushesTheMapsAndTakesTheMouse()
    {
        // client-scripts.md 3.8: playGui pushes playMap then actionMap; leaving pops them; the play view owns the mouse.
        Boot("newActionMap(\"actionMap.sae\"); newActionMap(\"playMap.sae\"); newActionMap(\"pdaMap.sae\");");
        EnterPlay();
        Assert.Equal(2, _app!.Input.StackDepth);
        Assert.False(_app.Canvas.CursorOn);
        Assert.Equal("True", _app.Interp.PeekVariable("$ConnectedToServer"));
        var menu = new GuiControl { Id = 100100 };
        _app.Canvas.SetContent(menu);
        _app.Play.UpdateContent(menu.Id);
        Assert.Equal(0, _app.Input.StackDepth);
        Assert.True(_app.Canvas.CursorOn);
    }

    [Fact]
    public void MovementKeysFillTheMoveAndMouseLookTurnsIt()
    {
        // client-net.md 4.1 and the stock playMap of client-scripts.md 3.9.
        Boot("newActionMap(\"actionMap.sae\");" +
             "newActionMap(\"playMap.sae\");" +
             "bindAction(keyboard0, make, \"w\", TO, 220010, 1.0); bindAction(keyboard0, break, \"w\", TO, 220010, 0.0);" +
             "bindAction(mouse0, make, button1, TO, 220107, 1.0);" +
             "bindAction(mouse0, xaxis0, TO, 220004, Flip, Scale, 0.001);" +
             "newActionMap(\"pdaMap.sae\");");
        EnterPlay();
        _app!.InjectKey(0x11, true);
        Assert.Equal(1f, _app.Play.Moves.Forward);
        _app.InjectKey(0x11, false);
        Assert.Equal(0f, _app.Play.Moves.Forward);
        _app.InjectButton(1, true);
        Assert.True(_app.Play.Moves.JetOn);
        _app.InjectAxis(0, 100f);
        // Flip makes the value 1 - 100, scale 0.001, then * pi (scaled(v) with fov 90): a turn to the right.
        Assert.True(_app.Play.Moves.TurnAcc < 0);
    }

    [Fact]
    public void MoveBuilderMakesOneMovePerCrossedTick()
    {
        // client-net.md 4.2: n = ((now - 1) >> 5) - ((prev - 1) >> 5) moves; at most 30 wait for acknowledgement.
        var psc = new ClientPsc();
        var b = new MoveBuilder();
        Assert.Equal(0, b.Collect(psc, 100, 120));    // 3 -> 3: no tick crossed
        Assert.Equal(1, b.Collect(psc, 120, 130));    // 3 -> 4
        Assert.Equal(3, b.Collect(psc, 130, 230));    // 4 -> 7
        Assert.Equal(4, psc.Moves.Count);
        b.OnAction(MoveBuilder.MoveForward, 1f);
        b.Collect(psc, 230, 100000);
        Assert.Equal(MoveBuilder.MaxMoves, psc.Moves.Count);
        Assert.Equal(1f, psc.Moves[^1].Forward);
    }

    [Fact]
    public void FireCounterMakesAPressAndReleaseInsideOneTickFireOnce()
    {
        // client-net.md 4.1 FIRE1 / BREAK1 and 4.2 step 4.
        var psc = new ClientPsc();
        var b = new MoveBuilder();
        b.OnAction(MoveBuilder.Fire1, 0);
        b.OnAction(MoveBuilder.Break1, 0);
        Assert.Equal(1, b.Collect(psc, 1, 40));
        Assert.True(psc.Moves[0].Fire);
        b.Collect(psc, 40, 80);
        Assert.False(psc.Moves[1].Fire);
        b.OnAction(MoveBuilder.Fire1, 0);   // held
        b.Collect(psc, 80, 150);
        Assert.True(psc.Moves[^1].Fire);
    }

    [Fact]
    public void RunAndStrafeSplitTheirAxis()
    {
        var b = new MoveBuilder();
        b.OnAction(MoveBuilder.Run, 0.5f);
        Assert.Equal((1f, 0f), (b.Forward, b.Backward));
        b.OnAction(MoveBuilder.Run, -0.5f);
        Assert.Equal((0f, 1f), (b.Forward, b.Backward));
        b.OnAction(MoveBuilder.Strafe, 0.005f);
        Assert.Equal((0f, 0f), (b.Left, b.Right));
        b.OnAction(MoveBuilder.IncSniperFov, 1f);
        b.UpdateFov(10);
        Assert.Equal(90, b.CurrentFov);           // not zoomed: only the target changes (PlayerPSC_onActionDefault 0x35bd2)
        b.OnAction(MoveBuilder.SniperFov, 1f);
        b.UpdateFov(20);
        Assert.Equal(11.25, b.CurrentFov);
        b.OnAction(MoveBuilder.IncSniperFov, 1f);
        b.UpdateFov(30);
        Assert.Equal(5.625, b.CurrentFov);
    }

    // ---- automation ----

    [Theory]
    [InlineData("w", 0x11, false)]
    [InlineData("W", 0x11, true)]
    [InlineData("escape", 0x01, false)]
    [InlineData("Enter", 0x1c, false)]
    [InlineData("!", 0x02, true)]
    [InlineData("space", 0x39, false)]
    [InlineData("numpad5", 0x4c, false)]
    [InlineData("nosuchkey", 0, false)]
    public void ScriptKeyNamesFollowTheKeyTable(string name, int scan, bool shift) => Assert.Equal((scan, shift), ClientScript.Lookup(name));

    [Fact]
    public void TimedScriptRunsLinesInOrderAndWaitsForCondition()
    {
        Boot();
        var path = Path.Combine(_root, "run.txt");
        File.WriteAllText(path, "@0.5 eval $A = 1;\n@1.0 eval $B = 1;\nwaitfor $Gate 5\neval $C = 1;\n");
        // Load through a second app so the script object is built by the public path (--input).
        _app!.Dispose();
        _app = null;
        File.WriteAllText(Path.Combine(_root, "console.cs"), "newClient(); focusClient(); newObject(MainWindow, SimGui::Canvas, \"T\", 640, 480, True, \"512 384\", \"1024 768\");");
        var opt = ClientOptions.Parse(["--root", _root, "--headless", "--input", path], out _)!;
        _app = new ClientApp(opt);
        Assert.True(_app.Start(out _));
        for (var i = 0; i < 40; ++i)
            _app.Step();   // 16 ms of virtual time each: 0.64 s
        Assert.Equal("1", _app.Interp.PeekVariable("$A"));
        Assert.Equal("", _app.Interp.PeekVariable("$B"));
        for (var i = 0; i < 40; ++i)
            _app.Step();   // 1.28 s
        Assert.Equal("1", _app.Interp.PeekVariable("$B"));
        Assert.Equal("", _app.Interp.PeekVariable("$C"));      // held back by waitfor
        _app.Interp.SetVariable("$Gate", "1");
        for (var i = 0; i < 5; ++i)
            _app.Step();
        Assert.Equal("1", _app.Interp.PeekVariable("$C"));
    }

    // ---- command line (client-app.md 1.2) ----

    [Fact]
    public void OwnOptionsAreRemovedAndGameTokensGoToCargv()
    {
        var o = ClientOptions.Parse(["--root", "/x", "-mod", "m", "+connect", "127.0.0.1:1", "--no-audio", "--screenshot-at", "3,1.5", "+exec", "a.cs"], out var e)!;
        Assert.Equal("", e);
        Assert.Equal("/x", o.Root);
        Assert.True(o.NoAudio);
        Assert.Equal(new[] { 1.5, 3.0 }, o.ScreenshotAt);
        Assert.Equal(new[] { "-mod", "m", "+connect", "127.0.0.1:1", "+exec", "a.cs" }, o.GameArgs);
    }

    [Fact]
    public void BadOptionsAreRejected()
    {
        Assert.Null(ClientOptions.Parse(["--bogus"], out var e));
        Assert.Contains("--bogus", e);
        Assert.Null(ClientOptions.Parse(["--size", "640"], out e));
        Assert.Null(ClientOptions.Parse(["--screenshot-at"], out e));
    }

    [Fact]
    public void ToolFlagsKeepTheEarlierStagesRunnable()
    {
        Assert.True(ClientOptions.IsToolInvocation(["--root", "x", "--gui", "MainMenu"]));
        Assert.True(ClientOptions.IsToolInvocation(["--demo", "a.rec", "--at", "3"]));
        Assert.False(ClientOptions.IsToolInvocation(["--root", "x", "+connect", "127.0.0.1:28001"]));
    }
}
