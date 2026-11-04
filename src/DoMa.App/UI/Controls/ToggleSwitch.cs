using System.Drawing.Drawing2D;

namespace DoMa.App.UI.Controls;

/// <summary>Công tắc bật/tắt thay cho ô đánh dấu, kèm dòng mô tả phụ.</summary>
public sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch(string text, string description)
    {
        Text = text;
        Description = description;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        AutoSize = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = Theme.Scale(this, 48);
    }

    public string Description { get; }

    private int S(int v) => Theme.Scale(this, v);

    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var trackW = S(40);
        var trackH = S(22);
        var track = new RectangleF(Width - trackW - S(2), (Height - trackH) / 2f, trackW, trackH);
        Theme.FillRound(g, Checked ? Theme.Accent : Theme.BorderStrong, track, trackH / 2f);
        var knob = trackH - S(6);
        var knobX = Checked ? track.Right - knob - S(3) : track.X + S(3);
        using (var brush = new SolidBrush(Color.White))
            g.FillEllipse(brush, knobX, track.Y + S(3), knob, knob);
        if (Focused && ShowFocusCues)
            Theme.DrawRound(g, Theme.Tint(Theme.Accent, 0.4f), RectangleF.Inflate(track, 2, 2), trackH / 2f + 2, 1.5f);

        var textW = (int)track.X - S(12);
        TextRenderer.DrawText(g, Text, Theme.BodySemibold, new Rectangle(0, S(5), textW, Height / 2), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Description, Theme.Small, new Rectangle(0, Height / 2 + S(1), textW, Height / 2), Theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}
