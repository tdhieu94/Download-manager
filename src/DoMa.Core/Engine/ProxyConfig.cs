using System.Net;

namespace DoMa.Core.Engine;

/// <summary>
/// Proxy do người dùng đặt trong Cài đặt. Để trống = theo cài đặt proxy của Windows.
/// Nhận "máy:cổng" (coi là http), "http://…", "socks5://…", "socks4://…".
/// </summary>
public static class ProxyConfig
{
    private static readonly string[] Schemes = ["http", "https", "socks5", "socks4", "socks4a"];

    /// <summary>Địa chỉ proxy dạng chuẩn; null nếu trống hoặc không hợp lệ.</summary>
    public static Uri? Parse(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0 || value.Any(char.IsWhiteSpace))
            return null;
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;
        if (!Schemes.Contains(uri.Scheme) || uri.Host.Length == 0 || uri.AbsolutePath.Trim('/').Length > 0)
            return null;
        // "http://máy" không ghi cổng thì Uri tự lấy 80: proxy hầu như không bao giờ ở cổng đó, bắt ghi rõ.
        return HasExplicitPort(value) ? uri : null;
    }

    private static bool HasExplicitPort(string value)
    {
        var authority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
        var host = authority[(authority.LastIndexOf('@') + 1)..];
        var colon = host.LastIndexOf(':');
        return colon > host.LastIndexOf(']') && colon < host.Length - 1;
    }

    /// <summary>Hợp lệ khi để trống (theo Windows) hoặc là một địa chỉ proxy đọc được.</summary>
    public static bool IsValid(string? text) => string.IsNullOrWhiteSpace(text) || Parse(text) is not null;

    /// <summary>Dạng lưu vào cài đặt và đưa cho yt-dlp/aria2c, vd. "socks5://127.0.0.1:1080"; "" nếu không đặt.</summary>
    public static string Normalize(string? text) => Parse(text) is { } uri ? uri.GetLeftPart(UriPartial.Authority) : "";

    /// <summary>aria2c chỉ dùng được proxy HTTP.</summary>
    public static bool IsHttp(string? text) => Parse(text) is { Scheme: "http" or "https" };

    /// <summary>
    /// Proxy cho HttpClient, đọc cài đặt ở mỗi yêu cầu nên đổi proxy không cần mở lại DoMa.
    /// Không đặt proxy riêng thì dùng proxy của Windows như trước.
    /// </summary>
    public static IWebProxy Dynamic(Func<string?> setting, IWebProxy? system = null) => new DynamicProxy(setting, system ?? HttpClient.DefaultProxy);

    private sealed class DynamicProxy(Func<string?> setting, IWebProxy system) : IWebProxy
    {
        public ICredentials? Credentials
        {
            get => Parse(setting()) is { UserInfo.Length: > 0 } uri ? Login(uri) : system.Credentials;
            set { }
        }

        public Uri? GetProxy(Uri destination) =>
            Parse(setting()) is { } uri ? new UriBuilder(uri) { UserName = "", Password = "" }.Uri : system.GetProxy(destination);

        public bool IsBypassed(Uri host) =>
            Parse(setting()) is not null ? host.IsLoopback : system.IsBypassed(host);

        private static NetworkCredential Login(Uri uri)
        {
            var parts = uri.UserInfo.Split(':', 2);
            return new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
    }
}
