using System.Text.Json;

namespace DoMa.Core.Updates;

/// <summary>Một bản phát hành trên GitHub có bộ cài DoMa đi kèm.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string SetupName, string SetupUrl, string? Sha256, string? Sha256Url, string? PageUrl, string? Notes = null);

public static class ReleaseParser
{
    public const string SetupPrefix = "DoMa-Setup-";

    /// <summary>Đọc "v1.2.3" / "1.2.3" thành Version (luôn đủ 3 thành phần). Trả về null nếu không hợp lệ.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0)
            core = core[..cut];
        if (!Version.TryParse(core.Contains('.') ? core : core + ".0", out var v))
            return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>Chuẩn hoá Version của assembly (1.2.3.0) về 3 thành phần để so sánh.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Phân tích JSON của API /releases/latest. Bỏ qua bản nháp, bản thử nghiệm, hoặc không có bộ cài.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (Bool(root, "draft") || Bool(root, "prerelease"))
            return null;
        var tag = Str(root, "tag_name");
        var version = ParseVersion(tag);
        if (tag is null || version is null || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        string? setupName = null, setupUrl = null, digest = null, shaUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = Str(asset, "name");
            var url = Str(asset, "browser_download_url");
            if (name is null || url is null || !IsHttps(url))
                continue;
            if (name.StartsWith(SetupPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                setupName = name;
                setupUrl = url;
                var d = Str(asset, "digest");
                if (d is not null && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    digest = d["sha256:".Length..].ToLowerInvariant();
            }
        }
        if (setupName is null || setupUrl is null)
            return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (string.Equals(Str(asset, "name"), setupName + ".sha256", StringComparison.OrdinalIgnoreCase) && Str(asset, "browser_download_url") is { } u && IsHttps(u))
                shaUrl = u;
        }
        return new ReleaseInfo(version, tag, setupName, setupUrl, digest, shaUrl, Str(root, "html_url"), Str(root, "body"));
    }

    /// <summary>Lấy mã băm từ nội dung file .sha256 (định dạng "HASH" hoặc "HASH  tên-file").</summary>
    public static string? ParseShaFile(string text)
    {
        var first = text.Trim().Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is { Length: 64 } && first.All(Uri.IsHexDigit) ? first.ToLowerInvariant() : null;
    }

    public static bool IsNewer(ReleaseInfo release, Version current) => release.Version > Normalize(current);

    static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
