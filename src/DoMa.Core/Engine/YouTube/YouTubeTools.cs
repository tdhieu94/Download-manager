using System.IO.Compression;

namespace DoMa.Core.Engine.YouTube;

/// <summary>
/// Công cụ phụ để tải YouTube: yt-dlp (đọc trang video) và ffmpeg (ghép hình với tiếng).
/// Không kèm sẵn trong bộ cài; chỉ tải về từ GitHub lần đầu người dùng tải video YouTube (khi họ đồng ý).
/// </summary>
public sealed class YouTubeTools(string folder, HttpClient http)
{
    public const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    /// <summary>yt-dlp cần cập nhật thường xuyên vì YouTube hay đổi cách phát video.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Folder => folder;

    /// <summary>Đường dẫn chương trình yt-dlp (thay được khi kiểm thử).</summary>
    public string YtDlpPath { get; init; } = Path.Combine(folder, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

    public string FfmpegPath => Path.Combine(folder, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    public bool Ready => File.Exists(YtDlpPath) && File.Exists(FfmpegPath);

    public bool NeedsRefresh => File.Exists(YtDlpPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(YtDlpPath) > RefreshAfter;

    /// <summary>Tải các công cụ còn thiếu. <paramref name="progress"/> báo phần trăm (0–100).</summary>
    public async Task InstallAsync(IProgress<int>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(folder);
            var needYt = !File.Exists(YtDlpPath);
            var needFf = !File.Exists(FfmpegPath);
            if (needYt)
                await DownloadAsync(YtDlpUrl, YtDlpPath, p => progress?.Report(needFf ? p / 6 : p), ct);
            if (needFf)
            {
                var zip = Path.Combine(folder, "ffmpeg.zip.part");
                try
                {
                    await DownloadAsync(FfmpegUrl, zip, p => progress?.Report((needYt ? 17 : 0) + p * (needYt ? 83 : 100) / 100), ct);
                    ExtractFfmpeg(zip, folder);
                }
                finally
                {
                    TryDelete(zip);
                }
            }
            progress?.Report(100);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Tải lại bản yt-dlp mới nhất nếu bản đang có đã cũ; lỗi thì giữ bản cũ.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!NeedsRefresh || !await _gate.WaitAsync(0, ct))
            return;
        try
        {
            var temp = YtDlpPath + ".new";
            try
            {
                await DownloadAsync(YtDlpUrl, temp, null, ct);
                File.Move(temp, YtDlpPath, true);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
            {
                TryDelete(temp);
                // Không cập nhật được (mất mạng, tệp đang dùng): lần sau thử lại.
                try
                {
                    File.SetLastWriteTimeUtc(YtDlpPath, DateTime.UtcNow - RefreshAfter + TimeSpan.FromDays(1));
                }
                catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DownloadAsync(string url, string path, Action<int>? percent, CancellationToken ct)
    {
        var temp = path + ".tmp";
        using (var resp = await GetFollowingRedirectsAsync(url, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buffer = new byte[1 << 16];
            long done = 0;
            var last = -1;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total is > 0 && (int)(done * 100 / total.Value) is var p && p != last)
                {
                    last = p;
                    percent?.Invoke(p);
                }
            }
        }
        File.Move(temp, path, true);
    }

    /// <summary>
    /// GitHub trả link tải bằng chuyển hướng (302) sang máy chủ chứa tệp; HttpClient của DoMa tắt tự chuyển hướng
    /// (để giữ cookie đúng trang) nên phải tự đi theo.
    /// </summary>
    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        for (var hop = 0; ; hop++)
        {
            var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)resp.StatusCode;
            if (code is < 300 or >= 400 || resp.Headers.Location is not { } location || hop >= 10)
                return resp;
            resp.Dispose();
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    /// <summary>Lấy ffmpeg (và ffprobe) trong thư mục bin của tệp zip.</summary>
    public static void ExtractFfmpeg(string zipPath, string folder)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var found = false;
        foreach (var entry in zip.Entries)
        {
            var name = entry.Name.ToLowerInvariant();
            if (name is not ("ffmpeg.exe" or "ffprobe.exe" or "ffmpeg" or "ffprobe"))
                continue;
            entry.ExtractToFile(Path.Combine(folder, entry.Name), true);
            found |= name.StartsWith("ffmpeg", StringComparison.Ordinal);
        }
        if (!found)
            throw new InvalidDataException("Không tìm thấy ffmpeg trong gói tải về.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
