// Port of src/game/player_ghost.cpp: Player ghost packUpdate (0x00417148, docs/game-stream-ghosts.md 4.7) with the image
// slots (0x00419414, docs/player-sim.md 7.2).
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

public static partial class PlayerGhost
{
    // src/game/player_ghost.cpp ftolBits.
    internal static uint FtolBits(double x, uint bits)
    {
        var i = double.IsFinite(x) && Math.Abs(x) < 2147483648.0 ? (long)Math.Truncate(x) : 0;
        return (uint)i & ((1u << (int)bits) - 1);
    }

    // src/game/player_ghost.cpp writeUnit: BitStream::writeFloat (0x00582fb0).
    internal static void WriteUnit(BitStream bs, float v, uint bits) =>
        bs.WriteInt(FtolBits(Mul((int)((1u << (int)bits) - 1), v), bits), bits);

    // src/game/player_ghost.cpp writeSignedUnit: writeSignedFloat (0x00583018).
    internal static void WriteSignedUnit(BitStream bs, float v, uint bits) =>
        bs.WriteInt(FtolBits(Mul(Mul(Add(v, 1.0f), 0.5f), (int)((1u << (int)bits) - 1)), bits), bits);

    // src/game/player_ghost.cpp writeNormal: BitStream_writeNormalVector: x and y as signed units, then the sign of z.
    internal static void WriteNormal(BitStream bs, ReadOnlySpan<float> n, uint bits)
    {
        WriteSignedUnit(bs, n[0], bits);
        WriteSignedUnit(bs, n[1], bits);
        bs.WriteFlag(n[2] < 0.0f);
    }

    // src/game/player_ghost.cpp packShapeBase1: ShapeBase part 1 (0x0044cff8, 4.2): GameBase part, flash, threads, fade.
    // Same layout as the StaticBase classes in ghost_classes.cpp.
    internal static void PackShapeBase1(GameBase gb, BitStream bs, uint mask)
    {
        if (bs.WriteFlag((mask & 0x04) != 0))
        {
            bs.WriteInt((uint)gb.Team, 4);
            if (bs.WriteFlag(gb.ControlClient != 0))
                bs.WriteInt((uint)(gb.ControlClient - 0x800), 7);
            if (bs.WriteFlag(gb.OwnerClient != 0))
                bs.WriteInt((uint)(gb.OwnerClient - 0x800), 7);
        }
        if ((mask & 0x01) != 0)
        {
            mask &= ~0x480u;
            foreach (var t in gb.Threads)
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
            WriteUnit(bs, X87.F32(Mul(0.1, Add(a, 5.0f))), 8);
        }
        if (bs.WriteFlag((mask & 0x400) != 0))
            for (var i = 0; i < 3; ++i)
            {
                var t = gb.Threads[i];
                if (bs.WriteFlag(t.Sequence != -1 && (mask & (0x800u << i)) != 0))
                {
                    bs.WriteInt((uint)t.Sequence, 4);
                    bs.WriteInt((uint)(t.Flags & GameBase.ThreadState), 2);
                    bs.WriteFlag((t.Flags & GameBase.ThreadForward) != 0);
                    bs.WriteFlag((t.Flags & GameBase.ThreadAtEnd) != 0);
                }
            }
        gb.UpdateFade(); // a fade in reverts 2.5 s after its start (0x0044cff8)
        if (bs.WriteFlag(gb.FadeState != 0))
            bs.WriteFlag(gb.FadeState == 2);
    }

    // src/game/player_ghost.cpp packPlayer.
    private static uint PackPlayer(SimObject obj, GhostContext ctx, BitStream bs, uint mask)
    {
        using var chop = X87.Chop();
        var p = (Player)obj;
        // The control client's connection gets no state block: it has the control state.
        var controller = p.ControlClient == ctx.ClientId;
        var isNew = mask == 0xffffffffu;
        PackShapeBase1(p, bs, mask);
        if (bs.WriteFlag((mask & 0x04) != 0))
        {
            // GameBase_packDataBlock (0x00447efc): bitLength(group count + 1) bits.
            uint n = (uint)p.DataBlockCount() + 1, bits = 1;
            while (bits < 32 && (n >> (int)bits) != 0)
                ++bits;
            bs.WriteInt((uint)p.DataBlockIndex, bits);
            bs.WriteFlag(p.AiControlled);
        }
        // Mount (0x8000): `flag` mounted -> ghost index of the mount object (10 bits) and the point (5); dismounted is
        // flag 1, flag 0. While the mount object has no ghost on this connection the outer flag is 0 and the bit stays
        // pending (docs/game-stream-ghosts.md 4.7).
        uint pending = 0;
        if ((mask & 0x8000) != 0)
        {
            var mo = p.MountObject();
            var gi = mo != null ? ctx.Manager.GhostIndex(mo) : -1;
            if (mo != null && gi < 0)
            {
                bs.WriteFlag(false);
                pending = 0x8000;
            }
            else
            {
                bs.WriteFlag(true);
                if (bs.WriteFlag(mo != null))
                {
                    bs.WriteInt((uint)gi, 10);
                    bs.WriteInt((uint)p.MountPoint & 31u, 5);
                }
            }
        }
        else
        {
            bs.WriteFlag(false);
        }
        if (bs.WriteFlag((mask & 0x100) != 0))
        {
            if (!bs.WriteFlag(p.Dead))
                bs.WriteInt(FtolBits(Mul(Div(p.Damage, p.PData.MaxDamage), 63.0), 6), 6);
            else if (!bs.WriteFlag(p.BlownUp))
                bs.WriteInt((uint)p.CurrentAnim & 0x3f, 6);
        }
        if (bs.WriteFlag(isNew || ((mask & 0x2000) != 0 && !controller)))
        {
            WriteSignedUnit(bs, X87.F32(Div(p.Pitch, 1.5359)), 5);
            bs.WriteF32s(p.Transform.P);
            var speed = X87.F32(X87.Len(p.Velocity));
            if (bs.WriteFlag(speed > 0.02))
            {
                var inv = X87.F32(Div(1.0, speed));
                float[] dir = [X87.F32(Mul(inv, p.Velocity[0])), X87.F32(Mul(inv, p.Velocity[1])), X87.F32(Mul(inv, p.Velocity[2]))];
                var scaled = X87.F32(Mul(speed, 512.0f));
                if (scaled > 131071.0f)
                    scaled = 131071.0f;
                bs.WriteInt(FtolBits(scaled, 17), 17);
                WriteNormal(bs, dir, 10);
            }
            bs.WriteFlag(p.ContactFlag);
            bs.WriteInt(FtolBits(Div(Mul(p.Rotation[2], 511.0), 6.283185307179586), 9), 9);
            if (!isNew)
            {
                Moves.PackMove(bs, p.LastMove, false, null);
                bs.WriteInt(FtolBits(Div(Mul(p.Energy, 127.0), p.PData.MaxEnergy), 7), 7);
                bs.WriteInt((uint)(p.TicksWithoutMove > 14 ? 15 : p.TicksWithoutMove), 4);
                bs.WriteFlag((mask & 0x10000) != 0);
            }
        }
        if (isNew)
            bs.WriteFlag(false);
        else if (bs.WriteFlag((mask & 0x4000) != 0 && p.PendingAnim != 0))
            bs.WriteInt((uint)p.PendingAnim & 0x3f, 6);
        if (bs.WriteFlag((controller || isNew) && (mask & 0x02) != 0))
            bs.WriteF32(p.RechargeRate);
        bs.WriteFlag(p.PdaFlag);
        bs.WriteFlag(p.Crouching);
        // Player_packImages (0x00419414).
        if (bs.WriteFlag((mask & 0x20000) != 0))
            for (var i = 0; i < Player.ImageSlots; ++i)
            {
                var s = p.Images[i];
                if (!bs.WriteFlag((mask & (0x100000u << i)) != 0))
                    continue;
                bs.WriteInt((uint)(s.Item + 1) & 0xff, 8);
                if (bs.WriteFlag(s.Team != -1))
                    bs.WriteInt((uint)s.Team, 3);
                bs.WriteFlag(s.Triggered);
                bs.WriteFlag(s.Ammo);
                bs.WriteInt((uint)s.FireCount & 7, 3);
                if ((mask & 0x01) != 0)
                    bs.WriteFlag(s.State == 1);
            }
        return pending;
    }

    /// <summary>Registers the Player ghost class (tag 0x218).</summary>
    public static void Register() => GhostManager.RegisterGhostClass("Player", new GhostClass { Tag = 0x218, Pack = PackPlayer });
}
