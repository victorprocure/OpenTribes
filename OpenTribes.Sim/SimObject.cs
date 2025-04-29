// Port of src/sim/sim_object.hpp and src/sim/sim_object.cpp: simulation objects (docs/sim.md 4): SimObject, SimSet,
// SimGroup. Names are interned strings (StringTable) compared by reference, as the C++ compares the pointers. The C++
// `delete` of an object is Destroy(): the destructors of SimSet and SimGroup delete their members.
namespace OpenTribes.Sim;

// src/sim/sim_object.hpp class SimObject.
public abstract class SimObject
{
    internal int mId;
    internal string? mName;
    internal SimGroup? mGroup;
    internal SimManager? mManager;
    internal uint mSequence;
    internal bool mOwnsPoolId;
    internal bool mRemoved;
    private readonly List<KeyValuePair<string, string>> mFields = new();
    internal readonly List<SimSet> mInSets = new(); // sets that hold this object (delete notification)

    // C++ RTTI-style class name shown by listObjects (e.g. "SimSet").
    public abstract string ClassName();
    // Creation arguments after the class name (OB3). Base SimObject accepts none.
    public virtual bool ProcessArguments(IReadOnlyList<string> args) => args.Count == 0;
    public virtual bool OnAdd() => true;
    public virtual void OnRemove() { }
    // SimGroup::addObject / removeObject notifications (vtable +0x3c, +0x40).
    public virtual void OnGroupAdd() { }
    public virtual void OnGroupRemove() { }
    // Typed (persist) field assignment from instant slots. False: not a persist field.
    public virtual bool SetPersistField(string field, IReadOnlyList<string> values) => false;
    // Stored persist value, for engine code that reads mission fields (scripts cannot).
    public virtual IReadOnlyList<string>? PersistValue(string field) => null;
    // Element of an array persist field (`textures[3] = ...`). False: not one.
    public virtual bool SetPersistElement(string field, int index, IReadOnlyList<string> values) => false;
    // Id fixed by the constructor (ID1), or 0 for a pool id.
    public virtual int FixedId() => 0;
    // Net flags (+0x4c) given the class constructor's; objects that change them after construction override this
    // (docs/game-stream-ghosts.md 6.1).
    public virtual uint NetFlags(uint constructed) => constructed;

    // Per-tick server update (GameBase vtable +0x158): once per 32 ms tick for every member of sets 718, 719 and 727,
    // with the tick clock in ms (docs/sim.md TK3).
    public virtual void ServerTick(uint tickMs) { }
    // SimTimerSet notification (event 0x40a) for members of set 12 (docs/sim.md TK6).
    public virtual void OnTimerEvent(float time, int timerId, float interval) { }

    // Event 0x420 (the console `activateGroup`) with its value; SimLight sets its colour (0x0052d290).
    public virtual void OnActivateEvent(float value) { }

    public virtual SimSet? AsSet() => null;
    public virtual SimGroup? AsGroup() => null;
    // asGameBase: GameBase is a class of src/game; the port casts with `as GameBase` (OpenTribes.Game).

    // The C++ destructor, run by delete (SimManager::deleteObject, the SimSet and SimGroup destructors).
    public virtual void Destroy() { }

    public int Id() => mId;
    public string? Name() => mName; // interned, null when unnamed
    public SimGroup? Group() => mGroup;
    public SimManager? Manager() => mManager;
    public uint Sequence() => mSequence;

    // src/sim/sim_object.cpp SimObject::dataField: dynamic data fields (OB4), interned name -> value, newest first.
    public string? DataField(string name)
    {
        for (int i = mFields.Count - 1; i >= 0; --i)
            if (ReferenceEquals(mFields[i].Key, name))
                return mFields[i].Value;
        return null;
    }

    // src/sim/sim_object.cpp SimObject::setDataField.
    public void SetDataField(string name, string value)
    {
        int it = mFields.FindIndex(f => ReferenceEquals(f.Key, name));
        if (value.Length == 0)
        {
            if (it >= 0)
                mFields.RemoveAt(it);
            return;
        }
        if (it >= 0)
            mFields[it] = new KeyValuePair<string, string>(name, value);
        else
            mFields.Add(new KeyValuePair<string, string>(name, value));
    }
}

// src/sim/sim_object.hpp class SimSet.
public class SimSet : SimObject
{
    protected readonly List<SimObject> mMembers = new();
    protected bool mOwnsMembers;

    // src/sim/sim_object.cpp SimSet::~SimSet.
    public override void Destroy()
    {
        if (mOwnsMembers)
            foreach (SimObject m in mMembers)
                m.Destroy();
    }

    public override string ClassName() => "SimSet";

    // src/sim/sim_object.cpp SimSet::processArguments.
    public override bool ProcessArguments(IReadOnlyList<string> args)
    {
        if (args.Count == 1)
            mOwnsMembers = SimConsole.CAtoi(args[0]) != 0;
        return true;
    }

    public override SimSet? AsSet() => this;

    public IReadOnlyList<SimObject> Members() => mMembers;

    // src/sim/sim_object.cpp SimSet::addObject (GS2).
    public virtual void AddObject(SimObject obj)
    {
        if (mMembers.Contains(obj))
            return;
        mMembers.Add(obj);
        obj.mInSets.Add(this);
    }

    // src/sim/sim_object.cpp SimSet::removeObject (GS4 swap-remove).
    public virtual void RemoveObject(SimObject obj)
    {
        int it = mMembers.IndexOf(obj);
        if (it < 0)
            return;
        mMembers[it] = mMembers[^1]; // swap-remove (GS4)
        mMembers.RemoveAt(mMembers.Count - 1);
        obj.mInSets.RemoveAll(s => s == this);
    }

    // src/sim/sim_object.cpp SimSet::findMemberByName.
    public SimObject? FindMemberByName(string interned)
    {
        foreach (SimObject m in mMembers)
            if (ReferenceEquals(m.mName, interned))
                return m;
        return null;
    }

    // src/sim/sim_object.cpp SimSet::onRemove.
    public override void OnRemove()
    {
        var members = new List<SimObject>(mMembers);
        if (mOwnsMembers)
        {
            foreach (SimObject m in members)
                if (m.Manager() != null)
                    Manager()!.UnregisterObject(m);
            return; // the destructor deletes them
        }
        foreach (SimObject m in members)
            m.mInSets.RemoveAll(s => s == this);
        mMembers.Clear();
    }

    // src/sim/sim_object.cpp SimSet::onAdd: registers members added before this group was registered, in order (IN4). A
    // member whose onAdd fails is reported and deleted after the loop (0x004fed68, mission.md 1.2).
    public override bool OnAdd()
    {
        var failed = new List<SimObject>();
        for (int i = 0; i < mMembers.Count; ++i)
        {
            SimObject m = mMembers[i];
            if (m.Manager() != null || Manager()!.RegisterObject(m))
                continue;
            Manager()!.Print?.Invoke("Failed to registerObject of type: " + m.ClassName() + " (" + (m.Name() ?? "<Unnamed>") + ")");
            failed.Add(m);
        }
        foreach (SimObject m in failed)
            Manager()!.DeleteObject(m);
        return true;
    }
}

// src/sim/sim_object.hpp class SimGroup.
public class SimGroup : SimSet
{
    private readonly List<SimObject> mNamed = new(); // newest at the back

    // src/sim/sim_object.cpp SimGroup::~SimGroup.
    public override void Destroy()
    {
        foreach (SimObject m in mMembers)
            m.Destroy();
        mMembers.Clear();
    }

    public override string ClassName() => "SimGroup";
    public override bool ProcessArguments(IReadOnlyList<string> args) => true;
    public override SimGroup? AsGroup() => this;
    // TeamGroup answers with its team (docs/mission.md GB2).
    public virtual bool TeamOf(ref int team) => false;

    // src/sim/sim_object.cpp SimGroup::onRemove: members are unregistered newest first; they stay in the group for the
    // destructor (RD2).
    public override void OnRemove()
    {
        var members = new List<SimObject>(mMembers);
        members.Sort((a, b) => b.Sequence().CompareTo(a.Sequence()));
        foreach (SimObject m in members)
        {
            if (m.Manager() == null)
                continue;
            Manager()!.UnregisterObject(m);
            m.mGroup = this;
            if (!mMembers.Contains(m))
                mMembers.Add(m);
        }
    }

    // src/sim/sim_object.cpp SimGroup::addObject (GS3).
    public override void AddObject(SimObject obj)
    {
        if (obj.mGroup == this)
            return;
        obj.mGroup?.RemoveObject(obj);
        obj.mGroup = this;
        mMembers.Add(obj);
        NameInsert(obj);
        obj.OnGroupAdd();
    }

    // src/sim/sim_object.cpp SimGroup::removeObject.
    public override void RemoveObject(SimObject obj)
    {
        int it = mMembers.IndexOf(obj);
        if (it < 0)
            return;
        NameErase(obj);
        mMembers[it] = mMembers[^1];
        mMembers.RemoveAt(mMembers.Count - 1);
        obj.OnGroupRemove();
        obj.mGroup = null;
    }

    // src/sim/sim_object.cpp SimGroup::findChild: name dictionary of direct children; newest insertion wins (NM3).
    public SimObject? FindChild(string interned)
    {
        for (int i = mNamed.Count - 1; i >= 0; --i)
            if (ReferenceEquals(mNamed[i].mName, interned))
                return mNamed[i];
        return null;
    }

    // src/sim/sim_object.cpp SimGroup::assignName.
    public void AssignName(SimObject obj, string? interned)
    {
        if (obj.mGroup == this)
            NameErase(obj);
        obj.mName = interned;
        if (obj.mGroup == this)
            NameInsert(obj);
    }

    // src/sim/sim_object.cpp SimGroup::nameInsert.
    private void NameInsert(SimObject obj)
    {
        if (obj.mName != null)
            mNamed.Add(obj);
    }

    // src/sim/sim_object.cpp SimGroup::nameErase.
    private void NameErase(SimObject obj) => mNamed.RemoveAll(o => o == obj);
}
