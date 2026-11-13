namespace OpenTribes.Core;

/// <summary>The compression type stored in a volume directory record.</summary>
public enum VolCompression : byte
{
    /// <summary>Stored uncompressed.</summary>
    None = 0,
    /// <summary>Run-length encoded.</summary>
    Rle = 1,
    /// <summary>Adaptive-Huffman LZSS, see <see cref="Lzh"/>.</summary>
    Lzh = 3,
}

/// <summary>One directory record of a PVOL archive.</summary>
public sealed class VolEntry
{
    /// <summary>The numeric resource id, 0 when unused.</summary>
    public uint Id;
    /// <summary>The entry name. Empty when the entry has no name (name offset -1).</summary>
    public string Name = "";      // empty when the entry has no name (name offset -1)
    /// <summary>The file offset of the entry's VBLK header.</summary>
    public uint BlockOffset;
    /// <summary>The size from the directory record.</summary>
    public uint Size;
    /// <summary>The compression type, a <see cref="VolCompression"/> value.</summary>
    public byte Compression;
}

/// <summary>Reader for a PVOL archive: opens the directory, finds entries by name and decompresses their payloads.</summary>
/// <remarks>Port of src/core/vol.cpp.</remarks>
public sealed class VolArchive : IDisposable
{
    private const uint kTagPVOL = 0x4c4f5650; // "PVOL"
    private const uint kTagVols = 0x736c6f76; // "vols" string table
    private const uint kTagVoli = 0x696c6f76; // "voli" directory
    private const uint kTagVBLK = 0x4b4c4256; // "VBLK" entry payload
    private const uint kEntrySize = 17;
    private const uint kSizeMask = 0x7fffffff;
    private const uint kAlign4Flag = 0x80000000;

    private FileStream? mFile;
    private readonly List<VolEntry> mEntries = new();
    private readonly List<int> mNameIndex = new(); // indices into mEntries, sorted by name
    private string mError = "";

    // src/core/vol.cpp readU32.
    private static uint ReadU32(ReadOnlySpan<byte> p) =>
        p[0] | (uint)p[1] << 8 | (uint)p[2] << 16 | (uint)p[3] << 24;

    // src/core/vol.cpp paddedSize: block payloads are padded to 4 bytes when the size's high bit is set, else to 2.
    private static uint PaddedSize(uint rawSize)
    {
        uint size = rawSize & kSizeMask;
        return (rawSize & kAlign4Flag) != 0 ? (size + 3) & ~3u : (size + 1) & ~1u;
    }

    // src/core/vol.cpp upperAscii (on the signed char of the C++ build).
    private static sbyte UpperAscii(sbyte c) => (c > '`' && c < '{') ? (sbyte)(c - 0x20) : c;

    private static sbyte CharAt(string s, int i) => i < s.Length ? unchecked((sbyte)(byte)s[i]) : (sbyte)0;

    /// <summary>Compares two resource names the way the original does: ASCII case-insensitive, each string ending at its first NUL.</summary>
    /// <param name="a">The first name.</param>
    /// <param name="b">The second name.</param>
    /// <returns>A negative, zero or positive value like a <c>strcmp</c> result.</returns>
    public static int VolNameCompare(string a, string b)
    {
        for (int i = 0; ; ++i)
        {
            sbyte ca = UpperAscii(CharAt(a, i));
            sbyte cb = UpperAscii(CharAt(b, i));
            if (ca != cb || cb == 0)
                return ca - cb;
        }
    }

    /// <summary>Gets the directory entries in file order.</summary>
    /// <returns>The entries. Empty when no archive is open.</returns>
    public IReadOnlyList<VolEntry> Entries() => mEntries;

    /// <summary>Gets the message of the last failed open or read.</summary>
    /// <returns>The message, empty after a success.</returns>
    public string Error() => mError;

    /// <summary>Closes the archive.</summary>
    public void Dispose() => Close();

    /// <summary>Closes the file and drops the directory.</summary>
    public void Close()
    {
        mFile?.Dispose();
        mFile = null;
        mEntries.Clear();
        mNameIndex.Clear();
    }

    // src/core/vol.cpp VolArchive::fail.
    private bool Fail(string msg)
    {
        mError = msg;
        Close();
        return false;
    }

    // Reads up to dst.Length bytes, stopping at the end of the file (fread). Returns the count read.
    private static int ReadFull(FileStream f, Span<byte> dst)
    {
        int total = 0;
        while (total < dst.Length)
        {
            int n = f.Read(dst[total..]);
            if (n <= 0)
                break;
            total += n;
        }
        return total;
    }

    /// <summary>Opens an archive and reads its directory, replacing any open archive.</summary>
    /// <param name="path">The archive file path.</param>
    /// <returns>True on success. On failure <see cref="Error"/> holds the reason and the archive is closed.</returns>
    public bool Open(string path)
    {
        Close();
        try
        {
            mFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception)
        {
            return Fail("cannot open " + path);
        }

        long fileSize = mFile.Length;
        if (fileSize < 8)
            return Fail("file too small");

        Span<byte> header = stackalloc byte[8];
        mFile.Position = 0;
        if (ReadFull(mFile, header) != 8 || ReadU32(header) != kTagPVOL)
            return Fail("missing PVOL header");

        // The directory (vols + voli blocks) runs from the header's offset to EOF.
        ulong dirPos = ReadU32(header[4..]);
        if (dirPos + 8 > (ulong)fileSize)
            return Fail("directory offset out of range");
        byte[] data = new byte[(ulong)fileSize - dirPos];
        mFile.Position = (long)dirPos;
        if (ReadFull(mFile, data) != data.Length)
            return Fail("short read");

        // Reads a block header at pos, checks the tag, and returns payload bounds.
        bool ReadBlock(ulong pos, uint tag, out ulong payload, out uint size)
        {
            payload = 0;
            size = 0;
            if (pos + 8 > (ulong)data.Length || ReadU32(data.AsSpan((int)pos)) != tag)
                return false;
            payload = pos + 8;
            size = ReadU32(data.AsSpan((int)pos + 4)) & kSizeMask;
            return payload + size <= (ulong)data.Length;
        }

        if (!ReadBlock(0, kTagVols, out ulong strPos, out uint strSize))
            return Fail("bad vols block");
        ulong voliPos = strPos + PaddedSize(ReadU32(data.AsSpan(4)));
        if (!ReadBlock(voliPos, kTagVoli, out ulong idxPos, out uint idxSize))
            return Fail("bad voli block");

        uint count = idxSize / kEntrySize;
        mEntries.Capacity = (int)count;
        for (uint i = 0; i < count; ++i)
        {
            ReadOnlySpan<byte> rec = data.AsSpan((int)(idxPos + (ulong)i * kEntrySize), (int)kEntrySize);
            var e = new VolEntry
            {
                Id = ReadU32(rec),
                BlockOffset = ReadU32(rec[8..]),
                Size = ReadU32(rec[12..]),
                Compression = rec[16],
            };
            uint nameOffset = ReadU32(rec[4..]);
            if (nameOffset != 0xffffffff && strSize != 0)
            {
                if (nameOffset >= strSize)
                    return Fail("name offset out of range");
                ReadOnlySpan<byte> s = data.AsSpan((int)(strPos + nameOffset), (int)(strSize - nameOffset));
                int len = s.IndexOf((byte)0);
                e.Name = ByteString.FromBytes(len < 0 ? s : s[..len]);
            }
            mEntries.Add(e);
        }

        var named = new List<int>();
        for (int i = 0; i < (int)count; ++i)
            if (mEntries[i].Name.Length != 0)
                named.Add(i);
        // std::stable_sort: OrderBy is stable.
        mNameIndex.AddRange(named.OrderBy(i => i, Comparer<int>.Create(
            (x, y) => VolNameCompare(mEntries[x].Name, mEntries[y].Name))));

        mError = "";
        return true;
    }

    /// <summary>Finds an entry by name with ASCII case-insensitive matching.</summary>
    /// <param name="name">The entry name.</param>
    /// <returns>The entry, or null when absent.</returns>
    public VolEntry? Find(string name)
    {
        // std::lower_bound over mNameIndex.
        int lo = 0, hi = mNameIndex.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (VolNameCompare(mEntries[mNameIndex[mid]].Name, name) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }
        if (lo == mNameIndex.Count || VolNameCompare(mEntries[mNameIndex[lo]].Name, name) != 0)
            return null;
        return mEntries[mNameIndex[lo]];
    }

    /// <summary>Decodes volume compression type 1 (docs/resources.md 7).</summary>
    /// <param name="input">The packed bytes.</param>
    /// <param name="output">Receives the decoded bytes. The output may end mid-run when the caller wants fewer bytes.</param>
    /// <returns>False when the input ends before the output is full.</returns>
    /// <remarks>A header byte with bit 7 set is a run: the low 7 bits repeat the next byte. Otherwise the byte counts the literal bytes that follow. A zero count is skipped.</remarks>
    public static bool RleDecode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int ip = 0, op = 0;
        while (op < output.Length)
        {
            if (ip >= input.Length)
                return false;
            byte header = input[ip++];
            int count = header & 0x7f, take = Math.Min(count, output.Length - op);
            if ((header & 0x80) != 0)
            {
                if (ip >= input.Length)
                    return false;
                output.Slice(op, take).Fill(input[ip++]);
            }
            else
            {
                int have = Math.Min(take, input.Length - ip);
                input.Slice(ip, have).CopyTo(output[op..]);
                if (have < take)
                    return false;
                ip += take;
            }
            op += take;
        }
        return true;
    }

    /// <summary>Reads and decompresses an entry's payload.</summary>
    /// <param name="entry">The entry to read.</param>
    /// <param name="output">Receives the uncompressed bytes. The VBLK size is the uncompressed length.</param>
    /// <returns>False for a closed archive, an unknown compression type, a bad VBLK header, a short read, or RLE data that ends before the VBLK size is reached. <see cref="Error"/> holds the reason.</returns>
    /// <remarks>For LZH the compressed bytes sit at the start of the block and reads past the end of the file yield zeros.</remarks>
    public bool Read(VolEntry entry, out byte[] output)
    {
        output = Array.Empty<byte>();
        if (mFile == null)
        {
            mError = "archive not open";
            return false;
        }
        var compression = (VolCompression)entry.Compression;
        if (compression != VolCompression.None && compression != VolCompression.Lzh &&
            compression != VolCompression.Rle)
        {
            mError = "unsupported compression type " + entry.Compression;
            return false;
        }
        Span<byte> header = stackalloc byte[8];
        mFile.Position = entry.BlockOffset;
        if (ReadFull(mFile, header) != 8 || ReadU32(header) != kTagVBLK)
        {
            mError = "bad VBLK for " + entry.Name;
            return false;
        }
        // The VBLK size is the uncompressed length. For LZH the compressed bytes sit at the
        // start of the block, and reads past the end of the file yield zeros.
        uint size = ReadU32(header[4..]) & kSizeMask;
        if (compression == VolCompression.None)
        {
            output = new byte[size];
            if (ReadFull(mFile, output) != size)
            {
                mError = "short read for " + entry.Name;
                return false;
            }
            return true;
        }
        if (compression == VolCompression.Rle)
        {
            // The input is the memory stream over the block: `size` bytes, the file's end cuts it
            // shorter. Any run expands 2 bytes into at most 127, which bounds the output before it is
            // allocated.
            long here = mFile.Position;
            long end = mFile.Length;
            byte[] packed = new byte[Math.Min((ulong)size, end > here ? (ulong)(end - here) : 0)];
            int got = ReadFull(mFile, packed);
            if ((ulong)size > (ulong)got * 64)
            {
                mError = "RLE data too short for " + entry.Name;
                return false;
            }
            output = new byte[size];
            if (!RleDecode(packed.AsSpan(0, got), output))
            {
                mError = "RLE data ends early in " + entry.Name;
                return false;
            }
            return true;
        }
        byte[] lzPacked = new byte[size];
        int lzGot = ReadFull(mFile, lzPacked);
        output = new byte[size];
        Lzh.LzhDecode(lzPacked.AsSpan(0, lzGot), output);
        return true;
    }
}
