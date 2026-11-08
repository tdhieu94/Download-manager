using System.Text.RegularExpressions;

namespace DoMa.Core.Engine.YouTube;

/// <summary>Nhận ra link trang video YouTube (watch, shorts, youtu.be, embed…) và đưa về một dạng chuẩn.</summary>
public static partial class YouTubeLink
{
    /// <summary>https://www.youtube.com/watch?v=ID nếu là link video YouTube; null nếu không phải.</summary>
    public static string? Normalize(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        var host = uri.Host.ToLowerInvariant();
        foreach (var prefix in new[] { "www.", "m.", "music." })
        {
            if (host.StartsWith(prefix, StringComparison.Ordinal))
            {
                host = host[prefix.Length..];
                break;
            }
        }
        var parts = uri.AbsolutePath.Split('/');
        string? id = null;
        if (host == "youtu.be")
            id = parts.Length > 1 ? parts[1] : null;
        else if (host is "youtube.com" or "youtube-nocookie.com")
        {
            if (uri.AbsolutePath == "/watch")
                id = QueryValue(uri.Query, "v");
            else if (parts.Length > 2 && parts[1] is "shorts" or "embed" or "live" or "v")
                id = parts[2];
        }
        return id is not null && VideoId().IsMatch(id) ? $"https://www.youtube.com/watch?v={id}" : null;
    }

    public static bool IsVideo(string? url) => Normalize(url) is not null;

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

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoId();
}
