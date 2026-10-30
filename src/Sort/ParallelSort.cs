namespace WinUtils.Sort;

/// <summary>Sorts segments of an array on separate threads, then merges them pairwise (also in parallel).</summary>
internal static class ParallelSort
{
    private const int MinimumSegment = 64 * 1024;

    public static void Sort<T>(T[] items, Comparison<T> comparison)
    {
        int segments = Math.Min(Environment.ProcessorCount, Math.Max(1, items.Length / MinimumSegment));
        if (segments <= 1)
        {
            Array.Sort(items, comparison);
            return;
        }

        var bounds = Enumerable.Range(0, segments + 1).Select(i => (int)((long)items.Length * i / segments)).ToArray();
        Parallel.For(0, segments, i => Array.Sort(items, bounds[i], bounds[i + 1] - bounds[i], Comparer<T>.Create(comparison)));

        var source = items;
        var target = new T[items.Length];
        var runs = bounds.ToList();
        while (runs.Count > 2)
        {
            int pairs = (runs.Count - 1) / 2;
            var next = new List<int> { 0 };
            Parallel.For(0, pairs, p =>
            {
                int lo = runs[2 * p], mid = runs[2 * p + 1], hi = runs[2 * p + 2];
                Merge(source, lo, mid, hi, target, comparison);
            });
            for (int p = 0; p < pairs; p++) next.Add(runs[2 * p + 2]);
            if ((runs.Count - 1) % 2 == 1)
            {
                // An odd run out is copied across unchanged.
                int lo = runs[^2], hi = runs[^1];
                Array.Copy(source, lo, target, lo, hi - lo);
                next.Add(hi);
            }
            runs = next;
            (source, target) = (target, source);
        }
        if (!ReferenceEquals(source, items)) Array.Copy(source, items, items.Length);
    }

    /// <summary>Merges source[lo..mid) and source[mid..hi) into target; the left run wins ties.</summary>
    private static void Merge<T>(T[] source, int lo, int mid, int hi, T[] target, Comparison<T> comparison)
    {
        int i = lo, j = mid, k = lo;
        while (i < mid && j < hi) target[k++] = comparison(source[j], source[i]) < 0 ? source[j++] : source[i++];
        while (i < mid) target[k++] = source[i++];
        while (j < hi) target[k++] = source[j++];
    }
}
