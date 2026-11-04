using System.Text.Json.Nodes;
using DoMa.Core.Models;

namespace DoMa.Core.Messaging;

public static class MessageTypes
{
    public const string Ping = "ping";
    public const string Capture = "bat";
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

public static class Messages
{
    public static JsonNode Reply(string type, string id) => new JsonObject { ["type"] = type, ["id"] = id };

    public static string? TypeOf(JsonNode msg) =>
        msg is JsonObject obj && obj["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : null;
}
