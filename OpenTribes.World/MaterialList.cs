namespace OpenTribes.World;

using System.Buffers.Binary;
using OpenTribes.Core;

/// <summary>The surface properties of one material in a .dml material list.</summary>
/// <remarks>Port of src/world/material_list.cpp (docs/collision.md E6, 4.2).</remarks>
public sealed class Material
{
    /// <summary>The surface type, 0 to 14.</summary>
    public int Type;
    /// <summary>The elasticity (entry +0x3c).</summary>
    public float Elasticity = 1.0f;
    /// <summary>The friction (entry +0x40).</summary>
    public float Friction = 1.0f;
    /// <summary>Whether the surface type's table values apply instead of this material's own (entry +0x44).</summary>
    public bool UseDefaults = true;
}

/// <summary>The process-wide table of friction and elasticity per surface type, written by setMaterialProperty (006c1ff0).</summary>
public sealed class MaterialTable
{
    /// <summary>The friction and the elasticity of each of the 15 surface types, as two arrays.</summary>
    public readonly float[] Friction = new float[15], Elasticity = new float[15];

    /// <summary>Creates a table with every entry 1.0.</summary>
    public MaterialTable() => Reset();

    /// <summary>Sets every entry to 1.0.</summary>
    public void Reset()
    {
        for (var i = 0; i < 15; ++i)
            Friction[i] = Elasticity[i] = 1.0f;
    }

    /// <summary>Gets the friction of a material: the table entry of its type unless it carries its own value (005afae8).</summary>
    /// <param name="m">The material.</param>
    /// <returns>The friction.</returns>
    public float FrictionOf(Material m) => m.UseDefaults ? Friction[ClampType(m.Type)] : m.Friction;
    /// <summary>Gets the elasticity of a material: the table entry of its type unless it carries its own value (005afac8).</summary>
    /// <param name="m">The material.</param>
    /// <returns>The elasticity.</returns>
    public float ElasticityOf(Material m) => m.UseDefaults ? Elasticity[ClampType(m.Type)] : m.Elasticity;

    private static int ClampType(int t) => t is < 0 or > 14 ? 0 : t;
}

/// <summary>Reads .dml material lists and loads them through the resource manager.</summary>
public static class MaterialList
{
    /// <summary>The 15 surface type names in order (00614b10).</summary>
    public static readonly string[] kMaterialTypeNames =
    [
        "Default", "Concrete", "Carpet", "Metal", "Glass", "Plastic", "Wood", "Marble", "Snow", "Ice",
        "Sand", "Mud", "Stone", "SoftEarth", "PackedEarth",
    ];

    private static bool Take(byte[] d, ref long pos, int size, out ReadOnlySpan<byte> s)
    {
        s = default;
        if (pos + size > d.Length)
            return false;
        s = d.AsSpan((int)pos, size);
        pos += size;
        return true;
    }

    /// <summary>Parses a PERS TS::MaterialList block, versions 1 to 4.</summary>
    /// <param name="d">The file bytes.</param>
    /// <param name="output">Receives the materials.</param>
    /// <returns>False on malformed data.</returns>
    public static bool ParseMaterialList(byte[] d, List<Material> output)
    {
        // PERS tag, size, u16 name length, name padded to even, u32 version (docs/interiors.md 2).
        if (d.Length < 12 || d[0] != 'P' || d[1] != 'E' || d[2] != 'R' || d[3] != 'S')
            return false;
        long pos = 8;
        if (!Take(d, ref pos, 2, out var s))
            return false;
        var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(s);
        pos += nameLen + (nameLen & 1);
        if (!Take(d, ref pos, 4, out s))
            return false;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(s);
        if (version is < 1 or > 4)
            return false;
        if (!Take(d, ref pos, 4, out s))
            return false;
        var details = BinaryPrimitives.ReadUInt32LittleEndian(s);
        if (!Take(d, ref pos, 4, out s))
            return false;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(s);
        // Entry bytes: v1, v2 0x30; v3 0x3c; v4 0x40 (docs/collision.md 4.2).
        long entry = version <= 2 ? 0x30 : version == 3 ? 0x3c : 0x40;
        if (details > 4096 || count > 65536 || pos + ((long)details * count * entry) > d.Length)
            return false;
        output.Clear();
        for (long i = 0; i < (long)details * count; ++i)
        {
            var m = new Material();
            var e = pos + (i * entry);
            if (version >= 3)
            {
                m.Type = Reader.I32At(d, e + 0x30);
                m.Elasticity = Reader.F32At(d, e + 0x34);
                m.Friction = Reader.F32At(d, e + 0x38);
            }
            else
            {
                m.Type = 0;
            }
            if (version == 4)
            {
                var flag = Reader.U32At(d, e + 0x3c);
                m.UseDefaults = flag != 0;
            }
            else
            {
                m.UseDefaults = true;
            }
            output.Add(m);
        }
        return true;
    }

    /// <summary>Lists the bitmap names of the texture materials of every detail, in order.</summary>
    /// <param name="d">The file bytes.</param>
    /// <param name="output">Receives the bitmap names.</param>
    /// <returns>False on malformed data.</returns>
    /// <remarks>A texture material has flags &amp; 0xf equal to 3 and its map name at +0x10 of the file record. TSMaterialList_load (005af87c) loads these per entry.</remarks>
    public static bool MaterialListTextures(byte[] d, List<string> output)
    {
        if (d.Length < 12 || d[0] != 'P' || d[1] != 'E' || d[2] != 'R' || d[3] != 'S')
            return false;
        long pos = 8;
        if (!Take(d, ref pos, 2, out var s))
            return false;
        var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(s);
        pos += nameLen + (nameLen & 1);
        if (!Take(d, ref pos, 4, out s))
            return false;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(s);
        if (version < 1 || version > 4 || !Take(d, ref pos, 4, out s))
            return false;
        var details = BinaryPrimitives.ReadUInt32LittleEndian(s);
        if (!Take(d, ref pos, 4, out s))
            return false;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(s);
        long entry = version <= 2 ? 0x30 : version == 3 ? 0x3c : 0x40;
        if (details > 4096 || count > 65536 || pos + ((long)details * count * entry) > d.Length)
            return false;
        output.Clear();
        for (long i = 0; i < (long)details * count; ++i)
        {
            var e = pos + (i * entry);
            if ((Reader.U32At(d, e) & 0xf) == 3)
                output.Add(Reader.CString(d, e + 0x10, 0x20));
        }
        return true;
    }

    private sealed class Cached
    {
        public bool Ok;
        public readonly List<Material> List = [];
    }
    private static readonly Dictionary<string, Cached> sCache = new(StringComparer.Ordinal);

    /// <summary>Empties the by-name cache, the start state of a C++ test process.</summary>
    public static void ResetCache() => sCache.Clear();

    /// <summary>Loads a material list through the resource manager.</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="name">The file name.</param>
    /// <returns>The materials, or null when missing or malformed. Cached by name for the process.</returns>
    public static List<Material>? Get(ResourceManager res, string name)
    {
        if (!sCache.TryGetValue(name, out var c))
        {
            c = new Cached();
            c.Ok = res.Read(name, out var data) && ParseMaterialList(data, c.List);
            sCache.Add(name, c);
        }
        return c.Ok ? c.List : null;
    }
}
