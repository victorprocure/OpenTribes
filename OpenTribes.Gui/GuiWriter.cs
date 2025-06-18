// The inverse of GuiLoader (docs/gui.md 1.2 to 1.4, D44): writes a control tree with the same routines.
using System.Text;

namespace OpenTribes.Gui;

public static class GuiWriter
{
    public static byte[] Write(GuiControl c)
    {
        var ms = new MemoryStream();
        WriteObject(ms, c);
        return ms.ToArray();
    }

    private static void U32(MemoryStream m, uint v) => m.Write(BitConverter.GetBytes(v));
    private static void Pstr(MemoryStream m, string s)
    {
        byte[] b = Encoding.Latin1.GetBytes(s.Length > 255 ? s[..255] : s);
        m.WriteByte((byte)b.Length);
        m.Write(b);
    }
    private static void Buf81(MemoryStream m, string s)
    {
        byte[] b = new byte[81];
        Encoding.Latin1.GetBytes(s.Length > 80 ? s[..80] : s).CopyTo(b, 0);
        m.Write(b);
    }

    private static void WriteObject(MemoryStream m, GuiControl c)
    {
        GuiClassRow row = GuiClassTable.ByName(c.ClassName)!;
        var body = new MemoryStream();
        U32(body, c.PersistVersion);
        WriteRoutine(body, c, GuiLoader.RoutineFor(row)!);
        byte[] b = body.ToArray();
        if (row.Tag.Length == 4)
            m.Write(Encoding.Latin1.GetBytes(row.Tag));
        else
        {
            m.Write(Encoding.Latin1.GetBytes("PERS"));
        }
        byte[] name = Encoding.Latin1.GetBytes(c.ClassName);
        int head = row.Tag.Length == 4 ? 0 : 2 + ((name.Length + 1) & ~1);
        int size = head + b.Length;
        U32(m, (uint)size);
        if (row.Tag.Length != 4)
        {
            m.Write(BitConverter.GetBytes((ushort)name.Length));
            m.Write(name);
            if ((name.Length & 1) != 0)
                m.WriteByte(0);
        }
        m.Write(b);
        if ((size & 1) != 0)
            m.WriteByte(0);
    }

    private static void WriteRoutine(MemoryStream m, GuiControl c, string routine)
    {
        switch (routine)
        {
            case "Control":
                WriteControl(m, c);
                return;
            case "ShellBorder":
                if (c.F.TryGetValue("bitmapName", out object? bn))
                {
                    m.WriteByte(0xff);
                    Pstr(m, (string)bn);
                }
                WriteRoutine(m, c, "Control");
                return;
            case "UniversalButton":
                {
                    uint v = c.U("bodyVersion");
                    U32(m, v);
                    U32(m, c.U("radioId"));
                    if (v < 2) U32(m, c.U("bitmapTag")); else Pstr(m, c.S("bitmapRoot"));
                    U32(m, c.U("lowResX"));
                    U32(m, c.U("lowResY"));
                    m.WriteByte((byte)c.U("on"));
                    if (v >= 1) m.WriteByte((byte)c.U("isSwitch"));
                    if (v >= 3) m.WriteByte((byte)c.U("mirrorVar"));
                    WriteRoutine(m, c, "Active");
                    return;
                }
            case "Smacker":
                U32(m, c.U("movieTag"));
                m.WriteByte((byte)c.U("loop"));
                if (c.PersistVersion >= 1) U32(m, c.U("movieDoneTag"));
                m.WriteByte((byte)c.U("preload"));
                U32(m, c.U("palStart"));
                U32(m, c.U("palColors"));
                m.WriteByte((byte)c.U("stretch"));
                WriteRoutine(m, c, "Active");
                return;
        }
        foreach (string op in GuiLoader.Routines[routine])
        {
            if (op[0] == '@')
            {
                WriteRoutine(m, c, op[1..]);
                continue;
            }
            int sp = op.IndexOf(' ');
            string kind = sp < 0 ? op : op[..sp], name = sp < 0 ? "" : op[(sp + 1)..];
            switch (kind)
            {
                case "u8": m.WriteByte((byte)c.U(name)); break;
                case "u32": case "i32": U32(m, c.U(name)); break;
                case "pstr": Pstr(m, c.S(name)); break;
                case "buf81": Buf81(m, c.S(name)); break;
                case "skip32": U32(m, 0); break;
                case "skip81": Buf81(m, ""); break;
            }
        }
    }

    private static void WriteControl(MemoryStream m, GuiControl c)
    {
        m.WriteByte(4);
        U32(m, 0);
        foreach (byte b in new[] { c.Opaque, c.FillColor, c.SelectedFill, c.DisabledFill, c.Border, c.BorderColor, c.SelectedBorder, c.DisabledBorder })
            m.WriteByte(b);
        Pstr(m, c.Command);
        Pstr(m, c.AltCommand);
        foreach (int v in new[] { c.X, c.Y, c.W, c.H })
            U32(m, (uint)v);
        U32(m, c.Flags);
        U32(m, c.Id);
        U32(m, c.HSizing);
        U32(m, c.VSizing);
        U32(m, c.HelpTag);
        Buf81(m, c.ConsoleVar);
        U32(m, (uint)c.Children.Count);
        foreach (GuiControl k in c.Children)
            WriteObject(m, k);
        foreach (GuiControl k in c.Children)
            Pstr(m, k.Name ?? "");
    }
}
