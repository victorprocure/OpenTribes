// Round trip helper of the client tests (docs/client-net.md 3): the bytes a server pack routine wrote go through the
// client unpack routine of the same class tag. The unpack must consume exactly the bits the pack wrote.
namespace OpenTribes.Tests;

using OpenTribes.Game;
using OpenTribes.Sim;

public static class ClientRoundTrip
{
    // Datablock group of each ghost class tag that carries a datablock index (game-stream-ghosts.md 2.3, 4).
    private static readonly Dictionary<uint, int> Groups = new()
    {
        [0x202] = 19,
        [0x21b] = 21,
        [0x21e] = 23,
        [0x220] = 7,
        [0x221] = 4,
        [0x223] = 10,
        [0x229] = 8,
        [0x22a] = 5,
        [0x22c] = 9,
        [0x218] = 3,
        [0x258] = 14,
        [0x259] = 15,
        [0x25a] = 16,
        [0x25b] = 17,
        [0x25c] = 24,
        [0x25d] = 25,
        [0x25e] = 28,
        [0x25f] = 29,
    };

    // The client world with the datablock counts a server of `obj`'s class would have sent: the object's own group and
    // the explosion group (20).
    public static ClientWorld WorldFor(uint tag, SimObject? obj, int explosionCount = 3)
    {
        var w = new ClientWorld();
        if (obj is GameBase gb && Groups.TryGetValue(tag, out var group))
            w.DataBlocks.Counts[group] = gb.DataBlockCount();
        w.DataBlocks.Counts[20] = explosionCount;
        return w;
    }

    // Unpacks `buf` (`bits` valid) as an update of class `tag`; asserts the reader ends exactly at `bits`. A
    // creating update starts from an empty ghost, a later one from `prev`.
    public static ClientGhost Unpack(uint tag, byte[] buf, uint bits, bool isNew, ClientWorld world, ClientGhost? prev = null)
    {
        var (name, fn) = ClientGhostManager.Classes[tag];
        var r = new ClientReader((byte[])buf.Clone(), world);
        var g = prev ?? new ClientGhost { Tag = tag, Class = name };
        r.Out = [];
        fn(r, g, isNew);
        Assert.True(r.Pos == (int)bits, name + ": client read " + r.Pos + " bits, the pack wrote " + bits);
        foreach (var f in r.Out)
            g.State[f.Name] = f.Value;
        g.Last = r.Out;
        return g;
    }

    public static object? Field(ClientGhost g, string name) => g.Last.Where(f => f.Name == name).Select(f => f.Value).FirstOrDefault();

    public static bool Has(ClientGhost g, string name) => g.Last.Any(f => f.Name == name);

    public static double D(object? v) => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);

    public static double[] V(object? v) => (double[])v!;

    public static bool Near(double a, double b, double tol = 1e-5) => Math.Abs(a - b) <= tol;

    public static bool VecIs(object? v, double x, double y, double z) =>
        v is double[] a && a.Length == 3 && Near(a[0], x, 1e-6 * Math.Max(1, Math.Abs(x))) && Near(a[1], y, 1e-6 * Math.Max(1, Math.Abs(y))) &&
        Near(a[2], z, 1e-6 * Math.Max(1, Math.Abs(z)));
}
