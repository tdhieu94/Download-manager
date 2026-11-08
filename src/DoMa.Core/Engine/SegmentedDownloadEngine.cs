using System.Diagnostics;
using System.Net;
using DoMa.Core.Files;
using Microsoft.Win32.SafeHandles;

namespace DoMa.Core.Engine;

public class SegmentedDownloadEngine(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null) : IDownloadEngine
{
    private const int BufferSize = 64 * 1024;
    private static readonly RetryPolicy Retry = new();
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(2);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public TimeSpan BodyTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Bộ giới hạn tốc độ dùng chung cho mọi lượt tải (null = không giới hạn).</summary>
    public BandwidthLimiter? Limiter { get; init; }

    public async Task<EngineResult> RunAsync(DownloadJob job, IProgress<EngineProgress> progress, CancellationToken pauseToken)
    {
        var partPath = job.TargetPath + ".part";
        try
        {
            var saved = File.Exists(partPath) ? LoadResumable(job.StatePath) : null;
            if (saved is not null && Changed(saved, job.Probe, job.LinkRefreshed))
                return new EngineResult(EngineOutcome.NeedsRestart, null, null);
            if (saved is not null && job.LinkRefreshed)
            {
                // Link mới (thường khác ETag/thời gian) cho cùng tệp: nhận dấu vết mới để lần sau so cho đúng.
                saved.ETag = job.Probe.ETag;
                saved.LastModified = job.Probe.LastModified;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.TargetPath))!);
            Run run;
            EngineOutcome outcome;
            string? error;
            using (var handle = File.OpenHandle(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                if (saved is not null && (!job.Probe.SupportsRange || RandomAccess.GetLength(handle) != saved.Size))
                    saved = null;
                var state = saved ?? CreateState(job.Probe, job.Connections);
                if (saved is null)
                    RandomAccess.SetLength(handle, state.Size ?? 0);
                run = new Run(job, state, handle, pauseToken);
                (outcome, error) = await TransferAsync(run, progress);
            }

            if (outcome != EngineOutcome.Completed)
            {
                run.SaveState();
                return new EngineResult(outcome, null, error, outcome == EngineOutcome.Failed && error == NetworkMessage);
            }

            var finalPath = FileNameResolver.MakeUnique(job.TargetPath, p => File.Exists(p) || Directory.Exists(p));
            File.Move(partPath, finalPath);
            if (File.Exists(job.StatePath))
                File.Delete(job.StatePath);
            return new EngineResult(EngineOutcome.Completed, finalPath, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EngineResult(EngineOutcome.Failed, null, WriteError(ex, partPath));
        }
    }

    private static DownloadState? LoadResumable(string statePath)
    {
        var state = DownloadState.Load(statePath);
        return state is { SupportsRange: true, Size: long size } && Covers(state.Segments, size) ? state : null;
    }

    private static bool Covers(List<Segment>? segments, long size)
    {
        if (segments is not { Count: > 0 })
            return false;
        var next = 0L;
        foreach (var s in segments.OrderBy(s => s.Start))
        {
            if (s.Start != next || s.End < s.Start || s.Downloaded < 0 || s.Downloaded > s.End - s.Start + 1)
                return false;
            next = s.End + 1;
        }
        return next == size;
    }

    private static bool Changed(DownloadState saved, ProbeResult probe, bool linkRefreshed) =>
        saved.Size != probe.Size
        || (!linkRefreshed && ChangedTag(saved, probe));

    private static bool ChangedTag(DownloadState saved, ProbeResult probe) =>
        (saved.ETag is not null && saved.ETag != probe.ETag)
        || (saved.LastModified is not null && saved.LastModified != probe.LastModified);

    private static DownloadState CreateState(ProbeResult probe, int connections)
    {
        var ranged = probe.SupportsRange && probe.Size > 0;
        return new DownloadState
        {
            Size = probe.Size,
            ETag = probe.ETag,
            LastModified = probe.LastModified,
            SupportsRange = ranged,
            Segments = ranged
                ? SegmentPlanner.Initial(probe.Size!.Value, connections)
                : [new Segment { Start = 0, End = (probe.Size ?? 0) - 1 }],
        };
    }

    private async Task<(EngineOutcome, string?)> TransferAsync(Run run, IProgress<EngineProgress> progress)
    {
        using var monitorCts = new CancellationTokenSource();
        var monitor = MonitorAsync(run, progress, monitorCts.Token);
        try
        {
            var fault = run.State.SupportsRange ? await RunRangedAsync(run) : await RunSingleAsync(run);
            if (run.SourceChanged)
                return (EngineOutcome.NeedsRestart, null);
            if (fault is null && run.RangeIgnored && !run.PauseToken.IsCancellationRequested)
            {
                run.ResetToSingle();
                fault = await RunSingleAsync(run);
            }

            if (fault is null && run.IsComplete())
                return (EngineOutcome.Completed, null);
            if (run.PauseToken.IsCancellationRequested)
                return (EngineOutcome.Paused, null);
            if (fault is not null)
                return (EngineOutcome.Failed, Describe(fault, run.PartPath));
            return (EngineOutcome.Failed, L.T("Dung lượng tải về không khớp"));
        }
        finally
        {
            monitorCts.Cancel();
            await monitor;
            run.Report(progress);
        }
    }

    private async Task<Exception?> RunRangedAsync(Run run)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(run.PauseToken);
        var workers = Math.Max(1, Math.Min(run.Job.Connections, run.PendingCount()));
        run.StartWorkers(workers);
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => RangedWorkerAsync(run, cts)));
        return run.Fault;
    }

    private async Task RangedWorkerAsync(Run run, CancellationTokenSource cts)
    {
        Interlocked.Increment(ref run.Active);
        try
        {
            Segment? segment = null;
            while ((segment = run.Next(segment)) is not null)
            {
                var allowed = run.Allowed;
                try
                {
                    if (!await DownloadSegmentAsync(run, segment, cts.Token))
                    {
                        run.RangeIgnored = true;
                        cts.Cancel();
                        return;
                    }
                }
                catch (Exception ex) when (!cts.IsCancellationRequested && Retryable(ex))
                {
                    var throttled = ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable }
                        && run.Throttle(allowed);
                    var attempt = throttled ? 1 : run.Fail(segment);
                    if (attempt >= Retry.MaxAttemptsPerSegment)
                        throw;
                    await _delay(Retry.Delay(attempt), cts.Token);
                }
            }
        }
        catch (SourceChangedException)
        {
            run.SourceChanged = true;
            cts.Cancel();
        }
        catch (Exception ex) when (cts.IsCancellationRequested && ex is OperationCanceledException or HttpRequestException or IOException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            run.SetFault(ex);
            cts.Cancel();
        }
        finally
        {
            Interlocked.Decrement(ref run.Active);
        }
    }

    private static bool Retryable(Exception ex) => ex switch
    {
        WriteFailedException => false,
        HttpRequestException { StatusCode: { } code } => code is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
        _ => ex is HttpRequestException or IOException,
    };

    private async Task<bool> DownloadSegmentAsync(Run run, Segment segment, CancellationToken ct)
    {
        var (position, end) = run.Bounds(segment);
        if (position > end)
            return true;

        using var resp = await SendAsync(run.Job, (position, end), ct);
        if (resp.StatusCode == HttpStatusCode.OK)
            return false;
        if (resp.StatusCode != HttpStatusCode.PartialContent)
            throw new HttpRequestException(null, null, resp.StatusCode);
        var range = resp.Content.Headers.ContentRange;
        if (range?.From != position || (run.State.Size is long size && range.Length != size))
            return false;
        if (run.State.ETag is not null && resp.Headers.ETag?.Tag is { } etag && etag != run.State.ETag)
            throw new SourceChangedException();

        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await ReadAsync(body, buffer, idle, ct)) > 0)
        {
            var take = run.Claim(segment, position, read);
            if (Limiter is not null)
                await Limiter.WaitAsync(take, ct);
            await WriteAsync(run.Handle, buffer.AsMemory(0, take), position, ct);
            position += take;
            if (run.Advance(segment, take))
                return true;
        }
        throw new HttpIOException(HttpRequestError.ResponseEnded);
    }

    private async Task<Exception?> RunSingleAsync(Run run)
    {
        Interlocked.Increment(ref run.Active);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadSingleAsync(run);
                    return null;
                }
                catch (Exception ex) when (!run.PauseToken.IsCancellationRequested && Retryable(ex) && attempt < Retry.MaxAttemptsPerSegment)
                {
                    run.RestartSingle();
                    await _delay(Retry.Delay(attempt), run.PauseToken);
                }
            }
        }
        catch (OperationCanceledException) when (run.PauseToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return ex;
        }
        finally
        {
            Interlocked.Decrement(ref run.Active);
        }
    }

    private async Task DownloadSingleAsync(Run run)
    {
        var segment = run.State.Segments[0];
        using var resp = await SendAsync(run.Job, null, run.PauseToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(null, null, resp.StatusCode);

        await using var body = await resp.Content.ReadAsStreamAsync(run.PauseToken);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(run.PauseToken);
        var buffer = new byte[BufferSize];
        var position = segment.Start + segment.Downloaded;
        int read;
        while ((read = await ReadAsync(body, buffer, idle, run.PauseToken)) > 0)
        {
            if (Limiter is not null)
                await Limiter.WaitAsync(read, run.PauseToken);
            await WriteAsync(run.Handle, buffer.AsMemory(0, read), position, run.PauseToken);
            position += read;
            run.Advance(segment, read);
        }
        run.StreamEnded = true;
    }

    private async Task<HttpResponseMessage> SendAsync(DownloadJob job, (long From, long To)? range, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HeaderTimeout);
        try
        {
            return await RequestSender.SendAsync(http, job.Request, job.Probe.FinalUrl ?? job.Request.Url, range, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpIOException(HttpRequestError.Unknown);
        }
    }

    private async ValueTask<int> ReadAsync(Stream body, Memory<byte> buffer, CancellationTokenSource idle, CancellationToken ct)
    {
        idle.CancelAfter(BodyTimeout);
        try
        {
            return await body.ReadAsync(buffer, idle.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpIOException(HttpRequestError.Unknown);
        }
    }

    private static async ValueTask WriteAsync(SafeFileHandle handle, ReadOnlyMemory<byte> data, long offset, CancellationToken ct)
    {
        try
        {
            await RandomAccess.WriteAsync(handle, data, offset, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WriteFailedException(ex);
        }
    }

    private static async Task MonitorAsync(Run run, IProgress<EngineProgress> progress, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ProgressInterval);
        var sinceSave = Stopwatch.StartNew();
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                run.Report(progress);
                if (sinceSave.Elapsed >= SaveInterval)
                {
                    try
                    {
                        run.SaveState();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                    sinceSave.Restart();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string Describe(Exception fault, string partPath) => fault switch
    {
        WriteFailedException w => WriteError(w.InnerException!, partPath),
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Gone } => L.T("Link đã hết hạn. Bấm chuột phải › Làm mới link để tải tiếp"),
        HttpRequestException { StatusCode: { } code } => L.F("Máy chủ trả lỗi {0}", (int)code),
        _ => NetworkMessage,
    };

    private static string NetworkMessage => L.T("Lỗi mạng, đã thử lại nhiều lần nhưng không tải được");

    private static string WriteError(Exception ex, string path) =>
        (ex.HResult & 0xFFFF) is 112 or 39
            ? L.T("Ổ đĩa đầy, không đủ chỗ để lưu tệp")
            : L.F("Không ghi được tệp: {0}", path);

    private sealed class WriteFailedException(Exception inner) : IOException(inner.Message, inner);

    private sealed class SourceChangedException : Exception;

    private sealed class Run(DownloadJob job, DownloadState state, SafeFileHandle handle, CancellationToken pauseToken)
    {
        private readonly Lock _gate = new();
        private readonly HashSet<Segment> _owned = [];
        private readonly Dictionary<Segment, (int Count, long Downloaded)> _failures = [];
        private int _workers;
        private int _allowed;
        public int Active;

        public DownloadJob Job { get; } = job;
        public DownloadState State { get; } = state;
        public SafeFileHandle Handle { get; } = handle;
        public CancellationToken PauseToken { get; } = pauseToken;
        public string PartPath => Job.TargetPath + ".part";
        public Exception? Fault { get; private set; }
        public volatile bool RangeIgnored;
        public volatile bool StreamEnded;
        public volatile bool SourceChanged;

        public int Allowed
        {
            get
            {
                lock (_gate)
                    return _allowed;
            }
        }

        public void StartWorkers(int count)
        {
            lock (_gate)
                _workers = _allowed = count;
        }

        public bool Throttle(int seen)
        {
            lock (_gate)
            {
                if (_allowed == seen)
                    _allowed = Math.Max(1, seen / 2);
                return seen > 1;
            }
        }

        public int Fail(Segment segment)
        {
            lock (_gate)
            {
                var count = _failures.TryGetValue(segment, out var last) && last.Downloaded == segment.Downloaded ? last.Count + 1 : 1;
                _failures[segment] = (count, segment.Downloaded);
                return count;
            }
        }

        public int PendingCount()
        {
            lock (_gate)
                return State.Segments.Count(s => SegmentPlanner.Remaining(s) > 0);
        }

        public Segment? Next(Segment? finished)
        {
            lock (_gate)
            {
                if (finished is not null)
                    _owned.Remove(finished);
                if (_workers > _allowed)
                {
                    _workers--;
                    return null;
                }
                var next = State.Segments.FirstOrDefault(s => !_owned.Contains(s) && SegmentPlanner.Remaining(s) > 0)
                    ?? SegmentPlanner.SplitLargest(State.Segments);
                if (next is not null)
                    _owned.Add(next);
                else
                    _workers--;
                return next;
            }
        }

        public (long Position, long End) Bounds(Segment segment)
        {
            lock (_gate)
                return (segment.Start + segment.Downloaded, segment.End);
        }

        public int Claim(Segment segment, long position, int read)
        {
            lock (_gate)
                return (int)Math.Min(read, segment.End - position + 1);
        }

        public bool Advance(Segment segment, int count)
        {
            lock (_gate)
            {
                segment.Downloaded += count;
                return SegmentPlanner.Remaining(segment) <= 0;
            }
        }

        public void SetFault(Exception ex)
        {
            lock (_gate)
                Fault ??= ex;
        }

        public void RestartSingle()
        {
            lock (_gate)
            {
                State.Segments[0].Downloaded = 0;
                StreamEnded = false;
                RandomAccess.SetLength(Handle, 0);
                RandomAccess.SetLength(Handle, State.Size ?? 0);
            }
        }

        public void ResetToSingle()
        {
            lock (_gate)
            {
                _owned.Clear();
                State.SupportsRange = false;
                State.Segments = [new Segment { Start = 0, End = State.Size!.Value - 1 }];
            }
        }

        public bool IsComplete()
        {
            lock (_gate)
            {
                var total = State.Segments.Sum(s => s.Downloaded);
                var length = RandomAccess.GetLength(Handle);
                return State.Size is long size
                    ? total == size && length == size && State.Segments.All(s => SegmentPlanner.Remaining(s) == 0)
                    : StreamEnded && length == total;
            }
        }

        public void Report(IProgress<EngineProgress> progress)
        {
            long downloaded;
            lock (_gate)
                downloaded = State.Segments.Sum(s => s.Downloaded);
            progress.Report(new EngineProgress(downloaded, State.Size, Volatile.Read(ref Active)));
        }

        public void SaveState()
        {
            lock (_gate)
                State.Save(Job.StatePath);
        }
    }
}
