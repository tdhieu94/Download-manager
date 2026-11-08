using System.Globalization;

namespace DoMa.Core.Files;

/// <summary>Kiểm tra chỗ trống trên ổ trước khi tải, để báo sớm thay vì lỗi "ổ đầy" khi đã tải gần xong.</summary>
public static class DiskSpace
{
    /// <summary>Chừa lại một ít cho hệ thống và tệp trạng thái.</summary>
    public const long Reserve = 16L << 20;

    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Số byte còn trống trên ổ chứa <paramref name="path"/>; null nếu không biết (ổ mạng, đường dẫn lạ…).</summary>
    public static long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                      or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Câu báo thiếu chỗ nếu ổ không đủ để lưu thêm <paramref name="needed"/> byte; null nếu đủ hoặc không biết.
    /// </summary>
    public static string? Shortage(string path, long? needed, Func<string, long?>? free = null)
    {
        if (needed is not > 0)
            return null;
        var available = (free ?? FreeBytes)(path);
        if (available is not { } left || left >= needed.Value + Reserve)
            return null;
        var drive = DriveName(path);
        return L.F("Ổ {0} không đủ chỗ: cần {1}, chỉ còn {2}", drive, Format(needed), Format(Math.Max(0, left)));
    }

    private static string DriveName(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
            return root.TrimEnd('\\', '/') is { Length: > 0 } name ? name : root;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return L.T("đĩa");
        }
    }

    /// <summary>Dung lượng dễ đọc: 1,5 GB, 700,0 MB…</summary>
    public static string Format(long? bytes)
    {
        if (bytes is null or < 0)
            return L.T("Không rõ");
        if (bytes < 1024)
            return $"{bytes} B";
        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString("0.0", Vi)} {Units[unit]}";
    }
}
