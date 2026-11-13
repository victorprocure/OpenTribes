namespace OpenTribes.Client.Shapes;

using OpenTribes.Client.World;
using OpenTribes.Platform.Gfx;

/// <summary>A texture of a shape bitmap with the blend it needs.</summary>
public sealed class TexEntry
{
    /// <summary>The GL texture, or null when the bitmap is missing.</summary>
    public IGfxTexture? Texture;
    /// <summary>The blend mode of the bitmap.</summary>
    public BlendMode Blend;
    /// <summary>True for a translucent bitmap with a kind 2 subpalette: the alpha is in the texture.</summary>
    public bool PaletteAlpha;
    /// <summary>The bitmap width in pixels.</summary>
    public int Width;
    /// <summary>The bitmap height in pixels.</summary>
    public int Height;
}

/// <summary>Loads shapes and shape textures for the renderer: the <c>.dts</c> through the resource manager, bitmaps through the world palette, skin variants with damage decals, and one GL texture per bitmap.</summary>
/// <remarks>docs/shapes-render.md 1.3 and 2.9.</remarks>
public sealed class ShapeAssets : IDisposable
{
    /// <summary>The world assets the files load from.</summary>
    public readonly WorldAssets World;
    private readonly IGfxDevice? mDev;
    private readonly Dictionary<string, RenderShape?> mShapes = new(StringComparer.OrdinalIgnoreCase);
    private readonly record struct TexKey(string Map, string Skin, int Level, bool Keyed);

    private sealed class TexKeyComparer : IEqualityComparer<TexKey>
    {
        public bool Equals(TexKey a, TexKey b) => a.Level == b.Level && a.Keyed == b.Keyed &&
            string.Equals(a.Map, b.Map, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Skin, b.Skin, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(TexKey k) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Map),
            StringComparer.OrdinalIgnoreCase.GetHashCode(k.Skin), k.Level, k.Keyed);
    }

    private readonly Dictionary<TexKey, TexEntry?> mTextures = new(new TexKeyComparer());
    /// <summary>The names of the files that could not be loaded.</summary>
    public readonly HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The bitmaps of the damage skins (DamageSkinData bmpName).</summary>
    public IReadOnlyList<PbmpBitmap> DamageDecals = Array.Empty<PbmpBitmap>();
    /// <summary>True to use the custom skins of a player.</summary>
    public bool UseCustomSkins = true;
    /// <summary>The detail of the damage skins.</summary>
    public int DamageSkinDetail = 2;

    /// <summary>Creates the loader.</summary>
    /// <param name="dev">The graphics device, or null to load without textures.</param>
    /// <param name="world">The world assets.</param>
    public ShapeAssets(IGfxDevice? dev, WorldAssets world)
    {
        mDev = dev;
        World = world;
    }

    /// <summary>Tells whether a resource exists.</summary>
    /// <param name="name">The resource name.</param>
    /// <returns>True when the resource exists.</returns>
    public bool Exists(string name) => GfxAssets.Read(World.Res, name, out _);

    /// <summary>Installs a shape built in memory (tests and generated effects) under a name.</summary>
    /// <param name="name">The shape name.</param>
    /// <param name="shape">The shape.</param>
    public void Register(string name, RenderShape shape) => mShapes[name] = shape;

    /// <summary>Loads a shape once and caches it.</summary>
    /// <param name="name">The shape file name.</param>
    /// <returns>The shape, or null when it is missing.</returns>
    public RenderShape? Shape(string name)
    {
        if (name.Length == 0)
            return null;
        if (mShapes.TryGetValue(name, out RenderShape? s))
            return s;
        var file = name.EndsWith(".dts", StringComparison.OrdinalIgnoreCase) ? name : name + ".dts";
        s = GfxAssets.Read(World.Res, file, out var data) ? RenderShape.Parse(data, name) : null;
        if (s == null)
            Missing.Add(file);
        mShapes[name] = s;
        return s;
    }

    /// <summary>Gets the texture of a material map.</summary>
    /// <param name="map">The bitmap name of the material.</param>
    /// <param name="skin">The skin name.</param>
    /// <param name="damageLevel">The damage level.</param>
    /// <param name="keyed">True to keep the transparent key.</param>
    /// <returns>The texture, or null when the bitmap is missing or there is no device.</returns>
    public TexEntry? Texture(string map, string skin = "", int damageLevel = 0, bool keyed = false)
    {
        var key = new TexKey(map, skin, damageLevel > 0 ? damageLevel : 0, keyed);
        if (mTextures.TryGetValue(key, out TexEntry? t))
            return t;
        var resolved = skin.Length > 0 ? Skins.Resolve(map, skin, Exists, UseCustomSkins) : map;
        t = null;
        PbmpBitmap? bmp = World.Bitmap(resolved);
        if (bmp == null)
        {
            Missing.Add(resolved);
        }
        else
        {
            if (damageLevel > 0)
                bmp = Skins.ApplyDamage(bmp, damageLevel, DamageDecals);
            SubPalette sub = World.Palette.FindSubPalette(bmp.PaletteId);
            var rgba = World.Rgba(bmp, false);
            if (keyed && bmp.Depth == 8)
                for (var i = 0; i < bmp.Width * bmp.Height; ++i)
                {
                    int idx = bmp.Pixels[(i / bmp.Width * bmp.Stride) + (i % bmp.Width)];
                    if (idx == 0)
                        rgba[(i * 4) + 3] = 0;
                }
            // the stock upload packs every palette indexed bitmap (5551, or 4444 for a translucent palette, 005d7148)
            if (bmp.Depth == 8)
                PaletteTables.Pack(rgba, sub);
            t = new TexEntry
            {
                Blend = bmp.Blend,
                Width = bmp.Width,
                Height = bmp.Height,
                PaletteAlpha = bmp.Blend == BlendMode.Translucent && sub.Kind == 2,
                Texture = mDev != null ? World.CreateTexture(mDev, bmp, rgba, false, false, true, keyed) : null,
            };
        }
        mTextures[key] = t;
        return t;
    }

    /// <summary>Creates the textures of every bitmap material of a shape under a skin (level 0 damage), so the first frame that draws the shape does not stall on bitmap decoding and texture uploads.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="skin">The skin name.</param>
    public void Preload(RenderShape shape, string skin)
    {
        foreach (RsMaterial m in shape.Materials)
            if (m.Type == 3)
                Texture(m.Name, skin, 0, m.Keyed);
    }

    /// <summary>Loads the DamageSkinData bitmaps (bmpName) once.</summary>
    /// <param name="names">The bitmap names.</param>
    public void SetDamageDecals(IEnumerable<string> names)
    {
        var l = new List<PbmpBitmap>();
        foreach (var n in names)
            if (n.Length > 0 && World.Bitmap(n) is PbmpBitmap b)
                l.Add(b);
        DamageDecals = l;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (TexEntry? t in mTextures.Values)
            t?.Texture?.Dispose();
        mTextures.Clear();
    }
}
