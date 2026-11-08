namespace DoMa.Core.Models;

/// <summary>Lựa chọn khi tải video từ trang (YouTube, Facebook…): chất lượng, chỉ lấy tiếng, kèm phụ đề.</summary>
public sealed record MediaOptions(string Quality = MediaOptions.Best, bool Subtitles = false)
{
    public const string Best = "best";
    public const string Audio = "audio";

    public static readonly MediaOptions Default = new();

    public bool AudioOnly => Quality == Audio;

    /// <summary>Chiều cao tối đa (vd. 720) nếu người dùng chọn một độ phân giải cụ thể.</summary>
    public int? MaxHeight => int.TryParse(Quality, out var h) && h > 0 ? h : null;

    public string Extension => AudioOnly ? ".mp3" : ".mp4";
}

/// <summary>Các mức chất lượng cho người dùng chọn, theo độ phân giải video thật sự có.</summary>
public static class MediaQualities
{
    private static readonly int[] Standard = [2160, 1440, 1080, 720, 480, 360];

    /// <param name="heights">Độ phân giải video có sẵn; null nếu chưa biết (vd. tải cả playlist).</param>
    public static IReadOnlyList<(string Value, string Label)> For(IReadOnlyList<int>? heights)
    {
        var max = heights is { Count: > 0 } ? heights.Max() : (int?)null;
        var list = new List<(string, string)> { (MediaOptions.Best, max is { } m ? $"Cao nhất ({Name(m)})" : "Cao nhất có thể") };
        foreach (var h in Standard)
        {
            // Chỉ liệt kê mức thấp hơn mức cao nhất của video (mức cao nhất đã nằm ở dòng đầu).
            if (max is null ? h <= 1080 : h < max && heights!.Any(x => x >= h))
                list.Add((h.ToString(System.Globalization.CultureInfo.InvariantCulture), Name(h)));
        }
        list.Add((MediaOptions.Audio, "Chỉ âm thanh (MP3)"));
        return list;
    }

    public static string Name(int height) => height switch
    {
        >= 2160 => $"4K ({height}p)",
        >= 1440 => $"2K ({height}p)",
        _ => $"{height}p",
    };
}
