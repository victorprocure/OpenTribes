// Port of tests/test_datablock_pack.cpp: DataBlockEvent bodies (docs/game-stream.md 4.2) of synthetic datablocks. 64
// asserts.
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Game;

public class DatablockPackTests
{
    private sealed class Fx
    {
        public StringTable Strings { get; } = new();
        public string Log { get; set; } = "";
        public DataBlockManager Db { get; }

        public Fx() => Db = new DataBlockManager(Strings, l => Log += l + "\n");

        public DataBlockManager.Block Make(string cls, string name)
        {
            var ok = Db.Declare(cls, name);
            Assert.True(ok);
            return Db.Find(name)!;
        }

        public void Set(DataBlockManager.Block b, string field, string[] v, string index = "") => Db.SetField(b, field, v, index);

        public OutEvent Pack(DataBlockManager.Block b)
        {
            var w = new EventWriter();
            var ok = DatablockPack.PackDataBlock(Db, b, w);
            Assert.True(ok);
            return w.Finish(0x458);
        }
    }

    private static void Skip(Bits b, uint n)
    {
        for (; n >= 32; n -= 32)
            b.U(32);
        if (n != 0)
            b.U(n);
    }

    private static void SetRaw(DataBlockManager.Block b, string field, uint raw)
    {
        var v = b.Find(field)!.Elems[0];
        v.Set = true;
        v.F[0] = BitConverter.UInt32BitsToSingle(raw);
    }

    // GameBase (0x00447f64): sint(8) mapFilter, str mapIcon, str description.
    private static void GameBaseHead(Bits b, int mapFilter, string icon, string description)
    {
        var neg = b.Flag();
        var mag = (int)b.U(7);
        Assert.True((neg ? -mag : mag) == mapFilter);
        Assert.True(b.Str() == icon && b.Str() == description);
    }

    // ShapeBase (0x0044a434) after the shape name, all defaults.
    private static void ShapeBaseTail(Bits b)
    {
        Assert.Equal(0, b.Str().Length);              // shieldShapeName
        Assert.Equal(0u, b.U(8));                      // +0x44: not a field, the ctor writes -1, sent + 1
        Assert.Equal(0u, b.U(8));                      // shadowDetailMask
        Assert.Equal(0u, b.U(8));
        Assert.Equal(0u, b.U(8));       // explosionId + 1, damageSkinData + 1
        Assert.Equal(0xffffffff, b.U(32));            // debrisId
        Assert.Equal(0.0f, b.F32());                  // maxEnergy
        for (var k = 0; k < 16; ++k)
            Assert.False(b.Flag()); // sequence sounds
        Assert.False(b.Flag()); // isPerspective: storage no ctor writes, sent 0 (D12)
    }

    [Fact]
    public void TestSoundProfile()
    {
        var fx = new Fx();
        // Group 0: int(6) flags, unit(10) baseVolume, then each optional field only when it differs from a
        // default-constructed profile.
        var d = fx.Make("SoundProfileData", "spDefault");
        {
            var b = new Bits(fx.Pack(d));
            Assert.True(b.U(6) == 0 && b.U(10) == 0);
            for (var k = 0; k < 6; ++k)
                Assert.False(b.Flag());
            Assert.True(b.Done());
        }
        // unit(n) = ftol(f * (2^n - 1)): truncated toward zero, low n bits of the integer; out of range gives 0x80000000,
        // so 0 bits.
        (string, uint)[] volumes = [("1", 1023), ("0.1", 102), ("2", 1022), ("-0.5", 0x201 /* -511, not floor -512 */), ("3e6", 0)];
        foreach ((var text, var want) in volumes)
        {
            var p = fx.Make("SoundProfileData", "vol" + text);
            fx.Set(p, "baseVolume", [text]);
            var b = new Bits(fx.Pack(p));
            b.U(6);
            Assert.True(b.U(10) == want);
        }
        // Cone angles compare their raw 32 bits with the ctor integers 90 / 180 and are sent as unit(10) of
        // float(raw * 1/360) under round toward zero: raw 100 sends 284; raw 2893 sends 8220 (low bits 28) where round to
        // nearest would give 8221 (29).
        var a = fx.Make("SoundProfileData", "spAngles");
        SetRaw(a, "coneInsideAngle", 100);
        SetRaw(a, "coneOutsideAngle", 2893);
        {
            var b = new Bits(fx.Pack(a));
            b.U(16);
            Assert.True(b.Flag() && b.U(10) == 284);
            Assert.True(b.Flag() && b.U(10) == 28);
            Assert.True(!b.Flag() && !b.Flag() && !b.Flag() && !b.Flag() && b.Done());
        }
        // Raw 90 is the default; a script float 180 (bits 0x43340000) differs and is out of range.
        var r = fx.Make("SoundProfileData", "spRaw");
        SetRaw(r, "coneInsideAngle", 90);
        fx.Set(r, "coneOutsideAngle", ["180"]);
        {
            var b = new Bits(fx.Pack(r));
            b.U(16);
            Assert.False(b.Flag());
            Assert.True(b.Flag() && b.U(10) == 0);
        }
        // normal(10): sunit x, sunit y = ftol((c + 1) * 0.5 * 1023), flag z < 0. f32 tails.
        var n = fx.Make("SoundProfileData", "spCone");
        fx.Set(n, "coneVector", ["0 -1 -1"]); // normalized at set: (0, -0.7071, -0.7071)
        fx.Set(n, "minDistance", ["30"]);
        fx.Set(n, "maxDistance", ["100"]);
        {
            var b = new Bits(fx.Pack(n));
            b.U(16);
            Assert.True(!b.Flag() && !b.Flag());
            Assert.True(b.Flag() && b.U(10) == 511 && b.U(10) == 149 && b.Flag());
            Assert.True(!b.Flag() && !b.Flag()); // coneOutsideVolume, minDistance equal the defaults
            Assert.True(b.Flag() && b.F32() == 100.0f && b.Done());
        }
        var x = fx.Make("SoundProfileData", "spX");
        fx.Set(x, "coneVector", ["1 0 0"]);
        {
            var b = new Bits(fx.Pack(x));
            b.U(18);
            Assert.True(b.Flag() && b.U(10) == 1023 && b.U(10) == 511 && !b.Flag());
        }
    }

    [Fact]
    public void TestSoundAndSkins()
    {
        var fx = new Fx();
        fx.Make("SoundProfileData", "prof0");
        fx.Make("SoundProfileData", "prof1");
        // Group 1: str wavFileName, unit(6) priority, int(8) profile + 1.
        var s = fx.Make("SoundData", "shot");
        fx.Set(s, "wavFileName", ["shot.wav"]);
        fx.Set(s, "priority", ["0.5"]);
        fx.Set(s, "profile", ["prof1"]);
        {
            var b = new Bits(fx.Pack(s));
            Assert.True(b.Str() == "shot.wav" && b.U(6) == 31 && b.U(8) == 2 && b.Done());
        }
        var u = fx.Make("SoundData", "bare");
        {
            var b = new Bits(fx.Pack(u));
            Assert.True(b.Str().Length == 0 && b.U(6) == 0 && b.U(8) == 0 && b.Done());
        }
        // Group 2: 10 x str bmpName.
        var d = fx.Make("DamageSkinData", "skins");
        fx.Set(d, "bmpName", ["dmg2.bmp"], "2");
        {
            var b = new Bits(fx.Pack(d));
            for (var k = 0; k < 10; ++k)
                Assert.True(b.Str() == (k == 2 ? "dmg2.bmp" : ""));
            Assert.True(b.Done());
        }
    }

    [Fact]
    public void TestShapes()
    {
        var fx = new Fx();
        // Group 4 StaticShapeData: ShapeBase, then StaticBase: int(8) sfxAmbient + 1, 2 flags.
        var g = fx.Make("StaticShapeData", "generator");
        fx.Set(g, "shapeFile", ["generator"]);
        fx.Set(g, "mapIcon", ["M_generator"]);
        fx.Set(g, "mapFilter", ["4"]);
        fx.Set(g, "description", ["Generator"]);
        {
            var b = new Bits(fx.Pack(g));
            GameBaseHead(b, 4, "M_generator", "Generator");
            Assert.Equal("generator", b.Str());
            ShapeBaseTail(b);
            Assert.True(b.U(8) == 0 && !b.Flag() && !b.Flag() && b.Done());
        }
        // Shape name with validateShape: 0xff (0xfe with validateMaterials) + name, u32 CRC.
        var v = fx.Make("StaticShapeData", "validated");
        fx.Set(v, "shapeFile", ["tower"]);
        fx.Set(v, "validateShape", ["True"]);
        v.ValidationCrc = 0x12345678;
        {
            var b = new Bits(fx.Pack(v));
            GameBaseHead(b, -1, "", "Shape");
            Assert.True(b.Str() == "\xfftower" && b.U(32) == 0x12345678);
        }
        fx.Set(v, "validateMaterials", ["True"]);
        {
            var b = new Bits(fx.Pack(v));
            GameBaseHead(b, -1, "", "Shape");
            Assert.True(b.Str() == "\xfetower" && b.U(32) == 0x12345678);
        }
        // Group 21 MarkerData: GameBase, str shapeFile. Groups 26, 30: GameBase only.
        var m = fx.Make("MarkerData", "marker");
        {
            var b = new Bits(fx.Pack(m));
            GameBaseHead(b, -1, "", "Shape");
            Assert.True(b.Str().Length == 0 && b.Done());
        }
        var t = fx.Make("TriggerData", "trigger");
        {
            var b = new Bits(fx.Pack(t));
            GameBaseHead(b, -1, "", "Shape");
            Assert.True(b.Done());
        }
        // Groups 11, 12, 18, 27: the factory has no case; never sent.
        foreach (var group in new[] { 11, 12, 18, 27 })
        {
            var blk = new DataBlockManager.Block { Group = group };
            var w = new EventWriter();
            Assert.True(!DatablockPack.PackDataBlock(fx.Db, blk, w) && w.Bits() == 0);
        }
        // Group 10 FlierData: VehicleData ends with 4 x raw(12) TankData suspension storage that a FlierData never writes,
        // sent zero (D12); then 7 x f32 FlierData fields.
        var f = fx.Make("FlierData", "flier");
        var fe = fx.Pack(f);
        {
            var b = new Bits(fe);
            Skip(b, fe.BodyBits - ((12 + 7) * 32));
            for (var k = 0; k < 12; ++k)
                Assert.Equal(0u, b.U(32));
            Skip(b, 7 * 32);
            Assert.True(b.Done());
        }
    }

    // Projectile (0x00459458) up to the +0x80 flag, which it returns.
    private static bool ProjectileVisible(Bits b, float slot68, float slot7c)
    {
        GameBaseHead(b, -1, "", "Shape");
        Assert.Equal(0, b.Str().Length);                    // bulletShapeName
        Assert.Equal(0u, b.U(32));                           // explosionTag 0 (RF4)
        Assert.True(b.U(32) == 0 && !b.Flag());              // expRandCycle, collideWithOwner
        Assert.Equal(1000u, b.U(32));                        // ownerGraceMS
        Assert.Equal(0.0f, b.F32());
        Assert.Equal(1.0f, b.F32());     // collisionRadius, mass
        Assert.True(b.F32() == slot68 && b.F32() == 0.0f && b.F32() == 0.0f); // +0x68, +0x6c, +0x70
        Assert.Equal(10000.0f, b.F32());
        Assert.Equal(10000.0f, b.F32());             // totalTime, +0x78
        Assert.True(b.F32() == slot7c);                                      // +0x7c
        return b.Flag();
    }

    private static void ProjectileTail(Bits b)
    {
        Assert.Equal(0.0f, b.F32()); // lightRange
        for (var c = 0; c < 3; ++c)
            Assert.Equal(0.0f, b.F32());   // lightColor
        Assert.Equal(0xffffffff, b.U(32)); // +0x98 soundId
        Assert.Equal(0.0f, b.F32());       // explosionRadius
    }

    [Fact]
    public void TestProjectiles()
    {
        var fx = new Fx();
        // Group 15 GrenadeData: +0x80 isVisible is not a Grenade field and no ctor writes it: sent set as every reference
        // capture shows (D12). +0x68 is maxLevelFlightDist as stored, sqrt(|v| * 20) (5 -> 10), +0x7c projSpecialTime.
        var g = fx.Make("GrenadeData", "grenade");
        fx.Set(g, "maxLevelFlightDist", ["5"]);
        fx.Set(g, "projSpecialTime", ["2"]);
        {
            var b = new Bits(fx.Pack(g));
            Assert.True(ProjectileVisible(b, 10.0f, 2.0f));
            ProjectileTail(b);
            Assert.True(b.F32() == 0.5f && b.Str() == "smoke.dts" && b.Done());
        }
        // Group 14 BulletData registers isVisible: the script value is sent. Then f32 rotationPeriod, u32 bulletHoleIndex,
        // f32 tracerLength.
        var bu = fx.Make("BulletData", "bullet");
        fx.Set(bu, "muzzleVelocity", ["425"]);
        fx.Set(bu, "isVisible", ["True"]);
        {
            var b = new Bits(fx.Pack(bu));
            Assert.True(ProjectileVisible(b, 425.0f, 0.0f));
            ProjectileTail(b);
            Assert.True(b.F32() == 0.0f && b.S32() == -1 && b.F32() == 10.0f && b.Done());
        }
    }

    [Fact]
    public void TestPlayer()
    {
        var fx = new Fx();
        // Group 3 PlayerData: maxForwardSpeed 11 goes out as unit(10) of 11/64 = 175 (4.2 spot value); jumpSurfaceMinDot
        // 0.2 as unit(8) = 51; jumpImpulse 75 as f32.
        var p = fx.Make("PlayerData", "larmor");
        fx.Set(p, "maxForwardSpeed", ["11"]);
        fx.Set(p, "jumpImpulse", ["75"]);
        fx.Set(p, "canCrouch", ["True"]);
        var b = new Bits(fx.Pack(p));
        GameBaseHead(b, -1, "", "Shape");
        Assert.Equal(0, b.Str().Length);
        ShapeBaseTail(b);
        Assert.Equal(0, b.Str().Length); // flameShapeName
        for (var k = 0; k < 51; ++k)
        {
            b.Str();
            if (b.Flag())
                b.U(8);
            b.Flag();
            b.U(8);
        }
        Assert.Equal(13.0f, b.F32());
        Assert.Equal(1.0f, b.F32()); // minDamageSpeed, jetEnergyDrain
        Assert.Equal(175u, b.U(10));
        Assert.Equal(0u, b.U(10));
        Assert.Equal(0u, b.U(10));
        Assert.True(b.Flag());      // canCrouch
        Assert.Equal(255u, b.U(8)); // maxJetSideForceFactor 1
        Assert.Equal(20.0f, b.F32());
        Assert.Equal(1.0f, b.F32());
        Assert.Equal(20.0f, b.F32());
        Assert.Equal(51u, b.U(8));
        Assert.Equal(75.0f, b.F32());
    }
}
