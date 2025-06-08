// Port of src/game/player.hpp and src/game/player.cpp: the Player (docs/player-sim.md, docs/collision.md 4,
// docs/player-control.md): creation and datablock binding, move credit, the 32 ms step (jump, jet, energy, ground
// control, stepped move with the Player collision callback), the control state. The collision event dispatch of
// player.cpp (GameState::postCollisionEvent, dispatchCollisionEvent) is in Displace.cs. The Chop class of the file is
// X87.Chop; world::x87::f32 is X87.F32, the long double arithmetic is the Fpu double operations in C++ order.
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

public sealed partial class Player : GameBase
{
    public const int kItemTypes = 200; // inventory entries (+0x1038)
    public const int kImageSlots = 8;
    public const uint kLiveMask = 0x74e00086u; // 0x0060fb5c
    public const uint kDeadMask = 0x20c00086u; // 0x0060fb64
    public const uint kCorpseType = 0x40000000u;

    private const float kDt = 0.032f;          // time slice 0x3d03126f
    private const float kSeparation = 0.0001f; // 0x0060ff58
    private const uint kGravityForce = 1, kJetForce = 0x100;
    private const uint kNonBlockingTypes = 0x44200000u; // 0x0060ff64: Item, Mine, corpse
    private const uint kStepUpTypes = 0x00400006u;      // 0x0060ff68: terrain, interior, moveable

    // src/game/player.hpp Player::Data: PlayerData values read by the step (the f32 the script path stored, 2.2).
    public sealed class Data
    {
        public float MaxForwardSpeed, MaxBackwardSpeed, MaxSideSpeed;
        public float Mass = 1, Drag = 1, HorzVelClamp, JetForce;
        public float GroundForce = 20, GroundTraction = 1, JumpImpulse = 100, JumpSurfaceMinDot = 0.2f;
        public float MaxJetSideForceFactor = 1, MaxJetForwardVelocity = 20, MinJetEnergy = 1, JetEnergyDrain = 1;
        public float BoxWidth, BoxDepth, BoxNormalHeight, BoxCrouchHeight;
        public float MinDamageSpeed = 13, DamageScale = 0.00899999f;
        public float MaxEnergy, MaxDamage = 1;
        public bool CanCrouch;
        public int[] AnimPriority = new int[51];
        public int[] AnimDirection = new int[51];
        public float[] AnimDuration = new float[51];
        public bool[] AnimCyclic = new bool[51];  // sequence +4: the position wraps instead of stopping
        public int[] AnimSequence = new int[51];  // sequence handle in the armor shape (+0x88c)
        public int[] AnimViewFlags = new int[51]; // bit 0 first person

        public Data Clone()
        {
            var d = (Data)MemberwiseClone();
            d.AnimPriority = (int[])AnimPriority.Clone();
            d.AnimDirection = (int[])AnimDirection.Clone();
            d.AnimDuration = (float[])AnimDuration.Clone();
            d.AnimCyclic = (bool[])AnimCyclic.Clone();
            d.AnimSequence = (int[])AnimSequence.Clone();
            d.AnimViewFlags = (int[])AnimViewFlags.Clone();
            return d;
        }
    }

    // src/game/player.hpp Player::Force.
    public sealed class Force
    {
        public uint Type;  // 1 gravity, 0x100 jet
        public bool Acceleration;
        public float[] V = new float[3]; // stored acceleration (non-acceleration entries times invMass)
    }

    // src/game/player.hpp Player::ImageState: image state names, indexed by state (table 0x0060ffec).
    public const int kReset = 0, kFire = 1, kNoAmmo = 2, kIdle = 3, kReady = 4, kReload = 5, kSpinUp = 6, kSpinDown = 7,
                     kActivate = 8, kDeactivate = 9;

    // src/game/player.hpp Player::ImageInfo: ItemImageData values the state machine reads (docs/datablocks.md 5.15),
    // cached when the image is set.
    public sealed class ImageInfo
    {
        public int WeaponType = -1; // +0x54
        public int AmmoType = -1;   // +0x58 ItemData index
        public float ActivateTime = 0.5f, FireTime = 0.2f, ReloadTime = 1.0f; // +0x5c, +0x60, +0x64
        public float SpinUpTime = 2.0f, SpinDownTime = 2.0f;                 // +0x68, +0x6c
        public int ProjGroup = -1, ProjIndex = -1;                           // +0x70 projectileType
        public float MinEnergy = 2.0f, MaxEnergy = 10.0f;                    // +0x78, +0x7c
        public bool AccuFire;                                                // +0xb5
        public int MountPoint = 2;                                           // +0x38
        public readonly float[] MountOffset = new float[3], MountRotation = new float[3]; // +0x3c, +0x48
        public string? ShapeFile;                                            // +0xb8 image shape
        public string? Name;                                                 // ItemImageData block name

        // `info = ImageInfo{}` in place.
        public void Reset()
        {
            WeaponType = -1;
            AmmoType = -1;
            ActivateTime = 0.5f;
            FireTime = 0.2f;
            ReloadTime = 1.0f;
            SpinUpTime = 2.0f;
            SpinDownTime = 2.0f;
            ProjGroup = ProjIndex = -1;
            MinEnergy = 2.0f;
            MaxEnergy = 10.0f;
            AccuFire = false;
            MountPoint = 2;
            Array.Clear(MountOffset);
            Array.Clear(MountRotation);
            ShapeFile = null;
            Name = null;
        }
    }

    // src/game/player.hpp Player::ImageSlot. A class reset in place (`images[slot] = ImageSlot{}`), so that a reference
    // held across a reset sees the reset slot as the C++ reference does.
    public sealed class ImageSlot
    {
        public int State;          // +0x00
        public int Item = -1;      // +0x04 ItemData index
        public int Image = -1;     // +0x08 ItemImageData index
        public int Team = -1;      // +0x0c
        public int NextItem = -2;  // +0x10
        public int NextTeam = -1;  // +0x14
        public float Timer;        // +0x18
        public int FireCount;      // +0x1c
        public bool Triggered, Ammo;
        public int Projectile;     // +0x28: object id of a live sustained projectile
        public readonly ImageInfo Info = new();

        public void Reset()
        {
            State = 0;
            Item = -1;
            Image = -1;
            Team = -1;
            NextItem = -2;
            NextTeam = -1;
            Timer = 0;
            FireCount = 0;
            Triggered = Ammo = false;
            Projectile = 0;
            Info.Reset();
        }
    }

    // src/game/player.hpp Player::ShapePose: shape pose of the last Player_updateAnimation (docs/player-sim.md 5.5): the
    // thread state behind the node entries until the next animate.
    public sealed class ShapePose
    {
        public int AnimIndex = -1;    // animation of the animation thread (-1: sequence 0)
        public float AnimPosition;
        public bool CrouchLooks;      // look thread on `crouch looks`
        public bool LookSet;          // false until the first update: the look thread is on sequence 0
        public float LookPosition;    // before the sequence normalises it
        public bool FirstPerson = true; // instance +0x9e = 2, look thread priority -1
    }

    // src/game/player.hpp Player::Contact.
    private struct Contact
    {
        public Float3 N;
        public float UpDot;
        public float StepHeight;
        public CollisionSurface Surface;
        public object? Object;
        public uint Type;
    }

    public bool Dead;             // +0x1019
    public bool BlownUp;          // +0xf94
    public float LastDamage;      // value of the last applyDamage call
    public float Pullback;        // +0xfd0
    public bool RaySkip;          // +0x7f9 (image +0xd): set by Player_kill (0x004111e8), never cleared
    public readonly ShapePose Pose = new();
    public readonly int[] Counts = new int[kItemTypes];
    public readonly int[] SlotOf = new int[kItemTypes];
    public readonly ImageSlot[] Images = new ImageSlot[kImageSlots];

    // State (offsets of the original object).
    public Data PData = new();
    public float Pitch;                   // +0xfc4
    public float Mass = 9, InvMass;       // +0x170, +0x174
    public bool ContactFlag;              // +0x1f4
    public bool RestFlag = true;          // +0x164 bit 0
    public float Traction;                // +0xfcc
    public readonly float[] JumpNormal = { 0, 0, 1 }; // +0xfb0
    public int TicksSinceJumpContact = 100; // +0xfac
    public int TicksSinceContact;         // +0xfc0
    public bool Jetting;                  // +0xfe4
    public uint LastJetTime;              // +0xfe8
    public bool Falling;                  // +0xfec
    public bool PdaFlag;                  // +0xfed
    public bool Crouching;                // +0xfee
    public float Flash;                   // +0xfbc
    public int TicksWithoutMove;          // +0xf8c
    public int Credit;                    // +0xf90
    public Move LastMove = new();         // +0xff0
    public bool AiControlled;             // +0x101b
    public bool LocallyControlled;        // +0x1018: the client's own control object (client-net.md 4.4, vtable +0x144)
    public bool InMissionArea = true;     // +0x101a
    public bool FreeLookCopy = true;      // +0x7dc: $pref::freeLook (0060fb50) as of onAdd and the last tick
    public int PendingAnim;               // +0xf98
    public int CurrentAnim = -1;          // +0xf9c
    public float AnimPosition;            // animation thread position
    public float DetectHazeFactor = 0.0287f, DetectMinVisibility = 1.0f; // +0xfd8, +0xfdc
    public float SensorSuppression;       // +0xfe0
    public readonly float[] BoxMin = new float[3], BoxMax = new float[3]; // movement box (+0x1d4, +0x1e0)
    public float BoxHeight;               // box B max z (+0x864): follows crouch
    public float Radius;                  // +0x874
    public uint ObjectTypeBits = ObjectType.kPlayerType; // +0x48
    public uint CollisionMask = kLiveMask; // +0x1ec
    public float DeathTime;               // +0x1024

    // Mounting (docs/vehicle-sim.md 2, docs/player-sim.md 2.7), player_mount.cpp.
    public int MountId;    // +0x1028: id of the mount object, 0 none
    public int MountPoint; // +0x102c

    private readonly GameState mGame;
    private readonly List<Force> mForces = new();
    private readonly SteppedMove mMove = new();
    private object? mTerrainContainer;  // terrain entry the gravity came from
    private float mContainerDrag;       // SimMovement +0x124, set on container entry
    private object? mParent;            // container object holding the player
    private bool mInContainer;
    private bool mAdded;

    private static ClassContext OwnContext(GameState game) => new() { Game = game, Resources = game.Resources };

    // src/game/player.cpp Player::Player.
    public Player(GameState game) : base("Player", Kind.ShapeBase, 3, Array.Empty<PersistDef>(), OwnContext(game))
    {
        mGame = game;
        for (int i = 0; i < kImageSlots; ++i)
            Images[i] = new ImageSlot();
        for (int i = 0; i < kItemTypes; ++i)
            SlotOf[i] = -1;
        DamageState = kEnabled; // ShapeBase getDamageState: always Enabled
        mMove.OnCollision = info => ProcessCollision(info);
        mMove.OnTouch = owner => PostCollision(owner as SimObject, this);
    }

    public GameState Game() => mGame;

    // src/game/player.cpp Player::from: the Player behind a sim object, or null.
    public static Player? From(SimObject? obj) => obj as Player;

    public IReadOnlyList<Force> Forces() => mForces;

    // src/game/player.cpp fieldFloat.
    private static float FieldFloat(DataBlockManager.Block b, string name, float def)
    {
        DataBlockManager.Field? f = b.Find(name);
        if (f == null || f.Elems.Count == 0)
            return def;
        if (f.Elems[0].Set)
            return f.Elems[0].F[0];
        float v = def;
        if (f.Def.DefaultText.Length != 0)
            v = DataBlockManager.CtorFloat(f.Def.DefaultText);
        return v;
    }

    // src/game/player.cpp Player::block.
    private DataBlockManager.Block? Block()
    {
        if (mGame.Sim == null || DataBlockName == null)
            return null;
        DataBlockManager.Block? b = mGame.Sim.DataBlocks().Find(DataBlockName);
        return b != null && b.Group == 3 ? b : null;
    }

    // ---- creation (docs/player-sim.md 1.3, 1.4) ----

    // src/game/player.cpp Player::onAdd: 0x0040fb9c.
    public override bool OnAdd()
    {
        // GameBase_onAdd (0x00448108): the player has no group, so the team stays -1; the datablock resolves
        // (Player_onNewDataBlock, then the script onInit), then onAdd.
        if (DataBlockName == null || !mGame.ResolveDataBlock(this) || !BindDataBlock())
            return false;
        mAdded = true;
        mGame.ScriptCallback(this, "onInit", Array.Empty<string>());
        mGame.ScriptCallback(this, "onAdd", Array.Empty<string>());
        mGame.JoinSet(this, 719);
        CollisionMask = kLiveMask;
        float[] p = { Transform.P[0], Transform.P[1], Transform.P[2] };
        SetPosition(p);
        FreeLookCopy = FreeLookPref();
        return true;
    }

    // Global 0060fb50 ($pref::freeLook, default TRUE; an unset variable keeps the default).
    private bool FreeLookPref()
    {
        if (mGame.Console == null)
            return true;
        string v = mGame.Console.GetVariable("$pref::freeLook", out bool found);
        return !found || Numbers.ParseBool(v);
    }

    // src/game/player.cpp Player::onRemove: 0x0040fb30.
    public override void OnRemove()
    {
        mGame.SensorJam(this, false); // Player_onRemove 0x0040fb30 unlinks, the radius stays (docs/static-sim.md 3.8)
        for (int slot = 0; slot < kImageSlots; ++slot)
            ResetImage(slot);
        // GameBase_onRemove (0x00448180): the script onRemove, then the container.
        if (mAdded && DataBlockName != null)
            mGame.ScriptCallback(this, "onRemove", Array.Empty<string>());
        if (mInContainer)
            mGame.World.Remove(this);
        mInContainer = false;
        mAdded = false;
        // SimMovement dtor (0x0050471e): a deleted Player leaves the carry list of the Moveable it stood on. The lists
        // hold ids here, and the next Player gets the freed id.
        DetachAll();
        mGame.DropTriggerOccupant(this);
    }

    // src/game/player.cpp Player::readLiveData: PlayerData values the step and the contact response read through the
    // datablock pointer (0x00415304, 0x00416e4c): a redeclaration of the block changes a bound Player at its next step
    // (docs/player-sim.md 9.5 `clamp`). Mass, drag, the box and the energy and damage limits are copied into the object
    // at bind (1.4) and stay.
    public static void ReadLiveData(DataBlockManager.Block b, Data d)
    {
        d.MaxForwardSpeed = FieldFloat(b, "maxForwardSpeed", 0);
        d.MaxBackwardSpeed = FieldFloat(b, "maxBackwardSpeed", 0);
        d.MaxSideSpeed = FieldFloat(b, "maxSideSpeed", 0);
        d.HorzVelClamp = FieldFloat(b, "horzVelClamp", 0);
        d.JetForce = FieldFloat(b, "jetForce", 0);
        d.GroundForce = FieldFloat(b, "groundForce", 20);
        d.GroundTraction = FieldFloat(b, "groundTraction", 1);
        d.JumpImpulse = FieldFloat(b, "jumpImpulse", 100);
        d.JumpSurfaceMinDot = FieldFloat(b, "jumpSurfaceMinDot", 0.2f);
        d.MaxJetSideForceFactor = FieldFloat(b, "maxJetSideForceFactor", 1);
        d.MaxJetForwardVelocity = FieldFloat(b, "maxJetForwardVelocity", 20);
        d.MinJetEnergy = FieldFloat(b, "minJetEnergy", 1);
        d.JetEnergyDrain = FieldFloat(b, "jetEnergyDrain", 1);
        d.MinDamageSpeed = FieldFloat(b, "minDamageSpeed", 13);
        d.DamageScale = FieldFloat(b, "damageScale", 0.00899999f);
        if (b.Find("canCrouch") is DataBlockManager.Field f && f.Elems.Count != 0)
            d.CanCrouch = f.Elems[0].I != 0;
    }

    // src/game/player.cpp Player::bindDataBlock: datablock (re)bind (0x0040fcdc); false when the name is no PlayerData.
    public bool BindDataBlock()
    {
        DataBlockManager.Block? b = Block();
        if (b == null)
            return false;
        var d = new Data();
        ReadLiveData(b, d);
        d.Mass = FieldFloat(b, "mass", 0);
        d.Drag = FieldFloat(b, "drag", 0);
        d.BoxWidth = FieldFloat(b, "boxWidth", 0);
        d.BoxDepth = FieldFloat(b, "boxDepth", 0);
        d.BoxNormalHeight = FieldFloat(b, "boxNormalHeight", 0);
        d.BoxCrouchHeight = FieldFloat(b, "boxCrouchHeight", 0);
        d.MaxEnergy = FieldFloat(b, "maxEnergy", 0);
        d.MaxDamage = FieldFloat(b, "maxDamage", 1);
        ShapeAnim? shapeAnim = null;
        if (mGame.Resources != null && mGame.Sim!.DataBlocks().GetField(b, "shapeFile", "", out string shape) && shape.Length != 0)
            shapeAnim = Shape.GetShapeAnim(mGame.Resources, shape);
        if (b.Find("animData") is DataBlockManager.Field af)
            for (int i = 0; i < af.Elems.Count && i < 51; ++i)
            {
                d.AnimDirection[i] = af.Elems[i].I2;
                d.AnimPriority[i] = af.Elems[i].I4;
                // A missing sequence gives handle 0 (1.4 step 7).
                int seq = shapeAnim != null && af.Elems[i].Str != null ? shapeAnim.FindSequence(af.Elems[i].Str!) : -1;
                if (seq < 0)
                    seq = 0;
                d.AnimDuration[i] = shapeAnim != null && seq < shapeAnim.Sequences.Count ? shapeAnim.Sequences[seq].Duration : 0;
                d.AnimCyclic[i] = shapeAnim != null && seq < shapeAnim.Sequences.Count && shapeAnim.Sequences[seq].Cyclic;
                d.AnimSequence[i] = seq;
                d.AnimViewFlags[i] = af.Elems[i].I3;
            }
        PData = d;
        using var chop = X87.Chop();
        // ShapeBase_onNewDataBlock (0x0044b274): energy = maxEnergy; a dead level drops to 0.9.
        MaxEnergy = d.MaxEnergy;
        Energy = d.MaxEnergy;
        MaxDamage = d.MaxDamage;
        if (Damage >= d.MaxDamage)
            Damage = X87.F32(Mul(d.MaxDamage, 0.9));
        // Collision box (step 11) and the sphere radius.
        BoxMin[0] = -d.BoxWidth;
        BoxMin[1] = -d.BoxDepth;
        BoxMin[2] = 0;
        BoxMax[0] = d.BoxWidth;
        BoxMax[1] = d.BoxDepth;
        BoxMax[2] = d.BoxNormalHeight;
        Radius = X87.F32(Mul(Sub(BoxMax[2], BoxMin[2]), 0.5f));
        UpdateBoxHeight();
        RecomputeMass();
        if (mGame.World.Find(this) is Container.Entry e)
        {
            Span<float> box = stackalloc float[6];
            WorldBox(box);
            mGame.World.SetBox(this, box);
            e.Type = ObjectTypeBits;
        }
        else
        {
            var entry = new Container.Entry(this, ObjectTypeBits, default, false, null);
            WorldBox(entry.Box);
            // Line image (collision.md 9, 0x0043599c): the segment goes into the space of the image transform (the object
            // transform, yaw included) and meets box B there (m_Box3F_collideLine). The position is (end - start) * t +
            // start on the world segment, one f32 store per component; the normal is (0, 0, 1).
            entry.Cast = (Vec3 s, Vec3 en, ref Vec3 hit, ref Vec3 n) =>
            {
                if (RaySkip)
                    return false; // image byte +0xd: rays skip a player that was killed
                using var c = X87.Chop();
                Span<float> a = stackalloc float[3] { s.X, s.Y, s.Z };
                Span<float> bb = stackalloc float[3] { en.X, en.Y, en.Z };
                var toWorld = new TMat3();
                toWorld.Flags = Transform.Flags;
                toWorld.M = Transform.M;
                toWorld.P = Transform.P;
                Shape.TmatInverse(toWorld, out TMat3 toLocal);
                Span<float> la = stackalloc float[3], lb = stackalloc float[3];
                X87.MulPoint(a, toLocal.M, toLocal.P, la);
                X87.MulPoint(bb, toLocal.M, toLocal.P, lb);
                Span<float> mx = stackalloc float[3] { BoxMax[0], BoxMax[1], BoxHeight };
                float t = 0.0f;
                if (!Shape.BoxCollideLine(la, lb, BoxMin, mx, ref t))
                    return false;
                float At(int i, ReadOnlySpan<float> aa, ReadOnlySpan<float> b2) => X87.F32(Add(Mul(Sub(b2[i], aa[i]), t), aa[i]));
                hit = new Vec3(At(0, a, bb), At(1, a, bb), At(2, a, bb));
                n = new Vec3(0, 0, 1);
                return true;
            };
            mGame.World.Add(entry);
            mInContainer = true;
        }
        UpdateContainer();
        SetMaskBits(0x100);
        return true;
    }

    // src/game/player.cpp Player::setArmor: Player::setArmor (0x00420798).
    public bool SetArmor(string name)
    {
        // GameBase_setDataBlockName (0x00447d20): a failed resolve restores the old block.
        string? old = DataBlockName;
        DataBlockName = StringTable.Global().Intern(name);
        if (!mAdded)
            return true;
        if (mGame.ResolveDataBlock(this) && BindDataBlock())
        {
            mGame.ScriptCallback(this, "onInit", Array.Empty<string>());
            SetMaskBits(0x04);
            return true;
        }
        DataBlockName = old;
        if (mGame.ResolveDataBlock(this) && BindDataBlock())
            mGame.ScriptCallback(this, "onInit", Array.Empty<string>());
        return false;
    }

    // src/game/player.cpp Player::recomputeMass: 0x0041a0f8: PlayerData mass plus the masses of the mounted images,
    // summed in slot order in an f32; 0x00504758 rescales the non-acceleration forces.
    private void RecomputeMass()
    {
        using var chop = X87.Chop();
        float sum = 0;
        if (mGame.Sim != null)
            foreach (ImageSlot s in Images)
            {
                if (s.Item < 0)
                    continue;
                DataBlockManager.Block? item = mGame.Sim.DataBlocks().At(5, s.Item);
                DataBlockManager.Field? f = item?.Find("imageType");
                if (f == null || f.Elems.Count == 0 || !f.Elems[0].Set)
                    continue;
                if (mGame.Sim.DataBlocks().At(6, f.Elems[0].I) is DataBlockManager.Block image)
                    sum = X87.F32(Add(sum, FieldFloat(image, "mass", 0)));
            }
        float newMass = X87.F32(Add(PData.Mass, sum));
        if (newMass == Mass && InvMass != 0)
            return;
        float oldMass = Mass;
        Mass = newMass;
        InvMass = X87.F32(Div(1.0, Mass));
        if (oldMass != 0)
            foreach (Force fc in mForces)
                if (!fc.Acceleration)
                    for (int i = 0; i < 3; ++i)
                        fc.V[i] = X87.F32(Div(Mul(fc.V[i], oldMass), Mass));
    }

    // Client copy of the control object (docs/client-net.md 4.4): the item of each image slot as the ghost sends it
    // (3.4, Player_unpackImages), so the mass of the prediction is the server's. The image state machine stays idle.
    public void SetClientItems(ReadOnlySpan<int> items)
    {
        for (int slot = 0; slot < kImageSlots && slot < items.Length; ++slot)
            Images[slot].Item = items[slot];
        RecomputeMass();
    }

    // src/game/player.cpp Player::worldBox: 0x005069fc: the movement box translated, never rotated.
    private void WorldBox(Span<float> output)
    {
        for (int i = 0; i < 3; ++i)
        {
            output[i] = X87.F32(Add(BoxMin[i], Transform.P[i]));
            output[i + 3] = X87.F32(Add(BoxMax[i], Transform.P[i]));
        }
    }

    // src/game/player.cpp Player::updateBoxHeight: 0x004104fc.
    private void UpdateBoxHeight() => BoxHeight = Crouching ? PData.BoxCrouchHeight : PData.BoxNormalHeight;

    // ---- forces and containers (2.6) ----

    // src/game/player.cpp Player::setForce: SimMovement force list (0x005047f0).
    public void SetForce(uint type, ReadOnlySpan<float> v, bool acceleration)
    {
        var f = new Force { Type = type, Acceleration = acceleration };
        f.V[0] = v[0];
        f.V[1] = v[1];
        f.V[2] = v[2];
        if (!acceleration)
            for (int i = 0; i < 3; ++i)
                f.V[i] = X87.F32(Mul(InvMass, f.V[i]));
        for (int i = 0; i < mForces.Count; ++i)
            if (mForces[i].Type == type)
            {
                mForces[i] = f;
                return;
            }
        mForces.Add(f);
    }

    // src/game/player.cpp Player::removeForce: 0x005048f4.
    public void RemoveForce(uint type)
    {
        for (int i = 0; i < mForces.Count; ++i)
            if (mForces[i].Type == type)
            {
                mForces.RemoveAt(i);
                return;
            }
    }

    // src/game/player.cpp Player::refileBox: SimObject::setTransform (0x005054a4): the container entry takes the new
    // world box at once; the container change (gravity force) is seen by the next step's updateContainer.
    public void RefileBox()
    {
        if (!mInContainer)
            return;
        Span<float> box = stackalloc float[6];
        WorldBox(box);
        // SimObject_setTransformRaw (0x005054a4) -> SimContainer_setBox (0x004f9a38) runs the database update for an
        // unchanged box too: in the terrain database the object's box ends then move in front of equal ends (the
        // findObjects order of objects with the same max x).
        if (mGame.World.Find(this) != null)
            mGame.World.SetBox(this, box);
    }

    // SimObject_setTransformRaw on the client ghost (Player_applyControlState 00414a00): the transform is rebuilt from the Euler
    // angles and `position`, no collision query; the container entry takes the new box.
    public void PlaceRaw(ReadOnlySpan<float> position)
    {
        using var chop = X87.Chop();
        var m = new Transform();
        MissionTransform.EulerToMatrix(Rotation, ref m);
        Transform.M = m.M;
        Transform.Flags = m.Flags | 3;
        for (int i = 0; i < 3; ++i)
            Transform.P[i] = position[i];
        AtRest = false;
        RefileBox();
    }

    // src/game/player.cpp Player::updateContainer: container entry and the terrain container's gravity (FC2-FC5).
    public void UpdateContainer()
    {
        if (!mInContainer)
            return;
        Span<float> box = stackalloc float[6];
        WorldBox(box);
        Container.Entry? own = mGame.World.Find(this);
        if (own == null)
            return;
        mGame.World.SetBox(this, box);
        // FC2/FC3: entering a container inside the terrain adds its gravity, leaving removes it; a move between nested
        // containers does both, which re-appends the entry (FC5).
        Container.Entry? terrain = mGame.World.TerrainOf(this);
        object? parent = mGame.World.ParentOf(this);
        if (parent == mParent && terrain == mTerrainContainer)
            return;
        if (mTerrainContainer != null)
            RemoveForce(kGravityForce);
        // SimTerrain_enterContainer (0x005195b4): the gravity force and the container drag (+0x124) are taken on entry.
        // Leaving (0x0051963c) removes the force only, the drag stays.
        if (terrain != null)
        {
            SetForce(kGravityForce, terrain.Gravity, true);
            mContainerDrag = terrain.Drag;
        }
        mParent = parent;
        mTerrainContainer = terrain;
    }

    // src/game/player.cpp inventoryCounts: the item counts of a Player control object, or of the Player an
    // ObserverCamera orbits; null otherwise.
    public static int[]? InventoryCounts(GameBase? control)
    {
        if (From(control) is Player p)
            return p.Counts;
        if (control is ObserverCamera cam && From(cam.OrbitObject) is Player o)
            return o.Counts;
        return null;
    }

    // ---- moves (2.1) ----

    // src/game/player.cpp Player::applyMoves: 0x00413f18.
    public override void ApplyMoves(Move[] moves, int count)
    {
        if (Dead)
            return;
        SimManager? m = Manager();
        int self = Id();
        for (int i = 0; i < count; ++i)
        {
            if (Credit > 5)
                break;
            ++Credit;
            Move mv = moves[i];
            if (mv.UseItem != -1 && mGame.Console != null)
                mGame.Console.CallFunction("remoteUseItem", new[] { Numbers.FormatInt(Id()), Numbers.FormatInt(mv.UseItem) });
            // The script may have deleted the player (the original would run on freed memory).
            if (m == null || m.FindById(self) != this)
                return;
            if (Dead)
                break;
            if (mv.Fire && !LastMove.Fire)
                TriggerImage(0, true);
            else if (!mv.Fire && LastMove.Fire)
                TriggerImage(0, false);
            if (AiControlled)
                LastMove = mv;
            RepairStep(kDt); // ShapeBase_repairTick (0x0044b610) before every processed move (6.3)
            UpdateMove(LastMove);
            UpdateAnimation(kDt);
            if (!AiControlled)
                LastMove = mv;
        }
        if (!(MountId != 0 && MountPoint == 1))
            SetMaskBits(0x2000); // TK7: not for a pilot
        TicksWithoutMove = 0;
    }

    // src/game/player.cpp Player::serverTick: 0x00415108.
    public override void ServerTick(uint tickMs)
    {
        if (!mAdded)
            return;
        if (Credit < -25 || Dead || (!AiControlled && ControlClient == 0))
        {
            LastMove = new Move();
            TicksWithoutMove = 0;
            RepairStep(kDt); // before the server tick's null move (0x0041514a)
            UpdateMove(LastMove);
            UpdateAnimation(kDt);
            if (!(MountId != 0 && MountPoint == 1))
                SetMaskBits(0x2000); // TK4: not for a pilot (the mounted step sets it, vehicle-sim VQ5)
        }
        else
        {
            --Credit;
            ++TicksWithoutMove;
        }
        ExpireCollisions(); // 0x00448248: the onCollision records go when the latest has expired
        if (Jetting)
            LastJetTime = tickMs;
        // 0x00415108: freeLook went from TRUE to FALSE: zero the pitch and send it.
        bool freeLook = FreeLookPref();
        if (!freeLook && FreeLookCopy)
        {
            Pitch = 0;
            SetMaskBits(0x2000);
        }
        FreeLookCopy = freeLook;
        // +0xfed: 0 for AI, else 1 unless the control client is a human whose record +0x1c is 1.
        if (AiControlled)
        {
            PdaFlag = false;
        }
        else
        {
            ClientRecord? r = mGame.Players.FindHuman(ControlClient);
            PdaFlag = !(r != null && r.GuiMirror == 1);
        }
        // Mission area (6.6).
        float[] area = { 0, 0, 0, 0 };
        mGame.Players.MissionArea(area);
        using var chop = X87.Chop();
        bool inside = Transform.P[0] >= area[0] && X87.F32(Add(area[0], area[2])) >= Transform.P[0] && Transform.P[1] >= area[1] &&
                      X87.F32(Add(area[1], area[3])) >= Transform.P[1];
        if (inside != InMissionArea && mGame.Console != null)
        {
            string line = "Player::" + (inside ? "enter" : "leave") + "MissionArea(" + Numbers.FormatInt(Id()) + ");";
            InMissionArea = inside;
            mGame.Console.Evaluate(line);
        }
    }

    // ---- the step (2.3, 2.4) ----

    private static float MaxF(float a, float b) => a < b ? b : a; // std::max

    // src/game/player.cpp Player::updateMove: one 32 ms step (0x00415304).
    public void UpdateMove(Move mv)
    {
        using var chop = X87.Chop();
        if (Block() is DataBlockManager.Block blk)
            ReadLiveData(blk, PData);
        Data d = PData;
        const float dt = kDt;
        float[] v = Velocity;
        UpdateContainer(); // a script setPosition since the last step (RA7, FC5)
        float[] r0 = { Transform.M[0], Transform.M[1], Transform.M[2] };
        float[] r1 = { Transform.M[3], Transform.M[4], Transform.M[5] };
        float[] r2 = { Transform.M[6], Transform.M[7], Transform.M[8] };

        // S1 item images (7.5): slots 0..7 in order, dt 0.032.
        {
            SimManager? mgr = Manager();
            int self = Id();
            for (int slot = 0; slot < kImageSlots; ++slot)
            {
                UpdateImageState(slot, dt);
                if (mgr == null || mgr.FindById(self) != this)
                    return; // an image script deleted the player
            }
        }

        // MV1 desired speed.
        float fwd = X87.F32(Sub(Mul(mv.Forward, d.MaxForwardSpeed), Mul(mv.Backward, d.MaxBackwardSpeed)));
        float side = X87.F32(Mul(Sub(mv.Right, mv.Left), d.MaxSideSpeed));
        float speed = X87.F32(Sqrt(X87.F32(Add(Mul(fwd, fwd), Mul(side, side)))));
        float cap = fwd > 0.0f ? MaxF(d.MaxForwardSpeed, d.MaxSideSpeed) : MaxF(d.MaxBackwardSpeed, d.MaxSideSpeed);
        if (speed > cap)
        {
            float k = X87.F32(Div(cap, speed));
            fwd = X87.F32(Mul(k, fwd));
            side = X87.F32(Mul(k, side));
        }
        if (Crouching)
        {
            fwd = X87.F32(Mul(fwd, 0.5f));
            side = X87.F32(Mul(side, 0.5f));
        }

        // Move direction in world space, used by the jump and the jet.
        void MoveDirection(float[] output)
        {
            float a = X87.F32(Sub(mv.Right, mv.Left)), b = X87.F32(Sub(mv.Forward, mv.Backward));
            for (int i = 0; i < 3; ++i)
                output[i] = X87.F32(Add(X87.F32(Mul(a, r0[i])), X87.F32(Mul(b, r1[i]))));
        }

        // MV2 jump.
        // A ghost jumps only when it is the client's own control object (Player_updateMove 0x00415304).
        if (mv.Jump && MountId == 0 && !Dead && CurrentAnim != 15 && TicksSinceJumpContact < 8 && (!ClientSide || LocallyControlled))
        {
            TicksSinceJumpContact = 8;
            if (CurrentAnim != 6)
                StartAnimation(6);
            float[] dir = new float[3];
            MoveDirection(dir);
            float l = X87.F32(X87.Len(dir));
            float along = 0.0f;
            if (l >= 0.01)
            {
                float k = X87.F32(Div(1.0, X87.F32(X87.Len(dir))));
                for (int i = 0; i < 3; ++i)
                    dir[i] = X87.F32(Mul(k, dir[i]));
                along = X87.F32(X87.Dot(dir, JumpNormal));
            }
            float[] impulse = { 0, 0, X87.F32(Mul(JumpNormal[2], d.JumpImpulse)) };
            if (along > 0.0f)
            {
                float k = X87.F32(Mul(along, d.JumpImpulse));
                impulse[0] = X87.F32(Mul(k, dir[0]));
                impulse[1] = X87.F32(Mul(k, dir[1]));
            }
            for (int i = 0; i < 3; ++i)
                v[i] = X87.F32(Add(Mul(impulse[i], InvMass), v[i]));
        }

        // MV3 jet.
        if (Jetting)
        {
            if (!(Energy > d.MinJetEnergy) || !mv.Jet)
                Jetting = false;
        }
        else if (Energy >= 5.0f && mv.Jet && MountId == 0)
        {
            Jetting = true;
        }
        if (Jetting && !Dead)
        {
            Energy = X87.F32(Sub(Energy, d.JetEnergyDrain));
            float l = 0.0f;
            float[] dir = { 0, 0, 0 };
            if (TicksSinceJumpContact > 8)
            {
                MoveDirection(dir);
                l = X87.F32(X87.Len(dir));
            }
            float[] force = { 0, 0, d.JetForce };
            if (l > 0.01)
            {
                float k1 = X87.F32(Div(1.0, l));
                for (int i = 0; i < 3; ++i)
                    dir[i] = X87.F32(Mul(k1, dir[i]));
                float fv = X87.F32(X87.Dot(dir, v));
                float k = fv > d.MaxJetForwardVelocity ? 0.0f : fv < 0.0f ? 1.0f : X87.F32(Sub(1.0, Div(fv, d.MaxJetForwardVelocity)));
                if (k > d.MaxJetSideForceFactor)
                    k = d.MaxJetSideForceFactor;
                float upForce = X87.F32(Mul(Sub(1.0, k), d.JetForce));
                float sideForce = X87.F32(Mul(k, d.JetForce));
                for (int i = 0; i < 3; ++i)
                    force[i] = X87.F32(Add(Mul(upForce, r2[i]), X87.F32(Mul(sideForce, dir[i]))));
            }
            SetForce(kJetForce, force, false);
        }
        else
        {
            Jetting = false;
            float[] zero = { 0, 0, 0 };
            SetForce(kJetForce, zero, false);
        }

        // S5 yaw and pitch (section 4).
        {
            const double twoPi = 6.283185307179586;
            float yaw = X87.F32(Add(mv.Turn, Rotation[2]));
            if (yaw < 0.0f)
                yaw = X87.F32(Add(yaw, twoPi));
            else if (yaw > twoPi)
                yaw = X87.F32(Sub(yaw, twoPi));
            Rotation[2] = yaw;
            Pitch = X87.F32(Add(mv.Pitch, Pitch));
            if (Pitch > 1.53588974175501)
                Pitch = 1.5358897f; // 0x3fc49809
            else if (Pitch < -1.53588974175501)
                Pitch = -1.5358897f;
        }

        // S6 energy recharge (6.2).
        if (!Dead)
        {
            Energy = X87.F32(Add(Mul(RechargeRate, dt), Energy));
            if (Energy > d.MaxEnergy)
                Energy = d.MaxEnergy;
            else if (Energy < 0.0f)
                Energy = 0.0f;
        }
        // S7 (server only: a ghost keeps the crouch flag of the last update, 0x00415304), S8.
        if (!ClientSide)
            Crouching = d.CanCrouch && mv.Crouch && !Jetting && TicksSinceContact < 32;
        Falling = v[2] <= -10.0f;

        // S11-S14, shared by the free and the mounted step.
        void FinishStep()
        {
            UpdateWeaponPullback();
            TicksSinceContact = ContactFlag ? 0 : TicksSinceContact + 1;
            if (TicksSinceContact > 0x3fffffff)
                TicksSinceContact = 0x3fffffff;
            if (TicksSinceJumpContact < 0x3fffffff)
                ++TicksSinceJumpContact;
            if ((Flash > 0.0f && !Dead) || (Flash > 0.4 && Dead))
                Flash = X87.F32(Sub(Flash, (double)0.007f));
            if (Flash < 0.0f)
                Flash = 0.0f;
            UpdateBoxHeight();
        }
        // S10 mounted (2.7): the mount point's velocity and transform replace the movement.
        if (MountId != 0)
        {
            MountedStep(mv);
            FinishStep();
            return;
        }

        // A ghost counts ticks without a move unless it is the client's own control object (0x00415304).
        if (ClientSide)
            TicksWithoutMove = LocallyControlled ? 0 : TicksWithoutMove + 1;

        // MV4 velocity.
        float[] desired = new float[3];
        for (int i = 0; i < 3; ++i)
            desired[i] = X87.F32(Add(Add(Mul(side, r0[i]), Mul(fwd, r1[i])), Mul(0.0, r2[i])));
        float[] vOld = { v[0], v[1], v[2] };
        foreach (Force fc in mForces)
            for (int i = 0; i < 3; ++i)
                v[i] = X87.F32(Add(Mul(fc.V[i], dt), v[i]));
        if (mContainerDrag != 0.0f)
        {
            float c = X87.F32(Mul(Mul(d.Drag, mContainerDrag), dt));
            for (int i = 0; i < 3; ++i)
                v[i] = X87.F32(Sub(v[i], Mul(v[i], c)));
        }
        if (d.HorzVelClamp > 3.0f)
        {
            float h = X87.F32(Sqrt(X87.F32(Add(Mul(v[0], v[0]), Mul(v[1], v[1])))));
            if (h > d.HorzVelClamp)
            {
                float k = X87.F32(Div(d.HorzVelClamp, h));
                v[0] = X87.F32(Mul(k, v[0]));
                v[1] = X87.F32(Mul(k, v[1]));
            }
        }

        // MV5 ground control.
        if (ContactFlag)
        {
            float[] diff = { X87.F32(Sub(desired[0], v[0])), X87.F32(Sub(desired[1], v[1])), 0.0f };
            float dl = X87.F32(X87.Len(diff));
            float t = X87.F32(Mul(Traction, d.GroundTraction));
            if (t > 1.0f)
                t = 1.0f;
            float maxDv = X87.F32(Mul(Mul(Div(d.GroundForce, Mass), t), dt));
            if (dl > maxDv)
            {
                float k = X87.F32(Div(maxDv, dl));
                for (int i = 0; i < 3; ++i)
                    diff[i] = X87.F32(Mul(k, diff[i]));
                RestFlag = false;
            }
            else
            {
                RestFlag = true;
            }
            for (int i = 0; i < 3; ++i)
                v[i] = X87.F32(Add(diff[i], v[i]));
        }
        else
        {
            RestFlag = X87.Len(vOld) < 0.1;
        }

        // MV6 target and traction decay.
        float half = X87.F32(Mul(dt, 0.5f));
        float[] target = new float[3];
        for (int i = 0; i < 3; ++i)
            target[i] = X87.F32(Add(Mul(Add(vOld[i], v[i]), half), Transform.P[i]));
        float saved = X87.F32(Mul(Traction, 0.6));
        if (saved < 0.1)
            saved = 0.0f;
        Traction = 0.0f;

        // 0x005067f8: a Player leaves the carry lists of the Moveables it stood on before the move.
        DetachAll();
        // MV7 stepped move with the rotation of the target.
        SteppedMoveTo(target, 0.3f, 0.075f);

        // MV8.
        if (Traction < saved)
            Traction = saved;
        if (Traction > 1.0f)
            Traction = 1.0f;

        // S11 weapon pull-back (0x00416486), S12 counters, S13 damage flash, S14 box height.
        FinishStep();
    }

    // src/game/player.cpp Player::steppedMove: SimMovement_stepMove (0x00505a3c) with the failed-move fallback
    // (0x00505720): the stepped move of the Player toward `target`, the transform commit, the collision events and the
    // container update. False when the move was blocked in every iteration.
    private bool SteppedMoveTo(float[] target, float step0, float step1, bool keepMatrix = false)
    {
        float[] v = Velocity;
        var rot = new Transform();
        float[] euler = { Rotation[0], Rotation[1], Rotation[2] };
        MissionTransform.EulerToMatrix(euler, ref rot);
        // SimMovement_pushBy (0x005064f8) moves the object's own transform: a mounted Player keeps the matrix of its
        // mount point (the pilot carried by the displace of its vehicle).
        if (keepMatrix)
        {
            rot.M = Transform.M;
            rot.Flags = Transform.Flags;
        }
        ContactFlag = false;
        AtRest = false;
        mMove.Image.Rot = rot.M;
        mMove.Image.BoxMin = Float3.Of(BoxMin);
        mMove.Image.BoxMax = Float3.Of(BoxMax);
        mMove.Image.Radius = Radius;
        mMove.Dt = kDt;
        BuildCandidates(target);
        float[] pos = { Transform.P[0], Transform.P[1], Transform.P[2] };
        float[] goal = { target[0], target[1], target[2] };
        bool moved = mMove.Run(pos, goal, step0, step1);
        if (!moved)
        {
            // Three blocked iterations: at rest, v = 0, and the fallback 0x00505720 clears the contact flag
            // (docs/collision.md 3 notes).
            AtRest = true;
            v[0] = v[1] = v[2] = 0.0f;
            ContactFlag = false;
            // SimMovement_moveTo -> SimObject_setTransform (0x00505814): the image is tested at p with a fresh candidate
            // list (v = 0). A hit runs the callback with used = 0 and endTime = 1.0, which sets the contact flag again
            // when the player still touches something; the position stays p.
            var notified = new List<object>(mMove.Notify);
            BuildCandidates(pos);
            var hits = new List<CollisionHit>();
            for (int it = 0; it < mMove.MaxIterations; ++it)
            {
                if (mMove.TestAt(pos, hits))
                    break;
                var info = new MoveInfo();
                info.Iteration = it;
                info.Used = 0.0f;
                info.EndTime = 1.0f;
                info.Current = new[] { pos[0], pos[1], pos[2] };
                info.Attempted = new[] { pos[0], pos[1], pos[2] };
                info.Target = new[] { pos[0], pos[1], pos[2] };
                info.Candidates = mMove.Candidates;
                info.Hits = hits;
                info.Notify = notified;
                if (!ProcessCollision(info))
                    break;
            }
            mMove.Notify = notified;
        }
        Transform.M = rot.M;
        Transform.Flags = rot.Flags | 3;
        for (int i = 0; i < 3; ++i)
            Transform.P[i] = pos[i];
        // Collision events to both sides for every notified object (0x00507180).
        // The client prediction posts no events: they run server scripts in the prediction world (client-net.md 4.4).
        if (!ClientSide)
            foreach (object other in mMove.Notify)
            {
                PostCollision(other as SimObject, this);
                PostCollision(this, other as SimObject);
            }
        UpdateContainer();
        return moved;
    }

    // src/game/player.cpp Player::testPositionAt: GameBase::testPosition for a Player: true when its collision image at
    // `q` touches nothing.
    public bool TestPositionAt(ReadOnlySpan<float> q)
    {
        using var chop = X87.Chop();
        var rot = new Transform();
        float[] euler = { Rotation[0], Rotation[1], Rotation[2] };
        MissionTransform.EulerToMatrix(euler, ref rot);
        mMove.Image.Rot = rot.M;
        mMove.Image.BoxMin = Float3.Of(BoxMin);
        mMove.Image.BoxMax = Float3.Of(BoxMax);
        mMove.Image.Radius = Radius;
        BuildCandidates(q);
        Action<object?>? touch = mMove.OnTouch; // a test posts no collision events
        mMove.OnTouch = null;
        var hits = new List<CollisionHit>();
        bool free = mMove.TestAt(q, hits);
        mMove.OnTouch = touch;
        return free;
    }

    // src/game/player.cpp Player::throwDirection: 0x004113e8: (0, 1, 0) pitched by pitch + 0.5 * cos(pitch) and turned by
    // the player matrix (docs/static-sim.md 5.4). 0x00606890 is the C runtime cosine.
    public override bool ThrowDirection(Span<float> output)
    {
        using var chop = X87.Chop();
        double c = Math.Cos((double)Pitch);
        float term = X87.F32(Mul(c, 0.5f));
        float angle = X87.F32(Add(Pitch, term));
        var pitched = new Transform();
        float[] euler = { angle, 0.0f, 0.0f };
        MissionTransform.EulerToMatrix(euler, ref pitched);
        // Row 1 of pitched * transform (0x005ed570), the image of (0, 1, 0).
        for (int j = 0; j < 3; ++j)
            output[j] = X87.F32(Add(Add(Mul(pitched.M[3], Transform.M[j]), Mul(pitched.M[4], Transform.M[3 + j])),
                                    Mul(pitched.M[5], Transform.M[6 + j])));
        return true;
    }

    // src/game/player.cpp Player::pushBy: Moveable carry (docs/collision.md E3.2): the displacement of a rider or an
    // overlapped Player by `delta` (0x005064f8): terrain and interiors only, sub-steps from the box extents.
    public bool PushBy(ReadOnlySpan<float> delta, ReadOnlySpan<float> mover)
    {
        using var chop = X87.Chop();
        uint savedMask = CollisionMask;
        CollisionMask = 6;
        // Bits 0x05 of +0x164 are cleared and stay cleared. Bit 0 is the Player's rest flag (CR5): a pushed Player is not
        // stopped by the rest rule of its contact response until its own next move sets the flag.
        RestFlag = false;
        float[] box = { BoxMin[0], BoxMin[1], BoxMin[2], BoxMax[0], BoxMax[1], BoxMax[2] };
        bool ok = Displace.PushBy(delta, mover, box, Velocity, ref AtRest, i => Transform.P[i],
                                  (target, step0, step1) => SteppedMoveTo(target, step0, step1, true));
        CollisionMask = savedMask;
        return ok;
    }

    // src/game/player.cpp Player::moverImage: the Player's image for the overlap test.
    public void MoverImageOf(MoverImage output)
    {
        output.Pos = Transform.P;
        output.Rot = Transform.M;
        output.BoxMin = Float3.Of(BoxMin);
        output.BoxMax = Float3.Of(BoxMax);
        output.Radius = Radius;
    }

    // ---- collision (docs/collision.md 2.1, 4) ----

    // src/game/player.cpp Player::buildCandidates.
    private void BuildCandidates(ReadOnlySpan<float> target)
    {
        mMove.Candidates.Clear();
        Span<float> query = stackalloc float[6];
        Collision.SweptBox(BoxMin, BoxMax, target, Velocity, kDt, query);
        mGame.CollisionCandidates(this, CollisionMask, query, mMove.Candidates);
        // Player bind (0x00410f..: image +0x48 = the shape instance, +0x8c = detail 0, mesh mode): sphere and box images
        // are tested against the Player's own shape in its current pose.
        mMove.Image.Instance = null;
        mMove.Image.Detail = 0;
        mMove.Image.MeshMode = true;
        foreach (CollisionImage image in mMove.Candidates)
            if (image.ImageKind == CollisionImage.Kind.TriggerSphere || image.ImageKind == CollisionImage.Kind.Trigger)
            {
                mMove.Image.Instance = ShapeState.ShapeInstanceOf(mGame, this);
                break;
            }
    }

    // src/game/player.cpp Player::processCollision: 0x00416598.
    private bool ProcessCollision(MoveInfo info)
    {
        List<CollisionHit> hits = info.Hits!;
        List<object> notify = info.Notify!;
        // Step 1: Items, Mines and corpses are notified and never block.
        for (int i = 0; i < hits.Count;)
        {
            if ((hits[i].Type & kNonBlockingTypes) != 0)
            {
                notify.Add(hits[i].Object!);
                hits.RemoveAt(i);
            }
            else
            {
                ++i;
            }
        }
        // Step 2: up = -normalize(gravity).
        float[] up = { 0, 0, 1 };
        foreach (Force f in mForces)
            if (f.Type == kGravityForce)
            {
                float l = X87.F32(X87.Len(f.V));
                float k = X87.F32(Div(1.0, l));
                for (int i = 0; i < 3; ++i)
                    up[i] = -X87.F32(Mul(k, f.V[i]));
                break;
            }
        // Step 3: contacts.
        var contacts = new List<Contact>();
        foreach (CollisionHit h in hits)
        {
            if (!notify.Contains(h.Object!))
                notify.Add(h.Object!);
            foreach (CollisionSurface s in h.Surfaces)
            {
                var c = new Contact();
                Float3 sn = s.Normal;
                X87.MulVector(sn, h.Rot, c.N);
                c.UpDot = X87.F32(X87.Dot(c.N, up));
                c.StepHeight = h.StepHeight;
                c.Surface = s;
                c.Object = h.Object;
                c.Type = h.Type;
                contacts.Add(c);
            }
        }
        // Step 5: step-up.
        if (contacts.Count > 1 && StepUp(info, contacts))
            return true;
        // Step 6: response on every contact in order.
        bool any = false;
        foreach (Contact c in contacts)
            any = ContactResponse(c) || any;
        if (!any)
            return false;
        float rem = X87.F32(Sub(mMove.Dt, info.Used));
        for (int i = 0; i < 3; ++i)
            info.Target![i] = X87.F32(Add(X87.F32(Mul(Velocity[i], rem)), info.Current![i]));
        return true;
    }

    // src/game/player.cpp Player::stepUp: 0x00416a64.
    private bool StepUp(MoveInfo info, List<Contact> contacts)
    {
        const double kWall = 0.707; // double at 0x00416de4
        bool wall = false;
        foreach (Contact c in contacts)
            if ((c.Type & kStepUpTypes) != 0 && (double)c.UpDot <= kWall)
                wall = true;
        if (!wall)
            return false;
        int best = -1;
        float maxH = 0.0f;
        for (int i = 0; i < contacts.Count; ++i)
        {
            Contact c = contacts[i];
            if ((c.Type & kStepUpTypes) == 0 || !((double)c.UpDot >= kWall))
                continue;
            float h = c.StepHeight;
            if (h < 0.0f)
                h = X87.F32(Sub(Radius, c.Surface.Distance));
            if (h > maxH)
                maxH = h;
            if (best < 0 || c.UpDot >= contacts[best].UpDot)
                best = i;
        }
        if (best < 0 || maxH > X87.F32(Mul(Radius, 0.6f)))
            return false;
        float[] raised = { info.Attempted![0], info.Attempted[1], X87.F32(Add(Add(maxH, 0.001f), info.Attempted[2])) };
        // Test with a copy of the candidate list (0x00505790); trigger images hit here get their event too.
        var scratch = new List<CollisionHit>();
        Contact response = contacts[best];
        var probe = new SteppedMove();
        probe.Image = mMove.Image;
        probe.Candidates = new List<CollisionImage>(mMove.Candidates);
        probe.OnTouch = mMove.OnTouch;
        if (!probe.TestAt(raised, scratch))
            return false;
        ContactResponse(response);
        float rem = X87.F32(Sub(mMove.Dt, info.EndTime));
        for (int i = 0; i < 3; ++i)
        {
            info.Current![i] = raised[i];
            info.Target![i] = X87.F32(Add(X87.F32(Mul(Velocity[i], rem)), raised[i]));
        }
        return true;
    }

    // src/game/player.cpp Player::contactResponse: 0x00416e4c.
    private bool ContactResponse(in Contact c)
    {
        float[] v = Velocity;
        ContactFlag = true;
        if (c.Surface.Distance < 0.0f)
            return false;
        ReadOnlySpan<float> n = c.N;
        float vn = X87.F32(X87.Dot(v, n));
        if (vn >= 0.0f)
            return false;
        if (n[2] > PData.JumpSurfaceMinDot && (n[2] > JumpNormal[2] || TicksSinceJumpContact > 1))
        {
            JumpNormal[0] = n[0];
            JumpNormal[1] = n[1];
            JumpNormal[2] = n[2];
            TicksSinceJumpContact = 0;
        }
        // Landing damage (CR4).
        if (-vn > PData.MinDamageSpeed)
        {
            float[] origin = { 0, 0, 0 };
            float value = X87.F32(Mul(PData.DamageScale, X87.F32(Sub(-(double)vn, PData.MinDamageSpeed))));
            bool wasAlive = !Dead;
            int anim = CurrentAnim;
            ApplyDamage(0, value, origin, OwnerClient);
            if (wasAlive && !Dead && anim != 15)
                StartAnimation(15);
        }
        float k = X87.F32(Add(-(double)vn, kSeparation));
        for (int i = 0; i < 3; ++i)
            v[i] = X87.F32(Add(X87.F32(Mul(n[i], k)), v[i]));
        if (c.UpDot > 0.0f)
        {
            float t = c.Surface.HasMaterial ? X87.F32(Mul(c.Surface.Friction, c.UpDot)) : c.UpDot;
            Traction = X87.F32(Add(t, Traction));
            if (RestFlag && Mul(t, 0.75f) > X87.Len(v))
                v[0] = v[1] = v[2] = 0.0f;
        }
        // CR6: standing on a Moveable (upDot > 0.985) puts the Player on its carry list (0x005067d0).
        if (c.UpDot > (double)0.985f && (c.Type & ObjectType.kMoveableType) != 0 && c.Object is GameBase m)
            Attach(m);
        return true;
    }

    // src/game/player.cpp Player::postCollision.
    private void PostCollision(SimObject? target, SimObject? sender) => mGame.PostCollisionEvent(target, sender);

    // ---- animation (5.3, 5.5 server branch) ----

    // src/game/player.cpp Player::setAnimation: 0x00410568.
    public void SetAnimation(int index)
    {
        // The original reads the priority table without a range check (5.9 Q6); an index outside the table is refused
        // here (D11).
        if (index < 0 || index > 50)
            return;
        if (index == PendingAnim)
            return;
        if (PendingAnim != -1 && !(PData.AnimPriority[PendingAnim] < PData.AnimPriority[index]))
            return;
        if (!Dead)
            SetMaskBits(0x4000);
        PendingAnim = index;
        StartAnimation(index);
    }

    // src/game/player.cpp Player::startAnimation: 0x004105d0.
    public void StartAnimation(int index)
    {
        bool inCrouch = CurrentAnim == 7 || CurrentAnim == 8 || (CurrentAnim >= 10 && CurrentAnim <= 13);
        if ((index == 7 && inCrouch) || (index == 9 && !inCrouch))
            return;
        if (index < 0 || index > 50) // index 51 passes the original's test: off by one, refused here
            return;
        CurrentAnim = index;
        AnimPosition = PData.AnimDirection[index] > 0 ? 0.0f : 0.99f;
    }

    // The 80-bit constant at 0x00411160 rounded to 53 bits (portable build).
    private static readonly double kLookScale = (double)0xa6adbac815dcea70UL * Math.ScaleB(1.0, -64);

    // src/game/player.cpp Player::updateAnimation: 0x004108dc, server branch.
    public void UpdateAnimation(float dt)
    {
        using var chop = X87.Chop();
        // Look thread (0x004108e7..0x00410947): set-sequence resets the position to 0 every call, then the position is
        // set from the pitch unless it is exactly 0.
        {
            float target = X87.F32(Add(Mul(Mul(-(double)Pitch, kLookScale), 0.5f), 0.5f));
            Pose.CrouchLooks = Crouching;
            Pose.LookSet = true;
            Pose.LookPosition = target != 0.0f ? X87.F32(Add(X87.F32(Sub(target, 0.0f)), 0.0f)) : 0.0f;
        }
        UpdateAnimationState(dt);
        // Server tail (0x00410acc..0x00410afb): first person flag from the current animation, then the shape is animated.
        if (CurrentAnim >= 0 && CurrentAnim < 51)
        {
            Pose.FirstPerson = (PData.AnimViewFlags[CurrentAnim] & 1) != 0;
            Pose.AnimIndex = CurrentAnim;
        }
        Pose.AnimPosition = AnimPosition;
    }

    // src/game/player.cpp Player::updateAnimationState: its state table (5.5).
    public void UpdateAnimationState(float dt)
    {
        bool finished = true; // current == -1 reads a direction outside the table, normally 0
        if (CurrentAnim >= 0)
            finished = PData.AnimDirection[CurrentAnim] > 0 ? AnimPosition >= 0.99 : AnimPosition <= 0.01;
        void Advance()
        {
            float duration = CurrentAnim >= 0 ? PData.AnimDuration[CurrentAnim] : 0;
            if (!(duration > 0.0f))
            {
                AnimPosition = 1.0f;
                return;
            }
            float delta = X87.F32(Div(dt, duration));
            AnimPosition = X87.F32(Add(AnimPosition, delta));
            // TSSequence_normalizePosition (0x005b8d94): outside 0..1 a cyclic sequence keeps the fraction, a one-shot
            // sequence stops at the end. A cyclic request therefore finishes only when a step lands in the last
            // hundredth (docs/player-sim.md 5.5).
            if (AnimPosition < 0.0f || AnimPosition > 1.0f)
            {
                if (CurrentAnim >= 0 && PData.AnimCyclic[CurrentAnim])
                    AnimPosition = X87.F32(Sub(AnimPosition, Math.Floor((double)AnimPosition)));
                else
                    AnimPosition = AnimPosition < 0.0f ? 0.0f : 1.0f;
            }
        }
        if (Dead)
        {
            if (!finished)
                Advance();
            return;
        }
        if (finished)
        {
            PendingAnim = -1;
            CurrentAnim = -1;
        }
        if (PendingAnim != -1)
        {
            Advance();
            return;
        }
        int bse = (Crouching && !Jetting && !Falling) ? 8 : 0;
        if (bse != CurrentAnim)
        {
            CurrentAnim = bse;
            AnimPosition = bse == 8 ? 1.0f : 0.0f;
        }
    }

    // ---- damage and death (6.4, 6.5) ----

    // src/game/player.cpp Player::applyDamage: landing damage.
    public void ApplyDamage(int type, float value, ReadOnlySpan<float> pos, int sourceId)
    {
        float[] zero = { 0, 0, 0 };
        ApplyDamage(type, value, pos, zero, zero, sourceId);
    }

    // src/game/player.cpp Player::applyDamage: 0x00412f94.
    public override void ApplyDamage(int type, float value, ReadOnlySpan<float> pos, ReadOnlySpan<float> vec,
                                     ReadOnlySpan<float> mom, int sourceId)
    {
        LastDamage = value;
        string fn = mGame.VirtualFunction(this, "onDamage");
        if (fn.Length == 0 || mGame.Console == null)
            return;
        using var chop = X87.Chop();
        // Hit location (0x00413074): pos in object space.
        float[] rel = { X87.F32(Sub(pos[0], Transform.P[0])), X87.F32(Sub(pos[1], Transform.P[1])), X87.F32(Sub(pos[2], Transform.P[2])) };
        ReadOnlySpan<float> m = Transform.M;
        float[] local =
        {
            X87.F32(Add(Add(Mul(rel[0], m[0]), Mul(rel[1], m[1])), Mul(rel[2], m[2]))),
            X87.F32(Add(Add(Mul(rel[0], m[3]), Mul(rel[1], m[4])), Mul(rel[2], m[5]))),
            X87.F32(Add(Add(Mul(rel[0], m[6]), Mul(rel[1], m[7])), Mul(rel[2], m[8]))),
        };
        DataBlockManager.Block? b = Block();
        float Pct(string name, float def) => b != null ? FieldFloat(b, name, def) : def;
        float height = Crouching ? PData.BoxCrouchHeight : PData.BoxNormalHeight;
        float torsoTop = X87.F32(Mul(height, Pct(Crouching ? "boxCrouchTorsoPercentage" : "boxNormalTorsoPercentage", 0.333299f)));
        float headBase = X87.F32(Mul(height, Pct(Crouching ? "boxCrouchHeadPercentage" : "boxNormalHeadPercentage", 0.666599f)));
        string vert = local[2] <= torsoTop ? "legs" : local[2] <= headBase ? "torso" : "head";
        string quad;
        if (vert[0] != 'h')
        {
            quad = local[1] >= 0.0f ? (local[0] <= 0.0f ? "front_left" : "front_right")
                                    : (local[0] <= 0.0f ? "back_left" : "back_right");
        }
        else
        {
            float w = X87.F32(Sub(BoxMax[0], BoxMin[0])), dd = X87.F32(Sub(BoxMax[1], BoxMin[1]));
            float back = X87.F32(Mul(Sub(Pct("boxHeadBackPercentage", 0), 0.5), w));
            float front = X87.F32(Mul(Sub(Pct("boxHeadFrontPercentage", 1), 0.5), w));
            float left = X87.F32(Mul(Sub(Pct("boxHeadLeftPercentage", 0), 0.5), dd));
            float right = X87.F32(Mul(Sub(Pct("boxHeadRightPercentage", 1), 0.5), dd));
            string row = local[1] < back ? "back" : local[1] <= front ? "middle" : "front";
            string col = local[0] < left ? "left" : local[0] <= right ? "middle" : "right";
            quad = col + "_" + row;
        }
        // A formatted script line (0x005f4280): every float goes through %g.
        static string G(float v) => Numbers.FormatFloat(v);
        string line = fn + "(" + Numbers.FormatInt(Id()) + "," + Numbers.FormatInt(type) + "," + G(value) + ",\"" + G(pos[0]) +
                      " " + G(pos[1]) + " " + G(pos[2]) + "\",\"" + G(vec[0]) + " " + G(vec[1]) + " " + G(vec[2]) +
                      "\",\"" + G(mom[0]) + " " + G(mom[1]) + " " + G(mom[2]) + "\"," + vert + "," + quad + "," +
                      Numbers.FormatInt(sourceId) + ");";
        mGame.Console.Evaluate(line);
    }

    // src/game/player.cpp Player::setDamage: ShapeBase_setDamageLevel (0x0044bbdc), then Player_setDamageLevel
    // (0x004111c4).
    public void SetDamage(float level)
    {
        float value = level < 0.0f ? 0.0f : level >= MaxDamage ? MaxDamage : level;
        if (value != Damage)
        {
            Damage = value;
            SetMaskBits(0x100);
        }
        if (!Dead)
        {
            if (Damage == MaxDamage)
                Kill();
        }
        else if (Damage < MaxDamage)
        {
            Dead = false;
            SetMaskBits(0x100);
            ObjectTypeBits = ObjectType.kPlayerType;
            CollisionMask = kLiveMask;
            if (mGame.World.Find(this) is Container.Entry e)
                e.Type = ObjectTypeBits;
        }
    }

    // src/game/player.hpp Player::setDamageLevel: vtable +0x190.
    public override void SetDamageLevel(float level) => SetDamage(level);

    // src/game/damage.cpp Player::damageCoverage: Player coverage (0x00411538): four rays from the explosion to the
    // legs, the chest and the two sides, each unobstructed ray adds a quarter.
    public override float DamageCoverage(ReadOnlySpan<float> pos)
    {
        using var chop = X87.Chop();
        float h = Radius; // +0x870: half the box height
        float[] p = { Transform.P[0], Transform.P[1], Transform.P[2] };
        float[][] ends = { new float[3], new float[3], new float[3], new float[3] };
        float[] half = { 0, 0, X87.F32(Mul(h, 0.5f)) };
        for (int i = 0; i < 3; ++i)
            ends[0][i] = X87.F32(Add(p[i], half[i]));
        for (int i = 0; i < 3; ++i)
            ends[1][i] = X87.F32(Add(ends[0][i], i == 2 ? h : 0.0f));
        float[] xs = { X87.F32(Mul(BoxMin[0], 0.5f)), X87.F32(Mul(BoxMax[0], 0.5f)) };
        for (int k = 0; k < 2; ++k)
        {
            float[] v = { xs[k], 0, 0 };
            float[] rv = new float[3];
            X87.MulVector(v, Transform.M, rv);
            for (int i = 0; i < 3; ++i)
                ends[2 + k][i] = X87.F32(Add(p[i], rv[i]));
            ends[2 + k][2] = X87.F32(Add(ends[2 + k][2], h));
            ends[2 + k][2] = X87.F32(Add(Mul(h, 0.25f), ends[2 + k][2]));
        }
        float cover = 0.0f;
        foreach (float[] e in ends)
        {
            object? hit = null;
            Vec3 hp = default, n = default;
            if (!mGame.World.CastRay(new Vec3(pos[0], pos[1], pos[2]), new Vec3(e[0], e[1], e[2]), 0x800086u, this, ref hit, ref hp, ref n))
                cover = AddF(cover, 0.25f);
        }
        return cover;
    }

    // src/game/player.hpp Player::damageFlash: 0x00413830.
    public override float DamageFlash() => Flash;

    // src/game/player.cpp Player::kill: 0x004112ac.
    public void Kill()
    {
        if (Dead)
            return;
        Dead = true;
        RaySkip = true;
        Damage = MaxDamage;
        SetMaskBits(0x100);
        mGame.SensorJam(this, false); // Player_kill 0x004112ac unlinks; the radius stays
        DeathTime = Manager() != null ? Manager()!.Time() : 0;
        int self = Id();
        SimManager? m = Manager();
        mGame.ScriptCallback(this, "onKilled", Array.Empty<string>());
        if (m == null || m.FindById(self) != this)
            return; // the script deleted the player
        ObjectTypeBits = kCorpseType;
        CollisionMask = kDeadMask;
        if (mGame.World.Find(this) is Container.Entry e)
            e.Type = ObjectTypeBits;
        // Timer event 0x401 value 200 at time + 4.0: releases every image trigger.
        m.PostEvent(this, Store(Add(m.Time(), 4.0)), () =>
        {
            for (int slot = 0; slot < kImageSlots; ++slot)
                TriggerImage(slot, false);
        });
        Jetting = false;
    }

    // ---- inventory and image slots (7.1 - 7.4), without the image state machine ----

    // src/game/player.cpp Player::itemCount.
    public int ItemCountOf(int type) => type < 0 || type >= kItemTypes ? 0 : Counts[type];

    // src/game/player.cpp Player::setItemCount.
    public int SetItemCount(int type, int count)
    {
        // The original has no upper bound on the type (7.1 F2); out-of-table types are refused.
        if (type < 0 || type >= kItemTypes)
            return 0;
        Counts[type] = Math.Max(count, 0);
        if (Counts[type] == 0 && SlotOf[type] >= 0)
            UnmountItem(SlotOf[type]);
        return Counts[type];
    }

    // src/game/player.cpp Player::itemCallback: Player_findItemCallback (0x004191a0): <item>::event, <className>::event,
    // item::event.
    public void ItemCallback(int type, string ev, string? extra = null)
    {
        DataBlockManager.Block? item = mGame.Sim?.DataBlocks().At(5, type);
        if (item == null || mGame.Console == null)
            return;
        var names = new List<string> { item.Name + "::" + ev };
        if (mGame.Sim!.DataBlocks().GetField(item, "className", "", out string cls) && cls.Length != 0)
            names.Add(cls + "::" + ev);
        names.Add("item::" + ev);
        foreach (string fn in names)
            if (mGame.Console.HasFunction(fn))
            {
                var args = new List<string> { Numbers.FormatInt(Id()), item.Name };
                if (extra != null)
                    args.Add(extra);
                mGame.Console.CallFunction(fn, args);
                return;
            }
    }

    // ---- control state (8.1) ----

    // The control state as the client reads it (docs/client-net.md 4.4 table, 00413b90).
    public sealed class ClientControl
    {
        public float Yaw;
        public bool HasPosition;                                  // not mounted
        public readonly float[] Position = new float[3], Vel = new float[3];
        public float Pitch, Energy;
        public bool Contact, Jetting, Crouch;
        public uint Traction7;                                    // unit(7) raw
        public int JumpContact;                                   // int(4)
    }

    // Player_readControlState 0x00413b90 on the client copy (docs/client-net.md 4.4): the server state replaces the
    // predicted one; the transform is rebuilt from the Euler x, y, the new yaw and the position, without a collision
    // query. The sensor state bits are not part of the prediction.
    public void ApplyClientControlState(ClientControl c)
    {
        using var chop = X87.Chop();
        Rotation[2] = c.Yaw;
        if (c.HasPosition)
        {
            for (int i = 0; i < 3; ++i)
                Velocity[i] = c.Vel[i];
            var m = new Transform();
            MissionTransform.EulerToMatrix(Rotation, ref m);
            Transform.M = m.M;
            Transform.Flags = m.Flags | 3;
            for (int i = 0; i < 3; ++i)
                Transform.P[i] = c.Position[i];
            AtRest = false;
            RefileBox();
        }
        Pitch = c.Pitch;
        Energy = c.Energy;
        ContactFlag = c.Contact;
        Jetting = c.Jetting;
        Traction = X87.F32(Div(c.Traction7, 127.0)); // BitStream readFloat(7)
        if (c.Crouch != Crouching)
            StartAnimation(c.Crouch ? 7 : 9);
        Crouching = c.Crouch;
        UpdateBoxHeight();
        TicksSinceJumpContact = c.JumpContact;
        SetRotation(Rotation);
    }

    // src/game/player.cpp Player::writeControlState: 0x00413e08.
    public override void WriteControlState(BitStream bs, ControlContext ctx)
    {
        using var chop = X87.Chop();
        bs.WriteInt((SensorWord >> 24) & 3u, 2); // sensor state (GameBase +0x203, docs/static-sim.md 3.7)
        bs.WriteF32(Rotation[2]);
        if (bs.WriteFlag(MountId == 0))
        {
            // not mounted
            bs.WriteF32s(Transform.P);
            bs.WriteF32s(Velocity);
        }
        bs.WriteF32(Pitch);
        bs.WriteF32(Energy);
        bs.WriteFlag(ContactFlag);
        bs.WriteFlag(Jetting);
        double t = Mul(Traction, 127.0);
        bs.WriteInt((uint)(int)t & 0x7f, 7);
        bs.WriteFlag(Crouching);
        bs.WriteInt((uint)Math.Min(TicksSinceJumpContact, 15), 4);
    }
}
