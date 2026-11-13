namespace OpenTribes.Platform.Gl;

using OpenTribes.Platform.Gfx;

/// <summary>The OpenGL 3.3 core implementation of <see cref="IGfxDevice"/> (D30, D31).</summary>
/// <remarks>Everything draws into an offscreen framebuffer of the window size. <see cref="EndFrame"/> copies it to the window and swaps, and <see cref="ReadPixels"/> reads it back. 2D calls are batched per call into a streaming vertex buffer. One shader draws flat colors and textures (docs/renderer.md 4).</remarks>
public sealed unsafe partial class GlGfxDevice : IGfxDevice, IGfxStats
{
    /// <summary>Gets the draw calls issued so far.</summary>
    public long DrawCalls { get; private set; }
    /// <summary>Gets the triangles issued so far.</summary>
    public long Triangles { get; private set; }

    private const string VertexSource = @"#version 330 core
in vec2 aPos;
in vec2 aUv;
in vec4 aColor;
uniform vec2 uViewport;
out vec2 vUv;
out vec4 vColor;
void main() {
    gl_Position = vec4(aPos.x / uViewport.x * 2.0 - 1.0, 1.0 - aPos.y / uViewport.y * 2.0, 0.0, 1.0);
    vUv = aUv;
    vColor = aColor;
}";

    private const string FragmentSource = @"#version 330 core
in vec2 vUv;
in vec4 vColor;
uniform sampler2D uTex;
uniform int uUseTex;
out vec4 oColor;
void main() {
    vec4 c = vColor;
    if (uUseTex != 0) c *= texture(uTex, vUv);
    oColor = c;
}";

    private sealed class CachedTexture
    {
        public uint Name { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    private readonly GlWindow _win;
    private uint _fbo, _colorRb, _depthRb;
    private readonly uint _program, _vao, _vbo, _samplerNearest, _samplerLinear;
    private readonly int _uViewport, _uUseTex, _uTex;
    private PaletteBank _bank = PaletteTables.Grayscale();
    private float _gamma = 1f;
    private byte[] _gammaTable = PaletteTables.GammaTable(1f);
    private readonly Dictionary<PbmpBitmap, CachedTexture> _cache = [];
    private GfxRect? _clip;
    private float[] _verts = new float[8 * 6 * 64];
    private int _vertCount;
    private bool _disposed;

    /// <inheritdoc/>
    public int Width { get; private set; }
    /// <inheritdoc/>
    public int Height { get; private set; }
    /// <summary>Gets the GL function table.</summary>
    public GlApi Api { get; }
    /// <summary>Gets the GL renderer string.</summary>
    public string Renderer => Api.GetString(GlApi.Renderer);

    /// <summary>Creates a device that draws into a window.</summary>
    /// <param name="window">The window, with its GL context current.</param>
    public GlGfxDevice(GlWindow window)
    {
        _win = window;
        Api = window.Gl;
        (Width, Height) = window.PixelSize();
        if (Width <= 0 || Height <= 0)
            (Width, Height) = (1, 1);

        _program = Api.BuildProgram(VertexSource, FragmentSource, (0, "aPos"), (1, "aUv"), (2, "aColor"));
        _uViewport = Api.UniformLocation(_program, "uViewport");
        _uUseTex = Api.UniformLocation(_program, "uUseTex");
        _uTex = Api.UniformLocation(_program, "uTex");
        _vao = Api.GenVertexArray();
        _vbo = Api.GenBuffer();
        Api.BindVertexArray_(_vao);
        Api.BindBuffer_(GlApi.ArrayBuffer, _vbo);
        Api.VertexAttribPointer_(0, 2, GlApi.Float, 0, 32, (void*)0);
        Api.VertexAttribPointer_(1, 2, GlApi.Float, 0, 32, (void*)8);
        Api.VertexAttribPointer_(2, 4, GlApi.Float, 0, 32, (void*)16);
        Api.EnableVertexAttribArray_(0);
        Api.EnableVertexAttribArray_(1);
        Api.EnableVertexAttribArray_(2);
        _samplerNearest = MakeSampler(GlApi.Nearest);
        _samplerLinear = MakeSampler(GlApi.Linear);
        CreateTarget();
    }

    private uint MakeSampler(uint filter)
    {
        var s = Api.GenSampler();
        Api.SamplerParameteri_(s, GlApi.TextureMinFilter, (int)filter);
        Api.SamplerParameteri_(s, GlApi.TextureMagFilter, (int)filter);
        Api.SamplerParameteri_(s, GlApi.TextureWrapS, (int)GlApi.ClampToEdge);
        Api.SamplerParameteri_(s, GlApi.TextureWrapT, (int)GlApi.ClampToEdge);
        return s;
    }

    private void CreateTarget()
    {
        if (_fbo != 0)
        {
            uint f = _fbo, c = _colorRb, d = _depthRb;
            Api.DeleteFramebuffers_(1, &f);
            Api.DeleteRenderbuffers_(1, &c);
            Api.DeleteRenderbuffers_(1, &d);
        }
        _fbo = Api.GenFramebuffer();
        _colorRb = Api.GenRenderbuffer();
        _depthRb = Api.GenRenderbuffer();
        Api.BindRenderbuffer_(GlApi.Renderbuffer, _colorRb);
        Api.RenderbufferStorage_(GlApi.Renderbuffer, GlApi.Rgba8, Width, Height);
        Api.BindRenderbuffer_(GlApi.Renderbuffer, _depthRb);
        Api.RenderbufferStorage_(GlApi.Renderbuffer, GlApi.DepthComponent16, Width, Height);
        Api.BindFramebuffer_(GlApi.Framebuffer, _fbo);
        Api.FramebufferRenderbuffer_(GlApi.Framebuffer, GlApi.ColorAttachment0, GlApi.Renderbuffer, _colorRb);
        Api.FramebufferRenderbuffer_(GlApi.Framebuffer, GlApi.DepthAttachment, GlApi.Renderbuffer, _depthRb);
        if (Api.CheckFramebufferStatus_(GlApi.Framebuffer) != GlApi.FramebufferComplete)
            throw new InvalidOperationException("offscreen framebuffer incomplete");
        Prepare2D();
    }

    // 1.8 item 2: 2D state. The depth test stays off for all 2D draws (5.4 endScene3d).
    private void Prepare2D()
    {
        Api.BindFramebuffer_(GlApi.Framebuffer, _fbo);
        Api.Viewport_(0, 0, Width, Height);
        Api.Disable_(GlApi.DepthTest);
        Api.Disable_(GlApi.CullFace);
        Api.UseProgram_(_program);
        Api.Uniform2f_(_uViewport, Width, Height);
        Api.Uniform1i_(_uTex, 0);
        Api.BindVertexArray_(_vao);
        Api.BindBuffer_(GlApi.ArrayBuffer, _vbo);
    }

    /// <inheritdoc/>
    public void SetPalette(PaletteBank bank, float gamma = 1f)
    {
        _bank = bank;
        _gamma = Math.Clamp(gamma, PaletteTables.MinGamma, PaletteTables.MaxGamma);
        _gammaTable = PaletteTables.GammaTable(_gamma);
        FlushTextureCache();
    }

    /// <summary>The packing gate of 005d3ecc, read at each upload. It returns false when <c>$pref::OpenGL::NoPackedTextures</c> or <c>Use32BitTex</c> is set.</summary>
    public Func<bool>? PackedTextures { get; set; }

    /// <inheritdoc/>
    public void FlushTextureCache()
    {
        foreach (var t in _cache.Values)
            Api.DeleteTexture(t.Name);
        _cache.Clear();
    }

    /// <inheritdoc/>
    public void FlushTexture(PbmpBitmap bitmap)
    {
        if (_cache.Remove(bitmap, out var t))
            Api.DeleteTexture(t.Name);
    }

    /// <summary>Gets the number of cached textures.</summary>
    public int CachedTextureCount => _cache.Count;

    /// <inheritdoc/>
    public void BeginFrame()
    {
        (var w, var h) = _win.PixelSize();
        if (w > 0 && h > 0 && (w != Width || h != Height))
        {
            Width = w;
            Height = h;
            CreateTarget();
        }
        Prepare2D();
        Api.Disable_(GlApi.ScissorTest);
    }

    /// <inheritdoc/>
    public void EndFrame()
    {
        Api.BindFramebuffer_(GlApi.ReadFramebuffer, _fbo);
        Api.BindFramebuffer_(GlApi.DrawFramebuffer, 0);
        Api.Disable_(GlApi.ScissorTest);
        if (_win.Scaled)
        {
            // No display mode was set: the canvas keeps the full screen resolution and is scaled into the window, aspect kept.
            (var ww, var wh) = _win.WindowPixels();
            (var x, var y, var w, var h) = FullscreenPlan.Letterbox((Width, Height), (ww, wh));
            Api.ClearColor_(0f, 0f, 0f, 1f);
            Api.Clear_(GlApi.ColorBufferBit);
            Api.BlitFramebuffer_(0, 0, Width, Height, x, y, x + w, y + h, GlApi.ColorBufferBit, GlApi.Nearest);
        }
        else
            Api.BlitFramebuffer_(0, 0, Width, Height, 0, 0, Width, Height, GlApi.ColorBufferBit, GlApi.Nearest);
        _win.Swap();
        Prepare2D();
    }

    /// <inheritdoc/>
    public void Clear(int colorIndex)
    {
        var sub = _bank.SubPalettes[0];
        colorIndex = Math.Clamp(colorIndex, 0, 255);
        ClearRgb(sub.Colors[colorIndex * 4], sub.Colors[(colorIndex * 4) + 1], sub.Colors[(colorIndex * 4) + 2]);
    }

    /// <inheritdoc/>
    public void ClearRgb(byte r, byte g, byte b)
    {
        Api.Disable_(GlApi.ScissorTest);
        Api.ClearColor_(r / 255f, g / 255f, b / 255f, 1f);
        Api.Clear_(GlApi.ColorBufferBit);
    }

    /// <inheritdoc/>
    public void SetClip(GfxRect? clip) => _clip = clip;

    // ---- vertex batching ----

    private void Vertex(float x, float y, float u, float v, float r, float g, float b, float a)
    {
        if ((_vertCount + 1) * 8 > _verts.Length)
            Array.Resize(ref _verts, _verts.Length * 2);
        var o = _vertCount++ * 8;
        _verts[o] = x;
        _verts[o + 1] = y;
        _verts[o + 2] = u;
        _verts[o + 3] = v;
        _verts[o + 4] = r;
        _verts[o + 5] = g;
        _verts[o + 6] = b;
        _verts[o + 7] = a;
    }

    private void Quad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1,
                      float r, float g, float b, float a)
    {
        Vertex(x0, y0, u0, v0, r, g, b, a);
        Vertex(x1, y0, u1, v0, r, g, b, a);
        Vertex(x1, y1, u1, v1, r, g, b, a);
        Vertex(x0, y0, u0, v0, r, g, b, a);
        Vertex(x1, y1, u1, v1, r, g, b, a);
        Vertex(x0, y1, u0, v1, r, g, b, a);
    }

    private void ApplyClip()
    {
        if (_clip is not GfxRect c)
        {
            Api.Disable_(GlApi.ScissorTest);
            return;
        }
        var r = c.Intersect(new GfxRect(0, 0, Width - 1, Height - 1));
        Api.Enable_(GlApi.ScissorTest);
        if (r.IsEmpty)
            Api.Scissor_(0, 0, 0, 0);
        else
            Api.Scissor_(r.Left, Height - 1 - r.Bottom, r.Width, r.Height);
    }

    private void SetBlend(BlendMode mode)
    {
        switch (mode)
        {
            case BlendMode.Opaque:
                Api.Disable_(GlApi.Blend);
                return;
            case BlendMode.Additive: // 5.4 add: (SRC_ALPHA, ONE)
                Api.Enable_(GlApi.Blend);
                Api.BlendFunc_(GlApi.SrcAlpha, GlApi.ONE);
                return;
            case BlendMode.Subtractive: // 5.4 sub: (ZERO, ONE_MINUS_SRC_COLOR)
                Api.Enable_(GlApi.Blend);
                Api.BlendFunc_(GlApi.ZERO, GlApi.OneMinusSrcColor);
                return;
            default: // transparent, translucent: (SRC_ALPHA, ONE_MINUS_SRC_ALPHA)
                Api.Enable_(GlApi.Blend);
                Api.BlendFunc_(GlApi.SrcAlpha, GlApi.OneMinusSrcAlpha);
                return;
        }
    }

    private void Flush(uint mode, BlendMode blend, CachedTexture? tex, bool linear)
    {
        if (_vertCount == 0)
            return;
        ApplyClip();
        SetBlend(blend);
        Api.Uniform1i_(_uUseTex, tex != null ? 1 : 0);
        if (tex != null)
        {
            Api.ActiveTexture_(GlApi.Texture0);
            Api.BindTexture_(GlApi.Texture2D, tex.Name);
            Api.BindSampler_(0, linear ? _samplerLinear : _samplerNearest);
        }
        fixed (float* p = _verts)
            Api.BufferData_(GlApi.ArrayBuffer, _vertCount * 32, p, GlApi.StreamDraw);
        Api.DrawArrays_(mode, 0, _vertCount);
        ++DrawCalls;
        Triangles += _vertCount / 3;
        _vertCount = 0;
    }

    // ---- flat colors ----

    private (float r, float g, float b) FlatColor(int index)
    {
        index = Math.Clamp(index, 0, 255);
        var sp = _bank.SubPalettes[0];
        return (PaletteTables.GammaFloat(sp.IndexToRed![index], _gamma),
                PaletteTables.GammaFloat(sp.IndexToGreen![index], _gamma),
                PaletteTables.GammaFloat(sp.IndexToBlue![index], _gamma));
    }

    private void Pixels(int x0, int y0, int x1, int y1, int colorIndex, float alpha)
    {
        // Inclusive pixel run as one quad (docs/renderer.md 4: the quad spans right + 1, bottom + 1).
        var (r, g, b) = FlatColor(colorIndex);
        Quad(x0, y0, x1 + 1, y1 + 1, 0, 0, 0, 0, r, g, b, alpha);
    }

    /// <inheritdoc/>
    public void FillRect(GfxRect rect, int colorIndex, float alpha = 1f)
    {
        if (rect.IsEmpty)
            return;
        Pixels(rect.Left, rect.Top, rect.Right, rect.Bottom, colorIndex, alpha);
        Flush(GlApi.Triangles, alpha < 1f ? BlendMode.Transparent : BlendMode.Opaque, null, false);
    }

    /// <inheritdoc/>
    public void FillRectRgb(GfxRect rect, byte r, byte g, byte b, float alpha)
    {
        if (rect.IsEmpty || alpha <= 0f)
            return;
        Quad(rect.Left, rect.Top, rect.Right + 1, rect.Bottom + 1, 0, 0, 0, 0, r / 255f, g / 255f, b / 255f, Math.Min(alpha, 1f));
        Flush(GlApi.Triangles, alpha < 1f ? BlendMode.Transparent : BlendMode.Opaque, null, false);
    }

    /// <inheritdoc/>
    public void DrawRectOutline(GfxRect rect, int colorIndex)
    {
        if (rect.IsEmpty)
            return;
        Pixels(rect.Left, rect.Top, rect.Right, rect.Top, colorIndex, 1f);
        if (rect.Bottom > rect.Top)
            Pixels(rect.Left, rect.Bottom, rect.Right, rect.Bottom, colorIndex, 1f);
        if (rect.Bottom - rect.Top >= 2)
        {
            Pixels(rect.Left, rect.Top + 1, rect.Left, rect.Bottom - 1, colorIndex, 1f);
            if (rect.Right > rect.Left)
                Pixels(rect.Right, rect.Top + 1, rect.Right, rect.Bottom - 1, colorIndex, 1f);
        }
        Flush(GlApi.Triangles, BlendMode.Opaque, null, false);
    }

    /// <inheritdoc/>
    public void DrawLine(int x0, int y0, int x1, int y1, int colorIndex)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        if (dx > 1 << 16 || -dy > 1 << 16)
            return;
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        while (true)
        {
            Pixels(x0, y0, x0, y0, colorIndex, 1f);
            if (x0 == x1 && y0 == y1)
                break;
            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }
            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
        Flush(GlApi.Triangles, BlendMode.Opaque, null, false);
    }

    // ---- bitmaps ----

    private CachedTexture Texture(PbmpBitmap bmp)
    {
        if (_cache.TryGetValue(bmp, out var t))
            return t;
        var sub = _bank.FindSubPalette(bmp.PaletteId);
        var rgba = PaletteTables.ToRgba(bmp, sub, _gammaTable);
        // Stock blit 005d8290 uploads through 005d3ecc, which packs palette-indexed bitmaps by subpalette kind (005d7148) unless
        // $pref::OpenGL::NoPackedTextures or Use32BitTex is set; then it uploads the 8 bit tables unpacked (005d5164, 005d5e34, 005d5a3c).
        if (bmp.Depth == 8 && (PackedTextures?.Invoke() ?? true))
            PaletteTables.Pack(rgba, sub);
        t = new CachedTexture { Name = Api.GenTexture(), Width = Math.Max(1, bmp.Width), Height = Math.Max(1, bmp.Height) };
        Api.BindTexture_(GlApi.Texture2D, t.Name);
        Api.PixelStorei_(GlApi.UnpackAlignment, 1);
        Api.TexParameteri_(GlApi.Texture2D, GlApi.TextureMinFilter, (int)GlApi.Nearest);
        Api.TexParameteri_(GlApi.Texture2D, GlApi.TextureMagFilter, (int)GlApi.Nearest);
        if (bmp.Width > 0 && bmp.Height > 0)
            fixed (byte* p = rgba)
                Api.TexImage2D_(GlApi.Texture2D, 0, (int)GlApi.Rgba8, bmp.Width, bmp.Height, 0, GlApi.Rgba,
                               GlApi.UnsignedByte, p);
        _cache[bmp] = t;
        return t;
    }

    /// <inheritdoc/>
    public void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None) =>
        BlitRegion(bitmap, new GfxRect(0, 0, bitmap.Width - 1, bitmap.Height - 1), x, y, flip);

    /// <inheritdoc/>
    public void BlitRegion(PbmpBitmap bitmap, GfxRect source, int x, int y, BlitFlip flip = BlitFlip.None) =>
        Draw(bitmap, source, x, y, source.Width, source.Height, flip, false);

    /// <inheritdoc/>
    public void BlitStretch(PbmpBitmap bitmap, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) =>
        BlitRegionStretch(bitmap, new GfxRect(0, 0, bitmap.Width - 1, bitmap.Height - 1), x, y, width, height, flip);

    /// <inheritdoc/>
    public void BlitRegionStretch(PbmpBitmap bitmap, GfxRect source, int x, int y, int width, int height,
                                  BlitFlip flip = BlitFlip.None) =>
        Draw(bitmap, source, x, y, width, height, flip, width != source.Width || height != source.Height);

    private void Draw(PbmpBitmap bmp, GfxRect src, int x, int y, int w, int h, BlitFlip flip, bool linear)
    {
        src = src.Intersect(new GfxRect(0, 0, bmp.Width - 1, bmp.Height - 1));
        if (src.IsEmpty || w <= 0 || h <= 0)
            return;
        var tex = Texture(bmp);
        var mode = bmp.Blend;
        // 4: translucent is a 50% blend [I]; a translucent (kind 2) subpalette carries its own alpha in the texture.
        var alpha = 1f;
        if (mode == BlendMode.Translucent && _bank.FindSubPalette(bmp.PaletteId).Kind != 2)
            alpha = 0.5f;
        float u0 = (float)src.Left / tex.Width, u1 = (float)(src.Right + 1) / tex.Width;
        float v0 = (float)src.Top / tex.Height, v1 = (float)(src.Bottom + 1) / tex.Height;
        if ((flip & BlitFlip.X) != 0)
            (u0, u1) = (u1, u0);
        if ((flip & BlitFlip.Y) != 0)
            (v0, v1) = (v1, v0);
        Quad(x, y, x + w, y + h, u0, v0, u1, v1, 1, 1, 1, alpha);
        Flush(GlApi.Triangles, mode, tex, linear);
    }

    /// <inheritdoc/>
    public byte[] ReadPixels()
    {
        Api.BindFramebuffer_(GlApi.ReadFramebuffer, _fbo);
        Api.PixelStorei_(GlApi.PackAlignment, 1);
        var raw = new byte[Width * Height * 4];
        fixed (byte* p = raw)
            Api.ReadPixels_(0, 0, Width, Height, GlApi.Rgba, GlApi.UnsignedByte, p);
        var flipped = new byte[raw.Length];
        var row = Width * 4;
        for (var y = 0; y < Height; ++y)
            Buffer.BlockCopy(raw, (Height - 1 - y) * row, flipped, y * row, row);
        return flipped;
    }

    /// <summary>Gets the last GL error.</summary>
    /// <returns>The GL error code.</returns>
    public uint GetError() => Api.GetError_();

    /// <summary>Releases the GL objects of the device.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        FlushTextureCache();
        uint f = _fbo, c = _colorRb, d = _depthRb, vao = _vao, vbo = _vbo, s0 = _samplerNearest, s1 = _samplerLinear;
        Api.DeleteFramebuffers_(1, &f);
        Api.DeleteRenderbuffers_(1, &c);
        Api.DeleteRenderbuffers_(1, &d);
        Api.DeleteVertexArrays_(1, &vao);
        Api.DeleteBuffers_(1, &vbo);
        Api.DeleteSamplers_(1, &s0);
        Api.DeleteSamplers_(1, &s1);
        Api.DeleteProgram_(_program);
        Dispose3D();
    }
}
