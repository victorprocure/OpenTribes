// Port of src/game/ghost_classes.hpp and src/game/ghost_classes.cpp: ghost packUpdate of the mission environment classes
// (docs/game-stream-ghosts.md 3) and of the GameBase family (4): StaticShape, Sensor, Turret, Item, Mine, Moveable,
// Marker, Trigger; the Player, Vehicle and projectile classes register theirs from here.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

public static partial class GhostClasses
{
    private const uint kInitialMask = 0x80000000u;

    private static PersistFields FieldsOf(SimObject obj) => ((PersistObject)obj).Fields();

    private static void WriteF32(BitStream bs, float v) => bs.WriteF32(v);
    private static void WriteVec(BitStream bs, ReadOnlySpan<float> v) => bs.WriteF32s(v[..3]);

    // src/game/ghost_classes.cpp writePoint: persist point3F field with its constructor default.
    private static void WritePoint(BitStream bs, PersistFields f, string name, float x, float y, float z, int index = -1)
    {
        Span<float> p = stackalloc float[3];
        f.Point(name, p, x, y, z, index);
        WriteVec(bs, p);
    }

    // ---- 3.1: no mask bits ----

    // src/game/ghost_classes.cpp packSimVolume: SimVolume 0x005081b8: file name (+0x68).
    private static uint PackSimVolume(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        SimClasses.SimVolumeInfo(obj, out var file, out _);
        bs.WriteString(file);
        return 0;
    }

    // src/game/ghost_classes.cpp packSimPalette: SimPalette 0x004fd908: fileName.
    private static uint PackSimPalette(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        bs.WriteString(FieldsOf(obj).Str("fileName"));
        return 0;
    }

    // src/game/ghost_classes.cpp packMissionCenterPos: MissionCenterPos 0x004ce9e4: x y w h, constructor defaults 1230
    // 1230 1400 1400.
    private static uint PackMissionCenterPos(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        PersistFields f = FieldsOf(obj);
        WriteF32(bs, f.Real("x", 1230.0f));
        WriteF32(bs, f.Real("y", 1230.0f));
        WriteF32(bs, f.Real("w", 1400.0f));
        WriteF32(bs, f.Real("h", 1400.0f));
        return 0;
    }

    // src/game/ghost_classes.cpp packSimTerrain: 3.2 SimTerrain 0x0051ca08.
    private static uint PackSimTerrain(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        var t = (TerrainObject)obj;
        PersistFields f = t.Fields();
        if (bs.WriteFlag((mask & 0x01) != 0))
            bs.WriteString(f.Str("tedFileName"));
        if (bs.WriteFlag((mask & 0x02) != 0))
        {
            WriteF32(bs, f.Real("visibleDistance", 3000.0f));
            WriteF32(bs, f.Real("hazeDistance", 2000.0f));
            WriteF32(bs, f.Real("hazeVerticalMin", 1000.0f));
            WriteF32(bs, f.Real("hazeVerticalMax", 2000.0f));
        }
        if (bs.WriteFlag((mask & 0x04) != 0))
        {
            WriteF32(bs, f.Real("perspectiveDistance", 500.0f));
            WriteF32(bs, f.Real("screenSize", 120.0f));
        }
        if (bs.WriteFlag((mask & 0x08) != 0))
        {
            WritePoint(bs, f, "position", 0, 0, 0);
            WritePoint(bs, f, "rotation", 0, 0, 0);
        }
        if (bs.WriteFlag((mask & 0x10) != 0))
        {
            WritePoint(bs, f, "contGravity", 0, 0, -9.8f);
            WriteF32(bs, f.Real("contDrag", 0.0f));
            WriteF32(bs, f.Real("contHeight", 10000.0f));
        }
        bs.WriteFlag(false); // +0x6a4 rendering suppressed, set only by the editor (0x00518f6c)
        if (bs.WriteFlag((mask & 0x40) != 0))
            bs.WriteInt(t.Crc(), 32);
        return 0;
    }

    // src/game/ghost_classes.cpp packSimLight: 3.3 SimLight 0x0052dac8. Fields the constructor leaves unset (range,
    // color, ambient, position, aim) are sent as 0 (D12).
    private static uint PackSimLight(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        PersistFields f = FieldsOf(obj);
        if (bs.WriteFlag((mask & 0x40) != 0))
            bs.WriteFlag(f.Boolean("isStatic", false));
        if (bs.WriteFlag((mask & 0x01) != 0))
            bs.WriteInt((uint)f.Integer("type", 0), 3);
        if (bs.WriteFlag((mask & 0x02) != 0))
            WriteF32(bs, f.Real("range", 0.0f));
        if (bs.WriteFlag((mask & 0x04) != 0))
            WritePoint(bs, f, "color", 0, 0, 0);
        if (bs.WriteFlag((mask & 0x08) != 0))
            WritePoint(bs, f, "ambient", 0, 0, 0);
        if (bs.WriteFlag((mask & 0x10) != 0))
            WritePoint(bs, f, "position", 0, 0, 0);
        if (bs.WriteFlag((mask & 0x20) != 0))
            WritePoint(bs, f, "aim", 0, 0, 0);
        return 0;
    }

    // src/game/ghost_classes.cpp packGameBase: GameBase part 0x00448d80 (4.1).
    private static void PackGameBase(GameBase gb, BitStream bs, uint mask)
    {
        if (bs.WriteFlag((mask & 0x04) != 0))
        {
            bs.WriteInt((uint)gb.Team, 4);
            if (bs.WriteFlag(gb.ControlClient != 0)) // +0x230
                bs.WriteInt((uint)(gb.ControlClient - 0x800), 7);
            if (bs.WriteFlag(gb.OwnerClient != 0)) // +0x238
                bs.WriteInt((uint)(gb.OwnerClient - 0x800), 7);
        }
    }

    // src/game/ghost_classes.cpp packInteriorShape: 3.4 InteriorShape 0x0047ff9c.
    private static uint PackInteriorShape(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        var gb = (GameBase)obj;
        PersistFields f = gb.Fields();
        PackGameBase(gb, bs, mask);
        InteriorNetInfo net = gb.InteriorNet;
        if (bs.WriteFlag((mask & kInitialMask) != 0))
        {
            // 0x0047ff04
            bs.WriteString(f.Str("filename"));
            bs.WriteSignedInt(gb.IsContainer, 2);
            WritePoint(bs, f, "position", 0, 0, 0);
            WritePoint(bs, f, "rotation", 0, 0, 0);
            bs.WriteInt((uint)net.StateWords.Count, 32);
            foreach (var w in net.StateWords)
                bs.WriteInt(w, 32);
            bs.WriteInt((uint)net.Lights.Count, 32);
            foreach (InteriorNetInfo.Light l in net.Lights)
            {
                // {f32 intensity, u8 on, 3 padding bytes, u32 0, u32 0}. The original's padding is uninitialized heap
                // memory; sent as 0 (D12).
                var rec = new byte[16];
                BitConverter.TryWriteBytes(rec.AsSpan(0, 4), l.Intensity);
                rec[4] = l.On ? (byte)1 : (byte)0;
                bs.WriteBytes(rec);
            }
            bs.WriteFlag(gb.CollisionDisabled);
            return 0;
        }
        if (bs.WriteFlag((mask & 0x01) != 0))
        {
            WritePoint(bs, f, "position", 0, 0, 0);
            WritePoint(bs, f, "rotation", 0, 0, 0);
        }
        if (bs.WriteFlag((mask & 0x02) != 0))
            foreach (InteriorNetInfo.Light l in net.Lights)
                bs.WriteFlag(l.On);
        if (bs.WriteFlag((mask & 0x04) != 0))
            bs.WriteFlag(false); // +0x418 bit 0, set by event 0x429 (not modeled)
        return 0;
    }

    // ---- 4 GameBase family ----

    // src/game/ghost_classes.cpp storeF32: f32 store of an x87 result under the engine's control word (round toward
    // zero).
    private static float StoreF32(double x) => X87.F32(x);

    // src/game/ghost_classes.cpp writeUnit: BitStream::writeFloat (0x00582fb0): ftol(value * (2^bits - 1)), low bits only,
    // so a negative value wraps (Item rotation) and nothing is clamped.
    private static void WriteUnit(BitStream bs, float v, uint bits)
    {
        var x = Mul((int)((1u << (int)bits) - 1), v);
        var i = double.IsFinite(x) && Math.Abs(x) < 2147483648.0 ? (long)Math.Truncate(x) : 0;
        bs.WriteInt((uint)i & ((1u << (int)bits) - 1), bits);
    }

    // src/game/ghost_classes.cpp writeSignedUnit: BitStream::writeSignedFloat (0x00583018): (value + 1) * 0.5 through
    // writeFloat.
    private static void WriteSignedUnit(BitStream bs, float v, uint bits)
    {
        var x = Mul(Mul(Add(v, 1.0f), 0.5f), (int)((1u << (int)bits) - 1));
        var i = double.IsFinite(x) && Math.Abs(x) < 2147483648.0 ? (long)Math.Truncate(x) : 0;
        bs.WriteInt((uint)i & ((1u << (int)bits) - 1), bits);
    }

    // src/game/ghost_classes.cpp packDataBlock: GameBase_packDataBlock (0x00447efc): index (+0x220) in bitLength(group
    // count + 1) bits.
    private static void PackDataBlock(GameBase gb, BitStream bs)
    {
        uint n = (uint)gb.DataBlockCount() + 1, bits = 1;
        while (bits < 32 && (n >> (int)bits) != 0)
            ++bits;
        bs.WriteInt((uint)gb.DataBlockIndex, bits);
    }

    // src/game/ghost_classes.cpp packShapeBase1: ShapeBase part 1 (0x0044cff8): flash, threads, fade.
    private static void PackShapeBase1(GameBase gb, BitStream bs, uint mask)
    {
        PackGameBase(gb, bs, mask);
        if ((mask & 0x01) != 0)
        {
            // A full update drops the flash and sends the threads only when one is set.
            mask &= ~0x480u;
            foreach (GameBase.Thread t in gb.Threads)
                if (t.Sequence != -1)
                {
                    mask |= 0x400;
                    break;
                }
        }
        if (bs.WriteFlag((mask & 0x80) != 0))
        {
            WriteSignedUnit(bs, gb.FlashNormal[0], 7);
            WriteSignedUnit(bs, gb.FlashNormal[1], 7);
            bs.WriteFlag(gb.FlashNormal[2] < 0.0f);
            var a = gb.FlashAmount;
            if (a > 5.0f)
                a = 5.0f;
            if (a < -5.0f)
                a = -5.0f;
            WriteUnit(bs, StoreF32(Mul(0.1, Add(a, 5.0f))), 8);
        }
        if (bs.WriteFlag((mask & 0x400) != 0))
            for (var i = 0; i < 3; ++i)
            {
                GameBase.Thread t = gb.Threads[i];
                if (bs.WriteFlag(t.Sequence != -1 && (mask & (0x800u << i)) != 0))
                {
                    bs.WriteInt((uint)t.Sequence, 4);
                    bs.WriteInt((uint)(t.Flags & GameBase.kThreadState), 2);
                    bs.WriteFlag((t.Flags & GameBase.kThreadForward) != 0);
                    bs.WriteFlag((t.Flags & GameBase.kThreadAtEnd) != 0);
                }
            }
        // +0x280: a fade in (2) reverts to 0 once 2500 ms have passed since +0x284 (0x0044cff8).
        gb.UpdateFade();
        if (bs.WriteFlag(gb.FadeState != 0))
            bs.WriteFlag(gb.FadeState == 2);
    }

    // src/game/ghost_classes.cpp packShapeBase: ShapeBase part 2 (0x00450098): transform, damage, target flag.
    private static void PackShapeBase(GameBase gb, BitStream bs, uint mask)
    {
        PackShapeBase1(gb, bs, mask);
        if (bs.WriteFlag((mask & 0x18) != 0))
        {
            WriteVec(bs, gb.Transform.P);
            WriteVec(bs, gb.Rotation);
        }
        if (bs.WriteFlag((mask & 0x100) != 0))
        {
            if (!bs.WriteFlag(gb.DamageState == GameBase.kEnabled))
                bs.WriteFlag(gb.DamageState == GameBase.kDisabled);
            WriteUnit(bs, StoreF32(Div(gb.Damage, gb.MaxDamage)), 6);
        }
        if (bs.WriteFlag((mask & 0x02) != 0))
            bs.WriteFlag(gb.IsTarget);
    }

    // src/game/ghost_classes.cpp packStaticShape: 4.3 StaticShape 0x0045059c.
    private static uint PackStaticShape(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        var gb = (GameBase)obj;
        PackShapeBase(gb, bs, mask);
        if (bs.WriteFlag((mask & 0x02) != 0))
        {
            PackDataBlock(gb, bs);
            if (bs.WriteFlag(gb.TargetIndex != -1))
                bs.WriteInt((uint)gb.TargetIndex, 7);
        }
        return 0;
    }

    // src/game/ghost_classes.cpp packSensorPart: 4.3 Sensor 0x0043943c.
    private static void PackSensorPart(GameBase gb, BitStream bs, uint mask)
    {
        PackShapeBase(gb, bs, mask);
        if (bs.WriteFlag((mask & 0x01) != 0))
            PackDataBlock(gb, bs);
    }

    // src/game/ghost_classes.cpp packSensor.
    private static uint PackSensor(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        PackSensorPart((GameBase)obj, bs, mask);
        return 0;
    }

    // src/game/ghost_classes.cpp packTurret: 4.5 Turret 0x004573a0.
    private static uint PackTurret(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        var gb = (GameBase)obj;
        if (gb.ControlClient == ctx.ClientId && (mask & 0x05) == 0)
        {
            bs.WriteFlag(true); // the controlling client simulates its own turret
            return 0;
        }
        bs.WriteFlag(false);
        PackSensorPart(gb, bs, mask);
        if (bs.WriteFlag((mask & 0x2000) != 0))
            WriteUnit(bs, StoreF32(Mul(0.15915494309189533577, gb.TurretRotation)), 8);
        if (bs.WriteFlag((mask & 0x4000) != 0))
            WriteUnit(bs, StoreF32(Div(Sub(gb.TurretElevation, gb.TurretMinElevation), Sub(gb.TurretMaxElevation, gb.TurretMinElevation))), 8);
        if (bs.WriteFlag((mask & 0x8000) != 0))
            bs.WriteInt((uint)gb.TurretFireState, 2);
        if (bs.WriteFlag((mask & 0x10000) != 0))
            bs.WriteInt((uint)gb.TurretMode, 4);
        bs.WriteFlag(gb.TurretBarrelState == 2);
        return 0;
    }

    // src/game/ghost_classes.cpp packItemPart: 4.4 Item 0x00451e18; Mine 0x004590a0 drops the rotation of a mine at rest.
    private static uint PackItemPart(GameBase gb, GhostContext ctx, BitStream bs, uint mask)
    {
        PackShapeBase(gb, bs, mask & ~0x38u);
        if (bs.WriteFlag((mask & 0x02) != 0))
        {
            PackDataBlock(gb, bs);
            if (bs.WriteFlag(gb.TargetIndex != -1))
                bs.WriteInt((uint)gb.TargetIndex, 7);
            var thrower = gb.ItemThrower != null ? ctx.Manager.GhostIndex(gb.ItemThrower) : -1;
            if (bs.WriteFlag(thrower != -1))
                bs.WriteInt((uint)thrower, 10);
            bs.WriteFlag(gb.ItemRotates);
            bs.WriteFlag(gb.ItemCollideable);
        }
        if (bs.WriteFlag((mask & 0x10) != 0))
            WriteUnit(bs, StoreF32(Mul(0.15915494309189533577, gb.Rotation[2])), 8);
        if (bs.WriteFlag((mask & 0x08) != 0))
            WriteVec(bs, gb.Transform.P);
        if (!bs.WriteFlag(gb.AtRest || gb.ItemRotates))
            if (bs.WriteFlag((mask & 0x20) != 0))
                WriteVec(bs, gb.Velocity);
        return 0;
    }

    // src/game/ghost_classes.cpp packItem.
    private static uint PackItem(SimObject obj, GhostContext ctx, BitStream bs, uint mask) => PackItemPart((GameBase)obj, ctx, bs, mask);

    // src/game/ghost_classes.cpp packMine.
    private static uint PackMine(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        var gb = (GameBase)obj;
        if (gb.AtRest)
            mask &= ~0x10u;
        return PackItemPart(gb, ctx, bs, mask);
    }

    // src/game/ghost_classes.cpp packMoveable: 4.6 Moveable 0x00444af0.
    private static uint PackMoveable(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        var gb = (GameBase)obj;
        PackShapeBase(gb, bs, mask & ~0x38u);
        if ((mask & 0x01) != 0)
        {
            PackDataBlock(gb, bs);
            if (gb.PathId == 0xff)
                mask &= ~0x6008u; // no path yet: nothing to say about it
        }
        if (bs.WriteFlag((mask & 0x2000) != 0))
            bs.WriteInt((uint)gb.PathId, 8);
        if (bs.WriteFlag((mask & 0x08) != 0))
        {
            bs.WriteInt((uint)gb.Waypoint, 4);
            WriteF32(bs, gb.WaypointTime);
        }
        if (bs.WriteFlag((mask & 0x4000) != 0))
        {
            bs.WriteFlag(gb.MoveBlocked);
            bs.WriteInt((uint)gb.MoveTarget, 4);
            bs.WriteInt((uint)gb.MoveState, 2);
        }
        return 0;
    }

    // src/game/ghost_classes.cpp packMarker: 4.9 Marker 0x00486410: position (+0x188) and rotation (+0x1ac) are the
    // persist fields.
    private static uint PackMarker(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        var gb = (GameBase)obj;
        PackGameBase(gb, bs, mask);
        if (bs.WriteFlag((mask & 0x01) != 0))
            PackDataBlock(gb, bs);
        if (bs.WriteFlag((mask & 0x18) != 0))
        {
            WritePoint(bs, gb.Fields(), "position", 0, 0, 0);
            WritePoint(bs, gb.Fields(), "rotation", 0, 0, 0);
        }
        return 0;
    }

    // src/game/ghost_classes.cpp packTrigger: 4.9 Trigger 0x00487b58: the raw transform (+0x94: flags, 3x3, translation)
    // and the boundingBox persist field (+0x1d4).
    private static uint PackTrigger(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        var gb = (GameBase)obj;
        if (bs.WriteFlag((mask & 0x08) != 0))
        {
            bs.WriteInt(gb.Transform.Flags, 32);
            bs.WriteF32s(gb.Transform.M);
            bs.WriteF32s(gb.Transform.P);
        }
        if (bs.WriteFlag((mask & 0x80) != 0))
        {
            float[] box = [0, 0, 0, 0, 0, 0];
            if (gb.PersistValue("boundingBox") is IReadOnlyList<string> v && v.Count != 0)
                CScan.Floats(v[0], box);
            bs.WriteF32s(box);
        }
        return 0;
    }

    // src/game/ghost_classes.cpp packSnowfall: 3.5 Snowfall 0x00442fac.
    private static uint PackSnowfall(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        PersistFields f = FieldsOf(obj);
        var rain = f.Boolean("rain", false);
        if (bs.WriteFlag((mask & 0x01) != 0))
            WriteF32(bs, f.Real("intensity", 0.0f));
        if (bs.WriteFlag((mask & 0x02) != 0))
        {
            // Global wind (0x004419f0): x and y of the wind field, z -35 for snow, -75 for rain.
            Span<float> wind = stackalloc float[3];
            f.Point("wind", wind, 0, 0, 0);
            wind[2] = rain ? -75.0f : -35.0f;
            WriteVec(bs, wind);
        }
        bs.WriteFlag(f.Boolean("suspendRendering", true));
        bs.WriteFlag(rain);
        return 0;
    }

    // src/game/ghost_classes.cpp packSky: Sky 0x00477780. skyColor and hazeColor are not set by the constructor (D12: 0).
    private static uint PackSky(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        PersistFields f = FieldsOf(obj);
        bs.WriteString(f.Str("dmlName"));
        WritePoint(bs, f, "skyColor", 0, 0, 0);
        WritePoint(bs, f, "hazeColor", 0, 0, 0);
        WriteF32(bs, f.Real("featurePosition", 0.0f));
        WriteF32(bs, f.Real("size", 600.0f));
        for (var i = 0; i < 16; ++i)
            bs.WriteInt((uint)f.Integer("textures", i % 2, i), 4);
        return 0;
    }

    // src/game/ghost_classes.cpp packPlanet: Planet 0x0047cfcc. onAdd (0x0047c4c8) clamps incidence to [-89, 89].
    private static uint PackPlanet(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        PersistFields f = FieldsOf(obj);
        var incidence = f.Real("incidence", 30.0f);
        if (incidence > 89.0f)
            incidence = 89.0f;
        if (incidence < -89.0f)
            incidence = -89.0f;
        bs.WriteString(f.Str("fileName"));
        WriteF32(bs, f.Real("azimuth", 0.0f));
        WriteF32(bs, incidence);
        bs.WriteInt(f.Boolean("castShadows", false) ? 1u : 0u, 8);
        bs.WriteInt(f.Boolean("useLensFlare", false) ? 1u : 0u, 8);
        WritePoint(bs, f, "intensity", 0, 0, 0);
        WritePoint(bs, f, "ambient", 0, 0, 0);
        WriteF32(bs, f.Real("size", 2000.0f));
        WriteF32(bs, f.Real("distance", 11000.0f));
        return 0;
    }

    // src/game/ghost_classes.cpp packStarField: StarField 0x00478e0c: two constructor flags (+0xaf = 1, +0xb0 = 0),
    // colors[3], inFrontOfSky.
    private static uint PackStarField(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        PersistFields f = FieldsOf(obj);
        bs.WriteFlag(true);
        bs.WriteFlag(false);
        WritePoint(bs, f, "colors", 1.0f, 1.0f, 1.0f, 0);
        WritePoint(bs, f, "colors", 0.5f, 0.5f, 0.5f, 1);
        WritePoint(bs, f, "colors", 0.25f, 0.25f, 0.25f, 2);
        bs.WriteFlag(f.Boolean("inFrontOfSky", true));
        return 0;
    }

    /// <summary>Packs the ShapeBase part of a ghost update for the vehicle layout.</summary>
    /// <param name="gb">The object.</param>
    /// <param name="bs">The stream to write.</param>
    /// <param name="mask">The dirty mask.</param>
    /// <remarks>ShapeBase part 2 of a ghost update (docs/game-stream-ghosts.md 4.2).</remarks>
    public static void PackShapeBasePart(GameBase gb, BitStream bs, uint mask) => PackShapeBase(gb, bs, mask);

    private static GhostClass G(uint tag, GhostClass.PackFn pack) => new() { Tag = tag, Pack = pack };

    /// <summary>Registers the ghost classes with the ghost manager.</summary>
    public static void Register()
    {
        GhostManager.RegisterGhostClass("SimVolume", G(0x001, PackSimVolume));
        GhostManager.RegisterGhostClass("SimTerrain", G(0x002, PackSimTerrain));
        GhostManager.RegisterGhostClass("SimPalette", G(0x003, PackSimPalette));
        GhostManager.RegisterGhostClass("SimLight", G(0x006, PackSimLight));
        GhostManager.RegisterGhostClass("InteriorShape", G(0x20a, PackInteriorShape));
        GhostManager.RegisterGhostClass("Snowfall", G(0x216, PackSnowfall));
        GhostManager.RegisterGhostClass("Planet", G(0x22f, PackPlanet));
        GhostManager.RegisterGhostClass("Sky", G(0x230, PackSky));
        GhostManager.RegisterGhostClass("StarField", G(0x232, PackStarField));
        GhostManager.RegisterGhostClass("MissionCenterPos", G(0x2cc, PackMissionCenterPos));
        GhostManager.RegisterGhostClass("Turret", G(0x202, PackTurret));
        GhostManager.RegisterGhostClass("Marker", G(0x21b, PackMarker));
        GhostManager.RegisterGhostClass("Mine", G(0x21e, PackMine));
        GhostManager.RegisterGhostClass("Trigger", G(0x21f, PackTrigger));
        GhostManager.RegisterGhostClass("Moveable", G(0x220, PackMoveable));
        GhostManager.RegisterGhostClass("StaticShape", G(0x221, PackStaticShape));
        GhostManager.RegisterGhostClass("Sensor", G(0x229, PackSensor));
        GhostManager.RegisterGhostClass("Item", G(0x22a, PackItem));
        PlayerGhost.Register();
        Vehicle.RegisterGhostClasses();
        ProjectileGhost.Register();
    }
}
