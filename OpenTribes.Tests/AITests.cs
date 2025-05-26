// Port of tests/test_ai.cpp: AI players (ROADMAP G10, docs/ai-sim.md): commands and their texts (6), directive order rule
// (2.2), typed variables (5.1), the manager tick (1.6), steering (3.3), the trigger (3.6), callbacks (4) and the record
// bookkeeping (1.5, 7). Synthetic flat terrain, no game data. Expected texts and sequences are those the reference
// server printed (tools/aidiff.py). 52 asserts, one to one.
using OpenTribes.Game;
using OpenTribes.World;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Tests;

public class AITests
{
    // ---- the AI math (3.3, 3.5, 5.2) ----

    private static void TestMath()
    {
        var s = new AISkill();
        s.Build(false);
        // Constructor block = iq 0 (5.2).
        Assert.True(s.Think == 50 && s.Spot == 50.0f && s.MaxSpeed == 0.4f);
        s.ApplyIq(1.0f);
        Assert.True(s.Think == 3 && s.Spot == 150.0f && s.MaxSpeed == 1.0f && s.Range == 145.0f && s.Skill == 1.0f);
        s.ApplyIq(0.0f);
        Assert.True(s.Think == 50 && s.Range == 55.0f && s.MinAccuracy == 0.0f);
        s.ApplyIq(0.5f);
        Assert.True(s.Think == 50 + (int)(-47 * 0.5) && s.Spot == 100.0f);
        // accuracy = (minAccuracy - skill) * clamp(dist / range) + skill
        s.ApplyIq(1.0f);
        Assert.True(s.Accuracy(0.0f) == 1.0f && s.Accuracy(145.0f) == s.MinAccuracy && s.Accuracy(500.0f) == s.MinAccuracy);
        // The yaw rate triple (r, 2r, 0.1r).
        var d = new AISkill();
        d.Build(false);
        Assert.True(d.Rate[1] == MulF(2.0f, d.Rate[0]));
    }

    // The aim solver of the projectile datablocks (docs/ai-sim.md 3.7, 12.4; 0x0045b99c, 0x00460b5c, 0x0045ed40).
    private static void TestAimSolver()
    {
        using var chop = X87.Chop();
        float[] s = { 0, 0, 0 };
        float[] dir = new float[3];
        bool ok = false;
        // Bullet and Rocket: the straight line; ok when speed x totalTime is above the distance; no high arc.
        float[] far = { 300, 400, 0 };
        Assert.True(AIAim.AiSolveAim(Projectile.kBulletGroup, 100.0f, 10.0f, s, far, false, -20.0f, ref ok, dir) && ok);
        Assert.True(Math.Abs(SubF(dir[0], 0.6f)) < 1e-6f && Math.Abs(SubF(dir[1], 0.8f)) < 1e-6f && dir[2] == 0.0f);
        Assert.True(AIAim.AiSolveAim(Projectile.kBulletGroup, 100.0f, 4.0f, s, far, false, -20.0f, ref ok, dir) && !ok); // 400 < 500
        Assert.True(!AIAim.AiSolveAim(Projectile.kBulletGroup, 100.0f, 10.0f, s, far, true, -20.0f, ref ok, dir));
        Assert.True(AIAim.AiSolveAim(Projectile.kRocketGroup, 100.0f, 4.0f, s, far, false, -20.0f, ref ok, dir) && !ok);
        Assert.True(AIAim.AiSolveAim(Projectile.kSeekerGroup, 100.0f, 4.0f, s, far, false, -20.0f, ref ok, dir) && ok); // the base solver
        Assert.True(!AIAim.AiSolveAim(Projectile.kSeekerGroup, 100.0f, 4.0f, s, far, true, -20.0f, ref ok, dir));
        // Grenade: both arcs land on the target under the gravity -20 (the low arc is flatter).
        float[] tgt = { 150, 200, 12 };
        float[] lo = new float[3], hi = new float[3];
        bool okLo = false, okHi = false;
        Assert.True(AIAim.AiSolveAim(Projectile.kGrenadeGroup, 100.0f, 10.0f, s, tgt, false, -20.0f, ref okLo, lo) && okLo);
        Assert.True(AIAim.AiSolveAim(Projectile.kGrenadeGroup, 100.0f, 10.0f, s, tgt, true, -20.0f, ref okHi, hi) && okHi);
        Assert.True(lo[2] < hi[2]);
        foreach (float[] d in new[] { lo, hi })
        {
            double hl = Sqrt(Add(Mul(d[0], d[0]), Mul(d[1], d[1])));
            double dist = Sqrt(Add(Mul(150.0, 150.0), Mul(200.0, 200.0)));
            double T = Div(dist, Mul(100.0, hl));
            double z = Sub(Mul(Mul(100.0, d[2]), T), Mul(Mul(10.0, T), T));
            Assert.True(Math.Abs(Sub(z, 12.0)) < 0.05);
            Assert.True(Math.Abs(Sub(Sqrt(Add(Add(Mul(d[0], d[0]), Mul(d[1], d[1])), Mul(d[2], d[2]))), 1.0)) < 1e-5);
        }
        // Out of reach: the straight line with ok false, and no high arc when the discriminant is under 1.
        float[] distant = { 5000, 0, 0 };
        Assert.True(AIAim.AiSolveAim(Projectile.kGrenadeGroup, 100.0f, 10.0f, s, distant, false, -20.0f, ref ok, dir) && !ok);
        Assert.True(Math.Abs(SubF(dir[0], 1.0f)) < 1e-6f && dir[1] == 0.0f && dir[2] == 0.0f);
        Assert.True(!AIAim.AiSolveAim(Projectile.kGrenadeGroup, 100.0f, 10.0f, s, distant, true, -20.0f, ref ok, dir) || !ok);
    }

    [Fact]
    public void Main()
    {
        TestAimSolver();
        TestMath();
    }
}
