namespace OpenTribes.Platform.Audio;

/// <summary>A decoded RIFF/WAVE sound (docs/audio.md 1.1, loader 00531094).</summary>
/// <remarks>The reader accepts what the stock loader accepts: the 12-byte header, a chunk walk with even padding that stops at the first <c>data</c> chunk (so <c>fmt </c> must come first), and a <c>fmt </c> chunk of at least 14 bytes. It does not check the format tag or the channel count. The bits per sample (offset 14) decide how the data reads: 8-bit unsigned or 16-bit signed little endian. Anything else fails the load, which makes the play call return 0.</remarks>
public sealed class WavData
{
    /// <summary>The channel count.</summary>
    public int Channels;
    /// <summary>The sample rate.</summary>
    public int Rate;
    /// <summary>The bits per sample, 8 or 16.</summary>
    public int Bits;
    /// <summary>The number of frames.</summary>
    public int Frames;
    /// <summary>The interleaved samples, -1 to 1.</summary>
    public float[] Samples = [];

    private readonly Dictionary<(int Rate, bool Loop), WavData> mRates = [];

    /// <summary>Converts the wave to another sample rate with a band-limited filter.</summary>
    /// <param name="rate">The target rate.</param>
    /// <param name="loop">Whether the wave loops. A looping wave wraps around and a one-shot continues with silence.</param>
    /// <returns>The converted wave. Cached per rate and loop flag.</returns>
    /// <remarks>The filter is a windowed sinc with 16 lobes each side and a cutoff at 97% of the lower Nyquist. The mixer's linear interpolation droops 3 dB at 4 to 5 kHz and mirrors the band above 5.5 kHz for 11025 Hz sources, where the stock client's output has no energy above the source Nyquist and a flat passband to about 5 kHz.</remarks>
    public WavData AtRate(int rate, bool loop)
    {
        if (rate == Rate || Frames == 0)
            return this;
        lock (mRates)
        {
            if (!mRates.TryGetValue((rate, loop), out var w))
                mRates[(rate, loop)] = w = Resample(rate, loop);
            return w;
        }
    }

    private WavData Resample(int rate, bool loop)
    {
        const int H = 16;
        var ratio = (double)Rate / rate;                       // source frames per output frame
        var fc = 0.97 * Math.Min(Rate, rate) / 2.0 / Rate;     // cutoff in cycles per source sample
        var outFrames = (int)Math.Max(1, Math.Round(Frames / ratio));
        var o = new float[outFrames * Channels];
        var scale = 2 * fc;
        const int Res = 512;                                       // kernel table steps per source sample
        var tab = new double[(2 * H * Res) + 2];
        for (var i = 0; i < tab.Length; ++i)
        {
            var u = ((double)i / Res) - H;
            var x = u * scale * Math.PI;
            var sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(x) / x;
            var wx = u / H;
            tab[i] = scale * sinc * (0.42 + (0.5 * Math.Cos(Math.PI * wx)) + (0.08 * Math.Cos(2 * Math.PI * wx)));
        }
        for (var j = 0; j < outFrames; ++j)
        {
            var t = j * ratio;
            var k0 = (int)Math.Floor(t);
            for (var c = 0; c < Channels; ++c)
            {
                double acc = 0;
                for (var k = k0 - H + 1; k <= k0 + H; ++k)
                {
                    var idx = k;
                    if (idx < 0 || idx >= Frames)
                    {
                        if (!loop)
                            continue;
                        idx = ((idx % Frames) + Frames) % Frames;
                    }
                    var pos = (t - k + H) * Res;
                    var pi = (int)pos;
                    var h = tab[pi] + ((tab[pi + 1] - tab[pi]) * (pos - pi));
                    acc += Samples[(idx * Channels) + c] * h;
                }
                o[(j * Channels) + c] = (float)acc;
            }
        }
        return new WavData { Channels = Channels, Rate = rate, Bits = Bits, Frames = outFrames, Samples = o };
    }

    /// <summary>Parses a RIFF/WAVE file.</summary>
    /// <param name="d">The file bytes.</param>
    /// <returns>The wave, or null when the file is not accepted.</returns>
    public static WavData? TryParse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 12 || d[0] != 'R' || d[1] != 'I' || d[2] != 'F' || d[3] != 'F' || d[8] != 'W' || d[9] != 'A' || d[10] != 'V' || d[11] != 'E')
            return null;
        var pos = 12;
        int fmtPos = -1, fmtSize = 0;
        while (pos + 8 <= d.Length)
        {
            var size = BitConverter.ToUInt32(d.Slice(pos + 4, 4));
            var isFmt = d[pos] == 'f' && d[pos + 1] == 'm' && d[pos + 2] == 't' && d[pos + 3] == ' ';
            var isData = d[pos] == 'd' && d[pos + 1] == 'a' && d[pos + 2] == 't' && d[pos + 3] == 'a';
            var body = pos + 8;
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
                var rate = (int)BitConverter.ToUInt32(d.Slice(fmtPos + 4, 4));
                var bits = fmtSize >= 16 ? BitConverter.ToUInt16(d.Slice(fmtPos + 14, 2)) : 0;
                if (channels is < 1 or > 2 || rate <= 0 || bits is not (8 or 16))
                    return null;
                var avail = (int)Math.Min(size, (uint)(d.Length - body));
                var bytesPerFrame = channels * bits / 8;
                var frames = avail / bytesPerFrame;
                var s = new float[frames * channels];
                var p = d.Slice(body, frames * bytesPerFrame);
                if (bits == 8)
                    for (var i = 0; i < s.Length; ++i)
                        s[i] = (p[i] - 128) / 128f;
                else
                    for (var i = 0; i < s.Length; ++i)
                        s[i] = BitConverter.ToInt16(p.Slice(i * 2, 2)) / 32768f;
                return new WavData { Channels = channels, Rate = rate, Bits = bits, Frames = frames, Samples = s };
            }
            var next = body + size + (size & 1);
            if (next > d.Length)
                return null;
            pos = (int)next;
        }
        return null;
    }
}
