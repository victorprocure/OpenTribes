namespace OpenTribes.Server;

using System.Globalization;

/// <summary>The list of banned addresses with optional expiry (docs/server-ops.md 4).</summary>
/// <remarks>Port of src/server/ban_list.cpp. The original is a global at 0065dbc8 with add 00401ab4, remove 00401bdc, check 00401c60, match 00401d0c and export 00401d64.</remarks>
public sealed class BanList
{
    /// <summary>One banned address pattern.</summary>
    public sealed class Entry
    {
        /// <summary>The address pattern, at most 127 characters.</summary>
        public string Address = ""; // at most 127 characters
        /// <summary>The expiry as Unix time. 0 never expires.</summary>
        public int Expiry;
    }
    /// <summary>The longest stored address in characters.</summary>
    public const int kMaxAddress = 127;

    private readonly Func<int> mClock;
    private readonly List<Entry> mEntries = [];

    private static char LowerAscii(char c) => (c is >= 'A' and <= 'Z') ? (char)(c + 0x20) : c;

    private static bool EqualNoCase(string a, string b)
    {
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; ++i)
            if (LowerAscii(a[i]) != LowerAscii(b[i]))
                return false;
        return true;
    }

    /// <summary>Creates an empty list.</summary>
    /// <param name="clock">Returns seconds since the epoch. Defaults to the system clock, as the original calls <c>time()</c>.</param>
    public BanList(Func<int>? clock = null) => mClock = clock ?? (() => unchecked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    /// <summary>Bans an address for a duration.</summary>
    /// <param name="address">The address pattern.</param>
    /// <param name="seconds">The duration in seconds. -1 is permanent and 0 expires at once (00401bac).</param>
    public void Add(string address, int seconds) =>
        // 0x00401bac: -1 means permanent; anything else is relative to now (0 expires at once).
        AddAbsolute(address, seconds == -1 ? 0 : unchecked((int)((uint)seconds + (uint)mClock())));

    /// <summary>Bans an address until a time. Ignored when the expiry is non-zero and already past.</summary>
    /// <param name="address">The address pattern. An <c>IP:a.b.c.d:port</c> address is stored with the port replaced by <c>*</c>.</param>
    /// <param name="expiry">The expiry as Unix time, 0 for never.</param>
    /// <remarks>The duplicate test uses the address as given, before the port is replaced, so banning <c>IP:a.b.c.d:port</c> twice stores two <c>IP:a.b.c.d:*</c> entries. The original copies into a 128-byte stack buffer without a length check (D11).</remarks>
    public void AddAbsolute(string address, int expiry)
    {
        if (expiry != 0 && mClock() > expiry)
            return;
        // The duplicate test uses the address as given, before the port is replaced, so
        // banning "IP:a.b.c.d:port" twice stores two "IP:a.b.c.d:*" entries.
        foreach (var e in mEntries)
            if (EqualNoCase(e.Address, address))
            {
                e.Expiry = expiry;
                return;
            }
        // The original copies into a 128-byte stack buffer without a length check (D11).
        var name = address.Length > kMaxAddress ? address[..kMaxAddress] : address;
        if (name.Length >= 3 && EqualNoCase(name[..3], "ip:"))
        {
            var colon = name.IndexOf(':', 3);
            if (colon >= 0)
            {
                name = name[..(colon + 1)];
                if (name.Length < kMaxAddress)
                    name += '*';
            }
        }
        mEntries.Add(new Entry { Address = name, Expiry = expiry });
    }

    /// <summary>Removes the first entry equal to the address, ignoring ASCII case.</summary>
    /// <param name="address">The stored address.</param>
    public void Remove(string address)
    {
        for (var i = 0; i < mEntries.Count; ++i)
            if (EqualNoCase(mEntries[i].Address, address))
            {
                mEntries.RemoveAt(i);
                return;
            }
    }

    /// <summary>Tests an address against a stored pattern. A <c>*</c> in the pattern matches the rest. A pattern that ends where the address has <c>:</c> matches. Other characters compare without case.</summary>
    /// <param name="pattern">The stored pattern.</param>
    /// <param name="address">The address to test.</param>
    /// <returns>True when the address matches.</returns>
    public static bool Matches(string pattern, string address)
    {
        for (var i = 0; ; ++i)
        {
            var p = i < pattern.Length ? pattern[i] : '\0';
            var a = i < address.Length ? address[i] : '\0';
            if (p == '*' || (p == '\0' && a == ':'))
                return true;
            if (LowerAscii(p) != LowerAscii(a))
                return false;
            if (p == '\0')
                return true;
        }
    }

    /// <summary>Tells whether an unexpired entry matches the address. Expired entries met on the way are erased.</summary>
    /// <param name="address">The address to test.</param>
    /// <returns>True when banned.</returns>
    public bool IsBanned(string address)
    {
        var now = mClock();
        for (var i = 0; i < mEntries.Count;)
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

    /// <summary>Exports the list as script lines, one <c>BanList::addAbsolute("address", expiry);</c> per entry.</summary>
    /// <returns>The lines.</returns>
    public List<string> ExportLines()
    {
        var output = new List<string>();
        foreach (var e in mEntries)
            output.Add("BanList::addAbsolute(\"" + e.Address + "\", " + e.Expiry.ToString(CultureInfo.InvariantCulture) + ");");
        return output;
    }

    /// <summary>Gets the entries.</summary>
    /// <returns>The entries.</returns>
    public IReadOnlyList<Entry> Entries() => mEntries;
}
