namespace OpenTribes.Platform.Gfx;

/// <summary>The graphics device behind our own interface (D30, D31): the OpenGL device contract of docs/renderer.md 1.4, the 2D calls of 4 and the 3D submission of 5.</summary>
/// <remarks>All coordinates are surface pixels with the origin at the top left, and rectangles are inclusive.</remarks>
public interface IGfxDevice : IDisposable
{
    /// <summary>Gets the surface width in pixels.</summary>
    int Width { get; }
    /// <summary>Gets the surface height in pixels.</summary>
    int Height { get; }

    /// <summary>Installs the active palette bank and the surface gamma (docs/renderer.md 3.2, 3.3). Flushes the texture cache.</summary>
    /// <param name="bank">The palette bank.</param>
    /// <param name="gamma">The gamma, 0.05 to 20 (1.9).</param>
    void SetPalette(PaletteBank bank, float gamma = 1f);

    /// <summary>Flushes every cached texture (docs/renderer.md 3.4, flushTextureCache).</summary>
    void FlushTextureCache();
    /// <summary>Flushes the cached texture of a bitmap (docs/renderer.md 3.4, flushTexture).</summary>
    /// <param name="bitmap">The bitmap.</param>
    void FlushTexture(PbmpBitmap bitmap);

    /// <summary>Starts a frame with the surface bound and the 2D state set (docs/renderer.md 1.8).</summary>
    void BeginFrame();
    /// <summary>Ends the frame by presenting it (docs/renderer.md 1.8).</summary>
    void EndFrame();

    /// <summary>Clears the surface to a palette colour (docs/renderer.md 4), with no gamma (3.3).</summary>
    /// <param name="colorIndex">The palette index in the active palette's first subpalette.</param>
    void Clear(int colorIndex);
    /// <summary>Clears the surface to an RGB colour.</summary>
    /// <param name="r">The red.</param>
    /// <param name="g">The green.</param>
    /// <param name="b">The blue.</param>
    void ClearRgb(byte r, byte g, byte b);

    /// <summary>Sets the clip rectangle (docs/renderer.md 1.7). Every 2D call is clipped to it.</summary>
    /// <param name="clip">The inclusive rectangle, or null to turn clipping off.</param>
    void SetClip(GfxRect? clip);

    /// <summary>Draws a line (docs/renderer.md 4 colors). Colours are palette indexes of the first subpalette, raised to the gamma exponent.</summary>
    /// <param name="x0">The start x.</param>
    /// <param name="y0">The start y.</param>
    /// <param name="x1">The end x.</param>
    /// <param name="y1">The end y.</param>
    /// <param name="colorIndex">The palette index.</param>
    void DrawLine(int x0, int y0, int x1, int y1, int colorIndex);
    /// <summary>Draws a rectangle outline.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="colorIndex">The palette index.</param>
    void DrawRectOutline(GfxRect rect, int colorIndex);
    /// <summary>Fills a rectangle.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="colorIndex">The palette index.</param>
    /// <param name="alpha">The surface alpha.</param>
    void FillRect(GfxRect rect, int colorIndex, float alpha = 1f);
    /// <summary>Blends a plain colour over a rectangle of the surface, for example the damage flash. Devices without a surface ignore it.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="r">The red.</param>
    /// <param name="g">The green.</param>
    /// <param name="b">The blue.</param>
    /// <param name="alpha">The blend alpha.</param>
    void FillRectRgb(GfxRect rect, byte r, byte g, byte b, float alpha)
    {
    }

    /// <summary>Draws a bitmap (docs/renderer.md 4 blits). The blend mode comes from the bitmap attributes (2.2.1) and the palette from its PiDX (3.2).</summary>
    /// <param name="bitmap">The bitmap.</param>
    /// <param name="x">The left x.</param>
    /// <param name="y">The top y.</param>
    /// <param name="flip">The flip.</param>
    void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None);
    /// <summary>Draws a region of a bitmap.</summary>
    /// <param name="bitmap">The bitmap.</param>
    /// <param name="source">The source region.</param>
    /// <param name="x">The left x.</param>
    /// <param name="y">The top y.</param>
    /// <param name="flip">The flip.</param>
    void BlitRegion(PbmpBitmap bitmap, GfxRect source, int x, int y, BlitFlip flip = BlitFlip.None);
    /// <summary>Draws a bitmap stretched to a size.</summary>
    /// <param name="bitmap">The bitmap.</param>
    /// <param name="x">The left x.</param>
    /// <param name="y">The top y.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="flip">The flip.</param>
    void BlitStretch(PbmpBitmap bitmap, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None);
    /// <summary>Draws a region of a bitmap stretched to a size.</summary>
    /// <param name="bitmap">The bitmap.</param>
    /// <param name="source">The source region.</param>
    /// <param name="x">The left x.</param>
    /// <param name="y">The top y.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="flip">The flip.</param>
    void BlitRegionStretch(PbmpBitmap bitmap, GfxRect source, int x, int y, int width, int height,
                           BlitFlip flip = BlitFlip.None);

    /// <summary>Draws text at a point, see <see cref="GfxText"/>.</summary>
    /// <param name="font">The font.</param>
    /// <param name="x">The pen start x.</param>
    /// <param name="y">The point y.</param>
    /// <param name="text">The text.</param>
    /// <returns>The bottom y of the lowest glyph drawn.</returns>
    int DrawText(PftFont font, int x, int y, string text) => GfxText.DrawAt(this, font, x, y, text);
    /// <summary>Draws justified text in a rectangle, see <see cref="GfxText"/>.</summary>
    /// <param name="font">The font.</param>
    /// <param name="rect">The rectangle.</param>
    /// <param name="text">The text.</param>
    /// <returns>The bottom y of the lowest glyph drawn.</returns>
    int DrawTextInRect(PftFont font, GfxRect rect, string text) => GfxText.DrawInRect(this, font, rect, text);

    /// <summary>Reads the surface back.</summary>
    /// <returns>width * height * 4 bytes of RGBA, top row first.</returns>
    byte[] ReadPixels();

    /// <summary>Creates a 3D texture (docs/renderer.md 5, designed in 5.6).</summary>
    /// <param name="rgba">The RGBA8 pixels.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="options">The options.</param>
    /// <returns>The texture.</returns>
    /// <remarks>Vertices are world-space triangles and the device projects them with the camera. The vertex layout and the shading are ours (docs/world-render.md 7). <see cref="BeginScene3D"/> clears the depth buffer and <see cref="EndScene3D"/> returns to the 2D state of <see cref="BeginFrame"/>.</remarks>
    IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options);
    /// <summary>Creates a 3D texture with the bitmap's own mip levels and the stock minification filter, the nearest level with linear filtering inside it (005d3fb9).</summary>
    /// <param name="rgba">The RGBA8 pixels of level 0.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="options">The options.</param>
    /// <param name="mipLevels">The levels 1 and up, RGBA8, each half the size of the last. Devices without mip data ignore them.</param>
    /// <returns>The texture.</returns>
    IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options, IReadOnlyList<byte[]> mipLevels) =>
        CreateTexture3D(rgba, width, height, options);
    /// <summary>Starts a 3D scene and clears the depth buffer.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="hazeR">The haze red.</param>
    /// <param name="hazeG">The haze green.</param>
    /// <param name="hazeB">The haze blue.</param>
    void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB);
    /// <summary>Replaces the camera, for example the near and far planes, inside a scene.</summary>
    /// <param name="camera">The camera.</param>
    void SetCamera3D(in Camera3D camera);
    /// <summary>The most dynamic lights a device uses.</summary>
    const int MaxDynamicLights = 16;
    /// <summary>Sets the dynamic point lights added to the light map of every later draw that has one, interiors and terrain. Devices without lighting ignore the call.</summary>
    /// <param name="lights">The lights. Up to <see cref="MaxDynamicLights"/> are used. An empty span turns them off.</param>
    void SetDynamicLights(ReadOnlySpan<DynamicLight> lights) { }
    /// <summary>Draws world-space triangles.</summary>
    /// <param name="triangles">The vertices, three per triangle.</param>
    /// <param name="state">The draw state.</param>
    void Draw3D(ReadOnlySpan<Vertex3D> triangles, in DrawState3D state);
    /// <summary>Ends the 3D scene and returns to the 2D state.</summary>
    void EndScene3D();
    /// <summary>Restricts the next <see cref="BeginScene3D"/> to a rectangle of the surface.</summary>
    /// <param name="rect">The inclusive rectangle with a top-left origin, or null to remove the restriction. The depth clear and the draws are cut to it and the camera's width and height are the rectangle's.</param>
    /// <remarks>Devices without a surface rectangle ignore the call. A GUI control that shows a model, such as the player setup preview, uses it.</remarks>
    void SetViewport3D(GfxRect? rect)
    {
    }
}
