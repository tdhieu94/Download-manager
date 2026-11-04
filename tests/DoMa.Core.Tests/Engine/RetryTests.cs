using System.Collections.Concurrent;
using System.Security.Cryptography;
using DoMa.Core.Engine;
using DoMa.Core.Models;
using DoMa.Core.Tests.TestServer;

namespace DoMa.Core.Tests.Engine;

public sealed class RetryTests : IDisposable
{
    private const int MiB = 1024 * 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DoMaTests", Guid.NewGuid().ToString("N"));
    private readonly string _target;
    private readonly string _statePath;
    private readonly ConcurrentQueue<TimeSpan> _delays = new();

    public RetryTests()
    {
        Directory.CreateDirectory(_dir);
        _target = Path.Combine(_dir, "f.bin");
        _statePath = Path.Combine(_dir, "state", "f.json");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static byte[] RandomContent()
    {
        var content = new byte[20 * MiB];
        Random.Shared.NextBytes(content);
        return content;
    }

    private Task NoDelay(TimeSpan delay, CancellationToken ct)
    {
        _delays.Enqueue(delay);
        return Task.CompletedTask;
    }

    private async Task<EngineResult> Download(
        DownloadJob job,
        IProgress<EngineProgress>? progress = null,
        CancellationToken pauseToken = default,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? bodyTimeout = null,
        TimeSpan? headerTimeout = null)
    {
        using var http = HttpClientFactory.Create();
        var engine = new SegmentedDownloadEngine(http, delay ?? NoDelay)
        {
            BodyTimeout = bodyTimeout ?? TimeSpan.FromSeconds(60),
            HeaderTimeout = headerTimeout ?? TimeSpan.FromSeconds(30),
        };
        return await engine.RunAsync(job, progress ?? new Progress<EngineProgress>(), pauseToken);
    }

    private async Task<DownloadJob> Job(RangeServer server, int connections = 16)
    {
        using var http = HttpClientFactory.Create();
        var req = new DownloadRequest(server.Url("/f.bin").ToString(), null, null, null, null, null, null);
        var probe = await new LinkProber(http).ProbeAsync(req, CancellationToken.None);
        Assert.True(probe.Ok);
        return new DownloadJob(req, _target, _statePath, connections, probe);
    }

    private static void AssertSameContent(byte[] expected, string? path)
    {
        Assert.NotNull(path);
        Assert.Equal(SHA256.HashData(expected), SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void policy_backs_off_exponentially_capped()
    {
        var policy = new RetryPolicy();

        Assert.Equal(5, policy.MaxAttemptsPerSegment);
        Assert.Equal(
            [1, 2, 4, 8, 16, 30, 30],
            Enumerable.Range(1, 7).Select(a => policy.Delay(a).TotalSeconds));
    }

    [Fact]
    public async Task reduces_connections_on_429()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.ChunkDelay = TimeSpan.FromMilliseconds(10);
        });
        var job = await Job(server);
        var rejected = 0;
        server.Options.StatusForConcurrent = c =>
        {
            if (c <= 4)
                return null;
            Interlocked.Increment(ref rejected);
            return 429;
        };
        var windowStarted = 0;
        var progress = new SyncProgress(p =>
        {
            if (p.Downloaded >= server.Options.Content.Length / 2 && Interlocked.Exchange(ref windowStarted, 1) == 0)
                server.ResetMaxConcurrent();
        });

        var result = await Download(job, progress);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
        Assert.True(rejected > 0);
        Assert.Equal(1, windowStarted);
        Assert.InRange(server.MaxConcurrentSeen, 1, 4);
    }

    [Fact]
    public async Task throttles_down_to_one_connection()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.ChunkDelay = TimeSpan.FromMilliseconds(2);
        });
        var job = await Job(server);
        server.Options.StatusForConcurrent = c => c > 1 ? 429 : null;

        var result = await Download(job);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
        Assert.NotEmpty(_delays);
        Assert.All(_delays, d => Assert.Equal(TimeSpan.FromSeconds(1), d));
    }

    [Fact]
    public async Task drops_with_progress_do_not_exhaust_retries()
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = RandomContent());
        var job = await Job(server, connections: 1);
        server.Options.FailAfterBytes = 256 * 1024;

        var result = await Download(job);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
        Assert.True(_delays.Count >= 10);
        Assert.All(_delays, d => Assert.Equal(TimeSpan.FromSeconds(1), d));
    }

    [Fact]
    public async Task gives_up_after_max_attempts()
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = RandomContent());
        var job = await Job(server, connections: 1);
        server.Options.FailAfterBytes = 0;

        var result = await Download(job);

        Assert.Equal(EngineOutcome.Failed, result.Outcome);
        Assert.NotNull(result.ErrorMessage);
        Assert.StartsWith("Lỗi mạng", result.ErrorMessage);
        Assert.Equal(5, server.RequestCount - 1);
        Assert.Equal([1, 2, 4, 8], _delays.Select(d => d.TotalSeconds));
        Assert.True(File.Exists(_target + ".part"));
    }

    [Fact]
    public async Task transient_503_recovers()
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = RandomContent());
        var job = await Job(server, connections: 4);
        var failures = 0;
        server.Options.StatusForConcurrent = _ => Interlocked.Increment(ref failures) <= 3 ? 503 : null;

        var result = await Download(job);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
        Assert.NotEmpty(_delays);
    }

    [Theory]
    [InlineData(502)]
    [InlineData(504)]
    public async Task transient_gateway_errors_recover(int status)
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = RandomContent());
        var job = await Job(server, connections: 4);
        var failures = 0;
        server.Options.StatusForConcurrent = _ => Interlocked.Increment(ref failures) <= 3 ? status : null;

        var result = await Download(job);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
        Assert.Equal(3, _delays.Count);
    }

    [Fact]
    public async Task single_connection_retries_from_start()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.HonorRange = false;
        });
        var job = await Job(server);
        server.Options.FailAfterBytes = 3 * MiB;
        server.Options.FailOnce = true;

        var result = await Download(job);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
        Assert.Single(_delays);
    }

    [Fact]
    public async Task stalled_body_times_out_and_retries()
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = RandomContent());
        var job = await Job(server, connections: 2);
        server.Options.StallOnceAfterBytes = 1 * MiB;

        var result = await Download(job, bodyTimeout: TimeSpan.FromMilliseconds(300));

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
        Assert.Single(_delays);
    }

    [Fact]
    public async Task stalled_headers_time_out_and_retry()
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = RandomContent());
        var job = await Job(server, connections: 2);
        server.Options.StallHeadersOnce = true;

        var result = await Download(job, headerTimeout: TimeSpan.FromMilliseconds(300)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
        Assert.NotEmpty(_delays);
    }

    [Fact]
    public async Task single_connection_stalled_headers_time_out_and_retry()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.HonorRange = false;
        });
        var job = await Job(server);
        server.Options.StallHeadersOnce = true;

        var result = await Download(job, headerTimeout: TimeSpan.FromMilliseconds(300)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
        Assert.Single(_delays);
    }

    [Fact]
    public async Task etag_change_within_run_needs_restart()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.ETag = "\"v1\"";
            o.ChunkDelay = TimeSpan.FromMilliseconds(5);
        });
        var job = await Job(server, connections: 1);
        var changed = 0;
        var progress = new SyncProgress(p =>
        {
            if (p.Downloaded >= 1 * MiB && Interlocked.Exchange(ref changed, 1) == 0)
            {
                server.Options.ETag = "\"v2\"";
                server.Options.FailOnce = true;
                server.Options.FailAfterBytes = 0;
            }
        });

        var result = await Download(job, progress);

        Assert.Equal(1, changed);
        Assert.Equal(EngineOutcome.NeedsRestart, result.Outcome);
        Assert.False(File.Exists(_target));
    }

    [Fact]
    public async Task failed_periodic_state_save_does_not_fail_download()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.ChunkDelay = TimeSpan.FromMilliseconds(20);
        });
        var job = await Job(server, connections: 2);
        Directory.CreateDirectory(_statePath);

        var result = await Download(job);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
    }

    [Fact]
    public async Task pause_during_backoff_keeps_part()
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = RandomContent());
        var job = await Job(server, connections: 2);
        server.Options.FailAfterBytes = 1 * MiB;
        using var pause = new CancellationTokenSource();

        var result = await Download(job, pauseToken: pause.Token, delay: (_, ct) =>
        {
            pause.Cancel();
            return Task.Delay(Timeout.Infinite, ct);
        });

        Assert.Equal(EngineOutcome.Paused, result.Outcome);
        Assert.True(File.Exists(_target + ".part"));
        Assert.False(File.Exists(_target));
        var state = DownloadState.Load(_statePath);
        Assert.NotNull(state);
        Assert.InRange(state.Segments.Sum(s => s.Downloaded), 1, server.Options.Content.LongLength - 1);
    }

    [Fact]
    public async Task expired_link_is_not_retried()
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = RandomContent());
        var job = await Job(server, connections: 1);
        server.Options.StatusCode = 403;

        var result = await Download(job);

        Assert.Equal(EngineOutcome.Failed, result.Outcome);
        Assert.Equal("Link đã hết hạn, hãy bấm tải lại trên trang web", result.ErrorMessage);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task single_run_survives_connection_drop()
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = RandomContent());
        var job = await Job(server, connections: 2);
        server.Options.FailAfterBytes = 3 * MiB;
        server.Options.FailOnce = true;

        var result = await Download(job);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
        Assert.NotEmpty(_delays);
        Assert.True(server.BytesSent < 1.2 * server.Options.Content.Length);
    }

    private sealed class SyncProgress(Action<EngineProgress> onReport) : IProgress<EngineProgress>
    {
        public void Report(EngineProgress value) => onReport(value);
    }
}
