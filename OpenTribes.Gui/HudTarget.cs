namespace OpenTribes.Gui;

using OpenTribes.Platform.Gfx;

/// <summary>One player the picker looks at.</summary>
/// <param name="Name">The name shown under the crosshair.</param>
/// <param name="Head">The mount point 2 transform position plus 0.65 up (004aca84).</param>
/// <param name="Position">The object transform position (+0x188).</param>
/// <param name="Width">The world box width: the smaller horizontal extent.</param>
/// <param name="Height">The world box height.</param>
/// <param name="Team">The team index.</param>
/// <param name="Damage">The damage level, 0 to 1.</param>
/// <param name="Dead">True when the player is dead.</param>
/// <param name="Visible">True when the player is on screen and shown.</param>
/// <param name="LineClear">True when the eye has a clear line to the player.</param>
/// <param name="IsObject">True for a sensor set object (turret, station, generator, flag) instead of a player.</param>
/// <param name="Icon">The map icon bitmap of an object, or null.</param>
public sealed record HudTargetCandidate(
    string Name, (double X, double Y, double Z) Head, (double X, double Y, double Z) Position, double Width, double Height,
    int Team, double Damage, bool Dead, bool Visible, bool LineClear, bool IsObject = false, string? Icon = null);

/// <summary>A friend or foe mark above a player's head.</summary>
/// <param name="X">The mark x.</param>
/// <param name="Y">The mark y.</param>
/// <param name="Friend">True for a friend mark, false for a foe mark.</param>
public readonly record struct HudTargetMark(int X, int Y, bool Friend);

/// <summary>The marks of every counted player and the picked target.</summary>
/// <param name="Marks">The marks of every counted player.</param>
/// <param name="Name">The label of the picked target, null without a pick.</param>
/// <param name="Damage">The damage level of the picked target, below 0 for no health box.</param>
/// <param name="Icon">The map icon bitmap of a picked object, drawn with its bottom centre at (<paramref name="IconX"/>, <paramref name="IconY"/>).</param>
/// <param name="IconX">The icon x.</param>
/// <param name="IconY">The icon y.</param>
public sealed record HudTargetResult(IReadOnlyList<HudTargetMark> Marks, string? Name, double Damage, string? Icon = null, int IconX = 0, int IconY = 0);

/// <summary>The crosshair target of the play HUD (docs/gui.md 4.2 Crosshair).</summary>
/// <remarks>Every other player on screen gets a friend or foe mark above its head. The one nearest to the viewer whose pick box holds the view centre gives the label under the crosshair. Objects of the sensor set (turrets, stations, generators, flags: anything with a target index or a Sensor) of the viewer's own team compete for the label too and bring their green map icon, but get no mark. Follows the stock crosshair draw (004ab500) and Crosshair_findTarget (004aca84).</remarks>
public static class HudTargetPicker
{
    /// <summary>The half width in pixels from which the health box shows (004ad411).</summary>
    public const double ShowHealthFrom = 1.0;
    /// <summary>The minimum half width of the pick box in pixels.</summary>
    public const int MinHalfWidth = 8;
    /// <summary>The minimum height of the pick box below the head in pixels.</summary>
    public const int MinBelow = 12;
    /// <summary>The height of the pick box above the head in pixels.</summary>
    public const int Above = 4;

    /// <summary>Marks every player and picks the target under the crosshair.</summary>
    /// <param name="cam">The camera of the viewer.</param>
    /// <param name="players">The players and sensor objects.</param>
    /// <param name="myTeam">The local player's team.</param>
    /// <param name="teams">The team count of the client player manager.</param>
    /// <returns>The marks and the picked target.</returns>
    public static HudTargetResult Find(Camera3D cam, IReadOnlyList<HudTargetCandidate> players, int myTeam, int teams)
    {
        var marks = new List<HudTargetMark>();
        HudTargetCandidate? best = null, bestPlayer = null;
        int bestDist = -1, bestPlayerDist = -1, bestX = 0, bestY = 0;
        double bestHealth = -1, bestPlayerHealth = -1;
        int cx = cam.Width / 2, cy = cam.Height / 2;
        foreach (HudTargetCandidate c in players)
        {
            if (c.Dead || !c.Visible)
                continue;
            // [code 004aca84] objects pass without the line test (it comes after the pick) and only when they are of the viewer's team.
            if (!c.IsObject && !c.LineClear)
                continue;
            if (c.IsObject && c.Team != myTeam)
                continue;
            if (!cam.Project(c.Head.X, c.Head.Y, c.Head.Z, out var fx, out var fy, out _))
                continue;
            int sx = (int)fx, sy = (int)fy;
            if (sx < 0 || sx > cam.Width || sy < 0 || sy > cam.Height)
                continue;
            // [code 004aca84] two or more teams and the same team: friend, else foe.
            if (!c.IsObject)
                marks.Add(new HudTargetMark(sx, sy, teams >= 2 && myTeam == c.Team));
            var (_, depth, _) = cam.ToCamera(c.Head.X, c.Head.Y, c.Head.Z);
            var half = cam.ProjectRadius(depth, c.Width) * 0.5;
            var tall = cam.ProjectRadius(depth, c.Height);
            var hw = Math.Max((int)half, MinHalfWidth);
            var below = Math.Max((int)tall, MinBelow);
            if (cx < sx - hw || cx > sx + hw || cy < sy - Above || cy > sy + below)
                continue;
            double dx = cam.X - c.Position.X, dy = cam.Y - c.Position.Y, dz = cam.Z - c.Position.Z;
            var dist = Math.Abs((int)Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)));
            var health = half >= ShowHealthFrom ? c.Damage : -1;
            if (best == null || dist < bestDist)
            {
                best = c;
                bestDist = dist;
                bestHealth = health;
                bestX = sx;
                bestY = sy;
            }
            if (!c.IsObject && (bestPlayer == null || dist < bestPlayerDist))
            {
                bestPlayer = c;
                bestPlayerDist = dist;
                bestPlayerHealth = health;
            }
        }
        // [code 004aca84] a picked object whose centre the eye cannot see gives way to the nearest picked player.
        if (best is { IsObject: true, LineClear: false })
        {
            best = bestPlayer;
            bestHealth = bestPlayerHealth;
        }
        if (best == null || (!best.IsObject && best.Damage >= 1.0))
            return new HudTargetResult(marks, null, -1);
        return new HudTargetResult(marks, best.Name, bestHealth, best.IsObject ? best.Icon : null, bestX, bestY);
    }

    /// <summary>Returns the length of the health bar.</summary>
    /// <param name="damage">The damage level.</param>
    /// <param name="innerWidth">The inner width of the box (27 - 2, low resolution 20 - 2).</param>
    /// <returns>The length in pixels: at least 1, none at full damage (004ac88d).</returns>
    public static int BarLength(double damage, int innerWidth)
    {
        if (damage is < 0 or > 1)
            return 0;
        if (damage == 1.0)
            return 0;
        return Math.Max((int)((1.0 - damage) * innerWidth), 1);
    }
}
