using DoMa.App.UI.Controls;
using DoMa.Core.Models;
using DoMa.Core.Scheduling;
using DoMa.Core.Storage;

namespace DoMa.App.UI;

public sealed class SettingsForm : Form
{
    private static readonly Category[] Listed = [Category.Document, Category.Software, Category.Archive];

    private readonly InputBox _root = new(IconKind.Folder);
    private readonly ModernButton _browse = new("Chọn…");
    private readonly Dictionary<Category, InputBox> _extensions = Listed.ToDictionary(c => c, c => new InputBox(Theme.CategoryIcon(c)));
    private readonly NumericUpDown _connections = new() { Minimum = 1, Maximum = 32 };
    private readonly NumericUpDown _concurrent = new() { Minimum = 1, Maximum = 10 };
    private readonly NumericUpDown _speed = new() { Minimum = 0, Maximum = 1_000_000, Increment = 256, ThousandsSeparator = true };
    private readonly ToggleSwitch _schedule = new("Tải theo lịch hằng ngày", "Tự bắt đầu các tệp “Chờ tải sau” vào giờ hẹn, tự đưa về hàng chờ khi tới giờ dừng");
    private readonly InputBox _scheduleStart = new(IconKind.Clock) { PlaceholderText = "23:00" };
    private readonly InputBox _scheduleStop = new(IconKind.Clock) { PlaceholderText = "Để trống = không dừng" };
    private readonly ToggleSwitch _startup = new("Chạy cùng Windows", "Tự khởi động và ẩn dưới khay hệ thống");
    private readonly ToggleSwitch _notify = new("Thông báo khi tải xong", "Hiện thông báo ở góc màn hình khi một tệp tải xong");
    private readonly ToggleSwitch _updates = new("Tự động kiểm tra cập nhật", "Báo khi có phiên bản DoMa mới trên GitHub");
    private readonly ModernButton _save = new("Lưu", IconKind.Check, ButtonKind.Primary) { DialogResult = DialogResult.OK };
    private readonly ModernButton _cancel = new("Huỷ", IconKind.None, ButtonKind.Ghost) { DialogResult = DialogResult.Cancel };
    private readonly Settings _source;

    public SettingsForm(Settings current)
    {
        _source = current;
        Text = "Cài đặt";
        StartPosition = FormStartPosition.CenterParent;
        DialogParts.ApplyBase(this, new Size(640, 712));
        AcceptButton = _save;
        CancelButton = _cancel;

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, AutoScroll = true };
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

        Section("Lưu trữ");
        Field("Thư mục gốc", _root, _browse);

        Section("Phân loại theo đuôi file");
        foreach (var category in Listed)
        {
            var caption = DialogParts.FieldLabel(Theme.CategoryName(category));
            caption.Location = new Point(x, y + S(9));
            _extensions[category].SetBounds(x + S(100), y, w - S(100), S(36));
            body.Controls.Add(caption);
            body.Controls.Add(_extensions[category]);
            y += S(44);
        }
        var hint = DialogParts.Hint("Các đuôi cách nhau bằng dấu phẩy; đuôi không có trong danh sách rơi vào Khác.");
        hint.Location = new Point(x + S(101), y - S(2));
        body.Controls.Add(hint);
        y += S(34);

        Section("Hiệu suất");
        var half = (w - S(16)) / 2;
        StyleNumber(_connections);
        StyleNumber(_concurrent);
        var connCaption = DialogParts.FieldLabel("Số kết nối mỗi file (1–32)");
        var concCaption = DialogParts.FieldLabel("Số file tải cùng lúc (1–10)");
        connCaption.Location = new Point(x, y);
        concCaption.Location = new Point(x + half + S(16), y);
        y += S(24);
        _connections.SetBounds(x, y, half, S(30));
        _concurrent.SetBounds(x + half + S(16), y, half, S(30));
        body.Controls.AddRange([connCaption, concCaption, _connections, _concurrent]);
        y += S(50);

        Section("Tốc độ và lịch tải");
        StyleNumber(_speed);
        var speedCaption = DialogParts.FieldLabel("Tốc độ tối đa, KB/s (0 = không giới hạn)");
        speedCaption.Location = new Point(x, y);
        y += S(24);
        _speed.SetBounds(x, y, half, S(30));
        body.Controls.AddRange([speedCaption, _speed]);
        y += S(46);
        _schedule.SetBounds(x, y, w, S(46));
        body.Controls.Add(_schedule);
        y += S(52);
        var startCaption = DialogParts.FieldLabel("Bắt đầu lúc (giờ:phút)");
        var stopCaption = DialogParts.FieldLabel("Dừng lúc");
        startCaption.Location = new Point(x, y);
        stopCaption.Location = new Point(x + half + S(16), y);
        y += S(24);
        _scheduleStart.SetBounds(x, y, half, S(36));
        _scheduleStop.SetBounds(x + half + S(16), y, half, S(36));
        body.Controls.AddRange([startCaption, stopCaption, _scheduleStart, _scheduleStop]);
        y += S(54);
        _schedule.CheckedChanged += (_, _) => _scheduleStart.Enabled = _scheduleStop.Enabled = _schedule.Checked;

        Section("Hệ thống");
        _startup.SetBounds(x, y, w, S(46));
        y += S(50);
        _notify.SetBounds(x, y, w, S(46));
        y += S(50);
        _updates.SetBounds(x, y, w, S(46));
        y += S(52);
        body.Controls.AddRange([_startup, _notify, _updates]);
        body.AutoScrollMinSize = new Size(0, y);

        Controls.Add(body);
        Controls.Add(DialogParts.Footer(this, _save, _cancel));
        Controls.Add(DialogParts.Header(this, IconKind.Settings, Theme.Accent, "Cài đặt", "Thư mục lưu, phân loại file và cách DoMa tải xuống."));

        _root.Text = current.RootFolder;
        foreach (var category in Listed)
            _extensions[category].Text = string.Join(", ", current.Extensions.TryGetValue(category, out var list) ? list : []);
        _connections.Value = current.ConnectionsPerFile;
        _concurrent.Value = Math.Clamp(current.MaxConcurrent, 1, 10);
        _startup.Checked = current.RunAtStartup;
        _notify.Checked = current.NotifyOnComplete;
        _updates.Checked = current.AutoCheckUpdates;
        _speed.Value = Math.Clamp(current.SpeedLimitKBps, 0, 1_000_000);
        _schedule.Checked = current.ScheduleEnabled;
        _scheduleStart.Text = current.ScheduleStart;
        _scheduleStop.Text = current.ScheduleStop ?? "";
        _scheduleStart.Enabled = _scheduleStop.Enabled = _schedule.Checked;

        _browse.Click += (_, _) => Browse();
        _save.Click += (_, _) =>
        {
            if (!Path.IsPathRooted(_root.Text.Trim()))
            {
                MessageBox.Show(this, "Thư mục gốc không hợp lệ.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _root.Box.Focus();
                return;
            }
            if (_schedule.Checked && !ScheduleClock.TryParse(_scheduleStart.Text, out _))
            {
                MessageBox.Show(this, "Giờ bắt đầu không hợp lệ. Hãy nhập dạng 23:00.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _scheduleStart.Box.Focus();
                return;
            }
            if (_schedule.Checked && _scheduleStop.Text.Trim().Length > 0 && !ScheduleClock.TryParse(_scheduleStop.Text, out _))
            {
                MessageBox.Show(this, "Giờ dừng không hợp lệ. Hãy nhập dạng 07:00 hoặc để trống.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        return new Settings
        {
            RootFolder = _root.Text.Trim(),
            Extensions = extensions,
            ConnectionsPerFile = (int)_connections.Value,
            MaxConcurrent = (int)_concurrent.Value,
            RunAtStartup = _startup.Checked,
            NotifyOnComplete = _notify.Checked,
            AutoCheckUpdates = _updates.Checked,
            SpeedLimitKBps = (int)_speed.Value,
            ScheduleEnabled = _schedule.Checked,
            ScheduleStart = _scheduleStart.Text.Trim().Length > 0 ? _scheduleStart.Text.Trim() : _source.ScheduleStart,
            ScheduleStop = _scheduleStop.Text.Trim().Length > 0 ? _scheduleStop.Text.Trim() : null,
        };
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
