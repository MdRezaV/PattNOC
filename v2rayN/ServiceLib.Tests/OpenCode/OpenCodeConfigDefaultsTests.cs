namespace ServiceLib.Tests.OpenCode;

public class OpenCodeConfigDefaultsTests
{
    [Test]
    public async Task Create_SeedsOpenCodeFreeDefaults()
    {
        var item = OpenCodeConfigDefaults.Create();

        await item.Enabled.Should().BeFalse();
        await item.GatewayEnabled.Should().BeTrue();
        await item.DefaultTarget.Should().BeEqualTo(TargetCatalogDefaults.OpenCodeFreeTargetId);
        await item.DefaultModel.Should().BeEqualTo("big-pickle");
        await item.GatewayHost.Should().BeEqualTo(Global.Loopback);
        await item.GatewayPort.Should().BeEqualTo(OpenCodeConfigDefaults.DefaultGatewayPort);
        await item.ConnectTimeoutSeconds.Should().BeEqualTo(10);
        await item.RequestTimeoutSeconds.Should().BeEqualTo(120);
        await item.MaxRetry.Should().BeEqualTo(1);
        await item.MaxConcurrentRequests.Should().BeEqualTo(8);
        await item.Targets.Should().HaveCount(1);
        await item.Targets[0].Id.Should().BeEqualTo("opencode-free");
        await item.Targets[0].KeyOptional.Should().BeTrue();
        await item.Targets[0].ApiKey.Should().BeNull();
    }

    [Test]
    public async Task CreateFreeModels_ContainsChatCompletionsFreeModels()
    {
        var models = TargetCatalogDefaults.CreateFreeModels();

        await models.Should().NotBeEmpty();
        await models.Should().Contain(m => m.Id == "big-pickle" && m.ApiStyle == EOpenCodeApiStyle.ChatCompletions);
        await models.Should().Contain(m => m.Id == "mimo-v2.5-free");
        await models.Should().Contain(m => m.Id == "space-bunny-free");
        await models.Should().Contain(m => m.ApiStyle == EOpenCodeApiStyle.Responses);
        await models.All(m => m.Source == "default").Should().BeTrue();
        await models.All(m => m.IsFree).Should().BeTrue();
    }

    [Test]
    public async Task Normalize_ClampsOutOfRangeValues()
    {
        var item = new OpenCodeItem
        {
            GatewayPort = 99999,
            ConnectTimeoutSeconds = 0,
            RequestTimeoutSeconds = 1,
            MaxRetry = 10,
            MaxConcurrentRequests = 0,
            DefaultModel = "",
            GatewayHost = "",
            Targets = [],
        };

        OpenCodeConfigDefaults.Normalize(item);

        await item.GatewayPort.Should().BeEqualTo(OpenCodeConfigDefaults.DefaultGatewayPort);
        await item.ConnectTimeoutSeconds.Should().BeEqualTo(10);
        await item.RequestTimeoutSeconds.Should().BeEqualTo(120);
        await item.MaxRetry.Should().BeEqualTo(3);
        await item.MaxConcurrentRequests.Should().BeEqualTo(8);
        await item.DefaultModel.Should().BeEqualTo("big-pickle");
        await item.GatewayHost.Should().BeEqualTo(Global.Loopback);
        await item.Targets.Should().NotBeEmpty();
    }

    [Test]
    public async Task Normalize_ResetsDefaultTargetWhenMissing()
    {
        var item = new OpenCodeItem
        {
            DefaultTarget = "does-not-exist",
            DefaultModel = "big-pickle",
            Targets = TargetCatalogDefaults.CreateDefaultTargets(),
        };

        OpenCodeConfigDefaults.Normalize(item);

        await item.DefaultTarget.Should().BeEqualTo(TargetCatalogDefaults.OpenCodeFreeTargetId);
    }

    [Test]
    public async Task Normalize_ForcesKeyOptionalAndEnabledOnTargets()
    {
        var item = new OpenCodeItem
        {
            DefaultTarget = "opencode-free",
            DefaultModel = "big-pickle",
            Targets =
            [
                new OpenCodeTargetItem
                {
                    Id = "opencode-free",
                    Enabled = false,
                    KeyOptional = false,
                    ApiKey = "sk-secret",
                },
            ],
        };

        OpenCodeConfigDefaults.Normalize(item);

        await item.Targets[0].Enabled.Should().BeTrue();
        await item.Targets[0].KeyOptional.Should().BeTrue();
        await item.Targets[0].ApiKey.Should().BeEqualTo("sk-secret");
    }

    [Test]
    public async Task OpenCodeUrl_Combine_NormalizesSlashes()
    {
        await OpenCodeUrl.Combine("https://opencode.ai/zen/v1", "/chat/completions")
            .Should().BeEqualTo("https://opencode.ai/zen/v1/chat/completions");
        await OpenCodeUrl.Combine("https://opencode.ai/zen/v1/", "chat/completions")
            .Should().BeEqualTo("https://opencode.ai/zen/v1/chat/completions");
        await OpenCodeUrl.Combine("https://opencode.ai/zen/v1", "responses")
            .Should().BeEqualTo("https://opencode.ai/zen/v1/responses");
        await OpenCodeUrl.Combine(null, "/models")
            .Should().BeEqualTo("/models");
    }
}
