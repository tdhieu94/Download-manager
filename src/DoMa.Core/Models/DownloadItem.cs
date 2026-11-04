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
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
