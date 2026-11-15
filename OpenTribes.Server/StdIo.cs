namespace OpenTribes.Server;

using OpenTribes.Core;

/// <summary>The stdio streams of the C++ server. Byte strings go out as their bytes. Stdout is line buffered (<c>setvbuf _IOLBF</c> in src/server/main.cpp).</summary>
public static class StdIo
{
    private static readonly Stream OutWriter = Console.OpenStandardOutput();
    private static readonly Stream ErrWriter = Console.OpenStandardError();
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
            TryWrite(OutWriter, ByteString.ToBytes(text), text.Contains('\n'));
    }

    /// <summary>Flushes stdout.</summary>
    public static void Flush()
    {
        lock (Lock)
            TryWrite(OutWriter, [], true);
    }

    /// <summary>Writes unbuffered text to stderr.</summary>
    /// <param name="text">The text.</param>
    public static void Err(string text) => TryWrite(ErrWriter, ByteString.ToBytes(text), true);

    /// <summary>Writes bytes to a stream and flushes it when asked. An IO error drops the text.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="flush">True to flush after the write.</param>
    /// <returns>False when the write or the flush failed.</returns>
    /// <remarks>Like printf and fwrite, whose result the C++ server ignores, a closed pipe or a full disk loses the text and the server keeps running.</remarks>
    public static bool TryWrite(Stream stream, ReadOnlySpan<byte> bytes, bool flush)
    {
        try
        {
            stream.Write(bytes);
            if (flush)
                stream.Flush();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
