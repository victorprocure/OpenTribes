using System.Runtime.InteropServices;
using System.Text;

namespace OpenTribes.Platform.Gl;

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
    public const uint Rgba = 0x1908, Rgb = 0x1907, UnsignedByte = 0x1401, Float = 0x1406, Rgba8 = 0x8058;
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

    private readonly Func<string, IntPtr> mLoader;

    private IntPtr Load(string name)
    {
        IntPtr p = mLoader(name);
        if (p == IntPtr.Zero)
            throw new InvalidOperationException("OpenGL function missing: " + name);
        return p;
    }

    /// <summary>The glGetError entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint> GetError_;
    /// <summary>The glGetString entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, byte*> GetString_;
    /// <summary>The glClearColor entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<float, float, float, float, void> ClearColor_;
    /// <summary>The glClear entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, void> Clear_;
    /// <summary>The glClearDepth entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<double, void> ClearDepth_;
    /// <summary>The glViewport entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, int, int, int, void> Viewport_;
    /// <summary>The glScissor entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, int, int, int, void> Scissor_;
    /// <summary>The glEnable entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, void> Enable_;
    /// <summary>The glDisable entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, void> Disable_;
    /// <summary>The glBlendFunc entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, void> BlendFunc_;
    /// <summary>The glGenTextures entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> GenTextures_;
    /// <summary>The glDeleteTextures entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> DeleteTextures_;
    /// <summary>The glBindTexture entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, void> BindTexture_;
    /// <summary>The glTexImage2D entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D_;
    /// <summary>The glTexParameteri entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, int, void> TexParameteri_;
    /// <summary>The glActiveTexture entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, void> ActiveTexture_;
    /// <summary>The glReadPixels entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, int, int, int, uint, uint, void*, void> ReadPixels_;
    /// <summary>The glPixelStorei entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, int, void> PixelStorei_;
    /// <summary>The glDrawArrays entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, int, int, void> DrawArrays_;
    /// <summary>The glGenBuffers entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> GenBuffers_;
    /// <summary>The glDeleteBuffers entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> DeleteBuffers_;
    /// <summary>The glBindBuffer entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, void> BindBuffer_;
    /// <summary>The glBufferData entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, nint, void*, uint, void> BufferData_;
    /// <summary>The glGenVertexArrays entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> GenVertexArrays_;
    /// <summary>The glDeleteVertexArrays entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> DeleteVertexArrays_;
    /// <summary>The glBindVertexArray entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, void> BindVertexArray_;
    /// <summary>The glVertexAttribPointer entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, int, uint, byte, int, void*, void> VertexAttribPointer_;
    /// <summary>The glEnableVertexAttribArray entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, void> EnableVertexAttribArray_;
    /// <summary>The glCreateShader entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint> CreateShader_;
    /// <summary>The glShaderSource entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, int, byte**, int*, void> ShaderSource_;
    /// <summary>The glCompileShader entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, void> CompileShader_;
    /// <summary>The glGetShaderiv entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, int*, void> GetShaderiv_;
    /// <summary>The glGetShaderInfoLog entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void> GetShaderInfoLog_;
    /// <summary>The glDeleteShader entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, void> DeleteShader_;
    /// <summary>The glCreateProgram entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint> CreateProgram_;
    /// <summary>The glAttachShader entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, void> AttachShader_;
    /// <summary>The glBindAttribLocation entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, byte*, void> BindAttribLocation_;
    /// <summary>The glLinkProgram entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, void> LinkProgram_;
    /// <summary>The glGetProgramiv entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, int*, void> GetProgramiv_;
    /// <summary>The glGetProgramInfoLog entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void> GetProgramInfoLog_;
    /// <summary>The glUseProgram entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, void> UseProgram_;
    /// <summary>The glDeleteProgram entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, void> DeleteProgram_;
    /// <summary>The glGetUniformLocation entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, byte*, int> GetUniformLocation_;
    /// <summary>The glUniform1i entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, int, void> Uniform1i_;
    /// <summary>The glUniform2f entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, float, float, void> Uniform2f_;
    /// <summary>The glGenFramebuffers entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> GenFramebuffers_;
    /// <summary>The glDeleteFramebuffers entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> DeleteFramebuffers_;
    /// <summary>The glBindFramebuffer entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, void> BindFramebuffer_;
    /// <summary>The glFramebufferTexture2D entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void> FramebufferTexture2D_;
    /// <summary>The glCheckFramebufferStatus entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint> CheckFramebufferStatus_;
    /// <summary>The glGenRenderbuffers entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> GenRenderbuffers_;
    /// <summary>The glDeleteRenderbuffers entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> DeleteRenderbuffers_;
    /// <summary>The glBindRenderbuffer entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, void> BindRenderbuffer_;
    /// <summary>The glRenderbufferStorage entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, int, int, void> RenderbufferStorage_;
    /// <summary>The glFramebufferRenderbuffer entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, uint, uint, void> FramebufferRenderbuffer_;
    /// <summary>The glBlitFramebuffer entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, int, uint, uint, void> BlitFramebuffer_;
    /// <summary>The glGenSamplers entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> GenSamplers_;
    /// <summary>The glDeleteSamplers entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> DeleteSamplers_;
    /// <summary>The glBindSampler entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, void> BindSampler_;
    /// <summary>The glSamplerParameteri entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, uint, int, void> SamplerParameteri_;
    /// <summary>The glGenerateMipmap entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, void> GenerateMipmap_;
    /// <summary>The glDepthFunc entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<uint, void> DepthFunc_;
    /// <summary>The glDepthMask entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<byte, void> DepthMask_;
    /// <summary>The glUniform1f entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, float, void> Uniform1f_;
    /// <summary>The glUniform3f entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, float, float, float, void> Uniform3f_;
    /// <summary>The glUniformMatrix4fv entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, int, byte, float*, void> UniformMatrix4fv_;
    /// <summary>The glUniform3fv entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, int, float*, void> Uniform3fv_;
    /// <summary>The glUniform4fv entry point.</summary>
    public readonly delegate* unmanaged[Cdecl]<int, int, float*, void> Uniform4fv_;

    /// <summary>Loads every GL function the renderer uses.</summary>
    /// <param name="loader">Returns the address of a GL function, or zero (SDL_GL_GetProcAddress).</param>
    /// <exception cref="InvalidOperationException">A function is missing.</exception>
    public GlApi(Func<string, IntPtr> loader)
    {
        mLoader = loader;
        GetError_ = (delegate* unmanaged[Cdecl]<uint>)Load("glGetError");
        GetString_ = (delegate* unmanaged[Cdecl]<uint, byte*>)Load("glGetString");
        ClearColor_ = (delegate* unmanaged[Cdecl]<float, float, float, float, void>)Load("glClearColor");
        Clear_ = (delegate* unmanaged[Cdecl]<uint, void>)Load("glClear");
        ClearDepth_ = (delegate* unmanaged[Cdecl]<double, void>)Load("glClearDepth");
        Viewport_ = (delegate* unmanaged[Cdecl]<int, int, int, int, void>)Load("glViewport");
        Scissor_ = (delegate* unmanaged[Cdecl]<int, int, int, int, void>)Load("glScissor");
        Enable_ = (delegate* unmanaged[Cdecl]<uint, void>)Load("glEnable");
        Disable_ = (delegate* unmanaged[Cdecl]<uint, void>)Load("glDisable");
        BlendFunc_ = (delegate* unmanaged[Cdecl]<uint, uint, void>)Load("glBlendFunc");
        GenTextures_ = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glGenTextures");
        DeleteTextures_ = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glDeleteTextures");
        BindTexture_ = (delegate* unmanaged[Cdecl]<uint, uint, void>)Load("glBindTexture");
        TexImage2D_ = (delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void>)Load("glTexImage2D");
        TexParameteri_ = (delegate* unmanaged[Cdecl]<uint, uint, int, void>)Load("glTexParameteri");
        ActiveTexture_ = (delegate* unmanaged[Cdecl]<uint, void>)Load("glActiveTexture");
        ReadPixels_ = (delegate* unmanaged[Cdecl]<int, int, int, int, uint, uint, void*, void>)Load("glReadPixels");
        PixelStorei_ = (delegate* unmanaged[Cdecl]<uint, int, void>)Load("glPixelStorei");
        DrawArrays_ = (delegate* unmanaged[Cdecl]<uint, int, int, void>)Load("glDrawArrays");
        GenBuffers_ = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glGenBuffers");
        DeleteBuffers_ = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glDeleteBuffers");
        BindBuffer_ = (delegate* unmanaged[Cdecl]<uint, uint, void>)Load("glBindBuffer");
        BufferData_ = (delegate* unmanaged[Cdecl]<uint, nint, void*, uint, void>)Load("glBufferData");
        GenVertexArrays_ = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glGenVertexArrays");
        DeleteVertexArrays_ = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glDeleteVertexArrays");
        BindVertexArray_ = (delegate* unmanaged[Cdecl]<uint, void>)Load("glBindVertexArray");
        VertexAttribPointer_ = (delegate* unmanaged[Cdecl]<uint, int, uint, byte, int, void*, void>)Load("glVertexAttribPointer");
        EnableVertexAttribArray_ = (delegate* unmanaged[Cdecl]<uint, void>)Load("glEnableVertexAttribArray");
        CreateShader_ = (delegate* unmanaged[Cdecl]<uint, uint>)Load("glCreateShader");
        ShaderSource_ = (delegate* unmanaged[Cdecl]<uint, int, byte**, int*, void>)Load("glShaderSource");
        CompileShader_ = (delegate* unmanaged[Cdecl]<uint, void>)Load("glCompileShader");
        GetShaderiv_ = (delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Load("glGetShaderiv");
        GetShaderInfoLog_ = (delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void>)Load("glGetShaderInfoLog");
        DeleteShader_ = (delegate* unmanaged[Cdecl]<uint, void>)Load("glDeleteShader");
        CreateProgram_ = (delegate* unmanaged[Cdecl]<uint>)Load("glCreateProgram");
        AttachShader_ = (delegate* unmanaged[Cdecl]<uint, uint, void>)Load("glAttachShader");
        BindAttribLocation_ = (delegate* unmanaged[Cdecl]<uint, uint, byte*, void>)Load("glBindAttribLocation");
        LinkProgram_ = (delegate* unmanaged[Cdecl]<uint, void>)Load("glLinkProgram");
        GetProgramiv_ = (delegate* unmanaged[Cdecl]<uint, uint, int*, void>)Load("glGetProgramiv");
        GetProgramInfoLog_ = (delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void>)Load("glGetProgramInfoLog");
        UseProgram_ = (delegate* unmanaged[Cdecl]<uint, void>)Load("glUseProgram");
        DeleteProgram_ = (delegate* unmanaged[Cdecl]<uint, void>)Load("glDeleteProgram");
        GetUniformLocation_ = (delegate* unmanaged[Cdecl]<uint, byte*, int>)Load("glGetUniformLocation");
        Uniform1i_ = (delegate* unmanaged[Cdecl]<int, int, void>)Load("glUniform1i");
        Uniform2f_ = (delegate* unmanaged[Cdecl]<int, float, float, void>)Load("glUniform2f");
        GenFramebuffers_ = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glGenFramebuffers");
        DeleteFramebuffers_ = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glDeleteFramebuffers");
        BindFramebuffer_ = (delegate* unmanaged[Cdecl]<uint, uint, void>)Load("glBindFramebuffer");
        FramebufferTexture2D_ = (delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void>)Load("glFramebufferTexture2D");
        CheckFramebufferStatus_ = (delegate* unmanaged[Cdecl]<uint, uint>)Load("glCheckFramebufferStatus");
        GenRenderbuffers_ = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glGenRenderbuffers");
        DeleteRenderbuffers_ = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glDeleteRenderbuffers");
        BindRenderbuffer_ = (delegate* unmanaged[Cdecl]<uint, uint, void>)Load("glBindRenderbuffer");
        RenderbufferStorage_ = (delegate* unmanaged[Cdecl]<uint, uint, int, int, void>)Load("glRenderbufferStorage");
        FramebufferRenderbuffer_ = (delegate* unmanaged[Cdecl]<uint, uint, uint, uint, void>)Load("glFramebufferRenderbuffer");
        BlitFramebuffer_ = (delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, int, uint, uint, void>)Load("glBlitFramebuffer");
        GenSamplers_ = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glGenSamplers");
        DeleteSamplers_ = (delegate* unmanaged[Cdecl]<int, uint*, void>)Load("glDeleteSamplers");
        BindSampler_ = (delegate* unmanaged[Cdecl]<uint, uint, void>)Load("glBindSampler");
        SamplerParameteri_ = (delegate* unmanaged[Cdecl]<uint, uint, int, void>)Load("glSamplerParameteri");
        GenerateMipmap_ = (delegate* unmanaged[Cdecl]<uint, void>)Load("glGenerateMipmap");
        DepthFunc_ = (delegate* unmanaged[Cdecl]<uint, void>)Load("glDepthFunc");
        DepthMask_ = (delegate* unmanaged[Cdecl]<byte, void>)Load("glDepthMask");
        Uniform1f_ = (delegate* unmanaged[Cdecl]<int, float, void>)Load("glUniform1f");
        Uniform3f_ = (delegate* unmanaged[Cdecl]<int, float, float, float, void>)Load("glUniform3f");
        UniformMatrix4fv_ = (delegate* unmanaged[Cdecl]<int, int, byte, float*, void>)Load("glUniformMatrix4fv");
        Uniform3fv_ = (delegate* unmanaged[Cdecl]<int, int, float*, void>)Load("glUniform3fv");
        Uniform4fv_ = (delegate* unmanaged[Cdecl]<int, int, float*, void>)Load("glUniform4fv");
    }

    /// <summary>Reads a GL string.</summary>
    /// <param name="name">The string name enumerant.</param>
    /// <returns>The string, empty when null.</returns>
    public string GetString(uint name)
    {
        byte* p = GetString_(name);
        return p == null ? "" : Marshal.PtrToStringUTF8((IntPtr)p) ?? "";
    }

    /// <summary>Creates a texture.</summary>
    /// <returns>The texture name.</returns>
    public uint GenTexture()
    {
        uint t;
        GenTextures_(1, &t);
        return t;
    }

    /// <summary>Deletes a texture.</summary>
    /// <param name="t">The texture name.</param>
    public void DeleteTexture(uint t) => DeleteTextures_(1, &t);

    /// <summary>Creates a buffer.</summary>
    /// <returns>The buffer name.</returns>
    public uint GenBuffer()
    {
        uint b;
        GenBuffers_(1, &b);
        return b;
    }

    /// <summary>Creates a vertex array.</summary>
    /// <returns>The vertex array name.</returns>
    public uint GenVertexArray()
    {
        uint v;
        GenVertexArrays_(1, &v);
        return v;
    }

    /// <summary>Creates a framebuffer.</summary>
    /// <returns>The framebuffer name.</returns>
    public uint GenFramebuffer()
    {
        uint v;
        GenFramebuffers_(1, &v);
        return v;
    }

    /// <summary>Creates a renderbuffer.</summary>
    /// <returns>The renderbuffer name.</returns>
    public uint GenRenderbuffer()
    {
        uint v;
        GenRenderbuffers_(1, &v);
        return v;
    }

    /// <summary>Creates a sampler.</summary>
    /// <returns>The sampler name.</returns>
    public uint GenSampler()
    {
        uint v;
        GenSamplers_(1, &v);
        return v;
    }

    /// <summary>Gets the location of a uniform.</summary>
    /// <param name="program">The program.</param>
    /// <param name="name">The uniform name.</param>
    /// <returns>The location, -1 when absent.</returns>
    public int UniformLocation(uint program, string name)
    {
        byte[] n = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = n)
            return GetUniformLocation_(program, p);
    }

    /// <summary>Compiles and links a program.</summary>
    /// <param name="vertexSource">The vertex shader source.</param>
    /// <param name="fragmentSource">The fragment shader source.</param>
    /// <param name="attributes">The attribute locations to bind.</param>
    /// <returns>The program.</returns>
    /// <exception cref="InvalidOperationException">Compiling or linking fails. The message holds the info log.</exception>
    public uint BuildProgram(string vertexSource, string fragmentSource, params (uint index, string name)[] attributes)
    {
        uint vs = Compile(VertexShader, vertexSource);
        uint fs = Compile(FragmentShader, fragmentSource);
        uint prog = CreateProgram_();
        AttachShader_(prog, vs);
        AttachShader_(prog, fs);
        foreach (var (index, name) in attributes)
        {
            byte[] n = Encoding.ASCII.GetBytes(name + "\0");
            fixed (byte* p = n)
                BindAttribLocation_(prog, index, p);
        }
        LinkProgram_(prog);
        int ok;
        GetProgramiv_(prog, LinkStatus, &ok);
        if (ok == 0)
        {
            int len;
            GetProgramiv_(prog, InfoLogLength, &len);
            var log = new byte[Math.Max(len, 1)];
            fixed (byte* lp = log)
                GetProgramInfoLog_(prog, log.Length, null, lp);
            throw new InvalidOperationException("GL link failed: " + Encoding.UTF8.GetString(log).TrimEnd('\0'));
        }
        DeleteShader_(vs);
        DeleteShader_(fs);
        return prog;
    }

    private uint Compile(uint type, string source)
    {
        uint sh = CreateShader_(type);
        byte[] src = Encoding.UTF8.GetBytes(source + "\0");
        fixed (byte* sp = src)
        {
            byte* s = sp;
            ShaderSource_(sh, 1, &s, null);
        }
        CompileShader_(sh);
        int ok;
        GetShaderiv_(sh, CompileStatus, &ok);
        if (ok == 0)
        {
            int len;
            GetShaderiv_(sh, InfoLogLength, &len);
            var log = new byte[Math.Max(len, 1)];
            fixed (byte* lp = log)
                GetShaderInfoLog_(sh, log.Length, null, lp);
            throw new InvalidOperationException("GL compile failed: " + Encoding.UTF8.GetString(log).TrimEnd('\0'));
        }
        return sh;
    }
}
