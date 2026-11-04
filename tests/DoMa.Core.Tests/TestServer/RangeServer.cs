using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DoMa.Core.Tests.TestServer;

public class RangeServerOptions
{
    public byte[] Content { get; set; } = [];
    public bool HonorRange { get; set; } = true;
    public bool HonorRangeAfterProbe { get; set; } = true;
    public bool AdvertiseRanges { get; set; } = true;
    public bool SendLength { get; set; } = true;
    public string? ETag { get; set; }
    public string? RequiredCookie { get; set; }
    public string? ContentDisposition { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    public int? FailAfterBytes { get; set; }
    public bool FailOnce { get; set; }
    public int? StallOnceAfterBytes { get; set; }
    public bool StallHeadersOnce { get; set; }
    public TimeSpan ChunkDelay { get; set; }
    public Func<long, TimeSpan>? ChunkDelayForStart { get; set; }
    public bool ShiftRangeStart { get; set; }
    public Func<int, int?>? StatusForConcurrent { get; set; }
    public int StatusCode { get; set; } = 200;
    public string RedirectLocation { get; set; } = "/final/file.bin";
}

public record RecordedRequest(string Path, string Cookie, string Referer);

public sealed class RangeServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Uri _baseUri;
    private int _current;
    private int _maxConcurrent;
    private int _requestCount;
    private long _bytesSent;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public RangeServerOptions Options { get; }
    public int MaxConcurrentSeen => Volatile.Read(ref _maxConcurrent);
    public int RequestCount => Volatile.Read(ref _requestCount);
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    public void ResetMaxConcurrent() => Volatile.Write(ref _maxConcurrent, 0);

    private RangeServer(WebApplication app, Uri baseUri, RangeServerOptions options)
    {
        _app = app;
        _baseUri = baseUri;
        Options = options;
    }

    public Uri Url(string path) => new(_baseUri, path);

    public static async Task<RangeServer> StartAsync(Action<RangeServerOptions>? configure = null)
    {
        var options = new RangeServerOptions();
        configure?.Invoke(options);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        RangeServer? server = null;
        app.Run(ctx => server!.HandleAsync(ctx));
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new RangeServer(app, new Uri(address), options);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        Interlocked.Increment(ref _requestCount);
        _requests.Enqueue(new RecordedRequest(ctx.Request.Path.Value ?? "/", ctx.Request.Headers.Cookie.ToString(), ctx.Request.Headers.Referer.ToString()));
        var now = Interlocked.Increment(ref _current);
        try
        {
            RecordConcurrency(now);
            await RespondAsync(ctx, now);
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }

    private void RecordConcurrency(int now)
    {
        int seen;
        while (now > (seen = Volatile.Read(ref _maxConcurrent)))
        {
            if (Interlocked.CompareExchange(ref _maxConcurrent, now, seen) == seen)
                break;
        }
    }

    private async Task RespondAsync(HttpContext ctx, int concurrent)
    {
        var o = Options;
        var path = ctx.Request.Path.Value ?? "/";

        if (path == "/redirect")
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers.Location = o.RedirectLocation;
            return;
        }

        if (o.StallHeadersOnce)
        {
            o.StallHeadersOnce = false;
            await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
        }

        var throttled = o.StatusForConcurrent?.Invoke(concurrent);
        if (throttled is int throttleStatus)
        {
            ctx.Response.StatusCode = throttleStatus;
            return;
        }

        if (o.RequiredCookie is not null && !ctx.Request.Headers.Cookie.ToString().Contains(o.RequiredCookie, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 403;
            return;
        }

        if (o.StatusCode != 200)
        {
            ctx.Response.StatusCode = o.StatusCode;
            return;
        }

        var content = o.Content;
        var total = content.LongLength;
        var res = ctx.Response;
        res.ContentType = o.ContentType;
        if (o.ETag is not null)
            res.Headers.ETag = o.ETag;
        if (o.ContentDisposition is not null)
            res.Headers.ContentDisposition = o.ContentDisposition;
        if (o.AdvertiseRanges)
            res.Headers.AcceptRanges = "bytes";

        long start = 0, end = total - 1;
        var partial = false;
        var rangeHeader = ctx.Request.Headers.Range.ToString();
        if (o.HonorRange && rangeHeader.StartsWith("bytes=", StringComparison.Ordinal)
            && (o.HonorRangeAfterProbe || rangeHeader == "bytes=0-0")
            && TryParseRange(rangeHeader[6..], total, out start, out end))
        {
            partial = true;
            if (o.ShiftRangeStart && rangeHeader != "bytes=0-0" && start < end)
                start++;
        }

        if (partial)
        {
            res.StatusCode = 206;
            res.Headers.ContentRange = $"bytes {start}-{end}/{total}";
        }
        else
        {
            start = 0;
            end = total - 1;
        }

        var length = end - start + 1;
        if (o.SendLength)
            res.ContentLength = length;

        var written = 0L;
        var offset = start;
        while (offset <= end)
        {
            var count = (int)Math.Min(64 * 1024, end - offset + 1);
            if (o.FailAfterBytes is int limit)
            {
                var remaining = limit - written;
                if (remaining <= 0)
                {
                    Abort(ctx);
                    return;
                }
                count = (int)Math.Min(count, remaining);
            }
            if (o.StallOnceAfterBytes is int stall && written >= stall)
            {
                o.StallOnceAfterBytes = null;
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
            }
            await res.Body.WriteAsync(content.AsMemory((int)offset, count), ctx.RequestAborted);
            await res.Body.FlushAsync(ctx.RequestAborted);
            Interlocked.Add(ref _bytesSent, count);
            var delay = o.ChunkDelayForStart?.Invoke(start) ?? o.ChunkDelay;
            if (delay > TimeSpan.Zero && offset + count <= end)
                await Task.Delay(delay, ctx.RequestAborted);
            offset += count;
            written += count;
        }

        if (o.FailAfterBytes is int after && written >= after && length > after)
            Abort(ctx);
    }

    private void Abort(HttpContext ctx)
    {
        if (Options.FailOnce)
            Options.FailAfterBytes = null;
        ctx.Abort();
    }

    private static bool TryParseRange(string spec, long total, out long start, out long end)
    {
        start = 0;
        end = total - 1;
        var parts = spec.Split('-', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start))
            return false;
        if (parts[1].Length > 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end))
                return false;
            end = Math.Min(end, total - 1);
        }
        return start <= end;
    }
}
