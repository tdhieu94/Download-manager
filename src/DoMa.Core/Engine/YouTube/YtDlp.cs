using System.Globalization;
using System.Text;
using System.Text.Json;
using DoMa.Core.Models;

namespace DoMa.Core.Engine.YouTube;

/// <summary>
/// Thông tin video lấy từ yt-dlp: tiêu đề, dung lượng ước tính của bản tốt nhất,
/// các độ phân giải có sẵn (cao xuống thấp), dung lượng bản chỉ có tiếng và có phụ đề hay không.
/// </summary>
public sealed record YouTubeInfo(string Title, long? Size, bool IsLive, IReadOnlyList<int>? Heights = null, long? AudioSize = null, bool HasSubtitles = false, int? Duration = null);

/// <summary>Một video trong playlist/kênh.</summary>
public sealed record PlaylistEntry(string Url, string Title);

/// <summary>Danh sách video của playlist/kênh.</summary>
public sealed record PlaylistInfo(string Title, IReadOnlyList<PlaylistEntry> Entries);

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

    /// <summary>Lấy hình và tiếng tốt nhất mà video có.</summary>
    public const string Format = "bv*+ba/b";

    /// <summary>
    /// Độ phân giải cao nhất trước (4K nếu video có 4K), rồi khung hình/giây. Cùng độ phân giải thì ưu tiên
    /// H.264 + AAC vì mở được trên mọi máy; trên 1080p YouTube chỉ có VP9/AV1 nên chọn VP9 trước AV1.
    /// </summary>
    public const string Sort = "res,fps,vcodec:h264,acodec:aac";

    public static IReadOnlyList<string> InfoArguments(string url, string? cookieFile, string? denoPath = null) =>
    [
        "-J", "--no-playlist", "--no-warnings", "--encoding", "utf-8", "-f", Format, "-S", Sort,
        .. Runtime(denoPath),
        .. Cookies(cookieFile),
        "--", url,
    ];

    /// <param name="outputStem">Đường dẫn tệp đích không có đuôi; yt-dlp tự thêm đuôi (.mp4).</param>
    public const int MaxPlaylistItems = 1000;

    /// <summary>Danh sách video của playlist/kênh (không tải gì, chỉ đọc tên và link).</summary>
    /// <param name="limit">Chỉ đọc chừng này video đầu danh sách (kênh: mới nhất trước).</param>
    public static IReadOnlyList<string> PlaylistArguments(string url, string? cookieFile, string? denoPath = null, int? limit = null) =>
    [
        "-J", "--flat-playlist", "--yes-playlist", "--no-warnings", "--encoding", "utf-8",
        "--playlist-end", Math.Clamp(limit ?? MaxPlaylistItems, 1, MaxPlaylistItems).ToString(CultureInfo.InvariantCulture),
        .. Runtime(denoPath),
        .. Cookies(cookieFile),
        "--", url,
    ];

    /// <summary>Phụ đề tiếng Việt và tiếng Anh (cả phụ đề tự động), gắn vào tệp video.</summary>
    public const string SubtitleLanguages = "vi,vi-VN,en,en-US,en-GB";

    public static string[] FormatArguments(MediaOptions? options)
    {
        options ??= MediaOptions.Default;
        if (options.AudioOnly)
            return ["-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0", .. Clip(options)];
        var sort = options.MaxHeight is { } h ? $"res:{h},fps,vcodec:h264,acodec:aac" : Sort;
        string[] video = ["-f", Format, "-S", sort, "--merge-output-format", "mp4", .. Clip(options)];
        return options.Subtitles
            ? [.. video, "--write-subs", "--write-auto-subs", "--sub-langs", SubtitleLanguages, "--convert-subs", "srt", "--embed-subs"]
            : video;
    }

    /// <summary>Chỉ tải một đoạn; cắt đúng giây (ffmpeg mã hoá lại phần đầu đoạn).</summary>
    private static string[] Clip(MediaOptions options) => options.IsClip
        ? ["--download-sections", $"*{options.ClipStart ?? 0}-{(options.ClipEnd is { } e ? e.ToString(CultureInfo.InvariantCulture) : "inf")}", "--force-keyframes-at-cuts"]
        : [];

    public static IReadOnlyList<string> DownloadArguments(string url, string outputStem, string? ffmpegDir, string? cookieFile, string? denoPath = null, MediaOptions? options = null,
        long limitBytesPerSec = 0) =>
    [
        "--no-playlist", "--no-warnings", "--encoding", "utf-8", "--newline", "--progress", "--no-mtime",
        .. (limitBytesPerSec > 0 ? ["--limit-rate", limitBytesPerSec.ToString(CultureInfo.InvariantCulture)] : Array.Empty<string>()),
        .. FormatArguments(options),
        .. (ffmpegDir is null ? Array.Empty<string>() : ["--ffmpeg-location", ffmpegDir]),
        "-o", outputStem.Replace("%", "%%") + ".%(ext)s",
        "--progress-template", "download:" + ProgressPrefix + "%(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s",
        "--print", "after_move:" + FilePrefix + "%(filepath)s", "--no-simulate",
        .. Runtime(denoPath),
        .. Cookies(cookieFile),
        "--", url,
    ];

    private static string[] Cookies(string? file) => file is null ? [] : ["--cookies", file];

    /// <summary>YouTube cần chạy JavaScript để lấy link video; chỉ cho yt-dlp dùng deno đi kèm DoMa.</summary>
    private static string[] Runtime(string? denoPath) => denoPath is null ? [] : ["--js-runtimes", "deno:" + denoPath];

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
        var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        long? audio = null;
        if (root.TryGetProperty("formats", out var all) && all.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in all.EnumerateArray())
            {
                var vcodec = Text(f, "vcodec");
                var acodec = Text(f, "acodec");
                if (vcodec is not null and not "none" && f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out var height) && height > 0)
                    heights.Add(height);
                if (vcodec == "none" && acodec is not null and not "none" && Size(f) is { } s && s > (audio ?? 0))
                    audio = s;
            }
        }
        var subs = HasLanguage(root, "subtitles") || HasLanguage(root, "automatic_captions");
        int? duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var secs) && secs > 0 ? (int)Math.Ceiling(secs) : null;
        return new YouTubeInfo(title, size, live, heights.Count > 0 ? [.. heights] : null, audio, subs, duration);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool HasLanguage(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var subs) || subs.ValueKind != JsonValueKind.Object)
            return false;
        var wanted = SubtitleLanguages.Split(',');
        return subs.EnumerateObject().Any(p => wanted.Contains(p.Name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Đọc kết quả <see cref="PlaylistArguments"/>: bỏ video riêng tư/đã xoá.</summary>
    public static PlaylistInfo ParsePlaylist(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var title = Text(root, "title") ?? "Playlist";
        var entries = new List<PlaylistEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                    continue;
                var name = Text(e, "title") ?? "";
                if (name is "[Private video]" or "[Deleted video]")
                    continue;
                var url = Text(e, "url") ?? Text(e, "webpage_url");
                if (url is null && Text(e, "id") is { Length: 11 } id)
                    url = "https://www.youtube.com/watch?v=" + id;
                if (url is null || !MediaLink.IsVideo(url))
                    continue;
                url = MediaLink.Normalize(url);
                if (seen.Add(url))
                    entries.Add(new PlaylistEntry(url, name.Length > 0 ? name : "Video " + (entries.Count + 1)));
            }
        }
        return new PlaylistInfo(title, entries);
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
            return (L.T("YouTube yêu cầu xác minh. Hãy mở YouTube trên trình duyệt (đăng nhập nếu được) rồi tải lại bằng DoMa."), false);
        if (Has("age") && Has("confirm"))
            return (L.T("Video giới hạn độ tuổi. Hãy đăng nhập YouTube trên trình duyệt rồi tải lại bằng DoMa."), false);
        if (Has("Private video"))
            return (L.T("Video riêng tư, không tải được."), false);
        if (Has("members-only") || Has("Join this channel"))
            return (L.T("Video chỉ dành cho hội viên kênh, không tải được."), false);
        if (Has("live event") || Has("is live") || Has("Premieres"))
            return (L.T("Video đang phát trực tiếp hoặc chưa công chiếu, hãy tải sau khi phát xong."), false);
        if (Has("Requested format is not available") || Has("Only images are available") || Has("JavaScript runtime") || Has("n challenge"))
            return (WithRaw(L.T("YouTube không cho lấy video lúc này. DoMa sẽ cập nhật công cụ, hãy thử lại sau ít phút."), error), false);
        if (Has("Video unavailable") || Has("video is not available") || Has("content isn't available") || Has("not available in your country"))
            return (WithRaw(L.T("Video không còn hoặc bị chặn ở khu vực của bạn."), error), false);
        if (Has("subtitle"))
            return (WithRaw(L.T("Không tải được phụ đề. Hãy bỏ chọn “Tải kèm phụ đề” rồi tải lại."), error), false);
        if (Has("login required") || Has("log in") || Has("cookies") && Has("authentication"))
            return (L.T("Trang yêu cầu đăng nhập để xem video này. Hãy đăng nhập trên trình duyệt rồi tải lại bằng DoMa."), false);
        if (Has("timed out") || Has("Unable to download") || Has("getaddrinfo") || Has("Connection") || Has("Network"))
            return (L.T("Mất kết nối khi tải video YouTube."), true);
        if (Has("DRM"))
            return (L.T("Video có bảo vệ bản quyền (DRM), không tải được."), false);
        return (error.Length > 0 ? "yt-dlp: " + error : L.T("Không tải được video."), false);
    }

    /// <summary>Kèm nguyên văn lỗi của yt-dlp (rút gọn) để dễ tìm nguyên nhân.</summary>
    private static string WithRaw(string message, string raw) =>
        raw.Length == 0 ? message : $"{message} (yt-dlp: {(raw.Length > 160 ? raw[..160] + "…" : raw)})";

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
