using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using DoMa.Core.Engine;
using DoMa.Core.Models;
using DoMa.Core.Tests.TestServer;

namespace DoMa.Core.Tests.Engine;

public sealed class SegmentedDownloadEngineTests : IDisposable
{
    private const int MiB = 1024 * 1024;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DoMaTests", Guid.NewGuid().ToString("N"));
    private readonly List<(DirectoryInfo Dir, FileSystemAccessRule Rule)> _denied = [];

    public SegmentedDownloadEngineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var (dir, rule) in _denied)
        {
            var acl = dir.GetAccessControl();
            acl.RemoveAccessRuleSpecific(rule);
            dir.SetAccessControl(acl);
        }
        Directory.Delete(_dir, true);
    }

    private static byte[] RandomContent()
    {
        var content = new byte[20 * MiB];
        Random.Shared.NextBytes(content);
        return content;
    }

    private string StatePath() => Path.Combine(_dir, "state", Guid.NewGuid().ToString("N") + ".json");

    private static async Task<EngineResult> Download(DownloadJob job, CancellationToken pauseToken = default, IProgress<EngineProgress>? progress = null)
    {
        using var http = HttpClientFactory.Create();
        return await new SegmentedDownloadEngine(http).RunAsync(job, progress ?? new Progress<EngineProgress>(), pauseToken);
    }

    private static async Task<DownloadJob> Job(RangeServer server, string target, string statePath, int connections = 16)
    {
        using var http = HttpClientFactory.Create();
        var req = new DownloadRequest(server.Url("/f.bin").ToString(), null, null, null, null, null, null);
        var probe = await new LinkProber(http).ProbeAsync(req, CancellationToken.None);
        Assert.True(probe.Ok);
        return new DownloadJob(req, target, statePath, connections, probe);
    }

    private static void AssertSameContent(byte[] expected, string path) =>
        Assert.Equal(SHA256.HashData(expected), SHA256.HashData(File.ReadAllBytes(path)));

    private async Task<(EngineResult Result, string Target, string StatePath)> DownloadFresh(RangeServer server, string? target = null)
    {
        target ??= Path.Combine(_dir, "f.bin");
        var statePath = StatePath();
        var result = await Download(await Job(server, target, statePath));
        return (result, target, statePath);
    }

    [Fact]
    public async Task downloads_with_many_connections()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.ChunkDelay = TimeSpan.FromMilliseconds(2);
        });
        var (result, target, statePath) = await DownloadFresh(server);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        Assert.Equal(target, result.FinalPath);
        AssertSameContent(server.Options.Content, target);
        Assert.True(server.MaxConcurrentSeen > 1);
        Assert.False(File.Exists(target + ".part"));
        Assert.False(File.Exists(statePath));
    }

    [Fact]
    public async Task cookies_are_not_sent_to_another_origin()
    {
        await using var target = await RangeServer.StartAsync(o => o.Content = RandomContent());
        await using var origin = await RangeServer.StartAsync(o => o.RedirectLocation = target.Url("/f.bin").ToString());
        using var http = HttpClientFactory.Create();
        var req = new DownloadRequest(origin.Url("/redirect").ToString(), null, null, null, null, "s=1", null);
        var probe = await new LinkProber(http).ProbeAsync(req, CancellationToken.None);
        var path = Path.Combine(_dir, "f.bin");

        var result = await Download(new DownloadJob(req, path, StatePath(), 4, probe));

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(target.Options.Content, path);
        Assert.True(target.Requests.Count > 1);
        Assert.All(target.Requests, q => Assert.Equal("", q.Cookie));
    }

    [Fact]
    public async Task range_ignored_falls_back_to_single_connection()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.HonorRangeAfterProbe = false;
        });
        var (result, target, _) = await DownloadFresh(server);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, target);
    }

    [Fact]
    public async Task no_range_single_connection()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.HonorRange = false;
        });
        var (result, target, _) = await DownloadFresh(server);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, target);
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task unknown_size_streams_to_end()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.HonorRange = false;
            o.SendLength = false;
        });
        var target = Path.Combine(_dir, "f.bin");
        var job = await Job(server, target, StatePath());
        Assert.Null(job.Probe.Size);

        var result = await Download(job);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, target);
    }

    [Fact]
    public async Task free_worker_splits_largest_segment()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.ChunkDelayForStart = start => start == 10 * MiB ? TimeSpan.FromMilliseconds(20) : TimeSpan.Zero;
        });
        var target = Path.Combine(_dir, "f.bin");
        var job = await Job(server, target, StatePath(), connections: 2);

        var result = await Download(job);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, target);
        Assert.True(server.RequestCount > 3);
    }

    [Fact]
    public async Task wrong_content_range_falls_back_to_single_connection()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.ShiftRangeStart = true;
        });
        var (result, target, _) = await DownloadFresh(server);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, target);
    }

    [Fact]
    public async Task stale_longer_part_is_truncated()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.HonorRange = false;
            o.SendLength = false;
        });
        var target = Path.Combine(_dir, "f.bin");
        File.WriteAllBytes(target + ".part", new byte[server.Options.Content.Length + 1000]);

        var (result, _, _) = await DownloadFresh(server, target);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, target);
    }

    [Fact]
    public async Task creates_missing_folder()
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = RandomContent());
        var (result, target, _) = await DownloadFresh(server, Path.Combine(_dir, "a", "b", "f.bin"));

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        AssertSameContent(server.Options.Content, target);
    }

    [Fact]
    public async Task unwritable_target_fails_cleanly()
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = RandomContent());
        var locked = Directory.CreateDirectory(Path.Combine(_dir, "locked"));
        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
            AccessControlType.Deny);
        var acl = locked.GetAccessControl();
        acl.AddAccessRule(rule);
        locked.SetAccessControl(acl);
        _denied.Add((locked, rule));

        var (result, target, _) = await DownloadFresh(server, Path.Combine(locked.FullName, "f.bin"));

        Assert.Equal(EngineOutcome.Failed, result.Outcome);
        Assert.NotNull(result.ErrorMessage);
        Assert.StartsWith("Không ghi được", result.ErrorMessage);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task does_not_overwrite_existing()
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = RandomContent());
        var target = Path.Combine(_dir, "f.bin");
        byte[] original = [1, 2, 3, 4, 5];
        File.WriteAllBytes(target, original);

        var (result, _, _) = await DownloadFresh(server, target);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        Assert.NotNull(result.FinalPath);
        Assert.EndsWith(" (1).bin", result.FinalPath);
        AssertSameContent(server.Options.Content, result.FinalPath);
        Assert.Equal(original, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task pause_returns_paused_and_keeps_part()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = RandomContent();
            o.ChunkDelay = TimeSpan.FromMilliseconds(100);
        });
        var target = Path.Combine(_dir, "f.bin");
        var statePath = StatePath();
        var job = await Job(server, target, statePath);
        using var pause = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Downloaded >= 2 * MiB)
                pause.Cancel();
        });

        var result = await Download(job, pause.Token, progress);

        Assert.Equal(EngineOutcome.Paused, result.Outcome);
        Assert.True(File.Exists(target + ".part"));
        Assert.False(File.Exists(target));
        var state = DownloadState.Load(statePath);
        Assert.NotNull(state);
        var downloaded = state.Segments.Sum(s => s.Downloaded);
        Assert.InRange(downloaded, 1, server.Options.Content.LongLength - 1);
    }

    private sealed class SyncProgress(Action<EngineProgress> onReport) : IProgress<EngineProgress>
    {
        public void Report(EngineProgress value) => onReport(value);
    }
}
