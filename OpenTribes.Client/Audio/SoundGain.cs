namespace OpenTribes.Client.Audio;

/// <summary>The profile record of 2.1 (11 dwords).</summary>
/// <remarks>Flags: 2 looping, 4 streaming, 8 pitching, 0x10 software 3D, 0x20 hardware 3D.</remarks>
public struct SoundProfile
{
    /// <summary>The flag of a looping sound.</summary>
    public const uint Looping = 2;
    /// <summary>The flag of a streaming sound.</summary>
    public const uint Streaming = 4;
    /// <summary>The flag of a pitching sound.</summary>
    public const uint Pitching = 8;
    /// <summary>The flag of a software 3D sound.</summary>
    public const uint Software3D = 0x10;
    /// <summary>The flag of a hardware 3D sound.</summary>
    public const uint Hardware3D = 0x20;
    /// <summary>The profile id.</summary>
    public int Id { get; set; }
    /// <summary>The flags.</summary>
    [System.Diagnostics.CodeAnalysis.UnscopedRef] public ref uint Flags => ref _flags;
    private uint _flags;
    /// <summary>The base volume.</summary>
    public double BaseVolume { get; set; }
    /// <summary>The inside cone angle.</summary>
    public int ConeInside { get; set; }
    /// <summary>The outside cone angle.</summary>
    public int ConeOutside { get; set; }
    /// <summary>The cone axis x.</summary>
    public double ConeX { get; set; }
    /// <summary>The cone axis y.</summary>
    public double ConeY { get; set; }
    /// <summary>The cone axis z.</summary>
    public double ConeZ { get; set; }
    /// <summary>The volume outside the outside cone.</summary>
    public double ConeOutsideVolume { get; set; }
    /// <summary>The distance within which the sound is at full volume.</summary>
    public double MinDist { get; set; }
    /// <summary>The distance beyond which the sound is silent.</summary>
    public double MaxDist { get; set; }

    /// <summary>The profile of a sound without an explicit profile.</summary>
    public static SoundProfile Default => new()
    {
        ConeInside = 90,
        ConeOutside = 180,
        ConeX = 0,
        ConeY = -1,
        ConeZ = 0,
        ConeOutsideVolume = 0,
        MinDist = 30,
        MaxDist = 300,
    };

    /// <summary>True when the profile is a software or hardware 3D profile.</summary>
    public readonly bool Is3D => (_flags & (Software3D | Hardware3D)) != 0;
    /// <summary>True when the profile loops or streams.</summary>
    public readonly bool Loops => (_flags & (Looping | Streaming)) != 0;
}

/// <summary>The listener: position and the world right axis (first row of the rotation, 2.6).</summary>
public struct Listener
{
    /// <summary>The x position.</summary>
    public double X { get; set; }
    /// <summary>The y position.</summary>
    public double Y { get; set; }
    /// <summary>The z position.</summary>
    public double Z { get; set; }
    /// <summary>The right axis x.</summary>
    public double RightX { get; set; }
    /// <summary>The right axis y.</summary>
    public double RightY { get; set; }
    /// <summary>The right axis z.</summary>
    public double RightZ { get; set; }

    /// <summary>A listener at the origin that looks along +Y.</summary>
    public static Listener Identity => new() { RightX = 1 };
}

/// <summary>A sound transform: position and the nine rotation floats (rows are the axes). A null rotation is the identity.</summary>
public struct SoundXf
{
    /// <summary>The x position.</summary>
    public double X { get; set; }
    /// <summary>The y position.</summary>
    public double Y { get; set; }
    /// <summary>The z position.</summary>
    public double Z { get; set; }
    /// <summary>The rotation, or null for the identity.</summary>
    public double[]? Rot { get; set; }
    /// <summary>Creates a transform without rotation.</summary>
    /// <param name="x">The x position.</param>
    /// <param name="y">The y position.</param>
    /// <param name="z">The z position.</param>
    public SoundXf(double x, double y, double z) { X = x; Y = y; Z = z; Rot = null; }
}

/// <summary>The gain and pan model as pure functions of (profile, source, listener, prefs): the software 3D path of the stock client for every voice (D39).</summary>
/// <remarks>docs/audio.md 2.7, 2.8 and 2.11.</remarks>
public static class SoundGain
{
    /// <summary>Converts a pref to an attenuation (2.7): 0.069 * -log2(max(v, 0.01)).</summary>
    /// <param name="v">The preference value.</param>
    /// <returns>The attenuation.</returns>
    public static double Master(double v) => 0.069 * -Math.Log2(Math.Max(v, 0.01));

    /// <summary>Converts an attenuation to device volume (2.7): trunc(-10000 * a) hundredths of a decibel, floored at -10000.</summary>
    /// <param name="attenuation">The attenuation.</param>
    /// <returns>The volume.</returns>
    public static int DeviceVolume(double attenuation) => (int)Math.Clamp(Math.Truncate(-10000 * attenuation), -10000, 0);
    /// <summary>Converts an attenuation to device gain (2.7): 10^(volume / 2000).</summary>
    /// <param name="attenuation">The attenuation.</param>
    /// <returns>The gain.</returns>
    public static double DeviceGain(double attenuation) => Math.Pow(10, DeviceVolume(attenuation) / 2000.0);

    /// <summary>Converts a device pan (2.11) in hundredths of a decibel to channel gains. A positive pan attenuates the left channel, a negative one the right.</summary>
    /// <param name="pan">The pan.</param>
    /// <returns>The left and right gain.</returns>
    public static (double L, double R) PanGains(double pan)
    {
        var p = (int)Math.Clamp(Math.Truncate(10000 * pan), -10000, 10000);
        return p > 0 ? (Math.Pow(10, -p / 2000.0), 1) : p < 0 ? (1, Math.Pow(10, p / 2000.0)) : (1, 1);
    }

    /// <summary>Computes the left and right amplitude of a voice.</summary>
    /// <param name="a">The attenuation.</param>
    /// <param name="pan">The pan.</param>
    /// <returns>The left and right amplitude.</returns>
    public static (double L, double R) Gains(double a, double pan)
    {
        var g = DeviceGain(a);
        var (l, r) = PanGains(pan);
        return (g * l, g * r);
    }

    /// <summary>Computes the attenuation of a wave play or pair play without position (2.8): min(1, master(sfx2DVolume) + baseVolume), once at start.</summary>
    /// <param name="sfx2DVolume">The 2D volume preference.</param>
    /// <param name="baseVolume">The base volume of the profile.</param>
    /// <returns>The attenuation.</returns>
    public static double Attenuation2D(double sfx2DVolume, double baseVolume) => Math.Min(1, Master(sfx2DVolume) + baseVolume);

    /// <summary>Computes the attenuation of a 3D voice (2.7 steps 1 to 4).</summary>
    /// <param name="p">The profile.</param>
    /// <param name="src">The source transform.</param>
    /// <param name="l">The listener.</param>
    /// <param name="sfx3DVolume">The 3D volume preference.</param>
    /// <param name="pan">The pan, -0.2 to 0.2. Positive means the source is on the right.</param>
    /// <returns>The attenuation, where 1 is silent.</returns>
    public static double Attenuation3D(in SoundProfile p, in SoundXf src, in Listener l, double sfx3DVolume, out double pan)
    {
        double dx = src.X - l.X, dy = src.Y - l.Y, dz = src.Z - l.Z;
        var d = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        if (d * d < 1e-12)
            d = 1e-5;
        pan = 0.2 * ((l.RightX * dx) + (l.RightY * dy) + (l.RightZ * dz)) / d;
        var a = Master(sfx3DVolume) + p.BaseVolume;
        if (p.ConeOutsideVolume * p.ConeOutsideVolume >= 1e-12)
        {
            double inside = p.ConeInside / 360.0, outside = p.ConeOutside / 360.0;
            double wx = p.ConeX, wy = p.ConeY, wz = p.ConeZ;
            if (src.Rot is { Length: >= 9 } m)
            {
                wx = (p.ConeX * m[0]) + (p.ConeY * m[3]) + (p.ConeZ * m[6]);
                wy = (p.ConeX * m[1]) + (p.ConeY * m[4]) + (p.ConeZ * m[7]);
                wz = (p.ConeX * m[2]) + (p.ConeY * m[5]) + (p.ConeZ * m[8]);
            }
            var c = ((wx * -dx) + (wy * -dy) + (wz * -dz)) / d;
            var s = 1 - ((c + 1) * 0.5);
            if (s >= outside)
                a += p.ConeOutsideVolume / 12;
            else if (s > inside)
                a += p.ConeOutsideVolume / 12 * (s - inside) / (outside - inside);
        }
        if (d >= p.MaxDist)
            a = 1;
        else if (d > p.MinDist)
        {
            var t = Math.Max(0, (d - p.MinDist) / (p.MaxDist - p.MinDist));
            a += (1 - a) * t;
        }
        return a;
    }

    /// <summary>Computes the right axis of a camera. Yaw 0 looks along +Y, positive turns left, Z is up.</summary>
    /// <param name="yawRad">The yaw in radians.</param>
    /// <returns>The right axis.</returns>
    public static (double X, double Y, double Z) RightOfYaw(double yawRad) => (Math.Cos(yawRad), Math.Sin(yawRad), 0);
}
