// Port of src/game/projectile_fire.cpp: projectile creation from an item image fire and from
// Projectile::spawnProjectile (docs/projectile-sim.md 1.1, 1.3), with the Player muzzle, eye and pull-back transforms
// (docs/projectile-sim.md 1.5).
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

public static partial class ProjectileFire
{
    // src/game/projectile_fire.cpp normalise.
    private static void Normalise(Span<float> v)
    {
        float k = X87.F32(Div(1.0, X87.F32(X87.Len(v))));
        for (int i = 0; i < 3; ++i)
            v[i] = X87.F32(Mul(v[i], k));
    }

    // src/game/projectile_fire.cpp cross.
    private static void Cross(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> output)
    {
        output[0] = X87.F32(Sub(Mul(a[1], b[2]), Mul(a[2], b[1])));
        output[1] = X87.F32(Sub(Mul(a[2], b[0]), Mul(a[0], b[2])));
        output[2] = X87.F32(Sub(Mul(a[0], b[1]), Mul(a[1], b[0])));
    }

    // src/game/projectile_fire.cpp toTMat.
    internal static TMat3 ToTMat(in Transform t)
    {
        var m = new TMat3();
        m.Flags = t.Flags;
        m.M = t.M;
        m.P = t.P;
        return m;
    }

    // src/game/projectile_fire.cpp toTransform.
    internal static Transform ToTransform(in TMat3 m)
    {
        var t = new Transform();
        t.Flags = m.Flags & 7u;
        t.M = m.M;
        t.P = m.P;
        return t;
    }

    // src/game/projectile_fire.cpp detailNode: TSShapeInstance_findDetailNode (0x005c2754) at detail 0: the node named
    // name + itoa(ftol(size of detail 0)).
    internal static int DetailNode(ShapeModel model, string name)
    {
        if (model.Details.Length == 0)
            return -1;
        float size = model.Details[0].Size;
        if (!(size > -2147483648.0f && size < 2147483648.0f))
            return -1;
        return model.FindNode(name + ((int)size).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // src/game/projectile_fire.cpp pitchAt: pitch rotation at a translation: Mat3_fromEulerWrap of (pitch, 0, 0),
    // flags |= 2.
    internal static TMat3 PitchAt(float pitch, ReadOnlySpan<float> t)
    {
        var m = new TMat3();
        m.P = Float3.Of(t);
        float[] e = { pitch, 0.0f, 0.0f };
        Shape.EulerMatrix(e, ref m);
        m.Flags |= 2u;
        return m;
    }
}

public sealed partial class Player
{
    private static readonly string[] kMountNodeNames = { "dummy hand", "dummy unused", "dummy midback", "dummy lowback" };

    // src/game/projectile_fire.cpp Player::imageMountTransform: Player_getImageMountTransform (0x00419ba0), object space.
    // False when the armor has no node for the mount point.
    public bool ImageMountTransform(int slot, out TMat3 output)
    {
        output = new TMat3();
        if (slot < 0 || slot >= kImageSlots || Images[slot].Image == -1)
            return false;
        using var chop = X87.Chop();
        ImageInfo img = Images[slot].Info;
        var m = new TMat3();
        m.P = Float3.Of(img.MountOffset);
        if (img.MountPoint == 0)
            m.P[1] = X87.F32(Add(Pullback, m.P[1]));
        Shape.EulerMatrix(img.MountRotation, ref m);
        m.Flags |= 2u;
        // Node table +0x7c4: names with the detail size appended (0x0040fcdc). A mount point outside the table has no node
        // (the original reads past it, D11).
        ShapeInstance? inst = ShapeState.ShapeInstanceOf(mGame, this);
        int node = inst != null && img.MountPoint >= 0 && img.MountPoint < 4
                       ? ProjectileFire.DetailNode(inst.ShapeData!, kMountNodeNames[img.MountPoint])
                       : -1;
        if (node < 0)
            return false;
        TMat3 e = inst!.Entries[node];
        if (!Dead && (img.MountPoint == 0 || img.MountPoint == 1) && (inst.ViewMode != 0 || AiControlled))
        {
            TMat3 p = ProjectileFire.PitchAt(Pitch, e.P);
            Shape.TmatMul(m, p, out output);
        }
        else
        {
            Shape.TmatMul(m, e, out output);
        }
        return true;
    }

    // src/game/projectile_fire.cpp Player::muzzleObjectTransform: Player_getMuzzleTransform (vtable +0x10c, 0x00419d88),
    // object space; false without an item.
    public bool MuzzleObjectTransform(int slot, out TMat3 output)
    {
        output = new TMat3();
        if (slot < 0 || slot >= kImageSlots || Images[slot].Item == -1)
            return false;
        using var chop = X87.Chop();
        ImageInfo img = Images[slot].Info;
        ShapeInstance? image = Images[slot].Image != -1 && img.ShapeFile != null ? ShapeState.StaticShapeInstance(mGame, img.ShapeFile) : null;
        int n = image != null ? image.ShapeData!.FindNode("dummyalways muzzle") : -1;
        if (n < 0)
        {
            ImageMountTransform(slot, out output);
            return true;
        }
        ImageMountTransform(slot, out TMat3 t);
        Shape.TmatMul(image!.Entries[n], t, out output);
        return true;
    }

    // src/game/projectile_fire.cpp Player::eyeTransform: Player_getEyeTransform (vtable +0x110, 0x0041248c), world space.
    public override Transform EyeTransform()
    {
        using var chop = X87.Chop();
        ShapeInstance? inst = ShapeState.ShapeInstanceOf(mGame, this);
        int n = inst != null ? ProjectileFire.DetailNode(inst.ShapeData!, "dummy eye") : -1;
        var l = new TMat3();
        if (n >= 0)
            l = ProjectileFire.PitchAt(Pitch, inst!.Entries[n].P);
        Shape.TmatMul(l, ProjectileFire.ToTMat(Transform), out TMat3 output);
        return ProjectileFire.ToTransform(output);
    }

    // src/game/projectile_fire.cpp Player::muzzleTransform: world muzzle of a shot without aim correction (0x0041b986):
    // muzzle x object transform.
    public bool MuzzleTransform(int slot, out Transform output)
    {
        output = new Transform();
        if (!MuzzleObjectTransform(slot, out TMat3 mz))
            return false;
        using var chop = X87.Chop();
        Shape.TmatMul(mz, ProjectileFire.ToTMat(Transform), out TMat3 w);
        output = ProjectileFire.ToTransform(w);
        return true;
    }

    // src/game/projectile_fire.cpp Player::updateWeaponPullback: Player_updateWeaponPullback (0x004198d4): +0xfd0 for the
    // first mount point 0 image.
    public void UpdateWeaponPullback()
    {
        Pullback = 0.0f;
        int slot = -1;
        for (int i = 0; i < kImageSlots && slot < 0; ++i)
            if (Images[i].Image != -1 && Images[i].Info.MountPoint == 0)
                slot = i;
        if (slot < 0)
            return;
        using var chop = X87.Chop();
        if (!ImageMountTransform(slot, out TMat3 mt) || !MuzzleObjectTransform(slot, out TMat3 mz))
            return;
        float[] start = new float[3], end = new float[3], d2 = new float[3], neg = new float[3];
        X87.MulPoint(mt.P, Transform.M, Transform.P, start);
        X87.MulPoint(mz.P, Transform.M, Transform.P, end);
        for (int i = 0; i < 3; ++i)
        {
            d2[i] = X87.F32(Sub(end[i], start[i]));
            neg[i] = -start[i];
        }
        object? obj = null;
        Vec3 hp = default, n = default;
        if (!mGame.World.CastRay(new Vec3(start[0], start[1], start[2]), new Vec3(end[0], end[1], end[2]), 0xbfffffffu, this,
                                 ref obj, ref hp, ref n))
            return;
        float[] h = { hp.X, hp.Y, hp.Z };
        float[] d1v = new float[3], fwd = new float[3];
        for (int i = 0; i < 3; ++i)
        {
            d1v[i] = X87.F32(Add(h[i], neg[i]));
            // Row 1 of mount x object, 3x3 only (0x005ed570).
            fwd[i] = X87.F32(Add(Add(Mul(mt.M[3], Transform.M[i]), Mul(mt.M[4], Transform.M[3 + i])), Mul(mt.M[5], Transform.M[6 + i])));
        }
        float d1 = X87.F32(X87.Dot(d1v, fwd));
        double dd2 = X87.Dot(d2, fwd);
        Pullback = X87.F32(Sub(Sub(d1, dd2), 0.2));
    }
}
