using System.Globalization;
using DoMa.Core.Models;

namespace DoMa.App.UI;

public enum DownloadFilter { All, Active, Completed, Later, Documents, Software, Archives, Media, Other }

public static class DownloadView
{
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
            return $"{seconds} giây";
        var minutes = (seconds + 30) / 60;
        if (minutes < 60)
            return $"{minutes} phút";
        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours} giờ" : $"{hours} giờ {rest} phút";
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
        var text = $"Thêm lúc {F(item.AddedAt)}";
        if (item.CompletedAt is { } done)
        {
            text += $"\nXong lúc {F(done)}";
            var took = done - item.AddedAt;
            if (took > TimeSpan.Zero)
                text += $"\nMất {FormatDuration(took)}";
        }
        return text;
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} giờ {t.Minutes} phút"
        : t.TotalMinutes >= 1 ? $"{t.Minutes} phút {t.Seconds} giây"
        : $"{Math.Max(1, t.Seconds)} giây";

    public static string StatusText(DownloadItem item)
    {
        if (item.Threat is not null)
            return "Có mã độc";
        if (!string.IsNullOrWhiteSpace(item.ErrorMessage))
            return item.ErrorMessage;
        var text = item.Status switch
        {
            DownloadStatus.Queued => "Đang chờ",
            DownloadStatus.Downloading => "Đang tải",
            DownloadStatus.Paused => "Tạm dừng",
            DownloadStatus.Later => "Chờ tải sau",
            DownloadStatus.Completed => "Xong",
            _ => "Lỗi",
        };
        return !item.CanResume && item.Status is DownloadStatus.Downloading or DownloadStatus.Paused
            ? $"{text} — Không tải tiếp được"
            : text;
    }
}
