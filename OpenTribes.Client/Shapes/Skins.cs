using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Shapes;

/// <summary>The skin rules: the texture name rule, the damage skin level and the deterministic damage decal placement.</summary>
/// <remarks>docs/shapes-render.md 2.9. Names and numbers only. <see cref="ShapeAssets"/> applies them.</remarks>
public static class Skins
{
    /// <summary>Builds the candidate texture name of a skin (2.9 "Algorithm (names)"): a name that starts with <c>base.</c> has that prefix replaced by <c>&lt;skin&gt;.</c>, any other name becomes <c>&lt;skin&gt;.&lt;name&gt;</c> (custom skins, <c>$pref::useCustomSkins</c>).</summary>
    /// <param name="map">The bitmap name of the material.</param>
    /// <param name="skin">The skin name.</param>
    /// <param name="custom">True to allow custom skins.</param>
    /// <returns>The candidate name. It is used only when the resource exists.</returns>
    public static string Candidate(string map, string skin, bool custom = true)
    {
        if (skin.Length == 0)
            return map;
        if (map.StartsWith("base.", StringComparison.OrdinalIgnoreCase))
            return skin + "." + map[5..];
        return custom ? skin + "." + map : map;
    }

    /// <summary>Gets the skin of an image from the team of its mount.</summary>
    /// <param name="team">The team of the mount, -1 without one.</param>
    /// <param name="teamSkin">The skin of the team.</param>
    /// <returns>The skin, or empty to keep the own materials of the shape: no team, or the skin <c>base</c>.</returns>
    /// <remarks>Follows Player_setImage (0041a4xx): an image gets the team skin of its mount team only (the optional fourth argument of Player::mountItem, -1 without it).</remarks>
    public static string ImageSkin(int team, string teamSkin) => team < 0 || teamSkin == "base" ? "" : teamSkin;

    /// <summary>Resolves the texture name of a material under a skin.</summary>
    /// <param name="map">The bitmap name of the material.</param>
    /// <param name="skin">The skin name.</param>
    /// <param name="exists">Tells whether a resource exists.</param>
    /// <param name="custom">True to allow custom skins.</param>
    /// <returns>The candidate name when it exists, else the original name.</returns>
    public static string Resolve(string map, string skin, Func<string, bool> exists, bool custom = true)
    {
        string c = Candidate(map, skin, custom);
        return c != map && exists(c) ? c : map;
    }

    /// <summary>Computes the damage skin level (2.9 inputs (b), FUN_0044b0a8) for <c>$pref::damageSkinDetail</c> 1 and 2: levels 0 to 3, and 0 with detail 0.</summary>
    /// <param name="damage">The damage value.</param>
    /// <param name="detail">The damage skin detail.</param>
    /// <returns>The level.</returns>
    public static int DamageLevel(double damage, int detail = 2)
    {
        if (detail == 1)
            return damage > 0.4 ? 2 : 0;
        if (detail == 2)
            return damage > 0.75 ? 3 : damage > 0.5 ? 2 : damage > 0.25 ? 1 : 0;
        return 0;
    }

    /// <summary>Computes the damage skin level of the Player and turret path (ShapeBase_getDamageSkinLevel, 0044b518): above 0.7 gives 2, above 0.3 gives 1.</summary>
    /// <param name="damage">The damage value.</param>
    /// <returns>The level.</returns>
    public static int DamageLevelShort(double damage) => damage > 0.7 ? 2 : damage > 0.3 ? 1 : 0;

    /// <summary>Places damage decal number i (2.9 "Damage decals"): x = (0x01cc0a03 * i) mod 2^32 mod (W - w) and y = ((-0x34328f9b) * i) mod 2^32 mod (H - h).</summary>
    /// <param name="i">The decal number, 1, 2, ...</param>
    /// <param name="skinW">The skin width.</param>
    /// <param name="decalW">The decal width.</param>
    /// <param name="skinH">The skin height.</param>
    /// <param name="decalH">The decal height.</param>
    /// <returns>The position of the decal on the skin.</returns>
    public static (int x, int y) DecalPosition(int i, int skinW, int decalW, int skinH, int decalH)
    {
        uint x = unchecked(0x01cc0a03u * (uint)i), y = unchecked((uint)(-0x34328f9b) * (uint)i);
        int dx = skinW - decalW, dy = skinH - decalH;
        return (dx > 0 ? (int)(x % (uint)dx) : 0, dy > 0 ? (int)(y % (uint)dy) : 0);
    }

    /// <summary>Copies the skin and blits decals (index 0 clear) until the covered area reaches level * W * H / 4 or no decal is smaller than the skin.</summary>
    /// <param name="skin">The skin bitmap.</param>
    /// <param name="level">The damage level.</param>
    /// <param name="decals">The decal bitmaps. They cycle in order. A decal not smaller than the skin in either dimension is skipped.</param>
    /// <returns>The damaged copy of the skin.</returns>
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
