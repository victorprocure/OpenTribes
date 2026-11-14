// Port of tests/test_qsort.cpp: sort correctness of the original's qsort port on random inputs
// with many equal keys.
namespace OpenTribes.Tests;

using OpenTribes.Core;

public class QsortTests
{
    [Fact]
    public void TestQsort()
    {
        uint seed = 12345;
        uint Rnd()
        {
            seed = (seed * 1103515245u) + 12345u;
            return seed >> 16;
        }
        for (var round = 0; round < 2000; ++round)
        {
            var n = (int)(Rnd() % 40);
            var v = new int[n];
            for (var i = 0; i < n; ++i)
                v[i] = (int)(Rnd() % (uint)(1 + (round % 7)));
            var want = (int[])v.Clone();
            Array.Sort(want);
            Borland.BorlandQsort(v, (a, b) => a < b ? -1 : a > b ? 1 : 0);
            Assert.Equal(want, v);
        }
    }
}
