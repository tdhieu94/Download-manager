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
    private readonly Core.Engine.YouTube.YouTubeTools _ytTools;
    private readonly Core.Engine.YouTube.YouTubeDownloadEngine _ytEngine;
    private readonly SynchronizationContext _ui;
    private Task<bool>? _ytInstall;
    private volatile int _ytInstallPercent = -1;
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
        L.Use(_settings.Language);
        _limiter.LimitBytesPerSec = _settings.SpeedLimitKBps * 1024L;
        Theme.Use(Enum.TryParse<ThemeMode>(_settings.Appearance, true, out var mode) ? mode : ThemeMode.System);

        var ui = _ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        _ytTools = new Core.Engine.YouTube.YouTubeTools(Path.Combine(AppPaths.DataDir, "tools"), _http);
        _ytEngine = new Core.Engine.YouTube.YouTubeDownloadEngine(_ytTools);

        var prober = new LinkProber(_http);
        _manager = new DownloadManager(
            new Core.Engine.Hls.MediaAwareEngine(
                new SegmentedDownloadEngine(_http) { Limiter = _limiter },
                new Core.Engine.Hls.HlsDownloadEngine(_http) { Limiter = _limiter },
                _ytEngine,
                new Core.Engine.Torrent.TorrentDownloadEngine(_ytTools) { SpeedLimit = () => _limiter.LimitBytesPerSec }),
            (req, ct) => Core.Engine.Torrent.TorrentLink.IsTorrent(req.Url, req.Mime) ? Task.FromResult(TorrentReady(req))
                : Core.Engine.YouTube.MediaLink.IsVideo(req.Url) ? Task.FromResult(MediaReady(req)) : prober.ProbeAsync(req, ct),
            new DownloadStore(AppPaths.DataDir),
            () => _settings,
            AppPaths.StatesDir,
            ui);
        _coordinator = new CaptureCoordinator(
            (req, ct) => Core.Engine.Torrent.TorrentLink.IsTorrent(req.Url, req.Mime) ? ProbeTorrentAsync(req)
                : Core.Engine.YouTube.MediaLink.IsVideo(req.Url) ? ProbeMediaAsync(req, ct) : prober.ProbeAsync(req, ct),
            new NewDownloadDialogHost(ui, () => _settings.WarnBeforeDownload, () => LastMedia, RememberMedia),
            _manager,
            () => new CategoryResolver(_settings.Extensions, _settings.RootFolder),
            Notify);
        _coordinator.TryRefresh = TryRefreshLink;
        _coordinator.HandlePlaylist = AddPlaylistAsync;

        foreach (var item in _manager.Items)
            _lastStatus[item.Id] = item.Status;
        _manager.Changed += OnChanged;
        _manager.Completed += OnCompleted;
        _manager.Removed += id => _lastStatus.Remove(id);

        _form = new MainForm(_manager, AddLinkAsync, OpenSettings, Exit, ExitOnShutdown, _log);
        _form.ThreatFound = item => HandleThreat(item.Id, item.TargetPath);
        _form.AddMany = AddManyAsync;
        _form.ConvertMedia = (item, extension) => _ = ConvertMediaAsync(item, extension);
        _form.WaitForNewLink = item =>
        {
            _refresh.Arm(item.Id);
            _tray?.Toast(L.T("Đang chờ link mới"), L.F("Bấm tải “{0}” trên trang vừa mở, DoMa sẽ tải tiếp phần còn lại.", Path.GetFileName(item.TargetPath)), ToolTipIcon.None, null, 10);
        };
        _schedule = new ScheduleService(_manager, () => _settings, text => _tray?.Notify(text, ShowNow), ConfirmPower);
        _form.QuickActions = new QuickActions(
            () => _settings.SpeedLimitKBps,
            SetSpeedLimit,
            () => _schedule.StartLaterNow(),
            () => _schedule.AfterDone,
            _schedule.SetAfterDone,
            _schedule.Describe,
            ManageChannels);
        _tray = new TrayController(ShowNow, Exit, UI.MainForm.OpenFolder, () => _updates?.CheckNow(),
            PauseAll, ResumeAll,
            () => (_manager.Items.Any(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued),
                   _manager.Items.Any(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed)));
        _updates = new UpdateService(_form, _tray, () => _settings, ExitForUpdate, _log);
        _clipboard = new Capture.ClipboardWatcher(
            () => _settings.WatchClipboard,
            () => _settings.Extensions.Values.SelectMany(v => v),
            url => _tray.Toast(L.T("Bắt được link vừa copy"), L.F("{0} · Bấm để tải bằng DoMa", LinkName(url)), ToolTipIcon.None, () => _ = AddLinkAsync(url), 10));
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

    // ───────────── Video từ trang: YouTube, Facebook, TikTok… (qua yt-dlp) ─────────────

    private static ProbeResult MediaReady(DownloadRequest req) =>
        new(true, Core.Engine.YouTube.MediaLink.Normalize(req.Url), null, true, null, null, null, "video/mp4", ProbeFailure.None, 200);

    private static ProbeResult MediaFailed() => new(false, null, null, false, null, null, null, null, ProbeFailure.Other, null);

    private MediaOptions LastMedia => new(_settings.MediaQuality, _settings.MediaSubtitles);

    // ───────────── Torrent (qua aria2c) ─────────────

    private static ProbeResult TorrentReady(DownloadRequest req) =>
        new(true, req.Url, null, true, null, null, Core.Engine.Torrent.TorrentLink.DisplayName(req.Url), "application/x-bittorrent", ProbeFailure.None, 200);

    private Task<bool>? _aria2Install;

    /// <summary>Link torrent: lần đầu thì hỏi rồi cài aria2c (khoảng 3 MB).</summary>
    private async Task<ProbeResult> ProbeTorrentAsync(DownloadRequest req)
    {
        if (!_ytTools.HasAria2)
        {
            Task<bool> install;
            lock (_refresh)
                install = _aria2Install is { IsCompleted: false } running ? running : _aria2Install = InstallAria2Async();
            if (!await install)
                return MediaFailed();
        }
        return TorrentReady(req);
    }

    private async Task<bool> InstallAria2Async()
    {
        var asked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            var text = L.T("Để tải torrent, DoMa cần thêm công cụ miễn phí, mã nguồn mở aria2 ")
                + L.F("(khoảng {0} MB).\n\nDoMa sẽ tải nó từ GitHub một lần duy nhất. Tải ngay?\n\n", Core.Engine.YouTube.YouTubeTools.Aria2SizeMb)
                + L.T("Lưu ý: DoMa không chia sẻ lại (seed) sau khi tải xong. Chỉ tải nội dung bạn có quyền tải.");
            var caption = L.T("Tải torrent");
            var answer = _form.Visible
                ? MessageBox.Show(_form, text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                : MessageBox.Show(text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
            asked.TrySetResult(answer == System.Windows.Forms.DialogResult.Yes);
        }, null);
        if (!await asked.Task)
            return false;
        try
        {
            await _ytTools.InstallAria2Async(null, CancellationToken.None);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            _log.Write("aria2", e, null);
            _tray.Toast(L.T("Không tải được công cụ torrent"), L.F("Kiểm tra kết nối mạng rồi thử lại. ({0})", e.Message), ToolTipIcon.Error, null, 10);
            return false;
        }
    }

    /// <summary>Nhớ chất lượng vừa chọn để lần sau chọn sẵn.</summary>
    private void RememberMedia(MediaOptions options)
    {
        if (options == LastMedia)
            return;
        var updated = _settings.Clone();
        updated.MediaQuality = options.Quality;
        updated.MediaSubtitles = options.Subtitles;
        try
        {
            _settingsStore.Save(updated);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Write("settings", e, null);
        }
        _settings = updated;
    }

    private static string SiteOf(string url) => Core.Engine.YouTube.MediaLink.SiteName(url) ?? "video";

    /// <summary>Có công cụ yt-dlp chưa (hỏi rồi cài nếu thiếu), và cập nhật yt-dlp nếu đã lâu.</summary>
    private async Task<bool> MediaToolsReadyAsync(CancellationToken ct)
    {
        if (!_ytTools.Ready && !await EnsureYouTubeToolsAsync())
            return false;
        await _ytTools.RefreshAsync(ct);
        return true;
    }

    /// <summary>Link video vừa bắt: cài công cụ nếu cần (hỏi trước), rồi lấy tiêu đề, dung lượng, độ phân giải để hiện hộp tải.</summary>
    private async Task<ProbeResult> ProbeMediaAsync(DownloadRequest req, CancellationToken ct)
    {
        if (!await MediaToolsReadyAsync(ct))
            return MediaFailed();
        var (info, error) = await _ytEngine.InfoAsync(req, ct);
        if (info is null)
        {
            _tray.Toast(L.F("Không tải được video {0}", SiteOf(req.Url)), error ?? "", ToolTipIcon.Warning, null, 10);
            return MediaFailed();
        }
        var options = LastMedia;
        return MediaReady(req) with { Size = options.AudioOnly ? info.AudioSize : info.Size, FileName = info.Title + options.Extension, Media = info };
    }

    /// <summary>Link playlist hoặc kênh YouTube: đọc danh sách video, cho chọn video và chất lượng rồi thêm từng video.</summary>
    private async Task<bool> AddPlaylistAsync(DownloadRequest req)
    {
        if (!await MediaToolsReadyAsync(CancellationToken.None))
            return false;
        _tray.Toast(L.T("Đang đọc playlist"), L.T("DoMa đang lấy danh sách video…"), ToolTipIcon.None, null, 5);
        var (playlist, error) = await _ytEngine.PlaylistAsync(req, CancellationToken.None);
        if (playlist is null)
        {
            _tray.Toast(L.T("Không đọc được playlist"), error ?? "", ToolTipIcon.Warning, null, 10);
            return false;
        }
        var url = Core.Engine.YouTube.MediaLink.Normalize(req.Url);
        var isChannel = Core.Channels.ChannelWatch.CheckLimit(url) is not null;
        var following = _channels.Any(c => c.Url == url);
        var chosen = new TaskCompletionSource<(IReadOnlyList<BatchLink> Links, MediaOptions Media, bool StartNow, bool Follow)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PostToForm(() =>
        {
            try
            {
                var links = playlist.Entries
                    .Select(e => new BatchLink(e.Url, FileNameResolver.Sanitize(e.Title), LinkKind.Media, "youtube.com"))
                    .ToList();
                var more = playlist.Entries.Count >= Core.Engine.YouTube.YtDlp.MaxPlaylistItems ? L.F(" (DoMa lấy tối đa {0} video)", Core.Engine.YouTube.YtDlp.MaxPlaylistItems) : "";
                var follow = following ? null
                    : isChannel ? L.T("Theo dõi kênh này: tự tải video mới khi kênh đăng")
                    : L.T("Theo dõi playlist này: tự tải video mới được thêm vào");
                using var dialog = new UI.BatchForm(links, "", LastMedia, playlist.Title,
                    L.F("{0} video{1}. Tích chọn các video muốn tải{2}.", links.Count, more, (following ? L.T(" (đang theo dõi)") : "")), follow, checkAll: !isChannel);
                chosen.TrySetResult(dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                    ? (dialog.Selected, dialog.Media ?? LastMedia, dialog.StartNow, dialog.Follow)
                    : null);
            }
            catch (Exception e)
            {
                chosen.TrySetException(e);
            }
        });
        if (await chosen.Task is not { } pick)
            return false;
        RememberMedia(pick.Media);
        if (pick.Follow)
            Follow(url, playlist, pick.Media);
        var added = AddMedia(pick.Links.Select(l => (l.Url, (string?)l.FileName)), pick.Media, pick.StartNow, req.Cookies, req.UserAgent);
        if (added > 0)
            _tray.Toast(L.T("Đã thêm playlist"), $"{added} video · {playlist.Title}", ToolTipIcon.Info, ShowNow);
        else if (pick.Follow)
            _tray.Toast(L.T("Đang theo dõi"), L.F("{0} · DoMa sẽ tự tải video mới", playlist.Title), ToolTipIcon.Info, null);
        return added > 0 || pick.Follow;
    }

    // ───────────── Theo dõi kênh/playlist YouTube ─────────────

    private readonly Core.Channels.ChannelStore _channelStore = new(AppPaths.DataDir);
    private List<Core.Channels.FollowedChannel> _channels = [];
    private readonly System.Windows.Forms.Timer _channelTimer = new() { Interval = 30 * 60_000 };
    private bool _checkingChannels;

    /// <summary>Bắt đầu theo dõi: các video đang có coi như đã thấy, chỉ tải video đăng sau này.</summary>
    private void Follow(string url, Core.Engine.YouTube.PlaylistInfo playlist, MediaOptions options)
    {
        lock (_channelStore)
        {
            if (_channels.Any(c => c.Url == url))
                return;
            var channel = new Core.Channels.FollowedChannel
            {
                Url = url,
                Title = playlist.Title,
                Quality = options.Quality,
                Subtitles = options.Subtitles,
                LastCheck = DateTimeOffset.UtcNow,
            };
            Core.Channels.ChannelWatch.MarkSeen(channel, playlist.Entries.Select(e => e.Url));
            _channels.Add(channel);
            SaveChannels();
        }
    }

    private void SaveChannels()
    {
        try
        {
            lock (_channelStore)
                _channelStore.Save(_channels);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Write("channels", e, null);
        }
    }

    private void StartChannelWatch()
    {
        _channels = _channelStore.Load();
        _channelTimer.Tick += (_, _) => _ = CheckChannelsAsync(force: false);
        _channelTimer.Start();
        // Kiểm tra lần đầu sau khi mở DoMa 2 phút, để không làm chậm lúc khởi động.
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => PostToForm(() => _ = CheckChannelsAsync(force: false)), TaskScheduler.Default);
    }

    /// <summary>Xem các kênh đến hạn có video mới không; có thì thêm vào danh sách và tải ngay.</summary>
    private async Task<int> CheckChannelsAsync(bool force)
    {
        if (_checkingChannels || !_ytTools.Ready)
            return 0;
        _checkingChannels = true;
        var total = 0;
        try
        {
            List<Core.Channels.FollowedChannel> due;
            lock (_channelStore)
                due = _channels.Where(c => force || Core.Channels.ChannelWatch.Due(c, DateTimeOffset.UtcNow)).ToList();
            foreach (var channel in due)
            {
                var req = new DownloadRequest(channel.Url, null, null, null, null, null, null);
                var (playlist, error) = await _ytEngine.PlaylistAsync(req, CancellationToken.None, Core.Channels.ChannelWatch.CheckLimit(channel.Url));
                if (playlist is null)
                {
                    _log.Write("channels", new IOException(error ?? "?"), channel.Url);
                    continue;
                }
                List<Core.Engine.YouTube.PlaylistEntry> fresh;
                lock (_channelStore)
                {
                    fresh = Core.Channels.ChannelWatch.NewEntries(channel, playlist.Entries);
                    Core.Channels.ChannelWatch.MarkSeen(channel, fresh.Select(e => e.Url));
                    channel.LastCheck = DateTimeOffset.UtcNow;
                }
                SaveChannels();
                if (fresh.Count == 0)
                    continue;
                var added = AddMedia(fresh.Select(e => (e.Url, (string?)e.Title)), channel.Options, true, null, null);
                total += added;
                if (added > 0)
                    _tray.Toast(L.F("Video mới từ {0}", channel.Title), added == 1 ? fresh[0].Title : L.F("{0} video mới · đang tải", added), ToolTipIcon.Info, ShowNow);
            }
        }
        catch (Exception e)
        {
            _log.Write("channels", e, null);
        }
        finally
        {
            _checkingChannels = false;
        }
        return total;
    }

    private void ManageChannels()
    {
        List<Core.Channels.FollowedChannel> snapshot;
        lock (_channelStore)
            snapshot = [.. _channels];
        using var dialog = new UI.ChannelsForm(snapshot);
        dialog.CheckNow = async () =>
        {
            // Kênh vừa bỏ theo dõi trong hộp này thì không kiểm tra nữa.
            lock (_channelStore)
                _channels.RemoveAll(c => dialog.Removed.Contains(c.Url));
            return await CheckChannelsAsync(force: true);
        };
        dialog.ShowDialog(_form);
        lock (_channelStore)
            _channels.RemoveAll(c => dialog.Removed.Contains(c.Url));
        SaveChannels();
    }

    /// <summary>Thêm các video từ trang vào danh sách, lưu vào thư mục Video với tên theo tiêu đề.</summary>
    private int AddMedia(IEnumerable<(string Url, string? Title)> videos, MediaOptions options, bool startNow, string? cookies, string? userAgent)
    {
        var resolver = new CategoryResolver(_settings.Extensions, _settings.RootFolder);
        var folder = resolver.FolderFor(Category.Media);
        var added = 0;
        foreach (var (url, title) in videos)
        {
            var name = FileNameResolver.Sanitize(title ?? "");
            if (name.Length == 0)
                name = "Video " + SiteOf(url);
            var taken = _manager.Items.Select(i => i.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var path = FileNameResolver.MakeUnique(Path.Combine(folder, name + options.Extension), p => File.Exists(p) || taken.Contains(p));
            try
            {
                _manager.Add(new DownloadRequest(Core.Engine.YouTube.MediaLink.Normalize(url), null, null, null, null, cookies, userAgent, options), path, startNow);
                added++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _log.Write("media", e, null);
            }
        }
        return added;
    }

    /// <summary>Trang video dán/chọn trong hộp "Tải nhiều link": lấy tiêu đề từng video rồi mới thêm (chạy nền).</summary>
    private async Task AddMediaPagesAsync(IReadOnlyList<BatchLink> links, MediaOptions options, bool startNow,
        IReadOnlyDictionary<string, string>? cookiesByHost, string? userAgent)
    {
        if (!await MediaToolsReadyAsync(CancellationToken.None))
            return;
        if (links.Count > 1)
            _tray.Toast(L.T("Đang lấy thông tin video"), L.F("{0} video · DoMa sẽ thêm vào danh sách khi xong", links.Count), ToolTipIcon.None, null, 5);
        var added = 0;
        foreach (var link in links)
        {
            var cookies = cookiesByHost is not null && cookiesByHost.TryGetValue(link.Host, out var c) ? c : null;
            var req = new DownloadRequest(link.Url, null, null, null, null, cookies, userAgent);
            var (info, _) = await _ytEngine.InfoAsync(req, CancellationToken.None);
            var title = info?.Title ?? link.FileName;
            PostToForm(() => added += AddMedia([(link.Url, title)], options, startNow, cookies, userAgent));
        }
        PostToForm(() =>
        {
            if (added > 0 && links.Count > 1)
                _tray.Toast(L.T("Đã thêm video"), L.F("{0} video vào danh sách", added), ToolTipIcon.Info, ShowNow);
        });
    }

    /// <summary>Lần đầu tải YouTube: hỏi rồi tải yt-dlp và ffmpeg (chỉ một lần, các lượt bắt cùng lúc dùng chung).</summary>
    private Task<bool> EnsureYouTubeToolsAsync()
    {
        lock (_refresh)
        {
            if (_ytInstall is { IsCompleted: false } running)
                return running;
            return _ytInstall = InstallYouTubeToolsAsync();
        }
    }

    private async Task<bool> InstallYouTubeToolsAsync()
    {
        var asked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var missing = _ytTools.Missing;
        var lines = string.Join("\n", missing.Select(m => L.F("• {0} (khoảng {1} MB): ", m.Name, m.SizeMb) + m.Name switch
        {
            "yt-dlp" => L.T("đọc trang video YouTube, Facebook, TikTok…"),
            "deno" => L.T("chạy JavaScript, yt-dlp cần để lấy link video YouTube"),
            _ => L.T("ghép hình và tiếng thành một tệp .mp4, đổi sang MP3"),
        }));
        _ui.Post(_ =>
        {
            var text = L.T("Để tải video từ YouTube, Facebook, TikTok…, DoMa cần thêm công cụ miễn phí, mã nguồn mở:\n\n")
                + lines + L.T("\n\nDoMa sẽ tải chúng từ GitHub một lần duy nhất. Tải ngay?");
            var caption = L.T("Tải video từ trang web");
            // Cửa sổ DoMa đang ẩn dưới khay: hiện hộp hỏi nổi lên trên trình duyệt.
            var answer = _form.Visible
                ? MessageBox.Show(_form, text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                : MessageBox.Show(text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
            asked.TrySetResult(answer == System.Windows.Forms.DialogResult.Yes);
        }, null);
        if (!await asked.Task)
            return false;
        _tray.Toast(L.T("Đang chuẩn bị tải video"), L.F("Đang tải {0} (một lần duy nhất, có thể mất vài phút)…", string.Join(", ", missing.Select(m => m.Name))), ToolTipIcon.None, null, 10);
        try
        {
            _ytInstallPercent = 0;
            await _ytTools.InstallAsync(new Progress<int>(p => _ytInstallPercent = p), CancellationToken.None);
            _ytInstallPercent = -1;
            _tray.Toast(L.T("Đã sẵn sàng tải video"), L.T("Đang lấy thông tin video…"), ToolTipIcon.Info, null);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            _log.Write("youtube-tools", e, null);
            _ytInstallPercent = -1;
            _tray.Toast(L.T("Không tải được công cụ tải video"), L.F("Kiểm tra kết nối mạng rồi thử lại. ({0})", e.Message), ToolTipIcon.Error, null, 10);
            return false;
        }
    }

    /// <summary>Link vừa bắt từ trình duyệt là link mới của một tệp đang chờ làm mới link: nhận nó và tải tiếp.</summary>
    private bool TryRefreshLink(DownloadRequest req, ProbeResult probe, string fileName)
    {
        if (_refresh.Match(_manager.Items, fileName, probe.Size) is not { } item)
            return false;
        if (!_manager.ReplaceLink(item.Id, req with { Media = item.Request.Media }))
            return false;
        _refresh.Disarm(item.Id);
        _tray.Toast(L.T("Đã làm mới link"), L.F("{0} · đang tải tiếp phần còn lại", Path.GetFileName(item.TargetPath)), ToolTipIcon.Info, ShowNow);
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
            _tray.Toast(L.T("Không có link nào để tải"), source.Length > 0 ? L.F("Trang {0} không có link tải được.", source) : L.T("Không tìm thấy link http/https."), ToolTipIcon.Warning, null);
            return false;
        }
        using var dialog = new UI.BatchForm(links, source, links.Any(l => l.IsMediaPage) ? LastMedia : null);
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return false;
        var referer = Uri.TryCreate(referrer, UriKind.Absolute, out var r) && r.Scheme is "http" or "https" ? referrer : null;
        var pages = dialog.Selected.Where(l => l.IsMediaPage).ToList();
        if (pages.Count > 0)
        {
            var media = dialog.Media ?? LastMedia;
            RememberMedia(media);
            _ = AddMediaPagesAsync(pages, media, dialog.StartNow, cookiesByHost, userAgent);
        }
        foreach (var link in dialog.Selected.Where(l => !l.IsMediaPage))
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
    private async Task AddManyAsync(IReadOnlyList<string> urls)
    {
        // Link magnet không nằm trong hộp "Tải nhiều link" (chỉ cho http/https): mở hộp tải riêng cho từng link.
        foreach (var magnet in urls.Where(Core.Engine.Torrent.TorrentLink.IsMagnet))
            await AddLinkAsync(magnet);
        urls = urls.Where(u => !Core.Engine.Torrent.TorrentLink.IsMagnet(u)).ToList();
        if (urls.Count == 0)
            return;
        if (urls.Count == 1)
        {
            await AddLinkAsync(urls[0]);
            return;
        }
        ChooseAndAdd(urls.Select(u => (u, (string?)null)), "", null, null, null);
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
                PostToForm(() => MessageBox.Show(_form, L.T("Không tải được link này."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning));
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
            MessageBox.Show(_form, L.T("Không lưu được cài đặt."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        var appearanceChanged = !string.Equals(updated.Appearance, _settings.Appearance, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(updated.Language, _settings.Language, StringComparison.OrdinalIgnoreCase);
        ApplySettings(updated);
        if (appearanceChanged)
            MessageBox.Show(_form, L.T("Chế độ màu hoặc ngôn ngữ mới sẽ được áp dụng khi bạn mở lại DoMa (Thoát ở khay hệ thống rồi mở lại)."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                _tray.Toast(L.T("Mất kết nối"), L.F("{0} · DoMa sẽ tự tải tiếp khi có mạng lại", Path.GetFileName(item.TargetPath)), ToolTipIcon.Warning, ShowNow);
            return;
        }
        _tray.Toast(L.T("Tải lỗi"), Path.GetFileName(item.TargetPath), ToolTipIcon.Error, ShowNow);
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
        var status = TrayController.Summary(active.Count, downloaded, total, speed);
        if (_ytInstallPercent >= 0)
            status = L.F("Đang tải công cụ YouTube {0}%", _ytInstallPercent) + (status is null ? "" : " · " + status);
        _tray.SetStatus(status);
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
        StartChannelWatch();
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
            _tray.Toast(L.T("Đã tải xong"), Path.GetFileName(item.TargetPath), ToolTipIcon.Info, () => UI.MainForm.OpenFolder(path));
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
        var threat = L.T("Windows Defender phát hiện");
        try
        {
            _manager.MarkThreat(id, threat);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Write("threat", e, null);
        }
        var name = Path.GetFileName(path);
        _tray.Notify(L.F("Phát hiện mã độc trong {0}. Đừng mở tệp này.", name), ShowNow);
        if (!File.Exists(path))
        {
            MessageBox.Show(_form.Visible ? _form : null,
                L.F("Windows Defender phát hiện mã độc trong “{0}” và đã cách ly tệp này. Máy của bạn an toàn.\n\nTệp vẫn được đánh dấu đỏ trong danh sách để bạn biết nguồn tải không đáng tin.", name),
                L.T("Cảnh báo bảo mật"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var answer = MessageBox.Show(_form.Visible ? _form : null,
            L.F("Windows Defender phát hiện mã độc trong “{0}”.\n\nBạn có muốn xoá tệp này ngay không? (Khuyến nghị: Có)\nNếu giữ lại, tệp được đánh dấu đỏ và DoMa sẽ hỏi lại mỗi khi bạn mở.", name),
            L.T("Cảnh báo bảo mật"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
        if (answer != System.Windows.Forms.DialogResult.Yes)
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Write("threat", e, null);
            MessageBox.Show(_form.Visible ? _form : null, L.T("Không xoá được tệp. Có thể Windows Defender đang xử lý nó."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Chuyển video .ts sang .mp4 (đóng gói lại, không nén), xoá tệp .ts và cập nhật danh sách.
    /// Trả về đường dẫn .mp4, hoặc null nếu không chuyển được (giữ nguyên .ts).
    /// </summary>
    /// <param name="quiet">Không báo khi không chuyển được (người gọi sẽ thử cách khác).</param>
    private async Task<string?> ConvertVideoAsync(Guid id, string path, bool notify, bool quiet = false)
    {
        var output = FileNameResolver.MakeUnique(Path.ChangeExtension(path, ".mp4"), p => File.Exists(p) || Directory.Exists(p));
        try
        {
            await Task.Run(() => TsToMp4.Convert(path, output));
            File.Delete(path);
            _manager.Relocate(id, output);
            if (notify)
                PostToForm(() => _tray.Notify(L.F("Đã chuyển sang MP4: {0}", Path.GetFileName(output)), output, ToolTipIcon.Info));
            return output;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
        {
            _log.Write("mp4", e, null);
            if (quiet)
                return null;
            var reason = e is NotSupportedException ? e.Message : L.T("Không chuyển được sang MP4, tệp .ts vẫn được giữ nguyên.");
            PostToForm(() => _tray.Notify(reason, path, ToolTipIcon.Warning));
            return null;
        }
    }

    /// <summary>
    /// Menu "Chuyển sang MP4/MP3". MP4: thay tệp gốc (đóng gói lại, nếu không được thì nén lại).
    /// MP3: tạo tệp mới cạnh tệp gốc, giữ nguyên tệp gốc.
    /// </summary>
    private async Task ConvertMediaAsync(DownloadItem item, string extension)
    {
        var id = item.Id;
        var path = item.TargetPath;
        var toMp3 = extension == ".mp3";
        // Video .ts: DoMa tự đóng gói lại được, không cần ffmpeg.
        if (!toMp3 && TsToMp4.CanConvert(path) && await ConvertVideoAsync(id, path, notify: true, quiet: true) is not null)
            return;
        if (!File.Exists(_ytTools.FfmpegPath) && !await EnsureYouTubeToolsAsync())
            return;
        var output = FileNameResolver.MakeUnique(Path.ChangeExtension(path, extension), p => File.Exists(p) || Directory.Exists(p));
        var name = Path.GetFileName(path);
        PostToForm(() => _tray.Toast(toMp3 ? L.T("Đang tách nhạc MP3") : L.T("Đang chuyển sang MP4"), name, ToolTipIcon.None, null, 5));
        try
        {
            if (toMp3)
                await Core.PostProcess.MediaConverter.RunAsync(_ytTools.FfmpegPath, Core.PostProcess.MediaConverter.Mp3Arguments(path, output));
            else
            {
                try
                {
                    await Core.PostProcess.MediaConverter.RunAsync(_ytTools.FfmpegPath, Core.PostProcess.MediaConverter.Mp4CopyArguments(path, output));
                }
                catch (IOException)
                {
                    // Định dạng gốc không đóng gói thẳng vào MP4 được: nén lại (chậm hơn).
                    await Core.PostProcess.MediaConverter.RunAsync(_ytTools.FfmpegPath, Core.PostProcess.MediaConverter.Mp4EncodeArguments(path, output));
                }
                File.Delete(path);
                _manager.Relocate(id, output);
            }
            PostToForm(() => _tray.Notify(toMp3 ? L.F("Đã tách nhạc: {0}", Path.GetFileName(output)) : L.F("Đã chuyển sang MP4: {0}", Path.GetFileName(output)), output, ToolTipIcon.Info));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _log.Write("convert", e, null);
            PostToForm(() => _tray.Notify(L.F("Không đổi được định dạng {0}. {1}", name, e.Message), path, ToolTipIcon.Warning));
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
                PostToForm(() => _tray.Notify(L.F("Đã giải nén {0}", Path.GetFileName(path)), folder, ToolTipIcon.Info));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            _log.Write("postprocess", e, null);
            PostToForm(() => _tray.Notify(L.F("Không xử lý được {0} sau khi tải: {1}", Path.GetFileName(path), e.Message), path, ToolTipIcon.Warning));
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
        _channelTimer.Dispose();
        _trayTimer.Dispose();
        _updates.Dispose();
        _clipboard.Dispose();
        _schedule.Dispose();
        _tray.Dispose();
        return true;
    }
}
