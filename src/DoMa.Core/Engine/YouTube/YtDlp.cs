using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DoMa.Core.Engine.YouTube;

/// <summary>Thông tin video lấy từ yt-dlp: tiêu đề và dung lượng ước tính của bản sẽ tải.</summary>
public sealed record YouTubeInfo(string Title, long? Size, bool IsLive);

/// <summary>Một dòng tiến độ yt-dlp in ra (theo mẫu <see cref="YtDlp.ProgressPrefix"/>).</summary>
public sealed record YtDlpProgress(long Downloaded, long? Total);

/// <summary>
/// Cách gọi công cụ yt-dlp và đọc kết quả của nó. DoMa không tự giải mã trang YouTube (thay đổi liên tục),
/// mà để yt-dlp, công cụ mã nguồn mở được cập nhật thường xuyên, làm việc đó.
/// </summary>
public static class YtDlp
{
    public const string ProgressPrefix = "DOMA_P ";
    public const string FilePrefix = "DOMA_FILE:";

    /// <summary>Ưu tiên H.264 + AAC (mở được trên mọi máy) tới 1080p; không có thì lấy bản tốt nhất còn lại.</summary>
    public const string Format = "bv*[height<=1080][vcodec^=avc1]+ba[ext=m4a]/bv*[height<=1080]+ba/b[height<=1080]/b";

    public static IReadOnlyList<string> InfoArguments(string url, string? cookieFile) =>
    [
        "-J", "--no-playlist", "--no-warnings", "--encoding", "utf-8", "-f", Format,
        .. Cookies(cookieFile),
        "--", url,
    ];

    /// <param name="outputStem">Đường dẫn tệp đích không có đuôi; yt-dlp tự thêm đuôi (.mp4).</param>
    public static IReadOnlyList<string> DownloadArguments(string url, string outputStem, string? ffmpegDir, string? cookieFile) =>
    [
        "--no-playlist", "--no-warnings", "--encoding", "utf-8", "--newline", "--progress", "--no-mtime",
        "-f", Format, "--merge-output-format", "mp4",
        .. (ffmpegDir is null ? Array.Empty<string>() : ["--ffmpeg-location", ffmpegDir]),
        "-o", outputStem.Replace("%", "%%") + ".%(ext)s",
        "--progress-template", "download:" + ProgressPrefix + "%(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s",
        "--print", "after_move:" + FilePrefix + "%(filepath)s", "--no-simulate",
        .. Cookies(cookieFile),
        "--", url,
    ];

    private static string[] Cookies(string? file) => file is null ? [] : ["--cookies", file];

    public static YtDlpProgress? ParseProgress(string line)
    {
        if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            return null;
        var parts = line[ProgressPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || Number(parts[0]) is not { } done)
            return null;
        var total = parts.Length > 1 ? Number(parts[1]) : null;
        total ??= parts.Length > 2 ? Number(parts[2]) : null;
        return new YtDlpProgress(done, total);
    }

    private static long? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? (long)v : null;

    public static string? ParseFile(string line) =>
        line.StartsWith(FilePrefix, StringComparison.Ordinal) && line.Length > FilePrefix.Length ? line[FilePrefix.Length..].Trim() : null;

    public static YouTubeInfo ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "YouTube";
        long? size = null;
        if (root.TryGetProperty("requested_formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            long sum = 0;
            var known = true;
            foreach (var f in formats.EnumerateArray())
            {
                if (Size(f) is { } s)
                    sum += s;
                else
                    known = false;
            }
            size = known && sum > 0 ? sum : null;
        }
        size ??= Size(root);
        var live = root.TryGetProperty("is_live", out var l) && l.ValueKind == JsonValueKind.True;
        return new YouTubeInfo(title, size, live);
    }

    private static long? Size(JsonElement e)
    {
        foreach (var name in new[] { "filesize", "filesize_approx" })
        {
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d > 0)
                return (long)d;
        }
        return null;
    }

    /// <summary>Đổi lỗi tiếng Anh của yt-dlp sang câu dễ hiểu.</summary>
    public static (string Message, bool Network) DescribeError(IEnumerable<string> stderr)
    {
        var error = stderr.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal))?[6..].Trim() ?? "";
        bool Has(string s) => error.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("Sign in to confirm") || Has("not a bot"))
            return ("YouTube yêu cầu xác minh. Hãy mở YouTube trên trình duyệt (đăng nhập nếu được) rồi tải lại bằng DoMa.", false);
        if (Has("age") && Has("confirm"))
            return ("Video giới hạn độ tuổi. Hãy đăng nhập YouTube trên trình duyệt rồi tải lại bằng DoMa.", false);
        if (Has("Private video"))
            return ("Video riêng tư, không tải được.", false);
        if (Has("members-only") || Has("Join this channel"))
            return ("Video chỉ dành cho hội viên kênh, không tải được.", false);
        if (Has("live event") || Has("is live") || Has("Premieres"))
            return ("Video đang phát trực tiếp hoặc chưa công chiếu, hãy tải sau khi phát xong.", false);
        if (Has("Video unavailable") || Has("not available"))
            return ("Video không còn hoặc bị chặn ở khu vực của bạn.", false);
        if (Has("timed out") || Has("Unable to download") || Has("getaddrinfo") || Has("Connection") || Has("Network"))
            return ("Mất kết nối khi tải video YouTube.", true);
        if (Has("DRM"))
            return ("Video có bảo vệ bản quyền (DRM), không tải được.", false);
        return (error.Length > 0 ? "YouTube: " + error : "Không tải được video YouTube.", false);
    }

    /// <summary>Tệp cookie dạng Netscape cho yt-dlp, từ chuỗi Cookie của trình duyệt ("a=1; b=2").</summary>
    public static string NetscapeCookies(string cookieHeader, string domain = ".youtube.com")
    {
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (name.IndexOfAny(['\t', '\n', '\r']) >= 0 || value.IndexOfAny(['\t', '\n', '\r']) >= 0)
                continue;
            sb.Append(domain).Append("\tTRUE\t/\tTRUE\t0\t").Append(name).Append('\t').Append(value).Append('\n');
        }
        return sb.ToString();
    }
}
