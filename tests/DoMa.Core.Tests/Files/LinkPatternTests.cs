using DoMa.Core.Files;

namespace DoMa.Core.Tests.Files;

public class LinkPatternTests
{
    [Fact]
    public void expands_numbers_keeping_leading_zeros()
    {
        Assert.Equal(["https://h/a08.jpg", "https://h/a09.jpg", "https://h/a10.jpg"], LinkPattern.Expand("https://h/a[08-10].jpg", 100));
        Assert.Equal(["https://h/p1", "https://h/p2", "https://h/p3"], LinkPattern.Expand("https://h/p[1-3]", 100));
        Assert.Equal(["https://h/3", "https://h/2", "https://h/1"], LinkPattern.Expand("https://h/[3-1]", 100));
    }

    [Fact]
    public void expands_letters_and_several_patterns()
    {
        Assert.Equal(["https://h/a.zip", "https://h/b.zip", "https://h/c.zip"], LinkPattern.Expand("https://h/[a-c].zip", 100));
        Assert.Equal(["https://h/s1/e1", "https://h/s1/e2", "https://h/s2/e1", "https://h/s2/e2"], LinkPattern.Expand("https://h/s[1-2]/e[1-2]", 100));
    }

    [Fact]
    public void stops_at_the_limit()
    {
        var links = LinkPattern.Expand("https://h/[1-999999999]", 500);
        Assert.Equal(500, links.Count);
        Assert.Equal("https://h/500", links[^1]);
        Assert.Equal(500, LinkPattern.Expand("https://h/[1-100]/[1-100]", 500).Count);
        Assert.DoesNotContain(LinkPattern.Expand("https://h/[1-100]/[1-100]", 500), l => l.Contains('['));
    }

    [Theory]
    [InlineData("https://h/plain.zip")]
    [InlineData("http://[::1]:8080/f.zip")]
    [InlineData("https://h/a[1].jpg")]
    [InlineData("https://h/a[a-Z].jpg")]
    [InlineData("https://h/?ids[]=1-2")]
    public void leaves_other_links_alone(string link)
    {
        Assert.False(LinkPattern.HasPattern(link));
        Assert.Equal([link], LinkPattern.Expand(link, 100));
    }
}
