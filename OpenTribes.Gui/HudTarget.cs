// The crosshair target of the play HUD (docs/gui.md 4.2 Crosshair): every other player on screen gets a friend or foe mark above its
// head, and the one nearest to the viewer whose pick box holds the view centre gives the label under the crosshair.
// Own code from the stock behaviour of `FearGui::Crosshair` draw [code 004ab500] and `Crosshair_findTarget` [code 004aca84].
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Gui;

// One player the picker looks at. Head is the mount point 2 transform position plus 0.65 up [code 004aca84]; Position is the object
// transform position (+0x188); Width and Height are the world box (the smaller horizontal extent and the vertical one).
public sealed record HudTargetCandidate(
    string Name, (double X, double Y, double Z) Head, (double X, double Y, double Z) Position, double Width, double Height,
    int Team, double Damage, bool Dead, bool Visible, bool LineClear);

public readonly record struct HudTargetMark(int X, int Y, bool Friend);

// Marks of every counted player, the label of the picked one (null without a pick) and its damage level (below 0: no health box).
public sealed record HudTargetResult(IReadOnlyList<HudTargetMark> Marks, string? Name, double Damage);

public static class HudTargetPicker
{
    public const double ShowHealthFrom = 1.0;      // half width in pixels from which the health box shows [code 004ad411]
    public const int MinHalfWidth = 8, MinBelow = 12, Above = 4;

    // The eye is the camera position, myTeam the local player's team and teams the team count of the client player manager.
    public static HudTargetResult Find(Camera3D cam, IReadOnlyList<HudTargetCandidate> players, int myTeam, int teams)
    {
        var marks = new List<HudTargetMark>();
        HudTargetCandidate? best = null;
        int bestDist = -1;
        double bestHealth = -1;
        int cx = cam.Width / 2, cy = cam.Height / 2;
        foreach (HudTargetCandidate c in players)
        {
            if (c.Dead || !c.Visible || !c.LineClear)
                continue;
            if (!cam.Project(c.Head.X, c.Head.Y, c.Head.Z, out double fx, out double fy, out _))
                continue;
            int sx = (int)fx, sy = (int)fy;
            if (sx < 0 || sx > cam.Width || sy < 0 || sy > cam.Height)
                continue;
            // [code 004aca84] two or more teams and the same team: friend, else foe.
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
            if (best == null || dist < bestDist)
            {
                best = c;
                bestDist = dist;
                bestHealth = half >= ShowHealthFrom ? c.Damage : -1;
            }
        }
        if (best == null || best.Damage >= 1.0)
            return new HudTargetResult(marks, null, -1);
        return new HudTargetResult(marks, best.Name, bestHealth);
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
