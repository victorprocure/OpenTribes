// Player ghost visual (docs/shapes-render.md 3.4, 3.6, 5): the armor shape with the animation and looks threads, mounted
// item images, the jet flame, footprints, the death pose and the burst into debris.
using OpenTribes.Client.Effects;
using OpenTribes.Client.Shapes;
using OpenTribes.Game;

namespace OpenTribes.Client.Scene;

public sealed class ImageMount
{
    public int Slot, Image;
    public ClientDataBlock? Db;
    public RenderShape? Shape;
    public RenderInstance? Inst;
    public ImageStateMachine? Sm;
    public double FireTime = -1000;       // scene time of the last Fire entry
    public int MountPoint;
    public bool FirstPerson;
}

public sealed class PlayerVisual : ShapeVisual
{
    private RenderThread? mAnim, mLooks;
    private int mAnimIndex = -1;
    private double mVx, mVy, mVz, mUpdateTime;
    private bool mDead, mContact, mJet, mJump, mCrouch;
    private double mSignalUntil, mNextChoice;
    private readonly ImageMount?[] mImages = new ImageMount?[8];
    private RenderShape? mFlameShape;
    private RenderInstance? mFlame;
    private RenderThread? mFlameThread;
    private bool mJetting;
    private readonly List<uint> mTriggers = new();
    public bool Exploded { get; private set; }
    public bool Jetting => mJetting;
    public int AnimIndex => mAnimIndex;
    public string SkinName => Skin;
    public IReadOnlyList<ImageMount?> Images => mImages;

    public override void Init(SceneModel m, ClientGhost g)
    {
        base.Init(m, g);
        Group = 3;
    }

    private (string Name, int Sound, bool Fwd, int ViewFlags, int Prio)? AnimData(int index)
    {
        if (Block == null)
            return null;
        object? v = Db.Get(Block, $"animData[{index}]");
        if (v is not object[] a || a.Length < 5)
            return null;
        return ((string)a[0], (int)Db.ToDouble(a[1]), Db.ToDouble(a[2]) != 0, (int)Db.ToDouble(a[3]), (int)Db.ToDouble(a[4]));
    }

    public override void OnUpdate(DecodedGhost u, bool isNew)
    {
        base.OnUpdate(u, isNew);
        if (Inst == null || Shape == null)
            return;
        if (mAnim == null)
        {
            mAnim = Inst.AddThread(Shape.FindSequence("root"), 0, 0);
            int looks = Shape.FindSequence("looks");
            if (looks >= 0)
            {
                mLooks = Inst.AddThread(looks, 0.5, -1);
                mLooks.TimeScale = 0;
            }
            string flame = Db.Str(Block, "flameShapeName");
            if (flame.Length > 0)
            {
                mFlameShape = M.Assets.Shape(flame);
                if (mFlameShape != null)
                {
                    mFlame = new RenderInstance(mFlameShape);
                    int amb = mFlameShape.FindSequence("ambient");
                    mFlameThread = mFlame.AddThread(amb >= 0 ? amb : 0, 0, 0);
                }
            }
        }
        bool has(string n) => u.Fields.Any(f => f.Name == n);
        // 2.9 inputs (a): the skin of the owning client, else the team skin.
        int cid = (int)Num("controlClient", Num("ownerClient", -1));
        string cs = cid >= 0 ? M.World.Players.Get(cid)?.Skin ?? "" : "";
        if (cs.Length > 0)
            Skin = cs;
        if (has("pos") && !Driven)
        {
            double[] p = Vec("pos"), v = Vec("vel");
            (X, Y, Z) = (p[0], p[1], p[2]);
            (mVx, mVy, mVz) = (v[0], v[1], v[2]);
            if (!has("vel"))
                (mVx, mVy, mVz) = (0, 0, 0);
            mUpdateTime = M.Time;
            HavePos = true;
        }
        if (has("rotZ") && !Driven)
            Yaw = Num("rotZ");
        if (has("pitch") && !Driven)
            ViewPitch = Num("pitch");
        if (has("flag1f4"))
            mContact = Num("flag1f4") != 0;
        if (has("mount") && Ghost.State.TryGetValue("mountGhost", out object? mg) && has("mount"))
            MountGhost = (int)Db.ToDouble(mg);
        if (has("move") && Ghost.State["move"] is string mv)
        {
            mJet = mv.Contains("jet=1", StringComparison.Ordinal);
            mJump = mv.Contains("jump=1", StringComparison.Ordinal);
        }
        if (has("flagfee"))
            mCrouch = Num("flagfee") != 0;
        if (has("f98") && !mDead)
        {
            int a = (int)Num("f98");
            SetAnimation(a);
            // signal animations (38 to 50) play once, then the movement choice takes over again
            if (a >= 38 && Shape != null && Shape.FindSequence(AnimData(a)?.Name ?? "") is int sq && sq >= 0)
                mSignalUntil = M.Time + Shape.Sequences[sq].Duration;
        }
        if (has("dead"))
        {
            bool exploded = !has("deathAnim");
            if (!mDead)
            {
                mDead = true;
                if (exploded)
                    Explode();
                else
                    SetAnimation((int)Num("deathAnim"));
            }
        }
        else if (has("damage"))
        {
            if (mDead && !Exploded)
            {
                // respawn on the same ghost: a living update after a death
            }
        }
        if (has("images"))
            foreach (ImageSlot s in ImageSlot.ParseAll(Ghost.State["images"]))
                ApplyImage(s, isNew);
        else if (u.Fields.Any(f => f.Name == "images"))
            { }
    }

    public int MountGhost = -1;
    public double ViewPitch;

    private void Explode()
    {
        if (Exploded || Inst == null)
            return;
        Exploded = true;
        int expl = (int)Db.Num(Block, "explosionId", -1);
        int debris = (int)Db.Num(Block, "debrisId", -1);
        var (cx, cy, cz) = PoseXf().Apply(Shape!.Cx, Shape.Cy, Shape.Cz);
        if (expl >= 0)
            M.SpawnExplosion(expl, cx, cy, cz, 0, 0, 1);
        if (debris >= 0)
            M.Effects.Add(new DebrisEffect(Inst, PoseXf(), mVx, mVy, mVz, M.Terrain, M.Rng));
        Hidden = true;
    }

    private void SetAnimation(int index)
    {
        if (Inst == null || Shape == null || mAnim == null || index == mAnimIndex)
            return;
        var ad = AnimData(index);
        if (ad == null)
            return;
        // docs/audio.md 3.2: a ghost plays the animation's sound (death scream, landing) when the animation starts.
        M.Audio?.AnimationSound(this, ad.Value.Sound);
        int seq = Shape.FindSequence(ad.Value.Name);
        if (seq < 0)
            return;
        mAnimIndex = index;
        mAnim.Priority = ad.Value.Prio;
        double dir = ad.Value.Fwd ? 1 : -1;
        mAnim.TimeScale = dir;
        double start = ad.Value.Fwd ? 0 : 0.99;
        if (M.AnimateWithTransitions)
            Inst.StartTransition(mAnim, seq, start, 0.12);
        else
        {
            mAnim.Sequence = seq;
            mAnim.Position = start;
        }
    }

    // player-sim.md 5.4 (first match wins): mounted, jetting or jumping in the air, falling, crouching, else the best of the four
    // ground motion candidates by the velocity along each travel direction (threshold 0.35 m/s). Every 65 ms.
    public int ChooseAnimation()
    {
        if (MountGhost >= 0 && Num("mountGhost", -1) >= 0)
            return 23;
        if (mJet || (mJump && !mContact))
            return 19;
        if (!mContact && mVz < -3)
            return 14;
        double c = Math.Cos(Yaw), s = Math.Sin(Yaw);
        double right = mVx * c + mVy * s, fwd = -mVx * s + mVy * c;
        (int Index, double Dir)[] cand = mCrouch
            ? new[] { (10, fwd), (11, -fwd), (12, -right), (13, right) }
            : new[] { (1, fwd), (2, -fwd), (3, -right), (4, right) };
        int pick = mCrouch ? 8 : 0;
        double best = 0.35;
        foreach (var (idx, d) in cand)
            if (d > best)
            {
                best = d;
                pick = idx;
            }
        return pick;
    }

    // The control player moves with the prediction or the control state, not with its ghost updates (ControlSample).
    public void ApplyControl(in ControlSample c)
    {
        (X, Y, Z) = (c.X, c.Y, c.Z);
        (mVx, mVy, mVz) = (c.Vx, c.Vy, c.Vz);
        mContact = c.Contact;
        mJet = c.Jetting;
        mCrouch = c.Crouch;
        Yaw = c.Yaw;
        mUpdateTime = M.Time;
        HavePos = true;
    }

    // Rotation x and y: only a corpse has them (the ground slope of Player.ClientCorpseStep).
    public double TiltX, TiltY;
    protected override Xf PoseXf() => Xf.Euler(TiltX, TiltY, Yaw).Then(Xf.Translate(X + mExtrapX, Y + mExtrapY, Z + mExtrapZ));
    private double mExtrapX, mExtrapY, mExtrapZ;

    // 3.4: ground motion animations (indices 1 to 4 and 10 to 13) run at a rate that follows the speed along the animation direction.
    private double SpeedScale()
    {
        if (!(mAnimIndex is >= 1 and <= 4 or >= 10 and <= 13))
            return 1;
        double speed = Math.Sqrt(mVx * mVx + mVy * mVy);
        double measured = Math.Max(Db.Num(Block, "maxForwardSpeed", 10.9), 1);
        return Math.Clamp(speed / measured, 0.66, 1.5);
    }

    private void ApplyImage(ImageSlot s, bool isNew)
    {
        if (s.Slot < 0 || s.Slot >= 8)
            return;
        ImageMount? im = mImages[s.Slot];
        if (s.Image < 0)
        {
            if (im != null)
                M.Audio?.ImageStateChanged(this, s.Slot);
            mImages[s.Slot] = null;
            return;
        }
        if (im == null || im.Image != s.Image)
        {
            if (im != null)
                M.Audio?.ImageStateChanged(this, s.Slot);
            // The stream carries the ItemData index (client-net.md 3.4); its imageType names the ItemImageData (group 6).
            ClientDataBlock? item = M.Db.Block(5, s.Image);
            ClientDataBlock? db = M.Db.Block(6, (int)Db.Num(item, "imageType", -1));
            string name = Db.Str(db, "shapeFile");
            RenderShape? sh = name.Length > 0 ? M.Assets.Shape(name) : null;
            im = new ImageMount { Slot = s.Slot, Image = s.Image, Db = db, Shape = sh, MountPoint = (int)Db.Num(db, "mountPoint"), FirstPerson = Db.Flag(db, "firstPerson") };
            if (sh != null)
            {
                im.Inst = new RenderInstance(sh);
                var data = new ImageStateData
                {
                    WeaponType = (int)Db.Num(db, "weaponType", -1), AmmoType = (int)Db.Num(db, "ammoType", -1),
                    ActivateTime = Db.Num(db, "activateTime"), FireTime = Db.Num(db, "fireTime"), ReloadTime = Db.Num(db, "reloadTime"),
                    SpinUpTime = Db.Num(db, "spinUpTime"), SpinDownTime = Db.Num(db, "spinDownTime"),
                };
                ImageMount mount = im;
                int slot = s.Slot;
                im.Sm = new ImageStateMachine(data, im.Inst)
                {
                    Fired = () => mount.FireTime = M.Time,
                    PlaySound = field => M.Audio?.ImageSound(this, slot, mount.Db, field),
                    StopLoopingSound = () => M.Audio?.ImageStateChanged(this, slot),
                };
                // A mount on a registered object enters Activate; a new ghost starts in Reset (player-sim.md 7.5).
                if (!isNew)
                    im.Sm.Mount();
            }
            mImages[s.Slot] = im;
        }
        im.Sm?.Unpack(s.Triggered, s.Ammo, s.Phase, !isNew, s.Initial);
    }

    // The control player's image state machines follow the prediction steps (ImageStateMachine.Stepped); steps = 0 with
    // `predicted` false hands them back to the time based update.
    public void SetImageSteps(bool predicted, int steps)
    {
        foreach (ImageMount? im in mImages)
            if (im?.Sm is { } sm)
            {
                sm.Stepped = predicted;
                if (predicted && steps > 0)
                    sm.StepMoves(steps);
            }
    }

    public override void Tick(double dt)
    {
        base.Tick(dt);
        if (Inst == null || mAnim == null)
            return;
        // extrapolate the last update by its velocity, at most 0.3 s
        if (!mDead && M.Time >= mNextChoice && M.Time >= mSignalUntil)
        {
            mNextChoice = M.Time + 0.065;
            SetAnimation(ChooseAnimation());
        }
        double age = Driven ? 0 : Math.Clamp(M.Time - mUpdateTime, 0, 0.3);
        (mExtrapX, mExtrapY, mExtrapZ) = (mVx * age, mVy * age, mVz * age);
        mAnim.TimeScale = (mAnim.TimeScale < 0 ? -1 : 1) * (mDead ? 1 : SpeedScale());
        mTriggers.Clear();
        Inst.Advance(mAnim, dt, mTriggers);
        if (mLooks != null)
            mLooks.Position = LooksPosition(ViewPitch);
        foreach (uint t in mTriggers)
        {
            ++M.TriggerCount;
            if (mContact)
                ++M.ContactTriggers;
            if (t is 1 or 2 && mContact && !mDead)
                M.AddFootprint(this, t == 1 ? 1 : -1);
            if (t is 1 or 2 && mContact && !mDead && MountGhost < 0)
                M.Audio?.Footstep(this, t == 1, X + mExtrapX, Y + mExtrapY, Z + mExtrapZ);
        }
        mJetting = mAnimIndex == 19 && !mDead;
        if (mFlame != null && mFlameThread != null)
            mFlame.Advance(mFlameThread, dt);
        foreach (ImageMount? im in mImages)
            im?.Sm?.Advance(dt);
    }

    protected override int DamageLevel => Skins.DamageLevel(Damage, M.Assets.DamageSkinDetail);

    public (double x, double y, double z) Foot(double side)
    {
        // 4.6: +-0.25 along the player's yaw frame x axis
        double cx = Math.Cos(Yaw), cy = Math.Sin(Yaw);
        return (X + mExtrapX + cx * 0.25 * side, Y + mExtrapY + cy * 0.25 * side, Z + mExtrapZ);
    }

    public double YawAngle => Yaw;
    public string ArmorShape => Shape?.Name ?? "";
    public int FootprintBase => (int)Db.Num(Block, "footPrintsIndex", -1);

    // The looks thread position of a view pitch (player-sim.md 5.2): 0.5 - pitch * 0.6511 / 2, the pitch range +-1.5359 maps to +-0.5.
    public static double LooksPosition(double pitch) => Math.Clamp(0.5 - pitch * 0.6511 * 0.5, 0, 1);

    // First person eye in object space (the `dummy eye` node, `Player_getEyeTransform`) with the looks thread at `pitch`.
    public bool TryEyeLocal(double pitch, out double x, out double y, out double z)
    {
        x = y = z = 0;
        if (Inst == null || Shape == null || mDead)
            return false;
        int n = Shape.FindNodeBase("dummy eye", 0);
        if (n < 0)
            return false;
        ViewModeSet(true);
        if (mLooks != null)
            mLooks.Position = LooksPosition(pitch);
        Inst.Animate(0);
        Xf e = Inst.NodeWorld[n];
        (x, y, z) = (e.Px, e.Py, e.Pz);
        return true;
    }

    // The height of the `dummyalways chasecam` node (Player +0x778) in the current pose: Player_getCameraTransform 00412324 keeps only the
    // node's z (x and y are set to 0) for the third person camera. False when the shape has no such node.
    public bool TryChaseZ(out double z)
    {
        z = 0;
        if (Inst == null || Shape == null || mDead)
            return false;
        int n = Shape.FindNodeBase("dummyalways chasecam", 0);
        if (n < 0)
            return false;
        Inst.Animate(0);
        z = Inst.NodeWorld[n].Pz;
        return true;
    }

    // The animation's view flag that applies to this camera (3.4): bit 0 first person, bit 1 chase view of the control object, bit 2 anyone
    // else. When it is set the instance view mode is 2 (first person) or 1 and the looks thread has priority -1 (it overrides the aim
    // nodes), else mode 0 and priority 5000 (the animation wins over the pitch). Returns whether the mode is set.
    private bool ViewModeSet(bool firstPerson, bool control = false)
    {
        var ad = AnimData(mAnimIndex);
        bool on = ad == null || (ad.Value.ViewFlags & (firstPerson ? 1 : control ? 2 : 4)) != 0;
        if (Inst != null)
            Inst.ViewMode = on ? (firstPerson ? 2 : 1) : 0;
        if (mLooks != null)
            mLooks.Priority = on ? -1 : 5000;
        return on;
    }

    // `pitch` set: the pitch-only node rotation of `Player_getImageMountTransform` (3.6 step 2) for mount points 0 and 1.
    public Xf ImageTransform(ImageMount im, int detail, Xf body, double? pitch = null)
    {
        RenderShape sh = Inst!.Shape;
        string[] names = { "dummy hand", "dummy unused", "dummy midback", "dummy lowback" };
        int node = im.MountPoint >= 0 && im.MountPoint < 4 ? sh.FindNodeBase(names[im.MountPoint], detail) : -1;
        Xf mount = node >= 0 ? Inst.NodeWorld[node] : Xf.Identity;
        if (node >= 0 && pitch is double pv && !mDead && im.MountPoint is 0 or 1)
        {
            Xf r = Xf.Euler(pv, 0, 0);
            (r.Px, r.Py, r.Pz) = (mount.Px, mount.Py, mount.Pz);
            mount = r;
        }
        Xf local = Xf.Euler(Db.Num(im.Db, "mountRotation.x"), Db.Num(im.Db, "mountRotation.y"), Db.Num(im.Db, "mountRotation.z"));
        local.Px = Db.Num(im.Db, "mountOffset.x"); local.Py = Db.Num(im.Db, "mountOffset.y"); local.Pz = Db.Num(im.Db, "mountOffset.z");
        return local.Then(mount).Then(body);
    }

    // Player_getMountPointTransform(2) [code 004aca84]: the `dummy midback` node in world space, null without the node or a position.
    public (double x, double y, double z)? MidBack()
    {
        if (Inst == null || Shape == null || !HavePos)
            return null;
        int n = Shape.FindNodeBase("dummy midback", 0);
        if (n < 0)
            return null;
        return Inst.NodeWorld[n].Then(PoseXf()).Apply(0, 0, 0);
    }

    // The muzzle of the first mounted weapon in world space (projectile start of beams), or the chest as a fallback.
    public override (double x, double y, double z)? MuzzlePoint => HavePos ? Muzzle() : null;

    public (double x, double y, double z) Muzzle()
    {
        Xf body = PoseXf();
        foreach (ImageMount? im in mImages)
        {
            if (im?.Inst == null || im.MountPoint != 0)
                continue;
            int d = Math.Max(M.Renderer.LastDetail, 0);
            Xf t = ImageTransform(im, Math.Min(d, Math.Max(Inst!.Shape.Details.Length - 1, 0)), body, ViewModeSet(false) ? ViewPitch : null);
            int mn = im.Shape!.FindNodeBase("dummyalways muzzle", 0);
            if (mn < 0)
                mn = im.Shape.FindNodeBase("dummy muzzle", 0);
            var (x, y, z) = mn >= 0 ? im.Inst.NodeWorld[mn].Then(t).Apply(0, 0, 0) : t.Apply(0, 0.5, 0);
            return (x, y, z);
        }
        var (bx, by, bz) = body.Apply(0, 0, 1.4);
        return (bx, by, bz);
    }

    // Player_renderQuery 004118e8: the pilot (mount slot 1) of a Vehicle or Flier is not drawn when that vehicle is the control object
    // seen from inside (first person, view distance 0), nor when the vehicle's datablock says the driver is not visible
    // (Vehicle_isDriverVisible, vtable +0x164).
    private bool PilotHidden(FrameContext ctx)
    {
        if (MountGhost < 0 || !Ghost.State.TryGetValue("mount", out object? ms) || ms is not string s || !s.EndsWith("/1", StringComparison.Ordinal))
            return false;
        if (ctx.FirstPersonOfControl && ctx.ControlGhost == MountGhost)
            return true;
        return M.Visuals.TryGetValue(MountGhost, out Visual? mv) && mv is ShapeVisual sv && !sv.DriverVisible;
    }

    public override void Build(FrameContext ctx)
    {
        if (Inst == null || Shape == null || !HavePos || Hidden)
            return;
        if (PilotHidden(ctx))
            return;
        bool firstPerson = ctx.FirstPersonOfControl && ctx.ControlGhost == Index;
        Xf body = PoseXf();
        double? pitchOnly = ViewModeSet(firstPerson, ctx.ControlGhost == Index) ? ViewPitch : null;
        if (firstPerson)
        {
            // The eye node (looks thread at the camera pitch, first person view mode) lands on the camera and the weapon hangs on the hand
            // node of the same pose: the body is the yaw turned transform that puts the eye there (5.2, `Player_getEyeTransform`).
            Xf yaw = Xf.Euler(0, 0, ctx.Camera.Yaw);
            TryEyeLocal(ctx.Camera.Pitch, out double ex, out double ey, out double ez);
            var (rx, ry, rz) = yaw.Apply(ex, ey, ez);
            body = yaw.Then(Xf.Translate(ctx.Camera.X - rx, ctx.Camera.Y - ry, ctx.Camera.Z - rz));
            if (pitchOnly != null)
                pitchOnly = ctx.Camera.Pitch;
        }
        double fade = FadeAmount();
        int detail = -1;
        if (!firstPerson)
        {
            ShapeDraw d = ctx.Draw(body, Shape.Name);
            d.Light = ProbedLight(ctx);
            d.Skin = Skin;
            d.DamageLevel = DamageLevel;
            d.Fade = fade;
            d.DetailScale = M.PlayerShapeDetail;
            d.NoRootTransform = true;
            RenderImage? img = ctx.Renderer.Build(Inst, ctx.Camera, d);
            detail = ctx.Renderer.LastDetail;
            if (img != null)
                ctx.Lists.Add(img);
        }
        else
        {
            detail = 0;
            Inst.Animate(0);
        }
        if (detail < 0)
            return;
        // Mounted images (3.6, 5.2): first person draws only the weapons, with the fixed sort key 0.999.
        foreach (ImageMount? im in mImages)
        {
            if (im?.Inst == null || im.Shape == null)
                continue;
            if (firstPerson && !im.FirstPerson)
                continue;
            Inst.Animate(Math.Min(detail, Shape.Details.Length - 1));
            // The renderer puts the image's root transform (node 0 default) in front of the mount transform (3.6).
            Xf xf = ImageTransform(im, Math.Min(detail, Math.Max(Shape.Details.Length - 1, 0)), body, pitchOnly);
            ShapeDraw d = ctx.Draw(xf, im.Shape.Name);
            d.Skin = Skin;
            d.Fade = fade;
            d.ForceTranslucent = firstPerson;
            if (firstPerson)
                d.FixedSortKey = 0.999;
            RenderImage? img = ctx.Renderer.Build(im.Inst, ctx.Camera, d);
            if (img != null)
                ctx.Lists.Add(img);
        }
        if (mJetting && mFlame != null && !firstPerson)
        {
            Inst.Animate(Math.Min(detail, Shape.Details.Length - 1));
            int n = Shape.FindNodeBase("dummy lowback", Math.Min(detail, Math.Max(Shape.Details.Length - 1, 0)));
            Xf mount = n >= 0 ? Inst.NodeWorld[n] : Xf.Identity;
            ShapeDraw d = ctx.Draw(mount.Then(body), mFlameShape!.Name);
            d.ForceTranslucent = true;
            d.SortBias = 0.01;
            RenderImage? img = ctx.Renderer.Build(mFlame, ctx.Camera, d);
            if (img != null)
                ctx.Lists.Add(img);
        }
    }

    // Dynamic lights of this player: the weapon fire light (4.9): lightType 3 decays linearly over lightTime.
    public void CollectLights(LightSet set)
    {
        foreach (ImageMount? im in mImages)
        {
            if (im == null || im.Db == null)
                continue;
            int type = (int)Db.Num(im.Db, "lightType");
            double radius = Db.Num(im.Db, "lightRadius"), time = Db.Num(im.Db, "lightTime");
            double age = M.Time - im.FireTime;
            double k = type switch { 1 => 1, 3 => age >= 0 && time > 0 && age < time ? 1 - age / time : 0, _ => 0 };
            if (k <= 0 || radius <= 0)
                continue;
            var (x, y, z) = Muzzle();
            set.Add(new PointLight(x, y, z, (float)(Db.Num(im.Db, "lightColor.x") * k), (float)(Db.Num(im.Db, "lightColor.y") * k), (float)(Db.Num(im.Db, "lightColor.z") * k), radius));
        }
    }
}
