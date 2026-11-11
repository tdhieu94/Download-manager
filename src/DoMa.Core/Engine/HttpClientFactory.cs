namespace DoMa.Core.Engine;

public static class HttpClientFactory
{
    /// <param name="proxy">Đọc địa chỉ proxy trong cài đặt (trống = theo Windows); null = luôn theo Windows.</param>
    public static HttpClient Create(Func<string?>? proxy = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 64,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        if (proxy is not null)
            handler.Proxy = ProxyConfig.Dynamic(proxy);
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
