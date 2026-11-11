using DoMa.Core.Engine;
using DoMa.Core.Files;
using DoMa.Core.Models;
using DoMa.Core.Storage;

namespace DoMa.Core.Queue;

public class DownloadManager
{
    private static string NeedsRestartMessage => L.T("Tệp trên máy chủ đã thay đổi — bấm Tải lại để tải từ đầu");
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(5);

    private readonly IDownloadEngine _engine;
    private readonly Func<DownloadRequest, CancellationToken, Task<ProbeResult>> _probe;
    private readonly DownloadStore _store;
    private readonly Func<Settings> _settings;
    private readonly string _statesDir;
    private readonly SynchronizationContext? _context;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<DownloadItem> _items;
    private readonly Dictionary<Guid, Run> _running = [];
    private readonly HashSet<Guid> _restarting = [];
    /// <summary>Tệp vừa bị dừng để chạy lại (vd. đổi giới hạn tốc độ): được chạy lại trước các tệp khác đang chờ.</summary>
    private readonly HashSet<Guid> _preferred = [];
    private DateTimeOffset _lastPersist;

    public DownloadManager(
        IDownloadEngine engine,
        Func<DownloadRequest, CancellationToken, Task<ProbeResult>> probe,
        DownloadStore store,
        Func<Settings> settings,
        string statesDir,
        SynchronizationContext? context = null,
        TimeProvider? time = null)
    {
        _engine = engine;
        _probe = probe;
        _store = store;
        _settings = settings;
        _statesDir = statesDir;
        _context = context;
        _time = time ?? TimeProvider.System;
        _items = store.Load();
        lock (_gate)
            Pump();
    }

    public event Action<DownloadItem>? Changed;
    public event Action<DownloadItem>? Completed;
    public event Action<Guid>? Removed;

    public IReadOnlyList<DownloadItem> Items
    {
        get
        {
            lock (_gate)
                return _items.ToArray();
        }
    }

    public DownloadItem Add(DownloadRequest req, string targetPath, bool startNow)
    {
        var settings = _settings();
        var item = new DownloadItem
        {
            Id = Guid.NewGuid(),
            Request = req,
            Category = new CategoryResolver(settings.Extensions, settings.RootFolder).Resolve(Path.GetFileName(targetPath)),
            Status = startNow ? DownloadStatus.Queued : DownloadStatus.Later,
            TotalBytes = req.SizeHint,
            AddedAt = _time.GetUtcNow(),
        };
        lock (_gate)
        {
            var taken = _items.Where(i => i.Status != DownloadStatus.Completed).Select(i => i.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            item.TargetPath = FileNameResolver.MakeUnique(targetPath, p => taken.Contains(p) || File.Exists(p) || File.Exists(p + ".part"));
            _items.Add(item);
            Persist();
            Raise(Changed, item);
            Pump();
        }
        return item;
    }

    public void Reschedule()
    {
        lock (_gate)
            Pump();
    }

    public void Resume(IEnumerable<Guid> ids)
    {
        lock (_gate)
        {
            foreach (var item in Find(ids))
            {
                if (_restarting.Contains(item.Id))
                    continue;
                if (_running.TryGetValue(item.Id, out var run))
                {
                    if (run.Cts.IsCancellationRequested && !run.Removed)
                        run.ResumeRequested = true;
                    continue;
                }
                if (item.Status is not (DownloadStatus.Later or DownloadStatus.Paused or DownloadStatus.Failed))
                    continue;
                item.AutoRetries = 0;
                item.Status = DownloadStatus.Queued;
                item.ErrorMessage = null;
                Raise(Changed, item);
            }
            Persist();
            Pump();
        }
    }

    /// <summary>
    /// "Tải trước": đưa các tệp lên đầu hàng đợi và cho chạy ngay. Nếu đã đủ số tệp tải cùng lúc, tạm nhường
    /// các tệp đang tải ở cuối hàng (chúng quay về hàng đợi và tải tiếp sau, không mất phần đã tải).
    /// </summary>
    public void StartFirst(IEnumerable<Guid> ids)
    {
        lock (_gate)
        {
            var targets = Find(ids)
                .Where(i => i.Status != DownloadStatus.Completed && !_running.ContainsKey(i.Id) && !_restarting.Contains(i.Id))
                .ToList();
            if (targets.Count == 0)
                return;
            foreach (var item in Enumerable.Reverse(targets))
            {
                _items.Remove(item);
                _items.Insert(0, item);
                item.Status = DownloadStatus.Queued;
                item.ErrorMessage = null;
                item.AutoRetries = 0;
                Raise(Changed, item);
            }

            var max = Math.Max(1, _settings().MaxConcurrent);
            var active = _running.Values.Where(r => !r.Removed && !r.Cts.IsCancellationRequested).ToList();
            var free = max - active.Count;
            var bump = active
                .OrderByDescending(r => _items.IndexOf(r.Item))
                .Take(Math.Max(0, Math.Min(targets.Count - free, active.Count)));
            foreach (var run in bump)
            {
                run.ResumeRequested = true;
                run.Cts.Cancel();
            }
            Persist();
            Pump();
        }
    }

    /// <summary>
    /// Đưa các tệp đang chờ lên (hoặc xuống) một bậc trong hàng chờ. Chỉ đổi chỗ với các tệp chưa tải xong
    /// và chưa chạy; tệp đang tải không bị ảnh hưởng. Trả về true nếu có thay đổi.
    /// </summary>
    public bool Move(IEnumerable<Guid> ids, bool up)
    {
        lock (_gate)
        {
            var selected = ids.ToHashSet();
            var slots = Enumerable.Range(0, _items.Count).Where(i => IsWaiting(_items[i])).ToList();
            if (!up)
                slots.Reverse();
            var moved = false;
            for (var k = 1; k < slots.Count; k++)
            {
                var (before, here) = (slots[k - 1], slots[k]);
                if (!selected.Contains(_items[here].Id) || selected.Contains(_items[before].Id))
                    continue;
                (_items[before], _items[here]) = (_items[here], _items[before]);
                moved = true;
            }
            if (!moved)
                return false;
            Persist();
            foreach (var item in Find(selected))
                Raise(Changed, item);
            return true;
        }
    }

    private bool IsWaiting(DownloadItem item) =>
        item.Status is DownloadStatus.Queued or DownloadStatus.Later or DownloadStatus.Paused or DownloadStatus.Failed
        && !_running.ContainsKey(item.Id) && !_restarting.Contains(item.Id);

    /// <summary>Thứ tự sẽ được tải của các tệp "Đang chờ" (1 = tải tiếp theo).</summary>
    public IReadOnlyDictionary<Guid, int> QueuePositions()
    {
        lock (_gate)
        {
            var queued = _items.Where(i => i.Status == DownloadStatus.Queued && !_running.ContainsKey(i.Id) && !_restarting.Contains(i.Id)).ToList();
            var order = queued.Where(i => _preferred.Contains(i.Id)).Concat(queued.Where(i => !_preferred.Contains(i.Id)));
            var positions = new Dictionary<Guid, int>();
            foreach (var item in order)
                positions[item.Id] = positions.Count + 1;
            return positions;
        }
    }

    /// <summary>
    /// Đặt tốc độ tối đa riêng cho các tệp (KB/s, 0 = bỏ giới hạn riêng). Tệp đang tải bằng luồng của DoMa đổi tốc độ ngay;
    /// tệp tải bằng yt-dlp hoặc aria2c được dừng rồi chạy lại ngay (không mất phần đã tải) để áp mức mới.
    /// </summary>
    public void SetSpeedLimit(IEnumerable<Guid> ids, int kbps)
    {
        kbps = Math.Max(0, kbps);
        lock (_gate)
        {
            var changed = false;
            foreach (var item in Find(ids))
            {
                if (item.Status == DownloadStatus.Completed || item.SpeedLimitKBps == kbps)
                    continue;
                item.SpeedLimitKBps = kbps;
                changed = true;
                if (_running.TryGetValue(item.Id, out var run) && !run.Cts.IsCancellationRequested)
                {
                    run.Limiter.LimitBytesPerSec = kbps * 1024L;
                    if (Engine.Hls.MediaAwareEngine.UsesExternalTool(item.Request))
                        Requeue(run);
                }
                Raise(Changed, item);
            }
            if (changed)
                Persist();
        }
    }

    /// <summary>Giới hạn tốc độ chung vừa đổi: chạy lại các tệp đang tải bằng yt-dlp/aria2c để chúng theo mức mới.</summary>
    public void ApplySpeedLimitToTools()
    {
        lock (_gate)
        {
            foreach (var run in _running.Values.Where(r => !r.Removed && !r.Cts.IsCancellationRequested && Engine.Hls.MediaAwareEngine.UsesExternalTool(r.Item.Request)).ToList())
                Requeue(run);
        }
    }

    private void Requeue(Run run)
    {
        run.ResumeRequested = true;
        _preferred.Add(run.Item.Id);
        run.Cts.Cancel();
    }

    /// <summary>Bỏ khỏi danh sách các tệp đã tải xong trước <paramref name="cutoff"/> (giữ nguyên tệp trên ổ đĩa). Trả về số tệp đã bỏ.</summary>
    public int ClearCompletedBefore(DateTimeOffset cutoff)
    {
        List<Guid> old;
        lock (_gate)
            old = _items.Where(i => i.Status == DownloadStatus.Completed && (i.CompletedAt ?? i.AddedAt) < cutoff).Select(i => i.Id).ToList();
        if (old.Count > 0)
            Remove(old, deleteFiles: false);
        return old.Count;
    }

    /// <summary>Số lần tự tải tiếp tối đa cho một tệp sau lỗi mạng (sau đó chờ người dùng bấm).</summary>
    public const int MaxAutoRetries = 8;

    /// <summary>
    /// Có mạng (lại): cho chạy tiếp các tệp lỗi vì mất mạng. Mặc định chỉ những tệp đã tới lượt thử
    /// (lùi dần giữa các lần); <paramref name="now"/> = true khi vừa có mạng lại thì thử ngay. Trả về số tệp được chạy lại.
    /// </summary>
    public int ResumeNetworkFailures(bool now = false)
    {
        lock (_gate)
        {
            var at = _time.GetUtcNow();
            var due = _items.Where(i => i is { Status: DownloadStatus.Failed, NetworkError: true }
                                        && i.AutoRetries < MaxAutoRetries
                                        && !_running.ContainsKey(i.Id)
                                        && (now || i.NextAutoRetry is null || i.NextAutoRetry <= at)).ToList();
            foreach (var item in due)
            {
                item.AutoRetries++;
                item.Status = DownloadStatus.Queued;
                item.ErrorMessage = null;
                Raise(Changed, item);
            }
            if (due.Count > 0)
            {
                Persist();
                Pump();
            }
            return due.Count;
        }
    }

    public void Pause(IEnumerable<Guid> ids)
    {
        lock (_gate)
        {
            foreach (var item in Find(ids))
            {
                if (_running.TryGetValue(item.Id, out var run))
                {
                    run.ResumeRequested = false;
                    run.Cts.Cancel();
                }
                else if (item.Status == DownloadStatus.Queued)
                {
                    item.Status = DownloadStatus.Paused;
                    Raise(Changed, item);
                }
            }
            Persist();
        }
    }

    /// <summary>
    /// Thay link tải (link cũ hết hạn) cho một tệp chưa xong rồi tải tiếp. Phần đã tải được giữ lại
    /// nếu link mới trỏ tới tệp cùng dung lượng.
    /// </summary>
    public bool ReplaceLink(Guid id, DownloadRequest request)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null || item.Status == DownloadStatus.Completed || _running.ContainsKey(id) || _restarting.Contains(id))
                return false;
            item.Request = request;
            item.ErrorMessage = null;
            item.NetworkError = false;
            item.AutoRetries = 0;
            item.LinkRefreshed = true;
            item.Status = DownloadStatus.Queued;
            Persist();
            Raise(Changed, item);
            Pump();
            return true;
        }
    }

    /// <summary>
    /// Đổi tên tệp (cả tệp trên ổ, hoặc tệp .part nếu đang tải dở). Trả về câu báo lỗi, hoặc null nếu xong.
    /// Không đổi được khi tệp đang tải.
    /// </summary>
    public string? Rename(Guid id, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName is "." or ".." || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || newName.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 || newName.EndsWith('.'))
            return L.T("Tên tệp không hợp lệ. Tên không được chứa các ký tự \\ / : * ? \" < > |");
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null)
                return null;
            if (_running.ContainsKey(id))
                return L.T("Hãy tạm dừng tệp trước khi đổi tên.");
            var oldPath = item.TargetPath;
            var newPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? "", newName);
            if (newPath == oldPath)
                return null;
            var sameFile = string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase);
            if (!sameFile && (File.Exists(newPath) || Directory.Exists(newPath) || File.Exists(newPath + ".part")
                              || _items.Any(i => i.Id != id && string.Equals(i.TargetPath, newPath, StringComparison.OrdinalIgnoreCase))))
                return L.F("Đã có tệp tên “{0}” trong thư mục này.", newName);
            try
            {
                var source = item.Status == DownloadStatus.Completed ? oldPath : oldPath + ".part";
                var target = item.Status == DownloadStatus.Completed ? newPath : newPath + ".part";
                if (File.Exists(source))
                    File.Move(source, target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return L.T("Không đổi tên được: ") + e.Message;
            }
            var settings = _settings();
            item.TargetPath = newPath;
            item.Category = new CategoryResolver(settings.Extensions, settings.RootFolder).Resolve(newName);
            Persist();
            Raise(Changed, item);
            return null;
        }
    }

    /// <summary>Tệp đã tải được đổi sang tệp khác (vd. .ts → .mp4): cập nhật đường dẫn trong danh sách.</summary>
    public void Relocate(Guid id, string newPath)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null || item.Status != DownloadStatus.Completed)
                return;
            item.TargetPath = newPath;
            item.TotalBytes = new FileInfo(newPath) is { Exists: true } info ? info.Length : item.TotalBytes;
            item.DownloadedBytes = item.TotalBytes ?? item.DownloadedBytes;
            Persist();
            Raise(Changed, item);
        }
    }

    /// <summary>Ghi nhận (hoặc xoá, khi threat = null) cảnh báo mã độc cho một tệp.</summary>
    public void MarkThreat(Guid id, string? threat)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null || item.Threat == threat)
                return;
            item.Threat = threat;
            Persist();
            Raise(Changed, item);
        }
    }

    /// <summary>Dừng và đưa các tệp về hàng "Chờ tải sau" (dùng khi hết giờ tải theo lịch).</summary>
    public void Defer(IEnumerable<Guid> ids)
    {
        lock (_gate)
        {
            foreach (var item in Find(ids))
            {
                if (_running.TryGetValue(item.Id, out var run))
                {
                    run.ResumeRequested = false;
                    run.DeferRequested = true;
                    run.Cts.Cancel();
                }
                else if (item.Status is DownloadStatus.Queued or DownloadStatus.Paused)
                {
                    item.Status = DownloadStatus.Later;
                    Raise(Changed, item);
                }
            }
            Persist();
        }
    }

    public void Remove(IEnumerable<Guid> ids, bool deleteFiles)
    {
        var removed = new List<(DownloadItem Item, Run? Run)>();
        lock (_gate)
        {
            foreach (var item in Find(ids))
            {
                _items.Remove(item);
                _preferred.Remove(item.Id);
                item.Request = item.Request with { Cookies = null };
                _running.TryGetValue(item.Id, out var run);
                if (run is not null)
                {
                    run.Removed = true;
                    run.Cts.Cancel();
                }
                removed.Add((item, run));
                Raise(Removed, item.Id);
            }
            Persist();
        }

        foreach (var (item, run) in removed)
        {
            if (run is not null)
            {
                if (!deleteFiles)
                    continue;
                run.Task.Wait();
            }
            DeleteFile(StatePath(item));
            if (!deleteFiles)
                continue;
            DeleteFile(item.TargetPath + ".part");
            DeleteStreamParts(item);
            if (item.Status == DownloadStatus.Completed)
                DeleteFile(item.TargetPath);
            if (run?.FinalPath is string finalPath)
                DeleteFile(finalPath);
        }
    }

    public void Restart(Guid id)
    {
        Task? task = null;
        DownloadItem? item;
        lock (_gate)
        {
            item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null || !_restarting.Add(id))
                return;
            if (_running.TryGetValue(id, out var run))
            {
                run.ResumeRequested = false;
                run.Cts.Cancel();
                task = run.Task;
            }
        }

        try
        {
            task?.Wait();
            DeleteFile(StatePath(item));
            DeleteFile(item.TargetPath + ".part");
            DeleteStreamParts(item);
        }
        finally
        {
            lock (_gate)
                _restarting.Remove(id);
        }

        lock (_gate)
        {
            if (!_items.Contains(item) || _running.ContainsKey(id))
                return;
            item.Status = DownloadStatus.Queued;
            item.DownloadedBytes = 0;
            item.ErrorMessage = null;
            item.Threat = null;
            item.CompletedAt = null;
            Persist();
            Raise(Changed, item);
            Pump();
        }
    }

    public double SpeedBytesPerSec(Guid id)
    {
        lock (_gate)
            return _running.TryGetValue(id, out var run) ? Speed(run) : 0;
    }

    public TimeSpan? Eta(Guid id)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(id, out var run) || run.Item.TotalBytes is not long total)
                return null;
            var speed = Speed(run);
            if (speed <= 0)
                return null;
            return TimeSpan.FromSeconds(Math.Max(0, total - run.Item.DownloadedBytes) / speed);
        }
    }

    private double Speed(Run run)
    {
        var from = _time.GetUtcNow() - SpeedWindow;
        var window = run.Samples.Where(s => s.At >= from).ToList();
        if (window.Count < 2)
            return 0;
        var seconds = (window[^1].At - window[0].At).TotalSeconds;
        return seconds <= 0 ? 0 : (window[^1].Bytes - window[0].Bytes) / seconds;
    }

    private List<DownloadItem> Find(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        return _items.Where(i => set.Contains(i.Id)).ToList();
    }

    private string StatePath(DownloadItem item) => Path.Combine(_statesDir, $"{item.Id}.json");

    /// <summary>Video YouTube: yt-dlp để lại các tệp dở riêng cho hình và tiếng (vd. "Tên.f137.mp4.part").</summary>
    private static void DeleteStreamParts(DownloadItem item)
    {
        if (!Engine.YouTube.MediaLink.IsVideo(item.Request.Url))
            return;
        foreach (var path in Engine.YouTube.YouTubeDownloadEngine.PartialFiles(item.TargetPath))
            DeleteFile(path);
    }

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static long? ActualSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private void Persist()
    {
        _lastPersist = _time.GetUtcNow();
        try
        {
            _store.Save(_items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Raise<T>(Action<T>? handler, T arg)
    {
        if (handler is null)
            return;
        if (_context is not null)
            _context.Post(_ => handler(arg), null);
        else
            ThreadPool.QueueUserWorkItem(_ => handler(arg));
    }

    private void Pump()
    {
        var max = Math.Max(1, _settings().MaxConcurrent);
        while (_running.Count < max)
        {
            var next = _items.FirstOrDefault(i => i.Status == DownloadStatus.Queued && !_restarting.Contains(i.Id) && _preferred.Contains(i.Id))
                ?? _items.FirstOrDefault(i => i.Status == DownloadStatus.Queued && !_restarting.Contains(i.Id));
            if (next is null)
                return;
            _preferred.Remove(next.Id);
            next.Status = DownloadStatus.Downloading;
            next.ErrorMessage = null;
            var run = new Run(this, next);
            _running[next.Id] = run;
            Persist();
            Raise(Changed, next);
            run.Task = Task.Run(() => Execute(run));
        }
    }

    private async Task Execute(Run run)
    {
        EngineResult result;
        try
        {
            var probe = await _probe(run.Item.Request, run.Cts.Token);
            if (!probe.Ok)
            {
                result = new EngineResult(EngineOutcome.Failed, null, ProbeMessage(probe), probe.Failure == ProbeFailure.Network || RetryPolicy.IsTemporary(probe.StatusCode));
            }
            else
            {
                lock (_gate)
                {
                    if (Engine.Hls.MediaAwareEngine.IsStream(probe.FinalUrl ?? run.Item.Request.Url, probe.ContentType ?? run.Item.Request.Mime))
                    {
                        // Luồng video: probe chỉ thấy cỡ danh sách phát; dung lượng thật được ước lượng khi tải.
                        run.Item.CanResume = true;
                    }
                    else
                    {
                        run.Item.TotalBytes = probe.Size ?? run.Item.TotalBytes;
                        run.Item.CanResume = probe.SupportsRange && probe.Size is not null;
                    }
                }
                bool refreshed;
                lock (_gate)
                {
                    refreshed = run.Item.LinkRefreshed;
                    run.Item.LinkRefreshed = false;
                }
                var isStream = Engine.Hls.MediaAwareEngine.IsStream(probe.FinalUrl ?? run.Item.Request.Url, probe.ContentType ?? run.Item.Request.Mime);
                if (!isStream && SpaceShortage(run.Item, probe.Size) is { } shortage)
                {
                    result = new EngineResult(EngineOutcome.Failed, null, shortage);
                }
                else
                {
                    var job = new DownloadJob(run.Item.Request, run.Item.TargetPath, StatePath(run.Item), _settings().ConnectionsPerFile, probe, refreshed, run.Limiter);
                    result = await _engine.RunAsync(job, run, run.Cts.Token);
                }
            }
        }
        catch (OperationCanceledException) when (run.Cts.IsCancellationRequested)
        {
            result = new EngineResult(EngineOutcome.Paused, null, null);
        }
        catch (Exception ex)
        {
            result = new EngineResult(EngineOutcome.Failed, null, ex.Message, ex is HttpRequestException);
        }
        Finish(run, result);
    }

    /// <summary>Đo chỗ trống trên ổ (thay được khi kiểm thử).</summary>
    public Func<string, long?> FreeSpace { get; init; } = DiskSpace.FreeBytes;

    /// <summary>Báo thiếu chỗ nếu ổ không đủ cho phần còn phải tải (tệp .part đã được cấp đủ chỗ thì coi như đủ).</summary>
    private string? SpaceShortage(DownloadItem item, long? size)
    {
        if (size is not > 0)
            return null;
        long reserved = 0;
        try
        {
            var part = new FileInfo(item.TargetPath + ".part");
            if (part.Exists)
                reserved = part.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return DiskSpace.Shortage(item.TargetPath, size.Value - reserved, FreeSpace);
    }

    private static string ProbeMessage(ProbeResult probe) => probe.Failure switch
    {
        ProbeFailure.Expired => L.T("Link đã hết hạn. Bấm chuột phải › Làm mới link để tải tiếp"),
        ProbeFailure.NotFound => L.T("Không tìm thấy tệp trên máy chủ"),
        ProbeFailure.HtmlInsteadOfFile => L.T("Máy chủ trả về trang web thay vì tệp"),
        ProbeFailure.Network => L.T("Không kết nối được tới máy chủ"),
        _ when RetryPolicy.IsTemporary(probe.StatusCode) => RetryPolicy.BusyMessage(probe.StatusCode!.Value),
        _ => probe.StatusCode is int code ? L.F("Máy chủ báo lỗi {0}", code) : L.T("Máy chủ báo lỗi"),
    };

    private void Finish(Run run, EngineResult result)
    {
        var item = run.Item;
        lock (_gate)
        {
            _running.Remove(item.Id);
            run.Cts.Dispose();
            if (result.Outcome == EngineOutcome.Completed)
                run.FinalPath = result.FinalPath ?? item.TargetPath;
            if (run.Removed)
            {
                DeleteFile(StatePath(item));
                Pump();
                return;
            }
            switch (result.Outcome)
            {
                case EngineOutcome.Completed:
                    item.Status = DownloadStatus.Completed;
                    item.TargetPath = result.FinalPath ?? item.TargetPath;
                    // Máy chủ không báo dung lượng (vd. zip của GitHub): lấy kích thước thật của tệp đã tải.
                    item.TotalBytes ??= ActualSize(item.TargetPath) ?? (item.DownloadedBytes > 0 ? item.DownloadedBytes : null);
                    item.DownloadedBytes = item.TotalBytes ?? item.DownloadedBytes;
                    item.CompletedAt = _time.GetUtcNow();
                    item.ErrorMessage = null;
                    item.Request = item.Request with { Cookies = null };
                    break;
                case EngineOutcome.Paused:
                    item.Status = run.ResumeRequested ? DownloadStatus.Queued
                        : run.DeferRequested ? DownloadStatus.Later
                        : DownloadStatus.Paused;
                    break;
                case EngineOutcome.NeedsRestart:
                    item.Status = DownloadStatus.Paused;
                    item.ErrorMessage = NeedsRestartMessage;
                    break;
                default:
                    item.Status = DownloadStatus.Failed;
                    item.ErrorMessage = result.ErrorMessage;
                    break;
            }
            item.NetworkError = result.Outcome == EngineOutcome.Failed && result.Network;
            if (item.NetworkError)
            {
                // Lùi dần: 30 giây, 1, 2, 4… phút (tối đa 10 phút) giữa các lần tự tải tiếp.
                var wait = TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, Math.Min(item.AutoRetries, 5))));
                item.NextAutoRetry = _time.GetUtcNow() + wait;
            }
            else if (result.Outcome != EngineOutcome.Paused)
            {
                item.AutoRetries = 0;
                item.NextAutoRetry = null;
            }
            Persist();
            Raise(Changed, item);
            if (result.Outcome == EngineOutcome.Completed)
                Raise(Completed, item);
            Pump();
        }
    }

    private void OnProgress(Run run, EngineProgress p)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(run.Item.Id, out var current) || current != run)
                return;
            var now = _time.GetUtcNow();
            run.Item.DownloadedBytes = p.Downloaded;
            run.Item.TotalBytes = p.Total ?? run.Item.TotalBytes;
            run.Samples.Enqueue((now, p.Downloaded));
            while (run.Samples.Count > 1 && run.Samples.Peek().At < now - SpeedWindow)
                run.Samples.Dequeue();
            if (now - _lastPersist >= PersistInterval)
                Persist();
            Raise(Changed, run.Item);
        }
    }

    private sealed class Run(DownloadManager owner, DownloadItem item) : IProgress<EngineProgress>
    {
        public DownloadItem Item { get; } = item;
        public CancellationTokenSource Cts { get; } = new();
        public Queue<(DateTimeOffset At, long Bytes)> Samples { get; } = new();
        public BandwidthLimiter Limiter { get; } = new() { LimitBytesPerSec = item.SpeedLimitKBps * 1024L };
        public Task Task { get; set; } = Task.CompletedTask;
        public bool Removed { get; set; }
        public bool ResumeRequested { get; set; }
        public bool DeferRequested { get; set; }
        public string? FinalPath { get; set; }

        public void Report(EngineProgress value) => owner.OnProgress(this, value);
    }
}
