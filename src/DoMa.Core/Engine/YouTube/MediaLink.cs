namespace DoMa.Core.Engine.YouTube;

/// <summary>
/// Các link trang video mà DoMa tải bằng yt-dlp: video YouTube, playlist/kênh YouTube,
/// và video trên Facebook, TikTok, Instagram, X (Twitter).
/// </summary>
public static class MediaLink
{
    /// <summary>Link một video (không phải playlist) trên các trang được hỗ trợ.</summary>
    public static bool IsVideo(string? url) => YouTubeLink.IsVideo(url) || OtherSite(url) is not null;

    /// <summary>Link được tải bằng yt-dlp: một video hoặc playlist/kênh YouTube.</summary>
    public static bool IsSupported(string? url) => IsVideo(url) || IsPlaylist(url);

    /// <summary>Playlist hoặc kênh YouTube (nhiều video).</summary>
    public static bool IsPlaylist(string? url) => Playlist(url) is not null;

    /// <summary>Dạng chuẩn của link playlist/kênh: kênh không ghi tab thì lấy tab Video.</summary>
    public static string? Playlist(string? url)
    {
        if (YouTubeHost(url) is not { } uri)
            return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.AbsolutePath.TrimEnd('/') == "/playlist")
            return QueryValue(uri.Query, "list") is { Length: > 0 } list ? "https://www.youtube.com/playlist?list=" + Uri.EscapeDataString(list) : null;
        if (parts.Length == 0)
            return null;
        string channel;
        if (parts[0].StartsWith('@') && parts[0].Length > 1)
            channel = parts[0];
        else if (parts[0] is "channel" or "c" or "user" && parts.Length > 1)
            channel = parts[0] + "/" + parts[1];
        else
            return null;
        var rest = parts.Skip(channel.Contains('/') ? 2 : 1).FirstOrDefault();
        var tab = rest is "videos" or "shorts" or "streams" ? rest : rest is null or "featured" ? "videos" : null;
        return tab is null ? null : $"https://www.youtube.com/{channel}/{tab}";
    }

    /// <summary>Playlist trong link xem video ("watch?v=…&amp;list=…"), để đề nghị tải cả playlist.</summary>
    public static string? PlaylistOfVideo(string? url) =>
        YouTubeHost(url) is { } uri && uri.AbsolutePath == "/watch" && QueryValue(uri.Query, "list") is { Length: > 0 } list
            && !list.StartsWith("RD", StringComparison.Ordinal) // "Mix" do YouTube tự tạo, không có hồi kết
            ? "https://www.youtube.com/playlist?list=" + Uri.EscapeDataString(list)
            : null;

    /// <summary>Link gửi cho yt-dlp: video YouTube đưa về dạng chuẩn, link khác giữ nguyên (bỏ phần #).</summary>
    public static string Normalize(string url) =>
        YouTubeLink.Normalize(url) ?? Playlist(url)
        ?? (Uri.TryCreate(url, UriKind.Absolute, out var uri) ? new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri : url);

    /// <summary>Tên trang để hiện cho người dùng: "YouTube", "Facebook"…; null nếu không hỗ trợ.</summary>
    public static string? SiteName(string? url) =>
        YouTubeLink.IsVideo(url) || IsPlaylist(url) ? "YouTube" : OtherSite(url);

    /// <summary>Tên miền gốc cho tệp cookie của yt-dlp, vd. ".facebook.com".</summary>
    public static string CookieDomain(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return ".youtube.com";
        var host = uri.Host.ToLowerInvariant();
        if (host == "youtu.be")
            return ".youtube.com";
        if (host == "fb.watch")
            return ".facebook.com";
        var labels = host.Split('.');
        return "." + (labels.Length >= 2 ? string.Join('.', labels[^2..]) : host);
    }

    private static string? OtherSite(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        var host = Bare(uri.Host);
        var path = uri.AbsolutePath;
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool First(params string[] names) => parts.Length > 1 && names.Contains(parts[0], StringComparer.OrdinalIgnoreCase);
        switch (host)
        {
            case "fb.watch":
                return parts.Length > 0 ? "Facebook" : null;
            case "facebook.com":
                if (path.TrimEnd('/') == "/watch" && QueryValue(uri.Query, "v") is { Length: > 0 })
                    return "Facebook";
                if (First("reel") || (parts.Length > 2 && parts[0] == "share" && parts[1] is "v" or "r") || (parts.Length > 2 && parts[1] == "videos"))
                    return "Facebook";
                return null;
            case "tiktok.com":
                return parts.Length > 2 && parts[0].StartsWith('@') && parts[1] == "video" ? "TikTok" : First("t") ? "TikTok" : null;
            case "vm.tiktok.com" or "vt.tiktok.com":
                return parts.Length > 0 ? "TikTok" : null;
            case "instagram.com":
                return First("reel", "reels", "p", "tv") ? "Instagram" : null;
            case "x.com" or "twitter.com":
                return parts.Length > 2 && parts[1] == "status" && parts[2].All(char.IsAsciiDigit) ? "X" : null;
            default:
                return null;
        }
    }

    private static string Bare(string host)
    {
        host = host.ToLowerInvariant();
        foreach (var prefix in new[] { "www.", "m.", "web.", "mobile." })
        {
            if (host.StartsWith(prefix, StringComparison.Ordinal))
                return host[prefix.Length..];
        }
        return host;
    }

    private static Uri? YouTubeHost(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        return Bare(uri.Host) is "youtube.com" or "music.youtube.com" ? uri : null;
    }

    private static string? QueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == name)
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }
}
