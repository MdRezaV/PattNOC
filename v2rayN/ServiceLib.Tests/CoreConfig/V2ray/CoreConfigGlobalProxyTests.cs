namespace ServiceLib.Tests.CoreConfig.V2ray;

public class CoreConfigGlobalProxyTests
{
    private static async Task<V2rayConfig> Generate(Config config, ProfileItem node, FullConfigTemplateItem? template = null)
    {
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray, fullConfigTemplate: template);
        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        await result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString());
        await cfg.Should().NotBeNull();
        return cfg!;
    }

    [Test]
    public async Task Disabled_NoAppUpstream_NoDialerProxy()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray, enabled: false);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);

        var cfg = await Generate(config, node);

        await cfg.outbounds.Any(o => o.tag == Global.UpstreamProxyTag).Should().BeFalse();
        var proxyOutbound = cfg.outbounds.First(o => o.tag == Global.ProxyTag);
        var detour = proxyOutbound.streamSettings?.sockopt?.dialerProxy;
        await detour.Should().BeNull();
    }

    [Test]
    public async Task EnabledSocks5_AppliesDialerProxy_AndInjectsUpstreamOutbound()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray,
            EUpstreamProxyType.Socks5, "192.0.2.10", 1080, "u", "p");
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);

        var cfg = await Generate(config, node);

        var upstream = cfg.outbounds.First(o => o.tag == Global.UpstreamProxyTag);
        await upstream.protocol.Should().BeEqualTo("socks");
        await upstream.settings.address!.ToString().Should().BeEqualTo("192.0.2.10");
        await upstream.settings.port.Should().BeEqualTo(1080);
        await upstream.settings.user.Should().BeEqualTo("u");
        await upstream.settings.pass.Should().BeEqualTo("p");

        var proxyOutbound = cfg.outbounds.First(o => o.tag == Global.ProxyTag);
        await proxyOutbound.streamSettings!.sockopt!.dialerProxy.Should().BeEqualTo(Global.UpstreamProxyTag);
    }

    [Test]
    public async Task EnabledHttp_UseHttpProtocol()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray,
            EUpstreamProxyType.Http, "192.0.2.20", 8080);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);

        var cfg = await Generate(config, node);

        var upstream = cfg.outbounds.First(o => o.tag == Global.UpstreamProxyTag);
        await upstream.protocol.Should().BeEqualTo("http");
        await upstream.settings.user.Should().BeNull();
        await upstream.settings.pass.Should().BeNull();
    }

    [Test]
    public async Task EnabledHttps_FallsBackToHttpProtocol_ForXray()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray,
            EUpstreamProxyType.Https, "192.0.2.30", 443, "u", "p");
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);

        var cfg = await Generate(config, node);

        var upstream = cfg.outbounds.First(o => o.tag == Global.UpstreamProxyTag);
        await upstream.protocol.Should().BeEqualTo("http");
        await upstream.settings.address!.ToString().Should().BeEqualTo("192.0.2.30");
    }

    [Test]
    public async Task PrivateAddressServer_NotDetoured()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray,
            EUpstreamProxyType.Socks5, "192.0.2.10", 1080);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        node.Address = "10.1.2.3";
        node.Port = 443;

        var cfg = await Generate(config, node);

        await cfg.outbounds.Any(o => o.tag == Global.UpstreamProxyTag).Should().BeTrue();
        var proxyOutbound = cfg.outbounds.First(o => o.tag == Global.ProxyTag);
        var detour = proxyOutbound.streamSettings?.sockopt?.dialerProxy;
        await detour.Should().BeNull();
    }

    [Test]
    public async Task LoopbackAddressServer_NotDetoured()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray,
            EUpstreamProxyType.Socks5, "192.0.2.10", 1080);
        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.Xray);

        var cfg = await Generate(config, node);

        var proxyOutbound = cfg.outbounds.First(o => o.tag == Global.ProxyTag);
        var detour = proxyOutbound.streamSettings?.sockopt?.dialerProxy;
        await detour.Should().BeNull();
    }

    [Test]
    public async Task FreedomOutbound_NotDetoured()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray,
            EUpstreamProxyType.Socks5, "192.0.2.10", 1080);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);

        var cfg = await Generate(config, node);

        foreach (var freedom in cfg.outbounds.Where(o => o.protocol is "freedom" or "blackhole" or "dns"))
        {
            var detour = freedom.streamSettings?.sockopt?.dialerProxy;
            await detour.Should().BeNull();
        }
    }

    [Test]
    public async Task PreExistingDialerProxy_Preserved()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray,
            EUpstreamProxyType.Socks5, "192.0.2.10", 1080);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);

        var template = new FullConfigTemplateItem
        {
            Enabled = true,
            CoreType = ECoreType.Xray,
            Config = "{}",
            ProxyDetour = "user-chain-outbound",
        };

        var cfg = await Generate(config, node, template);

        var proxyOutbound = cfg.outbounds.First(o => o.tag == Global.ProxyTag);
        await proxyOutbound.streamSettings!.sockopt!.dialerProxy.Should().BeEqualTo("user-chain-outbound");
    }

    [Test]
    public async Task SpeedtestConfig_AlsoDetoured()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray,
            EUpstreamProxyType.Socks5, "192.0.2.10", 1080, "u", "p");
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray);

        var selecteds = new List<ServerTestItem>
        {
            new()
            {
                IndexId = node.IndexId,
                Address = node.Address,
                Port = node.Port,
                ConfigType = node.ConfigType,
                AllowTest = true,
                Profile = node,
                CoreType = ECoreType.Xray,
            }
        };

        var result = new CoreConfigV2rayService(context).GenerateClientSpeedtestConfig(selecteds);

        await result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;

        await cfg.outbounds.Any(o => o.tag == Global.UpstreamProxyTag).Should().BeTrue();
        var testOutbound = cfg.outbounds.First(o => o.tag.StartsWith(Global.ProxyTag)
                                                    && o.tag != Global.UpstreamProxyTag
                                                    && o.protocol == "vmess");
        await testOutbound.streamSettings!.sockopt!.dialerProxy.Should().BeEqualTo(Global.UpstreamProxyTag);
    }

    [Test]
    public async Task ProxyChain_ChainChildrenKeepValidDialerProxies()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.Xray,
            EUpstreamProxyType.Socks5, "192.0.2.10", 1080);
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var hop1 = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "hop-1");
        hop1.Address = "hop1.example.com";
        var hop2 = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray, "hop-2");
        hop2.Address = "hop2.example.com";
        var chain = CoreConfigTestFactory.CreateProxyChainNode(ECoreType.Xray, "chain-1", "chain",
            [hop1.IndexId, hop2.IndexId]);

        var context = CoreConfigTestFactory.CreateContext(config, chain, ECoreType.Xray);
        context.AllProxiesMap[hop1.IndexId] = hop1;
        context.AllProxiesMap[hop2.IndexId] = hop2;

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        await result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;

        await cfg.outbounds.Any(o => o.tag == Global.UpstreamProxyTag).Should().BeTrue();

        var chainOutbounds = cfg.outbounds
            .Where(o => o.tag.StartsWith("chain-proxy-", StringComparison.Ordinal))
            .ToList();
        await chainOutbounds.Count.Should().BeGreaterThan(0);
        foreach (var o in chainOutbounds)
        {
            var detour = o.streamSettings?.sockopt?.dialerProxy;
            if (!detour.IsNullOrEmpty())
            {
                var ok = detour == Global.UpstreamProxyTag
                         || detour!.StartsWith("chain-proxy-", StringComparison.Ordinal);
                await ok.Should().BeTrue();
            }
        }
    }
}
