namespace OpenTribes.Server;

using OpenTribes.Core;

/// <summary>The stdio streams of the C++ server. Byte strings go out as their bytes. Stdout is line buffered (<c>setvbuf _IOLBF</c> in src/server/main.cpp).</summary>
public static class StdIo
{
    private static readonly Stream sOut = Console.OpenStandardOutput();
    private static readonly Stream sErr = Console.OpenStandardError();
    private static readonly Lock Lock = new();

    private static string CStr(string s)
    {
        var nul = s.IndexOf('\0');
        return nul < 0 ? s : s[..nul];
    }

    /// <summary>Writes a line like <c>printf("%s\n", line)</c>, which stops at the first NUL.</summary>
    /// <param name="line">The line.</param>
    public static void Out(string line) => Write(CStr(line) + "\n");

    /// <summary>Writes preformatted text and flushes at its line ends.</summary>
    /// <param name="text">The text.</param>
    public static void Write(string text)
    {
        lock (Lock)
        {
            sOut.Write(ByteString.ToBytes(text));
            if (text.Contains('\n'))
                sOut.Flush();
        }
    }

    /// <summary>Flushes stdout.</summary>
    public static void Flush()
    {
        lock (Lock)
            sOut.Flush();
    }

    /// <summary>Writes unbuffered text to stderr.</summary>
    /// <param name="text">The text.</param>
    public static void Err(string text)
    {
        sErr.Write(ByteString.ToBytes(text));
        sErr.Flush();
    }
}
