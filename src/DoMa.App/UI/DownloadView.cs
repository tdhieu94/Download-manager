using System.Globalization;
using DoMa.Core.Models;

namespace DoMa.App.UI;

public enum DownloadFilter { All, Active, Completed, Later, Documents, Software, Archives, Media, Other }

/// <summary>Cột dùng để sắp xếp danh sách; Added = mặc định (tệp mới thêm đứng trên cùng).</summary>
public enum SortColumn { Added, Name, Size, Progress, Time, Status }

public static class DownloadView
{
    /// <summary>Sắp xếp danh sách theo một cột. Tệp bằng nhau thì tệp mới thêm đứng trước.</summary>
    public static IEnumerable<DownloadItem> Sort(IEnumerable<DownloadItem> items, SortColumn column, bool ascending)
    {
        var newest = items.OrderByDescending(i => i.AddedAt);
        if (column == SortColumn.Added)
            return ascending ? items.OrderBy(i => i.AddedAt) : newest;
        Func<DownloadItem, IComparable> key = column switch
        {
            SortColumn.Name => i => Path.GetFileName(i.TargetPath).ToUpperInvariant(),
            SortColumn.Size => i => i.TotalBytes ?? i.DownloadedBytes,
            SortColumn.Progress => i => i.Status == DownloadStatus.Completed ? 100 : Percent(i.DownloadedBytes, i.TotalBytes),
            SortColumn.Time => i => i.CompletedAt ?? i.AddedAt,
            _ => i => StatusOrder(i.Status),
        };
        // OrderBy ổn định nên thứ tự "mới thêm trước" được giữ giữa các tệp bằng nhau.
        return ascending ? newest.OrderBy(key) : newest.OrderByDescending(key);
    }

    /// <summary>Tăng dần: đang tải, đang chờ, tạm dừng, lỗi, chờ tải sau, rồi đã xong.</summary>
    private static int StatusOrder(DownloadStatus status) => status switch
    {
        DownloadStatus.Downloading => 0,
        DownloadStatus.Queued => 1,
        DownloadStatus.Paused => 2,
        DownloadStatus.Failed => 3,
        DownloadStatus.Later => 4,
        _ => 5,
    };

    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0)
            return "";
        var value = bytesPerSec;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString("0.#", Vi)} {Units[unit]}/s";
    }

    public static string FormatEta(TimeSpan? eta)
    {
        if (eta is null)
            return "";
        var seconds = Math.Max(1, (long)Math.Round(eta.Value.TotalSeconds));
        if (seconds < 60)
            return L.F("{0} giây", seconds);
        var minutes = (seconds + 30) / 60;
        if (minutes < 60)
            return L.F("{0} phút", minutes);
        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? L.F("{0} giờ", hours) : L.F("{0} giờ {1} phút", hours, rest);
    }

    /// <summary>Cột tốc độ: tốc độ hiện tại, kèm mức tối đa riêng nếu tệp có đặt (vd. "820 KB/s · ≤1 MB/s").</summary>
    public static string SpeedText(double bytesPerSec, int limitKBps)
    {
        var speed = FormatSpeed(bytesPerSec);
        if (limitKBps <= 0)
            return speed;
        var limit = "≤" + FormatSpeed(limitKBps * 1024.0);
        return speed.Length == 0 ? limit : $"{speed} · {limit}";
    }

    /// <summary>
    /// Đọc mức tốc độ người dùng gõ: "750" hoặc "750 KB" là KB/s, "1.5 MB" (hay "1,5m") là MB/s.
    /// Trống = 0 (không giới hạn); null nếu không đọc được.
    /// </summary>
    public static int? ParseSpeedKBps(string? text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant().Replace("/s", "").Replace(',', '.').Trim();
        if (value.Length == 0)
            return 0;
        var scale = 1.0;
        foreach (var (suffix, factor) in new[] { ("mb", 1024.0), ("kb", 1.0), ("m", 1024.0), ("k", 1.0) })
        {
            if (!value.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            scale = factor;
            value = value[..^suffix.Length].Trim();
            break;
        }
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            return null;
        var kbps = number * scale;
        return kbps is >= 0 and <= 10_000_000 ? (int)Math.Round(kbps) : null;
    }

    public static int Percent(long downloaded, long? total)
    {
        if (total is null or <= 0)
            return -1;
        return (int)Math.Clamp(downloaded * 100 / total.Value, 0, 100);
    }

    public static bool Matches(DownloadFilter filter, DownloadItem item) => filter switch
    {
        DownloadFilter.All => true,
        DownloadFilter.Active => item.Status is not (DownloadStatus.Completed or DownloadStatus.Later),
        DownloadFilter.Completed => item.Status == DownloadStatus.Completed,
        DownloadFilter.Later => item.Status == DownloadStatus.Later,
        DownloadFilter.Documents => item.Category == Category.Document,
        DownloadFilter.Software => item.Category == Category.Software,
        DownloadFilter.Archives => item.Category == Category.Archive,
        DownloadFilter.Media => item.Category == Category.Media,
        _ => item.Category == Category.Other,
    };

    /// <summary>Ô tìm kiếm: khớp tên tệp hoặc tên trang web (máy chủ) của link tải, không phân biệt hoa thường.</summary>
    public static bool MatchesSearch(DownloadItem item, string query)
    {
        query = query.Trim();
        if (query.Length == 0)
            return true;
        if (Path.GetFileName(item.TargetPath).Contains(query, StringComparison.CurrentCultureIgnoreCase))
            return true;
        var link = item.Request.Referrer ?? item.Request.Url;
        return (Uri.TryCreate(item.Request.Url, UriKind.Absolute, out var url) && url.Host.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (Uri.TryCreate(link, UriKind.Absolute, out var page) && page.Host.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    public static int Count(DownloadFilter filter, IEnumerable<DownloadItem> items) =>
        items.Count(i => Matches(filter, i));

    /// <summary>
    /// Dung lượng hiển thị. Khi máy chủ không báo trước (TotalBytes = null) thì hiện số đã tải;
    /// tệp đã xong thì đó chính là dung lượng thật.
    /// </summary>
    public static string SizeText(DownloadItem item)
    {
        if (item.TotalBytes is long total)
            return SizeFormatter.Format(total);
        if (item.DownloadedBytes <= 0)
            return SizeFormatter.Unknown;
        var done = SizeFormatter.Format(item.DownloadedBytes);
        return item.Status == DownloadStatus.Completed ? done : $"{done} / ?";
    }

    /// <summary>Ngày giờ hiển thị: lúc tải xong nếu đã xong, ngược lại lúc thêm vào danh sách.</summary>
    public static string TimeText(DownloadItem item) =>
        (item.CompletedAt ?? item.AddedAt).ToLocalTime().ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public static string TimeTooltip(DownloadItem item)
    {
        static string F(DateTimeOffset t) => t.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var text = L.F("Thêm lúc {0}", F(item.AddedAt));
        if (item.CompletedAt is { } done)
        {
            text += L.F("\nXong lúc {0}", F(done));
            var took = done - item.AddedAt;
            if (took > TimeSpan.Zero)
                text += L.F("\nMất {0}", FormatDuration(took));
        }
        return text;
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? L.F("{0} giờ {1} phút", (int)t.TotalHours, t.Minutes)
        : t.TotalMinutes >= 1 ? L.F("{0} phút {1} giây", t.Minutes, t.Seconds)
        : L.F("{0} giây", Math.Max(1, t.Seconds));

    /// <param name="queuePosition">Thứ tự trong hàng chờ (1 = tải tiếp theo) nếu tệp đang chờ.</param>
    public static string StatusText(DownloadItem item, int? queuePosition = null)
    {
        if (item.Threat is not null)
            return L.T("Có mã độc");
        if (!string.IsNullOrWhiteSpace(item.ErrorMessage))
            return item.ErrorMessage;
        var text = item.Status switch
        {
            DownloadStatus.Queued => queuePosition is { } n ? L.F("Đang chờ · thứ {0}", n) : L.T("Đang chờ"),
            DownloadStatus.Downloading => L.T("Đang tải"),
            DownloadStatus.Paused => L.T("Tạm dừng"),
            DownloadStatus.Later => L.T("Chờ tải sau"),
            DownloadStatus.Completed => L.T("Xong"),
            _ => L.T("Lỗi"),
        };
        return !item.CanResume && item.Status is DownloadStatus.Downloading or DownloadStatus.Paused
            ? L.F("{0} — Không tải tiếp được", text)
            : text;
    }
}
