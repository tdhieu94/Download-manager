using System.Text.Json.Nodes;
using DoMa.Core.Engine;
using DoMa.Core.Files;
using DoMa.Core.Messaging;
using DoMa.Core.Models;
using DoMa.Core.Queue;

namespace DoMa.App.Capture;

public enum DialogChoice { DownloadNow, Later, Cancel }

public record DialogResult(DialogChoice Choice, string TargetPath);

public interface IDownloadDialog
{
    Task<DialogResult> ShowAsync(DownloadRequest req, ProbeResult probe, string proposedPath, Category category);
}

public class CaptureCoordinator(
    Func<DownloadRequest, CancellationToken, Task<ProbeResult>> probe,
    IDownloadDialog dialog,
    DownloadManager manager,
    Func<CategoryResolver> categories,
    Action<string> notify)
{
    private const string BrowserTookOverMessage = "Trình duyệt đã tự tải lại tệp này vì mất kết nối với DoMa.";

    private readonly object _gate = new();
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Hỏi xem link vừa bắt có phải link mới cho một tệp đang chờ làm mới link không (tên tệp hoặc dung lượng khớp).
    /// Trả true nếu đã nhận link để tải tiếp tệp cũ; khi đó không hiện hộp tải mới.
    /// </summary>
    public Func<DownloadRequest, ProbeResult, string, bool>? TryRefresh { get; set; }

    public async Task HandleAsync(JsonNode msg, Func<JsonNode, Task<bool>> reply, Func<bool> isConnected)
    {
        CaptureMessage capture;
        try
        {
            capture = CaptureMessage.From(msg);
        }
        catch (Exception)
        {
            var id = ReadId(msg);
            if (id is not null)
                await reply(Messages.Reply(MessageTypes.GiveBack, id));
            return;
        }

        var req = capture.ToRequest();
        ProbeResult result;
        try
        {
            result = await probe(req, CancellationToken.None);
        }
        catch (Exception)
        {
            await reply(Messages.Reply(MessageTypes.GiveBack, capture.Id));
            return;
        }
        if (!result.Ok)
        {
            await reply(Messages.Reply(MessageTypes.GiveBack, capture.Id));
            return;
        }

        var resolver = categories();
        var fileName = FileNameResolver.Sanitize(FirstNonBlank(result.FileName, req.SuggestedFileName) ?? LastUrlSegment(req.Url));
        // Luồng video HLS: tệp ghép ra là video .ts, không phải danh sách phát .m3u8.
        if (Core.Engine.Hls.HlsPlaylist.Looks(result.FinalUrl ?? req.Url, result.ContentType ?? req.Mime))
        {
            fileName = Core.Engine.Hls.HlsDownloadEngine.WithExtension(fileName.Length > 0 ? fileName : "video", ".ts");
            result = result with { Size = null };
        }
        if (TryRefresh?.Invoke(req, result, fileName) == true)
        {
            await TryReply(reply, Messages.Reply(MessageTypes.Accepted, capture.Id));
            return;
        }
        var category = resolver.Resolve(fileName);
        var proposed = Reserve(Path.Combine(resolver.FolderFor(category), fileName));
        try
        {
            await reply(Messages.Reply(MessageTypes.Asking, capture.Id));
            var choice = await dialog.ShowAsync(req, result, proposed, category);
            if (choice.Choice == DialogChoice.Cancel)
            {
                await reply(Messages.Reply(MessageTypes.Cancelled, capture.Id));
                return;
            }
            if (!isConnected())
            {
                notify(BrowserTookOverMessage);
                return;
            }
            var item = manager.Add(req, choice.TargetPath, choice.Choice == DialogChoice.DownloadNow);
            if (await TryReply(reply, Messages.Reply(MessageTypes.Accepted, capture.Id)))
                return;
            manager.Remove([item.Id], true);
            notify(BrowserTookOverMessage);
        }
        catch (Exception)
        {
            await TryReply(reply, Messages.Reply(MessageTypes.GiveBack, capture.Id));
        }
        finally
        {
            lock (_gate)
                _reserved.Remove(proposed);
        }
    }

    private static async Task<bool> TryReply(Func<JsonNode, Task<bool>> reply, JsonNode message)
    {
        try
        {
            return await reply(message);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string Reserve(string path)
    {
        lock (_gate)
        {
            var taken = manager.Items.Select(i => i.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unique = FileNameResolver.MakeUnique(path, p => File.Exists(p) || taken.Contains(p) || _reserved.Contains(p));
            _reserved.Add(unique);
            return unique;
        }
    }

    private static string? ReadId(JsonNode msg)
    {
        try
        {
            return (string?)msg["id"];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string LastUrlSegment(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "";
        return Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "").Trim('/');
    }
}
