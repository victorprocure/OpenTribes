// Port of tests/test_mission.cpp, the sim part: Euler transforms (docs/mission.md 5.1) and the fixed ids of a new
// server. The script cases of its main (teams, GameBase registration, persist fields, the GameBase/Group/team
// commands) and their `c.failures == 0` check need the game layer: stage P4.
using OpenTribes.Core;
using OpenTribes.Script;
using OpenTribes.Sim;

namespace OpenTribes.Tests;

public class MissionTests
{
    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    // 5.1 Euler to matrix. Expected words: the formula of 0x005ed42e evaluated exactly, each element truncated toward
    // zero to f32 (none lies within 2^-30 ulp of a float boundary).
    private static void EulerCase(float x, float y, float z, uint[] want)
    {
        float[] e = { x, y, z };
        var t = new Transform();
        t.Flags = 6; // bit 1 (translation) kept, bit 2 cleared
        MissionTransform.EulerToMatrix(e, ref t);
        Assert.True(t.Flags == 3);
        for (int i = 0; i < 9; ++i)
        {
            if (Bits(t.M[i]) != want[i])
                Console.Error.WriteLine($"euler {x} {y} {z} m{i}: {Bits(t.M[i]):x8} want {want[i]:x8}");
            Assert.True(Bits(t.M[i]) == want[i]);
        }
    }

    [Fact]
    public void TestEuler()
    {
        Fpu.TowardZero = false; // process start
        Numbers.EnterScriptFpuMode();
        // z only: cos 7, sin 7 (round to nearest would give 0x3f40ffbd for cos 7).
        uint[] z7 = { 0x3f40ffbc, 0x3f283046, 0, 0xbf283046, 0x3f40ffbc, 0, 0, 0, 0x3f800000 };
        EulerCase(0, 0, 7, z7);
        uint[] general = { 0x3f1f9181, 0x3f405050, 0x3e5e5406, 0xbf3d0bbb, 0x3ef2c515, 0x3ef57743, 0x3e83b0ce, 0xbeeb17b3, 0x3f59ad4d };
        EulerCase(0.5f, -0.25f, 1.0f, general);
        uint[] x16 = { 0x3f800000, 0, 0, 0, 0xbcef33e2, 0x3f7fe40e, 0, 0xbf7fe40e, 0xbcef33e2 };
        EulerCase(1.6f, 0, 0, x16);
        // x == 0 but y != 0 takes the general branch: m3 = -cx * sz = -0.
        uint[] y75 = { 0x3f3b4ff6, 0, 0xbf2e7fe0, 0x80000000, 0x3f800000, 0, 0x3f2e7fe0, 0, 0x3f3b4ff6 };
        EulerCase(0, 0.75f, 0, y75);

        // All zero (also -0): identity, flags bits 0 and 2 cleared.
        float[] zeros = { 0, -0.0f, -0.0f };
        var t = new Transform();
        t.Flags = 7;
        MissionTransform.EulerToMatrix(zeros, ref t);
        Assert.True(t.Flags == 2);
        for (int i = 0; i < 9; ++i)
            Assert.True(t.M[i] == (i % 4 == 0 ? 1.0f : 0.0f));

        // Matrix to Euler: flag bit 0 clear reads 0 0 0; m5 == 1 is the singular branch (x = asin(1) truncated:
        // 0x3fc90fda, y = 0, z = atan2(m1, m0)).
        float[] e = { 9, 9, 9 };
        var rot = new Transform();
        rot.Flags = 0;
        rot.M[5] = 1;
        MissionTransform.MatrixToEuler(rot, e);
        Assert.True(e[0] == 0 && e[1] == 0 && e[2] == 0);
        rot = new Transform();
        rot.Flags = 1;
        float[] m = { 1, 0, 0, 0, 0, 1, 0, -1, 0 };
        for (int i = 0; i < 9; ++i)
            rot.M[i] = m[i];
        MissionTransform.MatrixToEuler(rot, e);
        Assert.True(Bits(e[0]) == 0x3fc90fda && Bits(e[1]) == 0 && e[2] == 0);
    }

    // main after testEuler: the world, then the script cases (P4), then the fixed ids of the new server (sim.md 2.2).
    [Fact]
    public void TestMissionWorld()
    {
        Numbers.EnterScriptFpuMode();
        string root = Path.Combine(TestPaths.TmpDir(""), "mission_root");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "base"));
        var w = new Fixtures.World(root);
        // P4: Cases c; the c.check cases of 2.1/2.2 teams, GameBase registration (1.2, TM6, GB1-GB7), persist fields
        //     (3.3), sets (3.1), the 4.1 and 4.2 commands, constructor-fixed ids (3.5) and assert(c.failures == 0).
        foreach (int id in new[] { 5, 2, 25, 1, 3, 38, 36, 34, 7, 12, 9, 23, 13, 14, 6, 44, 45, 718, 719, 725, 727, 723 })
            Assert.True(w.Sim.Server()!.FindById(id) != null);
    }
}
