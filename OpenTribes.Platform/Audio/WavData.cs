// docs/audio.md 1.1 (loader [code 00531094]): the RIFF/WAVE reader of the sound system. It accepts what the stock loader accepts:
// the 12 byte header, a chunk walk with even padding that stops at the first `data` chunk (so `fmt ` must come first), a `fmt `
// chunk of at least 14 bytes, no check of the format tag or the channel count. The bits per sample (offset 14) decide how the
// data reads: 8 bit unsigned, 16 bit signed little endian. Anything else fails the load, which makes the play call return 0.
namespace OpenTribes.Platform.Audio;

public sealed class WavData
{
    public int Channels;
    public int Rate;
    public int Bits;
    public int Frames;
    // Interleaved samples, -1..1.
    public float[] Samples = Array.Empty<float>();

    public static WavData? TryParse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 12 || d[0] != 'R' || d[1] != 'I' || d[2] != 'F' || d[3] != 'F' || d[8] != 'W' || d[9] != 'A' || d[10] != 'V' || d[11] != 'E')
            return null;
        int pos = 12;
        int fmtPos = -1, fmtSize = 0;
        while (pos + 8 <= d.Length)
        {
            uint size = BitConverter.ToUInt32(d.Slice(pos + 4, 4));
            bool isFmt = d[pos] == 'f' && d[pos + 1] == 'm' && d[pos + 2] == 't' && d[pos + 3] == ' ';
            bool isData = d[pos] == 'd' && d[pos + 1] == 'a' && d[pos + 2] == 't' && d[pos + 3] == 'a';
            int body = pos + 8;
            if (isFmt)
            {
                fmtPos = body;
                fmtSize = (int)Math.Min(size, int.MaxValue);
            }
            else if (isData)
            {
                if (fmtPos < 0 || fmtSize < 14 || fmtPos + 16 > d.Length)
                    return null;
                int channels = BitConverter.ToUInt16(d.Slice(fmtPos + 2, 2));
                int rate = (int)BitConverter.ToUInt32(d.Slice(fmtPos + 4, 4));
                int bits = fmtSize >= 16 ? BitConverter.ToUInt16(d.Slice(fmtPos + 14, 2)) : 0;
                if (channels is < 1 or > 2 || rate <= 0 || bits is not (8 or 16))
                    return null;
                int avail = (int)Math.Min(size, (uint)(d.Length - body));
                int bytesPerFrame = channels * bits / 8;
                int frames = avail / bytesPerFrame;
                var s = new float[frames * channels];
                ReadOnlySpan<byte> p = d.Slice(body, frames * bytesPerFrame);
                if (bits == 8)
                    for (int i = 0; i < s.Length; ++i)
                        s[i] = (p[i] - 128) / 128f;
                else
                    for (int i = 0; i < s.Length; ++i)
                        s[i] = BitConverter.ToInt16(p.Slice(i * 2, 2)) / 32768f;
                return new WavData { Channels = channels, Rate = rate, Bits = bits, Frames = frames, Samples = s };
            }
            long next = (long)body + size + (size & 1);
            if (next > d.Length)
                return null;
            pos = (int)next;
        }
        return null;
    }
}
