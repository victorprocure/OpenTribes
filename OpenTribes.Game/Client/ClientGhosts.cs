// Client GhostManager (docs/client-net.md 3.1, docs/game-stream-ghosts.md 2): read of the ghost block (00552cc8),
// creation and deletion of client ghost objects, the always phase list. The per-class unpack routines sit next to the
// pack routine of each class (GhostClasses, PlayerGhost, ProjectileGhost) and register here.
namespace OpenTribes.Game;

using System.Globalization;

/// <summary>A client ghost object: the decoded state of the class, as the unpack routines leave it (docs/game-stream-ghosts.md 3.2 to 3.6).</summary>
public sealed class ClientGhost
{
    /// <summary>The ghost index.</summary>
    public int Index { get; set; }
    /// <summary>The class tag.</summary>
    public uint Tag { get; set; }
    /// <summary>The class name.</summary>
    public string Class { get; set; } = "";
    /// <summary>The 32-bit priority value of the always phase.</summary>
    public long ObjectValue { get; set; } = -1;
    /// <summary>True once registered with the client manager (object +0x44 is not 0).</summary>
    public bool Registered { get; set; }
    /// <summary>The number of updates received.</summary>
    public int Updates { get; set; }
    /// <summary>The decoded state by field name.</summary>
    public Dictionary<string, object?> State { get; } = new(StringComparer.Ordinal);
    /// <summary>The fields of the last update.</summary>
    public List<DecodedField> Last { get; set; } = [];
}

/// <summary>Reads the update of one class into the reader output.</summary>
/// <param name="r">The reader.</param>
/// <param name="g">The ghost.</param>
/// <param name="isNew">True for the creating update, when the object is not registered.</param>
public delegate void ClientUnpackFn(ClientReader r, ClientGhost g, bool isNew);

/// <summary>One ghost entry as read from the stream.</summary>
public sealed class DecodedGhost
{
    /// <summary>The ghost index.</summary>
    public int Index { get; set; }
    /// <summary>The operation: "new", "update" or "delete".</summary>
    public string Op { get; set; } = "";               // "new", "update" or "delete"
    /// <summary>The class name.</summary>
    public string Class { get; set; } = "";
    /// <summary>The class tag.</summary>
    public uint Tag { get; set; }
    /// <summary>The priority value of the always phase.</summary>
    public long ObjectValue { get; set; } = -1;
    /// <summary>The number of bits the entry took.</summary>
    public int Bits { get; set; }
    /// <summary>The decoded fields.</summary>
    public List<DecodedField> Fields { get; set; } = [];
    /// <summary>The one line text.</summary>
    public string Text { get; set; } = "";
}

/// <summary>The ghost block of a packet as read from the stream.</summary>
public sealed class DecodedGhostBlock
{
    /// <summary>True for the always phase.</summary>
    public bool Always { get; set; }
    /// <summary>The number of bits of a ghost index.</summary>
    public int IdBits { get; set; }
    /// <summary>True when the phase is done.</summary>
    public bool Done { get; set; }
    /// <summary>The number of bits the block took.</summary>
    public int Bits { get; set; }
    /// <summary>The entries.</summary>
    public List<DecodedGhost> Updates { get; } = [];
}

/// <summary>The client GhostManager: reads the ghost block, creates and deletes client ghost objects and keeps the always phase list.</summary>
/// <remarks>See docs/client-net.md 3.1 and docs/game-stream-ghosts.md 2. The per-class unpack routines sit next to the pack routine of each class (GhostClasses, PlayerGhost, ProjectileGhost) and register here.</remarks>
public sealed class ClientGhostManager
{
    private readonly ClientWorld _world;
    private readonly ClientGhost?[] _table = new ClientGhost?[1024];
    private readonly List<ClientGhost> _always = [];           // always phase ghosts waiting for registration (2.7)
    /// <summary>Called when a ghost is created.</summary>
    public Action<ClientGhost>? OnCreate { get; set; }
    /// <summary>Called when a ghost is deleted.</summary>
    public Action<ClientGhost>? OnDelete { get; set; }
    /// <summary>Called once when the always phase completes.</summary>
    public Action? OnAlwaysDone { get; set; }

    private static readonly Dictionary<uint, (string Name, ClientUnpackFn Fn)> sClasses = [];
    private static readonly Lock Lock = new();

    /// <summary>Creates a manager for a client world.</summary>
    /// <param name="world">The client world.</param>
    public ClientGhostManager(ClientWorld world)
    {
        _world = world;
        EnsureClasses();
    }

    // Registers the unpack routines of every class once (next to each pack routine).
    private static void EnsureClasses()
    {
        lock (Lock)
        {
            if (sClasses.Count != 0)
                return;
            GhostClasses.RegisterClient();
            PlayerGhost.RegisterClient();
            ProjectileGhost.RegisterClient();
        }
    }

    /// <summary>Registers the unpack routine of a class.</summary>
    /// <param name="tag">The class tag.</param>
    /// <param name="name">The class name.</param>
    /// <param name="fn">The unpack routine.</param>
    public static void RegisterClass(uint tag, string name, ClientUnpackFn fn) => sClasses[tag] = (name, fn);

    /// <summary>Gets the registered classes by tag.</summary>
    public static IReadOnlyDictionary<uint, (string Name, ClientUnpackFn Fn)> Classes
    {
        get
        {
            EnsureClasses();
            return sClasses;
        }
    }

    /// <summary>Returns a ghost by index.</summary>
    /// <param name="index">The ghost index.</param>
    /// <returns>The ghost, or null.</returns>
    public ClientGhost? Get(int index) => index >= 0 && index < _table.Length ? _table[index] : null;
    /// <summary>Returns the class name of a ghost.</summary>
    /// <param name="index">The ghost index.</param>
    /// <returns>The class name, or null.</returns>
    public string? ClassOf(int index) => Get(index)?.Class;
    /// <summary>Returns every ghost in the table.</summary>
    /// <returns>The ghosts.</returns>
    public IEnumerable<ClientGhost> All() => _table.Where(g => g != null)!;
    /// <summary>Gets the number of ghosts in the table.</summary>
    public int Count => _table.Count(g => g != null);

    /// <summary>Puts a ghost into the table without a ghost block. For tests and tools that build the client state directly.</summary>
    /// <param name="g">The ghost.</param>
    public void Insert(ClientGhost g)
    {
        _table[g.Index] = g;
        g.Registered = true;
    }

    /// <summary>Clears the table.</summary>
    public void Clear()
    {
        Array.Clear(_table);
        _always.Clear();
    }

    /// <summary>Reads the ghost block of a packet. The entries decode into the returned block and the table follows docs/client-net.md 3.1.</summary>
    /// <param name="r">The reader.</param>
    /// <returns>The decoded block.</returns>
    /// <remarks>Follows GhostManager readPacket (00552cc8).</remarks>
    public DecodedGhostBlock Read(ClientReader r)
    {
        var start = r.Pos;
        var block = new DecodedGhostBlock
        {
            Always = r.Flag(),
            IdBits = (int)r.Int(3) + 3
        };
        try
        {
            while (r.Flag())
            {
                var idx = (int)r.Int((uint)block.IdBits);
                if (idx >= _table.Length)
                    throw new UnsupportedException("ghost index " + idx);
                if (r.Flag())
                {
                    var old = _table[idx];
                    _table[idx] = null;
                    if (old != null)
                    {
                        _always.Remove(old);
                        OnDelete?.Invoke(old);
                        Unlink(old);
                    }
                    ++_world.GhostDeletes;
                    block.Updates.Add(new DecodedGhost { Index = idx, Op = "delete", Class = old?.Class ?? "", Text = "ghost " + idx + " delete" });
                    continue;
                }
                var g = _table[idx];
                var isNew = g == null;
                long objValue = -1;
                if (isNew)
                {
                    if (block.Always)
                        objValue = r.Int(32);
                    var tag = r.Int(10);
                    if (!Classes.TryGetValue(tag, out var cls))
                        throw new UnsupportedException("new ghost " + idx + " class 0x" + tag.ToString("x", CultureInfo.InvariantCulture) + " objid=" + (block.Always ? objValue.ToString(CultureInfo.InvariantCulture) : "None"));
                    g = new ClientGhost { Index = idx, Tag = tag, Class = cls.Name, ObjectValue = objValue };
                }
                (string Name, var Fn) = Classes[g!.Tag];
                var bodyStart = r.Pos;
                var save = r.Out;
                var fields = new List<DecodedField>();
                r.Out = fields;
                try
                {
                    Fn(r, g, isNew);
                }
                finally
                {
                    r.Out = save;
                }
                foreach (var f in fields)
                    g.State[f.Name] = f.Value;
                g.Last = fields;
                ++g.Updates;
                ++_world.GhostUpdates;
                _world.GhostCounts[g.Class] = _world.GhostCounts.GetValueOrDefault(g.Class) + 1;
                if (isNew)
                {
                    _table[idx] = g;
                    if (block.Always)
                        _always.Add(g);
                    else
                        Register(g);
                    OnCreate?.Invoke(g);
                }
                var dg = new DecodedGhost
                {
                    Index = idx,
                    Op = isNew ? "new" : "update",
                    Class = g.Class,
                    Tag = g.Tag,
                    ObjectValue = objValue,
                    Bits = r.Pos - bodyStart,
                    Fields = fields,
                };
                dg.Text = "ghost " + idx + (isNew ? " NEW " : " ") + g.Class + (isNew && block.Always ? " obj=" + objValue : "") + " (" + dg.Bits +
                          " bits) " + ClientPsc.FieldsText(fields);
                block.Updates.Add(dg);
            }
            if (block.Always)
            {
                block.Done = r.Flag();
                if (block.Done)
                    RegisterAlways();
            }
        }
        finally
        {
            block.Bits = r.Pos - start;
        }
        return block;
    }

    // Net flag bookkeeping of a registered ghost (3.1 "After a new ghost").
    private static void Register(ClientGhost g) => g.Registered = true;

    // GhostManager_registerAlwaysGhosts 005531fc: sorted by the 32 bit value, registered, then onClientGhostAlwaysDone().
    private void RegisterAlways()
    {
        foreach (var g in _always.OrderBy(x => (ulong)x.ObjectValue))
            Register(g);
        _always.Clear();
        _world.AlwaysDone = true;
        ++_world.AlwaysDoneCount;
        OnAlwaysDone?.Invoke();
    }

    // The control and owner links the GameBase part sets on the PlayerManager records (3.2).
    private void Unlink(ClientGhost g)
    {
        foreach (var rec in _world.Players.Clients.Values)
        {
            if (rec.ControlGhost == g.Index)
                rec.ControlGhost = -1;
            if (rec.OwnedGhost == g.Index)
                rec.OwnedGhost = -1;
        }
    }
}
