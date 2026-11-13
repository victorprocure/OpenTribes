using OpenTribes.Core;
using OpenTribes.Script;

namespace OpenTribes.Client.App;

/// <summary>The variables the engine registers with a type.</summary>
/// <remarks>docs/client-scripts.md 5.1, Console_addVariable (005f40d8). A script assigns text. The registered cell parses it by its type and reads back in its own format, so <c>export("pref::*", ...)</c> writes <c>$pref::TerrainDetail = "1.000000";</c> and <c>$pref::IgnoreTargets = "False";</c> like the install's ClientPrefs.cs shows. Types (005f5240): 1 bool, 2 int, 3 float, 4 double, 5 float clamped to 0..1. Variables with a plugin id (6 floats, strings) and the ones the engine owns elsewhere keep the plain script store.</remarks>
public static class EngineVariables
{
    /// <summary>A registered variable.</summary>
    /// <param name="Name">The variable name with its <c>$</c>.</param>
    /// <param name="Type">The type code of 005f5240.</param>
    /// <param name="Default">The image default.</param>
    public readonly record struct Cell(string Name, int Type, double Default);

    /// <summary>The cells of the startup registration, with the image defaults of 5.1. A script default overrides them (clientDefaults.cs runs after registration).</summary>
    public static readonly Cell[] Cells =
    {
        new("$pref::PlayerFov", 3, 0), new("$pref::PlayerZoomSpeed", 3, 0), new("$pref::IgnoreTargets", 1, 0),
        new("$pref::NumDecals", 2, 50), new("$pref::TerrainVisibleDistance", 3, 10000), new("$pref::TerrainPerspectiveDistance", 5, 1),
        new("$pref::TerrainDetail", 5, 1), new("$pref::TerrainTextureDetail", 5, 1), new("$pref::InteriorTextureDetail", 2, 0),
        new("$SensorNetworkEnabled", 1, 1), new("$pref::damageSkinDetail", 2, 2), new("$pref::useCustomSkins", 1, 1),
        new("$pref::shadowDetailMask", 2, 0), new("$pref::shadowDetailScale", 5, 1), new("$pref::playerShapeDetail", 5, 1),
        new("$pref::vehicleShapeDetail", 5, 1), new("$pref::staticShapeDetail", 5, 1), new("$pref::skyDetail", 5, 1),
        new("$pref::starDetail", 5, 1), new("$pref::freeLook", 1, 1), new("$pref::interiorLightUpdateMS", 2, 67),
        new("$pref::maxNumSmokePuffs", 2, 100), new("$pref::weatherPrecipitation", 1, 1), new("$pref::flipFlierY", 1, 0),
        new("$pref::filterBadWords", 1, 0), new("$pref::animateWithTransitions", 1, 0), new("$pref::HrlmDetail", 2, 1),
        new("$pref::LagTimeout", 2, 700), new("$pref::UseFilter", 2, 0), new("$pref::CommanderZoomTime", 2, 250),
        new("$pref::mapFilter", 2, 1), new("$pref::mapSensorRange", 1, 0), new("$pref::mapNames", 1, 1),
        new("$pref::mapSensorTranslucent", 1, 0), new("$pref::mipcap", 2, 9), new("$pref::OpenGL::AlwaysRGBA", 1, 0),
        new("$pref::OpenGL::Use32BitTex", 1, 0), new("$pref::OpenGL::NoPackedTextures", 1, 0), new("$pref::OpenGL::NoAddFade", 1, 0),
        new("$pref::sfx2DVolume", 5, 1), new("$pref::sfx3DVolume", 5, 1), new("$pref::politeGui", 1, 0),
        new("$pref::dynamicLightDistance", 3, 0),
        // The two terrain tuning variables the startup registration binds to cells that nothing reads (0x6101fc, 0x610200).
        new("$screenError", 3, 0), new("$farDistance", 3, 500),
        // 1.11 registers both on the time manager (0x00424540, 0x00424552) with the defaults 0 (0x004eaec0) and 64 (0x004eaec8). The exe
        // reads them in the ghost smoothing and the vehicle correction; LastHope 1.30 hard-wires the defaults (L3). The port's client
        // does not smooth ghosts or look ahead, so the values are only readable and settable.
        new("$net::predictForwardTime", 2, 0), new("$net::interpolateTime", 2, 64),
    };

    /// <summary>The variables the T2 terrain renderer registers when it is created.</summary>
    /// <remarks>TerrainRender_registerVars (005768e4), called at the end of the renderer constructor (0057497e). They take over the names of the startup cells and bind to the statics of the renderer. <c>$screenError</c> is read by the frame setup (0057c08d), the others are only registered. The image defaults are 9, 45, 4, 500 and renderSimTerrain true. The statistics are 0. LastHope 1.30 overwrote this function with a return (docs/lasthope-1.30.md L7).</remarks>
    public static readonly Cell[] TerrainCells =
    {
        new("$T2::dynamicTextureCount", 2, 0), new("$T2::unusedTextureCount", 2, 0), new("$T2::textureSpaceUsed", 2, 0),
        new("$T2::staticTextureCount", 2, 0), new("$T2::levelZeroCount", 2, 0), new("$T2::fullMipCount", 2, 0),
        new("$T2::staticTSU", 2, 0), new("$screenError", 3, 9), new("$screenSize", 3, 45), new("$minSquareSize", 3, 4),
        new("$farDistance", 3, 500), new("$renderSimTerrain", 1, 1),
    };

    /// <summary>Reads the typed bool of a registered variable: the <c>True</c> or <c>False</c> readback of type 1.</summary>
    /// <param name="interp">The interpreter.</param>
    /// <param name="name">The variable name.</param>
    /// <returns>True when the variable reads <c>True</c>.</returns>
    public static bool IsSet(Interpreter interp, string name) => interp.GetVariable(name) == "True";

    /// <summary>Returns the text a typed cell shows for an assigned text (the readback of 5.1).</summary>
    /// <param name="type">The type code.</param>
    /// <param name="text">The assigned text.</param>
    /// <returns>The text in the format of the type.</returns>
    public static string Normalize(int type, string text)
    {
        switch (type)
        {
            case 1:
                return Numbers.IsTrueString(text) || Numbers.BorlandAtoi(text) != 0 ? "True" : "False";
            case 2:
                return Numbers.FormatInt(Numbers.BorlandAtoi(text));
            case 3:
                return CFormat.G(Fpu.F32Chop(ParsePrinted(text, -1, true)), 6);
            case 4:
                return CFormat.G(ParsePrinted(text, -1, false), 6);
            case 5:
                return CFormat.F(Fpu.F32Chop(Math.Clamp(ParsePrinted(text, 6, true), 0.0, 1.0)), 6);
            default:
                return text;
        }
    }

    // The toward-zero parse of 5.1, except that a text the engine itself printed (six decimals for a clamped float, six significant
    // digits for the others) parses to a value that prints the same text. The original loses one unit of the last digit per
    // save and load (client-scripts.md E7); a text typed by hand (`0.9`) still reads back as the original does.
    private static double ParsePrinted(string text, int decimals, bool single)
    {
        double d = Numbers.BorlandAtof(text);
        if (single)
            d = Fpu.F32Chop(d);
        string t = text.Trim();
        return Fpu.IsPrintedDecimal(t, decimals) ? Fpu.AtLeastDecimal(d, t, single) : d;
    }

    /// <summary>Registers every cell on the interpreter: the image default now, the typed readback after every assignment.</summary>
    /// <param name="interp">The interpreter.</param>
    public static void Register(Interpreter interp) => RegisterCells(interp, Cells);

    /// <summary>Registers the terrain renderer cells. The renderer registers once per process, so a later terrain keeps the values, as the statics keep them.</summary>
    /// <param name="interp">The interpreter.</param>
    public static void RegisterTerrain(Interpreter interp) => RegisterCells(interp, TerrainCells);

    private static void RegisterCells(Interpreter interp, Cell[] cells)
    {
        foreach (Cell c in cells)
        {
            Cell cell = c;
            bool inside = false;
            interp.SetVariable(cell.Name, Normalize(cell.Type, cell.Default.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            interp.BindVariable(cell.Name, v =>
            {
                if (inside)
                    return;
                inside = true;
                try
                {
                    string n = Normalize(cell.Type, v);
                    if (n != v)
                        interp.SetVariable(cell.Name, n);
                }
                finally
                {
                    inside = false;
                }
            });
        }
    }
}
