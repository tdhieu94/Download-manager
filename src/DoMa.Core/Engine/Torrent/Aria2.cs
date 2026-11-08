using System.Globalization;
using System.Text.RegularExpressions;

namespace DoMa.Core.Engine.Torrent;

/// <summary>Cách gọi aria2c (công cụ tải torrent mã nguồn mở) và đọc những gì nó in ra.</summary>
public static partial class Aria2
{
    /// <param name="dir">Thư mục chứa nội dung torrent (torrent nhiều tệp tự tạo thư mục con theo tên torrent).</param>
    public static IReadOnlyList<string> Arguments(string url, string dir, string? cookies, string? referrer, long? limitBytesPerSec = null)
    {
        var args = new List<string>
        {
            "--dir=" + dir,
            "--seed-time=0",
            "--continue=true",
            "--file-allocation=none",
            "--auto-save-interval=5",
            "--follow-torrent=mem",
            "--bt-save-metadata=false",
            "--summary-interval=0",
            "--console-log-level=notice",
            "--enable-color=false",
            "--human-readable=false",
            "--show-console-readout=true",
            "--allow-overwrite=false",
            "--auto-file-renaming=false",
        };
        if (limitBytesPerSec is > 0)
            args.Add("--max-overall-download-limit=" + limitBytesPerSec.Value.ToString(CultureInfo.InvariantCulture));
        if (!url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(cookies) && cookies.IndexOfAny(['\r', '\n']) < 0)
                args.Add("--header=Cookie: " + cookies);
            if (!string.IsNullOrWhiteSpace(referrer))
                args.Add("--referer=" + referrer);
        }
        args.Add(url);
        return args;
    }

    /// <summary>Dòng tiến độ, vd. "[#2089b0 3500000B/33000000B(10%) CN:8 SD:3 DL:1200000B ETA:20s]".</summary>
    public static (long Done, long? Total, int? Seeders)? ParseProgress(string line)
    {
        var m = Progress().Match(line);
        if (!m.Success)
            return null;
        var done = Bytes(m.Groups[1].Value, m.Groups[2].Value);
        var total = Bytes(m.Groups[3].Value, m.Groups[4].Value);
        if (done is null)
            return null;
        int? seeders = int.TryParse(m.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : null;
        return (done.Value, total is > 0 ? total : null, seeders);
    }

    private static long? Bytes(string number, string unit)
    {
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return null;
        var scale = unit.ToUpperInvariant() switch
        {
            "KIB" => 1L << 10,
            "MIB" => 1L << 20,
            "GIB" => 1L << 30,
            "TIB" => 1L << 40,
            _ => 1L,
        };
        return (long)(v * scale);
    }

    /// <summary>Đường dẫn tệp vừa tải xong trong dòng "Download complete: …" (bỏ qua phần tải thông tin torrent).</summary>
    public static string? ParseComplete(string line)
    {
        const string marker = "Download complete: ";
        var at = line.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var path = line[(at + marker.Length)..].Trim();
        return path.StartsWith("[MEMORY]", StringComparison.Ordinal) || path.StartsWith("[METADATA]", StringComparison.Ordinal) || path.Length == 0 ? null : path;
    }

    /// <summary>Mục nằm ngay trong <paramref name="dir"/> chứa tệp vừa tải: thư mục torrent, hoặc chính tệp nếu torrent một tệp.</summary>
    public static string TopEntry(string dir, string file)
    {
        var relative = Path.GetRelativePath(dir, file);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return file;
        var first = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], 2)[0];
        return Path.Combine(dir, first);
    }

    /// <summary>Mã lỗi aria2c ra câu dễ hiểu.</summary>
    public static (string Message, bool Network) DescribeExit(int code, IEnumerable<string> output)
    {
        var detail = output.LastOrDefault(l => l.Contains("errorCode=", StringComparison.Ordinal) || l.Contains("Exception:", StringComparison.Ordinal))?.Trim();
        var message = code switch
        {
            2 => ("Không kết nối được với ai đang chia sẻ torrent này (hết thời gian chờ).", true),
            3 => ("Không tìm thấy tệp torrent.", false),
            6 or 19 => ("Mất kết nối mạng khi tải torrent.", true),
            9 => ("Ổ đĩa không đủ chỗ trống.", false),
            13 => ("Tệp đã có sẵn ở thư mục lưu.", false),
            16 or 17 or 18 => ("Không ghi được tệp vào thư mục lưu.", false),
            24 => ("Trang yêu cầu đăng nhập để tải tệp .torrent.", false),
            26 or 27 or 28 => ("Tệp .torrent hoặc link magnet không hợp lệ.", false),
            _ => ("Không tải được torrent.", false),
        };
        return (detail is { Length: > 0 } ? $"{message.Item1} (aria2: {(detail.Length > 160 ? detail[..160] + "…" : detail)})" : message.Item1, message.Item2);
    }

    [GeneratedRegex(@"\[#\w+\s+([\d.]+)(B|KiB|MiB|GiB|TiB)/([\d.]+)(B|KiB|MiB|GiB|TiB)(?:\(\d+%\))?(?:\s+CN:\d+)?(?:\s+SD:(\d+))?")]
    private static partial Regex Progress();
}
