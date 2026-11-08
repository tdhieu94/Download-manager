using DoMa.App.UI.Controls;

namespace DoMa.App.UI;

public sealed class TrayController : IDisposable
{
    private const string Title = "Download Manager (DoMa)";

    private readonly NotifyIcon _icon;
    private readonly Action<string> _openFolder;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public TrayController(Action open, Action exit, Action<string> openFolder, Action checkUpdates,
        Action? pauseAll = null, Action? resumeAll = null, Func<(bool CanPause, bool CanResume)>? canToggle = null)
    {
        _openFolder = openFolder;
        var menu = ThemedRenderer.CreateMenu();
        var openItem = ThemedRenderer.Item(menu, L.T("Mở DoMa"), IconKind.Download, (_, _) => open());
        openItem.Font = Theme.BodySemibold;
        if (pauseAll is not null && resumeAll is not null)
        {
            menu.Items.Add(new ToolStripSeparator());
            var pause = ThemedRenderer.Item(menu, L.T("Tạm dừng tất cả"), IconKind.Pause, (_, _) => pauseAll());
            var resume = ThemedRenderer.Item(menu, L.T("Tiếp tục tất cả"), IconKind.Play, (_, _) => resumeAll());
            menu.Opening += (_, _) =>
            {
                var (canPause, canResume) = canToggle?.Invoke() ?? (true, true);
                pause.Enabled = canPause;
                resume.Enabled = canResume;
            };
            menu.Items.Add(new ToolStripSeparator());
        }
        ThemedRenderer.Item(menu, L.T("Kiểm tra cập nhật"), IconKind.Refresh, (_, _) => checkUpdates());
        menu.Items.Add(new ToolStripSeparator());
        ThemedRenderer.Item(menu, L.T("Thoát"), IconKind.Power, (_, _) => exit());
        _icon = new NotifyIcon
        {
            Icon = Theme.AppIconSized(SystemInformation.SmallIconSize.Width),
            Text = Title,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                open();
        };
    }

    public void Notify(string text, string? pathToOpen, ToolTipIcon icon) =>
        Toast(Title, text, icon, pathToOpen is null ? null : () => _openFolder(pathToOpen));

    /// <summary>Thông báo kèm hành động khi người dùng bấm vào thông báo.</summary>
    public void Notify(string text, Action onClick) => Toast(Title, text, ToolTipIcon.Info, onClick, 8);

    /// <summary>
    /// Hiện thông báo bằng cửa sổ nhỏ tự vẽ ở góc màn hình (không dùng balloon của Windows,
    /// vốn bị ẩn khi tắt thông báo hoặc bật Focus Assist).
    /// </summary>
    public void Toast(string title, string text, ToolTipIcon kind, Action? onClick, int seconds = 6)
    {
        void Show() => ToastForm.Show(title, text, kind, onClick, seconds);
        if (_ui is null || SynchronizationContext.Current == _ui)
            Show();
        else
            _ui.Post(_ => Show(), null);
    }

    /// <summary>Chữ hiện khi trỏ chuột vào biểu tượng ở khay: tiến độ chung khi đang tải.</summary>
    public void SetStatus(string? status)
    {
        var text = string.IsNullOrEmpty(status) ? Title : $"DoMa · {status}";
        // Windows chỉ cho tối đa 127 ký tự.
        if (text.Length > 127)
            text = text[..126] + "…";
        if (_icon.Text != text)
            _icon.Text = text;
    }

    /// <summary>Tóm tắt các tệp đang tải: "Đang tải 3 tệp · 45% · 2,1 MB/s"; null khi không tải gì.</summary>
    public static string? Summary(int active, long downloaded, long? total, double bytesPerSec)
    {
        if (active == 0)
            return null;
        var parts = new List<string> { active == 1 ? L.T("Đang tải 1 tệp") : L.F("Đang tải {0} tệp", active) };
        if (total is > 0)
            parts.Add($"{Math.Clamp(downloaded * 100 / total.Value, 0, 100)}%");
        if (DownloadView.FormatSpeed(bytesPerSec) is { Length: > 0 } speed)
            parts.Add(speed);
        return string.Join(" · ", parts);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
