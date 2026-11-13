namespace OpenTribes.Platform.Gl;

// Size selection for full screen, with no display needed. Stock switches the display to $pref::VideoFullScreenRes
// (setFSResolution: nearestResolution 0051e590 case 0xc, then changeResolution on the full screen device 00580364) and
// switchMode (005806d8) re-lays the canvas out at that size; leaving full screen restores the windowed device size (0057d4c4).
public static class FullscreenPlan
{
    // "WxH" of the pref, or null when it is not two positive numbers.
    public static (int W, int H)? ParseResolution(string? text)
    {
        if (text == null)
            return null;
        string[] wh = text.Trim().Split('x', 'X');
        if (wh.Length == 2 && int.TryParse(wh[0], out int w) && int.TryParse(wh[1], out int h) && w > 0 && h > 0)
            return (w, h);
        return null;
    }

    // The listed mode nearest to the wanted size: an exact match, else the smallest total distance in width and height
    // (ties go to the larger mode). Null when the list is empty.
    public static (int W, int H)? NearestMode(IReadOnlyList<(int W, int H)> modes, int w, int h)
    {
        (int W, int H)? best = null;
        long bestDist = long.MaxValue;
        foreach ((int W, int H) m in modes)
        {
            long d = Math.Abs((long)m.W - w) + Math.Abs((long)m.H - h);
            if (d < bestDist || d == bestDist && best is { } b && (long)m.W * m.H > (long)b.W * b.H)
            {
                best = m;
                bestDist = d;
            }
        }
        return best;
    }

    // The canvas extent: windowed it is the window's pixel size. Full screen it is the full screen resolution: the mode
    // that was set (exclusive mode, which can differ from the request when no exact mode exists) or, when no mode could
    // be set, the requested size rendered scaled into the window.
    // "Fullscreen windowed" (borderless desktop mode, our own choice) keeps the canvas at the window's pixel size, the desktop.
    public static (int W, int H) CanvasSize(bool fullscreen, (int W, int H)? exclusiveMode, (int W, int H) requested, (int W, int H) windowPixels, bool desktopMode = false)
        => !fullscreen || desktopMode ? windowPixels : exclusiveMode ?? requested;

    // $pref::OpenTribes::FullscreenWindowed is "1" or "true" (any case) for on. The command line option (--fullscreen-windowed)
    // turns it on whatever the pref says.
    public static bool UseDesktopMode(bool optionGiven, string? pref)
        => optionGiven || pref != null && (pref.Trim() == "1" || pref.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

    // The destination of a canvas of the given size in a window of the given pixel size: scaled to fit, aspect kept, centred.
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
