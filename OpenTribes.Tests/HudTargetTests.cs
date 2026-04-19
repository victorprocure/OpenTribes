// docs/gui.md 4.2 Crosshair: the target marks and the label of Crosshair_findTarget [code 004aca84].
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;
using Xunit;

namespace OpenTribes.Tests;

public class HudTargetTests
{
    private static Camera3D Cam() => new(0, 0, 0, 0, 0, 90, 640, 480, 0.1, 1000);

    private static HudTargetCandidate P(string name, double x, double y, double z, int team = 1, double damage = 0, bool dead = false,
                                        bool visible = true, bool clear = true) =>
        new(name, (x, y, z), (x, y, z - 1.5), 0.5, 2.3, team, damage, dead, visible, clear);

    [Fact]
    public void PlayerInSightGetsFoeMarkAndLabelWhenTeamsDiffer()
    {
        HudTargetResult r = HudTargetPicker.Find(Cam(), new[] { P("Ann", 0, 6, 0.3, team: 1) }, 0, 2);
        Assert.Single(r.Marks);
        Assert.Equal(new HudTargetMark(320, 224, false), r.Marks[0]);
        Assert.Equal("Ann", r.Name);
        Assert.Equal(0, r.Damage);
    }

    [Fact]
    public void SameTeamIsFriendOnlyWithTwoTeams()
    {
        var c = new[] { P("Ann", 0, 6, 0.3, team: 0) };
        Assert.True(HudTargetPicker.Find(Cam(), c, 0, 2).Marks[0].Friend);
        Assert.False(HudTargetPicker.Find(Cam(), c, 0, 1).Marks[0].Friend);
    }

    [Fact]
    public void DeadHiddenAndBlockedPlayersAreIgnored()
    {
        var all = new[] { P("a", 0, 6, 0.3, dead: true), P("b", 0, 6, 0.3, visible: false), P("c", 0, 6, 0.3, clear: false) };
        HudTargetResult r = HudTargetPicker.Find(Cam(), all, 0, 2);
        Assert.Empty(r.Marks);
        Assert.Null(r.Name);
    }

    [Fact]
    public void PlayerBehindTheViewerGetsNoMark()
    {
        Assert.Empty(HudTargetPicker.Find(Cam(), new[] { P("Ann", 0, -6, 0.3) }, 0, 2).Marks);
    }

    [Fact]
    public void NearestPlayerUnderTheCentreGivesTheLabel()
    {
        var far = P("Far", 0, 12, 0.3);
        var near = P("Near", 0, 6, 0.3);
        Assert.Equal("Near", HudTargetPicker.Find(Cam(), new[] { far, near }, 0, 2).Name);
        Assert.Equal("Near", HudTargetPicker.Find(Cam(), new[] { near, far }, 0, 2).Name);
    }

    [Fact]
    public void PlayerOutsideThePickBoxHasAMarkButNoLabel()
    {
        // 3 m to the side at 6 m: 160 pixels from the centre, the box is about 32 wide
        HudTargetResult r = HudTargetPicker.Find(Cam(), new[] { P("Ann", 3, 6, 0.3) }, 0, 2);
        Assert.Single(r.Marks);
        Assert.Null(r.Name);
    }

    [Fact]
    public void BoxStartsFourAboveTheMarkAndReachesBelowTheHeadByTheHeight()
    {
        // head projects to y 224: the centre row 240 is inside [220, 224 + height 122]; a head 20 pixels lower is above the box
        Assert.NotNull(HudTargetPicker.Find(Cam(), new[] { P("Ann", 0, 6, 0.3) }, 0, 2).Name);
        Assert.Null(HudTargetPicker.Find(Cam(), new[] { P("Ann", 0, 6, -0.45) }, 0, 2).Name);
    }

    [Fact]
    public void DeadlyDamageHasNoLabelAndFarPlayersNoHealthBox()
    {
        Assert.Null(HudTargetPicker.Find(Cam(), new[] { P("Ann", 0, 6, 0.3, damage: 1.0) }, 0, 2).Name);
        // 0.5 m wide at 400 m: half width 0.2 pixels, below 1
        HudTargetResult far = HudTargetPicker.Find(Cam(), new[] { P("Ann", 0, 400, 0.3) }, 0, 2);
        Assert.Equal("Ann", far.Name);
        Assert.True(far.Damage < 0);
    }

    [Theory]
    [InlineData(0.0, 25, 25)]
    [InlineData(0.5, 25, 12)]
    [InlineData(0.99, 25, 1)]
    [InlineData(1.0, 25, 0)]
    [InlineData(-1.0, 25, 0)]
    [InlineData(0.0, 18, 18)]
    public void BarLengthIsTheHealthShareOfTheInnerWidth(double damage, int inner, int expected)
    {
        Assert.Equal(expected, HudTargetPicker.BarLength(damage, inner));
    }
}
