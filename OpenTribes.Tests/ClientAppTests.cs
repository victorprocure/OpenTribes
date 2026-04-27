// Tests of the `tribes` client (ROADMAP M9 C8): the command table with real effects (docs/client-scripts.md 2.2 to 2.15), the
// engine variables and prefs files (5.1, 5.4), the play delegate and the move builder (3.8, client-net.md 4.1, 4.2), the timed
// script and the command line. The client runs headless on a synthetic game directory (R5): no window, no game data.
using OpenTribes.Client.App;
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Script;

namespace OpenTribes.Tests;

public sealed class ClientAppTests : IDisposable
{
    private readonly string mRoot = Path.Combine(Path.GetTempPath(), "ot-c8-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> mLines = new();
    private ClientApp? mApp;

    public ClientAppTests()
    {
        Directory.CreateDirectory(Path.Combine(mRoot, "config"));
        Directory.CreateDirectory(Path.Combine(mRoot, "temp"));
    }

    public void Dispose()
    {
        mApp?.Dispose();
        try { Directory.Delete(mRoot, true); } catch (IOException) { }
    }

    private ClientApp Boot(string consoleCs = "", params string[] args)
    {
        File.WriteAllText(Path.Combine(mRoot, "console.cs"),
            "newClient(); focusClient();\n" +
            "newObject(MainWindow, SimGui::Canvas, \"Tribes\", 640, 480, True, \"512 384\", \"1024 768\");\n" +
            "function onConnection(%m) { $conn = %m; }\nfunction Game::EndFrame() {}\n" +
            "$ConsoleWorld::DefaultSearchPath = \"config;base;temp\";\n" + consoleCs);
        var all = new List<string> { "--root", mRoot, "--headless" };
        all.AddRange(args);
        ClientOptions opt = ClientOptions.Parse(all.ToArray(), out string err)!;
        Assert.Equal("", err);
        mApp = new ClientApp(opt);
        Assert.True(mApp.Start(out string error), error);
        mApp.Interp.SetOutput(l => mLines.Add(l));
        return mApp;
    }

    private string Eval(string text) => mApp!.Interp.Evaluate(text);

    // ---- quit ----

    [Fact]
    public void LoadingTheQuitScreenEndsTheProcess() // client-app.md 6.1: the exit dialog loads quit.gui, whose delegate quits when installed
    {
        Boot();
        Assert.False(mApp!.QuitRequested);
        mApp.Canvas.SetContent(new GuiControl { Name = "BlankQuitGui" }, mApp.Canvas.Delegates["quit"]);
        Assert.True(mApp.QuitRequested);
    }

    [Fact]
    public void OtherDelegatesDoNotQuit()
    {
        Boot();
        mApp!.Canvas.SetContent(new GuiControl { Name = "x" }, mApp.Canvas.Delegates["MainMenu"]);
        Assert.False(mApp.QuitRequested);
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
            Group = 3, Index = 2, Class = "Armor", Fields = new List<DecodedField> { new("maxEnergy", 60.0) },
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
        Assert.Contains("Out of bounds gamma, must be between 0.05 and 20, value clamped", mLines);
        Assert.Equal("20", mApp!.Interp.PeekVariable("$pref::Display::gammaValue"));
        Assert.Equal(20f, mApp.Gamma);
        mLines.Clear();
        Eval("setGamma(MainWindow, 1.5);");
        Assert.Empty(mLines);
        Assert.Equal("1.5", mApp.Interp.PeekVariable("$pref::Display::gammaValue"));
    }

    [Fact]
    public void WrongCanvasNamePrintsAndFails()
    {
        // 2.3: a wrong canvas name prints `Could not find simCanvas: <name>` and returns False.
        Boot();
        Assert.Equal("False", Eval("setGamma(NoWindow, 1);"));
        Assert.Contains("Could not find simCanvas: NoWindow", mLines);
    }

    [Fact]
    public void DevicesAreTheOneGlDeviceWithTheStockNames()
    {
        // D31: OpenGL is the device; Software and Glide are accepted names; D36 listDevices reports the one device.
        Boot();
        Assert.Equal("True", Eval("setFullscreenDevice(MainWindow, \"Software\");"));
        Assert.Equal("True", Eval("setWindowedDevice(MainWindow, \"Glide\");"));
        Assert.Equal("False", Eval("setFullscreenDevice(MainWindow, \"Direct3D\");"));
        Assert.Contains("Failed to set Fullscreen Device to Direct3D", mLines);
        Assert.Equal("", Eval("testDevice(OpenGL);"));
        Assert.Equal("False", Eval("testDevice(Voodoo);"));
        Assert.Equal("true", Eval("isGfxDriver(MainWindow, \"OpenGL\");"));
        Assert.Equal("False", Eval("isGfxDriver(MainWindow, \"Glide\");"));
        mLines.Clear();
        Eval("listDevices();");
        Assert.Equal("Number of devices: 1", mLines[0]);
        Assert.StartsWith("OpenGL", mLines[1]);
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
        Assert.Equal("Mode: 800 x 600", mLines[^1]);
        Eval("prevRes(MainWindow);");
        Assert.Equal("Mode: 640 x 480", mLines[^1]);
        Eval("prevRes(MainWindow);");
        Assert.Equal("Mode: 1600 x 1200", mLines[^1]);
    }

    [Fact]
    public void ScreenShotNeedsAWriteablePath()
    {
        // 2.3 screenShot and the writeable path rule (client-app.md 5.2).
        Boot();
        Assert.Equal("False", Eval("screenShot(MainWindow, \"base\\\\x.bmp\");"));
        Assert.Contains("Error creating file 'base\\x.bmp'", mLines);
        Assert.Equal("", Eval("screenShot(MainWindow, \"temp\\\\x.bmp\");"));
    }

    // ---- 2.2 shell, 2.5 redbook ----

    [Fact]
    public void ShellCommandsCheckTheirArgumentsAndOpenNothingWithoutTheOption()
    {
        // 2.2: prefix check; R6: the system opens nothing unless the user passed --allow-browser.
        Boot();
        Assert.Equal("False", Eval("HTMLOpen(\"www.example.com\");"));
        Assert.Contains("HTMLOpen: can only open properly prefixed HTML URLs.", mLines);
        Assert.Equal("True", Eval("HTMLOpen(\"http://127.0.0.1/\");"));
        Assert.Equal("False", Eval("Notepad(\"a.doc\");"));
        Assert.Contains("Notepad: can only open .txt files", mLines);
    }

    [Fact]
    public void RedbookIsAbsentLikeAMachineWithoutADrive()
    {
        // 2.5 and D36: newRedbook fails, every later call finds no object (the install's console.log line).
        Boot();
        Assert.Equal("False", Eval("newRedbook(CD, MainWindow);"));
        mLines.Clear();
        Eval("rbSetVolume(CD, 0.5);");
        Eval("rbPlay(CD, 2);");
        Assert.Equal(new[] { "unable to find Redbook Obj CD", "unable to find Redbook Obj CD" }, mLines);
    }

    // ---- 2.4 net and R6 ----

    [Fact]
    public void ConnectToARemoteAddressFailsLocally()
    {
        // R6: nothing but 127.0.0.0/8 is contacted; the join screen sees onConnection("Rejected") and the error text.
        Boot();
        Eval("connect(\"IP:203.0.113.9:28001\");");
        Assert.Equal("Rejected", mApp!.Interp.PeekVariable("$conn"));
        Assert.Contains("127.0.0.0/8", mApp.Interp.PeekVariable("$errorString"));
        Assert.Null(mApp.Game);
    }

    [Fact]
    public void MasterListStaysLocal()
    {
        // R6, client-scripts.md 4.1: DNet::resolveMasters resolves nothing that is not a loopback address.
        Boot();
        Assert.Equal("0", Eval("DNet::resolveMasters(\"IP:master1.example.org:28000\", \"IP:198.51.100.7:28000\");"));
        Assert.Equal("", Eval("DNet::getResolvedMaster(0);"));
        Assert.Equal("1", Eval("DNet::resolveMasters(\"IP:master1.example.org:28000\", \"IP:127.0.0.1:28000\");"));
        // 00422a74: a resolved master reads back as the address text, the others as nothing
        Assert.Equal("IP:127.0.0.1:28000", Eval("DNet::getResolvedMaster(0);"));
        Assert.Equal("", Eval("DNet::getResolvedMaster(1);"));
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
        Assert.Equal(local, mApp!.IsLocalAddress(address));
    }

    [Fact]
    public void DisconnectWithoutAGameSaysSo()
    {
        Boot();
        Eval("disconnect();");
        Assert.Contains("Disconnect: no CSDelegate.", mLines);
    }

    // ---- 2.6 and 2.7 input ----

    [Fact]
    public void DevicesActivateAndDeactivateByName()
    {
        // 2.6: inputActivate(keyboard0), an unknown device prints; a deactivated device delivers nothing.
        Boot("newActionMap(\"t.sae\"); bindCommand(mouse0, make, button0, TO, \"$Click = 1;\"); pushActionMap(\"t.sae\");");
        Assert.Equal("False", Eval("inputActivate(gamepad9);"));
        Assert.Contains("inputActivate: unknown device gamepad9", mLines);
        Eval("cursorOff(MainWindow);");   // the GUI takes the mouse while the cursor is on (client-app.md 3.8)
        Eval("inputDeactivate(mouse0);");
        mApp!.InjectButton(0, true);
        Assert.Equal("", mApp.Interp.PeekVariable("$Click"));
        mApp.InjectButton(0, false);
        Eval("inputActivate(mouse0);");
        mApp.InjectButton(0, true);
        Assert.Equal("1", mApp.Interp.PeekVariable("$Click"));
    }

    [Fact]
    public void PushAndPopActionMapCommandsAreRealAndNeedAMap()
    {
        // 2.6 and 3.8: pushActionMap puts the map on the stack, popActionMap takes it off; a map binding a command works.
        Boot("newActionMap(\"t.sae\"); bindCommand(keyboard, make, q, TO, \"$Q = $Q @ 1;\");");
        mApp!.InjectKey(0x10, true);
        mApp.InjectKey(0x10, false);
        Assert.Equal("", mApp.Interp.PeekVariable("$Q"));
        Assert.Equal("True", Eval("pushActionMap(\"t.sae\");"));
        Assert.Equal(1, mApp.Input.StackDepth);
        mApp.InjectKey(0x10, true);
        mApp.InjectKey(0x10, false);
        Assert.Equal("1", mApp.Interp.PeekVariable("$Q"));
        Eval("popActionMap(\"t.sae\");");
        Assert.Equal(0, mApp.Input.StackDepth);
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
    public void TypedVariablesReadBackInTheirFormat(int type, string input, string expected)
    {
        // client-scripts.md 5.1 types 0 string, 1 bool, 2 int, 3 float, 5 float clamped to 0..1.
        Assert.Equal(expected, EngineVariables.Normalize(type, input));
    }

    [Fact]
    public void EngineVariablesStartAtTheirImageDefaultAndNormalise()
    {
        Boot();
        Assert.Equal("50", mApp!.Interp.PeekVariable("$pref::NumDecals"));
        Assert.Equal("1.000000", mApp.Interp.PeekVariable("$pref::TerrainDetail"));
        Assert.Equal("False", mApp.Interp.PeekVariable("$pref::IgnoreTargets"));
        Eval("$pref::TerrainDetail = 3; $pref::NumDecals = \"12abc\"; $pref::IgnoreTargets = true;");
        Assert.Equal("1.000000", mApp.Interp.PeekVariable("$pref::TerrainDetail"));
        Assert.Equal("12", mApp.Interp.PeekVariable("$pref::NumDecals"));
        Assert.Equal("True", mApp.Interp.PeekVariable("$pref::IgnoreTargets"));
    }

    [Fact]
    public void PrefsSaveToConfigAndLoadBack()
    {
        // 5.4: export writes sorted `$name = "value";` lines to config\ClientPrefs.cs; exec of that file restores them.
        Boot();
        Eval("$pref::zeta = \"last\"; $pref::alpha = \"first\"; $pref::TerrainDetail = 0.5; export(\"pref::*\", \"config\\\\ClientPrefs.cs\", False);");
        string text = File.ReadAllText(Path.Combine(mRoot, "config", "ClientPrefs.cs"));
        string[] lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("$pref::alpha = \"first\";", lines);
        Assert.Contains("$pref::TerrainDetail = \"0.500000\";", lines);
        Assert.True(Array.IndexOf(lines, "$pref::alpha = \"first\";") < Array.IndexOf(lines, "$pref::zeta = \"last\";"));
        // A second client run reads the same file (client-scripts.md 6.1 step 11).
        mApp!.Dispose();
        mApp = null;
        Boot("exec(\"clientPrefs.cs\");");
        Assert.Equal("first", mApp!.Interp.PeekVariable("$pref::alpha"));
        Assert.Equal("0.500000", mApp.Interp.PeekVariable("$pref::TerrainDetail"));
    }

    [Fact]
    public void PrefsOutsideTheWriteablePathsAreRefused()
    {
        // client-app.md 5.2: only recordings, temp, config, base\missions, base\recordings and base\gui accept writes.
        Boot();
        Eval("export(\"pref::*\", \"scripts\\\\x.cs\", False);");
        Assert.False(File.Exists(Path.Combine(mRoot, "scripts", "x.cs")));
        Eval("export(\"pref::*\", \"temp\\\\x.cs\", False);");
        Assert.True(File.Exists(Path.Combine(mRoot, "temp", "x.cs")));
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
        string text = File.ReadAllText(Path.Combine(mRoot, "config", "config.cs"));
        Assert.StartsWith("NewActionMap(\"playMap.sae\");\n", text);
        Assert.Contains("bindAction(mouse0, xaxis0, TO, IDACTION_YAW, Flip, Scale, 0.00099", text);
        Assert.Contains("bindAction(keyboard0, make, \"w\", TO, IDACTION_MOVEFORWARD, 1.000000);", text);
        mApp!.Dispose();
        mApp = null;
        Boot("IDACTION_YAW = 00220004; IDACTION_MOVEFORWARD = 00220010; exec(\"config.cs\");");
        ActionMap m = mApp!.Maps.FindMap("playMap.sae")!;
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
        mApp!.Game = new LiveGame(mApp);
        var play = new GuiControl { Id = PlayDelegate.PlayGuiId };
        mApp.Canvas.SetContent(play);
        mApp.Play.UpdateContent(play.Id);
    }

    [Fact]
    public void PlayScreenPushesTheMapsAndTakesTheMouse()
    {
        // client-scripts.md 3.8: playGui pushes playMap then actionMap; leaving pops them; the play view owns the mouse.
        Boot("newActionMap(\"actionMap.sae\"); newActionMap(\"playMap.sae\"); newActionMap(\"pdaMap.sae\");");
        EnterPlay();
        Assert.Equal(2, mApp!.Input.StackDepth);
        Assert.False(mApp.Canvas.CursorOn);
        Assert.Equal("True", mApp.Interp.PeekVariable("$ConnectedToServer"));
        var menu = new GuiControl { Id = 100100 };
        mApp.Canvas.SetContent(menu);
        mApp.Play.UpdateContent(menu.Id);
        Assert.Equal(0, mApp.Input.StackDepth);
        Assert.True(mApp.Canvas.CursorOn);
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
        mApp!.InjectKey(0x11, true);
        Assert.Equal(1f, mApp.Play.Moves.Forward);
        mApp.InjectKey(0x11, false);
        Assert.Equal(0f, mApp.Play.Moves.Forward);
        mApp.InjectButton(1, true);
        Assert.True(mApp.Play.Moves.JetOn);
        mApp.InjectAxis(0, 100f);
        // Flip makes the value 1 - 100, scale 0.001, then * pi (scaled(v) with fov 90): a turn to the right.
        Assert.True(mApp.Play.Moves.TurnAcc < 0);
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
        Assert.Equal(90, b.CurrentFov);           // not zoomed: only the target changes (PlayerPSC_onActionDefault 0x35bd2)
        b.OnAction(MoveBuilder.SniperFov, 1f);
        Assert.Equal(22.5, b.CurrentFov);
        b.OnAction(MoveBuilder.IncSniperFov, 1f);
        Assert.Equal(11.25, b.CurrentFov);
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
    public void ScriptKeyNamesFollowTheKeyTable(string name, int scan, bool shift)
    {
        Assert.Equal((scan, shift), ClientScript.Lookup(name));
    }

    [Fact]
    public void TimedScriptRunsLinesInOrderAndWaitsForCondition()
    {
        Boot();
        string path = Path.Combine(mRoot, "run.txt");
        File.WriteAllText(path, "@0.5 eval $A = 1;\n@1.0 eval $B = 1;\nwaitfor $Gate 5\neval $C = 1;\n");
        // Load through a second app so the script object is built by the public path (--input).
        mApp!.Dispose();
        mApp = null;
        File.WriteAllText(Path.Combine(mRoot, "console.cs"), "newClient(); focusClient(); newObject(MainWindow, SimGui::Canvas, \"T\", 640, 480, True, \"512 384\", \"1024 768\");");
        var opt = ClientOptions.Parse(new[] { "--root", mRoot, "--headless", "--input", path }, out _)!;
        mApp = new ClientApp(opt);
        Assert.True(mApp.Start(out _));
        for (int i = 0; i < 40; ++i)
            mApp.Step();   // 16 ms of virtual time each: 0.64 s
        Assert.Equal("1", mApp.Interp.PeekVariable("$A"));
        Assert.Equal("", mApp.Interp.PeekVariable("$B"));
        for (int i = 0; i < 40; ++i)
            mApp.Step();   // 1.28 s
        Assert.Equal("1", mApp.Interp.PeekVariable("$B"));
        Assert.Equal("", mApp.Interp.PeekVariable("$C"));      // held back by waitfor
        mApp.Interp.SetVariable("$Gate", "1");
        for (int i = 0; i < 5; ++i)
            mApp.Step();
        Assert.Equal("1", mApp.Interp.PeekVariable("$C"));
    }

    // ---- command line (client-app.md 1.2) ----

    [Fact]
    public void OwnOptionsAreRemovedAndGameTokensGoToCargv()
    {
        ClientOptions o = ClientOptions.Parse(new[] { "--root", "/x", "-mod", "m", "+connect", "127.0.0.1:1", "--no-audio", "--screenshot-at", "3,1.5", "+exec", "a.cs" }, out string e)!;
        Assert.Equal("", e);
        Assert.Equal("/x", o.Root);
        Assert.True(o.NoAudio);
        Assert.Equal(new[] { 1.5, 3.0 }, o.ScreenshotAt);
        Assert.Equal(new[] { "-mod", "m", "+connect", "127.0.0.1:1", "+exec", "a.cs" }, o.GameArgs);
    }

    [Fact]
    public void BadOptionsAreRejected()
    {
        Assert.Null(ClientOptions.Parse(new[] { "--bogus" }, out string e));
        Assert.Contains("--bogus", e);
        Assert.Null(ClientOptions.Parse(new[] { "--size", "640" }, out e));
        Assert.Null(ClientOptions.Parse(new[] { "--screenshot-at" }, out e));
    }

    [Fact]
    public void ToolFlagsKeepTheEarlierStagesRunnable()
    {
        Assert.True(ClientOptions.IsToolInvocation(new[] { "--root", "x", "--gui", "MainMenu" }));
        Assert.True(ClientOptions.IsToolInvocation(new[] { "--demo", "a.rec", "--at", "3" }));
        Assert.False(ClientOptions.IsToolInvocation(new[] { "--root", "x", "+connect", "127.0.0.1:28001" }));
    }
}
