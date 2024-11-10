// Port of src/core/borland_qsort.hpp: the original's C runtime qsort, median-of-three Hoare
// partition with an equal-key run kept next to the pivot. It is not stable; callers that need the
// original's order of equal keys use this. The C++ pointers are indices into the span here.
namespace OpenTribes.Core;

public static class Borland
{
    private static void Swap<T>(Span<T> a, int i, int j)
    {
        (a[i], a[j]) = (a[j], a[i]);
    }

    // src/core/borland_qsort.hpp detail::borlandQsortRange.
    private static void BorlandQsortRange<T>(Span<T> a, int @base, int n, Comparison<T> cmp)
    {
        for (;;)
        {
            if (n <= 2)
            {
                if (n == 2 && cmp(a[@base], a[@base + 1]) > 0)
                    Swap(a, @base, @base + 1);
                return;
            }
            int right = @base + (n - 1);
            int left = @base + (n >> 1);
            // Median of three into base.
            if (cmp(a[left], a[right]) > 0)
                Swap(a, left, right);
            if (cmp(a[left], a[@base]) > 0)
                Swap(a, left, @base);
            else if (cmp(a[@base], a[right]) > 0)
                Swap(a, @base, right);
            if (n == 3)
            {
                Swap(a, @base, left);
                return;
            }
            int pivotEnd = @base + 1;
            left = pivotEnd;
            int r;
            do
            {
                bool done = false;
                while ((r = cmp(a[left], a[@base])) <= 0)
                {
                    if (r == 0)
                    {
                        Swap(a, left, pivotEnd);
                        ++pivotEnd;
                    }
                    if (left < right)
                    {
                        ++left;
                    }
                    else
                    {
                        done = true;
                        break;
                    }
                }
                if (done)
                    break;
                while (left < right)
                {
                    if ((r = cmp(a[@base], a[right])) < 0)
                    {
                        --right;
                    }
                    else
                    {
                        Swap(a, left, right);
                        if (r != 0)
                        {
                            ++left;
                            --right;
                        }
                        break;
                    }
                }
            } while (left < right);
            if (cmp(a[left], a[@base]) <= 0)
                ++left;
            // Move the run of keys equal to the pivot into the middle.
            int pivotTemp = @base;
            int leftTemp = left - 1;
            while (pivotTemp < pivotEnd && leftTemp >= pivotEnd)
            {
                Swap(a, pivotTemp, leftTemp);
                ++pivotTemp;
                --leftTemp;
            }
            int lNum = left - pivotEnd;
            int rNum = @base + n - left;
            // The smaller side recurses, the larger one loops.
            if (rNum < lNum)
            {
                BorlandQsortRange(a, left, rNum, cmp);
                n = lNum;
            }
            else
            {
                BorlandQsortRange(a, @base, lNum, cmp);
                @base = left;
                n = rNum;
            }
        }
    }

    // src/core/borland_qsort.hpp borlandQsort: cmp(a, b) returns <0, 0, >0 like a qsort comparator.
    public static void BorlandQsort<T>(Span<T> items, Comparison<T> cmp)
    {
        if (items.Length != 0)
            BorlandQsortRange(items, 0, items.Length, cmp);
    }
}
