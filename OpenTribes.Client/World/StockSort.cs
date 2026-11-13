namespace OpenTribes.Client.World;

/// <summary>The element order the stock client's C runtime sort (the exe's <c>qsort</c>, world-render.md 2.8) leaves for keys that compare equal.</summary>
/// <remarks>Written from the algorithm as measured on the exe: median of three (middle, last, first), a partition pass that gathers keys equal to the pivot next to it and swaps them back around it, recursion into the smaller side and a loop on the larger one. Not stable.</remarks>
public static class StockSort
{
    /// <summary>Sorts the first elements of an array in the stock order.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="a">The array to sort in place.</param>
    /// <param name="count">The number of elements to sort.</param>
    /// <param name="cmp">The key comparison.</param>
    public static void Sort<T>(T[] a, int count, Comparison<T> cmp)
    {
        if (count > 1)
            Run(a, 0, count, cmp);
    }

    private static void Swap<T>(T[] a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);

    private static void Run<T>(T[] a, int lo, int n, Comparison<T> cmp)
    {
        while (true)
        {
            if (n < 3)
            {
                if (n == 2 && cmp(a[lo], a[lo + 1]) > 0)
                    Swap(a, lo, lo + 1);
                return;
            }
            int hi = lo + n - 1, mid = lo + (n >> 1);
            if (cmp(a[mid], a[hi]) > 0)
                Swap(a, mid, hi);
            if (cmp(a[mid], a[lo]) > 0)
                Swap(a, mid, lo);
            else if (cmp(a[lo], a[hi]) > 0)
                Swap(a, lo, hi);
            if (n == 3)
            {
                Swap(a, lo, mid);
                return;
            }
            // The pivot is a[lo]. `eq` marks the end of the run of keys equal to it that gathers after the pivot.
            int eq = lo + 1, i = eq;
            while (true)
            {
                int r = cmp(a[i], a[lo]);
                if (r <= 0)
                {
                    if (r == 0)
                    {
                        Swap(a, i, eq);
                        ++eq;
                    }
                    if (hi <= i)
                        break;
                    ++i;
                    continue;
                }
                if (hi > i)
                {
                    while (true)
                    {
                        r = cmp(a[lo], a[hi]);
                        if (r < 0)
                        {
                            --hi;
                            if (hi > i)
                                continue;
                            break;
                        }
                        Swap(a, i, hi);
                        if (r != 0)
                        {
                            ++i;
                            --hi;
                        }
                        break;
                    }
                }
                if (hi > i)
                    continue;
                break;
            }
            if (cmp(a[i], a[lo]) <= 0)
                ++i;
            int j = i - 1, k = lo;
            while (k < eq && j >= eq)
            {
                Swap(a, k, j);
                ++k;
                --j;
            }
            int left = i - eq, right = lo + n - i;
            if (left <= right)
            {
                Run(a, lo, left, cmp);
                lo = i;
                n = right;
            }
            else
            {
                Run(a, i, right, cmp);
                n = left;
            }
        }
    }
}
