// Port of src/script/client_commands.hpp and src/script/client_commands.cpp: client-side console commands as
// they behave on a dedicated server: GUI, canvas, input, sound, CD audio, tool windows, editors, IRC
// (docs/mods.md 5). There is no canvas, input manager or sound device, so most commands print their usage
// or "not found" message. Action maps keep real state (docs/mods.md 5.4).
using System.Text;
using OpenTribes.Core;

namespace OpenTribes.Script;

// src/script/client_commands.hpp struct InputEvent: input event of a binding (parser 0x005228fc).
public struct InputEvent : IEquatable<InputEvent>
{
    public byte DeviceType; // 1 device, 2 mouse, 3 keyboard, 4 joystick
    public byte DeviceInst; // trailing number of the device word
    public byte ObjType;    // 2 button, 3-9 pov, 10 key, 11-16 axes, 17 slider
    public byte ObjInst;    // key code for keys, trailing number otherwise
    public byte Action;     // 1 make, 2 break, 3 move
    public byte Modifiers;  // 1/2 shift, 4/8 control, 16/32 alt (left/right)

    public readonly bool Equals(InputEvent o) =>
        DeviceType == o.DeviceType && DeviceInst == o.DeviceInst && ObjType == o.ObjType &&
        ObjInst == o.ObjInst && Action == o.Action && Modifiers == o.Modifiers;
    public override readonly bool Equals(object? obj) => obj is InputEvent o && Equals(o);
    public override readonly int GetHashCode() => HashCode.Combine(DeviceType, DeviceInst, ObjType, ObjInst, Action, Modifiers);
    public static bool operator ==(InputEvent a, InputEvent b) => a.Equals(b);
    public static bool operator !=(InputEvent a, InputEvent b) => !a.Equals(b);
}

// src/script/client_commands.hpp struct ActionBinding.
public sealed class ActionBinding
{
    public const uint None = 1, Value = 2, Flip = 4, Center = 8, Square = 16, Cube = 32, DeadZone = 64, Scale = 128;
    public InputEvent Event;
    public bool IsCommand;
    public string Command = ""; // bindCommand / bind
    public int Action;          // bindAction: action tag id
    public uint Flags;
    public float ValueF, DeadZoneF, ScaleF = 1;
}

// src/script/client_commands.hpp struct ActionMap.
public sealed class ActionMap
{
    public string Name = ""; // resource name, ends in ".sae"
    public readonly List<ActionBinding> Bindings = new();
}

// src/script/client_commands.hpp struct ClientState.
public sealed class ClientState
{
    public string CurrentMap = ""; // set by editActionMap/newActionMap, "" when unset
    public readonly List<ActionMap> Maps = new();

    // src/script/client_commands.cpp ClientState::findMap: ASCII case-insensitive.
    public ActionMap? FindMap(string name)
    {
        foreach (ActionMap m in Maps)
            if (ClientCommands.EqualNoCase(m.Name, name))
                return m;
        return null;
    }
}

public static class ClientCommands
{
    // src/script/client_commands.cpp struct Rule (table: ClientCommandTable.cs).
    public readonly record struct Rule(string Name, byte Lo, byte Hi, string Out, string Ret, bool Crash);
    // src/script/client_commands.cpp struct KeyName (table: ClientKeyTable.cs).
    public readonly record struct KeyName(byte Code, char Lower, char Upper, string? Name);

    private readonly record struct NameValue(string Name, byte Value);

    // Name tables of the input plugin (0x00637860, 0x00637888, 0x006378a8, 0x00637930, 0x00637980).
    private static readonly NameValue[] kDevices = { new("keyboard", 3), new("mouse", 2), new("joystick", 4), new("device", 1) };
    private static readonly NameValue[] kEventActions = { new("make", 1), new("break", 2), new("move", 3) };
    private static readonly NameValue[] kObjects =
    {
        new("xaxis", 11), new("yaxis", 12), new("zaxis", 13), new("rxaxis", 14), new("ryaxis", 15), new("rzaxis", 16),
        new("slider", 17), new("pov", 3), new("xpov", 4), new("ypov", 5), new("upov", 6), new("dpov", 7),
        new("lpov", 8), new("rpov", 9), new("button", 2), new("key", 10),
    };
    private static readonly NameValue[] kModifiers =
    {
        new("shift", 3), new("control", 12), new("alt", 48), new("l_shift", 1), new("r_shift", 2),
        new("l_control", 4), new("r_control", 8), new("l_alt", 16), new("r_alt", 32),
    };
    private static readonly NameValue[] kFlags =
    {
        new("None", 1), new("Value", 2), new("Flip", 4), new("Center", 8),
        new("Square", 16), new("Cube", 32), new("DeadZone", 64), new("Scale", 128),
    };

    private static char UpperAscii(char c) => (c >= 'a' && c <= 'z') ? (char)(c - 0x20) : c;

    // src/script/client_commands.cpp equalNoCase.
    internal static bool EqualNoCase(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; ++i)
            if (UpperAscii(a[i]) != UpperAscii(b[i]))
                return false;
        return true;
    }

    // src/script/client_commands.cpp hasPrefixNoCase: strnicmp(word + from, prefix, strlen(prefix)) == 0.
    private static bool HasPrefixNoCase(string word, int from, string prefix)
    {
        for (int i = 0; i < prefix.Length; ++i)
        {
            char w = from + i < word.Length ? word[from + i] : '\0';
            if (UpperAscii(w) != UpperAscii(prefix[i]))
                return false;
        }
        return true;
    }

    // src/script/client_commands.cpp lookupPrefix (0x00520af4): the first entry that is a prefix of the word.
    private static byte LookupPrefix(NameValue[] table, string word)
    {
        foreach (NameValue e in table)
            if (HasPrefixNoCase(word, 0, e.Name))
                return e.Value;
        return 0;
    }

    // src/script/client_commands.cpp nameOf.
    private static string NameOf(NameValue[] table, byte value)
    {
        foreach (NameValue e in table)
            if (e.Value == value)
                return e.Name;
        return "";
    }

    // src/script/client_commands.cpp trailingNumber (0x00520b3c): the decimal number that ends the word, 0 without one.
    private static byte TrailingNumber(string word)
    {
        int i = word.Length;
        while (i > 0 && word[i - 1] >= '0' && word[i - 1] <= '9')
            --i;
        return i == word.Length ? (byte)0 : unchecked((byte)Numbers.BorlandAtoi(word[i..]));
    }

    // src/script/client_commands.cpp keyFromChar.
    private static byte KeyFromChar(char c)
    {
        foreach (KeyName k in ClientKeyTable.Keys)
            if (c == k.Lower || c == k.Upper)
                return k.Code;
        return 0;
    }

    // src/script/client_commands.cpp keyFromName.
    private static byte KeyFromName(string name)
    {
        foreach (KeyName k in ClientKeyTable.Keys)
            if (k.Name != null && EqualNoCase(name, k.Name))
                return k.Code;
        return 0;
    }

    // src/script/client_commands.cpp keyByCode.
    private static KeyName? KeyByCode(byte code)
    {
        foreach (KeyName k in ClientKeyTable.Keys)
            if (k.Code == code)
                return k;
        return null;
    }

    // src/script/client_commands.cpp floatText: "%f" of the double.
    private static string FloatText(float v) => CFormat.F(v, 6);

    // src/script/client_commands.cpp expand: "{n}" is argument n.
    private static string Expand(string tpl, string[] argv)
    {
        var output = new StringBuilder();
        for (int p = 0; p < tpl.Length; ++p)
        {
            if (tpl[p] == '{' && p + 2 < tpl.Length && tpl[p + 1] >= '1' && tpl[p + 1] <= '9' && tpl[p + 2] == '}')
            {
                int k = tpl[p + 1] - '0';
                if (k < argv.Length)
                    output.Append(argv[k]);
                p += 2;
            }
            else
            {
                output.Append(tpl[p]);
            }
        }
        return output.ToString();
    }

    // src/script/client_commands.cpp printLines.
    private static void PrintLines(Interpreter interp, string text)
    {
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            interp.Print(1, end < 0 ? text[start..] : text[start..end]);
            start = end < 0 ? text.Length : end + 1;
        }
    }

    // src/script/client_commands.cpp actionMapName: appends ".sae" unless the name already has that extension (0x00520a98).
    public static string ActionMapName(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && EqualNoCase(name[dot..], ".sae"))
            return name;
        return name + ".sae";
    }

    // src/script/client_commands.cpp parseInputEvent: words before "TO" of bind/bindAction/bindCommand.
    // False: malformed device section.
    public static bool ParseInputEvent(List<string> w, ref InputEvent ev)
    {
        int i = 0, n = w.Count;
        if (n == 0)
            return false;
        ev.DeviceType = LookupPrefix(kDevices, w[0]);
        ev.DeviceInst = TrailingNumber(w[0]);
        ++i;
        if (i == n || ev.DeviceType == 0)
            return false;
        ev.Action = LookupPrefix(kEventActions, w[i]);
        bool button = ev.Action == 1 || ev.Action == 2;
        if (button)
            ++i;
        if (i == n)
            return false;
        ev.Modifiers = 0;
        for (; i < n; ++i)
        {
            byte m = LookupPrefix(kModifiers, w[i]);
            if (m == 0)
                break;
            ev.Modifiers |= m;
        }
        if (i == n)
        {
            // A modifier alone is a key event of that modifier (keyboard only).
            if (ev.DeviceType == 3 && button && (ev.Modifiers == 3 || ev.Modifiers == 12 || ev.Modifiers == 48))
            {
                ev.ObjType = 10;
                ev.ObjInst = 0;
                return true;
            }
            return false;
        }
        string o = w[i];
        if (button)
        {
            if (HasPrefixNoCase(o, 0, "button"))
            {
                ev.ObjType = 2;
                ev.ObjInst = TrailingNumber(o);
            }
            else if (o.Length < 4 || !HasPrefixNoCase(o, 1, "pov"))
            {
                ev.ObjType = 10;
                ev.ObjInst = o.Length == 1 ? KeyFromChar(o[0]) : KeyFromName(o);
                if (ev.ObjInst == 0)
                    return false;
            }
            else
            {
                ev.ObjType = LookupPrefix(kObjects, o);
                ev.ObjInst = TrailingNumber(o);
            }
        }
        else
        {
            ev.Action = 3;
            ev.ObjType = LookupPrefix(kObjects, o);
            ev.ObjInst = TrailingNumber(o);
            if (ev.ObjType == 0)
                return false;
        }
        return true;
    }

    // src/script/client_commands.cpp parseActionTarget: words after "TO" of bindAction (action id, value, flags).
    // False: malformed TO section.
    public static bool ParseActionTarget(List<string> w, ActionBinding b)
    {
        int n = w.Count;
        if (n == 0)
            return false;
        b.Action = Numbers.BorlandAtoi(w[0]);
        if (b.Action == 0)
            return false;
        b.Flags = 0;
        b.ValueF = 0;
        b.DeadZoneF = 0;
        b.ScaleF = 1;
        int i = 1;
        if (i < n && ScanFloat(w[i], out float value))
        {
            b.ValueF = value;
            b.Flags |= ActionBinding.Value;
            ++i;
        }
        for (; i < n; ++i)
        {
            NameValue? flag = null;
            foreach (NameValue f in kFlags)
                if (EqualNoCase(w[i], f.Name))
                {
                    flag = f;
                    break;
                }
            if (flag == null)
                return false;
            b.Flags |= flag.Value.Value;
            if (flag.Value.Value == ActionBinding.DeadZone || flag.Value.Value == ActionBinding.Scale)
            {
                if (++i == n)
                    return false;
                float v = Fpu.Store(Numbers.BorlandAtof(w[i]));
                v = v >= 1.0f ? 1.0f : v;
                v = v <= 0.0f ? 0.0f : v;
                if (flag.Value.Value == ActionBinding.DeadZone)
                    b.DeadZoneF = v;
                else
                    b.ScaleF = v;
            }
        }
        return true;
    }

    // sscanf(s, "%f", &f) == 1 of the C library: white space, then a decimal, hexadecimal, infinity or NaN
    // number, stored to float in the current mode (strtof; here the double parse, then the store).
    internal static bool ScanFloat(string s, out float f)
    {
        f = 0;
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || (s[i] >= '\t' && s[i] <= '\r')))
            ++i;
        int start = i;
        bool negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            negative = s[i++] == '-';
        bool Word(string w) => i + w.Length <= s.Length && string.Compare(s, i, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0;
        if (Word("inf"))
        {
            f = negative ? float.NegativeInfinity : float.PositiveInfinity;
            return true;
        }
        if (Word("nan"))
        {
            f = (float)(negative ? Numbers.NegNaN : Numbers.NaN);
            return true;
        }
        static bool Hex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        if (i + 2 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X') &&
            (Hex(s[i + 2]) || (s[i + 2] == '.' && i + 3 < s.Length && Hex(s[i + 3]))))
        {
            // Hexadecimal: mantissa digits, optional point, optional binary exponent.
            i += 2;
            double m = 0;
            int scale = 0;
            bool point = false;
            for (; i < s.Length; ++i)
            {
                char c = s[i];
                if (c == '.' && !point)
                {
                    point = true;
                    continue;
                }
                if (!Hex(c))
                    break;
                m = m * 16 + (c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10);
                if (point)
                    scale -= 4;
            }
            if (i < s.Length && (s[i] == 'p' || s[i] == 'P'))
            {
                int j = i + 1;
                bool eneg = false;
                if (j < s.Length && (s[j] == '+' || s[j] == '-'))
                    eneg = s[j++] == '-';
                if (j < s.Length && s[j] >= '0' && s[j] <= '9')
                {
                    int e = 0;
                    while (j < s.Length && s[j] >= '0' && s[j] <= '9')
                        e = Math.Min(e * 10 + (s[j++] - '0'), 100000);
                    scale += eneg ? -e : e;
                }
            }
            f = Fpu.Store(Math.ScaleB(negative ? -m : m, scale));
            return true;
        }
        int digits = 0;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            ++i;
            ++digits;
        }
        if (i < s.Length && s[i] == '.')
        {
            ++i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9')
            {
                ++i;
                ++digits;
            }
        }
        if (digits == 0)
            return false;
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < s.Length && (s[j] == '+' || s[j] == '-'))
                ++j;
            if (j < s.Length && s[j] >= '0' && s[j] <= '9')
            {
                while (j < s.Length && s[j] >= '0' && s[j] <= '9')
                    ++j;
                i = j;
            }
        }
        f = Fpu.Store(Numbers.Strtod(s[start..i]));
        return true;
    }

    // src/script/client_commands.cpp formatBinding: one saveActionMap line without the line end (writer 0x00522ca8).
    public static string FormatBinding(ActionBinding b, Func<int, string>? tagName)
    {
        InputEvent ev = b.Event;
        var line = new StringBuilder(b.IsCommand ? "bindCommand(" : "bindAction(");
        line.Append(NameOf(kDevices, ev.DeviceType) + Numbers.FormatInt(ev.DeviceInst) + ", ");
        if (ev.Action == 1 || ev.Action == 2)
        {
            line.Append(NameOf(kEventActions, ev.Action) + ", ");
            byte mod = ev.Modifiers;
            foreach (NameValue m in kModifiers)
                if ((mod & m.Value) == m.Value)
                {
                    line.Append(m.Name + ", ");
                    mod = (byte)(mod & ~m.Value);
                }
            if (ev.ObjType == 10)
            {
                if (ev.ObjInst != 0)
                {
                    KeyName? k = KeyByCode(ev.ObjInst);
                    if (k != null && k.Value.Name != null)
                        line.Append("\"" + k.Value.Name + "\", ");
                    else if (k != null && k.Value.Lower == '\\')
                        line.Append("\"\\\\\", ");
                    else
                        line.Append("\"" + (k != null ? k.Value.Lower : ' ') + "\", ");
                }
            }
            else
            {
                line.Append(NameOf(kObjects, ev.ObjType) + Numbers.FormatInt(ev.ObjInst) + ", ");
            }
        }
        else
        {
            line.Append(NameOf(kObjects, ev.ObjType) + Numbers.FormatInt(ev.ObjInst) + ", ");
        }
        line.Append("TO, ");
        if (b.IsCommand)
        {
            line.Append("\"" + Builtins.ExportEscape(b.Command) + "\"");
        }
        else
        {
            line.Append(tagName != null ? tagName(b.Action) : "");
            if ((b.Flags & ActionBinding.Value) != 0)
                line.Append(", " + FloatText(b.ValueF));
            foreach (NameValue f in kFlags)
                if ((b.Flags & f.Value) != 0 && (b.Flags & ActionBinding.Value) == 0)
                {
                    line.Append(", " + f.Name);
                    if (f.Value == ActionBinding.Scale)
                        line.Append(", " + FloatText(b.ScaleF));
                    else if (f.Value == ActionBinding.DeadZone)
                        line.Append(", " + FloatText(b.DeadZoneF));
                }
        }
        return line + ");";
    }

    // src/script/client_commands.cpp createMap.
    private static ActionMap CreateMap(ClientState st, ResourceManager? res, string name)
    {
        var m = new ActionMap { Name = name };
        st.Maps.Add(m);
        res?.AddGenerated(name); // the map is a generated resource (ResManager::add 0x00587fcc)
        return m;
    }

    // src/script/client_commands.cpp addBinding (0x00509240): a binding of the same event is removed, the new one goes to the end.
    private static void AddBinding(ActionMap map, ActionBinding b)
    {
        for (int k = 0; k < map.Bindings.Count; ++k)
            if (map.Bindings[k].Event == b.Event)
            {
                map.Bindings.RemoveAt(k);
                break;
            }
        map.Bindings.Add(b);
    }

    // src/script/client_commands.cpp countBeforeTo: index of the first "to" word in argv[1..], or argv.size() - 1 without one.
    private static int CountBeforeTo(string[] argv)
    {
        int i = 1;
        while (i < argv.Length && !EqualNoCase(argv[i], "to"))
            ++i;
        return i - 1;
    }

    // src/script/client_commands.cpp usage.
    private static void Usage(Interpreter interp, string c)
    {
        interp.Print(1, c + " keyboard[0-n] [make, break] {ctrl, alt, shift} {ascii, keyName}");
        interp.Print(1, c + " joystick[0-n] [make, break] {ctrl, alt, shift} button[0-n]");
        interp.Print(1, c + " joystick[0-n] move[x,y,z,r,u,v] ");
        interp.Print(1, c + " mouse[0-n] [make, break] {ctrl, alt, shift} button[0-n]");
        interp.Print(1, c + " mouse[0-n] move[x,y,z,r,u,v] ");
    }

    private static List<string> Slice(string[] argv, int from, int to) => argv[from..to].ToList();

    // src/script/client_commands.cpp registerActionMaps.
    private static void RegisterActionMaps(Interpreter interp, ResourceManager? res, string rootDir, ClientState st)
    {
        // editActionMap 0x00520e18 case 10, newActionMap case 11: any other argument count
        // clears the current map name.
        Interpreter.Command Select(bool clear) => (i, argv) =>
        {
            if (argv.Length != 2)
            {
                st.CurrentMap = "";
                return "False";
            }
            st.CurrentMap = ActionMapName(argv[1]);
            ActionMap? m = st.FindMap(st.CurrentMap);
            if (m != null)
            {
                if (clear)
                    m.Bindings.Clear();
            }
            else
            {
                CreateMap(st, res, st.CurrentMap);
            }
            return "False";
        };
        interp.AddCommand("editActionMap", Select(false));
        interp.AddCommand("newActionMap", Select(true));

        interp.AddCommand("bindAction", (i, argv) =>
        {
            if (argv.Length < 5)
            {
                Usage(i, "bindAction");
                return "False";
            }
            ActionMap? map = st.FindMap(st.CurrentMap);
            if (map == null)
            {
                i.Print(1, "bindAction error: must call 'editActionMap' or 'newActionMap' first. (" + st.CurrentMap + ")");
                return "False";
            }
            string Fail(string section)
            {
                i.Print(1, "bindAction event TO action - error: malformed '" + section + "'.");
                var echo = new StringBuilder("-->bindAction ");
                foreach (string a in argv)
                    echo.Append(a + " ");
                i.Print(1, echo.ToString());
                return "False";
            }
            int before = CountBeforeTo(argv);
            var b = new ActionBinding();
            if (!ParseInputEvent(Slice(argv, 1, 1 + before), ref b.Event))
                return Fail("Device section");
            int after = before + 2; // first word behind "TO"
            if (!ParseActionTarget(after < argv.Length ? Slice(argv, after, argv.Length) : new List<string>(), b))
                return Fail("TO section");
            AddBinding(map, b);
            return "False";
        });

        // bindCommand binds in the current map, bind in SimGame.sae (cases 13, 14).
        Interpreter.Command BindCommand(string name, bool simGame) => (i, argv) =>
        {
            if (argv.Length < 5)
            {
                Usage(i, name);
                return "False";
            }
            ActionMap? map = st.FindMap(simGame ? "SimGame.sae" : st.CurrentMap);
            if (map == null)
            {
                if (simGame)
                    i.Print(1, "bind error: No SimGame action map!!");
                else
                    i.Print(1, "bindAction error: must call 'editActionMap' or 'newActionMap' first. (" + st.CurrentMap + ")");
                return "False";
            }
            int before = CountBeforeTo(argv);
            var b = new ActionBinding();
            // Without a word behind "TO" the original reads past argv (rejected, D11).
            if (!ParseInputEvent(Slice(argv, 1, 1 + before), ref b.Event) || before + 2 >= argv.Length)
            {
                i.Print(1, name + " event TO command - error: malformed 'Device section'.");
                return "False";
            }
            b.IsCommand = true;
            b.Command = argv[before + 2];
            AddBinding(map, b);
            return "False";
        };
        interp.AddCommand("bindCommand", BindCommand("bindCommand", false));
        interp.AddCommand("bind", BindCommand("bind", true));

        interp.AddCommand("saveActionMap", (i, argv) =>
        {
            if (argv.Length < 3)
            {
                i.Print(1, "saveactionMap filename.cs mapname1 {mapname2 ...}");
                return "False";
            }
            string file = argv[1];
            int dot = file.LastIndexOf('.');
            if (dot < 0 || !EqualNoCase(file[dot..], ".cs"))
                file += ".cs";
            if (!Builtins.IsWriteablePath(file))
            {
                i.Print(1, "Error creating file '" + file + "'");
                return "False";
            }
            FileStream f;
            try
            {
                f = new FileStream(Builtins.HostFilePath(rootDir, file), FileMode.Create, FileAccess.Write);
            }
            catch (Exception)
            {
                i.Print(1, "Error creating file '" + file + "'");
                return "False";
            }
            using (f)
            {
                for (int k = 2; k < argv.Length; ++k)
                {
                    string name = ActionMapName(argv[k]);
                    ActionMap? map = st.FindMap(name);
                    if (map == null)
                    {
                        i.Print(1, "Unknown map '" + name + "'");
                        continue;
                    }
                    f.Write(ByteString.ToBytes("NewActionMap(\"" + name + "\");\n"));
                    foreach (ActionBinding b in map.Bindings)
                        f.Write(ByteString.ToBytes(FormatBinding(b, id => i.TagName(id)) + "\r\n"));
                }
            }
            return "False";
        });

        // pushActionMap/popActionMap (cases 16, 17) act on the client world's input set;
        // a server has none, so only the result is visible.
        foreach (string name in new[] { "pushActionMap", "popActionMap" })
            interp.AddCommand(name, (i, argv) => argv.Length == 2 ? "True" : "False");
    }

    // src/script/client_commands.cpp registerMouseGetters: Client::getMouseSensitivity / getMouseXAxisFlip /
    // getMouseYAxisFlip (0x0041d7c4, 0x0041d808, 0x0041d850 with the action map readers 0x0049d418 and 0x0049d56c):
    // the first mouse or joystick axis binding of the map that is neither a key nor a make/break/command binding.
    private static void RegisterMouseGetters(Interpreter interp, ClientState st)
    {
        ActionMap? Find(Interpreter i, string name)
        {
            ActionMap? m = st.FindMap(name);
            if (m == null)
                i.Print(1, "error: must call 'editActionMap' or 'newActionMap' first. (" + name + ")");
            return m;
        }
        static ActionBinding? Axis(ActionMap m, byte objType)
        {
            foreach (ActionBinding b in m.Bindings)
                if (b.Event.DeviceType != 3 && b.Event.Action != 1 && b.Event.Action != 2 && !b.IsCommand &&
                    b.Event.ObjType == objType)
                    return b;
            return null;
        }
        interp.AddCommand("Client::getMouseSensitivity", (i, argv) =>
        {
            if (argv.Length != 2)
            {
                i.Print(1, "invalid call to " + argv[0] + "(actionMap)");
                return "";
            }
            if (argv[1].Length == 0)
                return "1";
            string r = "-1";
            ActionMap? m = Find(i, argv[1]);
            ActionBinding? b = m != null ? Axis(m, 11) : null;
            if (b != null)
                r = Numbers.FormatFloat(b.ScaleF);
            return r;
        });
        foreach (bool y in new[] { false, true })
            interp.AddCommand(y ? "Client::getMouseYAxisFlip" : "Client::getMouseXAxisFlip", (i, argv) =>
            {
                if (argv.Length != 2)
                {
                    i.Print(1, "invalid call to " + argv[0] + "(actionMap)");
                    return "";
                }
                string r = "FALSE";
                if (argv[1].Length == 0)
                    return r;
                ActionMap? m = Find(i, argv[1]);
                ActionBinding? b = m != null ? Axis(m, (byte)(y ? 12 : 11)) : null;
                if (b != null)
                    r = (b.Flags & ActionBinding.Flip) != 0 ? "FALSE" : "TRUE";
                return r;
            });
    }

    // src/script/client_commands.cpp registerLandscape: LS:: landscape scripting (plugin 00598a39..): every command but
    // LS::Create and LS::createGridFile prints "<name>: must call LSCreate first" until LS::Create made the mapper.
    // The paths that need a real rules, texture or script file are not reproduced (the file lookups always fail
    // here; docs/script.md 5.2 open question).
    private static void RegisterLandscape(Interpreter interp)
    {
        bool created = false;
        var types = new List<string>();
        interp.AddCommand("LS::Create", (i, argv) =>
        {
            if (created)
                i.Print(1, "LSCreate: LandScapeMapper object already exists");
            created = true;
            return "False";
        });
        string[] kNames = { "Rules", "Textures", "Script", "ApplyTextures", "ApplyLandScape", "Editor",
                            "flushTextures", "addTerrainTexture", "addTerrainType", "flushRules", "addRule",
                            "flushCommands", "addCommand", "parseCommands", "createGridFile" };
        foreach (string baseName in kNames)
        {
            string name = "LS::" + baseName;
            interp.AddCommand(name, (i, argv) =>
            {
                if (!created)
                {
                    if (baseName == "createGridFile" && argv.Length - 1 <= 1)
                        i.Print(1, "LS::createGridFile: gridfile, matlist");
                    else
                        i.Print(1, name + ": must call LSCreate first");
                    return "False";
                }
                int n = argv.Length - 1;
                string b = baseName;
                if (b == "Rules")
                {
                    i.Print(1, n == 1 ? "LSRules: Could not load rules " + argv[1] : "LSRules terrain.rules.dat");
                }
                else if (b == "Textures")
                {
                    i.Print(1, n == 2 ? "LSRules: Could not load texture files " + argv[1] + ", " + argv[2]
                                      : "LSRules terrain.dml terrain.grid.dat ");
                }
                else if (b == "Script")
                {
                    i.Print(1, n == 1 ? "LSScript: execScript(" + argv[1] + ") failed" : "LSScript: script.pls");
                }
                else if (b == "ApplyTextures")
                {
                    i.Print(1, "LSApplyTextures: error!  Are rules.dat, grid.dat and terrain.dml loaded?");
                }
                else if (b == "Editor")
                {
                    i.Print(1, n == 1 ? "LSDlg: " + argv[1] + " SimCanvas not found" : "LSDlg: SimCanvas");
                }
                else if (b == "addTerrainTexture")
                {
                    if (n != 7)
                        i.Print(1, "LS::addTerrainTexture: file, sides, rotate, percent, type, elasticity, friction");
                    else if (argv[2].Length != 4)
                        i.Print(1, "LS::addTerrainTexture: sides is the four character terrain type id's");
                }
                else if (b == "addTerrainType")
                {
                    if (n != 2)
                        i.Print(1, "LS::addTerrainType: typeTag, description");
                    else
                        types.Add(argv[1]);
                }
                else if (b == "addRule")
                {
                    if (n != 13)
                        i.Print(1, "LS::addRule: group, altMin, altMax, altMean, altSdev, altWeight, adjWeight, slopeMin, slopeMax, slopeMean, slopeSdev, slopeWeight, adjSlopes");
                    else
                        i.Print(1, "LS::addRule: cannot add rule to a nonexisting terrain type '" + argv[1] + "'");
                }
                else if (b == "createGridFile")
                {
                    if (n != 2) // two arguments hang the original (D11)
                        i.Print(1, "LS::createGridFile: gridfile, matlist");
                }
                else if (b == "addCommand")
                {
                    if (n != 1)
                        i.Print(1, "LS::addCommand: command string");
                }
                return "False";
            });
        }
    }

    // src/script/client_commands.cpp registerIrcAndConsole: ircNick / ircName keep the identity ircListPeople prints;
    // Console::enable takes the words true or false only (the numbers 1 and 0 and any other word print the usage line).
    private static void RegisterIrcAndConsole(Interpreter interp)
    {
        string nick = "", realName = "";
        interp.AddCommand("ircNick", (i, argv) =>
        {
            if (argv.Length == 2)
            {
                nick = argv[1];
                return "true";
            }
            i.Print(1, "ircNick( nickname );");
            return "False";
        });
        interp.AddCommand("ircName", (i, argv) =>
        {
            if (argv.Length == 2)
            {
                realName = argv[1];
                return "true";
            }
            i.Print(1, "ircName( realname );");
            return "False";
        });
        interp.AddCommand("ircListPeople", (i, argv) =>
        {
            if (argv.Length == 1)
            {
                i.Print(1, "IRC People:");
                i.Print(1, "  01 " + nick + " () -- " + realName);
            }
            return "true";
        });
        interp.AddCommand("Console::enable", (i, argv) =>
        {
            if (argv.Length == 2 && (EqualNoCase(argv[1], "true") || EqualNoCase(argv[1], "false")))
                return "True";
            i.Print(1, "Console::enable(<true/false>);");
            return "False";
        });
    }

    // src/script/client_commands.cpp registerClientCommands: registers every client-side command.
    // rootDir: game directory for saveActionMap.
    public static ClientState RegisterClientCommands(Interpreter interp, ResourceManager? res, string rootDir)
    {
        var st = new ClientState();
        // The world owns the SimGame action map from the start; `bind` writes to it.
        CreateMap(st, res, "SimGame.sae");

        // Table-driven commands: output depends only on the argument count and texts.
        Rule[] rules = ClientCommandTable.Rules;
        int count = rules.Length;
        for (int first = 0; first < count;)
        {
            int last = first;
            while (last + 1 < count && rules[last + 1].Name == rules[first].Name)
                ++last;
            string nm = rules[first].Name;
            if (nm.StartsWith("LS::", StringComparison.Ordinal) || nm == "ircNick" || nm == "ircName" ||
                nm == "ircListPeople" || nm == "Console::enable")
            {
                first = last + 1; // landscape commands: registerLandscape
                continue;
            }
            int lo = first, hi = last;
            interp.AddCommand(nm, (i, argv) =>
            {
                int n = argv.Length - 1;
                for (int k = lo; k <= hi; ++k)
                    if (n >= rules[k].Lo && n <= rules[k].Hi)
                    {
                        PrintLines(i, Expand(rules[k].Out, argv));
                        return rules[k].Ret;
                    }
                return "False";
            });
            first = last + 1;
        }

        RegisterActionMaps(interp, res, rootDir, st);
        RegisterMouseGetters(interp, st);
        RegisterLandscape(interp);
        RegisterIrcAndConsole(interp);
        return st;
    }
}
