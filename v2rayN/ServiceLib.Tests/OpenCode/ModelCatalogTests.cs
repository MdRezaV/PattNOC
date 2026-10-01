namespace ServiceLib.Tests.OpenCode;

public class ModelCatalogTests
{
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class FakeProxyProvider : IActiveProxyProvider
    {
        public Task<ActiveProxySnapshot?> TryGetSnapshotAsync(CancellationToken ct = default)
            => Task.FromResult<ActiveProxySnapshot?>(null);

        public Task<bool> IsCoreRunningAsync(CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<bool> IsProxyAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private static string TempCachePath()
        => Path.Combine(Path.GetTempPath(), $"opencode_catalog_test_{Guid.NewGuid():N}.json");

    private static ModelCatalog CreateCatalog(string? cachePath = null)
        => new(new FakeProxyProvider(), cachePath ?? TempCachePath());

    private static OpenCodeItem Settings(string defaultTarget = "opencode-free") => new()
    {
        DefaultTarget = defaultTarget,
        DefaultModel = "big-pickle",
        Targets = TargetCatalogDefaults.CreateDefaultTargets(),
    };

    [Test]
    public async Task GetModels_ReturnsFreeDefaults_WhenNoCache()
    {
        var catalog = CreateCatalog();

        var models = catalog.GetModels("opencode-free");

        await models.Should().NotBeEmpty();
        await models.Should().Contain(m => m.Id == "big-pickle");
    }

    [Test]
    public async Task GetModels_ReturnsEmpty_ForUnknownTarget()
    {
        var catalog = CreateCatalog();
        await catalog.GetModels("unknown-target").Should().BeEmpty();
    }

    [Test]
    public async Task Resolve_BareModel_UsesDefaultTarget()
    {
        var catalog = CreateCatalog();

        var model = catalog.Resolve("big-pickle", "opencode-free");

        await model.Should().NotBeNull();
        await model!.Id.Should().BeEqualTo("big-pickle");
    }

    [Test]
    public async Task Resolve_TargetModelRef_ParsesBothParts()
    {
        var catalog = CreateCatalog();

        var model = catalog.Resolve("opencode-free/space-bunny-free", "opencode-free");

        await model.Should().NotBeNull();
        await model!.Id.Should().BeEqualTo("space-bunny-free");
    }

    [Test]
    public async Task ResolveModelAndTarget_BareModel_ReturnsDefaultTarget()
    {
        var catalog = CreateCatalog();
        var settings = Settings();

        var (target, model) = catalog.ResolveTargetModel("big-pickle", settings);

        await target.Should().NotBeNull();
        await target!.Id.Should().BeEqualTo("opencode-free");
        await model!.Id.Should().BeEqualTo("big-pickle");
    }

    [Test]
    public async Task ResolveTargetModel_ExplicitTargetRef_UsesThatTarget()
    {
        var catalog = CreateCatalog();
        var settings = Settings();

        var (target, model) = catalog.ResolveTargetModel("opencode-free/mimo-v2.5-free", settings);

        await target!.Id.Should().BeEqualTo("opencode-free");
        await model!.Id.Should().BeEqualTo("mimo-v2.5-free");
    }

    [Test]
    public async Task ResolveTargetModel_UnknownModel_ReturnsNullModel()
    {
        var catalog = CreateCatalog();
        var settings = Settings();

        var (target, model) = catalog.ResolveTargetModel("does-not-exist", settings);

        await model.Should().BeNull();
        await target.Should().NotBeNull();
    }

    [Test]
    public async Task ResolveTargetModel_EmptyRef_ReturnsNulls()
    {
        var catalog = CreateCatalog();
        var settings = Settings();

        var (target, model) = catalog.ResolveTargetModel("", settings);

        await target.Should().BeNull();
        await model.Should().BeNull();
    }

    [Test]
    public async Task ResolveTargetModel_UnknownTarget_ReturnsNullTarget()
    {
        var catalog = CreateCatalog();
        var settings = Settings();

        var (target, model) = catalog.ResolveTargetModel("unknown-target/big-pickle", settings);

        await target.Should().BeNull();
        await model.Should().BeNull();
    }

    [Test]
    public async Task ResolveTargetModel_DisabledTarget_ReturnsNullTarget()
    {
        var catalog = CreateCatalog();
        var settings = Settings();
        settings.Targets[0].Enabled = false;

        var (target, model) = catalog.ResolveTargetModel("big-pickle", settings);

        await target.Should().BeNull();
        await model.Should().NotBeNull();
    }

    [Test]
    public async Task RefreshAsync_WithoutCatalogUrl_SucceedsAndKeepsDefaults()
    {
        var cachePath = TempCachePath();
        try
        {
            var catalog = CreateCatalog(cachePath);
            var target = new OpenCodeTargetItem
            {
                Id = "opencode-free",
                BaseUrl = "https://opencode.ai/zen/v1",
                CatalogUrl = null,
                KeyOptional = true,
                Enabled = true,
            };

            var ok = await catalog.RefreshAsync(target);

            await ok.Should().BeTrue();
            await catalog.GetModels("opencode-free").Should().Contain(m => m.Id == "big-pickle");
        }
        finally
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
    }

    [Test]
    public async Task RefreshAsync_WithHandlerFactory_ParsesRemoteModelList()
    {
        var cachePath = TempCachePath();
        try
        {
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"remote-model-a"},{"id":"big-pickle"}]}""",
                    Encoding.UTF8, "application/json"),
            });
            var catalog = new ModelCatalog(new FakeProxyProvider(), cachePath, () => handler);
            var target = new OpenCodeTargetItem
            {
                Id = "opencode-free",
                BaseUrl = "https://example.invalid/v1",
                CatalogUrl = "https://example.invalid/v1/models",
                KeyOptional = true,
                Enabled = true,
            };

            var ok = await catalog.RefreshAsync(target);

            await ok.Should().BeTrue();
            var models = catalog.GetModels("opencode-free");
            await models.Should().Contain(m => m.Id == "remote-model-a");
            await models.Should().Contain(m => m.Id == "big-pickle");
            // remote-model-a has no -free suffix → IsFree false; big-pickle inherits from defaults → IsFree true
            await models.Should().Contain(m => m.Id == "remote-model-a" && !m.IsFree);
            await models.Should().Contain(m => m.Id == "big-pickle" && m.IsFree);
        }
        finally
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
    }

    [Test]
    public async Task RefreshAsync_HttpFailure_KeepsBuiltInDefaults()
    {
        var cachePath = TempCachePath();
        try
        {
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"nope"}""", Encoding.UTF8, "application/json"),
            });
            var catalog = new ModelCatalog(new FakeProxyProvider(), cachePath, () => handler);
            var target = new OpenCodeTargetItem
            {
                Id = "opencode-free",
                BaseUrl = "https://example.invalid/v1",
                CatalogUrl = "https://example.invalid/v1/models",
                KeyOptional = true,
                Enabled = true,
            };

            var ok = await catalog.RefreshAsync(target);

            await ok.Should().BeFalse();
            await catalog.GetModels("opencode-free").Should().Contain(m => m.Id == "big-pickle");
        }
        finally
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
    }

    [Test]
    public async Task CacheRoundTrip_PreservesModelsAcrossInstances()
    {
        var cachePath = TempCachePath();
        try
        {
            var cache = new OpenCodeCatalogCache
            {
                UpdatedAt = DateTime.UtcNow,
                ModelsByTarget =
                {
                    ["opencode-free"] =
                    [
                        new OpenCodeModel("cached-model", "Cached", EOpenCodeApiStyle.ChatCompletions,
                            null, true, true, true, false, "remote"),
                    ],
                },
            };
            File.WriteAllText(cachePath, JsonUtils.Serialize(cache, true));

            var catalog2 = CreateCatalog(cachePath);
            var models = catalog2.GetModels("opencode-free");

            await models.Should().HaveCount(1);
            await models.Should().Contain(m => m.Id == "cached-model");
        }
        finally
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
    }
}
