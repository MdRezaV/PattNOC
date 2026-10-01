using ServiceLib.Tests.CoreConfig;

namespace ServiceLib.Tests.Helper;

public class AppProxyResolverTests
{
    private sealed class FakeProbe(bool available) : ILocalSocksProbe
    {
        public bool Available { get; set; } = available;
        public int LastPort { get; private set; } = -1;

        public Task<bool> IsAvailableAsync(int port, CancellationToken ct = default)
        {
            LastPort = port;
            return Task.FromResult(Available);
        }
    }

    private static AppProxyResolver CreateResolver(FakeProbe probe, UpstreamProxyItem? item)
    {
        return new AppProxyResolver(probe, () => new Config { UpstreamProxyItem = item ?? new UpstreamProxyItem() });
    }

    [Test]
    public async Task GetUpstreamWebProxy_Disabled_ReturnsNull()
    {
        var resolver = CreateResolver(new FakeProbe(false), new UpstreamProxyItem { Enabled = false });
        await resolver.GetUpstreamWebProxy().Should().BeNull();
        await resolver.IsUpstreamEnabled.Should().BeFalse();
    }

    [Test]
    public async Task GetUpstreamWebProxy_EmptyServer_ReturnsNull()
    {
        var resolver = CreateResolver(new FakeProbe(false),
            new UpstreamProxyItem { Enabled = true, ProxyType = EUpstreamProxyType.Socks5, Server = "", Port = 1080 });
        await resolver.GetUpstreamWebProxy().Should().BeNull();
        await resolver.IsUpstreamEnabled.Should().BeFalse();
    }

    [Test]
    public async Task GetUpstreamWebProxy_InvalidPort_ReturnsNull()
    {
        var resolver = CreateResolver(new FakeProbe(false),
            new UpstreamProxyItem { Enabled = true, ProxyType = EUpstreamProxyType.Socks5, Server = "1.2.3.4", Port = 0 });
        await resolver.GetUpstreamWebProxy().Should().BeNull();
    }

    [Test]
    public async Task BuildUpstreamWebProxy_Socks5_BuildsUriAndCredentials()
    {
        var proxy = AppProxyResolver.BuildUpstreamWebProxy(new UpstreamProxyItem
        {
            Enabled = true,
            ProxyType = EUpstreamProxyType.Socks5,
            Server = "1.2.3.4",
            Port = 1080,
            Username = "user",
            Password = "pass",
        });

        await proxy.Should().NotBeNull();
        var webProxy = (WebProxy)proxy!;
        await webProxy.Address!.Host.Should().BeEqualTo("1.2.3.4");
        await webProxy.Address.Port.Should().BeEqualTo(1080);
        await webProxy.Address.Scheme.Should().BeEqualTo("socks5");
        var cred = (NetworkCredential)webProxy.Credentials!;
        await cred.UserName.Should().BeEqualTo("user");
        await cred.Password.Should().BeEqualTo("pass");
    }

    [Test]
    public async Task BuildUpstreamWebProxy_Http_BuildsHttpUri_NoCredentialsWhenEmpty()
    {
        var proxy = AppProxyResolver.BuildUpstreamWebProxy(new UpstreamProxyItem
        {
            Enabled = true,
            ProxyType = EUpstreamProxyType.Http,
            Server = "proxy.example.com",
            Port = 8080,
        });

        var webProxy = (WebProxy)proxy!;
        await webProxy.Address!.Scheme.Should().BeEqualTo("http");
        await webProxy.Address.Host.Should().BeEqualTo("proxy.example.com");
        await webProxy.Address.Port.Should().BeEqualTo(8080);
        await webProxy.Credentials.Should().BeNull();
    }

    [Test]
    public async Task BuildUpstreamWebProxy_Https_BuildsHttpsUri()
    {
        var proxy = AppProxyResolver.BuildUpstreamWebProxy(new UpstreamProxyItem
        {
            Enabled = true,
            ProxyType = EUpstreamProxyType.Https,
            Server = "secure.example.com",
            Port = 8443,
            Username = "u",
            Password = "p",
        });

        var webProxy = (WebProxy)proxy!;
        await webProxy.Address!.Scheme.Should().BeEqualTo("https");
        await webProxy.Address.Port.Should().BeEqualTo(8443);
    }

    [Test]
    public async Task ResolveAsync_LocalProbeWins_ReturnsLocalSocks()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray,
            EUpstreamProxyType.Socks5, "10.0.0.1", 1080, "up", "up");
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var probe = new FakeProbe(true);
        var resolver = CreateResolver(probe, config.UpstreamProxyItem);

        var proxy = await resolver.ResolveAsync(true);

        await proxy.Should().NotBeNull();
        var bypass = (LoopbackBypassProxy)proxy!;
        await bypass.IsBypassed(new Uri("http://127.0.0.1:10808")).Should().BeTrue();
        await bypass.GetProxy(new Uri("http://127.0.0.1:10808")).Should().BeNull();
        await probe.LastPort.Should().BeEqualTo(config.Inbound.First().LocalPort + (int)EInboundProtocol.socks);
    }

    [Test]
    public async Task ResolveAsync_LocalProbeFails_FallsBackToUpstream()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray,
            EUpstreamProxyType.Http, "203.0.113.5", 3128, "cu", "cp");
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var resolver = CreateResolver(new FakeProbe(false), config.UpstreamProxyItem);
        var proxy = await resolver.ResolveAsync(true);

        await proxy.Should().NotBeNull();
        var bypass = (LoopbackBypassProxy)proxy!;
        await bypass.IsBypassed(new Uri("http://127.0.0.1:9")).Should().BeTrue();
        await bypass.IsBypassed(new Uri("http://example.com/")).Should().BeFalse();
        var target = bypass.GetProxy(new Uri("http://example.com/"));
        await target.Should().NotBeNull();
        await target!.Host.Should().BeEqualTo("203.0.113.5");
        await target.Port.Should().BeEqualTo(3128);
    }

    [Test]
    public async Task ResolveAsync_UseLocalV2rayFalse_UsesUpstreamWhenEnabled()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray,
            EUpstreamProxyType.Socks5, "198.51.100.9", 1080);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var resolver = CreateResolver(new FakeProbe(true), config.UpstreamProxyItem);
        var proxy = await resolver.ResolveAsync(false);

        var bypass = (LoopbackBypassProxy)proxy!;
        var target = bypass.GetProxy(new Uri("http://example.com/"));
        await target.Should().NotBeNull();
        await target!.Scheme.Should().BeEqualTo("socks5");
        await target.Host.Should().BeEqualTo("198.51.100.9");
    }

    [Test]
    public async Task ResolveAsync_NoUpstreamAndProbeFails_ReturnsNull()
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var resolver = CreateResolver(new FakeProbe(false), null);
        await (await resolver.ResolveAsync(false)).Should().BeNull();
        await (await resolver.ResolveAsync(true)).Should().BeNull();
    }

    [Test]
    public async Task ResolveLocalCoreOnly_ProbeSucceeds_ReturnsLocalSocks()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray,
            EUpstreamProxyType.Socks5, "10.0.0.1", 1080);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var resolver = CreateResolver(new FakeProbe(true), config.UpstreamProxyItem);
        var proxy = await resolver.ResolveLocalCoreOnlyAsync();

        await proxy.Should().NotBeNull();
        var bypass = (LoopbackBypassProxy)proxy!;
        var target = bypass.GetProxy(new Uri("http://example.com/"));
        await target.Should().NotBeNull();
        await target!.Host.Should().BeEqualTo(Global.Loopback);
    }

    [Test]
    public async Task ResolveLocalCoreOnly_ProbeFails_DoesNotFallBackToUpstream()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray,
            EUpstreamProxyType.Http, "203.0.113.5", 3128);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var resolver = CreateResolver(new FakeProbe(false), config.UpstreamProxyItem);
        var proxy = await resolver.ResolveLocalCoreOnlyAsync();

        await proxy.Should().BeNull();
    }

    [Test]
    public async Task LoopbackBypassProxy_LoopbackTargets_NeverProxied()
    {
        var inner = new WebProxy("socks5://1.2.3.4:1080");
        var bypass = new LoopbackBypassProxy(inner);

        await bypass.IsBypassed(new Uri("http://127.0.0.1:8080/")).Should().BeTrue();
        await bypass.IsBypassed(new Uri("http://localhost:8080/")).Should().BeTrue();
        await bypass.IsBypassed(new Uri("http://[::1]:8080/")).Should().BeTrue();
        await bypass.IsBypassed(new Uri("http://example.com/")).Should().BeFalse();

        await bypass.GetProxy(new Uri("http://127.0.0.1:1/")).Should().BeNull();
        await bypass.GetProxy(new Uri("http://localhost:1/")).Should().BeNull();
        await bypass.GetProxy(new Uri("http://example.com/")).Should().NotBeNull();
    }
}
