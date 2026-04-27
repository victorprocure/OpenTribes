using OpenTribes.Script;
using Xunit;

namespace OpenTribes.Tests;

// FearGui::CFGButton key text (docs/gui.md 3.3, find 0049df98, format 0049cfa0).
public class BindingTextTests
{
    private static byte Code(char c) => ClientKeyTable.Keys.First(k => k.Lower == c).Code;

    private static ActionBinding Key(char c, int action, byte kind = 1, byte mods = 0, float value = 0) => new()
    {
        Event = new InputEvent { DeviceType = 3, ObjType = 10, ObjInst = Code(c), Action = kind, Modifiers = mods },
        Action = action, Flags = ActionBinding.None, ValueF = value,
    };

    [Fact]
    public void KeyNameIsCapitalisedAndModifiersAreCodes()
    {
        var m = new ActionMap();
        m.Bindings.Add(Key('w', 5));
        m.Bindings.Add(Key('x', 6, mods: 4 | 2));
        Assert.Equal("W", BindingText.For(m, "", 5, 0, "", 0, 0, ActionBinding.None));
        Assert.Equal("\x01\x02X", BindingText.For(m, "", 6, 0, "", 0, 0, ActionBinding.None));
        Assert.Equal("", BindingText.For(m, "", 7, 0, "", 0, 0, ActionBinding.None));
        Assert.Equal("", BindingText.For(null, "", 5, 0, "", 0, 0, ActionBinding.None));
    }

    [Fact]
    public void NamedKeysAndMouseButtons()
    {
        var m = new ActionMap();
        m.Bindings.Add(new ActionBinding { Event = new InputEvent { DeviceType = 3, ObjType = 10, ObjInst = ClientKeyTable.Keys.First(k => k.Name == "tab").Code, Action = 1 }, Action = 1, Flags = ActionBinding.None });
        m.Bindings.Add(new ActionBinding { Event = new InputEvent { DeviceType = 2, ObjType = 2, ObjInst = 1, Action = 1 }, Action = 2, Flags = ActionBinding.None });
        Assert.Equal("Tab", BindingText.For(m, "", 1, 0, "", 0, 0, ActionBinding.None));
        Assert.Equal("R Button", BindingText.For(m, "", 2, 0, "", 0, 0, ActionBinding.None));
    }

    [Fact]
    public void CommandRowsMatchTheCommandAndBreakMustShareTheKey()
    {
        var m = new ActionMap();
        m.Bindings.Add(new ActionBinding { Event = new InputEvent { DeviceType = 3, ObjType = 10, ObjInst = Code('a'), Action = 1 }, IsCommand = true, Command = "go();" });
        m.Bindings.Add(new ActionBinding { Event = new InputEvent { DeviceType = 3, ObjType = 10, ObjInst = Code('b'), Action = 1 }, IsCommand = true, Command = "go();" });
        m.Bindings.Add(new ActionBinding { Event = new InputEvent { DeviceType = 3, ObjType = 10, ObjInst = Code('b'), Action = 2 }, IsCommand = true, Command = "stop();" });
        Assert.Equal("A", BindingText.For(m, "GO();", 0, 0, "", 0, 0, 0));
        Assert.Equal("B", BindingText.For(m, "go();", 0, 0, "stop();", 0, 0, 0));
    }

    [Fact]
    public void ValueMatchesWithinOnePercent()
    {
        var m = new ActionMap();
        m.Bindings.Add(Key('q', 9, value: 1.005f));
        Assert.Equal("Q", BindingText.For(m, "", 9, 1.0f, "", 0, 0, ActionBinding.None));
        Assert.Equal("", BindingText.For(m, "", 9, 2.0f, "", 0, 0, ActionBinding.None));
    }
}
