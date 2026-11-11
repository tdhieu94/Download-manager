using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DoMa.Core.Files;
using DoMa.Core.Models;

namespace DoMa.Core.Engine.Hls;

/// <summary>
/// Tải video dạng luồng HLS (.m3u8): chọn chất lượng cao nhất, tải lần lượt các đoạn (vài đoạn song song),
/// giải mã AES-128 nếu có, rồi ghép thành một tệp .ts (hoặc .mp4 với fMP4). Tạm dừng / tải tiếp được theo từng đoạn.
/// Không hỗ trợ video có DRM và phát trực tiếp.
/// </summary>
public sealed class HlsDownloadEngine(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null) : IDownloadEngine
{
    private const int MaxAttempts = 4;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public BandwidthLimiter? Limiter { get; init; }

    public async Task<EngineResult> RunAsync(DownloadJob job, IProgress<EngineProgress> progress, CancellationToken pauseToken)
    {
        var partPath = job.TargetPath + ".part";
        try
        {
            var (playlist, mediaUrl) = await LoadMediaPlaylistAsync(job, pauseToken);
            if (playlist.Unsupported is { } reason)
                return new EngineResult(EngineOutcome.Failed, null, reason);
            if (!playlist.Ended)
                return new EngineResult(EngineOutcome.Failed, null, L.T("Đây là video phát trực tiếp, DoMa chỉ tải được video đã phát xong."));

            var units = new List<(Uri Uri, HlsSegment? Segment)>();
            if (playlist.InitSegment is { } init)
                units.Add((init, null));
            units.AddRange(playlist.Segments.Select(s => (s.Uri, (HlsSegment?)s)));

            var state = HlsState.Load(job.StatePath);
            // Link mới (link cũ hết hạn) thường đổi địa chỉ danh sách phát: tải tiếp được nếu cùng số đoạn
            // và cùng tên tệp đoạn đầu (tránh nối nhầm sang chất lượng khác của video).
            var firstName = FirstName(units);
            var sameStream = state is not null && (state.Playlist == mediaUrl.AbsoluteUri
                || (job.LinkRefreshed && (state.First is null || state.First == firstName)));
            if (state is null || !sameStream || state.Units != units.Count
                || !File.Exists(partPath) || new FileInfo(partPath).Length < state.Bytes)
                state = new HlsState { Playlist = mediaUrl.AbsoluteUri, Units = units.Count };
            state.Playlist = mediaUrl.AbsoluteUri;
            state.First = firstName;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.TargetPath))!);
            var outcome = EngineOutcome.Completed;
            string? error = null;
            var network = false;
            await using (var file = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1 << 16, true))
            {
                file.SetLength(state.Bytes);
                file.Seek(state.Bytes, SeekOrigin.Begin);
                (outcome, error, network) = await TransferAsync(job, units, state, file, progress, pauseToken);
            }
            state.Save(job.StatePath);
            if (outcome != EngineOutcome.Completed)
                return new EngineResult(outcome, null, error, network);

            var target = WithExtension(job.TargetPath, playlist.Extension);
            var finalPath = FileNameResolver.MakeUnique(target, p => File.Exists(p) || Directory.Exists(p));
            File.Move(partPath, finalPath);
            File.Delete(job.StatePath);
            return new EngineResult(EngineOutcome.Completed, finalPath, null);
        }
        catch (OperationCanceledException) when (pauseToken.IsCancellationRequested)
        {
            return new EngineResult(EngineOutcome.Paused, null, null);
        }
        catch (HlsException ex)
        {
            return new EngineResult(EngineOutcome.Failed, null, ex.Message, ex.Network);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EngineResult(EngineOutcome.Failed, null, L.F("Không ghi được tệp: {0}", ex.Message));
        }
    }

    private async Task<(EngineOutcome, string?, bool)> TransferAsync(DownloadJob job, List<(Uri Uri, HlsSegment? Segment)> units,
        HlsState state, FileStream file, IProgress<EngineProgress> progress, CancellationToken ct)
    {
        var window = Math.Clamp(job.Connections, 1, 6);
        var keys = new ConcurrentDictionary<Uri, Task<byte[]>>();
        var queue = new Queue<Task<byte[]>>();
        var next = state.Done;
        try
        {
            while (state.Done < units.Count)
            {
                while (queue.Count < window && next < units.Count)
                {
                    var unit = units[next++];
                    queue.Enqueue(FetchUnitAsync(job.Request, unit.Uri, unit.Segment, keys, ct));
                }
                var data = await queue.Dequeue();
                if (job.Limiter is { } own)
                    await own.WaitAsync(data.Length, ct);
                await file.WriteAsync(data, ct);
                await file.FlushAsync(ct);
                state.Done++;
                state.Bytes += data.Length;
                if (state.Done % 5 == 0)
                    state.Save(job.StatePath);
                // Chưa biết dung lượng thật: ước lượng theo cỡ trung bình các đoạn đã tải.
                var estimate = state.Done < units.Count ? state.Bytes * units.Count / Math.Max(1, state.Done) : state.Bytes;
                progress.Report(new EngineProgress(state.Bytes, estimate, Math.Min(window, units.Count - state.Done + 1)));
            }
            return (EngineOutcome.Completed, null, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DrainAsync(queue);
            return (EngineOutcome.Paused, null, false);
        }
        catch (HlsException ex)
        {
            await DrainAsync(queue);
            return (EngineOutcome.Failed, ex.Message, ex.Network);
        }
    }

    private static async Task DrainAsync(Queue<Task<byte[]>> queue)
    {
        foreach (var task in queue)
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task<(HlsPlaylist Playlist, Uri Url)> LoadMediaPlaylistAsync(DownloadJob job, CancellationToken ct)
    {
        var url = new Uri(job.Probe.FinalUrl ?? job.Request.Url);
        for (var depth = 0; depth < 3; depth++)
        {
            var (text, finalUrl) = await GetTextAsync(job.Request, url, ct);
            var playlist = HlsPlaylist.Parse(text, finalUrl);
            if (!playlist.IsMaster)
                return (playlist, finalUrl);
            url = playlist.Best()!.Uri;
        }
        throw new HlsException(L.T("Danh sách phát lồng nhau quá nhiều cấp."));
    }

    private async Task<(string Text, Uri FinalUrl)> GetTextAsync(DownloadRequest req, Uri url, CancellationToken ct)
    {
        var (bytes, final) = await GetAsync(req, url, ct);
        return (System.Text.Encoding.UTF8.GetString(bytes), final);
    }

    private async Task<byte[]> FetchUnitAsync(DownloadRequest req, Uri uri, HlsSegment? segment,
        ConcurrentDictionary<Uri, Task<byte[]>> keys, CancellationToken ct)
    {
        var data = await GetBytesAsync(req, uri, ct);
        if (segment?.Key is not { } key)
            return data;
        var secret = await keys.GetOrAdd(key.Uri, k => GetBytesAsync(req, k, ct));
        if (secret.Length != 16)
            throw new HlsException(L.T("Khoá giải mã video không hợp lệ."));
        var iv = key.Iv ?? SequenceIv(segment.Sequence);
        try
        {
            using var aes = Aes.Create();
            aes.Key = secret;
            return aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            throw new HlsException(L.T("Không giải mã được một đoạn video."));
        }
    }

    private static string? FirstName(List<(Uri Uri, HlsSegment? Segment)> units) =>
        units.Count > 0 ? Path.GetFileName(units[0].Uri.AbsolutePath) : null;

    /// <summary>IV mặc định của HLS: số thứ tự đoạn, 16 byte big-endian.</summary>
    public static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (var i = 0; i < 8; i++)
            iv[15 - i] = (byte)(sequence >> (8 * i));
        return iv;
    }

    private async Task<byte[]> GetBytesAsync(DownloadRequest req, Uri uri, CancellationToken ct) =>
        (await GetAsync(req, uri, ct)).Data;

    /// <summary>Tải trọn một tài nguyên nhỏ (danh sách phát, khoá, một đoạn), thử lại khi lỗi mạng.</summary>
    private async Task<(byte[] Data, Uri Final)> GetAsync(DownloadRequest req, Uri uri, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = await RequestSender.SendAsync(http, req, uri.AbsoluteUri, null, ct);
                if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.Gone or HttpStatusCode.NotFound)
                    throw new HlsException(L.T("Link video đã hết hạn hoặc bị chặn. Bấm chuột phải › Làm mới link để tải tiếp."));
                resp.EnsureSuccessStatusCode();
                var data = await resp.Content.ReadAsByteArrayAsync(ct);
                if (Limiter is not null)
                    await Limiter.WaitAsync(data.Length, ct);
                return (data, resp.RequestMessage?.RequestUri ?? uri);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                if (attempt >= MaxAttempts)
                    throw new HlsException(L.T("Mất kết nối khi tải video. Bấm tải tiếp để thử lại."), network: true);
                await _delay(TimeSpan.FromSeconds(Math.Min(10, attempt * 2)), ct);
            }
        }
    }

    /// <summary>Đổi đuôi .m3u8 (hoặc đuôi video khác) thành đuôi thật của tệp ghép (.ts / .mp4).</summary>
    public static string WithExtension(string path, string extension)
    {
        var current = Path.GetExtension(path);
        string[] replaceable = [".m3u8", ".m3u", ".ts", ".mp4", ".m4v", ".mkv", ".webm", ".m4s", ""];
        return replaceable.Contains(current, StringComparer.OrdinalIgnoreCase)
            ? Path.ChangeExtension(path, null) + extension
            : path + extension;
    }

    private sealed class HlsException(string message, bool network = false) : Exception(message)
    {
        public bool Network { get; } = network;
    }

    private sealed class HlsState
    {
        public string Playlist { get; set; } = "";
        public int Units { get; set; }
        public string? First { get; set; }
        public int Done { get; set; }
        public long Bytes { get; set; }

        public static HlsState? Load(string path)
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<HlsState>(File.ReadAllText(path)) : null;
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, path, true);
        }
    }
}

/// <summary>
/// Chọn engine theo loại link: video YouTube dùng engine YouTube (yt-dlp), luồng HLS dùng HlsDownloadEngine,
/// còn lại dùng engine tải nhiều luồng.
/// </summary>
public sealed class MediaAwareEngine(IDownloadEngine files, IDownloadEngine hls, IDownloadEngine? youtube = null, IDownloadEngine? torrent = null) : IDownloadEngine
{
    public Task<EngineResult> RunAsync(DownloadJob job, IProgress<EngineProgress> progress, CancellationToken pauseToken)
    {
        if (torrent is not null && Torrent.TorrentLink.IsTorrent(job.Request.Url, job.Request.Mime))
            return torrent.RunAsync(job, progress, pauseToken);
        if (youtube is not null && YouTube.MediaLink.IsVideo(job.Request.Url))
            return youtube.RunAsync(job, progress, pauseToken);
        return HlsPlaylist.Looks(job.Probe.FinalUrl ?? job.Request.Url, job.Probe.ContentType ?? job.Request.Mime)
            ? hls.RunAsync(job, progress, pauseToken)
            : files.RunAsync(job, progress, pauseToken);
    }

    /// <summary>
    /// Tải bằng công cụ ngoài (yt-dlp, aria2c): giới hạn tốc độ chỉ đặt được lúc bắt đầu, nên muốn đổi thì phải chạy lại.
    /// </summary>
    public static bool UsesExternalTool(DownloadRequest request) =>
        Torrent.TorrentLink.IsTorrent(request.Url, request.Mime) || YouTube.MediaLink.IsVideo(request.Url);

    /// <summary>Video dạng luồng (HLS, YouTube): chưa biết chính xác dung lượng, tải tiếp được theo cách riêng.</summary>
    public static bool IsStream(string url, string? contentType) =>
        YouTube.MediaLink.IsVideo(url) || HlsPlaylist.Looks(url, contentType) || Torrent.TorrentLink.IsTorrent(url, contentType);
}
