using System.Drawing.Drawing2D;

namespace DoMa.App.UI;

/// <summary>
/// Thông báo nhỏ ở góc dưới bên phải màn hình, do DoMa tự vẽ. Không phụ thuộc vào cài đặt thông báo
/// của Windows (Focus Assist, thông báo bị tắt…), nên luôn hiện khi tải xong. Không cướp focus,
/// tự ẩn sau vài giây (dừng đếm khi di chuột vào), nhiều thông báo xếp chồng lên nhau.
/// </summary>
public sealed class ToastForm : Form
{
    private static readonly List<ToastForm> Open = [];
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly string _title;
    private readonly string _text;
    private readonly ToolTipIcon _kind;
    private readonly Action? _onClick;
    private bool _hoverClose;

    private ToastForm(string title, string text, ToolTipIcon kind, Action? onClick, int seconds)
    {
        _title = title;
        _text = text;
        _kind = kind;
        _onClick = onClick;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Theme.Surface;
        Font = Theme.Body;
        Cursor = onClick is null ? Cursors.Default : Cursors.Hand;
        DoubleBuffered = true;
        Size = new Size(S(360), S(84));
        Theme.StyleWindow(this);

        _timer.Interval = seconds * 1000;
        _timer.Tick += (_, _) => Close();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) =>
        {
            if (!ClientRectangle.Contains(PointToClient(MousePosition)))
                _timer.Start();
        };
        MouseMove += (_, e) =>
        {
            var hover = CloseBox.Contains(e.Location);
            if (hover != _hoverClose)
            {
                _hoverClose = hover;
                Invalidate();
            }
        };
        MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            if (!CloseBox.Contains(e.Location))
                _onClick?.Invoke();
            Close();
        };
        FormClosed += (_, _) =>
        {
            _timer.Dispose();
            Open.Remove(this);
            Restack();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80;
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int CS_DROPSHADOW = 0x20000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    private int S(int v) => Theme.Scale(this, v);

    private Rectangle CloseBox => new(Width - S(34), S(8), S(24), S(24));

    /// <summary>Hiện một thông báo. Phải gọi trên luồng giao diện.</summary>
    public static void Show(string title, string text, ToolTipIcon kind, Action? onClick, int seconds = 6)
    {
        var toast = new ToastForm(title, text, kind, onClick, seconds);
        Open.Insert(0, toast);
        while (Open.Count > 4)
            Open[^1].Close();
        Restack();
        toast.Show();
        toast._timer.Start();
    }

    private static void Restack()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var bottom = area.Bottom;
        foreach (var toast in Open)
        {
            var gap = toast.S(12);
            bottom -= toast.Height + gap;
            toast.Location = new Point(area.Right - toast.Width - gap, bottom);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Theme.BorderStrong))
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        var color = _kind switch
        {
            ToolTipIcon.Error => Theme.Danger,
            ToolTipIcon.Warning => Theme.Warning,
            ToolTipIcon.None => Theme.Accent,
            _ => Theme.Success,
        };
        var icon = _kind switch
        {
            ToolTipIcon.Error or ToolTipIcon.Warning => IconKind.Shield,
            ToolTipIcon.None => IconKind.Download,
            _ => IconKind.Check,
        };
        using (var bar = new SolidBrush(color))
            g.FillRectangle(bar, 0, 0, S(4), Height);
        var tile = new RectangleF(S(18), (Height - S(40)) / 2f, S(40), S(40));
        Theme.FillRound(g, Theme.Tint(color, 0.85f), tile, S(10));
        Icons.Draw(g, icon, RectangleF.Inflate(tile, -S(10), -S(10)), color);

        var x = S(72);
        var w = Width - x - S(42);
        var flags = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, _title, Theme.BodySemibold, new Rectangle(x, S(16), w, S(22)), Theme.Text, flags | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, _text, Theme.Small, new Rectangle(x, S(40), w, Height - S(46)), Theme.TextSecondary, flags | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        var close = CloseBox;
        if (_hoverClose)
            Theme.FillRound(g, Theme.Hover, close, S(6));
        var c = RectangleF.Inflate(close, -S(8), -S(8));
        using var x1 = new Pen(Theme.TextMuted, Math.Max(1.5f, S(2) * 0.75f));
        g.DrawLine(x1, c.Left, c.Top, c.Right, c.Bottom);
        g.DrawLine(x1, c.Right, c.Top, c.Left, c.Bottom);
    }
}
