namespace ServiceLib.OpenCode;

public static class ProxyHttpClientFactory
{
    /// <summary>
    /// Builds a request-scoped proxy-aware client. Timeout is left infinite by default;
    /// callers enforce deadlines via CancellationToken (required for streaming responses).
    /// </summary>
    public static HttpClient Create(IWebProxy? proxy, int connectTimeoutSeconds)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = proxy != null,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, connectTimeoutSeconds)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.All,
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }
}
