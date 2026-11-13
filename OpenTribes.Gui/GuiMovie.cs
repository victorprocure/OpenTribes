namespace OpenTribes.Gui;

using OpenTribes.Platform.Audio;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Video;

/// <summary>The Smacker movie control (FearGui::SmackerCtrl, docs/gui.md 3.3 and Q12).</summary>
/// <remarks>
/// <para>The stock control calls smackw32.dll, this one decodes the file itself. It shows one frame per frame duration of the movie. The palette records of the movie replace the palette entries palStart to palStart + palColors - 1. The audio track 0 plays through the host's mixer.</para>
/// <para>After the last frame (or at once for a movie that will not open) the control posts its done event: the done tag as a message when the tag is not 0, else its own command. A key or mouse press does the same, so the movie can be skipped.</para>
/// <para>Follows 004d6dd0 to 004d693c.</para>
/// </remarks>
public sealed class GuiSmacker : GuiActive
{
    private PbmpBitmap? _picture;
    private Action? _stopAudio;
    /// <summary>The color translation of the stock control (004d670c): movie color i draws as palette entry i + palStart for the first palColors colors, every other value draws as itself.</summary>
    private readonly byte[] _map = new byte[256];
    private double _startMs;
    private bool _started, _finished, _donePosted, _paletteDirty, _pictureDirty, _lastFrameDrawn;
    /// <summary>Gets the number of frames decoded so far.</summary>
    public int FramesShown { get; private set; }
    /// <summary>Gets a value that is true when a movie is open.</summary>
    public bool HasMovie => Movie != null;
    /// <summary>Gets the open movie, or null.</summary>
    public SmackerDecoder? Movie { get; private set; }

    /// <inheritdoc/>
    public override void Wake()
    {
        _donePosted = false;
        _finished = false;
        _lastFrameDrawn = false;
        _started = false;
        FramesShown = 0;
        var name = Host?.TagText(U("movieTag")) is string t ? GuiHost.FirstItem(t) : null;
        byte[]? data = null;
        if (name != null && name.Length > 0 && Host?.Res != null && GfxAssets.Read(Host.Res, name, out var d))
            data = d;
        Movie = data == null ? null : SmackerDecoder.TryOpen(data, out _);
        if (Movie == null)
        {
            _finished = true;
            return;
        }
        int start = (int)U("palStart"), count = (int)U("palColors");
        for (var i = 0; i < 256; ++i)
            _map[i] = (byte)(i < count ? i + start : i);
        _picture = new PbmpBitmap { Width = Movie.Width, Height = Movie.Height, Depth = 8, Pixels = new byte[Movie.Width * Movie.Height] };
    }

    /// <inheritdoc/>
    public override void Close()
    {
        _stopAudio?.Invoke();
        _stopAudio = null;
        if (Movie != null)
            Host?.OnMovieEnd?.Invoke();
        Movie = null;
        _picture = null;
    }

    /// <inheritdoc/>
    public override void PreRender()
    {
        if (Host == null)
            return;
        if (Movie != null && !_finished)
        {
            double now = Host.TickMs;
            if (!_started)
            {
                _started = true;
                _startMs = now;
                StartAudio();
            }
            var frameMs = Movie.FrameMicroseconds / 1000.0;
            // Decode every frame up to the one due now: frames are deltas.
            var due = (int)Math.Floor((now - _startMs) / frameMs) + 1;
            var decoded = false;
            while (FramesShown < due && FramesShown < Movie.FrameCount && !_finished)
            {
                if (!Movie.DecodeNext())
                {
                    _finished = true;
                    break;
                }
                ++FramesShown;
                decoded = true;
                if (Movie.PaletteChanged || FramesShown == 1)
                    _paletteDirty = true;
                if (FramesShown >= Movie.FrameCount)
                {
                    if (U("loop") != 0)
                    {
                        Movie.Rewind();
                        FramesShown = 0;
                        _startMs += Movie.FrameCount * frameMs;
                        due = 0;
                    }
                    else
                    {
                        _finished = true;
                    }
                }
            }
            if (decoded && _picture != null)
            {
                byte[] src = Movie.Pixels, dst = _picture.Pixels;
                for (var i = 0; i < src.Length; ++i)
                    dst[i] = _map[src[i]];
                _pictureDirty = true;
            }
            return;
        }
        // The last frame was drawn by a render pass already; the done event goes out now (a movie that did not open posts at once).
        if (_finished && !_donePosted && (Movie == null || _lastFrameDrawn))
            PostDone();
    }

    private void StartAudio()
    {
        if (Movie == null || Host?.StartMovieAudio == null)
            return;
        for (var t = 0; t < Movie.Audio.Length; ++t)
        {
            var a = Movie.Audio[t];
            if (!a.HasData)
                continue;
            var pcm = Movie.ReadAudio(t);
            if (pcm == null || pcm.Length == 0)
                return;
            var wav = ToWave(pcm, a);
            _stopAudio = Host.StartMovieAudio(wav);
            return;
        }
    }

    /// <summary>Converts the PCM data of a Smacker audio track to a wave.</summary>
    /// <param name="pcm">The 8 or 16 bit PCM bytes.</param>
    /// <param name="a">The track description.</param>
    /// <returns>The wave with float samples.</returns>
    public static WavData ToWave(byte[] pcm, SmackerAudioTrack a)
    {
        var bps = a.Bits / 8;
        var frames = pcm.Length / (bps * a.Channels);
        var s = new float[frames * a.Channels];
        for (var i = 0; i < s.Length; ++i)
            s[i] = bps == 2 ? (short)(pcm[i * 2] | (pcm[(i * 2) + 1] << 8)) / 32768f : (pcm[i] - 128) / 128f;
        return new WavData { Channels = a.Channels, Rate = a.Rate, Bits = a.Bits, Frames = frames, Samples = s };
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        if (Movie != null && _picture != null && FramesShown > 0)
        {
            if (_paletteDirty)
            {
                _paletteDirty = false;
                Host?.OnMoviePalette?.Invoke(Movie.Palette, (int)U("palStart"), (int)U("palColors"));
            }
            if (_pictureDirty)
            {
                _pictureDirty = false;
                // The draw goes through the keyed copy of the picture and the device caches its texture under that copy, so the copy is the bitmap to flush.
                // The stock control redraws its surface every frame (004d693c, 004d670c).
                p.Dev.FlushTexture(GuiPaint.KeyedBitmap(_picture));
            }
            var h = Movie.YDoubled ? Movie.Height * 2 : Movie.Height;
            if (U("stretch") != 0)
                p.BitmapStretch(_picture, gx, gy, W, H);
            else if (h != Movie.Height)
                p.BitmapStretch(_picture, gx, gy, Movie.Width, h);
            else
                p.Bitmap(_picture, gx, gy);
            if (_finished && FramesShown >= Movie.FrameCount)
                _lastFrameDrawn = true;
        }
        RenderChildren(p, gx, gy);
    }

    private void PostDone()
    {
        if (_donePosted)
            return;
        _donePosted = true;
        var tag = U("movieDoneTag");
        if (tag == 0)
        {
            if (Command.Length > 0)
                Host?.Eval(Command);
        }
        else
        {
            Next?.Message(this, (int)tag);
        }
    }

    /// <inheritdoc/>
    public override bool KeyDown(GuiEvent e)
    {
        PostDone();
        return true;
    }

    /// <inheritdoc/>
    public override bool MsDown(GuiEvent e)
    {
        PostDone();
        return true;
    }
}

/// <summary>The animated bitmap array control (SimGui::AnimateBMA): frame f of the array is drawn at the control origin.</summary>
/// <remarks>
/// <para>The file stores the inverse of the continuous flag (0 = continuous, the constructor default, and what the stock files hold) (005329d8). A control that is not continuous is stopped on wake and keeps showing frame 0. A continuous one steps to the next frame every frameTime seconds and wraps after count + loopWait frames (the draw shows the last frame while the counter is in the wait).</para>
/// <para>Follows load 005329d8, construct 005323ac, wake 00532414, prepare 00532584 and draw 00532664.</para>
/// </remarks>
public sealed class GuiAnimate : GuiActive
{
    private PbmaArray? _array;
    private long _lastMs;
    /// <summary>True when the control steps through the frames on its own.</summary>
    public bool Continuous { get; set; }
    /// <summary>True while the control steps through the frames.</summary>
    public bool Running { get; set; }
    /// <summary>The frame index drawn.</summary>
    public int Frame { get; set; }

    /// <inheritdoc/>
    public override bool WantsTab => false;

    /// <inheritdoc/>
    public override void Wake()
    {
        _lastMs = Host?.TickMs ?? 0;
        Frame = 0;
        _array = Host?.TagText(U("pbaTag")) is string t ? Host.ArrayByName(GuiHost.FirstItem(t)) : null;
        Continuous = U("continuous") == 0;
        Running = Continuous;
    }

    /// <inheritdoc/>
    public override void PreRender()
    {
        if (Host == null || _array == null || !(Continuous || Running))
            return;
        var frameTime = BitConverter.UInt32BitsToSingle(U("frameTime"));
        if ((Host.TickMs - _lastMs) / 1000.0 <= frameTime)
            return;
        ++Frame;
        if (_array.Bitmaps.Count + (int)U("loopWait") <= Frame)
        {
            Frame = 0;
            if (!Continuous)
            {
                Running = false;
                Active = false;
            }
        }
        _lastMs = Host.TickMs;
    }

    /// <inheritdoc/>
    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        if (_array != null && _array.Bitmaps.Count > 0)
            p.Bitmap(_array.Bitmaps[Math.Min(Frame, _array.Bitmaps.Count - 1)], gx, gy);
        RenderChildren(p, gx, gy);
    }
}
