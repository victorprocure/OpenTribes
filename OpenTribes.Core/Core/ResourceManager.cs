namespace OpenTribes.Core;

/// <summary>Index of resources over the search directories and the mounted volumes.</summary>
/// <remarks>Port of src/core/resource_manager.cpp, rules in docs/resources.md. Names are byte strings (<see cref="ByteString"/>). Interned names are canonical, so the C++ pointer comparisons are ordinal string comparisons here.</remarks>
public sealed class ResourceManager
{
    /// <summary>Entry flag: the data is in a mounted volume.</summary>
    public const uint InVolume = 1;
    /// <summary>Entry flag: the data is a file in a search directory.</summary>
    public const uint LooseFile = 2;
    /// <summary>Entry flag: no backing file is left. Also marks entries added at run time.</summary>
    public const uint Stale = 4; // "added": no backing file left
    /// <summary>Entry flag: the volume entry is run-length encoded.</summary>
    public const uint Rle = 8;
    /// <summary>Entry flag: the volume entry is LZH compressed.</summary>
    public const uint Lzh = 0x10;

    /// <summary>A mounted volume archive.</summary>
    public sealed class Volume : IDisposable
    {
        /// <summary>The directory part of the volume's lookup name, or null.</summary>
        public string? Path;
        /// <summary>The file name part of the volume's lookup name.</summary>
        public string Name = "";
        /// <summary>The backslash separated directory that holds the .vol file.</summary>
        public string Dir = ""; // backslash directory of the .vol
        /// <summary>The number of mounts. The volume unmounts when it drops to 0.</summary>
        public int RefCount = 1;
        /// <summary>The open archive.</summary>
        public readonly VolArchive Archive = new();

        /// <inheritdoc/>
        public void Dispose() => Archive.Dispose();
    }

    /// <summary>One resource: a loose file, a volume entry or a generated resource.</summary>
    public sealed class Entry
    {
        /// <summary>The directory part of the lookup name, or null.</summary>
        public string? Path;
        /// <summary>The file name part of the lookup name.</summary>
        public string Name = "";      // file name part
        /// <summary>The real directory, backslash separated and relative to the root.</summary>
        public string FilePath = "";  // real directory (backslash separated, relative to root)
        /// <summary>The real file name. For volume entries this is the .vol file.</summary>
        public string FileName = "";  // real file name (the .vol for volume entries)
        /// <summary>The entry flags: <see cref="InVolume"/>, <see cref="LooseFile"/>, <see cref="Stale"/>, <see cref="Rle"/> and <see cref="Lzh"/>.</summary>
        public uint Flags;
        /// <summary>The volume that holds the entry, or null.</summary>
        public Volume? Volume;
        /// <summary>The index of the entry in the volume's directory.</summary>
        public int VolumeIndex;
        /// <summary>The data size in bytes.</summary>
        public uint Size;
        /// <summary>True once a load built an instance (stock +0x14). The instance keeps a stale entry alive.</summary>
        public bool HasInstance;
        /// <summary>The number of holders of the instance (stock +0x44). A purge keeps a locked entry.</summary>
        public uint Locks;
    }

    private readonly string mRoot;
    private readonly StringTable mStrings;
    private readonly List<Entry> mList = []; // creation order; iteration walks it from the back
    private readonly Dictionary<(string?, string), List<Entry>> mChains = [];
    private readonly List<Volume> mVolumes = [];

    /// <summary>Creates a manager over a root directory.</summary>
    /// <param name="rootDir">The root, a host path.</param>
    /// <param name="strings">The string table for interned names. Defaults to <see cref="StringTable.Global"/>.</param>
    public ResourceManager(string rootDir, StringTable? strings = null)
    {
        mRoot = rootDir;
        mStrings = strings ?? StringTable.Global();
    }

    // src/core/resource_manager.cpp upperAscii.
    private static char UpperAscii(char c) => (c is >= 'a' and <= 'z') ? (char)(c - 0x20) : c;

    // src/core/resource_manager.cpp ntfsLess: Windows (NTFS) enumeration order, emulated with ASCII case folding.
    private static bool NtfsLess(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; ++i)
        {
            var ca = UpperAscii(a[i]);
            var cb = UpperAscii(b[i]);
            if (ca != cb)
                return ca < cb;
        }
        return a.Length < b.Length;
    }

    // src/core/resource_manager.cpp toHost: backslashes to slashes, and the byte string to a host string.
    private static string ToHost(string s) => ByteString.ToHost(s.Replace('\\', '/'));

    // strcasecmp in the C locale: ASCII case folding only.
    private static bool EqualsAsciiNoCase(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; ++i)
            if (UpperAscii(a[i]) != UpperAscii(b[i]))
                return false;
        return true;
    }

    // stat(): true when the path exists (following links).
    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    // src/core/resource_manager.cpp resolveCase: resolves each component of a relative host path
    // case-insensitively below root.
    private static bool ResolveCase(string root, string rel, out string output)
    {
        output = root;
        var start = 0;
        while (start <= rel.Length)
        {
            var end = rel.IndexOf('/', start);
            var part = end < 0 ? rel[start..] : rel[start..end];
            start = end < 0 ? rel.Length + 1 : end + 1;
            if (part.Length == 0)
                continue;
            var candidate = output + "/" + part;
            if (!Exists(candidate))
            {
                if (!Directory.Exists(output))
                    return false;
                var found = false;
                try
                {
                    foreach (var full in Directory.EnumerateFileSystemEntries(output))
                    {
                        var entName = Path.GetFileName(full);
                        if (EqualsAsciiNoCase(entName, part))
                        {
                            candidate = output + "/" + entName;
                            found = true;
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    return false;
                }
                if (!found)
                    return false;
            }
            output = candidate;
        }
        return true;
    }

    /// <summary>Matches a name against a pattern with <c>*</c> and <c>?</c>, the original wildcard match in case-insensitive mode.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <param name="str">The name to test.</param>
    /// <returns>True when the name matches.</returns>
    public static bool WildcardMatch(string pattern, string str) => WildcardMatchAt(pattern, 0, str, 0);

    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    // strchr from index `from`: the index of c, the terminator index for '\0', or -1.
    private static int StrChr(string s, int from, char c)
    {
        for (var i = from; ; ++i)
        {
            var ch = At(s, i);
            if (ch == c)
                return i;
            if (ch == '\0')
                return -1;
        }
    }

    private static bool WildcardMatchAt(string pat, int p, string str, int s)
    {
        var ok = true;
        while (ok && At(pat, p) != '\0' && At(str, s) != '\0')
        {
            if (At(pat, p) == '*')
            {
                // Each later occurrence of the next pattern character, case-sensitive.
                var next = At(pat, p + 1);
                for (var q = StrChr(str, s, next); q >= 0; q = At(str, q) != '\0' ? StrChr(str, q + 1, next) : -1)
                    if (WildcardMatchAt(pat, p + 1, str, q))
                        return true;
                return false;
            }
            if (At(pat, p) == '?')
            {
                ++p;
                ++s;
                continue;
            }
            ok = UpperAscii(At(pat, p)) == UpperAscii(At(str, s));
            ++p;
            ++s;
        }
        if (At(pat, p) != At(str, s))
            ok = false;
        return ok;
    }

    // src/core/resource_manager.cpp ResourceManager::hostPath.
    private string HostPath(string backslashPath)
    {
        if (ResolveCase(mRoot, ToHost(backslashPath), out var resolved))
            return resolved;
        return mRoot + "/" + ToHost(backslashPath);
    }

    // src/core/resource_manager.cpp ResourceManager::split.
    private (string? path, string? name) Split(string name, bool create)
    {
        string? Get(string s) => create ? mStrings.Intern(s) : mStrings.Lookup(s);
        var slash = name.LastIndexOf('\\');
        if (slash < 0)
            return (null, Get(name));
        return (Get(name[..slash]), Get(name[(slash + 1)..]));
    }

    private List<Entry> Chain(string? path, string name)
    {
        if (!mChains.TryGetValue((path, name), out List<Entry>? chain))
        {
            chain = [];
            mChains.Add((path, name), chain);
        }
        return chain;
    }

    // src/core/resource_manager.cpp ResourceManager::createResource.
    private Entry CreateResource(string? path, string name, string fileName, string filePath)
    {
        List<Entry> chain = Chain(path, name);
        foreach (Entry existing in chain)
            if (existing.FileName == fileName && existing.FilePath == filePath)
                return existing; // reused entries keep their list position
        var e = new Entry { Path = path, Name = name, FileName = fileName, FilePath = filePath };
        mList.Add(e); // the original prepends; mList is read from the back
        chain.Add(e);
        return e;
    }

    // src/core/resource_manager.cpp ResourceManager::insertBehind.
    private void InsertBehind(Entry e, uint mask)
    {
        List<Entry> chain = Chain(e.Path, e.Name);
        chain.Remove(e);
        var it = 0;
        while (it < chain.Count && (chain[it].Flags & mask) != 0)
            ++it;
        chain.Insert(it, e);
    }

    // src/core/resource_manager.cpp ResourceManager::moveToBack.
    private void MoveToBack(Entry e)
    {
        List<Entry> chain = Chain(e.Path, e.Name);
        chain.Remove(e);
        chain.Add(e);
    }

    // src/core/resource_manager.cpp ResourceManager::unlink.
    private void Unlink(Entry e)
    {
        List<Entry> chain = Chain(e.Path, e.Name);
        chain.Remove(e);
        mList.Remove(e);
    }

    // src/core/resource_manager.cpp ResourceManager::scanDirectory.
    private void ScanDirectory(string dir, string rel)
    {
        var backslashDir = rel.Length == 0 ? dir : dir + "\\" + rel;
        if (!ResolveCase(mRoot, ToHost(backslashDir), out var host))
            return;
        if (!Directory.Exists(host))
            return;
        var items = new List<(string name, bool isDir, long size)>(); // name, is directory
        try
        {
            foreach (var full in Directory.EnumerateFileSystemEntries(host))
            {
                var hostName = Path.GetFileName(full);
                var p = host + "/" + hostName;
                if (Directory.Exists(p))
                    items.Add((ByteString.FromHost(hostName), true, 0));
                else if (File.Exists(p))
                    items.Add((ByteString.FromHost(hostName), false, new FileInfo(p).Length));
            }
        }
        catch (Exception)
        {
            return;
        }
        items.Sort((a, b) => NtfsLess(a.name, b.name) ? -1 : NtfsLess(b.name, a.name) ? 1 : 0);

        foreach (var (n, isDir, size) in items)
        {
            if (isDir)
            {
                if (n[0] != '.')
                    ScanDirectory(dir, rel.Length == 0 ? n : rel + "\\" + n);
                continue;
            }
            var name = mStrings.Intern(n);
            Entry e = CreateResource(rel.Length == 0 ? null : mStrings.Intern(rel), name, name,
                                     mStrings.Intern(backslashDir));
            InsertBehind(e, LooseFile);
            e.Flags = LooseFile;
            e.Volume = null;
            e.Size = unchecked((uint)size);
        }
    }

    /// <summary>Rescans the search path. Loose files that disappear become stale entries and are dropped unless an instance keeps them.</summary>
    /// <param name="searchPath">Directories separated by <c>;</c>, backslash separated and relative to the root. Directories that are absolute or contain <c>.</c> or <c>:</c> are skipped, as the original does once start-up is over.</param>
    public void SetSearchPath(string searchPath)
    {
        foreach (Entry e in mList.ToArray())
            if ((e.Flags & LooseFile) != 0)
            {
                e.Flags = Stale;
                MoveToBack(e);
            }

        var start = 0;
        while (start <= searchPath.Length)
        {
            var end = searchPath.IndexOf(';', start);
            var dir = end < 0 ? searchPath[start..] : searchPath[start..end];
            start = end < 0 ? searchPath.Length + 1 : end + 1;
            if (dir.Length == 0 || dir[0] == '\\' || dir.Contains('.') || dir.Contains(':'))
                continue; // allowUnsafePaths is 0 after startup
            ScanDirectory(dir, "");
        }

        foreach (Entry e in mList.ToArray())
            if (e.Flags == Stale && !e.HasInstance)
                Unlink(e);
    }

    /// <summary>Registers a resource built at run time (action maps). It has no file, is found by lookup and iteration, and survives rescans.</summary>
    /// <param name="resName">The resource name.</param>
    /// <returns>False when it is already registered.</returns>
    public bool AddGenerated(string resName)
    {
        var (path, name) = Split(resName, true);
        Entry? e = mChains.TryGetValue((path, name!), out List<Entry>? chain) && chain.Count != 0 ? chain[0] : null;
        if (e != null && e.HasInstance)
            return false;
        if (e == null)
        {
            var none = mStrings.Intern(""); // no backing file
            e = CreateResource(path, name!, none, none);
            e.Flags = Stale;
        }
        InsertBehind(e, InVolume | LooseFile);
        e.HasInstance = true;
        e.Locks = 1; // add takes a lock for its caller
        return true;
    }

    /// <summary>Locks an entry and gives it an instance, which the entry keeps after the last unlock until <see cref="Purge"/>.</summary>
    /// <param name="resName">The resource name.</param>
    /// <returns>The entry, or null when the name is missing or the entry has neither a file nor an instance.</returns>
    /// <remarks>Follows ResManager::load (00587e7c), docs/resources.md 8.7.</remarks>
    public Entry? Load(string resName)
    {
        var (path, name) = Split(resName, true);
        Entry? e = mChains.TryGetValue((path, name!), out List<Entry>? chain) && chain.Count != 0 ? chain[0] : null;
        if (e == null || (!e.HasInstance && (e.Flags & (InVolume | LooseFile)) == 0))
            return null;
        ++e.Locks;
        e.HasInstance = true;
        return e;
    }

    /// <summary>Releases one lock. At 0 the instance waits for <see cref="Purge"/>.</summary>
    /// <param name="e">The entry.</param>
    /// <remarks>Follows unlock (00587d64).</remarks>
    public static void Unlock(Entry e) => --e.Locks;

    /// <summary>Drops every unlocked instance and frees the stale entries among them.</summary>
    /// <remarks>Follows purge(force) (00588074), the console command <c>purgeResources</c>.</remarks>
    public void Purge()
    {
        foreach (Entry e in mList.ToArray())
        {
            if (!e.HasInstance || e.Locks != 0)
                continue;
            e.HasInstance = false;
            if ((e.Flags & Stale) != 0)
                Unlink(e);
        }
    }

    /// <summary>Mounts a volume found as a loose file and indexes its entries. Mounting a volume again only raises its reference count.</summary>
    /// <param name="volName">The lookup name of the .vol file.</param>
    /// <returns>True on success.</returns>
    public bool AddVolume(string volName)
    {
        var (path, name) = Split(volName, true);
        foreach (Volume v in mVolumes)
            if (v.Path == path && v.Name == name)
            {
                ++v.RefCount;
                return true;
            }
        Entry? file = Find(volName);
        if (file == null || file.Flags != LooseFile)
            return false;

        var vol = new Volume { Path = path, Name = name!, Dir = file.FilePath };
        var hostFile = HostPath(file.FilePath + "\\" + file.FileName);
        if (!vol.Archive.Open(hostFile))
            return false;

        var volFile = file.FileName;
        var volDir = file.FilePath;
        IReadOnlyList<VolEntry> entries = vol.Archive.Entries();
        for (var i = 0; i < entries.Count; ++i)
        {
            VolEntry ve = entries[i];
            if (ve.Name.Length == 0)
                continue;
            var (epath, ename) = Split(ve.Name, true);
            Entry e = CreateResource(epath, ename!, volFile, volDir);
            e.Flags = InVolume | (ve.Compression == 1 ? Rle : ve.Compression == 3 ? Lzh : 0u);
            e.Volume = vol;
            e.VolumeIndex = i;
            e.Size = ve.Size;
            InsertBehind(e, LooseFile);
        }
        mVolumes.Add(vol);
        return true;
    }

    /// <summary>Drops one mount of a volume. At 0 the volume closes and its entries are removed, or kept as stale entries when they have an instance.</summary>
    /// <param name="volName">The lookup name of the .vol file.</param>
    public void RemoveVolume(string volName)
    {
        var (path, name) = Split(volName, false);
        var it = mVolumes.FindIndex(v => v.Path == path && v.Name == name);
        if (it < 0 || --mVolumes[it].RefCount > 0)
            return;
        Volume vol = mVolumes[it];
        foreach (Entry e in mList.ToArray())
        {
            if (e.Volume != vol)
                continue;
            if (e.HasInstance)
            {
                e.Flags = Stale;
                e.Volume = null;
                MoveToBack(e);
            }
            else
            {
                Unlink(e);
            }
        }
        vol.Archive.Close();
        mVolumes.RemoveAt(it);
    }

    /// <summary>Finds the first entry of a name.</summary>
    /// <param name="name">The lookup name, with an optional backslash directory.</param>
    /// <returns>The entry, or null.</returns>
    /// <remarks>The original interns lookup names, so a lookup can fix the spelling later entries display.</remarks>
    public Entry? Find(string name)
    {
        var (path, n) = Split(name, true);
        return mChains.TryGetValue((path, n!), out List<Entry>? chain) && chain.Count != 0 ? chain[0] : null;
    }

    /// <summary>Reads the bytes of an entry from its volume or loose file.</summary>
    /// <param name="e">The entry.</param>
    /// <param name="output">Receives the uncompressed bytes.</param>
    /// <returns>True on success.</returns>
    public bool Read(Entry e, out byte[] output)
    {
        output = [];
        if ((e.Flags & InVolume) != 0)
            return e.Volume != null && e.Volume.Archive.Read(e.Volume.Archive.Entries()[e.VolumeIndex], out output);
        if ((e.Flags & LooseFile) == 0)
            return false;
        var host = HostPath(e.FilePath + "\\" + e.FileName);
        try
        {
            output = File.ReadAllBytes(host);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Finds an entry by name and reads its bytes.</summary>
    /// <param name="name">The lookup name.</param>
    /// <param name="output">Receives the uncompressed bytes.</param>
    /// <returns>True when the entry exists and was read.</returns>
    public bool Read(string name, out byte[] output)
    {
        output = [];
        Entry? e = Find(name);
        return e != null && Read(e, out output);
    }

    /// <summary>Gets the display name of an entry, <c>path\name</c> or <c>name</c>.</summary>
    /// <param name="e">The entry.</param>
    /// <returns>The display name.</returns>
    public static string DisplayName(Entry e) => e.Path != null ? e.Path + "\\" + e.Name : e.Name;

    /// <summary>Iterates the entries that match a pattern, newest first.</summary>
    /// <param name="pattern">The wildcard pattern, see <see cref="WildcardMatch"/>.</param>
    /// <param name="after">The previous result, or null to start.</param>
    /// <param name="name">Receives the display name of the result.</param>
    /// <returns>The next matching entry, or null at the end.</returns>
    public Entry? FindNext(string pattern, Entry? after, out string name)
    {
        name = "";
        var i = mList.Count;
        if (after != null)
        {
            var it = mList.IndexOf(after);
            if (it < 0)
                return null;
            i = it;
        }
        while (i-- > 0)
        {
            name = DisplayName(mList[i]);
            if (WildcardMatch(pattern, name))
                return mList[i];
        }
        return null;
    }
}

/// <summary>The resources an object loaded, unlocked when the object goes. These are the resource handles the original objects hold.</summary>
public sealed class ResourceLocks
{
    private readonly List<ResourceManager.Entry> mEntries = [];

    /// <summary>Loads a resource with <see cref="ResourceManager.Load"/> and keeps the lock.</summary>
    /// <param name="res">The resource manager.</param>
    /// <param name="name">The resource name.</param>
    /// <returns>False when nothing was loaded.</returns>
    public bool Load(ResourceManager res, string name)
    {
        ResourceManager.Entry? e = res.Load(name);
        if (e != null)
            mEntries.Add(e);
        return e != null;
    }

    /// <summary>Keeps a lock taken with <see cref="ResourceManager.Load"/>.</summary>
    /// <param name="e">The locked entry.</param>
    public void Adopt(ResourceManager.Entry e) => mEntries.Add(e);

    /// <summary>Unlocks every kept resource. This is the C++ destructor.</summary>
    public void Release()
    {
        foreach (ResourceManager.Entry e in mEntries)
            ResourceManager.Unlock(e);
        mEntries.Clear();
    }
}
