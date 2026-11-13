// Port of src/game/datablock_pack.hpp and src/game/datablock_pack.cpp: the datablock pack routines
// (docs/game-stream.md 4.2), the body of a DataBlockEvent. The original packs under round toward zero (its crt_ftol
// 0x0048813c leaves that mode): the RoundTowardZero guard is Fpu.ChopScope.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;
using Block = OpenTribes.Game.DataBlockManager.Block;
using Field = OpenTribes.Game.DataBlockManager.Field;
using Value = OpenTribes.Game.DataBlockManager.Value;

/// <summary>The datablock pack routines, the body of a DataBlockEvent (docs/game-stream.md 4.2).</summary>
/// <remarks>The original packs under round toward zero, which its crt_ftol (0x0048813c) leaves set. The guard here is <c>Fpu.ChopScope</c>.</remarks>
public static class DatablockPack
{
    // src/game/datablock_pack.cpp ftol: crt_ftol: fistp to 32 bits; out of range and NaN give the integer indefinite.
    private static int Ftol(double v)
    {
        if (v is not (> (-2147483649.0) and < 2147483648.0))
            return int.MinValue;
        return (int)v;
    }

    // C atoi.
    private static int Atoi(string s)
    {
        var v = 0;
        CScan.Int(s, ref v);
        return v;
    }

    // src/game/datablock_pack.cpp Reader: reads a block with the original's storage semantics: the set element, else the
    // class default (zero for uninitialized storage, D11), else, for a field the class does not register, the constructor
    // value given as `absent`.
    private sealed class Reader
    {
        private readonly Block mBlock;

        public Reader(Block b) => mBlock = b;
        public Block BlockOf() => mBlock;
        public bool Has(string name) => mBlock.Find(name) != null;

        public Value? ValueOf(string name, int k = 0)
        {
            Field? f = mBlock.Find(name);
            return f != null && f.Elems[k].Set ? f.Elems[k] : null;
        }

        public int I(string name, int absent, int k = 0)
        {
            Field? f = mBlock.Find(name);
            if (f == null)
                return absent;
            if (f.Elems[k].Set)
                return f.Elems[k].I;
            return f.Def.Uninit ? 0 : Atoi(f.Def.DefaultText);
        }

        public bool B(string name, bool absent)
        {
            Field? f = mBlock.Find(name);
            if (f == null)
                return absent;
            if (f.Elems[0].Set)
                return f.Elems[0].I != 0;
            return !f.Def.Uninit && f.Def.DefaultText == "True";
        }

        public float F(string name, float absent, int k = 0)
        {
            Field? f = mBlock.Find(name);
            if (f == null)
                return absent;
            if (f.Elems[k].Set)
                return f.Elems[k].F[0];
            return f.Def.Uninit ? 0.0f : DataBlockManager.CtorFloat(f.Def.DefaultText);
        }

        // Component c of a point3F, colorF or normal.
        public float V3(string name, int c, float absent)
        {
            Field? f = mBlock.Find(name);
            if (f == null)
                return absent;
            if (f.Elems[0].Set)
                return f.Elems[0].F[c];
            if (f.Def.Uninit)
                return 0.0f;
            var p = f.Def.DefaultText;
            var at = 0;
            float v = 0;
            for (var k = 0; k <= c; ++k)
            {
                v = DataBlockManager.CtorFloat(p, at, out var end);
                at = end;
            }
            return v;
        }

        // Single-group reference index: unset is -1, or 0 for explosionTag (RF4).
        public int Ref(string name, int absent, int k = 0)
        {
            Field? f = mBlock.Find(name);
            if (f == null)
                return absent;
            if (f.Elems[k].Set)
                return f.Elems[k].I;
            return f.Def.DefaultText == "@index0" ? 0 : -1;
        }

        // String as stored; a null pointer is sent as "".
        public string S(string name, string? absent)
        {
            Field? f = mBlock.Find(name);
            if (f == null)
                return absent ?? "";
            if (f.Elems[0].Set)
                return f.Elems[0].Str ?? "";
            return f.Def.Uninit ? "" : f.Def.DefaultText;
        }
    }

    private static void PutU32(EventWriter w, uint v) => w.PutInt(v, 32);
    private static void PutF32(EventWriter w, float f) => w.PutInt(BitConverter.SingleToUInt32Bits(f), 32);

    // src/game/datablock_pack.cpp putUnit: writeFloat (0x00582fb0): ftol(f * (2^bits - 1)).
    private static void PutUnit(EventWriter w, float f, uint bits) =>
        w.PutInt((uint)Ftol(Mul(f, (1u << (int)bits) - 1)), bits);

    // src/game/datablock_pack.cpp putSignedUnit: writeSignedFloat (0x00583018): ftol((f + 1) * 0.5 * (2^bits - 1)).
    private static void PutSignedUnit(EventWriter w, float f, uint bits)
    {
        var v = Mul(Add(f, 1.0), 0.5);
        w.PutInt((uint)Ftol(Mul(v, (1u << (int)bits) - 1)), bits);
    }

    // src/game/datablock_pack.cpp putNormal: writeNormalVector (0x00583140): x and y as signed units, then the sign of z.
    private static void PutNormal(EventWriter w, float[] n, uint bits)
    {
        PutSignedUnit(w, n[0], bits);
        PutSignedUnit(w, n[1], bits);
        w.PutFlag(n[2] < 0.0f);
    }

    // src/game/datablock_pack.cpp putSignedInt: writeSignedInt (0x005830a4): sign flag, then the magnitude in bits - 1.
    private static void PutSignedInt(EventWriter w, int v, uint bits)
    {
        w.PutFlag(v < 0);
        w.PutInt((uint)(v < 0 ? -v : v), bits - 1);
    }

    // src/game/datablock_pack.cpp putIndex: datablock index in 8 bits as index + 1 (0 = none).
    private static void PutIndex(EventWriter w, int index) => w.PutInt((uint)(index + 1), 8);

    // src/game/datablock_pack.cpp differs: x87 fcomp then jne/setne: unordered operands compare equal.
    private static bool Differs(float a, float b) => !float.IsNaN(a) && !float.IsNaN(b) && a != b;

    // ---- shared parts (docs/game-stream.md 4.2) ----

    // src/game/datablock_pack.cpp packGameBase: GameBase 0x00447f64. Classes without GameBase fields send the constructor
    // values.
    private static void PackGameBase(Reader r, EventWriter w)
    {
        PutSignedInt(w, r.I("mapFilter", -1), 8);
        w.PutString(r.S("mapIcon", null));
        w.PutString(r.S("description", "Shape"));
    }

    // src/game/datablock_pack.cpp packShapeName: shape name 0x0044a0e8: with validateShape, a 0xff prefix (0xfe with
    // validateMaterials) and the CRC computed at preload.
    private static void PackShapeName(Reader r, EventWriter w, string name)
    {
        if (!r.B("validateShape", false))
        {
            w.PutString(name);
            return;
        }
        var prefix = r.B("validateMaterials", false) ? '\xfe' : '\xff';
        w.PutString(prefix + name);
        PutU32(w, r.BlockOf().ValidationCrc);
    }

    // src/game/datablock_pack.cpp packShapeBase: ShapeBase 0x0044a434.
    private static void PackShapeBase(Reader r, EventWriter w)
    {
        PackGameBase(r, w);
        PackShapeName(r, w, r.S("shapeFile", null));
        w.PutString(r.S("shieldShapeName", null));
        PutIndex(w, -1); // +0x44: not a field, the constructor writes -1
        w.PutInt((uint)r.I("shadowDetailMask", 0), 8);
        PutIndex(w, r.Ref("explosionId", -1));
        PutIndex(w, r.Ref("damageSkinData", -1));
        PutU32(w, (uint)r.Ref("debrisId", -1));
        PutF32(w, r.F("maxEnergy", 0));
        for (var k = 0; k < 16; ++k)
        {
            Value? v = r.ValueOf("sequenceSound", k);
            var present = v?.Str != null;
            w.PutFlag(present);
            if (present)
            {
                w.PutString(v!.Str!);
                PutIndex(w, v.I);
            }
        }
        w.PutFlag(r.B("isPerspective", false));
    }

    // src/game/datablock_pack.cpp packStaticBase: StaticBase 0x0044f34c.
    private static void PackStaticBase(Reader r, EventWriter w)
    {
        PackShapeBase(r, w);
        PutIndex(w, r.Ref("sfxAmbient", -1));
        w.PutFlag(r.B("isTranslucent", false));
        w.PutFlag(r.B("disableCollision", false));
    }

    // src/game/datablock_pack.cpp packProjectileRef: projectile reference (type 12): group, then index; (-1, -1) when
    // unset.
    private static void PackProjectileRef(Reader r, EventWriter w, string name)
    {
        Value? v = r.ValueOf(name);
        PutU32(w, (uint)(v != null ? v.I : -1));
        PutU32(w, (uint)(v != null ? v.I2 : -1));
    }

    // src/game/datablock_pack.cpp slotFloat: a storage slot the projectile classes register under different names.
    private static float SlotFloat(Reader r, string[] names, float absent)
    {
        foreach (var n in names)
            if (r.Has(n))
                return r.F(n, absent);
        return absent;
    }

    // src/game/datablock_pack.cpp packProjectile: Projectile 0x00459458. Slots a class does not register keep the
    // ProjectileData constructor value (0x00459384).
    private static void PackProjectile(Reader r, EventWriter w)
    {
        PackGameBase(r, w);
        PackShapeName(r, w, r.S("bulletShapeName", null));
        PutU32(w, (uint)r.Ref("explosionTag", 0));
        PutU32(w, (uint)r.I("expRandCycle", 0));
        w.PutFlag(r.B("collideWithOwner", false));
        PutU32(w, (uint)r.I("ownerGraceMS", 1000));
        PutF32(w, r.F("collisionRadius", 0));
        PutF32(w, r.F("mass", 1));
        PutF32(w, SlotFloat(r, ["muzzleVelocity", "maxLevelFlightDist"], 0)); // +0x68
        PutF32(w, r.F("terminalVelocity", 0));                                        // +0x6c
        PutF32(w, r.F("acceleration", 0));                                            // +0x70
        PutF32(w, r.F("totalTime", 10000));
        PutF32(w, SlotFloat(r, ["liveTime", "beamTime"], 10000));                          // +0x78
        PutF32(w, SlotFloat(r, ["tracerPercentage", "projSpecialTime", "smokeDist"], 0));  // +0x7c
        // +0x80: only BulletData registers isVisible. No constructor writes it; the reference server sends it set for
        // every other class, so they are sent visible.
        w.PutFlag(r.B("isVisible", true));
        PutF32(w, r.F("lightRange", 0));
        for (var c = 0; c < 3; ++c)
            PutF32(w, r.V3("lightColor", c, 0));
        PutU32(w, (uint)(r.Has("hitSoundId") ? r.Ref("hitSoundId", -1) : r.Ref("soundId", -1))); // +0x98
        PutF32(w, r.F("explosionRadius", 0));
    }

    // ---- groups ----

    private static readonly double kInv360 = Math.ScaleB(0xb60bb60bb60bb60bUL, -72);

    // src/game/datablock_pack.cpp packSoundProfile: SoundProfileData 0x00485124: the optional fields are sent when they
    // differ from a default-constructed profile (0x004850ac).
    private static void PackSoundProfile(Reader r, EventWriter w)
    {
        w.PutInt((uint)r.I("flags", 0), 6);
        PutUnit(w, r.F("baseVolume", 0), 10);
        // The constructor stores the cone angles as the integers 90 and 180; a script set stores float bits. Both are
        // compared and scaled as the raw 32-bit integer times 1/360 (an 80-bit constant), stored to a float.
        string[] angles = ["coneInsideAngle", "coneOutsideAngle"];
        uint[] angleDefaults = [90, 180];
        for (var k = 0; k < 2; ++k)
        {
            Value? v = r.ValueOf(angles[k]);
            var raw = v != null ? BitConverter.SingleToUInt32Bits(v.F[0]) : angleDefaults[k];
            w.PutFlag(raw != angleDefaults[k]);
            if (raw != angleDefaults[k])
                PutUnit(w, Store(Mul(raw, kInv360)), 10);
        }
        float[] cone = [r.V3("coneVector", 0, 0), r.V3("coneVector", 1, -1), r.V3("coneVector", 2, 0)];
        var coneSet = Differs(cone[0], 0) || Differs(cone[1], -1) || Differs(cone[2], 0);
        w.PutFlag(coneSet);
        if (coneSet)
            PutNormal(w, cone, 10);
        string[] tail = ["coneOutsideVolume", "minDistance", "maxDistance"];
        float[] tailDefaults = [0, 30, 300];
        for (var k = 0; k < 3; ++k)
        {
            var v = r.F(tail[k], tailDefaults[k]);
            w.PutFlag(Differs(v, tailDefaults[k]));
            if (Differs(v, tailDefaults[k]))
                PutF32(w, v);
        }
    }

    // src/game/datablock_pack.cpp packSound: SoundData 0x00485580.
    private static void PackSound(Reader r, EventWriter w)
    {
        w.PutString(r.S("wavFileName", null));
        PutUnit(w, r.F("priority", 0), 6);
        PutIndex(w, r.Ref("profile", -1));
    }

    // src/game/datablock_pack.cpp packDamageSkin: DamageSkinData 0x00449ee8.
    private static void PackDamageSkin(Reader r, EventWriter w)
    {
        for (var k = 0; k < 10; ++k)
        {
            Value? v = r.ValueOf("bmpName", k);
            w.PutString(v?.Str ?? "");
        }
    }

    // src/game/datablock_pack.cpp packPlayer: PlayerData 0x00417e1c.
    private static void PackPlayer(Reader r, EventWriter w)
    {
        PackShapeBase(r, w);
        w.PutString(r.S("flameShapeName", null));
        var zero = new Value(); // animData is uninitialized storage until set (D11)
        for (var k = 0; k < 51; ++k)
        {
            Value v = r.ValueOf("animData", k) ?? zero;
            w.PutString(v.Str ?? "");
            w.PutFlag(v.I != -1); // sound
            if (v.I != -1)
                PutIndex(w, v.I);
            w.PutFlag(v.I2 > 0); // direction
            w.PutInt((uint)v.I3, 4);
            w.PutInt((uint)v.I4, 4);
        }
        PutF32(w, r.F("minDamageSpeed", 0));
        PutF32(w, r.F("jetEnergyDrain", 0));
        foreach (var n in new[] { "maxForwardSpeed", "maxBackwardSpeed", "maxSideSpeed" })
            PutUnit(w, MulF(r.F(n, 0), 1.0f / 64.0f), 10);
        w.PutFlag(r.B("canCrouch", false));
        PutUnit(w, r.F("maxJetSideForceFactor", 0), 8);
        foreach (var n in new[] { "groundForce", "groundTraction", "maxJetForwardVelocity" })
            PutF32(w, r.F(n, 0));
        PutUnit(w, r.F("jumpSurfaceMinDot", 0), 8);
        foreach (var n in new[] { "jumpImpulse", "mass", "drag", "horzVelClamp", "jetForce" })
            PutF32(w, r.F(n, 0));
        PutIndex(w, r.Ref("jetSound", -1));
        for (var k = 0; k < 15; ++k)
        {
            PutIndex(w, r.Ref("rFootSounds", -1, k));
            PutIndex(w, r.Ref("lFootSounds", -1, k));
        }
        PutU32(w, (uint)r.I("footPrints", 0, 0));
        PutU32(w, (uint)r.I("footPrints", 0, 1));
        foreach (var n in new[] { "boxWidth", "boxDepth", "boxNormalHeight", "boxCrouchHeight" })
            PutF32(w, r.F(n, 0));
    }

    // src/game/datablock_pack.cpp packItem: ItemData 0x00450aa8, also MineData: ShapeBase, not StaticBase.
    private static void PackItem(Reader r, EventWriter w)
    {
        PackShapeBase(r, w);
        PutU32(w, (uint)r.Ref("imageType", -1));
        PutU32(w, (uint)r.I("price", 0));
        foreach (var n in new[] { "mass", "drag", "density", "friction", "elasticity" })
            PutF32(w, r.F(n, 0));
        w.PutString(r.S("hudIcon", null));
        foreach (var n in new[] { "showInventory", "showWeaponBar", "hiliteOnActive" })
            w.PutFlag(r.B(n, false));
        w.PutString(r.S("heading", null));
        PutU32(w, (uint)r.I("lightType", 0));
        PutF32(w, r.F("lightRadius", 0));
        PutF32(w, r.F("lightTime", 0));
        for (var c = 0; c < 3; ++c)
            PutF32(w, r.V3("lightColor", c, 0));
    }

    // src/game/datablock_pack.cpp packItemImage: ItemImageData 0x00418a64.
    private static void PackItemImage(Reader r, EventWriter w)
    {
        PackGameBase(r, w);
        w.PutString(r.S("shapeFile", null));
        PackProjectileRef(r, w, "projectileType");
        PutU32(w, (uint)r.I("mountPoint", 0));
        foreach (var n in new[] { "mountOffset", "mountRotation" })
            for (var c = 0; c < 3; ++c)
                PutF32(w, r.V3(n, c, 0));
        w.PutFlag(r.B("firstPerson", false));
        PutU32(w, (uint)r.I("weaponType", 0));
        PutU32(w, (uint)r.Ref("ammoType", -1));
        foreach (var n in new[] { "activateTime", "fireTime", "reloadTime", "spinUpTime", "spinDownTime", "mass" })
            PutF32(w, r.F(n, 0));
        foreach (var n in new[] { "sfxActivate", "sfxFire", "sfxReady", "sfxReload", "sfxSpinUp", "sfxSpinDown" })
            PutIndex(w, r.Ref(n, -1));
        PutU32(w, (uint)r.I("lightType", 0));
        PutF32(w, r.F("lightRadius", 0));
        PutF32(w, r.F("lightTime", 0));
        for (var c = 0; c < 3; ++c)
            PutF32(w, r.V3("lightColor", c, 0));
        PutF32(w, r.F("minEnergy", 0));
        PutF32(w, r.F("maxEnergy", 0));
    }

    // src/game/datablock_pack.cpp packMoveable: MoveableData 0x00443498.
    private static void PackMoveable(Reader r, EventWriter w)
    {
        PackStaticBase(r, w);
        foreach (var n in new[] { "displace", "blockForward", "blockBackward" })
            w.PutFlag(r.B(n, false));
        foreach (var n in new[] { "sfxStart", "sfxStop", "sfxRun", "sfxBlocked" })
            PutIndex(w, r.Ref(n, -1));
        PutF32(w, r.F("speed", 0));
    }

    // src/game/datablock_pack.cpp packSensor: SensorData 0x00438d28.
    private static void PackSensor(Reader r, EventWriter w)
    {
        PackStaticBase(r, w);
        PutU32(w, (uint)r.I("range", 0));
        PutU32(w, (uint)r.I("dopplerVelocity", 0));
        w.PutFlag(r.B("supression", false));
        w.PutFlag(r.B("pinger", false));
    }

    // src/game/datablock_pack.cpp packVehicle: VehicleData 0x00447558.
    private static void PackVehicle(Reader r, EventWriter w)
    {
        PackStaticBase(r, w);
        foreach (var n in new[] { "mass", "drag", "density", "maxSpeed", "minSpeed" })
            PutF32(w, r.F(n, 0));
        w.PutInt(r.B("visibleDriver", false) ? 1u : 0u, 8); // the bool byte, raw
        PutU32(w, (uint)r.I("driverPose", 0));
        foreach (var n in new[] { "fireSound", "damageSound", "mountSound", "dismountSound", "idleSound", "moveSound" })
            PutIndex(w, r.Ref(n, -1));
        // suspension (+0x140) is a TankData field: other vehicle classes send uninitialized storage, zero here (D11).
        for (var k = 0; k < 4; ++k)
        {
            Value? v = r.ValueOf("suspension", k);
            for (var c = 0; c < 3; ++c)
                PutF32(w, v != null ? v.F[c] : 0.0f);
        }
    }

    // src/game/datablock_pack.cpp packFlier: FlierData 0x00430948.
    private static void PackFlier(Reader r, EventWriter w)
    {
        PackVehicle(r, w);
        foreach (var n in new[] { "maxBank", "maxPitch", "lift", "maxAlt", "maxVertical", "accel", "groundDamageScale" })
            PutF32(w, r.F(n, 0));
    }

    // src/game/datablock_pack.cpp packBullet: BulletData 0x0045b8e4.
    private static void PackBullet(Reader r, EventWriter w)
    {
        PackProjectile(r, w);
        PutF32(w, r.F("rotationPeriod", 0));
        PutU32(w, (uint)r.I("bulletHoleIndex", -1));
        PutF32(w, r.F("tracerLength", 10));
    }

    // src/game/datablock_pack.cpp packGrenade: GrenadeData 0x0045ec28.
    private static void PackGrenade(Reader r, EventWriter w)
    {
        PackProjectile(r, w);
        PutF32(w, r.F("elasticity", 0.5f));
        w.PutString(r.S("smokeName", "smoke.dts"));
    }

    // src/game/datablock_pack.cpp packRocket: RocketData 0x00460960.
    private static void PackRocket(Reader r, EventWriter w)
    {
        PackProjectile(r, w);
        PutU32(w, (uint)r.I("trailType", 0));
        w.PutString(r.S("trailString", null));
        PutF32(w, r.F("trailLength", 10));
        PutF32(w, r.F("trailWidth", DataBlockManager.CtorFloat("0.3")));
    }

    // src/game/datablock_pack.cpp packLaser: LaserData, TargetLaserData 0x00462860 (+0xa4 is hitName or hitShapeName).
    private static void PackLaser(Reader r, EventWriter w)
    {
        PackProjectile(r, w);
        w.PutString(r.S("laserBitmapName", "laserPulse.bmp"));
        w.PutString(r.Has("hitShapeName") ? r.S("hitShapeName", null) : r.S("hitName", null));
        w.PutFlag(r.B("detachFromShooter", false));
    }

    // src/game/datablock_pack.cpp packTurret: TurretData 0x00457ab4.
    private static void PackTurret(Reader r, EventWriter w)
    {
        PackSensor(r, w);
        PackProjectileRef(r, w, "projectileType");
        foreach (var n in new[] { "gunRange", "speed", "speedModifier", "FOV" })
            PutF32(w, r.F(n, 0));
        foreach (var n in new[] { "fireSound", "activationSound", "deactivateSound", "whirSound" })
            PutIndex(w, r.Ref(n, -1));
        w.PutFlag(r.B("isSustained", false));
    }

    // src/game/datablock_pack.cpp packExplosion: ExplosionData 0x0046dbe0.
    private static void PackExplosion(Reader r, EventWriter w)
    {
        PackGameBase(r, w);
        PackShapeName(r, w, r.S("shapeName", null));
        PutU32(w, (uint)r.Ref("soundId", -1));
        PutF32(w, r.F("timeScale", 1));
        w.PutFlag(r.B("faceCamera", false));
        w.PutFlag(r.B("randomSpin", false));
        var light = r.B("hasLight", false);
        w.PutFlag(light);
        if (light)
        {
            foreach (var n in new[] { "lightRange", "timeZero", "timeOne" })
                PutF32(w, r.F(n, 0));
            for (var k = 0; k < 3; ++k)
            {
                Value? v = r.ValueOf("colors", k);
                for (var c = 0; c < 3; ++c)
                    PutF32(w, v != null ? v.F[c] : 0.0f);
            }
            for (var k = 0; k < 3; ++k)
                PutF32(w, r.F("radFactors", 0, k));
        }
        w.PutFlag(r.B("shiftPosition", false));
    }

    // src/game/datablock_pack.cpp packMarker: MarkerData 0x00485e0c.
    private static void PackMarker(Reader r, EventWriter w)
    {
        PackGameBase(r, w);
        w.PutString(r.S("shapeFile", null));
    }

    // src/game/datablock_pack.cpp packDebris: DebrisData 0x00470bec.
    private static void PackDebris(Reader r, EventWriter w)
    {
        PackGameBase(r, w);
        PutU32(w, (uint)r.I("type", 0));
        w.PutString(r.S("pShapeName", null));
        PutU32(w, (uint)r.I("imageType", 0));
        PutU32(w, (uint)r.I("imageSortValue", 0));
        foreach (var n in new[] { "mass", "elasticity", "friction" })
            PutF32(w, r.F(n, 0));
        for (var k = 0; k < 3; ++k)
            PutF32(w, r.F("center", 0, k));
        foreach (var n in new[] { "collisionMask", "knockMask", "animationSequence" })
            PutU32(w, (uint)r.I(n, 0));
        foreach (var n in new[] { "animRestartTime", "minTimeout", "maxTimeout", "explodeOnBounce", "damage", "damageThreshold" })
            PutF32(w, r.F(n, 0));
        PutU32(w, (uint)r.I("spawnedDebrisMask", 0));
        PutF32(w, r.F("spawnedDebrisStrength", 0));
        PutF32(w, r.F("spawnedDebrisRadius", 0));
        PutU32(w, (uint)r.Ref("spawnedExplosionID", -1));
        PutF32(w, r.F("p", 0));
        w.PutFlag((r.I("explodeOnRest", 0) & 0xff) != 0); // writeFlag of the int's low byte
        w.PutInt((uint)r.I("collisionDetail", 0), 2);
    }

    // src/game/datablock_pack.cpp packSeekingMissile: SeekingMissileData 0x00466108.
    private static void PackSeekingMissile(Reader r, EventWriter w)
    {
        PackProjectile(r, w);
        PutF32(w, r.F("smokeDist", 0));
        PutF32(w, r.F("seekingTurningRadius", 15));
        PutF32(w, r.F("nonSeekingTurningRadius", 50));
    }

    // src/game/datablock_pack.cpp packLightning: LightningData, RepairEffectData 0x00467d84.
    private static void PackLightning(Reader r, EventWriter w)
    {
        PackProjectile(r, w);
        PutU32(w, (uint)r.I("segmentDivisions", 1));
        PutU32(w, (uint)r.I("numSegments", 2)); // RepairEffectData: not a field, ctor 2
        PutU32(w, (uint)r.I("updateTime", 0));
        PutF32(w, r.F("skipPercent", 0));
        PutF32(w, r.F("displaceBias", 0.25f));
        PutF32(w, r.F("beamWidth", DataBlockManager.CtorFloat("0.2")));
        w.PutString(r.S("bitmapName", "lightningTemp.bmp"));
    }

    /// <summary>Writes the pack layout of a datablock.</summary>
    /// <param name="db">The datablock manager.</param>
    /// <param name="block">The block to pack.</param>
    /// <param name="w">The event writer.</param>
    /// <returns>False for groups that cannot be sent.</returns>
    public static bool PackDataBlock(DataBlockManager db, Block block, EventWriter w)
    {
        _ = db;
        using var fpu = ChopScope();
        var r = new Reader(block);
        switch (block.Group)
        {
            case 0:
                PackSoundProfile(r, w);
                return true;
            case 1:
                PackSound(r, w);
                return true;
            case 2:
                PackDamageSkin(r, w);
                return true;
            case 3:
                PackPlayer(r, w);
                return true;
            case 4:
                PackStaticBase(r, w);
                return true;
            case 5:
            case 23:
                PackItem(r, w);
                return true;
            case 6:
                PackItemImage(r, w);
                return true;
            case 7:
                PackMoveable(r, w);
                return true;
            case 8:
                PackSensor(r, w);
                return true;
            case 9:
                PackVehicle(r, w);
                return true;
            case 10:
                PackFlier(r, w);
                return true;
            case 13:
                PackProjectile(r, w);
                return true;
            case 14:
                PackBullet(r, w);
                return true;
            case 15:
                PackGrenade(r, w);
                return true;
            case 16:
                PackRocket(r, w);
                return true;
            case 17:
            case 24:
                PackLaser(r, w);
                return true;
            case 19:
                PackTurret(r, w);
                return true;
            case 20:
                PackExplosion(r, w);
                return true;
            case 21:
                PackMarker(r, w);
                return true;
            case 22:
                PackDebris(r, w);
                return true;
            case 25:
                PackSeekingMissile(r, w);
                return true;
            case 26:
            case 30:
                PackGameBase(r, w);
                return true;
            case 28:
            case 29:
                PackLightning(r, w);
                return true;
            default:
                return false; // 11, 12, 18, 27: the factory has no case, never sent
        }
    }
}
