namespace DoMa.Core.Models;

public record DownloadRequest(
    string Url,
    string? Referrer,
    string? SuggestedFileName,
    string? Mime,
    long? SizeHint,
    string? Cookies,
    string? UserAgent,
    MediaOptions? Media = null);
