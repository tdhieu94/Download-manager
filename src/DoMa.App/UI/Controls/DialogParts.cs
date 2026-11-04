namespace DoMa.App.UI.Controls;

/// <summary>Các khối dựng sẵn cho hộp thoại: đầu trang có icon, chân trang chứa nút.</summary>
public static class DialogParts
{
    public static void ApplyBase(Form form, Size clientSize)
    {
        form.Font = Theme.Body;
        form.BackColor = Theme.Surface;
        form.ForeColor = Theme.Text;
        form.Icon = Theme.AppIcon;
        form.ShowIcon = false;
        form.ShowInTaskbar = false;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
        Theme.StyleWindow(form);
        form.ClientSize = new Size(Theme.Scale(form, clientSize.Width), Theme.Scale(form, clientSize.Height));
    }

    /// <summary>Đầu hộp thoại: ô icon màu + tiêu đề + mô tả.</summary>
    public static Panel Header(Form form, IconKind icon, Color color, string title, string subtitle)
    {
        int S(int v) => Theme.Scale(form, v);
        var header = new Panel { Dock = DockStyle.Top, Height = S(76), BackColor = Theme.Surface };
        var titleLabel = new Label
        {
            Text = title,
            Font = Theme.Subtitle,
            ForeColor = Theme.Text,
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(S(76), S(16)),
            Size = new Size(form.ClientSize.Width - S(96), S(26)),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
        };
        var subtitleLabel = new Label
        {
            Text = subtitle,
            Font = Theme.Small,
            ForeColor = Theme.TextMuted,
            AutoSize = false,
            AutoEllipsis = true,
            Location = new Point(S(77), S(42)),
            Size = new Size(form.ClientSize.Width - S(96), S(20)),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
        };
        header.Controls.Add(titleLabel);
        header.Controls.Add(subtitleLabel);
        header.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var tile = new RectangleF(S(24), S(18), S(40), S(40));
            Theme.FillRound(g, Theme.Tint(color, 0.88f), tile, S(10));
            Icons.Draw(g, icon, RectangleF.Inflate(tile, -S(10), -S(10)), color);
        };
        return header;
    }

    /// <summary>Chân hộp thoại nền xám nhạt; nút xếp từ phải sang trái theo thứ tự truyền vào.</summary>
    public static Panel Footer(Form form, params Button[] rightToLeft)
    {
        int S(int v) => Theme.Scale(form, v);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = S(64), BackColor = Theme.SurfaceAlt };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Padding = new Padding(S(20), S(15), S(20), 0),
            BackColor = Theme.SurfaceAlt,
            WrapContents = false,
        };
        foreach (var button in rightToLeft)
        {
            button.Margin = new Padding(S(8), 0, 0, 0);
            flow.Controls.Add(button);
        }
        footer.Controls.Add(flow);
        return footer;
    }

    public static Label FieldLabel(string text) => new()
    {
        Text = text,
        Font = Theme.BodySemibold,
        ForeColor = Theme.TextSecondary,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 6),
    };

    public static Label Hint(string text) => new()
    {
        Text = text,
        Font = Theme.Small,
        ForeColor = Theme.TextMuted,
        AutoSize = true,
    };
}
