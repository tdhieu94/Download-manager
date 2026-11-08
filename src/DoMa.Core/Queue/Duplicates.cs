using DoMa.Core.Models;

namespace DoMa.Core.Queue;

/// <summary>Tìm tệp đã có trong danh sách trùng với link vừa bắt, để nhắc người dùng trước khi tải trùng.</summary>
public static class Duplicates
{
    /// <summary>
    /// Trùng khi cùng link (bỏ phần #…), hoặc cùng tên tệp và cùng dung lượng với một tệp đã tải xong.
    /// Ưu tiên tệp chưa xong (đang có trong hàng đợi), sau đó tệp tải gần nhất.
    /// </summary>
    public static DownloadItem? Find(IEnumerable<DownloadItem> items, string url, string? finalUrl, string fileName, long? size)
    {
        var links = new[] { url, finalUrl }.Select(Normalize).Where(u => u is not null).ToHashSet();
        var name = RefreshWatch.Normalize(fileName);
        return items
            .Where(i => links.Contains(Normalize(i.Request.Url))
                        || (i.Status == DownloadStatus.Completed && size is > 0 && i.TotalBytes == size
                            && name.Length > 0 && RefreshWatch.Normalize(Path.GetFileName(i.TargetPath)) == name))
            .OrderBy(i => i.Status == DownloadStatus.Completed)
            .ThenByDescending(i => i.CompletedAt ?? i.AddedAt)
            .FirstOrDefault();
    }

    private static string? Normalize(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        var builder = new UriBuilder(uri) { Fragment = "" };
        if (builder.Uri.IsDefaultPort)
            builder.Port = -1;
        return builder.Uri.AbsoluteUri;
    }
}
