using System.Text.RegularExpressions;

namespace DoMa.Core.Tests;

public class LocalizationTests
{
    [Fact]
    public void translations_keep_placeholders()
    {
        static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{\d+[^}]*\}").Select(m => m.Value).Order());
        foreach (var (vi, en) in En.Text)
            Assert.True(Holes(vi) == Holes(en), $"{vi} => {en}");
    }

    [Fact]
    public void every_wrapped_text_has_a_translation()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.EndsWith("En.cs", StringComparison.Ordinal))
                continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"L\.[TF]\(""((?:[^""\\]|\\.)*)"""))
            {
                var key = Regex.Unescape(m.Groups[1].Value);
                if (!En.Text.ContainsKey(key))
                    missing.Add(key);
            }
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void vietnamese_is_default()
    {
        Assert.Equal("Tải ngay", L.T("Tải ngay"));
        Assert.Equal("Đã chọn 1/2", L.F("Đã chọn {0}/{1}", 1, 2));
    }
}
