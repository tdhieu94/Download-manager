using System.Globalization;

namespace DoMa.Core.Engine.Hls;

/// <summary>Khoá giải mã AES-128 của một đoạn (chuẩn HLS, không phải DRM).</summary>
public sealed record HlsKey(Uri Uri, byte[]? Iv);

public sealed record HlsSegment(Uri Uri, long Sequence, HlsKey? Key);

public sealed record HlsVariant(Uri Uri, long Bandwidth, string? Resolution);

/// <summary>
/// Danh sách phát HLS (.m3u8). Master: liệt kê các chất lượng (Variants). Media: liệt kê các đoạn (Segments).
/// </summary>
public sealed class HlsPlaylist
{
    public IReadOnlyList<HlsVariant> Variants { get; private init; } = [];
    public IReadOnlyList<HlsSegment> Segments { get; private init; } = [];

    /// <summary>Đoạn khởi tạo của fMP4 (#EXT-X-MAP); có thì ghép ra .mp4, không thì .ts.</summary>
    public Uri? InitSegment { get; private init; }

    /// <summary>Có #EXT-X-ENDLIST: video đã đủ. Không có thường là phát trực tiếp.</summary>
    public bool Ended { get; private init; }

    /// <summary>Lý do không tải được (DRM, kiểu mã hoá lạ…); null nếu tải được.</summary>
    public string? Unsupported { get; private init; }

    public bool IsMaster => Variants.Count > 0;

    public string Extension => InitSegment is null ? ".ts" : ".mp4";

    /// <summary>Link trông giống danh sách phát HLS (theo đuôi .m3u8 hoặc kiểu nội dung).</summary>
    public static bool Looks(string? url, string? contentType)
    {
        var type = contentType?.Split(';')[0].Trim() ?? "";
        if (type.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/x-mpegurl", StringComparison.OrdinalIgnoreCase)
            || type.Equals("audio/mpegurl", StringComparison.OrdinalIgnoreCase)
            || type.Equals("audio/x-mpegurl", StringComparison.OrdinalIgnoreCase))
            return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Chọn chất lượng cao nhất (băng thông lớn nhất).</summary>
    public HlsVariant? Best() => Variants.OrderByDescending(v => v.Bandwidth).FirstOrDefault();

    public static HlsPlaylist Parse(string text, Uri baseUri)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("#EXTM3U", StringComparison.Ordinal))
            return new HlsPlaylist { Unsupported = "Không phải danh sách phát HLS hợp lệ." };

        var variants = new List<HlsVariant>();
        var segments = new List<HlsSegment>();
        Uri? init = null;
        HlsKey? key = null;
        string? unsupported = null;
        var ended = false;
        long sequence = 0;
        (long Bandwidth, string? Resolution)? pendingVariant = null;
        var expectSegment = false;

        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith('#'))
            {
                var (tag, value) = SplitTag(line);
                switch (tag)
                {
                    case "#EXT-X-STREAM-INF":
                        var attrs = Attributes(value);
                        long.TryParse(attrs.GetValueOrDefault("BANDWIDTH"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bw);
                        pendingVariant = (bw, attrs.GetValueOrDefault("RESOLUTION"));
                        break;
                    case "#EXT-X-MEDIA-SEQUENCE":
                        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence);
                        break;
                    case "#EXTINF":
                        expectSegment = true;
                        break;
                    case "#EXT-X-ENDLIST":
                        ended = true;
                        break;
                    case "#EXT-X-MAP":
                        if (Attributes(value).GetValueOrDefault("URI") is { } mapUri && Resolve(baseUri, mapUri) is { } map)
                            init ??= map;
                        if (Attributes(value).ContainsKey("BYTERANGE"))
                            unsupported ??= "Danh sách phát dùng BYTERANGE, DoMa chưa hỗ trợ.";
                        break;
                    case "#EXT-X-BYTERANGE":
                        unsupported ??= "Danh sách phát dùng BYTERANGE, DoMa chưa hỗ trợ.";
                        break;
                    case "#EXT-X-KEY":
                        var k = Attributes(value);
                        var method = k.GetValueOrDefault("METHOD") ?? "NONE";
                        if (method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                            key = null;
                        else if (method.Equals("AES-128", StringComparison.OrdinalIgnoreCase)
                                 && k.GetValueOrDefault("URI") is { } keyUri && Resolve(baseUri, keyUri) is { } resolved)
                            key = new HlsKey(resolved, ParseIv(k.GetValueOrDefault("IV")));
                        else
                            unsupported ??= "Video được bảo vệ bản quyền (DRM), không thể tải.";
                        break;
                }
                continue;
            }

            var uri = Resolve(baseUri, line);
            if (uri is null)
                continue;
            if (pendingVariant is { } v)
            {
                variants.Add(new HlsVariant(uri, v.Bandwidth, v.Resolution));
                pendingVariant = null;
            }
            else if (expectSegment)
            {
                segments.Add(new HlsSegment(uri, sequence++, key));
                expectSegment = false;
            }
        }

        if (variants.Count == 0 && segments.Count == 0)
            unsupported ??= "Danh sách phát không có đoạn video nào.";
        return new HlsPlaylist { Variants = variants, Segments = segments, InitSegment = init, Ended = ended, Unsupported = unsupported };
    }

    private static (string Tag, string Value) SplitTag(string line)
    {
        var colon = line.IndexOf(':');
        return colon < 0 ? (line, "") : (line[..colon], line[(colon + 1)..]);
    }

    /// <summary>Đọc danh sách thuộc tính dạng KEY=VALUE,KEY="VALUE, có dấu phẩy".</summary>
    public static Dictionary<string, string> Attributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < text.Length)
        {
            var eq = text.IndexOf('=', i);
            if (eq < 0)
                break;
            var name = text[i..eq].Trim().TrimStart(',').Trim();
            i = eq + 1;
            string value;
            if (i < text.Length && text[i] == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0)
                    end = text.Length;
                value = text[(i + 1)..end];
                i = Math.Min(text.Length, end + 1);
            }
            else
            {
                var end = text.IndexOf(',', i);
                if (end < 0)
                    end = text.Length;
                value = text[i..end].Trim();
                i = end;
            }
            if (i < text.Length && text[i] == ',')
                i++;
            if (name.Length > 0)
                result[name] = value;
        }
        return result;
    }

    private static Uri? Resolve(Uri baseUri, string reference) =>
        Uri.TryCreate(baseUri, reference, out var uri) && uri.Scheme is "http" or "https" ? uri : null;

    private static byte[]? ParseIv(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
            return null;
        var digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        try
        {
            var bytes = Convert.FromHexString(digits.PadLeft(32, '0'));
            return bytes.Length == 16 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
