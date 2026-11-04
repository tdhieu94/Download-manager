using System.Text;

namespace DoMa.Core.Files;

public static class FileNameResolver
{
    private const int MaxLength = 200;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>
    /// Sửa tên file bị lỗi mã hoá: máy chủ gửi tên UTF-8 thô trong header (bị đọc thành Latin-1, ví dụ "TÃ i liá»‡u"),
    /// tên còn dạng %XX, hoặc dấu tiếng Việt ở dạng tổ hợp (NFD). Kết quả luôn ở dạng chuẩn NFC.
    /// </summary>
    public static string RepairEncoding(string name)
    {
        var text = name;
        if (text.Contains('%'))
        {
            try
            {
                var unescaped = Uri.UnescapeDataString(text);
                if (!unescaped.Contains('\uFFFD'))
                    text = unescaped;
            }
            catch (UriFormatException)
            {
            }
        }
        if (text.Any(c => c >= 0x80) && text.All(c => c <= 0xFF))
        {
            try
            {
                text = StrictUtf8.GetString(Encoding.Latin1.GetBytes(text));
            }
            catch (DecoderFallbackException)
            {
                // Không phải UTF-8 đọc nhầm: giữ nguyên (tên Latin-1 thật như "café.pdf").
            }
        }
        return text.Normalize(NormalizationForm.FormC);
    }

    public static string Sanitize(string name)
    {
        name = RepairEncoding(name);
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray())
            .Trim(' ')
            .TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned.All(c => c == '.'))
            return "download";

        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (Reserved.Contains(stem))
            cleaned = "_" + cleaned;

        if (cleaned.Length > MaxLength)
        {
            var extPart = Path.GetExtension(cleaned);
            if (extPart.Length >= MaxLength)
                return cleaned[..MaxLength];
            cleaned = Path.GetFileNameWithoutExtension(cleaned)[..(MaxLength - extPart.Length)] + extPart;
        }
        return cleaned;
    }

    public static string MakeUnique(string fullPath, Func<string, bool> isTaken)
    {
        if (!isTaken(fullPath))
            return fullPath;

        var dir = Path.GetDirectoryName(fullPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!isTaken(candidate))
                return candidate;
        }
    }
}
