using DoMa.App.UI.Controls;
using DoMa.Core.Models;
using DoMa.Core.Scheduling;
using DoMa.Core.Storage;

namespace DoMa.App.UI;

public sealed class SettingsForm : Form
{
    private static readonly Category[] Listed = [Category.Document, Category.Software, Category.Archive, Category.Media];

    private readonly InputBox _root = new(IconKind.Folder);
    private readonly ModernButton _browse = new(L.T("Chọn…"));
    private readonly Dictionary<Category, InputBox> _extensions = Listed.ToDictionary(c => c, c => new InputBox(Theme.CategoryIcon(c)));
    private readonly NumericUpDown _connections = new() { Minimum = 1, Maximum = 32 };
    private readonly NumericUpDown _concurrent = new() { Minimum = 1, Maximum = 10 };
    private readonly NumericUpDown _speed = new() { Minimum = 0, Maximum = 1_000_000, Increment = 256, ThousandsSeparator = true };
    private readonly InputBox _proxy = new(IconKind.Link) { PlaceholderText = L.T("Để trống = theo Windows") };
    private readonly NumericUpDown _autoClear = new() { Minimum = 0, Maximum = 3650 };
    private readonly ModernButton _backup = new(L.T("Sao lưu…"), IconKind.Download);
    private readonly ModernButton _restore = new(L.T("Khôi phục…"), IconKind.Refresh);

    /// <summary>Việc làm khi bấm "Sao lưu…" / "Khôi phục…" (do nơi mở hộp thoại gán).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? Backup { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? Restore { get; set; }

    private readonly ToggleSwitch _schedule = new(L.T("Tải theo lịch hằng ngày"), L.T("Tự bắt đầu các tệp “Chờ tải sau” vào giờ hẹn, tự đưa về hàng chờ khi tới giờ dừng"));
    private readonly InputBox _scheduleStart = new(IconKind.Clock) { PlaceholderText = "23:00" };
    private readonly InputBox _scheduleStop = new(IconKind.Clock) { PlaceholderText = L.T("Để trống = không dừng") };
    private static readonly (string Value, string Text)[] Appearances = [("System", L.T("Theo Windows")), ("Light", L.T("Sáng")), ("Dark", L.T("Tối"))];
    private static readonly (string Value, string Text)[] Languages = [("auto", L.T("Theo Windows")), ("vi", "Tiếng Việt"), ("en", "English")];
    private readonly ComboBox _appearance = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly ToggleSwitch _warn = new(L.T("Cảnh báo trước khi tải"), L.T("Báo khi tệp là chương trình, giả dạng đuôi khác hoặc tải từ nguồn đáng ngờ"));
    private readonly ToggleSwitch _scan = new(L.T("Quét bằng Windows Defender"), L.T("Quét mỗi tệp vừa tải xong; báo ngay nếu phát hiện mối nguy"));
    private readonly ToggleSwitch _mp4 = new(L.T("Tự chuyển video .ts sang .mp4"), L.T("Video tải từ luồng (HLS) được đổi sang .mp4 để mở trên mọi máy, không giảm chất lượng"));
    private readonly ToggleSwitch _extract = new(L.T("Tự giải nén tệp .zip"), L.T("Giải nén vào thư mục cùng tên, cạnh tệp vừa tải"));
    private readonly ToggleSwitch _startup = new(L.T("Chạy cùng Windows"), L.T("Tự khởi động và ẩn dưới khay hệ thống"));
    private readonly ToggleSwitch _network = new(L.T("Tự tải tiếp khi có mạng lại"), L.T("Tệp bị lỗi vì mất mạng sẽ tự chạy tiếp, không cần bấm lại"));
    private readonly ToggleSwitch _clipboard = new(L.T("Bắt link khi copy"), L.T("Copy một link tải (.zip, .exe, .mp4…) là DoMa đề nghị tải giúp"));
    private readonly ToggleSwitch _notify = new(L.T("Thông báo khi tải xong"), L.T("Hiện thông báo ở góc màn hình khi một tệp tải xong"));
    private readonly ToggleSwitch _updates = new(L.T("Tự động kiểm tra cập nhật"), L.T("Báo khi có phiên bản DoMa mới trên GitHub"));
    private readonly ModernButton _save = new(L.T("Lưu"), IconKind.Check, ButtonKind.Primary) { DialogResult = DialogResult.OK };
    private readonly ModernButton _cancel = new(L.T("Huỷ"), IconKind.None, ButtonKind.Ghost) { DialogResult = DialogResult.Cancel };
    private readonly Settings _source;

    public SettingsForm(Settings current)
    {
        _source = current;
        Text = L.T("Cài đặt");
        StartPosition = FormStartPosition.CenterParent;
        DialogParts.ApplyBase(this, new Size(640, 712));
        AcceptButton = _save;
        CancelButton = _cancel;

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, AutoScroll = true };
        Theme.StyleScrollbars(body);
        var x = S(24);
        var w = ClientSize.Width - S(48);
        var y = S(4);

        void Section(string title)
        {
            var label = new Label { Text = title.ToUpperInvariant(), Font = Theme.SmallBold, ForeColor = Theme.Accent, AutoSize = true, Location = new Point(x, y) };
            body.Controls.Add(label);
            y += S(26);
        }

        void Field(string label, Control field, Control? action = null, int fieldWidth = 0)
        {
            var caption = DialogParts.FieldLabel(label);
            caption.Location = new Point(x, y);
            body.Controls.Add(caption);
            y += S(24);
            var width = fieldWidth > 0 ? fieldWidth : w;
            if (action is not null)
            {
                if (action is ModernButton button)
                    button.AutoSize = false;
                action.Size = action.PreferredSize with { Height = S(36) };
                action.Location = new Point(x + w - action.Width, y);
                body.Controls.Add(action);
                width = w - action.Width - S(8);
            }
            field.SetBounds(x, y, width, S(36));
            body.Controls.Add(field);
            y += S(48);
        }

        Section(L.T("Lưu trữ"));
        Field(L.T("Thư mục gốc"), _root, _browse);

        Section(L.T("Phân loại theo đuôi file"));
        foreach (var category in Listed)
        {
            var caption = DialogParts.FieldLabel(Theme.CategoryName(category));
            caption.Location = new Point(x, y + S(9));
            _extensions[category].SetBounds(x + S(100), y, w - S(100), S(36));
            body.Controls.Add(caption);
            body.Controls.Add(_extensions[category]);
            y += S(44);
        }
        var hint = DialogParts.Hint(L.T("Các đuôi cách nhau bằng dấu phẩy; đuôi không có trong danh sách rơi vào Khác."));
        hint.Location = new Point(x + S(101), y - S(2));
        body.Controls.Add(hint);
        y += S(34);

        Section(L.T("Hiệu suất"));
        var half = (w - S(16)) / 2;
        StyleNumber(_connections);
        StyleNumber(_concurrent);
        var connCaption = DialogParts.FieldLabel(L.T("Số kết nối mỗi file (1–32)"));
        var concCaption = DialogParts.FieldLabel(L.T("Số file tải cùng lúc (1–10)"));
        connCaption.Location = new Point(x, y);
        concCaption.Location = new Point(x + half + S(16), y);
        y += S(24);
        _connections.SetBounds(x, y, half, S(30));
        _concurrent.SetBounds(x + half + S(16), y, half, S(30));
        body.Controls.AddRange([connCaption, concCaption, _connections, _concurrent]);
        y += S(50);

        Section(L.T("Mạng"));
        Field(L.T("Proxy (ví dụ http://127.0.0.1:8080 hoặc socks5://127.0.0.1:1080)"), _proxy);

        Section(L.T("Tốc độ và lịch tải"));
        StyleNumber(_speed);
        var speedCaption = DialogParts.FieldLabel(L.T("Tốc độ tối đa, KB/s (0 = không giới hạn)"));
        speedCaption.Location = new Point(x, y);
        y += S(24);
        _speed.SetBounds(x, y, half, S(30));
        body.Controls.AddRange([speedCaption, _speed]);
        y += S(46);
        _schedule.SetBounds(x, y, w, S(46));
        body.Controls.Add(_schedule);
        y += S(52);
        var startCaption = DialogParts.FieldLabel(L.T("Bắt đầu lúc (giờ:phút)"));
        var stopCaption = DialogParts.FieldLabel(L.T("Dừng lúc"));
        startCaption.Location = new Point(x, y);
        stopCaption.Location = new Point(x + half + S(16), y);
        y += S(24);
        _scheduleStart.SetBounds(x, y, half, S(36));
        _scheduleStop.SetBounds(x + half + S(16), y, half, S(36));
        body.Controls.AddRange([startCaption, stopCaption, _scheduleStart, _scheduleStop]);
        y += S(54);
        _schedule.CheckedChanged += (_, _) => _scheduleStart.Enabled = _scheduleStop.Enabled = _schedule.Checked;

        Section(L.T("Bảo mật và sau khi tải"));
        _warn.SetBounds(x, y, w, S(46));
        y += S(50);
        _scan.SetBounds(x, y, w, S(46));
        y += S(50);
        _extract.SetBounds(x, y, w, S(46));
        y += S(50);
        _mp4.SetBounds(x, y, w, S(46));
        y += S(56);
        body.Controls.AddRange([_warn, _scan, _extract, _mp4]);

        Section(L.T("Giao diện"));
        var appearanceCaption = DialogParts.FieldLabel(L.T("Chế độ màu (áp dụng khi mở lại DoMa)"));
        appearanceCaption.Location = new Point(x, y);
        y += S(24);
        _appearance.Font = Theme.Body;
        _appearance.BackColor = Theme.Surface;
        _appearance.ForeColor = Theme.Text;
        _appearance.Items.AddRange(Appearances.Select(a => (object)a.Text).ToArray());
        _appearance.SetBounds(x, y, half, S(30));
        body.Controls.AddRange([appearanceCaption, _appearance]);
        y += S(50);
        var languageCaption = DialogParts.FieldLabel(L.T("Ngôn ngữ / Language (áp dụng khi mở lại DoMa)"));
        languageCaption.Location = new Point(x, y);
        y += S(24);
        _language.Font = Theme.Body;
        _language.BackColor = Theme.Surface;
        _language.ForeColor = Theme.Text;
        _language.Items.AddRange(Languages.Select(a => (object)a.Text).ToArray());
        _language.SetBounds(x, y, half, S(30));
        body.Controls.AddRange([languageCaption, _language]);
        y += S(50);

        Section(L.T("Hệ thống"));
        _startup.SetBounds(x, y, w, S(46));
        y += S(50);
        _notify.SetBounds(x, y, w, S(46));
        y += S(50);
        _updates.SetBounds(x, y, w, S(46));
        y += S(50);
        _clipboard.SetBounds(x, y, w, S(46));
        y += S(50);
        _network.SetBounds(x, y, w, S(46));
        y += S(52);
        body.Controls.AddRange([_startup, _notify, _updates, _clipboard, _network]);

        Section(L.T("Danh sách và sao lưu"));
        StyleNumber(_autoClear);
        var clearCaption = DialogParts.FieldLabel(L.T("Tự bỏ tệp đã xong khỏi danh sách sau (ngày, 0 = giữ mãi)"));
        clearCaption.Location = new Point(x, y);
        y += S(24);
        _autoClear.SetBounds(x, y, half, S(30));
        body.Controls.AddRange([clearCaption, _autoClear]);
        y += S(46);
        var backupHint = DialogParts.Hint(L.T("Sao lưu cài đặt, danh sách tải và kênh đang theo dõi ra một tệp, để khôi phục khi cài lại máy."));
        backupHint.Location = new Point(x, y);
        body.Controls.Add(backupHint);
        y += S(26);
        _backup.AutoSize = false;
        _restore.AutoSize = false;
        _backup.SetBounds(x, y, S(150), S(36));
        _restore.SetBounds(x + S(162), y, S(150), S(36));
        _backup.Click += (_, _) => Backup?.Invoke();
        _restore.Click += (_, _) => Restore?.Invoke();
        body.Controls.AddRange([_backup, _restore]);
        y += S(52);
        body.AutoScrollMinSize = new Size(0, y);

        Controls.Add(body);
        Controls.Add(DialogParts.Footer(this, _save, _cancel));
        Controls.Add(DialogParts.Header(this, IconKind.Settings, Theme.Accent, L.T("Cài đặt"), L.T("Thư mục lưu, phân loại file và cách DoMa tải xuống.")));

        _root.Text = current.RootFolder;
        foreach (var category in Listed)
            _extensions[category].Text = string.Join(", ", current.Extensions.TryGetValue(category, out var list) ? list : []);
        _connections.Value = current.ConnectionsPerFile;
        _concurrent.Value = Math.Clamp(current.MaxConcurrent, 1, 10);
        _startup.Checked = current.RunAtStartup;
        _notify.Checked = current.NotifyOnComplete;
        _clipboard.Checked = current.WatchClipboard;
        _network.Checked = current.AutoResumeOnNetwork;
        _updates.Checked = current.AutoCheckUpdates;
        _appearance.SelectedIndex = Math.Max(0, Array.FindIndex(Appearances, a => string.Equals(a.Value, current.Appearance, StringComparison.OrdinalIgnoreCase)));
        _language.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, a => string.Equals(a.Value, current.Language, StringComparison.OrdinalIgnoreCase)));
        _warn.Checked = current.WarnBeforeDownload;
        _scan.Checked = current.ScanAfterDownload;
        _extract.Checked = current.AutoExtractZip;
        _mp4.Checked = current.ConvertTsToMp4;
        _speed.Value = Math.Clamp(current.SpeedLimitKBps, 0, 1_000_000);
        _proxy.Text = current.Proxy;
        _autoClear.Value = Math.Clamp(current.AutoClearDays, 0, 3650);
        _schedule.Checked = current.ScheduleEnabled;
        _scheduleStart.Text = current.ScheduleStart;
        _scheduleStop.Text = current.ScheduleStop ?? "";
        _scheduleStart.Enabled = _scheduleStop.Enabled = _schedule.Checked;

        _browse.Click += (_, _) => Browse();
        _save.Click += (_, _) =>
        {
            if (!Path.IsPathRooted(_root.Text.Trim()))
            {
                MessageBox.Show(this, L.T("Thư mục gốc không hợp lệ."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _root.Box.Focus();
                return;
            }
            if (!Core.Engine.ProxyConfig.IsValid(_proxy.Text))
            {
                MessageBox.Show(this, L.T("Proxy không hợp lệ. Hãy nhập dạng máy:cổng, ví dụ 127.0.0.1:8080 hoặc socks5://127.0.0.1:1080."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _proxy.Box.Focus();
                return;
            }
            if (_schedule.Checked && !ScheduleClock.TryParse(_scheduleStart.Text, out _))
            {
                MessageBox.Show(this, L.T("Giờ bắt đầu không hợp lệ. Hãy nhập dạng 23:00."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _scheduleStart.Box.Focus();
                return;
            }
            if (_schedule.Checked && _scheduleStop.Text.Trim().Length > 0 && !ScheduleClock.TryParse(_scheduleStop.Text, out _))
            {
                MessageBox.Show(this, L.T("Giờ dừng không hợp lệ. Hãy nhập dạng 07:00 hoặc để trống."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _scheduleStop.Box.Focus();
            }
        };
    }

    private int S(int v) => Theme.Scale(this, v);

    private static void StyleNumber(NumericUpDown box)
    {
        box.Font = Theme.Body;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.BackColor = Theme.Surface;
        box.ForeColor = Theme.Text;
        box.TextAlign = HorizontalAlignment.Left;
    }

    public Settings Result()
    {
        var extensions = _source.Extensions.ToDictionary(p => p.Key, p => p.Value);
        foreach (var category in Listed)
            extensions[category] = ParseExtensions(_extensions[category].Text);
        // Bắt đầu từ cài đặt hiện tại để giữ những mục không có trong hộp này (vd. chất lượng video đã chọn lần trước).
        var result = _source.Clone();
        Apply(result, extensions);
        return result;
    }

    private void Apply(Settings s, Dictionary<Category, string[]> extensions)
    {
        {
            s.RootFolder = _root.Text.Trim();
            s.Extensions = extensions;
            s.ConnectionsPerFile = (int)_connections.Value;
            s.MaxConcurrent = (int)_concurrent.Value;
            s.RunAtStartup = _startup.Checked;
            s.NotifyOnComplete = _notify.Checked;
            s.WatchClipboard = _clipboard.Checked;
            s.AutoResumeOnNetwork = _network.Checked;
            s.AutoCheckUpdates = _updates.Checked;
            s.SpeedLimitKBps = (int)_speed.Value;
            s.WarnBeforeDownload = _warn.Checked;
            s.ScanAfterDownload = _scan.Checked;
            s.AutoExtractZip = _extract.Checked;
            s.ConvertTsToMp4 = _mp4.Checked;
            s.Appearance = Appearances[Math.Max(0, _appearance.SelectedIndex)].Value;
            s.Language = Languages[Math.Max(0, _language.SelectedIndex)].Value;
            s.ScheduleEnabled = _schedule.Checked;
            s.ScheduleStart = _scheduleStart.Text.Trim().Length > 0 ? _scheduleStart.Text.Trim() : _source.ScheduleStart;
            s.ScheduleStop = _scheduleStop.Text.Trim().Length > 0 ? _scheduleStop.Text.Trim() : null;
            s.Proxy = _proxy.Text.Trim();
        s.AutoClearDays = (int)_autoClear.Value;
        }
    }

    public static string[] ParseExtensions(string text) =>
        text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().TrimStart('.').ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToArray();

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _root.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _root.Text = dialog.SelectedPath;
    }
}
