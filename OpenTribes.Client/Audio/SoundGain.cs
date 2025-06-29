// docs/audio.md 2.7, 2.8, 2.11: the gain and pan model as pure functions of (profile, source, listener, prefs). The software 3D
// path of the stock client for every voice (D39).
namespace OpenTribes.Client.Audio;

// The profile record of 2.1 (11 dwords). Flags: 2 looping, 4 streaming, 8 pitching, 0x10 software 3D, 0x20 hardware 3D.
public struct SoundProfile
{
    public const uint Looping = 2, Streaming = 4, Pitching = 8, Software3D = 0x10, Hardware3D = 0x20;
    public int Id;
    public uint Flags;
    public double BaseVolume;
    public int ConeInside, ConeOutside;
    public double ConeX, ConeY, ConeZ;
    public double ConeOutsideVolume;
    public double MinDist, MaxDist;

    public static SoundProfile Default => new()
    {
        ConeInside = 90, ConeOutside = 180, ConeX = 0, ConeY = -1, ConeZ = 0, ConeOutsideVolume = 0, MinDist = 30, MaxDist = 300,
    };

    public readonly bool Is3D => (Flags & (Software3D | Hardware3D)) != 0;
    public readonly bool Loops => (Flags & (Looping | Streaming)) != 0;
}

// Listener: position and the world right axis (first row of the rotation, 2.6).
public struct Listener
{
    public double X, Y, Z;
    public double RightX, RightY, RightZ;

    public static Listener Identity => new() { RightX = 1 };
}

// A sound transform: position and the nine rotation floats (rows are the axes); null rotation is the identity.
public struct SoundXf
{
    public double X, Y, Z;
    public double[]? Rot;
    public SoundXf(double x, double y, double z) { X = x; Y = y; Z = z; Rot = null; }
}

public static class SoundGain
{
    // 2.7 pref to attenuation: 0.069 * -log2(max(v, 0.01)).
    public static double Master(double v) => 0.069 * -Math.Log2(Math.Max(v, 0.01));

    // 2.7 device units: volume = trunc(-10000 * a) hundredths of a decibel (floor at -10000), gain = 10^(volume / 2000).
    public static int DeviceVolume(double attenuation) => (int)Math.Clamp(Math.Truncate(-10000 * attenuation), -10000, 0);
    public static double DeviceGain(double attenuation) => Math.Pow(10, DeviceVolume(attenuation) / 2000.0);

    // 2.11 device pan: pan hundredths of a decibel; a positive pan attenuates the left channel, a negative one the right.
    public static (double L, double R) PanGains(double pan)
    {
        int p = (int)Math.Clamp(Math.Truncate(10000 * pan), -10000, 10000);
        return p > 0 ? (Math.Pow(10, -p / 2000.0), 1) : p < 0 ? (1, Math.Pow(10, p / 2000.0)) : (1, 1);
    }

    // Left and right amplitude of a voice with attenuation `a` and pan `pan`.
    public static (double L, double R) Gains(double a, double pan)
    {
        double g = DeviceGain(a);
        var (l, r) = PanGains(pan);
        return (g * l, g * r);
    }

    // 2.8 wave play and pair play without position: min(1, master(sfx2DVolume) + baseVolume), once at start.
    public static double Attenuation2D(double sfx2DVolume, double baseVolume) => Math.Min(1, Master(sfx2DVolume) + baseVolume);

    // 2.7 steps 1 to 4 for a 3D voice. Returns the attenuation (1 = silent) and the pan (-0.2..0.2, positive = source on the right).
    public static double Attenuation3D(in SoundProfile p, in SoundXf src, in Listener l, double sfx3DVolume, out double pan)
    {
        double dx = src.X - l.X, dy = src.Y - l.Y, dz = src.Z - l.Z;
        double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (d * d < 1e-12)
            d = 1e-5;
        pan = 0.2 * (l.RightX * dx + l.RightY * dy + l.RightZ * dz) / d;
        double a = Master(sfx3DVolume) + p.BaseVolume;
        if (p.ConeOutsideVolume * p.ConeOutsideVolume >= 1e-12)
        {
            double inside = p.ConeInside / 360.0, outside = p.ConeOutside / 360.0;
            double wx = p.ConeX, wy = p.ConeY, wz = p.ConeZ;
            if (src.Rot is { Length: >= 9 } m)
            {
                wx = p.ConeX * m[0] + p.ConeY * m[3] + p.ConeZ * m[6];
                wy = p.ConeX * m[1] + p.ConeY * m[4] + p.ConeZ * m[7];
                wz = p.ConeX * m[2] + p.ConeY * m[5] + p.ConeZ * m[8];
            }
            double c = (wx * -dx + wy * -dy + wz * -dz) / d;
            double s = 1 - (c + 1) * 0.5;
            if (s >= outside)
                a += p.ConeOutsideVolume / 12;
            else if (s > inside)
                a += (p.ConeOutsideVolume / 12) * (s - inside) / (outside - inside);
        }
        if (d >= p.MaxDist)
            a = 1;
        else if (d > p.MinDist)
        {
            double t = Math.Max(0, (d - p.MinDist) / (p.MaxDist - p.MinDist));
            a += (1 - a) * t;
        }
        return a;
    }

    // Right axis of a camera of yaw `yawRad` (yaw 0 looks along +Y, positive turns left, Z up).
    public static (double X, double Y, double Z) RightOfYaw(double yawRad) => (Math.Cos(yawRad), Math.Sin(yawRad), 0);
}
