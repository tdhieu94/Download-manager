using System.Drawing.Drawing2D;
using DoMa.Core.Models;

namespace DoMa.App.UI;

/// <summary>Bảng màu, font và các hàm vẽ dùng chung cho toàn bộ giao diện.</summary>
public static class Theme
{
    public static readonly Color Window = Color.FromArgb(243, 245, 249);
    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceAlt = Color.FromArgb(248, 250, 252);
    public static readonly Color Border = Color.FromArgb(226, 232, 240);
    public static readonly Color BorderStrong = Color.FromArgb(203, 213, 225);
    public static readonly Color Text = Color.FromArgb(15, 23, 42);
    public static readonly Color TextSecondary = Color.FromArgb(71, 85, 105);
    public static readonly Color TextMuted = Color.FromArgb(100, 116, 139);
    public static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
    public static readonly Color AccentPressed = Color.FromArgb(30, 64, 175);
    public static readonly Color AccentSoft = Color.FromArgb(232, 240, 254);
    public static readonly Color AccentSoftStrong = Color.FromArgb(214, 228, 253);
    public static readonly Color Hover = Color.FromArgb(241, 245, 249);
    public static readonly Color Pressed = Color.FromArgb(226, 232, 240);
    public static readonly Color Track = Color.FromArgb(226, 232, 240);
    public static readonly Color Success = Color.FromArgb(22, 163, 74);
    public static readonly Color Warning = Color.FromArgb(217, 119, 6);
    public static readonly Color Danger = Color.FromArgb(220, 38, 38);
    public static readonly Color Neutral = Color.FromArgb(148, 163, 184);
    public static readonly Color Violet = Color.FromArgb(124, 58, 237);

    private static readonly string Family = PickFamily();

    public static readonly Font Body = new(Family, 9.75f);
    public static readonly Font BodySemibold = new(Family, 9.75f, FontStyle.Bold);
    public static readonly Font Small = new(Family, 8.75f);
    public static readonly Font SmallBold = new(Family, 8.25f, FontStyle.Bold);
    public static readonly Font Title = new(Family, 15f, FontStyle.Bold);
    public static readonly Font Subtitle = new(Family, 12f, FontStyle.Bold);
    public static readonly Font Brand = new(Family, 13f, FontStyle.Bold);

    private static string PickFamily()
    {
        foreach (var name in new[] { "Segoe UI" })
        {
            using var probe = new Font(name, 10f);
            if (string.Equals(probe.Name, name, StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Segoe UI";
    }

    public static int Scale(Control control, int value) => (int)Math.Round(value * control.DeviceDpi / 96f);

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, Color color, RectangleF r, float radius)
    {
        using var path = RoundRect(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void DrawRound(Graphics g, Color color, RectangleF r, float radius, float width = 1f)
    {
        using var path = RoundRect(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    public static Color CategoryColor(Category category) => category switch
    {
        Category.Document => Accent,
        Category.Software => Violet,
        Category.Archive => Warning,
        _ => TextMuted,
    };

    public static IconKind CategoryIcon(Category category) => category switch
    {
        Category.Document => IconKind.Document,
        Category.Software => IconKind.Package,
        Category.Archive => IconKind.Archive,
        _ => IconKind.Shapes,
    };

    public static string CategoryName(Category category) => category switch
    {
        Category.Document => "Tài liệu",
        Category.Software => "Phần mềm",
        Category.Archive => "Nén",
        _ => "Khác",
    };

    public static Color StatusColor(DownloadItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ErrorMessage) && item.Status != DownloadStatus.Completed)
            return Danger;
        return item.Status switch
        {
            DownloadStatus.Downloading => Accent,
            DownloadStatus.Completed => Success,
            DownloadStatus.Paused => Warning,
            DownloadStatus.Later => Violet,
            DownloadStatus.Queued => Neutral,
            _ => Danger,
        };
    }

    /// <summary>Pha màu với trắng (amount = 0 giữ nguyên, 1 thành trắng).</summary>
    public static Color Tint(Color color, float amount) => Color.FromArgb(
        color.R + (int)((255 - color.R) * amount),
        color.G + (int)((255 - color.G) * amount),
        color.B + (int)((255 - color.B) * amount));

    public static Icon AppIcon { get; } = LoadAppIcon();

    private static Icon LoadAppIcon()
    {
        using var stream = typeof(Theme).Assembly.GetManifestResourceStream("DoMa.App.doma.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    public static Icon AppIconSized(int size)
    {
        using var stream = typeof(Theme).Assembly.GetManifestResourceStream("DoMa.App.doma.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream, new Size(size, size));
    }
}
