using System.Security.Cryptography;
using DoMa.Core.Engine;
using DoMa.Core.Models;
using DoMa.Core.Tests.TestServer;

namespace DoMa.Core.Tests.Engine;

public sealed class ResumeTests : IDisposable
{
    private const int MiB = 1024 * 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DoMaTests", Guid.NewGuid().ToString("N"));
    private readonly string _target;
    private readonly string _statePath;

    public ResumeTests()
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

    private static async Task<EngineResult> Download(DownloadJob job, CancellationToken pauseToken = default, IProgress<EngineProgress>? progress = null)
    {
        using var http = HttpClientFactory.Create();
        return await new SegmentedDownloadEngine(http, (_, _) => Task.CompletedTask).RunAsync(job, progress ?? new Progress<EngineProgress>(), pauseToken);
    }

    private async Task<DownloadJob> Job(RangeServer server, int connections = 16)
    {
        using var http = HttpClientFactory.Create();
        var req = new DownloadRequest(server.Url("/f.bin").ToString(), null, null, null, null, null, null);
        var probe = await new LinkProber(http).ProbeAsync(req, CancellationToken.None);
        Assert.True(probe.Ok);
        return new DownloadJob(req, _target, _statePath, connections, probe);
    }

    private static Task<RangeServer> SlowServer(Action<RangeServerOptions>? configure = null) =>
        RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.ChunkDelay = TimeSpan.FromMilliseconds(100);
            configure?.Invoke(o);
        });

    private async Task<DownloadJob> PauseAt(RangeServer server, long bytes)
    {
        var job = await Job(server);
        using var pause = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Downloaded >= bytes)
                pause.Cancel();
        });

        var result = await Download(job, pause.Token, progress);

        Assert.Equal(EngineOutcome.Paused, result.Outcome);
        Assert.True(File.Exists(_target + ".part"));
        server.Options.ChunkDelay = TimeSpan.Zero;
        return job;
    }

    private static void AssertSameContent(byte[] expected, string? path)
    {
        Assert.NotNull(path);
        Assert.Equal(SHA256.HashData(expected), SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task resumes_after_pause_byte_exact()
    {
        await using var server = await SlowServer();
        await PauseAt(server, 5 * MiB);
        var saved = DownloadState.Load(_statePath)!.Segments.Sum(s => s.Downloaded);
        Assert.True(saved >= 5 * MiB);
        long lowest = long.MaxValue;
        var progress = new SyncProgress(p => lowest = Math.Min(lowest, p.Downloaded));

        var result = await Download(await Job(server), progress: progress);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
        // Tải tiếp bắt đầu từ phần đã lưu chứ không tải lại từ đầu. (Không đo số byte máy chủ gửi: các đoạn bị bỏ dở
        // giữa chừng vẫn được máy chủ gửi thêm một ít, nhiều hay ít tuỳ tải của máy chạy kiểm thử.)
        Assert.True(lowest >= saved);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public async Task resumes_after_connection_drop()
    {
        await using var server = await SlowServer(o => o.ChunkDelay = TimeSpan.FromMilliseconds(20));
        var progress = new SyncProgress(p =>
        {
            if (p.Downloaded >= 2 * MiB)
                server.Options.FailAfterBytes = 0;
        });

        var failed = await Download(await Job(server, connections: 2), progress: progress);
        Assert.Equal(EngineOutcome.Failed, failed.Outcome);
        Assert.StartsWith("Lỗi mạng", failed.ErrorMessage);
        Assert.True(failed.Network);
        server.Options.FailAfterBytes = null;
        server.Options.ChunkDelay = TimeSpan.Zero;
        var sentBefore = server.BytesSent;

        var result = await Download(await Job(server, connections: 2));

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
        Assert.True(server.BytesSent - sentBefore < server.Options.Content.Length);
    }

    [Fact]
    public async Task etag_change_needs_restart()
    {
        await using var server = await SlowServer(o => o.ETag = "\"v1\"");
        await PauseAt(server, 2 * MiB);
        server.Options.ETag = "\"v2\"";

        var result = await Download(await Job(server));

        Assert.Equal(EngineOutcome.NeedsRestart, result.Outcome);
        Assert.True(File.Exists(_target + ".part"));
        Assert.False(File.Exists(_target));
    }

    [Fact]
    public async Task refreshed_link_resumes_despite_new_etag()
    {
        await using var server = await SlowServer(o => o.ETag = "\"v1\"");
        await PauseAt(server, 2 * MiB);
        server.Options.ETag = "\"v2\"";
        server.Options.ChunkDelay = TimeSpan.Zero;
        var sentBefore = server.BytesSent;

        var result = await Download(await Job(server) with { LinkRefreshed = true });

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
        Assert.True(server.BytesSent - sentBefore < server.Options.Content.Length);
    }

    [Fact]
    public async Task expired_link_on_resume()
    {
        await using var server = await SlowServer();
        var job = await PauseAt(server, 2 * MiB);
        server.Options.StatusCode = 403;

        var result = await Download(job);

        Assert.Equal(EngineOutcome.Failed, result.Outcome);
        Assert.Equal("Link đã hết hạn. Bấm chuột phải › Làm mới link để tải tiếp", result.ErrorMessage);
        Assert.True(File.Exists(_target + ".part"));
    }

    [Fact]
    public async Task no_range_download_restarts_from_zero()
    {
        await using var server = await SlowServer(o => o.HonorRange = false);
        await PauseAt(server, 1 * MiB);

        var result = await Download(await Job(server));

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
    }

    [Fact]
    public async Task missing_part_file_restarts()
    {
        await using var server = await SlowServer();
        await PauseAt(server, 2 * MiB);
        File.Delete(_target + ".part");

        var result = await Download(await Job(server));

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
    }

    [Fact]
    public async Task corrupt_state_restarts()
    {
        await using var server = await SlowServer();
        await PauseAt(server, 2 * MiB);
        File.WriteAllText(_statePath, "{not json");

        var result = await Download(await Job(server));

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
    }

    [Fact]
    public async Task inconsistent_state_restarts()
    {
        await using var server = await SlowServer();
        await PauseAt(server, 2 * MiB);
        var state = DownloadState.Load(_statePath);
        Assert.NotNull(state);
        state.Segments.RemoveAt(1);
        state.Save(_statePath);

        var result = await Download(await Job(server));

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, result.FinalPath);
    }

    private sealed class SyncProgress(Action<EngineProgress> onReport) : IProgress<EngineProgress>
    {
        public void Report(EngineProgress value) => onReport(value);
    }
}
