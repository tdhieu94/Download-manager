using System.Text.Json.Nodes;
using DoMa.App.Capture;
using DoMa.App.Ipc;
using DoMa.App.UI;
using DoMa.App.Updates;
using DoMa.Core.Engine;
using DoMa.Core.Files;
using DoMa.Core.Messaging;
using DoMa.Core.Models;
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
    private volatile Settings _settings;
    private bool _exited;

    public MainContext(bool hidden)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Directory.CreateDirectory(AppPaths.StatesDir);
        _settings = _settingsStore.Load();

        var ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);

        var prober = new LinkProber(_http);
        _manager = new DownloadManager(
            new SegmentedDownloadEngine(_http),
            prober.ProbeAsync,
            new DownloadStore(AppPaths.DataDir),
            () => _settings,
            AppPaths.StatesDir,
            ui);
        _coordinator = new CaptureCoordinator(
            prober.ProbeAsync,
            new NewDownloadDialogHost(ui),
            _manager,
            () => new CategoryResolver(_settings.Extensions, _settings.RootFolder),
            Notify);

        foreach (var item in _manager.Items)
            _lastStatus[item.Id] = item.Status;
        _manager.Changed += OnChanged;
        _manager.Completed += OnCompleted;
        _manager.Removed += id => _lastStatus.Remove(id);

        _form = new MainForm(_manager, AddLinkAsync, OpenSettings, Exit, ExitOnShutdown, _log);
        _tray = new TrayController(ShowNow, Exit, UI.MainForm.OpenFolder, () => _updates?.CheckNow());
        _updates = new UpdateService(_form, _tray, () => _settings, ExitForUpdate, _log);
        _ = _form.Handle;
        if (!hidden)
            _form.Show();
        ResumeAfterUpdate();
        _updates.Start();
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
        }
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
        _settings = updated;
        _manager.Reschedule();
    }

    private void OnChanged(DownloadItem item)
    {
        _lastStatus.TryGetValue(item.Id, out var previous);
        _lastStatus[item.Id] = item.Status;
        if (item.Status == DownloadStatus.Failed && previous != DownloadStatus.Failed)
            _tray.Notify($"Tải lỗi: {Path.GetFileName(item.TargetPath)}", item.TargetPath, ToolTipIcon.Error);
    }

    private void OnCompleted(DownloadItem item)
    {
        if (_settings.NotifyOnComplete)
            _tray.Notify($"Đã tải xong: {Path.GetFileName(item.TargetPath)}", item.TargetPath, ToolTipIcon.Info);
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
        _updates.Dispose();
        _tray.Dispose();
        return true;
    }
}
