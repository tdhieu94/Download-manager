namespace DoMa.Core.Engine;

public static class HttpClientFactory
{
    public static HttpClient Create()
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
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
