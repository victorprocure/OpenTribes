// Shared pieces of the scene model: datablock access, ghost state parsing, the per-frame context and the effect interface.
using System.Globalization;
using OpenTribes.Client.Shapes;
using OpenTribes.Game;
using OpenTribes.Platform.Gfx;

namespace OpenTribes.Client.Scene;

// Read access to the client datablocks (client-net.md 2.6): group and index to named fields.
public sealed class Db
{
    private readonly ClientDataBlocks mBlocks;
    public Db(ClientDataBlocks blocks) => mBlocks = blocks;

    public ClientDataBlock? Block(int group, long index) => mBlocks.Blocks.TryGetValue((group, (int)index), out ClientDataBlock? b) ? b : null;

    public static object? Get(ClientDataBlock? b, string name)
    {
        if (b == null)
            return null;
        foreach (DecodedField f in b.Fields)
            if (f.Name == name)
                return f.Value;
        return null;
    }

    public static string Str(ClientDataBlock? b, string name) => Get(b, name) as string ?? "";
    public static double Num(ClientDataBlock? b, string name, double def = 0) => ToDouble(Get(b, name), def);
    public static bool Flag(ClientDataBlock? b, string name) => ToDouble(Get(b, name), 0) != 0;

    public static double ToDouble(object? v, double def = 0) => v switch
    {
        double d => d,
        long l => l,
        int i => i,
        float f => f,
        uint u => u,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) => r,
        _ => def,
    };

    public static double[] Vec(object? v) => v is double[] d && d.Length >= 3 ? d : new double[3];
}

// One of the three ShapeBase thread slots of a ghost update: "i:seq=S state=T fwd=F end=E" (client-net.md 3.2).
public readonly record struct ThreadSlot(int Slot, int Sequence, int State, bool Forward, bool AtEnd)
{
    public static List<ThreadSlot> ParseAll(object? threads)
    {
        var l = new List<ThreadSlot>();
        if (threads is not System.Collections.IEnumerable e)
            return l;
        foreach (object? o in e)
            if (o is string s && TryParse(s, out ThreadSlot t))
                l.Add(t);
        return l;
    }

    public static bool TryParse(string s, out ThreadSlot t)
    {
        t = default;
        int colon = s.IndexOf(':');
        if (colon < 0)
            return false;
        int slot = int.Parse(s[..colon], CultureInfo.InvariantCulture);
        int Field(string key)
        {
            int p = s.IndexOf(key + "=", StringComparison.Ordinal);
            if (p < 0)
                return 0;
            p += key.Length + 1;
            int e = p;
            while (e < s.Length && (char.IsDigit(s[e]) || s[e] == '-'))
                ++e;
            return int.Parse(s.AsSpan(p, e - p), CultureInfo.InvariantCulture);
        }
        t = new ThreadSlot(slot, Field("seq"), Field("state"), Field("fwd") != 0, Field("end") != 0);
        return true;
    }
}

// One image slot of a Player update: "i:img=N state=S AB C [f=F]".
public readonly record struct ImageSlot(int Slot, int Image, int State, int Phase)
{
    public static List<ImageSlot> ParseAll(object? images)
    {
        var l = new List<ImageSlot>();
        if (images is not System.Collections.IEnumerable e)
            return l;
        foreach (object? o in e)
        {
            if (o is not string s)
                continue;
            int colon = s.IndexOf(':'), ip = s.IndexOf("img=", StringComparison.Ordinal), sp = s.IndexOf("state=", StringComparison.Ordinal);
            if (colon < 0 || ip < 0 || sp < 0)
                continue;
            int Num(int p)
            {
                int e2 = p;
                while (e2 < s.Length && (char.IsDigit(s[e2]) || s[e2] == '-'))
                    ++e2;
                return int.Parse(s.AsSpan(p, e2 - p), CultureInfo.InvariantCulture);
            }
            // The trailing int(3) is the image state (player-sim.md 7.5: 1 Fire, 3 Idle, 4 Ready, 5 Reload); absent in the text, -1.
            string[] parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int phase = parts.Length >= 4 && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ph) ? ph : -1;
            l.Add(new ImageSlot(int.Parse(s[..colon], CultureInfo.InvariantCulture), Num(ip + 4), Num(sp + 6), phase));
        }
        return l;
    }
}

// What a visual needs to build its images for one frame.
public sealed class FrameContext
{
    public Camera3D Camera;
    public ShapeRenderer Renderer = null!;
    public ShapeAssets Assets = null!;
    public LightSet Light = LightSet.Default();
    public RenderLists Lists = new();
    public double Time;            // seconds of demo time
    public double HazeStart = 1e9, Visible = 1e9;
    public bool FirstPersonOfControl;
    public int ControlGhost = -1;

    public ShapeDraw Draw(Xf toWorld, string label = "") => new()
    {
        ToWorld = toWorld, Light = Light, HazeStart = HazeStart, Visible = Visible, Label = label,
    };
}

public interface IEffect
{
    bool Alive { get; }
    void Tick(double dt);
    void Build(FrameContext ctx);
    // A point light for the shapes near the effect (4.9), or null.
    PointLight? Light { get; }
}

public static class Mathx
{
    // A transform that places a shape at `p` with its +Y axis along `forward` (unit) and +Z as close to `up` as possible.
    public static Xf Basis(double px, double py, double pz, double fx, double fy, double fz, double ux = 0, double uy = 0, double uz = 1)
    {
        double rx = fy * uz - fz * uy, ry = fz * ux - fx * uz, rz = fx * uy - fy * ux;
        double rl = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        if (rl < 1e-6)
        {
            (ux, uy, uz) = (1, 0, 0);
            rx = fy * uz - fz * uy; ry = fz * ux - fx * uz; rz = fx * uy - fy * ux;
            rl = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        }
        rx /= rl; ry /= rl; rz /= rl;
        double zx = ry * fz - rz * fy, zy = rz * fx - rx * fz, zz = rx * fy - ry * fx;
        return new Xf { M0 = rx, M1 = ry, M2 = rz, M3 = fx, M4 = fy, M5 = fz, M6 = zx, M7 = zy, M8 = zz, Px = px, Py = py, Pz = pz };
    }

    // Rotation of `angle` about the local +Y axis of the basis, applied before the basis (the spin of a facing quad).
    public static Xf SpinY(double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new Xf { M0 = c, M2 = s, M4 = 1, M6 = -s, M8 = c };
    }
}
