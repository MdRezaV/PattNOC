namespace ServiceLib.Tests.CoreConfig.Singbox;

public class CoreConfigGlobalProxyTests
{
    private static async Task<SingboxConfig> Generate(Config config, ProfileItem node)
    {
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        await result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString());
        await cfg.Should().NotBeNull();
        return cfg!;
    }

    [Test]
    public async Task Disabled_NoAppUpstream_NoDetour()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.sing_box, enabled: false);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box);

        var cfg = await Generate(config, node);

        await cfg.outbounds.Any(o => o.tag == Global.UpstreamProxyTag).Should().BeFalse();
        var proxyOutbound = cfg.outbounds.First(o => o.tag == Global.ProxyTag);
        var detour = proxyOutbound.detour;
        await detour.Should().BeNull();
    }

    [Test]
    public async Task EnabledSocks5_AppliesDetour_AndInjectsUpstreamOutbound()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.sing_box,
            EUpstreamProxyType.Socks5, "192.0.2.10", 1080, "u", "p");
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box);

        var cfg = await Generate(config, node);

        var upstream = cfg.outbounds.First(o => o.tag == Global.UpstreamProxyTag);
        await upstream.type.Should().BeEqualTo("socks");
        await upstream.server.Should().BeEqualTo("192.0.2.10");
        await upstream.server_port.Should().BeEqualTo(1080);
        await upstream.version.Should().BeEqualTo("5");
        await upstream.username.Should().BeEqualTo("u");
        await upstream.password.Should().BeEqualTo("p");
        var tls = upstream.tls;
        await tls.Should().BeNull();

        var proxyOutbound = cfg.outbounds.First(o => o.tag == Global.ProxyTag);
        await proxyOutbound.detour.Should().BeEqualTo(Global.UpstreamProxyTag);
    }

    [Test]
    public async Task EnabledHttp_UseHttpType_NoVersion()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.sing_box,
            EUpstreamProxyType.Http, "192.0.2.20", 8080);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box);

        var cfg = await Generate(config, node);

        var upstream = cfg.outbounds.First(o => o.tag == Global.UpstreamProxyTag);
        await upstream.type.Should().BeEqualTo("http");
        var version = upstream.version;
        await version.Should().BeNull();
        var username = upstream.username;
        await username.Should().BeNull();
        var password = upstream.password;
        await password.Should().BeNull();
        var tls = upstream.tls;
        await tls.Should().BeNull();
    }

    [Test]
    public async Task EnabledHttps_SetsTlsEnabled()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.sing_box,
            EUpstreamProxyType.Https, "192.0.2.30", 443, "u", "p");
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box);

        var cfg = await Generate(config, node);

        var upstream = cfg.outbounds.First(o => o.tag == Global.UpstreamProxyTag);
        await upstream.type.Should().BeEqualTo("http");
        await upstream.tls.Should().NotBeNull();
        await upstream.tls!.enabled.Should().BeTrue();
    }

    [Test]
    public async Task PrivateAddressServer_NotDetoured()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.sing_box,
            EUpstreamProxyType.Socks5, "192.0.2.10", 1080);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box);
        node.Address = "192.168.1.50";
        node.Port = 443;

        var cfg = await Generate(config, node);

        await cfg.outbounds.Any(o => o.tag == Global.UpstreamProxyTag).Should().BeTrue();
        var proxyOutbound = cfg.outbounds.First(o => o.tag == Global.ProxyTag);
        var detour = proxyOutbound.detour;
        await detour.Should().BeNull();
    }

    [Test]
    public async Task DirectOutbound_NotDetoured()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.sing_box,
            EUpstreamProxyType.Socks5, "192.0.2.10", 1080);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box);

        var cfg = await Generate(config, node);

        foreach (var direct in cfg.outbounds.Where(o =>
                     o.type is "direct" or "block" or "dns" or "selector" or "urltest"))
        {
            var detour = direct.detour;
            await detour.Should().BeNull();
        }
    }

    [Test]
    public async Task SpeedtestConfig_AlsoDetoured()
    {
        var config = CoreConfigTestFactory.CreateConfigWithGlobalProxy(ECoreType.sing_box,
            EUpstreamProxyType.Socks5, "192.0.2.10", 1080, "u", "p");
        CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);

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
                CoreType = ECoreType.sing_box,
            }
        };

        var result = new CoreConfigSingboxService(context).GenerateClientSpeedtestConfig(selecteds);

        await result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;

        await cfg.outbounds.Any(o => o.tag == Global.UpstreamProxyTag).Should().BeTrue();
        var testOutbound = cfg.outbounds.First(o => o.tag.StartsWith(Global.ProxyTag)
                                                    && o.tag != Global.UpstreamProxyTag);
        await testOutbound.detour.Should().BeEqualTo(Global.UpstreamProxyTag);
    }
}
