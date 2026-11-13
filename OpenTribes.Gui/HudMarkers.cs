using System.Globalization;

namespace OpenTribes.Gui;

/// <summary>One marker triangle.</summary>
/// <param name="X">The tip pixel x.</param>
/// <param name="Y">The tip pixel y.</param>
/// <param name="Reaches">True when the datablock solver says the projectile reaches the target (palette 0xfb), else the triangle uses 0xf1.</param>
public readonly record struct HudMarkPoint(int X, int Y, bool Reaches);

/// <summary>A marked target: the low arc triangle, the high arc one (grenades only) and the distance label under each.</summary>
/// <param name="Low">The low arc triangle, or null.</param>
/// <param name="High">The high arc triangle, or null.</param>
/// <param name="Label">The distance label.</param>
public sealed record HudTargetMarker(HudMarkPoint? Low, HudMarkPoint? High, string Label);

/// <summary>The eye position and the muzzle transform of a Vehicle, Flier or Turret control object in world space, with the object transform.</summary>
/// <param name="Eye">The eye position.</param>
/// <param name="MuzzleP">The translation of the muzzle transform, null when the object reports none.</param>
/// <param name="ObjectM">The object rotation.</param>
/// <param name="ObjectP">The object position.</param>
/// <remarks>Follows the stock object reads at vtable +0x110 and +0x10c, with the object transform at +0x94.</remarks>
public sealed record HudControlAim(float[] Eye, float[]? MuzzleP, float[] ObjectM, float[] ObjectP)
{
    /// <summary>Returns the world point the distance label is measured to.</summary>
    /// <returns>The muzzle translation multiplied by the object transform, or the object position when there is no muzzle.</returns>
    /// <remarks>The stock marker routine multiplies the muzzle translation by the object transform once more (004ae799..004ae7c1, m_mul_point 005ed17e). The Player muzzle is in object space, the Vehicle and Turret ones already in world space, so for those the point is transformed twice.</remarks>
    public (double, double, double) MuzzleLabelPoint()
    {
        var o = new float[3];
        OpenTribes.World.X87.MulPoint(MuzzleP ?? new float[3], ObjectM, ObjectP, o);
        return (o[0], o[1], o[2]);
    }
}

/// <summary>The target markers of the crosshair HUD (docs/gui.md 4.2 Crosshair).</summary>
/// <remarks>A small triangle on the screen point where the weapon has to aim to hit a placed target (an object with the target flag, or the point a targeting laser paints), with the distance below it. Follows the stock routine at 004ada90, called at the end of the crosshair draw (004ab500).</remarks>
public static class HudMarkers
{
    /// <summary>The palette index of a triangle whose projectile reaches the target.</summary>
    public const int ReachColor = 0xfb;
    /// <summary>The palette index of a triangle whose projectile falls short.</summary>
    public const int ShortColor = 0xf1;

    /// <summary>Formats a distance as the stock label "%2.1f m".</summary>
    /// <param name="distance">The distance in metres.</param>
    /// <returns>The label.</returns>
    /// <remarks>Follows the stock format at 00623f8a (004ae847). The exe's sprintf cuts the value after the first decimal, it does not round (distance 6.87576 prints "6.8 m").</remarks>
    public static string Label(double distance)
    {
        string t = distance.ToString("F8", CultureInfo.InvariantCulture);
        int dot = t.IndexOf('.');
        return t[..(dot + 2)].PadLeft(2) + " m";
    }

    /// <summary>Converts a projected coordinate to a pixel.</summary>
    /// <param name="v">The projected coordinate.</param>
    /// <returns>ftol(v + 0.5) with the truncating control word of the exe (004ae224..004ae26f).</returns>
    public static int Pixel(double v) => (int)(float)(v + 0.5);

    /// <summary>Returns the three corners of the downward triangle.</summary>
    /// <param name="x">The tip x.</param>
    /// <param name="y">The tip y.</param>
    /// <param name="lowRes">True at low resolution.</param>
    /// <returns>The tip and the two base corners.</returns>
    /// <remarks>The base is two half widths above the tip. The half width is 4, or 3 at low resolution (004ae27b..004ae2e6).</remarks>
    public static (int X, int Y)[] Triangle(int x, int y, bool lowRes)
    {
        int d = lowRes ? 3 : 4;
        return new[] { (x, y), (x - d, y - 2 * d), (x + d, y - 2 * d) };
    }

    /// <summary>Returns the top left of the distance label.</summary>
    /// <param name="x">The tip x.</param>
    /// <param name="y">The tip y.</param>
    /// <param name="textWidth">The label width in pixels.</param>
    /// <param name="lowRes">True at low resolution.</param>
    /// <returns>The label origin: centred under the tip, its top 4 pixels below it (3 at low resolution) (004ae877..004ae8e5).</returns>
    public static (int X, int Y) LabelOrigin(int x, int y, int textWidth, bool lowRes) => (x - (textWidth >> 1), y + (lowRes ? 3 : 4));

    /// <summary>Returns the distance from the weapon to the target.</summary>
    /// <param name="from">The muzzle of the image in slot 0, or the player position without one.</param>
    /// <param name="to">The target point.</param>
    /// <returns>The distance, rounded to float at each step like the exe.</returns>
    public static double Distance((double X, double Y, double Z) from, (double X, double Y, double Z) to)
    {
        double dx = (float)(from.X - to.X), dy = (float)(from.Y - to.Y), dz = (float)(from.Z - to.Z);
        return (float)Math.Sqrt((float)(dx * dx + dy * dy + dz * dz));
    }
}
