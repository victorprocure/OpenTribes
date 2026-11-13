// Port of tests/test_backlog.cpp: backlog commands (docs/script.md 6.8): WaypointToWorld, the commander commands and
// their events, voice and sound events, item masks, terrain and object file commands. Expected values come from the
// reference server (tests/ref/backlog*.txt, tools/cmddiff.py). Check-style like the C++ test: every failed check is
// printed and counted, one assertion at the end.
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Game;
using OpenTribes.Script;
using OpenTribes.Sim;
using OpenTribes.World;
using static OpenTribes.Tests.Fixtures;

public class BacklogTests
{
    private static int sFailures;

    private static void Expect(bool ok, string what)
    {
        if (!ok)
        {
            Console.Error.WriteLine("FAIL " + what);
            ++sFailures;
        }
    }

    private static void ExpectEq(string got, string want, string what)
    {
        if (got != want)
        {
            Console.Error.WriteLine("FAIL " + what + "\n  want: " + want + "\n  got:  " + got);
            ++sFailures;
        }
    }

    private sealed record Sent(int To, OutEvent E);

    // Reads a PlayerCommandEvent body as the client does (docs/game-stream.md 2.5).
    private static string DescribeCommand(OutEvent e)
    {
        var b = new Bits(e);
        var client = (int)b.U(10) + 2048;
        var status = b.U(2);
        var ack = b.Flag();
        var s = "client=" + client + " status=" + status;
        if (ack)
        {
            s += " ack";
        }
        else if (b.Flag())
        {
            var issuer = (int)b.U(10) + 2048;
            var icon = b.U(6);
            var target = b.Flag() ? (int)b.U(8) : -1;
            uint x = b.U(10), y = b.U(10);
            s += " issuer=" + issuer + " icon=" + icon + " target=" + target + " x=" + x + " y=" + y;
        }
        if (b.Flag())
            s += b.Flag() ? " text='" + b.Str() + "'" : " text=cached";
        Expect(b.Done(), "command event fully read: " + s);
        return s;
    }

    private static List<byte> Zeros(int n, byte v = 0) => [.. Enumerable.Repeat(v, n)];

    private static void Helpers()
    {
        // WaypointToWorld on the Broadside mission area {-827, -717, 1000, 1000} (reference values).
        float[] area = [-827, -717, 1000, 1000];
        (float X, float Y, string Want)[] cases =
        [
            (1, 2, "-826.023 -715.046"), (0, 0, "-827 -717"), (512, 512, "-327 -217"),
            (1023, 1023, "172.023 282.023"), (1024, 0, "173 -717"), (-5, -5, "-831.882 -721.882"),
            (1.5f, 2.5f, "-825.535 -714.558"),
        ];
        foreach ((var x, var y, var want) in cases)
        {
            var o = new float[2];
            BacklogCommands.WaypointToWorld(area, x, y, o);
            ExpectEq(MathCommands.FormatF32(o[0]) + " " + MathCommands.FormatF32(o[1]), want, "waypointToWorld");
        }

        // Item type argument (0x004197a4): digits below the count, else a datablock name.
        static int Lookup(string n) => n == "Blaster" ? 7 : -1;
        Expect(BacklogCommands.ParseItemType("5", 44, Lookup) == 5, "type 5");
        Expect(BacklogCommands.ParseItemType("  12", 44, Lookup) == 12, "type with leading blanks");
        Expect(BacklogCommands.ParseItemType("44", 44, Lookup) == -1, "type at the count");
        Expect(BacklogCommands.ParseItemType("-3", 44, Lookup) == -1, "negative type is a name");
        Expect(BacklogCommands.ParseItemType("7abc", 44, Lookup) == 7, "digits then letters read atoi");
        Expect(BacklogCommands.ParseItemType("Blaster", 44, Lookup) == 7, "name");
        Expect(BacklogCommands.ParseItemType("", 44, Lookup) == -1, "empty");

        var mask = new uint[7];
        Expect(!BacklogCommands.ItemMaskTest(mask, 3) && !BacklogCommands.ItemMaskTest(mask, -1), "empty mask");
        mask[1] = 1u << 4; // bit 36
        Expect(BacklogCommands.ItemMaskTest(mask, 36) && !BacklogCommands.ItemMaskTest(mask, 35), "bit test");
        Expect(BacklogCommands.ItemMaskTest(mask, 200) && BacklogCommands.ItemMaskTest(mask, -1), "out of range type tests any bit");

        // Persistent stream heads (loadObject).
        Expect(ObjectFiles.ClassifyPersistentFile([.. Pers("TS::Shape", 8, Zeros(4))]) == ObjectFiles.PersistentFileKind.NotSimObject,
               "TS::Shape is not a SimObject");
        Expect(ObjectFiles.ClassifyPersistentFile([.. Pers("SimFoo", 1, Zeros(4))]) == ObjectFiles.PersistentFileKind.Invalid,
               "unknown class");
        Expect(ObjectFiles.ClassifyPersistentFile([(byte)'P', (byte)'E', (byte)'R']) == ObjectFiles.PersistentFileKind.Invalid,
               "short file");
        Expect(ObjectFiles.ClassifyPersistentFile([.. Zeros(40, (byte)'x')]) == ObjectFiles.PersistentFileKind.Invalid, "no PERS tag");
        byte[] dis = [(byte)'I', (byte)'T', (byte)'R', (byte)'s', 0, 0, 0, 0];
        Expect(ObjectFiles.ClassifyPersistentFile(dis) == ObjectFiles.PersistentFileKind.NotSimObject, "ITRs block stream");
        byte[] ppl = [(byte)'P', (byte)'L', (byte)'9', (byte)'8', 0, 0, 0, 0, 0, 0];
        Expect(ObjectFiles.ClassifyPersistentFile(ppl) == ObjectFiles.PersistentFileKind.Invalid, "palette files are not persistent streams");

        // Event bodies (reference captures, tools/cmddiff.py).
        {
            OutEvent e = CommandMap.MakeCommandEvent(2050, 2049, 1, false, 3, -1, 100, 200, true, true, "Attack the base");
            ExpectEq(DescribeCommand(e), "client=2050 status=1 issuer=2049 icon=3 target=-1 x=100 y=200 text='Attack the base'",
                     "command event with text");
            Expect(e.Tag == 0x455 && e.Guaranteed && e.Ordered, "command event is ordered");
            e = CommandMap.MakeCommandEvent(2050, 2049, 1, false, 3, 12, 1023, 0, true, false, "Attack the base");
            ExpectEq(DescribeCommand(e), "client=2050 status=1 issuer=2049 icon=3 target=12 x=1023 y=0 text=cached",
                     "command event with cached text");
            e = CommandMap.MakeCommandEvent(2050, -1, 0, true, -1, -1, 0, 0, true, true, "");
            ExpectEq(DescribeCommand(e), "client=2050 status=0 ack text=''", "ack event with an empty text");
            e = CommandMap.MakeCommandEvent(2050, 2050, 2, false, -1, -1, 0, 0, false, false, null);
            ExpectEq(DescribeCommand(e), "client=2050 status=2", "no command part when the icon is -1");
            e = CommandMap.MakeSelectCommanderEvent(2050, -1);
            var b = new Bits(e);
            Expect(e.Tag == 0x456 && b.U(7) == 2 && b.Flag() && b.U(7) == 127 && b.Done(), "commander -1 is written raw");
            e = CommandMap.MakeSelectCommanderEvent(2050, 0);
            var z = new Bits(e);
            Expect(z.U(7) == 2 && !z.Flag() && z.Done(), "commander 0 is absent");
        }
        {
            OutEvent e = CommandMap.MakeVoiceEvent(2049, "a_very_long_voice_name");
            var b = new Bits(e);
            Expect(e.Tag == 0x45f && e.Guaranteed && !e.Ordered, "voice event is guaranteed unordered");
            Expect(b.U(7) == 1 && b.Str() == "a_very_" && b.Done(), "voice name is cut to 7 characters");
            e = CommandMap.MakeSoundEvent(22, 1, 86);
            var s = new Bits(e);
            Expect(e.Tag == 0x460 && e.Guaranteed && !e.Ordered, "sound event is guaranteed unordered");
            Expect(s.U(10) == 22 && s.U(2) == 1 && s.U(8) == 87 && s.Done(), "sound event body");
            float[] zero = [0, 0, 0];
            e = CommandMap.MakeLocSoundEvent(86, zero, 0.0f, 300.0f);
            var l = new Bits(e);
            Expect(e.Tag == 0x45c && !e.Guaranteed, "loc sound event is unguaranteed");
            Expect(l.U(8) == 87 && !l.Flag() && l.Done(), "loc sound at distance 0 has no direction");
            float[] d = [0, -3, 0];
            e = CommandMap.MakeLocSoundEvent(86, d, 3.0f, 300.0f);
            var n = new Bits(e);
            Expect(n.U(8) == 87 && n.Flag(), "loc sound with direction");
            Expect(n.U(8) == 127 && n.U(8) == 0 && !n.Flag() && n.U(9) == 5 && n.Done(),
                   "direction (0,-1,0): x 127, y 0, z >= 0; distance 3/300 * 511 = 5");
        }
        {
            // Terrain parameter text must read back to the same float.
            foreach (var v in new[] { 1234.5f, 777.25f, 0.1f, 1e-8f, 3.0e30f, -4.9f })
            {
                var back = Fpu.Store(Numbers.BorlandAtof(TerrainCommands.PersistFloatText(v)));
                Expect(back == v, "persistFloatText " + TerrainCommands.PersistFloatText(v));
            }
        }
    }

    private static void Commands()
    {
        var root = Path.Combine(Path.GetTempPath(), "ot_test_backlog");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        PutFile(Path.Combine(root, "plain.txt"), "not a persistent object");
        PutFile(Path.Combine(root, "shape.dts"), Pers("TS::Shape", 8, Zeros(8)));
        var w = new Fixtures.World(root);
        var sent = new List<Sent>();
        w.Game.Players.Send = (id, e) => sent.Add(new Sent(id, e));
        w.In.SetVariable("$Server::teamName0", "Blood Eagle");
        w.In.SetVariable("$Server::teamSkin0", "beagle");
        w.In.SetVariable("$Server::teamName1", "Diamond Sword");
        w.In.SetVariable("$Server::teamSkin1", "dsword");
        w.Run("resetPlayerManager();");
        w.Game.AddTeam(0);
        w.Game.AddTeam(1);

        // ---- argument counts and failure texts (tests/ref/backlog.txt, identical on the reference) ----
        var c = new Cases();
        c.Check(w, "echo(issueCommand());", "False");
        c.Check(w, "echo(issueCommandI(1, 2, 3, \"x\", 4));", "False");
        c.Check(w, "echo(issueTargCommand(1, 2, 3, \"x\", 4, 5));", "False");
        c.Check(w, "echo(issueCommandI(2049, 2050, 3, \"Attack\", 10, 20));", "False");
        c.Check(w, "echo(setCommandStatus(2049, 0, \"x\"));", "False");
        c.Check(w, "echo(setCommandStatus(2049));", "False");
        c.Check(w, "echo(remoteSelectCommander(2049, 2050, 1));", "RemoteSelectCommander with 4 args.\nFalse");
        c.Check(w, "echo(remoteSelectCommander());", "RemoteSelectCommander with 1 args.\nFalse");
        c.Check(w, "echo(playSound(NoSuchSoundOt, \"0 0 0\"));", "False");
        c.Check(w, "echo(playVoice(2049, \"voice\"));", "True");
        c.Check(w, "echo(playVoice(2049));", "False");
        c.Check(w, "echo(GameBase::playSound(1, NoSuchSoundOt, 1));", "False");
        c.Check(w, "echo(Client::centerPrint(\"a\", 1));", "True");
        c.Check(w, "echo(Client::centerPrint(\"a\"));", "False");
        c.Check(w, "echo(Client::cmdObservePlayer(1));", "False");
        c.Check(w, "echo(Client::setItemShopping());", "wrong argcount to setItemShoppingList(client,<type,...>)\nFalse");
        c.Check(w, "echo(Client::clearItemShopping());", "wrong argcount to clearItemShoppingList(client,<type,...>)\nFalse");
        c.Check(w, "echo(Client::setItemBuying());", "wrong argcount to setItemBuyList(client,<type,...>)\nFalse");
        c.Check(w, "echo(Client::clearItemBuying());", "wrong argcount to clearItemBuyList(client,<type,...>)\nFalse");
        c.Check(w, "echo(Client::isItemShoppingOn(1));", "wrong argcount to isItemShoppingOn(client,type)\nFalse");
        c.Check(w, "echo(Client::isItemBuyingOn(1));", "wrong argcount to isItemBuyOn(client,type)\nFalse");
        c.Check(w, "echo(Client::isItemBuyingOn(2049, 1));", "False");
        c.Check(w, "echo(GameBase::getLOSInfo(1));", "wrong argcount to GameBase::getLOSInfo(object,range,<rotation>)\nFalse");
        c.Check(w, "echo(GameBase::getLOSInfo(nosuch, 10));", "False");
        c.Check(w, "echo(GameBase::getMuzzleTransform(nosuch));", "1 0 0 0 1 0 0 0 1 0 0 0");
        c.Check(w, "echo(Interior::toggleLight(1));", "wrong argcount to Interior::toggleLight(Interior, Light)\nFalse");
        c.Check(w, "echo(Interior::switchOnLight(1, a, b));", "wrong argcount to Interior::switchOnLight(Interior, Light)\nFalse");
        c.Check(w, "echo(Interior::setLightTime(1, a));", "wrong argcount to Interior::setLightTime(Interior, Light, Time)\nFalse");
        c.Check(w, "echo(Interior::setLightTime(1, a, 1));", "False");
        c.Check(w, "echo(WaypointToWorld());",
                "WaypointToWorld <waypoint coords>\nConverts waypoint coords (x,y in [0,1023]) to world coords.\n0 0 0");
        c.Check(w, "echo(saveTerrain(1, 2));", "saveTerrain: objectName volumeName\nFalse");
        c.Check(w, "echo(setTerrainVisibility(1, 2));", "setTerrainVisibility: objectName visibleDist hazeDist\nFalse");
        c.Check(w, "echo(setTerrainVisibility(nosuch, 2, 3));",
                "setTerrainVisibility: object not found or not a SimTerrain object\nFalse");
        c.Check(w, "echo(setTerrainDetail(nosuch, 2, 3));",
                "setTerrainDetail: object not found or not a SimTerrain object\nFalse");
        c.Check(w, "echo(setTerrainContainer(nosuch, 1, 2));", "setTerrainContainer NEEDS: objectName Gravity Drag Height\nFalse");
        c.Check(w, "echo(setTerrainContainer(nosuch, 1, 2, 3));",
                "setTerrainContainer: object not found or not a SimTerrain object\nFalse");
        c.Check(w, "echo(lightTerrain());", "lightTerrain: objectName [minx miny maxx maxy]\nFalse");
        c.Check(w, "echo(lightTerrain(nosuch));", "lightTerrain: object not found or not a SimTerrain object\nFalse");
        c.Check(w, "echo(lightTerrain(nosuch, 1, 2, 3));", "lightTerrain: objectName [minx miny maxx maxy]\nFalse");
        c.Check(w, "echo(newTerrain(a, b));", "False");
        c.Check(w, "echo(loadTerrain());", "False");
        c.Check(w, "echo(loadObject(x));", "loadObject: objectName fileName\n0");
        c.Check(w, "echo(loadObject(x, \"missing.bin\"));", "loadObject: Error reading file\n0");
        c.Check(w, "echo(loadObject(x, \"plain.txt\"));", "loadObject: Error reading file\n0");
        c.Check(w, "echo(storeObject(nosuch));", "storeObject: objectName fileName [userField]\nFalse");
        c.Check(w, "echo(storeObject(nosuch, \"x\"));", "storeObject: Could not find object\nFalse");
        c.Check(w, "echo(GuiToolbar());", "GuiToolbar: simCanvas\nFalse");
        c.Check(w, "echo(GuiToolbar(1));", "GuiToolbar: 1 is not a valid SimGui::Canvas\nFalse");
        c.Check(w, "echo(newSfx(1));", "newSfx\nFalse");
        c.Check(w, "echo(newInputManager());", "False");
        c.Check(w, "echo(ircConnect());", "ircConnect( ircServerAddress, {port} );\nFalse");
        c.Check(w, "echo(ircConnect(x));", "true");
        sFailures += c.Failures;
        // loadObject from the search path: a PERS file of a non-SimObject class.
        w.Res.SetSearchPath(".");
        ExpectEq(w.Run("echo(loadObject(x, \"" + Path.Combine(root, "shape.dts") + "\"));"),
                 "loadObject: Persistent object not a SimObject\n0", "loadObject of a shape file");

        // ---- mouse getters (reference values) ----
        c = new Cases();
        c.Check(w, "echo(Client::getMouseSensitivity());", "invalid call to Client::getMouseSensitivity(actionMap)\n");
        c.Check(w, "echo(Client::getMouseSensitivity(\"\"));", "1");
        c.Check(w, "echo(Client::getMouseSensitivity(\"SimGame.sae\"));", "-1");
        c.Check(w, "echo(Client::getMouseSensitivity(\"nonexist\"));",
                "error: must call 'editActionMap' or 'newActionMap' first. (nonexist)\n-1");
        c.Check(w, "echo(Client::getMouseXAxisFlip(\"nonexist\"));",
                "error: must call 'editActionMap' or 'newActionMap' first. (nonexist)\nFALSE");
        c.Check(w, "newActionMap(otm3);", "");
        c.Check(w, "echo(Client::getMouseSensitivity(\"otm3.sae\"), Client::getMouseXAxisFlip(\"otm3.sae\"));", "-1FALSE");
        c.Check(w, "bindAction(mouse0, xaxis0, TO, 700001, scale, 2);", "");
        c.Check(w, "echo(Client::getMouseSensitivity(\"otm3.sae\"), Client::getMouseXAxisFlip(\"otm3.sae\"));", "1TRUE");
        c.Check(w, "bindAction(mouse0, xaxis0, TO, 700001, flip, scale, 0.25);", "");
        c.Check(w, "echo(Client::getMouseSensitivity(\"otm3.sae\"), Client::getMouseXAxisFlip(\"otm3.sae\"));", "0.25FALSE");
        c.Check(w, "bindAction(mouse0, yaxis0, TO, 700002, scale, 0.5);", "");
        c.Check(w, "echo(Client::getMouseYAxisFlip(\"otm3.sae\"));", "TRUE");
        c.Check(w, "bind(keyboard0, make, a, TO, \"echo(2);\");", "");
        c.Check(w, "echo(Client::getMouseSensitivity(\"otm3.sae\"));", "0.25");
        sFailures += c.Failures;

        // ---- commander commands with two clients (tools/cmddiff.py: events identical to the reference) ----
        sent.Clear();
        w.Game.Players.AddClient(2049, "Alpha", "male1", true);
        w.Game.Players.AddClient(2050, "Beta", "male1", true);
        w.Run("Client::setDataFinished(2049); Client::setDataFinished(2050);");
        w.Run("Client::setInitialTeam(2049, 0); Client::setInitialTeam(2050, 0);");
        w.Run("Client::setGuiMode(2049, 2); Client::setGuiMode(2050, 2);");
        sent.Clear();
        List<string> Drain()
        {
            var o = new List<string>();
            foreach (Sent s in sent)
            {
                if (s.E.Tag == 0x455)
                {
                    o.Add(s.To + " " + DescribeCommand(s.E));
                }
                else if (s.E.Tag == 0x456)
                {
                    var b = new Bits(s.E);
                    var cl = b.U(7) + 2048;
                    var cm = b.Flag() ? (int)b.U(7) + 2048 : 0;
                    o.Add(s.To + " select " + cl + " " + cm);
                }
            }
            sent.Clear();
            return o;
        }
        static string Join(List<string> v)
        {
            var r = "";
            foreach (var s in v)
                r += (r.Length == 0 ? "" : "\n") + s;
            return r;
        }
        w.Run("issueCommandI(2049, 2050, 3, \"Attack the base\", 100, 200);");
        // Both clients are in the command map: each gets the event, the text goes to the issuer and
        // the commanded client only (they are both here).
        ExpectEq(Join(Drain()),
                 "2050 client=2050 status=1 issuer=2049 icon=3 target=-1 x=100 y=200 text='Attack the base'\n" +
                 "2049 client=2050 status=1 issuer=2049 icon=3 target=-1 x=100 y=200 text='Attack the base'",
                 "issueCommandI events");
        w.Run("issueCommandI(2049, 2050, 3, \"attack the base\", 100, 200);");
        ExpectEq(Join(Drain()),
                 "2050 client=2050 status=1 issuer=2049 icon=3 target=-1 x=100 y=200 text='attack the base'\n" +
                 "2049 client=2050 status=1 issuer=2049 icon=3 target=-1 x=100 y=200 text='attack the base'",
                 "a text that differs in case is a new text");
        w.Run("issueCommandI(2049, 2050, 4, \"attack the base\", 1023, 0);");
        ExpectEq(Join(Drain()),
                 "2050 client=2050 status=1 issuer=2049 icon=4 target=-1 x=1023 y=0 text=cached\n" +
                 "2049 client=2050 status=1 issuer=2049 icon=4 target=-1 x=1023 y=0 text=cached",
                 "the same text is not sent twice");
        w.Run("issueCommand(2049, 2049, 5, \"Self cmd\", -500.5, -300.25);");
        Drain();
        ExpectEq(w.Run("echo(setCommandStatus(2050, 0, \"Done\"));"), "True", "setCommandStatus applies");
        ExpectEq(Join(Drain()), "2050 client=2050 status=0 ack text='Done'\n2049 client=2050 status=0 ack text='Done'",
                 "ack events: the text goes to the commanded client and to the issuer of the command");
        ExpectEq(w.Run("echo(setCommandStatus(2050, 0, \"Again\"));"), "False", "no second change");
        Drain();
        ExpectEq(w.Run("echo(remoteSelectCommander(2050, 2049));"), "False", "plugin commands return False");
        ExpectEq(Join(Drain()), "2050 select 2050 2049\n2049 select 2050 2049", "commander selected");
        w.Run("remoteSelectCommander(2049, 2050);");
        ExpectEq(Join(Drain()), "", "a cycle is refused");
        w.Run("remoteSelectCommander(2050, -1);");
        ExpectEq(Join(Drain()), "2050 select 2050 2175\n2049 select 2050 2175", "commander -1 is written raw");
        w.Run("Client::setInitialTeam(2050, 1);");
        w.Run("issueCommandI(2049, 2050, 3, \"Cross team\", 1, 2);");
        ExpectEq(Join(Drain()), "", "no command across teams");
        w.Run("Client::setInitialTeam(2050, 0);");

        // ---- item masks ----
        w.Run("ItemData ItemOtA { showInventory = true; }; ItemData ItemOtB { showInventory = false; };");
        ExpectEq(w.Run("echo(Client::setItemShopping(2049, ItemOtA, ItemOtB));"), "True", "setItemShopping");
        ExpectEq(w.Run("echo(Client::isItemShoppingOn(2049, ItemOtA), Client::isItemShoppingOn(2049, ItemOtB));"),
                 "TrueFalse", "only ItemData with showInventory set a bit");
        ExpectEq(w.Run("echo(Client::setItemBuying(2049, ItemOtA));"), "True", "setItemBuying");
        ExpectEq(w.Run("echo(Client::isItemBuyingOn(2049, ItemOtA));"), "True", "buying bit");
        ExpectEq(w.Run("echo(Client::clearItemBuying(2049, ItemOtA));"), "True", "clearItemBuying");
        ExpectEq(w.Run("echo(Client::isItemBuyingOn(2049, ItemOtA), Client::isItemShoppingOn(2049, ItemOtA));"),
                 "FalseTrue", "clearItemBuying keeps the shopping bit");
        ExpectEq(w.Run("echo(Client::clearItemShopping(2049, ItemOtA));"), "True", "clearItemShopping");
        ExpectEq(w.Run("echo(Client::isItemShoppingOn(2049, ItemOtA));"), "False", "shopping bit cleared");
        ExpectEq(w.Run("echo(Client::setItemShopping(2049, 1));"), "True", "second item");
        ExpectEq(w.Run("echo(Client::isItemShoppingOn(2099, 0));"), "False", "unknown client");

        // ---- terrain commands with a real SimTerrain in the world are covered by cmddiff (ghost) ----
        Directory.Delete(root, true);
    }

    // docs/interiors.md 5.4: the four light commands change the run state of the instance, return False everywhere, and
    // leave the ghost light records alone.
    private static void InteriorLightsTest()
    {
        var root = Path.Combine(Path.GetTempPath(), "ot_test_backlog_lights");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        float[] box = [0, -1, -1, 10, 1, 1];
        PutFile(Path.Combine(root, "base/hut.dis"), DisFile("hut-00.dig", "hut-000.dil"));
        PutFile(Path.Combine(root, "base/hut-00.dig"), DigFile(0x1234, box, new[] { new Fixtures.Node(0, -44, -45) }, 1, 45, new[] { new float[] { 1, 0, 0, 0 } }));
        PutFile(Path.Combine(root, "base/hut-000.dil"),
                DilLightingFile(0x1234, new[] { new LightDef(7, 0, 2, 0, 1.0f, 1), new LightDef(9, 5, 3, 2, 2.0f, 0) },
                                new[] { new StateDef([0, 0, 0, 0], 0.0f), new StateDef([256, 0, 0, 0], 0.5f),
                                        new StateDef([0, 0, 0, 0], 0.0f), new StateDef([0, 0, 0, 0], 0.25f),
                                        new StateDef([0, 0, 0, 0], 0.5f) },
                                "lamp\0pulse\0"));
        PutFile(Path.Combine(root, "base/nodil.dis"), DisFile("nodil-00.dig", "nodil-000.dil"));
        PutFile(Path.Combine(root, "base/nodil-00.dig"), DigFile(0x1234, box, new[] { new Fixtures.Node(0, -44, -45) }, 1, 45, new[] { new float[] { 1, 0, 0, 0 } }));
        var w = new Fixtures.World(root);
        w.Run("instant SimGroup \"MissionGroup\" {" +
              " instant InteriorShape \"Hut\" { filename = \"hut.dis\"; };" +
              " instant InteriorShape \"Gone\" { filename = \"missing.dis\"; };" +
              " instant InteriorShape \"Bare\" { filename = \"nodil.dis\"; }; };");
        SimObject? hutObj = w.Object("MissionGroup/Hut");
        GameBase? hut = hutObj as GameBase;
        Expect(hut != null && hut.InteriorGeo != null && hut.InteriorNet.Lighting.Size() == 2, "interior with two lights");
        if (hut == null)
            return;
        InteriorLights lights = hut.InteriorNet.Lighting;
        int State(int id) => lights.StateOf(id);
        var answers = "";
        void Run(string line) => answers += w.Run("echo(" + line + ");") + "\n";
        const string hutName = "\"MissionGroup/Hut\"";
        // Toggle: 0 becomes 1, anything else 0. Switch on sets 1. Switch off also sets 1 (0x004222b0 passes 1).
        Run("Interior::toggleLight(" + hutName + ", lamp)");
        Expect(State(7) == 1, "toggle 0 -> 1");
        Run("Interior::toggleLight(" + hutName + ", lamp)");
        Expect(State(7) == 0, "toggle 1 -> 0");
        Run("Interior::switchOnLight(" + hutName + ", lamp)");
        Expect(State(7) == 1, "switch on");
        Run("Interior::switchOnLight(" + hutName + ", lamp)");
        Expect(State(7) == 1, "switch on twice");
        Run("Interior::toggleLight(" + hutName + ", lamp)");
        Run("Interior::switchOffLight(" + hutName + ", lamp)");
        Expect(State(7) == 1, "switch off sets state 1 like the original");
        Run("Interior::setLightTime(" + hutName + ", pulse, \"0.3\")");
        Expect(State(9) == 1 && lights.GetRun(1).Time == Fpu.Store(Numbers.BorlandAtof("0.3")), "time 0.3: state 1");
        Run("Interior::setLightTime(" + hutName + ", pulse, 9)");
        Expect(State(9) == 2 && lights.GetRun(1).Time == 2.0f, "time clamps to the duration");
        Run("Interior::setLightTime(" + hutName + ", pulse, -3)");
        Expect(State(9) == 0 && lights.GetRun(1).Time == 0.0f, "negative time clamps to 0");
        ExpectEq(answers, "False\nFalse\nFalse\nFalse\nFalse\nFalse\nFalse\nFalse\nFalse\n",
                 "every path returns False");
        // Unknown light, unknown object, a non-interior and an interior without an instance change nothing.
        answers = "";
        Run("Interior::toggleLight(" + hutName + ", nosuch)");
        Run("Interior::toggleLight(nosuch, lamp)");
        Run("Interior::toggleLight(\"MissionGroup\", lamp)");
        Run("Interior::toggleLight(\"MissionGroup/Gone\", lamp)");
        Run("Interior::switchOffLight(\"MissionGroup/Gone\", lamp)");
        Run("Interior::switchOnLight(\"MissionGroup/Gone\", lamp)");
        Run("Interior::setLightTime(\"MissionGroup/Gone\", lamp, 1)");
        Run("Interior::setLightTime(" + hutName + ", nosuch, 1)");
        Run("Interior::switchOnLight(\"MissionGroup/Bare\", lamp)");
        Expect(State(7) == 1 && State(9) == 0 && lights.GetRun(1).Time == 0.0f, "failed lookups change nothing");
        ExpectEq(answers, "False\nFalse\nFalse\nFalse\nFalse\nFalse\nFalse\nFalse\nFalse\n", "failed lookups return False");
        // The ghost light records keep the file values: no mask bit, no record change.
        Expect(hut.InteriorNet.Lights.Count == 2 && hut.InteriorNet.Lights[0].On && !hut.InteriorNet.Lights[1].On,
               "ghost light records unchanged");
    }

    [Fact]
    public void Main()
    {
        sFailures = 0;
        Numbers.EnterScriptFpuMode(); // round toward zero, as the script engine leaves it
        Helpers();
        Commands();
        InteriorLightsTest();
        if (sFailures != 0)
            Console.Error.WriteLine(sFailures + " failures");
        Assert.Equal(0, sFailures);
    }
}
