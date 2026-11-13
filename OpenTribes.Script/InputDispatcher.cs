// docs/client-scripts.md 3.5 to 3.8: the path of one device event to an action or a script call. The input manager
// tracks the modifier byte and the key character (3.6 step 1), offers the event to the GUI (step 3), tracks held keys
// (step 4) and walks the pushed map stack (step 5, 3.6a, 3.7). Pure logic: the platform layer makes the events.
namespace OpenTribes.Script;

// The object that receives action events of a pushed map (the play delegate, 3.8). `kind` is 2 for make and break
// events, 4 for mouse movement, 10 for pov events, else 1 (3.6 step 5).
public interface IActionConsumer
{
    // True when the consumer handled the action.
    bool OnAction(int action, int kind, float value);
    // True when the consumer is flagged as removing (flags & 3): every action counts as handled.
    bool Removing { get; }
}

public sealed class InputDispatcher
{
    private sealed class Held
    {
        public InputEvent Event;
    }

    public readonly ClientState State;
    private readonly List<(ActionMap Map, IActionConsumer Consumer)> mStack = new();
    private readonly List<Held> mHeld = new();

    // The manager's modifier byte (3.6 step 1).
    public byte Modifiers { get; private set; }
    // Console text of a command binding.
    public Func<string, string> Eval = _ => "";
    // The GUI side: offered every event before the maps. Returns true when the canvas consumed it. Arguments: the
    // event with modifiers and key character filled in, and the value.
    public Func<InputEvent, float, char, bool>? OfferToGui;

    public InputDispatcher(ClientState state) => State = state;

    public int StackDepth => mStack.Count;
    public int HeldCount => mHeld.Count;
    public bool IsPushed(string mapName) => mStack.Any(p => ClientCommands.EqualNoCase(p.Map.Name, ClientCommands.ActionMapName(mapName)));

    // 0x2a 0x36 shift, 0x1d 0x9d control, 0x38 0xb8 alt: scan code to modifier bit (3.6 step 1, 3.5).
    public static byte ModifierBit(int scan) => scan switch
    {
        0x2a => 1, 0x36 => 2, 0x1d => 4, 0x9d => 8, 0x38 => 16, 0xb8 => 32, _ => 0,
    };

    // 3.4 InputKey_characterFor: the shifted entry when either shift bit is set, else the unshifted entry.
    public static char CharacterFor(int scan, byte modifiers)
    {
        foreach (ClientCommands.KeyName k in ClientKeyTable.Keys)
            if (k.Code == scan)
                return (modifiers & 3) != 0 ? k.Upper : k.Lower;
        return '\0';
    }

    // ---- 3.6a binding match ----

    // Group compare through the 4 by 4 table (0063352c): rows are the binding's value of a group, columns the event's.
    // 0 matches only 0; 1 (left) matches 1 and 3; 2 (right) matches 2 and 3; 3 (either) matches 1, 2 and 3.
    public static bool GroupMatches(int binding, int ev) => binding switch
    {
        0 => ev == 0,
        1 => ev == 1 || ev == 3,
        2 => ev == 2 || ev == 3,
        _ => ev is 1 or 2 or 3,
    };

    public static bool ModifiersMatch(byte binding, byte ev) =>
        GroupMatches(binding & 3, ev & 3) && GroupMatches((binding >> 2) & 3, (ev >> 2) & 3) &&
        GroupMatches((binding >> 4) & 3, (ev >> 4) & 3);

    // 3.6a: device type, device instance, object type and object instance equal; action equal for make and break; the
    // modifier groups through the table. A binding that is a modifier alone matches the press of any key of that modifier.
    public static bool Matches(in InputEvent b, in InputEvent ev)
    {
        if (b.DeviceType != ev.DeviceType || b.DeviceInst != ev.DeviceInst)
            return false;
        bool modAlone = b.DeviceType == 3 && b.ObjType == 10 && b.ObjInst == 0 && b.Modifiers != 0 && (b.Action == 1 || b.Action == 2);
        if (modAlone)
        {
            if (ev.ObjType != 10 || ev.Action != b.Action)
                return false;
            byte bit = ModifierBit(ev.ObjInst);
            return bit != 0 && (b.Modifiers & bit) != 0;
        }
        if (b.ObjType != ev.ObjType || b.ObjInst != ev.ObjInst)
            return false;
        if ((b.Action == 1 || b.Action == 2) && b.Action != ev.Action)
            return false;
        if (b.Action == 3 && ev.Action != 3)
            return false;
        // 00508854 compares the modifier groups (00508a78) only when the binding's device is the keyboard (type 3). Mouse
        // axis and button events match whatever modifiers are held, so look keeps working while Shift is down.
        return b.DeviceType != 3 || ModifiersMatch(b.Modifiers, ev.Modifiers);
    }

    // ---- 3.7 value shaping ----

    // ActionEvent_pickValue and ActionEvent_shapeValue: floats, in the order of the table. `mouse` is the device test of 00509384
    // (event device type 2): Center and Flip treat every mouse event (axis and buttons) as the "not the plain position" case.
    public static float ShapeValue(ActionBinding b, float input, bool mouse)
    {
        if ((b.Flags & ActionBinding.Value) != 0)
            return b.ValueF;
        float v = input;
        bool center = (b.Flags & ActionBinding.Center) != 0;
        if (center && !mouse)
            v = 1f - 2f * v;
        if ((b.Flags & ActionBinding.DeadZone) != 0)
        {
            float d = b.DeadZoneF;
            if (v <= 0f)
                v = v <= -d ? (v + d) / (1f - d) : 0f;
            else
                v = v >= d ? (v - d) / (1f - d) : 0f;
        }
        if ((b.Flags & ActionBinding.Flip) != 0)
            v = !center && !mouse ? 1f - v : -v;
        if ((b.Flags & ActionBinding.Square) != 0)
        {
            bool neg = v < 0f;
            v *= v;
            if (neg)
                v = -v;
        }
        if ((b.Flags & ActionBinding.Cube) != 0)
            v = v * v * v;
        if ((b.Flags & ActionBinding.Scale) != 0)
            v = b.ScaleF * v;
        return v;
    }

    private static int KindOf(in InputEvent ev)
    {
        if (ev.Action == 1 || ev.Action == 2)
            return 2;
        if (ev.DeviceType == 2 && ev.Action == 3)
            return 4;
        if (ev.ObjType is >= 3 and <= 9)
            return 10;
        return 1;
    }

    // ---- 3.8 push and pop ----

    public void Push(ActionMap map, IActionConsumer consumer)
    {
        Pop(map, consumer, silent: true);
        // Held keys that the new map binds get a break first, so a movement key held from the old map stops.
        foreach (Held h in mHeld.ToArray())
            if (map.Bindings.Any(b => b.Event.Action == 1 && Matches(b.Event, h.Event) || b.Event.Action == 2 && Matches(b.Event, BreakOf(h.Event))))
                DispatchStack(BreakOf(h.Event), 0f);
        mStack.Insert(0, (map, consumer));
    }

    public void Pop(ActionMap map, IActionConsumer consumer, bool silent = false)
    {
        var removed = mStack.Where(p => p.Map == map && p.Consumer == consumer).ToList();
        if (removed.Count == 0)
            return;
        mStack.RemoveAll(p => p.Map == map && p.Consumer == consumer);
        if (silent)
            return;
        // Held keys the popped map was handling and no remaining map binds are released through the popped map.
        foreach (Held h in mHeld.ToArray())
        {
            InputEvent br = BreakOf(h.Event);
            ActionBinding? handled = FindBinding(map, br) ?? FindBinding(map, h.Event);
            if (handled == null || mStack.Any(p => FindBinding(p.Map, br) != null))
                continue;
            ActionBinding? bb = FindBinding(map, br);
            if (bb != null && !bb.IsCommand)
                consumer.OnAction(bb.Action, KindOf(br), ShapeValue(bb, 0f, br.DeviceType == 2));
        }
    }

    public void Clear()
    {
        mStack.Clear();
        mHeld.Clear();
        Modifiers = 0;
    }

    private static InputEvent BreakOf(InputEvent make)
    {
        make.Action = 2;
        return make;
    }

    private static ActionBinding? FindBinding(ActionMap map, in InputEvent ev)
    {
        foreach (ActionBinding b in map.Bindings)
            if (Matches(b.Event, ev))
                return b;
        return null;
    }

    // ---- 3.6 processing ----

    // One device event. `scan`/`obj` are in ev (ObjInst for keys is the scan code). Returns true when something handled it.
    public bool Process(InputEvent ev, float value)
    {
        bool isKey = ev.DeviceType == 3 && ev.DeviceInst == 0 && ev.ObjType == 10;
        char ch = '\0';
        // 1: modifier state; the modifier key's own event carries 0, every other event the current byte.
        if (isKey && (ev.Action == 1 || ev.Action == 2) && ModifierBit(ev.ObjInst) is byte bit && bit != 0)
        {
            Modifiers = ev.Action == 1 ? (byte)(Modifiers | bit) : (byte)(Modifiers & ~bit);
            ev.Modifiers = 0;
        }
        else
            ev.Modifiers = Modifiers;
        if (ev.DeviceType == 3 && ev.ObjType == 10)
            ch = CharacterFor(ev.ObjInst, Modifiers);

        // 3: a break whose make is held goes straight to the maps; everything else is offered to the GUI first.
        bool heldBreak = ev.Action == 2 && FindHeld(ev) != null;
        if (!heldBreak && OfferToGui != null && OfferToGui(ev, value, ch))
            return true;

        // 4: held list.
        if (ev.Action == 1)
        {
            if (FindHeld(ev) == null)
                mHeld.Add(new Held { Event = ev });
        }
        else if (ev.Action == 2)
        {
            Held? h = FindHeld(ev);
            if (h == null)
                return false;
            ev.Modifiers = h.Event.Modifiers;
            mHeld.Remove(h);
        }

        // 5 and 6: the stack, then once more without modifiers.
        if (DispatchStack(ev, value))
            return true;
        if ((ev.Action == 1 || ev.Action == 2) && ev.Modifiers != 0)
        {
            ev.Modifiers = 0;
            return DispatchStack(ev, value);
        }
        return false;
    }

    private Held? FindHeld(in InputEvent ev)
    {
        foreach (Held h in mHeld)
            if (h.Event.DeviceType == ev.DeviceType && h.Event.DeviceInst == ev.DeviceInst &&
                h.Event.ObjType == ev.ObjType && h.Event.ObjInst == ev.ObjInst)
                return h;
        return null;
    }

    // 3.6 step 5 (ActionMap_dispatch 00509738).
    // The world's global map (SimGame.sae, `bind` writes to it): searched after every pushed map, in both passes of step 5 and 6.
    // The stock client lets control-o (console.cs `messageCanvasDevice`) end the search; without it the retry without modifiers
    // would hand the key to the plain `o` binding (objectives screen). Only command bindings run from this map.
    public ActionMap? GlobalMap { get; set; }

    private bool DispatchStack(in InputEvent ev, float value)
    {
        foreach (var (map, consumer) in mStack.ToArray())
        {
            ActionBinding? b = FindBinding(map, ev);
            if (b == null)
                continue;
            if (b.IsCommand)
            {
                Eval(b.Command);
                return true;
            }
            float v = ShapeValue(b, value, ev.DeviceType == 2);
            bool r = consumer.OnAction(b.Action, KindOf(ev), v);
            if (consumer.Removing || r)
                return true;
        }
        if (GlobalMap != null && FindBinding(GlobalMap, ev) is { IsCommand: true } g)
        {
            Eval(g.Command);
            return true;
        }
        return false;
    }
}

// Q1 (docs/client-scripts.md 7): SDL3 scancodes to the PC scan codes of the key table (client-app.md 3.4).
public static class ScanCodeMap
{
    // SDL_Scancode numbers (SDL3 SDL_scancode.h) to DirectInput key codes; 0 when the key has no code.
    public static int FromSdl(int sdl)
    {
        if (sdl >= 4 && sdl <= 29)          // A..Z
            return kLetters[sdl - 4];
        if (sdl >= 30 && sdl <= 38)         // 1..9
            return 2 + (sdl - 30);
        if (sdl == 39)
            return 0x0b;                    // 0
        if (sdl >= 58 && sdl <= 67)         // F1..F10
            return 0x3b + (sdl - 58);
        if (sdl >= 89 && sdl <= 91)         // keypad 1 2 3
            return 0x4f + (sdl - 89);
        if (sdl >= 92 && sdl <= 94)         // keypad 4 5 6
            return 0x4b + (sdl - 92);
        if (sdl >= 95 && sdl <= 97)         // keypad 7 8 9
            return 0x47 + (sdl - 95);
        return sdl switch
        {
            40 => 0x1c,   // return
            41 => 0x01,   // escape
            42 => 0x0e,   // backspace
            43 => 0x0f,   // tab
            44 => 0x39,   // space
            45 => 0x0c,   // minus
            46 => 0x0d,   // equals
            47 => 0x1a,   // [
            48 => 0x1b,   // ]
            49 => 0x2b,   // backslash
            50 => 0x2b,   // non-US hash
            51 => 0x27,   // ;
            52 => 0x28,   // '
            53 => 0x29,   // `
            54 => 0x33,   // ,
            55 => 0x34,   // .
            56 => 0x35,   // /
            57 => 0x3a,   // caps lock
            68 => 0x57,   // F11
            69 => 0x58,   // F12
            70 => 0xb7,   // print screen (sysreq)
            71 => 0x46,   // scroll lock
            73 => 0xd2,   // insert
            74 => 0xc7,   // home
            75 => 0xc9,   // page up (prior)
            76 => 0xd3,   // delete
            77 => 0xcf,   // end
            78 => 0xd1,   // page down (next)
            79 => 0xcd,   // right
            80 => 0xcb,   // left
            81 => 0xd0,   // down
            82 => 0xc8,   // up
            83 => 0x45,   // num lock
            84 => 0xb5,   // keypad /
            85 => 0x37,   // keypad *
            86 => 0x4a,   // keypad -
            87 => 0x4e,   // keypad +
            88 => 0x9c,   // keypad enter
            98 => 0x52,   // keypad 0
            99 => 0x53,   // keypad .
            100 => 0x2b,  // non-US backslash
            103 => 0x8d,  // keypad =
            104 => 0x64,  // F13
            105 => 0x65,  // F14
            106 => 0x66,  // F15
            120 => 0x95,  // stop
            133 => 0xb3,  // keypad ,
            224 => 0x1d,  // left control
            225 => 0x2a,  // left shift
            226 => 0x38,  // left alt
            227 => 0xdb,  // left windows
            228 => 0x9d,  // right control
            229 => 0x36,  // right shift
            230 => 0xb8,  // right alt
            231 => 0xdc,  // right windows
            _ => 0,
        };
    }

    // A..Z in SDL order to the key codes: Q W E R T Y U I O P = 0x10.., A S D F G H J K L = 0x1e.., Z X C V B N M = 0x2c..
    private static readonly int[] kLetters =
    {
        0x1e, 0x30, 0x2e, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,   // A B C D E F G H I J K L M
        0x31, 0x18, 0x19, 0x10, 0x13, 0x1f, 0x14, 0x16, 0x2f, 0x11, 0x2d, 0x15, 0x2c,   // N O P Q R S T U V W X Y Z
    };
}
