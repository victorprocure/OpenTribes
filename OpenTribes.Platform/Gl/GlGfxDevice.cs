using OpenTribes.Platform.Gfx;

namespace OpenTribes.Platform.Gl;

// OpenGL 3.3 core implementation of IGfxDevice (D30, D31). Everything draws into an offscreen framebuffer of
// the window size; EndFrame copies it to the window and swaps, ReadPixels reads it back. 2D calls are batched per
// call into a streaming vertex buffer; one shader draws flat colors and textures (docs/renderer.md 4).
public sealed unsafe class GlGfxDevice : IGfxDevice
{
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
        public uint Name;
        public int Width, Height;
    }

    private readonly GlWindow mWin;
    private readonly GlApi gl;
    private uint mFbo, mColorRb, mDepthRb;
    private uint mProgram, mVao, mVbo, mSamplerNearest, mSamplerLinear;
    private int mUViewport, mUUseTex, mUTex;
    private int mWidth, mHeight;
    private PaletteBank mBank = PaletteTables.Grayscale();
    private float mGamma = 1f;
    private byte[] mGammaTable = PaletteTables.GammaTable(1f);
    private readonly Dictionary<PbmpBitmap, CachedTexture> mCache = new();
    private GfxRect? mClip;
    private float[] mVerts = new float[8 * 6 * 64];
    private int mVertCount;
    private bool mDisposed;

    public int Width => mWidth;
    public int Height => mHeight;
    public GlApi Api => gl;
    public string Renderer => gl.GetString(GlApi.Renderer);

    public GlGfxDevice(GlWindow window)
    {
        mWin = window;
        gl = window.Gl;
        (mWidth, mHeight) = window.PixelSize();
        if (mWidth <= 0 || mHeight <= 0)
            (mWidth, mHeight) = (1, 1);

        mProgram = gl.BuildProgram(VertexSource, FragmentSource, (0, "aPos"), (1, "aUv"), (2, "aColor"));
        mUViewport = gl.UniformLocation(mProgram, "uViewport");
        mUUseTex = gl.UniformLocation(mProgram, "uUseTex");
        mUTex = gl.UniformLocation(mProgram, "uTex");
        mVao = gl.GenVertexArray();
        mVbo = gl.GenBuffer();
        gl.BindVertexArray_(mVao);
        gl.BindBuffer_(GlApi.ArrayBuffer, mVbo);
        gl.VertexAttribPointer_(0, 2, GlApi.Float, 0, 32, (void*)0);
        gl.VertexAttribPointer_(1, 2, GlApi.Float, 0, 32, (void*)8);
        gl.VertexAttribPointer_(2, 4, GlApi.Float, 0, 32, (void*)16);
        gl.EnableVertexAttribArray_(0);
        gl.EnableVertexAttribArray_(1);
        gl.EnableVertexAttribArray_(2);
        mSamplerNearest = MakeSampler(GlApi.Nearest);
        mSamplerLinear = MakeSampler(GlApi.Linear);
        CreateTarget();
    }

    private uint MakeSampler(uint filter)
    {
        uint s = gl.GenSampler();
        gl.SamplerParameteri_(s, GlApi.TextureMinFilter, (int)filter);
        gl.SamplerParameteri_(s, GlApi.TextureMagFilter, (int)filter);
        gl.SamplerParameteri_(s, GlApi.TextureWrapS, (int)GlApi.ClampToEdge);
        gl.SamplerParameteri_(s, GlApi.TextureWrapT, (int)GlApi.ClampToEdge);
        return s;
    }

    private void CreateTarget()
    {
        if (mFbo != 0)
        {
            uint f = mFbo, c = mColorRb, d = mDepthRb;
            gl.DeleteFramebuffers_(1, &f);
            gl.DeleteRenderbuffers_(1, &c);
            gl.DeleteRenderbuffers_(1, &d);
        }
        mFbo = gl.GenFramebuffer();
        mColorRb = gl.GenRenderbuffer();
        mDepthRb = gl.GenRenderbuffer();
        gl.BindRenderbuffer_(GlApi.Renderbuffer, mColorRb);
        gl.RenderbufferStorage_(GlApi.Renderbuffer, GlApi.Rgba8, mWidth, mHeight);
        gl.BindRenderbuffer_(GlApi.Renderbuffer, mDepthRb);
        gl.RenderbufferStorage_(GlApi.Renderbuffer, GlApi.DepthComponent24, mWidth, mHeight);
        gl.BindFramebuffer_(GlApi.Framebuffer, mFbo);
        gl.FramebufferRenderbuffer_(GlApi.Framebuffer, GlApi.ColorAttachment0, GlApi.Renderbuffer, mColorRb);
        gl.FramebufferRenderbuffer_(GlApi.Framebuffer, GlApi.DepthAttachment, GlApi.Renderbuffer, mDepthRb);
        if (gl.CheckFramebufferStatus_(GlApi.Framebuffer) != GlApi.FramebufferComplete)
            throw new InvalidOperationException("offscreen framebuffer incomplete");
        Prepare2D();
    }

    // 1.8 item 2: 2D state. The depth test stays off for all 2D draws (5.4 endScene3d).
    private void Prepare2D()
    {
        gl.BindFramebuffer_(GlApi.Framebuffer, mFbo);
        gl.Viewport_(0, 0, mWidth, mHeight);
        gl.Disable_(GlApi.DepthTest);
        gl.Disable_(GlApi.CullFace);
        gl.UseProgram_(mProgram);
        gl.Uniform2f_(mUViewport, mWidth, mHeight);
        gl.Uniform1i_(mUTex, 0);
        gl.BindVertexArray_(mVao);
        gl.BindBuffer_(GlApi.ArrayBuffer, mVbo);
    }

    public void SetPalette(PaletteBank bank, float gamma = 1f)
    {
        mBank = bank;
        mGamma = Math.Clamp(gamma, PaletteTables.MinGamma, PaletteTables.MaxGamma);
        mGammaTable = PaletteTables.GammaTable(mGamma);
        FlushTextureCache();
    }

    public void FlushTextureCache()
    {
        foreach (CachedTexture t in mCache.Values)
            gl.DeleteTexture(t.Name);
        mCache.Clear();
    }

    public void FlushTexture(PbmpBitmap bitmap)
    {
        if (mCache.Remove(bitmap, out CachedTexture? t))
            gl.DeleteTexture(t.Name);
    }

    public int CachedTextureCount => mCache.Count;

    public void BeginFrame()
    {
        (int w, int h) = mWin.PixelSize();
        if (w > 0 && h > 0 && (w != mWidth || h != mHeight))
        {
            mWidth = w;
            mHeight = h;
            CreateTarget();
        }
        Prepare2D();
        gl.Disable_(GlApi.ScissorTest);
    }

    public void EndFrame()
    {
        gl.BindFramebuffer_(GlApi.ReadFramebuffer, mFbo);
        gl.BindFramebuffer_(GlApi.DrawFramebuffer, 0);
        gl.Disable_(GlApi.ScissorTest);
        gl.BlitFramebuffer_(0, 0, mWidth, mHeight, 0, 0, mWidth, mHeight, GlApi.ColorBufferBit, GlApi.Nearest);
        mWin.Swap();
        Prepare2D();
    }

    public void Clear(int colorIndex)
    {
        SubPalette sub = mBank.SubPalettes[0];
        colorIndex = Math.Clamp(colorIndex, 0, 255);
        ClearRgb(sub.Colors[colorIndex * 4], sub.Colors[colorIndex * 4 + 1], sub.Colors[colorIndex * 4 + 2]);
    }

    public void ClearRgb(byte r, byte g, byte b)
    {
        gl.Disable_(GlApi.ScissorTest);
        gl.ClearColor_(r / 255f, g / 255f, b / 255f, 1f);
        gl.Clear_(GlApi.ColorBufferBit);
    }

    public void SetClip(GfxRect? clip) => mClip = clip;

    // ---- vertex batching ----

    private void Vertex(float x, float y, float u, float v, float r, float g, float b, float a)
    {
        if ((mVertCount + 1) * 8 > mVerts.Length)
            Array.Resize(ref mVerts, mVerts.Length * 2);
        int o = mVertCount++ * 8;
        mVerts[o] = x;
        mVerts[o + 1] = y;
        mVerts[o + 2] = u;
        mVerts[o + 3] = v;
        mVerts[o + 4] = r;
        mVerts[o + 5] = g;
        mVerts[o + 6] = b;
        mVerts[o + 7] = a;
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
        if (mClip is not GfxRect c)
        {
            gl.Disable_(GlApi.ScissorTest);
            return;
        }
        GfxRect r = c.Intersect(new GfxRect(0, 0, mWidth - 1, mHeight - 1));
        gl.Enable_(GlApi.ScissorTest);
        if (r.IsEmpty)
            gl.Scissor_(0, 0, 0, 0);
        else
            gl.Scissor_(r.Left, mHeight - 1 - r.Bottom, r.Width, r.Height);
    }

    private void SetBlend(BlendMode mode)
    {
        switch (mode)
        {
            case BlendMode.Opaque:
                gl.Disable_(GlApi.Blend);
                return;
            case BlendMode.Additive: // 5.4 add: (SRC_ALPHA, ONE)
                gl.Enable_(GlApi.Blend);
                gl.BlendFunc_(GlApi.SrcAlpha, GlApi.ONE);
                return;
            case BlendMode.Subtractive: // 5.4 sub: (ZERO, ONE_MINUS_SRC_COLOR)
                gl.Enable_(GlApi.Blend);
                gl.BlendFunc_(GlApi.ZERO, GlApi.OneMinusSrcColor);
                return;
            default: // transparent, translucent: (SRC_ALPHA, ONE_MINUS_SRC_ALPHA)
                gl.Enable_(GlApi.Blend);
                gl.BlendFunc_(GlApi.SrcAlpha, GlApi.OneMinusSrcAlpha);
                return;
        }
    }

    private void Flush(uint mode, BlendMode blend, CachedTexture? tex, bool linear)
    {
        if (mVertCount == 0)
            return;
        ApplyClip();
        SetBlend(blend);
        gl.Uniform1i_(mUUseTex, tex != null ? 1 : 0);
        if (tex != null)
        {
            gl.ActiveTexture_(GlApi.Texture0);
            gl.BindTexture_(GlApi.Texture2D, tex.Name);
            gl.BindSampler_(0, linear ? mSamplerLinear : mSamplerNearest);
        }
        fixed (float* p = mVerts)
            gl.BufferData_(GlApi.ArrayBuffer, (nint)(mVertCount * 32), p, GlApi.StreamDraw);
        gl.DrawArrays_(mode, 0, mVertCount);
        mVertCount = 0;
    }

    // ---- flat colors ----

    private (float r, float g, float b) FlatColor(int index)
    {
        index = Math.Clamp(index, 0, 255);
        SubPalette sp = mBank.SubPalettes[0];
        return (PaletteTables.GammaFloat(sp.IndexToRed![index], mGamma),
                PaletteTables.GammaFloat(sp.IndexToGreen![index], mGamma),
                PaletteTables.GammaFloat(sp.IndexToBlue![index], mGamma));
    }

    private void Pixels(int x0, int y0, int x1, int y1, int colorIndex, float alpha)
    {
        // Inclusive pixel run as one quad (docs/renderer.md 4: the quad spans right + 1, bottom + 1).
        var (r, g, b) = FlatColor(colorIndex);
        Quad(x0, y0, x1 + 1, y1 + 1, 0, 0, 0, 0, r, g, b, alpha);
    }

    public void FillRect(GfxRect rect, int colorIndex, float alpha = 1f)
    {
        if (rect.IsEmpty)
            return;
        Pixels(rect.Left, rect.Top, rect.Right, rect.Bottom, colorIndex, alpha);
        Flush(GlApi.Triangles, alpha < 1f ? BlendMode.Transparent : BlendMode.Opaque, null, false);
    }

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

    // Bresenham, both end points included; one pixel quad per step.
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
            int e2 = 2 * err;
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
        if (mCache.TryGetValue(bmp, out CachedTexture? t))
            return t;
        SubPalette sub = mBank.FindSubPalette(bmp.PaletteId);
        byte[] rgba = PaletteTables.ToRgba(bmp, sub, mGammaTable);
        t = new CachedTexture { Name = gl.GenTexture(), Width = Math.Max(1, bmp.Width), Height = Math.Max(1, bmp.Height) };
        gl.BindTexture_(GlApi.Texture2D, t.Name);
        gl.PixelStorei_(GlApi.UnpackAlignment, 1);
        gl.TexParameteri_(GlApi.Texture2D, GlApi.TextureMinFilter, (int)GlApi.Nearest);
        gl.TexParameteri_(GlApi.Texture2D, GlApi.TextureMagFilter, (int)GlApi.Nearest);
        if (bmp.Width > 0 && bmp.Height > 0)
            fixed (byte* p = rgba)
                gl.TexImage2D_(GlApi.Texture2D, 0, (int)GlApi.Rgba8, bmp.Width, bmp.Height, 0, GlApi.Rgba,
                               GlApi.UnsignedByte, p);
        mCache[bmp] = t;
        return t;
    }

    public void Blit(PbmpBitmap bitmap, int x, int y, BlitFlip flip = BlitFlip.None) =>
        BlitRegion(bitmap, new GfxRect(0, 0, bitmap.Width - 1, bitmap.Height - 1), x, y, flip);

    public void BlitRegion(PbmpBitmap bitmap, GfxRect source, int x, int y, BlitFlip flip = BlitFlip.None) =>
        Draw(bitmap, source, x, y, source.Width, source.Height, flip, false);

    public void BlitStretch(PbmpBitmap bitmap, int x, int y, int width, int height, BlitFlip flip = BlitFlip.None) =>
        BlitRegionStretch(bitmap, new GfxRect(0, 0, bitmap.Width - 1, bitmap.Height - 1), x, y, width, height, flip);

    public void BlitRegionStretch(PbmpBitmap bitmap, GfxRect source, int x, int y, int width, int height,
                                  BlitFlip flip = BlitFlip.None) =>
        Draw(bitmap, source, x, y, width, height, flip, width != source.Width || height != source.Height);

    private void Draw(PbmpBitmap bmp, GfxRect src, int x, int y, int w, int h, BlitFlip flip, bool linear)
    {
        src = src.Intersect(new GfxRect(0, 0, bmp.Width - 1, bmp.Height - 1));
        if (src.IsEmpty || w <= 0 || h <= 0)
            return;
        CachedTexture tex = Texture(bmp);
        BlendMode mode = bmp.Blend;
        // 4: translucent is a 50% blend [I]; a translucent (kind 2) subpalette carries its own alpha in the texture.
        float alpha = 1f;
        if (mode == BlendMode.Translucent && mBank.FindSubPalette(bmp.PaletteId).Kind != 2)
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

    public byte[] ReadPixels()
    {
        gl.BindFramebuffer_(GlApi.ReadFramebuffer, mFbo);
        gl.PixelStorei_(GlApi.PackAlignment, 1);
        var raw = new byte[mWidth * mHeight * 4];
        fixed (byte* p = raw)
            gl.ReadPixels_(0, 0, mWidth, mHeight, GlApi.Rgba, GlApi.UnsignedByte, p);
        var flipped = new byte[raw.Length];
        int row = mWidth * 4;
        for (int y = 0; y < mHeight; ++y)
            Buffer.BlockCopy(raw, (mHeight - 1 - y) * row, flipped, y * row, row);
        return flipped;
    }

    public uint GetError() => gl.GetError_();

    // ---- 3D: C4 ----

    public void BeginScene3D() => throw new NotSupportedException("3D submission arrives in C4");
    public void EndScene3D() => throw new NotSupportedException("3D submission arrives in C4");
    public void SetDepthRange(float near, float far) => throw new NotSupportedException("3D submission arrives in C4");

    public void Dispose()
    {
        if (mDisposed)
            return;
        mDisposed = true;
        FlushTextureCache();
        uint f = mFbo, c = mColorRb, d = mDepthRb, vao = mVao, vbo = mVbo, s0 = mSamplerNearest, s1 = mSamplerLinear;
        gl.DeleteFramebuffers_(1, &f);
        gl.DeleteRenderbuffers_(1, &c);
        gl.DeleteRenderbuffers_(1, &d);
        gl.DeleteVertexArrays_(1, &vao);
        gl.DeleteBuffers_(1, &vbo);
        gl.DeleteSamplers_(1, &s0);
        gl.DeleteSamplers_(1, &s1);
        gl.DeleteProgram_(mProgram);
    }
}
