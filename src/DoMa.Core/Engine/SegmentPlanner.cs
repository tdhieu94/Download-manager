namespace DoMa.Core.Engine;

public static class SegmentPlanner
{
    private const long MinSegment = 1024 * 1024;

    public static List<Segment> Initial(long size, int connections)
    {
        var count = (int)Math.Max(1, Math.Min(connections, Math.Max(1, size / MinSegment)));
        var part = size / count;
        var segments = new List<Segment>(count);
        for (var i = 0; i < count; i++)
        {
            var start = i * part;
            var end = i == count - 1 ? size - 1 : start + part - 1;
            segments.Add(new Segment { Start = start, End = end });
        }
        return segments;
    }

    public static Segment? SplitLargest(List<Segment> segments)
    {
        var largest = segments.MaxBy(Remaining);
        if (largest is null || Remaining(largest) < 2 * MinSegment)
            return null;

        var tailStart = largest.Start + largest.Downloaded + Remaining(largest) / 2;
        var tail = new Segment { Start = tailStart, End = largest.End };
        largest.End = tailStart - 1;
        segments.Add(tail);
        return tail;
    }

    internal static long Remaining(Segment s) => s.End - (s.Start + s.Downloaded) + 1;
}
