using System.Runtime.InteropServices;
using System.Text;

namespace OpenTribes.Platform.Gl;

// OpenGL 3.3 core function loader through SDL_GL_GetProcAddress (D30). Only the calls the renderer uses.
public sealed unsafe class GlApi
{
    // Enumerants
    public const uint DepthBufferBit = 0x100, ColorBufferBit = 0x4000;
    public const uint Lines = 1, Triangles = 4;
    public const uint ZERO = 0, ONE = 1, SrcColor = 0x300, OneMinusSrcColor = 0x301, SrcAlpha = 0x302, OneMinusSrcAlpha = 0x303;
    public const uint Texture2D = 0x0DE1, Blend = 0x0BE2, ScissorTest = 0x0C11, DepthTest = 0x0B71, CullFace = 0x0B44;
    public const uint Rgba = 0x1908, Rgb = 0x1907, UnsignedByte = 0x1401, Float = 0x1406, Rgba8 = 0x8058;
    public const uint TextureMinFilter = 0x2801, TextureMagFilter = 0x2800, TextureWrapS = 0x2802, TextureWrapT = 0x2803;
    public const uint Nearest = 0x2600, Linear = 0x2601, ClampToEdge = 0x812F;
    public const uint ArrayBuffer = 0x8892, StreamDraw = 0x88E0;
    public const uint VertexShader = 0x8B31, FragmentShader = 0x8B30, CompileStatus = 0x8B81, LinkStatus = 0x8B82,
        InfoLogLength = 0x8B84;
    public const uint Framebuffer = 0x8D40, ReadFramebuffer = 0x8CA8, DrawFramebuffer = 0x8CA9, Renderbuffer = 0x8D41,
        ColorAttachment0 = 0x8CE0, DepthAttachment = 0x8D00, FramebufferComplete = 0x8CD5, DepthComponent24 = 0x81A6;
    public const uint Version = 0x1F02, Renderer = 0x1F01, Vendor = 0x1F00, UnpackAlignment = 0x0CF5, PackAlignment = 0x0D05;
    public const uint Texture0 = 0x84C0;

    private readonly Func<string, IntPtr> mLoader;

    private IntPtr Load(string name)
    {
        IntPtr p = mLoader(name);
        if (p == IntPtr.Zero)
            throw new InvalidOperationException("OpenGL function missing: " + name);
        return p;
    }

    public readonly delegate* unmanaged[Cdecl]<uint> GetError_;
    public readonly delegate* unmanaged[Cdecl]<uint, byte*> GetString_;
    public readonly delegate* unmanaged[Cdecl]<float, float, float, float, void> ClearColor_;
    public readonly delegate* unmanaged[Cdecl]<uint, void> Clear_;
    public readonly delegate* unmanaged[Cdecl]<int, int, int, int, void> Viewport_;
    public readonly delegate* unmanaged[Cdecl]<int, int, int, int, void> Scissor_;
    public readonly delegate* unmanaged[Cdecl]<uint, void> Enable_;
    public readonly delegate* unmanaged[Cdecl]<uint, void> Disable_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, void> BlendFunc_;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> GenTextures_;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> DeleteTextures_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, void> BindTexture_;
    public readonly delegate* unmanaged[Cdecl]<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, int, void> TexParameteri_;
    public readonly delegate* unmanaged[Cdecl]<uint, void> ActiveTexture_;
    public readonly delegate* unmanaged[Cdecl]<int, int, int, int, uint, uint, void*, void> ReadPixels_;
    public readonly delegate* unmanaged[Cdecl]<uint, int, void> PixelStorei_;
    public readonly delegate* unmanaged[Cdecl]<uint, int, int, void> DrawArrays_;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> GenBuffers_;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> DeleteBuffers_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, void> BindBuffer_;
    public readonly delegate* unmanaged[Cdecl]<uint, nint, void*, uint, void> BufferData_;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> GenVertexArrays_;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> DeleteVertexArrays_;
    public readonly delegate* unmanaged[Cdecl]<uint, void> BindVertexArray_;
    public readonly delegate* unmanaged[Cdecl]<uint, int, uint, byte, int, void*, void> VertexAttribPointer_;
    public readonly delegate* unmanaged[Cdecl]<uint, void> EnableVertexAttribArray_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint> CreateShader_;
    public readonly delegate* unmanaged[Cdecl]<uint, int, byte**, int*, void> ShaderSource_;
    public readonly delegate* unmanaged[Cdecl]<uint, void> CompileShader_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, int*, void> GetShaderiv_;
    public readonly delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void> GetShaderInfoLog_;
    public readonly delegate* unmanaged[Cdecl]<uint, void> DeleteShader_;
    public readonly delegate* unmanaged[Cdecl]<uint> CreateProgram_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, void> AttachShader_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, byte*, void> BindAttribLocation_;
    public readonly delegate* unmanaged[Cdecl]<uint, void> LinkProgram_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, int*, void> GetProgramiv_;
    public readonly delegate* unmanaged[Cdecl]<uint, int, int*, byte*, void> GetProgramInfoLog_;
    public readonly delegate* unmanaged[Cdecl]<uint, void> UseProgram_;
    public readonly delegate* unmanaged[Cdecl]<uint, void> DeleteProgram_;
    public readonly delegate* unmanaged[Cdecl]<uint, byte*, int> GetUniformLocation_;
    public readonly delegate* unmanaged[Cdecl]<int, int, void> Uniform1i_;
    public readonly delegate* unmanaged[Cdecl]<int, float, float, void> Uniform2f_;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> GenFramebuffers_;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> DeleteFramebuffers_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, void> BindFramebuffer_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, uint, uint, int, void> FramebufferTexture2D_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint> CheckFramebufferStatus_;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> GenRenderbuffers_;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> DeleteRenderbuffers_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, void> BindRenderbuffer_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, int, int, void> RenderbufferStorage_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, uint, uint, void> FramebufferRenderbuffer_;
    public readonly delegate* unmanaged[Cdecl]<int, int, int, int, int, int, int, int, uint, uint, void> BlitFramebuffer_;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> GenSamplers_;
    public readonly delegate* unmanaged[Cdecl]<int, uint*, void> DeleteSamplers_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, void> BindSampler_;
    public readonly delegate* unmanaged[Cdecl]<uint, uint, int, void> SamplerParameteri_;

    // The loader returns the address of a GL function, or zero (SDL_GL_GetProcAddress).
    public GlApi(Func<string, IntPtr> loader)
    {
        mLoader = loader;
        GetError_ = (delegate* unmanaged[Cdecl]<uint>)Load("glGetError");
        GetString_ = (delegate* unmanaged[Cdecl]<uint, byte*>)Load("glGetString");
        ClearColor_ = (delegate* unmanaged[Cdecl]<float, float, float, float, void>)Load("glClearColor");
        Clear_ = (delegate* unmanaged[Cdecl]<uint, void>)Load("glClear");
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
    }

    public string GetString(uint name)
    {
        byte* p = GetString_(name);
        return p == null ? "" : Marshal.PtrToStringUTF8((IntPtr)p) ?? "";
    }

    public uint GenTexture()
    {
        uint t;
        GenTextures_(1, &t);
        return t;
    }

    public void DeleteTexture(uint t) => DeleteTextures_(1, &t);

    public uint GenBuffer()
    {
        uint b;
        GenBuffers_(1, &b);
        return b;
    }

    public uint GenVertexArray()
    {
        uint v;
        GenVertexArrays_(1, &v);
        return v;
    }

    public uint GenFramebuffer()
    {
        uint v;
        GenFramebuffers_(1, &v);
        return v;
    }

    public uint GenRenderbuffer()
    {
        uint v;
        GenRenderbuffers_(1, &v);
        return v;
    }

    public uint GenSampler()
    {
        uint v;
        GenSamplers_(1, &v);
        return v;
    }

    public int UniformLocation(uint program, string name)
    {
        byte[] n = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = n)
            return GetUniformLocation_(program, p);
    }

    // Compiles and links a program; throws with the info log on failure.
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
