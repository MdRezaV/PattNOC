namespace ServiceLib.Tests.OpenCode;

/// <summary>
/// The tester must only report success when the stream actually produced a
/// completion: in-band errors, empty streams, and exceptions all fail, and every
/// run leaves a phase=test log line behind.
/// </summary>
public class ConnectivityTesterTests
{
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responder;
        public int CallCount;

        public StubHttpHandler(Func<HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder());
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

    private static OpenCodeItem Settings(int maxRetry = 0) => new()
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

    private static ConnectivityTester CreateTester(StubProxyProvider provider, HttpMessageHandler handler)
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
        File.WriteAllText(cachePath, JsonUtils.Serialize(cache, true));
        var catalog = new ModelCatalog(provider, cachePath);
        var executor = new RequestExecutor(provider, catalog, new OpenCodeTelemetry(), () => handler);
        return new ConnectivityTester(executor);
    }

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private static string OkStream() => string.Join("\n",
        "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}",
        "",
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}",
        "",
        "data: [DONE]",
        "");

    [Test]
    public async Task StreamWithTextAndDone_ReportsAccepted()
    {
        await using var logs = await OpenCodeLogTestScope.BeginAsync();
        var handler = new StubHttpHandler(() => Sse(OkStream()));
        var tester = CreateTester(new StubProxyProvider(), handler);

        var result = await tester.TestAsync(Settings());

        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.OpenCodeAccepted);
        await result.Detail.Should().BeEqualTo("OK");
        await logs.Lines.Any(l =>
            l.Contains("phase=test") && l.Contains("ok=true")
            && l.Contains("state=OpenCodeAccepted")).Should().BeTrue();
    }

    [Test]
    public async Task InBandErrorFrame_ReportsProviderError()
    {
        await using var logs = await OpenCodeLogTestScope.BeginAsync();
        var handler = new StubHttpHandler(() => Sse(string.Join("\n",
            "data: {\"error\":{\"message\":\"model exploded\",\"code\":\"server_error\"}}",
            "")));
        var tester = CreateTester(new StubProxyProvider(), handler);

        var result = await tester.TestAsync(Settings());

        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.ProviderError);
        await result.Detail.Should().Contain("model exploded");
        await logs.Lines.Any(l =>
            l.Contains("phase=test") && l.Contains("ok=false")).Should().BeTrue();
    }

    [Test]
    public async Task EmptyStream_ReportsProviderError_NotSuccess()
    {
        await using var logs = await OpenCodeLogTestScope.BeginAsync();
        var handler = new StubHttpHandler(() => Sse(""));
        var tester = CreateTester(new StubProxyProvider(), handler);

        var result = await tester.TestAsync(Settings());

        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.ProviderError);
        await result.Detail.Should().Contain("without a completion event");
        await logs.Lines.Any(l => l.Contains("phase=test") && l.Contains("ok=false")).Should().BeTrue();
    }

    [Test]
    public async Task MalformedSseLine_IsSkipped_GoodFramesStillParse()
    {
        await using var logs = await OpenCodeLogTestScope.BeginAsync();
        var handler = new StubHttpHandler(() => Sse(string.Join("\n",
            "data: {not valid json",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}",
            "",
            "data: [DONE]",
            "")));
        var tester = CreateTester(new StubProxyProvider(), handler);

        var result = await tester.TestAsync(Settings());

        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.OpenCodeAccepted);
        await result.Detail.Should().BeEqualTo("OK");
    }

    [Test]
    public async Task UpstreamException_ReportsNetworkError_AndLogsFailure()
    {
        await using var logs = await OpenCodeLogTestScope.BeginAsync();
        var handler = new StubHttpHandler(() => throw new HttpRequestException("connection refused"));
        var tester = CreateTester(new StubProxyProvider(), handler);

        var result = await tester.TestAsync(Settings());

        await result.State.Should().BeEqualTo(EOpenCodeConnectivityState.NetworkError);
        await logs.Lines.Any(l =>
            l.Contains("phase=outcome") && l.Contains("ok=false")
            && l.Contains("state=NetworkError")).Should().BeTrue();
        await logs.Lines.Any(l => l.Contains("phase=test") && l.Contains("ok=false")).Should().BeTrue();
    }

    [Test]
    public async Task EveryRun_LogsAttemptOutcomeAndTestLines()
    {
        await using var logs = await OpenCodeLogTestScope.BeginAsync();
        var handler = new StubHttpHandler(() => Sse(OkStream()));
        var tester = CreateTester(new StubProxyProvider(), handler);

        await tester.TestAsync(Settings());

        var lines = logs.Lines;
        await lines.Any(l => l.Contains("phase=attempt")).Should().BeTrue();
        await lines.Any(l => l.Contains("phase=outcome") && l.Contains("ok=true")).Should().BeTrue();
        await lines.Any(l => l.Contains("phase=stream")).Should().BeTrue();
        await lines.Any(l => l.Contains("phase=test")).Should().BeTrue();
    }
}
