using System.Drawing.Drawing2D;

namespace DoMa.App.UI.Controls;

public sealed class NavItem(DownloadFilter? filter, string text, IconKind icon)
{
    public DownloadFilter? Filter { get; } = filter;
    public string Text { get; } = text;
    public IconKind Icon { get; } = icon;
    public int Count { get; set; }
    public bool IsHeader => Filter is null;
}

/// <summary>Danh sách điều hướng bên trái: tiêu đề nhóm, icon, số lượng dạng huy hiệu.</summary>
public sealed class NavList : Control
{
    private readonly List<NavItem> _items = [];
    private int _hover = -1;
    private int _selected = -1;

    public NavList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Font = Theme.Body;
        BackColor = Theme.Window;
    }

    public event EventHandler? SelectionChanged;

    public IReadOnlyList<NavItem> Items => _items;

    public DownloadFilter Selected => _selected >= 0 && _items[_selected].Filter is { } f ? f : DownloadFilter.All;

    public NavItem? SelectedItem => _selected >= 0 ? _items[_selected] : null;

    public void Add(NavItem item)
    {
        _items.Add(item);
        if (_selected < 0 && !item.IsHeader)
            _selected = _items.Count - 1;
        Invalidate();
    }

    private int S(int v) => Theme.Scale(this, v);

    private int ItemHeight(NavItem item) => item.IsHeader ? S(34) : S(36);

    private Rectangle ItemBounds(int index)
    {
        var y = S(4);
        for (var i = 0; i < index; i++)
            y += ItemHeight(_items[i]);
        return new Rectangle(0, y, Width, ItemHeight(_items[index]));
    }

    private int HitTest(Point p)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (ItemBounds(i).Contains(p))
                return _items[i].IsHeader ? -1 : i;
        }
        return -1;
    }

    private void SelectIndex(int index)
    {
        if (index < 0 || index == _selected || _items[index].IsHeader)
            return;
        _selected = index;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left)
            SelectIndex(HitTest(e.Location));
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = e.KeyCode switch { Keys.Up => -1, Keys.Down => 1, _ => 0 };
        if (step != 0)
        {
            for (var i = _selected + step; i >= 0 && i < _items.Count; i += step)
            {
                if (!_items[i].IsHeader)
                {
                    SelectIndex(i);
                    break;
                }
            }
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var r = ItemBounds(i);
            if (item.IsHeader)
            {
                TextRenderer.DrawText(g, item.Text.ToUpperInvariant(), Theme.SmallBold,
                    new Rectangle(r.X + S(22), r.Y + S(12), r.Width - S(30), r.Height - S(12)), Theme.TextMuted,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
                continue;
            }

            var selected = i == _selected;
            var pill = new RectangleF(r.X + S(10), r.Y + S(2), r.Width - S(20), r.Height - S(4));
            if (selected)
                Theme.FillRound(g, Theme.AccentSoftStrong, pill, S(8));
            else if (i == _hover)
                Theme.FillRound(g, Theme.Pressed, pill, S(8));
            if (selected && Focused && ShowFocusCues)
                Theme.DrawRound(g, Theme.Tint(Theme.Accent, 0.4f), pill, S(8));

            var fore = selected ? Theme.AccentText : Theme.TextSecondary;
            var iconSize = S(18);
            Icons.Draw(g, item.Icon, new RectangleF(pill.X + S(12), pill.Y + (pill.Height - iconSize) / 2, iconSize, iconSize), selected ? Theme.Accent : Theme.TextMuted);

            var countText = item.Count.ToString();
            var countSize = TextRenderer.MeasureText(g, countText, Theme.SmallBold, Size.Empty, TextFormatFlags.NoPadding);
            var badgeW = Math.Max(countSize.Width + S(14), S(24));
            var badge = new RectangleF(pill.Right - badgeW - S(8), pill.Y + (pill.Height - S(20)) / 2, badgeW, S(20));

            var textRect = Rectangle.Round(new RectangleF(pill.X + S(42), pill.Y, badge.X - pill.X - S(46), pill.Height));
            TextRenderer.DrawText(g, item.Text, selected ? Theme.BodySemibold : Theme.Body, textRect, selected ? fore : Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            if (item.Count > 0 || selected)
            {
                Theme.FillRound(g, selected ? Theme.Accent : Theme.Border, badge, S(10));
                TextRenderer.DrawText(g, countText, Theme.SmallBold, Rectangle.Round(badge), selected ? Color.White : Theme.TextSecondary,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }
}
