using System.IO.Compression;
using DoMa.Core.Engine;
using DoMa.Core.Engine.Hls;
using DoMa.Core.Engine.YouTube;
using DoMa.Core.Models;

namespace DoMa.Core.Tests.Engine;

public sealed class YouTubeTests : IDisposable
{
    private const string W = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "doma-yt-" + Guid.NewGuid().ToString("N"));

    public YouTubeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PL1&t=30s")]
    [InlineData("https://m.youtube.com/watch?feature=share&v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    public void normalizes_video_links(string url) => Assert.Equal(W, YouTubeLink.Normalize(url));

    [Theory]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/@kenh")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://rr1.googlevideo.com/videoplayback?id=1")]
    [InlineData("not a url")]
    public void ignores_other_links(string url) => Assert.Null(YouTubeLink.Normalize(url));

    [Fact]
    public void youtube_counts_as_stream()
    {
        Assert.True(MediaAwareEngine.IsStream(W, "text/html"));
        Assert.False(MediaAwareEngine.IsStream("https://a.com/f.zip", "application/zip"));
    }

    [Fact]
    public void parses_progress_lines()
    {
        Assert.Equal(new YtDlpProgress(1024, 4096), YtDlp.ParseProgress("DOMA_P 1024 4096 NA"));
        Assert.Equal(new YtDlpProgress(1024, 5000), YtDlp.ParseProgress("DOMA_P 1024 NA 5000.0"));
        Assert.Equal(new YtDlpProgress(7, null), YtDlp.ParseProgress("DOMA_P 7 NA NA"));
        Assert.Null(YtDlp.ParseProgress("DOMA_P NA NA NA"));
        Assert.Null(YtDlp.ParseProgress("[download] 10%"));
        Assert.Equal(@"C:\v\a.mp4", YtDlp.ParseFile(@"DOMA_FILE:C:\v\a.mp4"));
    }

    [Fact]
    public void parses_info_with_merged_formats()
    {
        var info = YtDlp.ParseInfo("""{"title":"Bài hát","requested_formats":[{"filesize":1000},{"filesize_approx":234}],"is_live":false}""");
        Assert.Equal(new YouTubeInfo("Bài hát", 1234, false), info);
        Assert.Equal(55, YtDlp.ParseInfo("""{"title":"x","filesize_approx":55.6}""").Size);
        Assert.Null(YtDlp.ParseInfo("""{"title":"x","requested_formats":[{"filesize":1},{}]}""").Size);
        Assert.True(YtDlp.ParseInfo("""{"title":"x","is_live":true}""").IsLive);
    }

    [Fact]
    public void explains_common_errors()
    {
        Assert.Contains("xác minh", YtDlp.DescribeError(["ERROR: [youtube] x: Sign in to confirm you’re not a bot"]).Message);
        Assert.Contains("riêng tư", YtDlp.DescribeError(["ERROR: [youtube] x: Private video"]).Message);
        var format = YtDlp.DescribeError(["ERROR: [youtube] x: Requested format is not available. Use --list-formats for a list of available formats"]).Message;
        Assert.Contains("không cho lấy video", format);
        Assert.Contains("yt-dlp: [youtube] x: Requested format", format);
        Assert.Contains("bị chặn", YtDlp.DescribeError(["ERROR: [youtube] x: Video unavailable"]).Message);
        var net = YtDlp.DescribeError(["WARNING: retry", "ERROR: Unable to download webpage: timed out"]);
        Assert.True(net.Network);
        Assert.Equal("Không tải được video YouTube.", YtDlp.DescribeError([]).Message);
    }

    [Fact]
    public void download_arguments_escape_percent_and_set_output()
    {
        var args = YtDlp.DownloadArguments(W, Path.Combine(_dir, "50% off"), _dir, null);
        var output = args[args.ToList().IndexOf("-o") + 1];
        Assert.Equal(Path.Combine(_dir, "50%% off") + ".%(ext)s", output);
        Assert.Equal(W, args[^1]);
        Assert.Equal("--", args[^2]);
        Assert.DoesNotContain("--cookies", args);
        Assert.Contains("--cookies", YtDlp.DownloadArguments(W, "x", null, "c.txt"));
        var withDeno = YtDlp.InfoArguments(W, null, "/t/deno").ToList();
        Assert.Equal("deno:/t/deno", withDeno[withDeno.IndexOf("--js-runtimes") + 1]);
        Assert.DoesNotContain("--js-runtimes", YtDlp.InfoArguments(W, null));
    }

    [Fact]
    public void netscape_cookies_skip_bad_parts()
    {
        var text = YtDlp.NetscapeCookies("SID=abc; bad; PREF=f1=1&f2=2");
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal(".youtube.com\tTRUE\t/\tTRUE\t0\tSID\tabc", lines[1]);
        Assert.EndsWith("PREF\tf1=1&f2=2", lines[2]);
    }

    [Fact]
    public void finds_partial_files_of_a_video_only()
    {
        foreach (var name in new[] { "Bai.f137.mp4.part", "Bai.f140.m4a", "Bai.temp.mp4", "Bai.mp4.ytdl", "Bai.mp4", "Bai khac.f137.mp4.part", "Bai.txt" })
            File.WriteAllText(Path.Combine(_dir, name), "");
        var found = YouTubeDownloadEngine.PartialFiles(Path.Combine(_dir, "Bai.mp4")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["Bai.f137.mp4.part", "Bai.f140.m4a", "Bai.mp4.ytdl", "Bai.temp.mp4"], found);
    }

    [Fact]
    public async Task installs_tools_following_github_redirects()
    {
        var zipPath = Path.Combine(_dir, "src.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var exe = OperatingSystem.IsWindows() ? ".exe" : "";
            foreach (var name in new[] { "x/bin/ffmpeg" + exe, "deno" + exe })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(name);
            }
        }
        var zipBytes = File.ReadAllBytes(zipPath);
        var handler = new RedirectHandler(req => req.RequestUri!.Host == "github.com"
            ? Redirect("https://assets.example/" + Path.GetFileName(req.RequestUri.AbsolutePath))
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(req.RequestUri.AbsolutePath.EndsWith(".zip") ? zipBytes : [1, 2, 3]),
            });
        var folder = Path.Combine(_dir, "tools");
        var tools = new YouTubeTools(folder, new HttpClient(handler));

        await tools.InstallAsync(null, CancellationToken.None);

        Assert.True(File.Exists(tools.YtDlpPath));
        Assert.True(File.Exists(tools.FfmpegPath));
        Assert.True(File.Exists(tools.DenoPath));
        Assert.True(tools.Ready);
        Assert.Empty(tools.Missing);
        Assert.Empty(Directory.GetFiles(folder, "*.part*"));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    private static HttpResponseMessage Redirect(string to)
    {
        var resp = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
        resp.Headers.Location = new Uri(to);
        return resp;
    }

    private sealed class RedirectHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public void extracts_ffmpeg_from_zip()
    {
        var zipPath = Path.Combine(_dir, "f.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "ffmpeg-x/bin/ffmpeg.exe", "ffmpeg-x/bin/ffprobe.exe", "ffmpeg-x/doc/readme.txt" })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(name);
            }
        }
        var outDir = Path.Combine(_dir, "tools");
        Directory.CreateDirectory(outDir);
        YouTubeTools.ExtractFfmpeg(zipPath, outDir);
        Assert.True(File.Exists(Path.Combine(outDir, "ffmpeg.exe")));
        Assert.True(File.Exists(Path.Combine(outDir, "ffprobe.exe")));
        Assert.False(File.Exists(Path.Combine(outDir, "readme.txt")));
    }

    // Engine thật chạy một "yt-dlp" giả (script shell) nên chỉ chạy trên Linux/macOS.
    private YouTubeDownloadEngine FakeEngine(string script)
    {
        var tool = Path.Combine(_dir, "yt-dlp");
        File.WriteAllText(tool, "#!/bin/sh\n" + script);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new YouTubeDownloadEngine(new YouTubeTools(_dir, new HttpClient()) { YtDlpPath = tool });
    }

    private DownloadJob Job(string cookies = "") => new(
        new DownloadRequest(W, null, null, null, null, cookies, null), Path.Combine(_dir, "out", "Bai hat.mp4"),
        Path.Combine(_dir, "s.json"), 1, new ProbeResult(true, W, 300, true, null, null, null, "video/mp4", ProbeFailure.None, 200));

    [Fact]
    public async Task engine_reports_combined_progress_and_final_file()
    {
        if (OperatingSystem.IsWindows())
            return;
        // Đối số -o là "<thư mục>/Bai hat.%(ext)s": tạo tệp .mp4 tương ứng rồi in đường dẫn.
        var engine = FakeEngine("""
            out=""; prev=""
            for a in "$@"; do [ "$prev" = "-o" ] && out="$a"; prev="$a"; done
            f="${out%.%(ext)s}.mp4"
            echo "DOMA_P 100 200 NA"; echo "DOMA_P 200 200 NA"; echo "DOMA_P 50 100 NA"; echo "DOMA_P 100 100 NA"
            printf 'x' > "$f"; echo "DOMA_FILE:$f"
            """);
        var reports = new List<EngineProgress>();
        var result = await engine.RunAsync(Job(), new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(EngineOutcome.Completed, result.Outcome);
        Assert.Equal(Path.Combine(_dir, "out", "Bai hat.mp4"), result.FinalPath);
        Assert.Equal([100L, 200L, 250L, 300L], reports.Select(r => r.Downloaded));
        Assert.All(reports, r => Assert.Equal(300, r.Total));
    }

    [Fact]
    public async Task engine_maps_errors_and_removes_cookie_file()
    {
        if (OperatingSystem.IsWindows())
            return;
        var engine = FakeEngine("""
            prev=""; for a in "$@"; do [ "$prev" = "--cookies" ] && cat "$a" >&2; prev="$a"; done
            echo "ERROR: [youtube] dQw4w9WgXcQ: Private video" >&2; exit 1
            """);
        var result = await engine.RunAsync(Job("SID=1"), new SyncProgress(_ => { }), CancellationToken.None);

        Assert.Equal(EngineOutcome.Failed, result.Outcome);
        Assert.Contains("riêng tư", result.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(_dir, "s.json.cookies.txt")));
    }

    [Fact]
    public async Task engine_pause_stops_the_tool()
    {
        if (OperatingSystem.IsWindows())
            return;
        var engine = FakeEngine("""echo "DOMA_P 1 100 NA"; sleep 30""");
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var run = engine.RunAsync(Job(), new SyncProgress(_ => started.TrySetResult()), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EngineOutcome.Paused, result.Outcome);
    }

    [Fact]
    public async Task engine_without_tool_fails_with_hint()
    {
        var engine = new YouTubeDownloadEngine(new YouTubeTools(Path.Combine(_dir, "none"), new HttpClient()));
        var result = await engine.RunAsync(Job(), new SyncProgress(_ => { }), CancellationToken.None);
        Assert.Equal(EngineOutcome.Failed, result.Outcome);
        Assert.Contains("công cụ", result.ErrorMessage);
    }

    private sealed class SyncProgress(Action<EngineProgress> report) : IProgress<EngineProgress>
    {
        public void Report(EngineProgress value) => report(value);
    }
}
