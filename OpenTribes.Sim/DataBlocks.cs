namespace OpenTribes.Game;

using System.Globalization;
using OpenTribes.Core;
using OpenTribes.Script;
using OpenTribes.Sim;
using static OpenTribes.Core.Fpu;

/// <summary>Holds the datablocks (docs/datablocks.md): 31 groups, declaration, typed field access, preload and network identity.</summary>
/// <remarks>Port of src/game/datablocks.cpp. It lives in OpenTribes.Sim because the C++ SimConsole holds it. The script parse and the float products and stores follow the mode of <see cref="Fpu"/>, the script FPU mode while the console runs.</remarks>
public sealed class DataBlockManager
{
    /// <summary>One element of a field. Unset elements read the class default text.</summary>
    public sealed class Value
    {
        /// <summary>True once a script wrote the element.</summary>
        public bool Set;
        /// <summary>The text of string types and the animData sequence name. Null reads as an empty string.</summary>
        public string? Str;                     // string types, animData/sequence name (null reads "")
        /// <summary>The components of float, point3F, colorF, normal, damageLevel and sqrtScaled fields.</summary>
        public readonly float[] F = new float[3];
        /// <summary>The value of int, bool, sfxFlags, reference index and sound index fields.</summary>
        public int I;
        /// <summary>The index of a projectile reference, where <see cref="I"/> is the group, or the animData direction.</summary>
        public int I2;
        /// <summary>The animData view flags (bits 0-3).</summary>
        public int I3;
        /// <summary>The animData priority.</summary>
        public int I4;
    }

    /// <summary>One field of a block: its definition and its elements.</summary>
    public sealed class Field
    {
        /// <summary>The field definition.</summary>
        public FieldDef Def;
        /// <summary>The elements, one per array entry.</summary>
        public List<Value> Elems;

        /// <summary>Creates a field with unset elements.</summary>
        /// <param name="def">The field definition.</param>
        /// <param name="count">The number of elements.</param>
        public Field(FieldDef def, int count)
        {
            Def = def;
            Elems = new List<Value>(count);
            for (var i = 0; i < count; ++i)
                Elems.Add(new Value());
        }
    }

    /// <summary>One declared datablock.</summary>
    public sealed class Block
    {
        /// <summary>The class of the block.</summary>
        public ClassDef Cls = null!;
        /// <summary>The block name.</summary>
        public string Name = "";
        /// <summary>The group the block belongs to, 0 to 30.</summary>
        public int Group;
        /// <summary>The index of the block in its group.</summary>
        public int Index;
        /// <summary>The fields, inherited first and then own, in registration order.</summary>
        public readonly List<Field> Fields = [];
        /// <summary>The shape validation CRC. <see cref="Preload"/> sets it when <c>validateShape</c> is set (section 7.1).</summary>
        public uint ValidationCrc;

        /// <summary>Finds a field by name.</summary>
        /// <param name="name">The field name.</param>
        /// <returns>The field, or null.</returns>
        public Field? Find(string name)
        {
            foreach (var f in Fields)
                if (SimClasses.EqualsNoCase(f.Def.Name, name))
                    return f;
            return null;
        }
    }

    private readonly StringTable mStrings;
    private readonly Action<string> mPrint;
    private readonly List<Block>[] mGroups = new List<Block>[31];
    private readonly Dictionary<string, Block> mNames = new(ReferenceEqualityComparer.Instance);

    // Projectile reference groups in search order (PR1, table 0x00614b50).
    private static readonly int[] kProjectileGroups = [14, 15, 16, 17, 24, 25, 28, 29];

    // src/game/datablocks.cpp RefMessages.
    private sealed record RefMessages(int Group, string UndefinedFmt, string Multi);

    // src/game/datablocks.cpp refMessages.
    private static RefMessages? RefMessagesOf(int type) => type switch
    {
        10 => new(5, "Item data block \"%s\" undefined", "Cannot set multiple args to a Item data block name."),
        11 => new(6, "ItemImage data block \"%s\" is undefined", "Cannot set multiple args to a ItemImage data block name."),
        16 => new(0, "Sound profile data block \"%s\" is undefined", "Cannot set multiple args to a sound profile data block name."),
        17 => new(1, "Sound data block \"%s\" is undefined", "Cannot set multiple args to a sound data block name."),
        18 => new(20, "Explosion data block \"%s\" is undefined", "Cannot set multiple args to a sound data block name."),
        19 => new(22, "Debris data block \"%s\" is undefined", "Cannot set multiple args to a sound data block name."),
        21 => new(2, "Damage skin data block \"%s\" is undefined", "Cannot set multiple args to a damage skin data block name."),
        _ => null,
    };

    // src/game/datablocks.cpp fmt: snprintf with one %s.
    private static string Fmt(string f, string v)
    {
        var at = f.IndexOf("%s", StringComparison.Ordinal);
        return at < 0 ? f : f[..at] + v + f[(at + 2)..];
    }

    // src/game/datablocks.cpp toFloat: atof to double, fstp single (the current mode).
    private static float ToFloat(string s) => Store(Numbers.BorlandAtof(s));

    // src/game/datablocks.cpp parseBool.
    private static bool ParseBool(string v)
    {
        if (v.Length != 0 && (v[0] == 'T' || v[0] == 't'))
            return true;
        if (v.Length != 0 && (v[0] == 'F' || v[0] == 'f'))
            return false;
        return Numbers.BorlandAtof(v) != 0;
    }

    // src/game/datablocks.cpp zeroText: text a zero-filled element of an uninitialized field reads as (D11).
    private static string ZeroText(int type) => type switch
    {
        2 or 3 or 14 or 23 => "0",
        4 => "False",
        5 or 6 or 15 => "0 0 0",
        20 => "0 0",
        13 => "(null) (null)",
        _ => "",
    };

    private static uint[]? sCrcTable;

    // src/game/datablocks.cpp crc32: CRC-32 (reflected, polynomial 0xedb88320) without final inversion (0x00588324).
    private static uint Crc32(byte[] data, uint crc)
    {
        if (sCrcTable == null)
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; ++n)
            {
                var c = n;
                for (var k = 0; k < 8; ++k)
                    c = (c & 1) != 0 ? (c >> 1) ^ 0xedb88320u : c >> 1;
                table[n] = c;
            }
            sCrcTable = table;
        }
        foreach (var b in data)
            crc = sCrcTable[(b ^ crc) & 0xff] ^ (crc >> 8);
        return crc;
    }

    private static uint ReadU32(byte[] d, long at) =>
        d[at] | ((uint)d[at + 1] << 8) | ((uint)d[at + 2] << 16) | ((uint)d[at + 3] << 24);

    private static bool BytesAre(byte[] d, long at, string s)
    {
        if (at < 0 || at + s.Length > d.Length)
            return false;
        for (var i = 0; i < s.Length; ++i)
            if (d[at + i] != (byte)s[i])
                return false;
        return true;
    }

    // src/game/datablocks.cpp shapeTextures: texture names of the shape's material list (flags & 0xf == 3), detail 0. The
    // list is the nested `PERS TS::MaterialList` block that ends the `TS::Shape` block (docs/interiors.md 8.2).
    private static List<string> ShapeTextures(byte[] d)
    {
        var output = new List<string>();
        if (d.Length < 8 || !BytesAre(d, 0, "PERS"))
            return output;
        var end = Math.Min(d.Length, 8L + ReadU32(d, 4));
        const string kName = "TS::MaterialList";
        var nameLen = kName.Length;
        for (long p = 4; p + 10 + nameLen + 12 <= end; ++p)
        {
            if (!BytesAre(d, p, "PERS") || d[p + 8] != nameLen || d[p + 9] != 0 || !BytesAre(d, p + 10, kName) ||
                p + 8 + ReadU32(d, p + 4) != end)
                continue;
            var q = p + 10 + nameLen; // even length: no pad byte
            var details = ReadU32(d, q + 4);
            var count = ReadU32(d, q + 8);
            q += 12;
            if (details == 0 || count == 0)
                return output;
            // Material record: u32 flags, ..., char[32] name at +16; 60 bytes in v3, 64 in v4.
            var size = (end - q) / ((long)details * count);
            if (size < 48)
                return output;
            for (uint i = 0; i < count; ++i)
            {
                var m = q + (i * size);
                if ((ReadU32(d, m) & 0xf) != 3)
                    continue;
                var len = 0;
                while (len < 32 && d[m + 16 + len] != 0)
                    ++len;
                output.Add(ByteString.FromBytes(d.AsSpan((int)(m + 16), len)));
            }
            return output;
        }
        return output;
    }

    /// <summary>Computes the validation CRC of a shape file, then of each texture of its material list when <paramref name="materials"/> is set. Missing files leave the CRC unchanged (section 7.1, 0044a078).</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="shape">The shape resource name.</param>
    /// <param name="materials">True to include the textures.</param>
    /// <returns>The CRC.</returns>
    public static uint ValidationCrc(ResourceManager res, string shape, bool materials)
    {
        var crc = 0xffffffff;
        if (!res.Read(shape, out var bytes))
            return crc;
        crc = Crc32(bytes, crc);
        if (!materials)
            return crc;
        foreach (var tex in ShapeTextures(bytes))
            if (res.Read(tex, out var t))
                crc = Crc32(t, crc);
        return crc;
    }

    // src/game/datablocks.cpp chain.
    private static List<ClassDef> Chain(ClassDef? c)
    {
        var output = new List<ClassDef>();
        while (c != null)
        {
            output.Insert(0, c);
            ClassDef? parent = null;
            if (c.Parent != null)
                foreach (var k in DataBlockTables.kClasses)
                    if (SimClasses.EqualsNoCase(k.Name, c.Parent))
                        parent = k;
            c = parent;
        }
        return output;
    }

    /// <summary>Creates an empty manager.</summary>
    /// <param name="strings">The string table for block names.</param>
    /// <param name="print">Receives console messages.</param>
    public DataBlockManager(StringTable strings, Action<string> print)
    {
        mStrings = strings;
        mPrint = print;
        for (var i = 0; i < 31; ++i)
            mGroups[i] = [];
    }

    /// <summary>Finds a datablock class by name, ignoring case.</summary>
    /// <param name="name">The class name.</param>
    /// <returns>The class, or null.</returns>
    public static ClassDef? FindClass(string name)
    {
        foreach (var c in DataBlockTables.kClasses)
            if (SimClasses.EqualsNoCase(c.Name, name))
                return c;
        return null;
    }

    /// <summary>Finds a block by name.</summary>
    /// <param name="name">The block name, ignoring case.</param>
    /// <returns>The block, or null.</returns>
    public Block? Find(string? name)
    {
        if (name == null)
            return null;
        var key = mStrings.Lookup(name);
        return key != null && mNames.TryGetValue(key, out var b) ? b : null;
    }

    /// <summary>Gets a block by group and index.</summary>
    /// <param name="group">The group, 0 to 30.</param>
    /// <param name="index">The index in the group.</param>
    /// <returns>The block, or null when out of range.</returns>
    public Block? At(int group, int index)
    {
        if (group < 0 || group > 30 || index < 0 || index >= mGroups[group].Count)
            return null;
        return mGroups[group][index];
    }

    /// <summary>Gets the number of blocks in a group.</summary>
    /// <param name="group">The group, 0 to 30.</param>
    /// <returns>The count.</returns>
    public int GroupSize(int group) => mGroups[group].Count;

    /// <summary>Visits every block, groups 0 to 30, each in index order.</summary>
    /// <param name="f">The visitor.</param>
    public void ForEach(Action<Block> f)
    {
        foreach (var g in mGroups)
            foreach (var b in g)
                f(b);
    }

    /// <summary>Declares a block, as <c>declareDataBlock</c> does (DD1-DD5).</summary>
    /// <param name="className">The class name.</param>
    /// <param name="name">The block name.</param>
    /// <returns>False when nothing was created: the name exists, or the class is unknown or has no factory.</returns>
    public bool Declare(string className, string name)
    {
        if (Find(name) != null)
        {
            mPrint("warning: data block " + mStrings.Intern(name) + " already declared");
            return false;
        }
        var cls = FindClass(className);
        if (cls == null || !cls.Creatable || cls.Group < 0)
        {
            // Abstract classes and those without a factory crash the original (D11).
            mPrint("Unknown class " + className);
            return false;
        }
        var b = new Block { Cls = cls, Name = mStrings.Intern(name), Group = cls.Group, Index = mGroups[cls.Group].Count };
        foreach (var c in Chain(cls))
            foreach (var f in c.Fields)
                b.Fields.Add(new Field(f, f.Count));
        // ItemData and MineData constructors write a different description (5, notes).
        if (cls.Group is 5 or 23)
            if (b.Find("description") is Field d)
            {
                d.Elems[0].Set = true;
                d.Elems[0].Str = mStrings.Intern("Item"); // stringCS keeps this spelling
            }
        mNames[b.Name] = b;
        mGroups[cls.Group].Add(b);
        return true;
    }

    // src/game/datablocks.cpp DataBlockManager::lookupInGroup.
    private int LookupInGroup(int group, string name)
    {
        var g = mGroups[group];
        for (var i = 0; i < g.Count; ++i)
            if (SimClasses.EqualsNoCase(g[i].Name, name))
                return i;
        return -1;
    }

    // src/game/datablocks.cpp DataBlockManager::refName.
    private string RefName(int group, int index) => At(group, index)?.Name ?? "";

    // src/game/datablocks.cpp DataBlockManager::read.
    private string Read(Field f, int i)
    {
        var v = f.Elems[i];
        var type = f.Def.Type;
        if (!v.Set)
        {
            if (f.Def.Uninit)
                return ZeroText(type);
            if (f.Def.DefaultText == "@index0")
                return RefName(20, 0);
            return f.Def.DefaultText;
        }
        return type switch
        {
            0 or 1 or 9 => v.Str ?? "",
            2 or 14 => Numbers.FormatInt(v.I),
            3 => Numbers.FormatFloat(v.F[0]),
            4 => v.I != 0 ? "True" : "False",
            5 or 6 or 15 => Numbers.FormatFloat(v.F[0]) + " " + Numbers.FormatFloat(v.F[1]) + " " + Numbers.FormatFloat(v.F[2]),
            10 or 11 or 16 or 17 or 18 or 19 or 21 => v.I < 0 ? "" : RefName(RefMessagesOf(type)!.Group, v.I),
            12 => v.I < 0 ? "" : RefName(v.I2, v.I),// Original bug: group and index are swapped in the lookup (PR2).
            13 => (v.Str ?? "(null)") + " " + (v.I < 0 ? "(null)" : RefName(1, v.I)),
            20 => Numbers.FormatFloat(v.F[0]) + " " + Numbers.FormatFloat(v.F[1]),
            23 => Numbers.FormatFloat(Store(Mul(Mul(v.F[0], v.F[0]), 0.05))),
            _ => "",
        };
    }

    // src/game/datablocks.cpp DataBlockManager::write.
    private void Write(Field f, int i, IReadOnlyList<string> vals)
    {
        var v = f.Elems[i];
        var type = f.Def.Type;
        bool Single(string what)
        {
            if (vals.Count == 1)
                return true;
            mPrint("Cannot set multiple args to a single " + what + ".");
            return false;
        }
        void StartFromCurrent()
        {
            // point3F keeps unmatched components: begin from the current value.
            if (!v.Set)
                CScan.Floats(Read(f, i), v.F);
        }
        switch (type)
        {
            case 0:
            case 1:
                if (!Single("string"))
                    return;
                v.Str = type == 0 ? mStrings.Intern(vals[0]) : vals[0];
                break;
            case 2:
                if (!Single("int"))
                    return;
                v.I = Numbers.BorlandAtoi(vals[0]);
                break;
            case 3:
                if (!Single("float"))
                    return;
                v.F[0] = ToFloat(vals[0]);
                break;
            case 4:
                if (!Single("float")) // sic: the bool setter reuses the float message
                    return;
                v.I = ParseBool(vals[0]) ? 1 : 0;
                break;
            case 5:
            case 15:
                if (vals.Count == 1)
                {
                    StartFromCurrent();
                    CScan.Floats(vals[0], v.F);
                }
                else if (vals.Count == 3)
                {
                    for (var k = 0; k < 3; ++k)
                        v.F[k] = ToFloat(vals[k]);
                }
                else
                {
                    mPrint(type == 5 ? "Point3F must be set as { x, y, z } or \"x y z\""
                                     : "Normal Vector must be set as { x, y, z } or \"x y z\"");
                    return;
                }
                if (type == 15)
                {
                    var len = SqrtF(AddF(AddF(MulF(v.F[0], v.F[0]), MulF(v.F[1], v.F[1])), MulF(v.F[2], v.F[2])));
                    var inv = DivF(1.0f, len);
                    for (var k = 0; k < 3; ++k)
                        v.F[k] = MulF(v.F[k], inv);
                }
                break;
            case 6:
                if (vals.Count != 3)
                {
                    mPrint("ColorF must be set as { red, green, blue }");
                    return;
                }
                for (var k = 0; k < 3; ++k)
                    v.F[k] = ToFloat(vals[k]);
                break;
            case 9:
                if (vals.Count != 8)
                {
                    mPrint("Animdata set as { Name, soundTag, direction, viewFlags, priority }");
                    return;
                }
                v.Str = mStrings.Intern(vals[0]);
                v.I = SimClasses.EqualsNoCase(vals[1], "none") ? -1 : LookupInGroup(1, vals[1]);
                v.I2 = Numbers.BorlandAtoi(vals[2]);
                v.I3 = 0;
                for (var k = 0; k < 4; ++k)
                    if (ParseBool(vals[3 + k]))
                        v.I3 |= 1 << k;
                v.I4 = Numbers.BorlandAtoi(vals[7]);
                break;
            case 10:
            case 11:
            case 16:
            case 17:
            case 18:
            case 19:
            case 21:
            {
                var m = RefMessagesOf(type)!;
                if (vals.Count != 1)
                {
                    mPrint(m.Multi);
                    return;
                }
                v.I = LookupInGroup(m.Group, vals[0]);
                if (v.I < 0)
                    mPrint(Fmt(m.UndefinedFmt, vals[0]));
                break;
            }
            case 12:
                if (vals.Count != 1)
                {
                    mPrint("Cannot set multiple args to a Projectile data block name.");
                    return;
                }
                v.I = v.I2 = -1;
                foreach (var g in kProjectileGroups)
                {
                    var idx = LookupInGroup(g, vals[0]);
                    if (idx >= 0)
                    {
                        v.I = g;
                        v.I2 = idx;
                        break;
                    }
                }
                if (v.I < 0)
                    mPrint("Projectile data block \"" + vals[0] + "\" is undefined");
                break;
            case 13:
                if (vals.Count != 2)
                {
                    mPrint("Sequence sound is set as { sequenceName, soundTag }");
                    return;
                }
                v.I = LookupInGroup(1, vals[1]);
                v.Str = v.I < 0 ? null : mStrings.Intern(vals[0]);
                if (v.I < 0)
                    mPrint("Sound data block \"" + vals[0] + "\" is undefined"); // prints the sequence name (sic)
                break;
            case 14:
            {
                var flags = 0;
                (string, int)[] kNames =
                [
                    ("SFX_IS_LOOPING", 2), ("SFX_IS_STREAMING", 4), ("SFX_IS_PITCHING", 8), ("SFX_IS_SOFTWARE_3D", 16),
                    ("SFX_IS_HARDWARE_3D", 32),
                ];
                foreach (var s in vals)
                {
                    var n = Numbers.BorlandAtoi(s);
                    if (n != 0)
                    {
                        flags |= n;
                        continue;
                    }
                    foreach ((var name, var bit) in kNames)
                        if (SimClasses.EqualsNoCase(name, s))
                            flags |= bit;
                }
                v.I = flags;
                break;
            }
            case 20:
                if (vals.Count != 2)
                {
                    mPrint("Damage level must be set as { disable, destroyed }");
                    return;
                }
                v.F[0] = ToFloat(vals[0]);
                v.F[1] = ToFloat(vals[1]);
                break;
            case 23:
                if (!Single("float"))
                    return;
                v.F[0] = Store(Sqrt(Mul(Math.Abs((double)ToFloat(vals[0])), 20.0)));
                break;
            default:
                return;
        }
        v.Set = true;
    }

    /// <summary>Parses the default of an unset float field: the constant its datablock constructor stores, which is the nearest float to the default text (docs/datablocks.md 3.1), whatever the current rounding mode.</summary>
    /// <param name="text">The default text.</param>
    /// <param name="start">The index to start at.</param>
    /// <param name="end">Receives the index after the number, or <paramref name="start"/> when there is none.</param>
    /// <returns>The value, 0 when there is no number.</returns>
    public static float CtorFloat(string text, int start, out int end)
    {
        var i = start;
        while (i < text.Length && (text[i] == ' ' || (text[i] >= '\t' && text[i] <= '\r')))
            ++i;
        var s = i;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
            ++i;
        var digits = 0;
        while (i < text.Length && text[i] >= '0' && text[i] <= '9')
        {
            ++i;
            ++digits;
        }
        if (i < text.Length && text[i] == '.')
        {
            ++i;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9')
            {
                ++i;
                ++digits;
            }
        }
        if (digits == 0)
        {
            end = start;
            return 0;
        }
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            var j = i + 1;
            if (j < text.Length && (text[j] == '+' || text[j] == '-'))
                ++j;
            if (j < text.Length && text[j] >= '0' && text[j] <= '9')
            {
                while (j < text.Length && text[j] >= '0' && text[j] <= '9')
                    ++j;
                i = j;
            }
        }
        end = i;
        return float.Parse(text[s..i], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Parses the number at the start of <paramref name="text"/> like <see cref="CtorFloat(string,int,out int)"/>.</summary>
    /// <param name="text">The default text.</param>
    /// <returns>The value.</returns>
    public static float CtorFloat(string text) => CtorFloat(text, 0, out _);

    /// <summary>Reads a field as the text a script read prints.</summary>
    /// <param name="b">The block.</param>
    /// <param name="field">The field name.</param>
    /// <param name="index">The array index, or empty or -1 for a single value.</param>
    /// <param name="output">Receives the text.</param>
    /// <returns>False for an unknown field or an index out of range.</returns>
    public bool GetField(Block b, string field, string index, out string output)
    {
        output = "";
        var f = b.Find(field);
        if (f == null)
        {
            mPrint("No such field: " + field + " in block type " + b.Name);
            return false;
        }
        var count = f.Def.Count;
        if (index.Length == 0 || index == "-1")
        {
            if (count != 1)
                return false;
            output = Read(f, 0);
            return true;
        }
        var i = Numbers.BorlandAtoi(index);
        if (i < 0 || i >= count)
            return false;
        output = Read(f, i);
        return true;
    }

    /// <summary>Writes a field from script values. Unknown fields are ignored (FA5).</summary>
    /// <param name="b">The block.</param>
    /// <param name="field">The field name.</param>
    /// <param name="values">The values to parse.</param>
    /// <param name="index">The array index, or empty or -1 for the whole field.</param>
    public void SetField(Block b, string field, IReadOnlyList<string> values, string index)
    {
        var f = b.Find(field);
        if (f == null)
            return; // FA5: unknown fields are ignored on write
        var count = f.Def.Count;
        if (index.Length == 0 || index == "-1")
        {
            if (count == 1)
            {
                Write(f, 0, values);
                return;
            }
            if (values.Count == count)
            {
                for (var i = 0; i < count; ++i)
                    Write(f, i, new[] { values[i] });
                return;
            }
            mPrint("setDataField: array set access out of range.");
            return;
        }
        var at = Numbers.BorlandAtoi(index);
        if (count >= 2 && at >= 0 && at < count)
        {
            Write(f, at, values);
            return;
        }
        mPrint("setDataField: array set access out of range.");
    }

    /// <summary>Checks that the shapes the blocks name exist and computes the validation CRCs.</summary>
    /// <param name="res">The resource manager.</param>
    /// <returns>False if any preload failed.</returns>
    /// <remarks>Follows preloadServerDataBlocks (section 7).</remarks>
    public bool Preload(ResourceManager res)
    {
        var ok = true;
        static string Text(Block b, string field)
        {
            var f = b.Find(field);
            return f != null && f.Elems[0].Set && f.Elems[0].Str != null ? f.Elems[0].Str! : "";
        }
        static bool Flag(Block b, string field)
        {
            var f = b.Find(field);
            return f != null && f.Elems[0].Set && f.Elems[0].I != 0;
        }
        const string kMissing = ".dts.  The server has new art data you are missing.  Check server info for possible download locations.";
        for (var g = 0; g <= 30; ++g)
        {
            foreach (var b in mGroups[g])
            {
                var cls = b.Cls.Name;
                var shapeBase = b.Find("shapeFile") != null && b.Find("maxDamage") != null;
                if (shapeBase)
                {
                    var shield = Text(b, "shieldShapeName");
                    if (shield.Length != 0 && res.Find(shield + ".dts") == null)
                    {
                        mPrint("Unable to model: " + shield + kMissing);
                        ok = false;
                        continue;
                    }
                    var shape = Text(b, "shapeFile");
                    if (shape.Length != 0)
                    {
                        if (res.Find(shape + ".dts") == null)
                        {
                            mPrint("Unable to model: " + shape + kMissing);
                            ok = false;
                            continue;
                        }
                        if (Flag(b, "validateShape"))
                        {
                            b.ValidationCrc = ValidationCrc(res, shape + ".dts", Flag(b, "validateMaterials"));
                            mPrint("Server: Validation required for " + shape + ".dts");
                        }
                    }
                }
                else if (cls == "ItemImageData")
                {
                    var shape = Text(b, "shapeFile"); // NULL crashes the original (D11)
                    if (shape.Length != 0 && res.Find(shape + ".dts") == null)
                    {
                        mPrint("Unable to load ItemImageData shape: " + shape + ".dts");
                        ok = false;
                    }
                }
                else if (b.Find("bulletShapeName") != null || cls == "ExplosionData")
                {
                    var explosion = cls == "ExplosionData";
                    var shape = Text(b, explosion ? "shapeName" : "bulletShapeName");
                    if (shape.Length != 0)
                    {
                        if (res.Find(shape) == null)
                        {
                            mPrint((explosion ? "Unable to load ExplosionData shape: " : "Unable to load ProjectileData shape: ") + shape);
                            ok = false;
                            continue;
                        }
                        if (Flag(b, "validateShape"))
                        {
                            b.ValidationCrc = ValidationCrc(res, shape, Flag(b, "validateMaterials"));
                            mPrint("Server: Validation required for " + shape);
                        }
                    }
                }
            }
        }
        return ok;
    }
}
