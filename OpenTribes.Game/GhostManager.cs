// Port of src/game/ghost_manager.hpp and src/game/ghost_manager.cpp: the GhostManager (docs/game-stream-ghosts.md 1-2):
// per-connection ghost table, scoping, the always phase, update selection and notify.
using OpenTribes.Core;
using OpenTribes.Sim;
using static OpenTribes.Core.Fpu;

namespace OpenTribes.Game;

// src/game/ghost_manager.hpp struct GhostContext: what a class's packUpdate may ask about the connection.
public sealed class GhostContext
{
    public GhostManager Manager;
    public int ClientId;
    public bool IsNew; // first update of this ghost (mask 0xffffffff, 2.2)

    public GhostContext(GhostManager manager, int clientId, bool isNew)
    {
        Manager = manager;
        ClientId = clientId;
        IsNew = isNew;
    }
}

// src/game/ghost_manager.hpp struct GhostClass: ghost code of a class (2.3): 10-bit tag, packUpdate returning the mask
// bits it did not send, and the update priority for the normal phase.
public sealed class GhostClass
{
    public delegate uint PackFn(SimObject obj, GhostContext ctx, BitStream bs, uint mask);
    public delegate float PriorityFn(SimObject obj, GhostManager gm, uint mask, uint skipCount);

    public uint Tag;
    public PackFn? Pack;
    public PriorityFn? Priority;
}

// src/game/ghost_manager.hpp struct CameraRecord: camera record at GhostManager +0x80 (docs/game-stream-ghosts.md 6.1):
// filled by the scope object's query, read by the priority functions.
public sealed class CameraRecord
{
    public SimObject? Object;                        // +0x00
    public readonly float[] Pos = { 0, 0, 0 };       // +0x04
    public readonly float[] Dir = { 0, 1, 0 };       // +0x10 forward
    public float Fov = 0.785398185f;                 // +0x1c (0x3f490fdb)
    public float SinHalfFov = 0.5f;                  // +0x20
    public float CosHalfFov = 0.5f;                  // +0x24
    public float VisibleDistance = 1.0f;             // +0x28
}

// src/game/ghost_manager.hpp class GhostManager.
public sealed class GhostManager
{
    public const uint kMaxGhosts = 1023;

    public enum ModeKind { Idle = 0, Always = 1, Normal = 3 }

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

    public CameraRecord Camera = new();
    // Normal phase, with a scope object: the scope object's camera query (vtable +0x6c), which fills the camera record
    // and marks objects in scope.
    public Action<GhostManager>? ScopeQuery;
    // Priority of classes whose GhostClass has none.
    public GhostClass.PriorityFn? DefaultPriority;
    // Called when an entry takes a ghost index (debug trace).
    public Action<uint, SimObject>? OnNewGhost;
    // Called once when the always phase completes (delegate slot +0x68).
    public Action? OnAlwaysDone;

    // src/game/ghost_manager.cpp ghostClassOf: registry by class name; null for classes that are not ghosted.
    public static GhostClass? GhostClassOf(SimObject obj) => sRegistry.TryGetValue(obj.ClassName(), out GhostClass? c) ? c : null;

    // src/game/ghost_manager.cpp registerGhostClass.
    public static void RegisterGhostClass(string className, GhostClass cls) => sRegistry[className] = cls;

    // src/game/ghost_manager.cpp findGhostClass.
    public static GhostClass? FindGhostClass(string className) => sRegistry.TryGetValue(className, out GhostClass? c) ? c : null;

    // Port convention (no C++ function): empties the registry, the start state of a C++ test process.
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

    // src/game/ghost_manager.cpp GhostManager::GhostManager.
    public GhostManager(int clientId)
    {
        mClientId = clientId;
        for (uint i = 0; i < kMaxGhosts; ++i)
            mEntries[i] = new Entry { Index = (ushort)i };
        for (uint i = 0; i < kMaxGhosts; ++i)
            mEntries[i].Next = i + 1 < kMaxGhosts ? mEntries[i + 1] : null;
        mFree = mEntries[0];
    }

    public ModeKind Mode() => mMode;
    public uint IdBits() => mIdBits;
    public bool HasEntries() => mHead != null;
    public int ClientId() => mClientId;
    // Scope object (+0x7c): the control object, stored by every PlayerPSC write (setScopeObject 00553048).
    public void SetScopeObject(SimObject? obj) => mScopeObject = obj;
    public SimObject? ScopeObject() => mScopeObject;
    // 00553480.
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

    // src/game/ghost_manager.cpp GhostManager::markInScope: 005532a8.
    public void MarkInScope(SimObject obj, bool keep = false)
    {
        if (mByObject.TryGetValue(obj, out Entry? e))
        {
            e.Flags |= kInScope;
            return;
        }
        AddGhost(obj, keep);
    }

    // src/game/ghost_manager.cpp GhostManager::addAlwaysObjects: every ghost-always object (net flag 0x100) in set 34
    // order (00553500).
    public void AddAlwaysObjects(IReadOnlyList<SimObject> objects)
    {
        foreach (SimObject o in objects)
            MarkInScope(o, true);
    }

    // src/game/ghost_manager.cpp GhostManager::reset: resetGhostManagers: mode 0.
    public void Reset() => mMode = ModeKind.Idle;

    // src/game/ghost_manager.cpp GhostManager::setMaskBits.
    public void SetMaskBits(SimObject obj, uint bits)
    {
        if (mByObject.TryGetValue(obj, out Entry? e))
            e.Mask |= bits;
    }

    // src/game/ghost_manager.cpp GhostManager::objectRemoved.
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

    // src/game/ghost_manager.cpp GhostManager::ghostIndex: -1 when not ghosted (or not yet created).
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

    // src/game/ghost_manager.cpp GhostManager::writePacket: writes the ghost block (2.1). False when the block is absent.
    // The returned token identifies the packet for notify.
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

    // src/game/ghost_manager.cpp GhostManager::notify.
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
