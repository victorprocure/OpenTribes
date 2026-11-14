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
        public uint Name { get; set; }
        public int Width { get; init; }
        public int Height { get; init; }
        public GlApi? Gl { get; set; }

        public void Dispose()
        {
            if (Gl != null && Name != 0)
                Gl.DeleteTexture(Name);
            Name = 0;
        }
    }

    private uint _program3D, _vao3D, _vbo3D;
    private int _u3DynCount, _u3DynPos, _u3DynCol;
    private int _dynCount;
    private readonly float[] _dynPos = new float[16 * 4], _dynCol = new float[16 * 3];
    private int _u3ViewProj, _u3Tex, _u3Light, _u3UseTex, _u3UseLight, _u3AlphaTest, _u3Haze;
    private float _hazeR, _hazeG, _hazeB;
    private bool _in3D;

    /// <summary>Gets the draw calls issued since the last <see cref="BeginScene3D"/>, a statistic for tests and the console.</summary>
    public int Draws3D { get; private set; }

    private void Init3D()
    {
        _program3D = Api.BuildProgram(Vertex3DSource, Fragment3DSource, (0, "aPos"), (1, "aUv"), (2, "aLm"), (3, "aColor"),
                                     (4, "aHaze"));
        _u3ViewProj = Api.UniformLocation(_program3D, "uViewProj");
        _u3Tex = Api.UniformLocation(_program3D, "uTex");
        _u3Light = Api.UniformLocation(_program3D, "uLight");
        _u3UseTex = Api.UniformLocation(_program3D, "uUseTex");
        _u3UseLight = Api.UniformLocation(_program3D, "uUseLight");
        _u3AlphaTest = Api.UniformLocation(_program3D, "uAlphaTest");
        _u3Haze = Api.UniformLocation(_program3D, "uHaze");
        _u3DynCount = Api.UniformLocation(_program3D, "uDynCount");
        _u3DynPos = Api.UniformLocation(_program3D, "uDynPos[0]");
        _u3DynCol = Api.UniformLocation(_program3D, "uDynCol[0]");
        _vao3D = Api.GenVertexArray();
        _vbo3D = Api.GenBuffer();
        Api.BindVertexArrayPtr(_vao3D);
        Api.BindBufferPtr(GlApi.ArrayBuffer, _vbo3D);
        var stride = Marshal.SizeOf<Vertex3D>();
        Api.VertexAttribPointerPtr(0, 3, GlApi.FloatType, 0, stride, (void*)0);
        Api.VertexAttribPointerPtr(1, 2, GlApi.FloatType, 0, stride, (void*)12);
        Api.VertexAttribPointerPtr(2, 2, GlApi.FloatType, 0, stride, (void*)20);
        Api.VertexAttribPointerPtr(3, 4, GlApi.FloatType, 0, stride, (void*)28);
        Api.VertexAttribPointerPtr(4, 1, GlApi.FloatType, 0, stride, (void*)44);
        for (uint i = 0; i < 5; ++i)
            Api.EnableVertexAttribArrayPtr(i);
        Api.BindVertexArrayPtr(_vao);
    }

    private void Dispose3D()
    {
        if (_program3D == 0)
            return;
        uint vao = _vao3D, vbo = _vbo3D;
        Api.DeleteVertexArraysPtr(1, &vao);
        Api.DeleteBuffersPtr(1, &vbo);
        Api.DeleteProgramPtr(_program3D);
        _program3D = 0;
    }

    /// <inheritdoc/>
    public IGfxTexture CreateTexture3D(ReadOnlySpan<byte> rgba, int width, int height, TextureOptions options)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("texture data does not match its size");
        var name = Api.GenTexture();
        Api.ActiveTexturePtr(GlApi.Texture0);
        Api.BindTexturePtr(GlApi.Texture2D, name);
        Api.PixelStoreiPtr(GlApi.UnpackAlignment, 1);
        fixed (byte* p = rgba)
            Api.TexImage2DPtr(GlApi.Texture2D, 0, (int)GlApi.Rgba8, width, height, 0, GlApi.Rgba, GlApi.UnsignedByte, p);
        var filter = options.Linear ? GlApi.Linear : GlApi.Nearest;
        // Magnified texels are bilinear like the stock client's (compared at 640 x 480 and 1280 x 960 with tools/shotdiff.py).
        Api.TexParameteriPtr(GlApi.Texture2D, GlApi.TextureMagFilter, (int)(options.NearestMag ? GlApi.Nearest : filter));
        Api.TexParameteriPtr(GlApi.Texture2D, GlApi.TextureMinFilter,
                          options.Mipmaps ? (int)(options.Linear ? GlApi.LinearMipmapLinear : 0x2700) : (int)filter);
        var wrap = options.Repeat ? GlApi.Repeat : GlApi.ClampToEdge;
        Api.TexParameteriPtr(GlApi.Texture2D, GlApi.TextureWrapS, (int)wrap);
        Api.TexParameteriPtr(GlApi.Texture2D, GlApi.TextureWrapT, (int)wrap);
        if (options.Mipmaps)
            Api.GenerateMipmapPtr(GlApi.Texture2D);
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
        Api.ActiveTexturePtr(GlApi.Texture0);
        Api.BindTexturePtr(GlApi.Texture2D, name);
        Api.PixelStoreiPtr(GlApi.UnpackAlignment, 1);
        fixed (byte* p = rgba)
            Api.TexImage2DPtr(GlApi.Texture2D, 0, (int)GlApi.Rgba8, width, height, 0, GlApi.Rgba, GlApi.UnsignedByte, p);
        for (var k = 0; k < mipLevels.Count; ++k)
            fixed (byte* p = mipLevels[k])
                Api.TexImage2DPtr(GlApi.Texture2D, k + 1, (int)GlApi.Rgba8, Math.Max(1, width >> (k + 1)), Math.Max(1, height >> (k + 1)), 0,
                               GlApi.Rgba, GlApi.UnsignedByte, p);
        // Stock filters: linear magnification (nearest for a transparent bitmap), LINEAR_MIPMAP_NEAREST minification (code 005d3f99,
        // 005d3fa2, 005d3fb9).
        Api.TexParameteriPtr(GlApi.Texture2D, GlApi.TextureMagFilter, (int)(options.NearestMag ? GlApi.Nearest : GlApi.Linear));
        Api.TexParameteriPtr(GlApi.Texture2D, GlApi.TextureMinFilter, 0x2701);
        Api.TexParameteriPtr(GlApi.Texture2D, 0x813D, mipLevels.Count); // TEXTURE_MAX_LEVEL
        var wrap = options.Repeat ? GlApi.Repeat : GlApi.ClampToEdge;
        Api.TexParameteriPtr(GlApi.Texture2D, GlApi.TextureWrapS, (int)wrap);
        Api.TexParameteriPtr(GlApi.Texture2D, GlApi.TextureWrapT, (int)wrap);
        return new Texture3D { Name = name, Width = width, Height = height, Gl = Api };
    }

    private GfxRect? _viewport3D;

    /// <inheritdoc/>
    public void SetViewport3D(GfxRect? rect) => _viewport3D = rect;

    /// <inheritdoc/>
    public void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB)
    {
        if (_program3D == 0)
            Init3D();
        _in3D = true;
        Draws3D = 0;
        (_hazeR, _hazeG, _hazeB) = (hazeR, hazeG, hazeB);
        Api.BindFramebufferPtr(GlApi.Framebuffer, _fbo);
        if (_viewport3D is GfxRect vr && !vr.Intersect(new GfxRect(0, 0, Width - 1, Height - 1)).IsEmpty)
        {
            var r = vr.Intersect(new GfxRect(0, 0, Width - 1, Height - 1));
            // GL counts rows from the bottom
            Api.ViewportPtr(vr.Left, Height - 1 - vr.Bottom, vr.Width, vr.Height);
            Api.EnablePtr(GlApi.ScissorTest);
            Api.ScissorPtr(r.Left, Height - 1 - r.Bottom, r.Width, r.Height);
        }
        else
        {
            Api.ViewportPtr(0, 0, Width, Height);
            Api.DisablePtr(GlApi.ScissorTest);
        }
        Api.DisablePtr(GlApi.CullFace);
        Api.UseProgramPtr(_program3D);
        Api.BindVertexArrayPtr(_vao3D);
        Api.BindBufferPtr(GlApi.ArrayBuffer, _vbo3D);
        Api.BindSamplerPtr(0, 0);
        Api.BindSamplerPtr(1, 0);
        Api.Uniform1iPtr(_u3Tex, 0);
        Api.Uniform1iPtr(_u3Light, 1);
        Api.Uniform3fPtr(_u3Haze, hazeR, hazeG, hazeB);
        Api.EnablePtr(GlApi.DepthTest);
        Api.DepthFuncPtr(GlApi.GEqual);
        Api.DepthMaskPtr(1);
        Api.ClearDepthPtr(0.0);
        Api.ClearPtr(GlApi.DepthBufferBit);
        SetCamera3D(camera);
    }

    /// <inheritdoc/>
    public void SetDynamicLights(ReadOnlySpan<DynamicLight> lights)
    {
        if (_program3D == 0)
            Init3D();
        var n = Math.Min(lights.Length, IGfxDevice.MaxDynamicLights);
        for (var i = 0; i < n; ++i)
        {
            var l = lights[i];
            (_dynPos[i * 4], _dynPos[(i * 4) + 1], _dynPos[(i * 4) + 2], _dynPos[(i * 4) + 3]) = (l.X, l.Y, l.Z, Math.Max(l.Range, 1e-3f));
            (_dynCol[i * 3], _dynCol[(i * 3) + 1], _dynCol[(i * 3) + 2]) = (l.R, l.G, l.B);
        }
        _dynCount = n;
        Api.UseProgramPtr(_program3D);
        Api.Uniform1iPtr(_u3DynCount, n);
        if (n > 0)
        {
            fixed (float* p = _dynPos)
                Api.Uniform4fvPtr(_u3DynPos, n, p);
            fixed (float* p = _dynCol)
                Api.Uniform3fvPtr(_u3DynCol, n, p);
        }
    }

    /// <inheritdoc/>
    public void SetCamera3D(in Camera3D camera)
    {
        Span<float> m = stackalloc float[16];
        camera.ViewProjection(m);
        fixed (float* p = m)
            Api.UniformMatrix4fvPtr(_u3ViewProj, 1, 0, p);
    }

    /// <inheritdoc/>
    public void Draw3D(ReadOnlySpan<Vertex3D> triangles, in DrawState3D state)
    {
        if (!_in3D)
            throw new InvalidOperationException("Draw3D outside BeginScene3D");
        if (triangles.Length < 3)
            return;
        var tex = state.Texture as Texture3D;
        var light = state.LightMap as Texture3D;
        Api.Uniform1iPtr(_u3UseTex, tex != null ? 1 : 0);
        Api.Uniform1iPtr(_u3UseLight, light != null ? 1 : 0);
        Api.Uniform1fPtr(_u3AlphaTest, state.AlphaTest);
        if (tex != null)
        {
            Api.ActiveTexturePtr(GlApi.Texture0);
            Api.BindTexturePtr(GlApi.Texture2D, tex.Name);
        }
        if (light != null)
        {
            Api.ActiveTexturePtr(GlApi.Texture1);
            Api.BindTexturePtr(GlApi.Texture2D, light.Name);
        }
        Api.ActiveTexturePtr(GlApi.Texture0);
        if (state.DepthTest)
            Api.EnablePtr(GlApi.DepthTest);
        else
            Api.DisablePtr(GlApi.DepthTest);
        Api.DepthMaskPtr((byte)(state.DepthWrite ? 1 : 0));
        if (state.Blend)
        {
            Api.EnablePtr(GlApi.Blend);
            switch (state.BlendKind)
            {
                case BlendMode.Additive:
                    Api.BlendFuncPtr(GlApi.SrcAlpha, GlApi.ONE);
                    break;
                case BlendMode.Subtractive:
                    Api.BlendFuncPtr(GlApi.ZERO, GlApi.OneMinusSrcColor);
                    break;
                case BlendMode.Opaque:
                case BlendMode.Transparent:
                case BlendMode.Translucent:
                default:
                    Api.BlendFuncPtr(GlApi.SrcAlpha, GlApi.OneMinusSrcAlpha);
                    break;
            }
        }
        else
        {
            Api.DisablePtr(GlApi.Blend);
        }
        fixed (Vertex3D* p = triangles)
            Api.BufferDataPtr(GlApi.ArrayBuffer, triangles.Length * sizeof(Vertex3D), p, GlApi.StreamDraw);
        Api.DrawArraysPtr(GlApi.Triangles, 0, triangles.Length - (triangles.Length % 3));
        ++Draws3D;
        ++DrawCalls;
        Triangles += triangles.Length / 3;
    }

    /// <inheritdoc/>
    public void EndScene3D()
    {
        _in3D = false;
        Api.DepthMaskPtr(1);
        Api.DisablePtr(GlApi.Blend);
        Prepare2D();
        ApplyClip();
    }
}
