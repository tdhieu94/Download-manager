namespace DoMa.Core.Models;

public class DownloadItem
{
    public Guid Id { get; set; }
    public required DownloadRequest Request { get; set; }
    public string TargetPath { get; set; } = "";
    public Category Category { get; set; }
    public DownloadStatus Status { get; set; }
    public long? TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public bool CanResume { get; set; } = true;
    public string? ErrorMessage { get; set; }

    /// <summary>Mô tả mối nguy khi trình diệt virus phát hiện mã độc trong tệp; null = chưa phát hiện gì.</summary>
    public string? Threat { get; set; }

    /// <summary>Vừa được thay link mới (link cũ hết hạn): lần tải tiếp chỉ cần dung lượng khớp là nối tiếp được.</summary>
    public bool LinkRefreshed { get; set; }
    /// <summary>Lỗi lần trước do mất mạng: DoMa tự tải tiếp khi có mạng lại.</summary>
    public bool NetworkError { get; set; }

    /// <summary>Số lần đã tự tải tiếp liên tiếp sau lỗi mạng và thời điểm được thử lần tới (không lưu).</summary>
    public int AutoRetries { get; set; }
    public DateTimeOffset? NextAutoRetry { get; set; }

    /// <summary>Tốc độ tối đa riêng cho tệp này, KB/s (0 = chỉ theo giới hạn chung).</summary>
    public int SpeedLimitKBps { get; set; }

    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
