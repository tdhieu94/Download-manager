namespace DoMa.Core.Engine;

public enum ProbeFailure
{
    None,
    Expired,
    NotFound,
    HtmlInsteadOfFile,
    Network,
    Other,
}

public record ProbeResult(
    bool Ok,
    string? FinalUrl,
    long? Size,
    bool SupportsRange,
    string? ETag,
    string? LastModified,
    string? FileName,
    string? ContentType,
    ProbeFailure Failure,
    int? StatusCode,
    YouTube.YouTubeInfo? Media = null);
