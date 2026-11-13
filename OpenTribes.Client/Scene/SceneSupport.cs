namespace OpenTribes.Client.Scene;

using System.Globalization;
using OpenTribes.Client.Shapes;
using OpenTribes.Game;
using OpenTribes.Platform.Gfx;

/// <summary>Read access to the client datablocks: group and index to named fields (client-net.md 2.6).</summary>
public sealed class Db
{
    private readonly ClientDataBlocks _blocks;
    /// <summary>Creates the reader.</summary>
    /// <param name="blocks">The datablocks of the client.</param>
    public Db(ClientDataBlocks blocks) => _blocks = blocks;

    /// <summary>Gets a datablock.</summary>
    /// <param name="group">The datablock group.</param>
    /// <param name="index">The index in the group.</param>
    /// <returns>The block, or null when it does not exist.</returns>
    public ClientDataBlock? Block(int group, long index) => _blocks.Blocks.TryGetValue((group, (int)index), out var b) ? b : null;

    /// <summary>Lists the blocks of one group in index order (the order of the data block manager).</summary>
    /// <param name="group">The datablock group.</param>
    /// <returns>The index and block of each entry.</returns>
    public IEnumerable<(int Index, ClientDataBlock Block)> Group(int group) =>
        _blocks.Blocks.Where(kv => kv.Key.Group == group).OrderBy(kv => kv.Key.Index).Select(kv => (kv.Key.Index, kv.Value));

    /// <summary>Gets the value of a field.</summary>
    /// <param name="b">The block, or null.</param>
    /// <param name="name">The field name.</param>
    /// <returns>The value, or null when the block or the field is missing.</returns>
    public static object? Get(ClientDataBlock? b, string name)
    {
        if (b == null)
            return null;
        foreach (var f in b.Fields)
            if (f.Name == name)
                return f.Value;
        return null;
    }

    /// <summary>Gets a field as text.</summary>
    /// <param name="b">The block, or null.</param>
    /// <param name="name">The field name.</param>
    /// <returns>The text, or empty when the field is missing or is not text.</returns>
    public static string Str(ClientDataBlock? b, string name) => Get(b, name) as string ?? "";
    /// <summary>Gets a field as a number.</summary>
    /// <param name="b">The block, or null.</param>
    /// <param name="name">The field name.</param>
    /// <param name="def">The value when the field is missing or not a number.</param>
    /// <returns>The number or the default.</returns>
    public static double Num(ClientDataBlock? b, string name, double def = 0) => ToDouble(Get(b, name), def);
    /// <summary>Gets a field as a flag: true when the number is not zero.</summary>
    /// <param name="b">The block, or null.</param>
    /// <param name="name">The field name.</param>
    /// <returns>The flag.</returns>
    public static bool Flag(ClientDataBlock? b, string name) => ToDouble(Get(b, name), 0) != 0;

    /// <summary>Converts a field value to a number.</summary>
    /// <param name="v">The value.</param>
    /// <param name="def">The number when the value is not numeric.</param>
    /// <returns>The number or the default.</returns>
    public static double ToDouble(object? v, double def = 0) => v switch
    {
        double d => d,
        long l => l,
        int i => i,
        float f => f,
        uint u => u,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) => r,
        _ => def,
    };

    /// <summary>Converts a field value to a vector of at least three numbers.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The vector, or three zeros when the value is not a vector.</returns>
    public static double[] Vec(object? v) => v is double[] d && d.Length >= 3 ? d : new double[3];
}

/// <summary>One of the three ShapeBase thread slots of a ghost update: <c>i:seq=S state=T fwd=F end=E</c> (client-net.md 3.2).</summary>
/// <param name="Slot">The slot index.</param>
/// <param name="Sequence">The sequence index.</param>
/// <param name="State">The thread state.</param>
/// <param name="Forward">True when the thread plays forward.</param>
/// <param name="AtEnd">True when the thread is at its end.</param>
public readonly record struct ThreadSlot(int Slot, int Sequence, int State, bool Forward, bool AtEnd)
{
    /// <summary>Parses the thread slots of a ghost update.</summary>
    /// <param name="threads">The field value, a list of slot strings.</param>
    /// <returns>The slots that parsed.</returns>
    public static List<ThreadSlot> ParseAll(object? threads)
    {
        var l = new List<ThreadSlot>();
        if (threads is not System.Collections.IEnumerable e)
            return l;
        foreach (var o in e)
            if (o is string s && TryParse(s, out var t))
                l.Add(t);
        return l;
    }

    /// <summary>Parses one thread slot.</summary>
    /// <param name="s">The slot string.</param>
    /// <param name="t">The slot on success.</param>
    /// <returns>True when the string parsed.</returns>
    public static bool TryParse(string s, out ThreadSlot t)
    {
        t = default;
        var colon = s.IndexOf(':');
        if (colon < 0)
            return false;
        var slot = int.Parse(s[..colon], CultureInfo.InvariantCulture);
        int Field(string key)
        {
            var p = s.IndexOf(key + "=", StringComparison.Ordinal);
            if (p < 0)
                return 0;
            p += key.Length + 1;
            var e = p;
            while (e < s.Length && (char.IsDigit(s[e]) || s[e] == '-'))
                ++e;
            return int.Parse(s.AsSpan(p, e - p), CultureInfo.InvariantCulture);
        }
        t = new ThreadSlot(slot, Field("seq"), Field("state"), Field("fwd") != 0, Field("end") != 0);
        return true;
    }
}

/// <summary>One image slot of a Player update: <c>i:img=N state=S AB C [f=F]</c>.</summary>
/// <param name="Slot">The slot index.</param>
/// <param name="Image">The image datablock index.</param>
/// <param name="State">The optional team value.</param>
/// <param name="Phase">The fire count.</param>
/// <param name="Triggered">The trigger flag, the first of the two flags before the fire count.</param>
/// <param name="Ammo">The ammo flag, the second of the two flags.</param>
/// <param name="Initial">The state is Fire flag of a new ghost.</param>
public readonly record struct ImageSlot(int Slot, int Image, int State, int Phase, bool Triggered = false, bool Ammo = false, bool Initial = false)
{
    /// <summary>Parses the image slots of a Player update.</summary>
    /// <param name="images">The field value, a list of slot strings.</param>
    /// <returns>The slots that parsed.</returns>
    public static List<ImageSlot> ParseAll(object? images)
    {
        var l = new List<ImageSlot>();
        if (images is not System.Collections.IEnumerable e)
            return l;
        foreach (var o in e)
        {
            if (o is not string s)
                continue;
            int colon = s.IndexOf(':'), ip = s.IndexOf("img=", StringComparison.Ordinal), sp = s.IndexOf("state=", StringComparison.Ordinal);
            if (colon < 0 || ip < 0 || sp < 0)
                continue;
            int Num(int p)
            {
                var e2 = p;
                while (e2 < s.Length && (char.IsDigit(s[e2]) || s[e2] == '-'))
                    ++e2;
                return int.Parse(s.AsSpan(p, e2 - p), CultureInfo.InvariantCulture);
            }
            // After the two flags comes the fire count int(3) (client-net.md 3.4); absent in the text, -1.
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var phase = parts.Length >= 4 && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ph) ? ph : -1;
            bool trig = parts.Length >= 3 && parts[2].Length >= 2 && parts[2][0] == '1', ammo = parts.Length >= 3 && parts[2].Length >= 2 && parts[2][1] == '1';
            l.Add(new ImageSlot(int.Parse(s[..colon], CultureInfo.InvariantCulture), Num(ip + 4), Num(sp + 6), phase, trig, ammo, s.EndsWith("f=1", StringComparison.Ordinal)));
        }
        return l;
    }
}

/// <summary>What a visual needs to build its images for one frame.</summary>
public sealed class FrameContext
{
    /// <summary>The camera.</summary>
    public ref Camera3D Camera => ref _camera;
    private Camera3D _camera;
    /// <summary>The shape image builder.</summary>
    public ShapeRenderer Renderer { get; set; } = null!;
    /// <summary>The shape loader.</summary>
    public ShapeAssets Assets { get; set; } = null!;
    /// <summary>The scene light.</summary>
    public LightSet Light { get; set; } = LightSet.Default();
    /// <summary>The render lists the images go to.</summary>
    public RenderLists Lists { get; set; } = new();
    /// <summary>Finds the container interior (instance index) that holds a point, -1 for none (<c>InteriorRenderer.ContainerOf</c>).</summary>
    public Func<double[], int>? ContainerOf { get; set; }
    /// <summary>The demo time in seconds.</summary>
    public double Time { get; set; }
    /// <summary>The distance where the haze starts.</summary>
    public double HazeStart { get; set; } = 1e9;
    /// <summary>The visible distance.</summary>
    public double Visible { get; set; } = 1e9;
    /// <summary>True when the view is the first person view of the control object.</summary>
    public bool FirstPersonOfControl { get; set; }
    /// <summary>The ghost index of the control object, -1 for none.</summary>
    public int ControlGhost { get; set; } = -1;

    /// <summary>Creates the draw parameters of a shape with the light and haze of the frame.</summary>
    /// <param name="toWorld">The transform of the shape to the world.</param>
    /// <param name="label">The label of the render image.</param>
    /// <returns>The parameters.</returns>
    public ShapeDraw Draw(Xf toWorld, string label = "") => new()
    {
        ToWorld = toWorld,
        Light = Light,
        HazeStart = HazeStart,
        Visible = Visible,
        Label = label,
    };
}

/// <summary>A transient effect that builds its own images.</summary>
public interface IEffect
{
    /// <summary>True while the effect lives.</summary>
    public bool Alive { get; }
    /// <summary>Advances the effect.</summary>
    /// <param name="dt">The time step in seconds.</param>
    public void Tick(double dt);
    /// <summary>Adds the images of the effect to the frame.</summary>
    /// <param name="ctx">The frame context.</param>
    public void Build(FrameContext ctx);
    /// <summary>The point light the effect gives the shapes near it (4.9), or null.</summary>
    public PointLight? Light { get; }
}

/// <summary>Transform helpers for the images of the scene.</summary>
public static class Mathx
{
    /// <summary>Builds a transform that places a shape at a point with its +Y axis along a unit direction and +Z as close to the up vector as possible.</summary>
    /// <param name="px">The x position.</param>
    /// <param name="py">The y position.</param>
    /// <param name="pz">The z position.</param>
    /// <param name="fx">The forward x.</param>
    /// <param name="fy">The forward y.</param>
    /// <param name="fz">The forward z.</param>
    /// <param name="ux">The up x.</param>
    /// <param name="uy">The up y.</param>
    /// <param name="uz">The up z.</param>
    /// <returns>The transform.</returns>
    public static Xf Basis(double px, double py, double pz, double fx, double fy, double fz, double ux = 0, double uy = 0, double uz = 1)
    {
        double rx = (fy * uz) - (fz * uy), ry = (fz * ux) - (fx * uz), rz = (fx * uy) - (fy * ux);
        var rl = Math.Sqrt((rx * rx) + (ry * ry) + (rz * rz));
        if (rl < 1e-6)
        {
            (ux, uy, uz) = (1, 0, 0);
            rx = (fy * uz) - (fz * uy);
            ry = (fz * ux) - (fx * uz);
            rz = (fx * uy) - (fy * ux);
            rl = Math.Sqrt((rx * rx) + (ry * ry) + (rz * rz));
        }
        rx /= rl;
        ry /= rl;
        rz /= rl;
        double zx = (ry * fz) - (rz * fy), zy = (rz * fx) - (rx * fz), zz = (rx * fy) - (ry * fx);
        return new Xf { M0 = rx, M1 = ry, M2 = rz, M3 = fx, M4 = fy, M5 = fz, M6 = zx, M7 = zy, M8 = zz, Px = px, Py = py, Pz = pz };
    }

    /// <summary>Builds the image orientation of a rocket (00529e6c): +Y along the unit direction, right = dir x up with up = +Z. When the direction is within 0.95 of vertical, z = (1, 0, 0) x dir and right = dir x z, a quarter turn about the direction from the generic rule.</summary>
    /// <param name="px">The x position.</param>
    /// <param name="py">The y position.</param>
    /// <param name="pz">The z position.</param>
    /// <param name="fx">The direction x.</param>
    /// <param name="fy">The direction y.</param>
    /// <param name="fz">The direction z.</param>
    /// <returns>The transform.</returns>
    public static Xf RocketBasis(double px, double py, double pz, double fx, double fy, double fz)
    {
        if (Math.Abs(fz) < 0.95)
            return Basis(px, py, pz, fx, fy, fz);
        double zx = (0 * fz) - (0 * fy), zy = (0 * fx) - (1 * fz), zz = (1 * fy) - (0 * fx);     // (1, 0, 0) x f
        var zl = Math.Sqrt((zx * zx) + (zy * zy) + (zz * zz));
        zx /= zl;
        zy /= zl;
        zz /= zl;
        double rx = (fy * zz) - (fz * zy), ry = (fz * zx) - (fx * zz), rz = (fx * zy) - (fy * zx); // f x z
        return new Xf { M0 = rx, M1 = ry, M2 = rz, M3 = fx, M4 = fy, M5 = fz, M6 = zx, M7 = zy, M8 = zz, Px = px, Py = py, Pz = pz };
    }

    /// <summary>Builds a rotation of an angle about the local +Y axis of the basis, applied before the basis (the spin of a facing quad).</summary>
    /// <param name="angle">The angle in radians.</param>
    /// <returns>The transform.</returns>
    public static Xf SpinY(double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return new Xf { M0 = c, M2 = s, M4 = 1, M6 = -s, M8 = c };
    }
}
