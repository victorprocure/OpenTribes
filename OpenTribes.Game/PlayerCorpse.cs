// The client step of a dead Player: FUN_0041447c (1.11 0x0041447c, the same bytes in 1.30), called from Player_clientUpdate
// 0x00414c38 before the smoothing window when +0x1019 is set (docs/client-net.md 4.4.2). It does not move the corpse: it eases
// the pitch and roll (rotation x and y, +0x244 and +0x248) toward the slope of the ground under it.
namespace OpenTribes.Game;

public sealed partial class Player
{
    /// <summary>Runs the client step of a dead player: eases the pitch and roll toward the ground slope.</summary>
    /// <param name="dt">The step length in seconds.</param>
    /// <remarks>Follows FUN_0041447c (same bytes in 1.11 and 1.30). The ground is the higher of the terrain height (SimTerrain_getSurfaceInfo 0x005183e8) and a ray from z + 0.125 to z - 1.0 through container 7 with mask 4 (vtable +0x90). The normal is the unit normal of the hit (m_Point3F_len 0x005ed014, a true square root) or the terrain normal.</remarks>
    public void ClientCorpseStep(float dt)
    {
        GameState gs = Game();
        var ticks = 31.25 * dt;                        // the 80-bit constant at 0x004149d0 is 31.25 to 1e-17
        var c = (float)ticks;
        float px = Transform.P[0], py = Transform.P[1], pz = Transform.P[2];
        var th = -1.0e30f;                              // 0xf149f2ca
        var tn = new Vec3(0, 0, 0);
        Container.Entry? te = gs.World.TerrainOf(this);
        if (te != null && te.TerrainData != null)
        {
            float height = 0;
            Vec3 n = default;
            if (te.TerrainData.Height(px, py, ref height, ref n))
            {
                th = height;
                tn = n;
            }
        }
        var rayZ = th;
        var rayN = new Vec3(0, 0, 0);
        object? hitObj = null;
        Vec3 hit = default, hn = default;
        if (gs.World.CastRay(new Vec3(px, py, pz + 0.125f), new Vec3(px, py, pz - 1.0f), 4u, this, ref hitObj, ref hit, ref hn))
        {
            rayZ = hit.Z;
            var len = (float)Math.Sqrt(((double)hn.X * hn.X) + ((double)hn.Y * hn.Y) + ((double)hn.Z * hn.Z));
            var inv = 1.0f / len;
            rayN = new Vec3(inv * hn.X, inv * hn.Y, inv * hn.Z);
        }
        Vec3 normal;
        float ground;
        if (rayZ <= th)
        {
            normal = tn;
            ground = th;
        }
        else
        {
            normal = rayN;
            ground = rayZ;
        }
        var depth = pz - ground;
        if (!(-0.5f < depth && depth < 0.5f) || (normal.X == 0.0f && normal.Y == 0.0f && normal.Z == 0.0f))
            return;
        // The slope in the frame of the corpse: the normal turned by -yaw, then two angles from its components.
        var m = new TMat3();
        Shape.EulerMatrix([0.0f, 0.0f, -Rotation[2]], ref m);
        Span<float> v = stackalloc float[3];
        X87.MulVector([normal.X, normal.Y, normal.Z], m.M, v);
        const double halfPi = 1.5707963267948966;
        var tx = (float)(Math.Atan2(v[2], v[1]) - halfPi);
        var ty = -(float)(Math.Atan2(v[2], v[0]) - halfPi);
        float[] cur = [Rotation[0], Rotation[1], Rotation[2]];
        cur[0] = Ease(cur[0], tx, c);
        cur[1] = Ease(cur[1], ty, c);
        SetRotation(cur);
    }

    // 0x0041478c..: moves `cur` toward `target` by 0.05 * c and stops on it.
    private static float Ease(float cur, float target, float c)
    {
        if (cur < target)
        {
            cur = (float)((0.05 * c) + cur);
            if (cur > target)
                cur = target;
        }
        else if (cur > target)
        {
            cur = (float)(cur - (0.05 * c));
            if (cur < target)
                cur = target;
        }
        return cur;
    }
}
