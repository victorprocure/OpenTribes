using System.Buffers.Binary;

namespace OpenTribes.Platform.Gfx;

// docs/renderer.md 2.3: `.pba` PBMA bitmap array.
/// <summary>A <c>.pba</c> PBMA bitmap array (docs/renderer.md 2.3).</summary>
public sealed class PbmaArray
{
    /// <summary>The number of chunks.</summary>
    public uint ChunkCount;
    /// <summary>The bitmapCount of the head. <c>Bitmaps.Count</c> is the number of PBMP chunks actually read.</summary>
    public uint BitmapCount;
    /// <summary>The bitmaps.</summary>
    public List<PbmpBitmap> Bitmaps = new();
    /// <summary>The <c>user</c> data, BitmapCount * userSize bytes, or null.</summary>
    public byte[]? User;
    /// <summary>The <c>rmap</c> sorted sparse ids, one per bitmap, or null. Then the id is the slot.</summary>
    public uint[]? Rmap;

    /// <summary>Gets the size of the user data of each bitmap.</summary>
    public int UserSize => User != null && BitmapCount != 0 ? User.Length / (int)BitmapCount : 0;

    /// <summary>Gets a bitmap by id: a binary search in rmap, else the id is the slot.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The bitmap, or null when absent.</returns>
    public PbmpBitmap? Get(uint id)
    {
        if (Rmap != null)
        {
            int lo = 0, hi = Rmap.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (Rmap[mid] == id)
                    return mid < Bitmaps.Count ? Bitmaps[mid] : null;
                if (Rmap[mid] < id)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }
            return null;
        }
        return id < Bitmaps.Count ? Bitmaps[(int)id] : null;
    }

    /// <summary>Parses a <c>.pba</c> file.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="includePalette">Whether to read the embedded palettes.</param>
    /// <param name="array">Receives the array.</param>
    /// <param name="error">Receives the failure reason.</param>
    /// <returns>True on success.</returns>
    public static bool TryParse(ReadOnlySpan<byte> data, bool includePalette, out PbmaArray? array, out string error)
    {
        array = null;
        error = "";
        try
        {
            var c = new Cursor(data);
            array = Read(ref c, includePalette);
            return true;
        }
        catch (GfxFormatException e)
        {
            error = e.Message;
            return false;
        }
    }

    // The cursor may be at `PBMA` (size ignored) or at `head`. Ends after the last chunk.
    internal static PbmaArray Read(ref Cursor c, bool includePalette)
    {
        if (c.PeekTag() == "PBMA")
        {
            c.Tag();
            c.U32();
        }
        if (c.Tag() != "head")
            throw new GfxFormatException("first chunk is not head");
        uint headSize = c.U32();
        if (headSize < 8)
            throw new GfxFormatException("head chunk too small");
        var head = c.Bytes(headSize);
        var arr = new PbmaArray
        {
            ChunkCount = BinaryPrimitives.ReadUInt32LittleEndian(head) & 0xFFFFFF,
            BitmapCount = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]),
        };
        for (uint i = 0; i < arr.ChunkCount; ++i)
        {
            string tag = c.PeekTag();
            if (tag == "PBMP")
            {
                // The bitmap reader consumes the tag, ignores the size and reads its own chunks.
                arr.Bitmaps.Add(PbmpBitmap.Read(ref c, includePalette));
                continue;
            }
            c.Tag();
            uint size = c.U32();
            var body = c.Bytes(size);
            if (tag == "user")
                arr.User = body.ToArray();
            else if (tag == "rmap")
            {
                var ids = new uint[size / 4];
                for (int k = 0; k < ids.Length; ++k)
                    ids[k] = BinaryPrimitives.ReadUInt32LittleEndian(body[(k * 4)..]);
                arr.Rmap = ids;
            }
            // other chunks are skipped by size
        }
        return arr;
    }
}
