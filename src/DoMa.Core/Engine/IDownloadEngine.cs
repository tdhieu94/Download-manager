using DoMa.Core.Models;

namespace DoMa.Core.Engine;

public record EngineProgress(long Downloaded, long? Total, int ActiveConnections);

public enum EngineOutcome
{
    Completed,
    Paused,
    Failed,
    NeedsRestart,
}

/// <param name="Network">Lỗi do mất mạng / không tới được máy chủ: tự tải tiếp được khi có mạng lại.</param>
public record EngineResult(EngineOutcome Outcome, string? FinalPath, string? ErrorMessage, bool Network = false);

/// <param name="Limiter">Giới hạn tốc độ riêng của tệp này (null = không giới hạn riêng); đổi lúc đang tải cũng có hiệu lực ngay.</param>
public record DownloadJob(DownloadRequest Request, string TargetPath, string StatePath, int Connections, ProbeResult Probe, bool LinkRefreshed = false,
    BandwidthLimiter? Limiter = null);

public interface IDownloadEngine
{
    Task<EngineResult> RunAsync(DownloadJob job, IProgress<EngineProgress> progress, CancellationToken pauseToken);
}
