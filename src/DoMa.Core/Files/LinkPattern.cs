using System.Globalization;
using System.Text.RegularExpressions;

namespace DoMa.Core.Files;

/// <summary>
/// Link theo mẫu để tải hàng loạt: "https://site/anh[001-100].jpg" thành 100 link (giữ số 0 đứng đầu),
/// "https://site/tap-[a-e].zip" thành 5 link. Một link có thể có nhiều mẫu.
/// </summary>
public static partial class LinkPattern
{
    /// <summary>Các link sinh ra từ mẫu, tối đa <paramref name="max"/>; link không có mẫu thì trả về chính nó.</summary>
    public static List<string> Expand(string link, int max)
    {
        var results = new List<string> { link };
        // Mỗi vòng mở mẫu đầu tiên còn lại trong từng link; dừng khi hết mẫu hoặc đã đủ số link.
        while (max > 0 && results.Count <= max && results.Any(r => Range().IsMatch(r)))
        {
            var next = new List<string>();
            foreach (var item in results)
            {
                var m = Range().Match(item);
                if (!m.Success)
                {
                    next.Add(item);
                    continue;
                }
                foreach (var value in Values(m))
                {
                    next.Add(string.Concat(item.AsSpan(0, m.Index), value, item.AsSpan(m.Index + m.Length)));
                    if (next.Count >= max)
                        return next;
                }
            }
            results = next;
        }
        return results.Count > max ? results.GetRange(0, Math.Max(0, max)) : results;
    }

    public static bool HasPattern(string link) => Range().IsMatch(link);

    private static IEnumerable<string> Values(Match m)
    {
        if (m.Groups["a"].Success)
        {
            var (from, to) = (m.Groups["a"].Value[0], m.Groups["b"].Value[0]);
            var step = from <= to ? 1 : -1;
            for (var c = from; c != to + step; c = (char)(c + step))
                yield return c.ToString();
            yield break;
        }
        var first = m.Groups["x"].Value;
        var last = m.Groups["y"].Value;
        if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || !long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var end))
            yield break;
        // "[001-100]": giữ độ rộng của số đầu nếu nó có số 0 đứng trước.
        var width = first.Length > 1 && first[0] == '0' ? first.Length : 0;
        var direction = start <= end ? 1 : -1;
        for (var n = start; n != end + direction; n += direction)
            yield return n.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
    }

    [GeneratedRegex(@"\[(?:(?<x>\d{1,9})-(?<y>\d{1,9})|(?<a>[a-z])-(?<b>[a-z])|(?<a>[A-Z])-(?<b>[A-Z]))\]")]
    private static partial Regex Range();
}
