using DoMa.Core.Models;

namespace DoMa.Core.Storage;

public class DownloadStore(string dataDir)
{
    record StoredRequest(string Url, string? Referrer, string? SuggestedFileName, string? Mime, long? SizeHint, string? ProtectedCookies, string? UserAgent, MediaOptions? Media = null);

    record StoredItem(
        Guid Id,
        StoredRequest Request,
        string TargetPath,
        Category Category,
        DownloadStatus Status,
        long? TotalBytes,
        long DownloadedBytes,
        bool CanResume,
        string? ErrorMessage,
        DateTimeOffset AddedAt,
        DateTimeOffset? CompletedAt,
        string? Threat = null,
        bool NetworkError = false);

    string FilePath => Path.Combine(dataDir, "downloads.json");

    public List<DownloadItem> Load()
    {
        var stored = JsonFile.Read<List<StoredItem>>(FilePath);
        if (stored is null)
            return [];
        return stored.Select(s => new DownloadItem
        {
            Id = s.Id,
            Request = new DownloadRequest(
                s.Request.Url,
                s.Request.Referrer,
                s.Request.SuggestedFileName,
                s.Request.Mime,
                s.Request.SizeHint,
                TryUnprotect(s.Request.ProtectedCookies),
                s.Request.UserAgent,
                s.Request.Media),
            TargetPath = s.TargetPath,
            Category = s.Category,
            Status = s.Status == DownloadStatus.Downloading ? DownloadStatus.Paused : s.Status,
            TotalBytes = s.TotalBytes,
            DownloadedBytes = s.DownloadedBytes,
            CanResume = s.CanResume,
            ErrorMessage = s.ErrorMessage,
            AddedAt = s.AddedAt,
            CompletedAt = s.CompletedAt,
            Threat = s.Threat,
            NetworkError = s.NetworkError,
        }).ToList();
    }

    static string? TryUnprotect(string? blob)
    {
        if (blob is null)
            return null;
        try
        {
            return CookieProtector.Unprotect(blob);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public void Save(IEnumerable<DownloadItem> items)
    {
        var stored = items.Select(i => new StoredItem(
            i.Id,
            new StoredRequest(
                i.Request.Url,
                i.Request.Referrer,
                i.Request.SuggestedFileName,
                i.Request.Mime,
                i.Request.SizeHint,
                string.IsNullOrEmpty(i.Request.Cookies) ? null : CookieProtector.Protect(i.Request.Cookies),
                i.Request.UserAgent,
                i.Request.Media),
            i.TargetPath,
            i.Category,
            i.Status,
            i.TotalBytes,
            i.DownloadedBytes,
            i.CanResume,
            i.ErrorMessage,
            i.AddedAt,
            i.CompletedAt,
            i.Threat,
            i.NetworkError)).ToList();
        JsonFile.Write(FilePath, stored);
    }
}
