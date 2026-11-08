namespace DoMa.Core.Engine.Torrent;

/// <summary>Link torrent: "magnet:?xt=urn:btih:…" hoặc link http(s) tới tệp .torrent.</summary>
public static class TorrentLink
{
    public static bool IsMagnet(string? url) =>
        url is not null && url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) && url.Contains("xt=urn:bt", StringComparison.OrdinalIgnoreCase);

    public static bool IsTorrent(string? url, string? mime = null)
    {
        if (IsMagnet(url))
            return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;
        return uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mime?.Split(';')[0].Trim(), "application/x-bittorrent", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Tên hiển thị: tham số dn của magnet, tên tệp .torrent (bỏ đuôi), hoặc mã băm.</summary>
    public static string DisplayName(string url)
    {
        if (IsMagnet(url))
        {
            string? hash = null;
            foreach (var pair in url[(url.IndexOf('?') + 1)..].Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = pair[..eq];
                var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                if (key == "dn" && value.Trim().Length > 0)
                    return value.Trim();
                if (key == "xt")
                    hash ??= value.Split(':').LastOrDefault();
            }
            return "Torrent " + (hash is { Length: > 8 } h ? h[..8] : hash ?? "");
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var name = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(uri.AbsolutePath));
            if (name.Length > 0)
                return name;
        }
        return "Torrent";
    }
}
