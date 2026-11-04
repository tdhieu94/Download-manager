using System.Text.RegularExpressions;

namespace DoMa.Core.Storage;

public class ErrorLog(string dataDir)
{
    static readonly Regex UrlPattern = new(@"https?://\S+", RegexOptions.IgnoreCase);

    readonly object gate = new();

    public void Write(string context, Exception ex, string? url)
    {
        var line = $"{DateTimeOffset.Now:O} [{context}] {(url is null ? "" : RedactUrl(url) + " ")}{ex.GetType().Name}: {RedactMessage(ex.Message)}{Environment.NewLine}";
        lock (gate)
        {
            Directory.CreateDirectory(dataDir);
            File.AppendAllText(Path.Combine(dataDir, "errors.log"), line);
        }
    }

    public static string RedactUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "";
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}{uri.AbsolutePath}";
    }

    static string RedactMessage(string message) =>
        UrlPattern.Replace(message, m => RedactUrl(m.Value));
}
