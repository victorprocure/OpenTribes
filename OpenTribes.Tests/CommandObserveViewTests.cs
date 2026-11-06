// The observer panel of the command screen and its 3D view child (docs/gui.md 3.3): CMDObserve wake 004b47ac, toggle 004b48bc, observe 004b4818,
// draw 004b4de4 with the opening 004b4ad8, the bars 004b4d60 and the noise 004b4c9c / 004b4bdc, and the TSControl draw 00546dc4 that renders the
// world into the child's rectangle. The renderer is a stub: what GL draws is not testable here. Synthetic data only.
using OpenTribes.Game;
using OpenTribes.Gui;
using OpenTribes.Platform.Gfx;
using Rig = OpenTribes.Tests.CommandOrdersTests.Rig;
using ClipDev = OpenTribes.Tests.GuiCrosshairZoomTests.ClipDev;

namespace OpenTribes.Tests;

public class CommandObserveViewTests
{
    // The panel at (0, 0) 200 x 150 in the root, its view at (10, 20) 100 x 60, the root set as content (wake runs).
    private static (Rig Rig, GuiCmdObserve Panel, GuiPlayView View, GuiPaint Paint, ClipDev Dev) PanelRig()
    {
        var r = new Rig();
        var panel = (GuiCmdObserve)GuiFixture.Make("FearGui::CMDObserve", 0, 0, 200, 150);
        var view = (GuiPlayView)GuiFixture.Make("SimGui::TSControl", 10, 20, 100, 60);
        view.Id = GuiCmdObserve.ViewId;
        panel.AddChild(view);
        r.Root.AddChild(panel);
        r.Fx.Content(r.Root);
        var dev = new ClipDev();
        return (r, panel, view, new GuiPaint(dev, r.Fx.Host), dev);
    }

    private static void Show(Rig r, GuiCmdObserve panel)
    {
        r.World.Players.Clients[2050].Selected = true;
        panel.Toggle(true);
    }

    [Fact]
    public void WakeFindsTheViewHidesThePanelAndForgetsThePlayer() // [004b47ac]
    {
        var (_, panel, _, _, _) = PanelRig();
        Assert.False(panel.Visible);
        Assert.Equal(0, panel.Observed);
    }

    [Fact]
    public void TheToggleStartsTheViewAsALineAtTheMiddleWithTheControlObjectAsCamera() // [004b48bc, 005473ec]
    {
        var (r, panel, view, _, _) = PanelRig();
        Assert.False(view.CameraAttached);
        Show(r, panel);
        Assert.True(panel.Visible);
        Assert.True(view.CameraAttached);
        // x = w / 2 + x0, y = h / 2 + y0, width 0, height 2, applied to the view at once
        Assert.Equal((60, 50, 0, 2), panel.ViewRect);
        Assert.Equal((60, 50, 0, 2), (view.X, view.Y, view.W, view.H));
        Assert.False(panel.IsOpen);
        Assert.Equal(4, panel.NoiseValue);
    }

    [Fact]
    public void TheViewOpensWidthFirstThenHeightOneStepPerDraw() // [004b4ad8]
    {
        var (r, panel, view, p, dev) = PanelRig();
        Show(r, panel);
        var seen = new List<(int, int, int, int)>();
        for (int i = 0; i < 12; ++i)
        {
            panel.Render(p, 0, 0);
            seen.Add((view.X, view.Y, view.W, view.H));
        }
        // width: the left edge goes 8 per step to 10, the width 16 per step to 100 (7 steps); then height: 10 up to 20, 20 more to 60 (3 steps)
        Assert.Equal((52, 50, 16, 2), seen[0]);
        Assert.Equal((10, 50, 100, 2), seen[6]);
        Assert.Equal((10, 40, 100, 22), seen[7]);
        Assert.Equal((10, 20, 100, 60), seen[9]);
        Assert.True(panel.IsOpen);
        Assert.Equal((10, 20, 100, 60), panel.ViewRect);
        // the panel is filled with palette 0xf4 first
        Assert.Equal((0xf4, new GfxRect(0, 0, 199, 149)), (dev.Rects[0].Color, dev.Rects[0].Rect));
    }

    [Fact]
    public void AHiddenPanelDrawsNothing() // [00535830 visibility, 0053a3f4]
    {
        var (r, panel, _, p, dev) = PanelRig();
        r.Root.Render(p, 0, 0);
        Assert.DoesNotContain(dev.Rects, x => x.Color == 0xf4);
        Show(r, panel);
        r.Root.Render(p, 0, 0);
        Assert.Contains(dev.Rects, x => x.Color == 0xf4);
        panel.Toggle(false);
        dev.Rects.Clear();
        r.Root.Render(p, 0, 0);
        Assert.DoesNotContain(dev.Rects, x => x.Color == 0xf4);
    }

    private static void Open(GuiCmdObserve panel, GuiPaint p)
    {
        for (int i = 0; i < 20 && !panel.IsOpen; ++i)
            panel.Render(p, 0, 0);
        Assert.True(panel.IsOpen);
    }

    [Fact]
    public void TheBarsGrowFourPixelsEveryThirtyMillisecondsToTheHealthAndEnergyOfTheFollowedPlayer() // [004b4de4, 004b4d60, 004349e4, 00434a04]
    {
        var (r, panel, _, p, dev) = PanelRig();
        // the control object is an observer camera orbiting a player with 25 % damage and half energy
        var cam = new ClientGhost { Index = 2, Class = "ObserverCamera" };
        var body = new ClientGhost { Index = 7, Class = "Player" };
        body.State["damage"] = 0.25;
        body.State["energy"] = 0.5;
        r.World.Ghosts.Insert(cam);
        r.World.Ghosts.Insert(body);
        r.World.Psc.ControlGhost = 2;
        r.World.Psc.ControlClass = "ObserverCamera";
        r.World.Psc.ControlState = new Dictionary<string, object?> { ["camMode"] = 3L, ["orbitGhost"] = 7L };
        Show(r, panel);
        Open(panel, p);
        Assert.Equal((45, 30), panel.BarTargets());
        // the first step is overdue at once (the frame that finishes the opening), then every 30 ms
        Assert.Equal((4, 4), panel.BarHeights);
        r.Fx.Clock += 20;
        panel.Render(p, 0, 0);
        Assert.Equal((4, 4), panel.BarHeights);
        r.Fx.Clock += 20;
        panel.Render(p, 0, 0);
        Assert.Equal((8, 8), panel.BarHeights);
        for (int i = 0; i < 20; ++i)
        {
            r.Fx.Clock += 40;
            panel.Render(p, 0, 0);
        }
        Assert.Equal((45, 30), panel.BarHeights);
        dev.Rects.Clear();
        panel.Render(p, 0, 0);
        // right of the view (x 10 + 100), bottom at y 80: damage bar at +4, 2 wide, palette 0xf2 (28 to 56 pixels); energy at +8, 0xe7
        Assert.Contains(dev.Rects, x => x.Color == 0xf2 && x.Rect == new GfxRect(114, 35, 115, 79));
        Assert.Contains(dev.Rects, x => x.Color == 0xe7 && x.Rect == new GfxRect(118, 50, 119, 79));
    }

    [Fact]
    public void ABarWithAValueIsAtLeastOnePixelAndFullHealthUsesTheGreen() // [004b4de4]
    {
        var (r, panel, _, _, _) = PanelRig();
        Show(r, panel);
        // no control object: damage 0, energy full
        Assert.Equal((60, 60), panel.BarTargets());
        r.World.Ghosts.Insert(new ClientGhost { Index = 2, Class = "ObserverCamera" });
        var body = new ClientGhost { Index = 7, Class = "Player" };
        body.State["damage"] = 0.995;
        body.State["energy"] = 0.001;
        r.World.Ghosts.Insert(body);
        r.World.Psc.ControlGhost = 2;
        r.World.Psc.ControlClass = "ObserverCamera";
        r.World.Psc.ControlState = new Dictionary<string, object?> { ["camMode"] = 3L, ["orbitGhost"] = 7L };
        Assert.Equal((1, 1), panel.BarTargets());
    }

    [Fact]
    public void TheNoiseThinsByStepsOfSixOnceTheViewIsOpenAndStopsAtEighty() // [004b4c9c, 004b48bc]
    {
        var (r, panel, _, p, dev) = PanelRig();
        Show(r, panel);
        Assert.False(panel.NoiseDone);
        // during the opening it only draws
        for (int i = 0; i < 5; ++i)
            panel.Render(p, 0, 0);
        Assert.Equal(4, panel.NoiseValue);
        Open(panel, p);
        int steps = 0;
        while (!panel.NoiseDone && steps < 40)
        {
            r.Fx.Clock += 100; // longer than any interval (0.025 to 0.035 s)
            panel.Render(p, 0, 0);
            ++steps;
        }
        Assert.Equal(13, steps); // 4 + 6 * 13 = 82 >= 80, 4 + 6 * 12 = 76
        dev.Rects.Clear();
        panel.Render(p, 0, 0);
        Assert.DoesNotContain(dev.Rects, x => x.Color == 0xff);
    }

    [Fact]
    public void TheNoiseDrawsWhitePixelsInsideTheViewRectangle() // [004b4bdc]
    {
        var (r, panel, _, p, dev) = PanelRig();
        Show(r, panel);
        panel.Render(p, 0, 0); // 16 x 2 at (52, 50), 1 in 4 pixels
        var white = dev.Rects.Where(x => x.Color == 0xff).ToList();
        Assert.NotEmpty(white);
        Assert.All(white, x =>
        {
            Assert.Equal(x.Rect.Left, x.Rect.Right);
            Assert.InRange(x.Rect.Left, 52, 67);
            Assert.InRange(x.Rect.Top, 50, 51);
        });
        Assert.InRange(white.Count, 1, 16);
    }

    [Fact]
    public void ChoosingAnotherPlayerRestartsTheNoiseAndTheBarsButNotTheOpening() // [004b4818]
    {
        var (r, panel, _, p, _) = PanelRig();
        Show(r, panel);
        Open(panel, p);
        for (int i = 0; i < 15; ++i)
        {
            r.Fx.Clock += 100;
            panel.Render(p, 0, 0);
        }
        Assert.True(panel.NoiseDone);
        Assert.True(panel.Observe(2051));
        Assert.Equal("2048|SCOM|2051", r.Remote[^1]);
        Assert.False(panel.NoiseDone);
        Assert.Equal(4, panel.NoiseValue);
        Assert.Equal((0, 0), panel.BarHeights);
        Assert.True(panel.IsOpen);
    }

    // ---- the TSControl child draws the world itself [00546dc4, 00545f7c] ----

    [Fact]
    public void TheViewRendersTheWorldIntoItsRectangleAndThenDrawsItsChildren() // [00546dc4]
    {
        var (r, panel, view, p, dev) = PanelRig();
        GfxRect? got = null;
        r.Fx.Canvas.ViewRenderer = rect => { got = rect; return true; };
        view.CameraAttached = true;
        var child = GuiFixture.Make("SimGui::Control", 0, 0, 10, 10);
        child.Opaque = 1;
        child.FillColor = 200;
        view.AddChild(child);
        panel.SetVisible(true);
        view.Render(p, 0, 0);
        Assert.Equal(new GfxRect(10, 20, 109, 79), got);
        Assert.Contains(dev.Rects, x => x.Color == 200);
    }

    [Fact]
    public void WithoutACameraTheViewFillsColourZeroAndDrawsNoChildren() // [00546dc4 tail, 00545f7c]
    {
        var (r, panel, view, p, dev) = PanelRig();
        r.Fx.Canvas.ViewRenderer = _ => false;
        view.CameraAttached = true;
        var child = GuiFixture.Make("SimGui::Control", 0, 0, 10, 10);
        child.Opaque = 1;
        child.FillColor = 200;
        view.AddChild(child);
        view.Render(p, 0, 0);
        Assert.Contains(dev.Rects, x => x.Color == 0 && x.Rect == new GfxRect(10, 20, 109, 79));
        Assert.DoesNotContain(dev.Rects, x => x.Color == 200);
    }

    [Fact]
    public void AViewWithoutACameraObjectDoesNotCallTheRenderer() // [005473ec]
    {
        var (r, _, view, p, _) = PanelRig();
        bool called = false;
        r.Fx.Canvas.ViewRenderer = _ => called = true;
        view.Render(p, 0, 0);
        Assert.False(called);
    }

    [Fact]
    public void EnergyOfAnObserverCameraIsThatOfItsOrbitObject() // [00434a04]
    {
        var world = new ClientWorld();
        var src = new ClientHudSource(world);
        world.Ghosts.Insert(new ClientGhost { Index = 2, Class = "ObserverCamera" });
        var body = new ClientGhost { Index = 7, Class = "Player" };
        world.Ghosts.Insert(body);
        world.Psc.ControlGhost = 2;
        world.Psc.ControlClass = "ObserverCamera";
        world.Psc.ControlState = new Dictionary<string, object?> { ["camMode"] = 3L, ["orbitGhost"] = 7L };
        Assert.Equal(1, src.EnergyLevel);
        body.State["energy"] = 0.4;
        Assert.Equal(0.4, src.EnergyLevel);
        world.Psc.ControlState = new Dictionary<string, object?> { ["camMode"] = 4L };
        Assert.Equal(1, src.EnergyLevel);
    }
}
