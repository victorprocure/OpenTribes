// Port of src/core/string_table.hpp and src/core/string_table.cpp.
// Interned strings, matched with ASCII case folding. The first spelling interned is returned for
// every later case variant. The C++ pointer identity is reference identity of the returned string.
namespace OpenTribes.Core;

public sealed class StringTable
{
    private readonly Dictionary<string, string> mByFolded = new(StringComparer.Ordinal);

    private static readonly StringTable sGlobal = new();

    // src/core/string_table.cpp StringTable::fold: folds only a-z.
    private static string Fold(string s)
    {
        return string.Create(s.Length, s, static (dst, src) =>
        {
            for (int i = 0; i < src.Length; ++i)
            {
                char c = src[i];
                dst[i] = c >= 'a' && c <= 'z' ? (char)(c - 0x20) : c;
            }
        });
    }

    // src/core/string_table.cpp StringTable::intern: the canonical string for s, interning it if needed. Never null.
    public string Intern(string s)
    {
        string key = Fold(s);
        if (mByFolded.TryGetValue(key, out string? found))
            return found;
        mByFolded.Add(key, s);
        return s;
    }

    // src/core/string_table.cpp StringTable::lookup: the canonical string for s, or null when no case variant is interned.
    public string? Lookup(string s)
    {
        return mByFolded.TryGetValue(Fold(s), out string? found) ? found : null;
    }

    // src/core/string_table.cpp StringTable::global.
    public static StringTable Global() => sGlobal;
}
