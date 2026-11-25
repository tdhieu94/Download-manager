using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using DoMa.Core.Models;

namespace DoMa.Core.Engine;

public static class RequestSender
{
    private const int MaxRedirects = 10;

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, DownloadRequest req, string url, (long From, long To)? range, CancellationToken ct)
    {
        Uri.TryCreate(req.Url, UriKind.Absolute, out var cookieOrigin);
        var uri = new Uri(url);
        for (var hop = 0; ; hop++)
        {
            var resp = await SendOnceAsync(http, req, uri, range, cookieOrigin, ct);
            if (hop == MaxRedirects || !IsRedirect(resp.StatusCode) || resp.Headers.Location is not { } location)
                return resp;
            resp.RequestMessage?.Dispose();
            resp.Dispose();
            uri = new Uri(uri, location);
        }
    }

    /// <summary>
    /// Thử HTTP/3 trước nếu máy chủ có thể hỗ trợ: HTTP/3 chạy trên UDP (QUIC), nên vẫn nhanh khi nhà mạng bóp
    /// các kết nối TCP ra nước ngoài (trình duyệt cũng làm vậy). Không được thì gửi lại như cũ (HTTP/1.1, mỗi luồng một kết nối).
    /// </summary>
    private static async Task<HttpResponseMessage> SendOnceAsync(HttpClient http, DownloadRequest req, Uri uri, (long From, long To)? range, Uri? cookieOrigin, CancellationToken ct)
    {
        if (Http3.ShouldTry(uri))
        {
            var quic = Build(req, uri, range, cookieOrigin);
            quic.Version = HttpVersion.Version30;
            quic.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!Http3.Known(uri))
                limit.CancelAfter(Http3.FirstTryTimeout);
            try
            {
                var resp = await http.SendAsync(quic, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                Http3.Worked(uri);
                return resp;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
            {
                quic.Dispose();
                Http3.Failed(uri);
            }
        }
        return await http.SendAsync(Build(req, uri, range, cookieOrigin), HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static HttpRequestMessage Build(DownloadRequest req, Uri uri, (long From, long To)? range, Uri? cookieOrigin)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyHeaders(msg, req, cookieOrigin);
        if (range is var (from, to))
            msg.Headers.Range = new RangeHeaderValue(from, to);
        return msg;
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static void ApplyHeaders(HttpRequestMessage m, DownloadRequest r, Uri? cookieOrigin)
    {
        var target = m.RequestUri!;
        if (!string.IsNullOrEmpty(r.Cookies) && cookieOrigin is not null && SameOrigin(cookieOrigin, target))
            m.Headers.TryAddWithoutValidation("Cookie", r.Cookies);
        if (!string.IsNullOrEmpty(r.Referrer) && Uri.TryCreate(r.Referrer, UriKind.Absolute, out var referrer) && RefererFor(referrer, target) is { } referer)
            m.Headers.Referrer = referer;
        if (!string.IsNullOrEmpty(r.UserAgent))
            m.Headers.TryAddWithoutValidation("User-Agent", r.UserAgent);
    }

    private static Uri? RefererFor(Uri referrer, Uri target)
    {
        if (referrer.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp)
            return null;
        return SameOrigin(referrer, target) ? referrer : new Uri(referrer.GetLeftPart(UriPartial.Authority) + "/");
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}

/// <summary>Nhớ máy chủ nào tải được bằng HTTP/3 và máy chủ nào không (để khỏi thử lại liên tục).</summary>
public static class Http3
{
    private static readonly ConcurrentDictionary<string, byte> Working = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> Broken = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<bool> Supported = new(() =>
    {
        try
        {
            return System.Net.Quic.QuicConnection.IsSupported;
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>Lần đầu thử HTTP/3 với một máy chủ chỉ chờ chừng này (UDP có thể bị chặn), rồi quay về cách cũ.</summary>
    public static readonly TimeSpan FirstTryTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Máy chủ không dùng được HTTP/3 thì chừng này sau mới thử lại.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    /// <summary>Tắt hẳn HTTP/3 (ví dụ khi đặt proxy: proxy không chuyển được HTTP/3).</summary>
    public static Func<bool> Disabled { get; set; } = () => false;

    public static bool ShouldTry(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && !Disabled()
        && Supported.Value
        && !(Broken.TryGetValue(Key(uri), out var until) && until > DateTime.UtcNow);

    public static bool Known(Uri uri) => Working.ContainsKey(Key(uri));

    public static void Worked(Uri uri)
    {
        Working[Key(uri)] = 0;
        Broken.TryRemove(Key(uri), out _);
    }

    public static void Failed(Uri uri)
    {
        Working.TryRemove(Key(uri), out _);
        Broken[Key(uri)] = DateTime.UtcNow + RetryAfter;
    }

    private static string Key(Uri uri) => uri.IdnHost + ":" + uri.Port;
}
