using DoMa.Core.Models;

namespace DoMa.Core.Files;

/// <summary>Nhóm hiển thị trong hộp "Tải nhiều link": theo loại tệp, thêm nhóm video/nhạc và trang web.</summary>
public enum LinkKind { Document, Software, Archive, Media, Other }

public sealed record BatchLink(string Url, string FileName, LinkKind Kind, string Host)
{
    /// <summary>Link trỏ tới một tệp (có đuôi tệp đã biết) hoặc trang video tải được, không phải trang web thường.</summary>
    public bool IsFile => Kind != LinkKind.Other;

    /// <summary>Trang video (YouTube, Facebook…) tải bằng yt-dlp.</summary>
    public bool IsMediaPage => Engine.YouTube.MediaLink.IsVideo(Url);
}

public static class BatchLinks
{
    public const int MaxLinks = 1000;

    /// <summary>
    /// Lọc danh sách link lấy từ trang: chỉ http(s), bỏ trùng và bỏ phần #, đặt tên tệp theo đường dẫn
    /// (hoặc chữ hiển thị của link nếu đường dẫn không có tên), xếp loại theo đuôi tệp.
    /// </summary>
    public static List<BatchLink> Prepare(IEnumerable<(string Url, string? Text)> links, CategoryResolver resolver)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<BatchLink>();
        foreach (var (raw, text) in links)
        {
            if (result.Count >= MaxLinks)
                break;
            if (!Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                continue;
            var url = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
            if (!seen.Add(url))
                continue;
            var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/')));
            var ext = Path.GetExtension(name).TrimStart('.');
            var kind = KindOf(ext, resolver, name);
            if (Engine.YouTube.MediaLink.IsVideo(url))
            {
                // Trang video (YouTube, TikTok…): tên thật lấy từ tiêu đề video lúc thêm vào danh sách.
                kind = LinkKind.Media;
                name = string.IsNullOrWhiteSpace(text) ? $"Video {Engine.YouTube.MediaLink.SiteName(url)}" : text.Trim();
            }
            else if (name.Length == 0 || ext.Length == 0)
                name = string.IsNullOrWhiteSpace(text) ? (name.Length > 0 ? name : uri.Host) : text.Trim();
            result.Add(new BatchLink(url, FileNameResolver.Sanitize(name), kind, uri.Host));
        }
        return result;
    }

    private static LinkKind KindOf(string ext, CategoryResolver resolver, string name)
    {
        if (ext.Length == 0)
            return LinkKind.Other;
        if (ClipboardLinks.MediaExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return LinkKind.Media;
        return resolver.Resolve(name) switch
        {
            Category.Document => LinkKind.Document,
            Category.Software => LinkKind.Software,
            Category.Archive => LinkKind.Archive,
            Category.Media => LinkKind.Media,
            _ => LinkKind.Other,
        };
    }
}
