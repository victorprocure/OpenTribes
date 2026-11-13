// Port of src/game/backlog_commands.hpp and src/game/backlog_commands.cpp: commands of docs/script.md 6.8 that need only
// client records, mission objects or the datablocks: WaypointToWorld, the item shopping masks, GameBase::getLOSInfo and
// getMuzzleTransform, the Interior light commands and the client-only stubs of the game plugin. The f32 helper of the
// file is a plain store in the current mode (Fpu.Store).
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>Extension points for classes added later (Player). Each hook is optional.</summary>
public static class BacklogHooks
{
    /// <summary>The eye transform of a class that overrides ShapeBase vtable +0x1a4 (Player 0x00412f5c).</summary>
    public static Func<GameBase, Transform, (bool, Transform)>? EyeTransform;
    /// <summary>The muzzle transform of mounted image 0 (ShapeBase vtable +0x10c). The first result item is false when none is mounted.</summary>
    public static Func<GameBase, Transform, (bool, Transform)>? MuzzleTransform;
}

/// <summary>The backlog script commands: waypoints, shopping masks, item types and the commands that depend on the Player.</summary>
public static class BacklogCommands
{
    // src/game/backlog_commands.cpp compose: Euler rotation times a transform: the rotation applies first (0x005ed2b2 with
    // the product of 0x005ed000 as left operand), row-vector convention.
    private static Transform Compose(in Transform a, in Transform b)
    {
        var r = new Transform();
        for (var i = 0; i < 3; ++i)
            for (var j = 0; j < 3; ++j)
                r.M[(i * 3) + j] = Store(Add(Add(Mul(a.M[(i * 3) + 0], b.M[(0 * 3) + j]), Mul(a.M[(i * 3) + 1], b.M[(1 * 3) + j])),
                                           Mul(a.M[(i * 3) + 2], b.M[(2 * 3) + j])));
        for (var j = 0; j < 3; ++j)
            r.P[j] = Store(Add(Add(Add(Mul(a.P[0], b.M[(0 * 3) + j]), Mul(a.P[1], b.M[(1 * 3) + j])), Mul(a.P[2], b.M[(2 * 3) + j])), b.P[j]));
        return r;
    }

    private static bool IsSpaceChar(char c) => c is ' ' or >= '\t' and <= '\r';

    /// <summary>Converts waypoint coordinates to world x and y for the mission area.</summary>
    /// <param name="area">The mission area {x, y, w, h}.</param>
    /// <param name="wx">The waypoint x, 0 to 1023.</param>
    /// <param name="wy">The waypoint y, 0 to 1023.</param>
    /// <param name="output">Receives world x and y.</param>
    /// <remarks>Follows WaypointToWorld (0x0041e9e8). The original multiplies by 1/1024, not 1/1023.</remarks>
    public static void WaypointToWorld(ReadOnlySpan<float> area, float wx, float wy, Span<float> output)
    {
        const float k = 0.0009765625f;
        var x = Store(Mul(k, wx));
        var y = Store(Mul(k, wy));
        var px = Store(Mul(x, area[2]));
        var py = Store(Mul(y, area[3]));
        output[0] = Store(Add(area[0], px));
        output[1] = Store(Add(area[1], py));
    }

    /// <summary>Parses the item type argument of the shopping commands.</summary>
    /// <param name="text">The argument text.</param>
    /// <param name="itemCount">The number of item datablocks.</param>
    /// <param name="lookupByName">Returns the item index of a datablock name, or -1.</param>
    /// <returns>The item type.</returns>
    /// <remarks>Follows 0x004197a4.</remarks>
    public static int ParseItemType(string text, int itemCount, Func<string, int> lookupByName)
    {
        var i = 0;
        while (i < text.Length && IsSpaceChar(text[i]))
            ++i;
        var s = text[i..];
        if (s.Length != 0 && s[0] >= '0' && s[0] <= '9')
        {
            var n = Numbers.BorlandAtoi(s);
            return n >= 0 && n < itemCount ? n : -1;
        }
        return lookupByName(s);
    }

    /// <summary>Tests the mask bit of a shopping type.</summary>
    /// <param name="words">The mask words.</param>
    /// <param name="type">The item type.</param>
    /// <returns>The bit of a type in 0 to 199. For any other type, whether any bit is set.</returns>
    /// <remarks>Follows isItemShoppingOn and isItemBuyingOn (0x00407c78, 0x00407d5c).</remarks>
    public static bool ItemMaskTest(uint[] words, int type)
    {
        if (type is >= 0 and < 200)
            return ((words[type >> 5] >> (type & 31)) & 1u) != 0;
        for (var i = 0; i < 7; ++i)
            if (words[i] != 0)
                return true;
        return false;
    }

    private static void SetBit(uint[] words, int type) => words[type >> 5] |= 1u << (type & 31);
    private static void ClearBit(uint[] words, int type) => words[type >> 5] &= ~(1u << (type & 31));

    /// <summary>Registers the backlog commands.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="game">The game state the commands act on.</param>
    public static void Register(Interpreter interp, GameState game)
    {
        SimObject? FindPlain(string s) => game.Sim?.Server()?.Find(s);
        // 0x0041bf38: findObject in the server manager cast to ShapeBase (no client ids).
        GameBase? FindShapeBase(string s) => FindPlain(s) is GameBase gb && gb.IsShapeBase() ? gb : null;

        CommandMap.Register(interp, game);
        TerrainCommands.Register(interp, game);
        ObjectFiles.Register(interp, game);
        // WaypointToWorld(coords) 0x0041e9e8. The mission area is the MissionCenterPos object.
        interp.AddCommand("WaypointToWorld", (inr, a) =>
        {
            if (a.Length != 2)
            {
                inr.Print(1, a[0] + " <waypoint coords>");
                inr.Print(1, "Converts waypoint coords (x,y in [0,1023]) to world coords.");
                return "0 0 0";
            }
            float[] w = [0, 0, 0];
            MathCommands.ScanFloats3(a[1], w);
            var area = new float[4];
            game.Players.MissionArea(area);
            var output = new float[2];
            WaypointToWorld(area, w[0], w[1], output);
            return MathCommands.FormatF32(output[0]) + " " + MathCommands.FormatF32(output[1]) + " 0";
        });

        // Client::centerPrint(text, type) 0x0042276c copies the text into the local center-print buffer; nothing is
        // visible on a server. Any other argument count: nothing, False.
        interp.AddCommand("Client::centerPrint", (_, a) => a.Length == 3 ? "True" : "False");
        // Client::cmdObservePlayer(id) 0x0043a6e8 needs the client command GUI object.
        interp.AddCommand("Client::cmdObservePlayer", (_, _) => "False");
        // remotePlayAnim(client, n) 0x00424aa8: acts on the Player owned by the client. Players register their own handler
        // through the hook when they exist; none here.
        interp.AddCommand("remotePlayAnim", (_, _) => "False");

        // ---- item shopping and buying masks (docs/player-manager.md 12.4) ----
        int ItemLookup(string name) =>
            game.Sim?.DataBlocks().Find(name) is DataBlockManager.Block b && b.Group == 5 ? b.Index : -1;
        bool ShowInventory(int type) =>
            game.Sim?.DataBlocks().At(5, type) is DataBlockManager.Block b && game.Sim.DataBlocks().GetField(b, "showInventory", "", out var v) &&
            Numbers.ParseBool(v);
        (string Name, string Usage, bool Buying, bool Set)[] kCommands =
        [
            ("Client::setItemShopping", "wrong argcount to setItemShoppingList(client,<type,...>)", false, true),
            ("Client::clearItemShopping", "wrong argcount to clearItemShoppingList(client,<type,...>)", false, false),
            ("Client::setItemBuying", "wrong argcount to setItemBuyList(client,<type,...>)", true, true),
            ("Client::clearItemBuying", "wrong argcount to clearItemBuyList(client,<type,...>)", true, false),
        ];
        foreach (var (Name, Usage, Buying, Set) in kCommands)
        {
            interp.AddCommand(Name, (inr, a) =>
            {
                if (a.Length < 2)
                {
                    inr.Print(1, Usage);
                    return "False";
                }
                ClientRecord? rec = game.Players.Find(Numbers.BorlandAtoi(a[1]));
                if (rec == null)
                    return "False";
                var count = game.DataBlockCount(5);
                void Apply(int type)
                {
                    uint[] shop = rec.Shopping, buy = rec.Buying;
                    if (Set)
                    {
                        // 0x00407bd8 (shopping), then 0x00407cc4 for buying
                        if (type == -1)
                            Array.Fill(shop, 0xffffffffu);
                        else if (ShowInventory(type))
                            SetBit(shop, type);
                        if (Buying)
                        {
                            if (type == -1)
                                Array.Fill(buy, 0xffffffffu);
                            else if (ShowInventory(type))
                                SetBit(buy, type);
                        }
                    }
                    else
                    {
                        // 0x00407d28 (buying), then 0x00407c30 for shopping
                        if (type == -1)
                            Array.Clear(buy);
                        else
                            ClearBit(buy, type);
                        if (!Buying)
                        {
                            if (type == -1)
                                Array.Clear(shop);
                            else
                                ClearBit(shop, type);
                        }
                    }
                }
                if (a.Length == 2)
                {
                    Apply(-1);
                }
                else
                {
                    for (var i = 2; i < a.Length; ++i)
                    {
                        var type = ParseItemType(a[i], count, ItemLookup);
                        if (type != -1)
                            Apply(type);
                    }
                }
                return "True";
            });
        }
        foreach (var buying in new[] { false, true })
        {
            var name = buying ? "Client::isItemBuyingOn" : "Client::isItemShoppingOn";
            var usage = buying ? "wrong argcount to isItemBuyOn(client,type)" : "wrong argcount to isItemShoppingOn(client,type)";
            interp.AddCommand(name, (inr, a) =>
            {
                if (a.Length != 3)
                {
                    inr.Print(1, usage);
                    return "False";
                }
                ClientRecord? rec = game.Players.Find(Numbers.BorlandAtoi(a[1]));
                if (rec == null)
                    return "False";
                var type = ParseItemType(a[2], game.DataBlockCount(5), ItemLookup);
                return type != -1 && ItemMaskTest(buying ? rec.Buying : rec.Shopping, type) ? "True" : "False";
            });
        }

        // GameBase::getLOSInfo(object, range [, rotation]) 0x00420434. The ray starts at the eye transform (vtable +0x1a4),
        // optionally turned by the Euler angles, and runs range units along its local +Y axis; the caller is excluded;
        // mask 0x20e00086.
        interp.AddCommand("GameBase::getLOSInfo", (inr, a) =>
        {
            if (a.Length is not 3 and not 4)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "(object,range,<rotation>)");
                return "False";
            }
            GameBase? gb = FindShapeBase(a[1]);
            if (gb == null)
                return "False";
            Transform eye = gb.Transform;
            var hooked = false;
            if (BacklogHooks.EyeTransform != null)
            {
                (var ok, Transform t) = BacklogHooks.EyeTransform(gb, eye);
                if (ok)
                {
                    eye = t;
                    hooked = true;
                }
            }
            if (!hooked && game.World.Find(gb) is Container.Entry e)
                for (var i = 0; i < 3; ++i)
                    eye.P[i] = Store(Div(Add(e.Box[i], e.Box[i + 3]), 2));
            if (a.Length == 4)
            {
                float[] e3 = [0, 0, 0];
                CScan.Floats(a[3], e3);
                var rot = new Transform();
                MissionTransform.EulerToMatrix(e3, ref rot);
                eye = Compose(rot, eye);
            }
            var range = Store(Numbers.BorlandAtof(a[2]));
            var start = new Vec3(eye.P[0], eye.P[1], eye.P[2]);
            // ShapeBase_castLOS (0x0044bea4): the end is (0, range, 0) through the eye transform.
            float[] along = [0.0f, range, 0.0f];
            var endp = new float[3];
            X87.MulPoint(along, eye.M, eye.P, endp);
            var end = new Vec3(endp[0], endp[1], endp[2]);
            object? hitObj = null;
            Vec3 hit = default, n = default;
            if (!game.World.CastRay(start, end, 0x20e00086u, gb, ref hitObj, ref hit, ref n))
                return "False";
            inr.SetVariable("$los::position", GameUtil.Fmt3([hit.X, hit.Y, hit.Z]));
            inr.SetVariable("$los::normal", GameUtil.Fmt3([n.X, n.Y, n.Z]));
            inr.SetVariable("$los::object", Numbers.FormatInt(((SimObject)hitObj!).Id()));
            return "True";
        });

        // GameBase::getMuzzleTransform(object) 0x00422ea8: "%f" of the 3x3 rotation and the position. The object's own
        // transform when no image is mounted.
        interp.AddCommand("GameBase::getMuzzleTransform", (_, a) =>
        {
            const string kIdentity = "1 0 0 0 1 0 0 0 1 0 0 0";
            if (a.Length != 2)
                return kIdentity;
            GameBase? gb = FindShapeBase(a[1]);
            if (gb == null)
                return kIdentity;
            Transform t = gb.Transform;
            if (BacklogHooks.MuzzleTransform != null)
                t = BacklogHooks.MuzzleTransform(gb, t).Item2;
            var parts = new List<string>();
            for (var i = 0; i < 9; ++i)
                parts.Add(CFormat.F(t.M[i], 6));
            for (var i = 0; i < 3; ++i)
                parts.Add(CFormat.F(t.P[i], 6));
            return string.Join(" ", parts);
        });

        // Interior::toggleLight / switchOnLight / switchOffLight (Interior, Light) and setLightTime (Interior, Light, Time)
        // 0x004221a0, 0x0042223c, 0x004222b0, 0x00422324. They return False on every path. The light run state
        // (docs/interiors.md 5.4) is read by nothing on a server. switchOffLight calls the same routine with state 1 as
        // switchOnLight, as the original does. An InteriorShape without an instance does nothing here (D23).
        (string Name, int Argc, string Usage, int Op)[] kLights =
        [
            ("Interior::toggleLight", 3, "wrong argcount to %s(Interior, Light)", 0),
            ("Interior::switchOnLight", 3, "wrong argcount to %s(Interior, Light)", 1),
            ("Interior::switchOffLight", 3, "wrong argcount to %s(Interior, Light)", 2),
            ("Interior::setLightTime", 4, "wrong argcount to %s(Interior, Light, Time)", 3),
        ];
        foreach (var (Name, Argc, Usage, Op) in kLights)
            interp.AddCommand(Name, (inr, a) =>
            {
                if (a.Length != Argc)
                {
                    var at = Usage.IndexOf("%s", StringComparison.Ordinal);
                    inr.Print(1, Usage[..at] + a[0] + Usage[(at + 2)..]);
                    return "False";
                }
                if (FindPlain(a[1]) is not GameBase gb || !gb.IsInterior() || gb.InteriorGeo == null)
                    return "False";
                InteriorLights lights = gb.InteriorNet.Lighting;
                if (Op == 3)
                {
                    var t = Store(Numbers.BorlandAtof(a[3]));
                    var lid = lights.FindByName(a[2]);
                    if (lid != -1)
                        lights.SetTime(lid, t);
                    return "False";
                }
                var id = lights.FindByName(a[2]);
                if (id == -1)
                    return "False";
                if (Op == 0)
                {
                    var state = lights.StateOf(id);
                    if (state != -1)
                        lights.SetState(id, state == 0 ? 1 : 0);
                }
                else
                {
                    lights.SetState(id, 1);
                }
                return "False";
            });
    }
}
