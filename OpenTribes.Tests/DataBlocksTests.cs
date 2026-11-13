// Port of tests/test_datablocks.cpp: the datablock layer (docs/datablocks.md): classes and groups, typed fields, defaults,
// field access, declaration, preload. Expected values are the [live] reference results quoted in the spec. 16 asserts.
namespace OpenTribes.Tests;

using OpenTribes.Core;
using OpenTribes.Game;
using static OpenTribes.Tests.Fixtures;

public class DataBlocksTests
{
    // Standard zlib CRC-32 (bitwise), for the 7.1 identity crc(buf, seed) = ~zlib(buf, ~seed).
    private static uint ZlibCrc(IReadOnlyList<byte> b, uint crc)
    {
        crc = ~crc;
        foreach (var c in b)
        {
            crc ^= c;
            for (var k = 0; k < 8; ++k)
                crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1)));
        }
        return ~crc;
    }

    private static uint SpecCrc(IReadOnlyList<byte> b, uint seed) => ~ZlibCrc(b, ~seed);

    // TS::Shape whose last nested block is the TS::MaterialList (interiors.md 8.2): one detail, two materials, only the
    // first is a texture (flags & 0xf == 3).
    private static List<byte> ShapeWithMaterials()
    {
        var list = new List<byte>();
        PutU32(list, 1); // details
        PutU32(list, 2); // count
        foreach ((var flags, var name) in new[] { (3u, "t1.bmp"), (1u, "t2.bmp") })
        {
            var rec = new List<byte>(new byte[64]);
            SetU32(rec, 0, flags);
            var nb = ByteString.ToBytes(name);
            for (var i = 0; i < nb.Length; ++i)
                rec[16 + i] = nb[i];
            list.AddRange(rec);
        }
        var body = new List<byte>();
        PutStr(body, "meshdata");
        body.AddRange(Pers("TS::MaterialList", 3, list));
        return Pers("TS::Shape", 8, body);
    }

    [Fact]
    public void Main()
    {
        var root = Path.Combine(TestPaths.TmpDir(""), "db_root");
        if (Directory.Exists(root))
            Directory.Delete(root, true);
        PutFile(Path.Combine(root, "base/larmor.dts"), "abc");
        PutFile(Path.Combine(root, "base/pad.dts"), "pad");
        PutFile(Path.Combine(root, "base/flag.dts"), "flag");
        PutFile(Path.Combine(root, "base/shield.dts"), "shield");
        PutFile(Path.Combine(root, "base/b.DTS"), "bullet");
        PutFile(Path.Combine(root, "base/e.dts"), "explosion");
        PutFile(Path.Combine(root, "base/mat.dts"), ShapeWithMaterials());
        PutFile(Path.Combine(root, "base/t1.bmp"), "texture one");
        PutFile(Path.Combine(root, "base/t2.bmp"), "texture two");
        var w = new Fixtures.World(root);
        var c = new Cases();
        var db = w.Sim.DataBlocks();

        // 1.2: the 31 classes, their groups (-1 abstract) and parents' fields.
        (string, int)[] kGroups =
        [
            ("DamageSkinData", 2), ("GameBase", -1), ("ShapeBase", -1), ("StaticBase", -1), ("CarData", 27), ("TriggerData", 26),
            ("VehicleData", 9), ("TankData", 11), ("FlierData", 10), ("PlayerData", 3), ("StaticShapeData", 4), ("MarkerData", 21),
            ("ItemData", 5), ("MineData", 23), ("ItemImageData", 6), ("MoveableData", 7), ("SensorData", 8), ("TurretData", 19),
            ("BulletData", 14), ("GrenadeData", 15), ("LaserData", 17), ("TargetLaserData", 24), ("LightningData", 28),
            ("RepairEffectData", 29), ("RocketData", 16), ("SeekingMissileData", 25), ("ExplosionData", 20),
            ("SoundProfileData", 0), ("SoundData", 1), ("DebrisData", 22), ("IRCChannel", 30),
        ];
        foreach ((var name, var group) in kGroups)
        {
            var cls = DataBlockManager.FindClass(name);
            Assert.True(cls != null && cls.Group == group);
        }
        Assert.True(DataBlockManager.FindClass("itemdata") == DataBlockManager.FindClass("ItemData")); // CR4 case-insensitive
        // D11: classes without a factory are rejected instead of crashing (9 note 3).
        c.Check(w, "CarData Car1; TankData Tank1; GameBase GB1; ShapeBase SB1; StaticBase StB1; echo(Car1.maxDamage);",
                "Unknown class CarData\nUnknown class TankData\nUnknown class GameBase\nUnknown class ShapeBase\n" +
                "Unknown class StaticBase\nFalse");

        // 3 types 0-4 and FA7 (ItemData fields, 5.13).
        c.Check(w, "ItemData I1 { className = \"Weapon\"; price = 85; mass = 0.1; drag = \"0.1\"; showInventory = \"yes\"; };" +
                   "echo(I1.price, \"|\", I1.mass, \"|\", I1.drag, \"|\", I1.showInventory, \"|\", I1.description, \"|\"," +
                   " I1.heading, \"|\", I1.lightColor, \"|\", I1.className);",
                "85|0.0999998|0.0999999|False|Item|z----|1 1 1|Weapon");
        // type 2 int: Borland atoi.
        c.Check(w, "I1.price = \"True\"; echo(I1.price); I1.price = \"0x10\"; echo(I1.price); I1.price = \"1.9\"; echo(I1.price);" +
                   "I1.price = \"12abc\"; echo(I1.price); I1.price = \"3000000000\"; echo(I1.price); I1.price = {1, 2};",
                "0\n0\n1\n12\n-1294967296\nCannot set multiple args to a single int.");
        // type 3 float and 3.1 output examples.
        c.Check(w, "I1.mass = \"True\"; echo(I1.mass); I1.mass = \"abc\"; echo(I1.mass); I1.mass = \"1e40\"; echo(I1.mass);" +
                   "I1.mass = \"1e-40\"; echo(I1.mass); I1.mass = \"-0\"; echo(I1.mass); I1.mass = {1, 2};",
                "0\n0\n3.40282e+38\n9.99994e-41\n-0\nCannot set multiple args to a single float.");
        c.Check(w, "I1.mass = \"0.3\"; echo(I1.mass); I1.mass = \"7.7\"; echo(I1.mass); I1.mass = \"100.1\"; echo(I1.mass);" +
                   "I1.mass = \"2.5\"; echo(I1.mass); I1.mass = \"0.333333333\"; echo(I1.mass);" +
                   "I1.mass = \"123456789\"; echo(I1.mass); I1.mass = \"1e20\"; echo(I1.mass);",
                "0.299999\n7.69999\n100.099\n2.5\n0.333333\n1.23456e+08\n9.99999e+19");
        // type 4 bool, with the float message (sic).
        c.Check(w, "I1.showInventory = \"\"; echo(I1.showInventory); I1.showInventory = \"2\"; echo(I1.showInventory);" +
                   "I1.showInventory = \"-1\"; echo(I1.showInventory); I1.showInventory = \"0.5\"; echo(I1.showInventory);" +
                   "I1.showInventory = \"no\"; echo(I1.showInventory); I1.showInventory = \"tx\"; echo(I1.showInventory);" +
                   "I1.showInventory = \"fred\"; echo(I1.showInventory); I1.showInventory = {1, 2};",
                "False\nTrue\nTrue\nTrue\nFalse\nTrue\nFalse\nCannot set multiple args to a single float.");
        // type 5 point3F: one string keeps unmatched components.
        c.Check(w, "I1.lightColor = \"7 8\"; echo(I1.lightColor); I1.lightColor = \"9,8,7\"; echo(I1.lightColor);" +
                   "I1.lightColor = \"a b c\"; echo(I1.lightColor); I1.lightColor = {1, 2, 3}; echo(I1.lightColor);" +
                   "I1.lightColor = {1, 2};",
                "7 8 1\n9 8 1\n9 8 1\n1 2 3\nPoint3F must be set as { x, y, z } or \"x y z\"");
        // types 0/1: stringCS keeps the exact spelling; string keeps the first spelling interned.
        c.Check(w, "I1.hudIcon = \"MiXed\"; echo(I1.hudIcon); I1.hudIcon = {a, b}; I1.className = \"WEAPON\"; echo(I1.className);",
                "MiXed\nCannot set multiple args to a single string.\nWeapon");
        // type 6 colorF: exactly 3 values, no clamping.
        c.Check(w, "BulletData B3 { lightColor = {1, 2, 3}; }; echo(B3.lightColor); B3.lightColor = \"1 2 3\";" +
                   "B3.lightColor = {-5, 2.5, 9}; echo(B3.lightColor);",
                "1 2 3\nColorF must be set as { red, green, blue }\n-5 2.5 9");

        // 4.1 single-group references (RF1-RF4) and their messages.
        c.Check(w, "ExplosionData Exp0 { shapeName = \"x.dts\"; }; ExplosionData Exp1; SoundProfileData Prof0;" +
                   "SoundData Snd0 { profile = Prof0; }; SoundData Snd1 { profile = \"prOF0\"; }; echo(Snd1.profile);",
                "Prof0");
        c.Check(w, "SoundData Snd2 { profile = \"nope\"; }; SoundData Snd3 { profile = {a, b}; };" +
                   "SoundData Snd4 { profile = \"\"; }; echo(Snd2.profile, \"|\", Snd4.profile);",
                "Sound profile data block \"nope\" is undefined\n" +
                "Cannot set multiple args to a sound profile data block name.\n" +
                "Sound profile data block \"\" is undefined\n|");
        c.Check(w, "SoundData Snd5 { profile = LaterProf; }; SoundProfileData LaterProf; echo(Snd5.profile);",
                "Sound profile data block \"LaterProf\" is undefined\n"); // RF3
        c.Check(w, "BulletData B0; echo(B0.explosionTag); B0.explosionTag = Exp1; echo(B0.explosionTag);" +
                   "B0.explosionTag = \"none\"; echo(B0.explosionTag, \"|\", B0.className);",
                "Exp0\nExp1\nExplosion data block \"none\" is undefined\n" +
                "No such field: className in block type B0\n|False");
        c.Check(w, "ItemImageData Img0 { shapeFile = \"x\"; ammoType = \"bLASTER\"; ammoType = \"x\"; ammoType = {a, b};" +
                   "sfxFire = \"x\"; sfxFire = {a, b}; };",
                "Item data block \"bLASTER\" undefined\nItem data block \"x\" undefined\n" +
                "Cannot set multiple args to a Item data block name.\nSound data block \"x\" is undefined\n" +
                "Cannot set multiple args to a sound data block name.");
        c.Check(w, "ItemData Blaster; Img0.ammoType = \"bLASTER\"; echo(Img0.ammoType);", "Blaster");
        c.Check(w, "DamageSkinData Skin; StaticShapeData SS { damageSkinData = \"zz\"; explosionId = \"zz\"; debrisId = \"zz\";" +
                   "damageSkinData = {a, b}; explosionId = {a, b}; debrisId = {a, b}; damageSkinData = sKIN; };" +
                   "echo(SS.damageSkinData);",
                "Damage skin data block \"zz\" is undefined\nExplosion data block \"zz\" is undefined\n" +
                "Debris data block \"zz\" is undefined\nCannot set multiple args to a damage skin data block name.\n" +
                "Cannot set multiple args to a sound data block name.\nCannot set multiple args to a sound data block name.\n" +
                "Skin");

        // 4.2 projectile reference: PR1 search, PR2 getter swap.
        c.Check(w, "BulletData B1; GrenadeData G0; GrenadeData G1; ItemImageData Img { shapeFile = \"x\"; projectileType = B0; };" +
                   "echo(Img.projectileType); Img.projectileType = \"nosuch\"; echo(Img.projectileType);",
                "\nProjectile data block \"nosuch\" is undefined\n");
        w.Run("for (%i = 6; %i <= 14; %i++) eval(\"SoundData Snd\" @ %i @ \";\");");
        c.Check(w, "Img.projectileType = B0; echo(Img.projectileType); Img.projectileType = B1; echo(Img.projectileType);" +
                   "Img.projectileType = G1; echo(Img.projectileType); Img.projectileType = {a, b};",
                "Snd14\n\n\nCannot set multiple args to a Projectile data block name.");
        {
            var v = db.Find("Img")!.Find("projectileType")!.Elems[0];
            Assert.True(v.I == 15 && v.I2 == 1); // PR1 stores (group, index)
        }

        // type 13 sequenceSound: (null) text, sequence name in the sound message (sic).
        c.Check(w, "echo(SS.sequenceSound[0], \"|\", SS.sequenceSound); SS.sequenceSound[1] = {seqA, snd1};" +
                   "echo(SS.sequenceSound[1]); SS.sequenceSound[2] = {seqB, nosnd}; echo(SS.sequenceSound[2]);" +
                   "SS.sequenceSound[3] = seqC;",
                "(null) (null)|False\nseqA Snd1\nSound data block \"seqB\" is undefined\n(null) (null)\n" +
                "Sequence sound is set as { sequenceName, soundTag }");
        // type 20 damageLevel (StaticBase default 0.5 0.75).
        c.Check(w, "echo(SS.damageLevel); SS.damageLevel = {0.4, 0.7}; echo(SS.damageLevel); SS.damageLevel = \"0.4 0.7\";",
                "0.5 0.75\n0.399998 0.699998\nDamage level must be set as { disable, destroyed }");
        // type 14 sfxFlags, type 15 normal, ctor bit-pattern defaults (5.28 notes).
        c.Check(w, "SoundProfileData P2 { flags = { SFX_IS_LOOPING, \"sfx_is_hardware_3d\", 64, junk }; };" +
                   "echo(P2.flags, \"|\", P2.coneInsideAngle, \"|\", P2.coneOutsideAngle, \"|\", P2.coneVector);" +
                   "P2.flags = 1; echo(P2.flags);",
                "98|1.26116e-43|2.52233e-43|0 -1 0\n1");
        c.Check(w, "P2.coneVector = {3, 4, 0}; echo(P2.coneVector); P2.coneVector = \"0 0 0\"; echo(P2.coneVector);" +
                   "P2.coneVector = {1, 2};",
                "0.599999 0.799999 0\n-NAN -NAN -NAN\nNormal Vector must be set as { x, y, z } or \"x y z\"");
        // type 23 sqrtScaled.
        c.Check(w, "echo(G0.maxLevelFlightDist); G0.maxLevelFlightDist = 3; echo(G0.maxLevelFlightDist);" +
                   "G0.maxLevelFlightDist = -3; echo(G0.maxLevelFlightDist); G0.maxLevelFlightDist = 100;" +
                   "echo(G0.maxLevelFlightDist); G0.maxLevelFlightDist = \"0.5\"; echo(G0.maxLevelFlightDist);" +
                   "G0.maxLevelFlightDist = {1, 2};",
                "0\n2.99999\n2.99999\n99.9999\n0.499999\nCannot set multiple args to a single float.");
        // type 9 animData: exactly 8 values, reads the name.
        c.Check(w, "PlayerData Arm { animData[0] = { root, Snd1, 1, true, false, 1, 0, 5 };" +
                   "animData[1] = { run, none, 1, 1, 1, 1, 1, 0 }; animData[2] = { x, 1 }; };" +
                   "echo(Arm.animData[0], \"|\", Arm.animData[1]);",
                "Animdata set as { Name, soundTag, direction, viewFlags, priority }\nroot|run");
        {
            var arm = db.Find("Arm")!;
            var f = arm.Find("animData")!;
            Assert.True(f.Elems[0].I == 1 /* Snd1 index */ && f.Elems[0].I3 == 0x5 && f.Elems[0].I4 == 5);
            Assert.True(f.Elems[1].I == -1 && f.Elems[1].I3 == 0xf);
        }

        // 5: constructor defaults of fresh blocks.
        c.Check(w, "echo(Arm.damageScale, \"|\", Arm.boxNormalHeadPercentage, \"|\", Arm.jumpSurfaceMinDot, \"|\"," +
                   " Arm.maxDamage, \"|\", Arm.repairRate, \"|\", Arm.description, \"|\", Arm.mapFilter);",
                "0.00899999|0.666599|0.2|1|0.1|Shape|-1");
        c.Check(w, "SensorData Sen; TurretData Tur; echo(Sen.range, Sen.supressable, Sen.pinger, \"|\", Tur.range," +
                   " Tur.supressable, Tur.pinger, Tur.gunRange, \"|\", Tur.targetableFovRatio);",
                "100TrueTrue|100TrueTrue-1|0.5");
        c.Check(w, "MineData Mine; echo(Mine.description, \"|\", Mine.kickBackStrength, \"|\", Mine.heading);",
                "Item|100|z----");
        c.Check(w, "LaserData L; TargetLaserData TL; LightningData Li; RepairEffectData RE; echo(L.laserBitmapName, \"|\"," +
                   " TL.laserBitmapName, \"|\", Li.bitmapName, \"|\", RE.bitmapName, \"|\", Li.displaceBias, \"|\"," +
                   " G0.smokeName, \"|\", G0.ownerGraceMS, \"|\", G0.elasticity);",
                "laserPulse.bmp|laserPulse.bmp|lightningTemp.bmp|lightningTemp.bmp|0.25|smoke.dts|1000|0.5");
        c.Check(w, "DebrisData D0; echo(D0.center[0], D0.center[2], \"|\", D0.collisionMask, \"|\", D0.explodeOnRest, \"|\"," +
                   " D0.explodeOnBounce, \"|\", D0.animationSequence);",
                "00|6|1|0.3|-1");
        c.Check(w, "VehicleData Veh; echo(Veh.maxDamage, \"|\", Veh.mass, \"|\", Veh.visibleDriver);",
                "1|0|False"); // uninit fields read zero-filled (D11)

        // 2 field access: FA2-FA5, CR4.
        c.Check(w, "DebrisData D { center = {1, 2, 3}; }; echo(D.center, \"|\", D.center[1], \"|\", D.center[2], \"|\"," +
                   " D.center[3], \"|\", D.center[-1]); D.center = 8; D.center = {1, 2}; D.center[1] = 7;" +
                   "echo(D.center[1]); D.center[5] = 1;",
                "False|2|3|False|False\nsetDataField: array set access out of range.\n" +
                "setDataField: array set access out of range.\n7\nsetDataField: array set access out of range.");
        c.Check(w, "D.mass[0] = 9; echo(D.mass); D.mass[-1] = 9; echo(D.mass, \"|\", D.mass[0], \"|\", D.mass[\"abc\"]," +
                   " \"|\", D.mass[1]);",
                "setDataField: array set access out of range.\n1\n9|9|9|False");
        c.Check(w, "echo(D.noSuch); D.noSuch = 4; echo(D.MASS);", "No such field: noSuch in block type D\nFalse\n9");

        // 6.1 declaration: DD1 (warning before the class check), DD2 (existing class table).
        c.Check(w, "ItemData Weird; BulletData Weird { price = 3; damageClass = 4; }; NoClass Weird2 { price = 1; };" +
                   "NoClass Weird; echo(Weird.price, \"|\", Weird2.price);",
                "warning: data block Weird already declared\nUnknown class NoClass\n" +
                "warning: data block Weird already declared\n3|False");

        // 6.2 network identity: (group, index) in declaration order per group; 8 getItemData.
        {
            var weird = db.Find("weird");
            Assert.True(weird != null && weird.Group == 5 && db.At(5, weird.Index) == weird);
            Assert.True(db.At(5, 0) == db.Find("I1") && db.At(5, 1) == db.Find("Blaster") && db.At(5, 2) == weird);
            Assert.True(db.Find("Snd0")!.Index == 0 && db.Find("Snd14")!.Index == 14 && db.GroupSize(1) == 15);
            Assert.True(db.Find("B3")!.Index == 0 && db.Find("B0")!.Index == 1 && db.Find("B1")!.Index == 2);
            Assert.True(db.At(5, 3) == null && db.At(31, 0) == null && db.At(-1, 0) == null);
            // forEach walks groups 0..30, each in index order.
            int lastGroup = 0, lastIndex = -1;
            db.ForEach(b =>
            {
                Assert.True(b.Group > lastGroup || (b.Group == lastGroup && b.Index == lastIndex + 1));
                lastIndex = b.Index;
                lastGroup = b.Group;
            });
        }
        c.Check(w, "echo(getItemData(0), \"|\", getItemData(2), \"|\", getItemData(3), \"|\", getItemData(-1), \"|\"," +
                   " getItemData(\"1abc\")); echo(getItemData());",
                "I1|Weird|||Blaster\nwrong argcount to getItemData(type)\nFalse");
        c.Check(w, "echo(setMaterialProperty(ice, 0.1, 0.2), setMaterialProperty(icy, 1, 1), setMaterialProperty(Ice));",
                "TrueFalseFalse");

        // 7 preload: validation lines in group order, then index order; a ShapeBase name gets ".dts", projectile and
        // explosion names print as written.
        var w2 = new Fixtures.World(root);
        var db2 = w2.Sim.DataBlocks();
        w2.Run("ItemData FlagItem { shapeFile = \"flag\"; validateShape = true; };" +
               "PlayerData LightArmor { shapeFile = \"larmor\"; validateShape = true; };" +
               "StaticShapeData PadA { shapeFile = \"pad\"; validateShape = false; };" +
               "StaticShapeData Pad2 { shapeFile = \"PAD\"; shieldShapeName = \"shield\"; validateShape = true; };" +
               "BulletData Bul { bulletShapeName = \"b.DTS\"; validateShape = true; };" +
               "ExplosionData Ex { shapeName = \"e.dts\"; validateShape = true; };" +
               "ItemData MatItem { shapeFile = \"mat\"; validateShape = true; validateMaterials = true; };" +
               "SoundData S; MarkerData M { shapeFile = \"nothing\"; };");
        const string kValid =
            "Server: Validation required for larmor.dts\nServer: Validation required for pad.dts\n" +
            "Server: Validation required for flag.dts\nServer: Validation required for mat.dts\n" +
            "Server: Validation required for b.DTS\nServer: Validation required for e.dts";
        c.Check(w2, "echo(preloadServerDataBlocks());", kValid + "\nTrue");
        c.Check(w2, "echo(preloadServerDataBlocks());", kValid + "\nTrue"); // same list on every call
        c.Check(w2, "echo(preloadServerDataBlocks(1));", "False");
        // 7.1 CRC: shape bytes, then each texture material when validateMaterials.
        {
            byte[] larmor = [(byte)'a', (byte)'b', (byte)'c'];
            Assert.True(db2.Find("LightArmor")!.ValidationCrc == SpecCrc(larmor, 0xffffffffu));
            Assert.Equal(0xcadbbe3du, db2.Find("LightArmor")!.ValidationCrc);
            var shape = ShapeWithMaterials();
            var t1 = ByteString.ToBytes("texture one");
            Assert.True(db2.Find("MatItem")!.ValidationCrc == SpecCrc(t1, SpecCrc(shape, 0xffffffffu)));
            Assert.Equal(0u, db2.Find("PadA")!.ValidationCrc); // not validated
        }
        // Failures print and the loop continues; the command returns False.
        const string kModel = ".dts.  The server has new art data you are missing.  Check server info for possible download locations.";
        w2.Run("PlayerData NoShield { shapeFile = \"larmor\"; shieldShapeName = \"missingshield\"; validateShape = true; };" +
               "StaticShapeData NoShape { shapeFile = \"missingshape\"; }; ItemImageData NoImg { shapeFile = \"missingimg\"; };" +
               "ItemImageData NullImg { mountPoint = 1; }; RocketData NoBul { bulletShapeName = \"nobul.dts\"; };" +
               "ExplosionData NoEx { shapeName = \"missingex\"; };");
        c.Check(w2, "echo(preloadServerDataBlocks());",
                "Server: Validation required for larmor.dts\nUnable to model: missingshield" + kModel + "\n" +
                "Server: Validation required for pad.dts\nUnable to model: missingshape" + kModel + "\n" +
                "Server: Validation required for flag.dts\nServer: Validation required for mat.dts\n" +
                "Unable to load ItemImageData shape: missingimg.dts\nServer: Validation required for b.DTS\n" +
                "Unable to load ProjectileData shape: nobul.dts\nServer: Validation required for e.dts\n" +
                "Unable to load ExplosionData shape: missingex\nFalse");

        Assert.Equal(0, c.Failures);
    }
}
