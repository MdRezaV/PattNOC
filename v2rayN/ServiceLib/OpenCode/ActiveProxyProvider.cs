namespace ServiceLib.OpenCode;

public sealed record ActiveProxySnapshot(
    IWebProxy WebProxy,
    int SocksPort,
    string ProfileIndexId,
    string? ProfileRemark);

public interface IActiveProxyProvider
{
    Task<ActiveProxySnapshot?> TryGetSnapshotAsync(CancellationToken ct = default);
    Task<bool> IsCoreRunningAsync(CancellationToken ct = default);
    Task<bool> IsProxyAvailableAsync(CancellationToken ct = default);
}

public interface IProxyStateSource
{
    string? GetIndexId();
    Task<ProfileItem?> GetActiveProfileAsync(CancellationToken ct = default);
    int GetSocksPort();
    Task<bool> ProbeSocksPortAsync(int port, CancellationToken ct = default);
}

public sealed class AppProxyStateSource : IProxyStateSource
{
    public string? GetIndexId() => AppManager.Instance.Config?.IndexId;

    public Task<ProfileItem?> GetActiveProfileAsync(CancellationToken ct = default)
        => ConfigHandler.GetDefaultServer(AppManager.Instance.Config);

    public int GetSocksPort() => AppManager.Instance.GetLocalPort(EInboundProtocol.socks);

    public async Task<bool> ProbeSocksPortAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(1));
            await tcp.ConnectAsync(Global.Loopback, port, cts.Token);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class ActiveProxyProvider : IActiveProxyProvider
{
    private readonly IProxyStateSource _source;

    public ActiveProxyProvider()
        : this(new AppProxyStateSource())
    {
    }

    public ActiveProxyProvider(IProxyStateSource source)
    {
        _source = source;
    }

    public async Task<ActiveProxySnapshot?> TryGetSnapshotAsync(CancellationToken ct = default)
    {
        var profile = await _source.GetActiveProfileAsync(ct);
        if (profile is null || profile.IndexId.IsNullOrEmpty())
        {
            return null;
        }

        var port = _source.GetSocksPort();
        if (!await _source.ProbeSocksPortAsync(port, ct))
        {
            return null;
        }

        var proxy = new WebProxy($"{Global.Socks5Protocol}{Global.Loopback}:{port}");
        return new ActiveProxySnapshot(proxy, port, profile.IndexId, profile.Remarks);
    }

    public async Task<bool> IsCoreRunningAsync(CancellationToken ct = default)
    {
        return await _source.ProbeSocksPortAsync(_source.GetSocksPort(), ct);
    }

    public async Task<bool> IsProxyAvailableAsync(CancellationToken ct = default)
    {
        return await TryGetSnapshotAsync(ct) is not null;
    }
}
