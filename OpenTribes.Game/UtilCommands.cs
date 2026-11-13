// Port of src/game/util_commands.hpp and src/game/util_commands.cpp: utility commands that read game state:
// getIntegerTime, getClientByName, getNumItems, getObjectByTargetIndex, findPointOnSphere (docs/script.md 6.7). The
// f32 helper of the file is a plain store in the current mode (Fpu.Store).
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>The utility script commands that read game state: getIntegerTime, getClientByName, getNumItems, getObjectByTargetIndex, findPointOnSphere (docs/script.md 6.7).</summary>
public static class UtilCommands
{
    // src/game/util_commands.cpp sum3.
    private static double Sum3(double a, double b, double c) => Add(Add(a, b), c);

    /// <summary>Reads the radius stored in a shape file header (docs/interiors.md 8.2).</summary>
    /// <param name="d">The shape file bytes.</param>
    /// <param name="radius">Receives the radius.</param>
    /// <returns>False for malformed data.</returns>
    public static bool ReadShapeRadius(byte[] d, out float radius)
    {
        radius = 0;
        long size = d.Length;
        if (size < 14 || d[0] != 'P' || d[1] != 'E' || d[2] != 'R' || d[3] != 'S')
            return false;
        var nameLen = BitConverter.ToUInt16(d, 8);
        var pos = 10 + ((nameLen + 1L) & ~1L);
        if (pos + 4 > size)
            return false;
        var version = BitConverter.ToUInt32(d, (int)pos);
        pos += 4;
        if (version is < 1 or > 8)
            return false;
        pos += 4 * (9 + (version >= 2 ? 1 : 0) + (version >= 4 ? 1 : 0));
        if (pos + 4 > size)
            return false;
        radius = BitConverter.ToSingle(d, (int)pos);
        return true;
    }

    /// <summary>Computes the findPointOnSphere result after argument parsing.</summary>
    /// <param name="from">The ray origin.</param>
    /// <param name="to">The sphere centre.</param>
    /// <param name="dir">The ray direction.</param>
    /// <param name="radius">The shape radius. The sphere radius is this minus 0.5.</param>
    /// <param name="output">Receives the unit vector from the point where the ray meets the sphere back to <paramref name="to"/>.</param>
    /// <returns>False when the direction or the result is shorter than 0.01, which the script sees as "0 0 0".</returns>
    /// <remarks>Follows findPointOnSphere (0x0041ebf4).</remarks>
    public static bool FindPointOnSphere(ReadOnlySpan<float> from, ReadOnlySpan<float> to, ReadOnlySpan<float> dir, float radius, Span<float> output)
    {
        var r = Store(Sub(radius, 0.5f));
        var len = Store(Sqrt(Sum3(Mul(dir[0], dir[0]), Mul(dir[1], dir[1]), Mul(dir[2], dir[2]))));
        if (!(len >= 0.01))
            return false;
        Span<float> unit = stackalloc float[3], d = stackalloc float[3], w = stackalloc float[3];
        for (var i = 0; i < 3; ++i)
            unit[i] = Store(Div(dir[i], len));
        for (var i = 0; i < 3; ++i)
            d[i] = Store(Sub(to[i], from[i]));
        var b = Store(Sum3(Mul(d[0], unit[0]), Mul(d[1], unit[1]), Mul(d[2], unit[2])));
        var dd = Sum3(Mul(d[0], d[0]), Mul(d[1], d[1]), Mul(d[2], d[2]));
        var disc = Store(Sub(Mul(r, r), Sub(dd, Mul(b, b))));
        float t;
        if (float.IsNaN(disc) && !float.IsNegative(disc))
        {
            t = disc; // the original's sqrt wrapper reports a domain error here (DECISIONS D11)
        }
        else
        {
            var arg = disc >= 0 ? disc : Mul(disc, -1.0f);
            t = Store(Sqrt(arg));
        }
        var s = Store(Sub(b, t));
        for (var i = 0; i < 3; ++i)
        {
            var step = Store(Mul(unit[i], s));
            var point = Store(Add(from[i], step));
            w[i] = Store(Sub(to[i], point));
        }
        var len2 = Store(Sqrt(Sum3(Mul(w[0], w[0]), Mul(w[1], w[1]), Mul(w[2], w[2]))));
        if (!(len2 >= 0.01))
            return false;
        for (var i = 0; i < 3; ++i)
            output[i] = Store(Div(w[i], len2));
        return true;
    }

    /// <summary>Registers the utility commands.</summary>
    /// <param name="interp">The script interpreter.</param>
    /// <param name="game">The game state the commands act on.</param>
    public static void Register(Interpreter interp, GameState game)
    {
        // getIntegerTime(bool): "<ticks>.<rest>" of the 32 ms server tick counter (0x006a841c) or, for false, the
        // client-side counter (0x006a83a0), which stays 0 on a dedicated server.
        interp.AddCommand("getIntegerTime", (_, a) =>
        {
            if (a.Length != 2)
                return "";
            var t = Numbers.ParseBool(a[1]) && game.Sim != null ? game.Sim.TickMs() : 0;
            return Numbers.FormatInt((int)(t >> 5)) + "." + Numbers.FormatInt((int)(t & 0x1f));
        });

        interp.AddCommand("getClientByName", (_, a) =>
        {
            if (a.Length != 2)
                return "0";
            foreach (var id in game.Players.Humans())
                if (game.Players.Find(id) is ClientRecord c && GameUtil.EqualsNoCase(c.Name, a[1]))
                    return Numbers.FormatInt(c.Id);
            return "0";
        });

        interp.AddCommand("getNumItems", (inr, a) =>
        {
            if (a.Length != 1)
            {
                inr.Print(1, "wrong argcount to " + a[0] + "()");
                return null;
            }
            return Numbers.FormatInt(game.DataBlockCount(5));
        });

        // The original indexes a 128 entry table without a bound; indexes outside it read other memory (DECISIONS D11:
        // they give -1).
        interp.AddCommand("getObjectByTargetIndex", (_, a) =>
        {
            if (a.Length != 2)
                return "-1";
            var i = Numbers.BorlandAtoi(a[1]);
            if (i >= 0 && i < 128 && i < game.Targets.Count && game.Targets[i] is GameBase t)
                return Numbers.FormatInt(t.Id());
            return "-1";
        });

        interp.AddCommand("findPointOnSphere", (inr, a) =>
        {
            if (a.Length != 5)
            {
                inr.Print(1, a[0] + ": <playerPos, objectPos, vec, object>");
                return "0 0 0";
            }
            float[] from = new float[3], to = new float[3], dir = new float[3];
            if (MathCommands.ScanFloats3(a[1], from) + MathCommands.ScanFloats3(a[2], to) + MathCommands.ScanFloats3(a[3], dir) != 9)
                return "0 0 0";
            if (game.Sim?.Server()?.Find(a[4]) is not GameBase gb || !gb.IsShapeBase() || gb.DataBlockName == null || game.Resources == null)
                return "0 0 0";
            var block = game.Sim.DataBlocks().Find(gb.DataBlockName);
            if (block == null || !game.Sim.DataBlocks().GetField(block, "shapeFile", "", out var shape) || shape.Length == 0)
                return "0 0 0";
            if (!game.Resources.Read(shape + ".dts", out var data) || !ReadShapeRadius(data, out var radius))
                return "0 0 0";
            var output = new float[3];
            if (FindPointOnSphere(from, to, dir, radius, output))
                return MathCommands.FormatF32(output[0]) + " " + MathCommands.FormatF32(output[1]) + " " + MathCommands.FormatF32(output[2]);
            return "0 0 0";
        });

        BacklogCommands.Register(interp, game); // docs/script.md 6.8
    }
}
