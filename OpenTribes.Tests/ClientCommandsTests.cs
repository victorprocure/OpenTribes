// Port of tests/test_client_commands.cpp: client-side commands on a dedicated server (docs/mods.md 5). Synthetic fixtures
// only: a temporary game root with a temp directory. 114 asserts.
namespace OpenTribes.Tests;

using System.Text;
using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Script;
using OpenTribes.Sim;

public class ClientCommandsTests
{
    private static string Slurp(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));

    private static bool Ev(ref InputEvent ev, params string[] words) => ClientCommands.ParseInputEvent([.. words], ref ev);

    private static bool Target(ActionBinding b, params string[] words) => ClientCommands.ParseActionTarget([.. words], b);

    [Fact]
    public void Main()
    {
        var root = TestPaths.TmpDir("client_cmd_root");
        Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "temp"));

        var strings = new StringTable();
        var inp = new Interpreter(strings);
        var output = "";
        inp.SetOutput(l => output += (output.Length == 0 ? "" : "\n") + l);
        var res = new ResourceManager(root, strings);
        inp.SetResources(res);
        Builtins.RegisterBuiltins(inp, res, root);
        res.SetSearchPath("temp");
        string Run(string s)
        {
            output = "";
            inp.Evaluate(s);
            return output;
        }

        // ---- 2: table-driven commands (argument count selects usage, "not found" or result) ----
        // GUI plugin: the canvas lookup comes first, then the per-command usage.
        Assert.Equal("cursorOn: MainWindow is not a valid SimGui::Canvas\nFalse", Run("echo(cursorOn(MainWindow));"));
        Assert.Equal("cursorOn: canvasName", Run("cursorOn();"));
        Assert.Equal("GuiLoadContentCtrl: MainWindow is not a valid SimGui::Canvas", Run("GuiLoadContentCtrl(MainWindow, \"gui\\\\empty.gui\");"));
        Assert.Equal("GuiLoadContentCtrl: simCanvas filename", Run("GuiLoadContentCtrl();"));
        Assert.Equal("GuiSaveSelection: must be in edit mode", Run("GuiSaveSelection();"));
        Assert.Equal("setCursor: MainWindow is not a valid SimGui::Canvas", Run("setCursor(MainWindow, \"Cur_Arrow.bmp\");"));
        // Control::* with a control argument crash the original (D11): silent "False" here.
        Assert.Equal("False", Run("echo(Control::setValue(a, b));"));
        Assert.Equal("Control::setValue(control, value);", Run("Control::setValue(a);"));
        Assert.Equal("Control::getPosition(control);\n<>", Run("echo(\"<\", Control::getPosition(), \">\");"));
        Assert.Equal("Control::getValue(control);\nControl::getValue(control, value);", Run("Control::getValue();"));
        // Display plugin.
        Assert.Equal("renderCanvas: could not find specified canvas\nFalse", Run("echo(renderCanvas(MainWindow));"));
        Assert.Equal("renderCanvas: could not find specified canvas\nFalse", Run("echo(renderCanvas());"));  // [live]
        Assert.Equal("swapSurfaces: simCanvasName", Run("isFullscreenMode();"));  // the original prints this name
        Assert.Equal("Could not find simCanvas: MainWindow", Run("setFullscreenDevice(MainWindow, Glide);"));
        Assert.Equal("Number of devices: 0\nFalse", Run("echo(listDevices());"));
        Assert.Equal("Incorrect syntax for isGfxDriver(CanvasName, DriverName)", Run("isGfxDriver(a);"));
        Assert.Equal("FalseFalse", Run("echo(screenShot(MainWindow), setScreenShotSeq(4));"));
        Assert.Equal("messageCanvasDevice: simCanvasName string", Run("messageCanvasDevice(MainWindow, outline);"));
        // Shell plugin.
        Assert.Equal("Notepad(<filename>);\nTrue", Run("echo(Notepad());"));
        Assert.Equal("Notepad: can only open .txt files\nFalse", Run("echo(Notepad(readme));"));
        Assert.Equal("HTMLOpen: can only open properly prefixed HTML URLs.", Run("HTMLOpen(page);"));
        // Sound and CD audio: no device objects.
        Assert.Equal("unable to find Sound object", Run("sfxPlay(3);"));
        Assert.Equal("Error: newSfx first.", Run("sfxOpen();"));
        Assert.Equal("sfxSetPosition id x y z", Run("sfxSetPosition(1);"));
        Assert.Equal("FalseFalse", Run("echo(sfxAddPair(1, 2, \"a.wav\"), sfxRemoveProfile(1));"));
        Assert.Equal("rbStop <obj Id>", Run("rbStop(CD, 1);"));
        Assert.Equal("unable to find Redbook Obj CD", Run("rbSetVolume(CD, 0.5);"));
        Assert.Equal("invalid canvas: MainWindow", Run("newRedbook(CD, MainWindow);"));
        // Input devices: no input manager, so right argument counts fail silently.
        Assert.Equal("FalseFalseFalse", Run("echo(inputActivate(keyboard0), inputDeactivate(mouse0), listInputDevices());"));
        Assert.Equal("inputActivate {[all/joystick/mouse/keyboard/device][0-n]}", Run("inputActivate();"));
        Assert.Equal("inputCapture", Run("inputCapture(x);"));
        // Tool windows, sim tree, IRC, editors.
        Assert.Equal("hideToolWin: Window name not found", Run("hideToolWin(w);"));
        Assert.Equal("hideToolWin: WindowName x y", Run("setToolWinPos();"));
        Assert.Equal("confirmBox: must set main main window first!", Run("confirmBox(a, b);"));
        Assert.Equal("browseBoxtitle question conVar::Result filterName filter [filterName filter...]", Run("browseBox();"));
        Assert.Equal("simTreeCreate: can't find parent", Run("simTreeCreate(t, p);"));
        Assert.Equal("truetrue", Run("echo(ircNick(me), ircEcho(all));"));
        Assert.Equal("ircNick( nickname );\nFalse", Run("echo(ircNick());"));
        Assert.Equal("IRC Channels:\n  Status\ntrue", Run("echo(ircListChannels());"));
        Assert.Equal("LS::Rules: must call LSCreate first", Run("LS::Rules();"));
        Assert.Equal("FalseFalse", Run("echo(ME::Save(x), Ted::save(x));"));  // crash in the original (D11)
        Assert.Equal("TED::ERRORcould not initialize TED.", Run("Ted::initTed();"));
        // Client-only game commands.
        Assert.Equal("Unable to locate the GAME server list control\nFALSE", Run("echo(addGameServer(a, b, c, d, e));"));
        Assert.Equal("addGameServer(address, name, version, ping, favorite <TRUE OR FALSE>);", Run("addGameServer();"));
        Assert.Equal("2048|0|TRUETRUE", Run("echo(getManagerId(), \"|\", setHudTimer(5), \"|\", Client::ExitLobbyMode(), resetPlayDelegate());"));
        Assert.Equal("Could not find manager\nfalse", Run("echo(GUI::isIn8BitMode());"));
        Assert.Equal("wrong argcount to getItemCount(description)\n-1|0", Run("echo(getMountedItem(0), \"|\", getItemCount());"));
        // GUI::issue8BitWarning and GUI::issueInternetWarning hang the original (modal box): unverified.
        Assert.Equal("usage: RegisterStaticTextures(<Canvas Name>)\nfalse", Run("echo(RegisterStaticTextures());"));
        Assert.Equal("TrueTrueFalse", Run("echo(flushExportText(), addExportText(a), addExportText());"));
        Assert.Equal("TrueTrue", Run("echo(dbgSetParams(), cls());"));
        Assert.Equal("Console::enable(<true/false>);", Run("Console::enable();"));
        Assert.Equal("True", Run("echo(Console::enable(true));"));
        // Every group is registered: nothing is an unknown command.
        foreach (var name in new[] {"swapSurfaces", "GuiEditMode", "TextList::AddLine", "windowsKeyboardDisable", "rbPlay",
                                 "sfxSetListenerPosition", "newToolWindow", "simTreeRegScript", "ircSetChannel",
                                 "LS::parseCommands", "ME::RebuildCommandMap", "Ted::undo", "editActionMap", "bind",
                                 "saveActionMap", "popActionMap", "HTMLOpenAndGoWin", "ShapeView::setSkin", "useItem" })
            Assert.True(inp.HasFunction(name));
        Assert.Equal(0, inp.UnknownCommands().Count);

        // ---- 3.1: event words ----
        var ev = new InputEvent();
        Assert.True(Ev(ref ev, "keyboard0", "make", "a") && ev.DeviceType == 3 && ev.DeviceInst == 0 && ev.Action == 1 &&
               ev.ObjType == 10 && ev.ObjInst == 30 && ev.Modifiers == 0);
        Assert.True(Ev(ref ev, "keyboard3", "break", "Q") && ev.DeviceInst == 3 && ev.Action == 2 && ev.ObjInst == 16);
        Assert.True(Ev(ref ev, "keyboard", "make", "control", "alt", "f1") && ev.Modifiers == 60 && ev.ObjInst == 59);
        Assert.True(Ev(ref ev, "keyboard0", "make", "l_shift", "r_alt", "numpad5") && ev.Modifiers == 33 && ev.ObjInst == 76);
        Assert.True(Ev(ref ev, "keyboard0", "make", "7") && ev.ObjInst == 8);  // first table match, not numpad7
        Assert.True(Ev(ref ev, "keyboard0", "make", "shift") && ev.ObjType == 10 && ev.ObjInst == 0 && ev.Modifiers == 3);
        Assert.False(Ev(ref ev, "keyboard0", "make", "shift", "control"));  // two modifiers alone
        Assert.False(Ev(ref ev, "mouse0", "make", "alt"));                   // modifier key needs the keyboard
        Assert.True(Ev(ref ev, "mouse0", "make", "button1") && ev.DeviceType == 2 && ev.ObjType == 2 && ev.ObjInst == 1);
        Assert.True(Ev(ref ev, "joystick1", "make", "button12") && ev.DeviceType == 4 && ev.DeviceInst == 1 && ev.ObjInst == 12);
        Assert.True(Ev(ref ev, "joystick0", "make", "upov0") && ev.ObjType == 6);
        Assert.True(Ev(ref ev, "mouse0", "xaxis0") && ev.Action == 3 && ev.ObjType == 11);
        Assert.True(Ev(ref ev, "joystick0", "slider2") && ev.ObjType == 17 && ev.ObjInst == 2);
        Assert.True(Ev(ref ev, "keyboard0", "make", "numpad+", "extra") && ev.ObjInst == 78);  // later words are ignored
        Assert.True(!Ev(ref ev) && !Ev(ref ev, "keyboard0") && !Ev(ref ev, "keyboard0", "make"));
        Assert.True(!Ev(ref ev, "bogus0", "make", "a") && !Ev(ref ev, "keyboard0", "make", "nokey"));
        Assert.False(Ev(ref ev, "keyboard0", "move", "a"));  // "move" is not consumed, so it is read as the axis word
        Assert.False(Ev(ref ev, "keyboard0", "alt"));

        // ---- 3.2: action words ----
        var b = new ActionBinding();
        Assert.True(Target(b, "730001") && b.Action == 730001 && b.Flags == 0 && b.ScaleF == 1.0f);
        Assert.True(Target(b, "5", "0.5", "Flip") && b.Flags == (ActionBinding.Value | ActionBinding.Flip) && b.ValueF == 0.5f);
        Assert.True(Target(b, "5", "flip", "SCALE", "2", "deadzone", "0.25") && b.ScaleF == 1.0f && b.DeadZoneF == 0.25f &&
               b.Flags == (ActionBinding.Flip | ActionBinding.Scale | ActionBinding.DeadZone));  // clamped to 0..1
        Assert.True(!Target(b) && !Target(b, "0") && !Target(b, "abc"));
        Assert.True(!Target(b, "5", "bogus") && !Target(b, "5", "Scale"));

        // ---- 3.3: commands and state ----
        Run("IDOTA_FIRE = 730001; IDOTA_YAW = 730002;");
        // No current map: the message carries the (empty) current name.
        Assert.Equal("bindAction error: must call 'editActionMap' or 'newActionMap' first. ()\nFalse", Run("echo(bindAction(keyboard0, make, a, TO, IDOTA_FIRE));"));
        Assert.Equal("bindAction error: must call 'editActionMap' or 'newActionMap' first. ()", Run("bindCommand(keyboard0, make, a, TO, x);"));
        Assert.True(Run("bindAction(a, b, c);") ==
               "bindAction keyboard[0-n] [make, break] {ctrl, alt, shift} {ascii, keyName}\n" +
               "bindAction joystick[0-n] [make, break] {ctrl, alt, shift} button[0-n]\n" +
               "bindAction joystick[0-n] move[x,y,z,r,u,v] \n" +
               "bindAction mouse[0-n] [make, break] {ctrl, alt, shift} button[0-n]\n" +
               "bindAction mouse[0-n] move[x,y,z,r,u,v] ");
        // The map is a generated resource: found by iteration, not a file.
        Assert.Equal("FalseFalse|otam1.sae", Run("echo(newActionMap(otam1), isFile(\"otam1.sae\"), \"|\", File::findFirst(\"otam1*\"));"));
        Assert.True(res.Find("OTAM1.SAE") != null && res.Find("SimGame.sae") != null);
        // g32 / resources.md 8.7: findNext after the end of a search (and before any findFirst) starts again.
        Assert.Equal("otam1.sae||otam1.sae", Run("echo(File::findFirst(\"otam1*\"), \"|\", File::findNext(\"otam1*\"), \"|\", File::findNext(\"otam1*\"));"));
        Assert.False(res.AddGenerated("otam1.sae"));  // already registered
        Run("bindCommand(keyboard0, make, a, TO, \"echo(1);\"); bindCommand(keyboard0, break, a, TO, \"echo(\\\"q\\\\\\\\\\\");\");");
        Run("bindCommand(keyboard0, make, a, TO, \"echo(3);\");");  // same event: replaced and moved to the end
        Run("bindCommand(keyboard0, make, shift, a, TO, \"s\"); bindCommand(keyboard, make, \"\\\\\", TO, \"bs\");");
        Run("bindCommand(mouse0, break, button1, TO, \"m1\"); bindCommand(keyboard0, make, \"space\", TO, \"sp\");");
        Run("bindAction(keyboard0, make, \"w\", TO, IDOTA_FIRE, 1.0); bindAction(mouse0, xaxis0, TO, IDOTA_YAW, Scale, 0.5);");
        Run("bindAction(mouse0, yaxis0, TO, IDOTA_YAW, Flip, Scale, 2, DeadZone, 0.25, Center, Square);");
        Run("bindAction(joystick0, rxaxis1, TO, IDOTA_YAW, None); bindAction(keyboard0, make, \"e\", TO, IDOTA_FIRE, 0.5, Flip);");
        Run("bindCommand(keyboard0, make, shift, TO, \"x\");");
        Assert.True(Run("bindAction(keyboard0, make, \"w\", TO, IDOTA_FIRE, bogus);") ==
               "bindAction event TO action - error: malformed 'TO section'.\n" +
               "-->bindAction bindAction keyboard0 make w TO 730001 bogus ");
        Assert.True(Run("bindAction(keyboard0, make, \"nokey\", TO, IDOTA_FIRE);") ==
               "bindAction event TO action - error: malformed 'Device section'.\n" +
               "-->bindAction bindAction keyboard0 make nokey TO 730001 ");
        Assert.True(Run("bindAction(keyboard0, make, \"w\", IDOTA_FIRE, 1);") ==  // no TO: the event takes all words
               "bindAction event TO action - error: malformed 'TO section'.\n" +
               "-->bindAction bindAction keyboard0 make w 730001 1 ");
        Assert.Equal("bindCommand event TO command - error: malformed 'Device section'.", Run("bindCommand(bogus0, make, a, TO, \"x\");"));
        Assert.Equal("Unknown map 'nomap.sae'\nFalse", Run("echo(saveActionMap(\"temp\\\\otam1\", otam1, nomap));"));
        Assert.True(Slurp(root + "/temp/otam1.cs") ==
               "NewActionMap(\"otam1.sae\");\n" +
               "bindCommand(keyboard0, break, \"a\", TO, \"echo(\\\"q\\\\\\\\\\\");\");\r\n" +
               "bindCommand(keyboard0, make, \"a\", TO, \"echo(3);\");\r\n" +
               "bindCommand(keyboard0, make, shift, \"a\", TO, \"s\");\r\n" +
               "bindCommand(keyboard0, make, \"\\\\\", TO, \"bs\");\r\n" +
               "bindCommand(mouse0, break, button1, TO, \"m1\");\r\n" +
               "bindCommand(keyboard0, make, \"space\", TO, \"sp\");\r\n" +
               "bindAction(keyboard0, make, \"w\", TO, IDOTA_FIRE, 1.000000);\r\n" +
               "bindAction(mouse0, xaxis0, TO, IDOTA_YAW, Scale, 0.500000);\r\n" +
               "bindAction(mouse0, yaxis0, TO, IDOTA_YAW, Flip, Center, Square, DeadZone, 0.250000, Scale, 1.000000);\r\n" +
               "bindAction(joystick0, rxaxis1, TO, IDOTA_YAW, None);\r\n" +
               "bindAction(keyboard0, make, \"e\", TO, IDOTA_FIRE, 0.500000);\r\n" +
               "bindCommand(keyboard0, make, shift, TO, \"x\");\r\n");
        Assert.Equal("saveactionMap filename.cs mapname1 {mapname2 ...}", Run("saveActionMap(x);"));
        Assert.Equal("Error creating file 'notwriteable.cs'", Run("saveActionMap(\"notwriteable.cs\", otam1);"));  // D11
        // editActionMap keeps bindings, newActionMap clears them; a wrong count unsets the current map.
        Run("newActionMap(otam2); bindCommand(keyboard0, make, b, TO, b1); editActionMap(otam1); editActionMap(otam2);");
        Run("bindCommand(keyboard0, make, c, TO, c1); saveActionMap(\"temp\\\\otam2.cs\", \"OTAM2.sae\");");
        Assert.True(Slurp(root + "/temp/otam2.cs") ==
               "NewActionMap(\"OTAM2.sae\");\nbindCommand(keyboard0, make, \"b\", TO, \"b1\");\r\n" +
               "bindCommand(keyboard0, make, \"c\", TO, \"c1\");\r\n");
        Run("newActionMap(otam2); saveActionMap(\"temp\\\\otam2.cs\", otam2);");
        Assert.Equal("NewActionMap(\"otam2.sae\");\n", Slurp(root + "/temp/otam2.cs"));
        Assert.Equal("bindAction error: must call 'editActionMap' or 'newActionMap' first. ()", Run("editActionMap(); bindCommand(keyboard0, make, c, TO, c1);"));
        // bind writes to SimGame.sae, which exists from the start.
        Assert.Equal("False", Run("echo(bind(keyboard0, make, \"f5\", TO, \"otbind\")); saveActionMap(\"temp\\\\sg.cs\", \"SimGame.sae\");"));
        Assert.Equal("NewActionMap(\"SimGame.sae\");\nbindCommand(keyboard0, make, \"f5\", TO, \"otbind\");\r\n", Slurp(root + "/temp/sg.cs"));
        Assert.Equal("bind event TO command - error: malformed 'Device section'.", Run("bind(keyboard0, make, zz, TO, x);"));
        Assert.Equal("TrueTrueFalseFalse", Run("echo(pushActionMap(\"otam1.sae\"), popActionMap(\"otam1.sae\"), pushActionMap(), popActionMap(a, b));"));

        // ---- 4: chat menus need the object model ----
        {
            var strings2 = new StringTable();
            var in2 = new Interpreter(strings2);
            var out2 = "";
            in2.SetOutput(l => out2 += (out2.Length == 0 ? "" : "\n") + l);
            Builtins.RegisterBuiltins(in2, null, root);
            var ctx = new ClassContext();
            var sim = new SimConsole(in2, ctx, strings2);
            sim.RegisterCommands();
            in2.SetObjects(sim);
            var game = new GameState
            {
                Sim = sim,
                Console = in2
            };
            ctx.Game = game;
            MissionClasses.Register();
            GameCommands.Register(in2, game);
            string Run2(string s)
            {
                out2 = "";
                in2.Evaluate(s);
                return out2;
            }
            Run2("newServer(); focusServer(); newObject(OTCM, ChatMenu, \"Head\"); newObject(OTSet, SimSet);");
            Assert.Equal("False", Run2("echo(addCMCommand(OTCM, \"aAlpha\\\\bBeta\", cmd1, x));"));
            Assert.Equal("Error adding menu to OTCM", Run2("addCMCommand(OTCM, \"aAlpha\\\\bBeta\", cmd1);"));  // key exists
            Assert.Equal("Error adding menu to OTCM", Run2("addCMCommand(OTCM, \"aAlpha\\\\\", c);"));           // empty item
            Assert.Equal("Error adding menu to OTCM", Run2("addCMCommand(OTCM, \"\", c);"));
            Assert.Equal("Error adding menu to OTCM", Run2("addCMCommand(OTCM, \"aAlpha\", c);"));
            Assert.Equal("Error adding menu to OTCM", Run2("addCMCommand(OTCM, \"x\", c);"));  // key without text
            Assert.Equal(0, Run2("addCMCommand(OTCM, \"cGamma\", c); addCMCommand(OTCM, \"cGamma\\\\dDelta\", c);").Length);
            Assert.Equal(0, Run2("addCMCommand(OTCM, \"aAlpha\\\\gGo\", c);").Length);
            Assert.Equal("FalseFalse", Run2("echo(setCMMode(OTCM, 1), setCMMode(OTCM, 7));"));
            Assert.Equal("setCMMode(menuObject, mode);", Run2("setCMMode(OTCM);"));
            Assert.Equal("setCMMode - invalid menu object.\naddCMCommand - invalid menu object.", Run2("setCMMode(OTSet, 1); addCMCommand(OTSet, \"aA\", c);"));

            var tree = new ChatMenuTree();
            Assert.True(tree.Add("aAlpha\\bBeta", new[] { "c1", "c2" }) && tree.Root.Children.Count == 1);
            var a = tree.Root.Children[0];
            Assert.True(a.Key == 'a' && a.Text == "Alpha" && !a.Leaf && a.Children.Count == 1);
            Assert.True(a.Children[0].Leaf && a.Children[0].Commands.Count == 2 && a.Children[0].Text == "Beta");
            Assert.True(tree.Add("z" + new string('y', 40), Enumerable.Repeat("w", 12).ToList()));
            Assert.True(tree.Root.Children[1].Text.Length == 31 && tree.Root.Children[1].Commands.Count == 10);
        }
    }
}
