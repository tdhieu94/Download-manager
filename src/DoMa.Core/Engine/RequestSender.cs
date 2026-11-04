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
            var msg = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyHeaders(msg, req, cookieOrigin);
            if (range is var (from, to))
                msg.Headers.Range = new RangeHeaderValue(from, to);
            var resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
            if (hop == MaxRedirects || !IsRedirect(resp.StatusCode) || resp.Headers.Location is not { } location)
                return resp;
            resp.Dispose();
            msg.Dispose();
            uri = new Uri(uri, location);
        }
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
