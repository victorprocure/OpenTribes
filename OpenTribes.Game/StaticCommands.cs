// Port of src/game/static_commands.cpp: script commands of the non-player objects (docs/static-sim.md 8): GameBase
// state, power, damage, energy, threads, Moveable, targets, Item and fade. Argument-count texts and the failure returns
// are those of the registered handlers. The projectile commands are ProjectileFire.RegisterCommands.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>The script commands of the non-player objects: GameBase state, power, damage, energy, threads, Moveable, targets, Item and fade.</summary>
/// <remarks>Argument-count texts and failure returns are those of the registered handlers (docs/static-sim.md 8). The projectile commands are in <see cref="ProjectileFire"/>.</remarks>
public static class StaticCommands
{
    /// <summary>Registers the non-player object commands.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="game">The game state the commands act on.</param>
    public static void Register(Interpreter interp, GameState game)
    {
        // "GB": 0x00422d18, findObject cast to GameBase, else a client id naming its owned object.
        GameBase? FindGB(string s) => game.Players.FindServerObject(s);
        // findObject in the server manager, no client ids.
        SimObject? FindPlain(string s) => game.Sim?.Server()?.Find(s);
        // "SB": 0x0041bf38, a ShapeBase (a StaticBase class here).
        GameBase? FindSB(string s) => FindGB(s) is GameBase gb && gb.IsShapeBase() ? gb : null;
        // "ST": findObject cast to StaticBase (0x0041bf88).
        GameBase? FindST(string s) => FindPlain(s) is GameBase gb && gb.IsStaticBase() ? gb : null;
        GameBase? FindItem(string s) => FindPlain(s) is GameBase gb && gb.ShapeClass() == GameBase.ShapeKind.Item ? gb : null;

        // A command with a fixed argument count: the wrong count prints `usage` (with the called name for a "%s" in it)
        // and returns `bad`; otherwise body runs with the result preset to `fail`.
        void Def(string name, int argc, string usage, string bad, string fail, Func<Interpreter, string[], string, string> body) =>
            interp.AddCommand(name, (inr, a) =>
            {
                if (a.Length != argc)
                {
                    if (usage.Length != 0)
                    {
                        var at = usage.IndexOf("%s", StringComparison.Ordinal);
                        inr.Print(1, at >= 0 ? usage[..at] + a[0] + usage[(at + 2)..] : usage);
                    }
                    return bad;
                }
                return body(inr, a, fail);
            });

        // ---- power (1.6) ----
        Def("GameBase::generatePower", 3, "wrong argcount to generatePower(object,bool)", "False", "False", (_, a, r) =>
        {
            if (FindGB(a[1]) is GameBase gb)
            {
                gb.GeneratePower(Numbers.ParseBool(a[2]));
                return "True";
            }
            return r;
        });
        Def("GameBase::isPowerGenerator", 2, "wrong argcount to isPowerGenerator(object)", "False", "False", (_, a, r) =>
            FindGB(a[1]) is GameBase gb && gb.PowerGenerator ? "True" : r);
        Def("GameBase::getPowerCount", 2, "wrong argcount to getPowerCount(object)", "0", "0", (_, a, r) =>
            FindGB(a[1]) is GameBase gb ? Numbers.FormatInt(gb.PowerCount) : r);
        Def("GameBase::isPowered", 2, "wrong argcount to isPowered(object)", "False", "False", (_, a, r) =>
            FindGB(a[1]) is GameBase gb && gb.PowerCount != 0 ? "True" : r);

        // ---- active flag and target flag (1.5, 6.4) ----
        Def("GameBase::setActive", 3, "wrong argcount to %s(sensor,bool)", "False", "False", (_, a, r) =>
            FindGB(a[1]) is GameBase gb && gb.SetActive(Numbers.ParseBool(a[2])) ? "True" : r);
        Def("GameBase::isActive", 2, "wrong argcount to %s(sensor)", "False", "False", (_, a, r) =>
            FindGB(a[1]) is GameBase gb && gb.IsActiveFlag() ? "True" : r);
        Def("GameBase::setIsTarget", 3, "wrong argcount to %s(staticbase, bool)", "False", "False", (_, a, r) =>
        {
            if (FindST(a[1]) is GameBase gb)
            {
                var on = Numbers.ParseBool(a[2]);
                gb.SetIsTarget(on);
                if (on)
                    return "True";
            }
            return r;
        });

        // ---- damage (1.2) ----
        Def("GameBase::getDamageLevel", 2, "wrong argcount to %s(object)", "0", "0", (_, a, r) =>
            FindSB(a[1]) is GameBase gb ? Numbers.FormatFloat(gb.Damage) : r);
        Def("GameBase::setDamageLevel", 3, "wrong argcount to setDamageLevel(object,value)", "False", "False", (_, a, r) =>
        {
            // vtable +0x190: StaticBase and Player (0x004111c4) have their own versions.
            if (FindSB(a[1]) is GameBase gb && (gb.IsStaticBase() || Player.From(gb) != null))
            {
                gb.SetDamageLevel(Store(Numbers.BorlandAtof(a[2])));
                return "True";
            }
            return r;
        });
        Def("GameBase::getDamageState", 2, "wrong argcount to %s(object)", "0", "0", (_, a, r) =>
            FindSB(a[1]) is GameBase gb ? (gb.DamageState == 1 ? "Enabled" : gb.DamageState == 2 ? "Disabled" : "Destroyed") : r);
        Def("GameBase::getDisabledDamage", 2, "wrong argcount to %s(object)", "false", "false", (_, a, r) =>
            FindST(a[1]) is GameBase gb ? Numbers.FormatFloat(Store(Mul(gb.MaxDamage, gb.DisabledLevel))) : r);
        Def("GameBase::repairDamage", 3, "wrong argcount to repairDamage(object,amount)", "False", "False", (_, a, r) =>
        {
            if (FindSB(a[1]) is GameBase gb)
            {
                gb.RepairDamage(Store(Numbers.BorlandAtof(a[2])));
                return "True";
            }
            return r;
        });
        Def("GameBase::setAutoRepairRate", 3, "wrong argcount to setAutoRepairRate(object,value)", "False", "False", (_, a, r) =>
        {
            if (FindSB(a[1]) is GameBase gb)
            {
                gb.AutoRepairRate = Store(Numbers.BorlandAtof(a[2]));
                return "True";
            }
            return r;
        });
        Def("GameBase::getAutoRepairRate", 2, "wrong argcount to getAutoRepairRate(object)", "0", "0", (_, a, r) =>
            FindSB(a[1]) is GameBase gb ? Numbers.FormatFloat(gb.AutoRepairRate) : r);
        // ---- energy (1.4) ----
        Def("GameBase::setEnergy", 3, "wrong argcount to setEnergy(object,value)", "False", "False", (_, a, r) =>
        {
            if (FindSB(a[1]) is GameBase gb)
            {
                gb.SetEnergy(Store(Numbers.BorlandAtof(a[2])));
                return "True";
            }
            return r;
        });
        Def("GameBase::getEnergy", 2, "wrong argcount to getEnergy(object)", "0", "0", (_, a, r) =>
            FindSB(a[1]) is GameBase gb ? Numbers.FormatFloat(gb.Energy) : r);
        Def("GameBase::setRechargeRate", 3, "wrong argcount to setRechargeRate(object,value)", "False", "False", (_, a, r) =>
        {
            if (FindSB(a[1]) is GameBase gb)
            {
                gb.RechargeRate = Store(Numbers.BorlandAtof(a[2]));
                gb.SetMaskBits(0x02);
                return "True";
            }
            return r;
        });
        Def("GameBase::getRechargeRate", 2, "wrong argcount to getRechargeRate(object)", "0", "0", (_, a, r) =>
            FindSB(a[1]) is GameBase gb ? Numbers.FormatFloat(gb.RechargeRate) : r);
        interp.AddCommand("GameBase::activateShield", (inr, a) =>
        {
            if (a.Length is not 3 and not 4)
            {
                inr.Print(1, "wrong argcount to activateShield(object,vec,[zOffset])");
                return "False";
            }
            if (FindSB(a[1]) is GameBase gb)
            {
                var v = new float[3];
                GameUtil.Scan3(a[2], v);
                gb.ActivateShield(v, a.Length == 4 ? Store(Numbers.BorlandAtof(a[3])) : 0.0f);
                return "True";
            }
            return "False";
        });
        Def("GameBase::getRadius", 2, "wrong argcount to %s(ObjectId)", "false", "false", (_, a, r) =>
            // A vehicle keeps a collision sphere of a quarter of the shape radius (docs/vehicle-sim.md 1.4).
            FindSB(a[1]) is GameBase gb
                ? Numbers.FormatFloat(gb.ShapeClass() == GameBase.ShapeKind.Vehicle ? MulF(gb.ShapeRadius, 4.0f) : gb.ShapeRadius)
                : r);
        interp.AddCommand("activateGroup", (inr, a) =>
        {
            if (a.Length is not 2 and not 3)
            {
                // The usage text is a printf format with "%o" in "%object1": the original prints the octal of a stale
                // stack slot there. It read 212652310 in every context tried [live]; D12 applies.
                inr.Print(1, "wrong argcount to " + a[0] + "(212652310bject1,<value>)");
                return "False";
            }
            // Event 0x420 with the value (default 0) goes to every member of the set, or of the group the object is in,
            // except the object itself (0x0041e704). Only SimLight handles it.
            var obj = FindPlain(a[1]);
            if (obj == null)
                return "False";
            var set = obj.AsSet() ?? obj.Group();
            if (set == null)
                return "True";
            var value = a.Length > 2 ? Store(Numbers.BorlandAtof(a[2])) : 0.0f;
            var members = new List<SimObject>(set.Members());
            foreach (var m in members)
                if (m != obj)
                    m.OnActivateEvent(value);
            return "True";
        });

        // ---- threads (1.7) ----
        static bool SlotOf(string s, out int slot)
        {
            slot = Numbers.BorlandAtoi(s);
            return slot is >= 0 and < 3;
        }
        interp.AddCommand("GameBase::playSequence", (inr, a) =>
        {
            if (a.Length is not 3 and not 4)
            {
                inr.Print(1, "wrong argcount to playSequence(object, thread, <sequence>)");
                return "False";
            }
            var gb = FindSB(a[1]);
            if (gb == null || !SlotOf(a[2], out var slot))
                return "False";
            if (a.Length == 4)
            {
                var seq = gb.Anim != null ? gb.Anim.FindSequence(a[3]) : -1;
                if (seq != -1 && gb.PlayThread(slot, seq, true))
                    return "True";
            }
            else if (gb.ResumeThread(slot))
            {
                return "True";
            }
            return "False";
        });
        Def("GameBase::stopSequence", 3, "wrong argcount to stopSequence(object, thread)", "False", "False", (_, a, r) =>
            FindSB(a[1]) is GameBase gb && SlotOf(a[2], out var slot) && gb.StopThread(slot) ? "True" : r);
        Def("GameBase::pauseSequence", 3, "wrong argcount to pauseSequence(object, thread)", "False", "False", (_, a, r) =>
            FindSB(a[1]) is GameBase gb && SlotOf(a[2], out var slot) && gb.PauseThread(slot) ? "True" : r);
        Def("GameBase::setSequenceDirection", 4, "wrong argcount to setSequenceDirection(object, thread, bool forward)", "False",
            "False", (_, a, r) =>
                FindSB(a[1]) is GameBase gb && SlotOf(a[2], out var slot) && gb.SetThreadDirection(slot, Numbers.ParseBool(a[3])) ? "True" : r);

        // ---- Moveable (2.6, 8.2) ----
        GameBase? MoveableOf(string s) => FindPlain(s) is GameBase gb && gb.ShapeClass() == GameBase.ShapeKind.Moveable ? gb : null;
        Def("Moveable::moveToWaypoint", 3, "wrong argcount to %s(object,waypoint)", "False", "False", (_, a, r) =>
            MoveableOf(a[1]) is GameBase gb && gb.MoveToWaypoint(Numbers.BorlandAtoi(a[2])) ? "True" : r);
        foreach ((var name, var state) in new[]
                 {
                     ("Moveable::moveForward", GameBase.kMoveForward), ("Moveable::moveBackward", GameBase.kMoveBackward),
                     ("Moveable::stop", GameBase.kMoveStopped),
                 })
            Def(name, 2, "wrong argcount to %s(object)", "False", "False", (_, a, r) =>
            {
                if (MoveableOf(a[1]) is GameBase gb)
                {
                    gb.MoveForward(state);
                    return "True";
                }
                return r;
            });
        Def("Moveable::getState", 2, "wrong argcount to %s(object)", "False", "False", (_, a, r) =>
        {
            if (MoveableOf(a[1]) is GameBase gb)
            {
                string[] kStates = ["Undefined", "MoveForward", "MoveBackward", "Stop"];
                return gb.MoveState is >= 0 and <= 3 ? kStates[gb.MoveState] : "";
            }
            return r;
        });
        Def("Moveable::getWaypointCount", 2, "wrong argcount to %s(object)", "0", "0", (_, a, r) =>
            MoveableOf(a[1]) is GameBase gb ? Numbers.FormatInt(gb.WaypointCount) : r);
        Def("Moveable::getPosition", 2, "wrong argcount to %s(object)", "0", "0", (_, a, r) =>
            MoveableOf(a[1]) is GameBase gb ? Numbers.FormatFloat(gb.MoveablePosition()) : r);
        Def("Moveable::setWaypoint", 3, "wrong argcount to %s(object,%wayPoint)", "False", "False", (_, a, r) =>
        {
            if (MoveableOf(a[1]) is GameBase gb)
            {
                gb.SetWaypoint(Numbers.BorlandAtoi(a[2]));
                return "True";
            }
            return r;
        });

        // ---- sensor targets (3.3, 8.3) ----
        interp.AddCommand("Turret::setTarget", (inr, a) =>
        {
            // 0x00421590: the turret and the Player are found like any script object argument (an id, a name or a client
            // id). The Player becomes the preferred target (4.4), never cleared.
            if (a.Length != 3)
            {
                inr.Print(1, "wrong argcount to setTarget (Turret, Player)");
                return "False";
            }
            var turret = game.Players.FindServerObject(a[1]);
            var player = game.Players.FindServerObject(a[2]);
            if (turret != null && turret.ShapeClass() == GameBase.ShapeKind.Turret && player != null && Player.From(player) != null)
            {
                turret.TurretPreferred = player.Id();
                return "True";
            }
            return "False";
        });

        // ---- Item and fade (5, 8.4) ----
        Def("Item::getCount", 2, "wrong argcount to getObjectItemCount(item)", "-1", "-1", (inr, a, r) =>
        {
            var gb = FindItem(a[1]);
            if (gb == null)
            {
                inr.Print(1, a[0] + ": could not find item");
                return "0";
            }
            return Numbers.FormatInt(gb.ItemCount);
        });
        Def("Item::isRotating", 2, "wrong argcount to isRotatingItem(item)", "False", "False", (inr, a, r) =>
        {
            var gb = FindItem(a[1]);
            if (gb == null)
                inr.Print(1, a[0] + ": could not find item");
            else if (gb.ItemRotates)
                return "True";
            return r;
        });
        Def("Item::hide", 3, "wrong argcount to hideItem(item,bool)", "False", "False", (inr, a, r) =>
        {
            var gb = FindItem(a[1]);
            if (gb == null)
            {
                inr.Print(1, a[0] + ": could not find item (" + a[1] + ")");
                return r;
            }
            gb.ItemHide(Numbers.ParseBool(a[2]));
            return "True";
        });
        interp.AddCommand("Item::setVelocity", (_, a) =>
        {
            if (a.Length != 3)
                return "False";
            if (FindGB(a[1]) is GameBase gb)
            {
                GameUtil.Scan3(a[2], gb.Velocity);
                gb.ResetItemRest();
                return "True";
            }
            return "False";
        });
        interp.AddCommand("Item::getVelocity", (_, a) =>
        {
            if (a.Length != 2)
                return "0 0 0";
            return FindGB(a[1]) is GameBase gb ? GameUtil.Fmt3(gb.Velocity) : "0 0 0";
        });
        Def("GameBase::throw", 5, "wrong argcount to throwObject(item,object,vel,bool random)", "False", "False", (inr, a, r) =>
        {
            var item = FindGB(a[1]);
            if (item == null)
            {
                inr.Print(1, a[0] + ": could not find item to throw");
                return r;
            }
            var thrower = FindGB(a[2]);
            if (thrower == null)
            {
                inr.Print(1, a[0] + ": could not find object");
                return r;
            }
            if (item.ShapeClass() == GameBase.ShapeKind.Item || item.ShapeClass() == GameBase.ShapeKind.Mine)
                item.ItemThrow(thrower, Store(Numbers.BorlandAtof(a[3])), Numbers.ParseBool(a[4]));
            // The join counter of the throwing client's record is stored with it (0x00451284, read at 0x004524f8).
            if (item.ShapeClass() == GameBase.ShapeKind.Mine || item.ShapeClass() == GameBase.ShapeKind.Item)
            {
                var rec = item.ItemThrowerClient > 0 ? game.Players.Find(item.ItemThrowerClient) : null;
                item.ItemThrowerJoin = rec != null ? rec.JoinOrder : -1;
            }
            return "True";
        });
        Def("GameBase::isAtRest", 2, "wrong argcount to %s(object)", "False", "False", (_, a, r) =>
            FindGB(a[1]) is GameBase gb && gb.AtRest ? "True" : r);
        Def("GameBase::testPosition", 3, "wrong argcount to %s(object,pos)", "False", "False", (_, a, r) =>
        {
            if (FindGB(a[1]) is not GameBase gb)
                return r;
            var p = new float[3];
            GameUtil.Scan3(a[2], p);
            if (Player.From(gb) is Player pl)
                return pl.TestPositionAt(p) ? "True" : r;
            return gb.TestPosition(p) ? "True" : r;
        });
        foreach (var name in new[] { "GameBase::startFadeOut", "GameBase::startFadeIn" })
        {
            var fadeOut = name == "GameBase::startFadeOut";
            interp.AddCommand(name, (_, a) =>
            {
                if (a.Length != 2)
                    return "False";
                if (FindSB(a[1]) is GameBase gb)
                {
                    gb.StartFade(fadeOut ? 1 : 2);
                    return "True";
                }
                return "False";
            });
        }
        DamageCommands.RegisterCommands(interp, game);
        ProjectileFire.RegisterCommands(interp, game);
    }
}
