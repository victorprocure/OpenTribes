// Port of tests/test_sensor.cpp: sensor jamming by Players, the jamming list, detection by eye and the target slot table
// (ROADMAP G8, docs/static-sim.md 3.3, 3.5, 3.8, 14.7). Synthetic terrain and datablocks. 48 asserts, one to one.
namespace OpenTribes.Tests;

using System.Globalization;
using OpenTribes.Game;
using OpenTribes.Script;
using static OpenTribes.Tests.Fixtures;

public class SensorTests
{
    private Fixtures.World _gw = null!;
    private bool _started; // the virtual clock starts 1 ms ahead, clear of a tick boundary

    private void Tick(int n = 1)
    {
        for (var i = 0; i < n; ++i)
        {
            _gw.Sim.AdvanceRealTime(_started ? 32u : 33u);
            _started = true;
        }
    }

    private GameBase? GbOf(string id) => _gw.Sim.Server()!.FindById(Numbers.BorlandAtoi(id)) as GameBase;

    private static string IdOf(GameBase g) => g.Id().ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void Main()
    {
        var root = Path.Combine(TestPaths.TmpDir(""), "sensor_root");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        var t = new TerrainSpec
        {
            Scale = 5,
            Height = 2,
            Heights = [.. Enumerable.Repeat(50.0f, 25)],
            Squares = [.. new byte[32]]
        };
        PutFile(Path.Combine(root, "base/m.dtf"), DtfFile(t, 50.0f, 50.0f));
        PutFile(Path.Combine(root, "base/m#0.dtb"), DtbFile(t, 50.0f, 50.0f));
        PutFile(Path.Combine(root, "base/t.dml"), "dml");
        float[] box = [-1, -1, 0, 1, 1, 2];
        PutFile(Path.Combine(root, "base/plain.dts"), DtsCollisionFile(box));

        var w = new Fixtures.World(root);
        _gw = w;
        Numbers.EnterScriptFpuMode();
        w.Run("$Server::teamName0 = \"A\"; $Server::teamName1 = \"B\"; resetPlayerManager();");
        w.Game.AddTeam(0);
        w.Game.AddTeam(1);
        w.Run("PlayerData tarmor { shapeFile = \"none\"; maxForwardSpeed = 11; maxBackwardSpeed = 10; maxSideSpeed = 10;" +
              " mass = 9; drag = 1; groundForce = 360; groundTraction = 3; jumpImpulse = 75; jumpSurfaceMinDot = 0.25;" +
              " jetForce = 236; jetEnergyDrain = 0.75; minJetEnergy = 1; maxJetSideForceFactor = 0.75;" +
              " maxJetForwardVelocity = 22; maxEnergy = 60; maxDamage = 0.5; boxWidth = 0.5; boxDepth = 0.5;" +
              " boxNormalHeight = 2.25; boxCrouchHeight = 1.75; canCrouch = true; minDamageSpeed = 25;" +
              " damageScale = 0.0078125; };" +
              "StaticShapeData Tgt { shapeFile = \"plain\"; visibleToSensor = true; };" +
              "SensorData Radar { shapeFile = \"plain\"; range = 100; };");
        w.Run("instant SimTerrain \"Terrain\" { tedFileName = \"m.dtf\"; position = \"0 0 0\"; contGravity = \"0 0 -20\"; };" +
              "instant SimGroup \"MissionGroup\" { };" +
              "instant SimGroup \"MissionCleanup\";");
        var sensors = w.Sim.Server()!.FindById(720) as SensorManager;
        Assert.NotNull(sensors);
        w.Game.Players.AddClient(2049, "Alpha", "male1", true);
        w.Game.Players.AddClient(2050, "Bravo", "male1", true);
        Player Spawn(string pos, int client, int team)
        {
            var id = w.Run("echo(spawnPlayer(tarmor, \"" + pos + "\", \"0 0 0\"));");
            var p = Player.From(w.Sim.Server()!.FindById(Numbers.BorlandAtoi(id)));
            Assert.NotNull(p);
            if (client != 0)
            {
                w.Run("Client::setOwnedObject(" + client.ToString(CultureInfo.InvariantCulture) + ", " + id + ");");
                w.Run("Client::setControlObject(" + client.ToString(CultureInfo.InvariantCulture) + ", " + id + ");");
            }
            w.Run("GameBase::setTeam(" + id + ", " + team.ToString(CultureInfo.InvariantCulture) + ");");
            return p!;
        }
        var a = Spawn("10 10 52", 2049, 0);
        var b = Spawn("200 200 52", 2050, 1);
        var c = Spawn("210 210 52", 0, 1); // a third body for the list tests, no client of its own
        Tick(8);

        // ---- 3.8 the jamming list: set / unlink / link by Player::setSensorSupression (0x0040fc88) ----
        string SetR(Player p, string r) => w.Run("echo(Player::setSensorSupression(" + IdOf(p) + ", " + r + "));");
        Assert.Equal(0, sensors!.JamFirst());
        Assert.True(SetR(a, "20") == "True" && sensors.JamFirst() == a.Id() && a.JamPrev == -1 && a.JamNext == 0);
        Assert.Equal(20.0f, a.SensorSuppression);
        SetR(b, "30"); // after the head: b, a
        Assert.True(sensors.JamFirst() == b.Id() && b.JamNext == a.Id() && a.JamPrev == b.Id());
        SetR(c, "5"); // c, b, a
        Assert.True(sensors.JamFirst() == c.Id() && c.JamNext == b.Id() && b.JamPrev == c.Id());
        SetR(b, "30"); // relinked in front: b, c, a
        Assert.True(sensors.JamFirst() == b.Id() && b.JamNext == c.Id() && c.JamNext == a.Id() && a.JamPrev == c.Id());
        SetR(c, "0"); // b, a
        Assert.True(sensors.JamFirst() == b.Id() && b.JamNext == a.Id() && a.JamPrev == b.Id());
        Assert.True(c.JamNext == a.Id() && c.JamPrev == b.Id() && c.SensorSuppression == 0.0f);
        SetR(c, "-3"); // unlink again through the stale links: harmless here (b.next = c.next = a)
        Assert.True(sensors.JamFirst() == b.Id() && b.JamNext == a.Id() && c.SensorSuppression == -3.0f);
        SetR(b, "0");
        Assert.True(sensors.JamFirst() == a.Id() && a.JamPrev == b.JamPrev);
        SetR(a, "0");
        Assert.Equal(0, sensors.JamFirst());
        SetR(b, "0");
        Assert.True(sensors.JamFirst() == a.Id()); // the stale next of b
        Assert.Equal(0.0f, a.SensorSuppression);
        Assert.Equal("wrong argcount to setSensorSupression(player,value)\nFalse", w.Run("echo(Player::setSensorSupression(" + IdOf(a) + "));"));
        Assert.Equal("False", w.Run("echo(Player::setSensorSupression(nosuch, 5));"));
        SetR(a, "0");
        Assert.Equal(0, sensors.JamFirst());

        // ---- the dead keep the radius; death and removal unlink (Player_kill 0x004112ac, Player_onRemove) ----
        SetR(a, "25");
        SetR(b, "15"); // b, a
        w.Run("Player::kill(" + IdOf(b) + ");");
        Assert.Equal(15.0f, b.SensorSuppression); // not cleared by the death
        Assert.True(sensors.JamFirst() == a.Id() && a.JamPrev == b.JamPrev);
        SetR(c, "9"); // c, a
        var cid = c.Id();
        w.Run("deleteObject(" + cid.ToString(CultureInfo.InvariantCulture) + ");");
        Assert.True(sensors.JamFirst() == a.Id());
        Assert.Null(w.Sim.Server()!.FindById(cid));
        SetR(a, "0");
        Assert.Equal(0, sensors.JamFirst());

        // ---- 3.5 step 4 and 3.6: a sensor of team 0 sees a team 1 target; a jamming Player hides it ----
        w.Run("$s = newObject(\"\", Sensor, Radar); $t = newObject(\"\", StaticShape, Tgt);");
        var s = GbOf(w.Run("echo($s);"));
        var tg = GbOf(w.Run("echo($t);"));
        Assert.True(s != null && tg != null);
        s!.SetTeam(0);
        tg!.SetTeam(1);
        w.Run("GameBase::setPosition($s, \"100 100 60\"); GameBase::setPosition($t, \"140 100 60\"); GameBase::setActive($s, true);");
        w.Run("GameBase::setPosition(" + IdOf(a) + ", \"180 100 52\");");
        Tick(8);
        Assert.True((tg.SensorWord & 0x3) == 0x3 && (tg.SensorWord >> 24) == 1); // pinged by the sensor
        SetR(a, "30"); // 40 m away: out of the radius
        Tick(8);
        Assert.True((tg.SensorWord & 1) != 0 && (tg.SensorWord >> 24) == 1);
        SetR(a, "50"); // inside: jammed, the supressable sensor is skipped (state 2)
        Tick(8);
        Assert.True((tg.SensorWord & 1) == 0 && (tg.SensorWord >> 24) == 2);
        SetR(a, "0");
        w.Run("GameBase::setPosition(" + IdOf(b) + ", \"140 110 52\");");
        SetR(b, "20");
        Tick(8);
        Assert.True((tg.SensorWord & 1) == 0 && (tg.SensorWord >> 24) == 2);
        SetR(b, "0");
        Tick(8);
        Assert.True((tg.SensorWord & 1) != 0 && (tg.SensorWord >> 24) == 1);
        w.Run("GameBase::setPosition(" + IdOf(b) + ", \"110 100 52\");");
        Tick(8);
        Assert.True((b.SensorWord & 1) != 0 && (b.SensorWord >> 24) == 1); // seen by the team 0 sensor
        SetR(b, "20");
        Tick(8);
        Assert.True((b.SensorWord & 1) == 0 && (b.SensorWord >> 24) == 2);
        SetR(b, "0");

        // ---- 3.5 step 6: eye detection by a Player of another team ----
        w.Run("deleteObject($s);");
        w.Run("GameBase::setPosition(" + IdOf(a) + ", \"10 10 52\"); GameBase::setRotation(" + IdOf(a) + ", \"0 0 0\");");
        w.Run("GameBase::setPosition(" + IdOf(b) + ", \"10 40 52\");");
        Tick(10);
        Assert.True(PlayerManager.VisibleToTeam(b, 0) && (b.SensorWord >> 24) == 0);
        w.Run("GameBase::setPosition(" + IdOf(b) + ", \"10 -20 52\");");
        Tick(10);
        Assert.False(PlayerManager.VisibleToTeam(b, 0));
        w.Run("GameBase::setPosition(" + IdOf(b) + ", \"10 100 52\");");
        Tick(10);
        Assert.False(PlayerManager.VisibleToTeam(b, 0));
        w.Run("Player::setDetectParameters(" + IdOf(a) + ", 0.01, 1);");
        Tick(10);
        Assert.True(PlayerManager.VisibleToTeam(b, 0));

        // ---- 3.4 the sweep schedule (SensorManager_sweep 0x00439cc4, SimTimerSet 0x0050782c) ----
        Tick(2);
        Assert.True(sensors.TargetCursor() == tg.TargetIndex);
        w.Run("$u1 = newObject(\"\", StaticShape, Tgt); $u2 = newObject(\"\", StaticShape, Tgt);");
        {
            int[] slots = [GbOf(w.Run("echo($u1);"))!.TargetIndex, tg.TargetIndex, GbOf(w.Run("echo($u2);"))!.TargetIndex];
            int Rank(int slot)
            {
                var r = 0;
                foreach (var x in slots)
                    r += x < slot ? 1 : 0;
                return r;
            }
            Assert.True(slots[0] != slots[1] && slots[1] != slots[2] && slots[0] != slots[2]);
            int at = Rank(tg.TargetIndex), steps = 0, doubles = 0;
            for (var i = 0; i < 15; ++i)
            {
                Tick();
                var now = Rank(sensors.TargetCursor());
                var d = (now - at + 3) % 3;
                if (d == 0)
                    d = 3;
                Assert.True(d is 1 or 2);
                doubles += d == 2 ? 1 : 0;
                steps += d;
                at = now;
            }
            Assert.True(steps == 16 && doubles == 1);
        }
        w.Run("deleteObject($u1); deleteObject($u2);");
        tg.SetTeam(5);
        tg.SensorWord = (tg.SensorWord & ~0xffu) | 0x40u;
        Tick(3);
        Assert.Equal(0x40u, tg.SensorWord & 0xff);
        tg.SensorWord &= ~0xffu;
        tg.SetTeam(1);
        Tick(3);
        Assert.Equal(0x2u, tg.SensorWord & 0xff);

        // ---- 3.3 target slots: lowest free slot, freed slots reused, a full table gives -1 ----
        var first = tg.TargetIndex;
        Assert.True(first >= 0);
        w.Run("function mk(%i) { $o[%i] = newObject(\"\", StaticShape, Tgt); }");
        w.Run("for (%i = 0; %i < 130; %i++) mk(%i);");
        var full = 0;
        for (var i = 0; i < 128; ++i)
            full += w.Game.Targets[i] != null ? 1 : 0;
        Assert.Equal(128, full);
        Assert.NotEqual("-1", w.Run("echo(getObjectByTargetIndex(127));"));
        w.Run("deleteObject($o[3]); deleteObject($o[20]); deleteObject($o[7]);");
        var freedLow = -1;
        for (var i = 0; i < 128; ++i)
            if (w.Game.Targets[i] == null)
            {
                freedLow = i;
                break;
            }
        Assert.True(freedLow >= 0);
        w.Run("$n1 = newObject(\"\", StaticShape, Tgt); $n2 = newObject(\"\", StaticShape, Tgt);");
        Assert.True(GbOf(w.Run("echo($n1);"))!.TargetIndex == freedLow);
        Assert.True(GbOf(w.Run("echo($n2);"))!.TargetIndex > freedLow);
        Assert.True(GbOf(w.Run("echo($o[129]);"))!.TargetIndex == -1);
        w.Run("deleteObject(" + IdOf(a) + "); deleteObject(" + IdOf(b) + ");");
    }
}
