// Port convention (no C++ file): the stdio streams of the C++ server. Byte strings go out as their bytes; stdout is
// line buffered (setvbuf _IOLBF in src/server/main.cpp), printf("%s\n") stops at the first NUL.
using OpenTribes.Core;

namespace OpenTribes.Server;

public static class StdIo
{
    private static readonly Stream sOut = Console.OpenStandardOutput();
    private static readonly Stream sErr = Console.OpenStandardError();
    private static readonly object sLock = new();

    private static string CStr(string s)
    {
        int nul = s.IndexOf('\0');
        return nul < 0 ? s : s[..nul];
    }

    // printf("%s\n", line) with a line-buffered stdout.
    public static void Out(string line) => Write(CStr(line) + "\n");

    // printf of preformatted text: written, flushed at its line ends.
    public static void Write(string text)
    {
        lock (sLock)
        {
            sOut.Write(ByteString.ToBytes(text));
            if (text.Contains('\n'))
                sOut.Flush();
        }
    }

    // fflush(stdout).
    public static void Flush()
    {
        lock (sLock)
            sOut.Flush();
    }

    // fprintf(stderr, ...): unbuffered.
    public static void Err(string text)
    {
        sErr.Write(ByteString.ToBytes(text));
        sErr.Flush();
    }
}
