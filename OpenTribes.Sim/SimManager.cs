// Port of src/sim/sim_manager.hpp and src/sim/sim_manager.cpp: simulation manager (docs/sim.md 1, 2, 3.2, 8.1, 8.2):
// root group of one world, id pool and dictionary, path lookup, registration, events and time.
using OpenTribes.Core;

namespace OpenTribes.Sim;

// src/sim/sim_manager.hpp class SimManager.
public sealed class SimManager : SimGroup
{
    // src/sim/sim_manager.hpp SimManager::ConsoleHandler.
    public delegate void ConsoleHandler(SimObject? target, string text);

    // src/sim/sim_manager.hpp SimManager::Event.
    private sealed class Event
    {
        public float Time;
        public SimObject? Dest;
        public string Text = "";
        public Action? Action;     // engine event when set, else console text
        public SimObject? Sender;
    }

    private readonly StringTable mStrings;
    private int mManagerId;
    private uint mSequenceCounter;
    private int mNextId = 8192;
    private readonly List<int> mFreeIds = new();
    private readonly Dictionary<int, List<SimObject>> mIds = new(); // newest at the back
    private readonly LinkedList<Event> mEvents = new(); // sorted by time, FIFO for equal times (EV5)
    private float mQueueTime;  // +0x80
    private float mTargetTime; // +0x6c
    private ConsoleHandler? mConsoleHandler;

    // Observer of every successful registration (NetObject_onAdd 0050a420: a ghost-always object is marked in every
    // ghost manager).
    public Action<SimObject>? OnRegister;
    // Observer of every unregistration (ghost managers drop their entries).
    public Action<SimObject>? OnUnregister;
    // Console output of the manager itself (registration failures in SimSet::onAdd).
    public Action<string>? Print;

    // src/sim/sim_manager.cpp SimManager::SimManager: the manager is its own root, registered, never in the id
    // dictionary (MG7).
    public SimManager(StringTable strings)
    {
        mStrings = strings;
        mManager = this;
    }

    public override string ClassName() => "SimManager";

    public int ManagerId() => mManagerId;
    public void SetManagerId(int id) => mManagerId = id;
    public void SetConsoleHandler(ConsoleHandler? h) => mConsoleHandler = h;
    // Queue time (TM5): the time of the event being dispatched, else the last advance target. getSimTime and schedule
    // read it.
    public float Time() => mQueueTime;
    public int PendingEvents() => mEvents.Count;
    public StringTable Strings() => mStrings;

    // src/sim/sim_manager.cpp isDigit.
    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    // src/sim/sim_manager.cpp atoiWrap.
    private static int AtoiWrap(string s)
    {
        uint v = 0;
        foreach (char c in s)
        {
            if (!IsDigit(c))
                break;
            v = unchecked(v * 10 + (uint)(c - '0'));
        }
        return unchecked((int)v);
    }

    // src/sim/sim_manager.cpp SimManager::~SimManager.
    public override void Destroy()
    {
        mEvents.Clear();
        base.Destroy();
    }

    // src/sim/sim_manager.cpp SimManager::allocateId.
    private int AllocateId()
    {
        if (mFreeIds.Count != 0)
        {
            int id = mFreeIds[^1];
            mFreeIds.RemoveAt(mFreeIds.Count - 1);
            return id;
        }
        return mNextId++;
    }

    // src/sim/sim_manager.cpp SimManager::freeId.
    private void FreeId(int id)
    {
        mFreeIds.Add(id);
        while (mFreeIds.Count != 0 && mFreeIds[^1] == mNextId - 1)
        {
            mFreeIds.RemoveAt(mFreeIds.Count - 1);
            --mNextId;
        }
    }

    // src/sim/sim_manager.cpp SimManager::dictInsert.
    private void DictInsert(SimObject obj)
    {
        if (!mIds.TryGetValue(obj.mId, out List<SimObject>? v))
            mIds[obj.mId] = v = new List<SimObject>();
        v.Add(obj);
    }

    // src/sim/sim_manager.cpp SimManager::dictErase.
    private void DictErase(SimObject obj)
    {
        if (!mIds.TryGetValue(obj.mId, out List<SimObject>? v))
            return;
        v.RemoveAll(o => o == obj);
        if (v.Count == 0)
            mIds.Remove(obj.mId);
    }

    // src/sim/sim_manager.cpp SimManager::registerObject: registers obj (OB2): sequence, id (ID1), onAdd. Returns false if
    // onAdd fails.
    public bool RegisterObject(SimObject obj)
    {
        obj.mSequence = mSequenceCounter++;
        obj.mRemoved = false;
        obj.mManager = this;
        if (obj.mId == 0 && obj.FixedId() != 0)
            obj.mId = obj.FixedId();
        if (obj.mId == 0)
        {
            obj.mId = AllocateId();
            obj.mOwnsPoolId = true;
        }
        DictInsert(obj);
        if (obj.OnAdd())
        {
            if (OnRegister != null && obj.mManager == this)
                OnRegister(obj);
            return true;
        }
        DictErase(obj);
        obj.mManager = null;
        if (obj.mOwnsPoolId)
        {
            FreeId(obj.mId);
            obj.mOwnsPoolId = false;
            obj.mId = 0;
        }
        return false;
    }

    // src/sim/sim_manager.cpp SimManager::unregisterObject: unregisters obj (RD1); groups take their subtree with them
    // (RD2).
    public void UnregisterObject(SimObject obj)
    {
        if (obj.mManager == null || obj == this)
            return;
        obj.mRemoved = true;
        obj.OnRemove();
        OnUnregister?.Invoke(obj);
        obj.mGroup?.RemoveObject(obj);
        var sets = new List<SimSet>(obj.mInSets);
        foreach (SimSet s in sets)
            s.RemoveObject(obj);
        DictErase(obj);
        for (LinkedListNode<Event>? n = mEvents.First; n != null;)
        {
            LinkedListNode<Event>? next = n.Next;
            if (n.Value.Dest == obj || n.Value.Sender == obj)
                mEvents.Remove(n);
            n = next;
        }
        obj.mManager = null;
        if (obj.mOwnsPoolId)
        {
            FreeId(obj.mId);
            obj.mOwnsPoolId = false;
        }
    }

    // src/sim/sim_manager.cpp SimManager::deleteObject: unregister and destroy.
    public void DeleteObject(SimObject obj)
    {
        if (obj == this)
            return; // deleteObject(0) would delete the root in the original (D11)
        UnregisterObject(obj);
        obj.mGroup?.RemoveObject(obj);
        obj.Destroy();
    }

    // src/sim/sim_manager.cpp SimManager::addToRoot: root add (GS3, 004ff9dc), registers an unregistered object first.
    // False on failure.
    public bool AddToRoot(SimObject obj)
    {
        if (obj.mManager == null && !RegisterObject(obj))
            return false;
        if (obj.mGroup == null)
            base.AddObject(obj);
        return true;
    }

    // src/sim/sim_manager.cpp SimManager::findById.
    public SimObject? FindById(int id) =>
        mIds.TryGetValue(id, out List<SimObject>? v) && v.Count != 0 ? v[^1] : null;

    // src/sim/sim_manager.cpp SimManager::find: resolves a script string to an object (3.2). The root is returned for
    // "0".
    public SimObject? Find(string s)
    {
        if (s.Length == 0)
            return null;
        SimObject? current;
        int pos;
        if (IsDigit(s[0]))
        {
            int slash = s.IndexOfAny(new[] { '/', '\\' });
            int id = AtoiWrap(slash < 0 ? s : s[..slash]);
            current = id == 0 ? this : FindById(id);
            if (slash < 0 || current == null)
                return current;
            if (current.AsSet() == null)
                return null;
            pos = slash + 1;
        }
        else
        {
            pos = 0;
            while (pos < s.Length && (s[pos] == ' ' || (s[pos] >= '\t' && s[pos] <= '\r')))
                ++pos;
            current = this;
        }
        for (;;)
        {
            int end = pos <= s.Length ? s.IndexOfAny(new[] { '/', '\\' }, Math.Min(pos, s.Length)) : -1;
            string part = pos >= s.Length ? "" : s.Substring(pos, (end < 0 ? s.Length : end) - pos);
            string? name = part.Length == 0 ? null : mStrings.Lookup(part);
            if (name == null)
                return null;
            SimSet? container = current.AsSet();
            if (container == null)
                return null;
            SimGroup? group = container.AsGroup();
            SimObject? next = group != null ? group.FindChild(name) : container.FindMemberByName(name);
            if (next == null)
                return null;
            if (end < 0)
                return next;
            if (next.AsSet() == null)
                return null;
            current = next;
            pos = end + 1;
        }
    }

    // src/sim/sim_manager.cpp SimManager::setObjectId: 8 setObjectId.
    public void SetObjectId(SimObject obj, int id)
    {
        DictErase(obj);
        if (obj.mOwnsPoolId)
        {
            FreeId(obj.mId);
            obj.mOwnsPoolId = false;
        }
        if (id == 0)
        {
            id = AllocateId();
            obj.mOwnsPoolId = true;
        }
        obj.mId = id;
        DictInsert(obj);
    }

    // src/sim/sim_manager.cpp SimManager::insertEvent: 004fdf2c, upper bound by float time.
    private void InsertEvent(Event e)
    {
        LinkedListNode<Event>? it = mEvents.First;
        while (it != null && it.Value.Time <= e.Time)
            it = it.Next;
        if (it == null)
            mEvents.AddLast(e);
        else
            mEvents.AddBefore(it, e);
    }

    // src/sim/sim_manager.cpp SimManager::postConsoleEvent: queues console text for dest at time t (EV5: after every
    // event with time <= t).
    public void PostConsoleEvent(SimObject? dest, float t, string text) =>
        InsertEvent(new Event { Time = t, Dest = dest, Text = text });

    // src/sim/sim_manager.cpp SimManager::postEvent: queues an engine event: action runs when the event is dispatched.
    // Same queue and order as console events; unregistering dest cancels it (RD1). With a sender, unregistering the
    // sender cancels the event too: a later object may take its id and its address.
    public void PostEvent(SimObject? dest, float t, Action action, SimObject? sender = null) =>
        InsertEvent(new Event { Time = t, Dest = dest, Action = action, Sender = sender });

    // src/sim/sim_manager.cpp SimManager::advanceTo: SimManager_advanceToTime (TM6, 004ffb40), no-op when t equals the
    // last target; else dispatches every event with time <= the target in order. Returns the number of events run. The
    // target is re-read for every pop: resetSimTime inside an event sets it to 0.
    public int AdvanceTo(float t)
    {
        if (t == mTargetTime)
            return 0;
        mTargetTime = t;
        int count = 0;
        for (;;)
        {
            // 004fe120: nothing due sets the queue time to the target; a popped event raises it to its own time.
            if (mEvents.Count == 0 || mTargetTime < mEvents.First!.Value.Time)
            {
                mQueueTime = mTargetTime;
                break;
            }
            Event e = mEvents.First!.Value;
            mEvents.RemoveFirst();
            if (mQueueTime < e.Time)
                mQueueTime = e.Time;
            if (e.Action != null)
                e.Action();
            else
                mConsoleHandler?.Invoke(e.Dest, e.Text);
            ++count;
        }
        return count;
    }

    // src/sim/sim_manager.cpp SimManager::resetTime: resetSimTime (TM3, 004fdec4), every later event minus the first
    // event's time, then the first to 0.
    public void ResetTime()
    {
        if (mEvents.Count != 0)
        {
            float bas = mEvents.First!.Value.Time;
            for (LinkedListNode<Event>? it = mEvents.First.Next; it != null; it = it.Next)
                it.Value.Time = Fpu.SubF(it.Value.Time, bas);
            mEvents.First.Value.Time = 0;
        }
        mQueueTime = 0;
        mTargetTime = 0;
    }
}
