using System.Text;
using System.Text.RegularExpressions;

namespace DoMa.Core.Updates;

/// <summary>
/// Đổi phần mô tả bản phát hành (Markdown trên GitHub) thành chữ thường để hiện trong ứng dụng.
/// Nếu có cả mục tiếng Việt và tiếng Anh thì tiếng Việt luôn đứng trước.
/// </summary>
public static partial class ReleaseNotes
{
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";
        var sections = Split(markdown.Replace("\r\n", "\n"));
        var ordered = sections.OrderBy(s => Rank(s.Heading)).ToList();

        var sb = new StringBuilder();
        foreach (var (heading, lines) in ordered)
        {
            if (heading is not null)
            {
                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(Inline(heading).ToUpperInvariant()).Append('\n');
            }
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (line.TrimStart().StartsWith("<!--", StringComparison.Ordinal))
                    continue;
                var trimmed = line.TrimStart();
                var indent = line.Length - trimmed.Length;
                if (trimmed.StartsWith("### ", StringComparison.Ordinal))
                    line = Inline(trimmed[4..]);
                else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                    line = new string(' ', indent) + "• " + Inline(trimmed[2..]);
                else
                    line = Inline(line);
                sb.Append(line).Append('\n');
            }
        }
        return Regex.Replace(sb.ToString(), "\n{3,}", "\n\n").Trim();
    }

    /// <summary>Tách theo tiêu đề cấp 1–2 ("# " / "## "). Phần trước tiêu đề đầu tiên có Heading = null.</summary>
    private static List<(string? Heading, List<string> Lines)> Split(string text)
    {
        var result = new List<(string?, List<string>)> { (null, []) };
        foreach (var line in text.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("## ", StringComparison.Ordinal) || t.StartsWith("# ", StringComparison.Ordinal))
                result.Add((t.TrimStart('#').Trim(), []));
            else
                result[^1].Item2.Add(line);
        }
        if (result[0].Item2.All(string.IsNullOrWhiteSpace))
            result.RemoveAt(0);
        return result;
    }

    /// <summary>Phần không tiêu đề → tiếng Việt → các mục khác → tiếng Anh.</summary>
    private static int Rank(string? heading)
    {
        if (heading is null)
            return 0;
        var h = heading.ToLowerInvariant();
        if (h.Contains("tiếng việt") || h.Contains("vietnamese"))
            return 1;
        if (h.Contains("english") || h.Contains("tiếng anh"))
            return 3;
        return 2;
    }

    private static string Inline(string text)
    {
        text = LinkRegex().Replace(text, "$1");
        text = text.Replace("**", "").Replace("__", "").Replace("`", "");
        return text;
    }

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex LinkRegex();
}
