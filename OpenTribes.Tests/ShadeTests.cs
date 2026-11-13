namespace OpenTribes.Tests;

using OpenTribes.Client.Shapes;
using OpenTribes.Client.World;
using OpenTribes.World;

public class ShadeTests
{
    [Fact]
    public void TerrainNodeTakesTheLinearSquaredFalloffInEightBitSteps() // 00576a34: entry + L * (1 - d2 / R2) * 255, then >> 4
    {
        var b = new GridBlock { Size = 4, Heights = new float[25], Squares = new byte[32] };
        var lm = new ushort[25];
        Array.Fill(lm, (ushort)0x4444);
        var t = new TerrainData(b, 1, lm);
        var set = new TerrainLightSet(new[] { new PointLight(12, 12, 2, 1f, 0f, 0.5f, 8) }, 0, 0, 0);
        var mask = set.Mask(t, 0, 1, 1);
        Assert.Equal(1u, mask);
        var nib = new byte[81 * 3];
        set.Nodes(t, 0, 1, 1, mask, nib);
        // corner (1,1): d2 = 16 + 16 + 4, k = 111.56: R 68 + 111 = 179, G 68, B 68 + 55 = 123
        Assert.Equal((11, 4, 7), (nib[0], nib[1], nib[2]));
        // a square out of range keeps the static nibbles
        Assert.Equal(0u, set.Mask(t, 0, 3, 3));
    }

    [Fact]
    public void FarLightsLightNothingAndNearOnesLightPerVertex() // 005ae964 modes 2 and 3 (factor 0 for a divisor of 0), 005aeecc mode 1
    {
        var set = new LightSet { AmbR = 0, AmbG = 0, AmbB = 0, SunR = 0, SunG = 0, SunB = 0 };
        set.Add(new PointLight(0, 0, 100, 1, 1, 1, 200));
        // radius 1: the light is 100 away (> 20 radii) and inside the range: mode 2 or 3, the unset divisor makes the factor 0
        Assert.Equal(0f, set.ForShape(0, 0, 0, 1).At(5, 0, 0, 0, 0, 1).r);
        // radius 6: 100 <= 20 radii, lit per vertex by the cosine to the light
        Assert.Equal(1f, set.ForShape(0, 0, 0, 6).At(0, 0, 0, 0, 0, 1).r, 3);
    }

    [Fact]
    public void SunIsHalfLambert() // 005aee50
    {
        var set = new LightSet { AmbR = 0, AmbG = 0, AmbB = 0, SunX = 0, SunY = 0, SunZ = 1, SunR = 0.8f, SunG = 0.8f, SunB = 0.8f };
        Assert.Equal(0.4f, set.At(0, 0, 0, 1, 0, 0).r, 3);
        Assert.Equal(0.8f, set.At(0, 0, 0, 0, 0, 1).r, 3);
        Assert.Equal(0f, set.At(0, 0, 0, 0, 0, -1).r, 3);
    }
}
