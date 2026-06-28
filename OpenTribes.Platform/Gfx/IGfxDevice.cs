namespace OpenTribes.Platform.Gfx;

// The graphics device behind our own interface (D30, D31): the OpenGL device contract of
// docs/renderer.md 1.4, the 2D calls of 4 and the 3D submission of 5 (stubbed until C4).
// All coordinates are surface pixels with the origin at the top left; rectangles are inclusive.
public interface IGfxDevice : IDisposable
{
    int Width { get; }
    int Height { get; }

    // 3.2 / 3.3: installs the active palette bank and the surface gamma (1.9: 0.05..20). Flushes the texture cache.
    void SetPalette(PaletteBank bank, float gamma = 1f);

    // 3.4: flushTextureCache / flushTexture.
    void FlushTextureCache();
    void FlushTexture(PbmpBitmap bitmap);

    // 1.8: a frame starts with the surface bound and the 2D state set, and ends by presenting.
    void BeginFrame();
    void EndFrame();

    // 4: `clear` takes a palette index of the active palette's first subpalette, no gamma (3.3).
    void Clear(int colorIndex);
    void ClearRgb(byte r, byte g, byte b);

    // 1.7: the clip rectangle (inclusive); null turns clipping off. Every 2D call is clipped to it.
    void SetClip(GfxRect? clip);

    // 4 colors: palette index of the first subpalette, raised to the gamma exponent; alpha is the surface alpha.
    void DrawLine(int x0, int y0, int x1, int y1, int colorIndex);
    void DrawRectOutline(GfxRect rect, int colorIndex);
    void FillRect(GfxRect rect, int colorIndex, float alpha = 1f);
    // A blend of a plain colour over the surface (the damage flash); devices without a surface ignore it.
    void FillRectRgb(GfxRect rect, byte r, byte g, byte b, float alpha)
    {
    }

    // 4 blits: blend mode from the bitmap attributes (2.2.1), palette from its PiDX (3.2).
    void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None);
    void BlitRegion(PbmpBitmap bitmap, GfxRect source, int x, int y, BlitFlip flip = BlitFlip.None);
    void BlitStretch(PbmpBitmap bitmap, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None);
    void BlitRegionStretch(PbmpBitmap bitmap, GfxRect source, int x, int y, int width, int height,
                           BlitFlip flip = BlitFlip.None);

    // 4 text: see GfxText.
    int DrawText(PftFont font, int x, int y, string text) => GfxText.DrawAt(this, font, x, y, text);
    int DrawTextInRect(PftFont font, GfxRect rect, string text) => GfxText.DrawInRect(this, font, rect, text);

    // Reads the surface back, width * height * 4 bytes RGBA, top row first.
    byte[] ReadPixels();

    // 5: 3D primitive submission (docs/renderer.md 5, designed in 5.6; the vertex layout and the shading are ours,
    // docs/world-render.md 7). Vertices are world-space triangles; the device projects them with the camera.
    // BeginScene3D clears the depth buffer; EndScene3D returns to the 2D state of BeginFrame.
    IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options);
    // The same with the bitmap's own mip levels 1.. (RGBA8, each half the size of the last) and the stock minification filter,
    // the nearest level with linear filtering inside it (005d3fb9). Devices without mip data ignore the levels.
    IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options, IReadOnlyList<byte[]> mipLevels) =>
        CreateTexture3D(rgba, width, height, options);
    void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB);
    // Replaces the camera (for example the near and far planes) inside a scene.
    void SetCamera3D(in Camera3D camera);
    // Dynamic point lights added to the light map of every later draw that has one (interiors and terrain). Up to
    // MaxDynamicLights are used; an empty span turns them off. Devices without lighting ignore the call.
    const int MaxDynamicLights = 16;
    void SetDynamicLights(ReadOnlySpan<DynamicLight> lights) { }
    void Draw3D(ReadOnlySpan<Vertex3D> triangles, in DrawState3D state);
    void EndScene3D();
    // The next BeginScene3D draws into this rectangle of the surface only (inclusive, top-left origin; the depth clear and the
    // draws are cut to it) until it is set to null. The camera's width and height are the rectangle's then. Devices without a
    // surface rectangle ignore the call. A GUI control that shows a model (the player setup preview) uses it.
    void SetViewport3D(GfxRect? rect)
    {
    }
}
