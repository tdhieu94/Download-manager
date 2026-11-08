using DoMa.App.UI;
using DoMa.Core.Models;

namespace DoMa.App.Tests;

public class DownloadViewTests
{
    [Theory]
    [InlineData(0.0, "")]
    [InlineData(512.0, "512 B/s")]
    [InlineData(1536.0, "1,5 KB/s")]
    [InlineData(50331648.0, "48 MB/s")]
    [InlineData(1610612736.0, "1,5 GB/s")]
    public void formats_speed(double bytesPerSec, string expected)
    {
        Assert.Equal(expected, DownloadView.FormatSpeed(bytesPerSec));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(47.0, "47 giây")]
    [InlineData(0.2, "1 giây")]
    [InlineData(60.0, "1 phút")]
    [InlineData(170.0, "3 phút")]
    [InlineData(3600.0, "1 giờ")]
    [InlineData(7500.0, "2 giờ 5 phút")]
    public void formats_eta(double? seconds, string expected)
    {
        Assert.Equal(expected, DownloadView.FormatEta(seconds is null ? null : TimeSpan.FromSeconds(seconds.Value)));
    }

    [Theory]
    [InlineData(0L, 100L, 0)]
    [InlineData(50L, 200L, 25)]
    [InlineData(100L, 100L, 100)]
    [InlineData(500L, 100L, 100)]
    [InlineData(10L, null, -1)]
    [InlineData(10L, 0L, -1)]
    public void computes_percent(long downloaded, long? total, int expected)
    {
        Assert.Equal(expected, DownloadView.Percent(downloaded, total));
    }

    private static DownloadItem Item(DownloadStatus status, Category category) => new()
    {
        Request = new DownloadRequest("http://x/a", null, null, null, null, null, null),
        Status = status,
        Category = category,
    };

    [Theory]
    [InlineData(DownloadFilter.All, DownloadStatus.Failed, Category.Other, true)]
    [InlineData(DownloadFilter.Active, DownloadStatus.Downloading, Category.Other, true)]
    [InlineData(DownloadFilter.Active, DownloadStatus.Paused, Category.Other, true)]
    [InlineData(DownloadFilter.Active, DownloadStatus.Queued, Category.Other, true)]
    [InlineData(DownloadFilter.Active, DownloadStatus.Completed, Category.Other, false)]
    [InlineData(DownloadFilter.Active, DownloadStatus.Later, Category.Other, false)]
    [InlineData(DownloadFilter.Completed, DownloadStatus.Completed, Category.Archive, true)]
    [InlineData(DownloadFilter.Completed, DownloadStatus.Paused, Category.Archive, false)]
    [InlineData(DownloadFilter.Later, DownloadStatus.Later, Category.Archive, true)]
    [InlineData(DownloadFilter.Later, DownloadStatus.Queued, Category.Archive, false)]
    [InlineData(DownloadFilter.Documents, DownloadStatus.Completed, Category.Document, true)]
    [InlineData(DownloadFilter.Documents, DownloadStatus.Completed, Category.Software, false)]
    [InlineData(DownloadFilter.Software, DownloadStatus.Later, Category.Software, true)]
    [InlineData(DownloadFilter.Archives, DownloadStatus.Failed, Category.Archive, true)]
    [InlineData(DownloadFilter.Other, DownloadStatus.Failed, Category.Other, true)]
    [InlineData(DownloadFilter.Other, DownloadStatus.Failed, Category.Document, false)]
    public void filter_matches(DownloadFilter filter, DownloadStatus status, Category category, bool expected)
    {
        Assert.Equal(expected, DownloadView.Matches(filter, Item(status, category)));
    }

    [Fact]
    public void counts_per_filter()
    {
        var items = new[]
        {
            Item(DownloadStatus.Downloading, Category.Software),
            Item(DownloadStatus.Completed, Category.Document),
            Item(DownloadStatus.Completed, Category.Software),
            Item(DownloadStatus.Later, Category.Other),
        };
        Assert.Equal(4, DownloadView.Count(DownloadFilter.All, items));
        Assert.Equal(1, DownloadView.Count(DownloadFilter.Active, items));
        Assert.Equal(2, DownloadView.Count(DownloadFilter.Completed, items));
        Assert.Equal(1, DownloadView.Count(DownloadFilter.Later, items));
        Assert.Equal(2, DownloadView.Count(DownloadFilter.Software, items));
        Assert.Equal(0, DownloadView.Count(DownloadFilter.Archives, items));
    }

    [Theory]
    [InlineData(DownloadStatus.Downloading, null, "Đang tải")]
    [InlineData(DownloadStatus.Queued, null, "Đang chờ")]
    [InlineData(DownloadStatus.Paused, null, "Tạm dừng")]
    [InlineData(DownloadStatus.Later, null, "Chờ tải sau")]
    [InlineData(DownloadStatus.Completed, null, "Xong")]
    [InlineData(DownloadStatus.Failed, null, "Lỗi")]
    [InlineData(DownloadStatus.Failed, "Máy chủ báo lỗi 500", "Máy chủ báo lỗi 500")]
    public void status_text_prefers_error_message(DownloadStatus status, string? error, string expected)
    {
        var item = Item(status, Category.Other);
        item.ErrorMessage = error;
        Assert.Equal(expected, DownloadView.StatusText(item));
    }

    [Theory]
    [InlineData(DownloadStatus.Downloading, null, "Đang tải — Không tải tiếp được")]
    [InlineData(DownloadStatus.Paused, null, "Tạm dừng — Không tải tiếp được")]
    [InlineData(DownloadStatus.Queued, null, "Đang chờ")]
    [InlineData(DownloadStatus.Completed, null, "Xong")]
    [InlineData(DownloadStatus.Paused, "Máy chủ báo lỗi 500", "Máy chủ báo lỗi 500")]
    public void status_text_tells_when_download_cannot_resume(DownloadStatus status, string? error, string expected)
    {
        var item = Item(status, Category.Other);
        item.CanResume = false;
        item.ErrorMessage = error;
        Assert.Equal(expected, DownloadView.StatusText(item));
    }

    [Fact]
    public void time_shows_completion_then_added()
    {
        var added = new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);
        var item = new DownloadItem { Request = new DownloadRequest("https://h/a", null, null, null, null, null, null), AddedAt = added };
        Assert.Equal(added.ToLocalTime().ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture), DownloadView.TimeText(item));
        item.CompletedAt = added.AddMinutes(90);
        Assert.Equal(item.CompletedAt.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture), DownloadView.TimeText(item));
        Assert.Contains("Mất 1 giờ 30 phút", DownloadView.TimeTooltip(item));
    }
}
