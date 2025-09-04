// docs/client-scripts.md 5.1: the variables the engine registers with a type (Console_addVariable 005f40d8). A script assigns
// text; the registered cell parses it by its type and reads back in its own format, so `export("pref::*", ...)` writes
// `$pref::TerrainDetail = "1.000000";` and `$pref::IgnoreTargets = "False";` like the install's ClientPrefs.cs shows.
// Types (005f5240): 1 bool, 2 int, 3 float, 4 double, 5 float clamped to 0..1. Variables with a plugin id (6 floats, strings) and
// the ones the engine owns elsewhere keep the plain script store.
using OpenTribes.Core;
using OpenTribes.Script;

namespace OpenTribes.Client.App;

public static class EngineVariables
{
    public readonly record struct Cell(string Name, int Type, double Default);

    // The image defaults of 5.1; a script default overrides them (clientDefaults.cs runs after registration).
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
    };

    // The text a typed cell shows for an assigned text (the readback of 5.1).
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

    // Registers every cell on the interpreter: the image default now, the typed readback after every assignment.
    public static void Register(Interpreter interp)
    {
        foreach (Cell c in Cells)
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
