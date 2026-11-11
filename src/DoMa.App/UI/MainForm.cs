using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DoMa.App.UI.Controls;
using DoMa.Core.Models;
using DoMa.Core.Queue;
using DoMa.Core.Storage;

namespace DoMa.App.UI;

/// <summary>Các thao tác nhanh cho menu "Tốc độ và lịch" ở đầu cửa sổ.</summary>
internal sealed record QuickActions(
    Func<int> SpeedLimit,
    Action<int> SetSpeedLimit,
    Func<int> StartLaterNow,
    Func<Scheduling.AfterDone> AfterDone,
    Action<Scheduling.AfterDone> SetAfterDone,
    Func<string> Describe,
    Action? ManageChannels = null);

public sealed class MainForm : Form
{
    private const string AppTitle = "Download Manager (DoMa)";

    private static readonly NavItem[] NavEntries =
    [
        new(null, L.T("Trạng thái"), IconKind.None),
        new(DownloadFilter.All, L.T("Tất cả"), IconKind.Inbox),
        new(DownloadFilter.Active, L.T("Đang tải"), IconKind.Download),
        new(DownloadFilter.Completed, L.T("Đã xong"), IconKind.Check),
        new(DownloadFilter.Later, L.T("Chờ tải sau"), IconKind.Clock),
        new(null, L.T("Loại file"), IconKind.None),
        new(DownloadFilter.Documents, L.T("Tài liệu"), IconKind.Document),
        new(DownloadFilter.Software, L.T("Phần mềm"), IconKind.Package),
        new(DownloadFilter.Archives, L.T("Nén"), IconKind.Archive),
        new(DownloadFilter.Media, L.T("Video, nhạc"), IconKind.Play),
        new(DownloadFilter.Other, L.T("Khác"), IconKind.Shapes),
    ];

    private readonly DownloadManager _manager;
    private readonly Func<string, Task> _addLink;

    /// <summary>Khi quét thủ công phát hiện mã độc; MainContext ghi nhận và hỏi người dùng xử lý.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<DownloadItem>? ThreatFound { get; set; }

    /// <summary>Thêm nhiều link một lúc (dán/kéo-thả): MainContext mở hộp "Tải nhiều link".</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<IReadOnlyList<string>, Task>? AddMany { get; set; }

    /// <summary>Đổi định dạng tệp đã tải: ".mp4" (thay tệp gốc) hoặc ".mp3" (tách nhạc thành tệp mới).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<DownloadItem, string>? ConvertMedia { get; set; }

    /// <summary>"Làm mới link": chờ link mới từ trình duyệt cho tệp này (sau khi mở trang nguồn).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<DownloadItem>? WaitForNewLink { get; set; }
    private readonly Action _openSettings;
    private readonly Action _requestExit;
    private readonly Action _shutdown;
    private readonly ErrorLog _log;
    private readonly NavList _nav = new();
    private readonly SmoothGrid _grid = new();
    private readonly InputBox _search = new(IconKind.Search) { PlaceholderText = L.T("Tìm theo tên file  (Ctrl+F)") };
    private readonly Label _viewTitle = new() { Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true };
    private readonly Label _viewCount = new() { Font = Theme.Small, ForeColor = Theme.TextMuted, AutoSize = true };
    private readonly ModernButton _resume = new(L.T("Tiếp tục"), IconKind.Play);
    private readonly ModernButton _pause = new(L.T("Tạm dừng"), IconKind.Pause);
    private readonly ModernButton _remove = new(L.T("Xoá"), IconKind.Trash);
    private readonly Panel _statusBar = new();
    private readonly Panel _updateBanner = new();
    private readonly ModernButton _updateNow = new(L.T("Cập nhật ngay"), IconKind.Download, ButtonKind.Primary);
    private readonly ModernButton _updateLater = new(L.T("Để sau"), IconKind.None, ButtonKind.Ghost);
    private readonly ModernButton _updateNotes = new(L.T("Có gì mới"), IconKind.Document, ButtonKind.Secondary);
    // Control.Visible trả về false khi cửa sổ đang ẩn ở khay, nên tự nhớ nút nào cần hiện để xếp chỗ cho đúng.
    private bool _updateLaterShown = true;
    private bool _updateNotesShown;
    private string _updateTitle = "";
    private string _updateDetail = "";
    private Action? _onUpdate;
    private Action? _onUpdateLater;
    private Action? _onUpdateNotes;
    private readonly ToolTip _tips = new();
    private readonly Bitmap _logo = Theme.AppIconSized(64).ToBitmap();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private List<DownloadItem> _view = [];
    private string _statusLeft = "";
    private string _statusRight = "";
    private bool _anyActive;
    private int _hoverRow = -1;
    private bool _dirty = true;
    private bool _exiting;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal QuickActions? QuickActions { get; set; }

    public MainForm(DownloadManager manager, Func<string, Task> addLink, Action openSettings, Action requestExit, Action shutdown, ErrorLog log)
    {
        _manager = manager;
        _addLink = addLink;
        _openSettings = openSettings;
        _requestExit = requestExit;
        _shutdown = shutdown;
        _log = log;

        Text = L.T("DoMa — Trình quản lý tải xuống");
        Icon = Theme.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.Body;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        ClientSize = new Size(S(1200), S(660));
        MinimumSize = new Size(S(1020), S(520));
        KeyPreview = true;
        Theme.StyleWindow(this);

        // Thứ tự Add quyết định thứ tự Dock: thêm sau được neo trước.
        Controls.Add(BuildContent());
        Controls.Add(BuildSidebar());
        Controls.Add(BuildStatusBar());
        Controls.Add(BuildHeader());

        foreach (var control in new Control[] { this, _grid, _nav })
            EnableLinkDrop(control);

        _manager.Changed += _ => _dirty = true;
        _manager.Removed += _ => _dirty = true;
        _timer.Tick += (_, _) => RefreshView();
        _timer.Start();
        RefreshView();
        UpdateActions();
    }

    private int S(int value) => Theme.Scale(this, value);

    public void ExitForReal() => _exiting = true;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting)
        {
            if (e.CloseReason == CloseReason.WindowsShutDown)
            {
                _shutdown();
            }
            else
            {
                e.Cancel = true;
                if (e.CloseReason == CloseReason.UserClosing)
                    Hide();
                else
                    BeginInvoke(_requestExit);
            }
        }
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.N:
                _ = AddLinkAsync();
                return true;
            case Keys.Control | Keys.F:
                _search.Box.Focus();
                _search.Box.SelectAll();
                return true;
            case Keys.Control | Keys.V when !_search.Box.Focused:
                string? clip = null;
                try
                {
                    clip = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                }
                catch (ExternalException)
                {
                }
                var pasted = ExtractLinks(clip);
                if (pasted.Count > 0)
                {
                    _ = AddLinksAsync(pasted);
                    return true;
                }
                break;
            case Keys.Delete when _grid.Focused:
                RemoveSelected();
                return true;
            case Keys.Space when _grid.Focused:
                TogglePauseSelected();
                return true;
            case Keys.F2 when _grid.Focused:
                ForFirst(i =>
                {
                    if (i.Status != DownloadStatus.Downloading)
                        RenameItem(i);
                });
                return true;
            case Keys.Control | Keys.C when _grid.Focused:
                ForFirst(CopyLink);
                return true;
            case Keys.Control | Keys.O when _grid.Focused:
                ForFirst(i => OpenFolder(i.TargetPath));
                return true;
            case Keys.Control | Keys.Up when _grid.Focused:
                MoveSelected(up: true);
                return true;
            case Keys.Control | Keys.Down when _grid.Focused:
                MoveSelected(up: false);
                return true;
            case Keys.Control | Keys.A when _grid.Focused:
                _grid.SelectAll();
                return true;
            case Keys.Enter when _grid.Focused:
                ForFirst(i =>
                {
                    if (i.Status == DownloadStatus.Completed)
                        OpenItem(i);
                });
                return true;
            case Keys.Escape when _search.Box.Focused && _search.Text.Length > 0:
                _search.Text = "";
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Space: nếu trong các tệp chọn có tệp đang tải/chờ thì tạm dừng chúng, không thì tải tiếp.</summary>
    private void TogglePauseSelected()
    {
        var ids = SelectedIds();
        var selected = _manager.Items.Where(i => ids.Contains(i.Id)).ToList();
        var active = selected.Where(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued).Select(i => i.Id).ToList();
        if (active.Count > 0)
            _manager.Pause(active);
        else
            _manager.Resume(selected.Where(i => i.Status != DownloadStatus.Completed).Select(i => i.Id));
    }

    /// <summary>Mở tệp đã tải; tệp bị phát hiện mã độc thì phải xác nhận lại.</summary>
    private void OpenItem(DownloadItem item)
    {
        if (item.Threat is not null)
        {
            var answer = MessageBox.Show(this,
                L.F("“{0}” bị phát hiện có mã độc ({1}).\n\nMở tệp này có thể làm hại máy tính và dữ liệu của bạn. Bạn vẫn muốn mở?", Path.GetFileName(item.TargetPath), item.Threat),
                L.T("Cảnh báo bảo mật"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;
        }
        OpenFile(item.TargetPath);
    }

    public static void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(L.T("Không tìm thấy tệp trên ổ đĩa."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            MessageBox.Show(L.T("Không mở được tệp."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Win32Exception)
        {
            MessageBox.Show(L.T("Không mở được thư mục."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ───────────────────────── Đầu trang ─────────────────────────

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = S(64), BackColor = Theme.Surface };
        header.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var logo = S(34);
            g.DrawImage(_logo, new Rectangle(S(22), (header.Height - logo) / 2, logo, logo));
            TextRenderer.DrawText(g, "DoMa", Theme.Brand, new Point(S(66), S(12)), Theme.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, L.T("Trình quản lý tải xuống"), Theme.Small, new Point(S(67), S(35)), Theme.TextMuted, TextFormatFlags.NoPadding);
            using var pen = new Pen(Theme.Border);
            g.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        var add = new ModernButton(L.T("Thêm link"), IconKind.Plus, ButtonKind.Primary);
        add.Click += async (_, _) => await AddLinkAsync();
        _tips.SetToolTip(add, L.T("Thêm đường dẫn tải mới (Ctrl+N)"));

        var speed = new ModernButton(L.T("Tốc độ và lịch"), IconKind.Gauge, ButtonKind.Ghost) { IconOnly = true };
        speed.Click += (_, _) => ShowQuickMenu(speed);
        _tips.SetToolTip(speed, L.T("Giới hạn tốc độ, tải theo lịch, việc làm khi tải xong"));

        var settings = new ModernButton(L.T("Cài đặt"), IconKind.Settings, ButtonKind.Ghost) { IconOnly = true };
        settings.Click += (_, _) => _openSettings();
        _tips.SetToolTip(settings, L.T("Cài đặt"));

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, S(15), S(16), 0),
            BackColor = Theme.Surface,
        };
        settings.Margin = new Padding(S(2), 0, 0, 0);
        speed.Margin = new Padding(S(6), 0, 0, 0);
        add.Margin = new Padding(0);
        actions.Controls.Add(settings);
        actions.Controls.Add(speed);
        actions.Controls.Add(add);
        header.Controls.Add(actions);
        return header;
    }

    private static readonly (int KBps, string Text)[] SpeedPresets =
    [
        (0, L.T("Không giới hạn")),
        (256, "256 KB/s"),
        (512, "512 KB/s"),
        (1024, "1 MB/s"),
        (2048, "2 MB/s"),
        (5120, "5 MB/s"),
        (10240, "10 MB/s"),
    ];

    private void ShowQuickMenu(Control anchor)
    {
        if (QuickActions is not { } quick)
            return;
        var menu = ThemedRenderer.CreateMenu();
        menu.ShowCheckMargin = true;
        menu.ShowImageMargin = false;

        AddHeader(menu, L.T("Tốc độ tối đa"));
        var current = quick.SpeedLimit();
        var preset = false;
        foreach (var (kbps, text) in SpeedPresets)
        {
            var item = new ToolStripMenuItem(text) { Checked = current == kbps, Padding = new Padding(4, 4, 4, 4) };
            preset |= current == kbps;
            item.Click += (_, _) => { quick.SetSpeedLimit(kbps); RefreshStatus(); };
            menu.Items.Add(item);
        }
        if (!preset)
            menu.Items.Add(new ToolStripMenuItem(L.F("Tuỳ chỉnh: {0}", DownloadView.FormatSpeed(current * 1024.0))) { Checked = true, Enabled = false });
        menu.Items.Add(new ToolStripMenuItem(L.T("Đặt mức khác…"), null, (_, _) => _openSettings()));

        menu.Items.Add(new ToolStripSeparator());
        AddHeader(menu, L.T("Hàng chờ"));
        menu.Items.Add(new ToolStripMenuItem(L.T("Bắt đầu tải hàng “Chờ tải sau” ngay"), null, (_, _) =>
        {
            if (quick.StartLaterNow() == 0)
                MessageBox.Show(this, L.T("Hàng “Chờ tải sau” đang trống."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }));
        var failed = _manager.Items.Where(i => i.Status == DownloadStatus.Failed).Select(i => i.Id).ToList();
        menu.Items.Add(new ToolStripMenuItem(L.F("Thử lại các tệp lỗi ({0})", failed.Count), null, (_, _) => RunLogged(() => _manager.Resume(failed))) { Enabled = failed.Count > 0 });
        menu.Items.Add(new ToolStripMenuItem(L.T("Hẹn giờ tải tự động…"), null, (_, _) => _openSettings()));
        menu.Items.Add(new ToolStripMenuItem(L.T("Dọn tệp đã tải xong khỏi danh sách…"), null, (_, _) => ClearCompleted()));
        if (quick.ManageChannels is { } channels)
            menu.Items.Add(new ToolStripMenuItem(L.T("Kênh YouTube đang theo dõi…"), null, (_, _) => channels()));

        menu.Items.Add(new ToolStripSeparator());
        AddHeader(menu, L.T("Khi tải xong tất cả"));
        var after = quick.AfterDone();
        foreach (var (value, text) in new[] { (Scheduling.AfterDone.Nothing, L.T("Không làm gì")), (Scheduling.AfterDone.Sleep, L.T("Cho máy ngủ")), (Scheduling.AfterDone.Shutdown, L.T("Tắt máy")) })
        {
            var item = new ToolStripMenuItem(text) { Checked = after == value };
            item.Click += (_, _) => { quick.SetAfterDone(value); RefreshStatus(); };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(anchor, new Point(anchor.Width - menu.PreferredSize.Width, anchor.Height + S(4)));
    }

    /// <summary>Bỏ các tệp đã tải xong khỏi danh sách cho gọn; tệp trên ổ đĩa vẫn giữ nguyên.</summary>
    private void ClearCompleted()
    {
        var done = _manager.Items.Where(i => i.Status == DownloadStatus.Completed).Select(i => i.Id).ToList();
        if (done.Count == 0)
        {
            MessageBox.Show(this, L.T("Chưa có tệp nào tải xong."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var answer = MessageBox.Show(this,
            L.F("Bỏ {0} tệp đã tải xong khỏi danh sách?\n\nTệp trên ổ đĩa vẫn được giữ nguyên.", done.Count),
            L.T("Dọn danh sách"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.OK)
            RunLogged(() => _manager.Remove(done, false));
    }

    private static void AddHeader(ContextMenuStrip menu, string text) =>
        menu.Items.Add(new ToolStripLabel(text.ToUpperInvariant()) { Font = Theme.SmallBold, ForeColor = Theme.TextMuted, Tag = "header", Padding = new Padding(0, 6, 0, 2) });

    private void RefreshStatus()
    {
        _dirty = true;
        RefreshView();
    }

    // ───────────────────────── Thanh bên ─────────────────────────

    private Control BuildSidebar()
    {
        var side = new Panel { Dock = DockStyle.Left, Width = S(236), BackColor = Theme.Window, Padding = new Padding(S(6), S(10), 0, 0) };
        foreach (var entry in NavEntries)
            _nav.Add(entry);
        _nav.Dock = DockStyle.Fill;
        _nav.SelectionChanged += (_, _) =>
        {
            _dirty = true;
            RefreshView();
        };
        side.Controls.Add(_nav);
        return side;
    }

    // ───────────────────────── Thanh trạng thái ─────────────────────────

    private Control BuildStatusBar()
    {
        _statusBar.Dock = DockStyle.Bottom;
        _statusBar.Height = S(32);
        _statusBar.BackColor = Theme.Window;
        _statusBar.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var dot = S(8);
            using (var brush = new SolidBrush(_anyActive ? Theme.Success : Theme.Neutral))
                g.FillEllipse(brush, S(24), (_statusBar.Height - dot) / 2f, dot, dot);
            TextRenderer.DrawText(g, _statusLeft, Theme.Small, new Rectangle(S(40), 0, _statusBar.Width / 2, _statusBar.Height), Theme.TextSecondary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _statusRight, Theme.Small, new Rectangle(_statusBar.Width / 2, 0, _statusBar.Width / 2 - S(24), _statusBar.Height), Theme.TextMuted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
        };
        return _statusBar;
    }

    // ───────────────────────── Nội dung chính ─────────────────────────

    private Control BuildContent()
    {
        var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window, Padding = new Padding(S(4), S(16), S(20), S(4)) };
        var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(S(6), S(2), S(6), S(8)) };

        var top = new Panel { Dock = DockStyle.Top, Height = S(72), BackColor = Theme.Surface };
        _viewTitle.Location = new Point(S(16), S(14));
        _viewCount.Location = new Point(S(18), S(44));
        top.Controls.Add(_viewTitle);
        top.Controls.Add(_viewCount);

        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, S(19), S(10), 0),
            BackColor = Theme.Surface,
        };
        _search.Size = new Size(S(220), S(34));
        _search.Margin = new Padding(0, 0, S(12), 0);
        _search.TextChanged += (_, _) =>
        {
            _dirty = true;
            RefreshView();
        };
        _resume.Click += (_, _) => _manager.Resume(SelectedIds());
        _pause.Click += (_, _) => _manager.Pause(SelectedIds());
        _remove.Click += (_, _) => RemoveSelected();
        _tips.SetToolTip(_resume, L.T("Tiếp tục tải các tệp đã chọn (Space)"));
        _tips.SetToolTip(_pause, L.T("Tạm dừng các tệp đã chọn (Space)"));
        _tips.SetToolTip(_remove, L.T("Xoá các tệp đã chọn (Delete)"));
        foreach (var b in new[] { _resume, _pause, _remove })
            b.Margin = new Padding(0, 0, S(6), 0);
        tools.Controls.AddRange([_search, _resume, _pause, _remove]);
        top.Controls.Add(tools);

        BuildGrid();
        card.Controls.Add(_grid);
        card.Controls.Add(top);
        content.Controls.Add(card);
        content.Controls.Add(BuildUpdateBanner());
        return content;
    }

    private Control BuildUpdateBanner()
    {
        _updateBanner.Dock = DockStyle.Top;
        _updateBanner.Height = S(68);
        _updateBanner.BackColor = Theme.Window;
        _updateBanner.Visible = false;
        _updateBanner.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new RectangleF(0.5f, 0.5f, _updateBanner.Width - 1.5f, _updateBanner.Height - S(12));
            Theme.FillRound(g, Theme.AccentSoft, box, S(10));
            Theme.DrawRound(g, Theme.AccentSoftStrong, box, S(10));
            var icon = S(20);
            Icons.Draw(g, IconKind.Download, new RectangleF(S(16), box.Y + (box.Height - icon) / 2, icon, icon), Theme.Accent);
            var textW = Math.Max(0, LeftmostBannerButton() - S(60));
            TextRenderer.DrawText(g, _updateTitle, Theme.BodySemibold, new Rectangle(S(48), (int)box.Y + S(9), textW, S(20)), Theme.AccentText,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, _updateDetail, Theme.Small, new Rectangle(S(48), (int)box.Y + S(29), textW, S(18)), Theme.TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        };
        foreach (var b in new[] { _updateNow, _updateLater, _updateNotes })
        {
            b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _updateBanner.Controls.Add(b);
        }
        _updateBanner.Resize += (_, _) => LayoutBanner();
        _updateBanner.BackColorChanged += (_, _) => LayoutBanner();
        _updateBanner.VisibleChanged += (_, _) => LayoutBanner();
        _updateNow.Click += (_, _) => _onUpdate?.Invoke();
        _updateNotes.Click += (_, _) => _onUpdateNotes?.Invoke();
        _updateLater.Click += (_, _) =>
        {
            _updateBanner.Visible = false;
            _onUpdateLater?.Invoke();
        };
        return _updateBanner;
    }

    private void LayoutBanner()
    {
        var y = (_updateBanner.Height - S(12) - S(34)) / 2;
        _updateNow.Location = new Point(_updateBanner.Width - _updateNow.Width - S(12), y);
        _updateLater.Location = new Point(_updateNow.Left - _updateLater.Width - S(6), y);
        _updateNotes.Location = new Point((_updateLaterShown ? _updateLater.Left : _updateNow.Left) - _updateNotes.Width - S(6), y);
        _updateLater.Visible = _updateLaterShown;
        _updateNotes.Visible = _updateNotesShown;
        _updateBanner.Invalidate();
    }

    private int LeftmostBannerButton() =>
        _updateNotesShown ? _updateNotes.Left : _updateLaterShown ? _updateLater.Left : _updateNow.Left;

    /// <summary>Hiện dải thông báo có bản cập nhật mới phía trên danh sách tải.</summary>
    public void ShowUpdate(string title, string detail, Action install, Action later, Action? showNotes = null)
    {
        _onUpdateNotes = showNotes;
        _updateNotesShown = showNotes is not null;
        _updateTitle = title;
        _updateDetail = detail;
        _onUpdate = install;
        _onUpdateLater = later;
        _updateNow.Enabled = true;
        _updateLaterShown = true;
        _updateBanner.Visible = true;
        LayoutBanner();
    }

    /// <summary>Cập nhật dòng mô tả của dải thông báo (ví dụ tiến độ tải bộ cài).</summary>
    public void SetUpdateStatus(string detail, bool busy)
    {
        _updateDetail = detail;
        _updateNow.Enabled = !busy;
        _updateLaterShown = !busy;
        LayoutBanner();
    }

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.VirtualMode = true;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.MultiSelect = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.BackgroundColor = Theme.Surface;
        _grid.BorderStyle = BorderStyle.None;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.GridColor = Theme.Divider;
        Theme.StyleScrollbars(_grid);
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = S(38);
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.TextMuted,
            SelectionBackColor = Theme.SurfaceAlt,
            SelectionForeColor = Theme.TextMuted,
            Font = Theme.SmallBold,
            Padding = new Padding(S(10), 0, S(8), 0),
            Alignment = DataGridViewContentAlignment.MiddleLeft,
        };
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            SelectionBackColor = Theme.AccentSoft,
            SelectionForeColor = Theme.Text,
            Font = Theme.Body,
            Padding = new Padding(S(10), 0, S(8), 0),
            WrapMode = DataGridViewTriState.False,
        };
        _grid.RowTemplate.Height = S(54);

        var muted = new DataGridViewCellStyle { ForeColor = Theme.TextSecondary, SelectionForeColor = Theme.TextSecondary };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = L.T("TÊN FILE"), FillWeight = 40, MinimumWidth = S(200) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = L.T("DUNG LƯỢNG"), FillWeight = 13, DefaultCellStyle = muted });
        _grid.Columns.Add(new ProgressColumn { Name = "Progress", HeaderText = L.T("TIẾN ĐỘ"), FillWeight = 22 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Speed", HeaderText = L.T("TỐC ĐỘ"), FillWeight = 12, DefaultCellStyle = muted });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Eta", HeaderText = L.T("CÒN LẠI"), FillWeight = 12, DefaultCellStyle = muted });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = L.T("THỜI GIAN"), FillWeight = 17, MinimumWidth = S(118), DefaultCellStyle = muted });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = L.T("TRẠNG THÁI"), FillWeight = 22 });
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (DataGridViewColumn column in _grid.Columns)
            column.SortMode = DataGridViewColumnSortMode.NotSortable;

        _grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _view.Count)
                return;
            var item = _view[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => Path.GetFileName(item.TargetPath),
                1 => DownloadView.SizeText(item),
                2 => new ProgressValue(
                    item.Status == DownloadStatus.Completed ? 100 : DownloadView.Percent(item.DownloadedBytes, item.TotalBytes),
                    Theme.StatusColor(item)),
                3 => DownloadView.SpeedText(item.Status == DownloadStatus.Downloading ? _manager.SpeedBytesPerSec(item.Id) : 0,
                    item.Status == DownloadStatus.Completed ? 0 : item.SpeedLimitKBps),
                4 => DownloadView.FormatEta(_manager.Eta(item.Id)),
                5 => DownloadView.TimeText(item),
                _ => DownloadView.StatusText(item, QueuePosition(item)),
            };
        };
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex == _hoverRow && e.CellStyle is not null)
                e.CellStyle.BackColor = Theme.SurfaceAlt;
        };
        _grid.CellPainting += PaintCell;
        _grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || SortColumnAt(e.ColumnIndex) is not { } column)
                return;
            // Bấm lần đầu: sắp xếp theo cột; lần hai: đảo chiều; lần ba: về mặc định (mới thêm trước).
            if (_sortColumn != column)
                (_sortColumn, _sortAscending) = (column, column is SortColumn.Name or SortColumn.Status);
            else if (_sortAscending == column is SortColumn.Name or SortColumn.Status)
                _sortAscending = !_sortAscending;
            else
                (_sortColumn, _sortAscending) = (SortColumn.Added, false);
            _dirty = true;
            RefreshView();
            _grid.Invalidate();
        };
        _grid.ShowCellToolTips = true;
        _grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _view.Count)
                return;
            var item = _view[e.RowIndex];
            e.ToolTipText = e.ColumnIndex switch
            {
                5 => DownloadView.TimeTooltip(item),
                3 when item.SpeedLimitKBps > 0 && item.Status != DownloadStatus.Completed
                    => L.F("Tốc độ tối đa riêng cho tệp này: {0}", DownloadView.FormatSpeed(item.SpeedLimitKBps * 1024.0)),
                6 => DownloadView.StatusText(item, QueuePosition(item)),
                _ => "",
            };
        };
        _grid.Paint += PaintEmptyState;
        _grid.CellMouseEnter += (_, e) => SetHoverRow(e.RowIndex);
        _grid.MouseLeave += (_, _) => SetHoverRow(-1);
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < _view.Count && _view[e.RowIndex].Status == DownloadStatus.Completed)
                OpenItem(_view[e.RowIndex]);
        };
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && !_grid.Rows[e.RowIndex].Selected)
            {
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
            }
        };
        _grid.SelectionChanged += (_, _) => UpdateActions();
        _grid.ContextMenuStrip = BuildContextMenu();
    }

    private void SetHoverRow(int row)
    {
        if (row == _hoverRow)
            return;
        var old = _hoverRow;
        _hoverRow = row;
        if (old >= 0 && old < _grid.RowCount)
            _grid.InvalidateRow(old);
        if (row >= 0 && row < _grid.RowCount)
            _grid.InvalidateRow(row);
    }

    private void PaintCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        var g = e.Graphics;
        if (g is null || e.CellStyle is null)
            return;

        if (e.RowIndex < 0)
        {
            e.PaintBackground(e.CellBounds, false);
            if (e.ColumnIndex >= 0)
            {
                var sorted = SortColumnAt(e.ColumnIndex) is { } column && column == _sortColumn;
                TextRenderer.DrawText(g, e.Value + (sorted ? (_sortAscending ? "  ▲" : "  ▼") : ""), Theme.SmallBold,
                    new Rectangle(e.CellBounds.X + S(10), e.CellBounds.Y, e.CellBounds.Width - S(14), e.CellBounds.Height), Theme.TextMuted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            using var pen = new Pen(Theme.Border);
            g.DrawLine(pen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
            e.Handled = true;
            return;
        }
        if (e.RowIndex >= _view.Count || e.ColumnIndex is not (0 or 6))
            return;

        var item = _view[e.RowIndex];
        var selected = (e.State & DataGridViewElementStates.Selected) != 0;
        e.PaintBackground(e.CellBounds, selected);
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = e.CellBounds;

        if (e.ColumnIndex == 0)
        {
            var color = Theme.CategoryColor(item.Category);
            var tile = new RectangleF(bounds.X + S(10), bounds.Y + (bounds.Height - S(34)) / 2f, S(34), S(34));
            Theme.FillRound(g, Theme.Tint(color, 0.87f), tile, S(9));
            Icons.Draw(g, Theme.CategoryIcon(item.Category), RectangleF.Inflate(tile, -S(9), -S(9)), color);

            var textX = (int)tile.Right + S(12);
            var textW = bounds.Right - textX - S(8);
            var host = Uri.TryCreate(item.Request.Url, UriKind.Absolute, out var uri) ? uri.Host : item.Request.Url;
            TextRenderer.DrawText(g, e.Value?.ToString(), Theme.BodySemibold, new Rectangle(textX, bounds.Y + S(8), textW, S(20)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, $"{Theme.CategoryName(item.Category)} · {host}", Theme.Small, new Rectangle(textX, bounds.Y + S(29), textW, S(18)), Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        else
        {
            var color = Theme.StatusColor(item);
            var text = e.Value?.ToString() ?? "";
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            var maxText = bounds.Width - S(46);
            var measured = TextRenderer.MeasureText(g, text, Theme.SmallBold, new Size(maxText, S(24)), flags);
            var pillW = Math.Min(measured.Width, maxText) + S(28);
            var pill = new RectangleF(bounds.X + S(10), bounds.Y + (bounds.Height - S(24)) / 2f, pillW, S(24));
            Theme.FillRound(g, Theme.Tint(color, 0.88f), pill, S(12));
            var dot = S(7);
            using (var brush = new SolidBrush(color))
                g.FillEllipse(brush, pill.X + S(9), pill.Y + (pill.Height - dot) / 2f, dot, dot);
            var textColor = Theme.Readable(color);
            TextRenderer.DrawText(g, text, Theme.SmallBold, Rectangle.Round(new RectangleF(pill.X + S(21), pill.Y, pill.Width - S(27), pill.Height)), textColor, flags);
        }

        g.Restore(state);
        e.Paint(e.CellBounds, DataGridViewPaintParts.Border);
        e.Handled = true;
    }

    private void PaintEmptyState(object? sender, PaintEventArgs e)
    {
        if (_view.Count > 0)
            return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(0, _grid.ColumnHeadersHeight, _grid.Width, _grid.Height - _grid.ColumnHeadersHeight);
        var cy = area.Y + area.Height / 2 - S(40);
        var circle = new RectangleF(area.X + area.Width / 2f - S(32), cy - S(32), S(64), S(64));
        using (var brush = new SolidBrush(Theme.AccentSoft))
            g.FillEllipse(brush, circle);
        var searching = _search.Text.Trim().Length > 0;
        Icons.Draw(g, searching ? IconKind.Search : IconKind.Download, RectangleF.Inflate(circle, -S(18), -S(18)), Theme.Accent);
        var title = searching ? L.T("Không tìm thấy tệp phù hợp") : L.T("Chưa có tệp nào ở đây");
        var hint = searching
            ? L.T("Thử từ khoá khác hoặc xoá ô tìm kiếm.")
            : L.T("Bấm “Thêm link” hoặc tải từ trình duyệt — tệp sẽ xuất hiện tại đây.");
        TextRenderer.DrawText(g, title, Theme.Subtitle, new Rectangle(area.X, (int)circle.Bottom + S(16), area.Width, S(28)), Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        TextRenderer.DrawText(g, hint, Theme.Body, new Rectangle(area.X, (int)circle.Bottom + S(46), area.Width, S(24)), Theme.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = ThemedRenderer.CreateMenu();
        ThemedRenderer.Item(menu, L.T("Mở file"), IconKind.File, (_, _) => ForFirst(OpenItem)).ShortcutKeyDisplayString = "Enter";
        ThemedRenderer.Item(menu, L.T("Mở thư mục"), IconKind.Folder, (_, _) => ForFirst(i => OpenFolder(i.TargetPath))).ShortcutKeyDisplayString = "Ctrl+O";
        var source = ThemedRenderer.Item(menu, L.T("Mở trang nguồn"), IconKind.Link, (_, _) => ForFirst(OpenSource));
        ThemedRenderer.Item(menu, "Copy link", IconKind.Copy, (_, _) => ForFirst(CopyLink)).ShortcutKeyDisplayString = "Ctrl+C";
        ThemedRenderer.Item(menu, L.T("Xuất link ra tệp .txt…"), IconKind.Document, (_, _) => ExportLinks());
        var rename = ThemedRenderer.Item(menu, L.T("Đổi tên…"), IconKind.File, (_, _) => ForFirst(RenameItem));
        rename.ShortcutKeyDisplayString = "F2";
        menu.Items.Add(new ToolStripSeparator());
        var hash = ThemedRenderer.Item(menu, L.T("Kiểm tra mã băm…"), IconKind.Hash, (_, _) => ForFirst(i =>
        {
            using var dialog = new HashForm(i.TargetPath);
            dialog.ShowDialog(this);
        }));
        var scan = ThemedRenderer.Item(menu, L.T("Quét bằng Windows Defender"), IconKind.Shield, (_, _) => ForFirst(i => _ = ScanAsync(i)));
        var extract = ThemedRenderer.Item(menu, L.T("Giải nén tại đây"), IconKind.Archive, (_, _) => ForFirst(i => _ = ExtractAsync(i.TargetPath)));
        var mp4 = ThemedRenderer.Item(menu, L.T("Chuyển sang MP4"), IconKind.Refresh, (_, _) => ForFirst(i => ConvertMedia?.Invoke(i, ".mp4")));
        var mp3 = ThemedRenderer.Item(menu, L.T("Tách nhạc MP3"), IconKind.Refresh, (_, _) => ForFirst(i => ConvertMedia?.Invoke(i, ".mp3")));
        menu.Items.Add(new ToolStripSeparator());
        var startFirst = ThemedRenderer.Item(menu, L.T("Tải trước"), IconKind.Play, (_, _) =>
        {
            var ids = SelectedIds();
            RunLogged(() => _manager.StartFirst(ids));
        });
        var moveUp = ThemedRenderer.Item(menu, L.T("Đưa lên trong hàng chờ"), IconKind.ArrowUp, (_, _) => MoveSelected(up: true));
        moveUp.ShortcutKeyDisplayString = "Ctrl+↑";
        var moveDown = ThemedRenderer.Item(menu, L.T("Đưa xuống trong hàng chờ"), IconKind.ArrowDown, (_, _) => MoveSelected(up: false));
        moveDown.ShortcutKeyDisplayString = "Ctrl+↓";
        var limit = ThemedRenderer.Item(menu, L.T("Giới hạn tốc độ tệp này"), IconKind.Gauge, (_, _) => { });
        foreach (var (kbps, text) in SpeedPresets)
        {
            var option = new ToolStripMenuItem(kbps == 0 ? L.T("Không giới hạn riêng") : text) { Tag = kbps, Padding = new Padding(4, 4, 4, 4) };
            option.Click += (_, _) =>
            {
                var ids = SelectedIds();
                RunLogged(() => _manager.SetSpeedLimit(ids, kbps));
            };
            limit.DropDownItems.Add(option);
        }
        var customLimit = new ToolStripMenuItem(L.T("Mức khác…")) { Padding = new Padding(4, 4, 4, 4) };
        customLimit.Click += (_, _) => ForFirst(first =>
        {
            var ids = SelectedIds();
            using var dialog = new SpeedLimitForm(first.SpeedLimitKBps);
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            var kbps = dialog.KBps;
            RunLogged(() => _manager.SetSpeedLimit(ids, kbps));
        });
        limit.DropDownItems.Add(customLimit);
        if (limit.DropDown is ToolStripDropDownMenu limitMenu)
        {
            limitMenu.ShowCheckMargin = true;
            limitMenu.ShowImageMargin = false;
            limitMenu.Renderer = menu.Renderer;
            limitMenu.BackColor = menu.BackColor;
        }
        var refresh = ThemedRenderer.Item(menu, L.T("Làm mới link…"), IconKind.Link, (_, _) => ForFirst(RefreshLink));
        ThemedRenderer.Item(menu, L.T("Tải lại"), IconKind.Refresh, (_, _) =>
        {
            var ids = SelectedIds();
            RunLogged(() =>
            {
                foreach (var id in ids)
                    _manager.Restart(id);
            });
        });
        ThemedRenderer.Item(menu, L.T("Xoá"), IconKind.Trash, (_, _) => RemoveSelected(), Theme.Danger).ShortcutKeyDisplayString = "Delete";
        menu.Opening += (_, e) =>
        {
            e.Cancel = _grid.SelectedRows.Count == 0;
            var first = SelectedIds().Select(id => _manager.Items.FirstOrDefault(i => i.Id == id)).FirstOrDefault();
            var done = first is { Status: DownloadStatus.Completed } && File.Exists(first.TargetPath);
            source.Enabled = first is not null && SourceUrl(first) is not null;
            hash.Enabled = done;
            scan.Enabled = done && Core.PostProcess.SecurityTools.DefenderPath() is not null;
            extract.Visible = first is not null && Core.PostProcess.ArchiveExtractor.CanExtract(first.TargetPath);
            extract.Enabled = done;
            mp4.Visible = first is not null && Core.PostProcess.MediaConverter.CanConvertToMp4(first.TargetPath);
            mp4.Enabled = done;
            mp3.Visible = first is not null && Core.PostProcess.MediaConverter.CanConvertToMp3(first.TargetPath);
            mp3.Enabled = done;
            rename.Enabled = first is { Status: not DownloadStatus.Downloading };
            startFirst.Visible = SelectedIds().Any(id => _manager.Items.FirstOrDefault(i => i.Id == id) is { Status: not (DownloadStatus.Completed or DownloadStatus.Downloading) });
            var waiting = SelectedIds().Any(id => _manager.Items.FirstOrDefault(i => i.Id == id) is { Status: DownloadStatus.Queued or DownloadStatus.Later or DownloadStatus.Paused or DownloadStatus.Failed });
            moveUp.Visible = moveDown.Visible = waiting;
            limit.Visible = first is not null && first.Status != DownloadStatus.Completed;
            var isPreset = false;
            foreach (ToolStripMenuItem option in limit.DropDownItems)
            {
                if (option.Tag is not int kbps)
                    continue;
                option.Checked = first is not null && kbps == first.SpeedLimitKBps;
                isPreset |= option.Checked;
            }
            customLimit.Checked = first is not null && !isPreset;
            customLimit.Text = customLimit.Checked
                ? L.F("Mức khác… ({0})", DownloadView.FormatSpeed(first!.SpeedLimitKBps * 1024.0))
                : L.T("Mức khác…");
            refresh.Visible = first is not null && first.Status != DownloadStatus.Completed;
            refresh.Enabled = first is { Status: not DownloadStatus.Downloading };
        };
        return menu;
    }

    // ───────────────────────── Hành động ─────────────────────────

    private async Task ScanAsync(DownloadItem item)
    {
        var path = item.TargetPath;
        var result = await Core.PostProcess.SecurityTools.ScanAsync(path, CancellationToken.None);
        if (result == Core.PostProcess.ScanResult.ThreatFound)
        {
            ThreatFound?.Invoke(item);
            return;
        }
        if (result == Core.PostProcess.ScanResult.Clean)
            RunLogged(() => _manager.MarkThreat(item.Id, null));
        var (text, icon) = result switch
        {
            Core.PostProcess.ScanResult.Clean => (L.F("Không phát hiện mối nguy trong “{0}”.", Path.GetFileName(path)), MessageBoxIcon.Information),
            Core.PostProcess.ScanResult.Unavailable => (L.T("Máy này không có Windows Defender."), MessageBoxIcon.Information),
            _ => (L.T("Không quét được tệp. Windows Defender có thể đang tắt hoặc bị chương trình diệt virus khác thay thế."), MessageBoxIcon.Warning),
        };
        MessageBox.Show(this, text, AppTitle, MessageBoxButtons.OK, icon);
    }

    private async Task ExtractAsync(string path)
    {
        try
        {
            var folder = await Task.Run(() => Core.PostProcess.ArchiveExtractor.Extract(path));
            OpenFolder(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            MessageBox.Show(this, L.T("Không giải nén được: ") + e.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Lấy các link http/https từ văn bản (kéo-thả hoặc dán).</summary>
    private static List<string> ExtractLinks(string? text) =>
        (text ?? "").Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().Trim('"', '<', '>'))
            .Where(t => (Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") || Core.Engine.Torrent.TorrentLink.IsMagnet(t))
            .Distinct()
            .Take(Core.Files.BatchLinks.MaxLinks)
            .ToList();

    private static string? DroppedText(IDataObject? data)
    {
        if (data is null)
            return null;
        if (data.GetDataPresent(DataFormats.UnicodeText))
            return data.GetData(DataFormats.UnicodeText) as string;
        return data.GetDataPresent(DataFormats.Text) ? data.GetData(DataFormats.Text) as string : null;
    }

    private async Task AddLinksAsync(List<string> links)
    {
        if (links.Count > 1 && AddMany is { } addMany)
        {
            await addMany(links);
            return;
        }
        foreach (var link in links)
            await _addLink(link);
    }

    /// <summary>Cho phép kéo link từ trình duyệt thả vào bất kỳ đâu trên cửa sổ.</summary>
    private void EnableLinkDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) =>
            e.Effect = ExtractLinks(DroppedText(e.Data)).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        control.DragDrop += async (_, e) =>
        {
            var links = ExtractLinks(DroppedText(e.Data));
            if (links.Count > 0)
                await AddLinksAsync(links);
        };
    }

    private async Task AddLinkAsync()
    {
        var links = PromptLinks();
        if (links.Count > 0)
            await AddLinksAsync(links);
    }

    /// <summary>Hộp "Thêm link tải": một hoặc nhiều link (cách nhau bởi dấu cách), hoặc nhập từ tệp .txt.</summary>
    private List<string> PromptLinks()
    {
        using var dialog = new AddLinkForm(
            hint: L.T("Dán một hoặc nhiều link http/https hoặc magnet, cách nhau bởi dấu cách. Có thể nhập từ tệp .txt."),
            secondaryText: L.T("Nhập từ tệp .txt…"));
        var answer = dialog.ShowDialog(this);
        if (answer == DialogResult.Retry)
            return ImportLinks();
        if (answer != DialogResult.OK)
            return [];
        var links = ExtractLinks(dialog.Link);
        if (links.Count == 0)
            MessageBox.Show(this, L.T("Đường dẫn không hợp lệ."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return links;
    }

    /// <summary>Đọc các link trong một tệp văn bản (mỗi dòng một link, hoặc lẫn trong đoạn văn).</summary>
    private List<string> ImportLinks()
    {
        using var open = new OpenFileDialog
        {
            Title = L.T("Chọn tệp chứa danh sách link"),
            Filter = L.T("Tệp văn bản (*.txt)|*.txt|Mọi tệp (*.*)|*.*"),
        };
        if (open.ShowDialog(this) != DialogResult.OK)
            return [];
        string text;
        try
        {
            if (new FileInfo(open.FileName).Length > 10 * 1024 * 1024)
            {
                MessageBox.Show(this, L.T("Tệp quá lớn (tối đa 10 MB)."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return [];
            }
            text = File.ReadAllText(open.FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, L.T("Không đọc được tệp: ") + e.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return [];
        }
        var links = ExtractLinks(text);
        if (links.Count == 0)
            MessageBox.Show(this, L.T("Tệp này không có link http/https nào."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return links;
    }

    /// <summary>
    /// Link cũ hết hạn: dán link mới, hoặc mở lại trang nguồn và bấm tải như thường — DoMa nhận link đó cho tệp này
    /// và tải tiếp phần còn lại (nếu tệp trên máy chủ vẫn cùng dung lượng).
    /// </summary>
    private void RefreshLink(DownloadItem item)
    {
        var source = SourceUrl(item);
        using var dialog = new AddLinkForm(
            L.T("Làm mới link"),
            source is null
                ? L.T("Dán link mới của tệp này, DoMa sẽ tải tiếp phần còn lại.")
                : L.T("Dán link mới, hoặc mở trang nguồn rồi bấm tải lại như thường. DoMa sẽ tải tiếp phần còn lại."),
            L.T("Tải tiếp"),
            source is null ? null : L.T("Mở trang nguồn"));
        var answer = dialog.ShowDialog(this);
        if (answer == DialogResult.Retry)
        {
            WaitForNewLink?.Invoke(item);
            OpenSource(item);
            return;
        }
        if (answer != DialogResult.OK)
            return;
        var text = dialog.Link;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(this, L.T("Đường dẫn không hợp lệ."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        RunLogged(() =>
        {
            if (!_manager.ReplaceLink(item.Id, item.Request with { Url = uri.AbsoluteUri }))
                MessageBox.Show(this, L.T("Tệp này đang tải hoặc đã tải xong."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private void RenameItem(DownloadItem item)
    {
        using var dialog = new RenameForm(Path.GetFileName(item.TargetPath));
        while (dialog.ShowDialog(this) == DialogResult.OK)
        {
            var error = _manager.Rename(item.Id, dialog.NewName);
            if (error is null)
                return;
            MessageBox.Show(this, error, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RemoveSelected()
    {
        var ids = SelectedIds();
        if (ids.Count == 0)
            return;
        var answer = MessageBox.Show(this, L.T("Xoá luôn tệp trên ổ đĩa?"), L.T("Xoá"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel)
            return;
        RunLogged(() => _manager.Remove(ids, answer == DialogResult.Yes));
    }

    private void RunLogged(Action action) => Task.Run(() =>
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            _log.Write("main", e, null);
        }
    });

    /// <summary>Trang web nơi đã bấm tải (Referrer); nếu không có thì chính link tải. Chỉ nhận http/https.</summary>
    private static string? SourceUrl(DownloadItem item)
    {
        foreach (var candidate in new[] { item.Request.Referrer, item.Request.Url })
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                return uri.AbsoluteUri;
        }
        return null;
    }

    /// <summary>Truy ngược nguồn tải: mở trang đã tải tệp này trong trình duyệt mặc định.</summary>
    private static void OpenSource(DownloadItem item)
    {
        if (SourceUrl(item) is not { } url)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            MessageBox.Show(L.T("Không mở được trình duyệt."), AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void CopyLink(DownloadItem item)
    {
        try
        {
            DoMa.App.Capture.ClipboardWatcher.IgnoreOnce(item.Request.Url);
            Clipboard.SetText(item.Request.Url);
        }
        catch (ExternalException)
        {
        }
    }

    private SortColumn _sortColumn = SortColumn.Added;
    private bool _sortAscending;

    /// <summary>Cột nào sắp xếp được (tốc độ và thời gian còn lại đổi liên tục nên không).</summary>
    private static SortColumn? SortColumnAt(int index) => index switch
    {
        0 => SortColumn.Name,
        1 => SortColumn.Size,
        2 => SortColumn.Progress,
        5 => SortColumn.Time,
        6 => SortColumn.Status,
        _ => null,
    };

    /// <summary>Ghi link của các tệp đã chọn ra một tệp .txt (mỗi dòng một link), nhập lại được bằng "Nhập từ tệp .txt…".</summary>
    private void ExportLinks()
    {
        var ids = SelectedIds().ToHashSet();
        var links = _view.Where(i => ids.Contains(i.Id)).Select(i => SourceLink(i)).Distinct().ToList();
        if (links.Count == 0)
            return;
        using var dialog = new SaveFileDialog
        {
            Title = L.T("Xuất link ra tệp"),
            Filter = L.T("Tệp văn bản (*.txt)|*.txt"),
            FileName = "DoMa-links.txt",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            File.WriteAllLines(dialog.FileName, links);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, L.T("Không ghi được tệp: ") + e.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string SourceLink(DownloadItem item) => item.Request.Url;

    private IReadOnlyDictionary<Guid, int> _queuePositions = new Dictionary<Guid, int>();

    private int? QueuePosition(DownloadItem item) =>
        item.Status == DownloadStatus.Queued && _queuePositions.TryGetValue(item.Id, out var n) ? n : null;

    /// <summary>Đưa các tệp đang chờ đã chọn lên/xuống một bậc trong thứ tự tải.</summary>
    private void MoveSelected(bool up)
    {
        var ids = SelectedIds();
        if (ids.Count == 0)
            return;
        // Move báo Changed nên danh sách tự vẽ lại với thứ tự mới.
        RunLogged(() => _manager.Move(ids, up));
    }

    private List<Guid> SelectedIds() =>
        _grid.SelectedRows.Cast<DataGridViewRow>()
            .Where(r => r.Index < _view.Count)
            .OrderBy(r => r.Index)
            .Select(r => _view[r.Index].Id)
            .ToList();

    private void ForFirst(Action<DownloadItem> action)
    {
        var ids = SelectedIds();
        if (ids.Count == 0)
            return;
        var item = _manager.Items.FirstOrDefault(i => i.Id == ids[0]);
        if (item is not null)
            action(item);
    }

    private void UpdateActions()
    {
        var selected = _grid.SelectedRows.Cast<DataGridViewRow>().Where(r => r.Index < _view.Count).Select(r => _view[r.Index]).ToList();
        _resume.Enabled = selected.Any(i => i.Status != DownloadStatus.Completed);
        _pause.Enabled = selected.Any(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued);
        _remove.Enabled = selected.Count > 0;
    }

    // ───────────────────────── Làm mới ─────────────────────────

    private void RefreshView()
    {
        if (!Visible && !_dirty)
            return;
        var items = _manager.Items;
        _queuePositions = _manager.QueuePositions();
        foreach (var entry in _nav.Items)
        {
            if (entry.Filter is { } filter)
                entry.Count = DownloadView.Count(filter, items);
        }
        _nav.Invalidate();

        var current = _nav.Selected;
        var query = _search.Text.Trim();
        // Mặc định tệp mới thêm gần nhất đứng trên cùng; bấm tiêu đề cột để sắp xếp khác.
        var next = DownloadView.Sort(items, _sortColumn, _sortAscending)
            .Where(i => DownloadView.Matches(current, i))
            .Where(i => query.Length == 0 || Path.GetFileName(i.TargetPath).Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        if (_view.Select(i => i.Id).SequenceEqual(next.Select(i => i.Id)))
        {
            _view = next;
            _grid.Invalidate();
        }
        else
        {
            var selected = SelectedIds().ToHashSet();
            _view = next;
            _grid.RowCount = 0;
            _grid.RowCount = next.Count;
            for (var i = 0; i < next.Count; i++)
                _grid.Rows[i].Selected = selected.Contains(next[i].Id);
            _grid.Invalidate();
        }

        _viewTitle.Text = _nav.SelectedItem?.Text ?? L.T("Tất cả");
        _viewCount.Text = query.Length == 0 ? L.F("{0} tệp", next.Count) : L.F("{0} tệp khớp “{1}”", next.Count, query);

        var downloading = items.Where(i => i.Status == DownloadStatus.Downloading).ToList();
        var totalSpeed = downloading.Sum(i => _manager.SpeedBytesPerSec(i.Id));
        var queued = items.Count(i => i.Status == DownloadStatus.Queued);
        _anyActive = downloading.Count > 0;
        _statusLeft = downloading.Count == 0
            ? (queued > 0 ? L.F("Không có tệp đang tải · {0} đang chờ", queued) : L.T("Sẵn sàng"))
            : L.F("{0} tệp đang tải", downloading.Count) + (totalSpeed > 0 ? $" · {DownloadView.FormatSpeed(totalSpeed)}" : "") + (queued > 0 ? L.F(" · {0} đang chờ", queued) : "");
        _statusRight = L.F("Tổng cộng {0} tệp · {1} đã xong", items.Count, items.Count(i => i.Status == DownloadStatus.Completed));
        if (QuickActions?.Describe() is { Length: > 0 } extra)
            _statusRight = extra + "   |   " + _statusRight;
        _statusBar.Invalidate();
        UpdateActions();
        _dirty = false;
    }
}

/// <summary>DataGridView có bật double-buffer để cuộn và cập nhật không bị nháy.</summary>
public sealed class SmoothGrid : DataGridView
{
    public SmoothGrid()
    {
        DoubleBuffered = true;
    }
}
