using System.Diagnostics;

namespace DoMa.Core.Engine;

/// <summary>
/// Giới hạn tổng tốc độ tải của mọi kết nối (thuật toán "token bucket").
/// LimitBytesPerSec = 0 nghĩa là không giới hạn; đổi giá trị lúc nào cũng được, có hiệu lực ngay.
/// </summary>
public sealed class BandwidthLimiter(Func<TimeSpan, CancellationToken, Task>? delay = null, Func<TimeSpan>? clock = null)
{
    private readonly object _gate = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Func<TimeSpan> _clock = clock ?? StopwatchClock();
    private long _limit;
    private double _tokens;
    private TimeSpan _last;

    public long LimitBytesPerSec
    {
        get => Interlocked.Read(ref _limit);
        set
        {
            lock (_gate)
            {
                Interlocked.Exchange(ref _limit, Math.Max(0, value));
                _tokens = 0;
                _last = _clock();
            }
        }
    }

    /// <summary>Trả về thời gian cần chờ trước khi được dùng thêm <paramref name="bytes"/> byte.</summary>
    public TimeSpan Reserve(int bytes)
    {
        lock (_gate)
        {
            var limit = Interlocked.Read(ref _limit);
            if (limit <= 0 || bytes <= 0)
                return TimeSpan.Zero;
            var now = _clock();
            _tokens = Math.Min(limit, _tokens + (now - _last).TotalSeconds * limit);
            _last = now;
            _tokens -= bytes;
            return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / limit);
        }
    }

    public Task WaitAsync(int bytes, CancellationToken ct)
    {
        var wait = Reserve(bytes);
        return wait > TimeSpan.Zero ? _delay(wait, ct) : Task.CompletedTask;
    }

    /// <summary>Mức chặt hơn trong hai giới hạn (byte/giây, 0 = không giới hạn).</summary>
    public static long Tighter(long a, long b) => a <= 0 ? Math.Max(0, b) : b <= 0 ? a : Math.Min(a, b);

    private static Func<TimeSpan> StopwatchClock()
    {
        var sw = Stopwatch.StartNew();
        return () => sw.Elapsed;
    }
}
