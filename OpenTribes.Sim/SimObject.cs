namespace OpenTribes.Sim;

/// <summary>The base of all simulation objects (docs/sim.md 4).</summary>
/// <remarks>Port of src/sim/sim_object.cpp. Names are interned strings (<see cref="Core.StringTable"/>) compared by reference, as the C++ compares the pointers. The C++ <c>delete</c> of an object is <see cref="Destroy"/>, and the destructors of <see cref="SimSet"/> and <see cref="SimGroup"/> delete their members.</remarks>
public abstract class SimObject
{
    internal int mId { get; set; }
    internal string? mName { get; set; }
    internal SimGroup? mGroup { get; set; }
    internal SimManager? mManager { get; set; }
    internal uint mSequence { get; set; }
    internal bool OwnsPoolId { get; set; }
    internal bool Removed { get; set; }
    private readonly List<KeyValuePair<string, string>> _fields = [];
    internal List<SimSet> InSets { get; } = []; // sets that hold this object (delete notification)

    /// <summary>Gets the C++ RTTI-style class name that <c>listObjects</c> shows, for example <c>SimSet</c>.</summary>
    /// <returns>The class name.</returns>
    public abstract string ClassName();
    /// <summary>Takes the creation arguments after the class name (OB3). The base object accepts none.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>False when the arguments are not accepted.</returns>
    public virtual bool ProcessArguments(IReadOnlyList<string> args) => args.Count == 0;
    /// <summary>Called when the object is registered.</summary>
    /// <returns>False to refuse the registration.</returns>
    public virtual bool OnAdd() => true;
    /// <summary>Called when the object is unregistered.</summary>
    public virtual void OnRemove() { }
    /// <summary>Called when a <see cref="SimGroup"/> adds the object (vtable +0x3c).</summary>
    public virtual void OnGroupAdd() { }
    /// <summary>Called when a <see cref="SimGroup"/> removes the object (vtable +0x40).</summary>
    public virtual void OnGroupRemove() { }
    /// <summary>Assigns a typed (persist) field from instant slots.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="values">The values.</param>
    /// <returns>False when the field is not a persist field.</returns>
    public virtual bool SetPersistField(string field, IReadOnlyList<string> values) => false;
    /// <summary>Gets the stored persist value, for engine code that reads mission fields. Scripts cannot read it.</summary>
    /// <param name="field">The field name.</param>
    /// <returns>The values, or null.</returns>
    public virtual IReadOnlyList<string>? PersistValue(string field) => null;
    /// <summary>Assigns one element of an array persist field, for example <c>textures[3] = ...</c>.</summary>
    /// <param name="field">The field name.</param>
    /// <param name="index">The element index.</param>
    /// <param name="values">The values.</param>
    /// <returns>False when the field is not an array persist field.</returns>
    public virtual bool SetPersistElement(string field, int index, IReadOnlyList<string> values) => false;
    /// <summary>Gets the id the constructor fixes (ID1).</summary>
    /// <returns>The id, or 0 for a pool id.</returns>
    public virtual int FixedId() => 0;
    /// <summary>Gets the net flags (+0x4c). Objects that change them after construction override this (docs/game-stream-ghosts.md 6.1).</summary>
    /// <param name="constructed">The flags the class constructor sets.</param>
    /// <returns>The flags.</returns>
    public virtual uint NetFlags(uint constructed) => constructed;

    /// <summary>Runs the per-tick server update, once per 32 ms tick for every member of sets 718, 719 and 727 (docs/sim.md TK3).</summary>
    /// <param name="tickMs">The tick clock in milliseconds.</param>
    /// <remarks>Follows the GameBase vtable +0x158 entry.</remarks>
    public virtual void ServerTick(uint tickMs) { }
    /// <summary>Handles the SimTimerSet notification (event 0x40a) for members of set 12 (docs/sim.md TK6).</summary>
    /// <param name="time">The event time.</param>
    /// <param name="timerId">The timer id.</param>
    /// <param name="interval">The timer interval.</param>
    public virtual void OnTimerEvent(float time, int timerId, float interval) { }

    /// <summary>Handles event 0x420 (the console <c>activateGroup</c>). SimLight sets its colour (0052d290).</summary>
    /// <param name="value">The event value.</param>
    public virtual void OnActivateEvent(float value) { }

    /// <summary>Casts to <see cref="SimSet"/>.</summary>
    /// <returns>The object as a set, or null.</returns>
    public virtual SimSet? AsSet() => null;
    /// <summary>Casts to <see cref="SimGroup"/>.</summary>
    /// <returns>The object as a group, or null.</returns>
    /// <remarks>The stock <c>asGameBase</c> has no member here: GameBase is a class of src/game and the port casts with <c>as GameBase</c> (OpenTribes.Game).</remarks>
    public virtual SimGroup? AsGroup() => null;
    // asGameBase: GameBase is a class of src/game; the port casts with `as GameBase` (OpenTribes.Game).

    /// <summary>Runs the C++ destructor, called by <c>delete</c> (SimManager::deleteObject and the <see cref="SimSet"/> and <see cref="SimGroup"/> destructors).</summary>
    public virtual void Destroy() { }

    /// <summary>Gets the object id.</summary>
    /// <returns>The id.</returns>
    public int Id() => mId;
    /// <summary>Gets the object name.</summary>
    /// <returns>The interned name, or null when unnamed.</returns>
    public string? Name() => mName;
    /// <summary>Gets the group that holds the object.</summary>
    /// <returns>The group, or null.</returns>
    public SimGroup? Group() => mGroup;
    /// <summary>Gets the manager the object is registered with.</summary>
    /// <returns>The manager, or null.</returns>
    public SimManager? Manager() => mManager;
    /// <summary>Gets the registration sequence number. Groups unregister members newest first by it.</summary>
    /// <returns>The sequence number.</returns>
    public uint Sequence() => mSequence;

    /// <summary>Reads a dynamic data field (OB4). Fields are keyed by interned name and the newest wins.</summary>
    /// <param name="name">The interned field name.</param>
    /// <returns>The value, or null when unset.</returns>
    public string? DataField(string name)
    {
        for (var i = _fields.Count - 1; i >= 0; --i)
            if (ReferenceEquals(_fields[i].Key, name))
                return _fields[i].Value;
        return null;
    }

    /// <summary>Writes a dynamic data field. An empty value removes it.</summary>
    /// <param name="name">The interned field name.</param>
    /// <param name="value">The value.</param>
    public void SetDataField(string name, string value)
    {
        var it = _fields.FindIndex(f => ReferenceEquals(f.Key, name));
        if (value.Length == 0)
        {
            if (it >= 0)
                _fields.RemoveAt(it);
            return;
        }
        if (it >= 0)
            _fields[it] = new KeyValuePair<string, string>(name, value);
        else
            _fields.Add(new KeyValuePair<string, string>(name, value));
    }
}

/// <summary>A set of objects.</summary>
/// <remarks>Port of src/sim/sim_object.cpp SimSet.</remarks>
public class SimSet : SimObject
{
    /// <summary>The members in order.</summary>
    protected List<SimObject> mMembers { get; } = [];
    /// <summary>True when the set deletes its members with itself.</summary>
    protected bool OwnsMembers { get; set; }
    /// <summary>True when removal keeps the member order.</summary>
    protected bool Ordered { get; set; }

    /// <summary>Deletes the members when the set owns them.</summary>
    public override void Destroy()
    {
        if (OwnsMembers)
            foreach (var m in mMembers)
                m.Destroy();
    }

    /// <inheritdoc/>
    public override string ClassName() => "SimSet";

    /// <summary>Takes the optional argument that says whether the set owns its members.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>Always true.</returns>
    public override bool ProcessArguments(IReadOnlyList<string> args)
    {
        if (args.Count == 1)
            OwnsMembers = SimConsole.CAtoi(args[0]) != 0;
        return true;
    }

    /// <inheritdoc/>
    public override SimSet? AsSet() => this;

    /// <summary>Gets the members in order.</summary>
    /// <returns>The members.</returns>
    public IReadOnlyList<SimObject> Members() => mMembers;

    /// <summary>Sets the ordered-removal flag (+0x5d). Removal then keeps the member order (docs/sim.md GS4).</summary>
    /// <param name="ordered">True to keep the order.</param>
    public void SetOrdered(bool ordered) => Ordered = ordered;

    /// <summary>Adds an object unless it is already a member (GS2).</summary>
    /// <param name="obj">The object.</param>
    public virtual void AddObject(SimObject obj)
    {
        if (mMembers.Contains(obj))
            return;
        mMembers.Add(obj);
        obj.InSets.Add(this);
    }

    /// <summary>Removes an object. Unless the set is ordered, the last member takes its place (GS4 swap-remove).</summary>
    /// <param name="obj">The object.</param>
    /// <remarks>The ordered flag is checked at 004fe430.</remarks>
    public virtual void RemoveObject(SimObject obj)
    {
        var it = mMembers.IndexOf(obj);
        if (it < 0)
            return;
        if (Ordered)
        {
            mMembers.RemoveAt(it); // ordered flag +0x5d [code 004fe430]
        }
        else
        {
            mMembers[it] = mMembers[^1]; // swap-remove (GS4)
            mMembers.RemoveAt(mMembers.Count - 1);
        }
        obj.InSets.RemoveAll(s => s == this);
    }

    /// <summary>Finds a member by name.</summary>
    /// <param name="interned">The interned name.</param>
    /// <returns>The member, or null.</returns>
    public SimObject? FindMemberByName(string interned)
    {
        foreach (var m in mMembers)
            if (ReferenceEquals(m.mName, interned))
                return m;
        return null;
    }

    /// <summary>Unregisters the members when the set owns them, otherwise releases them.</summary>
    public override void OnRemove()
    {
        var members = new List<SimObject>(mMembers);
        if (OwnsMembers)
        {
            foreach (var m in members)
                if (m.Manager() != null)
                    Manager()!.UnregisterObject(m);
            return; // the destructor deletes them
        }
        foreach (var m in members)
            m.InSets.RemoveAll(s => s == this);
        mMembers.Clear();
    }

    /// <summary>Registers members added before the set was registered, in order (IN4). A member whose <see cref="SimObject.OnAdd"/> fails is reported and deleted after the loop.</summary>
    /// <returns>Always true.</returns>
    /// <remarks>Follows 004fed68, docs/mission.md 1.2.</remarks>
    public override bool OnAdd()
    {
        var failed = new List<SimObject>();
        for (var i = 0; i < mMembers.Count; ++i)
        {
            var m = mMembers[i];
            if (m.Manager() != null || Manager()!.RegisterObject(m))
                continue;
            Manager()!.Print?.Invoke("Failed to registerObject of type: " + m.ClassName() + " (" + (m.Name() ?? "<Unnamed>") + ")");
            failed.Add(m);
        }
        foreach (var m in failed)
            Manager()!.DeleteObject(m);
        return true;
    }
}

/// <summary>A set that owns its members and keeps a name dictionary of its direct children.</summary>
/// <remarks>Port of src/sim/sim_object.cpp SimGroup.</remarks>
public class SimGroup : SimSet
{
    private readonly List<SimObject> _named = []; // newest at the back

    /// <summary>Deletes all members.</summary>
    public override void Destroy()
    {
        foreach (var m in mMembers)
            m.Destroy();
        mMembers.Clear();
    }

    /// <inheritdoc/>
    public override string ClassName() => "SimGroup";
    /// <inheritdoc/>
    public override bool ProcessArguments(IReadOnlyList<string> args) => true;
    /// <inheritdoc/>
    public override SimGroup? AsGroup() => this;
    /// <summary>Gets the team of the group. A TeamGroup answers (docs/mission.md GB2).</summary>
    /// <param name="team">Receives the team.</param>
    /// <returns>True when the group has a team.</returns>
    public virtual bool TeamOf(ref int team) => false;

    /// <summary>Unregisters the members newest first. They stay in the group for the destructor (RD2).</summary>
    public override void OnRemove()
    {
        var members = new List<SimObject>(mMembers);
        members.Sort((a, b) => b.Sequence().CompareTo(a.Sequence()));
        foreach (var m in members)
        {
            if (m.Manager() == null)
                continue;
            Manager()!.UnregisterObject(m);
            m.mGroup = this;
            if (!mMembers.Contains(m))
                mMembers.Add(m);
        }
    }

    /// <summary>Moves an object into this group, leaving its old group (GS3).</summary>
    /// <param name="obj">The object.</param>
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

    /// <summary>Removes an object from the group and its name dictionary.</summary>
    /// <param name="obj">The object.</param>
    public override void RemoveObject(SimObject obj)
    {
        var it = mMembers.IndexOf(obj);
        if (it < 0)
            return;
        NameErase(obj);
        if (Ordered)
        {
            mMembers.RemoveAt(it);
        }
        else
        {
            mMembers[it] = mMembers[^1];
            mMembers.RemoveAt(mMembers.Count - 1);
        }
        obj.OnGroupRemove();
        obj.mGroup = null;
    }

    /// <summary>Finds a direct child by name. The newest insertion wins (NM3).</summary>
    /// <param name="interned">The interned name.</param>
    /// <returns>The child, or null.</returns>
    public SimObject? FindChild(string interned)
    {
        for (var i = _named.Count - 1; i >= 0; --i)
            if (ReferenceEquals(_named[i].mName, interned))
                return _named[i];
        return null;
    }

    /// <summary>Renames an object and updates the name dictionary when the group holds it.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="interned">The new interned name, or null.</param>
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
            _named.Add(obj);
    }

    // src/sim/sim_object.cpp SimGroup::nameErase.
    private void NameErase(SimObject obj) => _named.RemoveAll(o => o == obj);
}
