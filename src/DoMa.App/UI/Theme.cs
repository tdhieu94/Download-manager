using System.Drawing.Drawing2D;
using DoMa.Core.Models;

namespace DoMa.App.UI;

public enum ThemeMode { System, Light, Dark }

/// <summary>Bảng màu, font và các hàm vẽ dùng chung cho toàn bộ giao diện.</summary>
public static class Theme
{
    public static bool IsDark { get; private set; }

    public static Color Window { get; private set; }
    public static Color Surface { get; private set; }
    public static Color SurfaceAlt { get; private set; }
    public static Color Border { get; private set; }
    public static Color BorderStrong { get; private set; }
    public static Color Divider { get; private set; }
    public static Color Text { get; private set; }
    public static Color TextSecondary { get; private set; }
    public static Color TextMuted { get; private set; }
    public static Color Accent { get; private set; }
    public static Color AccentHover { get; private set; }
    public static Color AccentPressed { get; private set; }
    /// <summary>Màu chữ nhấn (mục đang chọn, tiêu đề dải thông báo).</summary>
    public static Color AccentText { get; private set; }
    public static Color AccentSoft { get; private set; }
    public static Color AccentSoftStrong { get; private set; }
    public static Color Hover { get; private set; }
    public static Color Pressed { get; private set; }
    public static Color Track { get; private set; }
    public static Color Success { get; private set; }
    public static Color Warning { get; private set; }
    public static Color Danger { get; private set; }
    public static Color Neutral { get; private set; }
    public static Color Violet { get; private set; }

    static Theme() => Use(ThemeMode.System);

    /// <summary>Chọn bảng màu. Gọi trước khi tạo cửa sổ (lúc khởi động).</summary>
    public static void Use(ThemeMode mode)
    {
        IsDark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemPrefersDark(),
        };
        if (IsDark)
        {
            Window = Color.FromArgb(15, 23, 42);
            Surface = Color.FromArgb(24, 33, 52);
            SurfaceAlt = Color.FromArgb(31, 42, 64);
            Border = Color.FromArgb(46, 59, 82);
            BorderStrong = Color.FromArgb(71, 85, 105);
            Divider = Color.FromArgb(37, 49, 71);
            Text = Color.FromArgb(241, 245, 249);
            TextSecondary = Color.FromArgb(203, 213, 225);
            TextMuted = Color.FromArgb(148, 163, 184);
            Accent = Color.FromArgb(59, 130, 246);
            AccentHover = Color.FromArgb(96, 150, 250);
            AccentPressed = Color.FromArgb(37, 99, 235);
            AccentText = Color.FromArgb(147, 197, 253);
            AccentSoft = Color.FromArgb(30, 52, 94);
            AccentSoftStrong = Color.FromArgb(36, 64, 118);
            Hover = Color.FromArgb(36, 48, 70);
            Pressed = Color.FromArgb(46, 59, 82);
            Track = Color.FromArgb(51, 65, 85);
            Success = Color.FromArgb(34, 197, 94);
            Warning = Color.FromArgb(245, 158, 11);
            Danger = Color.FromArgb(248, 113, 113);
            Neutral = Color.FromArgb(100, 116, 139);
            Violet = Color.FromArgb(167, 139, 250);
        }
        else
        {
            Window = Color.FromArgb(243, 245, 249);
            Surface = Color.White;
            SurfaceAlt = Color.FromArgb(248, 250, 252);
            Border = Color.FromArgb(226, 232, 240);
            BorderStrong = Color.FromArgb(203, 213, 225);
            Divider = Color.FromArgb(238, 242, 247);
            Text = Color.FromArgb(15, 23, 42);
            TextSecondary = Color.FromArgb(71, 85, 105);
            TextMuted = Color.FromArgb(100, 116, 139);
            Accent = Color.FromArgb(37, 99, 235);
            AccentHover = Color.FromArgb(29, 78, 216);
            AccentPressed = Color.FromArgb(30, 64, 175);
            AccentText = Color.FromArgb(30, 64, 175);
            AccentSoft = Color.FromArgb(232, 240, 254);
            AccentSoftStrong = Color.FromArgb(214, 228, 253);
            Hover = Color.FromArgb(241, 245, 249);
            Pressed = Color.FromArgb(226, 232, 240);
            Track = Color.FromArgb(226, 232, 240);
            Success = Color.FromArgb(22, 163, 74);
            Warning = Color.FromArgb(217, 119, 6);
            Danger = Color.FromArgb(220, 38, 38);
            Neutral = Color.FromArgb(148, 163, 184);
            Violet = Color.FromArgb(124, 58, 237);
        }
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Thanh tiêu đề tối cho cửa sổ (Windows 10 20H1+ / Windows 11).</summary>
    public static void StyleWindow(Form form)
    {
        void Apply()
        {
            var dark = IsDark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(form.Handle, 19, ref dark, sizeof(int));
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }
        if (form.IsHandleCreated)
            Apply();
        else
            form.HandleCreated += (_, _) => Apply();
    }

    /// <summary>Thanh cuộn tối cho control có thanh cuộn hệ thống.</summary>
    public static void StyleScrollbars(Control control)
    {
        if (!IsDark)
            return;
        void Apply()
        {
            try
            {
                SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
            }
        }
        if (control.IsHandleCreated)
            Apply();
        else
            control.HandleCreated += (_, _) => Apply();
    }

    /// <summary>Màu chữ đọc rõ trên nền nhạt của chính màu đó (nhãn trạng thái).</summary>
    public static Color Readable(Color color) => IsDark
        ? Color.FromArgb(Math.Min(255, color.R + 40), Math.Min(255, color.G + 40), Math.Min(255, color.B + 40))
        : Color.FromArgb(Math.Max(0, color.R - 40), Math.Max(0, color.G - 40), Math.Max(0, color.B - 40));

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

    /// <summary>Pha màu với nền (amount = 0 giữ nguyên, 1 thành màu nền). Nền sáng → màu nhạt, nền tối → màu trầm.</summary>
    public static Color Tint(Color color, float amount)
    {
        var bg = Surface;
        return Color.FromArgb(
            color.R + (int)((bg.R - color.R) * amount),
            color.G + (int)((bg.G - color.G) * amount),
            color.B + (int)((bg.B - color.B) * amount));
    }

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
