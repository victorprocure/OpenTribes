namespace OpenTribes.Tests;

using OpenTribes.Platform.Audio;

// WavData.AtRate: the band-limited conversion to the device rate (levels of the recording harness, docs/audio.md 2.9).
public class ResampleTests
{
    private static WavData Tone(int rate, double hz, int frames)
    {
        var s = new float[frames];
        for (var i = 0; i < frames; ++i)
            s[i] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate));
        return new WavData { Channels = 1, Rate = rate, Bits = 16, Frames = frames, Samples = s };
    }

    private static double RmsDb(float[] s, int from, int to)
    {
        double a = 0;
        for (var i = from; i < to; ++i)
            a += s[i] * s[i];
        return 10 * Math.Log10((a / (to - from)) + 1e-20);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(2000)]
    [InlineData(4500)]
    public void PassbandKeepsLevel(double hz)
    {
        WavData src = Tone(11025, hz, 11025);
        WavData dst = src.AtRate(48000, false);
        Assert.Equal(48000, dst.Rate);
        Assert.InRange(RmsDb(dst.Samples, 4000, 44000) - RmsDb(src.Samples, 1000, 10000), -0.15, 0.15);
    }

    [Fact]
    public void ImageAboveSourceNyquistIsRemoved()
    {
        // 6.5 kHz is above the 5.5 kHz Nyquist of an 11025 Hz source: it aliases to 4.5 kHz in the source and must not leave a mirror.
        WavData src = Tone(22050, 8000, 22050);       // 22050 Hz source, device 48000: 8 kHz is in band
        WavData dst = src.AtRate(48000, false);
        Assert.InRange(RmsDb(dst.Samples, 8000, 44000) - RmsDb(src.Samples, 4000, 18000), -0.5, 0.5);
        WavData low = Tone(11025, 4500, 11025).AtRate(48000, false);
        // a 4.5 kHz tone sampled at 11025 Hz has its mirror at 6525 Hz in a linear interpolation; measure by correlating with 6525 Hz
        double re = 0, im = 0;
        for (var i = 4000; i < 44000; ++i)
        {
            re += low.Samples[i] * Math.Cos(2 * Math.PI * 6525 * i / 48000.0);
            im += low.Samples[i] * Math.Sin(2 * Math.PI * 6525 * i / 48000.0);
        }
        var mirror = 2 * Math.Sqrt((re * re) + (im * im)) / 40000;
        Assert.True(mirror < 0.5 * 0.01, "mirror amplitude " + mirror);
    }

    [Fact]
    public void LoopWrapsAndIsCached()
    {
        WavData src = Tone(11025, 441, 11025);        // exactly 441 cycles in the data: the loop is seamless
        WavData a = src.AtRate(44100, true);
        Assert.Same(a, src.AtRate(44100, true));
        Assert.Equal(44100, a.Frames);
        Assert.InRange(Math.Abs(a.Samples[0] - a.Samples[44099] - (a.Samples[1] - a.Samples[0])), 0, 0.01);
        Assert.Same(src, src.AtRate(11025, true));
    }
}
