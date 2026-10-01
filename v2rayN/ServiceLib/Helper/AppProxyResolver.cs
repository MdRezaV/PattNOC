namespace ServiceLib.Helper;

public interface ILocalSocksProbe
{
    Task<bool> IsAvailableAsync(int port, CancellationToken ct = default);
}

public interface IAppProxyResolver
{
    /// <summary>
    /// Returns the proxy for one app HTTP request.
    /// useLocalV2ray=true  → prefer local core SOCKS (the core dials via the upstream in its config);
    ///                      falls back to upstream if the core is not running and upstream is enabled.
    /// useLocalV2ray=false → upstream if enabled, else null (direct).
    /// Loopback destinations are never proxied (enforced by the returned wrapper).
    /// </summary>
    Task<IWebProxy?> ResolveAsync(bool useLocalV2ray, CancellationToken ct = default);

    /// <summary>Raw upstream WebProxy from config, or null when disabled/invalid.</summary>
    IWebProxy? GetUpstreamWebProxy();

    bool IsUpstreamEnabled { get; }
}

public sealed class AppProxyResolver : IAppProxyResolver
{
    private readonly ILocalSocksProbe _probe;
    private readonly Func<Config> _configAccessor;

    public AppProxyResolver(ILocalSocksProbe probe, Func<Config> configAccessor)
    {
        _probe = probe;
        _configAccessor = configAccessor;
    }

    public static AppProxyResolver Instance { get; } = new(
        new DefaultLocalSocksProbe(),
        () => AppManager.Instance.Config);

    public bool IsUpstreamEnabled => _configAccessor().UpstreamProxyItem?.IsUsable() == true;

    public IWebProxy? GetUpstreamWebProxy()
    {
        var item = _configAccessor().UpstreamProxyItem;
        if (item is null || !item.IsUsable())
        {
            return null;
        }
        return BuildUpstreamWebProxy(item);
    }

    public async Task<IWebProxy?> ResolveAsync(bool useLocalV2ray, CancellationToken ct = default)
    {
        var config = _configAccessor();
        var item = config.UpstreamProxyItem;

        if (useLocalV2ray)
        {
            var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
            if (await _probe.IsAvailableAsync(port, ct))
            {
                return new LoopbackBypassProxy(new WebProxy($"{Global.Socks5Protocol}{Global.Loopback}:{port}"));
            }
            // Core not running: fall through to upstream if enabled
        }

        if (item is null || !item.IsUsable())
        {
            return null;
        }

        return new LoopbackBypassProxy(BuildUpstreamWebProxy(item)!);
    }

    public static IWebProxy? BuildUpstreamWebProxy(UpstreamProxyItem item)
    {
        var uri = item.ProxyType switch
        {
            EUpstreamProxyType.Socks5 => $"{Global.Socks5Protocol}{item.Server}:{item.Port}",
            EUpstreamProxyType.Http => $"{Global.HttpProtocol}{item.Server}:{item.Port}",
            EUpstreamProxyType.Https => $"{Global.HttpsProtocol}{item.Server}:{item.Port}",
            _ => null
        };
        if (uri.IsNullOrEmpty())
        {
            return null;
        }
        var proxy = new WebProxy(uri);
        if (!item.Username.IsNullOrEmpty())
        {
            proxy.Credentials = new NetworkCredential(item.Username, item.Password ?? string.Empty);
        }
        return proxy;
    }

    private sealed class DefaultLocalSocksProbe : ILocalSocksProbe
    {
        public Task<bool> IsAvailableAsync(int port, CancellationToken ct = default)
            => AppLocalSocksProbe.IsAvailableAsync(port, ct);
    }
}
