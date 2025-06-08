// Skins (docs/shapes-render.md 2.9): the texture name rule, the damage skin level and the deterministic damage decal
// placement. Names and numbers only; ShapeAssets applies them.
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Shapes;

public static class Skins
{
    // 2.9 "Algorithm (names)": a name that starts with `base.` has that prefix replaced by `<skin>.`; any other name becomes
    // `<skin>.<name>` (custom skins, $pref::useCustomSkins). The candidate is used only when the resource exists.
    public static string Candidate(string map, string skin, bool custom = true)
    {
        if (skin.Length == 0)
            return map;
        if (map.StartsWith("base.", StringComparison.OrdinalIgnoreCase))
            return skin + "." + map[5..];
        return custom ? skin + "." + map : map;
    }

    public static string Resolve(string map, string skin, Func<string, bool> exists, bool custom = true)
    {
        string c = Candidate(map, skin, custom);
        return c != map && exists(c) ? c : map;
    }

    // 2.9 inputs (b): FUN_0044b0a8 for $pref::damageSkinDetail 1 and 2 (levels 0 to 3; 0 with detail 0).
    public static int DamageLevel(double damage, int detail = 2)
    {
        if (detail == 1)
            return damage > 0.4 ? 2 : 0;
        if (detail == 2)
            return damage > 0.75 ? 3 : damage > 0.5 ? 2 : damage > 0.25 ? 1 : 0;
        return 0;
    }

    // ShapeBase_getDamageSkinLevel (0044b518), the variant of the Player and turret path: above 0.7 gives 2, above 0.3 gives 1.
    public static int DamageLevelShort(double damage) => damage > 0.7 ? 2 : damage > 0.3 ? 1 : 0;

    // 2.9 "Damage decals": decal number i (1, 2, ...) lands at x = (0x01cc0a03 * i) mod 2^32 mod (W - w) and
    // y = ((-0x34328f9b) * i) mod 2^32 mod (H - h).
    public static (int x, int y) DecalPosition(int i, int skinW, int decalW, int skinH, int decalH)
    {
        uint x = unchecked(0x01cc0a03u * (uint)i), y = unchecked((uint)(-0x34328f9b) * (uint)i);
        int dx = skinW - decalW, dy = skinH - decalH;
        return (dx > 0 ? (int)(x % (uint)dx) : 0, dy > 0 ? (int)(y % (uint)dy) : 0);
    }

    // Copies the skin and blits decals (index 0 clear) until the covered area reaches level * W * H / 4 or no decal is
    // smaller than the skin. Decals cycle in order; a decal not smaller than the skin in either dimension is skipped.
    public static PbmpBitmap ApplyDamage(PbmpBitmap skin, int level, IReadOnlyList<PbmpBitmap> decals)
    {
        if (level <= 0 || decals.Count == 0 || skin.Depth != 8)
            return skin;
        var copy = new PbmpBitmap
        {
            Width = skin.Width, Height = skin.Height, Depth = skin.Depth, Attributes = skin.Attributes, PaletteId = skin.PaletteId,
            Pixels = (byte[])skin.Pixels.Clone(),
        };
        long target = (long)level * skin.Width * skin.Height / 4, covered = 0;
        int n = 0, count = decals.Count, guard = 0;
        while (covered < target && guard++ < count * 8)
        {
            PbmpBitmap d = decals[n % count];
            ++n;
            if (d.Depth != 8 || d.Width >= skin.Width || d.Height >= skin.Height)
                continue;
            (int px, int py) = DecalPosition(n, skin.Width, d.Width, skin.Height, d.Height);
            for (int y = 0; y < d.Height; ++y)
                for (int x = 0; x < d.Width; ++x)
                {
                    byte v = d.Pixels[y * d.Stride + x];
                    if (v != 0)
                        copy.Pixels[(py + y) * copy.Stride + px + x] = v;
                }
            covered += (long)d.Width * d.Height;
        }
        return copy;
    }
}
