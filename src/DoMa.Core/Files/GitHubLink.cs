namespace DoMa.Core.Files;

/// <summary>
/// Link trang GitHub mà người dùng hay dán nhầm thay cho link tệp: trang xem một tệp (…/blob/…) hoặc trang kho mã.
/// Những link này trả về trang web; DoMa đổi sang link tải tệp tương ứng.
/// </summary>
public static class GitHubLink
{
    private static readonly HashSet<string> NotOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "apps", "collections", "contact", "customer-stories", "enterprise", "events", "explore", "features", "issues",
        "login", "join", "marketplace", "new", "notifications", "orgs", "organizations", "pricing", "pulls", "search",
        "security", "settings", "sponsors", "team", "topics", "trending", "users",
    };

    /// <summary>
    /// Link tải tệp ứng với một link trang GitHub; null nếu link không phải loại đổi được.
    /// Trang xem tệp → tệp gốc; trang kho mã (hoặc một nhánh/tag của nó) → tệp .zip mã nguồn.
    /// </summary>
    public static string? ToFileUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
            return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || NotOwners.Contains(parts[0]))
            return null;
        var repo = $"https://github.com/{parts[0]}/{(parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1])}";
        if (parts.Length == 2)
            return repo + "/archive/HEAD.zip";
        // Trang xem một tệp: …/blob/<nhánh>/<đường dẫn tệp>.
        if (parts[2] == "blob" && parts.Length >= 5)
            return $"{repo}/raw/{string.Join('/', parts[3..])}";
        // Trang một nhánh hoặc tag (không đi vào thư mục con): tải cả mã nguồn của nhánh/tag đó.
        if (parts[2] == "tree" && parts.Length == 4)
            return $"{repo}/archive/{parts[3]}.zip";
        return null;
    }
}
