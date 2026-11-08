using DoMa.App.UI.Controls;
using DoMa.Core.Channels;
using DoMa.Core.Models;

namespace DoMa.App.UI;

/// <summary>Danh sách kênh/playlist YouTube đang theo dõi: bỏ theo dõi, hoặc kiểm tra video mới ngay.</summary>
public sealed class ChannelsForm : Form
{
    private readonly ListView _list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        BorderStyle = BorderStyle.None,
        MultiSelect = true,
    };
    private readonly ModernButton _remove = new("Bỏ theo dõi", IconKind.Trash);
    private readonly ModernButton _check = new("Kiểm tra ngay", IconKind.Refresh, ButtonKind.Primary);
    private readonly Label _status = new() { AutoSize = true };

    public ChannelsForm(IReadOnlyList<FollowedChannel> channels)
    {
        Text = "Kênh YouTube đang theo dõi";
        StartPosition = FormStartPosition.CenterParent;
        DialogParts.ApplyBase(this, new Size(640, 420));
        int S(int v) => Theme.Scale(this, v);
        var close = new ModernButton("Đóng", IconKind.None, ButtonKind.Ghost) { DialogResult = DialogResult.Cancel };
        CancelButton = close;

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(S(24), S(4), S(24), S(8)) };
        var frame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Border, Padding = new Padding(1) };
        _list.Dock = DockStyle.Fill;
        _list.BackColor = Theme.Surface;
        _list.ForeColor = Theme.Text;
        _list.Font = Theme.Body;
        _list.Columns.Add("Kênh / playlist", S(300));
        _list.Columns.Add("Chất lượng", S(130));
        _list.Columns.Add("Kiểm tra lần cuối", S(150));
        foreach (var c in channels)
        {
            var quality = MediaQualities.For(null).FirstOrDefault(q => q.Value == c.Quality).Label ?? c.Quality;
            var last = c.LastCheck?.ToLocalTime().ToString("HH:mm dd/MM", System.Globalization.CultureInfo.InvariantCulture) ?? "chưa";
            _list.Items.Add(new ListViewItem([c.Title, quality, last]) { Tag = c.Url, ToolTipText = c.Url });
        }
        Theme.StyleScrollbars(_list);
        frame.Controls.Add(_list);
        _status.Font = Theme.Small;
        _status.ForeColor = Theme.TextSecondary;
        _status.Dock = DockStyle.Bottom;
        _status.Padding = new Padding(0, S(6), 0, 0);
        _status.Text = channels.Count == 0
            ? "Chưa theo dõi kênh nào. Mở trang kênh YouTube, bấm chuột phải › “Tải cả playlist/kênh này bằng DoMa…” rồi tích “Theo dõi”."
            : "DoMa kiểm tra mỗi 3 giờ khi đang chạy, có video mới thì tự tải vào thư mục Video.";
        _status.MaximumSize = new Size(S(590), 0);
        body.Controls.Add(frame);
        body.Controls.Add(_status);

        Controls.Add(body);
        Controls.Add(DialogParts.Footer(this, close, _check, _remove));
        Controls.Add(DialogParts.Header(this, IconKind.Refresh, Theme.Accent, Text, "Video mới của các kênh này được tải tự động."));

        _remove.Enabled = false;
        _check.Enabled = channels.Count > 0;
        _list.SelectedIndexChanged += (_, _) => _remove.Enabled = _list.SelectedItems.Count > 0;
        _remove.Click += (_, _) =>
        {
            foreach (ListViewItem item in _list.SelectedItems)
            {
                Removed.Add((string)item.Tag!);
                _list.Items.Remove(item);
            }
            _check.Enabled = _list.Items.Count > 0;
        };
        _check.Click += async (_, _) =>
        {
            if (CheckNow is null)
                return;
            _check.Enabled = false;
            _status.Text = "Đang kiểm tra…";
            var added = await CheckNow();
            if (IsDisposed)
                return;
            _status.Text = added > 0 ? $"Đã thêm {added} video mới vào danh sách." : "Chưa có video mới.";
            _check.Enabled = _list.Items.Count > 0;
        };
    }

    /// <summary>Link các kênh người dùng đã bỏ theo dõi.</summary>
    public HashSet<string> Removed { get; } = [];

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Task<int>>? CheckNow { get; set; }
}
