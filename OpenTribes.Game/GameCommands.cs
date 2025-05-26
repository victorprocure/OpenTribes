// Port of registerGameCommands of src/game/game_commands.cpp: the game commands (Group, datablocks, line of sight,
// containers, teams, the GameBase family). The Player and Vehicle commands are PlayerCommands and Vehicle; the AI
// commands (ai_commands.cpp) are AICommands.
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

public static class GameCommands
{
    // src/game/game_commands.cpp findObject.
    private static SimObject? FindObject(GameState game, string s) => game.Sim?.Server()?.Find(s);

    // src/game/game_commands.cpp findGameBase: 0x00422d18, findObject cast to GameBase, else a client id naming its owned
    // object. GameBase::getTeam and setTeam resolve the object themselves (owned = false).
    private static GameBase? FindGameBase(GameState game, string s, bool owned = true) =>
        owned ? game.Players.FindServerObject(s) : FindObject(game, s) as GameBase;

    // src/game/game_commands.cpp iterate: preorder over members, descending into groups (0x0041c2b0, iterator 0x0050fc2c).
    private static void Iterate(Interpreter interp, SimSet g, string[] a)
    {
        var members = new List<SimObject>(g.Members());
        foreach (SimObject m in members)
        {
            var args = new List<string> { Numbers.FormatInt(m.Id()) };
            args.AddRange(a.Skip(3));
            interp.CallFunction(a[2], args);
            if (m.AsSet() is SimSet child)
                Iterate(interp, child, a);
        }
    }

    // src/game/game_commands.cpp registerGameCommands.
    public static void Register(Interpreter interp, GameState game)
    {
        ClientGameCommands.Register(interp, game); // chat menus (docs/mods.md 5.5)
        UtilCommands.Register(interp, game);
        interp.AddCommand("Group::objectCount", (_, a) =>
        {
            SimObject? obj = a.Length == 2 ? FindObject(game, a[1]) : null;
            return obj?.AsSet() is SimSet s ? Numbers.FormatInt(s.Members().Count) : "0";
        });
        interp.AddCommand("Group::getObject", (_, a) =>
        {
            SimObject? obj = a.Length == 3 ? FindObject(game, a[1]) : null;
            if (obj?.AsSet() is not SimSet set)
                return "-1";
            int i = Numbers.BorlandAtoi(a[2]);
            return i >= 0 && i < set.Members().Count ? Numbers.FormatInt(set.Members()[i].Id()) : "-1";
        });
        interp.AddCommand("Group::iterateRecursive", (inr, a) =>
        {
            SimObject? obj = a.Length >= 3 ? FindObject(game, a[1]) : null;
            if (obj?.AsSet() is SimSet g)
                Iterate(inr, g, a);
            return "0";
        });
        // ---- datablock commands (docs/datablocks.md 8) ----
        interp.AddCommand("preloadServerDataBlocks", (_, a) =>
        {
            if (a.Length == 1 && game.Sim != null && game.Resources != null)
                return game.Sim.DataBlocks().Preload(game.Resources) ? "True" : "False";
            return "False";
        });
        interp.AddCommand("getItemData", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to getItemData(type)");
                return "False";
            }
            return game.Sim?.DataBlocks().At(5, Numbers.BorlandAtoi(a[1]))?.Name ?? "";
        });
        interp.AddCommand("setMaterialProperty", (_, a) =>
        {
            // 0x00425894: type name, friction, elasticity into the table of 0x006c1ff0 (E6.1).
            if (a.Length != 4)
                return "False";
            for (int i = 0; i < 15; ++i)
                if (GameUtil.EqualsNoCase(MaterialList.kMaterialTypeNames[i], a[1]))
                {
                    game.Materials.Friction[i] = Store(Numbers.BorlandAtof(a[2]));
                    game.Materials.Elasticity[i] = Store(Numbers.BorlandAtof(a[3]));
                    return "True";
                }
            return "False";
        });
        interp.AddCommand("GameBase::getDataName", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to GameBase::getDataName(object)");
                return "";
            }
            GameBase? gb = FindGameBase(game, a[1]);
            if (gb != null && gb.DataBlockName == null)
                return null; // NULL datablock name prints False (InteriorShape)
            return gb != null ? gb.DataBlockName : "";
        });
        interp.AddCommand("GameBase::virtual", (inr, a) =>
        {
            if (a.Length < 3)
            {
                inr.Print(1, "wrong argcount to GameBase::virtual(object,functionname,...)");
                return "";
            }
            GameBase? gb = FindGameBase(game, a[1]);
            if (gb == null)
                return "";
            string fn = game.VirtualFunction(gb, a[2]);
            if (fn.Length == 0)
                return "";
            var args = new List<string> { Numbers.FormatInt(gb.Id()) };
            args.AddRange(a.Skip(3));
            return inr.CallFunction(fn, args);
        });
        interp.AddCommand("Item::getItemData", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to Item::getItemData(item)");
                return "-1";
            }
            if (FindObject(game, a[1]) is not GameBase gb || gb.ClassName() != "Item")
            {
                inr.Print(1, "Item::getItemData: could not find item");
                return "-1";
            }
            return gb.DataBlockName ?? "";
        });
        interp.AddCommand("getNumTeams", (_, _) => Numbers.FormatInt(game.TeamCount));

        // ---- line of sight and containers (docs/interiors.md 10, docs/terrain.md 5) ----
        interp.AddCommand("getLOSInfo", (inr, a) =>
        {
            if (a.Length != 4)
            {
                inr.Print(1, "wrong argcount to getLOSInfo(start,end,detail)");
                return "False";
            }
            float[] s = { 0, 0, 0 }, e = { 0, 0, 0 };
            CScan.Floats(a[1], s);
            CScan.Floats(a[2], e);
            int bits = Numbers.BorlandAtoi(a[3]);
            uint[] kMap = { 0x6, 0x800000, 0x400000, 0x20000000, 0x80, 0x4000000, 0x200000 };
            uint mask = 0;
            for (int i = 0; i < 7; ++i)
                if ((bits & (1 << i)) != 0)
                    mask |= kMap[i];
            if (mask == 0)
                return "False";
            object? obj = null;
            Vec3 hit = default, n = default;
            if (!game.World.CastRay(new Vec3(s[0], s[1], s[2]), new Vec3(e[0], e[1], e[2]), mask, null, ref obj, ref hit, ref n))
                return "False";
            inr.SetVariable("$los::position", GameUtil.Fmt3(new[] { hit.X, hit.Y, hit.Z }));
            inr.SetVariable("$los::normal", GameUtil.Fmt3(new[] { n.X, n.Y, n.Z }));
            inr.SetVariable("$los::object", Numbers.FormatInt(((SimObject)obj!).Id()));
            return "True";
        });
        interp.AddCommand("getBoxCenter", (inr, a) =>
        {
            if (a.Length != 2)
                inr.Print(1, "wrong argcount to getBoxCenter(object)");
            SimObject? obj = a.Length == 2 ? FindObject(game, a[1]) : null;
            if (obj != null && game.World.Find(obj) is Container.Entry en)
            {
                float[] c = new float[3];
                for (int i = 0; i < 3; ++i)
                    c[i] = Store(Div(Add(en.Box[i], en.Box[i + 3]), 2));
                return GameUtil.Fmt3(c);
            }
            return "0 0 0";
        });
        interp.AddCommand("containerBoxFillSet", (inr, a) =>
        {
            if (a.Length != 7 && a.Length != 8)
            {
                inr.Print(1, "wrong argcount to containerBoxFillSet(set,mask,position,length,width,height,optional height);");
                return "False";
            }
            if (FindObject(game, a[1])?.AsSet() is not SimSet set)
                return "False";
            float[] c = { 0, 0, 0 };
            CScan.Floats(a[3], c);
            float[] half = { Store(Div(Numbers.BorlandAtof(a[4]), 2)), Store(Div(Numbers.BorlandAtof(a[5]), 2)), Store(Div(Numbers.BorlandAtof(a[6]), 2)) };
            float[] box =
            {
                SubF(c[0], half[0]), SubF(c[1], half[1]), SubF(c[2], half[2]), AddF(c[0], half[0]), AddF(c[1], half[1]), AddF(c[2], half[2]),
            };
            if (a.Length == 8 && Numbers.BorlandAtoi(a[7]) == 1)
            {
                box[2] = c[2];
                box[5] = AddF(c[2], half[2]);
            }
            List<object?> found = game.World.FindObjects(box, (uint)Numbers.BorlandAtoi(a[2]), null);
            foreach (object? o in found)
                if (o is SimObject so)
                    set.AddObject(so);
            return Numbers.FormatInt(found.Count);
        });

        // ---- teams (docs/mission.md 2.2) ----
        interp.AddCommand("resetPlayerManager", (_, _) =>
        {
            game.ResetTeams();
            return "True";
        });
        interp.AddCommand("resetGhostManagers", (_, _) => "True"); // clears connection ghost managers (ROADMAP 6.4)
        interp.AddCommand("getTeamName", (_, a) =>
        {
            if (a.Length != 2)
                return "0";
            int t = Numbers.BorlandAtoi(a[1]);
            return t >= -1 && t < game.TeamCount ? game.Teams[t + 1].Name : "0";
        });

        // ---- GameBase family (docs/mission.md 4.1) ----
        interp.AddCommand("GameBase::getTeam", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to getTream(object)");
                return "-1";
            }
            if (FindGameBase(game, a[1], false) is GameBase gb)
                return Numbers.FormatInt(gb.Team);
            int id = Numbers.BorlandAtoi(a[1]);
            if (id > 2048 && id < 2177 && game.Players.Find(id) is ClientRecord c)
                return Numbers.FormatInt(c.Team);
            return "-1";
        });
        interp.AddCommand("GameBase::setTeam", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "wrong argcount to setTeam(object,team)");
                return "-1";
            }
            if (FindGameBase(game, a[1], false) is GameBase gb)
            {
                gb.SetTeam(Numbers.BorlandAtoi(a[2]));
                return "True";
            }
            // A client id: team change (docs/player-manager.md 12.3), "-1" either way.
            int id = Numbers.BorlandAtoi(a[1]);
            if (game.Players.Find(id) is ClientRecord rec)
            {
                game.Players.SetTeam(id, Numbers.BorlandAtoi(a[2]));
                if (rec.IsAI)
                    return "True"; // the AI record answers as an object [live], a human record "-1"
            }
            return "-1";
        });
        interp.AddCommand("GameBase::getPosition", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(object)");
                return "0 0 0";
            }
            return FindGameBase(game, a[1]) is GameBase gb ? GameUtil.Fmt3(gb.Transform.P) : "0 0 0";
        });
        interp.AddCommand("GameBase::getRotation", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(object)");
                return "0 0 0";
            }
            if (FindGameBase(game, a[1]) is GameBase gb)
            {
                float[] e = new float[3];
                MissionTransform.MatrixToEuler(gb.Transform, e);
                return GameUtil.Fmt3(e);
            }
            return "0 0 0";
        });
        interp.AddCommand("GameBase::getTransform", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(object)");
                return "0 0 0 0 0 0";
            }
            if (FindGameBase(game, a[1]) is GameBase gb)
            {
                float[] e = new float[3];
                MissionTransform.MatrixToEuler(gb.Transform, e);
                return GameUtil.Fmt3(gb.Transform.P) + " " + GameUtil.Fmt3(e);
            }
            return "0 0 0 0 0 0";
        });
        interp.AddCommand("GameBase::setPosition", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(object,pos)");
                return "False";
            }
            if (FindGameBase(game, a[1]) is not GameBase gb)
                return "False";
            if (gb.IsShapeBase())
            {
                float[] p = new float[3];
                GameUtil.Scan3(a[2], p);
                // Event 0x405 with the object's matrix and the new position: the handler (0x0044c978) reads the rotation
                // back from that matrix before it moves.
                gb.TransformEvent(gb.Transform, p);
                if (gb.ShapeClass() == GameBase.ShapeKind.Vehicle)
                    gb.SetMaskBits(0x2000); // Vehicle_onSetTransform 0x00445898 (event 0x405)
                game.ObjectMoved(gb);
            }
            else if (gb.IsInterior())
            {
                // InteriorShape::setTransform (game-stream-ghosts.md 3.4): the persist position and the rotation read back
                // from its matrix go out in the mask 0x01 update. Collision and scope keep the add-time place.
                float[] p = new float[3];
                GameUtil.Scan3(a[2], p);
                gb.SetInteriorPosition(p);
            }
            else
            {
                float[] p = new float[3];
                GameUtil.Scan3(a[2], p);
                for (int i = 0; i < 3; ++i)
                    gb.Transform.P[i] = p[i];
            }
            return "True";
        });
        interp.AddCommand("GameBase::setRotation", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(object,rot)");
                return "False";
            }
            if (FindGameBase(game, a[1]) is not GameBase gb)
                return "False";
            float[] e = new float[3];
            GameUtil.Scan3(a[2], e);
            if (gb.IsShapeBase())
            {
                // Event 0x405 with the matrix of the new angles and the current position.
                Transform m = gb.Transform;
                MissionTransform.EulerToMatrix(e, ref m);
                float[] p = { gb.Transform.P[0], gb.Transform.P[1], gb.Transform.P[2] };
                gb.TransformEvent(m, p);
                if (gb.ShapeClass() == GameBase.ShapeKind.Vehicle)
                    gb.SetMaskBits(0x2000);
                game.ObjectMoved(gb);
            }
            else
            {
                MissionTransform.EulerToMatrix(e, ref gb.Transform);
                gb.Transform.Flags |= 2;
            }
            return "True";
        });
        interp.AddCommand("GameBase::getMapName", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(object)");
                return "";
            }
            GameBase? gb = FindGameBase(game, a[1]);
            if (gb == null || !gb.IsShapeBase())
                return "";
            return gb.MapName; // NULL prints False
        });
        interp.AddCommand("GameBase::setMapName", (inr, a) =>
        {
            if (a.Length != 3)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(object,name)");
                return "False";
            }
            if (FindGameBase(game, a[1]) is GameBase gb && gb.IsShapeBase())
            {
                gb.SetMapName(a[2]);
                return "True";
            }
            return "False";
        });
        interp.AddCommand("getObjectType", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, "wrong argcount to getObjectType(object);");
                return "False";
            }
            SimObject? obj = FindObject(game, a[1]);
            if (obj == null)
            {
                inr.Print(1, "getObjectType: could not find object");
                return "False";
            }
            return obj.ClassName();
        });
        interp.AddCommand("Object::getName", (_, a) =>
        {
            SimObject? obj = a.Length == 2 ? FindObject(game, a[1]) : null;
            if (obj == null)
                return "";
            if (obj.Name() == null || obj == game.Sim!.Server())
                return null;
            return obj.Name();
        });
        interp.AddCommand("removeFromSet", (_, a) =>
        {
            if (a.Length != 3)
                return "True";
            if (FindObject(game, a[1])?.AsSet() is not SimSet set)
                return "False";
            if (FindObject(game, a[2]) is SimObject obj)
                set.RemoveObject(obj);
            return "True";
        });
        StaticCommands.Register(interp, game);
        game.Players.RegisterCommands(interp);
        PlayerCommands.Register(interp, game);
        AICommands.Register(interp, game); // src/game/ai_commands.cpp
        Vehicle.RegisterCommands(interp, game);
    }
}
