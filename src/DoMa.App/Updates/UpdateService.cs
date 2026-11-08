using System.ComponentModel;
using System.Diagnostics;
using DoMa.App.UI;
using DoMa.Core.Storage;
using DoMa.Core.Updates;

namespace DoMa.App.Updates;

/// <summary>
/// Định kỳ hỏi GitHub Releases xem có bản DoMa mới không; khi người dùng đồng ý thì tải bộ cài,
/// kiểm tra SHA-256, chạy cài đặt im lặng rồi thoát để bộ cài thay file và mở lại ứng dụng.
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private const string AppTitle = "Download Manager (DoMa)";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    // Mở cửa sổ chính thì kiểm tra lại nếu lần trước đã cách quá khoảng này.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private readonly MainForm _form;
    private readonly TrayController _tray;
    private readonly Func<Settings> _settings;
    private readonly Action _exitForUpdate;
    private readonly ErrorLog _log;
    private readonly Version _current = typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
    private readonly UpdateClient _client;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly CancellationTokenSource _cts = new();
    private Version? _dismissed;
    private Version? _announced;
    private bool _checking;
    private bool _installing;
    private DateTime _lastCheck = DateTime.MinValue;

    public UpdateService(MainForm form, TrayController tray, Func<Settings> settings, Action exitForUpdate, ErrorLog log)
    {
        _form = form;
        _tray = tray;
        _settings = settings;
        _exitForUpdate = exitForUpdate;
        _log = log;
        _client = new UpdateClient(_current);
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = (int)Interval.TotalMilliseconds;
            if (_settings().AutoCheckUpdates)
                await CheckAsync(manual: false);
        };
        _form.Activated += async (_, _) =>
        {
            if (_timer.Enabled && _settings().AutoCheckUpdates && DateTime.UtcNow - _lastCheck > StaleAfter)
                await CheckAsync(manual: false);
        };
    }

    public string CurrentVersion => ReleaseParser.Normalize(_current).ToString();

    public void Start()
    {
        _timer.Interval = (int)FirstCheck.TotalMilliseconds;
        _timer.Start();
    }

    public void CheckNow() => _ = CheckAsync(manual: true);

    private async Task CheckAsync(bool manual)
    {
        if (_checking || _installing)
            return;
        _checking = true;
        _lastCheck = DateTime.UtcNow;
        try
        {
            var release = await _client.GetLatestAsync(_cts.Token);
            if (release is null || !ReleaseParser.IsNewer(release, _current))
            {
                if (manual)
                    Message($"Bạn đang dùng phiên bản mới nhất ({CurrentVersion}).", MessageBoxIcon.Information);
                return;
            }
            if (!manual && (release.Version == _dismissed || release.Version == _announced))
                return;
            Announce(release, manual);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            if (_cts.IsCancellationRequested)
                return;
            _log.Write("update", e, null);
            if (manual)
                Message("Không kiểm tra được bản cập nhật. Hãy kiểm tra kết nối mạng rồi thử lại.", MessageBoxIcon.Warning);
        }
        finally
        {
            _checking = false;
        }
    }

    private void Announce(ReleaseInfo release, bool manual)
    {
        _announced = release.Version;
        var title = $"Đã có DoMa {release.Version}";
        var detail = $"Bạn đang dùng bản {CurrentVersion}. Cập nhật mất chưa tới một phút, các tệp đang tải sẽ tự tải tiếp.";
        _form.ShowUpdate(title, detail, () => _ = InstallAsync(release), () => _dismissed = release.Version, () => ShowNotes(release));
        if (manual || !_form.Visible || !_form.ContainsFocus)
            _tray.Notify($"{title}. Bấm để xem có gì mới.", () => ShowNotes(release));
        if (manual)
            ShowNotes(release);
    }

    /// <summary>Mở cửa sổ chính và hiện nội dung bản cập nhật; người dùng có thể cập nhật ngay từ đó.</summary>
    private void ShowNotes(ReleaseInfo release)
    {
        if (_form.IsDisposed || _installing)
            return;
        ShowForm();
        using var dialog = new UpdateNotesForm(release.Version.ToString(), CurrentVersion, ReleaseNotes.ToPlainText(release.Notes));
        if (dialog.ShowDialog(_form) == DialogResult.OK)
            _ = InstallAsync(release);
    }

    private void ShowForm()
    {
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized)
            _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private async Task InstallAsync(ReleaseInfo release)
    {
        if (_installing)
            return;
        _installing = true;
        try
        {
            _form.SetUpdateStatus("Đang tải bộ cài…", busy: true);
            var progress = new Progress<double>(p => _form.SetUpdateStatus($"Đang tải bộ cài… {p:P0}", busy: true));
            var setup = await _client.DownloadSetupAsync(release, progress, _cts.Token);
            _form.SetUpdateStatus("Đang cài đặt, DoMa sẽ tự mở lại…", busy: true);
            Process.Start(new ProcessStartInfo(setup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /UPDATE=1") { UseShellExecute = true });
            _exitForUpdate();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or Win32Exception or UnauthorizedAccessException)
        {
            if (_cts.IsCancellationRequested)
                return;
            _log.Write("update", e, null);
            _installing = false;
            _form.SetUpdateStatus("Cập nhật chưa thành công. Bấm “Cập nhật ngay” để thử lại.", busy: false);
            var manual = release.PageUrl is null ? "" : "\n\nNếu vẫn lỗi, bạn có thể tải bộ cài trực tiếp tại:\n" + release.PageUrl;
            Message(UpdateErrors.Describe(e) + manual, MessageBoxIcon.Warning);
        }
    }

    private void Message(string text, MessageBoxIcon icon)
    {
        if (_form.IsDisposed)
            return;
        MessageBox.Show(_form.Visible ? _form : null, text, AppTitle, MessageBoxButtons.OK, icon);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _cts.Cancel();
        _client.Dispose();
    }
}
