using DoMa.App.UI.Controls;
using DoMa.Core.Models;
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

        _browse.Click += (_, _) => Browse();
        _save.Click += (_, _) =>
        {
            if (!Path.IsPathRooted(_root.Text.Trim()))
            {
                MessageBox.Show(this, "Thư mục gốc không hợp lệ.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                _root.Box.Focus();
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
