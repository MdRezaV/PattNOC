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
        DefaultModel = "",
        Targets = TargetCatalogDefaults.CreateDefaultTargets(),
    };

    [Test]
    public async Task GetModels_ReturnsEmpty_WhenNoCache()
    {
        var catalog = CreateCatalog();

        var models = catalog.GetModels("opencode-free");

        await models.Should().BeEmpty();
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
        var cachePath = TempCachePath();
        var cache = new OpenCodeCatalogCache
        {
            UpdatedAt = DateTime.UtcNow,
            ModelsByTarget =
            {
                ["opencode-free"] =
                [
                    new OpenCodeModel("test-model", "Test Model", EOpenCodeApiStyle.ChatCompletions,
                        null, true, true, true, false, "remote"),
                ],
            },
        };
        File.WriteAllText(cachePath, JsonUtils.Serialize(cache, true));
        var catalog = CreateCatalog(cachePath);

        var model = catalog.Resolve("test-model", "opencode-free");

        await model.Should().NotBeNull();
        await model!.Id.Should().BeEqualTo("test-model");
        try { File.Delete(cachePath); } catch { }
    }

    [Test]
    public async Task Resolve_TargetModelRef_ParsesBothParts()
    {
        var cachePath = TempCachePath();
        var cache = new OpenCodeCatalogCache
        {
            UpdatedAt = DateTime.UtcNow,
            ModelsByTarget =
            {
                ["opencode-free"] =
                [
                    new OpenCodeModel("model-a", "Model A", EOpenCodeApiStyle.ChatCompletions,
                        null, true, true, true, false, "remote"),
                ],
            },
        };
        File.WriteAllText(cachePath, JsonUtils.Serialize(cache, true));
        var catalog = CreateCatalog(cachePath);

        var model = catalog.Resolve("opencode-free/model-a", "opencode-free");

        await model.Should().NotBeNull();
        await model!.Id.Should().BeEqualTo("model-a");
        try { File.Delete(cachePath); } catch { }
    }

    [Test]
    public async Task ResolveModelAndTarget_BareModel_ReturnsDefaultTarget()
    {
        var cachePath = TempCachePath();
        var settings = Settings();
        var cache = new OpenCodeCatalogCache
        {
            UpdatedAt = DateTime.UtcNow,
            ModelsByTarget =
            {
                ["opencode-free"] =
                [
                    new OpenCodeModel("test-model", "Test Model", EOpenCodeApiStyle.ChatCompletions,
                        null, true, true, true, false, "remote"),
                ],
            },
        };
        File.WriteAllText(cachePath, JsonUtils.Serialize(cache, true));
        var catalog = CreateCatalog(cachePath);

        var (target, model) = catalog.ResolveTargetModel("test-model", settings);

        await target.Should().NotBeNull();
        await target!.Id.Should().BeEqualTo("opencode-free");
        await model!.Id.Should().BeEqualTo("test-model");
        try { File.Delete(cachePath); } catch { }
    }

    [Test]
    public async Task ResolveTargetModel_ExplicitTargetRef_UsesThatTarget()
    {
        var cachePath = TempCachePath();
        var settings = Settings();
        var cache = new OpenCodeCatalogCache
        {
            UpdatedAt = DateTime.UtcNow,
            ModelsByTarget =
            {
                ["opencode-free"] =
                [
                    new OpenCodeModel("model-a", "Model A", EOpenCodeApiStyle.ChatCompletions,
                        null, true, true, true, false, "remote"),
                ],
            },
        };
        File.WriteAllText(cachePath, JsonUtils.Serialize(cache, true));
        var catalog = CreateCatalog(cachePath);

        var (target, model) = catalog.ResolveTargetModel("opencode-free/model-a", settings);

        await target!.Id.Should().BeEqualTo("opencode-free");
        await model!.Id.Should().BeEqualTo("model-a");
        try { File.Delete(cachePath); } catch { }
    }

    [Test]
    public async Task ResolveTargetModel_UnknownModel_ReturnsNullModel()
    {
        var cachePath = TempCachePath();
        var settings = Settings();
        var cache = new OpenCodeCatalogCache
        {
            UpdatedAt = DateTime.UtcNow,
            ModelsByTarget =
            {
                ["opencode-free"] =
                [
                    new OpenCodeModel("test-model", "Test Model", EOpenCodeApiStyle.ChatCompletions,
                        null, true, true, true, false, "remote"),
                ],
            },
        };
        File.WriteAllText(cachePath, JsonUtils.Serialize(cache, true));
        var catalog = CreateCatalog(cachePath);

        var (target, model) = catalog.ResolveTargetModel("does-not-exist", settings);

        await model.Should().BeNull();
        await target.Should().NotBeNull();
        try { File.Delete(cachePath); } catch { }
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

        var (target, model) = catalog.ResolveTargetModel("unknown-target/unknown-model", settings);

        await target.Should().BeNull();
        await model.Should().BeNull();
    }

    [Test]
    public async Task ResolveTargetModel_DisabledTarget_ReturnsNullTarget()
    {
        var cachePath = TempCachePath();
        var settings = Settings();
        settings.Targets[0].Enabled = false;
        var cache = new OpenCodeCatalogCache
        {
            UpdatedAt = DateTime.UtcNow,
            ModelsByTarget =
            {
                ["opencode-free"] =
                [
                    new OpenCodeModel("test-model", "Test Model", EOpenCodeApiStyle.ChatCompletions,
                        null, true, true, true, false, "remote"),
                ],
            },
        };
        File.WriteAllText(cachePath, JsonUtils.Serialize(cache, true));
        var catalog = CreateCatalog(cachePath);

        var (target, model) = catalog.ResolveTargetModel("test-model", settings);

        await target.Should().BeNull();
        await model.Should().NotBeNull();
        try { File.Delete(cachePath); } catch { }
    }

    [Test]
    public async Task RefreshAsync_WithoutCatalogUrl_SucceedsAndReturnsEmpty()
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
            await catalog.GetModels("opencode-free").Should().BeEmpty();
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
                    """{"data":[{"id":"remote-model-a"},{"id":"remote-model-b"}]}""",
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
            await models.Should().HaveCount(2);
            await models.Should().Contain(m => m.Id == "remote-model-a" && m.Source == "remote");
            await models.Should().Contain(m => m.Id == "remote-model-b" && m.Source == "remote");
            // All models from remote should have Source="remote"
            await models.All(m => m.Source == "remote").Should().BeTrue();
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
    public async Task RefreshAsync_HttpFailure_ReturnsEmpty()
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
            await catalog.GetModels("opencode-free").Should().BeEmpty();
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
            await models.Should().Contain(m => m.Id == "cached-model" && m.Source == "remote");
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
    public async Task LoadCache_FiltersOutDefaultSourceEntries()
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
                        new OpenCodeModel("legacy-default", "Legacy Default", EOpenCodeApiStyle.ChatCompletions,
                            null, true, true, true, false, "default"),
                        new OpenCodeModel("remote-model", "Remote Model", EOpenCodeApiStyle.ChatCompletions,
                            null, true, true, true, false, "remote"),
                    ],
                },
            };
            File.WriteAllText(cachePath, JsonUtils.Serialize(cache, true));

            var catalog = CreateCatalog(cachePath);
            var models = catalog.GetModels("opencode-free");

            await models.Should().HaveCount(1);
            await models.Should().Contain(m => m.Id == "remote-model" && m.Source == "remote");
            await models.Any(m => m.Id == "legacy-default").Should().BeFalse();
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
