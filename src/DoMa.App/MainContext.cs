using System.Text.Json.Nodes;
using DoMa.App.Capture;
using DoMa.App.Ipc;
using DoMa.App.UI;
using DoMa.App.Scheduling;
using DoMa.App.Updates;
using DoMa.Core.Engine;
using DoMa.Core.Files;
using DoMa.Core.Messaging;
using DoMa.Core.Models;
using DoMa.Core.PostProcess;
using DoMa.Core.Queue;
using DoMa.Core.Storage;

namespace DoMa.App;

internal sealed class MainContext : ApplicationContext
{
    private const string AppTitle = "Download Manager (DoMa)";

    private readonly SettingsStore _settingsStore = new(AppPaths.DataDir);
    private readonly ErrorLog _log = new(AppPaths.DataDir);
    private readonly HttpClient _http = HttpClientFactory.Create();
    private readonly Dictionary<Guid, DownloadStatus> _lastStatus = [];
    private readonly DownloadManager _manager;
    private readonly CaptureCoordinator _coordinator;
    private readonly MainForm _form;
    private readonly TrayController _tray;
    private readonly UpdateService _updates;
    private readonly Capture.ClipboardWatcher _clipboard;
    private readonly BandwidthLimiter _limiter = new();
    private readonly RefreshWatch _refresh = new();
    private readonly System.Windows.Forms.Timer _networkTimer = new() { Interval = 30_000 };
    private readonly System.Windows.Forms.Timer _trayTimer = new() { Interval = 1_000 };
    private readonly ScheduleService _schedule;
    private volatile Settings _settings;
    private bool _exited;

    public MainContext(bool hidden)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Directory.CreateDirectory(AppPaths.StatesDir);
        _settings = _settingsStore.Load();
        _limiter.LimitBytesPerSec = _settings.SpeedLimitKBps * 1024L;
        Theme.Use(Enum.TryParse<ThemeMode>(_settings.Appearance, true, out var mode) ? mode : ThemeMode.System);

        var ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);

        var prober = new LinkProber(_http);
        _manager = new DownloadManager(
            new Core.Engine.Hls.MediaAwareEngine(
                new SegmentedDownloadEngine(_http) { Limiter = _limiter },
                new Core.Engine.Hls.HlsDownloadEngine(_http) { Limiter = _limiter }),
            prober.ProbeAsync,
            new DownloadStore(AppPaths.DataDir),
            () => _settings,
            AppPaths.StatesDir,
            ui);
        _coordinator = new CaptureCoordinator(
            prober.ProbeAsync,
            new NewDownloadDialogHost(ui, () => _settings.WarnBeforeDownload),
            _manager,
            () => new CategoryResolver(_settings.Extensions, _settings.RootFolder),
            Notify);
        _coordinator.TryRefresh = TryRefreshLink;

        foreach (var item in _manager.Items)
            _lastStatus[item.Id] = item.Status;
        _manager.Changed += OnChanged;
        _manager.Completed += OnCompleted;
        _manager.Removed += id => _lastStatus.Remove(id);

        _form = new MainForm(_manager, AddLinkAsync, OpenSettings, Exit, ExitOnShutdown, _log);
        _form.ThreatFound = item => HandleThreat(item.Id, item.TargetPath);
        _form.AddMany = AddManyAsync;
        _form.ConvertToMp4 = item => _ = ConvertVideoAsync(item.Id, item.TargetPath, notify: true);
        _form.WaitForNewLink = item =>
        {
            _refresh.Arm(item.Id);
            _tray?.Toast("Đang chờ link mới", $"Bấm tải “{Path.GetFileName(item.TargetPath)}” trên trang vừa mở, DoMa sẽ tải tiếp phần còn lại.", ToolTipIcon.None, null, 10);
        };
        _schedule = new ScheduleService(_manager, () => _settings, text => _tray?.Notify(text, ShowNow), ConfirmPower);
        _form.QuickActions = new QuickActions(
            () => _settings.SpeedLimitKBps,
            SetSpeedLimit,
            () => _schedule.StartLaterNow(),
            () => _schedule.AfterDone,
            _schedule.SetAfterDone,
            _schedule.Describe);
        _tray = new TrayController(ShowNow, Exit, UI.MainForm.OpenFolder, () => _updates?.CheckNow(),
            PauseAll, ResumeAll,
            () => (_manager.Items.Any(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued),
                   _manager.Items.Any(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed)));
        _updates = new UpdateService(_form, _tray, () => _settings, ExitForUpdate, _log);
        _clipboard = new Capture.ClipboardWatcher(
            () => _settings.WatchClipboard,
            () => _settings.Extensions.Values.SelectMany(v => v),
            url => _tray.Toast("Bắt được link vừa copy", $"{LinkName(url)} · Bấm để tải bằng DoMa", ToolTipIcon.None, () => _ = AddLinkAsync(url), 10));
        _ = _form.Handle;
        if (!hidden)
            _form.Show();
        ResumeAfterUpdate();
        _updates.Start();
        StartNetworkWatch();
        _trayTimer.Tick += (_, _) => UpdateTrayStatus();
        _trayTimer.Start();
    }

    public void OnMessage(PipeConnection connection, JsonNode msg)
    {
        switch (Messages.TypeOf(msg))
        {
            case MessageTypes.Show:
                ShowMain();
                break;
            case MessageTypes.Capture:
                _ = CaptureAsync(msg, connection.SendAsync, () => connection.IsConnected);
                break;
            case MessageTypes.CaptureMany:
                CaptureMany(msg, connection.SendAsync);
                break;
        }
    }

    /// <summary>Link vừa bắt từ trình duyệt là link mới của một tệp đang chờ làm mới link: nhận nó và tải tiếp.</summary>
    private bool TryRefreshLink(DownloadRequest req, ProbeResult probe, string fileName)
    {
        if (_refresh.Match(_manager.Items, fileName, probe.Size) is not { } item)
            return false;
        if (!_manager.ReplaceLink(item.Id, req))
            return false;
        _refresh.Disarm(item.Id);
        _tray.Toast("Đã làm mới link", $"{Path.GetFileName(item.TargetPath)} · đang tải tiếp phần còn lại", ToolTipIcon.Info, ShowNow);
        return true;
    }

    public void ShowMain() => PostToForm(ShowNow);

    private void PostToForm(Action action)
    {
        try
        {
            if (!_form.IsDisposed)
                _form.BeginInvoke(action);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private void ShowNow()
    {
        if (_form.IsDisposed)
            return;
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized)
            _form.WindowState = FormWindowState.Normal;
        _form.TopMost = true;
        _form.TopMost = false;
        _form.Activate();
    }

    private async Task CaptureAsync(JsonNode msg, Func<JsonNode, Task<bool>> reply, Func<bool> isConnected)
    {
        try
        {
            await _coordinator.HandleAsync(msg, reply, isConnected);
        }
        catch (Exception e)
        {
            _log.Write("capture", e, null);
        }
    }

    private void Notify(string message) =>
        PostToForm(() => MessageBox.Show(_form, message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information));

    /// <summary>Tiện ích gửi tất cả link của một trang: cho người dùng chọn rồi thêm vào hàng tải.</summary>
    private void CaptureMany(JsonNode msg, Func<JsonNode, Task<bool>> reply)
    {
        BatchCaptureMessage batch;
        try
        {
            batch = BatchCaptureMessage.From(msg);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or System.Text.Json.JsonException)
        {
            return;
        }
        var source = Uri.TryCreate(batch.Referrer, UriKind.Absolute, out var page) ? page.Host : "";
        PostToForm(() =>
        {
            var added = ChooseAndAdd(batch.Links, source, batch.Referrer, batch.CookiesByHost, batch.UserAgent);
            _ = reply(Messages.Reply(added ? MessageTypes.Accepted : MessageTypes.Cancelled, batch.Id));
        });
    }

    /// <summary>Mở hộp "Tải nhiều link"; trả về true nếu người dùng đã thêm ít nhất một link.</summary>
    private bool ChooseAndAdd(IEnumerable<(string Url, string? Text)> raw, string source, string? referrer,
        IReadOnlyDictionary<string, string>? cookiesByHost, string? userAgent)
    {
        var resolver = new CategoryResolver(_settings.Extensions, _settings.RootFolder);
        var links = BatchLinks.Prepare(raw, resolver);
        if (links.Count == 0)
        {
            _tray.Toast("Không có link nào để tải", source.Length > 0 ? $"Trang {source} không có link tải được." : "Không tìm thấy link http/https.", ToolTipIcon.Warning, null);
            return false;
        }
        using var dialog = new UI.BatchForm(links, source);
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return false;
        var referer = Uri.TryCreate(referrer, UriKind.Absolute, out var r) && r.Scheme is "http" or "https" ? referrer : null;
        foreach (var link in dialog.Selected)
        {
            var cookies = cookiesByHost is not null && cookiesByHost.TryGetValue(link.Host, out var c) ? c : null;
            var request = new DownloadRequest(link.Url, referer, link.FileName, null, null, cookies, userAgent);
            var folder = resolver.FolderFor(resolver.Resolve(link.FileName));
            try
            {
                _manager.Add(request, Path.Combine(folder, link.FileName), dialog.StartNow);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _log.Write("batch", e, null);
            }
        }
        ShowNow();
        return true;
    }

    /// <summary>Dán/kéo-thả nhiều link vào cửa sổ chính.</summary>
    private Task AddManyAsync(IReadOnlyList<string> urls)
    {
        if (urls.Count == 1)
            return AddLinkAsync(urls[0]);
        ChooseAndAdd(urls.Select(u => (u, (string?)null)), "", null, null, null);
        return Task.CompletedTask;
    }

    private static string LinkName(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath)) is { Length: > 0 } name
            ? name
            : url;

    private async Task AddLinkAsync(string url)
    {
        var msg = new JsonObject { ["type"] = MessageTypes.Capture, ["id"] = "manual", ["url"] = url };
        await CaptureAsync(msg, reply =>
        {
            if (Messages.TypeOf(reply) == MessageTypes.GiveBack)
                PostToForm(() => MessageBox.Show(_form, "Không tải được link này.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning));
            return Task.FromResult(true);
        }, () => true);
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(_settings);
        if (dialog.ShowDialog(_form) != System.Windows.Forms.DialogResult.OK)
            return;
        var updated = dialog.Result();
        try
        {
            _settingsStore.Save(updated);
            StartupRegistration.Apply(updated.RunAtStartup);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _log.Write("settings", e, null);
            MessageBox.Show(_form, "Không lưu được cài đặt.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        var appearanceChanged = !string.Equals(updated.Appearance, _settings.Appearance, StringComparison.OrdinalIgnoreCase);
        ApplySettings(updated);
        if (appearanceChanged)
            MessageBox.Show(_form, "Chế độ màu mới sẽ được áp dụng khi bạn mở lại DoMa (Thoát ở khay hệ thống rồi mở lại).", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ApplySettings(Settings updated)
    {
        _settings = updated;
        _limiter.LimitBytesPerSec = updated.SpeedLimitKBps * 1024L;
        _manager.Reschedule();
    }

    private void SetSpeedLimit(int kbps)
    {
        var current = _settings;
        var updated = current.Clone();
        updated.SpeedLimitKBps = Math.Max(0, kbps);
        try
        {
            _settingsStore.Save(updated);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Write("settings", e, null);
        }
        ApplySettings(updated);
    }

    private bool ConfirmPower(AfterDone action)
    {
        using var dialog = new PowerCountdownForm(action);
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK;
    }

    private void OnChanged(DownloadItem item)
    {
        _lastStatus.TryGetValue(item.Id, out var previous);
        _lastStatus[item.Id] = item.Status;
        if (item.Status != DownloadStatus.Failed || previous == DownloadStatus.Failed)
            return;
        if (item.NetworkError && _settings.AutoResumeOnNetwork)
        {
            // Chỉ báo lần đầu; các lần tự thử lại sau đó không báo nữa cho đỡ phiền.
            if (item.AutoRetries == 0)
                _tray.Toast("Mất kết nối", $"{Path.GetFileName(item.TargetPath)} · DoMa sẽ tự tải tiếp khi có mạng lại", ToolTipIcon.Warning, ShowNow);
            return;
        }
        _tray.Toast("Tải lỗi", Path.GetFileName(item.TargetPath), ToolTipIcon.Error, ShowNow);
    }

    private void PauseAll() =>
        _manager.Pause(_manager.Items.Where(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued).Select(i => i.Id).ToList());

    private void ResumeAll() =>
        _manager.Resume(_manager.Items.Where(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed).Select(i => i.Id).ToList());

    /// <summary>Cập nhật dòng chữ của biểu tượng ở khay theo các tệp đang tải.</summary>
    private void UpdateTrayStatus()
    {
        var active = _manager.Items.Where(i => i.Status == DownloadStatus.Downloading).ToList();
        long? total = active.Count > 0 && active.All(i => i.TotalBytes is > 0) ? active.Sum(i => i.TotalBytes!.Value) : null;
        var downloaded = active.Sum(i => i.DownloadedBytes);
        var speed = active.Sum(i => _manager.SpeedBytesPerSec(i.Id));
        _tray.SetStatus(TrayController.Summary(active.Count, downloaded, total, speed));
    }

    /// <summary>Có mạng lại thì cho chạy tiếp ngay các tệp lỗi vì mất mạng; ngoài ra cứ 30 giây xem có tệp nào tới lượt thử lại.</summary>
    private void StartNetworkWatch()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailability;
        _networkTimer.Tick += (_, _) =>
        {
            if (_settings.AutoResumeOnNetwork && System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
                _manager.ResumeNetworkFailures();
        };
        _networkTimer.Start();
    }

    private void OnNetworkAvailability(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable && _settings.AutoResumeOnNetwork && !_exited)
            _manager.ResumeNetworkFailures(now: true);
    }

    private void OnCompleted(DownloadItem item)
    {
        var settings = _settings;
        var path = item.TargetPath;
        var url = item.Request.Url;
        var id = item.Id;
        _ = Task.Run(() => PostProcessAsync(id, path, url, settings));
        if (_settings.NotifyOnComplete)
            _tray.Toast("Đã tải xong", Path.GetFileName(item.TargetPath), ToolTipIcon.Info, () => UI.MainForm.OpenFolder(path));
    }

    private static string ResumeFile => Path.Combine(AppPaths.DataDir, "resume-after-update.json");

    /// <summary>Thoát để bộ cài cập nhật chạy; ghi lại các tệp đang tải để tự tải tiếp khi mở lại.</summary>
    public void ExitForUpdate()
    {
        try
        {
            var active = _manager.Items.Where(i => i.Status == DownloadStatus.Downloading).Select(i => i.Id).ToList();
            File.WriteAllText(ResumeFile, System.Text.Json.JsonSerializer.Serialize(active));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Write("update", e, null);
        }
        Exit();
    }

    private void ResumeAfterUpdate()
    {
        try
        {
            if (!File.Exists(ResumeFile))
                return;
            var ids = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(File.ReadAllText(ResumeFile)) ?? [];
            File.Delete(ResumeFile);
            var known = _manager.Items.Select(i => i.Id).ToHashSet();
            var resume = ids.Where(known.Contains).ToList();
            if (resume.Count > 0)
                _manager.Resume(resume);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _log.Write("update", e, null);
        }
    }

    /// <summary>Sau khi tải xong: đánh dấu tệp từ Internet, quét Defender, tự giải nén zip (tuỳ cài đặt).</summary>
    /// <summary>
    /// Defender báo có mã độc: đánh dấu đỏ trong danh sách (chặn mở) và hỏi người dùng có xoá tệp không.
    /// Thường Defender đã tự cách ly tệp; khi đó chỉ cần báo cho người dùng biết.
    /// </summary>
    private void HandleThreat(Guid id, string path)
    {
        const string threat = "Windows Defender phát hiện";
        try
        {
            _manager.MarkThreat(id, threat);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Write("threat", e, null);
        }
        var name = Path.GetFileName(path);
        _tray.Notify($"Phát hiện mã độc trong {name}. Đừng mở tệp này.", ShowNow);
        if (!File.Exists(path))
        {
            MessageBox.Show(_form.Visible ? _form : null,
                $"Windows Defender phát hiện mã độc trong “{name}” và đã cách ly tệp này. Máy của bạn an toàn.\n\nTệp vẫn được đánh dấu đỏ trong danh sách để bạn biết nguồn tải không đáng tin.",
                "Cảnh báo bảo mật", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var answer = MessageBox.Show(_form.Visible ? _form : null,
            $"Windows Defender phát hiện mã độc trong “{name}”.\n\nBạn có muốn xoá tệp này ngay không? (Khuyến nghị: Có)\nNếu giữ lại, tệp được đánh dấu đỏ và DoMa sẽ hỏi lại mỗi khi bạn mở.",
            "Cảnh báo bảo mật", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
        if (answer != System.Windows.Forms.DialogResult.Yes)
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Write("threat", e, null);
            MessageBox.Show(_form.Visible ? _form : null, "Không xoá được tệp. Có thể Windows Defender đang xử lý nó.", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Chuyển video .ts sang .mp4 (đóng gói lại, không nén), xoá tệp .ts và cập nhật danh sách.
    /// Trả về đường dẫn .mp4, hoặc null nếu không chuyển được (giữ nguyên .ts).
    /// </summary>
    private async Task<string?> ConvertVideoAsync(Guid id, string path, bool notify)
    {
        var output = FileNameResolver.MakeUnique(Path.ChangeExtension(path, ".mp4"), p => File.Exists(p) || Directory.Exists(p));
        try
        {
            await Task.Run(() => TsToMp4.Convert(path, output));
            File.Delete(path);
            _manager.Relocate(id, output);
            if (notify)
                PostToForm(() => _tray.Notify($"Đã chuyển sang MP4: {Path.GetFileName(output)}", output, ToolTipIcon.Info));
            return output;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
        {
            _log.Write("mp4", e, null);
            var reason = e is NotSupportedException ? e.Message : "Không chuyển được sang MP4, tệp .ts vẫn được giữ nguyên.";
            PostToForm(() => _tray.Notify(reason, path, ToolTipIcon.Warning));
            return null;
        }
    }

    private async Task PostProcessAsync(Guid id, string path, string url, Settings settings)
    {
        try
        {
            if (!File.Exists(path))
                return;
            if (settings.ConvertTsToMp4 && TsToMp4.CanConvert(path) && await ConvertVideoAsync(id, path, notify: false) is { } mp4)
                path = mp4;
            SecurityTools.MarkFromInternet(path, url);
            if (settings.ScanAfterDownload)
            {
                var result = await SecurityTools.ScanAsync(path, CancellationToken.None);
                if (result == ScanResult.ThreatFound)
                {
                    PostToForm(() => HandleThreat(id, path));
                    return;
                }
            }
            if (settings.AutoExtractZip && ArchiveExtractor.CanExtract(path))
            {
                var folder = ArchiveExtractor.Extract(path);
                PostToForm(() => _tray.Notify($"Đã giải nén {Path.GetFileName(path)}", folder, ToolTipIcon.Info));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            _log.Write("postprocess", e, null);
            PostToForm(() => _tray.Notify($"Không xử lý được {Path.GetFileName(path)} sau khi tải: {e.Message}", path, ToolTipIcon.Warning));
        }
    }

    public void Exit()
    {
        if (!BeginExit(TimeSpan.FromSeconds(3)))
            return;
        _form.Close();
        ExitThread();
    }

    private void ExitOnShutdown()
    {
        if (BeginExit(TimeSpan.FromSeconds(1.5)))
            ExitThread();
    }

    private bool BeginExit(TimeSpan wait)
    {
        if (_exited)
            return false;
        _exited = true;
        _form.ExitForReal();
        var active = _manager.Items.Where(i => i.Status == DownloadStatus.Downloading).Select(i => i.Id).ToList();
        if (active.Count > 0)
        {
            _manager.Pause(active);
            var deadline = DateTime.UtcNow + wait;
            while (DateTime.UtcNow < deadline && _manager.Items.Any(i => i.Status == DownloadStatus.Downloading))
                Thread.Sleep(50);
        }
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailability;
        _networkTimer.Dispose();
        _trayTimer.Dispose();
        _updates.Dispose();
        _clipboard.Dispose();
        _schedule.Dispose();
        _tray.Dispose();
        return true;
    }
}
