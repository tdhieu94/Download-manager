using System.Text.Json.Nodes;
using DoMa.Core.Files;
using DoMa.Core.Messaging;

namespace DoMa.Core.Tests.Files;

public sealed class BatchLinksTests
{
    private static readonly CategoryResolver Resolver = new(CategoryResolver.Defaults.ToDictionary(p => p.Key, p => p.Value.ToArray()), "/dl");

    [Fact]
    public void filters_dedupes_and_classifies()
    {
        var links = BatchLinks.Prepare(
        [
            ("https://a.com/files/setup.exe", "Tải về"),
            ("https://a.com/files/setup.exe#top", null),
            ("https://a.com/docs/T%C3%A0i%20li%E1%BB%87u.pdf", null),
            ("https://cdn.a.com/v/clip.mp4?x=1", null),
            ("https://a.com/about", "Giới thiệu"),
            ("javascript:void(0)", "x"),
            ("mailto:a@b.c", "mail"),
        ], Resolver);

        Assert.Equal(4, links.Count);
        Assert.Equal(LinkKind.Software, links[0].Kind);
        Assert.Equal("Tài liệu.pdf", links[1].FileName);
        Assert.Equal(LinkKind.Document, links[1].Kind);
        Assert.Equal(LinkKind.Media, links[2].Kind);
        Assert.Equal("cdn.a.com", links[2].Host);
        Assert.False(links[3].IsFile);
        Assert.Equal("Giới thiệu", links[3].FileName);
    }

    [Fact]
    public void parses_batch_message()
    {
        var node = JsonNode.Parse("""
            {"type":"bat_nhieu","id":"m1","referrer":"https://a.com/p","userAgent":"UA",
             "links":[{"url":"https://a.com/x.zip","text":"X"},{"text":"no url"}],
             "cookies":{"a.com":"k=v","b.com":""}}
            """)!;
        var msg = BatchCaptureMessage.From(node);
        Assert.Equal("m1", msg.Id);
        Assert.Single(msg.Links);
        Assert.Equal("k=v", msg.CookiesByHost["a.com"]);
        Assert.False(msg.CookiesByHost.ContainsKey("b.com"));
    }
}
