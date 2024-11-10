// Port convention (no C++ counterpart): the C++ code keeps names and script text as byte strings
// (std::string, const char*). The port holds them in System.String with one char per byte (0-255),
// so comparisons, case folding and wire bytes stay identical. Host file names are UTF-8 on disk.
using System.Text;

namespace OpenTribes.Core;

public static class ByteString
{
    // Byte string from raw bytes.
    public static string FromBytes(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    // Raw bytes of a byte string (chars above 0xff do not occur in byte strings).
    public static byte[] ToBytes(string s) => Encoding.Latin1.GetBytes(s);

    // Byte string of a host (UTF-16) file name: its UTF-8 bytes, as the C++ readdir sees them.
    public static string FromHost(string hostName) => FromBytes(Encoding.UTF8.GetBytes(hostName));

    // Host (UTF-16) form of a byte string that holds UTF-8 bytes, for System.IO calls.
    public static string ToHost(string byteString) => Encoding.UTF8.GetString(ToBytes(byteString));
}
