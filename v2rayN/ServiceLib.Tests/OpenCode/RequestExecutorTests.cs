namespace ServiceLib.Tests.OpenCode;

public class RequestExecutorTests
{
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int CallCount;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubProxyProvider : IActiveProxyProvider
    {
        public ActiveProxySnapshot? Snapshot { get; set; }

        public Task<ActiveProxySnapshot?> TryGetSnapshotAsync(CancellationToken ct = default)
            => Task.FromResult(Snapshot);

        public Task<bool> IsCoreRunningAsync(CancellationToken ct = default)
            => Task.FromResult(Snapshot is not null);

        public Task<bool> IsProxyAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(Snapshot is not null);
    }

    private static ActiveProxySnapshot Snapshot() => new(
        new WebProxy("socks5://127.0.0.1:10808"),
        10808,
        "index-1",
        "Test Profile");

    private static OpenCodeItem Settings(int maxRetry = 1) => new()
    {
        Enabled = true,
        GatewayEnabled = true,
        DefaultTarget = "opencode-free",
        DefaultModel = "big-pickle",
        GatewayHost = Global.Loopback,
        GatewayPort = OpenCodeConfigDefaults.DefaultGatewayPort,
        ConnectTimeoutSeconds = 5,
        RequestTimeoutSeconds = 30,
        MaxRetry = maxRetry,
        MaxConcurrentRequests = 8,
        Targets = TargetCatalogDefaults.CreateDefaultTargets(),
    };

    private static NormalizedCompletionRequest Request(string model = "big-pickle") => new()
    {
        Model = model,
        Messages = [new NormalizedMessage { Role = "user", Content = "Reply with OK." }],
        MaxTokens = 16,
    };

    private static RequestExecutor CreateExecutor(
        StubProxyProvider provider,
        HttpMessageHandler handler,
        string? cachePath = null)
    {
        var actualCachePath = cachePath ?? Path.Combine(Path.GetTempPath(), $"oc_{Guid.NewGuid():N}.json");
        var cache = new OpenCodeCatalogCache
        {
            UpdatedAt = DateTime.UtcNow,
            ModelsByTarget =
            {
                ["opencode-free"] =
                [
                    new OpenCodeModel("big-pickle", "Big Pickle", EOpenCodeApiStyle.ChatCompletions,
                        null, true, true, true, false, "remote", IsFree: true),
                ],
            },
        };
        File.WriteAllText(actualCachePath, JsonUtils.Serialize(cache, true));
        var catalog = new ModelCatalog(provider, actualCachePath);
        return new RequestExecutor(provider, catalog, new OpenCodeTelemetry(), () => handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string ChatOkBody() => """
        {
          "id": "chatcmpl-1",
          "model": "big-pickle",
          "choices": [
            { "message": { "role": "assistant", "content": "OK" }, "finish_reason": "stop" }
          ],
          "usage": { "prompt_tokens": 3, "completion_tokens": 1 }
        }
        """;

    [Test]
    public async Task Execute_Success_ReturnsMaterializedResponse()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.OK, ChatOkBody()));
        var provider = new StubProxyProvider { Snapshot = Snapshot() };
        var executor = CreateExecutor(provider, handler);

        var result = await executor.ExecuteAsync(Request(), Settings());

        await result.Success.Should().BeTrue();
        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.OpenCodeAccepted);
        await result.Response.Should().NotBeNull();
        await result.Response!.Content.Should().BeEqualTo("OK");
        await result.HttpStatus.Should().BeEqualTo(200);
        await result.Route.Should().NotBeNull();
        await result.Route!.ProfileRemark.Should().BeEqualTo("Test Profile");
        await handler.CallCount.Should().BeEqualTo(1);
    }

    [Test]
    public async Task Execute_NoProxy_ReturnsClientRestricted_WithoutCallingUpstream()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.OK, ChatOkBody()));
        var provider = new StubProxyProvider { Snapshot = null };
        var executor = CreateExecutor(provider, handler);

        var result = await executor.ExecuteAsync(Request(), Settings());

        await result.Success.Should().BeFalse();
        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.ClientRestricted);
        await result.HttpStatus.Should().BeEqualTo(503);
        await result.Error!.Code.Should().BeEqualTo("client_restricted");
        await handler.CallCount.Should().BeEqualTo(0);
    }

    [Test]
    public async Task Execute_UnknownModel_ReturnsModelNotFound()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.OK, ChatOkBody()));
        var provider = new StubProxyProvider { Snapshot = Snapshot() };
        var executor = CreateExecutor(provider, handler);

        var result = await executor.ExecuteAsync(Request("no-such-model"), Settings());

        await result.Success.Should().BeFalse();
        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.ModelNotFound);
        await result.HttpStatus.Should().BeEqualTo(404);
        await handler.CallCount.Should().BeEqualTo(0);
    }

    [Test]
    public async Task Execute_ToolsOnModelWithoutSupport_ReturnsUnsupportedRequest()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.OK, ChatOkBody()));
        var provider = new StubProxyProvider { Snapshot = Snapshot() };

        var cachePath = Path.Combine(Path.GetTempPath(), $"oc_{Guid.NewGuid():N}.json");
        var cache = new OpenCodeCatalogCache
        {
            ModelsByTarget =
            {
                ["opencode-free"] =
                [
                    new OpenCodeModel("no-tools", "No Tools", EOpenCodeApiStyle.ChatCompletions,
                        null, true, false, true, false, "remote"),
                ],
            },
        };
        File.WriteAllText(cachePath, JsonUtils.Serialize(cache, true));
        var catalog = new ModelCatalog(provider, cachePath);
        var executor = new RequestExecutor(provider, catalog, new OpenCodeTelemetry(), () => handler);

        var request = Request("no-tools") with
        {
            Tools = [new NormalizedTool
            {
                Type = "function",
                Function = new NormalizedFunctionDef { Name = "x" },
            }],
        };

        var result = await executor.ExecuteAsync(request, Settings());

        await result.Success.Should().BeFalse();
        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.UnsupportedRequest);
        await result.HttpStatus.Should().BeEqualTo(400);
        await result.Error!.Param.Should().BeEqualTo("tools");
        await handler.CallCount.Should().BeEqualTo(0);

        try { File.Delete(cachePath); } catch { }
    }

    [Test]
    public async Task Execute_ConnectFailure_RetriesOnce_ThenFails()
    {
        var attempts = 0;
        var handler = new StubHttpHandler(_ =>
        {
            attempts++;
            throw new HttpRequestException("connection refused");
        });
        var provider = new StubProxyProvider { Snapshot = Snapshot() };
        var executor = CreateExecutor(provider, handler);

        var result = await executor.ExecuteAsync(Request(), Settings(maxRetry: 1));

        await result.Success.Should().BeFalse();
        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.NetworkError);
        await attempts.Should().BeEqualTo(2);
    }

    [Test]
    public async Task Execute_AuthFailure_DoesNotRetry()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.Unauthorized, """{"error":"invalid key"}"""));
        var provider = new StubProxyProvider { Snapshot = Snapshot() };
        var executor = CreateExecutor(provider, handler);

        var result = await executor.ExecuteAsync(Request(), Settings(maxRetry: 1));

        await result.Success.Should().BeFalse();
        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.AuthenticationFailed);
        await result.HttpStatus.Should().BeEqualTo(401);
        await handler.CallCount.Should().BeEqualTo(1);
    }

    [Test]
    public async Task Execute_RateLimit_DoesNotRetry_AndNeverRotatesProfile()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.TooManyRequests, """{"error":"rate limited"}"""));
        var provider = new StubProxyProvider { Snapshot = Snapshot() };
        var executor = CreateExecutor(provider, handler);

        var result = await executor.ExecuteAsync(Request(), Settings(maxRetry: 3));

        await result.Success.Should().BeFalse();
        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.RateLimited);
        await result.HttpStatus.Should().BeEqualTo(429);
        await handler.CallCount.Should().BeEqualTo(1);
        await result.Route!.ProfileIndexId.Should().BeEqualTo("index-1");
        await result.Route.ProfileRemark.Should().BeEqualTo("Test Profile");
    }

    [Test]
    public async Task Execute_FreeUsageLimit_ClassifiedDistinctlyFromRateLimit()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.TooManyRequests,
            """{"error":"Free usage limit reached for this model"}"""));
        var provider = new StubProxyProvider { Snapshot = Snapshot() };
        var executor = CreateExecutor(provider, handler);

        var result = await executor.ExecuteAsync(Request(), Settings());

        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.FreeUsageLimit);
        await result.Error!.Code.Should().BeEqualTo("free_usage_limit");
    }

    [Test]
    public async Task Execute_Provider5xx_ProbesBothStyles_RetriesLastOnce()
    {
        // A 5xx looks like "wrong request format": the preferred style is probed
        // once, then the alternate format gets 1 + MaxRetry attempts.
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.BadGateway, "upstream boom"));
        var provider = new StubProxyProvider { Snapshot = Snapshot() };
        var executor = CreateExecutor(provider, handler);

        var result = await executor.ExecuteAsync(Request(), Settings(maxRetry: 1));

        await result.Success.Should().BeFalse();
        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.ProviderError);
        // Preferred style failed → its failure is reported, not the alternate's.
        await result.Route!.Adapter.Style.Should().BeEqualTo(EOpenCodeApiStyle.ChatCompletions);
        await handler.CallCount.Should().BeEqualTo(3);
    }

    [Test]
    public async Task Execute_MaxRetryZero_OneAttemptPerFormat()
    {
        // MaxRetry=0 means no retries — but each registered format still gets its
        // single probe, since a 5xx may simply be the wrong endpoint for the model.
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.BadGateway, "boom"));
        var provider = new StubProxyProvider { Snapshot = Snapshot() };
        var executor = CreateExecutor(provider, handler);

        var result = await executor.ExecuteAsync(Request(), Settings(maxRetry: 0));

        await handler.CallCount.Should().BeEqualTo(2);
        await result.Success.Should().BeFalse();
    }

    [Test]
    public async Task Execute_WithoutKeyAndKeyRequired_ReturnsAuthenticationFailed()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.OK, ChatOkBody()));
        var provider = new StubProxyProvider { Snapshot = Snapshot() };
        var cachePath = Path.Combine(Path.GetTempPath(), $"oc_{Guid.NewGuid():N}.json");
        var cache = new OpenCodeCatalogCache
        {
            UpdatedAt = DateTime.UtcNow,
            ModelsByTarget =
            {
                ["opencode-free"] =
                [
                    new OpenCodeModel("big-pickle", "Big Pickle", EOpenCodeApiStyle.ChatCompletions,
                        null, true, true, true, false, "remote", IsFree: true),
                ],
            },
        };
        File.WriteAllText(cachePath, JsonUtils.Serialize(cache, true));
        var catalog = new ModelCatalog(provider, cachePath);
        var executor = new RequestExecutor(provider, catalog, new OpenCodeTelemetry(), () => handler);

        var settings = Settings();
        settings.Targets[0].KeyOptional = false;
        settings.Targets[0].ApiKey = null;

        var result = await executor.ExecuteAsync(Request(), settings);

        await result.Success.Should().BeFalse();
        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.AuthenticationFailed);
        await result.HttpStatus.Should().BeEqualTo(401);
        await handler.CallCount.Should().BeEqualTo(0);
        try { File.Delete(cachePath); } catch { }
    }

    [Test]
    public async Task Execute_WithOptionalKeyAndNoKey_SendsRequestWithoutAuthRequirement()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.OK, ChatOkBody()));
        var provider = new StubProxyProvider { Snapshot = Snapshot() };
        var executor = CreateExecutor(provider, handler);

        var settings = Settings();
        settings.Targets[0].KeyOptional = true;
        settings.Targets[0].ApiKey = null;

        var result = await executor.ExecuteAsync(Request(), settings);

        await result.Success.Should().BeTrue();
        await handler.CallCount.Should().BeEqualTo(1);
    }

    [Test]
    public async Task Execute_Streaming_ReturnsEventsAndPinsRoute()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}",
            "",
            "data: [DONE]",
            "");
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });
        var provider = new StubProxyProvider { Snapshot = Snapshot() };
        var executor = CreateExecutor(provider, handler);

        var result = await executor.ExecuteAsync(Request() with { Stream = true }, Settings());

        await result.Success.Should().BeTrue();
        await result.Events.Should().NotBeNull();
        await result.Route!.Adapter.Style.Should().BeEqualTo(EOpenCodeApiStyle.ChatCompletions);

        var events = new List<NormalizedStreamEvent>();
        await foreach (var evt in result.Events!)
        {
            events.Add(evt);
        }

        await events.Should().Contain(e => e.Type == NormalizedStreamEventType.TextDelta && e.Text == "OK");
        await events.Last().Type.Should().BeEqualTo(NormalizedStreamEventType.Done);
    }

    [Test]
    public async Task Execute_ModelRefWithTargetPrefix_UsesThatTargetsCatalog()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.OK, ChatOkBody()));
        var provider = new StubProxyProvider { Snapshot = Snapshot() };
        var executor = CreateExecutor(provider, handler);

        var result = await executor.ExecuteAsync(Request("opencode-free/big-pickle"), Settings());

        await result.Success.Should().BeTrue();
        await result.Route!.Target.Id.Should().BeEqualTo("opencode-free");
        await result.Route.Model.Id.Should().BeEqualTo("big-pickle");
    }
}
