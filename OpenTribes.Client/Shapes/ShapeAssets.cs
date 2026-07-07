// Loads shapes and shape textures for the renderer (docs/shapes-render.md 1.3, 2.9): the `.dts` through the resource
// manager, bitmaps through the world palette, skin variants with damage decals, and one GL texture per bitmap.
using OpenTribes.Client.World;
using OpenTribes.Core;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Shapes;

public sealed class TexEntry
{
    public IGfxTexture? Texture;
    public BlendMode Blend;
    public bool PaletteAlpha;      // translucent bitmap with a kind 2 subpalette: the alpha is in the texture
    public int Width, Height;
}

public sealed class ShapeAssets : IDisposable
{
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
    public readonly HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<PbmpBitmap> DamageDecals = Array.Empty<PbmpBitmap>();
    public bool UseCustomSkins = true;
    public int DamageSkinDetail = 2;

    public ShapeAssets(IGfxDevice? dev, WorldAssets world)
    {
        mDev = dev;
        World = world;
    }

    public bool Exists(string name) => GfxAssets.Read(World.Res, name, out _);

    // Installs a shape built in memory (tests and generated effects) under a name.
    public void Register(string name, RenderShape shape) => mShapes[name] = shape;

    public RenderShape? Shape(string name)
    {
        if (name.Length == 0)
            return null;
        if (mShapes.TryGetValue(name, out RenderShape? s))
            return s;
        string file = name.EndsWith(".dts", StringComparison.OrdinalIgnoreCase) ? name : name + ".dts";
        s = GfxAssets.Read(World.Res, file, out byte[] data) ? RenderShape.Parse(data, name) : null;
        if (s == null)
            Missing.Add(file);
        mShapes[name] = s;
        return s;
    }

    // The texture of a material map name under a skin and a damage level; null when the bitmap is missing or there is no device.
    public TexEntry? Texture(string map, string skin = "", int damageLevel = 0, bool keyed = false)
    {
        var key = new TexKey(map, skin, damageLevel > 0 ? damageLevel : 0, keyed);
        if (mTextures.TryGetValue(key, out TexEntry? t))
            return t;
        string resolved = skin.Length > 0 ? Skins.Resolve(map, skin, Exists, UseCustomSkins) : map;
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
            byte[] rgba = World.Rgba(bmp, false);
            if (keyed && bmp.Depth == 8)
                for (int i = 0; i < bmp.Width * bmp.Height; ++i)
                {
                    int idx = bmp.Pixels[(i / bmp.Width) * bmp.Stride + i % bmp.Width];
                    if (idx == 0)
                        rgba[i * 4 + 3] = 0;
                }
            // the stock upload packs every palette indexed bitmap (5551, or 4444 for a translucent palette, 005d7148)
            if (bmp.Depth == 8)
                PaletteTables.Pack(rgba, sub);
            t = new TexEntry
            {
                Blend = bmp.Blend, Width = bmp.Width, Height = bmp.Height,
                PaletteAlpha = bmp.Blend == BlendMode.Translucent && sub.Kind == 2,
                Texture = mDev != null ? World.CreateTexture(mDev, bmp, rgba, false, false, true, keyed) : null,
            };
        }
        mTextures[key] = t;
        return t;
    }

    // Creates the textures of every bitmap material of a shape under a skin (level 0 damage), so the first frame that draws
    // the shape does not stall on bitmap decoding and texture uploads.
    public void Preload(RenderShape shape, string skin)
    {
        foreach (RsMaterial m in shape.Materials)
            if (m.Type == 3)
                Texture(m.Name, skin, 0, m.Keyed);
    }

    // Loads the DamageSkinData bitmaps (bmpName[]) once.
    public void SetDamageDecals(IEnumerable<string> names)
    {
        var l = new List<PbmpBitmap>();
        foreach (string n in names)
            if (n.Length > 0 && World.Bitmap(n) is PbmpBitmap b)
                l.Add(b);
        DamageDecals = l;
    }

    public void Dispose()
    {
        foreach (TexEntry? t in mTextures.Values)
            t?.Texture?.Dispose();
        mTextures.Clear();
    }
}
