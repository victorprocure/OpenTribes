// Port of src/game/player_commands.cpp: script commands of the spawn-to-play loop (docs/player-control.md 6,
// docs/player-sim.md 1.1, 1.5, 5.6, 6.2-6.5, 7.6, docs/player-manager.md 12.4): spawnPlayer, the Player:: commands.
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

/// <summary>The script commands of the spawn-to-play loop: spawnPlayer and the Player commands.</summary>
/// <remarks>See docs/player-control.md 6, docs/player-sim.md 1.1, 1.5, 5.6, 6.2 to 6.5, 7.6 and docs/player-manager.md 12.4.</remarks>
public static class PlayerCommands
{
    // src/game/player_commands.cpp serverOf.
    private static SimManager? ServerOf(GameState game) => game.Sim?.Server();

    // src/game/player_commands.cpp findPlayer: findServerPlayer (0x0041bfd8): a server object cast to Player, else a
    // client id naming the client's owned object.
    private static Player? FindPlayer(GameState game, string arg)
    {
        SimManager? m = ServerOf(game);
        if (m == null)
            return null;
        if (Player.From(m.Find(arg)) is Player p)
            return p;
        ClientRecord? r = game.Players.Find(Numbers.BorlandAtoi(arg));
        return r != null && r.OwnedObject != 0 ? Player.From(m.FindById(r.OwnedObject)) : null;
    }

    // src/game/player_commands.cpp itemIndex: ItemData_indexFromArg (0x004197a4): a leading digit means atoi within the
    // item count, otherwise an ItemData block name. -1 when unknown.
    private static int ItemIndex(GameState game, string arg)
    {
        if (game.Sim == null)
            return -1;
        int i = 0;
        while (i < arg.Length && (arg[i] == ' ' || arg[i] == '\t' || arg[i] == '\n' || arg[i] == '\r'))
            ++i;
        DataBlockManager db = game.Sim.DataBlocks();
        if (i < arg.Length && arg[i] >= '0' && arg[i] <= '9')
        {
            int n = Numbers.BorlandAtoi(arg[i..]);
            return n >= 0 && n < db.GroupSize(5) ? n : -1;
        }
        DataBlockManager.Block? b = db.Find(arg[i..]);
        return b != null && b.Group == 5 ? b.Index : -1;
    }

    // src/game/player_commands.cpp playerAndItem: Player and item resolver (0x00420728) with its two messages.
    private static bool PlayerAndItem(Interpreter interp, GameState game, string[] a, out Player? p, out int item)
    {
        item = -1;
        p = FindPlayer(game, a[1]);
        if (p == null)
        {
            interp.Print(1, a[0] + ": could not find player");
            return false;
        }
        item = ItemIndex(game, a[2]);
        if (item < 0)
        {
            if (Numbers.BorlandAtoi(a[2]) != -1)
                interp.Print(1, a[0] + ": incorrect item type: " + a[2]);
            return false;
        }
        return true;
    }

    // src/game/player_commands.cpp itemName.
    private static string? ItemName(GameState game, int type)
    {
        DataBlockManager.Block? b = game.Sim != null && type >= 0 ? game.Sim.DataBlocks().At(5, type) : null;
        return b?.Name;
    }

    // src/game/player_commands.cpp scan3: sscanf "%f %f %f" into a zeroed vector.
    private static float[] Scan3(string s)
    {
        float[] v = { 0, 0, 0 };
        CScan.Floats(s, v);
        return v;
    }

    private sealed record Reader(string Name, string? Usage, string BadArgc, string NotFound, Func<Player, string> Read);
    private sealed record CountCmd(string Name, string Usage, int Mode); // 0 get, 1 set, 2 inc, 3 dec
    private sealed record SlotCmd(string Name, string? Usage, string Fail, int Kind);
    private sealed record UseCmd(string Name, string Usage, string Event);

    /// <summary>Registers the player commands.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="g">The game state the commands act on.</param>
    public static void Register(Interpreter interp, GameState g)
    {
        // spawnPlayer(armor, pos, rot) 0x0043a1d0.
        interp.AddCommand("spawnPlayer", (_, a) =>
        {
            SimManager? m = ServerOf(g);
            if (m == null || a.Length != 4)
                return "-1";
            float[] pos = Scan3(a[2]), rot = Scan3(a[3]);
            var p = new Player(g);
            p.SetRotation(rot);
            p.SetPosition(pos);
            p.DataBlockName = StringTable.Global().Intern(a[1]);
            if (!m.RegisterObject(p))
                return "-1";
            if (m.Find("MissionCleanup")?.AsSet() is SimSet set)
                set.AddObject(p);
            return Numbers.FormatInt(p.Id());
        });

        // ---- armor, animation, state reads ----
        interp.AddCommand("Player::setArmor", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(player,armor)");
                return "False";
            }
            if (FindPlayer(g, a[1]) is Player p)
            {
                p.SetArmor(a[2]);
                return "True";
            }
            return "False";
        });
        interp.AddCommand("Player::getArmor", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(player)");
                return "False";
            }
            if (FindPlayer(g, a[1]) is Player p)
                return p.DataBlockName ?? "";
            return "False";
        });
        interp.AddCommand("Player::setAnimation", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(player,anim)");
                return "False";
            }
            if (FindPlayer(g, a[1]) is Player p)
                p.SetAnimation(Numbers.BorlandAtoi(a[2]));
            return "True";
        });

        // Simple readers: name, usage suffix, wrong-argc result, not-found result.
        Reader[] kReaders =
        {
            new("Player::isDead", "isDead(player)", "False", "False", p => p.Dead ? "True" : "False"),
            new("Player::isJetting", null, "False", "False", p => p.Jetting ? "True" : "False"),
            new("Player::isCrouching", null, "-1", "-1", p => p.Crouching ? "True" : "False"),
            new("Player::isAiControlled", null, "False", "False", p => p.AiControlled ? "True" : "False"),
            new("Player::isExposed", "isExposed(player)", "False", "False", p =>
            {
                // 0x00421180: no mount object, or the mount object's vtable +0x164 (Vehicle: visibleDriver).
                GameBase? m = p.MountObject();
                Vehicle? v = Vehicle.From(m);
                return m == null || v == null || v.Data.VisibleDriver ? "True" : "False";
            }),
            new("Player::lastJetTime", null, "0", "0", p => Numbers.FormatInt(unchecked((int)p.LastJetTime))),
            new("Player::getLastContactCount", null, "-1", "-1", p => Numbers.FormatInt(p.TicksSinceContact)),
            new("Player::getDamageFlash", null, "0", "0", p => Numbers.FormatFloat(p.Flash)),
            new("Player::getSensorSupression", "getSensorSupression(player)", "0", "0", p => Numbers.FormatFloat(p.SensorSuppression)),
            new("Player::getMountObject", "Player::getMountObject(Player)", "False", "-1",
                p => Numbers.FormatInt(p.MountId != 0 ? p.MountId : -1)),
        };
        foreach (Reader rd in kReaders)
            interp.AddCommand(rd.Name, (inr, a) =>
            {
                if (a.Length != 2)
                {
                    inr.Print(1, "wrong argcount to " + (rd.Usage ?? a[0] + "(player)"));
                    return rd.BadArgc;
                }
                Player? p = FindPlayer(g, a[1]);
                return p != null ? rd.Read(p) : rd.NotFound;
            });

        interp.AddCommand("Player::kill", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to kill(player)");
                return "False";
            }
            if (FindPlayer(g, a[1]) is Player p)
            {
                p.Kill();
                return "True";
            }
            return "False";
        });
        interp.AddCommand("Player::blowUp", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(player)");
                return "True";
            }
            if (FindPlayer(g, a[1]) is Player p)
                p.BlownUp = true;
            return "True";
        });
        interp.AddCommand("Player::setDamageFlash", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(player,value)");
                return "False";
            }
            if (FindPlayer(g, a[1]) is Player p)
            {
                float v = Store(Numbers.BorlandAtof(a[2]));
                p.Flash = v < 0.0f ? 0.0f : v > 1.0f ? 1.0f : v;
                return "True";
            }
            return "False";
        });
        interp.AddCommand("Player::setSensorSupression", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "wrong argcount to setSensorSupression(player,value)");
                return "False";
            }
            if (FindPlayer(g, a[1]) is Player p)
            {
                // 0x0040fc88: unlink (also a player that is not linked), link after the head when r > 0, store r.
                float v = Store(Numbers.BorlandAtof(a[2]));
                g.SensorJam(p, false);
                if (0.0f < v)
                    g.SensorJam(p, true);
                p.SensorSuppression = v;
                return "True";
            }
            return "False";
        });
        interp.AddCommand("Player::setDetectParameters", (_, a) =>
        {
            Player? p = a.Length == 4 ? FindPlayer(g, a[1]) : null;
            if (p != null)
            {
                p.DetectHazeFactor = Store(Numbers.BorlandAtof(a[2]));
                p.DetectMinVisibility = Store(Numbers.BorlandAtof(a[3]));
                return "True";
            }
            return "False";
        });
        interp.AddCommand("Player::applyImpulse", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "wrong argcount to applyImpulse(player,vec)");
                return "False";
            }
            if (FindPlayer(g, a[1]) is Player p)
            {
                // 0x00504b5c: v = f32(vec * invMass + v), at-rest bit cleared.
                using var chop = X87.Chop();
                float[] vec = Scan3(a[2]);
                for (int i = 0; i < 3; ++i)
                    p.Velocity[i] = X87.F32(Add(Mul(vec[i], p.InvMass), p.Velocity[i]));
                p.AtRest = false;
            }
            return "False"; // always
        });
        interp.AddCommand("Player::setMountObject", (inr, a) =>
        {
            if (a.Length != 4)
            {
                inr.Print(1, "wrong argcount to mountObject(Player, Object, mountPoint)");
                return "False";
            }
            // 0x00420e7c: a missing player still answers True; the object is a server GameBase (none clears).
            if (FindPlayer(g, a[1]) is Player p)
                p.SetMountObject(g.Players.FindServerObject(a[2]), Numbers.BorlandAtoi(a[3]));
            return "True";
        });

        // ---- inventory (7.1, 7.6) ----
        CountCmd[] kCounts =
        {
            new("Player::getItemCount", "getItemCount(player,type)", 0),
            new("Player::setItemCount", "setItemCount(player,type,count)", 1),
            new("Player::incItemCount", "incItemCount(player,type,<count>)", 2),
            new("Player::decItemCount", "decItemCount(player,type,<count>)", 3),
        };
        foreach (CountCmd c in kCounts)
            interp.AddCommand(c.Name, (inr, a) =>
            {
                bool ok = c.Mode == 0 ? a.Length == 3 : c.Mode == 1 ? a.Length == 4 : a.Length == 3 || a.Length == 4;
                if (!ok)
                {
                    inr.Print(1, "wrong argcount to " + c.Usage);
                    return "0";
                }
                if (!PlayerAndItem(inr, g, a, out Player? p, out int item))
                    return "0";
                int n = a.Length == 4 ? Numbers.BorlandAtoi(a[3]) : 1;
                int cur = p!.ItemCountOf(item);
                // Counts are 32-bit sums that wrap like the original's add.
                int next = c.Mode == 0 ? cur : c.Mode == 1 ? n : c.Mode == 2 ? unchecked(cur + n) : unchecked(cur - n);
                return Numbers.FormatInt(c.Mode == 0 ? cur : p.SetItemCount(item, next));
            });
        interp.AddCommand("Player::getItemClassCount", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "wrong argcount to getItemClassCount(player,className)");
                return "0";
            }
            Player? p = FindPlayer(g, a[1]);
            if (p == null || g.Sim == null)
                return "0";
            DataBlockManager db = g.Sim.DataBlocks();
            int n = 0;
            for (int t = 0; t < Player.kItemTypes && t < db.GroupSize(5); ++t)
                if (p.Counts[t] != 0 && db.GetField(db.At(5, t)!, "className", "", out string cls) &&
                    GameUtil.EqualsNoCase(cls, a[2]))
                    ++n;
            return Numbers.FormatInt(n);
        });
        interp.AddCommand("Player::mountItem", (inr, a) =>
        {
            if (a.Length != 4 && a.Length != 5)
            {
                inr.Print(1, "wrong argcount to mountItem(player,type,imageSlot,<team>)");
                return "False";
            }
            if (!PlayerAndItem(inr, g, a, out Player? p, out int item))
                return "False";
            int slot = Numbers.BorlandAtoi(a[3]);
            if (slot < 0 || slot >= Player.kImageSlots)
            {
                inr.Print(1, "mountItem: invalid imageSlot");
                return "False";
            }
            return p!.MountItem(item, slot, a.Length == 5 ? Numbers.BorlandAtoi(a[4]) : -1) ? "True" : "False";
        });
        // Slot commands: name, usage, wrong-argc / not-found / bad-slot result.
        SlotCmd[] kSlots =
        {
            new("Player::unmountItem", "unmountItem", "False", 0),
            new("Player::getMountedItem", null, "-1", 1),
            new("Player::getNextMountedItem", null, "-1", 2),
            new("Player::getItemState", "getItemState", "False", 3),
            new("Player::isTriggered", "isTriggered", "False", 4),
        };
        foreach (SlotCmd c in kSlots)
            interp.AddCommand(c.Name, (inr, a) =>
            {
                string r = c.Fail;
                string label = c.Usage ?? a[0];
                if (a.Length != 3)
                {
                    inr.Print(1, "wrong argcount to " + label + "(player,imageSlot)");
                    return r;
                }
                Player? p = FindPlayer(g, a[1]);
                if (p == null)
                    return r;
                int slot = Numbers.BorlandAtoi(a[2]);
                if (slot < 0 || slot >= Player.kImageSlots)
                {
                    inr.Print(1, label + ": invalid imageSlot");
                    return r;
                }
                Player.ImageSlot s = p.Images[slot];
                switch (c.Kind)
                {
                    case 0:
                        r = p.UnmountItem(slot) ? "True" : "False";
                        break;
                    case 1:
                        if (ItemName(g, s.Item) is string name)
                            r = name;
                        break;
                    case 2:
                        r = "False"; // no pending item: the null name prints False [live]
                        if (ItemName(g, s.NextItem) is string next)
                            r = next;
                        break;
                    case 3:
                        r = Player.ImageStateName(s.Image == -1 ? 0 : s.State);
                        break;
                    default:
                        r = s.Triggered ? "True" : "False";
                        break;
                }
                return r;
            });
        interp.AddCommand("Player::trigger", (inr, a) =>
        {
            if (a.Length != 3 && a.Length != 4)
            {
                inr.Print(1, "wrong argcount to trigger(player,imageSlot,<bool>)");
                return "False";
            }
            Player? p = FindPlayer(g, a[1]);
            if (p == null)
                return "False";
            int slot = Numbers.BorlandAtoi(a[2]);
            if (slot < 0 || slot >= Player.kImageSlots)
            {
                inr.Print(1, "trigger: invalid imageSlot");
                return "False";
            }
            p.TriggerImage(slot, a.Length == 4 ? Numbers.ParseBool(a[3]) : !p.Images[slot].Triggered);
            return "True";
        });
        UseCmd[] kUses =
        {
            new("Player::useItem", "useItem(player,type)", "onUse"),
            new("Player::dropItem", "dropItem(player,type)", "onDrop"),
            new("Player::deployItem", "deployItem(player,type)", "onDeploy"),
        };
        foreach (UseCmd c in kUses)
            interp.AddCommand(c.Name, (inr, a) =>
            {
                if (a.Length != 3)
                {
                    inr.Print(1, "wrong argcount to " + c.Usage);
                    return "False";
                }
                if (!PlayerAndItem(inr, g, a, out Player? p, out int item))
                    return "False";
                if (p!.ItemCountOf(item) != 0)
                    p.ItemCallback(item, c.Event, c.Event[2] == 'D' && c.Event[3] == 'e' ? "0.000000,0.000000,0.000000" : null);
                return "True";
            });
        // getNumItems: src/game/util_commands.cpp.
        // Client item shopping and buying masks: src/game/backlog_commands.cpp.
        // GameBase::getEnergy, setEnergy, startFadeOut, startFadeIn: src/game/static_commands.cpp (every ShapeBase,
        // Players included).
    }
}
