namespace OpenTribes.Platform.Gl;

using System.Runtime.InteropServices;
using OpenTribes.Platform.Gfx;

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
            // The interior light merge (00557ea8, 00557c80): the colour is four bits per channel, the squared distance in 64
            // steps of the squared range, and each step adds trunc(L * (1 - sqrt(step / 64)) + 0.5) to the four bit light map value.
            float d = distance(vWorld, uDynPos[i].xyz);
            if (d >= uDynPos[i].w) continue;
            vec3 lv = floor(uDynCol[i] * 15.99);
            float step = min(floor(64.0 * d * d / (uDynPos[i].w * uDynPos[i].w)), 63.0);
            lm += floor(lv * (1.0 - sqrt(step / 64.0)) + 0.5) * (16.0 / 255.0);
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

    /// <summary>Gets the draw calls issued since the last <see cref="BeginScene3D"/>, a statistic for tests and the console.</summary>
    public int Draws3D { get; private set; }

    private void Init3D()
    {
        mProgram3D = Api.BuildProgram(Vertex3DSource, Fragment3DSource, (0, "aPos"), (1, "aUv"), (2, "aLm"), (3, "aColor"),
                                     (4, "aHaze"));
        mU3ViewProj = Api.UniformLocation(mProgram3D, "uViewProj");
        mU3Tex = Api.UniformLocation(mProgram3D, "uTex");
        mU3Light = Api.UniformLocation(mProgram3D, "uLight");
        mU3UseTex = Api.UniformLocation(mProgram3D, "uUseTex");
        mU3UseLight = Api.UniformLocation(mProgram3D, "uUseLight");
        mU3AlphaTest = Api.UniformLocation(mProgram3D, "uAlphaTest");
        mU3Haze = Api.UniformLocation(mProgram3D, "uHaze");
        mU3DynCount = Api.UniformLocation(mProgram3D, "uDynCount");
        mU3DynPos = Api.UniformLocation(mProgram3D, "uDynPos[0]");
        mU3DynCol = Api.UniformLocation(mProgram3D, "uDynCol[0]");
        mVao3D = Api.GenVertexArray();
        mVbo3D = Api.GenBuffer();
        Api.BindVertexArray_(mVao3D);
        Api.BindBuffer_(GlApi.ArrayBuffer, mVbo3D);
        var stride = Marshal.SizeOf<Vertex3D>();
        Api.VertexAttribPointer_(0, 3, GlApi.Float, 0, stride, (void*)0);
        Api.VertexAttribPointer_(1, 2, GlApi.Float, 0, stride, (void*)12);
        Api.VertexAttribPointer_(2, 2, GlApi.Float, 0, stride, (void*)20);
        Api.VertexAttribPointer_(3, 4, GlApi.Float, 0, stride, (void*)28);
        Api.VertexAttribPointer_(4, 1, GlApi.Float, 0, stride, (void*)44);
        for (uint i = 0; i < 5; ++i)
            Api.EnableVertexAttribArray_(i);
        Api.BindVertexArray_(mVao);
    }

    private void Dispose3D()
    {
        if (mProgram3D == 0)
            return;
        uint vao = mVao3D, vbo = mVbo3D;
        Api.DeleteVertexArrays_(1, &vao);
        Api.DeleteBuffers_(1, &vbo);
        Api.DeleteProgram_(mProgram3D);
        mProgram3D = 0;
    }

    /// <inheritdoc/>
    public IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("texture data does not match its size");
        var name = Api.GenTexture();
        Api.ActiveTexture_(GlApi.Texture0);
        Api.BindTexture_(GlApi.Texture2D, name);
        Api.PixelStorei_(GlApi.UnpackAlignment, 1);
        fixed (byte* p = rgba)
            Api.TexImage2D_(GlApi.Texture2D, 0, (int)GlApi.Rgba8, width, height, 0, GlApi.Rgba, GlApi.UnsignedByte, p);
        var filter = options.Linear ? GlApi.Linear : GlApi.Nearest;
        // Magnified texels are bilinear like the stock client's (compared at 640 x 480 and 1280 x 960 with tools/shotdiff.py).
        Api.TexParameteri_(GlApi.Texture2D, GlApi.TextureMagFilter, (int)(options.NearestMag ? GlApi.Nearest : filter));
        Api.TexParameteri_(GlApi.Texture2D, GlApi.TextureMinFilter,
                          options.Mipmaps ? (int)(options.Linear ? GlApi.LinearMipmapLinear : 0x2700) : (int)filter);
        var wrap = options.Repeat ? GlApi.Repeat : GlApi.ClampToEdge;
        Api.TexParameteri_(GlApi.Texture2D, GlApi.TextureWrapS, (int)wrap);
        Api.TexParameteri_(GlApi.Texture2D, GlApi.TextureWrapT, (int)wrap);
        if (options.Mipmaps)
            Api.GenerateMipmap_(GlApi.Texture2D);
        return new Texture3D { Name = name, Width = width, Height = height, Gl = Api };
    }

    /// <inheritdoc/>
    public IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options, IReadOnlyList<byte[]> mipLevels)
    {
        if (mipLevels.Count == 0 || !options.Mipmaps)
            return CreateTexture3D(rgba, width, height, options);
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("texture data does not match its size");
        var name = Api.GenTexture();
        Api.ActiveTexture_(GlApi.Texture0);
        Api.BindTexture_(GlApi.Texture2D, name);
        Api.PixelStorei_(GlApi.UnpackAlignment, 1);
        fixed (byte* p = rgba)
            Api.TexImage2D_(GlApi.Texture2D, 0, (int)GlApi.Rgba8, width, height, 0, GlApi.Rgba, GlApi.UnsignedByte, p);
        for (var k = 0; k < mipLevels.Count; ++k)
            fixed (byte* p = mipLevels[k])
                Api.TexImage2D_(GlApi.Texture2D, k + 1, (int)GlApi.Rgba8, Math.Max(1, width >> (k + 1)), Math.Max(1, height >> (k + 1)), 0,
                               GlApi.Rgba, GlApi.UnsignedByte, p);
        // Stock filters: linear magnification (nearest for a transparent bitmap), LINEAR_MIPMAP_NEAREST minification (code 005d3f99,
        // 005d3fa2, 005d3fb9).
        Api.TexParameteri_(GlApi.Texture2D, GlApi.TextureMagFilter, (int)(options.NearestMag ? GlApi.Nearest : GlApi.Linear));
        Api.TexParameteri_(GlApi.Texture2D, GlApi.TextureMinFilter, 0x2701);
        Api.TexParameteri_(GlApi.Texture2D, 0x813D, mipLevels.Count); // TEXTURE_MAX_LEVEL
        var wrap = options.Repeat ? GlApi.Repeat : GlApi.ClampToEdge;
        Api.TexParameteri_(GlApi.Texture2D, GlApi.TextureWrapS, (int)wrap);
        Api.TexParameteri_(GlApi.Texture2D, GlApi.TextureWrapT, (int)wrap);
        return new Texture3D { Name = name, Width = width, Height = height, Gl = Api };
    }

    private GfxRect? mViewport3D;

    /// <inheritdoc/>
    public void SetViewport3D(GfxRect? rect) => mViewport3D = rect;

    /// <inheritdoc/>
    public void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB)
    {
        if (mProgram3D == 0)
            Init3D();
        mIn3D = true;
        Draws3D = 0;
        (mHazeR, mHazeG, mHazeB) = (hazeR, hazeG, hazeB);
        Api.BindFramebuffer_(GlApi.Framebuffer, mFbo);
        if (mViewport3D is GfxRect vr && !vr.Intersect(new GfxRect(0, 0, Width - 1, Height - 1)).IsEmpty)
        {
            GfxRect r = vr.Intersect(new GfxRect(0, 0, Width - 1, Height - 1));
            // GL counts rows from the bottom
            Api.Viewport_(vr.Left, Height - 1 - vr.Bottom, vr.Width, vr.Height);
            Api.Enable_(GlApi.ScissorTest);
            Api.Scissor_(r.Left, Height - 1 - r.Bottom, r.Width, r.Height);
        }
        else
        {
            Api.Viewport_(0, 0, Width, Height);
            Api.Disable_(GlApi.ScissorTest);
        }
        Api.Disable_(GlApi.CullFace);
        Api.UseProgram_(mProgram3D);
        Api.BindVertexArray_(mVao3D);
        Api.BindBuffer_(GlApi.ArrayBuffer, mVbo3D);
        Api.BindSampler_(0, 0);
        Api.BindSampler_(1, 0);
        Api.Uniform1i_(mU3Tex, 0);
        Api.Uniform1i_(mU3Light, 1);
        Api.Uniform3f_(mU3Haze, hazeR, hazeG, hazeB);
        Api.Enable_(GlApi.DepthTest);
        Api.DepthFunc_(GlApi.GEqual);
        Api.DepthMask_(1);
        Api.ClearDepth_(0.0);
        Api.Clear_(GlApi.DepthBufferBit);
        SetCamera3D(camera);
    }

    /// <inheritdoc/>
    public void SetDynamicLights(ReadOnlySpan<DynamicLight> lights)
    {
        if (mProgram3D == 0)
            Init3D();
        var n = Math.Min(lights.Length, IGfxDevice.MaxDynamicLights);
        for (var i = 0; i < n; ++i)
        {
            DynamicLight l = lights[i];
            (mDynPos[i * 4], mDynPos[(i * 4) + 1], mDynPos[(i * 4) + 2], mDynPos[(i * 4) + 3]) = (l.X, l.Y, l.Z, Math.Max(l.Range, 1e-3f));
            (mDynCol[i * 3], mDynCol[(i * 3) + 1], mDynCol[(i * 3) + 2]) = (l.R, l.G, l.B);
        }
        mDynCount = n;
        Api.UseProgram_(mProgram3D);
        Api.Uniform1i_(mU3DynCount, n);
        if (n > 0)
        {
            fixed (float* p = mDynPos)
                Api.Uniform4fv_(mU3DynPos, n, p);
            fixed (float* p = mDynCol)
                Api.Uniform3fv_(mU3DynCol, n, p);
        }
    }

    /// <inheritdoc/>
    public void SetCamera3D(in Camera3D camera)
    {
        Span<float> m = stackalloc float[16];
        camera.ViewProjection(m);
        fixed (float* p = m)
            Api.UniformMatrix4fv_(mU3ViewProj, 1, 0, p);
    }

    /// <inheritdoc/>
    public void Draw3D(ReadOnlySpan<Vertex3D> triangles, in DrawState3D state)
    {
        if (!mIn3D)
            throw new InvalidOperationException("Draw3D outside BeginScene3D");
        if (triangles.Length < 3)
            return;
        var tex = state.Texture as Texture3D;
        var light = state.LightMap as Texture3D;
        Api.Uniform1i_(mU3UseTex, tex != null ? 1 : 0);
        Api.Uniform1i_(mU3UseLight, light != null ? 1 : 0);
        Api.Uniform1f_(mU3AlphaTest, state.AlphaTest);
        if (tex != null)
        {
            Api.ActiveTexture_(GlApi.Texture0);
            Api.BindTexture_(GlApi.Texture2D, tex.Name);
        }
        if (light != null)
        {
            Api.ActiveTexture_(GlApi.Texture1);
            Api.BindTexture_(GlApi.Texture2D, light.Name);
        }
        Api.ActiveTexture_(GlApi.Texture0);
        if (state.DepthTest)
            Api.Enable_(GlApi.DepthTest);
        else
            Api.Disable_(GlApi.DepthTest);
        Api.DepthMask_((byte)(state.DepthWrite ? 1 : 0));
        if (state.Blend)
        {
            Api.Enable_(GlApi.Blend);
            switch (state.BlendKind)
            {
                case BlendMode.Additive:
                    Api.BlendFunc_(GlApi.SrcAlpha, GlApi.ONE);
                    break;
                case BlendMode.Subtractive:
                    Api.BlendFunc_(GlApi.ZERO, GlApi.OneMinusSrcColor);
                    break;
                default:
                    Api.BlendFunc_(GlApi.SrcAlpha, GlApi.OneMinusSrcAlpha);
                    break;
            }
        }
        else
        {
            Api.Disable_(GlApi.Blend);
        }
        fixed (Vertex3D* p = triangles)
            Api.BufferData_(GlApi.ArrayBuffer, triangles.Length * sizeof(Vertex3D), p, GlApi.StreamDraw);
        Api.DrawArrays_(GlApi.Triangles, 0, triangles.Length - (triangles.Length % 3));
        ++Draws3D;
        ++DrawCalls;
        Triangles += triangles.Length / 3;
    }

    /// <inheritdoc/>
    public void EndScene3D()
    {
        mIn3D = false;
        Api.DepthMask_(1);
        Api.Disable_(GlApi.Blend);
        Prepare2D();
        ApplyClip();
    }
}
