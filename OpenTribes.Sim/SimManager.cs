namespace OpenTribes.Sim;

using OpenTribes.Core;

/// <summary>The simulation manager: the root group of one world with the id pool and dictionary, path lookup, registration, events and time (docs/sim.md 1, 2, 3.2, 8.1, 8.2).</summary>
/// <remarks>Port of src/sim/sim_manager.cpp.</remarks>
public sealed class SimManager : SimGroup
{
    /// <summary>Runs console text posted as an event.</summary>
    /// <param name="target">The destination object.</param>
    /// <param name="text">The console text.</param>
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
    private readonly List<int> mFreeIds = [];
    private readonly Dictionary<int, List<SimObject>> mIds = []; // newest at the back
    private readonly LinkedList<Event> mEvents = new(); // sorted by time, FIFO for equal times (EV5)
    private float mQueueTime;  // +0x80
    private float mTargetTime; // +0x6c
    private ConsoleHandler? mConsoleHandler;

    /// <summary>Observer of every successful registration. NetObject_onAdd (0050a420) marks a ghost-always object in every ghost manager.</summary>
    public Action<SimObject>? OnRegister;
    /// <summary>Observer of every unregistration. Ghost managers drop their entries.</summary>
    public Action<SimObject>? OnUnregister;
    /// <summary>Console output of the manager itself, for registration failures in <see cref="SimSet.OnAdd"/>.</summary>
    public Action<string>? Print;

    /// <summary>Creates a manager. It is its own root, registered, and never in the id dictionary (MG7).</summary>
    /// <param name="strings">The string table for names.</param>
    public SimManager(StringTable strings)
    {
        mStrings = strings;
        mManager = this;
    }

    /// <inheritdoc/>
    public override string ClassName() => "SimManager";

    /// <summary>Gets the manager id.</summary>
    /// <returns>The id.</returns>
    public int ManagerId() => mManagerId;
    /// <summary>Sets the manager id.</summary>
    /// <param name="id">The id.</param>
    public void SetManagerId(int id) => mManagerId = id;
    /// <summary>Sets the handler that runs console events.</summary>
    /// <param name="h">The handler, or null.</param>
    public void SetConsoleHandler(ConsoleHandler? h) => mConsoleHandler = h;
    /// <summary>Gets the queue time (TM5): the time of the event being dispatched, else the last advance target. <c>getSimTime</c> and <c>schedule</c> read it.</summary>
    /// <returns>The time in seconds.</returns>
    public float Time() => mQueueTime;
    /// <summary>Gets the number of queued events.</summary>
    /// <returns>The count.</returns>
    public int PendingEvents() => mEvents.Count;
    /// <summary>Gets the string table.</summary>
    /// <returns>The table.</returns>
    public StringTable Strings() => mStrings;

    // src/sim/sim_manager.cpp isDigit.
    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    // src/sim/sim_manager.cpp atoiWrap.
    private static int AtoiWrap(string s)
    {
        uint v = 0;
        foreach (var c in s)
        {
            if (!IsDigit(c))
                break;
            v = unchecked((v * 10) + (uint)(c - '0'));
        }
        return unchecked((int)v);
    }

    /// <summary>Drops the queued events and deletes the members.</summary>
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
            var id = mFreeIds[^1];
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
            mIds[obj.mId] = v = [];
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

    /// <summary>Registers an object (OB2): sequence, id (ID1), then <see cref="SimObject.OnAdd"/>.</summary>
    /// <param name="obj">The object.</param>
    /// <returns>False if OnAdd fails. The object is then unregistered again.</returns>
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

    /// <summary>Unregisters an object (RD1). Groups take their subtree with them (RD2). Events that name the object as destination or sender are cancelled.</summary>
    /// <param name="obj">The object.</param>
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

    /// <summary>Unregisters and destroys an object. Deleting the root does nothing, where the original crashes (D11).</summary>
    /// <param name="obj">The object.</param>
    public void DeleteObject(SimObject obj)
    {
        if (obj == this)
            return; // deleteObject(0) would delete the root in the original (D11)
        UnregisterObject(obj);
        obj.mGroup?.RemoveObject(obj);
        obj.Destroy();
    }

    /// <summary>Adds an object to the root group, registering it first when needed (GS3, 004ff9dc).</summary>
    /// <param name="obj">The object.</param>
    /// <returns>False when registration fails.</returns>
    public bool AddToRoot(SimObject obj)
    {
        if (obj.mManager == null && !RegisterObject(obj))
            return false;
        if (obj.mGroup == null)
            AddObject(obj);
        return true;
    }

    /// <summary>Finds an object by id. The newest registration wins.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The object, or null.</returns>
    public SimObject? FindById(int id) =>
        mIds.TryGetValue(id, out List<SimObject>? v) && v.Count != 0 ? v[^1] : null;

    /// <summary>Resolves a script string to an object (docs/sim.md 3.2): an id, a path of names, or an id followed by a path. The root is returned for <c>0</c>.</summary>
    /// <param name="s">The script string.</param>
    /// <returns>The object, or null.</returns>
    public SimObject? Find(string s)
    {
        if (s.Length == 0)
            return null;
        SimObject? current;
        int pos;
        if (IsDigit(s[0]))
        {
            var slash = s.IndexOfAny(['/', '\\']);
            var id = AtoiWrap(slash < 0 ? s : s[..slash]);
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
        for (; ; )
        {
            var end = pos <= s.Length ? s.IndexOfAny(['/', '\\'], Math.Min(pos, s.Length)) : -1;
            var part = pos >= s.Length ? "" : s[pos..(end < 0 ? s.Length : end)];
            var name = part.Length == 0 ? null : mStrings.Lookup(part);
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

    /// <summary>Gives an object an id (console <c>setObjectId</c>, 8).</summary>
    /// <param name="obj">The object.</param>
    /// <param name="id">The id, or 0 to take one from the pool.</param>
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

    /// <summary>Queues console text for an object. It runs after every event with time up to <paramref name="t"/> (EV5).</summary>
    /// <param name="dest">The destination object.</param>
    /// <param name="t">The event time.</param>
    /// <param name="text">The console text.</param>
    public void PostConsoleEvent(SimObject? dest, float t, string text) =>
        InsertEvent(new Event { Time = t, Dest = dest, Text = text });

    /// <summary>Queues an engine event whose action runs when it is dispatched. It uses the same queue and order as console events. Unregistering <paramref name="dest"/> cancels it (RD1).</summary>
    /// <param name="dest">The destination object.</param>
    /// <param name="t">The event time.</param>
    /// <param name="action">The action to run.</param>
    /// <param name="sender">The sender. Unregistering it cancels the event too, because a later object may take its id and address.</param>
    public void PostEvent(SimObject? dest, float t, Action action, SimObject? sender = null) =>
        InsertEvent(new Event { Time = t, Dest = dest, Action = action, Sender = sender });

    /// <summary>Runs every event with time up to <paramref name="t"/> in order. Does nothing when <paramref name="t"/> equals the last target.</summary>
    /// <param name="t">The target time.</param>
    /// <returns>The number of events run.</returns>
    /// <remarks>Follows SimManager_advanceToTime (TM6, 004ffb40). The target is read again for every pop, because <c>resetSimTime</c> inside an event sets it to 0. When nothing is due the queue time becomes the target, and a popped event raises it to its own time (004fe120).</remarks>
    public int AdvanceTo(float t)
    {
        if (t == mTargetTime)
            return 0;
        mTargetTime = t;
        var count = 0;
        for (; ; )
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

    /// <summary>Moves every later event back by the time of the first event, then sets the first to 0 and the clocks to 0.</summary>
    /// <remarks>Follows resetSimTime (TM3, 004fdec4).</remarks>
    public void ResetTime()
    {
        if (mEvents.Count != 0)
        {
            var bas = mEvents.First!.Value.Time;
            for (LinkedListNode<Event>? it = mEvents.First.Next; it != null; it = it.Next)
                it.Value.Time = Fpu.SubF(it.Value.Time, bas);
            mEvents.First.Value.Time = 0;
        }
        mQueueTime = 0;
        mTargetTime = 0;
    }
}
