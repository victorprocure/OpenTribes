using OpenTribes.Platform.Gl;
using Xunit;

namespace OpenTribes.Tests;

public class FullscreenPlanTests
{
    private static readonly (int W, int H)[] Modes = { (640, 480), (800, 600), (1024, 768), (1920, 1080) };

    [Theory]
    [InlineData("640x480", 640, 480)]
    [InlineData(" 1024x768 ", 1024, 768)]
    public void ParsesThePref(string text, int w, int h) => Assert.Equal((w, h), FullscreenPlan.ParseResolution(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("640")]
    [InlineData("0x480")]
    [InlineData("axb")]
    public void RejectsBadPrefs(string? text) => Assert.Null(FullscreenPlan.ParseResolution(text));

    [Fact]
    public void ExactModeIsChosen() => Assert.Equal((800, 600), FullscreenPlan.NearestMode(Modes, 800, 600));

    [Fact]
    public void NearestModeWhenNoExactOne() => Assert.Equal((1024, 768), FullscreenPlan.NearestMode(Modes, 1000, 700));

    [Fact]
    public void NoModesGivesNull() => Assert.Null(FullscreenPlan.NearestMode(System.Array.Empty<(int, int)>(), 640, 480));

    [Fact]
    public void FullscreenCanvasIsTheModeNotTheDesktop()
        => Assert.Equal((640, 480), FullscreenPlan.CanvasSize(true, (640, 480), (640, 480), (1920, 1080)));

    [Fact]
    public void FullscreenCanvasIsTheRequestWhenNoModeWasSet()
        => Assert.Equal((640, 480), FullscreenPlan.CanvasSize(true, null, (640, 480), (1920, 1080)));

    [Fact]
    public void WindowedCanvasIsTheWindow()
        => Assert.Equal((800, 600), FullscreenPlan.CanvasSize(false, null, (640, 480), (800, 600)));

    [Fact]
    public void LetterboxKeepsAspectAndCentres()
    {
        Assert.Equal((240, 0, 1440, 1080), FullscreenPlan.Letterbox((640, 480), (1920, 1080)));
        Assert.Equal((0, 0, 640, 480), FullscreenPlan.Letterbox((640, 480), (640, 480)));
        Assert.Equal((0, 135, 1920, 810), FullscreenPlan.Letterbox((1920, 810), (1920, 1080)));
    }

    [Fact]
    public void DesktopModeKeepsTheDesktopCanvas()
        => Assert.Equal((1920, 1080), FullscreenPlan.CanvasSize(true, null, (640, 480), (1920, 1080), desktopMode: true));

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, "", false)]
    [InlineData(false, "0", false)]
    [InlineData(false, "1", true)]
    [InlineData(false, "True", true)]
    [InlineData(true, null, true)]
    [InlineData(true, "0", true)]
    public void OptionOverridesThePref(bool option, string? pref, bool expected)
        => Assert.Equal(expected, FullscreenPlan.UseDesktopMode(option, pref));

    [Fact]
    public void CommandLineOption()
    {
        Assert.True(OpenTribes.Client.App.ClientOptions.Parse(new[] { "--fullscreen-windowed" }, out _)!.FullscreenWindowed);
        Assert.False(OpenTribes.Client.App.ClientOptions.Parse(System.Array.Empty<string>(), out _)!.FullscreenWindowed);
    }
}
