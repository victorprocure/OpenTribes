namespace OpenTribes.Gui;

using System.Text;

/// <summary>One row of the GUI class table (docs/gui.md 3.1).</summary>
/// <param name="Name">The class name.</param>
/// <param name="Tag">The four character file tag, empty for a class stored by name.</param>
/// <param name="Parent">The parent class name.</param>
/// <param name="Size">The object size of the stock class.</param>
/// <param name="Load">The address of the load routine, as hex text.</param>
public sealed record GuiClassRow(string Name, string Tag, string Parent, int Size, string Load);

/// <summary>The error of reading a `.gui` file.</summary>
public sealed class GuiLoadException : Exception
{
    /// <summary>The error code: 1 short block, 2 unknown or unusable class, 3 bad control body, 4 block not read to its end.</summary>
    public int Code;
    /// <summary>Creates an exception.</summary>
    /// <param name="code">The error code.</param>
    /// <param name="message">The message.</param>
    public GuiLoadException(int code, string message) : base(message) => Code = code;
}

/// <summary>Lookups over the table of all GUI classes (docs/gui.md 3.1).</summary>
public static partial class GuiClassTable
{
    private static Dictionary<string, GuiClassRow>? sByTag, sByName;

    /// <summary>Finds a class by its file tag.</summary>
    /// <param name="tag">The four character tag.</param>
    /// <returns>The row, or null.</returns>
    public static GuiClassRow? ByTag(string tag)
    {
        sByTag ??= Rows.Where(r => r.Tag.Length > 0).ToDictionary(r => r.Tag, StringComparer.Ordinal);
        return sByTag.GetValueOrDefault(tag);
    }

    /// <summary>Finds a class by name, case insensitive.</summary>
    /// <param name="name">The class name. The scripts also name the classes without the SimGui:: or FearGui:: namespace.</param>
    /// <returns>The row, or null.</returns>
    public static GuiClassRow? ByName(string name)
    {
        sByName ??= Rows.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        if (sByName.TryGetValue(name, out GuiClassRow? row))
            return row;
        foreach (var ns in new[] { "SimGui::", "FearGui::" })
            if (sByName.TryGetValue(ns + name, out row))
                return row;
        return null;
    }

    /// <summary>Returns whether a class is a control: everything but the delegates and the canvas.</summary>
    /// <param name="r">The class row.</param>
    /// <returns>True for a control class.</returns>
    public static bool IsControl(GuiClassRow r) => !r.Name.EndsWith("Delegate", StringComparison.Ordinal) && r.Name != "SimGui::Canvas";
}

/// <summary>A little endian cursor over a block body.</summary>
public sealed class GuiBytes
{
    /// <summary>The bytes of the file.</summary>
    public readonly byte[] Data;
    /// <summary>The read position.</summary>
    public int Pos;
    /// <summary>The end of the block body.</summary>
    public int End;
    /// <summary>Creates a cursor.</summary>
    /// <param name="data">The bytes of the file.</param>
    /// <param name="pos">The start of the block body.</param>
    /// <param name="end">The end of the block body.</param>
    public GuiBytes(byte[] data, int pos, int end) { Data = data; Pos = pos; End = end; }

    private void Need(int n)
    {
        if (Pos + n > End)
            throw new GuiLoadException(1, "read past the end of the block");
    }

    /// <summary>Reads one byte.</summary>
    /// <returns>The byte.</returns>
    public byte U8() { Need(1); return Data[Pos++]; }
    /// <summary>Returns the next byte without reading it.</summary>
    /// <returns>The byte.</returns>
    public byte Peek() { Need(1); return Data[Pos]; }

    /// <summary>Reads an unsigned 32 bit integer.</summary>
    /// <returns>The value.</returns>
    public uint U32()
    {
        Need(4);
        var v = BitConverter.ToUInt32(Data, Pos);
        Pos += 4;
        return v;
    }

    /// <summary>Reads a signed 32 bit integer.</summary>
    /// <returns>The value.</returns>
    public int I32() => (int)U32();

    /// <summary>Reads a string with a one byte length prefix.</summary>
    /// <returns>The string.</returns>
    public string Pstr()
    {
        int n = U8();
        Need(n);
        var s = Encoding.Latin1.GetString(Data, Pos, n);
        Pos += n;
        return s;
    }

    /// <summary>Reads a string in a fixed 81 byte buffer.</summary>
    /// <returns>The text up to the first zero byte.</returns>
    public string Buf81()
    {
        Need(81);
        var n = Array.IndexOf(Data, (byte)0, Pos, 81);
        var s = Encoding.Latin1.GetString(Data, Pos, (n < 0 ? Pos + 81 : n) - Pos);
        Pos += 81;
        return s;
    }

    /// <summary>Reads a four character tag.</summary>
    /// <returns>The tag.</returns>
    public string Tag4()
    {
        Need(4);
        var s = Encoding.Latin1.GetString(Data, Pos, 4);
        Pos += 4;
        return s;
    }
}

/// <summary>The `.gui` object file reader (docs/gui.md 1).</summary>
/// <remarks>It reads the class tags, the block header (1.2), the SimGui::Control body (1.3) and one body routine per load address of 1.4. Routines are written as op lists that <see cref="GuiWriter"/> reads the same way to write.</remarks>
public static class GuiLoader
{
    /// <summary>The body of each load routine in read order (docs/gui.md 1.4).</summary>
    /// <remarks>`u8 x`, `u32 x`, `i32 x`, `pstr x` and `buf81 x` store a named field, `skip32` and `skip81` read and drop, `@R` runs routine R (a control body ends with @Control).</remarks>
    public static readonly Dictionary<string, string[]> Routines = new()
    {
        ["Active"] = ["u8 active", "u32 messageTag", "@Control"],
        ["SimpleText"] = ["skip32", "skip32", "u32 disabledFontTag", "u32 fontTag", "u32 highlightFontTag", "u32 textTag", "buf81 text", "u32 alignment", "u32 textVOffset", "@Active"],
        ["TextEdit"] = ["u32 numbersOnly", "skip32", "skip32", "u32 maxLength", "@SimpleText"],
        ["Chat"] = ["u32 chatA", "u32 chatB", "u32 chatC", "u32 chatD", "@TextEdit"],
        ["Check"] = ["u8 checked", "@SimpleText"],
        ["Bitmap"] = ["u32 bitmapTag", "skip81", "u8 transparent", "@Active"],
        ["FGBitmap"] = ["@Bitmap", "u32 translucency"],
        ["Palette"] = ["u32 paletteTag", "skip81", "@Control"],
        ["Progress"] = ["u32 progressA", "u32 progressB", "u32 progressC", "@Active"],
        ["Timer"] = ["u32 messageTag", "u32 initialAlarm", "u32 repeatAlarm", "@Control"],
        ["BitmapBox"] = ["u32 boxValue", "@Control"],
        ["Scroll"] = ["u32 pbaTag", "u8 handleArrowKeys", "u32 horizontalPolicy", "u32 verticalPolicy", "u8 constantThumb", "@Control"],
        ["Matrix"] = ["u32 headerX", "u32 headerY", "@Scroll"],
        ["FGScroll"] = ["skip32", "u32 style", "@Matrix"],
        ["HudMatrix"] = ["u32 lowResX", "u32 lowResY", "u32 lowResW", "u32 lowResH", "@Matrix"],
        ["TextList"] = ["u32 fontTag", "u32 highlightFontTag", "u32 textVOffset", "u8 enumerate", "u8 noDuplicates", "@Active"],
        ["TextWrap"] = ["skip32", "u32 fontTag", "u32 disabledFontTag", "u32 textTag", "u32 lineSpacing", "u32 alignment", "@Active"],
        ["Slider"] = ["u32 minValue", "u32 maxValue", "u32 value", "@Active"],
        ["FGSlider"] = ["skip32", "u32 sliderVersion", "@Slider"],
        ["Animate"] = ["u32 pbaTag", "u32 frameTime", "u32 continuous", "u32 loopWait", "skip32", "skip32", "skip32", "skip32", "skip32", "skip32", "skip32", "skip32", "@Active"],
        ["TSControl"] = ["u8 produce3dEvents", "@Control"],
        ["IrcScroll"] = ["u8 rotation", "@Active"],
        ["MenuItem"] = ["u32 tabPage", "u8 tabSet", "u8 expanded", "@SimpleText"],
        ["ShapeView"] = ["pstr shapeFile", "@Active"],
        ["Dialog"] = ["u32 maxWidth", "u32 maxHeight", "@Control"],
        ["AbortDlg"] = ["u32 abortA", "u32 abortB", "@Dialog"],
        ["DlgBox"] = ["skip32", "@Active"],
        ["ChatDisplay"] = ["u32 offsetX", "u32 offsetY", "u32 lines", "u32 visibleFlag", "u32 chatE", "@Hud"],
        ["ChatMenu"] = ["skip32", "pstr menuName", "@Control"],
        ["Hud"] = ["u32 hudX", "u32 hudY", "u32 hudW", "u32 hudH", "u32 hudVisible", "@Control"],
        ["Combo"] = ["skip32", "pstr popupName", "@SimpleText"],
        ["Radio"] = ["u8 selected", "@Active"],
        ["Switch"] = ["u32 targetId", "u32 switchB", "u32 switchC", "u8 switchD", "@Active"],
        ["TabMenu"] = ["u32 tabMenuA", "@Active"],
        ["Tab"] = ["u32 tabPage", "u32 tabSet", "@SimpleText"],
        ["TextFormat"] = ["skip32", "@Active"],
        ["CFG"] = ["skip32", "pstr actionMap", "u32 makeAction", "u32 makeValue", "u32 breakAction", "u32 breakValue", "u32 actionFlags", "@SimpleText"],
    };

    // Load routine address (docs/gui.md 3.1) to the routine above.
    private static readonly Dictionary<string, string> kByAddress = new()
    {
        ["539098"] = "Control",
        ["53c900"] = "Active",
        ["538a14"] = "Active",
        ["5406bc"] = "SimpleText",
        ["53f6ac"] = "TextEdit",
        ["4a4c44"] = "Chat",
        ["53df7c"] = "Check",
        ["54de00"] = "Check",
        ["53fcf8"] = "Bitmap",
        ["4b28c4"] = "FGBitmap",
        ["53ff68"] = "Palette",
        ["544b60"] = "Progress",
        ["547fac"] = "Timer",
        ["5488a8"] = "BitmapBox",
        ["54aeb4"] = "Scroll",
        ["54b4a0"] = "Matrix",
        ["4c3a90"] = "FGScroll",
        ["4e4a88"] = "HudMatrix",
        ["54baa8"] = "TextList",
        ["54cb7c"] = "TextWrap",
        ["54d38c"] = "Slider",
        ["4cb0e8"] = "FGSlider",
        ["5329d8"] = "Animate",
        ["547864"] = "TSControl",
        ["493ea4"] = "IrcScroll",
        ["496148"] = "MenuItem",
        ["4993a8"] = "ShapeView",
        ["49ba58"] = "ShapeView",
        ["49c8d4"] = "ShellBorder",
        ["4a01a4"] = "UniversalButton",
        ["4aef68"] = "Dialog",
        ["4b16b4"] = "Dialog",
        ["4a0a8c"] = "AbortDlg",
        ["4a3514"] = "DlgBox",
        ["4a734c"] = "ChatDisplay",
        ["4a7cfc"] = "ChatMenu",
        ["4afef4"] = "Hud",
        ["4b76d0"] = "Combo",
        ["4bbc3c"] = "Combo",
        ["4c14cc"] = "Control",
        ["4c19b4"] = "Radio",
        ["4cbbcc"] = "Switch",
        ["4ccd1c"] = "TabMenu",
        ["4cd298"] = "Tab",
        ["4ce4bc"] = "TextFormat",
        ["4d6dd0"] = "Smacker",
        ["49ec7c"] = "CFG",
        ["4d4d00"] = "Active",
    };

    /// <summary>Returns the routine name of a class.</summary>
    /// <param name="row">The class row.</param>
    /// <returns>The routine, or null when its load address is unknown.</returns>
    public static string? RoutineFor(GuiClassRow row) => kByAddress.GetValueOrDefault(row.Load);

    /// <summary>Reads one object block (docs/gui.md 1.2).</summary>
    /// <param name="d">The file bytes.</param>
    /// <param name="pos">The block start. On return, the padded end of the block.</param>
    /// <param name="limit">The end of the enclosing body.</param>
    /// <param name="ctx">The load context.</param>
    /// <returns>The control.</returns>
    /// <exception cref="GuiLoadException">The block is short, its class is unknown or not a control, or its body is not read to the end.</exception>
    public static GuiControl ReadObject(byte[] d, ref int pos, int limit, GuiLoadContext ctx)
    {
        if (pos + 8 > limit)
            throw new GuiLoadException(1, "block header past the end");
        var tag = Encoding.Latin1.GetString(d, pos, 4);
        var rawSize = BitConverter.ToUInt32(d, pos + 4);
        var pad4 = (rawSize & 0x80000000) != 0;
        var size = (int)(rawSize & 0x7fffffff);
        var end = 8 + (pad4 ? (size + 3) & ~3 : (size + 1) & ~1);
        var bodyEnd = pos + 8 + size;
        if (bodyEnd > limit)
            throw new GuiLoadException(1, $"block {tag} at {pos} size {size} end {end} larger than the limit {limit}");
        var p = pos + 8;
        string className;
        if (tag == "PERS")
        {
            if (p + 2 > bodyEnd)
                throw new GuiLoadException(1, "short PERS block");
            int n = BitConverter.ToUInt16(d, p);
            p += 2;
            if (p + n > bodyEnd)
                throw new GuiLoadException(1, "short PERS name");
            className = Encoding.Latin1.GetString(d, p, n);
            p += (n + 1) & ~1;
        }
        else if (tag == "DICT")
            throw new GuiLoadException(2, "DICT back reference");
        else
        {
            GuiClassRow? r = GuiClassTable.ByTag(tag) ?? throw new GuiLoadException(2, "unknown class tag " + tag);
            className = r.Name;
        }
        GuiClassRow? row = GuiClassTable.ByName(className);
        if (row == null || !GuiClassTable.IsControl(row))
            throw new GuiLoadException(2, "class " + className + " is not a control class");
        GuiControl c = GuiFactory.Create(row);
        var bytes = new GuiBytes(d, p, bodyEnd);
        var version = bytes.U32();
        ctx.BodyVersion = version;
        c.PersistVersion = version;
        var routine = RoutineFor(row) ?? throw new GuiLoadException(2, "class " + className + " has no load routine");
        Run(c, routine, bytes, ctx, d, limit);
        // The 62 loadable stock files end every block exactly at its declared end. A block of an older layout does not, and fails here as it does in the original.
        if (bytes.Pos != bodyEnd)
            throw new GuiLoadException(4, $"block {className} read {bytes.Pos - p} of {bodyEnd - p} bytes");
        pos += end;
        return c;
    }

    private static void Run(GuiControl c, string routine, GuiBytes b, GuiLoadContext ctx, byte[] file, int limit)
    {
        switch (routine)
        {
            case "Control":
                ReadControl(c, b, ctx, file);
                return;
            case "ShellBorder":
                // docs/gui.md 1.4: marker 0xff means a string follows, any other value is pushed back.
                if (b.Peek() == 0xff)
                {
                    b.U8();
                    c.F["bitmapName"] = b.Pstr();
                }
                Run(c, "Control", b, ctx, file, limit);
                return;
            case "UniversalButton":
            {
                var v = b.U32();
                c.F["bodyVersion"] = v;
                c.F["radioId"] = b.U32();
                if (v < 2)
                    c.F["bitmapTag"] = b.U32();
                else
                    c.F["bitmapRoot"] = b.Pstr();
                c.F["lowResX"] = b.U32();
                c.F["lowResY"] = b.U32();
                c.F["on"] = b.U8();
                c.F["isSwitch"] = v >= 1 ? b.U8() : (byte)1;
                c.F["mirrorVar"] = v >= 3 ? b.U8() : (byte)0;
                Run(c, "Active", b, ctx, file, limit);
                return;
            }
            case "Smacker":
            {
                var v = ctx.BodyVersion;
                c.F["movieTag"] = b.U32();
                c.F["loop"] = b.U8();
                if (v >= 1)
                    c.F["movieDoneTag"] = b.U32();
                c.F["preload"] = b.U8();
                c.F["palStart"] = b.U32();
                c.F["palColors"] = b.U32();
                c.F["stretch"] = b.U8();
                Run(c, "Active", b, ctx, file, limit);
                return;
            }
        }
        if (!Routines.TryGetValue(routine, out var ops))
            throw new GuiLoadException(2, "no routine " + routine);
        foreach (var op in ops)
        {
            if (op[0] == '@')
            {
                Run(c, op[1..], b, ctx, file, limit);
                continue;
            }
            var sp = op.IndexOf(' ');
            string kind = sp < 0 ? op : op[..sp], name = sp < 0 ? "" : op[(sp + 1)..];
            switch (kind)
            {
                case "u8":
                    c.F[name] = b.U8();
                    break;
                case "u32":
                    c.F[name] = b.U32();
                    break;
                case "i32":
                    c.F[name] = b.I32();
                    break;
                case "pstr":
                    c.F[name] = b.Pstr();
                    break;
                case "buf81":
                    c.F[name] = b.Buf81();
                    break;
                case "skip32":
                    b.U32();
                    break;
                case "skip81":
                    b.Buf81();
                    break;
                default:
                    throw new GuiLoadException(2, "bad op " + op);
            }
        }
    }

    /// <summary>Reads the SimGui::Control body and its children (docs/gui.md 1.3).</summary>
    private static void ReadControl(GuiControl c, GuiBytes b, GuiLoadContext ctx, byte[] file)
    {
        var v = b.U8();
        if (v > 4)
            throw new GuiLoadException(3, "control body version " + v);
        c.BodyVersion = v;
        if (v == 0)
        {
            b.U32();
            b.U32();
            b.U8();
            b.U8();
            c.Opaque = 0;
            c.Border = 0;
        }
        else if (v == 1)
        {
            b.U32();
            c.Opaque = b.U8();
            c.FillColor = b.U8();
            c.Border = b.U8();
            c.BorderColor = b.U8();
        }
        else
        {
            b.U32();
            c.Opaque = b.U8();
            c.FillColor = b.U8();
            c.SelectedFill = b.U8();
            c.DisabledFill = b.U8();
            c.Border = b.U8();
            c.BorderColor = b.U8();
            c.SelectedBorder = b.U8();
            c.DisabledBorder = b.U8();
        }
        c.Command = b.Pstr();
        if (v >= 3)
            c.AltCommand = b.Pstr();
        c.X = b.I32();
        c.Y = b.I32();
        c.W = b.I32();
        c.H = b.I32();
        c.Flags = b.U32();
        c.Id = b.U32();
        c.HSizing = b.U32();
        c.VSizing = b.U32();
        if (v >= 4)
            c.HelpTag = b.U32();
        c.ConsoleVar = b.Buf81();
        var count = b.U32();
        if (count > 4096)
            throw new GuiLoadException(3, "child count " + count);
        var pos = b.Pos;
        var kids = new List<GuiControl>();
        for (uint i = 0; i < count; ++i)
            kids.Add(ReadObject(file, ref pos, b.End, ctx));
        b.Pos = pos;
        foreach (GuiControl k in kids)
        {
            k.Name = b.Pstr();
            if (k.Name.Length == 0)
                k.Name = null;
            c.AddChild(k);
        }
    }

    /// <summary>Loads a whole `.gui` file (docs/gui.md 1.1): the file is one object and the bytes after the root block are ignored.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="error">Receives the error text, empty on success.</param>
    /// <returns>The root control, null on an error.</returns>
    public static GuiControl Load(byte[] data, out string error)
    {
        error = "";
        try
        {
            var pos = 0;
            var ctx = new GuiLoadContext();
            return ReadObject(data, ref pos, data.Length, ctx);
        }
        catch (GuiLoadException e)
        {
            error = $"read error {e.Code}: {e.Message}";
            return null!;
        }
    }
}

/// <summary>The state shared by the blocks of one file.</summary>
public sealed class GuiLoadContext
{
    /// <summary>The body version of the block being read.</summary>
    public uint BodyVersion;
}
