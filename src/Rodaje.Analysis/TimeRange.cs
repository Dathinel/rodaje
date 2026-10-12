namespace Rodaje.Analysis;

/// <summary>Tramo <c>[Start, End)</c> en segundos desde el inicio del audio analizado.</summary>
public readonly record struct TimeRange(double Start, double End)
{
    public double Duration => End - Start;

    public bool Contains(double t) => t >= Start && t < End;

    /// <summary>Une los tramos que se tocan o se separan menos de <paramref name="gap"/> segundos.</summary>
    public static List<TimeRange> Merge(IEnumerable<TimeRange> ranges, double gap = 0)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var merged = new List<TimeRange>();
        foreach (TimeRange r in ranges.Where(r => r.End > r.Start).OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && r.Start - merged[^1].End <= gap)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, r.End) };
            }
            else
            {
                merged.Add(r);
            }
        }

        return merged;
    }
}
