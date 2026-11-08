using DoMa.App.UI.Controls;
using DoMa.Core.Files;
using DoMa.Core.Models;

namespace DoMa.App.UI;

/// <summary>
/// Hộp "Tải nhiều link": liệt kê các link lấy từ một trang (hoặc dán vào), lọc theo loại,
/// tích chọn rồi tải một lượt. Mặc định chọn sẵn các link trỏ tới tệp, bỏ qua trang web thường.
/// </summary>
public sealed class BatchForm : Form
{
    private static readonly (LinkKind Kind, string Text)[] Kinds =
    [
        (LinkKind.Document, "Tài liệu"),
        (LinkKind.Software, "Phần mềm"),
        (LinkKind.Archive, "Nén"),
        (LinkKind.Media, "Video, nhạc"),
        (LinkKind.Other, "Trang khác"),
    ];

    private readonly List<BatchLink> _links;
    private readonly HashSet<string> _checked;
    private readonly Dictionary<LinkKind, CheckBox> _filters = [];
    private readonly InputBox _search = new(IconKind.Search) { PlaceholderText = "Lọc theo tên tệp hoặc tên miền" };
    private readonly CheckBox _all = new() { Text = "Chọn tất cả", AutoSize = true };
    private readonly Label _count = new() { AutoSize = true };
    private readonly ListView _list = new()
    {
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.None,
        BorderStyle = BorderStyle.None,
        MultiSelect = true,
        ShowItemToolTips = true,
    };
    private readonly ModernButton _now = new("Tải ngay", IconKind.Download, ButtonKind.Primary);
    private readonly ModernButton _later = new("Tải sau", IconKind.Clock);
    private readonly ModernButton _cancel = new("Huỷ", IconKind.None, ButtonKind.Ghost) { DialogResult = DialogResult.Cancel };
    private List<BatchLink> _visible = [];
    private bool _updating;

    private readonly MediaChoice? _media;

    /// <param name="media">Có video từ trang (YouTube…): hiện thêm dòng chọn chất lượng, mở sẵn lựa chọn lần trước.</param>
    private readonly CheckBox? _follow;

    /// <param name="follow">Có chữ: hiện ô "theo dõi" (playlist/kênh YouTube) với chữ này.</param>
    public BatchForm(IReadOnlyList<BatchLink> links, string source, MediaOptions? media = null, string? title = null, string? hint = null, string? follow = null, bool checkAll = true)
    {
        _links = [.. links];
        _checked = checkAll ? _links.Where(l => l.IsFile).Select(l => l.Url).ToHashSet() : [];
        Text = "Tải nhiều link";
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        DialogParts.ApplyBase(this, new Size(720, 560));
        ShowInTaskbar = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(S(560), S(420));
        CancelButton = _cancel;

        var top = new Panel { Dock = DockStyle.Top, Height = S(92), BackColor = Theme.Surface };
        var x = S(24);
        foreach (var (kind, text) in Kinds)
        {
            var count = _links.Count(l => l.Kind == kind);
            var box = new CheckBox
            {
                Text = $"{text} ({count})",
                AutoSize = true,
                Checked = kind != LinkKind.Other && count > 0,
                Enabled = count > 0,
                ForeColor = Theme.Text,
                Font = Theme.Body,
                Location = new Point(x, S(6)),
            };
            box.CheckedChanged += (_, _) => Refill();
            top.Controls.Add(box);
            _filters[kind] = box;
            x += box.PreferredSize.Width + S(14);
        }
        _search.SetBounds(S(24), S(40), S(320), S(36));
        _search.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _search.Box.TextChanged += (_, _) => Refill();
        _all.ForeColor = Theme.Text;
        _all.Font = Theme.Body;
        _all.Location = new Point(S(360), S(48));
        _all.CheckedChanged += (_, _) => ToggleAll();
        _count.ForeColor = Theme.TextSecondary;
        _count.Font = Theme.Small;
        _count.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        top.Controls.AddRange([_search, _all, _count]);
        top.Resize += (_, _) => _count.Location = new Point(top.Width - _count.Width - S(24), S(50));

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(S(24), S(4), S(24), S(12)) };
        var frame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Border, Padding = new Padding(1) };
        _list.Dock = DockStyle.Fill;
        _list.BackColor = Theme.Surface;
        _list.ForeColor = Theme.Text;
        _list.Font = Theme.Body;
        _list.Columns.Add("Tên tệp", 300);
        _list.Columns.Add("Loại", 110);
        _list.Columns.Add("Nguồn", 180);
        _list.ItemChecked += (_, e) =>
        {
            if (_updating || e.Item.Tag is not BatchLink link)
                return;
            if (e.Item.Checked)
                _checked.Add(link.Url);
            else
                _checked.Remove(link.Url);
            UpdateCount();
        };
        _list.Resize += (_, _) => FitColumns();
        Theme.StyleScrollbars(_list);
        frame.Controls.Add(_list);
        body.Controls.Add(frame);

        Controls.Add(body);
        Controls.Add(top);
        if (media is not null)
        {
            var strip = new Panel { Dock = DockStyle.Bottom, Height = S(48), BackColor = Theme.Surface, Padding = new Padding(S(24), S(2), S(24), S(10)) };
            _media = new MediaChoice(media, null) { Dock = DockStyle.Fill };
            strip.Controls.Add(_media);
            if (follow is not null)
            {
                strip.Height += S(30);
                _follow = new CheckBox { Text = follow, AutoSize = true, Font = Theme.Body, ForeColor = Theme.Text, Dock = DockStyle.Bottom };
                _follow.CheckedChanged += (_, _) => UpdateCount();
                strip.Controls.Add(_follow);
            }
            Controls.Add(strip);
        }
        Controls.Add(DialogParts.Footer(this, _now, _later, _cancel));
        Controls.Add(DialogParts.Header(this, IconKind.Link, Theme.Accent, title ?? "Tải nhiều link",
            hint ?? $"{_links.Count} link{(source.Length > 0 ? " từ " + source : "")}. Tích chọn các tệp muốn tải."));

        _now.Click += (_, _) => Finish(true);
        _later.Click += (_, _) => Finish(false);
        Shown += (_, _) => Activate();
        Refill();
    }

    /// <summary>Các link được chọn khi bấm Tải ngay / Tải sau.</summary>
    public IReadOnlyList<BatchLink> Selected { get; private set; } = [];

    public bool StartNow { get; private set; }

    /// <summary>Chất lượng chọn cho các video từ trang; null nếu hộp không có dòng chọn chất lượng.</summary>
    public MediaOptions? Media => _media?.Options;

    /// <summary>Người dùng tích "theo dõi": tự tải video mới sau này.</summary>
    public bool Follow => _follow?.Checked == true;

    private int S(int v) => Theme.Scale(this, v);

    private void Refill()
    {
        var query = _search.Text.Trim();
        _visible = _links
            .Where(l => _filters[l.Kind].Checked)
            .Where(l => query.Length == 0
                || l.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || l.Host.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _updating = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var link in _visible)
        {
            var item = new ListViewItem([link.FileName, KindText(link.Kind), link.Host])
            {
                Tag = link,
                Checked = _checked.Contains(link.Url),
                ToolTipText = link.Url,
            };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _updating = false;
        FitColumns();
        UpdateCount();
    }

    private void ToggleAll()
    {
        if (_updating)
            return;
        foreach (var link in _visible)
        {
            if (_all.Checked)
                _checked.Add(link.Url);
            else
                _checked.Remove(link.Url);
        }
        Refill();
    }

    private void UpdateCount()
    {
        var chosen = ChosenVisible().Count;
        _count.Text = $"Đã chọn {chosen}/{_visible.Count}";
        _count.Location = new Point(_count.Parent is { } p ? p.Width - _count.Width - S(24) : _count.Left, S(50));
        _updating = true;
        _all.Checked = _visible.Count > 0 && chosen == _visible.Count;
        _updating = false;
        _now.Enabled = _later.Enabled = chosen > 0 || Follow;
    }

    private List<BatchLink> ChosenVisible() => _visible.Where(l => _checked.Contains(l.Url)).ToList();

    private void FitColumns()
    {
        if (_list.Columns.Count < 3)
            return;
        var width = _list.ClientSize.Width;
        _list.Columns[1].Width = S(110);
        _list.Columns[2].Width = S(180);
        _list.Columns[0].Width = Math.Max(S(160), width - _list.Columns[1].Width - _list.Columns[2].Width - 4);
    }

    private static string KindText(LinkKind kind) => Kinds.First(k => k.Kind == kind).Text;

    private void Finish(bool startNow)
    {
        // Chỉ tải những link đang hiện (đúng bộ lọc) và được tích.
        Selected = ChosenVisible();
        if (Selected.Count == 0 && !Follow)
            return;
        StartNow = startNow;
        DialogResult = DialogResult.OK;
        Close();
    }
}
