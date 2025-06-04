// Port of src/core/resource_manager.hpp and src/core/resource_manager.cpp.
// Resource index over search directories and mounted volumes. Rules: docs/resources.md.
// Names are byte strings (ByteString); interned names are canonical, so the C++ pointer
// comparisons are ordinal string comparisons here.
namespace OpenTribes.Core;

public sealed class ResourceManager
{
    // src/core/resource_manager.hpp enum Flags.
    public const uint InVolume = 1;
    public const uint LooseFile = 2;
    public const uint Stale = 4; // "added": no backing file left
    public const uint Rle = 8;
    public const uint Lzh = 0x10;

    // src/core/resource_manager.cpp struct ResourceManager::Volume.
    public sealed class Volume
    {
        public string? Path;
        public string Name = "";
        public string Dir = ""; // backslash directory of the .vol
        public int RefCount = 1;
        public readonly VolArchive Archive = new();
    }

    // src/core/resource_manager.hpp struct Entry.
    public sealed class Entry
    {
        public string? Path;          // directory part of the lookup name, or null
        public string Name = "";      // file name part
        public string FilePath = "";  // real directory (backslash separated, relative to root)
        public string FileName = "";  // real file name (the .vol for volume entries)
        public uint Flags;
        public Volume? Volume;
        public int VolumeIndex;       // index into the volume's directory
        public uint Size;
        public bool HasInstance;      // +0x14: load built an instance; it keeps a stale entry alive
        public uint Locks;            // +0x44: holders of the instance; purge keeps a locked entry
    }

    private readonly string mRoot;
    private readonly StringTable mStrings;
    private readonly List<Entry> mList = new(); // creation order; iteration walks it from the back
    private readonly Dictionary<(string?, string), List<Entry>> mChains = new();
    private readonly List<Volume> mVolumes = new();

    // src/core/resource_manager.cpp ResourceManager::ResourceManager. rootDir is a host path.
    public ResourceManager(string rootDir, StringTable? strings = null)
    {
        mRoot = rootDir;
        mStrings = strings ?? StringTable.Global();
    }

    // src/core/resource_manager.cpp upperAscii.
    private static char UpperAscii(char c) => (c >= 'a' && c <= 'z') ? (char)(c - 0x20) : c;

    // src/core/resource_manager.cpp ntfsLess: Windows (NTFS) enumeration order, emulated with ASCII case folding.
    private static bool NtfsLess(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; ++i)
        {
            char ca = UpperAscii(a[i]);
            char cb = UpperAscii(b[i]);
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
        for (int i = 0; i < a.Length; ++i)
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
        int start = 0;
        while (start <= rel.Length)
        {
            int end = rel.IndexOf('/', start);
            string part = end < 0 ? rel[start..] : rel[start..end];
            start = end < 0 ? rel.Length + 1 : end + 1;
            if (part.Length == 0)
                continue;
            string candidate = output + "/" + part;
            if (!Exists(candidate))
            {
                if (!Directory.Exists(output))
                    return false;
                bool found = false;
                try
                {
                    foreach (string full in Directory.EnumerateFileSystemEntries(output))
                    {
                        string entName = System.IO.Path.GetFileName(full);
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

    // src/core/resource_manager.cpp wildcardMatch: the original wildcard match in case-insensitive mode.
    public static bool WildcardMatch(string pattern, string str) => WildcardMatchAt(pattern, 0, str, 0);

    private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

    // strchr from index `from`: the index of c, the terminator index for '\0', or -1.
    private static int StrChr(string s, int from, char c)
    {
        for (int i = from; ; ++i)
        {
            char ch = At(s, i);
            if (ch == c)
                return i;
            if (ch == '\0')
                return -1;
        }
    }

    private static bool WildcardMatchAt(string pat, int p, string str, int s)
    {
        bool ok = true;
        while (ok && At(pat, p) != '\0' && At(str, s) != '\0')
        {
            if (At(pat, p) == '*')
            {
                // Each later occurrence of the next pattern character, case-sensitive.
                char next = At(pat, p + 1);
                for (int q = StrChr(str, s, next); q >= 0; q = At(str, q) != '\0' ? StrChr(str, q + 1, next) : -1)
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
        if (ResolveCase(mRoot, ToHost(backslashPath), out string resolved))
            return resolved;
        return mRoot + "/" + ToHost(backslashPath);
    }

    // src/core/resource_manager.cpp ResourceManager::split.
    private (string? path, string? name) Split(string name, bool create)
    {
        string? Get(string s) => create ? mStrings.Intern(s) : mStrings.Lookup(s);
        int slash = name.LastIndexOf('\\');
        if (slash < 0)
            return (null, Get(name));
        return (Get(name[..slash]), Get(name[(slash + 1)..]));
    }

    private List<Entry> Chain(string? path, string name)
    {
        if (!mChains.TryGetValue((path, name), out List<Entry>? chain))
        {
            chain = new List<Entry>();
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
        int it = 0;
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
        string backslashDir = rel.Length == 0 ? dir : dir + "\\" + rel;
        if (!ResolveCase(mRoot, ToHost(backslashDir), out string host))
            return;
        if (!Directory.Exists(host))
            return;
        var items = new List<(string name, bool isDir, long size)>(); // name, is directory
        try
        {
            foreach (string full in Directory.EnumerateFileSystemEntries(host))
            {
                string hostName = System.IO.Path.GetFileName(full);
                string p = host + "/" + hostName;
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
            string name = mStrings.Intern(n);
            Entry e = CreateResource(rel.Length == 0 ? null : mStrings.Intern(rel), name, name,
                                     mStrings.Intern(backslashDir));
            InsertBehind(e, LooseFile);
            e.Flags = LooseFile;
            e.Volume = null;
            e.Size = unchecked((uint)size);
        }
    }

    // src/core/resource_manager.cpp ResourceManager::setSearchPath: rescans the search path
    // (`;`-separated, backslash directories relative to root).
    public void SetSearchPath(string searchPath)
    {
        foreach (Entry e in mList.ToArray())
            if ((e.Flags & LooseFile) != 0)
            {
                e.Flags = Stale;
                MoveToBack(e);
            }

        int start = 0;
        while (start <= searchPath.Length)
        {
            int end = searchPath.IndexOf(';', start);
            string dir = end < 0 ? searchPath[start..] : searchPath[start..end];
            start = end < 0 ? searchPath.Length + 1 : end + 1;
            if (dir.Length == 0 || dir[0] == '\\' || dir.Contains('.') || dir.Contains(':'))
                continue; // allowUnsafePaths is 0 after startup
            ScanDirectory(dir, "");
        }

        foreach (Entry e in mList.ToArray())
            if (e.Flags == Stale && !e.HasInstance)
                Unlink(e);
    }

    // src/core/resource_manager.cpp ResourceManager::addGenerated: registers a resource built at run
    // time (action maps). It has no file, is found by lookup and iteration, and survives rescans.
    // False: already registered.
    public bool AddGenerated(string resName)
    {
        var (path, name) = Split(resName, true);
        Entry? e = mChains.TryGetValue((path, name!), out List<Entry>? chain) && chain.Count != 0 ? chain[0] : null;
        if (e != null && e.HasInstance)
            return false;
        if (e == null)
        {
            string none = mStrings.Intern(""); // no backing file
            e = CreateResource(path, name!, none, none);
            e.Flags = Stale;
        }
        InsertBehind(e, InVolume | LooseFile);
        e.HasInstance = true;
        e.Locks = 1; // add takes a lock for its caller
        return true;
    }

    // src/core/resource_manager.cpp ResourceManager::load: ResManager::load (0x00587e7c) locks the
    // entry and gives it an instance, which the entry keeps after the last unlock until purge
    // (docs/resources.md 8.7). Null when the name is missing, or the entry has neither a file nor
    // an instance.
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

    // src/core/resource_manager.hpp ResourceManager::unlock: unlock (0x00587d64), one lock less; at 0
    // the instance waits for purge.
    public static void Unlock(Entry e) => --e.Locks;

    // src/core/resource_manager.cpp ResourceManager::purge: purge(force) (0x00588074, console
    // purgeResources) drops every unlocked instance and frees the stale (flag 4) entries among them.
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

    // src/core/resource_manager.cpp ResourceManager::addVolume.
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
        string hostFile = HostPath(file.FilePath + "\\" + file.FileName);
        if (!vol.Archive.Open(hostFile))
            return false;

        string volFile = file.FileName;
        string volDir = file.FilePath;
        IReadOnlyList<VolEntry> entries = vol.Archive.Entries();
        for (int i = 0; i < entries.Count; ++i)
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

    // src/core/resource_manager.cpp ResourceManager::removeVolume.
    public void RemoveVolume(string volName)
    {
        var (path, name) = Split(volName, false);
        int it = mVolumes.FindIndex(v => v.Path == path && v.Name == name);
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

    // src/core/resource_manager.cpp ResourceManager::find. The original interns lookup names, so a
    // lookup can fix the spelling later entries display.
    public Entry? Find(string name)
    {
        var (path, n) = Split(name, true);
        return mChains.TryGetValue((path, n!), out List<Entry>? chain) && chain.Count != 0 ? chain[0] : null;
    }

    // src/core/resource_manager.cpp ResourceManager::read(const Entry&, ...).
    public bool Read(Entry e, out byte[] output)
    {
        output = Array.Empty<byte>();
        if ((e.Flags & InVolume) != 0)
            return e.Volume != null && e.Volume.Archive.Read(e.Volume.Archive.Entries()[e.VolumeIndex], out output);
        if ((e.Flags & LooseFile) == 0)
            return false;
        string host = HostPath(e.FilePath + "\\" + e.FileName);
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

    // src/core/resource_manager.cpp ResourceManager::read(const std::string&, ...).
    public bool Read(string name, out byte[] output)
    {
        output = Array.Empty<byte>();
        Entry? e = Find(name);
        return e != null && Read(e, out output);
    }

    // src/core/resource_manager.cpp ResourceManager::displayName: `path\name` or `name`.
    public static string DisplayName(Entry e) => e.Path != null ? e.Path + "\\" + e.Name : e.Name;

    // src/core/resource_manager.cpp ResourceManager::findNext: pass null to start. Returns the entry and its display name.
    public Entry? FindNext(string pattern, Entry? after, out string name)
    {
        name = "";
        int i = mList.Count;
        if (after != null)
        {
            int it = mList.IndexOf(after);
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

// src/core/resource_manager.hpp class ResourceLocks: the resources an object loaded, unlocked when
// the object goes (the resource handles the original objects hold). Release is the C++ destructor.
public sealed class ResourceLocks
{
    private readonly List<ResourceManager.Entry> mEntries = new();

    // ResourceManager.Load and keep the lock. False when nothing was loaded.
    public bool Load(ResourceManager res, string name)
    {
        ResourceManager.Entry? e = res.Load(name);
        if (e != null)
            mEntries.Add(e);
        return e != null;
    }

    // Keeps a lock taken with ResourceManager.Load.
    public void Adopt(ResourceManager.Entry e) => mEntries.Add(e);

    public void Release()
    {
        foreach (ResourceManager.Entry e in mEntries)
            ResourceManager.Unlock(e);
        mEntries.Clear();
    }
}
