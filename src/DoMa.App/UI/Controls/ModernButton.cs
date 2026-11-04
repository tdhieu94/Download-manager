using System.Drawing.Drawing2D;

namespace DoMa.App.UI.Controls;

public enum ButtonKind { Primary, Secondary, Ghost }

/// <summary>Nút phẳng bo góc, có icon, trạng thái hover/nhấn/vô hiệu.</summary>
public sealed class ModernButton : Button
{
    private bool _hover;
    private bool _pressed;

    public ModernButton(string text, IconKind icon = IconKind.None, ButtonKind kind = ButtonKind.Secondary)
    {
        Text = text;
        Icon = icon;
        Kind = kind;
        Font = Theme.BodySemibold;
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IconKind Icon { get; set; }

    /// <summary>Chỉ hiện icon (chữ dùng làm tooltip).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool IconOnly { get; set; }

    private int S(int v) => Theme.Scale(this, v);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var height = S(34);
        if (IconOnly)
            return new Size(height, height);
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        var width = text.Width + S(Icon == IconKind.None ? 32 : 52);
        return new Size(Math.Max(width, S(72)), height);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var (back, border, fore) = Colors();
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var radius = S(7);
        if (back != Color.Transparent)
            Theme.FillRound(g, back, rect, radius);
        if (border != Color.Transparent)
            Theme.DrawRound(g, border, rect, radius);
        if (Focused && ShowFocusCues)
            Theme.DrawRound(g, Theme.Tint(Theme.Accent, 0.4f), RectangleF.Inflate(rect, -2, -2), radius - 2, 1.5f);

        var iconSize = S(16);
        if (IconOnly)
        {
            Icons.Draw(g, Icon, new RectangleF((Width - iconSize) / 2f, (Height - iconSize) / 2f, iconSize, iconSize), fore);
            return;
        }
        var textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        var gap = Icon == IconKind.None ? 0 : iconSize + S(8);
        var x = (Width - textSize.Width - gap) / 2;
        if (Icon != IconKind.None)
            Icons.Draw(g, Icon, new RectangleF(x, (Height - iconSize) / 2f, iconSize, iconSize), fore);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x + gap, 0, textSize.Width + 2, Height), fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    private (Color Back, Color Border, Color Fore) Colors()
    {
        if (!Enabled)
        {
            return Kind == ButtonKind.Primary
                ? (Theme.Tint(Theme.Accent, 0.55f), Color.Transparent, Color.White)
                : (Kind == ButtonKind.Secondary ? Theme.Surface : Color.Transparent, Kind == ButtonKind.Secondary ? Theme.Border : Color.Transparent, Theme.Neutral);
        }
        return Kind switch
        {
            ButtonKind.Primary => (_pressed ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent, Color.Transparent, Color.White),
            ButtonKind.Secondary => (_pressed ? Theme.Pressed : _hover ? Theme.Hover : Theme.Surface, _hover ? Theme.BorderStrong : Theme.Border, Theme.Text),
            _ => (_pressed ? Theme.Pressed : _hover ? Theme.Hover : Color.Transparent, Color.Transparent, Theme.TextSecondary),
        };
    }
}
