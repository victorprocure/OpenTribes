// Port of tests/test_string_table.cpp (docs/resources.md 8.1 "Names and case", docs/script.md 2 interning).
// The C++ pointer comparisons are reference comparisons (Assert.Same / Assert.NotSame).
using OpenTribes.Core;

namespace OpenTribes.Tests;

public class StringTableTests
{
    [Fact]
    public void TestStringTable()
    {
        int cases = 0;

        // Case-insensitive insert returns the first spelling for every later variant.
        {
            var t = new StringTable();
            string a = t.Intern("serverConfig.cs");
            string b = t.Intern("SERVERCONFIG.CS");
            string c = t.Intern("serverconfig.cs");
            Assert.True(ReferenceEquals(a, b) && ReferenceEquals(b, c) && a == "serverConfig.cs");
            ++cases;
        }

        // lookup finds any case variant without inserting; unknown strings give null.
        {
            var t = new StringTable();
            Assert.Null(t.Lookup("Echo"));
            Assert.Null(t.Lookup("Echo")); // lookup did not intern
            string p = t.Intern("echo");
            Assert.True(ReferenceEquals(t.Lookup("ECHO"), p) && t.Lookup("eChO") == "echo");
            ++cases;
        }

        // stricmpAscii (used by the original string table) folds only a-z: '[' / '{', '@' / '`'
        // and bytes >= 0x80 stay distinct.
        {
            var t = new StringTable();
            Assert.NotSame(t.Intern("a["), t.Intern("a{"));
            Assert.NotSame(t.Intern("@"), t.Intern("`"));
            Assert.NotSame(t.Intern("é"), t.Intern("É"));
            Assert.Same(t.Intern(""), t.Intern(""));
            ++cases;
        }

        // Interned strings stay valid while the table grows.
        {
            var t = new StringTable();
            string first = t.Intern("Anchor");
            for (int i = 0; i < 10000; ++i)
                t.Intern("name" + i);
            Assert.True(ReferenceEquals(t.Intern("ANCHOR"), first) && first == "Anchor");
            Assert.Equal("name9999", t.Intern("NAME9999"));
            ++cases;
        }

        // One global table shared by the engine.
        {
            Assert.Same(StringTable.Global(), StringTable.Global());
            ++cases;
        }
    }
}
