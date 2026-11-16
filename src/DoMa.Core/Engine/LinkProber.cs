using System.Net;
using DoMa.Core.Files;
using DoMa.Core.Models;

namespace DoMa.Core.Engine;

public class LinkProber(HttpClient http)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    public async Task<ProbeResult> ProbeAsync(DownloadRequest req, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var resp = await RequestSender.SendAsync(http, req, req.Url, (0, 0), timeout.Token);
            return Evaluate(req, resp);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail(ProbeFailure.Network, null);
        }
        catch (HttpRequestException)
        {
            return Fail(ProbeFailure.Network, null);
        }
    }

    /// <summary>Giải thích cho người dùng vì sao một link dán tay không tải được, kèm cách xử lý.</summary>
    public static string Explain(ProbeResult probe) => probe.Failure switch
    {
        ProbeFailure.HtmlInsteadOfFile => L.T("Link này mở ra một trang web chứ không phải một tệp.\n\nHãy bấm chuột phải vào nút hoặc link tải trên trang › “Sao chép địa chỉ liên kết”, rồi dán vào DoMa. Hoặc bấm tải ngay trên trình duyệt để DoMa tự bắt."),
        ProbeFailure.NotFound => L.T("Không tìm thấy tệp ở link này (lỗi 404).\n\nLink có thể sai hoặc tệp đã bị xoá. Nếu tệp nằm ở nơi cần đăng nhập (ví dụ kho GitHub riêng tư), hãy bấm tải trên trình duyệt đã đăng nhập để DoMa bắt."),
        ProbeFailure.Expired => L.T("Máy chủ từ chối link này: cần đăng nhập hoặc link đã hết hạn.\n\nHãy bấm tải trên trình duyệt đã đăng nhập để DoMa bắt link kèm thông tin đăng nhập."),
        ProbeFailure.Network => L.T("Không kết nối được tới máy chủ.\n\nHãy kiểm tra kết nối mạng (và proxy trong Cài đặt nếu bạn có đặt) rồi thử lại."),
        _ => probe.StatusCode is int code ? L.F("Máy chủ báo lỗi {0} nên không tải được link này.", code) : L.T("Không tải được link này."),
    };

    private static ProbeResult Evaluate(DownloadRequest req, HttpResponseMessage resp)
    {
        var status = (int)resp.StatusCode;
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
            return Fail(ProbeFailure.Expired, status);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return Fail(ProbeFailure.NotFound, status);

        var supportsRange = resp.StatusCode == HttpStatusCode.PartialContent;
        if (!supportsRange && resp.StatusCode != HttpStatusCode.OK)
            return Fail(ProbeFailure.Other, status);

        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? req.Url;
        var size = supportsRange ? resp.Content.Headers.ContentRange?.Length : resp.Content.Headers.ContentLength;
        var contentType = resp.Content.Headers.ContentType?.MediaType;
        var fileName = ResolveFileName(req, resp, finalUrl);

        if (string.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase)
            && fileName is not null
            && !IsHtmlName(fileName))
            return new ProbeResult(false, finalUrl, size, supportsRange, null, null, fileName, contentType, ProbeFailure.HtmlInsteadOfFile, status);

        return new ProbeResult(
            true,
            finalUrl,
            size,
            supportsRange,
            resp.Headers.ETag?.Tag,
            resp.Content.Headers.LastModified?.ToString("R"),
            fileName,
            contentType,
            ProbeFailure.None,
            status);
    }

    private static string? ResolveFileName(DownloadRequest req, HttpResponseMessage resp, string finalUrl)
    {
        var disposition = resp.Content.Headers.ContentDisposition;
        // filename* (RFC 5987) đã có mã hoá rõ ràng; tên do trình duyệt đề xuất đã được trình duyệt giải mã đúng;
        // filename thường hay bị máy chủ gửi UTF-8 thô nên để sau và sửa mã hoá trong Sanitize.
        var name = disposition?.FileNameStar;
        if (string.IsNullOrWhiteSpace(name))
            name = req.SuggestedFileName;
        if (string.IsNullOrWhiteSpace(name))
            name = disposition?.FileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(name) && Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri))
            name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        return string.IsNullOrWhiteSpace(name) ? null : FileNameResolver.Sanitize(name);
    }

    private static bool IsHtmlName(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.Equals(".htm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".html", StringComparison.OrdinalIgnoreCase);
    }

    private static ProbeResult Fail(ProbeFailure failure, int? status) =>
        new(false, null, null, false, null, null, null, null, failure, status);
}
