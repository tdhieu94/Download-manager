using DoMa.Core.Engine;
using DoMa.Core.Files;

namespace DoMa.Core.Tests.Files;

public class GitHubLinkTests
{
    [Theory]
    [InlineData("https://github.com/o/r/blob/main/docs/a%20b.pdf", "https://github.com/o/r/raw/main/docs/a%20b.pdf")]
    [InlineData("https://github.com/o/r/blob/v1.2/README.md?plain=1#L3", "https://github.com/o/r/raw/v1.2/README.md")]
    [InlineData("https://github.com/o/r", "https://github.com/o/r/archive/HEAD.zip")]
    [InlineData("https://www.github.com/o/r/", "https://github.com/o/r/archive/HEAD.zip")]
    [InlineData("https://github.com/o/r.git", "https://github.com/o/r/archive/HEAD.zip")]
    [InlineData("https://github.com/o/r/tree/dev", "https://github.com/o/r/archive/dev.zip")]
    public void page_links_become_file_links(string page, string file) =>
        Assert.Equal(file, GitHubLink.ToFileUrl(page));

    [Theory]
    [InlineData("https://github.com/o/r/releases/download/v1/setup.exe")]
    [InlineData("https://github.com/o/r/archive/refs/heads/main.zip")]
    [InlineData("https://github.com/o/r/raw/main/a.txt")]
    [InlineData("https://github.com/o/r/releases/tag/v1")]
    [InlineData("https://github.com/o/r/tree/main/src")]
    [InlineData("https://github.com/o/r/issues/3")]
    [InlineData("https://github.com/o")]
    [InlineData("https://github.com/settings/profile")]
    [InlineData("https://github.com/orgs/acme")]
    [InlineData("https://gitlab.com/o/r")]
    [InlineData("https://raw.githubusercontent.com/o/r/main/a.txt")]
    [InlineData("magnet:?xt=urn:btih:abc")]
    [InlineData(null)]
    public void other_links_are_left_alone(string? link) =>
        Assert.Null(GitHubLink.ToFileUrl(link));

    [Fact]
    public void explains_why_a_pasted_link_failed()
    {
        ProbeResult Failed(ProbeFailure failure, int? status) => new(false, null, null, false, null, null, null, null, failure, status);
        Assert.Contains("trang web", LinkProber.Explain(Failed(ProbeFailure.HtmlInsteadOfFile, 200)));
        Assert.Contains("404", LinkProber.Explain(Failed(ProbeFailure.NotFound, 404)));
        Assert.Contains("đăng nhập", LinkProber.Explain(Failed(ProbeFailure.Expired, 403)));
        Assert.Contains("mạng", LinkProber.Explain(Failed(ProbeFailure.Network, null)));
        Assert.Contains("500", LinkProber.Explain(Failed(ProbeFailure.Other, 500)));
    }
}
