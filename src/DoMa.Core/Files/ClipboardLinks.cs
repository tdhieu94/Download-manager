namespace DoMa.Core.Files;

/// <summary>Nhận ra link tải trong nội dung vừa copy, để DoMa đề nghị tải giúp.</summary>
public static class ClipboardLinks
{
    /// <summary>Đuôi tệp media luôn được coi là link tải, ngoài các đuôi trong phần Phân loại.</summary>
    public static readonly string[] MediaExtensions =
        ["mp4", "mkv", "webm", "avi", "mov", "wmv", "flv", "m4v", "mp3", "m4a", "flac", "wav", "aac", "ogg", "opus"];

    /// <summary>
    /// Trả về link nếu nội dung copy chỉ là một link http(s) trỏ tới tệp có đuôi nằm trong danh sách;
    /// ngược lại trả về null (đoạn văn, link trang web thường, nhiều link…).
    /// </summary>
    public static string? Extract(string? text, IEnumerable<string> extensions)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 4096 || value.Any(char.IsWhiteSpace))
            return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        var name = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/'));
        var ext = Path.GetExtension(name).TrimStart('.');
        if (ext.Length == 0)
            return null;
        var known = extensions.Concat(MediaExtensions).Select(e => e.Trim().TrimStart('.'));
        return known.Contains(ext, StringComparer.OrdinalIgnoreCase) ? uri.AbsoluteUri : null;
    }
}
