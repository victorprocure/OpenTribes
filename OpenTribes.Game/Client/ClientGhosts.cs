// Client GhostManager (docs/client-net.md 3.1, docs/game-stream-ghosts.md 2): read of the ghost block (00552cc8),
// creation and deletion of client ghost objects, the always phase list. The per-class unpack routines sit next to the
// pack routine of each class (GhostClasses, PlayerGhost, ProjectileGhost) and register here.
using System.Globalization;

namespace OpenTribes.Game;

// A client ghost object: the decoded state of the class, as the unpack routines of 3.2 .. 3.6 leave it.
public sealed class ClientGhost
{
    public int Index;
    public uint Tag;
    public string Class = "";
    public long ObjectValue = -1;        // the 32 bit priority value of the always phase
    public bool Registered;              // registered with the client manager (object +0x44 != 0)
    public int Updates;
    public readonly Dictionary<string, object?> State = new(StringComparer.Ordinal);
    public List<DecodedField> Last = new();
}

// Unpack routine of one class: reads the update into r.Out; `isNew` is the creating update (object not registered).
public delegate void ClientUnpackFn(ClientReader r, ClientGhost g, bool isNew);

public sealed class DecodedGhost
{
    public int Index;
    public string Op = "";               // "new", "update" or "delete"
    public string Class = "";
    public uint Tag;
    public long ObjectValue = -1;
    public int Bits;
    public List<DecodedField> Fields = new();
    public string Text = "";
}

public sealed class DecodedGhostBlock
{
    public bool Always;
    public int IdBits;
    public bool Done;
    public int Bits;
    public readonly List<DecodedGhost> Updates = new();
}

public sealed class ClientGhostManager
{
    private readonly ClientWorld mWorld;
    private readonly ClientGhost?[] mTable = new ClientGhost?[1024];
    private readonly List<ClientGhost> mAlways = new();           // always phase ghosts waiting for registration (2.7)
    public Action<ClientGhost>? OnCreate;
    public Action<ClientGhost>? OnDelete;
    public Action? OnAlwaysDone;

    private static readonly Dictionary<uint, (string Name, ClientUnpackFn Fn)> sClasses = new();
    private static readonly object sLock = new();

    public ClientGhostManager(ClientWorld world)
    {
        mWorld = world;
        EnsureClasses();
    }

    // Registers the unpack routines of every class once (next to each pack routine).
    private static void EnsureClasses()
    {
        lock (sLock)
        {
            if (sClasses.Count != 0)
                return;
            GhostClasses.RegisterClient();
            PlayerGhost.RegisterClient();
            ProjectileGhost.RegisterClient();
        }
    }

    public static void RegisterClass(uint tag, string name, ClientUnpackFn fn) => sClasses[tag] = (name, fn);

    public static IReadOnlyDictionary<uint, (string Name, ClientUnpackFn Fn)> Classes
    {
        get
        {
            EnsureClasses();
            return sClasses;
        }
    }

    public ClientGhost? Get(int index) => index >= 0 && index < mTable.Length ? mTable[index] : null;
    public string? ClassOf(int index) => Get(index)?.Class;
    public IEnumerable<ClientGhost> All() => mTable.Where(g => g != null)!;
    public int Count => mTable.Count(g => g != null);

    public void Clear()
    {
        Array.Clear(mTable);
        mAlways.Clear();
    }

    // GhostManager readPacket 00552cc8. Entries decode into the returned block; the table follows 3.1.
    public DecodedGhostBlock Read(ClientReader r)
    {
        int start = r.Pos;
        var block = new DecodedGhostBlock { Always = r.Flag() };
        block.IdBits = (int)r.Int(3) + 3;
        try
        {
            while (r.Flag())
            {
                int idx = (int)r.Int((uint)block.IdBits);
                if (idx >= mTable.Length)
                    throw new UnsupportedException("ghost index " + idx);
                if (r.Flag())
                {
                    ClientGhost? old = mTable[idx];
                    mTable[idx] = null;
                    if (old != null)
                    {
                        mAlways.Remove(old);
                        OnDelete?.Invoke(old);
                        Unlink(old);
                    }
                    ++mWorld.GhostDeletes;
                    block.Updates.Add(new DecodedGhost { Index = idx, Op = "delete", Class = old?.Class ?? "", Text = "ghost " + idx + " delete" });
                    continue;
                }
                ClientGhost? g = mTable[idx];
                bool isNew = g == null;
                long objValue = -1;
                if (isNew)
                {
                    if (block.Always)
                        objValue = r.Int(32);
                    uint tag = r.Int(10);
                    if (!Classes.TryGetValue(tag, out var cls))
                        throw new UnsupportedException("new ghost " + idx + " class 0x" + tag.ToString("x", CultureInfo.InvariantCulture) + " objid=" + (block.Always ? objValue.ToString(CultureInfo.InvariantCulture) : "None"));
                    g = new ClientGhost { Index = idx, Tag = tag, Class = cls.Name, ObjectValue = objValue };
                }
                (string Name, ClientUnpackFn Fn) c = Classes[g!.Tag];
                int bodyStart = r.Pos;
                var save = r.Out;
                var fields = new List<DecodedField>();
                r.Out = fields;
                try
                {
                    c.Fn(r, g, isNew);
                }
                finally
                {
                    r.Out = save;
                }
                foreach (DecodedField f in fields)
                    g.State[f.Name] = f.Value;
                g.Last = fields;
                ++g.Updates;
                ++mWorld.GhostUpdates;
                mWorld.GhostCounts[g.Class] = mWorld.GhostCounts.GetValueOrDefault(g.Class) + 1;
                if (isNew)
                {
                    mTable[idx] = g;
                    if (block.Always)
                        mAlways.Add(g);
                    else
                        Register(g);
                    OnCreate?.Invoke(g);
                }
                var dg = new DecodedGhost
                {
                    Index = idx, Op = isNew ? "new" : "update", Class = g.Class, Tag = g.Tag, ObjectValue = objValue,
                    Bits = r.Pos - bodyStart, Fields = fields,
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
        foreach (ClientGhost g in mAlways.OrderBy(x => (ulong)x.ObjectValue))
            Register(g);
        mAlways.Clear();
        mWorld.AlwaysDone = true;
        OnAlwaysDone?.Invoke();
    }

    // The control and owner links the GameBase part sets on the PlayerManager records (3.2).
    private void Unlink(ClientGhost g)
    {
        foreach (ClientPlayerRecord rec in mWorld.Players.Clients.Values)
        {
            if (rec.ControlGhost == g.Index)
                rec.ControlGhost = -1;
            if (rec.OwnedGhost == g.Index)
                rec.OwnedGhost = -1;
        }
    }
}
