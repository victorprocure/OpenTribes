// Client unpackUpdate of the Player ghost (docs/game-stream-ghosts.md 4.7, client-net.md 3.4, 0041756c) and of the
// projectile family (4.10, projectile-sim.md 5, client-net.md 3.6, 0045ad90). The pack routines are in PlayerGhost.cs
// and ProjectileGhost.cs.
namespace OpenTribes.Game;

/// <summary>The Player ghost class: pack routine, registration and client unpack (docs/game-stream-ghosts.md 4.7, docs/client-net.md 3.4).</summary>
/// <remarks>The client unpack follows 0041756c. The pack routines are in PlayerGhost.cs.</remarks>
public static partial class PlayerGhost
{
    internal static void RegisterClient() => ClientGhostManager.RegisterClass(0x218, "Player", Unpack);

    private static void Unpack(ClientReader r, ClientGhost g, bool isNew)
    {
        GhostClasses.UnShapeBase1(r, g);
        if (r.Flag())
        {
            r.AddInt("datablock", r.DbBits(3));
            r.AddFlag("flag101b");
        }
        if (r.Flag())
        {
            if (r.Flag())
            {
                long ghost = r.Int(10), point = r.Int(5);
                r.Add("mount", ghost + "/" + point);
                g.State["mountGhost"] = ghost;
            }
            else
                r.Add("mount", "none");
        }
        if (r.Flag())
        {
            var dead = r.Flag();
            if (!dead)
                r.Add("damage", r.Int(6) / 63.0);
            else
            {
                r.Add("dead", 1L);
                if (!r.Flag())
                    r.AddInt("deathAnim", 6);
            }
        }
        if (r.Flag())
        {
            r.Add("pitch", r.SUnit(5) * 1.53589);
            r.AddVec("pos");
            if (r.Flag())
            {
                var speed = r.Int(17) / 512.0;
                var n = r.Normal(10);
                r.Add("vel", new[] { n[0] * speed, n[1] * speed, n[2] * speed });
            }
            r.AddFlag("flag1f4");
            r.Add("rotZ", r.Int(9) * 6.283185307 / 511);
            if (!isNew)
            {
                var m = new Move();
                Moves.UnpackMove(r.Bs, ref m, false, false);
                if (r.Bs.Error())
                    throw new OverrunException();
                r.Add("move", ClientPsc.FormatMove(m));
                g.State[RemoteGhosts.MoveKey] = m;       // the echoed move of the step (+0xff0)
                r.Add("energy", r.Int(7) / 127.0);
                r.AddInt("f8c", 4);
                r.AddFlag("flag10000");
            }
        }
        if (r.Flag())
            r.AddInt("f98", 6);
        if (r.Flag())
            r.AddF32("f728");
        r.AddFlag("flagfed");
        r.AddFlag("flagfee");
        if (r.Flag())
        {
            var imgs = new List<object?>();
            for (var i = 0; i < 8; ++i)
                if (r.Flag())
                {
                    var img = (long)r.Int(8) - 1;
                    g.State["item" + i] = img;           // the slot's item, kept for the prediction's mass (4.4)
                    long st = r.Flag() ? r.Int(3) : -1;
                    bool a = r.Flag(), b = r.Flag();
                    long c = r.Int(3);
                    var s = i + ":img=" + img + " state=" + st + " " + (a ? 1 : 0) + (b ? 1 : 0) + " " + c;
                    if (isNew)
                        s += " f=" + (r.Flag() ? 1 : 0);
                    imgs.Add(s);
                }
            r.Add("images", imgs);
        }
    }
}

/// <summary>The projectile ghost classes: pack routine, registration and client unpack (docs/game-stream-ghosts.md 4.10, docs/projectile-sim.md 5, docs/client-net.md 3.6).</summary>
/// <remarks>The client unpack follows 0045ad90. The pack routines are in ProjectileGhost.cs.</remarks>
public static partial class ProjectileGhost
{
    internal static void RegisterClient()
    {
        ClientGhostManager.RegisterClass(0x258, "Bullet", (r, g, n) => Frame(r, g, 14, InitialBullet, ExplodeBullet, null));
        ClientGhostManager.RegisterClass(0x259, "Grenade", (r, g, n) => Frame(r, g, 15, InitialPosVel, null, UpdateGrenade));
        ClientGhostManager.RegisterClass(0x25a, "RocketDumb", (r, g, n) => Frame(r, g, 16, InitialPosVel, ExplodeRocket, UpdateRocket));
        ClientGhostManager.RegisterClass(0x25b, "LaserProjectile", (r, g, n) => Frame(r, g, 17, InitialLaser, null, null));
        ClientGhostManager.RegisterClass(0x25c, "TargetLaser", (r, g, n) => Frame(r, g, 24, InitialTargetLaser, null, UpdateTargetLaser));
        ClientGhostManager.RegisterClass(0x25d, "SeekingMissile", (r, g, n) => Frame(r, g, 25, InitialPosVel, ExplodeRocket, UpdateSeeker));
        ClientGhostManager.RegisterClass(0x25e, "Lightning", (r, g, n) => Frame(r, g, 28, TargetBlock, null, UpdateLightning));
        ClientGhostManager.RegisterClass(0x25f, "RepairEffect", (r, g, n) => Frame(r, g, 29, TargetBlock, null, UpdateLightning));
    }

    private delegate void Part(ClientReader r, ClientGhost g);

    // Projectile_unpackUpdateBase 0045ad90 (projectile-sim.md 5.1): GameBase part, initial flag; not initial: exploded
    // flag and the class explode data; initial again: datablock, shooter ghost, class initial data; else the update data.
    private static void Frame(ClientReader r, ClientGhost g, int group, Part initial, Part? explode, Part? update)
    {
        GhostClasses.UnGameBase(r, g);
        if (!r.Flag())
        {
            if (r.Flag())
            {
                r.Add("explode", 1L);
                explode?.Invoke(r, g);
            }
        }
        if (r.Flag())
        {
            r.AddInt("datablock", r.DbBits(group));
            if (r.Flag())
                r.AddInt("shooterGhost", 10);
            initial(r, g);
            return;
        }
        update?.Invoke(r, g);
    }

    private static void InitialPosVel(ClientReader r, ClientGhost g)
    {
        r.AddVec("pos");
        r.AddVec("vel");
    }

    private static void ExplodeRocket(ClientReader r, ClientGhost g)
    {
        if (r.Flag())
            return;
        r.AddVec("hitPos");
        r.Add("hitNormal", r.Normal(5));
        r.AddInt("explosion", r.DbBits(20));
    }

    private static void UpdateRocket(ClientReader r, ClientGhost g)
    {
        if (r.Flag())
            r.AddVec("pos");
        r.AddFlag("flag314");
    }

    private static void UpdateSeeker(ClientReader r, ClientGhost g)
    {
        if (r.Flag())
            r.AddVec("pos");
    }

    private static void InitialBullet(ClientReader r, ClientGhost g)
    {
        r.AddVec("start");
        r.AddInt("elapsedMs", 15);
        r.Add("dir", r.Normal(20));
        r.AddInt("speed16", 14);
    }

    private static void ExplodeBullet(ClientReader r, ClientGhost g)
    {
        r.AddInt("timeMs", 15);
        r.Add("hitNormal", r.Normal(6));
        r.AddInt("explosion", r.DbBits(20));
    }

    private static void UpdateGrenade(ClientReader r, ClientGhost g)
    {
        if (r.Flag())
            r.AddVec("pos");
        if (r.Flag())
            r.AddVec("vel");
        else
            r.Add("dir9", r.Normal(9));
        if (r.Flag())
            r.AddInt("bounceType", 4);
    }

    private static void InitialLaser(ClientReader r, ClientGhost g)
    {
        if (!r.Out.Any(f => f.Name == "shooterGhost"))
            r.AddVec("start");
        r.AddFlag("hit");
        r.AddVec("end");
        r.AddInt("energy5", 5);
    }

    private static void InitialTargetLaser(ClientReader r, ClientGhost g)
    {
        r.AddVec("start");
        r.AddVec("end");
        r.Add("ownerClient", (long)r.Int(10) + 2048);
        r.AddInt("team", 3);
        r.AddFlag("hit");
    }

    private static void UpdateTargetLaser(ClientReader r, ClientGhost g)
    {
        r.AddVec("start");
        r.AddVec("end");
        r.AddFlag("hit");
    }

    private static void TargetBlock(ClientReader r, ClientGhost g)
    {
        if (r.Flag())
        {
            r.AddInt("targetGhost", 10);
            r.AddVec("offset");
        }
    }

    private static void UpdateLightning(ClientReader r, ClientGhost g)
    {
        if (r.Flag())
            TargetBlock(r, g);
    }
}
