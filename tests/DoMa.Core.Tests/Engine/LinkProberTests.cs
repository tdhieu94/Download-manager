using DoMa.Core.Engine;
using DoMa.Core.Models;
using DoMa.Core.Tests.TestServer;

namespace DoMa.Core.Tests.Engine;

public class LinkProberTests
{
    private static DownloadRequest Req(Uri url, string? cookies = null, string? referrer = null, string? name = null) =>
        new(url.ToString(), referrer, name, null, null, cookies, null);

    private static async Task<ProbeResult> Probe(DownloadRequest req)
    {
        using var http = HttpClientFactory.Create();
        return await new LinkProber(http).ProbeAsync(req, CancellationToken.None);
    }

    [Fact]
    public async Task range_supported_reports_size()
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = new byte[10_485_760]);
        var r = await Probe(Req(server.Url("/f.bin")));
        Assert.True(r.Ok);
        Assert.Equal(10_485_760, r.Size);
        Assert.True(r.SupportsRange);
    }

    [Fact]
    public async Task no_range_reports_single()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = new byte[1000];
            o.HonorRange = false;
        });
        var r = await Probe(Req(server.Url("/f.bin")));
        Assert.True(r.Ok);
        Assert.False(r.SupportsRange);
        Assert.Equal(1000, r.Size);
    }

    [Fact]
    public async Task unknown_size()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = new byte[1000];
            o.SendLength = false;
            o.HonorRange = false;
        });
        var r = await Probe(Req(server.Url("/f.bin")));
        Assert.True(r.Ok);
        Assert.Null(r.Size);
    }

    [Fact]
    public async Task sends_cookie_and_referer()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = new byte[100];
            o.RequiredCookie = "s=1";
        });
        Assert.False((await Probe(Req(server.Url("/f.bin")))).Ok);
        Assert.True((await Probe(Req(server.Url("/f.bin"), "s=1", "https://example.com/page"))).Ok);
    }

    [Fact]
    public async Task expired_403()
    {
        await using var server = await RangeServer.StartAsync(o => o.StatusCode = 403);
        var r = await Probe(Req(server.Url("/f.bin")));
        Assert.False(r.Ok);
        Assert.Equal(ProbeFailure.Expired, r.Failure);
        Assert.Equal(403, r.StatusCode);
    }

    [Fact]
    public async Task not_found_404()
    {
        await using var server = await RangeServer.StartAsync(o => o.StatusCode = 404);
        var r = await Probe(Req(server.Url("/f.bin")));
        Assert.Equal(ProbeFailure.NotFound, r.Failure);
    }

    [Fact]
    public async Task html_instead_of_file()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = new byte[100];
            o.ContentType = "text/html";
        });
        var r = await Probe(Req(server.Url("/download"), name: "x.iso"));
        Assert.False(r.Ok);
        Assert.Equal(ProbeFailure.HtmlInsteadOfFile, r.Failure);
    }

    [Fact]
    public async Task prefers_filename_star_utf8()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = new byte[100];
            o.ContentDisposition = "attachment; filename=\"a.iso\"; filename*=UTF-8''T%C3%A0i%20li%E1%BB%87u.pdf";
        });
        var r = await Probe(Req(server.Url("/f")));
        Assert.Equal("Tài liệu.pdf", r.FileName);
    }

    [Fact]
    public async Task follows_redirect_final_url()
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = new byte[100]);
        var r = await Probe(Req(server.Url("/redirect")));
        Assert.True(r.Ok);
        Assert.Equal(server.Url("/final/file.bin").ToString(), r.FinalUrl);
        Assert.Equal("file.bin", r.FileName);
    }

    [Fact]
    public async Task same_origin_redirect_keeps_cookie_and_referer()
    {
        await using var server = await RangeServer.StartAsync(o =>
        {
            o.Content = new byte[100];
            o.RequiredCookie = "s=1";
        });
        var page = server.Url("/page").ToString();

        var r = await Probe(Req(server.Url("/redirect"), "s=1", page));

        Assert.True(r.Ok);
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, q => Assert.Equal("s=1", q.Cookie));
        Assert.All(server.Requests, q => Assert.Equal(page, q.Referer));
    }

    [Fact]
    public async Task cross_origin_redirect_drops_cookie_and_trims_referer()
    {
        await using var target = await RangeServer.StartAsync(o => o.Content = new byte[100]);
        await using var origin = await RangeServer.StartAsync(o => o.RedirectLocation = target.Url("/f.bin").ToString());
        var page = origin.Url("/page?q=1").ToString();

        var r = await Probe(Req(origin.Url("/redirect"), "s=1", page));

        Assert.True(r.Ok);
        Assert.Equal(target.Url("/f.bin").ToString(), r.FinalUrl);
        var first = Assert.Single(origin.Requests);
        Assert.Equal("s=1", first.Cookie);
        Assert.Equal(page, first.Referer);
        var second = Assert.Single(target.Requests);
        Assert.Equal("", second.Cookie);
        Assert.Equal(origin.Url("/").ToString(), second.Referer);
    }

    [Fact]
    public async Task https_referer_is_not_sent_to_http()
    {
        await using var server = await RangeServer.StartAsync(o => o.Content = new byte[100]);

        var r = await Probe(Req(server.Url("/f.bin"), referrer: "https://example.com/page"));

        Assert.True(r.Ok);
        Assert.Equal("", Assert.Single(server.Requests).Referer);
    }

    [Fact]
    public async Task redirect_loop_stops()
    {
        await using var server = await RangeServer.StartAsync(o => o.RedirectLocation = "/redirect");

        var r = await Probe(Req(server.Url("/redirect")));

        Assert.False(r.Ok);
        Assert.Equal(302, r.StatusCode);
        Assert.Equal(11, server.RequestCount);
    }
}
