using System.Net;

namespace DoMa.Core.Updates;

/// <summary>Thử lại khi GitHub hoặc mạng lỗi tạm thời, và diễn giải lỗi cập nhật cho người dùng.</summary>
public static class UpdateErrors
{
    /// <summary>Lỗi có thể hết nếu thử lại: mất mạng, quá thời gian, hoặc GitHub trả lỗi 5xx/408/429.</summary>
    public static bool IsTransient(Exception e, CancellationToken ct) => e switch
    {
        HttpRequestException h => h.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)h.StatusCode >= 500,
        TaskCanceledException => !ct.IsCancellationRequested,
        IOException => true,
        _ => false,
    };

    public static async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct, int attempts = 3, Func<int, TimeSpan>? delay = null)
    {
        delay ??= attempt => TimeSpan.FromSeconds(attempt == 1 ? 3 : 10);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(ct);
            }
            catch (Exception e) when (attempt < attempts && IsTransient(e, ct))
            {
                await Task.Delay(delay(attempt), ct);
            }
        }
    }

    /// <summary>Câu báo lỗi có nêu lý do, để người dùng biết nên thử lại hay tự tải bộ cài.</summary>
    public static string Describe(Exception e)
    {
        var reason = e switch
        {
            InvalidDataException => e.Message,
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "Không tìm thấy bộ cài trên GitHub.",
            HttpRequestException { StatusCode: { } code } when (int)code >= 500 => $"Máy chủ GitHub đang lỗi ({(int)code}). Thường chỉ vài phút là hết.",
            HttpRequestException { StatusCode: { } code } => $"GitHub từ chối tải ({(int)code}).",
            HttpRequestException => "Không kết nối được tới GitHub. Hãy kiểm tra mạng.",
            TaskCanceledException => "Tải bộ cài quá lâu nên bị ngắt. Mạng có thể đang chậm.",
            UnauthorizedAccessException => "Windows không cho ghi hoặc chạy bộ cài.",
            IOException => "Mất kết nối khi đang tải bộ cài.",
            _ => e.Message,
        };
        return "Không tải được bộ cài cập nhật. " + reason;
    }
}
