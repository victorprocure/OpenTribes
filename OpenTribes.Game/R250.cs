// Port of src/game/r250.hpp and the StaticRandom part of src/game/static_sim.cpp: the R250 shift-register generator of
// the engine (docs/static-sim.md 1.7, 5.4, 11.3): constructor 0x005eb5a0 and seed fill 0x005eb5d4, draw 0x005eb660. The
// original keeps several function-static instances, each seeded with 0x8377 on first use and never reseeded, so their
// state runs across missions for the life of the process.
namespace OpenTribes.Game;

/// <summary>The engine's R250 shift-register random generator.</summary>
/// <remarks>Port of src/game/r250.hpp. Constructor 0x005eb5a0, seed fill 0x005eb5d4, draw 0x005eb660 (docs/static-sim.md 1.7, 5.4, 11.3).</remarks>
public sealed class R250
{
    private readonly uint[] _table = new uint[250];
    private uint _index;
    private uint _state;

    /// <summary>Creates a generator and seeds it.</summary>
    /// <param name="seed">The seed, 0x8377 by default as the engine's static instances use.</param>
    public R250(uint seed = 0x8377) => Reseed(seed);

    /// <summary>Refills the table from the seed and resets the index.</summary>
    /// <param name="seed">The seed of the linear congruential fill.</param>
    /// <remarks>Follows the stock seed fill (0x005eb5d4).</remarks>
    public void Reseed(uint seed)
    {
        _index = 0;
        _state = seed;
        for (var i = 0; i < 250; ++i)
            _table[i] = Lcg();
        for (var i = 0; i < 250; ++i)
            if (Lcg() > 0x40000000u)
                _table[i] |= 0x80000000u;
        uint keep = 0xffffffffu, set = 0x80000000u;
        for (var i = 0; i < 32; ++i)
        {
            ref var w = ref _table[(i * 7) + 3];
            w = (w & keep) | set;
            keep >>= 1;
            set >>= 1;
        }
    }

    /// <summary>Draws the next 32-bit value: <c>table[i] ^= table[(i + 103) mod 250]</c>.</summary>
    /// <returns>The new table word.</returns>
    /// <remarks>Follows the stock draw (0x005eb660).</remarks>
    public uint Next()
    {
        var tap = _index < 0x93 ? _index + 0x67 : _index - 0x93;
        var v = _table[_index] ^ _table[tap];
        _table[_index] = v;
        _index = _index < 0xf9 ? _index + 1 : 0;
        return v;
    }

    /// <summary>Returns the 250-word state table.</summary>
    /// <returns>The live table, not a copy.</returns>
    public uint[] Table() => _table;
    /// <summary>Returns the current table index.</summary>
    /// <returns>The index of the next word to update.</returns>
    public uint Index() => _index;

    /// <summary>Draws a value scaled to [0, 1) as the engine computes it (<c>u * 2^-32</c>).</summary>
    /// <returns>The scaled draw.</returns>
    public double Unit() => Fpu.Mul(Next(), 2.3283064365386963e-10);

    private uint Lcg()
    {
        _state = unchecked((_state * 0x19660du) + 0x3c6ef35fu);
        return _state;
    }
}

/// <summary>The engine's function-static random generators, each seeded with 0x8377 on first use (src/game/static_sim.cpp).</summary>
public static class StaticRandom
{
    private static readonly R250 ThreadGenerator = new(), ItemGenerator = new(), TurretGenerator = new(), AiGenerator = new();

    /// <summary>Returns the generator that picks thread speed at every apply (0x0068486c, docs/static-sim.md 1.7).</summary>
    /// <returns>The shared generator.</returns>
    public static R250 Thread() => ThreadGenerator;
    /// <summary>Returns the generator used by item throws (0x00685194, docs/static-sim.md 5.4).</summary>
    /// <returns>The shared generator.</returns>
    public static R250 Item() => ItemGenerator;
    /// <summary>Returns the generator used by turret deflection (docs/static-sim.md 4.7).</summary>
    /// <returns>The shared generator.</returns>
    public static R250 Turret() => TurretGenerator;
    /// <summary>Returns the generator used by AI aim error and trigger (0x00663cf8).</summary>
    /// <returns>The shared generator.</returns>
    public static R250 Ai() => AiGenerator;

    /// <summary>Returns the ambient value, which the engine never seeds (0x00684c68, docs/static-sim.md 1.1).</summary>
    /// <returns>Always zero.</returns>
    public static uint Ambient() => 0;

    /// <summary>Re-creates all generators as at process start.</summary>
    public static void Reset()
    {
        ThreadGenerator.Reseed(0x8377);
        ItemGenerator.Reseed(0x8377);
        TurretGenerator.Reseed(0x8377);
        AiGenerator.Reseed(0x8377);
    }
}
