using System.Net;
using System.Security.Cryptography;
using System.Text;
using DoMa.Core.Engine;
using DoMa.Core.Engine.Hls;
using DoMa.Core.Models;

namespace DoMa.Core.Tests.Engine;

public sealed class HlsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DoMaHls", Guid.NewGuid().ToString("N"));

    public HlsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void parses_master_and_picks_best()
    {
        var p = HlsPlaylist.Parse("""
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2"
            low/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720
            https://cdn.example.com/hd/index.m3u8
            """, new Uri("https://example.com/v/master.m3u8"));
        Assert.True(p.IsMaster);
        Assert.Equal("https://example.com/v/low/index.m3u8", p.Variants[0].Uri.AbsoluteUri);
        Assert.Equal("1280x720", p.Best()!.Resolution);
    }

    [Fact]
    public void parses_media_with_key_and_map()
    {
        var p = HlsPlaylist.Parse("""
            #EXTM3U
            #EXT-X-MEDIA-SEQUENCE:7
            #EXT-X-MAP:URI="init.mp4"
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x000102030405060708090a0b0c0d0e0f
            #EXTINF:4.0,
            s1.m4s
            #EXTINF:4.0,
            s2.m4s
            #EXT-X-ENDLIST
            """, new Uri("https://example.com/v/index.m3u8"));
        Assert.False(p.IsMaster);
        Assert.True(p.Ended);
        Assert.Null(p.Unsupported);
        Assert.Equal(".mp4", p.Extension);
        Assert.Equal(2, p.Segments.Count);
        Assert.Equal(8, p.Segments[1].Sequence);
        Assert.Equal("https://example.com/v/key.bin", p.Segments[0].Key!.Uri.AbsoluteUri);
        Assert.Equal(15, p.Segments[0].Key!.Iv![15]);
    }

    [Fact]
    public void drm_is_reported_unsupported()
    {
        var p = HlsPlaylist.Parse("#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://x\"\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST", new Uri("https://e.com/a.m3u8"));
        Assert.NotNull(p.Unsupported);
    }

    [Theory]
    [InlineData("https://e.com/a/index.m3u8?t=1", null, true)]
    [InlineData("https://e.com/a/play", "application/vnd.apple.mpegurl; charset=utf-8", true)]
    [InlineData("https://e.com/a/video.mp4", "video/mp4", false)]
    public void detects_hls(string url, string? type, bool expected) => Assert.Equal(expected, HlsPlaylist.Looks(url, type));

    [Theory]
    [InlineData(@"C:\dl\index.m3u8", @"C:\dl\index.ts")]
    [InlineData(@"C:\dl\Phim hay", @"C:\dl\Phim hay.ts")]
    [InlineData(@"C:\dl\Phim 1.5", @"C:\dl\Phim 1.5.ts")]
    public void replaces_playlist_extension(string path, string expected) =>
        Assert.Equal(expected, HlsDownloadEngine.WithExtension(path, ".ts"));

    [Fact]
    public async Task downloads_decrypts_and_joins_segments()
    {
        var key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        byte[] Encrypt(string text, long seq)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            return aes.EncryptCbc(Encoding.ASCII.GetBytes(text), HlsDownloadEngine.SequenceIv(seq), PaddingMode.PKCS7);
        }
        var files = new Dictionary<string, byte[]>
        {
            ["/master.m3u8"] = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=9\nhd.m3u8\n"),
            ["/hd.m3u8"] = Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:3\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\"\n#EXTINF:1,\na.ts\n#EXTINF:1,\nb.ts\n#EXT-X-KEY:METHOD=NONE\n#EXTINF:1,\nc.ts\n#EXT-X-ENDLIST\n"),
            ["/k"] = key,
            ["/a.ts"] = Encrypt("AAA", 3),
            ["/b.ts"] = Encrypt("BBBB", 4),
            ["/c.ts"] = Encoding.ASCII.GetBytes("CC"),
        };
        using var http = new HttpClient(new MapHandler(files));
        var engine = new HlsDownloadEngine(http, (_, _) => Task.CompletedTask);
        var target = Path.Combine(_dir, "Phim.ts");
        var url = "https://h.example/master.m3u8";
        var probe = new ProbeResult(true, url, 10, true, null, null, "master.m3u8", "application/vnd.apple.mpegurl", ProbeFailure.None, 206);
        var job = new DownloadJob(new DownloadRequest(url, null, null, null, null, null, null), target, Path.Combine(_dir, "s.json"), 3, probe);

        var result = await engine.RunAsync(job, new Progress<EngineProgress>(), CancellationToken.None);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        Assert.Equal(target, result.FinalPath);
        Assert.Equal("AAABBBBCC", File.ReadAllText(target));
        Assert.False(File.Exists(Path.Combine(_dir, "s.json")));
    }

    [Fact]
    public async Task live_stream_is_refused()
    {
        var files = new Dictionary<string, byte[]> { ["/live.m3u8"] = Encoding.UTF8.GetBytes("#EXTM3U\n#EXTINF:1,\na.ts\n") };
        using var http = new HttpClient(new MapHandler(files));
        var url = "https://h.example/live.m3u8";
        var probe = new ProbeResult(true, url, 10, true, null, null, null, null, ProbeFailure.None, 200);
        var job = new DownloadJob(new DownloadRequest(url, null, null, null, null, null, null), Path.Combine(_dir, "x.ts"), Path.Combine(_dir, "s.json"), 2, probe);
        var result = await new HlsDownloadEngine(http).RunAsync(job, new Progress<EngineProgress>(), CancellationToken.None);
        Assert.Equal(EngineOutcome.Failed, result.Outcome);
        Assert.Contains("trực tiếp", result.ErrorMessage);
    }

    private sealed class MapHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = files.TryGetValue(request.RequestUri!.AbsolutePath, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            resp.RequestMessage = request;
            return Task.FromResult(resp);
        }
    }
}
