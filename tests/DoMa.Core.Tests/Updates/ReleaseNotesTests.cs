using DoMa.Core.Updates;

namespace DoMa.Core.Tests.Updates;

public sealed class ReleaseNotesTests
{
    [Fact]
    public void vietnamese_section_comes_before_english()
    {
        var md = "## English\n- **New** warning\n\n## Tiếng Việt\n- **Mới**: cảnh báo\n";
        var text = ReleaseNotes.ToPlainText(md);
        Assert.True(text.IndexOf("TIẾNG VIỆT", StringComparison.Ordinal) < text.IndexOf("ENGLISH", StringComparison.Ordinal));
        Assert.Contains("• Mới: cảnh báo", text);
        Assert.Contains("• New warning", text);
    }

    [Fact]
    public void strips_links_and_comments()
    {
        var text = ReleaseNotes.ToPlainText("<!-- x -->\nXem [trang chủ](https://example.com) và `mã`\r\n");
        Assert.Equal("Xem trang chủ và mã", text);
    }

    [Fact]
    public void empty_body_gives_empty_text() => Assert.Equal("", ReleaseNotes.ToPlainText(null));

    [Fact]
    public void parser_reads_body()
    {
        var json = """
            {"tag_name":"v2.0.0","body":"## Tiếng Việt\n- a","assets":[{"name":"DoMa-Setup-2.0.0.exe","browser_download_url":"https://x/DoMa-Setup-2.0.0.exe"}]}
            """;
        Assert.Equal("## Tiếng Việt\n- a", ReleaseParser.Parse(json)!.Notes);
    }
}
