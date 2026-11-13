namespace OpenTribes.Client.Audio;

using OpenTribes.World;

/// <summary>The footstep surface type under a foot.</summary>
/// <remarks>docs/audio.md 3.2. The stock player takes it from its floor contact (Player_pickSurfaceType 00416df8: the contact with the largest upDot that has a material entry, entry +0x38), for terrain and interiors alike. The client approximates the contact with a short vertical line test of the prediction container, which reports the material entry of the surface it hit.</remarks>
public static class FootSurface
{
    /// <summary>Finds the surface type of the first terrain or interior surface under a point.</summary>
    /// <param name="world">The container to cast the ray in.</param>
    /// <param name="x">The x position of the foot.</param>
    /// <param name="y">The y position of the foot.</param>
    /// <param name="z">The z position of the foot.</param>
    /// <returns>The type 0 to 14, or -1 when there is no surface or it has no material entry (the stock player then keeps its previous value, here: no result).</returns>
    public static int TypeAt(Container world, double x, double y, double z)
    {
        object? obj = null;
        Vec3 hit = default, normal = default;
        var from = new Vec3((float)x, (float)y, (float)z + 0.5f);
        var to = new Vec3((float)x, (float)y, (float)z - 0.5f);
        if (!world.CastRay(from, to, ObjectType.kTerrainType | ObjectType.kInteriorType, null, ref obj, ref hit, ref normal) || obj == null)
            return -1;
        var m = world.Find(obj)?.HitMaterial?.Value;
        return m == null ? -1 : Math.Clamp(m.Type, 0, 14);
    }
}
