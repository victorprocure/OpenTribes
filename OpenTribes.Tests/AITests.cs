// Port of tests/test_ai.cpp: AI players (ROADMAP G10, docs/ai-sim.md): commands and their texts (6), directive order rule
// (2.2), typed variables (5.1), the manager tick (1.6), steering (3.3), the trigger (3.6), callbacks (4) and the record
// bookkeeping (1.5, 7). Synthetic flat terrain, no game data. Expected texts and sequences are those the reference
// server printed (tools/aidiff.py). 52 asserts, one to one.
using System.Globalization;
using OpenTribes.Game;
using OpenTribes.Script;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;
using static OpenTribes.Tests.Fixtures;

namespace OpenTribes.Tests;

public class AITests
{
    private const float kFlat = 50.0f;

    private sealed class Fixture
    {
        public readonly string Root;
        public readonly Fixtures.World W;

        public Fixture(string name)
        {
            Root = Path.Combine(TestPaths.TmpDir(""), name);
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
            var t = new TerrainSpec(); // 2 x 2 blocks of 4 x 4 squares of 32 m: 256 m x 256 m, flat
            t.Scale = 5;
            t.Height = 2;
            t.Heights = Enumerable.Repeat(kFlat, 25).ToList();
            t.Squares = new List<byte>(new byte[32]);
            PutFile(Path.Combine(Root, "base/m.dtf"), DtfFile(t, kFlat, kFlat));
            PutFile(Path.Combine(Root, "base/m#0.dtb"), DtbFile(t, kFlat, kFlat));
            PutFile(Path.Combine(Root, "base/t.dml"), "dml");
            W = new Fixtures.World(Root);
            Numbers.EnterScriptFpuMode();
            W.Run("$Server::teamName0 = \"A\"; resetPlayerManager();");
            W.Run("PlayerData tarmor { shapeFile = \"none\"; maxForwardSpeed = 11; maxBackwardSpeed = 10;" +
                  " maxSideSpeed = 10; mass = 9; drag = 1; groundForce = 360; groundTraction = 3; jumpImpulse = 75;" +
                  " jumpSurfaceMinDot = 0.25; jetForce = 236; jetEnergyDrain = 0.75; minJetEnergy = 1;" +
                  " maxJetSideForceFactor = 0.75; maxJetForwardVelocity = 22; maxEnergy = 60; maxDamage = 0.5;" +
                  " boxWidth = 0.5; boxDepth = 0.5; boxNormalHeight = 2.25; boxCrouchHeight = 1.75; canCrouch = true;" +
                  " minDamageSpeed = 25; damageScale = 0.0078125; };");
            W.Run("instant SimGroup \"MissionGroup\" {" +
                  " instant SimTerrain \"Terrain\" { tedFileName = \"m.dtf\"; position = \"0 0 0\";" +
                  "   contGravity = \"0 0 -20\"; }; };" +
                  "instant SimGroup \"MissionCleanup\";");
        }

        // Exactly n world ticks (a 32 ms step of the virtual clock can cross zero or two boundaries).
        public void Tick(int n = 1)
        {
            for (int i = 0; i < n; ++i)
            {
                uint before = W.Sim.TickMs();
                while (W.Sim.TickMs() == before)
                    W.Sim.AdvanceRealTime(1);
            }
        }

        public AIObject? Ai(string name) => W.Game.Ai?.FindByName(name);

        public Player? Player(string name) => Ai(name)?.Player();
    }

    // The z rotation the script reads (from the transform, not the stored angle).
    private static string RotZ(Fixtures.World w, Player p)
    {
        string r = w.Run("echo(GameBase::getRotation(" + p.Id().ToString(CultureInfo.InvariantCulture) + "));");
        return r[(r.LastIndexOf(' ') + 1)..];
    }

    // ---- commands (6) ----

    private static void TestCommands()
    {
        var f = new Fixture("ai_commands");
        Fixtures.World w = f.W;
        var c = new Cases();
        // The first AI command creates the manager as object 726 in MissionCleanup (1.2).
        c.Check(w, "echo(isObject(726));", "False");
        c.Check(w, "echo(AI::GetCount());", "0");
        c.Check(w, "echo(isObject(726));", "True");
        Assert.True(w.Game.Ai != null);
        // Usage texts (the reference prints these lines for argc < the minimum).
        c.Check(w, "AI::GetId();",
                "AI::getId(<ai name>);\nReturns the rep Id of the given AI.  The rep Id can in many\n" +
                "cases be used like a client Id (in those console functions that\ntake a client Id).");
        c.Check(w, "AI::Spawn(a);",
                "AI::spawn: <AI name> <armor type> <position> [rotation] [display name] [voice]\n" +
                "Create an AI of the given name and armor type at position.\n" +
                "If not given, display name is the same as the AI name.\nVoice defaults to male1 if not supplied here.");
        c.Check(w, "echo(AI::Spawn(a, tarmor));",
                "AI::spawn: <AI name> <armor type> <position> [rotation] [display name] [voice]\n" +
                "Create an AI of the given name and armor type at position.\n" +
                "If not given, display name is the same as the AI name.\nVoice defaults to male1 if not supplied here.\n" +
                "False");
        c.Check(w, "AI::Delete();", "AI::Delete( <AI wildcard> )\nDelete any AI which matches the given wildcard specification.");
        c.Check(w, "AI::DirectiveTarget();", "AI::DirectiveTarget: <ai wildcard> <player ID> [ord]");
        c.Check(w, "AI::DirectiveRemove();", "AI::DirectiveRemove( <ai wildcard> <order # key> )");
        c.Check(w, "AI::SetAutomaticTargets();", "AI::SetAutomaticTargets( <ai wildcard> );");
        c.Check(w, "echo(BaseRep::getFirst(1));",
                "BaseRep::getFirst()\nLike Client::getFirst(), but AI 'reps' are included.\n-1");
        c.Check(w, "echo(BaseRep::getNext());",
                "BaseRep::getNext( <previous base rep id> );\nLike Client::getNext(), but AI 'reps' are included.\n-1");
        // Spawn and lookups.
        c.Check(w, "echo(AI::Spawn(A1, nosuch, \"100 100 52\"));", "False");
        c.Check(w, "echo(AI::Spawn(A1, tarmor, \"100 100 52\"));", "MSG: \"Server: Player \\\"A1\\\" added - id = 2049\"\nTrue");
        c.Check(w, "echo(AI::Spawn(\"a1\", tarmor, \"0 0 52\"));", "An AI named a1 already exists!\nFalse");
        c.Check(w, "echo(AI::Spawn(B2, tarmor, \"120 100 52\", \"0 0 1\", Bee, female3));",
                "MSG: \"Server: Player \\\"Bee\\\" added - id = 2050\"\nTrue");
        c.Check(w, "echo(AI::GetId(\"a1\"), \" \", AI::GetId(B2), \" \", AI::GetCount());", "2049 2050 2");
        c.Check(w, "echo(AI::GetId(zz));",
                " Could not find drone zz.\nAI::getId(<ai name>);\nReturns the rep Id of the given AI.  The rep Id can in many\n" +
                "cases be used like a client Id (in those console functions that\ntake a client Id).\nFalse");
        c.Check(w, "echo(AI::GetObject(zz));",
                "Could not find drone zz.  Usage:\nAI::GetObject(<ai name>);\nGet an object id handle (for creating member variables).\n-1");
        c.Check(w, "echo(AI::ActionResult(A1), \" \", AI::GetTarget(A1));", "Done -1");
        c.Check(w, "echo(AI::ActionResult(zz));",
                "Couldn't find zz.  Usage:\nAI::ActionResult(<ai name>);\nGive last action result:  done, working, or foiled.\n-1");
        // The display name differs from the AI name: the record and the Player carry the display name (1.4, 1.5).
        c.Check(w, "echo(Client::getName(AI::GetId(B2)), \" \", Object::getName(Client::getOwnedObject(AI::GetId(B2))));", "Bee Bee");
        c.Check(w, "echo(Player::isAiControlled(Client::getOwnedObject(AI::GetId(A1))), \" \"," +
                   " GameBase::getControlClient(Client::getOwnedObject(AI::GetId(A1))));", "True -1");
        c.Check(w, "echo(isObject(\"MissionCleanup\\\\Bee\"), isObject(Bee), isObject(\"MissionCleanup\\\\B2\"));", "TrueFalseFalse");
        c.Check(w, "echo(Client::getFirst());", "-1"); // AI are not clients
        // BaseRep lists humans and AI, newest first (7).
        c.Check(w, "echo(BaseRep::getFirst(), \" \", BaseRep::getNext(2050), \" \", BaseRep::getNext(2049), \" \", BaseRep::getNext(77));",
                "2050 2049 -1 -1");
        // Wildcards: argc < 2 prints the usage, no match prints nothing (6).
        c.Check(w, "echo(AI::Attack(zz, 1));", "False");
        c.Check(w, "echo(AI::Attack(A1));",
                "AI::Attack( aiWildcard, clientId )\nAttack the player having the specified clientId.  No other\n" +
                "actor targeting will happen if this target is set (i.e. it's\nfor one target at a time).\n" +
                "For implementing a list of targets, see AI::DirectiveTarget()\nand AI::setAutomaticTargets()\nFalse");
        c.Check(w, "echo(AI::Attack(\"A*\", 0));", "True");
        c.Check(w, "echo(AI::CallWithId(A1, nosuchfn));", "nosuchfn: Unknown command.\nTrue");
        c.Check(w, "function ai::idarg(%id, %x) { echo(\"idarg \", %id, \" \", %x); return 5; }" +
                   " echo(AI::CallWithId(\"?2\", ai::idarg, 7));", "idarg 2050 7\nTrue");
        {
            string many = "echo(AI::CallWithId(A1, echo";
            for (int i = 0; i < 99; ++i)
                many += ", x" + i.ToString(CultureInfo.InvariantCulture);
            many += "));";
            string got = w.Run(many);
            Assert.True(got.Length > 100 && got[^4..] == "True");
            many = many.Insert(many.Length - 3, ", z"); // one argument more
            c.Check(w, many, "echo has too many args (99 is maximum). Not invoked.\nFalse");
        }
        // Deleting the Player removes the AI and its record at the next tick (1.5); the records are freed in reverse
        // collection order, so the first AI gets its id back first (reference).
        c.Check(w, "echo(AI::Delete(\"*\"));", "True");
        f.Tick();
        c.Check(w, "echo(AI::GetCount(), \" \", BaseRep::getFirst());", "0 -1");
        c.Check(w, "echo(AI::Spawn(C3, tarmor, \"100 100 52\"), AI::GetId(C3));",
                "MSG: \"Server: Player \\\"C3\\\" added - id = 2049\"\nTrue2049");
        Assert.True(c.Failures == 0);
    }

    // ---- directives (2.2, 2.3) ----

    private static void TestDirectives()
    {
        var f = new Fixture("ai_directives");
        Fixtures.World w = f.W;
        var c = new Cases();
        w.Run("AI::Spawn(A1, tarmor, \"100 100 52\"); AI::Spawn(B2, tarmor, \"100 120 52\");");
        // Order rule: no order, 0 and negative orders get `last - last % 100`; the first is 100.
        w.Run("AI::DirectiveWaypoint(A1, \"10 20 30\"); AI::DirectiveWaypoint(A1, \"10 20 30\");" +
              "AI::DirectiveWaypoint(A1, \"10 20 30 4\", 250); AI::DirectiveWaypoint(\"a*\", \"1 1 1\");" +
              "AI::DirectiveWaypoint(A1, \"1 2 3\", -5); AI::DirectiveWaypoint(A1, \"5 6 7\", 9999999999);");
        c.Check(w, "AI::DirectiveList(A1);",
                "Listing all directives (no bitset mask given):\nOrd=100  Type=Waypoint\nOrd=100  Type=Waypoint\n" +
                "Ord=200  Type=Waypoint\nOrd=200  Type=Waypoint\nOrd=250  Type=Waypoint\nOrd=1410065407  Type=Waypoint");
        // An explicit order that exists is replaced in place.
        w.Run("AI::DirectiveWaypoint(A1, \"9 9 9\", 250);");
        c.Check(w, "AI::DirectiveList(A1, 1);",
                "Listing directives with bitset mask of 1:\nOrd=100  Type=Waypoint\nOrd=100  Type=Waypoint\n" +
                "Ord=200  Type=Waypoint\nOrd=200  Type=Waypoint\nOrd=250  Type=Waypoint\nOrd=1410065407  Type=Waypoint");
        c.Check(w, "AI::DirectiveList(A1, 2);", "Listing directives with bitset mask of 2:");
        // Remove by wildcard on the decimal order.
        c.Check(w, "echo(AI::DirectiveRemove(A1, \"2*\"));", "True");
        c.Check(w, "echo(AI::DirectiveRemove(A1, \"2*\"));", "Directive #2* not found for A1\nFalse");
        // With an order above 0x2eb2c000 last (automatic targets) the walk-back rule applies: 100, 200.
        w.Run("AI::DirectiveWaypoint(B2, \"1 1 1\", 1566935042);");
        w.Run("AI::DirectiveWaypoint(B2, \"1 1 1\"); AI::DirectiveWaypoint(B2, \"1 1 1\");");
        c.Check(w, "AI::DirectiveList(B2);",
                "Listing all directives (no bitset mask given):\nOrd=100  Type=Waypoint\nOrd=200  Type=Waypoint\n" +
                "Ord=1566935042  Type=Waypoint");
        // Target kinds and their errors.
        c.Check(w, "echo(AI::DirectiveTarget(B2, AI::GetId(B2)));",
                "An AI may not have itself as a target, directive not added.\nFalse");
        c.Check(w, "echo(AI::DirectiveTargetLaser(B2, AI::GetId(B2)));", "OwnerId 2050 refers to SELF. Not installed.\nFalse");
        c.Check(w, "echo(AI::DirectiveFollow(B2, 99999));", "Couldn't find player for Id 99999.\nFalse");
        c.Check(w, "echo(AI::DirectiveFollow(B2, AI::GetId(A1), \"1 2 3\", -1));",
                "Error: the order# supplied to a directive must be a postive integer. Directive not installed.\nFalse");
        c.Check(w, "echo(AI::DirectiveTarget(A1, 7, 400), AI::DirectiveTargetPoint(A1, \"1 2 3\", 700)," +
                   " AI::DirectiveTargetLaser(A1, 0, 600));", "TrueTrueTrue");
        c.Check(w, "AI::DirectiveList(A1, 4);",
                "Listing directives with bitset mask of 4:\nOrd=400  Type=Target(player)\nOrd=600  Type=Target(laser)\n" +
                "Ord=700  Type=Target(point)");
        Assert.True(c.Failures == 0);
    }

    // ---- typed variables (5.1) ----

    private static void TestVariables()
    {
        var f = new Fixture("ai_variables");
        Fixtures.World w = f.W;
        var c = new Cases();
        w.Run("AI::Spawn(C3, tarmor, \"100 100 52\");");
        // Defaults.
        c.Check(w, "echo($AI::C3::iq, \"|\", $AI::C3::pathType, \"|\", $AI::C3::spotDist, \"|\", $AI::C3::seekOff, \"|\"," +
                   " $AI::C3::attackMode, \"|\", $AI::C3::triggerWindup, \"|\", $AI::C3::triggerPct, \"|\"," +
                   " $AI::C3::smartGuyWeaponRange, \"|\", $AI::C3::dumbGuyWeaponRange, \"|\", $AI::C3::smartGuyMinAccuracy);",
                "0|0|-1|3.000000 3.000000 0.000000|0|6|0.170000|145|55|0.2");
        // Script literals are f32 truncated by the script engine, then by the variable setter, and printed with the
        // engine's truncating printf.
        c.Check(w, "AI::SetVar(C3, triggerPct, 0.17); echo($AI::C3::triggerPct);", "0.169998");
        c.Check(w, "AI::SetVar(C3, triggerPct, 1.5); echo($AI::C3::triggerPct);", "1.000000");
        c.Check(w, "AI::SetVar(C3, triggerPct, -3); echo($AI::C3::triggerPct);", "0.000000");
        c.Check(w, "AI::SetVar(C3, iq, 0.1); echo($AI::C3::iq);", "0.0999998");
        c.Check(w, "AI::SetVar(C3, iq, 33.333); echo($AI::C3::iq);", "33.3328");
        c.Check(w, "AI::SetVar(C3, seekOff, \"0.1 0.2 0.3\"); echo($AI::C3::seekOff);", "0.099999 0.199999 0.299999");
        c.Check(w, "$AI::C3::seekOff = \"1 2\"; echo($AI::C3::seekOff);", "1.000000 2.000000 0.000000");
        c.Check(w, "AI::SetVar(C3, attackMode, 2.9); echo($AI::C3::attackMode);", "2");
        c.Check(w, "echo(AI::SetVar(C3, iq));", "Value of iq for C3 = 33.3328\nTrue");
        c.Check(w, "echo(AI::SetVar(C3, bogus, 7), \" \", $AI::C3::bogus);", "True 7");
        // A value set before the spawn is applied at registration (no recompute).
        w.Run("$AI::D4::iq = 80;");
        w.Run("AI::Spawn(D4, tarmor, \"120 100 52\");");
        c.Check(w, "echo($AI::D4::iq);", "80");
        Assert.True(f.Ai("D4")!.Iq == 80.0f);
        // Variables go away with the AI (4.3).
        w.Run("AI::Spawn(E5, tarmor, \"140 100 52\"); Player::kill(Client::getOwnedObject(AI::GetId(E5)));");
        f.Tick(2);
        c.Check(w, "echo($AI::E5::iq == \"\", \" \", AI::SetVar(\"Corpse*\", iq, 1));", "1 True");
        Assert.True(c.Failures == 0);
    }
    // ---- manager tick: steering, think gate, trigger, callbacks, death (1.6, 3, 4) ----

    private static void TestBehavior()
    {
        var f = new Fixture("ai_behavior");
        Fixtures.World w = f.W;
        // iq 0: a waypoint 32 m behind. The AI waits for the think gate (50 ticks), then turns by the iq 0 yaw rate every
        // tick (reference run, tools/aidiff.py c_rates).
        w.Run("AI::Spawn(W, tarmor, \"100 100 51\", \"0 0 0\");");
        Player? p = f.Player("W");
        Assert.True(p != null);
        f.Tick(5); // idle: state 1, the think counter runs
        w.Run("AI::DirectiveWaypoint(W, \"99.97 68 50\", 7);");
        int first = -1;
        for (int i = 0; i < 80 && first < 0; ++i)
        {
            f.Tick();
            if (p!.Rotation[2] != 0.0f)
                first = i;
        }
        Assert.True(first >= 0 && first <= 50);
        string[] want0 = { "0.0499999", "0.0999999", "0.149999", "0.199999", "0.249999", "0.299999", "0.349999", "0.399999" };
        Assert.True(RotZ(w, p!) == want0[0]);
        for (int i = 1; i < 8; ++i)
        {
            f.Tick();
            Assert.True(RotZ(w, p!) == want0[i]);
        }
        // The move: maximum speed 0.4 at iq 0 while the target is farther than 1.7 (3.3).
        Assert.True(f.Ai("W")!.Sk.MaxSpeed == 0.4f && f.Ai("W")!.Sk.Think == 50);

        // iq 100: yaw rate 0.07, speed 0.999999.
        w.Run("AI::Spawn(I, tarmor, \"140 100 51\", \"0 0 0\"); AI::SetVar(I, iq, 100);");
        f.Tick(5);
        w.Run("AI::DirectiveWaypoint(I, \"140 68 50\", 7);");
        Player q = f.Player("I")!;
        int firstQ = -1;
        for (int i = 0; i < 20 && firstQ < 0; ++i)
        {
            f.Tick();
            if (q.Rotation[2] != 0.0f)
                firstQ = i;
        }
        Assert.True(firstQ >= 0 && firstQ < 8);
        // iq 100: skill = f32(100 * 0.01) truncates to 0.999999, so the think period is 4 and the speed 0.999999.
        Assert.True(f.Ai("I")!.Skill == 0.99999994f && f.Ai("I")!.Sk.MaxSpeed == 0.99999994f && f.Ai("I")!.Sk.Think == 4);
        Assert.True(f.Ai("I")!.Sk.Rate[0] == X87.F32(0.07));
        string[] wantQ = { "-0.0699999", "-0.14", "-0.21", "-0.28", "-0.35" };
        Assert.True(RotZ(w, q) == wantQ[0]);
        for (int i = 1; i < 5; ++i)
        {
            f.Tick();
            Assert.True(RotZ(w, q) == wantQ[i]);
        }
        w.Run("AI::Delete(\"*\");");
        f.Tick(2);

        // Straight ahead: forward = cos(0) * maxSpeed, no side movement (3.3).
        w.Run("AI::Spawn(S, tarmor, \"100 100 51\", \"0 0 0\"); AI::SetVar(S, iq, 100); AI::DirectiveWaypoint(S, \"100 140 50\", 1);");
        f.Tick(20);
        AIObject s = f.Ai("S")!;
        Assert.True(s.Move.Forward == s.Sk.MaxSpeed && s.Move.Left == 0.0f && s.Move.Turn == 0.0f);
        w.Run("AI::Delete(\"*\");");
        f.Tick(2);

        // Point target: yaw and pitch under 0.08, then the trigger is held for the windup (6) pulls, after which each
        // pull is one R250 draw against triggerPct (3.6, 3.8).
        StaticRandom.Reset();
        w.Run("AI::Spawn(T, tarmor, \"100 100 51\", \"0 0 0\"); AI::SetVar(T, iq, 100);" +
              "AI::SetVar(T, triggerPct, 0.5); AI::DirectiveTargetPoint(T, \"100 140 51\");");
        var replay = new R250(0x8377);
        int fired = 0, aimed = 0, windup = 0;
        string pattern = "", expected = "";
        AIObject t = f.Ai("T")!;
        for (int i = 0; i < 40; ++i)
        {
            f.Tick();
            if (t.Move.Fire)
                ++fired;
            if (aimed == 0 && t.Move.Fire)
                aimed = i + 1;
            if (aimed != 0)
            {
                pattern += t.Move.Fire ? '1' : '0';
                if (windup < 6)
                    expected += '1';
                else
                    expected += replay.Unit() < (double)0.5f ? '1' : '0';
                ++windup;
            }
        }
        Assert.True(aimed > 0 && fired >= 6);
        Assert.True(pattern[..6] == "111111");
        Assert.True(pattern == expected);
        w.Run("AI::Delete(\"*\");");
        f.Tick(2);

        // D17: AI::Attack on a visible target (no target directive) does not crash. The AI engages (state 3) and
        // AI::GetTarget stays -1 where the original reads a NULL directive (docs/ai-sim.md 9 X1).
        w.Run("AI::Spawn(X, tarmor, \"100 100 51\"); AI::Spawn(Y, tarmor, \"100 120 51\");");
        f.Tick(5);
        w.Run("AI::Attack(X, AI::GetId(Y));");
        int engaged = 0;
        for (int i = 0; i < 80; ++i)
        {
            f.Tick();
            if (f.Ai("X")!.State == 3)
                ++engaged;
        }
        Assert.True(engaged > 20);
        Assert.True(w.Run("echo(AI::GetTarget(X));") == "-1");
        w.Run("AI::Delete(\"X\"); AI::Delete(\"Y\");");
        f.Tick(2);

        // Periodic callback: first call on the next tick, then every 32 ticks (4.2).
        w.Run("$per = 0; function per(%n) { $per++; $perName = %n; }" +
              "AI::Spawn(P, tarmor, \"100 100 51\"); AI::CallbackPeriodic(P, 1, per);");
        f.Tick();
        Assert.True(w.In.PeekVariable("$per") == "1" && w.In.PeekVariable("$perName") == "P");
        f.Tick(31);
        Assert.True(w.In.PeekVariable("$per") == "1");
        f.Tick(1);
        Assert.True(w.In.PeekVariable("$per") == "2");
        // Death: the died callback runs once, the AI is renamed and its variables go (4.3).
        w.Run("$died = \"\"; function died(%n) { $died = %n @ \" \" @ AI::GetId(%n); }" +
              "AI::CallbackDied(P, died); Player::kill(Client::getOwnedObject(AI::GetId(P)));");
        f.Tick();
        string died = w.In.PeekVariable("$died");
        Assert.True(died == "P 2049");
        AIObject? corpse = f.Ai("P");
        Assert.True(corpse == null && w.Game.Ai!.Ais.Count == 1);
        Assert.True(w.Game.Ai.Ais[0].AiName.StartsWith("Corpse", StringComparison.Ordinal) && w.Game.Ai.Ais[0].AiName.Length > 8);
        Assert.True(!w.In.HasVariable("$AI::P::iq"));
        // The periodic callback continues with the corpse name.
        f.Tick(33);
        Assert.True(string.CompareOrdinal(w.In.PeekVariable("$perName"), 0, "Corpse", 0, 6) == 0); // rfind("Corpse", 0) == 0
        // Deleting the corpse removes the record at the next tick (4.3).
        w.Run("deleteObject(Client::getOwnedObject(2049));");
        f.Tick();
        Assert.True(w.Run("echo(AI::GetCount());") == "0");
    }

    // ---- the AI math (3.3, 3.5, 5.2) ----

    private static void TestMath()
    {
        var s = new AISkill();
        s.Build(false);
        // Constructor block = iq 0 (5.2).
        Assert.True(s.Think == 50 && s.Spot == 50.0f && s.MaxSpeed == 0.4f);
        s.ApplyIq(1.0f);
        Assert.True(s.Think == 3 && s.Spot == 150.0f && s.MaxSpeed == 1.0f && s.Range == 145.0f && s.Skill == 1.0f);
        s.ApplyIq(0.0f);
        Assert.True(s.Think == 50 && s.Range == 55.0f && s.MinAccuracy == 0.0f);
        s.ApplyIq(0.5f);
        Assert.True(s.Think == 50 + (int)(-47 * 0.5) && s.Spot == 100.0f);
        // accuracy = (minAccuracy - skill) * clamp(dist / range) + skill
        s.ApplyIq(1.0f);
        Assert.True(s.Accuracy(0.0f) == 1.0f && s.Accuracy(145.0f) == s.MinAccuracy && s.Accuracy(500.0f) == s.MinAccuracy);
        // The yaw rate triple (r, 2r, 0.1r).
        var d = new AISkill();
        d.Build(false);
        Assert.True(d.Rate[1] == MulF(2.0f, d.Rate[0]));
    }

    // The aim solver of the projectile datablocks (docs/ai-sim.md 3.7, 12.4; 0x0045b99c, 0x00460b5c, 0x0045ed40).
    private static void TestAimSolver()
    {
        using var chop = X87.Chop();
        float[] s = { 0, 0, 0 };
        float[] dir = new float[3];
        bool ok = false;
        // Bullet and Rocket: the straight line; ok when speed x totalTime is above the distance; no high arc.
        float[] far = { 300, 400, 0 };
        Assert.True(AIAim.AiSolveAim(Projectile.kBulletGroup, 100.0f, 10.0f, s, far, false, -20.0f, ref ok, dir) && ok);
        Assert.True(Math.Abs(SubF(dir[0], 0.6f)) < 1e-6f && Math.Abs(SubF(dir[1], 0.8f)) < 1e-6f && dir[2] == 0.0f);
        Assert.True(AIAim.AiSolveAim(Projectile.kBulletGroup, 100.0f, 4.0f, s, far, false, -20.0f, ref ok, dir) && !ok); // 400 < 500
        Assert.True(!AIAim.AiSolveAim(Projectile.kBulletGroup, 100.0f, 10.0f, s, far, true, -20.0f, ref ok, dir));
        Assert.True(AIAim.AiSolveAim(Projectile.kRocketGroup, 100.0f, 4.0f, s, far, false, -20.0f, ref ok, dir) && !ok);
        Assert.True(AIAim.AiSolveAim(Projectile.kSeekerGroup, 100.0f, 4.0f, s, far, false, -20.0f, ref ok, dir) && ok); // the base solver
        Assert.True(!AIAim.AiSolveAim(Projectile.kSeekerGroup, 100.0f, 4.0f, s, far, true, -20.0f, ref ok, dir));
        // Grenade: both arcs land on the target under the gravity -20 (the low arc is flatter).
        float[] tgt = { 150, 200, 12 };
        float[] lo = new float[3], hi = new float[3];
        bool okLo = false, okHi = false;
        Assert.True(AIAim.AiSolveAim(Projectile.kGrenadeGroup, 100.0f, 10.0f, s, tgt, false, -20.0f, ref okLo, lo) && okLo);
        Assert.True(AIAim.AiSolveAim(Projectile.kGrenadeGroup, 100.0f, 10.0f, s, tgt, true, -20.0f, ref okHi, hi) && okHi);
        Assert.True(lo[2] < hi[2]);
        foreach (float[] d in new[] { lo, hi })
        {
            double hl = Sqrt(Add(Mul(d[0], d[0]), Mul(d[1], d[1])));
            double dist = Sqrt(Add(Mul(150.0, 150.0), Mul(200.0, 200.0)));
            double T = Div(dist, Mul(100.0, hl));
            double z = Sub(Mul(Mul(100.0, d[2]), T), Mul(Mul(10.0, T), T));
            Assert.True(Math.Abs(Sub(z, 12.0)) < 0.05);
            Assert.True(Math.Abs(Sub(Sqrt(Add(Add(Mul(d[0], d[0]), Mul(d[1], d[1])), Mul(d[2], d[2]))), 1.0)) < 1e-5);
        }
        // Out of reach: the straight line with ok false, and no high arc when the discriminant is under 1.
        float[] distant = { 5000, 0, 0 };
        Assert.True(AIAim.AiSolveAim(Projectile.kGrenadeGroup, 100.0f, 10.0f, s, distant, false, -20.0f, ref ok, dir) && !ok);
        Assert.True(Math.Abs(SubF(dir[0], 1.0f)) < 1e-6f && dir[1] == 0.0f && dir[2] == 0.0f);
        Assert.True(!AIAim.AiSolveAim(Projectile.kGrenadeGroup, 100.0f, 10.0f, s, distant, true, -20.0f, ref ok, dir) || !ok);
    }

    // The aim point of DirectiveTargetLaser (docs/ai-sim.md 12.5, 0x0043cec0): dir x 50 is rounded to f32 before the eye is
    // added. -0.1f x 50 = -5.0000000745 truncates to -5, so 200 - 5 = 195 exactly; one rounding would give 194.99998.
    private static void TestLaserAimPoint()
    {
        using var chop = X87.Chop();
        float[] eye = { 10.0f, 200.0f, 30.0f };
        float[] dir = { 0.0f, -0.1f, 1.0f };
        float[] aim = new float[3];
        AIAim.LaserAimPoint(eye, dir, aim);
        Assert.True(aim[0] == 10.0f && aim[1] == 195.0f && aim[2] == 80.0f);
    }

    [Fact]
    public void Main()
    {
        TestAimSolver();
        TestLaserAimPoint();
        TestMath();
        TestCommands();
        TestDirectives();
        TestVariables();
        TestBehavior();
    }
}
