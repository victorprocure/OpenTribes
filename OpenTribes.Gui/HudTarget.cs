// The crosshair target of the play HUD (docs/gui.md 4.2 Crosshair): every other player on screen gets a friend or foe mark above its
// head, and the one nearest to the viewer whose pick box holds the view centre gives the label under the crosshair. Objects of the
// sensor set (turrets, stations, generators, flags: anything with a target index or a Sensor) of the viewer's own team compete for the
// label too and bring their green map icon; they get no mark.
// Own code from the stock behaviour of `FearGui::Crosshair` draw [code 004ab500] and `Crosshair_findTarget` [code 004aca84].
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

// One player the picker looks at. Head is the mount point 2 transform position plus 0.65 up [code 004aca84]; Position is the object
// transform position (+0x188); Width and Height are the world box (the smaller horizontal extent and the vertical one).
public sealed record HudTargetCandidate(
    string Name, (double X, double Y, double Z) Head, (double X, double Y, double Z) Position, double Width, double Height,
    int Team, double Damage, bool Dead, bool Visible, bool LineClear, bool IsObject = false, string? Icon = null);

public readonly record struct HudTargetMark(int X, int Y, bool Friend);

// Marks of every counted player, the label of the picked one (null without a pick) and its damage level (below 0: no health box).
// Icon is the map icon bitmap of a picked object, drawn with its bottom centre at (IconX, IconY).
public sealed record HudTargetResult(IReadOnlyList<HudTargetMark> Marks, string? Name, double Damage, string? Icon = null, int IconX = 0, int IconY = 0);

public static class HudTargetPicker
{
    public const double ShowHealthFrom = 1.0;      // half width in pixels from which the health box shows [code 004ad411]
    public const int MinHalfWidth = 8, MinBelow = 12, Above = 4;

    // The eye is the camera position, myTeam the local player's team and teams the team count of the client player manager.
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
            if (!cam.Project(c.Head.X, c.Head.Y, c.Head.Z, out double fx, out double fy, out _))
                continue;
            int sx = (int)fx, sy = (int)fy;
            if (sx < 0 || sx > cam.Width || sy < 0 || sy > cam.Height)
                continue;
            // [code 004aca84] two or more teams and the same team: friend, else foe.
            if (!c.IsObject)
                marks.Add(new HudTargetMark(sx, sy, teams >= 2 && myTeam == c.Team));
            var (_, depth, _) = cam.ToCamera(c.Head.X, c.Head.Y, c.Head.Z);
            double half = cam.ProjectRadius(depth, c.Width) * 0.5;
            double tall = cam.ProjectRadius(depth, c.Height);
            int hw = Math.Max((int)half, MinHalfWidth);
            int below = Math.Max((int)tall, MinBelow);
            if (cx < sx - hw || cx > sx + hw || cy < sy - Above || cy > sy + below)
                continue;
            double dx = cam.X - c.Position.X, dy = cam.Y - c.Position.Y, dz = cam.Z - c.Position.Z;
            int dist = Math.Abs((int)Math.Sqrt(dx * dx + dy * dy + dz * dz));
            double health = half >= ShowHealthFrom ? c.Damage : -1;
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

    // The length of the health bar in pixels for a damage level and the inner width of the box (27 - 2, low resolution 20 - 2)
    // [code 004ac88d]: at least 1, none at full damage.
    public static int BarLength(double damage, int innerWidth)
    {
        if (damage < 0 || damage > 1)
            return 0;
        if (damage == 1.0)
            return 0;
        return Math.Max((int)((1.0 - damage) * innerWidth), 1);
    }
}
