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
    private readonly Dictionary<string, TexEntry?> mTextures = new(StringComparer.OrdinalIgnoreCase);
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
        string key = map + "|" + skin + "|" + (damageLevel > 0 ? damageLevel : 0) + (keyed ? "|k" : "");
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
            t = new TexEntry
            {
                Blend = bmp.Blend, Width = bmp.Width, Height = bmp.Height,
                PaletteAlpha = bmp.Blend == BlendMode.Translucent && sub.Kind == 2,
                Texture = mDev?.CreateTexture3D(rgba, bmp.Width, bmp.Height, new TextureOptions(true, true, true)),
            };
        }
        mTextures[key] = t;
        return t;
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
