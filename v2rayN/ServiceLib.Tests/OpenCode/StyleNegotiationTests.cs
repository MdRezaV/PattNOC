namespace ServiceLib.Tests.OpenCode;

/// <summary>
/// Request-format negotiation: models differ in which upstream API style they
/// answer on (Muse Spark is Responses-only, Mimo speaks Chat Completions), so a
/// format-shaped failure probes the other registered style and the winner is
/// persisted. Format-independent failures (auth, network) never switch.
/// </summary>
public class StyleNegotiationTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public readonly List<string> Paths = new();
        public int CallCount;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubProxyProvider : IActiveProxyProvider
    {
        public ActiveProxySnapshot? Snapshot { get; set; } = new(
            new WebProxy("socks5://127.0.0.1:10808"),
            10808,
            "index-1",
            "Test Profile");

        public Task<ActiveProxySnapshot?> TryGetSnapshotAsync(CancellationToken ct = default)
            => Task.FromResult(Snapshot);

        public Task<bool> IsCoreRunningAsync(CancellationToken ct = default)
            => Task.FromResult(Snapshot is not null);

        public Task<bool> IsProxyAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(Snapshot is not null);
    }

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

    private static NormalizedCompletionRequest Request() => new()
    {
        Model = "big-pickle",
        Messages = [new NormalizedMessage { Role = "user", Content = "Reply with OK." }],
        MaxTokens = 16,
    };

    private static string SeedCache(Action<OpenCodeCatalogCache>? mutate = null)
    {
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
        mutate?.Invoke(cache);
        File.WriteAllText(cachePath, JsonUtils.Serialize(cache, true));
        return cachePath;
    }

    private static RequestExecutor CreateExecutor(
        StubProxyProvider provider, HttpMessageHandler handler, string cachePath)
        => new(provider, new ModelCatalog(provider, cachePath), new OpenCodeTelemetry(), () => handler);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string ResponsesOkBody() => """
        {
          "id": "resp_1",
          "model": "big-pickle",
          "status": "completed",
          "output": [
            { "type": "message", "content": [ { "type": "output_text", "text": "OK" } ] }
          ]
        }
        """;

    [Test]
    public async Task FormatMismatch_500_SwitchesToResponses_AndSucceeds()
    {
        var handler = new RecordingHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/responses")
            ? Json(HttpStatusCode.OK, ResponsesOkBody())
            : Json(HttpStatusCode.InternalServerError, """{"error":"Internal server error"}"""));
        var provider = new StubProxyProvider();
        var cachePath = SeedCache();
        try
        {
            var executor = CreateExecutor(provider, handler, cachePath);

            var result = await executor.ExecuteAsync(Request(), Settings());

            await result.Success.Should().BeTrue();
            await result.Route!.Adapter.Style.Should().BeEqualTo(EOpenCodeApiStyle.Responses);
            await result.Response!.Content.Should().BeEqualTo("OK");
            await handler.CallCount.Should().BeEqualTo(2);
            await handler.Paths[0].Should().BeEqualTo("/zen/v1/chat/completions");
            await handler.Paths[1].Should().BeEqualTo("/zen/v1/responses");
        }
        finally
        {
            try { File.Delete(cachePath); } catch { }
        }
    }

    [Test]
    public async Task NegotiatedStyle_Persists_AndNextExecutorGoesStraightToResponses()
    {
        var cachePath = SeedCache();
        var provider = new StubProxyProvider();
        try
        {
            // Round 1: chat fails with 500 → probe responses → success (persisted).
            var handler1 = new RecordingHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/responses")
                ? Json(HttpStatusCode.OK, ResponsesOkBody())
                : Json(HttpStatusCode.InternalServerError, "boom"));
            var first = await CreateExecutor(provider, handler1, cachePath)
                .ExecuteAsync(Request(), Settings());
            await first.Success.Should().BeTrue();

            // Round 2: fresh executor on the same cache must start at /responses.
            var handler2 = new RecordingHandler(_ => Json(HttpStatusCode.OK, ResponsesOkBody()));
            var second = await CreateExecutor(provider, handler2, cachePath)
                .ExecuteAsync(Request(), Settings());

            await second.Success.Should().BeTrue();
            await handler2.CallCount.Should().BeEqualTo(1);
            await handler2.Paths[0].Should().BeEqualTo("/zen/v1/responses");
        }
        finally
        {
            try { File.Delete(cachePath); } catch { }
        }
    }

    [Test]
    public async Task AuthFailure_DoesNotSwitchStyles()
    {
        var handler = new RecordingHandler(_ =>
            Json(HttpStatusCode.Unauthorized, """{"error":"invalid key"}"""));
        var provider = new StubProxyProvider();
        var cachePath = SeedCache();
        try
        {
            var executor = CreateExecutor(provider, handler, cachePath);

            var result = await executor.ExecuteAsync(Request(), Settings());

            await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.AuthenticationFailed);
            await handler.CallCount.Should().BeEqualTo(1);
            await handler.Paths[0].Should().BeEqualTo("/zen/v1/chat/completions");
        }
        finally
        {
            try { File.Delete(cachePath); } catch { }
        }
    }

    [Test]
    public async Task NetworkFailure_RetriesSameStyle_WithoutProbingAlternate()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));
        var provider = new StubProxyProvider();
        var cachePath = SeedCache();
        try
        {
            var executor = CreateExecutor(provider, handler, cachePath);

            var result = await executor.ExecuteAsync(Request(), Settings(maxRetry: 1));

            await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.NetworkError);
            // Retries stay on the preferred style: a dead proxy is not a format issue.
            await handler.CallCount.Should().BeEqualTo(2);
            await handler.Paths.All(p => p == "/zen/v1/chat/completions").Should().BeTrue();
        }
        finally
        {
            try { File.Delete(cachePath); } catch { }
        }
    }

    [Test]
    public async Task PinnedTargetDefaultApiStyle_SkipsNegotiationEntirely()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.InternalServerError, "boom"));
        var provider = new StubProxyProvider();
        var cachePath = SeedCache();
        try
        {
            var settings = Settings(maxRetry: 0);
            settings.Targets[0].DefaultApiStyle = nameof(EOpenCodeApiStyle.Responses);
            var executor = CreateExecutor(provider, handler, cachePath);

            var result = await executor.ExecuteAsync(Request(), settings);

            await result.Success.Should().BeFalse();
            // One pinned style, one attempt — no chat probe, no fallback.
            await handler.CallCount.Should().BeEqualTo(1);
            await handler.Paths[0].Should().BeEqualTo("/zen/v1/responses");
        }
        finally
        {
            try { File.Delete(cachePath); } catch { }
        }
    }
}
