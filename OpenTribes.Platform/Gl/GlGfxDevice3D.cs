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
void main() {
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
    if (uUseLight != 0) c.rgb *= texture(uLight, vLm).rgb;
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
        // Magnified texels stay square: the stock shots show chunky pixels on terrain, floors and shapes at every distance
        // while minified areas are smooth (docs/renderer.md Q2).
        gl.TexParameteri_(GlApi.Texture2D, GlApi.TextureMagFilter, (int)GlApi.Nearest);
        gl.TexParameteri_(GlApi.Texture2D, GlApi.TextureMinFilter,
                          options.Mipmaps ? (int)(options.Linear ? GlApi.LinearMipmapLinear : 0x2700) : (int)filter);
        uint wrap = options.Repeat ? GlApi.Repeat : GlApi.ClampToEdge;
        gl.TexParameteri_(GlApi.Texture2D, GlApi.TextureWrapS, (int)wrap);
        gl.TexParameteri_(GlApi.Texture2D, GlApi.TextureWrapT, (int)wrap);
        if (options.Mipmaps)
            gl.GenerateMipmap_(GlApi.Texture2D);
        return new Texture3D { Name = name, Width = width, Height = height, Gl = gl };
    }

    public void BeginScene3D(in Camera3D camera, float hazeR, float hazeG, float hazeB)
    {
        if (mProgram3D == 0)
            Init3D();
        mIn3D = true;
        mDraws3D = 0;
        (mHazeR, mHazeG, mHazeB) = (hazeR, hazeG, hazeB);
        gl.BindFramebuffer_(GlApi.Framebuffer, mFbo);
        gl.Viewport_(0, 0, mWidth, mHeight);
        gl.Disable_(GlApi.ScissorTest);
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
        gl.DepthFunc_(GlApi.LEqual);
        gl.DepthMask_(1);
        gl.Clear_(GlApi.DepthBufferBit);
        SetCamera3D(camera);
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
    }
}
