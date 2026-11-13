// Port of src/game/ai_commands.cpp: the `AI::*` and `BaseRep::*` console commands (docs/ai-sim.md 6, plugin 0x0043f684 /
// 0x0043f8fc). A C++ handler sets its result through a reference and returns true; here it returns the result.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>The <c>AI::*</c> and <c>BaseRep::*</c> console commands.</summary>
/// <remarks>Plugin 0x0043f684 and 0x0043f8fc (docs/ai-sim.md 6). A C++ handler sets its result through a reference and returns true. Here it returns the result.</remarks>
public static class AICommands
{
    private static readonly string[] kSpawnUsage =
    [
        "AI::spawn: <AI name> <armor type> <position> [rotation] [display name] [voice]",
        "Create an AI of the given name and armor type at position.",
        "If not given, display name is the same as the AI name.",
        "Voice defaults to male1 if not supplied here.",
    ];
    private static readonly string[] kGetIdUsage =
    [
        "AI::getId(<ai name>);", "Returns the rep Id of the given AI.  The rep Id can in many",
        "cases be used like a client Id (in those console functions that", "take a client Id).",
    ];
    private static readonly string[] kGetTargetUsage =
    [
        "AI::GetTarget(<ai name>);", "If the given AI is currently attacking one of its targets, the rep",
        "id of that target is returned, else -1 is returned.",
    ];
    private static readonly string[] kAttackUsage =
    [
        "AI::Attack( aiWildcard, clientId )",
        "Attack the player having the specified clientId.  No other",
        "actor targeting will happen if this target is set (i.e. it's",
        "for one target at a time).",
        "For implementing a list of targets, see AI::DirectiveTarget()",
        "and AI::setAutomaticTargets()",
    ];
    private static readonly string[] kDeleteUsage =
        ["AI::Delete( <AI wildcard> )", "Delete any AI which matches the given wildcard specification."];
    private static readonly string[] kFollowUsage =
    [
        "AI::DirectiveFollow <ai wildcard> <playerID> [Pt/Rad] [Ord]",
        "Instructs the given AI(s) to follow the specified player.",
        "Ord is used for sorting directives. The optional point/radius",
        "(and note that the point can be given alone) tells the AI when",
        "to stop or move on to his or her next directive.",
    ];
    private static readonly string[] kWaypointUsage =
    [
        "AI::DirectiveWaypoint: <ai wildcard> <pt3F [Rad]> [ord]",
        "Adds the given waypoint to the directive list of any AI that",
        "that matches the wildcard specification.  The Radius is an optional",
        "addition in the point.  The optional Order number is used for",
        "sorting of the directives.  If not given, the waypoint will be",
        "put at the end of the directive list(s).",
    ];
    private static readonly string[] kTargetUsage = ["AI::DirectiveTarget: <ai wildcard> <player ID> [ord]"];
    private static readonly string[] kLaserUsage =
    [
        "AI::DirectiveTargetLaser: <ai wildcard> <owner Id> [ord] [point+R]",
        "Aim at the point targeted by the given Id.  If the owner Id is",
        "zero then the Ai will look for any laser 'paint' that can be hit.",
        "The optional point+radius gives a sphere inside that the paint",
        "must be inside of for the AI to fire (cf. weapons training).",
    ];
    private static readonly string[] kPointUsage =
    [
        "AI::DirectiveTargetPoint: <ai wildcard> <point> [ord]",
        "Assign the given point as a target.  The AI will fire at this",
        "point as long as the target current and LOS exists. First",
        "intended use: painting a target with the target laser.",
    ];
    private static readonly string[] kRemoveUsage = ["AI::DirectiveRemove( <ai wildcard> <order # key> )"];
    private static readonly string[] kListUsage =
    [
        "AI::DirectiveList( <ai wildcard> [,directive bitset mask] );",
        "For each AI matching the given wildcard, this lists its",
        "directives.  If a mask is given, then only those which match",
        "the given types will be listed. See AI::DirectiveCallback1()",
        "for mask definitions.",
    ];
    private static readonly string[] kDiedUsage =
        ["AI::CallbackDied: <ai wildcard> <script function name>", "Install the given death callback on the specified AI objects."];
    private static readonly string[] kPeriodicUsage =
    [
        "AI::CallbackPeriodic: <ai wildcard> <freqDelay> [,function name]",
        "Set up a periodic callback with the given frequency (delay between",
        "calls given in seconds).  If no function name is given then the",
        "default of AI::onPeriodic() is called with the (script) name of the AI",
        "as a parameter. For sake of efficiency, the frequency must be at least",
        "one.  Smaller values will disable periodic callbacks for the AIs",
        "specified (they are disabled by default).",
    ];
    private static readonly string[] kSetVarUsage =
    [
        "AI::setVar( <ai wildcard>, <variable name> [, <value>] )",
        "Command to set the given AI variable to <value> in all AIs that match",
        "the wild card spec.  e.g. ai::setVar( \"a*\", iq, 20 ); ",
        "If no value is provided, the current value is printed.",
    ];
    private static readonly string[] kCallWithIdUsage =
    [
        "AI::CallWithId( <ai wildcard>, <Console command with ID arg> [,arg 1] [...] );",
        "AI::CallWithId() can be used to invoke a console command for every AI object",
        "that matches the wildcard spec.  The console command must be one which takes",
        "a rep Id as its first argument.",
    ];
    private static readonly string[] kAutoUsage = ["AI::SetAutomaticTargets( <ai wildcard> );"];
    private static readonly string[] kScriptedUsage =
    [
        "AI::SetScriptedTargets( <ai wildcard> );",
        "Causes the specified AI objects to only attack those targets which they",
        "have been scripted to attack, and none others. This is the default mode.",
        "The opposite function is AI::SetAutomaticTargets()",
    ];

    // src/game/ai_commands.cpp usage.
    private static void Usage(Interpreter inr, string[] lines)
    {
        foreach (var l in lines)
            inr.Print(1, l);
    }

    // src/game/ai_commands.cpp scan4: "%f %f %f %f" the way the original's sscanf reads it (truncated to f32); the radius
    // is parsed and never read (docs/ai-sim.md 2.1).
    private static float[] Scan4(string s)
    {
        var v = new float[4];
        MathCommands.ScanFloats3(s, v);
        var p = CString.CStr(s);
        var at = 0;
        for (var skipped = 0; skipped < 3 && at < p.Length; ++skipped)
        {
            while (at < p.Length && (p[at] == ' ' || p[at] == '\t'))
                ++at;
            while (at < p.Length && p[at] != ' ' && p[at] != '\t')
                ++at;
        }
        CScan.Floats(p[at..], v.AsSpan(3, 1));
        return v;
    }

    // src/game/ai_commands.cpp truncName.
    private static string TruncName(string s) => s.Length > 31 ? s[..31] : s;

    /// <summary>Registers the AI and BaseRep commands.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="g">The game state the commands act on.</param>
    public static void Register(Interpreter interp, GameState g)
    {
        // Wildcard commands (ids 7+): argc < 2 runs the handler once without an AI.
        void Wild(string name, Func<Interpreter, AIObject?, string[], string> h) => interp.AddCommand(name, (inr, a) =>
                                                                                             {
                                                                                                 var r = "False";
                                                                                                 if (AIManager.EnsureAIManager(g, a[0]) == null)
                                                                                                     return r;
                                                                                                 if (a.Length < 2)
                                                                                                 {
                                                                                                     h(inr, null, a);
                                                                                                     return r;
                                                                                                 }
                                                                                                 var hit = new List<AIObject>();
                                                                                                 foreach (AIObject ai in g.Ai!.Ais)
                                                                                                     if (ai.Matches(a[1]))
                                                                                                         hit.Add(ai);
                                                                                                 var ids = new List<int>();
                                                                                                 foreach (AIObject ai in hit)
                                                                                                     ids.Add(ai.Id());
                                                                                                 SimManager? m = g.Sim?.Server();
                                                                                                 r = "False";
                                                                                                 for (var i = 0; i < hit.Count; ++i)
                                                                                                 {
                                                                                                     if (m == null || m.FindById(ids[i]) != hit[i])
                                                                                                         continue; // removed by an earlier callback
                                                                                                     r = h(inr, hit[i], a);
                                                                                                 }
                                                                                                 return r;
                                                                                             });
        // Commands 0..6 take (argc, argv) as they are.
        void Plain(string name, Func<Interpreter, string[], string> h) => interp.AddCommand(name, (inr, a) =>
                                                                                   {
                                                                                       if (AIManager.EnsureAIManager(g, a[0]) == null)
                                                                                           return "False";
                                                                                       return h(inr, a);
                                                                                   });

        // ---- AI::Spawn(name, armor, pos [, rot [, displayName [, voice]]]) 0x0043e85c ----
        Plain("AI::Spawn", (inr, a) =>
        {
            if (a.Length < 4)
            {
                Usage(inr, kSpawnUsage);
                return "False";
            }
            SimManager? m = g.Sim?.Server();
            if (m == null)
            {
                inr.Print(1, "Requisite manager(s) not found.");
                return "False";
            }
            if (g.Ai!.FindByName(a[1]) != null)
            {
                inr.Print(1, "An AI named " + a[1] + " already exists!");
                return "False";
            }
            if (g.Players.NextFreeId() == 0)
                return "False"; // X3: the original dereferences NULL (DECISIONS D11)
            float[] pos = [0, 0, 0], rot = [0, 0, 0];
            MathCommands.ScanFloats3(a[3], pos);
            if (a.Length > 4)
                MathCommands.ScanFloats3(a[4], rot);
            var display = a.Length > 5 ? a[5] : a[1];
            var defaultVoice = StringTable.Global().Intern("male1"); // the default is interned before the argument is read
            var voice = a.Length > 6 ? a[6] : defaultVoice;

            var p = new Player(g);
            p.SetRotation(rot);
            p.SetPosition(pos);
            p.DataBlockName = StringTable.Global().Intern(a[2]);
            p.AiControlled = true;
            if (!m.RegisterObject(p))
            {
                p.Destroy();
                return "False";
            }
            p.SetMapName(display);
            var ai = new AIObject(g) { AiName = TruncName(a[1]), Voice = voice, PlayerPtr = p, PlayerId = p.Id() };
            if (!m.RegisterObject(ai))
            {
                m.DeleteObject(p);
                ai.Destroy();
                return "False";
            }
            g.Ai.Ais.Add(ai);
            ai.RegisterVariables();
            if (m.Find("MissionCleanup")?.AsGroup() is SimGroup group)
            {
                group.AddObject(p);
                group.AssignName(p, StringTable.Global().Intern(display));
                group.AddObject(ai);
            }
            return "True";
        });

        // ---- AI::List() 0x0043e7a0 ----
        Plain("AI::List", (inr, _) =>
        {
            foreach (AIObject ai in g.Ai!.Ais)
            {
                Player? p = ai.Player();
                float[] pt = [0, 0, 0], rot = [0, 0, 0];
                if (p != null)
                {
                    using var chop = X87.Chop();
                    AIObject.AiTargetPoint(g, p, pt);
                    p.Rotation.CopyTo(rot, 0);
                }
                inr.Print(1, ai.AiName + "  L=(" + Numbers.FormatFloat(pt[0]) + "," + Numbers.FormatFloat(pt[1]) + "," +
                                 Numbers.FormatFloat(pt[2]) + ")  R=(" + Numbers.FormatFloat(rot[0]) + "," +
                                 Numbers.FormatFloat(rot[1]) + "," + Numbers.FormatFloat(rot[2]) + ")");
            }
            return "True";
        });
        Plain("AI::GetCount", (_, _) => Numbers.FormatInt(g.Ai!.Ais.Count));

        Plain("AI::GetId", (inr, a) =>
        {
            if (a.Length < 2)
            {
                Usage(inr, kGetIdUsage);
                return "False";
            }
            AIObject? ai = g.Ai!.FindByName(a[1]);
            if (ai == null)
            {
                inr.Print(1, " Could not find drone " + a[1] + ".");
                Usage(inr, kGetIdUsage);
                return "False";
            }
            return Numbers.FormatInt(ai.RepId);
        });
        Plain("AI::GetTarget", (inr, a) =>
        {
            if (a.Length < 2)
            {
                Usage(inr, kGetTargetUsage);
                return "False";
            }
            AIObject? ai = g.Ai!.FindByName(a[1]);
            if (ai == null)
            {
                inr.Print(1, " Could not find drone " + a[1] + ".");
                Usage(inr, kGetTargetUsage);
                return "False";
            }
            return Numbers.FormatInt(ai.TargetId);
        });
        static void ObjectUsage(Interpreter inr, string cmd) => inr.Print(1, cmd + "(<ai name>);");
        Plain("AI::GetObject", (inr, a) =>
        {
            if (a.Length < 2)
            {
                ObjectUsage(inr, a[0]);
                inr.Print(1, "Get an object id handle (for creating member variables).");
                return "-1";
            }
            AIObject? ai = g.Ai!.FindByName(a[1]);
            if (ai == null)
            {
                inr.Print(1, "Could not find drone " + a[1] + ".  Usage:");
                ObjectUsage(inr, a[0]);
                inr.Print(1, "Get an object id handle (for creating member variables).");
                return "-1";
            }
            return Numbers.FormatInt(ai.Id());
        });
        Plain("AI::ActionResult", (inr, a) =>
        {
            if (a.Length != 2)
            {
                ObjectUsage(inr, a[0]);
                inr.Print(1, "Give last action result:  done, working, or foiled.");
                return "-1";
            }
            AIObject? ai = g.Ai!.FindByName(a[1]);
            if (ai == null)
            {
                inr.Print(1, "Couldn't find " + a[1] + ".  Usage:");
                ObjectUsage(inr, a[0]);
                inr.Print(1, "Give last action result:  done, working, or foiled.");
                return "-1";
            }
            return ai.Result;
        });

        // ---- wildcard commands ----
        Wild("AI::Attack", (inr, ai, a) =>
        {
            if (ai == null || a.Length != 3)
            {
                Usage(inr, kAttackUsage);
                return "False";
            }
            ai.AttackId = Numbers.BorlandAtoi(a[2]);
            return "True";
        });
        Wild("AI::Delete", (inr, ai, _) =>
        {
            if (ai == null)
            {
                Usage(inr, kDeleteUsage);
                return "False";
            }
            if (ai.Player() is Player p && g.Sim?.Server() is SimManager m)
                m.DeleteObject(p);
            return "True";
        });

        static int OrderArg(string[] a, int at) => a.Length > at ? Numbers.BorlandAtoi(a[at]) : 0;

        Wild("AI::DirectiveWaypoint", (inr, ai, a) =>
        {
            if (ai == null || a.Length < 3)
            {
                Usage(inr, kWaypointUsage);
                return "False";
            }
            var d = new AIDirective { Type = AIDirective.kWaypoint };
            var v = Scan4(a[2]);
            Array.Copy(v, d.Point, 3);
            d.Radius = v[3];
            ai.AddDirective(d, OrderArg(a, 3));
            return "True";
        });
        Wild("AI::DirectiveFollow", (inr, ai, a) =>
        {
            if (ai == null || a.Length < 3)
            {
                Usage(inr, kFollowUsage);
                return "False";
            }
            var id = Numbers.BorlandAtoi(a[2]);
            ClientRecord? rec = g.Players.Find(id);
            SimManager? m = g.Sim?.Server();
            if (rec == null || rec.OwnedObject == 0 || m == null || Player.From(m.FindById(rec.OwnedObject)) == null)
            {
                inr.Print(1, "Couldn't find player for Id " + Numbers.FormatInt(id) + ".");
                return "False";
            }
            var order = OrderArg(a, 4);
            if (order < 0)
            {
                inr.Print(1, "Error: the order# supplied to a directive must be a postive integer. Directive not installed.");
                return "False";
            }
            var d = new AIDirective { Type = AIDirective.kFollow, Client = id };
            if (a.Length > 3)
            {
                var v = Scan4(a[3]);
                Array.Copy(v, d.Point, 3);
                d.Radius = v[3];
            }
            ai.AddDirective(d, order);
            return "True";
        });
        Wild("AI::DirectiveTarget", (inr, ai, a) =>
        {
            if (ai == null || a.Length < 3)
            {
                Usage(inr, kTargetUsage);
                return "False";
            }
            var id = Numbers.BorlandAtoi(a[2]);
            if (id == ai.RepId)
            {
                inr.Print(1, "An AI may not have itself as a target, directive not added.");
                return "False";
            }
            var d = new AIDirective { Type = AIDirective.kTarget, Sub = AIDirective.kPlayer, Client = id };
            ai.AddDirective(d, OrderArg(a, 3));
            return "True";
        });
        Wild("AI::DirectiveTargetLaser", (inr, ai, a) =>
        {
            if (ai == null || a.Length < 3)
            {
                Usage(inr, kLaserUsage);
                return "False";
            }
            var id = Numbers.BorlandAtoi(a[2]);
            if (id == ai.RepId)
            {
                inr.Print(1, "OwnerId " + Numbers.FormatInt(id) + " refers to SELF. Not installed.");
                return "False";
            }
            var d = new AIDirective { Type = AIDirective.kTarget, Sub = AIDirective.kLaser, Client = id };
            if (a.Length > 4)
            {
                var v = Scan4(a[4]);
                Array.Copy(v, d.Point, 3);
                d.Radius = v[3];
            }
            ai.AddDirective(d, OrderArg(a, 3));
            return "True";
        });
        Wild("AI::DirectiveTargetPoint", (inr, ai, a) =>
        {
            if (ai == null || a.Length < 3)
            {
                Usage(inr, kPointUsage);
                return "False";
            }
            var d = new AIDirective { Type = AIDirective.kTarget, Sub = AIDirective.kPoint };
            var v = Scan4(a[2]);
            Array.Copy(v, d.Point, 3);
            d.Radius = v[3];
            ai.AddDirective(d, OrderArg(a, 3));
            return "True";
        });
        Wild("AI::DirectiveRemove", (inr, ai, a) =>
        {
            if (ai == null || a.Length != 3)
            {
                Usage(inr, kRemoveUsage);
                return "False";
            }
            var any = false;
            var kept = new List<AIDirective>();
            foreach (AIDirective d in ai.Directives)
            {
                if (ResourceManager.WildcardMatch(a[2], Numbers.FormatInt(d.Order)))
                    any = true;
                else
                    kept.Add(d);
            }
            if (!any)
            {
                inr.Print(1, "Directive #" + a[2] + " not found for " + ai.AiName);
                return "False";
            }
            ai.Directives = kept;
            return "True";
        });
        Wild("AI::DirectiveList", (inr, ai, a) =>
        {
            if (ai == null)
            {
                Usage(inr, kListUsage);
                return "False";
            }
            var mask = a.Length > 2 ? Numbers.BorlandAtoi(a[2]) : 0xfffffff;
            if (a.Length <= 2)
                inr.Print(1, "Listing all directives (no bitset mask given):");
            else
                inr.Print(1, "Listing directives with bitset mask of " + Numbers.FormatInt(mask) + ":");
            foreach (AIDirective d in ai.Directives)
            {
                if ((d.Type & mask) == 0)
                    continue;
                var type = d.Type == AIDirective.kWaypoint ? "Waypoint" : d.Type == AIDirective.kFollow ? "Follow"
                              : d.Type == AIDirective.kTarget ? "Target" : d.Type == AIDirective.kGuard ? "Guard" : "unknown";
                var sub = "";
                if (d.Type == AIDirective.kTarget)
                    sub = d.Sub == AIDirective.kPlayer ? "(player)" : d.Sub == AIDirective.kLaser ? "(laser)"
                          : d.Sub == AIDirective.kPoint ? "(point)" : "(what?)";
                inr.Print(1, "Ord=" + Numbers.FormatInt(d.Order) + "  Type=" + type + sub);
            }
            return "True";
        });
        Wild("AI::CallbackDied", (inr, ai, a) =>
        {
            if (ai == null || a.Length < 3)
            {
                Usage(inr, kDiedUsage);
                return "False";
            }
            ai.CbDied = StringTable.Global().Intern(a[2]);
            return "True";
        });
        Wild("AI::CallbackPeriodic", (inr, ai, a) =>
        {
            if (ai == null || a.Length < 3)
            {
                Usage(inr, kPeriodicUsage);
                return "False";
            }
            using (X87.Chop())
            {
                var period = Numbers.FloatToInt(Mul(Numbers.BorlandAtof(a[2]), 32.0));
                ai.PeriodicPeriod = period;
                ai.PeriodicCounter = period;
            }
            if (a.Length > 3)
                ai.CbPeriodic = StringTable.Global().Intern(a[3]);
            return "True";
        });
        for (var slot = 1; slot <= 4; ++slot)
        {
            var s = slot;
            var name = "AI::DirectiveCallback" + s.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Wild(name, (inr, ai, a) =>
            {
                void Err(string what)
                {
                    if (what.Length != 0)
                        inr.Print(1, "ERROR: " + what + "...  Note Usage:");
                    inr.Print(1, "AI::DirectiveCallback" + Numbers.FormatInt(s) +
                                 "( <ai wildcard> <CB name> [<CB# wildcard>] [<directive filter>] )");
                    inr.Print(1, "  <CB# wildcard> gives which directive order#(s) to install to.");
                    inr.Print(1, "        If none (or 0) is given, the last directive is assumed.");
                    inr.Print(1, "  <directive filter> gives a mask to limit directive types:");
                    inr.Print(1, "        Waypoint=1, Follow=2, Target=4.");
                }
                if (ai == null || a.Length < 3)
                {
                    Err("");
                    return "False";
                }
                var pattern = a.Length > 3 ? a[3] : "*";
                var mask = a.Length > 4 ? Numbers.BorlandAtoi(a[4]) : 0xffff;
                var fn = StringTable.Global().Intern(a[2]);
                foreach (AIDirective d in ai.Directives)
                    if ((d.Type & mask) != 0 && ResourceManager.WildcardMatch(pattern, Numbers.FormatInt(d.Order)))
                        d.Callback[s - 1] = fn;
                return "True";
            });
        }
        Wild("AI::CallWithId", (inr, ai, a) =>
        {
            var n = a.Length >= 2 ? a.Length - 2 : 0;
            if (ai == null || n < 1)
            {
                Usage(inr, kCallWithIdUsage);
                return "False";
            }
            if (n > 100)
            {
                inr.Print(1, a[2] + " has too many args (99 is maximum). Not invoked.");
                return "False";
            }
            var args = new List<string> { Numbers.FormatInt(ai.RepId) };
            for (var i = 3; i < a.Length; ++i)
                args.Add(a[i]);
            g.Console!.CallFunction(a[2], args);
            return "True";
        });
        Wild("AI::SetVar", (inr, ai, a) =>
        {
            if (ai == null || a.Length < 3)
            {
                Usage(inr, kSetVarUsage);
                return "False";
            }
            if (a.Length == 3)
            {
                var value = "";
                if ((ai.Flags & 8) != 0)
                    value = inr.PeekVariable(ai.VarName(a[2]));
                inr.Print(1, "Value of " + a[2] + " for " + ai.AiName + " = " + value);
            }
            else if ((ai.Flags & 8) != 0)
            {
                inr.SetVariable(ai.VarName(a[2]), a[3]);
            }
            return "True";
        });
        Wild("AI::SetAutomaticTargets", (inr, ai, _) =>
        {
            if (ai == null)
            {
                Usage(inr, kAutoUsage);
                return "False";
            }
            ai.SetAutomaticTargets();
            return "True";
        });
        Wild("AI::SetScriptedTargets", (inr, ai, _) =>
        {
            if (ai == null)
            {
                Usage(inr, kScriptedUsage);
                return "False";
            }
            ai.SetScriptedTargets();
            return "True";
        });

        // ---- BaseRep::getFirst() / getNext(id): the player list with AI (0x0043e474, 0x0043e4d8) ----
        interp.AddCommand("BaseRep::getFirst", (inr, a) =>
        {
            if (a.Length != 1)
            {
                inr.Print(1, "BaseRep::getFirst()");
                inr.Print(1, "Like Client::getFirst(), but AI 'reps' are included.");
                return "-1";
            }
            List<int> ids = g.Players.PlayerIds();
            return ids.Count == 0 ? "-1" : Numbers.FormatInt(ids[0]);
        });
        interp.AddCommand("BaseRep::getNext", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "BaseRep::getNext( <previous base rep id> );");
                inr.Print(1, "Like Client::getNext(), but AI 'reps' are included.");
                return "-1";
            }
            List<int> ids = g.Players.PlayerIds();
            var id = Numbers.BorlandAtoi(a[1]);
            for (var i = 0; i < ids.Count; ++i)
                if (ids[i] == id)
                    return i + 1 < ids.Count ? Numbers.FormatInt(ids[i + 1]) : "-1";
            return "-1";
        });
    }
}
