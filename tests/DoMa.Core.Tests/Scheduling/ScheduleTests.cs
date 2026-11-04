using DoMa.Core.Engine;
using DoMa.Core.Scheduling;

namespace DoMa.Core.Tests.Scheduling;

public class ScheduleTests
{
    static readonly TimeOnly Night = new(23, 0);
    static readonly TimeOnly Morning = new(7, 0);
    static DateTime At(int day, int h, int m) => new(2026, 11, day, h, m, 0);

    [Fact]
    public void starts_when_crossing_start_time() =>
        Assert.Equal(ScheduleAction.Start, ScheduleClock.Evaluate(Night, Morning, At(4, 22, 59), At(4, 23, 0)));

    [Fact]
    public void stops_when_crossing_stop_time_after_midnight() =>
        Assert.Equal(ScheduleAction.Stop, ScheduleClock.Evaluate(Night, Morning, At(5, 6, 59), At(5, 7, 1)));

    [Fact]
    public void nothing_between_marks() =>
        Assert.Equal(ScheduleAction.None, ScheduleClock.Evaluate(Night, Morning, At(4, 12, 0), At(4, 12, 1)));

    [Fact]
    public void no_stop_when_stop_disabled() =>
        Assert.Equal(ScheduleAction.None, ScheduleClock.Evaluate(Night, null, At(5, 6, 59), At(5, 7, 1)));

    [Fact]
    public void long_gap_uses_latest_mark()
    {
        // Máy ngủ từ 22:00 tới 08:00 hôm sau: mốc gần nhất là 07:00 (dừng).
        Assert.Equal(ScheduleAction.Stop, ScheduleClock.Evaluate(Night, Morning, At(4, 22, 0), At(5, 8, 0)));
        // Ngủ từ 06:00 tới 23:30: mốc gần nhất là 23:00 (bắt đầu).
        Assert.Equal(ScheduleAction.Start, ScheduleClock.Evaluate(Night, Morning, At(4, 6, 0), At(4, 23, 30)));
    }

    [Theory]
    [InlineData("23:00", true)]
    [InlineData("7:05", true)]
    [InlineData("25:00", false)]
    [InlineData("", false)]
    public void parses_times(string text, bool ok) => Assert.Equal(ok, ScheduleClock.TryParse(text, out _));

    [Fact]
    public void limiter_unlimited_never_waits()
    {
        var limiter = new BandwidthLimiter(clock: () => TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(10_000_000));
    }

    [Fact]
    public void limiter_paces_to_limit()
    {
        var now = TimeSpan.Zero;
        var limiter = new BandwidthLimiter(clock: () => now) { LimitBytesPerSec = 1000 };
        Assert.Equal(TimeSpan.FromSeconds(2), limiter.Reserve(2000));
        now = TimeSpan.FromSeconds(2);
        Assert.Equal(TimeSpan.FromSeconds(0.5), limiter.Reserve(500));
        now = TimeSpan.FromSeconds(10);
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(1000));
    }
}
