// The chat menu key logic (docs/gui.md FGChatMenu notes, decompile 00402068 event handler, 004021cc key match, 00401fe0 setCMMode).
// Synthetic tree only.
using OpenTribes.Game;

namespace OpenTribes.Tests;

public class ChatMenuKeysTests
{
    private static ChatMenuTree Build()
    {
        var t = new ChatMenuTree();
        t.Add("vOffense\\aAttack!", new[] { "say", "1", "Attack!" });
        t.Add("vOffense\\wWait", new[] { "say", "1", "Wait" });
        t.Add("tTarget\\aAcquired", new[] { "say", "0", "Acquired" });
        return t;
    }

    private static bool Press(ChatMenuTree t, int scan, char ch, List<string>? ran = null, byte mods = 0) =>
        t.OnKey(scan, 1, ch, mods, w => ran?.AddRange(new[] { string.Join(' ', w) }));

    [Fact]
    public void ClosedMenuIgnoresKeys()
    {
        ChatMenuTree t = Build();
        Assert.False(t.Showing);
        Assert.False(Press(t, 0x2f, 'v'));
    }

    [Fact]
    public void TopLevelListsItemsAndNoPath()
    {
        ChatMenuTree t = Build();
        t.Mode = 2;
        Assert.True(t.Showing);
        Assert.Equal("", t.Path());
        Assert.Equal(new[] { "v: Offense", "t: Target" }, t.Lines());
    }

    [Fact]
    public void HeadingKeyOpensSubLevelWithoutRunning()
    {
        ChatMenuTree t = Build();
        t.Mode = 2;
        var ran = new List<string>();
        Assert.True(Press(t, 0x2f, 'v', ran));
        Assert.Empty(ran);
        Assert.Equal("Offense", t.Path());
        Assert.Equal(new[] { "a: Attack!", "w: Wait" }, t.Lines());
        Assert.True(t.Showing);
    }

    [Fact]
    public void LeafRunsCommandAndClosesMode2()
    {
        ChatMenuTree t = Build();
        t.Mode = 2;
        var ran = new List<string>();
        Press(t, 0x2f, 'v', ran);
        Assert.True(Press(t, 0x1e, 'a', ran));
        Assert.Equal(new[] { "say 1 Attack!" }, ran);
        Assert.False(t.Showing);
        Assert.Equal(0, t.Mode);
    }

    [Fact]
    public void LeafKeepsModeOneMenuOpenAtTop()
    {
        ChatMenuTree t = Build();
        t.Mode = 1;
        var ran = new List<string>();
        Press(t, 0x2f, 'v', ran);
        Press(t, 0x11, 'w', ran);
        Assert.Equal(new[] { "say 1 Wait" }, ran);
        Assert.True(t.Showing);
        Assert.Equal("", t.Path());
    }

    [Fact]
    public void UnknownKeyIsNotConsumed()
    {
        ChatMenuTree t = Build();
        t.Mode = 2;
        Assert.False(Press(t, 0x1e, 'a'));
        Assert.True(t.Showing);
    }

    [Fact]
    public void KeyMatchIsExactAndNeedsNoModifier()
    {
        ChatMenuTree t = Build();
        t.Mode = 2;
        Assert.False(Press(t, 0x2f, 'V'));
        Assert.False(Press(t, 0x2f, 'v', mods: 1));
        Assert.False(Press(t, 0x2f, '\0'));
        Assert.True(Press(t, 0x2f, 'v'));
    }

    [Fact]
    public void EscapeClosesMode2FromAnyLevel()
    {
        ChatMenuTree t = Build();
        t.Mode = 2;
        Assert.True(Press(t, 1, '\0'));
        Assert.False(t.Showing);
        t.Mode = 2;
        Press(t, 0x2f, 'v');
        Assert.True(Press(t, 1, '\0'));
        Assert.False(t.Showing);
    }

    [Fact]
    public void EscapeInMode1GoesBackThenPassesThrough()
    {
        ChatMenuTree t = Build();
        t.Mode = 1;
        Press(t, 0x2f, 'v');
        Assert.True(Press(t, 1, '\0'));
        Assert.Equal("", t.Path());
        Assert.True(t.Showing);
        Assert.False(Press(t, 1, '\0'));
    }

    [Fact]
    public void ReleaseOfConsumedKeyIsConsumedOnce()
    {
        ChatMenuTree t = Build();
        t.Mode = 2;
        Press(t, 0x2f, 'v');
        Assert.True(t.OnKey(0x2f, 2, 'v', 0, _ => { }));
        Assert.False(t.OnKey(0x2f, 2, 'v', 0, _ => { }));
        Assert.False(t.OnKey(0x1e, 2, 'a', 0, _ => { }));
    }

    [Fact]
    public void SetModeResetsLevel()
    {
        ChatMenuTree t = Build();
        t.Mode = 2;
        Press(t, 0x2f, 'v');
        t.Mode = 2;
        Assert.Equal("", t.Path());
    }

    [Fact]
    public void EmptyMenuIsNotShown()
    {
        var t = new ChatMenuTree { Mode = 2 };
        Assert.False(t.Showing);
        Assert.False(Press(t, 0x2f, 'v'));
    }

    [Fact]
    public void NestedPathJoinsWithColon()
    {
        var t = new ChatMenuTree();
        t.Add("aOne\\bTwo\\cLeaf", new[] { "x" });
        t.Mode = 1;
        Press(t, 0x1e, 'a');
        Press(t, 0x30, 'b');
        Assert.Equal("One:Two", t.Path());
        Assert.Equal(new[] { "c: Leaf" }, t.Lines());
    }
}
