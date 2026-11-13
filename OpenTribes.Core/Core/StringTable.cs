namespace OpenTribes.Core;

/// <summary>Interned strings matched with ASCII case folding. Every later case variant returns the first spelling interned.</summary>
/// <remarks>Port of src/core/string_table.cpp. The C++ pointer identity is reference identity of the returned string.</remarks>
public sealed class StringTable
{
    private readonly Dictionary<string, string> mByFolded = new(StringComparer.Ordinal);

    private static StringTable sGlobal = new();

    /// <summary>Folds only a-z to upper case, as the original does.</summary>
    private static string Fold(string s) => string.Create(s.Length, s, static (dst, src) =>
                                                 {
                                                     for (var i = 0; i < src.Length; ++i)
                                                     {
                                                         var c = src[i];
                                                         dst[i] = c is >= 'a' and <= 'z' ? (char)(c - 0x20) : c;
                                                     }
                                                 });

    /// <summary>Returns the canonical string for <paramref name="s"/>, interning it if needed.</summary>
    /// <param name="s">The string to intern.</param>
    /// <returns>The first spelling interned for any case variant of <paramref name="s"/>. Never null.</returns>
    public string Intern(string s)
    {
        var key = Fold(s);
        if (mByFolded.TryGetValue(key, out var found))
            return found;
        mByFolded.Add(key, s);
        return s;
    }

    /// <summary>Finds the canonical string for <paramref name="s"/> without interning it.</summary>
    /// <param name="s">The string to look up.</param>
    /// <returns>The canonical string, or null when no case variant is interned.</returns>
    public string? Lookup(string s) => mByFolded.TryGetValue(Fold(s), out var found) ? found : null;

    /// <summary>Gets the process-wide table.</summary>
    /// <returns>The global table.</returns>
    public static StringTable Global() => sGlobal;

    /// <summary>Replaces the process-wide table with an empty one. The unit tests share one process and call this before each test.</summary>
    public static void ResetGlobal() => sGlobal = new StringTable();
}
