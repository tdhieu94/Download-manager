using System.Globalization;

namespace DoMa;

/// <summary>
/// Dịch chữ hiển thị. Chữ gốc trong mã nguồn là tiếng Việt; khi chọn tiếng Anh thì tra bảng <see cref="En"/>.
/// Chữ chưa có trong bảng thì giữ nguyên tiếng Việt.
/// </summary>
public static class L
{
    /// <summary>"vi" hoặc "en". Đặt một lần lúc mở DoMa, trước khi tạo cửa sổ nào.</summary>
    public static string Language { get; private set; } = "vi";

    public static bool English => Language == "en";

    /// <param name="setting">"auto" (theo ngôn ngữ Windows), "vi" hoặc "en".</param>
    public static void Use(string? setting)
    {
        Language = setting switch
        {
            "en" => "en",
            "vi" => "vi",
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "vi" ? "vi" : "en",
        };
    }

    public static string T(string vi) => English && En.Text.TryGetValue(vi, out var en) ? en : vi;

    /// <summary>Chữ có chỗ điền ({0}, {1}…), như string.Format.</summary>
    public static string F(string vi, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(vi), args);
}
