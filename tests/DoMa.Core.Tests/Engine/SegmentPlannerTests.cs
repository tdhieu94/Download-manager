using DoMa.Core.Engine;

namespace DoMa.Core.Tests.Engine;

public class SegmentPlannerTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void initial_splits_evenly()
    {
        var size = 100 * MiB;
        var segments = SegmentPlanner.Initial(size, 16);
        Assert.Equal(16, segments.Count);
        Assert.Equal(0, segments[0].Start);
        Assert.Equal(size - 1, segments[^1].End);
        for (var i = 1; i < segments.Count; i++)
            Assert.Equal(segments[i - 1].End + 1, segments[i].Start);
        Assert.All(segments, s => Assert.Equal(size / 16, s.End - s.Start + 1));
        Assert.All(segments, s => Assert.Equal(0, s.Downloaded));
    }

    [Fact]
    public void small_file_one_segment()
    {
        var segments = SegmentPlanner.Initial(500_000, 16);
        var only = Assert.Single(segments);
        Assert.Equal(0, only.Start);
        Assert.Equal(499_999, only.End);
    }

    [Fact]
    public void split_largest_halves_remaining()
    {
        var first = new Segment { Start = 0, End = 10 * MiB - 1, Downloaded = 2 * MiB };
        var second = new Segment { Start = 10 * MiB, End = 20 * MiB - 1, Downloaded = 0 };
        var segments = new List<Segment> { first, second };

        var tail = SegmentPlanner.SplitLargest(segments);

        Assert.NotNull(tail);
        Assert.Equal(3, segments.Count);
        Assert.Contains(tail, segments);
        Assert.Equal(15 * MiB - 1, second.End);
        Assert.Equal(15 * MiB, tail.Start);
        Assert.Equal(20 * MiB - 1, tail.End);
        Assert.Equal(0, tail.Downloaded);
        Assert.Equal(10 * MiB - 1, first.End);
    }

    [Fact]
    public void split_refuses_below_2mib()
    {
        var only = new Segment { Start = 0, End = 4 * MiB - 1, Downloaded = 2 * MiB + 1 };
        var segments = new List<Segment> { only };

        Assert.Null(SegmentPlanner.SplitLargest(segments));
        Assert.Single(segments);
        Assert.Equal(4 * MiB - 1, only.End);
    }
}
