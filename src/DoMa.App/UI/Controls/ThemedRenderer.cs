using System.Drawing.Drawing2D;

namespace DoMa.App.UI.Controls;

/// <summary>Kiểu vẽ menu chuột phải / menu khay đồng bộ với giao diện.</summary>
public sealed class ThemedRenderer() : ToolStripProfessionalRenderer(new Palette())
{
    public static ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new ThemedRenderer(),
            Font = Theme.Body,
            ShowImageMargin = true,
            Padding = new Padding(4),
            BackColor = Theme.Surface,
        };
        return menu;
    }

    public static ToolStripMenuItem Item(ContextMenuStrip menu, string text, IconKind icon, EventHandler onClick, Color? color = null)
    {
        var item = new ToolStripMenuItem(text, Icons.Bitmap(icon, 16 * menu.DeviceDpi / 96, color ?? Theme.TextSecondary), onClick)
        {
            Padding = new Padding(4, 6, 4, 6),
            ForeColor = color ?? Theme.Text,
        };
        menu.Items.Add(item);
        return item;
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
            return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(2, 0, e.Item.Width - 4, e.Item.Height);
        Theme.FillRound(g, Theme.Hover, r, 6);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Theme.BorderStrong);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Border);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    private sealed class Palette : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color MenuBorder => Theme.BorderStrong;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Theme.Hover;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
    }
}
