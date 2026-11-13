using System.Globalization;
using OpenTribes.Script;

namespace OpenTribes.Client.Audio;

/// <summary>The sfx* console commands over the <see cref="SoundManager"/>.</summary>
/// <remarks>docs/client-scripts.md 2.8. The profile argument order follows the code (docs/audio.md F2): <c>sfxAdd3DProfile id, baseVolume, minDist, maxDist [, coneInside, coneOutside, x, y, z, coneOutsideVolume] [, flags...]</c>.</remarks>
public sealed class SfxCommands
{
    private readonly Interpreter mInterp;
    private readonly SoundManager mSound;
    private bool mCreated;
    /// <summary>Opens the output device. The caller decides what that means (SDL stream, offline mixer). False prints the stock failure text.</summary>
    public Func<bool> OpenDevice = () => true;
    /// <summary>Closes the output device.</summary>
    public Action? CloseDevice;

    /// <summary>Registers the commands.</summary>
    /// <param name="interp">The interpreter that gets the commands.</param>
    /// <param name="sound">The sound manager the commands drive.</param>
    public SfxCommands(Interpreter interp, SoundManager sound)
    {
        mInterp = interp;
        mSound = sound;
        Register();
    }

    private static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;

    private static string B(bool v) => v ? "TRUE" : "FALSE";

    private static uint FlagWord(string w) => w.ToUpperInvariant() switch
    {
        "SFX_IS_LOOPING" => SoundProfile.Looping,
        "SFX_IS_STREAMING" => SoundProfile.Streaming,
        "SFX_IS_PITCHING" => SoundProfile.Pitching,
        "SFX_IS_SOFTWARE_3D" => SoundProfile.Software3D,
        "SFX_IS_HARDWARE_3D" => SoundProfile.Hardware3D,
        _ => uint.MaxValue,
    };

    private bool ParseFlags(string cmd, string[] a, int from, ref uint flags)
    {
        for (int i = from; i < a.Length; ++i)
        {
            uint f = FlagWord(a[i]);
            if (f == uint.MaxValue)
            {
                if (uint.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out f) && f < 64)
                {
                    flags |= f;
                    continue;
                }
                mInterp.Print(1, $"{cmd}: bad tag ({a[i]})");
                return false;
            }
            flags |= f;
        }
        return true;
    }

    private string? NeedSound(string[] a, int argc, string usage, Func<string[], string?> body)
    {
        if (a.Length != argc)
        {
            mInterp.Print(1, usage);
            return "False";
        }
        if (!mCreated)
        {
            mInterp.Print(1, "unable to find Sound object");
            return "False";
        }
        return body(a);
    }

    private void Register()
    {
        mInterp.AddCommand("newSfx", (_, a) =>
        {
            if (a.Length != 1)
            {
                mInterp.Print(1, "newSfx");
                return "False";
            }
            mCreated = true;
            mSound.DeviceOpen = OpenDevice();
            if (!mSound.DeviceOpen)
            {
                mInterp.Print(1, "Sound System: error opening sound device.");
                return "False";
            }
            return "True";
        });
        mInterp.AddCommand("sfxOpen", (_, a) =>
        {
            if (!mCreated) { mInterp.Print(1, "Error: newSfx first."); return "False"; }
            mSound.DeviceOpen = OpenDevice();
            return B(mSound.DeviceOpen);
        });
        mInterp.AddCommand("sfxClose", (_, a) =>
        {
            if (!mCreated) { mInterp.Print(1, "Error: newSfx first."); return "False"; }
            mSound.StopAll();
            mSound.DeviceOpen = false;
            CloseDevice?.Invoke();
            return "True";
        });
        mInterp.AddCommand("sfxQuery", (_, a) =>
        {
            if (a.Length != 2) { mInterp.Print(1, "sfxQuery(driverID);"); return "False"; }
            if (!mCreated) { mInterp.Print(1, "Error: newSfx first."); return "False"; }
            return B(mSound.Query((int)D(a[1])));
        });
        mInterp.AddCommand("sfxSetFormat", (_, a) => NeedSound(a, 4, "sfxSetFormat kHz bits bool(stereo)", x =>
        {
            mSound.FormatRate = (int)D(x[1]);
            mSound.FormatBits = (int)D(x[2]);
            mSound.FormatStereo = x[3].Equals("true", StringComparison.OrdinalIgnoreCase) || (int)D(x[3]) != 0;
            return "True";
        }));
        mInterp.AddCommand("sfxGetFormat", (_, a) => NeedSound(a, 1, "sfxGetFormat", x =>
        {
            mInterp.Print(1, $"Sfx::Manager {mSound.FormatRate}kHz {mSound.FormatBits}bit {(mSound.FormatStereo ? "stereo" : "mono")}");
            return "True";
        }));
        mInterp.AddCommand("sfxAdd2DProfile", (_, a) =>
        {
            const string usage = "sfxAdd2DProfile( id, baseVolume, [flags] )";
            if (!mCreated) { mInterp.Print(1, "unable to find Sound object"); return "False"; }
            int id = a.Length >= 3 ? (int)D(a[1]) : 0;
            if (a.Length < 3 || id == 0) { mInterp.Print(1, usage); return "False"; }
            SoundProfile p = SoundProfile.Default;
            p.Id = id;
            p.BaseVolume = D(a[2]);
            if (!ParseFlags("sfxAdd2DProfile", a, 3, ref p.Flags))
                return "False";
            mSound.Profiles[id] = p;
            return "True";
        });
        mInterp.AddCommand("sfxAdd3DProfile", (_, a) =>
        {
            const string usage = "sfxAdd3DProfile( id, baseVolume, minDist, maxDist, [coneInside, coneOutside, x, y, z, coneOutsideVolume,] [flags]";
            if (!mCreated) { mInterp.Print(1, "unable to find Sound object"); return "False"; }
            int id = a.Length >= 5 ? (int)D(a[1]) : 0;
            if (a.Length < 5 || id == 0) { mInterp.Print(1, usage); return "False"; }
            SoundProfile p = SoundProfile.Default;
            p.Id = id;
            p.BaseVolume = D(a[2]);
            p.MinDist = D(a[3]);
            p.MaxDist = D(a[4]);
            int next = 5;
            // F2: the cone group is read when there are at least 11 arguments and argument 5 starts with a digit.
            if (a.Length >= 11 && a[5].Length > 0 && char.IsDigit(a[5][0]))
            {
                p.ConeInside = (int)D(a[5]);
                p.ConeOutside = (int)D(a[6]);
                double x = D(a[7]), y = D(a[8]), z = D(a[9]);
                double len = Math.Sqrt(x * x + y * y + z * z);
                if (len > 0) { x /= len; y /= len; z /= len; }
                (p.ConeX, p.ConeY, p.ConeZ) = (x, y, z);
                p.ConeOutsideVolume = D(a[10]);
                next = 11;
            }
            p.Flags = SoundProfile.Software3D;
            if (!ParseFlags("sfxAdd3DProfile", a, next, ref p.Flags))
                return "False";
            mSound.Profiles[id] = p;
            return "True";
        });
        mInterp.AddCommand("sfxAddPair", (_, a) =>
        {
            const string usage = "sfxAddPair( id, profileId, wav_name. [0.0-1.0 priority] )";
            if (!mCreated) { mInterp.Print(1, "unable to find Sound object"); return "False"; }
            if (a.Length is not (4 or 5)) { mInterp.Print(1, usage); return "False"; }
            mSound.Pairs[(int)D(a[1])] = ((int)D(a[2]), a[3], a.Length == 5 ? D(a[4]) : 0);
            return "True";
        });
        mInterp.AddCommand("sfxRemoveProfile", (_, a) => "");
        mInterp.AddCommand("sfxRemovePair", (_, a) => "");
        mInterp.AddCommand("sfxPlay", (_, a) =>
        {
            if (a.Length != 2) { mInterp.Print(1, "sfxPlay id"); return "False"; }
            if (!mCreated) { mInterp.Print(1, "unable to find Sound object"); return "False"; }
            int h = mSound.PlayPair((int)D(a[1]));
            mInterp.Print(1, $"sfxPlay handle = {h}");
            return h.ToString(CultureInfo.InvariantCulture);
        });
        mInterp.AddCommand("sfxStop", (_, a) => NeedSound(a, 2, "sfxStop id", x => { mSound.Stop((int)D(x[1])); return "True"; }));
        mInterp.AddCommand("sfxMute", (_, a) =>
        {
            if (a.Length != 2) { mInterp.Print(1, "sfxMute( true/false );"); return "False"; }
            if (!mCreated) { mInterp.Print(1, "unable to find Sound object"); return "False"; }
            mSound.Mute(a[1].Equals("true", StringComparison.OrdinalIgnoreCase));
            return "True";
        });
        mInterp.AddCommand("sfxSetPosition", (_, a) => NeedSound(a, 5, "sfxSetPosition id x y z", x =>
        {
            mSound.SetPosition((int)D(x[1]), D(x[2]), D(x[3]), D(x[4]));
            return "True";
        }));
        mInterp.AddCommand("sfxSetPan", (_, a) => NeedSound(a, 3, "sfxSetPan id fPan(-1.0 to +1.0)", x =>
        {
            if (mSound.Find((int)D(x[1])) == null)
            {
                mInterp.Print(1, "unable to find buffer id");
                return "False";
            }
            mSound.SetPan((int)D(x[1]), D(x[2]));
            return "True";
        }));
        mInterp.AddCommand("sfxSetListenerPosition", (_, a) => NeedSound(a, 4, "sfxSetListenerPosition x y z", x =>
        {
            Listener l = mSound.Listener;
            mSound.SetListener(D(x[1]), D(x[2]), D(x[3]), l.RightX, l.RightY, l.RightZ);
            return "True";
        }));
        mInterp.AddCommand("sfxSetVolume", (_, a) =>
        {
            if (a.Length is not (2 or 3)) { mInterp.Print(1, "sfxSetVolume float {SFX_HANDLE}"); return "False"; }
            if (!mCreated) { mInterp.Print(1, "unable to find Sound object"); return "False"; }
            double v = D(a[1]);
            if (a.Length == 3)
                mSound.SetVolume((int)D(a[2]), v);
            else
            {
                mSound.SetMasterVolume(v);
                mInterp.Print(1, $"Sfx master volume set to {v.ToString("F2", CultureInfo.InvariantCulture)}");
            }
            return "True";
        });
        mInterp.AddCommand("sfxSetMaxBuffers", (_, a) => NeedSound(a, 2, "sfxSetMaxBuffers n", x =>
        {
            mSound.MaxBuffers = Math.Clamp((int)D(x[1]), 0, 32);
            return "True";
        }));
        mInterp.AddCommand("sfxGetMaxBuffers", (_, a) => NeedSound(a, 1, "sfxGetMaxBuffers", x =>
        {
            mInterp.Print(1, $"Sfx::Manager::maxBuffers = {mSound.MaxBuffers}");
            return "True";
        }));
    }
}
