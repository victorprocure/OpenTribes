// Footstep surface type under a foot (docs/audio.md 3.2), synthetic container (R5, R9).
namespace OpenTribes.Tests;

using System.Runtime.CompilerServices;
using OpenTribes.Client.Audio;
using OpenTribes.Script;
using OpenTribes.World;

public class FootSurfaceTests
{
    private static Container FloorAt(float z, int type)
    {
        Numbers.EnterScriptFpuMode();
        var c = new Container();
        var slot = new StrongBox<Material?>(null);
        var e = new Container.Entry(new object(), ObjectType.InteriorType, Float6.Of(-10, -10, z - 1, 10, 10, z + 1), false,
            null)
        {
            HitMaterial = slot,
            Cast = (s, en, ref hit, ref n) =>
                    {
                        if (s.Z < z || en.Z > z)
                            return false;
                        hit = new Vec3(s.X, s.Y, z);
                        n = new Vec3(0, 0, 1);
                        slot.Value = new Material { Type = type };
                        return true;
                    }
        };
        c.Add(e);
        return c;
    }

    [Fact]
    public void InteriorFloorGivesItsMaterialType() => Assert.Equal(7, FootSurface.TypeAt(FloorAt(5, 7), 1, 1, 5.05));

    [Fact]
    public void NoSurfaceUnderFootGivesMinusOne() => Assert.Equal(-1, FootSurface.TypeAt(FloorAt(5, 7), 1, 1, 9));

    [Fact]
    public void TypeIsClampedToTheTable() => Assert.Equal(14, FootSurface.TypeAt(FloorAt(0, 40), 0, 0, 0.1));
}
