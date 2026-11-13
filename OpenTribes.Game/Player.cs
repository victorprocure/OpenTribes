// Port of src/game/player.hpp and src/game/player.cpp: the Player (docs/player-sim.md, docs/collision.md 4,
// docs/player-control.md): creation and datablock binding, move credit, the 32 ms step (jump, jet, energy, ground
// control, stepped move with the Player collision callback), the control state. The collision event dispatch of
// player.cpp (GameState::postCollisionEvent, dispatchCollisionEvent) is in Displace.cs. The Chop class of the file is
// X87.Chop; world::x87::f32 is X87.F32, the long double arithmetic is the Fpu double operations in C++ order.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>The Player: the server side object a client controls on foot.</summary>
/// <remarks>See docs/player-sim.md. This part holds creation, the datablock binding, move credit and the 32 ms step. The other parts are in the files named after the Player features.</remarks>
public sealed partial class Player : GameBase
{
    /// <summary>The number of inventory entries (+0x1038).</summary>
    public const int ItemTypes = 200;
    /// <summary>The number of image slots.</summary>
    public const int ImageSlots = 8;
    /// <summary>The collision mask of a live Player (0x0060fb5c).</summary>
    public const uint LiveMask = 0x74e00086u;
    /// <summary>The collision mask of a dead Player (0x0060fb64).</summary>
    public const uint DeadMask = 0x20c00086u;
    /// <summary>The object type of a corpse.</summary>
    public const uint CorpseType = 0x40000000u;

    private const float Dt = 0.032f;          // time slice 0x3d03126f
    private const float Separation = 0.0001f; // 0x0060ff58
    private const uint GravityForce = 1, JetForce = 0x100;
    private const uint NonBlockingTypes = 0x44200000u; // 0x0060ff64: Item, Mine, corpse
    private const uint StepUpTypes = 0x00400006u;      // 0x0060ff68: terrain, interior, moveable

    /// <summary>The PlayerData values the step reads, as the f32 the script path stored (docs/player-sim.md 2.2).</summary>
    public sealed class Data
    {
        /// <summary>The maximum forward speed.</summary>
        public float MaxForwardSpeed { get; set; }
        /// <summary>The maximum backward speed.</summary>
        public float MaxBackwardSpeed { get; set; }
        /// <summary>The maximum side speed.</summary>
        public float MaxSideSpeed { get; set; }
        /// <summary>The mass.</summary>
        public float Mass { get; set; } = 1;
        /// <summary>The drag.</summary>
        public float Drag { get; set; } = 1;
        /// <summary>The horizontal velocity clamp.</summary>
        public float HorzVelClamp { get; set; }
        /// <summary>The jet force.</summary>
        public float JetForce { get; set; }
        /// <summary>The ground force.</summary>
        public float GroundForce { get; set; } = 20;
        /// <summary>The ground traction.</summary>
        public float GroundTraction { get; set; } = 1;
        /// <summary>The jump impulse.</summary>
        public float JumpImpulse { get; set; } = 100;
        /// <summary>The minimum dot product of the surface normal for a jump.</summary>
        public float JumpSurfaceMinDot { get; set; } = 0.2f;
        /// <summary>The jet side force factor.</summary>
        public float MaxJetSideForceFactor { get; set; } = 1;
        /// <summary>The maximum forward velocity while jetting.</summary>
        public float MaxJetForwardVelocity { get; set; } = 20;
        /// <summary>The minimum energy to jet.</summary>
        public float MinJetEnergy { get; set; } = 1;
        /// <summary>The energy drain of jetting.</summary>
        public float JetEnergyDrain { get; set; } = 1;
        /// <summary>The box width.</summary>
        public float BoxWidth { get; set; }
        /// <summary>The box depth.</summary>
        public float BoxDepth { get; set; }
        /// <summary>The box height when standing.</summary>
        public float BoxNormalHeight { get; set; }
        /// <summary>The box height when crouching.</summary>
        public float BoxCrouchHeight { get; set; }
        /// <summary>The minimum speed that causes landing damage.</summary>
        public float MinDamageSpeed { get; set; } = 13;
        /// <summary>The scale of landing damage.</summary>
        public float DamageScale { get; set; } = 0.00899999f;
        /// <summary>The maximum energy.</summary>
        public float MaxEnergy { get; set; }
        /// <summary>The maximum damage.</summary>
        public float MaxDamage { get; set; } = 1;
        /// <summary>True when the Player can crouch.</summary>
        public bool CanCrouch { get; set; }
        /// <summary>The priority of each animation.</summary>
        public int[] AnimPriority { get; set; } = new int[51];
        /// <summary>The direction of each animation.</summary>
        public int[] AnimDirection { get; set; } = new int[51];
        /// <summary>The duration of each animation.</summary>
        public float[] AnimDuration { get; set; } = new float[51];
        /// <summary>True for each cyclic sequence (sequence +4): the position wraps instead of stopping.</summary>
        public bool[] AnimCyclic { get; set; } = new bool[51];
        /// <summary>The sequence handle of each animation in the armor shape (+0x88c).</summary>
        public int[] AnimSequence { get; set; } = new int[51];
        /// <summary>The view flags of each animation. Bit 0 is first person.</summary>
        public int[] AnimViewFlags { get; set; } = new int[51];

        /// <summary>Copies the values.</summary>
        /// <returns>The copy.</returns>
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

    /// <summary>A force of the SimMovement force list.</summary>
    public sealed class Force
    {
        /// <summary>The force type: 1 gravity, 0x100 jet.</summary>
        public uint Type { get; set; }
        /// <summary>True when the force is an acceleration.</summary>
        public bool Acceleration { get; set; }
        /// <summary>The stored acceleration. Entries that are not accelerations are multiplied by the inverse mass.</summary>
        public float[] V { get; set; } = new float[3];
    }

    /// <summary>The image state: reset.</summary>
    public const int Reset = 0;
    /// <summary>The image state: fire.</summary>
    public const int Fire = 1;
    /// <summary>The image state: no ammo.</summary>
    public const int NoAmmo = 2;
    /// <summary>The image state: idle.</summary>
    public const int Idle = 3;
    /// <summary>The image state: ready.</summary>
    public const int Ready = 4;
    /// <summary>The image state: reload.</summary>
    public const int Reload = 5;
    /// <summary>The image state: spin up.</summary>
    public const int SpinUp = 6;
    /// <summary>The image state: spin down.</summary>
    public const int SpinDown = 7;
    /// <summary>The image state: activate.</summary>
    public const int Activate = 8;
    /// <summary>The image state: deactivate (the state names are the table at 0x0060ffec).</summary>
    public const int Deactivate = 9;

    /// <summary>The ItemImageData values the state machine reads (docs/datablocks.md 5.15), cached when the image is set.</summary>
    public sealed class ImageInfo
    {
        /// <summary>The weapon type (+0x54), -1 for none.</summary>
        public int WeaponType { get; set; } = -1;
        /// <summary>The ItemData index of the ammo (+0x58), -1 for none.</summary>
        public int AmmoType { get; set; } = -1;
        /// <summary>The activate time (+0x5c).</summary>
        public float ActivateTime { get; set; } = 0.5f;
        /// <summary>The fire time (+0x60).</summary>
        public float FireTime { get; set; } = 0.2f;
        /// <summary>The reload time (+0x64).</summary>
        public float ReloadTime { get; set; } = 1.0f;
        /// <summary>The spin up time (+0x68).</summary>
        public float SpinUpTime { get; set; } = 2.0f;
        /// <summary>The spin down time (+0x6c).</summary>
        public float SpinDownTime { get; set; } = 2.0f;
        /// <summary>The projectile datablock group (projectileType, +0x70), -1 for none.</summary>
        public int ProjGroup { get; set; } = -1;
        /// <summary>The projectile datablock index, -1 for none.</summary>
        public int ProjIndex { get; set; } = -1;
        /// <summary>The minimum energy to fire (+0x78).</summary>
        public float MinEnergy { get; set; } = 2.0f;
        /// <summary>The maximum energy of a shot (+0x7c).</summary>
        public float MaxEnergy { get; set; } = 10.0f;
        /// <summary>True for accumulating fire (+0xb5).</summary>
        public bool AccuFire { get; set; }
        /// <summary>The mount point (+0x38).</summary>
        public int MountPoint { get; set; } = 2;
        /// <summary>The mount offset (+0x3c).</summary>
        public float[] MountOffset { get; } = new float[3];
        /// <summary>The mount rotation (+0x48).</summary>
        public float[] MountRotation { get; } = new float[3];
        /// <summary>The image shape file (+0xb8).</summary>
        public string? ShapeFile { get; set; }
        /// <summary>The ItemImageData block name.</summary>
        public string? Name { get; set; }

        /// <summary>Resets the values, as <c>info = ImageInfo{}</c> does.</summary>
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

    /// <summary>The state of one image slot (Player::ImageSlot).</summary>
    /// <remarks>It is reset in place (<c>images[slot] = ImageSlot{}</c>), so that a reference held across a reset sees the reset slot as the C++ reference does.</remarks>
    public sealed class ImageSlot
    {
        /// <summary>The image state (+0x00).</summary>
        public int State { get; set; }
        /// <summary>The ItemData index (+0x04), -1 for none.</summary>
        public int Item { get; set; } = -1;
        /// <summary>The ItemImageData index (+0x08), -1 for none.</summary>
        public int Image { get; set; } = -1;
        /// <summary>The team (+0x0c).</summary>
        public int Team { get; set; } = -1;
        /// <summary>The next item (+0x10), -2 for none.</summary>
        public int NextItem { get; set; } = -2;
        /// <summary>The next team (+0x14).</summary>
        public int NextTeam { get; set; } = -1;
        /// <summary>The state timer (+0x18).</summary>
        public float Timer { get; set; }
        /// <summary>The fire count (+0x1c).</summary>
        public int FireCount { get; set; }
        /// <summary>True while the trigger is down.</summary>
        public bool Triggered { get; set; }
        /// <summary>True when ammo is available.</summary>
        public bool Ammo { get; set; }
        /// <summary>The object id of a live sustained projectile (+0x28).</summary>
        public int Projectile { get; set; }
        /// <summary>The cached image values.</summary>
        public ImageInfo Info { get; } = new();

        /// <summary>Resets the slot in place.</summary>
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

    /// <summary>The shape pose of the last Player_updateAnimation: the thread state behind the node entries until the next animate (docs/player-sim.md 5.5).</summary>
    public sealed class ShapePose
    {
        /// <summary>The animation of the animation thread, -1 for sequence 0.</summary>
        public int AnimIndex { get; set; } = -1;
        /// <summary>The animation thread position.</summary>
        public float AnimPosition { get; set; }
        /// <summary>True when the look thread is on <c>crouch looks</c>.</summary>
        public bool CrouchLooks { get; set; }
        /// <summary>False until the first update: the look thread is on sequence 0.</summary>
        public bool LookSet { get; set; }
        /// <summary>The look thread position before the sequence normalises it.</summary>
        public float LookPosition { get; set; }
        /// <summary>True for first person (instance +0x9e = 2, look thread priority -1).</summary>
        public bool FirstPerson { get; set; } = true;
    }

    // src/game/player.hpp Player::Contact.
    private struct Contact
    {
        [System.Diagnostics.CodeAnalysis.UnscopedRef] public ref Float3 N => ref _n;
        private Float3 _n;
        public float UpDot { get; set; }
        public float StepHeight { get; set; }
        [System.Diagnostics.CodeAnalysis.UnscopedRef] public ref CollisionSurface Surface => ref _surface;
        private CollisionSurface _surface;
        public object? Object { get; set; }
        public uint Type { get; set; }
    }

    /// <summary>True when the Player is dead (+0x1019).</summary>
    public bool Dead { get; set; }
    /// <summary>True when the Player was blown up (+0xf94).</summary>
    public bool BlownUp { get; set; }
    /// <summary>The value of the last applyDamage call.</summary>
    public float LastDamage { get; set; }
    /// <summary>The weapon pullback (+0xfd0).</summary>
    public float Pullback { get; set; }
    /// <summary>True when rays skip the Player (image +0xd). Set by Player_kill (0x004111e8) and never cleared (+0x7f9).</summary>
    public bool RaySkip { get; set; }
    /// <summary>The shape pose.</summary>
    public ShapePose Pose { get; } = new();
    /// <summary>The item counts.</summary>
    public int[] Counts { get; } = new int[ItemTypes];
    /// <summary>The inventory slot of each item type.</summary>
    public int[] SlotOf { get; } = new int[ItemTypes];
    /// <summary>The image slots.</summary>
    public ImageSlot[] Images { get; } = new ImageSlot[ImageSlots];

    /// <summary>The PlayerData values the step reads.</summary>
    public Data PData { get; set; } = new();
    /// <summary>The pitch (+0xfc4).</summary>
    public float Pitch { get; set; }
    /// <summary>The mass (+0x170).</summary>
    public float Mass { get; set; } = 9;
    /// <summary>The inverse mass (+0x174).</summary>
    public float InvMass { get; set; }
    /// <summary>True when a contact surface was hit this step (+0x1f4).</summary>
    public bool ContactFlag { get; set; }
    /// <summary>The rest flag (+0x164 bit 0).</summary>
    public bool RestFlag { get; set; } = true;
    /// <summary>The ground traction (+0xfcc).</summary>
    public float Traction { get; set; }
    /// <summary>The normal of the jump contact (+0xfb0).</summary>
    public float[] JumpNormal { get; } = [0, 0, 1];
    /// <summary>The ticks since the jump contact (+0xfac).</summary>
    public int TicksSinceJumpContact { get; set; } = 100;
    /// <summary>The ticks since a contact (+0xfc0).</summary>
    public int TicksSinceContact { get; set; }
    /// <summary>True while jetting (+0xfe4).</summary>
    public bool Jetting { get; set; }
    /// <summary>The time of the last jet (+0xfe8).</summary>
    public uint LastJetTime { get; set; }
    /// <summary>True while falling (+0xfec).</summary>
    public bool Falling { get; set; }
    /// <summary>The PDA flag (+0xfed).</summary>
    public bool PdaFlag { get; set; }
    /// <summary>True while crouching (+0xfee).</summary>
    public bool Crouching { get; set; }
    /// <summary>The damage flash (+0xfbc).</summary>
    public float Flash { get; set; }
    /// <summary>The ticks without a move (+0xf8c).</summary>
    public int TicksWithoutMove { get; set; }
    /// <summary>The move credit (+0xf90).</summary>
    public int Credit { get; set; }
    /// <summary>The last move (+0xff0).</summary>
    public ref Move LastMove => ref _lastMove;
    private Move _lastMove = new();
    /// <summary>True when an AI controls the Player (+0x101b).</summary>
    public bool AiControlled { get; set; }
    /// <summary>True for the client's own control object (+0x1018, vtable +0x144; docs/client-net.md 4.4).</summary>
    public bool LocallyControlled { get; set; }
    /// <summary>True while inside the mission area (+0x101a).</summary>
    public bool InMissionArea { get; set; } = true;
    /// <summary>The <c>$pref::freeLook</c> value (0060fb50) as of onAdd and the last tick (+0x7dc).</summary>
    public bool FreeLookCopy { get; set; } = true;
    /// <summary>The pending animation (+0xf98).</summary>
    public int PendingAnim { get; set; }
    /// <summary>The current animation (+0xf9c), -1 for none.</summary>
    public int CurrentAnim { get; set; } = -1;
    /// <summary>The animation thread position.</summary>
    public float AnimPosition { get; set; }
    /// <summary>The detect haze factor (+0xfd8).</summary>
    public float DetectHazeFactor { get; set; } = 0.0287f;
    /// <summary>The detect minimum visibility (+0xfdc).</summary>
    public float DetectMinVisibility { get; set; } = 1.0f;
    /// <summary>The sensor suppression (+0xfe0).</summary>
    public float SensorSuppression { get; set; }
    /// <summary>The minimum of the movement box (+0x1d4).</summary>
    public float[] BoxMin { get; } = new float[3];
    /// <summary>The maximum of the movement box (+0x1e0).</summary>
    public float[] BoxMax { get; } = new float[3];
    /// <summary>The maximum z of box B, which follows the crouch (+0x864).</summary>
    public float BoxHeight { get; set; }
    /// <summary>The radius (+0x874).</summary>
    public float Radius { get; set; }
    /// <summary>The object type bits (+0x48).</summary>
    public uint ObjectTypeBits { get; set; } = ObjectType.PlayerType;
    /// <summary>The collision mask (+0x1ec).</summary>
    public uint CollisionMask { get; set; } = LiveMask;
    /// <summary>The time of death (+0x1024).</summary>
    public float DeathTime { get; set; }

    /// <summary>The id of the mount object, 0 for none (+0x1028). Mounting is in docs/vehicle-sim.md 2 and docs/player-sim.md 2.7.</summary>
    public int MountId { get; set; }
    /// <summary>The mount point (+0x102c).</summary>
    public int MountPoint { get; set; }

    private readonly GameState _game;
    private readonly List<Force> _forces = [];
    private readonly SteppedMove _move = new();
    private object? _terrainContainer;  // terrain entry the gravity came from
    private float _containerDrag;       // SimMovement +0x124, set on container entry
    private object? _parent;            // container object holding the player
    private bool _inContainer;
    private bool _added;

    private static ClassContext OwnContext(GameState game) => new() { Game = game, Resources = game.Resources };

    /// <summary>Creates a Player.</summary>
    /// <param name="game">The game state.</param>
    public Player(GameState game) : base("Player", Kind.ShapeBase, 3, [], OwnContext(game))
    {
        _game = game;
        for (var i = 0; i < ImageSlots; ++i)
            Images[i] = new ImageSlot();
        for (var i = 0; i < ItemTypes; ++i)
            SlotOf[i] = -1;
        DamageState = Enabled; // ShapeBase getDamageState: always Enabled
        _move.OnCollision = ProcessCollision;
        _move.OnTouch = owner => PostCollision(owner as SimObject, this);
    }

    /// <summary>Returns the game state.</summary>
    /// <returns>The game state.</returns>
    public GameState Game() => _game;

    /// <summary>Returns the Player behind a sim object.</summary>
    /// <param name="obj">The object, or null.</param>
    /// <returns>The Player, or null.</returns>
    public static Player? From(SimObject? obj) => obj as Player;

    /// <summary>Returns the force list.</summary>
    /// <returns>The forces.</returns>
    public IReadOnlyList<Force> Forces() => _forces;

    // src/game/player.cpp fieldFloat.
    private static float FieldFloat(DataBlockManager.Block b, string name, float def)
    {
        var f = b.Find(name);
        if (f == null || f.Elems.Count == 0)
            return def;
        if (f.Elems[0].Set)
            return f.Elems[0].F[0];
        var v = def;
        if (f.Def.DefaultText.Length != 0)
            v = DataBlockManager.CtorFloat(f.Def.DefaultText);
        return v;
    }

    // src/game/player.cpp Player::block.
    private DataBlockManager.Block? Block()
    {
        if (_game.Sim == null || DataBlockName == null)
            return null;
        var b = _game.Sim.DataBlocks().Find(DataBlockName);
        return b != null && b.Group == 3 ? b : null;
    }

    // ---- creation (docs/player-sim.md 1.3, 1.4) ----

    /// <inheritdoc/>
    /// <remarks>Follows 0x0040fb9c.</remarks>
    public override bool OnAdd()
    {
        // GameBase_onAdd (0x00448108): the player has no group, so the team stays -1; the datablock resolves
        // (Player_onNewDataBlock, then the script onInit), then onAdd.
        if (DataBlockName == null || !_game.ResolveDataBlock(this) || !BindDataBlock())
            return false;
        _added = true;
        _game.ScriptCallback(this, "onInit", []);
        _game.ScriptCallback(this, "onAdd", []);
        _game.JoinSet(this, 719);
        CollisionMask = LiveMask;
        float[] p = [Transform.P[0], Transform.P[1], Transform.P[2]];
        SetPosition(p);
        FreeLookCopy = FreeLookPref();
        return true;
    }

    // Global 0060fb50 ($pref::freeLook, default TRUE; an unset variable keeps the default).
    private bool FreeLookPref()
    {
        if (_game.Console == null)
            return true;
        var v = _game.Console.GetVariable("$pref::freeLook", out var found);
        return !found || Numbers.ParseBool(v);
    }

    /// <inheritdoc/>
    /// <remarks>Follows 0x0040fb30.</remarks>
    public override void OnRemove()
    {
        _game.SensorJam(this, false); // Player_onRemove 0x0040fb30 unlinks, the radius stays (docs/static-sim.md 3.8)
        for (var slot = 0; slot < ImageSlots; ++slot)
            ResetImage(slot);
        // GameBase_onRemove (0x00448180): the script onRemove, then the container.
        if (_added && DataBlockName != null)
            _game.ScriptCallback(this, "onRemove", []);
        if (_inContainer)
            _game.World.Remove(this);
        _inContainer = false;
        _added = false;
        // SimMovement dtor (0x0050471e): a deleted Player leaves the carry list of the Moveable it stood on. The lists
        // hold ids here, and the next Player gets the freed id.
        DetachAll();
        _game.DropTriggerOccupant(this);
    }

    /// <summary>Reads the PlayerData values that the step and the contact response read through the datablock pointer.</summary>
    /// <param name="b">The datablock.</param>
    /// <param name="d">The values to fill.</param>
    /// <remarks>Follows 0x00415304 and 0x00416e4c. A redeclaration of the block changes a bound Player at its next step (docs/player-sim.md 9.5 <c>clamp</c>). Mass, drag, the box and the energy and damage limits are copied into the object at bind (1.4) and stay.</remarks>
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

    /// <summary>Binds or rebinds the datablock.</summary>
    /// <returns>False when the name is no PlayerData.</returns>
    /// <remarks>Follows 0x0040fcdc.</remarks>
    public bool BindDataBlock()
    {
        var b = Block();
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
        if (_game.Resources != null && _game.Sim!.DataBlocks().GetField(b, "shapeFile", "", out var shape) && shape.Length != 0)
            shapeAnim = Shape.GetShapeAnim(_game.Resources, shape);
        if (b.Find("animData") is DataBlockManager.Field af)
            for (var i = 0; i < af.Elems.Count && i < 51; ++i)
            {
                d.AnimDirection[i] = af.Elems[i].I2;
                d.AnimPriority[i] = af.Elems[i].I4;
                // A missing sequence gives handle 0 (1.4 step 7).
                var seq = shapeAnim != null && af.Elems[i].Str != null ? shapeAnim.FindSequence(af.Elems[i].Str!) : -1;
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
        if (_game.World.Find(this) is Container.Entry e)
        {
            Span<float> box = stackalloc float[6];
            WorldBox(box);
            _game.World.SetBox(this, box);
            e.Type = ObjectTypeBits;
        }
        else
        {
            var entry = new Container.Entry(this, ObjectTypeBits, default, false, null);
            WorldBox(entry.Box);
            // Line image (collision.md 9, 0x0043599c): the segment goes into the space of the image transform (the object
            // transform, yaw included) and meets box B there (m_Box3F_collideLine). The position is (end - start) * t +
            // start on the world segment, one f32 store per component; the normal is (0, 0, 1).
            entry.Cast = (s, en, ref hit, ref n) =>
            {
                if (RaySkip)
                    return false; // image byte +0xd: rays skip a player that was killed
                using var c = X87.Chop();
                Span<float> a = [s.X, s.Y, s.Z];
                Span<float> bb = [en.X, en.Y, en.Z];
                var toWorld = new TMat3
                {
                    Flags = Transform.Flags,
                    M = Transform.M,
                    P = Transform.P
                };
                Shape.TmatInverse(toWorld, out var toLocal);
                Span<float> la = stackalloc float[3], lb = stackalloc float[3];
                X87.MulPoint(a, toLocal.M, toLocal.P, la);
                X87.MulPoint(bb, toLocal.M, toLocal.P, lb);
                Span<float> mx = [BoxMax[0], BoxMax[1], BoxHeight];
                var t = 0.0f;
                if (!Shape.BoxCollideLine(la, lb, BoxMin, mx, ref t))
                    return false;
                float At(int i, ReadOnlySpan<float> aa, ReadOnlySpan<float> b2) => X87.F32(Add(Mul(Sub(b2[i], aa[i]), t), aa[i]));
                hit = new Vec3(At(0, a, bb), At(1, a, bb), At(2, a, bb));
                n = new Vec3(0, 0, 1);
                return true;
            };
            _game.World.Add(entry);
            _inContainer = true;
        }
        UpdateContainer();
        SetMaskBits(0x100);
        return true;
    }

    /// <summary>Sets the armor.</summary>
    /// <param name="name">The armor datablock name.</param>
    /// <returns>True when the armor was set.</returns>
    /// <remarks>Follows Player::setArmor (0x00420798).</remarks>
    public bool SetArmor(string name)
    {
        // GameBase_setDataBlockName (0x00447d20): a failed resolve restores the old block.
        var old = DataBlockName;
        DataBlockName = StringTable.Global().Intern(name);
        if (!_added)
            return true;
        if (_game.ResolveDataBlock(this) && BindDataBlock())
        {
            _game.ScriptCallback(this, "onInit", []);
            SetMaskBits(0x04);
            return true;
        }
        DataBlockName = old;
        if (_game.ResolveDataBlock(this) && BindDataBlock())
            _game.ScriptCallback(this, "onInit", []);
        return false;
    }

    // src/game/player.cpp Player::recomputeMass: 0x0041a0f8: PlayerData mass plus the masses of the mounted images,
    // summed in slot order in an f32; 0x00504758 rescales the non-acceleration forces.
    private void RecomputeMass()
    {
        using var chop = X87.Chop();
        float sum = 0;
        if (_game.Sim != null)
            foreach (var s in Images)
            {
                if (s.Item < 0)
                    continue;
                var item = _game.Sim.DataBlocks().At(5, s.Item);
                var f = item?.Find("imageType");
                if (f == null || f.Elems.Count == 0 || !f.Elems[0].Set)
                    continue;
                if (_game.Sim.DataBlocks().At(6, f.Elems[0].I) is DataBlockManager.Block image)
                    sum = X87.F32(Add(sum, FieldFloat(image, "mass", 0)));
            }
        var newMass = X87.F32(Add(PData.Mass, sum));
        if (newMass == Mass && InvMass != 0)
            return;
        var oldMass = Mass;
        Mass = newMass;
        InvMass = X87.F32(Div(1.0, Mass));
        if (oldMass != 0)
            foreach (var fc in _forces)
                if (!fc.Acceleration)
                    for (var i = 0; i < 3; ++i)
                        fc.V[i] = X87.F32(Div(Mul(fc.V[i], oldMass), Mass));
    }

    /// <summary>Sets the items of the image slots on the client copy of the control object, as the ghost sends them (docs/game-stream-ghosts.md 3.4, Player_unpackImages).</summary>
    /// <param name="items">The item of each slot.</param>
    /// <remarks>The mass of the prediction is then the server's. The image state machine stays idle (docs/client-net.md 4.4).</remarks>
    public void SetClientItems(ReadOnlySpan<int> items)
    {
        for (var slot = 0; slot < ImageSlots && slot < items.Length; ++slot)
            Images[slot].Item = items[slot];
        RecomputeMass();
    }

    // src/game/player.cpp Player::worldBox: 0x005069fc: the movement box translated, never rotated.
    private void WorldBox(Span<float> output)
    {
        for (var i = 0; i < 3; ++i)
        {
            output[i] = X87.F32(Add(BoxMin[i], Transform.P[i]));
            output[i + 3] = X87.F32(Add(BoxMax[i], Transform.P[i]));
        }
    }

    // src/game/player.cpp Player::updateBoxHeight: 0x004104fc.
    private void UpdateBoxHeight() => BoxHeight = Crouching ? PData.BoxCrouchHeight : PData.BoxNormalHeight;

    // ---- forces and containers (2.6) ----

    /// <summary>Sets a force in the SimMovement force list.</summary>
    /// <param name="type">The force type.</param>
    /// <param name="v">The force vector.</param>
    /// <param name="acceleration">True when the vector is an acceleration.</param>
    /// <remarks>Follows 0x005047f0.</remarks>
    public void SetForce(uint type, ReadOnlySpan<float> v, bool acceleration)
    {
        var f = new Force { Type = type, Acceleration = acceleration };
        f.V[0] = v[0];
        f.V[1] = v[1];
        f.V[2] = v[2];
        if (!acceleration)
            for (var i = 0; i < 3; ++i)
                f.V[i] = X87.F32(Mul(InvMass, f.V[i]));
        for (var i = 0; i < _forces.Count; ++i)
            if (_forces[i].Type == type)
            {
                _forces[i] = f;
                return;
            }
        _forces.Add(f);
    }

    /// <summary>Removes a force from the force list.</summary>
    /// <param name="type">The force type.</param>
    /// <remarks>Follows 0x005048f4.</remarks>
    public void RemoveForce(uint type)
    {
        for (var i = 0; i < _forces.Count; ++i)
            if (_forces[i].Type == type)
            {
                _forces.RemoveAt(i);
                return;
            }
    }

    /// <summary>Refiles the container entry with the new world box at once.</summary>
    /// <remarks>Follows SimObject::setTransform (0x005054a4). The container change (gravity force) is seen by the next step's updateContainer.</remarks>
    public void RefileBox()
    {
        if (!_inContainer)
            return;
        Span<float> box = stackalloc float[6];
        WorldBox(box);
        // SimObject_setTransformRaw (0x005054a4) -> SimContainer_setBox (0x004f9a38) runs the database update for an
        // unchanged box too: in the terrain database the object's box ends then move in front of equal ends (the
        // findObjects order of objects with the same max x).
        if (_game.World.Find(this) != null)
            _game.World.SetBox(this, box);
    }

    /// <summary>Places the Player on the client ghost without a collision query: the transform is rebuilt from the Euler angles and the position.</summary>
    /// <param name="position">The position.</param>
    /// <remarks>Follows SimObject_setTransformRaw (Player_applyControlState 00414a00). The container entry takes the new box.</remarks>
    public void PlaceRaw(ReadOnlySpan<float> position)
    {
        using var chop = X87.Chop();
        var m = new Transform();
        MissionTransform.EulerToMatrix(Rotation, ref m);
        Transform.M = m.M;
        Transform.Flags = m.Flags | 3;
        for (var i = 0; i < 3; ++i)
            Transform.P[i] = position[i];
        AtRest = false;
        RefileBox();
    }

    /// <summary>Updates the container entry and the gravity of the terrain container (docs/player-sim.md FC2 to FC5).</summary>
    public void UpdateContainer()
    {
        if (!_inContainer)
            return;
        Span<float> box = stackalloc float[6];
        WorldBox(box);
        var own = _game.World.Find(this);
        if (own == null)
            return;
        _game.World.SetBox(this, box);
        // FC2/FC3: entering a container inside the terrain adds its gravity, leaving removes it; a move between nested
        // containers does both, which re-appends the entry (FC5).
        var terrain = _game.World.TerrainOf(this);
        var parent = _game.World.ParentOf(this);
        if (parent == _parent && terrain == _terrainContainer)
            return;
        if (_terrainContainer != null)
            RemoveForce(GravityForce);
        // SimTerrain_enterContainer (0x005195b4): the gravity force and the container drag (+0x124) are taken on entry.
        // Leaving (0x0051963c) removes the force only, the drag stays.
        if (terrain != null)
        {
            SetForce(GravityForce, terrain.Gravity, true);
            _containerDrag = terrain.Drag;
        }
        _parent = parent;
        _terrainContainer = terrain;
    }

    /// <summary>Returns the item counts of a Player control object, or of the Player an ObserverCamera orbits.</summary>
    /// <param name="control">The control object.</param>
    /// <returns>The counts, or null otherwise.</returns>
    public static int[]? InventoryCounts(GameBase? control)
    {
        if (From(control) is Player p)
            return p.Counts;
        if (control is ObserverCamera cam && From(cam.OrbitObject) is Player o)
            return o.Counts;
        return null;
    }

    // ---- moves (2.1) ----

    /// <inheritdoc/>
    /// <remarks>Follows 0x00413f18.</remarks>
    public override void ApplyMoves(Move[] moves, int count)
    {
        if (Dead)
            return;
        var m = Manager();
        var self = Id();
        for (var i = 0; i < count; ++i)
        {
            if (Credit > 5)
                break;
            ++Credit;
            var mv = moves[i];
            if (mv.UseItem != -1 && _game.Console != null)
                _game.Console.CallFunction("remoteUseItem", [Numbers.FormatInt(Id()), Numbers.FormatInt(mv.UseItem)]);
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
            RepairStep(Dt); // ShapeBase_repairTick (0x0044b610) before every processed move (6.3)
            UpdateMove(LastMove);
            UpdateAnimation(Dt);
            if (!AiControlled)
                LastMove = mv;
        }
        if (!(MountId != 0 && MountPoint == 1))
            SetMaskBits(0x2000); // TK7: not for a pilot
        TicksWithoutMove = 0;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 0x00415108.</remarks>
    public override void ServerTick(uint tickMs)
    {
        if (!_added)
            return;
        if (Credit < -25 || Dead || (!AiControlled && ControlClient == 0))
        {
            LastMove = new Move();
            TicksWithoutMove = 0;
            RepairStep(Dt); // before the server tick's null move (0x0041514a)
            UpdateMove(LastMove);
            UpdateAnimation(Dt);
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
        var freeLook = FreeLookPref();
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
            var r = _game.Players.FindHuman(ControlClient);
            PdaFlag = !(r != null && r.GuiMirror == 1);
        }
        // Mission area (6.6).
        float[] area = [0, 0, 0, 0];
        _game.Players.MissionArea(area);
        using var chop = X87.Chop();
        var inside = Transform.P[0] >= area[0] && X87.F32(Add(area[0], area[2])) >= Transform.P[0] && Transform.P[1] >= area[1] &&
                      X87.F32(Add(area[1], area[3])) >= Transform.P[1];
        if (inside != InMissionArea && _game.Console != null)
        {
            var line = "Player::" + (inside ? "enter" : "leave") + "MissionArea(" + Numbers.FormatInt(Id()) + ");";
            InMissionArea = inside;
            _game.Console.Evaluate(line);
        }
    }

    // ---- the step (2.3, 2.4) ----

    private static float MaxF(float a, float b) => a < b ? b : a; // std::max

    /// <summary>Runs one 32 ms step with a move.</summary>
    /// <param name="mv">The move.</param>
    /// <remarks>Follows 0x00415304.</remarks>
    public void UpdateMove(Move mv)
    {
        using var chop = X87.Chop();
        if (Block() is DataBlockManager.Block blk)
            ReadLiveData(blk, PData);
        var d = PData;
        const float dt = Dt;
        var v = Velocity;
        UpdateContainer(); // a script setPosition since the last step (RA7, FC5)
        float[] r0 = [Transform.M[0], Transform.M[1], Transform.M[2]];
        float[] r1 = [Transform.M[3], Transform.M[4], Transform.M[5]];
        float[] r2 = [Transform.M[6], Transform.M[7], Transform.M[8]];

        // S1 item images (7.5): slots 0..7 in order, dt 0.032.
        {
            var mgr = Manager();
            var self = Id();
            for (var slot = 0; slot < ImageSlots; ++slot)
            {
                UpdateImageState(slot, dt);
                if (mgr == null || mgr.FindById(self) != this)
                    return; // an image script deleted the player
            }
        }

        // MV1 desired speed.
        var fwd = X87.F32(Sub(Mul(mv.Forward, d.MaxForwardSpeed), Mul(mv.Backward, d.MaxBackwardSpeed)));
        var side = X87.F32(Mul(Sub(mv.Right, mv.Left), d.MaxSideSpeed));
        var speed = X87.F32(Sqrt(X87.F32(Add(Mul(fwd, fwd), Mul(side, side)))));
        var cap = fwd > 0.0f ? MaxF(d.MaxForwardSpeed, d.MaxSideSpeed) : MaxF(d.MaxBackwardSpeed, d.MaxSideSpeed);
        if (speed > cap)
        {
            var k = X87.F32(Div(cap, speed));
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
            for (var i = 0; i < 3; ++i)
                output[i] = X87.F32(Add(X87.F32(Mul(a, r0[i])), X87.F32(Mul(b, r1[i]))));
        }

        // MV2 jump.
        // A ghost jumps only when it is the client's own control object (Player_updateMove 0x00415304).
        if (mv.Jump && MountId == 0 && !Dead && CurrentAnim != 15 && TicksSinceJumpContact < 8 && (!ClientSide || LocallyControlled))
        {
            TicksSinceJumpContact = 8;
            if (CurrentAnim != 6)
                StartAnimation(6);
            var dir = new float[3];
            MoveDirection(dir);
            var l = X87.F32(X87.Len(dir));
            var along = 0.0f;
            if (l >= 0.01)
            {
                var k = X87.F32(Div(1.0, X87.F32(X87.Len(dir))));
                for (var i = 0; i < 3; ++i)
                    dir[i] = X87.F32(Mul(k, dir[i]));
                along = X87.F32(X87.Dot(dir, JumpNormal));
            }
            float[] impulse = [0, 0, X87.F32(Mul(JumpNormal[2], d.JumpImpulse))];
            if (along > 0.0f)
            {
                var k = X87.F32(Mul(along, d.JumpImpulse));
                impulse[0] = X87.F32(Mul(k, dir[0]));
                impulse[1] = X87.F32(Mul(k, dir[1]));
            }
            for (var i = 0; i < 3; ++i)
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
            var l = 0.0f;
            float[] dir = [0, 0, 0];
            if (TicksSinceJumpContact > 8)
            {
                MoveDirection(dir);
                l = X87.F32(X87.Len(dir));
            }
            float[] force = [0, 0, d.JetForce];
            if (l > 0.01)
            {
                var k1 = X87.F32(Div(1.0, l));
                for (var i = 0; i < 3; ++i)
                    dir[i] = X87.F32(Mul(k1, dir[i]));
                var fv = X87.F32(X87.Dot(dir, v));
                var k = fv > d.MaxJetForwardVelocity ? 0.0f : fv < 0.0f ? 1.0f : X87.F32(Sub(1.0, Div(fv, d.MaxJetForwardVelocity)));
                if (k > d.MaxJetSideForceFactor)
                    k = d.MaxJetSideForceFactor;
                var upForce = X87.F32(Mul(Sub(1.0, k), d.JetForce));
                var sideForce = X87.F32(Mul(k, d.JetForce));
                for (var i = 0; i < 3; ++i)
                    force[i] = X87.F32(Add(Mul(upForce, r2[i]), X87.F32(Mul(sideForce, dir[i]))));
            }
            SetForce(JetForce, force, false);
        }
        else
        {
            Jetting = false;
            float[] zero = [0, 0, 0];
            SetForce(JetForce, zero, false);
        }

        // S5 yaw and pitch (section 4).
        {
            const double twoPi = 6.283185307179586;
            var yaw = X87.F32(Add(mv.Turn, Rotation[2]));
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
        var desired = new float[3];
        for (var i = 0; i < 3; ++i)
            desired[i] = X87.F32(Add(Add(Mul(side, r0[i]), Mul(fwd, r1[i])), Mul(0.0, r2[i])));
        float[] vOld = [v[0], v[1], v[2]];
        foreach (var fc in _forces)
            for (var i = 0; i < 3; ++i)
                v[i] = X87.F32(Add(Mul(fc.V[i], dt), v[i]));
        if (_containerDrag != 0.0f)
        {
            var c = X87.F32(Mul(Mul(d.Drag, _containerDrag), dt));
            for (var i = 0; i < 3; ++i)
                v[i] = X87.F32(Sub(v[i], Mul(v[i], c)));
        }
        if (d.HorzVelClamp > 3.0f)
        {
            var h = X87.F32(Sqrt(X87.F32(Add(Mul(v[0], v[0]), Mul(v[1], v[1])))));
            if (h > d.HorzVelClamp)
            {
                var k = X87.F32(Div(d.HorzVelClamp, h));
                v[0] = X87.F32(Mul(k, v[0]));
                v[1] = X87.F32(Mul(k, v[1]));
            }
        }

        // MV5 ground control.
        if (ContactFlag)
        {
            float[] diff = [X87.F32(Sub(desired[0], v[0])), X87.F32(Sub(desired[1], v[1])), 0.0f];
            var dl = X87.F32(X87.Len(diff));
            var t = X87.F32(Mul(Traction, d.GroundTraction));
            if (t > 1.0f)
                t = 1.0f;
            var maxDv = X87.F32(Mul(Mul(Div(d.GroundForce, Mass), t), dt));
            if (dl > maxDv)
            {
                var k = X87.F32(Div(maxDv, dl));
                for (var i = 0; i < 3; ++i)
                    diff[i] = X87.F32(Mul(k, diff[i]));
                RestFlag = false;
            }
            else
            {
                RestFlag = true;
            }
            for (var i = 0; i < 3; ++i)
                v[i] = X87.F32(Add(diff[i], v[i]));
        }
        else
        {
            RestFlag = X87.Len(vOld) < 0.1;
        }

        // MV6 target and traction decay.
        var half = X87.F32(Mul(dt, 0.5f));
        var target = new float[3];
        for (var i = 0; i < 3; ++i)
            target[i] = X87.F32(Add(Mul(Add(vOld[i], v[i]), half), Transform.P[i]));
        var saved = X87.F32(Mul(Traction, 0.6));
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
        var v = Velocity;
        var rot = new Transform();
        float[] euler = [Rotation[0], Rotation[1], Rotation[2]];
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
        _move.Image.Rot = rot.M;
        _move.Image.BoxMin = Float3.Of(BoxMin);
        _move.Image.BoxMax = Float3.Of(BoxMax);
        _move.Image.Radius = Radius;
        _move.Dt = Dt;
        BuildCandidates(target);
        float[] pos = [Transform.P[0], Transform.P[1], Transform.P[2]];
        float[] goal = [target[0], target[1], target[2]];
        var moved = _move.Run(pos, goal, step0, step1);
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
            var notified = new List<object>(_move.Notify);
            BuildCandidates(pos);
            var hits = new List<CollisionHit>();
            for (var it = 0; it < _move.MaxIterations; ++it)
            {
                if (_move.TestAt(pos, hits))
                    break;
                var info = new MoveInfo
                {
                    Iteration = it,
                    Used = 0.0f,
                    EndTime = 1.0f,
                    Current = [pos[0], pos[1], pos[2]],
                    Attempted = [pos[0], pos[1], pos[2]],
                    Target = [pos[0], pos[1], pos[2]],
                    Candidates = _move.Candidates,
                    Hits = hits,
                    Notify = notified
                };
                if (!ProcessCollision(info))
                    break;
            }
            _move.Notify = notified;
        }
        Transform.M = rot.M;
        Transform.Flags = rot.Flags | 3;
        for (var i = 0; i < 3; ++i)
            Transform.P[i] = pos[i];
        // Collision events to both sides for every notified object (0x00507180).
        // The client prediction posts no events: they run server scripts in the prediction world (client-net.md 4.4).
        if (!ClientSide)
            foreach (var other in _move.Notify)
            {
                PostCollision(other as SimObject, this);
                PostCollision(this, other as SimObject);
            }
        UpdateContainer();
        return moved;
    }

    /// <summary>Tests whether the collision image of the Player at a position touches nothing.</summary>
    /// <param name="q">The position.</param>
    /// <returns>True when nothing is touched.</returns>
    public bool TestPositionAt(ReadOnlySpan<float> q)
    {
        using var chop = X87.Chop();
        var rot = new Transform();
        float[] euler = [Rotation[0], Rotation[1], Rotation[2]];
        MissionTransform.EulerToMatrix(euler, ref rot);
        _move.Image.Rot = rot.M;
        _move.Image.BoxMin = Float3.Of(BoxMin);
        _move.Image.BoxMax = Float3.Of(BoxMax);
        _move.Image.Radius = Radius;
        BuildCandidates(q);
        var touch = _move.OnTouch; // a test posts no collision events
        _move.OnTouch = null;
        var hits = new List<CollisionHit>();
        var free = _move.TestAt(q, hits);
        _move.OnTouch = touch;
        return free;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 0x004113e8: (0, 1, 0) pitched by <c>pitch + 0.5 * cos(pitch)</c> and turned by the player matrix (docs/static-sim.md 5.4). 0x00606890 is the C runtime cosine.</remarks>
    public override bool ThrowDirection(Span<float> output)
    {
        using var chop = X87.Chop();
        var c = Math.Cos(Pitch);
        var term = X87.F32(Mul(c, 0.5f));
        var angle = X87.F32(Add(Pitch, term));
        var pitched = new Transform();
        float[] euler = [angle, 0.0f, 0.0f];
        MissionTransform.EulerToMatrix(euler, ref pitched);
        // Row 1 of pitched * transform (0x005ed570), the image of (0, 1, 0).
        for (var j = 0; j < 3; ++j)
            output[j] = X87.F32(Add(Add(Mul(pitched.M[3], Transform.M[j]), Mul(pitched.M[4], Transform.M[3 + j])),
                                    Mul(pitched.M[5], Transform.M[6 + j])));
        return true;
    }

    /// <summary>Displaces a rider or an overlapped Player by a delta (Moveable carry, docs/collision.md E3.2). Only terrain and interiors count, with sub-steps from the box extents.</summary>
    /// <param name="delta">The displacement.</param>
    /// <param name="mover">The velocity of the mover.</param>
    /// <returns>True when the whole displacement was done.</returns>
    /// <remarks>Follows 0x005064f8.</remarks>
    public bool PushBy(ReadOnlySpan<float> delta, ReadOnlySpan<float> mover)
    {
        using var chop = X87.Chop();
        var savedMask = CollisionMask;
        CollisionMask = 6;
        // Bits 0x05 of +0x164 are cleared and stay cleared. Bit 0 is the Player's rest flag (CR5): a pushed Player is not
        // stopped by the rest rule of its contact response until its own next move sets the flag.
        RestFlag = false;
        float[] box = [BoxMin[0], BoxMin[1], BoxMin[2], BoxMax[0], BoxMax[1], BoxMax[2]];
        var ok = Displace.PushBy(delta, mover, box, Velocity, ref AtRest, i => Transform.P[i],
                                  (target, step0, step1) => SteppedMoveTo(target, step0, step1, true));
        CollisionMask = savedMask;
        return ok;
    }

    /// <summary>Fills the image of the Player for the overlap test.</summary>
    /// <param name="output">The image to fill.</param>
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
        _move.Candidates.Clear();
        Span<float> query = stackalloc float[6];
        Collision.SweptBox(BoxMin, BoxMax, target, Velocity, Dt, query);
        _game.CollisionCandidates(this, CollisionMask, query, _move.Candidates);
        // Player bind (0x00410f..: image +0x48 = the shape instance, +0x8c = detail 0, mesh mode): sphere and box images
        // are tested against the Player's own shape in its current pose.
        _move.Image.Instance = null;
        _move.Image.Detail = 0;
        _move.Image.MeshMode = true;
        foreach (var image in _move.Candidates)
            if (image.ImageKind is CollisionImage.Kind.TriggerSphere or CollisionImage.Kind.Trigger)
            {
                _move.Image.Instance = ShapeState.ShapeInstanceOf(_game, this);
                break;
            }
    }

    // src/game/player.cpp Player::processCollision: 0x00416598.
    private bool ProcessCollision(MoveInfo info)
    {
        var hits = info.Hits!;
        var notify = info.Notify!;
        // Step 1: Items, Mines and corpses are notified and never block.
        for (var i = 0; i < hits.Count;)
        {
            if ((hits[i].Type & NonBlockingTypes) != 0)
            {
                notify.Add(hits[i].HitObject!);
                hits.RemoveAt(i);
            }
            else
            {
                ++i;
            }
        }
        // Step 2: up = -normalize(gravity).
        float[] up = [0, 0, 1];
        foreach (var f in _forces)
            if (f.Type == GravityForce)
            {
                var l = X87.F32(X87.Len(f.V));
                var k = X87.F32(Div(1.0, l));
                for (var i = 0; i < 3; ++i)
                    up[i] = -X87.F32(Mul(k, f.V[i]));
                break;
            }
        // Step 3: contacts.
        var contacts = new List<Contact>();
        foreach (var h in hits)
        {
            if (!notify.Contains(h.HitObject!))
                notify.Add(h.HitObject!);
            foreach (var s in h.Surfaces)
            {
                var c = new Contact();
                var sn = s.Normal;
                X87.MulVector(sn, h.Rot, c.N);
                c.UpDot = X87.F32(X87.Dot(c.N, up));
                c.StepHeight = h.StepHeight;
                c.Surface = s;
                c.Object = h.HitObject;
                c.Type = h.Type;
                contacts.Add(c);
            }
        }
        // Step 5: step-up.
        if (contacts.Count > 1 && StepUp(info, contacts))
            return true;
        // Step 6: response on every contact in order.
        var any = false;
        foreach (var c in contacts)
            any = ContactResponse(c) || any;
        if (!any)
            return false;
        var rem = X87.F32(Sub(_move.Dt, info.Used));
        for (var i = 0; i < 3; ++i)
            info.Target![i] = X87.F32(Add(X87.F32(Mul(Velocity[i], rem)), info.Current![i]));
        return true;
    }

    // src/game/player.cpp Player::stepUp: 0x00416a64.
    private bool StepUp(MoveInfo info, List<Contact> contacts)
    {
        const double kWall = 0.707; // double at 0x00416de4
        var wall = false;
        foreach (var c in contacts)
            if ((c.Type & StepUpTypes) != 0 && c.UpDot <= kWall)
                wall = true;
        if (!wall)
            return false;
        var best = -1;
        var maxH = 0.0f;
        for (var i = 0; i < contacts.Count; ++i)
        {
            var c = contacts[i];
            if ((c.Type & StepUpTypes) == 0 || !(c.UpDot >= kWall))
                continue;
            var h = c.StepHeight;
            if (h < 0.0f)
                h = X87.F32(Sub(Radius, c.Surface.Distance));
            if (h > maxH)
                maxH = h;
            if (best < 0 || c.UpDot >= contacts[best].UpDot)
                best = i;
        }
        if (best < 0 || maxH > X87.F32(Mul(Radius, 0.6f)))
            return false;
        float[] raised = [info.Attempted![0], info.Attempted[1], X87.F32(Add(Add(maxH, 0.001f), info.Attempted[2]))];
        // Test with a copy of the candidate list (0x00505790); trigger images hit here get their event too.
        var scratch = new List<CollisionHit>();
        var response = contacts[best];
        var probe = new SteppedMove
        {
            Image = _move.Image,
            Candidates = [.. _move.Candidates],
            OnTouch = _move.OnTouch
        };
        if (!probe.TestAt(raised, scratch))
            return false;
        ContactResponse(response);
        var rem = X87.F32(Sub(_move.Dt, info.EndTime));
        for (var i = 0; i < 3; ++i)
        {
            info.Current![i] = raised[i];
            info.Target![i] = X87.F32(Add(X87.F32(Mul(Velocity[i], rem)), raised[i]));
        }
        return true;
    }

    // src/game/player.cpp Player::contactResponse: 0x00416e4c.
    private bool ContactResponse(in Contact c)
    {
        var v = Velocity;
        ContactFlag = true;
        if (c.Surface.Distance < 0.0f)
            return false;
        ReadOnlySpan<float> n = c.N;
        var vn = X87.F32(X87.Dot(v, n));
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
            float[] origin = [0, 0, 0];
            var value = X87.F32(Mul(PData.DamageScale, X87.F32(Sub(-(double)vn, PData.MinDamageSpeed))));
            var wasAlive = !Dead;
            var anim = CurrentAnim;
            ApplyDamage(0, value, origin, OwnerClient);
            if (wasAlive && !Dead && anim != 15)
                StartAnimation(15);
        }
        var k = X87.F32(Add(-(double)vn, Separation));
        for (var i = 0; i < 3; ++i)
            v[i] = X87.F32(Add(X87.F32(Mul(n[i], k)), v[i]));
        if (c.UpDot > 0.0f)
        {
            var t = c.Surface.HasMaterial ? X87.F32(Mul(c.Surface.Friction, c.UpDot)) : c.UpDot;
            Traction = X87.F32(Add(t, Traction));
            if (RestFlag && Mul(t, 0.75f) > X87.Len(v))
                v[0] = v[1] = v[2] = 0.0f;
        }
        // CR6: standing on a Moveable (upDot > 0.985) puts the Player on its carry list (0x005067d0).
        if (c.UpDot > (double)0.985f && (c.Type & ObjectType.MoveableType) != 0 && c.Object is GameBase m)
            Attach(m);
        return true;
    }

    // src/game/player.cpp Player::postCollision.
    private void PostCollision(SimObject? target, SimObject? sender) => _game.PostCollisionEvent(target, sender);

    // ---- animation (5.3, 5.5 server branch) ----

    /// <summary>Sets the animation.</summary>
    /// <param name="index">The animation index.</param>
    /// <remarks>Follows 0x00410568.</remarks>
    public void SetAnimation(int index)
    {
        // The original reads the priority table without a range check (5.9 Q6); an index outside the table is refused
        // here (D11).
        if (index is < 0 or > 50)
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

    /// <summary>Starts an animation.</summary>
    /// <param name="index">The animation index.</param>
    /// <remarks>Follows 0x004105d0.</remarks>
    public void StartAnimation(int index)
    {
        var inCrouch = CurrentAnim is 7 or 8 or (>= 10 and <= 13);
        if ((index == 7 && inCrouch) || (index == 9 && !inCrouch))
            return;
        if (index is < 0 or > 50) // index 51 passes the original's test: off by one, refused here
            return;
        CurrentAnim = index;
        AnimPosition = PData.AnimDirection[index] > 0 ? 0.0f : 0.99f;
    }

    // The 80-bit constant at 0x00411160 rounded to 53 bits (portable build).
    private static readonly double LookScale = 0xa6adbac815dcea70UL * Math.ScaleB(1.0, -64);

    /// <summary>Advances the animation, server branch.</summary>
    /// <param name="dt">The step length in seconds.</param>
    /// <remarks>Follows 0x004108dc.</remarks>
    public void UpdateAnimation(float dt)
    {
        using var chop = X87.Chop();
        // Look thread (0x004108e7..0x00410947): set-sequence resets the position to 0 every call, then the position is
        // set from the pitch unless it is exactly 0.
        {
            var target = X87.F32(Add(Mul(Mul(-(double)Pitch, LookScale), 0.5f), 0.5f));
            Pose.CrouchLooks = Crouching;
            Pose.LookSet = true;
            Pose.LookPosition = target != 0.0f ? X87.F32(Add(X87.F32(Sub(target, 0.0f)), 0.0f)) : 0.0f;
        }
        UpdateAnimationState(dt);
        // Server tail (0x00410acc..0x00410afb): first person flag from the current animation, then the shape is animated.
        if (CurrentAnim is >= 0 and < 51)
        {
            Pose.FirstPerson = (PData.AnimViewFlags[CurrentAnim] & 1) != 0;
            Pose.AnimIndex = CurrentAnim;
        }
        Pose.AnimPosition = AnimPosition;
    }

    /// <summary>Updates the animation state from its state table (docs/player-sim.md 5.5).</summary>
    /// <param name="dt">The step length in seconds.</param>
    public void UpdateAnimationState(float dt)
    {
        var finished = true; // current == -1 reads a direction outside the table, normally 0
        if (CurrentAnim >= 0)
            finished = PData.AnimDirection[CurrentAnim] > 0 ? AnimPosition >= 0.99 : AnimPosition <= 0.01;
        void Advance()
        {
            var duration = CurrentAnim >= 0 ? PData.AnimDuration[CurrentAnim] : 0;
            if (!(duration > 0.0f))
            {
                AnimPosition = 1.0f;
                return;
            }
            var delta = X87.F32(Div(dt, duration));
            AnimPosition = X87.F32(Add(AnimPosition, delta));
            // TSSequence_normalizePosition (0x005b8d94): outside 0..1 a cyclic sequence keeps the fraction, a one-shot
            // sequence stops at the end. A cyclic request therefore finishes only when a step lands in the last
            // hundredth (docs/player-sim.md 5.5).
            if (AnimPosition is < 0.0f or > 1.0f)
            {
                if (CurrentAnim >= 0 && PData.AnimCyclic[CurrentAnim])
                    AnimPosition = X87.F32(Sub(AnimPosition, Math.Floor(AnimPosition)));
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
        var bse = (Crouching && !Jetting && !Falling) ? 8 : 0;
        if (bse != CurrentAnim)
        {
            CurrentAnim = bse;
            AnimPosition = bse == 8 ? 1.0f : 0.0f;
        }
    }

    // ---- damage and death (6.4, 6.5) ----

    /// <summary>Applies landing damage.</summary>
    /// <param name="type">The damage type.</param>
    /// <param name="value">The damage amount.</param>
    /// <param name="pos">The hit position.</param>
    /// <param name="sourceId">The id of the damage source.</param>
    public void ApplyDamage(int type, float value, ReadOnlySpan<float> pos, int sourceId)
    {
        float[] zero = [0, 0, 0];
        ApplyDamage(type, value, pos, zero, zero, sourceId);
    }

    /// <inheritdoc/>
    /// <remarks>Follows 0x00412f94.</remarks>
    public override void ApplyDamage(int type, float value, ReadOnlySpan<float> pos, ReadOnlySpan<float> vec,
                                     ReadOnlySpan<float> mom, int source)
    {
        LastDamage = value;
        var fn = _game.VirtualFunction(this, "onDamage");
        if (fn.Length == 0 || _game.Console == null)
            return;
        using var chop = X87.Chop();
        // Hit location (0x00413074): pos in object space.
        float[] rel = [X87.F32(Sub(pos[0], Transform.P[0])), X87.F32(Sub(pos[1], Transform.P[1])), X87.F32(Sub(pos[2], Transform.P[2]))];
        ReadOnlySpan<float> m = Transform.M;
        float[] local =
        [
            X87.F32(Add(Add(Mul(rel[0], m[0]), Mul(rel[1], m[1])), Mul(rel[2], m[2]))),
            X87.F32(Add(Add(Mul(rel[0], m[3]), Mul(rel[1], m[4])), Mul(rel[2], m[5]))),
            X87.F32(Add(Add(Mul(rel[0], m[6]), Mul(rel[1], m[7])), Mul(rel[2], m[8]))),
        ];
        var b = Block();
        float Pct(string name, float def) => b != null ? FieldFloat(b, name, def) : def;
        var height = Crouching ? PData.BoxCrouchHeight : PData.BoxNormalHeight;
        var torsoTop = X87.F32(Mul(height, Pct(Crouching ? "boxCrouchTorsoPercentage" : "boxNormalTorsoPercentage", 0.333299f)));
        var headBase = X87.F32(Mul(height, Pct(Crouching ? "boxCrouchHeadPercentage" : "boxNormalHeadPercentage", 0.666599f)));
        var vert = local[2] <= torsoTop ? "legs" : local[2] <= headBase ? "torso" : "head";
        string quad;
        if (vert[0] != 'h')
        {
            quad = local[1] >= 0.0f ? (local[0] <= 0.0f ? "front_left" : "front_right")
                                    : (local[0] <= 0.0f ? "back_left" : "back_right");
        }
        else
        {
            float w = X87.F32(Sub(BoxMax[0], BoxMin[0])), dd = X87.F32(Sub(BoxMax[1], BoxMin[1]));
            var back = X87.F32(Mul(Sub(Pct("boxHeadBackPercentage", 0), 0.5), w));
            var front = X87.F32(Mul(Sub(Pct("boxHeadFrontPercentage", 1), 0.5), w));
            var left = X87.F32(Mul(Sub(Pct("boxHeadLeftPercentage", 0), 0.5), dd));
            var right = X87.F32(Mul(Sub(Pct("boxHeadRightPercentage", 1), 0.5), dd));
            var row = local[1] < back ? "back" : local[1] <= front ? "middle" : "front";
            var col = local[0] < left ? "left" : local[0] <= right ? "middle" : "right";
            quad = col + "_" + row;
        }
        // A formatted script line (0x005f4280): every float goes through %g.
        static string G(float v) => Numbers.FormatFloat(v);
        var line = fn + "(" + Numbers.FormatInt(Id()) + "," + Numbers.FormatInt(type) + "," + G(value) + ",\"" + G(pos[0]) +
                      " " + G(pos[1]) + " " + G(pos[2]) + "\",\"" + G(vec[0]) + " " + G(vec[1]) + " " + G(vec[2]) +
                      "\",\"" + G(mom[0]) + " " + G(mom[1]) + " " + G(mom[2]) + "\"," + vert + "," + quad + "," +
                      Numbers.FormatInt(source) + ");";
        _game.Console.Evaluate(line);
    }

    /// <summary>Sets the damage level.</summary>
    /// <param name="level">The new level.</param>
    /// <remarks>Follows ShapeBase_setDamageLevel (0x0044bbdc), then Player_setDamageLevel (0x004111c4).</remarks>
    // (0x004111c4).
    public void SetDamage(float level)
    {
        var value = level < 0.0f ? 0.0f : level >= MaxDamage ? MaxDamage : level;
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
            ObjectTypeBits = ObjectType.PlayerType;
            CollisionMask = LiveMask;
            if (_game.World.Find(this) is Container.Entry e)
                e.Type = ObjectTypeBits;
        }
    }

    /// <inheritdoc/>
    public override void SetDamageLevel(float level) => SetDamage(level);

    /// <inheritdoc/>
    /// <remarks>Follows 0x00411538: four rays from the explosion to the legs, the chest and the two sides, each unobstructed ray adds a quarter.</remarks>
    public override float DamageCoverage(ReadOnlySpan<float> pos)
    {
        using var chop = X87.Chop();
        var h = Radius; // +0x870: half the box height
        float[] p = [Transform.P[0], Transform.P[1], Transform.P[2]];
        float[][] ends = [new float[3], new float[3], new float[3], new float[3]];
        float[] half = [0, 0, X87.F32(Mul(h, 0.5f))];
        for (var i = 0; i < 3; ++i)
            ends[0][i] = X87.F32(Add(p[i], half[i]));
        for (var i = 0; i < 3; ++i)
            ends[1][i] = X87.F32(Add(ends[0][i], i == 2 ? h : 0.0f));
        float[] xs = [X87.F32(Mul(BoxMin[0], 0.5f)), X87.F32(Mul(BoxMax[0], 0.5f))];
        for (var k = 0; k < 2; ++k)
        {
            float[] v = [xs[k], 0, 0];
            var rv = new float[3];
            X87.MulVector(v, Transform.M, rv);
            for (var i = 0; i < 3; ++i)
                ends[2 + k][i] = X87.F32(Add(p[i], rv[i]));
            ends[2 + k][2] = X87.F32(Add(ends[2 + k][2], h));
            ends[2 + k][2] = X87.F32(Add(Mul(h, 0.25f), ends[2 + k][2]));
        }
        var cover = 0.0f;
        foreach (var e in ends)
        {
            object? hit = null;
            Vec3 hp = default, n = default;
            if (!_game.World.CastRay(new Vec3(pos[0], pos[1], pos[2]), new Vec3(e[0], e[1], e[2]), 0x800086u, this, ref hit, ref hp, ref n))
                cover = AddF(cover, 0.25f);
        }
        return cover;
    }

    /// <inheritdoc/>
    /// <remarks>Follows 0x00413830.</remarks>
    public override float DamageFlash() => Flash;

    /// <summary>Kills the Player.</summary>
    /// <remarks>Follows 0x004112ac.</remarks>
    public void Kill()
    {
        if (Dead)
            return;
        Dead = true;
        RaySkip = true;
        Damage = MaxDamage;
        SetMaskBits(0x100);
        _game.SensorJam(this, false); // Player_kill 0x004112ac unlinks; the radius stays
        DeathTime = Manager() != null ? Manager()!.Time() : 0;
        var self = Id();
        var m = Manager();
        _game.ScriptCallback(this, "onKilled", []);
        if (m == null || m.FindById(self) != this)
            return; // the script deleted the player
        ObjectTypeBits = CorpseType;
        CollisionMask = DeadMask;
        if (_game.World.Find(this) is Container.Entry e)
            e.Type = ObjectTypeBits;
        // Timer event 0x401 value 200 at time + 4.0: releases every image trigger.
        m.PostEvent(this, Store(Add(m.Time(), 4.0)), () =>
        {
            for (var slot = 0; slot < ImageSlots; ++slot)
                TriggerImage(slot, false);
        });
        Jetting = false;
    }

    // ---- inventory and image slots (7.1 - 7.4), without the image state machine ----

    /// <summary>Returns the count of an item type.</summary>
    /// <param name="type">The item type.</param>
    /// <returns>The count, 0 for a type out of range.</returns>
    public int ItemCountOf(int type) => type is < 0 or >= ItemTypes ? 0 : Counts[type];

    /// <summary>Sets the count of an item type.</summary>
    /// <param name="type">The item type.</param>
    /// <param name="count">The new count.</param>
    /// <returns>The resulting count.</returns>
    public int SetItemCount(int type, int count)
    {
        // The original has no upper bound on the type (7.1 F2); out-of-table types are refused.
        if (type is < 0 or >= ItemTypes)
            return 0;
        Counts[type] = Math.Max(count, 0);
        if (Counts[type] == 0 && SlotOf[type] >= 0)
            UnmountItem(SlotOf[type]);
        return Counts[type];
    }

    /// <summary>Calls the script callback of an item event.</summary>
    /// <param name="type">The item type.</param>
    /// <param name="ev">The event name.</param>
    /// <param name="extra">An extra argument, or null.</param>
    /// <remarks>Follows Player_findItemCallback (0x004191a0): <c>&lt;item&gt;::event</c>, <c>&lt;className&gt;::event</c>, <c>item::event</c>.</remarks>
    public void ItemCallback(int type, string ev, string? extra = null)
    {
        var item = _game.Sim?.DataBlocks().At(5, type);
        if (item == null || _game.Console == null)
            return;
        var names = new List<string> { item.Name + "::" + ev };
        if (_game.Sim!.DataBlocks().GetField(item, "className", "", out var cls) && cls.Length != 0)
            names.Add(cls + "::" + ev);
        names.Add("item::" + ev);
        foreach (var fn in names)
            if (_game.Console.HasFunction(fn))
            {
                var args = new List<string> { Numbers.FormatInt(Id()), item.Name };
                if (extra != null)
                    args.Add(extra);
                _game.Console.CallFunction(fn, args);
                return;
            }
    }

    // ---- control state (8.1) ----

    /// <summary>The control state as the client reads it (docs/client-net.md 4.4, 00413b90).</summary>
    public sealed class ClientControl
    {
        /// <summary>The yaw.</summary>
        public float Yaw { get; set; }
        /// <summary>True when the state carries a position, which a mounted Player does not.</summary>
        public bool HasPosition { get; set; }
        /// <summary>The position.</summary>
        public float[] Position { get; } = new float[3];
        /// <summary>The velocity.</summary>
        public float[] Vel { get; } = new float[3];
        /// <summary>The pitch.</summary>
        public float Pitch { get; set; }
        /// <summary>The energy.</summary>
        public float Energy { get; set; }
        /// <summary>True on contact.</summary>
        public bool Contact { get; set; }
        /// <summary>True while jetting.</summary>
        public bool Jetting { get; set; }
        /// <summary>True while crouching.</summary>
        public bool Crouch { get; set; }
        /// <summary>The raw unit(7) traction.</summary>
        public uint Traction7 { get; set; }
        /// <summary>The jump contact, int(4).</summary>
        public int JumpContact { get; set; }
    }

    /// <summary>Applies a control state read from the stream to the client copy.</summary>
    /// <param name="c">The control state.</param>
    /// <remarks>Follows Player_readControlState (0x00413b90, docs/client-net.md 4.4): the server state replaces the predicted one. The transform is rebuilt from the Euler x and y, the new yaw and the position, without a collision query. The sensor state bits are not part of the prediction.</remarks>
    public void ApplyClientControlState(ClientControl c)
    {
        using var chop = X87.Chop();
        Rotation[2] = c.Yaw;
        if (c.HasPosition)
        {
            for (var i = 0; i < 3; ++i)
                Velocity[i] = c.Vel[i];
            var m = new Transform();
            MissionTransform.EulerToMatrix(Rotation, ref m);
            Transform.M = m.M;
            Transform.Flags = m.Flags | 3;
            for (var i = 0; i < 3; ++i)
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

    /// <inheritdoc/>
    /// <remarks>Follows 0x00413e08.</remarks>
    public override void WriteControlState(BitBuffer bs, ControlContext ctx)
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
        var t = Mul(Traction, 127.0);
        bs.WriteInt((uint)(int)t & 0x7f, 7);
        bs.WriteFlag(Crouching);
        bs.WriteInt((uint)Math.Min(TicksSinceJumpContact, 15), 4);
    }
}
