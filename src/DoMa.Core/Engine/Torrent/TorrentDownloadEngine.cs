using System.Diagnostics;
using System.Text;
using DoMa.Core.Engine.YouTube;

namespace DoMa.Core.Engine.Torrent;

/// <summary>
/// Tải torrent (link magnet hoặc tệp .torrent) bằng aria2c. Không chia sẻ lại sau khi tải xong (seed-time=0).
/// Tạm dừng = dừng aria2c; tải tiếp = chạy lại, aria2c đọc tệp .aria2 để tải tiếp phần còn thiếu.
/// </summary>
public sealed class TorrentDownloadEngine(YouTubeTools tools) : IDownloadEngine
{
    private static string NotReady => L.T("Chưa có công cụ tải torrent. Hãy thêm lại link torrent để DoMa cài công cụ.");

    /// <summary>Giới hạn tốc độ tải hiện tại (byte/giây, 0 = không giới hạn).</summary>
    public Func<long>? SpeedLimit { get; init; }

    /// <summary>Thư mục chứa nội dung torrent của một mục: thư mục cha của đường dẫn đích.</summary>
    public static string FolderOf(string targetPath) => Path.GetDirectoryName(Path.GetFullPath(targetPath))!;

    public async Task<EngineResult> RunAsync(DownloadJob job, IProgress<EngineProgress> progress, CancellationToken pauseToken)
    {
        if (!File.Exists(tools.Aria2Path))
            return new EngineResult(EngineOutcome.Failed, null, NotReady);
        var dir = FolderOf(job.TargetPath);
        try
        {
            Directory.CreateDirectory(dir);
            var args = Aria2.Arguments(job.Request.Url, dir, job.Request.Cookies, job.Request.Referrer, BandwidthLimiter.Tighter(SpeedLimit?.Invoke() ?? 0, job.Limiter?.LimitBytesPerSec ?? 0));
            return await RunProcessAsync(args, dir, progress, pauseToken);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new EngineResult(EngineOutcome.Failed, null, L.T("Không chạy được công cụ tải torrent: ") + e.Message);
        }
    }

    private async Task<EngineResult> RunProcessAsync(IReadOnlyList<string> args, string dir, IProgress<EngineProgress> progress, CancellationToken ct)
    {
        var info = new ProcessStartInfo(tools.Aria2Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // aria2c chỉ dùng được proxy HTTP (cho tệp .torrent và tracker); proxy SOCKS thì để nó nối thẳng.
        if (tools.ProxyFor(httpOnly: true) is { } proxy)
            info.ArgumentList.Add("--all-proxy=" + proxy);
        foreach (var a in args)
            info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new IOException(L.T("aria2c không khởi động được."));
        var tail = new Queue<string>();
        void Keep(string line)
        {
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 40)
                    tail.Dequeue();
            }
        }
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                Keep(line);
        });
        string? completed = null;
        using (ct.Register(() => Kill(process)))
        {
            // Dòng tiến độ của aria2c kết thúc bằng \r (ghi đè trên cùng một dòng), nên tự tách dòng.
            var buffer = new char[4096];
            var line = new StringBuilder();
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer, CancellationToken.None)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (c is not ('\r' or '\n'))
                    {
                        line.Append(c);
                        continue;
                    }
                    if (line.Length == 0)
                        continue;
                    var text = line.ToString();
                    line.Clear();
                    if (Aria2.ParseProgress(text) is { } p)
                    {
                        progress.Report(new EngineProgress(p.Done, p.Total, Math.Max(1, p.Seeders ?? 1)));
                        continue;
                    }
                    Keep(text);
                    if (Aria2.ParseComplete(text) is { } file)
                        completed ??= file;
                }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await stderr;
        }
        if (ct.IsCancellationRequested)
            return new EngineResult(EngineOutcome.Paused, null, null);
        string[] lines;
        lock (tail)
            lines = [.. tail];
        if (process.ExitCode == 0)
        {
            var final = completed is null ? null : Aria2.TopEntry(dir, completed);
            return new EngineResult(EngineOutcome.Completed, final is not null && (File.Exists(final) || Directory.Exists(final)) ? final : null, null);
        }
        var (message, network) = Aria2.DescribeExit(process.ExitCode, lines);
        return new EngineResult(EngineOutcome.Failed, null, message, network);
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
}
