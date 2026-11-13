namespace OpenTribes.Client.Scene;

using OpenTribes.Client.Shapes;
using OpenTribes.Game;

/// <summary>An item image mounted on a player.</summary>
public sealed class ImageMount
{
    /// <summary>The image slot.</summary>
    public int Slot;
    /// <summary>The image datablock index.</summary>
    public int Image;
    /// <summary>The image datablock.</summary>
    public ClientDataBlock? Db;
    /// <summary>The image shape.</summary>
    public RenderShape? Shape;
    /// <summary>The image shape instance.</summary>
    public RenderInstance? Inst;
    /// <summary>The state machine of the image.</summary>
    public ImageStateMachine? Sm;
    /// <summary>The scene time of the last Fire entry.</summary>
    public double FireTime = -1000;
    /// <summary>The mount point of the image.</summary>
    public int MountPoint;
    /// <summary>True when the image shows in the first person view.</summary>
    public bool FirstPerson;
    /// <summary>The team of the mount (Player_setImage param_4, 0041a168), -1 for none.</summary>
    public int Team = -1;
}

/// <summary>The visual of a Player ghost: the armor shape with the animation and looks threads, mounted item images, the jet flame, footprints, the death pose and the burst into debris.</summary>
/// <remarks>docs/shapes-render.md 3.4, 3.6 and 5.</remarks>
public sealed class PlayerVisual : ShapeVisual
{
    private RenderThread? mAnim, mLooks;
    private double mVx, mVy, mVz, mUpdateTime;
    private bool mDead, mContact, mJet, mJump, mCrouch;
    private double mSignalUntil, mNextChoice;
    private readonly ImageMount?[] mImages = new ImageMount?[8];
    private RenderShape? mFlameShape;
    private RenderInstance? mFlame;
    private RenderThread? mFlameThread;
    private readonly List<uint> mTriggers = [];
    /// <summary>True once the player burst into debris.</summary>
    public bool Exploded { get; private set; }
    /// <summary>True while the jets fire.</summary>
    public bool Jetting { get; private set; }
    /// <summary>The index of the current animation, -1 for none.</summary>
    public int AnimIndex { get; private set; } = -1;
    /// <summary>The skin name.</summary>
    public string SkinName => Skin;
    /// <summary>The mounted images by slot.</summary>
    public IReadOnlyList<ImageMount?> Images => mImages;

    /// <inheritdoc/>
    public override void Init(SceneModel m, ClientGhost g)
    {
        base.Init(m, g);
        Group = 3;
    }

    private (string Name, int Sound, bool Fwd, int ViewFlags, int Prio)? AnimData(int index)
    {
        if (Block == null)
            return null;
        var v = Db.Get(Block, $"animData[{index}]");
        if (v is not object[] a || a.Length < 5)
            return null;
        return ((string)a[0], (int)Db.ToDouble(a[1]), Db.ToDouble(a[2]) != 0, (int)Db.ToDouble(a[3]), (int)Db.ToDouble(a[4]));
    }

    /// <inheritdoc/>
    public override void OnUpdate(DecodedGhost u, bool isNew)
    {
        base.OnUpdate(u, isNew);
        if (Inst == null || Shape == null)
            return;
        if (mAnim == null)
        {
            mAnim = Inst.AddThread(Shape.FindSequence("root"), 0, 0);
            var looks = Shape.FindSequence("looks");
            if (looks >= 0)
            {
                mLooks = Inst.AddThread(looks, 0.5, -1);
                mLooks.TimeScale = 0;
            }
            var flame = Db.Str(Block, "flameShapeName");
            if (flame.Length > 0)
            {
                mFlameShape = M.Assets.Shape(flame);
                if (mFlameShape != null)
                {
                    mFlame = new RenderInstance(mFlameShape);
                    var amb = mFlameShape.FindSequence("ambient");
                    mFlameThread = mFlame.AddThread(amb >= 0 ? amb : 0, 0, 0);
                }
            }
        }
        bool has(string n) => u.Fields.Any(f => f.Name == n);
        // 2.9 inputs (a): the skin of the owning client, else the team skin.
        var cid = (int)Num("controlClient", Num("ownerClient", -1));
        var cs = cid >= 0 ? M.World.Players.Get(cid)?.Skin ?? "" : "";
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
        if (has("mount") && Ghost.State.TryGetValue("mountGhost", out var mg) && has("mount"))
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
            var a = (int)Num("f98");
            SetAnimation(a);
            // signal animations (38 to 50) play once, then the movement choice takes over again
            if (a >= 38 && Shape != null && Shape.FindSequence(AnimData(a)?.Name ?? "") is int sq && sq >= 0)
                mSignalUntil = M.Time + Shape.Sequences[sq].Duration;
        }
        if (has("dead"))
        {
            var exploded = !has("deathAnim");
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
            foreach (var s in ImageSlot.ParseAll(Ghost.State["images"]))
                ApplyImage(s, isNew);
        else if (u.Fields.Any(f => f.Name == "images"))
        { }
    }

    /// <summary>The ghost index of the vehicle or turret the player sits in, -1 for none.</summary>
    public int MountGhost = -1;
    /// <summary>The view pitch in radians.</summary>
    public double ViewPitch;

    private void Explode()
    {
        if (Exploded || Inst == null)
            return;
        Exploded = true;
        var expl = (int)Db.Num(Block, "explosionId", -1);
        var debris = (int)Db.Num(Block, "debrisId", -1);
        var (cx, cy, cz) = PoseXf().Apply(Shape!.Cx, Shape.Cy, Shape.Cz);
        if (expl >= 0)
            M.SpawnExplosion(expl, cx, cy, cz, 0, 0, 1);
        if (debris >= 0)
            M.Effects.Add(M.NewDebris(Inst, PoseXf(), mVx, mVy, mVz, debris));
        Hidden = true;
    }

    private void SetAnimation(int index)
    {
        if (Inst == null || Shape == null || mAnim == null || index == AnimIndex)
            return;
        var ad = AnimData(index);
        if (ad == null)
            return;
        // docs/audio.md 3.2: a ghost plays the animation's sound (death scream, landing) when the animation starts.
        M.Audio?.AnimationSound(this, ad.Value.Sound);
        var seq = Shape.FindSequence(ad.Value.Name);
        if (seq < 0)
            return;
        AnimIndex = index;
        mAnim.Priority = ad.Value.Prio;
        double dir = ad.Value.Fwd ? 1 : -1;
        mAnim.TimeScale = dir;
        var start = ad.Value.Fwd ? 0 : 0.99;
        if (M.AnimateWithTransitions)
            Inst.StartTransition(mAnim, seq, start, 0.12);
        else
        {
            mAnim.Sequence = seq;
            mAnim.Position = start;
        }
    }

    /// <summary>Picks the animation of a mounted player: the driver pose of the mount for slot 1, else 23.</summary>
    /// <param name="slot1">True when the player sits in slot 1.</param>
    /// <param name="driverPose">The driver pose of the mount.</param>
    /// <returns>The animation index.</returns>
    /// <remarks>player-sim.md 5.4 (first match wins): mounted, jetting or jumping in the air, falling, crouching, else the best of the four ground motion candidates by the velocity along each travel direction (threshold 0.35 m/s). The choice runs every 65 ms.</remarks>
    public static int MountAnimation(bool slot1, int driverPose) => slot1 ? driverPose : 23;

    /// <summary>Chooses the animation of the player from its state.</summary>
    /// <returns>The animation index.</returns>
    public int ChooseAnimation()
    {
        if (MountGhost >= 0 && Num("mountGhost", -1) >= 0)
        {
            // player-sim.md 5.4 rows 3 and 4, Player_pickMoveAnimation 004106fc: mount point 1 takes the vehicle's driverPose (Vehicle_getDriverPose, vtable +0x168), any other point 23
            return MountAnimation(IsMountSlot1(), M.Visuals.TryGetValue(MountGhost, out var mv) && mv is ShapeVisual sv ? sv.DriverPose : 0);
        }
        if (mJet || (mJump && !mContact))
            return 19;
        if (!mContact && mVz < -3)
            return 14;
        double c = Math.Cos(Yaw), s = Math.Sin(Yaw);
        double right = (mVx * c) + (mVy * s), fwd = (-mVx * s) + (mVy * c);
        (int Index, double Dir)[] cand = mCrouch
            ? new[] { (10, fwd), (11, -fwd), (12, -right), (13, right) }
            : [(1, fwd), (2, -fwd), (3, -right), (4, right)];
        var pick = mCrouch ? 8 : 0;
        var best = 0.35;
        foreach (var (idx, d) in cand)
            if (d > best)
            {
                best = d;
                pick = idx;
            }
        return pick;
    }

    /// <summary>Moves the control player with the prediction or the control state instead of its ghost updates (<see cref="ControlSample"/>).</summary>
    /// <param name="c">The sample.</param>
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

    /// <summary>The rotation about x. Only a corpse has it: the ground slope of <c>Player.ClientCorpseStep</c>.</summary>
    public double TiltX;
    /// <summary>The rotation about y. Only a corpse has it.</summary>
    public double TiltY;
    /// <inheritdoc/>
    protected override Xf PoseXf() => Xf.Euler(TiltX, TiltY, Yaw).Then(Xf.Translate(X + mExtrapX, Y + mExtrapY, Z + mExtrapZ));
    private double mExtrapX, mExtrapY, mExtrapZ;

    // 3.4: ground motion animations (indices 1 to 4 and 10 to 13) run at a rate that follows the speed along the animation direction.
    private double SpeedScale()
    {
        if (AnimIndex is not ((>= 1 and <= 4) or (>= 10 and <= 13)))
            return 1;
        var speed = Math.Sqrt((mVx * mVx) + (mVy * mVy));
        var measured = Math.Max(Db.Num(Block, "maxForwardSpeed", 10.9), 1);
        return Math.Clamp(speed / measured, 0.66, 1.5);
    }

    private void ApplyImage(ImageSlot s, bool isNew)
    {
        if (s.Slot is < 0 or >= 8)
            return;
        var im = mImages[s.Slot];
        if (s.Image < 0)
        {
            if (im != null)
                M.Audio?.ImageReset(this, s.Slot);
            mImages[s.Slot] = null;
            return;
        }
        if (im == null || im.Image != s.Image)
        {
            if (im != null)
                M.Audio?.ImageReset(this, s.Slot);
            // The stream carries the ItemData index (client-net.md 3.4); its imageType names the ItemImageData (group 6).
            var item = M.Db.Block(5, s.Image);
            var db = M.Db.Block(6, (int)Db.Num(item, "imageType", -1));
            var name = Db.Str(db, "shapeFile");
            var sh = name.Length > 0 ? M.Assets.Shape(name) : null;
            im = new ImageMount { Slot = s.Slot, Image = s.Image, Db = db, Shape = sh, Team = s.State, MountPoint = (int)Db.Num(db, "mountPoint"), FirstPerson = Db.Flag(db, "firstPerson") };
            if (sh != null)
            {
                im.Inst = new RenderInstance(sh);
                var data = new ImageStateData
                {
                    WeaponType = (int)Db.Num(db, "weaponType", -1),
                    AmmoType = (int)Db.Num(db, "ammoType", -1),
                    ActivateTime = Db.Num(db, "activateTime"),
                    FireTime = Db.Num(db, "fireTime"),
                    ReloadTime = Db.Num(db, "reloadTime"),
                    SpinUpTime = Db.Num(db, "spinUpTime"),
                    SpinDownTime = Db.Num(db, "spinDownTime"),
                };
                var mount = im;
                var slot = s.Slot;
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

    /// <summary>Hands the image state machines of the control player to the prediction steps, or back to the time based update.</summary>
    /// <param name="predicted">True when the prediction steps drive them (<c>ImageStateMachine.Stepped</c>). False with 0 steps hands them back to the time based update.</param>
    /// <param name="steps">The number of prediction steps.</param>
    public void SetImageSteps(bool predicted, int steps)
    {
        foreach (var im in mImages)
            if (im?.Sm is { } sm)
            {
                sm.Stepped = predicted;
                if (predicted && steps > 0)
                    sm.StepMoves(steps);
            }
    }

    /// <inheritdoc/>
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
        var age = Driven ? 0 : Math.Clamp(M.Time - mUpdateTime, 0, 0.3);
        (mExtrapX, mExtrapY, mExtrapZ) = (mVx * age, mVy * age, mVz * age);
        mAnim.TimeScale = (mAnim.TimeScale < 0 ? -1 : 1) * (mDead ? 1 : SpeedScale());
        mTriggers.Clear();
        Inst.Advance(mAnim, dt, mTriggers);
        mLooks?.Position = LooksPosition(ViewPitch);
        foreach (var t in mTriggers)
        {
            ++M.TriggerCount;
            if (mContact)
                ++M.ContactTriggers;
            if (t is 1 or 2 && mContact && !mDead)
                M.AddFootprint(this, t == 1 ? 1 : -1);
            if (t is 1 or 2 && mContact && !mDead && MountGhost < 0)
                M.Audio?.Footstep(this, t == 1, X + mExtrapX, Y + mExtrapY, Z + mExtrapZ);
        }
        Jetting = AnimIndex == 19 && !mDead;
        if (mFlame != null && mFlameThread != null)
            mFlame.Advance(mFlameThread, dt);
        foreach (var im in mImages)
            im?.Sm?.Advance(dt);
    }

    /// <inheritdoc/>
    protected override int DamageLevel => Skins.DamageLevel(Damage, M.Assets.DamageSkinDetail);

    /// <summary>Computes the position of a foot: plus or minus 0.25 along the x axis of the yaw frame of the player (4.6).</summary>
    /// <param name="side">1 for the right foot, -1 for the left foot.</param>
    /// <returns>The foot position.</returns>
    public (double x, double y, double z) Foot(double side)
    {
        // 4.6: +-0.25 along the player's yaw frame x axis
        double cx = Math.Cos(Yaw), cy = Math.Sin(Yaw);
        return (X + mExtrapX + (cx * 0.25 * side), Y + mExtrapY + (cy * 0.25 * side), Z + mExtrapZ);
    }

    /// <summary>The heading of the player.</summary>
    public double YawAngle => Yaw;
    /// <summary>The name of the armor shape.</summary>
    public string ArmorShape => Shape?.Name ?? "";
    /// <summary>The footprint base index of the datablock, -1 for none.</summary>
    public int FootprintBase => (int)Db.Num(Block, "footPrintsIndex", -1);

    /// <summary>Computes the looks thread position of a view pitch (player-sim.md 5.2): 0.5 - pitch * 0.6511 / 2. The pitch range plus or minus 1.5359 maps to plus or minus 0.5.</summary>
    /// <param name="pitch">The view pitch in radians.</param>
    /// <returns>The position, 0 to 1.</returns>
    public static double LooksPosition(double pitch) => Math.Clamp(0.5 - (pitch * 0.6511 * 0.5), 0, 1);

    /// <summary>Gets the first person eye in object space (the <c>dummy eye</c> node, <c>Player_getEyeTransform</c>) with the looks thread at a pitch.</summary>
    /// <param name="pitch">The view pitch in radians.</param>
    /// <param name="x">The eye x.</param>
    /// <param name="y">The eye y.</param>
    /// <param name="z">The eye z.</param>
    /// <returns>False when the shape has no eye node.</returns>
    public bool TryEyeLocal(double pitch, out double x, out double y, out double z)
    {
        x = y = z = 0;
        if (Inst == null || Shape == null || mDead)
            return false;
        var n = Shape.FindNodeBase("dummy eye", 0);
        if (n < 0)
            return false;
        ViewModeSet(true);
        mLooks?.Position = LooksPosition(pitch);
        Inst.Animate(0);
        var e = Inst.NodeWorld[n];
        (x, y, z) = (e.Px, e.Py, e.Pz);
        return true;
    }

    /// <summary>Gets the height of the <c>dummyalways chasecam</c> node (Player +0x778) in the current pose.</summary>
    /// <param name="z">The height.</param>
    /// <returns>False when the shape has no such node.</returns>
    /// <remarks>Player_getCameraTransform (00412324) keeps only the z of the node (x and y are set to 0) for the third person camera.</remarks>
    public bool TryChaseZ(out double z)
    {
        z = 0;
        if (Inst == null || Shape == null || mDead)
            return false;
        var n = Shape.FindNodeBase("dummyalways chasecam", 0);
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
        var ad = AnimData(AnimIndex);
        var on = ad == null || (ad.Value.ViewFlags & (firstPerson ? 1 : control ? 2 : 4)) != 0;
        Inst?.ViewMode = on ? (firstPerson ? 2 : 1) : 0;
        mLooks?.Priority = on ? -1 : 5000;
        return on;
    }

    /// <summary>Builds the transform of a mounted image.</summary>
    /// <param name="im">The image.</param>
    /// <param name="detail">The detail index.</param>
    /// <param name="body">The transform of the body.</param>
    /// <param name="pitch">The pitch for the pitch only node rotation of Player_getImageMountTransform (3.6 step 2) for mount points 0 and 1, or null.</param>
    /// <param name="pullback">Player +0xfd0 (WeaponPullback). The mount transform adds it to the offset y of a mount point 0 image only (Player_getImageMountTransform 00419ba0, the add reads image +0x38 == 0).</param>
    /// <returns>The transform.</returns>
    public Xf ImageTransform(ImageMount im, int detail, Xf body, double? pitch = null, double pullback = 0)
    {
        var mount = MountNode(im, detail, pitch, out _);
        var local = ImageLocal(Db.Num(im.Db, "mountOffset.x"), Db.Num(im.Db, "mountOffset.y"), Db.Num(im.Db, "mountOffset.z"),
                              Db.Num(im.Db, "mountRotation.x"), Db.Num(im.Db, "mountRotation.y"), Db.Num(im.Db, "mountRotation.z"),
                              im.MountPoint, pullback);
        return local.Then(mount).Then(body);
    }

    /// <summary>Builds the own transform of an image: the Euler rotation at the offset, with the pullback added to the offset y for mount point 0 (Player_getImageMountTransform 00419ba0).</summary>
    /// <param name="ox">The offset x.</param>
    /// <param name="oy">The offset y.</param>
    /// <param name="oz">The offset z.</param>
    /// <param name="rx">The rotation x.</param>
    /// <param name="ry">The rotation y.</param>
    /// <param name="rz">The rotation z.</param>
    /// <param name="mountPoint">The mount point.</param>
    /// <param name="pullback">The weapon pullback.</param>
    /// <returns>The transform.</returns>
    public static Xf ImageLocal(double ox, double oy, double oz, double rx, double ry, double rz, int mountPoint, double pullback)
    {
        Xf local = Xf.Euler(rx, ry, rz);
        local.Px = ox;
        local.Py = mountPoint == 0 ? pullback + oy : oy;
        local.Pz = oz;
        return local;
    }

    private Xf MountNode(ImageMount im, int detail, double? pitch, out bool found)
    {
        var sh = Inst!.Shape;
        string[] names = ["dummy hand", "dummy unused", "dummy midback", "dummy lowback"];
        var node = im.MountPoint is >= 0 and < 4 ? sh.FindNodeBase(names[im.MountPoint], detail) : -1;
        found = node >= 0;
        var mount = node >= 0 ? Inst.NodeWorld[node] : Xf.Identity;
        if (node >= 0 && pitch is double pv && !mDead && im.MountPoint is 0 or 1)
        {
            Xf r = Xf.Euler(pv, 0, 0);
            (r.Px, r.Py, r.Pz) = (mount.Px, mount.Py, mount.Pz);
            mount = r;
        }
        return mount;
    }

    /// <summary>Computes Player +0xfd0, the weapon pullback.</summary>
    /// <param name="detail">The detail index.</param>
    /// <param name="body">The transform of the body.</param>
    /// <param name="pitch">The view pitch, or null.</param>
    /// <returns>The pullback. Zero without a mount point 0 image, a mount node, or a hit.</returns>
    /// <remarks>Follows Player_updateWeaponPullback (004198d4, called from Player_updateMove 00415304, which Player_clientUpdate 00414c38 runs for ghosts and Player_serverTick on the server): a container ray (mask 0xbfffffff, the player itself excluded) from the origin of the mount transform of the first mount point 0 image to its muzzle transform. On a hit the distance along the y axis of the mount between the hit and the muzzle minus 0.2. The ray is the container ray of the live game (<c>SceneModel.PullbackRay</c>).</remarks>
    public double WeaponPullback(int detail, Xf body, double? pitch)
    {
        if (Ray == null || Inst == null)
            return 0;
        foreach (var im in mImages)
        {
            if (im?.Inst == null || im.MountPoint != 0)
                continue;
            var mount = MountNode(im, detail, pitch, out var found);
            if (!found)
                return 0;
            var t = ImageTransform(im, detail, body, pitch);
            // Player_getMuzzleTransform 00419d88: the "dummyalways muzzle" node of the image shape times the mount, else the mount.
            var mn = im.Shape!.FindNodeBase("dummyalways muzzle", 0);
            var mz = mn >= 0 ? im.Inst.NodeWorld[mn].Then(t) : t;
            var (sx, sy, sz) = t.Apply(0, 0, 0);
            var (ex, ey, ez) = mz.Apply(0, 0, 0);
            var (fx, fy, fz) = t.Rotate(0, 1, 0);
            if (Ray(Index, (sx, sy, sz), (ex, ey, ez)) is not var (hx, hy, hz))
                return 0;
            return PullbackFrom((sx, sy, sz), (ex, ey, ez), (hx, hy, hz), (fx, fy, fz));
        }
        return 0;
    }

    // The ray of the live game: ghost index, start, end to the hit point (null on a miss).
    private Func<int, (double, double, double), (double, double, double), (double, double, double)?>? Ray => M.PullbackRay;

    /// <summary>Computes the pullback from a ray result (004198d4): (hit - start) . axis - (end - start) . axis - 0.2, with the axis the y axis of the image in world space.</summary>
    /// <param name="start">The ray start.</param>
    /// <param name="end">The ray end, the muzzle.</param>
    /// <param name="hit">The hit point.</param>
    /// <param name="axis">The y axis of the image.</param>
    /// <returns>The pullback.</returns>
    public static double PullbackFrom((double x, double y, double z) start, (double x, double y, double z) end,
                                      (double x, double y, double z) hit, (double x, double y, double z) axis)
    {
        var d1 = ((hit.x - start.x) * axis.x) + ((hit.y - start.y) * axis.y) + ((hit.z - start.z) * axis.z);
        var d2 = ((end.x - start.x) * axis.x) + ((end.y - start.y) * axis.y) + ((end.z - start.z) * axis.z);
        return d1 - d2 - 0.2;
    }

    /// <summary>Gets the <c>dummy midback</c> node in world space (Player_getMountPointTransform(2), 004aca84).</summary>
    /// <returns>The position, or null without the node or a position.</returns>
    public (double x, double y, double z)? MidBack()
    {
        if (Inst == null || Shape == null || !HavePos)
            return null;
        var n = Shape.FindNodeBase("dummy midback", 0);
        if (n < 0)
            return null;
        return Inst.NodeWorld[n].Then(PoseXf()).Apply(0, 0, 0);
    }

    /// <summary>The muzzle of the first mounted weapon in world space (projectile start of beams), or the chest as a fallback.</summary>
    public override (double x, double y, double z)? MuzzlePoint => HavePos ? Muzzle() : null;

    /// <summary>Gets the muzzle of the first mounted weapon in world space, or the chest as a fallback.</summary>
    /// <returns>The position.</returns>
    public (double x, double y, double z) Muzzle()
    {
        var body = mFpBody ?? PoseXf();
        foreach (var im in mImages)
        {
            if (im?.Inst == null || im.MountPoint != 0)
                continue;
            // First person draws detail 0 with the camera pitch (Build); the muzzle is taken from that same pose.
            var d = mFpBody != null ? 0 : Math.Max(M.Renderer.LastDetail, 0);
            var dd = Math.Min(d, Math.Max(Inst!.Shape.Details.Length - 1, 0));
            var pt = mFpBody != null ? mFpPitch : ViewModeSet(false) ? ViewPitch : null;
            var t = ImageTransform(im, dd, body, pt, WeaponPullback(dd, body, pt));
            var mn = im.Shape!.FindNodeBase("dummyalways muzzle", 0);
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
    private bool IsMountSlot1() => Ghost.State.TryGetValue("mount", out var ms) && ms is string s && s.EndsWith("/1", StringComparison.Ordinal);

    private bool PilotHidden(FrameContext ctx)
    {
        if (MountGhost < 0 || !IsMountSlot1())
            return false;
        if (ctx.FirstPersonOfControl && ctx.ControlGhost == MountGhost)
            return true;
        return M.Visuals.TryGetValue(MountGhost, out var mv) && mv is ShapeVisual sv && !sv.DriverVisible;
    }

    // First person frame of the control player: the eye node (looks thread at the camera pitch, first person view mode) lands on the
    // camera and the weapon hangs on the hand node of the same pose: the body is the yaw turned transform that puts the eye there
    // (5.2, `Player_getEyeTransform`), and the mount pitch is the camera pitch (Player +0xfc4 of the control object).
    private (Xf body, double? pitch) FirstPersonFrame(FrameContext ctx, Xf body, double? pitchOnly)
    {
        TryEyeLocal(ctx.Camera.Pitch, out var ex, out var ey, out var ez);
        body = EyeAlignedBody(ctx.Camera.X, ctx.Camera.Y, ctx.Camera.Z, ctx.Camera.Yaw, ex, ey, ez);
        if (pitchOnly != null)
            pitchOnly = ctx.Camera.Pitch;
        return (body, pitchOnly);
    }

    /// <summary>Builds the body transform, turned by the yaw, that puts the object space eye on the camera position.</summary>
    /// <param name="cx">The camera x.</param>
    /// <param name="cy">The camera y.</param>
    /// <param name="cz">The camera z.</param>
    /// <param name="yawRad">The yaw in radians.</param>
    /// <param name="ex">The eye x in object space.</param>
    /// <param name="ey">The eye y in object space.</param>
    /// <param name="ez">The eye z in object space.</param>
    /// <returns>The body transform.</returns>
    public static Xf EyeAlignedBody(double cx, double cy, double cz, double yawRad, double ex, double ey, double ez)
    {
        Xf yaw = Xf.Euler(0, 0, yawRad);
        var (rx, ry, rz) = yaw.Apply(ex, ey, ez);
        return yaw.Then(Xf.Translate(cx - rx, cy - ry, cz - rz));
    }

    /// <summary>Prepares the first person pose before any visual builds.</summary>
    /// <param name="ctx">The frame context.</param>
    /// <remarks>A beam that starts at the muzzle of this player then sees the first person pose of this frame. The stock beam reads the muzzle transform of the shooter every frame (LaserProjectile 004632a8 to Player_getMuzzleTransform 00419d88), whose mount transform 00419ba0 uses the own pitch of the control object (Player +0xfc4) and its transform, here the camera's.</remarks>
    public void PrepareFrame(FrameContext ctx)
    {
        mFpBody = null;
        if (Inst == null || Shape == null || !HavePos || Hidden || !(ctx.FirstPersonOfControl && ctx.ControlGhost == Index))
            return;
        var body = PoseXf();
        double? pitch = ViewModeSet(true, true) ? ViewPitch : null;
        (body, pitch) = FirstPersonFrame(ctx, body, pitch);
        mFpBody = body;
        mFpPitch = pitch;
        Inst.Animate(0);
    }

    private Xf? mFpBody;
    private double? mFpPitch;

    /// <inheritdoc/>
    public override void Build(FrameContext ctx)
    {
        if (Inst == null || Shape == null || !HavePos || Hidden)
            return;
        if (PilotHidden(ctx))
            return;
        var firstPerson = ctx.FirstPersonOfControl && ctx.ControlGhost == Index;
        var body = PoseXf();
        double? pitchOnly = ViewModeSet(firstPerson, ctx.ControlGhost == Index) ? ViewPitch : null;
        if (firstPerson)
            (body, pitchOnly) = FirstPersonFrame(ctx, body, pitchOnly);
        var fade = FadeAmount();
        var detail = -1;
        if (!firstPerson)
        {
            var d = ctx.Draw(body, Shape.Name);
            d.Light = ProbedLight(ctx);
            d.Skin = Skin;
            d.DamageLevel = DamageLevel;
            d.Fade = fade;
            d.DetailScale = M.PlayerShapeDetail;
            d.NoRootTransform = true;
            var img = ctx.Renderer.Build(Inst, ctx.Camera, d);
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
        double? pulled = null;
        foreach (var im in mImages)
        {
            if (im?.Inst == null || im.Shape == null)
                continue;
            if (firstPerson && !im.FirstPerson)
                continue;
            Inst.Animate(Math.Min(detail, Shape.Details.Length - 1));
            var pullback = pulled ??= WeaponPullback(Math.Min(detail, Math.Max(Shape.Details.Length - 1, 0)), body, pitchOnly);
            // The renderer puts the image's root transform (node 0 default) in front of the mount transform (3.6).
            var xf = ImageTransform(im, Math.Min(detail, Math.Max(Shape.Details.Length - 1, 0)), body, pitchOnly, pullback);
            var d = ctx.Draw(xf, im.Shape.Name);
            d.Skin = Skins.ImageSkin(im.Team, M.TeamSkin(im.Team));
            d.Fade = fade;
            d.ForceTranslucent = firstPerson;
            if (firstPerson)
                d.FixedSortKey = 0.999;
            var img = ctx.Renderer.Build(im.Inst, ctx.Camera, d);
            if (img != null)
                ctx.Lists.Add(img);
        }
        if (Jetting && mFlame != null && !firstPerson)
        {
            Inst.Animate(Math.Min(detail, Shape.Details.Length - 1));
            var n = Shape.FindNodeBase("dummy lowback", Math.Min(detail, Math.Max(Shape.Details.Length - 1, 0)));
            var mount = n >= 0 ? Inst.NodeWorld[n] : Xf.Identity;
            var d = ctx.Draw(mount.Then(body), mFlameShape!.Name);
            d.ForceTranslucent = true;
            d.SortBias = 0.01;
            var img = ctx.Renderer.Build(mFlame, ctx.Camera, d);
            if (img != null)
                ctx.Lists.Add(img);
        }
    }

    /// <summary>Adds the weapon fire light of the player to a light set (4.9): lightType 3 decays linearly over lightTime.</summary>
    /// <param name="set">The light set.</param>
    public void CollectLights(LightSet set)
    {
        foreach (var im in mImages)
        {
            if (im == null || im.Db == null)
                continue;
            var type = (int)Db.Num(im.Db, "lightType");
            double radius = Db.Num(im.Db, "lightRadius"), time = Db.Num(im.Db, "lightTime");
            var age = M.Time - im.FireTime;
            var k = type switch { 1 => 1, 3 => age >= 0 && time > 0 && age < time ? 1 - (age / time) : 0, _ => 0 };
            if (k <= 0 || radius <= 0)
                continue;
            var (x, y, z) = Muzzle();
            set.Add(new PointLight(x, y, z, (float)(Db.Num(im.Db, "lightColor.x") * k), (float)(Db.Num(im.Db, "lightColor.y") * k), (float)(Db.Num(im.Db, "lightColor.z") * k), radius));
        }
    }
}
