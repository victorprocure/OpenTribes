using OpenTribes.Client.Shapes;
using OpenTribes.Client.World;

namespace OpenTribes.Tests;

// world-render.md 3.7 step 5 (ITRInstance_mergeSunLight 005581a0): planet light on outside-visible surfaces.
public class InteriorSunTests
{
    private static InteriorGeometryData Geo(byte flags0, byte flags1, float nz)
    {
        var g = new InteriorGeometryData { Planes = new float[] { 0, 0, nz, 0 } };
        g.Surfaces = new[]
        {
            new InteriorGeometryData.Surface { Flags = flags0, Plane = 0 },
            new InteriorGeometryData.Surface { Flags = flags1, Plane = 0 },
        };
        return g;
    }

    [Fact]
    public void SunAddsAmbientPlusSunTimesHalfDotOnOutsideSurfacesOnly()
    {
        var sun = new LightSet { AmbR = 0.25f, AmbG = 0.25f, AmbB = 0.25f, SunX = 0, SunY = 0, SunZ = 1, SunR = 0.5f, SunG = 0.5f, SunB = 0.5f };
        var maps = new ushort[]?[] { new ushort[] { 0x0000, 0x0111 }, new ushort[] { 0x0000 } };
        // surface 0: plane front, visible to outside (0x80 | 0x40); surface 1: not visible to outside
        InteriorLightMaps.AddSun(maps, Geo(0xc0, 0x80, 1), sun);
        // t = (1 + 1) / 2 = 1: c = 0.75, nibble trunc(0.75 * 16) = 12 per channel and mono
        Assert.Equal(0xcccc, maps[0]![0]);
        Assert.Equal(0xcddd, maps[0]![1]);            // rgb 12 + 1 = 13, mono nibble 0 + 12
        Assert.Equal(0x0000, maps[1]![0]);
    }

    [Fact]
    public void SunClampsNibblesAndNegatesBackPlanes()
    {
        var sun = new LightSet { AmbR = 0.5f, AmbG = 0.5f, AmbB = 0.5f, SunZ = 1, SunR = 0.9f, SunG = 0.9f, SunB = 0.9f };
        var maps = new ushort[]?[] { new ushort[] { 0x0fff }, new ushort[] { 0 } };
        InteriorLightMaps.AddSun(maps, Geo(0xc0, 0x40, 1), sun);   // second surface: plane front clear, so the normal is (0, 0, -1)
        Assert.Equal(0xffff, maps[0]![0]);             // c = 1.4 clamps to 1, 16 saturates at 15
        // t = 0: no sun term from the light, nothing is added (the stock test is t > 0)
        Assert.Equal(0, maps[1]![0]);
    }
}
