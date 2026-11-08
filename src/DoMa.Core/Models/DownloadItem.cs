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
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
