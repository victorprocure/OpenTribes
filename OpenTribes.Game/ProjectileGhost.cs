// Port of src/game/projectile_ghost.cpp: the projectile ghost frame (docs/projectile-sim.md 5.1,
// game-stream-ghosts.md 4.10). The bit writers the classes share are Pj (Projectile.cs).
namespace OpenTribes.Game;

public static partial class ProjectileGhost
{
    // src/game/projectile_ghost.cpp packProjectile: Projectile::packUpdate (0x0045ad08) and the initial/update split of
    // the class packs.
    private static uint PackProjectile(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        var p = (Projectile)obj;
        uint remaining = 0;
        // GameBase part (4.1).
        if (bs.WriteFlag((mask & 0x04) != 0))
        {
            bs.WriteInt((uint)p.Team, 4);
            if (bs.WriteFlag(p.ControlClient != 0))
                bs.WriteInt((uint)(p.ControlClient - 0x800), 7);
            if (bs.WriteFlag(p.OwnerClient != 0))
                bs.WriteInt((uint)(p.OwnerClient - 0x800), 7);
        }
        bool initial = (mask & 0x80000000u) != 0;
        bs.WriteFlag(initial);
        if (!initial)
        {
            if (bs.WriteFlag(p.Exploded()))
                p.PackExplode(bs);
        }
        else if (p.Exploded())
        {
            remaining |= mask & 0x04; // the explode data follows in the next update
        }
        bs.WriteFlag(initial);
        if (initial)
        {
            Pj.WriteIndex(bs, p.DataBlockIndex, p.DataBlockCount());
            GameBase? sh = p.Shooter();
            int ghost = sh != null ? ctx.Manager.GhostIndex(sh) : -1;
            if (bs.WriteFlag(ghost != -1))
                bs.WriteInt((uint)ghost, 10);
            p.PackInitial(bs, ctx);
        }
        else
        {
            p.PackUpdate(bs, mask, ctx);
        }
        return remaining;
    }

    // src/game/projectile_ghost.cpp registerProjectileGhostClasses.
    public static void Register()
    {
        (string cls, uint tag)[] kClasses =
        {
            ("Bullet", 0x258), ("Grenade", 0x259), ("RocketDumb", 0x25a), ("LaserProjectile", 0x25b), ("TargetLaser", 0x25c),
            ("SeekingMissile", 0x25d), ("Lightning", 0x25e), ("RepairEffect", 0x25f),
        };
        foreach ((string cls, uint tag) in kClasses)
            GhostManager.RegisterGhostClass(cls, new GhostClass { Tag = tag, Pack = PackProjectile });
    }
}
