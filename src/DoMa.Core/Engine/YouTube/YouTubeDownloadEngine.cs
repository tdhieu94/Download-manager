using System.Diagnostics;
using System.Text;
using DoMa.Core.Models;

namespace DoMa.Core.Engine.YouTube;

/// <summary>
/// Tải video YouTube bằng yt-dlp (+ ffmpeg để ghép hình và tiếng thành một tệp .mp4).
/// Tạm dừng = dừng yt-dlp; tải tiếp = chạy lại, yt-dlp tự nối các tệp .part đang dở.
/// </summary>
public sealed class YouTubeDownloadEngine(YouTubeTools tools) : IDownloadEngine
{
    private const string NotReady = "Chưa có công cụ tải YouTube. Hãy tải lại video từ trình duyệt để DoMa cài công cụ.";

    public async Task<EngineResult> RunAsync(DownloadJob job, IProgress<EngineProgress> progress, CancellationToken pauseToken)
    {
        if (!File.Exists(tools.YtDlpPath))
            return new EngineResult(EngineOutcome.Failed, null, NotReady);
        var url = YouTubeLink.Normalize(job.Request.Url) ?? job.Request.Url;
        var cookieFile = WriteCookies(job);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.TargetPath))!);
            var stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(job.TargetPath))!, Path.GetFileNameWithoutExtension(job.TargetPath));
            var ffmpeg = File.Exists(tools.FfmpegPath) ? tools.Folder : null;
            var args = YtDlp.DownloadArguments(url, stem, ffmpeg, cookieFile);
            return await RunProcessAsync(args, job, progress, pauseToken);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new EngineResult(EngineOutcome.Failed, null, "Không chạy được công cụ tải YouTube: " + e.Message);
        }
        finally
        {
            if (cookieFile is not null)
            {
                try
                {
                    File.Delete(cookieFile);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>Lấy tiêu đề và dung lượng ước tính (chạy yt-dlp -J).</summary>
    public async Task<(YouTubeInfo? Info, string? Error)> InfoAsync(DownloadRequest req, CancellationToken ct)
    {
        if (!File.Exists(tools.YtDlpPath))
            return (null, NotReady);
        var url = YouTubeLink.Normalize(req.Url) ?? req.Url;
        var cookieFile = WriteCookies(req.Cookies, Path.Combine(Path.GetTempPath(), $"doma-yt-{Guid.NewGuid():N}.txt"));
        try
        {
            using var process = Start(YtDlp.InfoArguments(url, cookieFile));
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var reg = ct.Register(() => Kill(process));
            await process.WaitForExitAsync(ct);
            var output = await stdout;
            var errors = (await stderr).Split('\n', StringSplitOptions.TrimEntries);
            if (process.ExitCode != 0 || output.Trim().Length == 0)
                return (null, YtDlp.DescribeError(errors).Message);
            var info = YtDlp.ParseInfo(output);
            return info.IsLive ? (null, "Video đang phát trực tiếp, hãy tải sau khi phát xong.") : (info, null);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, "Không đọc được thông tin video YouTube.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return (null, "Không chạy được công cụ tải YouTube: " + e.Message);
        }
        finally
        {
            if (cookieFile is not null)
            {
                try
                {
                    File.Delete(cookieFile);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private async Task<EngineResult> RunProcessAsync(IReadOnlyList<string> args, DownloadJob job, IProgress<EngineProgress> progress, CancellationToken ct)
    {
        using var process = Start(args);
        var errors = new Queue<string>();
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                lock (errors)
                {
                    errors.Enqueue(line.Trim());
                    if (errors.Count > 50)
                        errors.Dequeue();
                }
            }
        });
        string? finalPath = null;
        long offset = 0, lastDone = 0, lastTotal = 0;
        using (ct.Register(() => Kill(process)))
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (YtDlp.ParseFile(line) is { } file)
                {
                    finalPath = file;
                    continue;
                }
                if (YtDlp.ParseProgress(line) is not { } p)
                    continue;
                // Hình và tiếng tải lần lượt thành hai tệp: cộng dồn để thanh tiến độ không tụt về 0.
                if (p.Downloaded < lastDone)
                    offset += Math.Max(lastTotal, lastDone);
                lastDone = p.Downloaded;
                lastTotal = p.Total ?? lastTotal;
                var done = offset + p.Downloaded;
                var total = Math.Max(job.Probe.Size ?? 0, offset + (p.Total ?? 0));
                progress.Report(new EngineProgress(done, total > 0 ? Math.Max(total, done) : null, 1));
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await stderr;
        }
        if (ct.IsCancellationRequested)
            return new EngineResult(EngineOutcome.Paused, null, null);
        if (process.ExitCode == 0 && finalPath is not null && File.Exists(finalPath))
            return new EngineResult(EngineOutcome.Completed, finalPath, null);
        string[] lines;
        lock (errors)
            lines = [.. errors];
        var (message, network) = YtDlp.DescribeError(lines);
        return new EngineResult(EngineOutcome.Failed, null, message, network);
    }

    /// <summary>Các tệp dở yt-dlp tạo cạnh tệp đích: "Tên.f137.mp4.part", "Tên.f140.m4a", "Tên.temp.mp4"…</summary>
    public static IReadOnlyList<string> PartialFiles(string targetPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        var stem = Path.GetFileNameWithoutExtension(targetPath);
        if (dir is null || stem.Length == 0 || !Directory.Exists(dir))
            return [];
        try
        {
            return Directory.EnumerateFiles(dir, stem + ".*")
                .Where(p =>
                {
                    var rest = Path.GetFileName(p)[stem.Length..];
                    return rest.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                        || rest.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                        || rest.StartsWith(".temp.", StringComparison.OrdinalIgnoreCase)
                        || System.Text.RegularExpressions.Regex.IsMatch(rest, @"^\.f[0-9a-z-]+\.[a-z0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                })
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private Process Start(IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(tools.YtDlpPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            info.ArgumentList.Add(a);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        return Process.Start(info) ?? throw new IOException("yt-dlp không khởi động được.");
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string? WriteCookies(DownloadJob job) => WriteCookies(job.Request.Cookies, job.StatePath + ".cookies.txt");

    private static string? WriteCookies(string? cookies, string path)
    {
        if (string.IsNullOrWhiteSpace(cookies))
            return null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, YtDlp.NetscapeCookies(cookies));
        return path;
    }
}
