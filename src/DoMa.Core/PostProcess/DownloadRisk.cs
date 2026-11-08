using System.Net;

namespace DoMa.Core.PostProcess;

public enum RiskLevel { None, Caution, High }

public record RiskReport(RiskLevel Level, IReadOnlyList<string> Reasons)
{
    public static readonly RiskReport Safe = new(RiskLevel.None, []);
}

/// <summary>
/// Đánh giá nhanh một lượt tải trước khi bắt đầu, chỉ dựa vào link, tên tệp và kiểu nội dung
/// (chưa có tệp nên chưa quét virus được). Không gửi gì ra ngoài.
/// </summary>
public static class DownloadRisk
{
    private static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".com", ".scr", ".pif", ".cpl", ".msc",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ps1", ".psm1", ".hta", ".lnk", ".jar",
        ".reg", ".dll", ".sys", ".inf", ".iso", ".img", ".vhd", ".vhdx", ".application", ".appref-ms",
    };

    private static readonly HashSet<string> Disguise = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".csv",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".mp3", ".mp4", ".mkv", ".avi", ".mov",
        ".zip", ".rar", ".7z",
    };

    private static readonly string[] ExecutableContentTypes =
    [
        "application/x-msdownload", "application/x-dosexec", "application/x-msdos-program",
        "application/x-ms-installer", "application/x-msi", "application/vnd.microsoft.portable-executable",
    ];

    // Ký tự đảo chiều/ẩn hay dùng để giả tên tệp, ví dụ "hoadon‮fdp.exe" hiện thành "hoadonexe.pdf".
    private static readonly char[] HiddenChars = ['‮', '‭', '‪', '‫', '⁦', '⁧', '⁨', '​', '‎', '‏'];

    public static bool IsExecutable(string fileName) => Executable.Contains(Path.GetExtension(fileName));

    public static RiskReport Assess(string url, string? finalUrl, string fileName, string? contentType)
    {
        var reasons = new List<string>();
        var level = RiskLevel.None;
        void Add(RiskLevel l, string reason)
        {
            reasons.Add(reason);
            if (l > level)
                level = l;
        }

        var name = fileName.Trim();
        var ext = Path.GetExtension(name);
        var executable = Executable.Contains(ext);

        if (name.IndexOfAny(HiddenChars) >= 0)
            Add(RiskLevel.High, L.T("Tên tệp chứa ký tự ẩn để giả dạng đuôi tệp khác."));

        var inner = Path.GetExtension(Path.GetFileNameWithoutExtension(name));
        if (executable && Disguise.Contains(inner))
            Add(RiskLevel.High, L.F("Tệp giả dạng \"{0}\" nhưng thực chất là tệp chạy được ({1}).", inner, ext));
        else if (executable && name.Contains("     ", StringComparison.Ordinal))
            Add(RiskLevel.High, L.T("Tên tệp có nhiều khoảng trắng liên tiếp để che đuôi thật."));

        var type = contentType?.Split(';')[0].Trim() ?? "";
        if (!executable && ExecutableContentTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            Add(RiskLevel.High, L.F("Máy chủ gửi về một chương trình chạy được, không khớp với tên tệp \"{0}\".", ext));

        var source = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null;
        var final = Uri.TryCreate(finalUrl, UriKind.Absolute, out var f) ? f : source;
        if (executable)
            Add(RiskLevel.Caution, L.F("Đây là tệp chạy được ({0}){1}. Chỉ mở nếu bạn tin nguồn tải.", ext, (final is null ? "" : L.F(" từ {0}", final.Host))));

        if (final is not null)
        {
            if (executable && final.Scheme == Uri.UriSchemeHttp)
                Add(RiskLevel.Caution, L.T("Tệp chạy được tải qua kết nối không mã hoá (http), có thể bị tráo trên đường truyền."));
            if (IPAddress.TryParse(final.Host.Trim('[', ']'), out var ip) && !IPAddress.IsLoopback(ip))
                Add(RiskLevel.Caution, L.F("Link trỏ thẳng tới địa chỉ IP ({0}) thay vì tên miền.", final.Host));
        }

        return reasons.Count == 0 ? RiskReport.Safe : new RiskReport(level, reasons);
    }
}
