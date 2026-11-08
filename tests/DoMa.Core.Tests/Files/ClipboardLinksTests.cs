using DoMa.Core.Files;

namespace DoMa.Core.Tests.Files;

public sealed class ClipboardLinksTests
{
    private static readonly string[] Ext = ["zip", "exe", "pdf"];

    [Theory]
    [InlineData("https://example.com/files/setup.exe", "https://example.com/files/setup.exe")]
    [InlineData("  http://example.com/a/b.ZIP?token=1  ", "http://example.com/a/b.ZIP?token=1")]
    [InlineData("https://example.com/video.mp4", "https://example.com/video.mp4")]
    [InlineData("https://example.com/t%C3%A0i%20li%E1%BB%87u.pdf", "https://example.com/t%C3%A0i%20li%E1%BB%87u.pdf")]
    public void recognises_download_links(string text, string expected) =>
        Assert.Equal(expected, ClipboardLinks.Extract(text, Ext));

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com/page.html")]
    [InlineData("xem https://example.com/a.zip nhé")]
    [InlineData("ftp://example.com/a.zip")]
    [InlineData("a.zip")]
    [InlineData("")]
    [InlineData(null)]
    public void ignores_other_text(string? text) => Assert.Null(ClipboardLinks.Extract(text, Ext));
}
