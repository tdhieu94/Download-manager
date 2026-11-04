using DoMa.App.UI.Controls;

namespace DoMa.App.UI;

public sealed class TrayController : IDisposable
{
    private const string Title = "Download Manager (DoMa)";

    private readonly NotifyIcon _icon;
    private readonly Action<string> _openFolder;
    private Action? _balloonAction;

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
        _icon.BalloonTipClicked += (_, _) =>
        {
            _balloonAction?.Invoke();
        };
    }

    public void Notify(string text, string? pathToOpen, ToolTipIcon icon)
    {
        _balloonAction = pathToOpen is null ? null : () => _openFolder(pathToOpen);
        _icon.ShowBalloonTip(5000, Title, text, icon);
    }

    /// <summary>Thông báo kèm hành động khi người dùng bấm vào thông báo.</summary>
    public void Notify(string text, Action onClick)
    {
        _balloonAction = onClick;
        _icon.ShowBalloonTip(8000, Title, text, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
