// Port of src/server/ban_list.hpp and src/server/ban_list.cpp: ban list (original: global at 0x0065dbc8, add 0x00401ab4,
// remove 0x00401bdc, check 0x00401c60, match 0x00401d0c, export 0x00401d64). docs/server-ops.md 4.
using System.Globalization;

namespace OpenTribes.Server;

public sealed class BanList
{
    // src/server/ban_list.hpp BanList::Entry.
    public sealed class Entry
    {
        public string Address = ""; // at most 127 characters
        public int Expiry;          // unix time, 0 = never expires
    }
    public const int kMaxAddress = 127;

    private readonly Func<int> mClock;
    private readonly List<Entry> mEntries = new();

    private static char LowerAscii(char c) => (c >= 'A' && c <= 'Z') ? (char)(c + 0x20) : c;

    private static bool EqualNoCase(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; ++i)
            if (LowerAscii(a[i]) != LowerAscii(b[i]))
                return false;
        return true;
    }

    // src/server/ban_list.cpp BanList::BanList: clock is seconds since the epoch (the original calls time()).
    public BanList(Func<int>? clock = null)
    {
        mClock = clock ?? (() => unchecked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    // src/server/ban_list.cpp BanList::add: expiry = now + seconds, or never when seconds is -1.
    public void Add(string address, int seconds)
    {
        // 0x00401bac: -1 means permanent; anything else is relative to now (0 expires at once).
        AddAbsolute(address, seconds == -1 ? 0 : unchecked((int)((uint)seconds + (uint)mClock())));
    }

    // src/server/ban_list.cpp BanList::addAbsolute: ignored when the expiry is non-zero and already past.
    public void AddAbsolute(string address, int expiry)
    {
        if (expiry != 0 && mClock() > expiry)
            return;
        // The duplicate test uses the address as given, before the port is replaced, so
        // banning "IP:a.b.c.d:port" twice stores two "IP:a.b.c.d:*" entries.
        foreach (Entry e in mEntries)
            if (EqualNoCase(e.Address, address))
            {
                e.Expiry = expiry;
                return;
            }
        // The original copies into a 128-byte stack buffer without a length check (D11).
        string name = address.Length > kMaxAddress ? address[..kMaxAddress] : address;
        if (name.Length >= 3 && EqualNoCase(name[..3], "ip:"))
        {
            int colon = name.IndexOf(':', 3);
            if (colon >= 0)
            {
                name = name[..(colon + 1)];
                if (name.Length < kMaxAddress)
                    name += '*';
            }
        }
        mEntries.Add(new Entry { Address = name, Expiry = expiry });
    }

    // src/server/ban_list.cpp BanList::remove: removes the first entry equal to address (ASCII case-insensitive).
    public void Remove(string address)
    {
        for (int i = 0; i < mEntries.Count; ++i)
            if (EqualNoCase(mEntries[i].Address, address))
            {
                mEntries.RemoveAt(i);
                return;
            }
    }

    // src/server/ban_list.cpp BanList::matches: '*' in the pattern matches the rest, a pattern that ends where the
    // address has ':' matches, otherwise characters compare without case.
    public static bool Matches(string pattern, string address)
    {
        for (int i = 0; ; ++i)
        {
            char p = i < pattern.Length ? pattern[i] : '\0';
            char a = i < address.Length ? address[i] : '\0';
            if (p == '*' || (p == '\0' && a == ':'))
                return true;
            if (LowerAscii(p) != LowerAscii(a))
                return false;
            if (p == '\0')
                return true;
        }
    }

    // src/server/ban_list.cpp BanList::isBanned: true when an unexpired entry matches. Expired entries met on the way are erased.
    public bool IsBanned(string address)
    {
        int now = mClock();
        for (int i = 0; i < mEntries.Count;)
        {
            if (mEntries[i].Expiry != 0 && mEntries[i].Expiry < now)
            {
                mEntries.RemoveAt(i);
                continue;
            }
            if (Matches(mEntries[i].Address, address))
                return true;
            ++i;
        }
        return false;
    }

    // src/server/ban_list.cpp BanList::exportLines: one "BanList::addAbsolute(\"<address>\", <expiry>);" line per entry.
    public List<string> ExportLines()
    {
        var output = new List<string>();
        foreach (Entry e in mEntries)
            output.Add("BanList::addAbsolute(\"" + e.Address + "\", " + e.Expiry.ToString(CultureInfo.InvariantCulture) + ");");
        return output;
    }

    // src/server/ban_list.hpp BanList::entries.
    public IReadOnlyList<Entry> Entries() => mEntries;
}
