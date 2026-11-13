// Port of src/game/ghost_manager.hpp and src/game/ghost_manager.cpp: the GhostManager (docs/game-stream-ghosts.md 1-2):
// per-connection ghost table, scoping, the always phase, update selection and notify.
namespace OpenTribes.Game;

using static OpenTribes.Core.Fpu;

/// <summary>What a class's pack routine may ask about the connection.</summary>
public sealed class GhostContext
{
    /// <summary>The ghost manager of the connection.</summary>
    public GhostManager Manager { get; set; }
    /// <summary>The client id of the connection.</summary>
    public int ClientId { get; set; }
    /// <summary>True on the first update of this ghost (mask 0xffffffff, docs/game-stream-ghosts.md 2.2).</summary>
    public bool IsNew { get; set; }

    /// <summary>Creates a context.</summary>
    /// <param name="manager">The ghost manager.</param>
    /// <param name="clientId">The client id.</param>
    /// <param name="isNew">True on the first update of the ghost.</param>
    public GhostContext(GhostManager manager, int clientId, bool isNew)
    {
        Manager = manager;
        ClientId = clientId;
        IsNew = isNew;
    }
}

/// <summary>The ghost code of a class: a 10-bit tag, a pack routine and an update priority (docs/game-stream-ghosts.md 2.3).</summary>
public sealed class GhostClass
{
    /// <summary>Packs an object's update.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="ctx">The connection context.</param>
    /// <param name="bs">The stream to write.</param>
    /// <param name="mask">The dirty mask.</param>
    /// <returns>The mask bits that were not sent.</returns>
    public delegate uint PackFn(SimObject obj, GhostContext ctx, BitBuffer bs, uint mask);
    /// <summary>Computes the update priority of an object for the normal phase.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="gm">The ghost manager.</param>
    /// <param name="mask">The dirty mask.</param>
    /// <param name="skipCount">How many times the object was skipped.</param>
    /// <returns>The priority.</returns>
    public delegate float PriorityFn(SimObject obj, GhostManager gm, uint mask, uint skipCount);

    /// <summary>The 10-bit class tag.</summary>
    public uint Tag { get; set; }
    /// <summary>The pack routine.</summary>
    public PackFn? Pack { get; set; }
    /// <summary>The priority routine, or null for the manager default.</summary>
    public PriorityFn? Priority { get; set; }
}

/// <summary>The camera record at GhostManager +0x80, filled by the scope object's query and read by the priority functions.</summary>
/// <remarks>See docs/game-stream-ghosts.md 6.1.</remarks>
public sealed class CameraRecord
{
    /// <summary>The camera object (+0x00).</summary>
    public SimObject? Object { get; set; }
    /// <summary>The camera position (+0x04).</summary>
    public float[] Pos { get; } = [0, 0, 0];
    /// <summary>The forward direction (+0x10).</summary>
    public float[] Dir { get; } = [0, 1, 0];
    /// <summary>The field of view (+0x1c, 0x3f490fdb).</summary>
    public float Fov { get; set; } = 0.785398185f;
    /// <summary>The sine of half the field of view (+0x20).</summary>
    public float SinHalfFov { get; set; } = 0.5f;
    /// <summary>The cosine of half the field of view (+0x24).</summary>
    public float CosHalfFov { get; set; } = 0.5f;
    /// <summary>The visible distance (+0x28).</summary>
    public float VisibleDistance { get; set; } = 1.0f;
}

/// <summary>The per-connection ghost manager: assigns ghost indices, orders updates and writes the ghost block of a packet.</summary>
/// <remarks>Port of src/game/ghost_manager.hpp. See docs/game-stream-ghosts.md.</remarks>
public sealed class GhostManager
{
    /// <summary>The number of ghost indices (1023).</summary>
    public const uint MaxGhosts = 1023;

    /// <summary>The ghosting phase.</summary>
    public enum ModeKind
    {
        /// <summary>Not ghosting.</summary>
        Idle = 0,
        /// <summary>The always phase: objects flagged ghost-always.</summary>
        Always = 1,
        /// <summary>The normal phase: objects in scope by priority.</summary>
        Normal = 3,
    }

    private sealed class Entry
    {
        public SimObject? Object { get; set; }
        public uint Mask { get; set; }
        public uint Flags { get; set; }
        public Record? InFlight { get; set; } // newest first
        public Entry? Next { get; set; }
        public Entry? Prev { get; set; }
        public ushort Index { get; set; }
        public ushort Skip { get; set; }
    }

    private sealed class Record
    {
        public uint Mask { get; set; }  // bits sent
        public uint State { get; set; } // 0x10 create, 0x40 delete
        public Entry? Entry { get; set; }
        public Record? NextInPacket { get; set; }
        public Record? NextInEntry { get; set; }
    }

    private const uint InScope = 0x02, Keep = 0x04, Create = 0x08, Creating = 0x10, Delete = 0x20, Deleting = 0x40;

    private static readonly Dictionary<string, GhostClass> Registry = new(StringComparer.OrdinalIgnoreCase);

    private readonly int _clientId;
    private SimObject? _scopeObject;
    private ModeKind _mode = ModeKind.Idle;
    private uint _idBits = 3;
    private readonly Entry[] _entries = new Entry[MaxGhosts];
    private Entry? _free;
    private Entry? _head;
    private readonly Dictionary<SimObject, Entry> _byObject = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<Record> _freeRecords = new();
    private readonly Dictionary<uint, Record> _packets = [];
    private uint _nextToken = 1;

    /// <summary>The camera record the priority functions read.</summary>
    public CameraRecord Camera { get; set; } = new();
    /// <summary>The scope object's camera query (vtable +0x6c) for the normal phase. It fills the camera record and marks objects in scope.</summary>
    // Normal phase, with a scope object: the scope object's camera query (vtable +0x6c), which fills the camera record
    // and marks objects in scope.
    public Action<GhostManager>? ScopeQuery { get; set; }
    /// <summary>The priority of classes whose <see cref="GhostClass"/> has none.</summary>
    public GhostClass.PriorityFn? DefaultPriority { get; set; }
    /// <summary>Called when an entry takes a ghost index (debug trace).</summary>
    public Action<uint, SimObject>? OnNewGhost { get; set; }
    /// <summary>Called once when the always phase completes (delegate slot +0x68).</summary>
    public Action? OnAlwaysDone { get; set; }

    /// <summary>Returns the ghost class of an object by its class name.</summary>
    /// <param name="obj">The object.</param>
    /// <returns>The class, or null for classes that are not ghosted.</returns>
    public static GhostClass? GhostClassOf(SimObject obj) => Registry.TryGetValue(obj.ClassName(), out var c) ? c : null;

    /// <summary>Registers a ghost class.</summary>
    /// <param name="className">The class name.</param>
    /// <param name="cls">The ghost class.</param>
    public static void RegisterGhostClass(string className, GhostClass cls) => Registry[className] = cls;

    /// <summary>Finds a ghost class by name.</summary>
    /// <param name="className">The class name.</param>
    /// <returns>The class, or null.</returns>
    public static GhostClass? FindGhostClass(string className) => Registry.TryGetValue(className, out var c) ? c : null;

    /// <summary>Empties the registry, the start state of a fresh test process.</summary>
    public static void ResetRegistry() => Registry.Clear();

    // src/game/ghost_manager.cpp indexBits: 00552360: bit length of a ghost index, at least 1.
    private static uint IndexBits(uint index)
    {
        uint n = 1;
        while (n < 32 && (index >> (int)n) != 0)
            ++n;
        return n;
    }

    // src/game/ghost_manager.cpp comparePriority: 0055231c: descending priority.
    private static int ComparePriority(float a, float b)
    {
        var d = SubF(b, a);
        if (d >= 0.0f)
            return d <= 0.0f ? 0 : 1;
        return -1;
    }

    /// <summary>Creates a manager for a connection.</summary>
    /// <param name="clientId">The client id.</param>
    public GhostManager(int clientId)
    {
        _clientId = clientId;
        for (uint i = 0; i < MaxGhosts; ++i)
            _entries[i] = new Entry { Index = (ushort)i };
        for (uint i = 0; i < MaxGhosts; ++i)
            _entries[i].Next = i + 1 < MaxGhosts ? _entries[i + 1] : null;
        _free = _entries[0];
    }

    /// <summary>Returns the ghosting phase.</summary>
    /// <returns>The phase.</returns>
    public ModeKind Mode() => _mode;
    /// <summary>Returns the number of bits of a ghost index.</summary>
    /// <returns>The bit count.</returns>
    public uint IdBits() => _idBits;
    /// <summary>Tells whether any ghost entry exists.</summary>
    /// <returns>True when there is an entry.</returns>
    public bool HasEntries() => _head != null;
    /// <summary>Returns the client id of the connection.</summary>
    /// <returns>The client id.</returns>
    public int ClientId() => _clientId;
    /// <summary>Sets the scope object (+0x7c), the control object stored by every PlayerPSC write.</summary>
    /// <param name="obj">The scope object, or null.</param>
    /// <remarks>Follows setScopeObject (00553048).</remarks>
    public void SetScopeObject(SimObject? obj) => _scopeObject = obj;
    /// <summary>Returns the scope object.</summary>
    /// <returns>The scope object, or null.</returns>
    public SimObject? ScopeObject() => _scopeObject;
    /// <summary>Starts ghosting with the always phase.</summary>
    /// <remarks>Follows 00553480.</remarks>
    public void StartGhosting() => _mode = ModeKind.Always;

    // src/game/ghost_manager.cpp GhostManager::addGhost: 00553300.
    private Entry? AddGhost(SimObject obj, bool keep)
    {
        var e = _free;
        if (e == null)
            return null;
        _free = e.Next;
        e.Flags = InScope | Create | (keep ? Keep : 0);
        e.Object = obj;
        e.Mask = 0xffffffffu;
        e.InFlight = null;
        e.Skip = 0;
        e.Prev = null;
        e.Next = _head;
        _head?.Prev = e;
        _head = e;
        _byObject[obj] = e;
        var bits = IndexBits(e.Index);
        if (bits > _idBits)
            _idBits = bits;
        OnNewGhost?.Invoke(e.Index, obj);
        return e;
    }

    // src/game/ghost_manager.cpp GhostManager::freeEntry: 005530e0.
    private void FreeEntry(Entry e)
    {
        if (e.Prev != null)
            e.Prev.Next = e.Next;
        else
            _head = e.Next;
        e.Next?.Prev = e.Prev;
        e.Next = _free;
        _free = e;
        if (e.Object != null)
        {
            _byObject.Remove(e.Object);
            e.Object = null;
        }
    }

    /// <summary>Marks an object as in scope for this update round.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="keep">True to keep the entry when it is not already ghosted.</param>
    /// <remarks>Follows 005532a8.</remarks>
    public void MarkInScope(SimObject obj, bool keep = false)
    {
        if (_byObject.TryGetValue(obj, out var e))
        {
            e.Flags |= InScope;
            return;
        }
        AddGhost(obj, keep);
    }

    /// <summary>Adds every ghost-always object (net flag 0x100) in set 34 order.</summary>
    /// <param name="objects">The objects of set 34.</param>
    /// <remarks>Follows 00553500.</remarks>
    public void AddAlwaysObjects(IReadOnlyList<SimObject> objects)
    {
        foreach (var o in objects)
            MarkInScope(o, true);
    }

    /// <summary>Resets the manager to the idle phase (resetGhostManagers).</summary>
    public void Reset() => _mode = ModeKind.Idle;

    /// <summary>Adds bits to the dirty mask of an object's ghost.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="bits">The mask bits.</param>
    public void SetMaskBits(SimObject obj, uint bits)
    {
        if (_byObject.TryGetValue(obj, out var e))
            e.Mask |= bits;
    }

    /// <summary>Tells the manager that an object was removed from the simulation.</summary>
    /// <param name="obj">The object.</param>
    public void ObjectRemoved(SimObject obj)
    {
        if (obj == _scopeObject)
            _scopeObject = null;
        if (!_byObject.TryGetValue(obj, out var e))
            return;
        _byObject.Remove(obj);
        e.Object = null;
        e.Flags = (e.Flags & ~InScope) | Delete;
    }

    /// <summary>Returns the ghost index of an object.</summary>
    /// <param name="obj">The object.</param>
    /// <returns>The index, or -1 when the object is not ghosted or not yet created.</returns>
    public int GhostIndex(SimObject? obj)
    {
        if (obj == null || !_byObject.TryGetValue(obj, out var e) || (e.Flags & (Create | Creating)) != 0)
            return -1;
        return e.Index;
    }

    // src/game/ghost_manager.cpp GhostManager::newRecord.
    private Record NewRecord()
    {
        if (_freeRecords.Count != 0)
        {
            var r = _freeRecords.Pop();
            r.Mask = r.State = 0;
            r.Entry = null;
            r.NextInPacket = r.NextInEntry = null;
            return r;
        }
        return new Record();
    }

    private struct Item
    {
        public float Priority { get; set; }
        public Entry Entry { get; set; }
    }

    /// <summary>Writes the ghost block of a packet (docs/game-stream-ghosts.md 2.1).</summary>
    /// <param name="bs">The packet stream.</param>
    /// <param name="maxBits">The bit budget of the block.</param>
    /// <param name="token">Receives the token that identifies the packet for <see cref="Notify"/>.</param>
    /// <returns>False when the block is absent.</returns>
    public bool WritePacket(BitBuffer bs, uint maxBits, out uint token)
    {
        token = 0;
        if (!(_mode == ModeKind.Always || _mode == ModeKind.Normal || (_mode == ModeKind.Idle && _head != null)))
            return false;
        Camera = new CameraRecord();
        bs.WriteFlag(_mode == ModeKind.Always);

        if (_mode is ModeKind.Normal or ModeKind.Idle)
        {
            if (_mode == ModeKind.Normal)
            {
                for (var e = _head; e != null; e = e.Next)
                    if ((e.Flags & Keep) == 0)
                        e.Flags &= ~InScope;
                if (_scopeObject != null && ScopeQuery != null)
                    ScopeQuery(this);
            }
            for (var e = _head; e != null; e = e.Next)
                if ((e.Flags & InScope) == 0)
                {
                    e.Flags |= Delete;
                    if (e.Object != null)
                    {
                        _byObject.Remove(e.Object);
                        e.Object = null;
                    }
                }
        }

        // Selection: deletes first (priority 0), then updates by priority.
        var updates = new List<Item>();
        var deletes = new List<Item>();
        for (var e = _head; e != null;)
        {
            var next = e.Next;
            if ((e.Flags & Delete) != 0 && (e.Flags & Create) != 0)
            {
                FreeEntry(e); // never sent
            }
            else if ((e.Flags & (Creating | Deleting)) == 0)
            {
                if ((e.Flags & Delete) != 0)
                {
                    deletes.Add(new Item { Priority = 0.0f, Entry = e });
                }
                else if ((e.Flags & Create) != 0 && _mode == ModeKind.Always)
                {
                    updates.Add(new Item { Priority = -(int)e.Object!.Sequence(), Entry = e });
                }
                else if (e.Mask != 0 && _mode == ModeKind.Normal)
                {
                    // 1.0 without a scope object; else the class priority (vtable +0x5c).
                    var p = 1.0f;
                    if (_scopeObject != null)
                    {
                        var cls = GhostClassOf(e.Object!);
                        if (cls?.Priority != null)
                            p = cls.Priority(e.Object!, this, e.Mask, e.Skip);
                        else if (DefaultPriority != null)
                            p = DefaultPriority(e.Object!, this, e.Mask, e.Skip);
                    }
                    updates.Add(new Item { Priority = p, Entry = e });
                }
            }
            e = next;
        }
        Item[] sorted = [.. updates];
        Borland.BorlandQsort(sorted, (a, b) => ComparePriority(a.Priority, b.Priority));

        bs.WriteInt(_idBits - 3, 3);
        Record? packet = null;
        void WriteEntry(Entry e)
        {
            if (bs.BitPosition() > maxBits)
            {
                ++e.Skip;
                return;
            }
            bs.WriteFlag(true);
            bs.WriteInt(e.Index, _idBits);
            var r = NewRecord();
            r.NextInPacket = packet;
            r.NextInEntry = e.InFlight;
            e.InFlight = r;
            r.Entry = e;
            if ((e.Flags & Delete) == 0)
            {
                bs.WriteFlag(false);
                var cls = GhostClassOf(e.Object!);
                var isNew = (e.Flags & Create) != 0;
                if (isNew)
                {
                    if (_mode == ModeKind.Always)
                        bs.WriteInt(e.Object!.Sequence(), 32);
                    e.Flags = (e.Flags & ~Create) | Creating;
                    r.State = Creating;
                    bs.WriteInt(cls != null ? cls.Tag : 0, 10);
                }
                var mask = e.Mask;
                var ctx = new GhostContext(this, _clientId, isNew);
                var retained = cls?.Pack != null ? cls.Pack(e.Object!, ctx, bs, mask) : 0;
                e.Mask = retained;
                r.Mask = mask & ~retained;
            }
            else
            {
                e.Flags = (e.Flags & ~Delete) | Deleting;
                r.State = Deleting;
                bs.WriteFlag(true);
            }
            e.Skip = 0;
            packet = r;
        }
        foreach (var it in deletes)
            WriteEntry(it.Entry);
        foreach (var it in sorted)
            WriteEntry(it.Entry);
        bs.WriteFlag(false);

        if (_mode == ModeKind.Always)
        {
            var done = true;
            for (var e = _head; e != null; e = e.Next)
                if ((e.Flags & (Create | Creating)) != 0)
                {
                    done = false;
                    break;
                }
            bs.WriteFlag(done);
            if (done)
            {
                _mode = ModeKind.Normal;
                OnAlwaysDone?.Invoke();
            }
        }
        if (packet != null)
        {
            token = _nextToken++;
            _packets[token] = packet;
        }
        return true;
    }

    /// <summary>Reports whether a packet was delivered.</summary>
    /// <param name="token">The token from <see cref="WritePacket"/>.</param>
    /// <param name="delivered">True when the packet was delivered.</param>
    public void Notify(uint token, bool delivered)
    {
        if (!_packets.TryGetValue(token, out var r))
            return;
        _packets.Remove(token);
        while (r != null)
        {
            var nextR = r.NextInPacket;
            var e = r.Entry!;
            // Unlink r (and the older records behind it) from the entry's in-flight list.
            uint later = 0;
            Record? prev = null;
            var cur = e.InFlight;
            while (cur != null && cur != r)
            {
                later |= cur.Mask;
                prev = cur;
                cur = cur.NextInEntry;
            }
            if (prev == null)
                e.InFlight = null;
            else
                prev.NextInEntry = null;
            if (delivered)
            {
                if ((r.State & Creating) != 0)
                    e.Flags &= ~Creating;
                else if ((r.State & Deleting) != 0)
                    FreeEntry(e);
            }
            else
            {
                e.Mask |= r.Mask & ~later;
                if ((r.State & Creating) != 0)
                    e.Flags = (e.Flags | Create) & ~Creating;
                else if ((r.State & Deleting) != 0)
                    e.Flags = (e.Flags | Delete) & ~Deleting;
            }
            _freeRecords.Push(r);
            r = nextR;
        }
    }
}
