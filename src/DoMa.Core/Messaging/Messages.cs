using System.Text.Json.Nodes;
using DoMa.Core.Models;

namespace DoMa.Core.Messaging;

public static class MessageTypes
{
    public const string Ping = "ping";
    public const string Capture = "bat";
    public const string CaptureMany = "bat_nhieu";
    public const string Show = "hien";
    public const string Pong = "pong";
    public const string Asking = "dang_hoi";
    public const string Accepted = "nhan";
    public const string Cancelled = "huy";
    public const string GiveBack = "tra_lai";
}

public record CaptureMessage(
    string Id,
    string Url,
    string? FinalUrl,
    string? Referrer,
    string? Filename,
    string? Mime,
    long? FileSize,
    string? Cookies,
    string? UserAgent)
{
    public static CaptureMessage From(JsonNode node) => new(
        (string?)node["id"] ?? "",
        (string?)node["url"] ?? "",
        (string?)node["finalUrl"],
        (string?)node["referrer"],
        (string?)node["filename"],
        (string?)node["mime"],
        (long?)node["fileSize"],
        (string?)node["cookies"],
        (string?)node["userAgent"]);

    public DownloadRequest ToRequest() => new(FinalUrl ?? Url, Referrer, Filename, Mime, FileSize, Cookies, UserAgent);
}

/// <summary>Nhiều link lấy từ một trang (menu "Tải tất cả link"). Cookie gửi theo từng tên miền.</summary>
public record BatchCaptureMessage(
    string Id,
    string? Referrer,
    IReadOnlyList<(string Url, string? Text)> Links,
    IReadOnlyDictionary<string, string> CookiesByHost,
    string? UserAgent)
{
    public static BatchCaptureMessage From(JsonNode node)
    {
        var links = new List<(string, string?)>();
        if (node["links"] is JsonArray array)
        {
            foreach (var entry in array)
            {
                if (entry?["url"] is JsonValue url && url.TryGetValue<string>(out var u))
                    links.Add((u, (string?)entry["text"]));
            }
        }
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node["cookies"] is JsonObject obj)
        {
            foreach (var (host, value) in obj)
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var c) && c.Length > 0)
                    cookies[host] = c;
            }
        }
        return new BatchCaptureMessage((string?)node["id"] ?? "", (string?)node["referrer"], links, cookies, (string?)node["userAgent"]);
    }
}

public static class Messages
{
    public static JsonNode Reply(string type, string id) => new JsonObject { ["type"] = type, ["id"] = id };

    public static string? TypeOf(JsonNode msg) =>
        msg is JsonObject obj && obj["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : null;
}
