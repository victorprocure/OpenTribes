// Client side of the game stream (ROADMAP M9 C3a, docs/client-net.md): the client unpack routines against the server
// pack code of the port. Every ghost class is packed by the server pack routine and read back by the client unpack
// routine, which must end exactly on the packed bit and hold the server state (client-net.md 3).
namespace OpenTribes.Tests;

using OpenTribes.Game;
using OpenTribes.Net;
using OpenTribes.Script;
using OpenTribes.Sim;
using static OpenTribes.Tests.ClientRoundTrip;
using static OpenTribes.Tests.Fixtures;

public class ClientNetProjectileTests
{
    private readonly GhostManager _gm = new(2049);

    private (ClientGhost, uint) PackAndRead(SimObject obj, uint tag, uint mask, ClientGhost? prev = null)
    {
        var cls = GhostManager.GhostClassOf(obj);
        Assert.True(cls != null && cls.Tag == tag);
        var buf = new byte[512];
        var w = new BitStream(buf, buf.Length);
        cls!.Pack!(obj, new GhostContext(_gm, 2049, mask == 0xffffffffu), w, mask);
        Assert.False(w.Error());
        var g = Unpack(tag, buf, w.BitPosition(), mask == 0xffffffffu, WorldFor(tag, obj, 4), prev);
        return (g, w.BitPosition());
    }

    [Fact]
    public void ProjectileFamilyRoundTrip()
    {
        // client-net.md 3.6, projectile-sim.md 5.2
        var root = Path.Combine(TestPaths.TmpDir(""), "clientnet_proj");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        var t = new TerrainSpec
        {
            Scale = 5,
            Height = 2,
            Heights = [.. Enumerable.Repeat(0.0f, 25)],
            Squares = [.. new byte[32]]
        };
        PutFile(Path.Combine(root, "base/m.dtf"), DtfFile(t, 0.0f, 0.0f));
        PutFile(Path.Combine(root, "base/m#0.dtb"), DtbFile(t, 0.0f, 0.0f));
        PutFile(Path.Combine(root, "base/t.dml"), "dml");
        var w = new Fixtures.World(root);
        Numbers.EnterScriptFpuMode();
        ProjectileGhost.Register();
        w.Run("$Server::teamName0 = \"A\"; resetPlayerManager();");
        var decl = w.Run("GrenadeData DG { totalTime = 3; }; RocketData DR { totalTime = 3; }; SeekingMissileData DS { totalTime = 3; };" +
              "");
        Assert.True(decl.Length == 0, decl);
        SimObject Spawn(string db)
        {
            var id = w.Run("echo(Projectile::spawnProjectile(" + db + ", \"1 0 0 0 1 0 0 0 1 16 16 80\", -1, \"0 0 0\"));");
            var o = w.Object(id);
            Assert.True(o != null, db + " spawn: " + id);
            return o!;
        }

        // Grenade 0x259: initial position and velocity, then the update flags.
        var gr = Spawn("DG");
        var (g1, _) = PackAndRead(gr, 0x259, 0xffffffffu);
        Assert.True(VecIs(g1.State["pos"], 16, 16, 80) && Has(g1, "vel") && (long)g1.State["datablock"]! == 0);
        var (g2, _) = PackAndRead(gr, 0x259, 0x01, g1);
        Assert.True((Has(g2, "pos") && !Has(g2, "vel") && !Has(g2, "dir9")) || Has(g2, "dir9"));
        var (g3, _) = PackAndRead(gr, 0x259, 0x0a, g1);
        Assert.True(Has(g3, "bounceType"));

        // RocketDumb 0x25a and SeekingMissile 0x25d.
        var ro = Spawn("DR");
        var (r1, _) = PackAndRead(ro, 0x25a, 0xffffffffu);
        Assert.True(VecIs(r1.State["pos"], 16, 16, 80) && Has(r1, "vel"));
        var (r2, _) = PackAndRead(ro, 0x25a, 0x01, r1);
        Assert.True(Has(r2, "flag314"));
        var se = Spawn("DS");
        var (s1, _) = PackAndRead(se, 0x25d, 0xffffffffu);
        Assert.True(VecIs(s1.State["pos"], 16, 16, 80));
        var (s2, _) = PackAndRead(se, 0x25d, 0x01, s1);
        Assert.True(Has(s2, "pos"));

        // TargetLaser, Lightning, RepairEffect need a shooter or a target object to spawn; their layouts are written by hand
        // from projectile-sim.md 5.2 (the pack routines are covered by the projectile tests).
        void Head(BitStream b, bool initial, bool exploded = false)
        {
            b.WriteFlag(true);              // GameBase part: team
            b.WriteInt(2, 4);
            b.WriteFlag(false);
            b.WriteFlag(false);
            b.WriteFlag(initial);
            if (!initial)
                b.WriteFlag(exploded);
            b.WriteFlag(initial);
        }
        // TargetLaser initial: datablock, shooter ghost flag, start, end, owner client - 2048 (10), team (3), hit.
        var buf = new byte[256];
        var tlw = new BitStream(buf, buf.Length);
        Head(tlw, true);
        tlw.WriteInt(1, 2);                 // 1 datablock of TargetLaserData + 1 = 2 bits
        tlw.WriteFlag(false);
        tlw.WriteF32s([1, 2, 3, 4, 5, 6]);
        tlw.WriteInt(5, 10);
        tlw.WriteInt(1, 3);
        tlw.WriteFlag(true);
        var wt = new ClientWorld();
        wt.DataBlocks.Counts[24] = 1;
        var t1 = Unpack(0x25c, buf, tlw.BitPosition(), true, wt);
        Assert.True(VecIs(t1.State["start"], 1, 2, 3) && VecIs(t1.State["end"], 4, 5, 6) && (long)t1.State["ownerClient"]! == 2053);
        Assert.True((long)t1.State["team"]! == 1 && (long)t1.State["hit"]! == 1);
        // TargetLaser update: start, end, hit (no mask bits).
        buf = new byte[256];
        var tuw = new BitStream(buf, buf.Length);
        Head(tuw, false);
        tuw.WriteF32s([7, 8, 9, 1, 1, 1]);
        tuw.WriteFlag(false);
        var t2 = Unpack(0x25c, buf, tuw.BitPosition(), false, wt, t1);
        Assert.True(VecIs(t2.State["start"], 7, 8, 9) && (long)t2.State["hit"]! == 0);
        // Lightning initial with a target: flag, target ghost (10), offset.
        buf = new byte[256];
        var lw = new BitStream(buf, buf.Length);
        Head(lw, true);
        lw.WriteInt(1, 2);
        lw.WriteFlag(false);
        lw.WriteFlag(true);
        lw.WriteInt(77, 10);
        lw.WriteF32s([0.5f, 0.25f, 0]);
        var wl = new ClientWorld();
        wl.DataBlocks.Counts[28] = 1;
        wl.DataBlocks.Counts[29] = 1;
        var l1 = Unpack(0x25e, buf, lw.BitPosition(), true, wl);
        Assert.True((long)l1.State["targetGhost"]! == 77 && VecIs(l1.State["offset"], 0.5, 0.25, 0));
        // RepairEffect update (mask 0x08): flag, target block with flag.
        buf = new byte[256];
        var rw = new BitStream(buf, buf.Length);
        Head(rw, false);
        rw.WriteFlag(true);
        rw.WriteFlag(true);
        rw.WriteInt(9, 10);
        rw.WriteF32s([1, 0, 0]);
        var e1 = Unpack(0x25f, buf, rw.BitPosition(), false, wl, new ClientGhost { Tag = 0x25f, Class = "RepairEffect" });
        Assert.Equal(9, (long)e1.State["targetGhost"]!);
    }
}
