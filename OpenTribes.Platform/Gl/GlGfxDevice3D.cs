using System.Runtime.InteropServices;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Platform.Gl;

// The 3D path of the OpenGL 3.3 device (docs/renderer.md 5, world-render.md 7). Our own design: world-space triangles
// are projected in the vertex shader (5.2 allows it, 1.3 "Projection contract"), the polygon is texture * shade * light
// map as fill mode 2 (5.5), and the haze overlay of 5.5 (fog colour drawn over the polygon with alpha = haze) is folded
// into the fragment as mix(colour, hazeColour, haze), which gives the same colour for an opaque polygon (Q7).
public sealed unsafe partial class GlGfxDevice
{
    private const string Vertex3DSource = @"#version 330 core
in vec3 aPos;
in vec2 aUv;
in vec2 aLm;
in vec4 aColor;
in float aHaze;
uniform mat4 uViewProj;
out vec2 vUv;
out vec2 vLm;
out vec4 vColor;
out float vHaze;
out vec3 vWorld;
void main() {
    vWorld = aPos;
    gl_Position = uViewProj * vec4(aPos, 1.0);
    vUv = aUv;
    vLm = aLm;
    vColor = aColor;
    vHaze = aHaze;
}";

    private const string Fragment3DSource = @"#version 330 core
in vec2 vUv;
in vec2 vLm;
in vec4 vColor;
in float vHaze;
in vec3 vWorld;
uniform int uDynCount;
uniform vec4 uDynPos[16];
uniform vec3 uDynCol[16];
uniform sampler2D uTex;
uniform sampler2D uLight;
uniform int uUseTex;
uniform int uUseLight;
uniform float uAlphaTest;
uniform vec3 uHaze;
out vec4 oColor;
void main() {
    vec4 c = vColor;
    if (uUseTex != 0) c *= texture(uTex, vUv);
    if (uAlphaTest > 0.0 && c.a < uAlphaTest) discard;
    if (uUseLight != 0) {
        vec3 lm = texture(uLight, vLm).rgb;
        for (int i = 0; i < uDynCount; ++i) {
            float d = distance(vWorld, uDynPos[i].xyz);
            lm += uDynCol[i] * max(0.0, 1.0 - d / uDynPos[i].w);
        }
        c.rgb *= min(lm, vec3(1.0));
    }
    c.rgb = mix(c.rgb, uHaze, clamp(vHaze, 0.0, 1.0));
    oColor = c;
}";

    private sealed class Texture3D : IGfxTexture
    {
        public uint Name;
        public int Width { get; init; }
        public int Height { get; init; }
        public GlApi? Gl;

        public void Dispose()
        {
            if (Gl != null && Name != 0)
                Gl.DeleteTexture(Name);
            Name = 0;
        }
    }

    private uint mProgram3D, mVao3D, mVbo3D;
    private int mU3DynCount, mU3DynPos, mU3DynCol;
    private int mDynCount;
    private readonly float[] mDynPos = new float[16 * 4], mDynCol = new float[16 * 3];
    private int mU3ViewProj, mU3Tex, mU3Light, mU3UseTex, mU3UseLight, mU3AlphaTest, mU3Haze;
    private float mHazeR, mHazeG, mHazeB;
    private bool mIn3D;
    private int mDraws3D;

    // Draw calls issued since the last BeginScene3D (a statistic for tests and the console).
    public int Draws3D => mDraws3D;

    private void Init3D()
    {
        mProgram3D = gl.BuildProgram(Vertex3DSource, Fragment3DSource, (0, "aPos"), (1, "aUv"), (2, "aLm"), (3, "aColor"),
                                     (4, "aHaze"));
        mU3ViewProj = gl.UniformLocation(mProgram3D, "uViewProj");
        mU3Tex = gl.UniformLocation(mProgram3D, "uTex");
        mU3Light = gl.UniformLocation(mProgram3D, "uLight");
        mU3UseTex = gl.UniformLocation(mProgram3D, "uUseTex");
        mU3UseLight = gl.UniformLocation(mProgram3D, "uUseLight");
        mU3AlphaTest = gl.UniformLocation(mProgram3D, "uAlphaTest");
        mU3Haze = gl.UniformLocation(mProgram3D, "uHaze");
        mU3DynCount = gl.UniformLocation(mProgram3D, "uDynCount");
        mU3DynPos = gl.UniformLocation(mProgram3D, "uDynPos[0]");
        mU3DynCol = gl.UniformLocation(mProgram3D, "uDynCol[0]");
        mVao3D = gl.GenVertexArray();
        mVbo3D = gl.GenBuffer();
        gl.BindVertexArray_(mVao3D);
        gl.BindBuffer_(GlApi.ArrayBuffer, mVbo3D);
        int stride = Marshal.SizeOf<Vertex3D>();
        gl.VertexAttribPointer_(0, 3, GlApi.Float, 0, stride, (void*)0);
        gl.VertexAttribPointer_(1, 2, GlApi.Float, 0, stride, (void*)12);
        gl.VertexAttribPointer_(2, 2, GlApi.Float, 0, stride, (void*)20);
        gl.VertexAttribPointer_(3, 4, GlApi.Float, 0, stride, (void*)28);
        gl.VertexAttribPointer_(4, 1, GlApi.Float, 0, stride, (void*)44);
        for (uint i = 0; i < 5; ++i)
            gl.EnableVertexAttribArray_(i);
        gl.BindVertexArray_(mVao);
    }

    private void Dispose3D()
    {
        if (mProgram3D == 0)
            return;
        uint vao = mVao3D, vbo = mVbo3D;
        gl.DeleteVertexArrays_(1, &vao);
        gl.DeleteBuffers_(1, &vbo);
        gl.DeleteProgram_(mProgram3D);
        mProgram3D = 0;
    }

    public IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("texture data does not match its size");
        uint name = gl.GenTexture();
        gl.ActiveTexture_(GlApi.Texture0);
        gl.BindTexture_(GlApi.Texture2D, name);
        gl.PixelStorei_(GlApi.UnpackAlignment, 1);
        fixed (byte* p = rgba)
            gl.TexImage2D_(GlApi.Texture2D, 0, (int)GlApi.Rgba8, width, height, 0, GlApi.Rgba, GlApi.UnsignedByte, p);
        uint filter = options.Linear ? GlApi.Linear : GlApi.Nearest;
        // Magnified texels are bilinear like the stock client's (compared at 640 x 480 and 1280 x 960 with tools/shotdiff.py).
        gl.TexParameteri_(GlApi.Texture2D, GlApi.TextureMagFilter, (int)filter);
        gl.TexParameteri_(GlApi.Texture2D, GlApi.TextureMinFilter,
                          options.Mipmaps ? (int)(options.Linear ? GlApi.LinearMipmapLinear : 0x2700) : (int)filter);
        uint wrap = options.Repeat ? GlApi.Repeat : GlApi.ClampToEdge;
        gl.TexParameteri_(GlApi.Texture2D, GlApi.TextureWrapS, (int)wrap);
        gl.TexParameteri_(GlApi.Texture2D, GlApi.TextureWrapT, (int)wrap);
        if (options.Mipmaps)
            gl.GenerateMipmap_(GlApi.Texture2D);
        return new Texture3D { Name = name, Width = width, Height = height, Gl = gl };
    }

    public IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options, IReadOnlyList<byte[]> mipLevels)
    {
        if (mipLevels.Count == 0 || !options.Mipmaps)
            return CreateTexture3D(rgba, width, height, options);
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("texture data does not match its size");
        uint name = gl.GenTexture();
        gl.ActiveTexture_(GlApi.Texture0);
        gl.BindTexture_(GlApi.Texture2D, name);
        gl.PixelStorei_(GlApi.UnpackAlignment, 1);
        fixed (byte* p = rgba)
            gl.TexImage2D_(GlApi.Texture2D, 0, (int)GlApi.Rgba8, width, height, 0, GlApi.Rgba, GlApi.UnsignedByte, p);
        for (int k = 0; k < mipLevels.Count; ++k)
            fixed (byte* p = mipLevels[k])
                gl.TexImage2D_(GlApi.Texture2D, k + 1, (int)GlApi.Rgba8, Math.Max(1, width >> (k + 1)), Math.Max(1, height >> (k + 1)), 0,
                               GlApi.Rgba, GlApi.UnsignedByte, p);
        // Stock filters: linear magnification, LINEAR_MIPMAP_NEAREST minification (code 005d3f99, 005d3fb9).
        gl.TexParameteri_(GlApi.Texture2D, GlApi.TextureMagFilter, (int)GlApi.Linear);
        gl.TexParameteri_(GlApi.Texture2D, GlApi.TextureMinFilter, 0x2701);
        gl.TexParameteri_(GlApi.Texture2D, 0x813D, mipLevels.Count); // TEXTURE_MAX_LEVEL
        uint wrap = options.Repeat ? GlApi.Repeat : GlApi.ClampToEdge;
        gl.TexParameteri_(GlApi.Texture2D, GlApi.TextureWrapS, (int)wrap);
        gl.TexParameteri_(GlApi.Texture2D, GlApi.TextureWrapT, (int)wrap);
        return new Texture3D { Name = name, Width = width, Height = height, Gl = gl };
    }

    private GfxRect? mViewport3D;

    public void SetViewport3D(GfxRect? rect) => mViewport3D = rect;

    public void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB)
    {
        if (mProgram3D == 0)
            Init3D();
        mIn3D = true;
        mDraws3D = 0;
        (mHazeR, mHazeG, mHazeB) = (hazeR, hazeG, hazeB);
        gl.BindFramebuffer_(GlApi.Framebuffer, mFbo);
        if (mViewport3D is GfxRect vr && !vr.Intersect(new GfxRect(0, 0, mWidth - 1, mHeight - 1)).IsEmpty)
        {
            GfxRect r = vr.Intersect(new GfxRect(0, 0, mWidth - 1, mHeight - 1));
            // GL counts rows from the bottom
            gl.Viewport_(vr.Left, mHeight - 1 - vr.Bottom, vr.Width, vr.Height);
            gl.Enable_(GlApi.ScissorTest);
            gl.Scissor_(r.Left, mHeight - 1 - r.Bottom, r.Width, r.Height);
        }
        else
        {
            gl.Viewport_(0, 0, mWidth, mHeight);
            gl.Disable_(GlApi.ScissorTest);
        }
        gl.Disable_(GlApi.CullFace);
        gl.UseProgram_(mProgram3D);
        gl.BindVertexArray_(mVao3D);
        gl.BindBuffer_(GlApi.ArrayBuffer, mVbo3D);
        gl.BindSampler_(0, 0);
        gl.BindSampler_(1, 0);
        gl.Uniform1i_(mU3Tex, 0);
        gl.Uniform1i_(mU3Light, 1);
        gl.Uniform3f_(mU3Haze, hazeR, hazeG, hazeB);
        gl.Enable_(GlApi.DepthTest);
        gl.DepthFunc_(GlApi.GEqual);
        gl.DepthMask_(1);
        gl.ClearDepth_(0.0);
        gl.Clear_(GlApi.DepthBufferBit);
        SetCamera3D(camera);
    }

    public void SetDynamicLights(ReadOnlySpan<DynamicLight> lights)
    {
        if (mProgram3D == 0)
            Init3D();
        int n = Math.Min(lights.Length, IGfxDevice.MaxDynamicLights);
        for (int i = 0; i < n; ++i)
        {
            DynamicLight l = lights[i];
            (mDynPos[i * 4], mDynPos[i * 4 + 1], mDynPos[i * 4 + 2], mDynPos[i * 4 + 3]) = (l.X, l.Y, l.Z, Math.Max(l.Range, 1e-3f));
            (mDynCol[i * 3], mDynCol[i * 3 + 1], mDynCol[i * 3 + 2]) = (l.R, l.G, l.B);
        }
        mDynCount = n;
        gl.UseProgram_(mProgram3D);
        gl.Uniform1i_(mU3DynCount, n);
        if (n > 0)
        {
            fixed (float* p = mDynPos)
                gl.Uniform4fv_(mU3DynPos, n, p);
            fixed (float* p = mDynCol)
                gl.Uniform3fv_(mU3DynCol, n, p);
        }
    }

    public void SetCamera3D(in Camera3D camera)
    {
        Span<float> m = stackalloc float[16];
        camera.ViewProjection(m);
        fixed (float* p = m)
            gl.UniformMatrix4fv_(mU3ViewProj, 1, 0, p);
    }

    public void Draw3D(ReadOnlySpan<Vertex3D> triangles, in DrawState3D state)
    {
        if (!mIn3D)
            throw new InvalidOperationException("Draw3D outside BeginScene3D");
        if (triangles.Length < 3)
            return;
        var tex = state.Texture as Texture3D;
        var light = state.LightMap as Texture3D;
        gl.Uniform1i_(mU3UseTex, tex != null ? 1 : 0);
        gl.Uniform1i_(mU3UseLight, light != null ? 1 : 0);
        gl.Uniform1f_(mU3AlphaTest, state.AlphaTest);
        if (tex != null)
        {
            gl.ActiveTexture_(GlApi.Texture0);
            gl.BindTexture_(GlApi.Texture2D, tex.Name);
        }
        if (light != null)
        {
            gl.ActiveTexture_(GlApi.Texture1);
            gl.BindTexture_(GlApi.Texture2D, light.Name);
        }
        gl.ActiveTexture_(GlApi.Texture0);
        if (state.DepthTest)
            gl.Enable_(GlApi.DepthTest);
        else
            gl.Disable_(GlApi.DepthTest);
        gl.DepthMask_((byte)(state.DepthWrite ? 1 : 0));
        if (state.Blend)
        {
            gl.Enable_(GlApi.Blend);
            switch (state.BlendKind)
            {
                case BlendMode.Additive:
                    gl.BlendFunc_(GlApi.SrcAlpha, GlApi.ONE);
                    break;
                case BlendMode.Subtractive:
                    gl.BlendFunc_(GlApi.ZERO, GlApi.OneMinusSrcColor);
                    break;
                default:
                    gl.BlendFunc_(GlApi.SrcAlpha, GlApi.OneMinusSrcAlpha);
                    break;
            }
        }
        else
        {
            gl.Disable_(GlApi.Blend);
        }
        fixed (Vertex3D* p = triangles)
            gl.BufferData_(GlApi.ArrayBuffer, (nint)(triangles.Length * sizeof(Vertex3D)), p, GlApi.StreamDraw);
        gl.DrawArrays_(GlApi.Triangles, 0, triangles.Length - triangles.Length % 3);
        ++mDraws3D;
        ++mDrawCalls;
        mTriangles += triangles.Length / 3;
    }

    public void EndScene3D()
    {
        mIn3D = false;
        gl.DepthMask_(1);
        gl.Disable_(GlApi.Blend);
        Prepare2D();
        ApplyClip();
    }
}
