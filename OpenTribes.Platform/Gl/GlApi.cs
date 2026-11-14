namespace OpenTribes.Platform.Gl;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>An OpenGL 3.3 core function loader through SDL_GL_GetProcAddress (D30). It loads only the calls the renderer uses.</summary>
public sealed unsafe class GlApi
{
    // Enumerants
    /// <summary>The buffer clear bits.</summary>
    public const uint DepthBufferBit = 0x100, ColorBufferBit = 0x4000;
    /// <summary>The primitive modes.</summary>
    public const uint Lines = 1, Triangles = 4;
    /// <summary>The blend factors.</summary>
    public const uint ZERO = 0, ONE = 1, SrcColor = 0x300, OneMinusSrcColor = 0x301, SrcAlpha = 0x302, OneMinusSrcAlpha = 0x303;
    /// <summary>The capability and texture target enumerants.</summary>
    public const uint Texture2D = 0x0DE1, Blend = 0x0BE2, ScissorTest = 0x0C11, DepthTest = 0x0B71, CullFace = 0x0B44;
    /// <summary>The pixel formats and types.</summary>
    public const uint Rgba = 0x1908, Rgb = 0x1907, UnsignedByte = 0x1401, FloatType = 0x1406, Rgba8 = 0x8058;
    /// <summary>The texture parameter names.</summary>
    public const uint TextureMinFilter = 0x2801, TextureMagFilter = 0x2800, TextureWrapS = 0x2802, TextureWrapT = 0x2803;
    /// <summary>The filter and wrap values.</summary>
    public const uint Nearest = 0x2600, Linear = 0x2601, ClampToEdge = 0x812F;
    /// <summary>The buffer target and usage.</summary>
    public const uint ArrayBuffer = 0x8892, StreamDraw = 0x88E0;
    /// <summary>The shader types and query names.</summary>
    public const uint VertexShader = 0x8B31, FragmentShader = 0x8B30, CompileStatus = 0x8B81, LinkStatus = 0x8B82,
        InfoLogLength = 0x8B84;
    /// <summary>The framebuffer and renderbuffer enumerants.</summary>
    public const uint Framebuffer = 0x8D40, ReadFramebuffer = 0x8CA8, DrawFramebuffer = 0x8CA9, Renderbuffer = 0x8D41,
        ColorAttachment0 = 0x8CE0, DepthAttachment = 0x8D00, FramebufferComplete = 0x8CD5, DepthComponent24 = 0x81A6, DepthComponent16 = 0x81A5;
    /// <summary>The string query names and the pixel store names.</summary>
    public const uint Version = 0x1F02, Renderer = 0x1F01, Vendor = 0x1F00, UnpackAlignment = 0x0CF5, PackAlignment = 0x0D05;
    /// <summary>The texture units.</summary>
    public const uint Texture0 = 0x84C0, Texture1 = 0x84C1;
    /// <summary>The depth functions, wrap mode and mipmap filter.</summary>
    public const uint Less = 0x201, LEqual = 0x203, GEqual = 0x206, Always = 0x207, Repeat = 0x2901, LinearMipmapLinear = 0x2703;
    /// <summary>The front face windings.</summary>
    public const uint CW = 0x900, CCW = 0x901;

    private readonly Func<string, IntPtr> _loader;

    private IntPtr Load(string name)
    {
        var p = _loader(name);
        if (p == IntPtr.Zero)
            throw new InvalidOperationException("OpenGL function missing: " + name);
        return p;
    }

    /// <summary>The glGetError entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint> GetErrorPtr { get; }
    /// <summary>The glGetString entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, byte*> GetStringPtr { get; }
    /// <summary>The glClearColor entry point.</summary>
    public delegate* unmanaged[Cdecl]<float, float, float, float, void> ClearColorPtr { get; }
    /// <summary>The glClear entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, void> ClearPtr { get; }
    /// <summary>The glClearDepth entry point.</summary>
    public delegate* unmanaged[Cdecl]<double, void> ClearDepthPtr { get; }
    /// <summary>The glViewport entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, int, int, int, void> ViewportPtr { get; }
    /// <summary>The glScissor entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, int, int, int, void> ScissorPtr { get; }
    /// <summary>The glEnable entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, void> EnablePtr { get; }
    /// <summary>The glDisable entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, void> DisablePtr { get; }
    /// <summary>The glBlendFunc entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, void> BlendFuncPtr { get; }
    /// <summary>The glGenTextures entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, uint*, void> GenTexturesPtr { get; }
    /// <summary>The glDeleteTextures entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, uint*, void> DeleteTexturesPtr { get; }
    /// <summary>The glBindTexture entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, void> BindTexturePtr { get; }
    /// <summary>The glTexImage2D entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2DPtr { get; }
    /// <summary>The glTexParameteri entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, int, void> TexParameteriPtr { get; }
    /// <summary>The glActiveTexture entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, void> ActiveTexturePtr { get; }
    /// <summary>The glReadPixels entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, int, int, int, uint, uint, void*, void> ReadPixelsPtr { get; }
    /// <summary>The glPixelStorei entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, int, void> PixelStoreiPtr { get; }
    /// <summary>The glDrawArrays entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, int, int, void> DrawArraysPtr { get; }
    /// <summary>The glGenBuffers entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, uint*, void> GenBuffersPtr { get; }
    /// <summary>The glDeleteBuffers entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, uint*, void> DeleteBuffersPtr { get; }
    /// <summary>The glBindBuffer entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, void> BindBufferPtr { get; }
    /// <summary>The glBufferData entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, nint, void*, uint, void> BufferDataPtr { get; }
    /// <summary>The glGenVertexArrays entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, uint*, void> GenVertexArraysPtr { get; }
    /// <summary>The glDeleteVertexArrays entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, uint*, void> DeleteVertexArraysPtr { get; }
    /// <summary>The glBindVertexArray entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, void> BindVertexArrayPtr { get; }
    /// <summary>The glVertexAttribPointer entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, int, uint, byte, int, void*, void> VertexAttribPointerPtr { get; }
    /// <summary>The glEnableVertexAttribArray entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, void> EnableVertexAttribArrayPtr { get; }
    /// <summary>The glCreateShader entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint> CreateShaderPtr { get; }
    /// <summary>The glShaderSource entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, int, byte**, int*, void> ShaderSourcePtr { get; }
    /// <summary>The glCompileShader entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, void> CompileShaderPtr { get; }
    /// <summary>The glGetShaderiv entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, int*, void> GetShaderivPtr { get; }
    /// <summary>The glGetShaderInfoLog entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void> GetShaderInfoLogPtr { get; }
    /// <summary>The glDeleteShader entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, void> DeleteShaderPtr { get; }
    /// <summary>The glCreateProgram entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint> CreateProgramPtr { get; }
    /// <summary>The glAttachShader entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, void> AttachShaderPtr { get; }
    /// <summary>The glBindAttribLocation entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, byte*, void> BindAttribLocationPtr { get; }
    /// <summary>The glLinkProgram entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, void> LinkProgramPtr { get; }
    /// <summary>The glGetProgramiv entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, int*, void> GetProgramivPtr { get; }
    /// <summary>The glGetProgramInfoLog entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void> GetProgramInfoLogPtr { get; }
    /// <summary>The glUseProgram entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, void> UseProgramPtr { get; }
    /// <summary>The glDeleteProgram entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, void> DeleteProgramPtr { get; }
    /// <summary>The glGetUniformLocation entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, byte*, int> GetUniformLocationPtr { get; }
    /// <summary>The glUniform1i entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, int, void> Uniform1iPtr { get; }
    /// <summary>The glUniform2f entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, float, float, void> Uniform2fPtr { get; }
    /// <summary>The glGenFramebuffers entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, uint*, void> GenFramebuffersPtr { get; }
    /// <summary>The glDeleteFramebuffers entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, uint*, void> DeleteFramebuffersPtr { get; }
    /// <summary>The glBindFramebuffer entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, void> BindFramebufferPtr { get; }
    /// <summary>The glFramebufferTexture2D entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void> FramebufferTexture2DPtr { get; }
    /// <summary>The glCheckFramebufferStatus entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint> CheckFramebufferStatusPtr { get; }
    /// <summary>The glGenRenderbuffers entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, uint*, void> GenRenderbuffersPtr { get; }
    /// <summary>The glDeleteRenderbuffers entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, uint*, void> DeleteRenderbuffersPtr { get; }
    /// <summary>The glBindRenderbuffer entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, void> BindRenderbufferPtr { get; }
    /// <summary>The glRenderbufferStorage entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, int, int, void> RenderbufferStoragePtr { get; }
    /// <summary>The glFramebufferRenderbuffer entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, uint, uint, void> FramebufferRenderbufferPtr { get; }
    /// <summary>The glBlitFramebuffer entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, int, uint, uint, void> BlitFramebufferPtr { get; }
    /// <summary>The glGenSamplers entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, uint*, void> GenSamplersPtr { get; }
    /// <summary>The glDeleteSamplers entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, uint*, void> DeleteSamplersPtr { get; }
    /// <summary>The glBindSampler entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, void> BindSamplerPtr { get; }
    /// <summary>The glSamplerParameteri entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, uint, int, void> SamplerParameteriPtr { get; }
    /// <summary>The glGenerateMipmap entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, void> GenerateMipmapPtr { get; }
    /// <summary>The glDepthFunc entry point.</summary>
    public delegate* unmanaged[Cdecl]<uint, void> DepthFuncPtr { get; }
    /// <summary>The glDepthMask entry point.</summary>
    public delegate* unmanaged[Cdecl]<byte, void> DepthMaskPtr { get; }
    /// <summary>The glUniform1f entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, float, void> Uniform1fPtr { get; }
    /// <summary>The glUniform3f entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, float, float, float, void> Uniform3fPtr { get; }
    /// <summary>The glUniformMatrix4fv entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, int, byte, float*, void> UniformMatrix4fvPtr { get; }
    /// <summary>The glUniform3fv entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, int, float*, void> Uniform3fvPtr { get; }
    /// <summary>The glUniform4fv entry point.</summary>
    public delegate* unmanaged[Cdecl]<int, int, float*, void> Uniform4fvPtr { get; }

    /// <summary>Loads every GL function the renderer uses.</summary>
    /// <param name="loader">Returns the address of a GL function, or zero (SDL_GL_GetProcAddress).</param>
    /// <exception cref="InvalidOperationException">A function is missing.</exception>
    public GlApi(Func<string, IntPtr> loader)
    {
        _loader = loader;
        GetErrorPtr = (delegate* unmanaged[Cdecl]<uint>)Load("glGetError");
        GetStringPtr = (delegate* unmanaged[Cdecl]<uint, byte*>)Load("glGetString");
        ClearColorPtr = (delegate* unmanaged[Cdecl]<float, float, float, float, void>)Load("glClearColor");
        ClearPtr = (delegate* unmanaged[Cdecl]<uint, void>)Load("glClear");
        ClearDepthPtr = (delegate* unmanaged[Cdecl]<double, void>)Load("glClearDepth");
        ViewportPtr = (delegate* unmanaged[Cdecl]<int, int, int, int, void>)Load("glViewport");
        ScissorPtr = (delegate* unmanaged[Cdecl]<int, int, int, int, void>)Load("glScissor");
        EnablePtr = (delegate* unmanaged[Cdecl]<uint, void>)Load("glEnable");
        DisablePtr = (delegate* unmanaged[Cdecl]<uint, void>)Load("glDisable");
        BlendFuncPtr = (delegate* unmanaged[Cdecl]<uint, uint, void>)Load("glBlendFunc");
        GenTexturesPtr = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glGenTextures");
        DeleteTexturesPtr = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glDeleteTextures");
        BindTexturePtr = (delegate* unmanaged[Cdecl]<uint, uint, void>)Load("glBindTexture");
        TexImage2DPtr = (delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void>)Load("glTexImage2D");
        TexParameteriPtr = (delegate* unmanaged[Cdecl]<uint, uint, int, void>)Load("glTexParameteri");
        ActiveTexturePtr = (delegate* unmanaged[Cdecl]<uint, void>)Load("glActiveTexture");
        ReadPixelsPtr = (delegate* unmanaged[Cdecl]<int, int, int, int, uint, uint, void*, void>)Load("glReadPixels");
        PixelStoreiPtr = (delegate* unmanaged[Cdecl]<uint, int, void>)Load("glPixelStorei");
        DrawArraysPtr = (delegate* unmanaged[Cdecl]<uint, int, int, void>)Load("glDrawArrays");
        GenBuffersPtr = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glGenBuffers");
        DeleteBuffersPtr = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glDeleteBuffers");
        BindBufferPtr = (delegate* unmanaged[Cdecl]<uint, uint, void>)Load("glBindBuffer");
        BufferDataPtr = (delegate* unmanaged[Cdecl]<uint, nint, void*, uint, void>)Load("glBufferData");
        GenVertexArraysPtr = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glGenVertexArrays");
        DeleteVertexArraysPtr = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glDeleteVertexArrays");
        BindVertexArrayPtr = (delegate* unmanaged[Cdecl]<uint, void>)Load("glBindVertexArray");
        VertexAttribPointerPtr = (delegate* unmanaged[Cdecl]<uint, int, uint, byte, int, void*, void>)Load("glVertexAttribPointer");
        EnableVertexAttribArrayPtr = (delegate* unmanaged[Cdecl]<uint, void>)Load("glEnableVertexAttribArray");
        CreateShaderPtr = (delegate* unmanaged[Cdecl]<uint, uint>)Load("glCreateShader");
        ShaderSourcePtr = (delegate* unmanaged[Cdecl]<uint, int, byte**, int*, void>)Load("glShaderSource");
        CompileShaderPtr = (delegate* unmanaged[Cdecl]<uint, void>)Load("glCompileShader");
        GetShaderivPtr = (delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Load("glGetShaderiv");
        GetShaderInfoLogPtr = (delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void>)Load("glGetShaderInfoLog");
        DeleteShaderPtr = (delegate* unmanaged[Cdecl]<uint, void>)Load("glDeleteShader");
        CreateProgramPtr = (delegate* unmanaged[Cdecl]<uint>)Load("glCreateProgram");
        AttachShaderPtr = (delegate* unmanaged[Cdecl]<uint, uint, void>)Load("glAttachShader");
        BindAttribLocationPtr = (delegate* unmanaged[Cdecl]<uint, uint, byte*, void>)Load("glBindAttribLocation");
        LinkProgramPtr = (delegate* unmanaged[Cdecl]<uint, void>)Load("glLinkProgram");
        GetProgramivPtr = (delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Load("glGetProgramiv");
        GetProgramInfoLogPtr = (delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void>)Load("glGetProgramInfoLog");
        UseProgramPtr = (delegate* unmanaged[Cdecl]<uint, void>)Load("glUseProgram");
        DeleteProgramPtr = (delegate* unmanaged[Cdecl]<uint, void>)Load("glDeleteProgram");
        GetUniformLocationPtr = (delegate* unmanaged[Cdecl]<uint, byte*, int>)Load("glGetUniformLocation");
        Uniform1iPtr = (delegate* unmanaged[Cdecl]<int, int, void>)Load("glUniform1i");
        Uniform2fPtr = (delegate* unmanaged[Cdecl]<int, float, float, void>)Load("glUniform2f");
        GenFramebuffersPtr = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glGenFramebuffers");
        DeleteFramebuffersPtr = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glDeleteFramebuffers");
        BindFramebufferPtr = (delegate* unmanaged[Cdecl]<uint, uint, void>)Load("glBindFramebuffer");
        FramebufferTexture2DPtr = (delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void>)Load("glFramebufferTexture2D");
        CheckFramebufferStatusPtr = (delegate* unmanaged[Cdecl]<uint, uint>)Load("glCheckFramebufferStatus");
        GenRenderbuffersPtr = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glGenRenderbuffers");
        DeleteRenderbuffersPtr = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glDeleteRenderbuffers");
        BindRenderbufferPtr = (delegate* unmanaged[Cdecl]<uint, uint, void>)Load("glBindRenderbuffer");
        RenderbufferStoragePtr = (delegate* unmanaged[Cdecl]<uint, uint, int, int, void>)Load("glRenderbufferStorage");
        FramebufferRenderbufferPtr = (delegate* unmanaged[Cdecl]<uint, uint, uint, uint, void>)Load("glFramebufferRenderbuffer");
        BlitFramebufferPtr = (delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, int, uint, uint, void>)Load("glBlitFramebuffer");
        GenSamplersPtr = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glGenSamplers");
        DeleteSamplersPtr = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glDeleteSamplers");
        BindSamplerPtr = (delegate* unmanaged[Cdecl]<uint, uint, void>)Load("glBindSampler");
        SamplerParameteriPtr = (delegate* unmanaged[Cdecl]<uint, uint, int, void>)Load("glSamplerParameteri");
        GenerateMipmapPtr = (delegate* unmanaged[Cdecl]<uint, void>)Load("glGenerateMipmap");
        DepthFuncPtr = (delegate* unmanaged[Cdecl]<uint, void>)Load("glDepthFunc");
        DepthMaskPtr = (delegate* unmanaged[Cdecl]<byte, void>)Load("glDepthMask");
        Uniform1fPtr = (delegate* unmanaged[Cdecl]<int, float, void>)Load("glUniform1f");
        Uniform3fPtr = (delegate* unmanaged[Cdecl]<int, float, float, float, void>)Load("glUniform3f");
        UniformMatrix4fvPtr = (delegate* unmanaged[Cdecl]<int, int, byte, float*, void>)Load("glUniformMatrix4fv");
        Uniform3fvPtr = (delegate* unmanaged[Cdecl]<int, int, float*, void>)Load("glUniform3fv");
        Uniform4fvPtr = (delegate* unmanaged[Cdecl]<int, int, float*, void>)Load("glUniform4fv");
    }

    /// <summary>Reads a GL string.</summary>
    /// <param name="name">The string name enumerant.</param>
    /// <returns>The string, empty when null.</returns>
    public string GetString(uint name)
    {
        var p = GetStringPtr(name);
        return p == null ? "" : Marshal.PtrToStringUTF8((IntPtr)p) ?? "";
    }

    /// <summary>Creates a texture.</summary>
    /// <returns>The texture name.</returns>
    public uint GenTexture()
    {
        uint t;
        GenTexturesPtr(1, &t);
        return t;
    }

    /// <summary>Deletes a texture.</summary>
    /// <param name="t">The texture name.</param>
    public void DeleteTexture(uint t) => DeleteTexturesPtr(1, &t);

    /// <summary>Creates a buffer.</summary>
    /// <returns>The buffer name.</returns>
    public uint GenBuffer()
    {
        uint b;
        GenBuffersPtr(1, &b);
        return b;
    }

    /// <summary>Creates a vertex array.</summary>
    /// <returns>The vertex array name.</returns>
    public uint GenVertexArray()
    {
        uint v;
        GenVertexArraysPtr(1, &v);
        return v;
    }

    /// <summary>Creates a framebuffer.</summary>
    /// <returns>The framebuffer name.</returns>
    public uint GenFramebuffer()
    {
        uint v;
        GenFramebuffersPtr(1, &v);
        return v;
    }

    /// <summary>Creates a renderbuffer.</summary>
    /// <returns>The renderbuffer name.</returns>
    public uint GenRenderbuffer()
    {
        uint v;
        GenRenderbuffersPtr(1, &v);
        return v;
    }

    /// <summary>Creates a sampler.</summary>
    /// <returns>The sampler name.</returns>
    public uint GenSampler()
    {
        uint v;
        GenSamplersPtr(1, &v);
        return v;
    }

    /// <summary>Gets the location of a uniform.</summary>
    /// <param name="program">The program.</param>
    /// <param name="name">The uniform name.</param>
    /// <returns>The location, -1 when absent.</returns>
    public int UniformLocation(uint program, string name)
    {
        var n = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = n)
            return GetUniformLocationPtr(program, p);
    }

    /// <summary>Compiles and links a program.</summary>
    /// <param name="vertexSource">The vertex shader source.</param>
    /// <param name="fragmentSource">The fragment shader source.</param>
    /// <param name="attributes">The attribute locations to bind.</param>
    /// <returns>The program.</returns>
    /// <exception cref="InvalidOperationException">Compiling or linking fails. The message holds the info log.</exception>
    public uint BuildProgram(string vertexSource, string fragmentSource, params (uint index, string name)[] attributes)
    {
        var vs = Compile(VertexShader, vertexSource);
        var fs = Compile(FragmentShader, fragmentSource);
        var prog = CreateProgramPtr();
        AttachShaderPtr(prog, vs);
        AttachShaderPtr(prog, fs);
        foreach (var (index, name) in attributes)
        {
            var n = Encoding.ASCII.GetBytes(name + "\0");
            fixed (byte* p = n)
                BindAttribLocationPtr(prog, index, p);
        }
        LinkProgramPtr(prog);
        int ok;
        GetProgramivPtr(prog, LinkStatus, &ok);
        if (ok == 0)
        {
            int len;
            GetProgramivPtr(prog, InfoLogLength, &len);
            var log = new byte[Math.Max(len, 1)];
            fixed (byte* lp = log)
                GetProgramInfoLogPtr(prog, log.Length, null, lp);
            throw new InvalidOperationException("GL link failed: " + Encoding.UTF8.GetString(log).TrimEnd('\0'));
        }
        DeleteShaderPtr(vs);
        DeleteShaderPtr(fs);
        return prog;
    }

    private uint Compile(uint type, string source)
    {
        var sh = CreateShaderPtr(type);
        var src = Encoding.UTF8.GetBytes(source + "\0");
        fixed (byte* sp = src)
        {
            var s = sp;
            ShaderSourcePtr(sh, 1, &s, null);
        }
        CompileShaderPtr(sh);
        int ok;
        GetShaderivPtr(sh, CompileStatus, &ok);
        if (ok == 0)
        {
            int len;
            GetShaderivPtr(sh, InfoLogLength, &len);
            var log = new byte[Math.Max(len, 1)];
            fixed (byte* lp = log)
                GetShaderInfoLogPtr(sh, log.Length, null, lp);
            throw new InvalidOperationException("GL compile failed: " + Encoding.UTF8.GetString(log).TrimEnd('\0'));
        }
        return sh;
    }
}
