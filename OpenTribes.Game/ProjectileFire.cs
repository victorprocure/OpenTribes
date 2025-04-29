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

    // src/game/projectile_fire.cpp castLOS: ShapeBase_castLOS (0x0044bea4): the first object hit from the translation
    // of `from` to (0, range, 0) through it.
    private static bool CastLOS(GameState game, in Transform from, uint mask, SimObject? exclude, float range, ref RayHit hit)
    {
        float[] local = { 0.0f, range, 0.0f };
        float[] end = new float[3];
        X87.MulPoint(local, from.M, from.P, end);
        return Projectile.CastWorld(game, from.P, end, mask, exclude, ref hit);
    }

    // src/game/projectile_fire.cpp projectileEyeTransform.
    public static Transform ProjectileEyeTransform(Player p) => p.EyeTransform(); // reads the shape instance cache

    // src/game/projectile_fire.cpp projectileCastLOS.
    public static bool ProjectileCastLOS(GameState game, in Transform from, uint mask, SimObject? exclude, float range, ref RayHit hit) =>
        CastLOS(game, from, mask, exclude, range, ref hit);

    // src/game/projectile_fire.cpp aimedMuzzle: ShapeBase_getAimedMuzzleTransform (0x0044c148).
    private static Transform AimedMuzzle(GameState game, Player pl, int slot)
    {
        pl.MuzzleTransform(slot, out Transform m);
        Transform e = pl.EyeTransform();
        var hit = new RayHit();
        if (CastLOS(game, e, 0xffffffffu, pl, 1200.0f, ref hit))
        {
            float[] d = { X87.F32(Sub(hit.Point[0], m.P[0])), X87.F32(Sub(hit.Point[1], m.P[1])), X87.F32(Sub(hit.Point[2], m.P[2])) };
            if (!(X87.Lenf(d) < 2.0f))
            {
                float[] dir = new float[3], x = new float[3], z = new float[3];
                float k = X87.F32(Div(1.0, X87.F32(X87.Len(d))));
                for (int i = 0; i < 3; ++i)
                    dir[i] = X87.F32(Mul(d[i], k));
                float[] up = { 0, 0, 1 };
                Cross(up, dir, x);
                if (X87.Len(x) > 0.0)
                {
                    // D11: a hit straight above or below keeps the muzzle axis
                    Normalise(x);
                    Cross(x, dir, z);
                    Normalise(z);
                    var output = new Transform();
                    for (int i = 0; i < 3; ++i)
                    {
                        output.M[i] = x[i];
                        output.M[3 + i] = dir[i];
                        output.M[6 + i] = z[i];
                        output.P[i] = m.P[i];
                    }
                    output.Flags = 3;
                    return output;
                }
            }
        }
        return m;
    }

    // src/game/projectile_fire.cpp createFor.
    private static Projectile? CreateFor(Player pl, int slot, bool sustained)
    {
        GameState game = pl.Game();
        Player.ImageSlot s = pl.Images[slot];
        Transform t;
        if (s.Info.AccuFire && !pl.Dead)
            t = AimedMuzzle(game, pl, slot);
        else if (!pl.MuzzleTransform(slot, out t))
            return null;
        Projectile? pr = Projectile.Create(game, s.Info.ProjGroup, s.Info.ProjIndex);
        if (pr == null)
            return null;
        pr.Init(t, pl.Velocity, pl.Id());
        if (pr.WantsTarget())
        {
            var hit = new RayHit();
            if (CastLOS(game, pl.EyeTransform(), 0xffffffffu, pl, pr.TargetRange(), ref hit) && hit.Object != null &&
                hit.Object.IsShapeBase())
                pr.SetTarget(hit.Object);
        }
        if (!sustained)
        {
            if (s.Info.AmmoType != -1)
            {
                pl.SetItemCount(s.Info.AmmoType, pl.ItemCountOf(s.Info.AmmoType) - 1);
            }
            else
            {
                using var chop = X87.Chop();
                float used, fraction;
                if (s.Info.MaxEnergy > pl.Energy)
                {
                    used = pl.Energy;
                    fraction = X87.F32(Div(pl.Energy, s.Info.MaxEnergy));
                    pl.Energy = 0.0f;
                }
                else
                {
                    used = s.Info.MaxEnergy;
                    fraction = 1.0f;
                    pl.Energy = X87.F32(Sub(pl.Energy, s.Info.MaxEnergy));
                }
                pr.SetShotEnergy(used, fraction);
            }
        }
        return pr;
    }

    // src/game/projectile_fire.cpp projectileFromImage: image fire paths of the Player (docs/projectile-sim.md 1.1,
    // player-sim.md 7.5): create and register the projectile of a slot (sustained: keep it in the slot).
    public static void ProjectileFromImage(Player pl, int slot, bool sustained)
    {
        SimManager? m = pl.Game().Sim?.Server();
        if (m == null)
            return;
        Projectile? pr = CreateFor(pl, slot, sustained);
        if (pr == null)
            return;
        if (!pr.RegisterWith(m))
            return; // dropped by registerWith
        if (sustained)
            pl.Images[slot].Projectile = pr.Id();
    }

    // src/game/projectile_fire.cpp projectileUpdateFromImage: update the transform of a sustained projectile.
    public static void ProjectileUpdateFromImage(Player pl, int slot)
    {
        SimManager? m = pl.Game().Sim?.Server();
        Player.ImageSlot s = pl.Images[slot];
        Projectile? pr = m != null ? Projectile.From(m.FindById(s.Projectile)) : null;
        if (pr == null)
        {
            s.Projectile = 0;
            return;
        }
        Transform t;
        if (s.Info.AccuFire && !pl.Dead)
            t = AimedMuzzle(pl.Game(), pl, slot);
        else if (!pl.MuzzleTransform(slot, out t))
            return;
        pr.UpdateTransform(t);
    }

    // src/game/projectile_fire.cpp projectileShutOffFromImage: shut a sustained projectile off.
    public static void ProjectileShutOffFromImage(Player pl, int slot)
    {
        SimManager? m = pl.Game().Sim?.Server();
        Projectile? pr = m != null ? Projectile.From(m.FindById(pl.Images[slot].Projectile)) : null;
        pr?.ShutOff();
    }

    private static readonly int[] kProjectileGroups = { 14, 15, 16, 17, 24, 25, 28, 29 };

    // src/game/projectile_fire.cpp registerProjectileCommands: Projectile::spawnProjectile (1.3, 0x004223a8) and the
    // transforms behind GameBase::getLOSInfo and GameBase::getMuzzleTransform (docs/projectile-sim.md 1.5).
    public static void RegisterCommands(Interpreter interp, GameState game)
    {
        BacklogHooks.EyeTransform = (obj, eye) =>
        {
            if (Player.From(obj) is not Player pl)
                return (false, eye);
            return (true, pl.EyeTransform()); // vtable +0x1a4 of a Player is a copy of +0x110
        };
        BacklogHooks.MuzzleTransform = (obj, t) =>
        {
            if (Player.From(obj) is Player pl)
            {
                // vtable +0x10c with slot 0, else the eye transform (0x00422f52).
                if (!pl.MuzzleTransform(0, out Transform mt))
                    mt = pl.EyeTransform();
                return (true, mt);
            }
            if (GameUtil.EqualsNoCase(obj.ClassName(), "Turret"))
            {
                // Turret_getMuzzleTransform (0x004565e0).
                // P4b: Turret: obj.turretMuzzleTransform(t) under the Chop guard (turret_fire.cpp).
            }
            return (true, t); // base classes: the object transform (0x0044827c)
        };
        interp.AddCommand("Projectile::spawnProjectile", (inr, a) =>
        {
            SimManager? m = game.Sim?.Server();
            if (m == null || (a.Length != 5 && a.Length != 6))
                return "-1";
            // The type is searched by name in the projectile groups in table order.
            DataBlockManager.Block? b = game.Sim!.DataBlocks().Find(a[1]);
            bool isProjectile = false;
            if (b != null)
                foreach (int grp in kProjectileGroups)
                    isProjectile = isProjectile || b.Group == grp;
            if (!isProjectile)
            {
                inr.Print(1, "Projectile data block \"" + a[1] + "\" is undefined");
                return "-1";
            }
            Projectile? pr = Projectile.Create(game, b!.Group, b.Index);
            if (pr == null)
                return "-1";
            // D11: missing transform values are zero (the original keeps stack garbage).
            float[] parsed = new float[12];
            int n = CScan.Floats(a[2], parsed);
            float[] v = new float[12];
            for (int i = 0; i < 12; ++i)
                v[i] = i < n ? parsed[i] : (i < 9 ? ((i % 4) == 0 ? 1.0f : 0.0f) : 0.0f);
            var t = new Transform();
            for (int i = 0; i < 9; ++i)
                t.M[i] = v[i];
            for (int i = 0; i < 3; ++i)
                t.P[i] = v[9 + i];
            t.Flags = 3;
            int shooter = -1;
            CScan.Int(a[3], ref shooter);
            float[] vel = { 0, 0, 0 };
            CScan.Floats(a[4], vel);
            pr.Init(t, vel, shooter);
            if (a.Length == 6 && pr.WantsTarget())
            {
                SimObject? o = m.Find(a[5]);
                if (o is GameBase gb && gb.IsShapeBase())
                    pr.SetTarget(gb);
            }
            if (!pr.RegisterWith(m))
                return "-1";
            return Numbers.FormatInt(pr.Id());
        });
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
