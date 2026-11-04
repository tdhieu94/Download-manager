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

public record EngineResult(EngineOutcome Outcome, string? FinalPath, string? ErrorMessage);

public record DownloadJob(DownloadRequest Request, string TargetPath, string StatePath, int Connections, ProbeResult Probe);

public interface IDownloadEngine
{
    Task<EngineResult> RunAsync(DownloadJob job, IProgress<EngineProgress> progress, CancellationToken pauseToken);
}
