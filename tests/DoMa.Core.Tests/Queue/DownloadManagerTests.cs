using System.Collections.Concurrent;
using DoMa.Core.Engine;
using DoMa.Core.Models;
using DoMa.Core.Queue;
using DoMa.Core.Storage;

namespace DoMa.Core.Tests.Queue;

public sealed class DownloadManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DoMaTests", Guid.NewGuid().ToString("N"));
    private readonly string _statesDir;
    private readonly FakeEngine _engine = new();
    private readonly ManualTime _time = new();
    private readonly Settings _settings = new();
    private ProbeResult _probe = new(true, null, 10000, true, null, null, "f.bin", null, ProbeFailure.None, 206);

    public DownloadManagerTests()
    {
        _statesDir = Path.Combine(_dir, "states");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private long? _free;

    private DownloadManager Manager(string? dataDir = null) =>
        new(_engine, (_, _) => Task.FromResult(_probe), new DownloadStore(dataDir ?? _dir), () => _settings, _statesDir, null, _time)
        {
            FreeSpace = _ => _free,
        };

    private static DownloadRequest Req(string url, string? cookies = null) => new(url, null, null, null, null, cookies, null);

    private string Target(string name) => Path.Combine(_dir, name);

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException();
            Thread.Sleep(10);
        }
    }

    private static DownloadStatus StatusOf(DownloadManager m, Guid id) => m.Items.Single(i => i.Id == id).Status;

    [Fact]
    public void runs_at_most_max_concurrent()
    {
        _settings.MaxConcurrent = 3;
        var m = Manager();
        for (var i = 0; i < 5; i++)
            m.Add(Req($"https://h/{i}"), Target($"{i}.bin"), true);

        WaitUntil(() => _engine.Pending.Count == 3);
        Assert.Equal(3, m.Items.Count(i => i.Status == DownloadStatus.Downloading));
        Assert.Equal(2, m.Items.Count(i => i.Status == DownloadStatus.Queued));
    }

    [Fact]
    public void mark_threat_is_saved_and_cleared_on_restart()
    {
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), false);

        m.MarkThreat(item.Id, "Defender");
        Assert.Equal("Defender", m.Items.Single().Threat);
        Assert.Equal("Defender", Manager().Items.Single().Threat);

        m.Restart(item.Id);
        Assert.Null(m.Items.Single().Threat);
    }

    [Fact]
    public void later_does_not_start()
    {
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), false);

        Thread.Sleep(100);
        Assert.Equal(DownloadStatus.Later, StatusOf(m, item.Id));
        Assert.Empty(_engine.Pending);
    }

    [Fact]
    public void resume_later_starts()
    {
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), false);

        m.Resume([item.Id]);

        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
        Assert.Equal(DownloadStatus.Downloading, StatusOf(m, item.Id));
    }

    [Fact]
    public void defer_moves_running_and_queued_back_to_later()
    {
        _settings.MaxConcurrent = 1;
        var m = Manager();
        var running = m.Add(Req("https://h/r"), Target("r.bin"), true);
        var queued = m.Add(Req("https://h/q"), Target("q.bin"), true);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/r"));

        m.Defer([running.Id, queued.Id]);
        WaitUntil(() => StatusOf(m, running.Id) == DownloadStatus.Later);
        Assert.Equal(DownloadStatus.Later, StatusOf(m, queued.Id));
    }

    [Fact]
    public void pause_then_resume()
    {
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), true);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));

        m.Pause([item.Id]);
        WaitUntil(() => StatusOf(m, item.Id) == DownloadStatus.Paused);

        m.Resume([item.Id]);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
        Assert.Equal(DownloadStatus.Downloading, StatusOf(m, item.Id));
        Assert.Equal(2, _engine.Runs);
    }

    [Fact]
    public async Task completed_raises_event_and_frees_slot()
    {
        _settings.MaxConcurrent = 1;
        var m = Manager();
        var completed = new TaskCompletionSource<DownloadItem>();
        m.Completed += i => completed.TrySetResult(i);
        var first = m.Add(Req("https://h/a"), Target("a.bin"), true);
        var second = m.Add(Req("https://h/b"), Target("b.bin"), true);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
        Assert.Equal(DownloadStatus.Queued, StatusOf(m, second.Id));

        _engine.Finish("https://h/a", new EngineResult(EngineOutcome.Completed, Target("a (1).bin"), null));

        var raised = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(first.Id, raised.Id);
        Assert.Equal(DownloadStatus.Completed, StatusOf(m, first.Id));
        Assert.Equal(Target("a (1).bin"), m.Items.Single(i => i.Id == first.Id).TargetPath);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/b"));
        Assert.Equal(DownloadStatus.Downloading, StatusOf(m, second.Id));
    }

    [Fact]
    public void unknown_size_is_taken_from_file_on_completion()
    {
        _probe = _probe with { Size = null };
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), true);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
        File.WriteAllText(Target("a.bin"), "12345");
        _engine.Finish("https://h/a", new EngineResult(EngineOutcome.Completed, Target("a.bin"), null));
        WaitUntil(() => StatusOf(m, item.Id) == DownloadStatus.Completed);

        var done = m.Items.Single();
        Assert.Equal(5, done.TotalBytes);
        Assert.Equal(5, done.DownloadedBytes);
    }

    [Fact]
    public void remove_with_delete_files_removes_part_and_target()
    {
        var m = Manager();
        var done = m.Add(Req("https://h/a"), Target("a.bin"), true);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
        File.WriteAllText(Target("a.bin"), "x");
        _engine.Finish("https://h/a", new EngineResult(EngineOutcome.Completed, Target("a.bin"), null));
        WaitUntil(() => StatusOf(m, done.Id) == DownloadStatus.Completed);

        _engine.WritePartOnPause = true;
        _engine.ReturnDelay = TimeSpan.FromMilliseconds(200);
        var running = m.Add(Req("https://h/b"), Target("b.bin"), true);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/b"));
        var state = Path.Combine(_statesDir, $"{running.Id}.json");
        Directory.CreateDirectory(_statesDir);
        File.WriteAllText(state, "{}");
        File.WriteAllText(Target("b.bin.part"), "x");
        File.WriteAllText(Target("b.bin"), "unrelated");

        var removedIds = new ConcurrentBag<Guid>();
        m.Removed += id => removedIds.Add(id);

        m.Remove([done.Id, running.Id], true);

        Assert.Empty(m.Items);
        WaitUntil(() => removedIds.Count == 2);
        Assert.Equal(new[] { done.Id, running.Id }.Order(), removedIds.Order());
        Assert.False(File.Exists(Target("a.bin")));
        Assert.False(File.Exists(Target("b.bin.part")));
        Assert.False(File.Exists(state));
        Assert.True(File.Exists(Target("b.bin")));
        Assert.Empty(new DownloadStore(_dir).Load());
    }

    [Fact]
    public void needs_restart_becomes_paused_with_message()
    {
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), true);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));

        _engine.Finish("https://h/a", new EngineResult(EngineOutcome.NeedsRestart, null, null));

        WaitUntil(() => StatusOf(m, item.Id) == DownloadStatus.Paused);
        Assert.Equal("Tệp trên máy chủ đã thay đổi — bấm Tải lại để tải từ đầu", m.Items.Single().ErrorMessage);
    }

    [Fact]
    public void expired_probe_fails_with_message()
    {
        _probe = _probe with { Ok = false, Failure = ProbeFailure.Expired, StatusCode = 403 };
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), true);

        WaitUntil(() => StatusOf(m, item.Id) == DownloadStatus.Failed);
        Assert.Equal("Link đã hết hạn. Bấm chuột phải › Làm mới link để tải tiếp", m.Items.Single().ErrorMessage);
        Assert.Equal(0, _engine.Runs);
    }

    [Fact]
    public void eta_from_speed()
    {
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), true);
        WaitUntil(() => _engine.Progress.ContainsKey("https://h/a"));
        var progress = _engine.Progress["https://h/a"];

        progress.Report(new EngineProgress(0, 10000, 1));
        _time.Advance(TimeSpan.FromSeconds(1));
        progress.Report(new EngineProgress(1000, 10000, 1));
        _time.Advance(TimeSpan.FromSeconds(1));
        progress.Report(new EngineProgress(2000, 10000, 1));

        Assert.Equal(1000, m.SpeedBytesPerSec(item.Id), 3);
        Assert.Equal(TimeSpan.FromSeconds(8), m.Eta(item.Id));

        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, m.SpeedBytesPerSec(item.Id));
        Assert.Null(m.Eta(item.Id));
    }

    [Fact]
    public void completed_or_removed_clears_cookies()
    {
        var m = Manager();
        var done = m.Add(Req("https://h/a", "sid=A"), Target("a.bin"), true);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
        Assert.Equal("sid=A", _engine.Jobs["https://h/a"].Request.Cookies);
        _engine.Finish("https://h/a", new EngineResult(EngineOutcome.Completed, Target("a.bin"), null));
        WaitUntil(() => StatusOf(m, done.Id) == DownloadStatus.Completed);

        var removed = m.Add(Req("https://h/b", "sid=B"), Target("b.bin"), false);
        m.Remove([removed.Id], false);

        Assert.Null(m.Items.Single(i => i.Id == done.Id).Request.Cookies);
        Assert.Null(removed.Request.Cookies);
        var stored = new DownloadStore(_dir).Load();
        Assert.Equal(done.Id, stored.Single().Id);
        Assert.Null(stored.Single().Request.Cookies);
        Assert.DoesNotContain("\"ProtectedCookies\": \"", File.ReadAllText(Path.Combine(_dir, "downloads.json")));
    }

    [Fact]
    public void persist_failure_does_not_stall_queue()
    {
        _settings.MaxConcurrent = 1;
        var dataDir = Path.Combine(_dir, "locked");
        Directory.CreateDirectory(Path.Combine(dataDir, "downloads.json"));
        var m = Manager(dataDir);
        var completed = 0;
        m.Completed += _ => Interlocked.Increment(ref completed);
        var first = m.Add(Req("https://h/a"), Target("a.bin"), true);
        var second = m.Add(Req("https://h/b"), Target("b.bin"), true);
        WaitUntil(() => _engine.Progress.ContainsKey("https://h/a"));

        _time.Advance(TimeSpan.FromSeconds(6));
        _engine.Progress["https://h/a"].Report(new EngineProgress(10, 10000, 1));
        _engine.Finish("https://h/a", new EngineResult(EngineOutcome.Completed, Target("a.bin"), null));

        WaitUntil(() => StatusOf(m, first.Id) == DownloadStatus.Completed);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/b"));
        Assert.Equal(DownloadStatus.Downloading, StatusOf(m, second.Id));
        WaitUntil(() => Volatile.Read(ref completed) == 1);
        m.Remove([second.Id], true);
        Assert.DoesNotContain(m.Items, i => i.Id == second.Id);
    }

    [Fact]
    public void pause_then_immediate_resume()
    {
        _engine.ReturnDelay = TimeSpan.FromMilliseconds(200);
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), true);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));

        m.Pause([item.Id]);
        m.Resume([item.Id]);

        WaitUntil(() => _engine.Runs == 2);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
        Assert.Equal(DownloadStatus.Downloading, StatusOf(m, item.Id));
    }

    [Fact]
    public void remove_after_engine_completed_deletes_target()
    {
        _engine.ReturnDelay = TimeSpan.FromMilliseconds(200);
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), true);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
        File.WriteAllText(Target("a (1).bin"), "done");

        _engine.Finish("https://h/a", new EngineResult(EngineOutcome.Completed, Target("a (1).bin"), null));
        m.Remove([item.Id], true);

        Assert.Empty(m.Items);
        Assert.False(File.Exists(Target("a (1).bin")));
    }

    [Fact]
    public void restart_paused_item_deletes_progress_and_runs()
    {
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), true);
        WaitUntil(() => _engine.Progress.ContainsKey("https://h/a"));
        _engine.Progress["https://h/a"].Report(new EngineProgress(500, 10000, 1));
        m.Pause([item.Id]);
        WaitUntil(() => StatusOf(m, item.Id) == DownloadStatus.Paused);
        var state = Path.Combine(_statesDir, $"{item.Id}.json");
        Directory.CreateDirectory(_statesDir);
        File.WriteAllText(state, "{}");
        File.WriteAllText(Target("a.bin.part"), "x");

        m.Restart(item.Id);

        Assert.False(File.Exists(state));
        Assert.False(File.Exists(Target("a.bin.part")));
        WaitUntil(() => _engine.Runs == 2);
        WaitUntil(() => StatusOf(m, item.Id) == DownloadStatus.Downloading);
        Assert.Equal(0, m.Items.Single().DownloadedBytes);
    }

    [Fact]
    public void restart_running_item_waits_for_run_then_deletes_progress()
    {
        _engine.WritePartOnPause = true;
        _engine.ReturnDelay = TimeSpan.FromMilliseconds(200);
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), true);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));

        m.Restart(item.Id);

        Assert.False(File.Exists(Path.Combine(_statesDir, $"{item.Id}.json")));
        Assert.False(File.Exists(Target("a.bin.part")));
        WaitUntil(() => _engine.Runs == 2);
        WaitUntil(() => StatusOf(m, item.Id) == DownloadStatus.Downloading);
    }

    [Fact]
    public void add_makes_target_unique_against_items_in_progress()
    {
        _settings.MaxConcurrent = 1;
        var m = Manager();

        var first = m.Add(Req("https://h/a"), Target("a.bin"), true);
        var second = m.Add(Req("https://h/b"), Target("A.bin"), false);

        Assert.Equal(Target("a.bin"), first.TargetPath);
        Assert.Equal(Target("A (1).bin"), second.TargetPath);
    }

    [Fact]
    public void add_skips_existing_part_and_file()
    {
        File.WriteAllText(Target("a.bin.part"), "x");
        File.WriteAllText(Target("a (1).bin"), "x");
        var m = Manager();

        var item = m.Add(Req("https://h/a"), Target("a.bin"), false);

        Assert.Equal(Target("a (2).bin"), item.TargetPath);
    }

    [Fact]
    public void reschedule_starts_queued_items_after_max_concurrent_grows()
    {
        _settings.MaxConcurrent = 1;
        var m = Manager();
        m.Add(Req("https://h/a"), Target("a.bin"), true);
        var second = m.Add(Req("https://h/b"), Target("b.bin"), true);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
        Assert.Equal(DownloadStatus.Queued, StatusOf(m, second.Id));

        _settings.MaxConcurrent = 2;
        m.Reschedule();

        WaitUntil(() => _engine.Pending.ContainsKey("https://h/b"));
        Assert.Equal(DownloadStatus.Downloading, StatusOf(m, second.Id));
    }

    [Fact]
    public void can_resume_until_probe_reports_no_range()
    {
        _probe = _probe with { SupportsRange = false };
        var m = Manager();

        var item = m.Add(Req("https://h/a"), Target("a.bin"), false);
        Assert.True(item.CanResume);

        m.Resume([item.Id]);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
        Assert.False(m.Items.Single().CanResume);
    }

    [Fact]
    public void fails_early_when_disk_is_too_small()
    {
        _probe = _probe with { Size = 100L << 20 };
        _free = 50L << 20;
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), true);

        WaitUntil(() => StatusOf(m, item.Id) == DownloadStatus.Failed);
        Assert.Contains("không đủ chỗ", m.Items.Single().ErrorMessage);
        Assert.Equal(0, _engine.Runs);
        Assert.False(m.Items.Single().NetworkError);
    }

    [Fact]
    public void resume_counts_space_already_reserved_by_part_file()
    {
        _probe = _probe with { Size = 100L << 20 };
        _free = 50L << 20;
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), false);
        using (var part = File.Create(item.TargetPath + ".part"))
            part.SetLength(80L << 20);

        m.Resume([item.Id]);

        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
    }

    [Fact]
    public void network_failure_resumes_after_backoff()
    {
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), true);
        _engine.Finish("https://h/a", new EngineResult(EngineOutcome.Failed, null, "mất mạng", Network: true));
        WaitUntil(() => StatusOf(m, item.Id) == DownloadStatus.Failed);
        Assert.True(m.Items.Single().NetworkError);

        Assert.Equal(0, m.ResumeNetworkFailures());
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, m.ResumeNetworkFailures());
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
        Assert.Equal(2, _engine.Runs);
    }

    [Fact]
    public void network_back_resumes_immediately_but_not_other_failures()
    {
        var m = Manager();
        var net = m.Add(Req("https://h/a"), Target("a.bin"), true);
        var other = m.Add(Req("https://h/b"), Target("b.bin"), true);
        _engine.Finish("https://h/a", new EngineResult(EngineOutcome.Failed, null, "mất mạng", Network: true));
        _engine.Finish("https://h/b", new EngineResult(EngineOutcome.Failed, null, "Máy chủ trả lỗi 500"));
        WaitUntil(() => StatusOf(m, net.Id) == DownloadStatus.Failed && StatusOf(m, other.Id) == DownloadStatus.Failed);

        Assert.Equal(1, m.ResumeNetworkFailures(now: true));
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
        Assert.Equal(DownloadStatus.Failed, StatusOf(m, other.Id));
    }

    [Fact]
    public void network_auto_resume_stops_after_max_tries()
    {
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), true);
        for (var i = 0; i <= DownloadManager.MaxAutoRetries; i++)
        {
            _engine.Finish("https://h/a", new EngineResult(EngineOutcome.Failed, null, "mất mạng", Network: true));
            WaitUntil(() => StatusOf(m, item.Id) == DownloadStatus.Failed && !_engine.Pending.ContainsKey("https://h/a"));
            if (i < DownloadManager.MaxAutoRetries)
                Assert.Equal(1, m.ResumeNetworkFailures(now: true));
        }
        Assert.Equal(0, m.ResumeNetworkFailures(now: true));

        m.Resume([item.Id]);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
        Assert.Equal(0, m.Items.Single().AutoRetries);
    }

    [Fact]
    public void network_error_flag_is_saved()
    {
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), true);
        _engine.Finish("https://h/a", new EngineResult(EngineOutcome.Failed, null, "mất mạng", Network: true));
        WaitUntil(() => StatusOf(m, item.Id) == DownloadStatus.Failed);

        Assert.True(new DownloadStore(_dir).Load().Single().NetworkError);
    }

    [Fact]
    public void replace_link_requeues_failed_item_with_new_url()
    {
        var m = Manager();
        var item = m.Add(Req("https://h/old"), Target("a.bin"), true);
        _engine.Finish("https://h/old", new EngineResult(EngineOutcome.Failed, null, "hết hạn"));
        WaitUntil(() => StatusOf(m, item.Id) == DownloadStatus.Failed);

        Assert.True(m.ReplaceLink(item.Id, Req("https://h/new")));

        WaitUntil(() => _engine.Pending.ContainsKey("https://h/new"));
        Assert.True(_engine.Jobs["https://h/new"].LinkRefreshed);
        Assert.Equal("https://h/new", m.Items.Single().Request.Url);
        Assert.Null(m.Items.Single().ErrorMessage);
    }

    [Fact]
    public void replace_link_refuses_running_or_completed()
    {
        var m = Manager();
        var item = m.Add(Req("https://h/a"), Target("a.bin"), true);
        WaitUntil(() => _engine.Pending.ContainsKey("https://h/a"));
        Assert.False(m.ReplaceLink(item.Id, Req("https://h/b")));

        _engine.Finish("https://h/a", new EngineResult(EngineOutcome.Completed, Target("a.bin"), null));
        WaitUntil(() => StatusOf(m, item.Id) == DownloadStatus.Completed);
        Assert.False(m.ReplaceLink(item.Id, Req("https://h/b")));
    }

    [Fact]
    public void refresh_watch_matches_by_name_or_size_within_window()
    {
        var watch = new RefreshWatch(_time);
        var a = new DownloadItem { Id = Guid.NewGuid(), Request = Req("https://h/a"), TargetPath = Target("Phim (2).mp4"), TotalBytes = 5 << 20, Status = DownloadStatus.Failed };
        var b = new DownloadItem { Id = Guid.NewGuid(), Request = Req("https://h/b"), TargetPath = Target("other.zip"), TotalBytes = 9 << 20, Status = DownloadStatus.Paused };
        DownloadItem[] items = [a, b];

        Assert.Null(watch.Match(items, "phim.mp4", null));
        watch.Arm(a.Id);
        watch.Arm(b.Id);
        Assert.Same(a, watch.Match(items, "PHIM.mp4", null));
        Assert.Same(b, watch.Match(items, "random-token.bin", 9 << 20));
        Assert.Null(watch.Match(items, "x.bin", 1000));

        _time.Advance(RefreshWatch.Window + TimeSpan.FromSeconds(1));
        Assert.Null(watch.Match(items, "phim.mp4", null));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeEngine : IDownloadEngine
    {
        private int _runs;

        public ConcurrentDictionary<string, TaskCompletionSource<EngineResult>> Pending { get; } = new();
        public ConcurrentDictionary<string, IProgress<EngineProgress>> Progress { get; } = new();
        public ConcurrentDictionary<string, DownloadJob> Jobs { get; } = new();
        public bool WritePartOnPause { get; set; }
        public TimeSpan ReturnDelay { get; set; }
        public int Runs => _runs;

        public async Task<EngineResult> RunAsync(DownloadJob job, IProgress<EngineProgress> progress, CancellationToken pauseToken)
        {
            Interlocked.Increment(ref _runs);
            var url = job.Request.Url;
            var tcs = new TaskCompletionSource<EngineResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Jobs[url] = job;
            Progress[url] = progress;
            Pending[url] = tcs;
            using var reg = pauseToken.Register(() => tcs.TrySetResult(new EngineResult(EngineOutcome.Paused, null, null)));
            var result = await tcs.Task;
            Pending.TryRemove(new KeyValuePair<string, TaskCompletionSource<EngineResult>>(url, tcs));
            await Task.Delay(ReturnDelay);
            if (result.Outcome == EngineOutcome.Paused && WritePartOnPause)
            {
                File.WriteAllText(job.TargetPath + ".part", "late");
                Directory.CreateDirectory(Path.GetDirectoryName(job.StatePath)!);
                File.WriteAllText(job.StatePath, "{}");
            }
            return result;
        }

        public void Finish(string url, EngineResult result)
        {
            WaitUntil(() => Pending.ContainsKey(url));
            Pending[url].TrySetResult(result);
        }
    }
}
