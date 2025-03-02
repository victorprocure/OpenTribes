// Port of src/game/r250.hpp and the StaticRandom part of src/game/static_sim.cpp: the R250 shift-register generator of
// the engine (docs/static-sim.md 1.7, 5.4, 11.3): constructor 0x005eb5a0 and seed fill 0x005eb5d4, draw 0x005eb660. The
// original keeps several function-static instances, each seeded with 0x8377 on first use and never reseeded, so their
// state runs across missions for the life of the process.
namespace OpenTribes.Game;

// src/game/r250.hpp class R250.
public sealed class R250
{
    private readonly uint[] mTable = new uint[250];
    private uint mIndex;
    private uint mState;

    public R250(uint seed = 0x8377) => Reseed(seed);

    // src/game/r250.hpp R250::reseed: 0x005eb5d4.
    public void Reseed(uint seed)
    {
        mIndex = 0;
        mState = seed;
        for (int i = 0; i < 250; ++i)
            mTable[i] = Lcg();
        for (int i = 0; i < 250; ++i)
            if (Lcg() > 0x40000000u)
                mTable[i] |= 0x80000000u;
        uint keep = 0xffffffffu, set = 0x80000000u;
        for (int i = 0; i < 32; ++i)
        {
            ref uint w = ref mTable[i * 7 + 3];
            w = (w & keep) | set;
            keep >>= 1;
            set >>= 1;
        }
    }

    // src/game/r250.hpp R250::next: 0x005eb660: table[i] ^= table[(i + 103) mod 250].
    public uint Next()
    {
        uint tap = mIndex < 0x93 ? mIndex + 0x67 : mIndex - 0x93;
        uint v = mTable[mIndex] ^ mTable[tap];
        mTable[mIndex] = v;
        mIndex = mIndex < 0xf9 ? mIndex + 1 : 0;
        return v;
    }

    public uint[] Table() => mTable;
    public uint Index() => mIndex;

    // src/game/r250.hpp R250::unit: u * 2^-32 as the engine computes it.
    public double Unit() => OpenTribes.Core.Fpu.Mul(Next(), 2.3283064365386963e-10);

    private uint Lcg()
    {
        mState = unchecked(mState * 0x19660du + 0x3c6ef35fu);
        return mState;
    }
}

// src/game/r250.hpp struct StaticRandom: the engine's function-static generators (static_sim.cpp).
public static class StaticRandom
{
    private static readonly R250 sThread = new(), sItem = new(), sTurret = new(), sAi = new();

    public static R250 Thread() => sThread;   // 0x0068486c: thread speed at every apply (1.7)
    public static R250 Item() => sItem;       // 0x00685194: Item throw (5.4)
    public static R250 Turret() => sTurret;   // turret deflection (4.7)
    public static R250 Ai() => sAi;           // 0x00663cf8: AI aim error and trigger

    // src/game/static_sim.cpp StaticRandom::ambient: 0x00684c68, never seeded, all zero (1.1).
    public static uint Ambient() => 0;

    // src/game/static_sim.cpp StaticRandom::reset: re-creates them as at process start.
    public static void Reset()
    {
        sThread.Reseed(0x8377);
        sItem.Reseed(0x8377);
        sTurret.Reseed(0x8377);
        sAi.Reseed(0x8377);
    }
}
