namespace DoMa.Core.Engine;

public class RetryPolicy
{
    public int MaxAttemptsPerSegment { get; init; } = 5;

    /// <summary>
    /// Mã lỗi cho biết máy chủ chỉ đang bận hoặc quá tải (408, 429, 502, 503, 504): thử lại sau thường sẽ được,
    /// nên DoMa coi như lỗi mạng và tự tải tiếp sau một lúc.
    /// </summary>
    public static bool IsTemporary(int? statusCode) => statusCode is 408 or 429 or 502 or 503 or 504;

    public static string BusyMessage(int statusCode) => L.F("Máy chủ đang bận (lỗi {0}). DoMa sẽ tự thử lại sau", statusCode);

    public TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Max(0, attempt - 1))));
}
