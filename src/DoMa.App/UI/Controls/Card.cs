using System.Drawing.Drawing2D;

namespace DoMa.App.UI.Controls;

/// <summary>Khung nền trắng bo góc có viền mảnh.</summary>
public class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 10;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Window);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = Theme.Scale(this, Radius);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRound(g, BackColor, rect, r);
        Theme.DrawRound(g, Theme.Border, rect, r);
    }
}
