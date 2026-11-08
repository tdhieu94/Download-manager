using DoMa.App.UI.Controls;

namespace DoMa.App.UI;

public sealed class TrayController : IDisposable
{
    private const string Title = "Download Manager (DoMa)";

    private readonly NotifyIcon _icon;
    private readonly Action<string> _openFolder;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public TrayController(Action open, Action exit, Action<string> openFolder, Action checkUpdates)
    {
        _openFolder = openFolder;
        var menu = ThemedRenderer.CreateMenu();
        var openItem = ThemedRenderer.Item(menu, "Mở DoMa", IconKind.Download, (_, _) => open());
        openItem.Font = Theme.BodySemibold;
        ThemedRenderer.Item(menu, "Kiểm tra cập nhật", IconKind.Refresh, (_, _) => checkUpdates());
        menu.Items.Add(new ToolStripSeparator());
        ThemedRenderer.Item(menu, "Thoát", IconKind.Power, (_, _) => exit());
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

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
