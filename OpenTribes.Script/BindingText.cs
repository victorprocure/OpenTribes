namespace OpenTribes.Script;

/// <summary>Builds the key text of a controls screen row (FearGui::CFGButton, find 0049df98 over 0049d110, format 0049cfa0 and 0049cf1c).</summary>
/// <remarks>The result starts with up to three modifier codes (1 shift, 2 control, 3 alt) followed by the key or button name.</remarks>
public static class BindingText
{
    // Object names by object type (table 00621fa8); "%s%d" appends the instance.
    private static string? ObjectName(byte objType) => objType switch
    {
        11 => "X-Axis",
        12 => "Y-Axis",
        13 => "Z-Axis",
        14 => "RX-Aaxis",
        15 => "RY_Axis",
        16 => "RZ-Axis",
        17 => "Slider",
        3 => "POV",
        4 => "X-POV",
        5 => "Y-POV",
        6 => "U-POV",
        7 => "D-POV",
        8 => "L-POV",
        9 => "R-POV",
        2 => "Button",
        10 => "Key",
        _ => null,
    };

    // 0049cf1c: every modifier bit of a group turns into one code, in the order shift, control, alt.
    private static string ModifierCodes(byte m)
    {
        var s = "";
        if ((m & 0x03) != 0)
            s += '\x01';
        if ((m & 0x0c) != 0)
            s += '\x02';
        if ((m & 0x30) != 0)
            s += '\x03';
        return s;
    }

    /// <summary>Formats the text of one input event (0049cfa0).</summary>
    /// <param name="ev">The input event.</param>
    /// <returns>The modifier codes followed by the key or button name.</returns>
    public static string Format(InputEvent ev)
    {
        var s = "";
        if (ev.DeviceType == 3)
        {
            s = ModifierCodes(ev.Modifiers);
            if (ev.ObjType == 10 && ev.ObjInst != 0)
            {
                ClientCommands.KeyName? k = null;
                foreach (ClientCommands.KeyName c in ClientKeyTable.Keys)
                    if (c.Code == ev.ObjInst)
                    {
                        k = c;
                        break;
                    }
                if (k != null)
                {
                    var n = k.Value.Name ?? k.Value.Lower.ToString();
                    s += n.Length > 0 ? char.ToUpperInvariant(n[0]) + n[1..] : n;   // toupper of the first character (00606454)
                }
            }
            return s;
        }
        s = (ObjectName(ev.ObjType) ?? "") + ev.ObjInst.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ev.ObjType == 2 && ev.DeviceType == 2)
        {
            if (ClientCommands.EqualNoCase(s, "button0"))
                return "L Button";
            if (ClientCommands.EqualNoCase(s, "button1"))
                return "R Button";
            if (ClientCommands.EqualNoCase(s, "button2"))
                return "C Button";
        }
        return s;
    }

    // 0049d110: the next binding at or after `start` with the command (when it has one) or the action, flags and value
    // (within 1 percent) that is not of the opposite make or break kind.
    private static int Find(ActionMap map, int start, string command, int action, bool make, uint flags, float value)
    {
        for (var i = start; i < map.Bindings.Count; ++i)
        {
            ActionBinding b = map.Bindings[i];
            bool hit;
            if (command.Length == 0)
            {
                if (action < 1)
                    return -1;
                var tol = value >= 0 ? value * 0.01f : value * -0.01f;
                hit = !b.IsCommand && b.Flags == flags && b.Action == action && b.ValueF >= value - tol && b.ValueF <= value + tol;
            }
            else
            {
                hit = b.IsCommand && ClientCommands.EqualNoCase(b.Command, command);
            }
            if (!hit)
                continue;
            byte want = (byte)(make ? 1 : 2), other = (byte)(make ? 2 : 1);
            if (b.Event.Action == want || b.Event.Action != other)
                return i;
        }
        return -1;
    }

    /// <summary>Finds the text for a row's make and break description (0049df98).</summary>
    /// <param name="map">The action map, or null.</param>
    /// <param name="makeCommand">The make command.</param>
    /// <param name="makeAction">The make action.</param>
    /// <param name="makeValue">The make value.</param>
    /// <param name="breakCommand">The break command.</param>
    /// <param name="breakAction">The break action.</param>
    /// <param name="breakValue">The break value.</param>
    /// <param name="flags">The binding flags.</param>
    /// <returns>The key text, empty when the map has no such binding.</returns>
    /// <remarks>Searches with 0049d110: the next binding at or after the start with the command (when it has one) or the action, flags and value (within 1 percent) that is not of the opposite make or break kind.</remarks>
    public static string For(ActionMap? map, string makeCommand, int makeAction, float makeValue,
                             string breakCommand, int breakAction, float breakValue, uint flags)
    {
        if (map == null)
            return "";
        var noBreak = breakCommand.Length == 0 && breakAction < 1;
        if (makeCommand.Length == 0 && makeAction < 1)
        {
            if (noBreak)
                return "";
            var bi = Find(map, 0, breakCommand, breakAction, false, flags, breakValue);
            return bi < 0 ? "" : Format(map.Bindings[bi].Event);
        }
        var from = 0;
        while (true)
        {
            var mi = Find(map, from, makeCommand, makeAction, true, flags, makeValue);
            if (mi < 0)
                return "";
            var text = Format(map.Bindings[mi].Event);
            if (noBreak)
                return text;
            var bfrom = 0;
            while (true)
            {
                var bi = Find(map, bfrom, breakCommand, breakAction, false, flags, breakValue);
                if (bi < 0)
                {
                    from = mi + 1;
                    break;
                }
                // 0049da90: the same device type and the same key text.
                if (map.Bindings[bi].Event.DeviceType == map.Bindings[mi].Event.DeviceType && Format(map.Bindings[bi].Event) == text)
                    return text;
                bfrom = bi + 1;
            }
        }
    }
}
