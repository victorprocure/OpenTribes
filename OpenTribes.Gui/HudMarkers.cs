// The target markers of the crosshair HUD (docs/gui.md 4.2 Crosshair): a small triangle on the screen point where the weapon has to aim to
// hit a placed target (an object with the target flag, or the point a targeting laser paints) and the distance below it.
// Own code from the stock behaviour of `FUN_004ada90` [code 004ada90], called at the end of `FearGui::Crosshair` draw [code 004ab500].
using System.Globalization;

namespace OpenTribes.Gui;

// One triangle: the tip pixel and whether the datablock solver says the projectile reaches the target (palette 0xfb, else 0xf1).
public readonly record struct HudMarkPoint(int X, int Y, bool Reaches);

// A marked target: the low arc triangle, the high arc one (grenades only) and the distance label drawn under each of them.
public sealed record HudTargetMarker(HudMarkPoint? Low, HudMarkPoint? High, string Label);

public static class HudMarkers
{
    public const int ReachColor = 0xfb, ShortColor = 0xf1;

    // The label of the distance, format "%2.1f m" at 00623f8a [code 004ae847].
    public static string Label(double distance) => distance.ToString("F1", CultureInfo.InvariantCulture).PadLeft(2) + " m";

    // The pixel of a projected coordinate: ftol(v + 0.5) with the truncating control word of the exe [code 004ae224..004ae26f].
    public static int Pixel(double v) => (int)(float)(v + 0.5);

    // The triangle points down: the tip at the target, the base 2 x half width above it (half width 4, 3 at low resolution) [code 004ae27b..004ae2e6].
    public static (int X, int Y)[] Triangle(int x, int y, bool lowRes)
    {
        int d = lowRes ? 3 : 4;
        return new[] { (x, y), (x - d, y - 2 * d), (x + d, y - 2 * d) };
    }

    // The label sits centred under the tip: its top is d below it [code 004ae877..004ae8e5].
    public static (int X, int Y) LabelOrigin(int x, int y, int textWidth, bool lowRes) => (x - (textWidth >> 1), y + (lowRes ? 3 : 4));

    // The distance on the weapon: from the muzzle of the image in slot 0 (the player position without one) to the target.
    public static double Distance((double X, double Y, double Z) from, (double X, double Y, double Z) to)
    {
        double dx = (float)(from.X - to.X), dy = (float)(from.Y - to.Y), dz = (float)(from.Z - to.Z);
        return (float)Math.Sqrt((float)(dx * dx + dy * dy + dz * dz));
    }
}
