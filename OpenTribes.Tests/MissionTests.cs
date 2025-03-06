// Port of tests/test_mission.cpp: Euler transforms (docs/mission.md 5.1), the script cases of its main (teams, GameBase
// registration, persist fields, the GameBase/Group/team commands) and the fixed ids of a new server.
using OpenTribes.Core;
using OpenTribes.Script;
using OpenTribes.Sim;

namespace OpenTribes.Tests;

public class MissionTests
{
    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    // 5.1 Euler to matrix. Expected words: the formula of 0x005ed42e evaluated exactly, each element truncated toward
    // zero to f32 (none lies within 2^-30 ulp of a float boundary).
    private static void EulerCase(float x, float y, float z, uint[] want)
    {
        float[] e = { x, y, z };
        var t = new Transform();
        t.Flags = 6; // bit 1 (translation) kept, bit 2 cleared
        MissionTransform.EulerToMatrix(e, ref t);
        Assert.True(t.Flags == 3);
        for (int i = 0; i < 9; ++i)
        {
            if (Bits(t.M[i]) != want[i])
                Console.Error.WriteLine($"euler {x} {y} {z} m{i}: {Bits(t.M[i]):x8} want {want[i]:x8}");
            Assert.True(Bits(t.M[i]) == want[i]);
        }
    }

    [Fact]
    public void TestEuler()
    {
        Fpu.TowardZero = false; // process start
        Numbers.EnterScriptFpuMode();
        // z only: cos 7, sin 7 (round to nearest would give 0x3f40ffbd for cos 7).
        uint[] z7 = { 0x3f40ffbc, 0x3f283046, 0, 0xbf283046, 0x3f40ffbc, 0, 0, 0, 0x3f800000 };
        EulerCase(0, 0, 7, z7);
        uint[] general = { 0x3f1f9181, 0x3f405050, 0x3e5e5406, 0xbf3d0bbb, 0x3ef2c515, 0x3ef57743, 0x3e83b0ce, 0xbeeb17b3, 0x3f59ad4d };
        EulerCase(0.5f, -0.25f, 1.0f, general);
        uint[] x16 = { 0x3f800000, 0, 0, 0, 0xbcef33e2, 0x3f7fe40e, 0, 0xbf7fe40e, 0xbcef33e2 };
        EulerCase(1.6f, 0, 0, x16);
        // x == 0 but y != 0 takes the general branch: m3 = -cx * sz = -0.
        uint[] y75 = { 0x3f3b4ff6, 0, 0xbf2e7fe0, 0x80000000, 0x3f800000, 0, 0x3f2e7fe0, 0, 0x3f3b4ff6 };
        EulerCase(0, 0.75f, 0, y75);

        // All zero (also -0): identity, flags bits 0 and 2 cleared.
        float[] zeros = { 0, -0.0f, -0.0f };
        var t = new Transform();
        t.Flags = 7;
        MissionTransform.EulerToMatrix(zeros, ref t);
        Assert.True(t.Flags == 2);
        for (int i = 0; i < 9; ++i)
            Assert.True(t.M[i] == (i % 4 == 0 ? 1.0f : 0.0f));

        // Matrix to Euler: flag bit 0 clear reads 0 0 0; m5 == 1 is the singular branch (x = asin(1) truncated:
        // 0x3fc90fda, y = 0, z = atan2(m1, m0)).
        float[] e = { 9, 9, 9 };
        var rot = new Transform();
        rot.Flags = 0;
        rot.M[5] = 1;
        MissionTransform.MatrixToEuler(rot, e);
        Assert.True(e[0] == 0 && e[1] == 0 && e[2] == 0);
        rot = new Transform();
        rot.Flags = 1;
        float[] m = { 1, 0, 0, 0, 0, 1, 0, -1, 0 };
        for (int i = 0; i < 9; ++i)
            rot.M[i] = m[i];
        MissionTransform.MatrixToEuler(rot, e);
        Assert.True(Bits(e[0]) == 0x3fc90fda && Bits(e[1]) == 0 && e[2] == 0);
    }

    // main after testEuler: the world, the script cases, then the fixed ids of the new server (sim.md 2.2).
    [Fact]
    public void TestMissionWorld()
    {
        Numbers.EnterScriptFpuMode();
        string root = Path.Combine(TestPaths.TmpDir(""), "mission_root");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "base"));
        var w = new Fixtures.World(root);
        var c = new Fixtures.Cases();

        // 2.1/2.2 teams: count -1 before the first reset, observer team "unnamed" with a ""
        // MSG line (TM4), names from $Server::teamName<c> only (TM3), escaped MSG text (TM2).
        c.Check(w, "echo(getNumTeams(), \"|\", getTeamName(-1));", "-1|0");
        c.Check(w, "$Server::teamName0 = \"Blood Eagle\"; $Server::teamName1 = \"Diamond \\\"Sword\\\"\"; resetPlayerManager();",
                "MSG: \"Server: Team \\\"\\\" added - id = -1\"");
        c.Check(w, "echo(getNumTeams(), \"|\", getTeamName(-1), \"|\", getTeamName(0), \"|\", getTeamName(), \"|\"," +
                   " getTeamName(-2));",
                "0|unnamed|0|0|0");

        // Datablocks and script callbacks for the GameBase tests (GB5 lookup).
        w.Run("StaticShapeData Pad { className = \"Station\"; visibleToSensor = true; };" +
              "StaticShapeData Plain { className = \"Station\"; }; MarkerData Mk { className = \"Mark\"; };" +
              "ItemData Flg { className = \"Flag\"; }; MoveableData Door { className = \"Door\"; };" +
              "function Station::onAdd(%this) { echo(\"Station::onAdd \", %this, \" \", GameBase::getTeam(%this)); }" +
              "function Pad::onSetTeam(%this, %old) { echo(\"Pad::onSetTeam \", %this, \" \", %old); }" +
              "function StaticShape::onInit(%this) { echo(\"StaticShape::onInit \", %this); }" +
              "function onAdd(%this) { echo(\"onAdd \", %this); }" +
              "function Flag::onRemove(%this) { echo(\"Flag::onRemove \", %this); }");

        // 1.2 / TM6 / GB1-GB7: registration order, team creation, callbacks, failures.
        c.Check(w,
                "instant SimGroup \"MissionGroup\" {" +
                "  instant StaticShape \"Top\" { dataBlock = \"Plain\"; };" +
                "  instant StaticShape \"Bad\" { dataBlock = \"Mk\"; };" +
                "  instant StaticShape \"Missing\" { dataBlock = \"Nope\"; };" +
                "  instant TeamGroup \"x\" { teamName = \"ignored\";" +
                "    instant StaticShape \"P1\" { dataBlock = \"Pad\"; name = \"<vServer::teamName0> pad\"; position = \"1 2 3\";" +
                "      rotation = \"0 0 7\"; bar = 5; };" +
                "    instant Marker \"M1\" { dataBlock = \"Mk\"; name = \"<vServer::teamName0> base\"; position = \"4 5 6\"; };" +
                "    instant InteriorShape \"I1\" { filename = \"none.dis\"; position = \"1 1 1\"; }; };" +
                "  instant TeamGroup \"y\" { instant Item \"F1\" { dataBlock = \"Flg\"; position = \"0.1 0 0\"; count = 3; };" +
                "    instant Moveable \"D1\" { dataBlock = \"Door\"; }; };" +
                "};",
                "StaticShape::onInit 8193\nStation::onAdd 8193 -1\n" +  // no setTeam: MissionGroup is unnamed (GB2)
                "Failed to registerObject of type: StaticShape (Bad)\n" +
                "Failed to registerObject of type: StaticShape (Missing)\n" +
                "MSG: \"Server: Team \\\"Blood Eagle\\\" added - id = 0\"\n" +
                "Pad::onSetTeam 8195 -1\nStaticShape::onInit 8195\nStation::onAdd 8195 0\n" +
                "onAdd 8196\n" +
                "MSG: \"Server: Team \\\"Diamond \\\"Sword\\\"\\\" added - id = 1\"\n" +
                "onAdd 8199\nonAdd 8200");
        // GS4 swap-remove of the two failed members, TM6 renaming in the parent group.
        c.Check(w, "listObjects(MissionGroup);",
                "   8193,\"Top\": StaticShape \n   8198,\"team1\": TeamGroup (g)\n   8194,\"team0\": TeamGroup (g)");
        c.Check(w, "echo(getNumTeams(), \"|\", getTeamName(0), \"|\", getTeamName(1), \"|\", getTeamName(2), \"|\"," +
                   " getTeamName(\"1abc\"), \"|\", isObject(\"MissionGroup/team0\"), isObject(\"MissionGroup/x\"));",
                "2|Blood Eagle|Diamond \"Sword\"|0|Diamond \"Sword\"|TrueFalse");
        // GB2/GB6 teams; GB4/GB7 map names: expanded only for visibleToSensor blocks, Marker
        // and InteriorShape are not ShapeBase ("").
        w.Run("$p = nameToID(\"MissionGroup/team0/P1\"); $m = nameToID(\"MissionGroup/team0/M1\");" +
              "$i = nameToID(\"MissionGroup/team0/I1\"); $f = nameToID(\"MissionGroup/team1/F1\");" +
              "$d = nameToID(\"MissionGroup/team1/D1\"); $t = nameToID(\"MissionGroup/Top\");");
        c.Check(w, "echo(GameBase::getTeam($t), GameBase::getTeam($p), GameBase::getTeam($m), GameBase::getTeam($i)," +
                   " GameBase::getTeam($f));",
                "-10-101");
        c.Check(w, "echo(GameBase::getMapName($p), \"|\", GameBase::getMapName($m), \"|\", GameBase::getMapName($i), \"|\"," +
                   " GameBase::getMapName($t), \"|\", GameBase::getMapName($f));",
                "Blood Eagle pad||||");
        c.Check(w, "echo(GameBase::getDataName($p), \"|\", GameBase::getDataName(MissionGroup), \"|\"," +
                   " GameBase::getDataName($i));",
                "Pad||False");
        // 3.3: persist fields are not readable, other slots are dynamic fields.
        c.Check(w, "echo($p.bar, \"|\", $p.position, \"|\", $p.dataBlock, \"|\", $p.name, \"|\", $f.count, \"|\"," +
                   " \"MissionGroup/team0\".teamName);",
                "5|||||ignored");
        // 3.1 sets: StaticShape and Item join 719, InteriorShape and Moveable 718.
        c.Check(w, "echo(isMember(719, $p), isMember(719, $f), isMember(718, $p), isMember(718, $i), isMember(718, $d)," +
                   " isMember(719, $d), isMember(719, $m));",
                "TrueTrueFalseTrueTrueFalseFalse");

        // 4.1 commands.
        c.Check(w, "echo(GameBase::getPosition($p), \"|\", GameBase::getRotation($p), \"|\", GameBase::getTransform($p), \"|\"," +
                   " GameBase::getPosition($m), \"|\", GameBase::getPosition($i));",
                "1 2 3|0 -0 0.716814|1 2 3 0 -0 0.716814|4 5 6|1 1 1");
        c.Check(w, "echo(GameBase::getTeam(), \"|\", GameBase::getTeam(MissionGroup), \"|\", GameBase::getTeam(2049));",
                "wrong argcount to getTream(object)\n-1|-1|-1");
        c.Check(w, "echo(GameBase::setTeam(), \"|\", GameBase::setTeam(MissionGroup, 1), \"|\", GameBase::setTeam($p, 9)," +
                   " \"|\", GameBase::getTeam($p));",
                "wrong argcount to setTeam(object,team)\nPad::onSetTeam 8195 0\n-1|-1|True|9");
        c.Check(w, "echo(GameBase::setTeam($m, 3), GameBase::getTeam($m));", "True3");
        c.Check(w, "echo(GameBase::getPosition(), \"|\", GameBase::getPosition(MissionGroup), \"|\"," +
                   " GameBase::getRotation(MissionGroup), \"|\", GameBase::getTransform(MissionGroup));",
                "wrong argcount to GameBase::getPosition(object)\n0 0 0|0 0 0|0 0 0|0 0 0 0 0 0");
        c.Check(w, "echo(GameBase::getRotation(), GameBase::getTransform());",
                "wrong argcount to GameBase::getRotation(object)\nwrong argcount to GameBase::getTransform(object)\n" +
                "0 0 00 0 0 0 0 0");
        c.Check(w, "echo(GameBase::setPosition($p, \"1 2\"), GameBase::getPosition($p), \"|\"," +
                   " GameBase::setPosition($p, \"1.1 2.2 3.3 4.4\"), GameBase::getPosition($p), \"|\"," +
                   " GameBase::setPosition($p, \"abc\"), GameBase::getPosition($p), \"|\"," +
                   " GameBase::setPosition(MissionGroup, \"1 1 1\"), GameBase::setPosition($p));",
                "wrong argcount to GameBase::setPosition(object,pos)\n" +
                "True1 2 0|True1.09999 2.19999 3.29999|True0 0 0|FalseFalse");
        // 5.1 [live] read-backs.
        c.Check(w, "GameBase::setRotation($p, \"0 0 -3.2\"); echo(GameBase::getRotation($p));" +
                   "GameBase::setRotation($p, \"1.6 0 0\"); echo(GameBase::getRotation($p));" +
                   "GameBase::setRotation($p, \"0 -0 2.64305\"); echo(GameBase::getRotation($p));" +
                   "GameBase::setRotation($p, \"0 0 0\"); echo(GameBase::getRotation($p));" +
                   "GameBase::setRotation($p, \"0 -0 -0\"); echo(GameBase::getRotation($p));" +
                   "echo(GameBase::setRotation(MissionGroup, \"1 1 1\"), GameBase::setRotation($p));",
                "0 -0 3.08318\n1.54159 -3.14159 -3.14159\n0 -0 2.64304\n0 0 0\n0 0 0\n" +
                "wrong argcount to GameBase::setRotation(object,rot)\nFalseFalse");
        c.Check(w, "echo(GameBase::setMapName($p, \"<vServer::teamName1>\"), GameBase::getMapName($p), \"|\"," +
                   " GameBase::setMapName($m, \"x\"), \"|\", GameBase::setMapName($p), \"|\", GameBase::getMapName());",
                "wrong argcount to GameBase::setMapName(object,name)\nwrong argcount to GameBase::getMapName(object)\n" +
                "TrueDiamond \"Sword\"|False|False|");
        // GB7 edge rules: '<v' without '>' is literal; a value that would pass 254 chars ends the copy.
        c.Check(w, "$Long = \"x\"; for (%i = 0; %i < 8; %i++) $Long = $Long @ $Long; GameBase::setMapName($p, \"ab<vLong>cd\");" +
                   "echo(GameBase::getMapName($p)); GameBase::setMapName($p, \"a<vServer::teamName0\");" +
                   "echo(GameBase::getMapName($p));",
                "ab\na<vServer::teamName0");
        c.Check(w, "echo(getObjectType($p), \"|\", getObjectType(MissionGroup), \"|\", getObjectType(\"MissionGroup/team0\")," +
                   " \"|\", getObjectType(nosuch), \"|\", getObjectType());",
                "getObjectType: could not find object\nwrong argcount to getObjectType(object);\n" +
                "StaticShape|SimGroup|TeamGroup|False|False");
        c.Check(w, "echo(Object::getName(MissionGroup), \"|\", Object::getName(718), \"|\", Object::getName(nosuch), \"|\"," +
                   " Object::getName(0), \"|\", Object::getName());",
                "MissionGroup|False||False|");
        c.Check(w, "echo(Item::getItemData($f), \"|\", Item::getItemData($p), \"|\", Item::getItemData());",
                "Item::getItemData: could not find item\nwrong argcount to Item::getItemData(item)\nFlg|-1|-1");
        // Group commands and 4.2 preorder.
        c.Check(w, "echo(Group::objectCount(MissionGroup), \"|\", Group::objectCount($p), \"|\"," +
                   " Group::getObject(MissionGroup, 1), \"|\", Group::getObject(MissionGroup, 3), \"|\"," +
                   " Group::getObject(MissionGroup, -1), \"|\", Group::getObject($p, 0), \"|\", Group::objectCount(719));",
                "3|0|8198|-1|-1|-1|3");
        c.Check(w, "function vis(%o, %a) { echo(\"vis \", %o, \" \", %a); }" +
                   "echo(Group::iterateRecursive(\"MissionGroup/team1\", vis, extra), \"|\"," +
                   " Group::iterateRecursive(nosuch, vis));",
                "vis 8199 extra\nvis 8200 extra\n0|0");
        c.Check(w, "echo(Group::iterateRecursive(MissionGroup, vis));",
                "vis 8193 \nvis 8198 \nvis 8199 \nvis 8200 \nvis 8194 \nvis 8195 \nvis 8196 \nvis 8197 \n0");
        // GameBase::virtual lookup order (datablocks.md 8, GB5).
        c.Check(w, "function Pad::ping(%this, %a) { return \"pad \" @ %this @ \" \" @ %a; }" +
                   "function Station::pong(%this) { return \"station\"; } function StaticShape::pang(%this) { return \"cls\"; }" +
                   "function peng(%this) { return \"plain\"; } function Diamond::peng(%this) { return \"no\"; }" +
                   "echo(GameBase::virtual($p, ping, 7), \"|\", GameBase::virtual($p, pong), \"|\"," +
                   " GameBase::virtual($p, PANG), \"|\", GameBase::virtual($p, peng), \"|\", GameBase::virtual($p, nofn)," +
                   " \"|\", GameBase::virtual(MissionGroup, peng), \"|\", GameBase::virtual($p));",
                "wrong argcount to GameBase::virtual(object,functionname,...)\npad 8195 7|station|cls|plain|||");
        c.Check(w, "GameBase::setMapName($p, \"Mapped\"); function Mapped::peng(%this) { return \"map\"; }" +
                   "echo(GameBase::virtual($p, peng));",
                "map");
        c.Check(w, "echo(removeFromSet(719, $p), isMember(719, $p), \"|\", removeFromSet($p, MissionGroup), \"|\"," +
                   " removeFromSet(719, nosuch), \"|\", removeFromSet(1));",
                "TrueFalse|False|True|True");
        c.Check(w, "deleteObject($f);", "Flag::onRemove 8199");

        // TM6: team ids follow registration; a ninth TeamGroup gets team 8 but no record.
        c.Check(w, "for (%i = 2; %i < 9; %i++) eval(\"instant SimGroup \\\"G\" @ %i @ \"\\\" { instant TeamGroup \\\"t\\\" {" +
                   " instant Marker \\\"m\\\" { position = \\\"0 0 0\\\"; }; }; };\");" +
                   "echo(getNumTeams(), \"|\", getTeamName(7), \"|\", getTeamName(8), \"|\", isObject(\"G8/team8\"));",
                "MSG: \"Server: Team \\\"\\\" added - id = 2\"\nMSG: \"Server: Team \\\"\\\" added - id = 3\"\n" +
                "MSG: \"Server: Team \\\"\\\" added - id = 4\"\nMSG: \"Server: Team \\\"\\\" added - id = 5\"\n" +
                "MSG: \"Server: Team \\\"\\\" added - id = 6\"\nMSG: \"Server: Team \\\"\\\" added - id = 7\"\n" +
                "8|unnamed|0|True");
        c.Check(w, "resetPlayerManager(); echo(getNumTeams());", "MSG: \"Server: Team \\\"\\\" added - id = -1\"\n0");

        // 3.5 / sim.md 2.2: constructor-fixed ids.
        c.Check(w, "instant SimGroup \"Env\" { instant Sky \"Sky\" { dmlName = \"a.dml\"; distance = 800; size = 300; };" +
                   " instant MissionCenterPos \"MC\" { x = 1; }; instant Snowfall \"Snow\" { intensity = 1; };" +
                   " instant Planet \"P\" { distance = 5; foo = 3; }; };" +
                   "echo(nameToID(\"Env/Sky\"), \"|\", nameToID(\"Env/MC\"), \"|\", nameToID(\"Env/Snow\"), \"|\"," +
                   " 729.distance, \"|\", 729.size, \"|\", \"Env/P\".distance, \"|\", \"Env/P\".foo);",
                "729|716|714|800|||3");
        c.Check(w, "echo(newObject(irc, IRCClient), \"|\", nameToID(ConsoleScheduler), \"|\", getObjectType(713), \"|\"," +
                   " getObjectType(721), \"|\", getObjectType(720), \"|\", getObjectType(42), \"|\", Object::getName(44));",
                "46|43|PlayerManager|DataBlockManager|SensorManager|SimPathManager|NamedGuiSet");
        foreach (int id in new[] { 5, 2, 25, 1, 3, 38, 36, 34, 7, 12, 9, 23, 13, 14, 6, 44, 45, 718, 719, 725, 727, 723 })
            Assert.True(w.Sim.Server()!.FindById(id) != null);

        Assert.True(c.Failures == 0);
    }
}
