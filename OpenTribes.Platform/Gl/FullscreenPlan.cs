namespace OpenTribes.Platform.Gl;

/// <summary>The size selection for full screen, which needs no display.</summary>
/// <remarks>Stock switches the display to <c>$pref::VideoFullScreenRes</c> (setFSResolution: nearestResolution 0051e590 case 0xc, then changeResolution on the full screen device 00580364). switchMode (005806d8) re-lays the canvas out at that size. Leaving full screen restores the windowed device size (0057d4c4).</remarks>
public static class FullscreenPlan
{
    /// <summary>Parses the resolution pref.</summary>
    /// <param name="text">The pref text, "WxH".</param>
    /// <returns>The size, or null when it is not two positive numbers.</returns>
    public static (int W, int H)? ParseResolution(string? text)
    {
        if (text == null)
            return null;
        var wh = text.Trim().Split('x', 'X');
        if (wh.Length == 2 && int.TryParse(wh[0], out var w) && int.TryParse(wh[1], out var h) && w > 0 && h > 0)
            return (w, h);
        return null;
    }

    /// <summary>Finds the listed mode nearest to the wanted size.</summary>
    /// <param name="modes">The available modes.</param>
    /// <param name="w">The wanted width.</param>
    /// <param name="h">The wanted height.</param>
    /// <returns>An exact match, else the mode with the smallest total distance in width and height, where ties go to the larger mode. Null when the list is empty.</returns>
    public static (int W, int H)? NearestMode(IReadOnlyList<(int W, int H)> modes, int w, int h)
    {
        (int W, int H)? best = null;
        var bestDist = long.MaxValue;
        foreach ((int W, int H) m in modes)
        {
            var d = Math.Abs((long)m.W - w) + Math.Abs((long)m.H - h);
            if (d < bestDist || d == bestDist && best is { } b && (long)m.W * m.H > (long)b.W * b.H)
            {
                best = m;
                bestDist = d;
            }
        }
        return best;
    }

    /// <summary>Gets the canvas extent.</summary>
    /// <param name="fullscreen">Whether the window is full screen.</param>
    /// <param name="exclusiveMode">The mode that was set, or null.</param>
    /// <param name="requested">The requested size.</param>
    /// <param name="windowPixels">The window's pixel size.</param>
    /// <param name="desktopMode">Whether borderless desktop mode is used.</param>
    /// <returns>Windowed it is the window's pixel size. Full screen it is the full screen resolution: the mode that was set (exclusive mode, which can differ from the request when no exact mode exists) or, when no mode could be set, the requested size rendered scaled into the window. Fullscreen windowed (borderless desktop mode, our own choice) keeps the canvas at the window's pixel size, the desktop.</returns>
    public static (int W, int H) CanvasSize(bool fullscreen, (int W, int H)? exclusiveMode, (int W, int H) requested, (int W, int H) windowPixels, bool desktopMode = false)
        => !fullscreen || desktopMode ? windowPixels : exclusiveMode ?? requested;

    /// <summary>Decides whether borderless desktop mode is used.</summary>
    /// <param name="optionGiven">Whether the command line option <c>--fullscreen-windowed</c> was given.</param>
    /// <param name="pref">The <c>$pref::OpenTribes::FullscreenWindowed</c> value.</param>
    /// <returns>True when the option was given, or the pref is "1" or "true" in any case.</returns>
    public static bool UseDesktopMode(bool optionGiven, string? pref)
        => optionGiven || pref != null && (pref.Trim() == "1" || pref.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Computes the destination of a canvas in a window: scaled to fit, aspect kept, centred.</summary>
    /// <param name="canvas">The canvas size.</param>
    /// <param name="window">The window's pixel size.</param>
    /// <returns>The destination rectangle as x, y, width and height.</returns>
    public static (int X, int Y, int W, int H) Letterbox((int W, int H) canvas, (int W, int H) window)
    {
        if (canvas.W <= 0 || canvas.H <= 0 || window.W <= 0 || window.H <= 0)
            return (0, 0, Math.Max(window.W, 0), Math.Max(window.H, 0));
        long wn = (long)window.W * canvas.H, hn = (long)window.H * canvas.W;
        int w, h;
        if (wn <= hn)
        {
            w = window.W;
            h = (int)Math.Max(1, (long)canvas.H * window.W / canvas.W);
        }
        else
        {
            h = window.H;
            w = (int)Math.Max(1, (long)canvas.W * window.H / canvas.H);
        }
        return ((window.W - w) / 2, (window.H - h) / 2, w, h);
    }
}
