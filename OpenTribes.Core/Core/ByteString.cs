using System.Text;

namespace OpenTribes.Core;

/// <summary>Conversions between byte strings and raw bytes or host file names.</summary>
/// <remarks>The C++ code keeps names and script text as byte strings. The port holds them in <see cref="string"/> with one char per byte (0-255), so comparisons, case folding and wire bytes stay identical. Host file names are UTF-8 on disk.</remarks>
public static class ByteString
{
    /// <summary>Converts raw bytes to a byte string.</summary>
    /// <param name="bytes">The raw bytes.</param>
    /// <returns>A string with one char per byte.</returns>
    public static string FromBytes(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    /// <summary>Converts a byte string to raw bytes. Chars above 0xff do not occur in byte strings.</summary>
    /// <param name="s">The byte string.</param>
    /// <returns>The raw bytes.</returns>
    public static byte[] ToBytes(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>Converts a host (UTF-16) file name to a byte string holding its UTF-8 bytes, as the C++ <c>readdir</c> sees it.</summary>
    /// <param name="hostName">The host file name.</param>
    /// <returns>The byte string.</returns>
    public static string FromHost(string hostName) => FromBytes(Encoding.UTF8.GetBytes(hostName));

    /// <summary>Converts a byte string that holds UTF-8 bytes to a host (UTF-16) file name for <c>System.IO</c> calls.</summary>
    /// <param name="byteString">The byte string.</param>
    /// <returns>The host file name.</returns>
    public static string ToHost(string byteString) => Encoding.UTF8.GetString(ToBytes(byteString));
}
