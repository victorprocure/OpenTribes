// Port of src/game/ghost_manager.hpp and src/game/ghost_manager.cpp: the GhostManager (docs/game-stream-ghosts.md 1-2):
// per-connection ghost table, scoping, the always phase, update selection and notify.
using OpenTribes.Core;
using OpenTribes.Sim;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

/// <summary>What a class's pack routine may ask about the connection.</summary>
public sealed class GhostContext
{
    /// <summary>The ghost manager of the connection.</summary>
    public GhostManager Manager;
    /// <summary>The client id of the connection.</summary>
    public int ClientId;
    /// <summary>True on the first update of this ghost (mask 0xffffffff, docs/game-stream-ghosts.md 2.2).</summary>
    public bool IsNew;

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
    public delegate uint PackFn(SimObject obj, GhostContext ctx, BitStream bs, uint mask);
    /// <summary>Computes the update priority of an object for the normal phase.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="gm">The ghost manager.</param>
    /// <param name="mask">The dirty mask.</param>
    /// <param name="skipCount">How many times the object was skipped.</param>
    /// <returns>The priority.</returns>
    public delegate float PriorityFn(SimObject obj, GhostManager gm, uint mask, uint skipCount);

    /// <summary>The 10-bit class tag.</summary>
    public uint Tag;
    /// <summary>The pack routine.</summary>
    public PackFn? Pack;
    /// <summary>The priority routine, or null for the manager default.</summary>
    public PriorityFn? Priority;
}

/// <summary>The camera record at GhostManager +0x80, filled by the scope object's query and read by the priority functions.</summary>
/// <remarks>See docs/game-stream-ghosts.md 6.1.</remarks>
public sealed class CameraRecord
{
    /// <summary>The camera object (+0x00).</summary>
    public SimObject? Object;
    /// <summary>The camera position (+0x04).</summary>
    public readonly float[] Pos = { 0, 0, 0 };
    /// <summary>The forward direction (+0x10).</summary>
    public readonly float[] Dir = { 0, 1, 0 };
    /// <summary>The field of view (+0x1c, 0x3f490fdb).</summary>
    public float Fov = 0.785398185f;
    /// <summary>The sine of half the field of view (+0x20).</summary>
    public float SinHalfFov = 0.5f;
    /// <summary>The cosine of half the field of view (+0x24).</summary>
    public float CosHalfFov = 0.5f;
    /// <summary>The visible distance (+0x28).</summary>
    public float VisibleDistance = 1.0f;
}

/// <summary>The per-connection ghost manager: assigns ghost indices, orders updates and writes the ghost block of a packet.</summary>
/// <remarks>Port of src/game/ghost_manager.hpp. See docs/game-stream-ghosts.md.</remarks>
public sealed class GhostManager
{
    /// <summary>The number of ghost indices (1023).</summary>
    public const uint kMaxGhosts = 1023;

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
        public SimObject? Object;
        public uint Mask;
        public uint Flags;
        public Record? InFlight; // newest first
        public Entry? Next;
        public Entry? Prev;
        public ushort Index;
        public ushort Skip;
    }

    private sealed class Record
    {
        public uint Mask;  // bits sent
        public uint State; // 0x10 create, 0x40 delete
        public Entry? Entry;
        public Record? NextInPacket;
        public Record? NextInEntry;
    }

    private const uint kInScope = 0x02, kKeep = 0x04, kCreate = 0x08, kCreating = 0x10, kDelete = 0x20, kDeleting = 0x40;

    private static readonly Dictionary<string, GhostClass> sRegistry = new(StringComparer.OrdinalIgnoreCase);

    private readonly int mClientId;
    private SimObject? mScopeObject;
    private ModeKind mMode = ModeKind.Idle;
    private uint mIdBits = 3;
    private readonly Entry[] mEntries = new Entry[kMaxGhosts];
    private Entry? mFree;
    private Entry? mHead;
    private readonly Dictionary<SimObject, Entry> mByObject = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<Record> mFreeRecords = new();
    private readonly Dictionary<uint, Record> mPackets = new();
    private uint mNextToken = 1;

    /// <summary>The camera record the priority functions read.</summary>
    public CameraRecord Camera = new();
    /// <summary>The scope object's camera query (vtable +0x6c) for the normal phase. It fills the camera record and marks objects in scope.</summary>
    // Normal phase, with a scope object: the scope object's camera query (vtable +0x6c), which fills the camera record
    // and marks objects in scope.
    public Action<GhostManager>? ScopeQuery;
    /// <summary>The priority of classes whose <see cref="GhostClass"/> has none.</summary>
    public GhostClass.PriorityFn? DefaultPriority;
    /// <summary>Called when an entry takes a ghost index (debug trace).</summary>
    public Action<uint, SimObject>? OnNewGhost;
    /// <summary>Called once when the always phase completes (delegate slot +0x68).</summary>
    public Action? OnAlwaysDone;

    /// <summary>Returns the ghost class of an object by its class name.</summary>
    /// <param name="obj">The object.</param>
    /// <returns>The class, or null for classes that are not ghosted.</returns>
    public static GhostClass? GhostClassOf(SimObject obj) => sRegistry.TryGetValue(obj.ClassName(), out GhostClass? c) ? c : null;

    /// <summary>Registers a ghost class.</summary>
    /// <param name="className">The class name.</param>
    /// <param name="cls">The ghost class.</param>
    public static void RegisterGhostClass(string className, GhostClass cls) => sRegistry[className] = cls;

    /// <summary>Finds a ghost class by name.</summary>
    /// <param name="className">The class name.</param>
    /// <returns>The class, or null.</returns>
    public static GhostClass? FindGhostClass(string className) => sRegistry.TryGetValue(className, out GhostClass? c) ? c : null;

    /// <summary>Empties the registry, the start state of a fresh test process.</summary>
    public static void ResetRegistry() => sRegistry.Clear();

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
        float d = SubF(b, a);
        if (d >= 0.0f)
            return d <= 0.0f ? 0 : 1;
        return -1;
    }

    /// <summary>Creates a manager for a connection.</summary>
    /// <param name="clientId">The client id.</param>
    public GhostManager(int clientId)
    {
        mClientId = clientId;
        for (uint i = 0; i < kMaxGhosts; ++i)
            mEntries[i] = new Entry { Index = (ushort)i };
        for (uint i = 0; i < kMaxGhosts; ++i)
            mEntries[i].Next = i + 1 < kMaxGhosts ? mEntries[i + 1] : null;
        mFree = mEntries[0];
    }

    /// <summary>Returns the ghosting phase.</summary>
    /// <returns>The phase.</returns>
    public ModeKind Mode() => mMode;
    /// <summary>Returns the number of bits of a ghost index.</summary>
    /// <returns>The bit count.</returns>
    public uint IdBits() => mIdBits;
    /// <summary>Tells whether any ghost entry exists.</summary>
    /// <returns>True when there is an entry.</returns>
    public bool HasEntries() => mHead != null;
    /// <summary>Returns the client id of the connection.</summary>
    /// <returns>The client id.</returns>
    public int ClientId() => mClientId;
    /// <summary>Sets the scope object (+0x7c), the control object stored by every PlayerPSC write.</summary>
    /// <param name="obj">The scope object, or null.</param>
    /// <remarks>Follows setScopeObject (00553048).</remarks>
    public void SetScopeObject(SimObject? obj) => mScopeObject = obj;
    /// <summary>Returns the scope object.</summary>
    /// <returns>The scope object, or null.</returns>
    public SimObject? ScopeObject() => mScopeObject;
    /// <summary>Starts ghosting with the always phase.</summary>
    /// <remarks>Follows 00553480.</remarks>
    public void StartGhosting() => mMode = ModeKind.Always;

    // src/game/ghost_manager.cpp GhostManager::addGhost: 00553300.
    private Entry? AddGhost(SimObject obj, bool keep)
    {
        Entry? e = mFree;
        if (e == null)
            return null;
        mFree = e.Next;
        e.Flags = kInScope | kCreate | (keep ? kKeep : 0);
        e.Object = obj;
        e.Mask = 0xffffffffu;
        e.InFlight = null;
        e.Skip = 0;
        e.Prev = null;
        e.Next = mHead;
        if (mHead != null)
            mHead.Prev = e;
        mHead = e;
        mByObject[obj] = e;
        uint bits = IndexBits(e.Index);
        if (bits > mIdBits)
            mIdBits = bits;
        OnNewGhost?.Invoke(e.Index, obj);
        return e;
    }

    // src/game/ghost_manager.cpp GhostManager::freeEntry: 005530e0.
    private void FreeEntry(Entry e)
    {
        if (e.Prev != null)
            e.Prev.Next = e.Next;
        else
            mHead = e.Next;
        if (e.Next != null)
            e.Next.Prev = e.Prev;
        e.Next = mFree;
        mFree = e;
        if (e.Object != null)
        {
            mByObject.Remove(e.Object);
            e.Object = null;
        }
    }

    /// <summary>Marks an object as in scope for this update round.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="keep">True to keep the entry when it is not already ghosted.</param>
    /// <remarks>Follows 005532a8.</remarks>
    public void MarkInScope(SimObject obj, bool keep = false)
    {
        if (mByObject.TryGetValue(obj, out Entry? e))
        {
            e.Flags |= kInScope;
            return;
        }
        AddGhost(obj, keep);
    }

    /// <summary>Adds every ghost-always object (net flag 0x100) in set 34 order.</summary>
    /// <param name="objects">The objects of set 34.</param>
    /// <remarks>Follows 00553500.</remarks>
    public void AddAlwaysObjects(IReadOnlyList<SimObject> objects)
    {
        foreach (SimObject o in objects)
            MarkInScope(o, true);
    }

    /// <summary>Resets the manager to the idle phase (resetGhostManagers).</summary>
    public void Reset() => mMode = ModeKind.Idle;

    /// <summary>Adds bits to the dirty mask of an object's ghost.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="bits">The mask bits.</param>
    public void SetMaskBits(SimObject obj, uint bits)
    {
        if (mByObject.TryGetValue(obj, out Entry? e))
            e.Mask |= bits;
    }

    /// <summary>Tells the manager that an object was removed from the simulation.</summary>
    /// <param name="obj">The object.</param>
    public void ObjectRemoved(SimObject obj)
    {
        if (obj == mScopeObject)
            mScopeObject = null;
        if (!mByObject.TryGetValue(obj, out Entry? e))
            return;
        mByObject.Remove(obj);
        e.Object = null;
        e.Flags = (e.Flags & ~kInScope) | kDelete;
    }

    /// <summary>Returns the ghost index of an object.</summary>
    /// <param name="obj">The object.</param>
    /// <returns>The index, or -1 when the object is not ghosted or not yet created.</returns>
    public int GhostIndex(SimObject? obj)
    {
        if (obj == null || !mByObject.TryGetValue(obj, out Entry? e) || (e.Flags & (kCreate | kCreating)) != 0)
            return -1;
        return e.Index;
    }

    // src/game/ghost_manager.cpp GhostManager::newRecord.
    private Record NewRecord()
    {
        if (mFreeRecords.Count != 0)
        {
            Record r = mFreeRecords.Pop();
            r.Mask = r.State = 0;
            r.Entry = null;
            r.NextInPacket = r.NextInEntry = null;
            return r;
        }
        return new Record();
    }

    private struct Item
    {
        public float Priority;
        public Entry Entry;
    }

    /// <summary>Writes the ghost block of a packet (docs/game-stream-ghosts.md 2.1).</summary>
    /// <param name="bs">The packet stream.</param>
    /// <param name="maxBits">The bit budget of the block.</param>
    /// <param name="token">Receives the token that identifies the packet for <see cref="Notify"/>.</param>
    /// <returns>False when the block is absent.</returns>
    public bool WritePacket(BitStream bs, uint maxBits, out uint token)
    {
        token = 0;
        if (!(mMode == ModeKind.Always || mMode == ModeKind.Normal || (mMode == ModeKind.Idle && mHead != null)))
            return false;
        Camera = new CameraRecord();
        bs.WriteFlag(mMode == ModeKind.Always);

        if (mMode == ModeKind.Normal || mMode == ModeKind.Idle)
        {
            if (mMode == ModeKind.Normal)
            {
                for (Entry? e = mHead; e != null; e = e.Next)
                    if ((e.Flags & kKeep) == 0)
                        e.Flags &= ~kInScope;
                if (mScopeObject != null && ScopeQuery != null)
                    ScopeQuery(this);
            }
            for (Entry? e = mHead; e != null; e = e.Next)
                if ((e.Flags & kInScope) == 0)
                {
                    e.Flags |= kDelete;
                    if (e.Object != null)
                    {
                        mByObject.Remove(e.Object);
                        e.Object = null;
                    }
                }
        }

        // Selection: deletes first (priority 0), then updates by priority.
        var updates = new List<Item>();
        var deletes = new List<Item>();
        for (Entry? e = mHead; e != null;)
        {
            Entry? next = e.Next;
            if ((e.Flags & kDelete) != 0 && (e.Flags & kCreate) != 0)
            {
                FreeEntry(e); // never sent
            }
            else if ((e.Flags & (kCreating | kDeleting)) == 0)
            {
                if ((e.Flags & kDelete) != 0)
                {
                    deletes.Add(new Item { Priority = 0.0f, Entry = e });
                }
                else if ((e.Flags & kCreate) != 0 && mMode == ModeKind.Always)
                {
                    updates.Add(new Item { Priority = -(int)e.Object!.Sequence(), Entry = e });
                }
                else if (e.Mask != 0 && mMode == ModeKind.Normal)
                {
                    // 1.0 without a scope object; else the class priority (vtable +0x5c).
                    float p = 1.0f;
                    if (mScopeObject != null)
                    {
                        GhostClass? cls = GhostClassOf(e.Object!);
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
        Item[] sorted = updates.ToArray();
        Borland.BorlandQsort<Item>(sorted, (a, b) => ComparePriority(a.Priority, b.Priority));

        bs.WriteInt(mIdBits - 3, 3);
        Record? packet = null;
        void WriteEntry(Entry e)
        {
            if (bs.BitPosition() > maxBits)
            {
                ++e.Skip;
                return;
            }
            bs.WriteFlag(true);
            bs.WriteInt(e.Index, mIdBits);
            Record r = NewRecord();
            r.NextInPacket = packet;
            r.NextInEntry = e.InFlight;
            e.InFlight = r;
            r.Entry = e;
            if ((e.Flags & kDelete) == 0)
            {
                bs.WriteFlag(false);
                GhostClass? cls = GhostClassOf(e.Object!);
                bool isNew = (e.Flags & kCreate) != 0;
                if (isNew)
                {
                    if (mMode == ModeKind.Always)
                        bs.WriteInt(e.Object!.Sequence(), 32);
                    e.Flags = (e.Flags & ~kCreate) | kCreating;
                    r.State = kCreating;
                    bs.WriteInt(cls != null ? cls.Tag : 0, 10);
                }
                uint mask = e.Mask;
                var ctx = new GhostContext(this, mClientId, isNew);
                uint retained = cls?.Pack != null ? cls.Pack(e.Object!, ctx, bs, mask) : 0;
                e.Mask = retained;
                r.Mask = mask & ~retained;
            }
            else
            {
                e.Flags = (e.Flags & ~kDelete) | kDeleting;
                r.State = kDeleting;
                bs.WriteFlag(true);
            }
            e.Skip = 0;
            packet = r;
        }
        foreach (Item it in deletes)
            WriteEntry(it.Entry);
        foreach (Item it in sorted)
            WriteEntry(it.Entry);
        bs.WriteFlag(false);

        if (mMode == ModeKind.Always)
        {
            bool done = true;
            for (Entry? e = mHead; e != null; e = e.Next)
                if ((e.Flags & (kCreate | kCreating)) != 0)
                {
                    done = false;
                    break;
                }
            bs.WriteFlag(done);
            if (done)
            {
                mMode = ModeKind.Normal;
                OnAlwaysDone?.Invoke();
            }
        }
        if (packet != null)
        {
            token = mNextToken++;
            mPackets[token] = packet;
        }
        return true;
    }

    /// <summary>Reports whether a packet was delivered.</summary>
    /// <param name="token">The token from <see cref="WritePacket"/>.</param>
    /// <param name="delivered">True when the packet was delivered.</param>
    public void Notify(uint token, bool delivered)
    {
        if (!mPackets.TryGetValue(token, out Record? r))
            return;
        mPackets.Remove(token);
        while (r != null)
        {
            Record? nextR = r.NextInPacket;
            Entry e = r.Entry!;
            // Unlink r (and the older records behind it) from the entry's in-flight list.
            uint later = 0;
            Record? prev = null;
            Record? cur = e.InFlight;
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
                if ((r.State & kCreating) != 0)
                    e.Flags &= ~kCreating;
                else if ((r.State & kDeleting) != 0)
                    FreeEntry(e);
            }
            else
            {
                e.Mask |= r.Mask & ~later;
                if ((r.State & kCreating) != 0)
                    e.Flags = (e.Flags | kCreate) & ~kCreating;
                else if ((r.State & kDeleting) != 0)
                    e.Flags = (e.Flags | kDelete) & ~kDeleting;
            }
            mFreeRecords.Push(r);
            r = nextR;
        }
    }
}
