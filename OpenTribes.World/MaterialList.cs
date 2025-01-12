// Port of src/world/material_list.hpp and src/world/material_list.cpp: TS::MaterialList (.dml) surface properties
// (docs/collision.md E6, 4.2): the type, the elasticity and the friction of each material, and the process-wide table
// that setMaterialProperty writes (0x006c1ff0).
using System.Buffers.Binary;
using OpenTribes.Core;

namespace OpenTribes.World;

// src/world/material_list.hpp struct Material.
public sealed class Material
{
    public int Type;               // surface type 0..14
    public float Elasticity = 1.0f; // entry +0x3c
    public float Friction = 1.0f;   // entry +0x40
    public bool UseDefaults = true; // entry +0x44
}

// src/world/material_list.hpp struct MaterialTable.
public sealed class MaterialTable
{
    public readonly float[] Friction = new float[15], Elasticity = new float[15];

    public MaterialTable() => Reset();

    public void Reset()
    {
        for (int i = 0; i < 15; ++i)
            Friction[i] = Elasticity[i] = 1.0f;
    }

    // Accessors 0x005afae8 and 0x005afac8: the table entry of the type unless the material carries its own values.
    public float FrictionOf(Material m) => m.UseDefaults ? Friction[ClampType(m.Type)] : m.Friction;
    public float ElasticityOf(Material m) => m.UseDefaults ? Elasticity[ClampType(m.Type)] : m.Elasticity;

    private static int ClampType(int t) => t < 0 || t > 14 ? 0 : t;
}

// The free functions of src/world/material_list.cpp.
public static class MaterialList
{
    // src/world/material_list.cpp kMaterialTypeNames: the 15 surface types of 0x00614b10 in order.
    public static readonly string[] kMaterialTypeNames =
    {
        "Default", "Concrete", "Carpet", "Metal", "Glass", "Plastic", "Wood", "Marble", "Snow", "Ice",
        "Sand", "Mud", "Stone", "SoftEarth", "PackedEarth",
    };

    private static bool Take(byte[] d, ref long pos, int size, out ReadOnlySpan<byte> s)
    {
        s = default;
        if (pos + size > d.Length)
            return false;
        s = d.AsSpan((int)pos, size);
        pos += size;
        return true;
    }

    // src/world/material_list.cpp parseMaterialList: a PERS TS::MaterialList block (versions 1 to 4). False on
    // malformed data.
    public static bool ParseMaterialList(byte[] d, List<Material> output)
    {
        // PERS tag, size, u16 name length, name padded to even, u32 version (docs/interiors.md 2).
        if (d.Length < 12 || d[0] != 'P' || d[1] != 'E' || d[2] != 'R' || d[3] != 'S')
            return false;
        long pos = 8;
        if (!Take(d, ref pos, 2, out var s))
            return false;
        ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(s);
        pos += nameLen + (nameLen & 1);
        if (!Take(d, ref pos, 4, out s))
            return false;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(s);
        if (version < 1 || version > 4)
            return false;
        if (!Take(d, ref pos, 4, out s))
            return false;
        uint details = BinaryPrimitives.ReadUInt32LittleEndian(s);
        if (!Take(d, ref pos, 4, out s))
            return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(s);
        // Entry bytes: v1, v2 0x30; v3 0x3c; v4 0x40 (docs/collision.md 4.2).
        long entry = version <= 2 ? 0x30 : version == 3 ? 0x3c : 0x40;
        if (details > 4096 || count > 65536 || pos + (long)details * count * entry > d.Length)
            return false;
        output.Clear();
        for (long i = 0; i < (long)details * count; ++i)
        {
            var m = new Material();
            long e = pos + i * entry;
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
                uint flag = Reader.U32At(d, e + 0x3c);
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

    private sealed class Cached
    {
        public bool Ok;
        public readonly List<Material> List = new();
    }
    private static readonly Dictionary<string, Cached> sCache = new(StringComparer.Ordinal);

    // src/world/material_list.cpp materialList: loads "<name>" through the resource manager; null when missing or
    // malformed. Cached by name for the process.
    public static List<Material>? Get(ResourceManager res, string name)
    {
        if (!sCache.TryGetValue(name, out Cached? c))
        {
            c = new Cached();
            c.Ok = res.Read(name, out byte[] data) && ParseMaterialList(data, c.List);
            sCache.Add(name, c);
        }
        return c.Ok ? c.List : null;
    }
}
