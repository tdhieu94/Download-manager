using DoMa.Core.Engine.YouTube;
using DoMa.Core.Files;
using DoMa.Core.Models;

namespace DoMa.Core.Tests.Engine;

public class MediaSiteTests
{
    [Theory]
    [InlineData("https://www.facebook.com/watch?v=123", "Facebook")]
    [InlineData("https://www.facebook.com/reel/123", "Facebook")]
    [InlineData("https://m.facebook.com/page/videos/123/", "Facebook")]
    [InlineData("https://fb.watch/abc/", "Facebook")]
    [InlineData("https://www.tiktok.com/@ai/video/7234", "TikTok")]
    [InlineData("https://vm.tiktok.com/ZM123/", "TikTok")]
    [InlineData("https://www.instagram.com/reel/Cabc/", "Instagram")]
    [InlineData("https://x.com/user/status/1234567", "X")]
    [InlineData("https://twitter.com/user/status/1234567", "X")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "YouTube")]
    public void recognizes_video_pages(string url, string site)
    {
        Assert.True(MediaLink.IsVideo(url));
        Assert.Equal(site, MediaLink.SiteName(url));
        Assert.False(MediaLink.IsPlaylist(url));
    }

    [Theory]
    [InlineData("https://www.facebook.com/profile.php")]
    [InlineData("https://x.com/user")]
    [InlineData("https://www.instagram.com/user/")]
    [InlineData("https://www.tiktok.com/@ai")]
    [InlineData("https://example.com/video.mp4")]
    public void ignores_other_pages(string url) => Assert.False(MediaLink.IsSupported(url));

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PL1", "https://www.youtube.com/playlist?list=PL1")]
    [InlineData("https://www.youtube.com/@kenh", "https://www.youtube.com/@kenh/videos")]
    [InlineData("https://www.youtube.com/@kenh/shorts", "https://www.youtube.com/@kenh/shorts")]
    [InlineData("https://www.youtube.com/channel/UC123", "https://www.youtube.com/channel/UC123/videos")]
    public void normalizes_playlists(string url, string expected)
    {
        Assert.True(MediaLink.IsPlaylist(url));
        Assert.False(MediaLink.IsVideo(url));
        Assert.Equal(expected, MediaLink.Normalize(url));
    }

    [Fact]
    public void playlist_of_watch_link_skips_mix()
    {
        Assert.Equal("https://www.youtube.com/playlist?list=PLx", MediaLink.PlaylistOfVideo("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLx"));
        Assert.Null(MediaLink.PlaylistOfVideo("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ"));
        Assert.False(MediaLink.IsPlaylist("https://www.youtube.com/@kenh/about"));
    }

    [Fact]
    public void cookie_domain_is_site_root()
    {
        Assert.Equal(".facebook.com", MediaLink.CookieDomain("https://web.facebook.com/reel/1"));
        Assert.Equal(".facebook.com", MediaLink.CookieDomain("https://fb.watch/x"));
        Assert.Equal(".tiktok.com", MediaLink.CookieDomain("https://vm.tiktok.com/x"));
        Assert.Equal(".youtube.com", MediaLink.CookieDomain("https://youtu.be/x"));
    }

    [Fact]
    public void format_arguments_follow_options()
    {
        Assert.Contains("res,fps,vcodec:h264,acodec:aac", YtDlp.FormatArguments(null));
        Assert.Contains("res:720,fps,vcodec:h264,acodec:aac", YtDlp.FormatArguments(new MediaOptions("720")));
        var audio = YtDlp.FormatArguments(new MediaOptions(MediaOptions.Audio, Subtitles: true));
        Assert.Contains("mp3", audio);
        Assert.DoesNotContain("--write-subs", audio);
        Assert.Contains("--embed-subs", YtDlp.FormatArguments(new MediaOptions(Subtitles: true)));
        Assert.Equal(".mp3", new MediaOptions(MediaOptions.Audio).Extension);
        Assert.Equal(720, new MediaOptions("720").MaxHeight);
    }

    [Fact]
    public void info_lists_heights_audio_and_subtitles()
    {
        var info = YtDlp.ParseInfo("""
        {"title":"x","formats":[
          {"vcodec":"none","acodec":"mp4a","filesize":300},
          {"vcodec":"avc1","acodec":"none","height":720},
          {"vcodec":"vp9","acodec":"none","height":2160},
          {"vcodec":"avc1","acodec":"none","height":720}],
         "subtitles":{"vi":[{}]}}
        """);
        Assert.Equal([2160, 720], info.Heights);
        Assert.Equal(300, info.AudioSize);
        Assert.True(info.HasSubtitles);
        Assert.False(YtDlp.ParseInfo("""{"title":"x","subtitles":{"fr":[{}]}}""").HasSubtitles);
    }

    [Fact]
    public void qualities_only_list_what_video_has()
    {
        var q = MediaQualities.For([1080, 720, 360]);
        Assert.Equal(["best", "720", "480", "360", "audio"], q.Select(x => x.Value));
        Assert.Equal("Cao nhất (1080p)", q[0].Label);
        Assert.Equal("Cao nhất (4K (2160p))", MediaQualities.For([2160])[0].Label);
        Assert.Contains(MediaQualities.For(null), x => x.Value == "1080");
    }

    [Fact]
    public void parses_flat_playlist()
    {
        var p = YtDlp.ParsePlaylist("""
        {"title":"Nhạc","entries":[
          {"id":"dQw4w9WgXcQ","title":"Bài 1","url":"https://www.youtube.com/watch?v=dQw4w9WgXcQ"},
          {"id":"aaaaaaaaaaa","title":"[Private video]","url":"https://www.youtube.com/watch?v=aaaaaaaaaaa"},
          {"id":"bbbbbbbbbbb","title":"Bài 2"},
          {"id":"dQw4w9WgXcQ","title":"Trùng","url":"https://www.youtube.com/watch?v=dQw4w9WgXcQ"}]}
        """);
        Assert.Equal("Nhạc", p.Title);
        Assert.Equal(["Bài 1", "Bài 2"], p.Entries.Select(e => e.Title));
        Assert.Equal("https://www.youtube.com/watch?v=bbbbbbbbbbb", p.Entries[1].Url);
    }

    [Fact]
    public void batch_marks_video_pages_as_media()
    {
        var links = BatchLinks.Prepare([("https://www.tiktok.com/@ai/video/7234", null), ("https://example.com/page", "Trang")],
            new CategoryResolver(CategoryResolver.Defaults, Path.GetTempPath()));
        Assert.Equal(LinkKind.Media, links[0].Kind);
        Assert.True(links[0].IsMediaPage);
        Assert.Equal("Video TikTok", links[0].FileName);
        Assert.False(links[1].IsMediaPage);
    }

    [Fact]
    public void subtitle_files_count_as_partial()
    {
        var dir = Path.Combine(Path.GetTempPath(), "doma-sub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Phim.vi.srt"), "");
            File.WriteAllText(Path.Combine(dir, "Phim.en-US.vtt"), "");
            File.WriteAllText(Path.Combine(dir, "Phim hay.mp4"), "");
            Assert.Equal(2, YouTubeDownloadEngine.PartialFiles(Path.Combine(dir, "Phim.mp4")).Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class MediaClipTests
{
    [Theory]
    [InlineData("90", 90)]
    [InlineData("1:30", 90)]
    [InlineData("1:02:03", 3723)]
    [InlineData("0:00", 0)]
    [InlineData("1:75", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData("-5", null)]
    public void parses_time(string text, int? expected) => Assert.Equal(expected, MediaOptions.ParseTime(text));

    [Fact]
    public void formats_time()
    {
        Assert.Equal("1:30", MediaOptions.FormatTime(90));
        Assert.Equal("1:02:03", MediaOptions.FormatTime(3723));
    }

    [Fact]
    public void clip_adds_download_sections()
    {
        var args = YtDlp.FormatArguments(new MediaOptions(ClipStart: 90, ClipEnd: 150)).ToList();
        Assert.Equal("*90-150", args[args.IndexOf("--download-sections") + 1]);
        var tail = YtDlp.FormatArguments(new MediaOptions(MediaOptions.Audio, ClipStart: 30)).ToList();
        Assert.Equal("*30-inf", tail[tail.IndexOf("--download-sections") + 1]);
        Assert.DoesNotContain("--download-sections", YtDlp.FormatArguments(new MediaOptions()));
        Assert.Equal(213, YtDlp.ParseInfo("""{"title":"x","duration":212.4}""").Duration);
    }
}
