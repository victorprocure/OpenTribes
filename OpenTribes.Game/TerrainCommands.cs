// Port of src/game/terrain_commands.hpp and src/game/terrain_commands.cpp: the terrain script commands (docs/terrain.md
// 7, registered at 0x00528d3c): newTerrain, newTerrainParam, loadTerrain, saveTerrain, setTerrainDetail,
// setTerrainVisibility, setTerrainContainer, lightTerrain.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>The terrain script commands: newTerrain, newTerrainParam, loadTerrain, saveTerrain, setTerrainDetail, setTerrainVisibility, setTerrainContainer, lightTerrain.</summary>
/// <remarks>Registered at 0x00528d3c (docs/terrain.md 7). Port of src/game/terrain_commands.cpp.</remarks>
public static class TerrainCommands
{
    // src/game/terrain_commands.cpp findTerrain: the original calls the terrain's virtual functions on whatever object the
    // name resolves to; a non-terrain object crashes or corrupts it (DECISIONS D11). Only a SimTerrain is accepted here.
    private static TerrainObject? FindTerrain(GameState game, string name) =>
        game.Sim?.Server()?.Find(name) is TerrainObject t && GameUtil.EqualsNoCase(t.ClassName(), "SimTerrain") ? t : null;

    // src/game/terrain_commands.cpp scanFloat.
    private static float ScanFloat(string s) => Store(Numbers.BorlandAtof(s));

    // src/game/terrain_commands.cpp setFloat.
    private static void SetFloat(TerrainObject t, string field, float v) => t.SetPersistField(field, [PersistFloatText(v)]);

    /// <summary>Formats a float as <c>%.9g</c> text that the persist float parser reads back to the same value.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The text.</returns>
    /// <remarks>The persist parser rounds toward zero, so the text has to be at or above the value after rounding to nearest. The digits grow until the parse gives <paramref name="v"/> back.</remarks>
    public static string PersistFloatText(float v)
    {
        var mode = TowardZero;
        var output = "";
        for (var digits = 9; digits <= 120; digits += digits < 17 ? 1 : 8)
        {
            TowardZero = false;
            output = CFormat.G(v, digits);
            TowardZero = mode;
            if (Store(Numbers.BorlandAtof(output)) == v)
                break;
        }
        return output;
    }

    /// <summary>Registers the terrain commands.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="game">The game state the commands act on.</param>
    public static void Register(Interpreter interp, GameState game)
    {
        // Plugin ids 0-2 fall outside the callback's switch (0x00528e42): nothing, no message.
        foreach (var name in new[] { "newTerrain", "newTerrainParam", "loadTerrain" })
            interp.AddCommand(name, (_, _) => "False");

        // saveTerrain(terrain, volume) 0x00517aa4 writes the terrain into a new volume file; on the reference the call
        // with a real terrain never returns (DECISIONS D11), so every call here fails with the usage line the original
        // prints for a failed save.
        interp.AddCommand("saveTerrain", (inr, _) =>
        {
            inr.Print(1, "saveTerrain: objectName volumeName");
            return "False";
        });

        // setTerrainVisibility(terrain, visibleDist, hazeDist) vtable +0xbc: net mask 0x02. The original also copies two
        // uninitialized stack floats into hazeVerticalMin/Max; they stay as they were here (DECISIONS D16).
        interp.AddCommand("setTerrainVisibility", (inr, a) =>
        {
            if (a.Length != 4)
            {
                inr.Print(1, "setTerrainVisibility: objectName visibleDist hazeDist");
                return "False";
            }
            var t = FindTerrain(game, a[1]);
            if (t == null)
            {
                inr.Print(1, "setTerrainVisibility: object not found or not a SimTerrain object");
                return "False";
            }
            SetFloat(t, "visibleDistance", ScanFloat(a[2]));
            SetFloat(t, "hazeDistance", ScanFloat(a[3]));
            game.SetMaskBits(t, 0x02);
            return "False";
        });

        // setTerrainDetail(terrain, perspectiveDist, screenSquareSize) vtable +0xc0: net mask 0x04.
        interp.AddCommand("setTerrainDetail", (inr, a) =>
        {
            if (a.Length != 4)
            {
                inr.Print(1, "setTerrainDetail: objectName perspectiveDist screenSquareSize");
                return "False";
            }
            var t = FindTerrain(game, a[1]);
            if (t == null)
            {
                inr.Print(1, "setTerrainDetail: object not found or not a SimTerrain object");
                return "False";
            }
            SetFloat(t, "perspectiveDistance", ScanFloat(a[2]));
            SetFloat(t, "screenSize", ScanFloat(a[3]));
            game.SetMaskBits(t, 0x04);
            return "False";
        });

        // setTerrainContainer(terrain, "gx gy gz", drag, height) vtable +0xc4. The reference sends no update for it
        // (tools/cmddiff.py): clients that join later get the new values.
        interp.AddCommand("setTerrainContainer", (inr, a) =>
        {
            if (a.Length != 5)
            {
                inr.Print(1, "setTerrainContainer NEEDS: objectName Gravity Drag Height");
                return "False";
            }
            var t = FindTerrain(game, a[1]);
            if (t == null)
            {
                inr.Print(1, "setTerrainContainer: object not found or not a SimTerrain object");
                return "False";
            }
            float[] g = [0, 0, 0]; // components that do not parse are uninitialized in the original (D12)
            CScan.Floats(a[2], g);
            t.SetPersistField("contGravity", [PersistFloatText(g[0]) + " " + PersistFloatText(g[1]) + " " + PersistFloatText(g[2])]);
            SetFloat(t, "contDrag", ScanFloat(a[3]));
            SetFloat(t, "contHeight", ScanFloat(a[4]));
            // 0x005183d0 copies the five values and nothing else: the container box keeps its height, and a moving object
            // takes the new gravity and drag when it next enters the container (0x005195b4).
            if (game.World.Find(t) is Container.Entry e)
            {
                Span<float> gv = stackalloc float[3];
                t.Fields().Point("contGravity", gv, 0.0f, 0.0f, -9.8f);
                e.Gravity = Float3.Of(gv);
                e.Drag = t.Fields().Real("contDrag", 0.0f);
            }
            return "False";
        });

        // lightTerrain(terrain [, minx miny maxx maxy]) 0x00519684, 0x0051a978: recomputes the light map. Light maps are
        // client-side data and not modeled; the checks and messages are.
        interp.AddCommand("lightTerrain", (inr, a) =>
        {
            if (a.Length is not 2 and not 6)
            {
                inr.Print(1, "lightTerrain: objectName [minx miny maxx maxy]");
                return "False";
            }
            if (FindTerrain(game, a[1]) == null)
                inr.Print(1, "lightTerrain: object not found or not a SimTerrain object");
            return "False";
        });
    }
}
