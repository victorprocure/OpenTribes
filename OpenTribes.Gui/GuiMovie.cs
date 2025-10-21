// docs/gui.md 3.3 and Q12: FearGui::SmackerCtrl plays a Smacker movie (the stock control calls smackw32.dll, this one decodes the
// file itself) and SimGui::AnimateBMA steps through a bitmap array.
using OpenTribes.Platform.Audio;
using OpenTribes.Platform.Gfx;
using OpenTribes.Platform.Video;

namespace OpenTribes.Gui;

// FearGui::SmackerCtrl [code 004d6dd0 .. 004d693c]. One frame per frame duration of the movie; the palette records of the movie
// replace the palette entries palStart .. palStart + palColors - 1; the audio track 0 plays through the host's mixer. After the
// last frame (or at once for a movie that will not open) the control posts its done event: the done tag as a message when the tag
// is not 0, else its own command. A key or mouse press does the same, so the movie can be skipped.
public sealed class GuiSmacker : GuiActive
{
    private SmackerDecoder? mMovie;
    private PbmpBitmap? mPicture;
    private Action? mStopAudio;
    // The color translation of the stock control [code 004d670c]: movie color i draws as palette entry i + palStart for the first
    // palColors colors, every other value draws as itself.
    private readonly byte[] mMap = new byte[256];
    private double mStartMs;
    private int mShown;            // frames decoded so far
    private bool mStarted, mFinished, mDonePosted, mPaletteDirty, mPictureDirty, mLastFrameDrawn;
    public int FramesShown => mShown;
    public bool HasMovie => mMovie != null;
    public SmackerDecoder? Movie => mMovie;

    public override void Wake()
    {
        mDonePosted = false;
        mFinished = false;
        mLastFrameDrawn = false;
        mStarted = false;
        mShown = 0;
        string? name = Host?.TagText(U("movieTag")) is string t ? GuiHost.FirstItem(t) : null;
        byte[]? data = null;
        if (name != null && name.Length > 0 && Host?.Res != null && GfxAssets.Read(Host.Res, name, out byte[] d))
            data = d;
        mMovie = data == null ? null : SmackerDecoder.TryOpen(data, out _);
        if (mMovie == null)
        {
            mFinished = true;
            return;
        }
        int start = (int)U("palStart"), count = (int)U("palColors");
        for (int i = 0; i < 256; ++i)
            mMap[i] = (byte)(i < count ? i + start : i);
        mPicture = new PbmpBitmap { Width = mMovie.Width, Height = mMovie.Height, Depth = 8, Pixels = new byte[mMovie.Width * mMovie.Height] };
    }

    public override void Close()
    {
        mStopAudio?.Invoke();
        mStopAudio = null;
        if (mMovie != null)
            Host?.OnMovieEnd?.Invoke();
        mMovie = null;
        mPicture = null;
    }

    public override void PreRender()
    {
        if (Host == null)
            return;
        if (mMovie != null && !mFinished)
        {
            double now = Host.TickMs;
            if (!mStarted)
            {
                mStarted = true;
                mStartMs = now;
                StartAudio();
            }
            double frameMs = mMovie.FrameMicroseconds / 1000.0;
            // The frame that is due at this time; every frame before it is decoded as well because frames are deltas.
            int due = (int)Math.Floor((now - mStartMs) / frameMs) + 1;
            bool decoded = false;
            while (mShown < due && mShown < mMovie.FrameCount && !mFinished)
            {
                if (!mMovie.DecodeNext())
                {
                    mFinished = true;
                    break;
                }
                ++mShown;
                decoded = true;
                if (mMovie.PaletteChanged || mShown == 1)
                    mPaletteDirty = true;
                if (mShown >= mMovie.FrameCount)
                {
                    if (U("loop") != 0)
                    {
                        mMovie.Rewind();
                        mShown = 0;
                        mStartMs += mMovie.FrameCount * frameMs;
                        due = 0;
                    }
                    else
                    {
                        mFinished = true;
                    }
                }
            }
            if (decoded && mPicture != null)
            {
                byte[] src = mMovie.Pixels, dst = mPicture.Pixels;
                for (int i = 0; i < src.Length; ++i)
                    dst[i] = mMap[src[i]];
                mPictureDirty = true;
            }
            return;
        }
        // The last frame was drawn by a render pass already; the done event goes out now (a movie that did not open posts at once).
        if (mFinished && !mDonePosted && (mMovie == null || mLastFrameDrawn))
            PostDone();
    }

    private void StartAudio()
    {
        if (mMovie == null || Host?.StartMovieAudio == null)
            return;
        for (int t = 0; t < mMovie.Audio.Length; ++t)
        {
            SmackerAudioTrack a = mMovie.Audio[t];
            if (!a.HasData)
                continue;
            byte[]? pcm = mMovie.ReadAudio(t);
            if (pcm == null || pcm.Length == 0)
                return;
            WavData wav = ToWave(pcm, a);
            mStopAudio = Host.StartMovieAudio(wav);
            return;
        }
    }

    public static WavData ToWave(byte[] pcm, SmackerAudioTrack a)
    {
        int bps = a.Bits / 8;
        int frames = pcm.Length / (bps * a.Channels);
        var s = new float[frames * a.Channels];
        for (int i = 0; i < s.Length; ++i)
            s[i] = bps == 2 ? (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)) / 32768f : (pcm[i] - 128) / 128f;
        return new WavData { Channels = a.Channels, Rate = a.Rate, Bits = a.Bits, Frames = frames, Samples = s };
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        if (mMovie != null && mPicture != null && mShown > 0)
        {
            if (mPaletteDirty)
            {
                mPaletteDirty = false;
                Host?.OnMoviePalette?.Invoke(mMovie.Palette, (int)U("palStart"), (int)U("palColors"));
            }
            if (mPictureDirty)
            {
                mPictureDirty = false;
                p.Dev.FlushTexture(mPicture);
            }
            int h = mMovie.YDoubled ? mMovie.Height * 2 : mMovie.Height;
            if (U("stretch") != 0)
                p.BitmapStretch(mPicture, gx, gy, W, H);
            else if (h != mMovie.Height)
                p.BitmapStretch(mPicture, gx, gy, mMovie.Width, h);
            else
                p.Bitmap(mPicture, gx, gy);
            if (mFinished && mShown >= mMovie.FrameCount)
                mLastFrameDrawn = true;
        }
        RenderChildren(p, gx, gy);
    }

    private void PostDone()
    {
        if (mDonePosted)
            return;
        mDonePosted = true;
        uint tag = U("movieDoneTag");
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

    public override bool KeyDown(GuiEvent e)
    {
        PostDone();
        return true;
    }

    public override bool MsDown(GuiEvent e)
    {
        PostDone();
        return true;
    }
}

// SimGui::AnimateBMA [code 005329d8 load, 005323ac construct, 00532414 wake, 00532584 prepare, 00532664 draw]. Frame f of the
// array is drawn at the control origin. The file stores the inverse of the continuous flag (0 = continuous, the constructor
// default, and what the stock files hold) [code 005329d8]. A control that is not continuous is stopped on wake and keeps
// showing frame 0; a continuous one steps to the next frame every frameTime seconds and wraps after count + loopWait frames
// (the draw shows the last frame while the counter is in the wait).
public sealed class GuiAnimate : GuiActive
{
    private PbmaArray? mArray;
    private long mLastMs;
    public bool Continuous, Running;
    public int Frame;

    public override bool WantsTab => false;

    public override void Wake()
    {
        mLastMs = Host?.TickMs ?? 0;
        Frame = 0;
        mArray = Host?.TagText(U("pbaTag")) is string t ? Host.ArrayByName(GuiHost.FirstItem(t)) : null;
        Continuous = U("continuous") == 0;
        Running = Continuous;
    }

    public override void PreRender()
    {
        if (Host == null || mArray == null || !(Continuous || Running))
            return;
        float frameTime = BitConverter.UInt32BitsToSingle(U("frameTime"));
        if ((Host.TickMs - mLastMs) / 1000.0 <= frameTime)
            return;
        ++Frame;
        if (mArray.Bitmaps.Count + (int)U("loopWait") <= Frame)
        {
            Frame = 0;
            if (!Continuous)
            {
                Running = false;
                Active = false;
            }
        }
        mLastMs = Host.TickMs;
    }

    public override void Render(GuiPaint p, int ox, int oy)
    {
        int gx = ox + X, gy = oy + Y;
        DrawBase(p, gx, gy);
        if (mArray != null && mArray.Bitmaps.Count > 0)
            p.Bitmap(mArray.Bitmaps[Math.Min(Frame, mArray.Bitmaps.Count - 1)], gx, gy);
        RenderChildren(p, gx, gy);
    }
}
