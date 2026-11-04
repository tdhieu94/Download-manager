namespace DoMa.App.UI.Controls;

/// <summary>Ô nhập liệu có viền bo góc, icon tuỳ chọn và viền xanh khi đang nhập.</summary>
public sealed class InputBox : Control
{
    private readonly IconKind _icon;

    public InputBox(IconKind icon = IconKind.None)
    {
        _icon = icon;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = Theme.Body,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
        };
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Box.TextChanged += (_, e) => OnTextChanged(e);
        Controls.Add(Box);
        Height = S(36);
        Cursor = Cursors.IBeam;
    }

    public TextBox Box { get; }

    public override string Text
    {
        get => Box is null ? base.Text : Box.Text;
#pragma warning disable CS8765
        set
        {
            if (Box is null)
                base.Text = value;
            else
                Box.Text = value;
        }
#pragma warning restore CS8765
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string PlaceholderText
    {
        get => Box.PlaceholderText;
        set => Box.PlaceholderText = value;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ReadOnly
    {
        get => Box.ReadOnly;
        set
        {
            Box.ReadOnly = value;
            var back = value ? Theme.SurfaceAlt : Theme.Surface;
            BackColor = back;
            Box.BackColor = back;
            Box.ForeColor = value ? Theme.TextSecondary : Theme.Text;
            Invalidate();
        }
    }

    private int S(int v) => Theme.Scale(this, v);

    protected override Size DefaultSize => new(200, 36);

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Box.Focus();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Box.Focus();
        base.OnMouseDown(e);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var left = _icon == IconKind.None ? S(11) : S(36);
        var h = Box.PreferredHeight;
        Box.SetBounds(left, (Height - h) / 2, Math.Max(10, Width - left - S(11)), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRound(g, BackColor, rect, S(7));
        var focused = Box.Focused && !Box.ReadOnly;
        Theme.DrawRound(g, focused ? Theme.Accent : Theme.BorderStrong, rect, S(7), focused ? 1.6f : 1f);
        if (_icon != IconKind.None)
        {
            var size = S(16);
            Icons.Draw(g, _icon, new RectangleF(S(12), (Height - size) / 2f, size, size), Theme.TextMuted);
        }
    }
}
