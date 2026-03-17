// Footstep surface type under a foot (docs/audio.md 3.2). The stock player takes it from its floor contact
// (Player_pickSurfaceType 0x00416df8: the contact with the largest upDot that has a material entry, entry +0x38),
// for terrain and interiors alike. The client approximates the contact with a short vertical line test of the prediction
// container, which reports the material entry of the surface it hit.
using OpenTribes.Core;
using OpenTribes.World;

namespace OpenTribes.Client.Audio;

public static class FootSurface
{
    // Surface type 0..14 of the first terrain or interior surface under (x, y, z), or -1 when there is none or it has no
    // material entry (the stock player then keeps its previous value, here: no result).
    public static int TypeAt(Container world, double x, double y, double z)
    {
        object? obj = null;
        Vec3 hit = default, normal = default;
        var from = new Vec3((float)x, (float)y, (float)z + 0.5f);
        var to = new Vec3((float)x, (float)y, (float)z - 0.5f);
        if (!world.CastRay(from, to, ObjectType.kTerrainType | ObjectType.kInteriorType, null, ref obj, ref hit, ref normal) || obj == null)
            return -1;
        Material? m = world.Find(obj)?.HitMaterial?.Value;
        return m == null ? -1 : Math.Clamp(m.Type, 0, 14);
    }
}
