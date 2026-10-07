namespace ServiceLib.Tests.OpenCode;

/// <summary>
/// The centralized request log: one entry format, always written regardless of
/// DebugLogRequests, never able to fail a request.
/// </summary>
public class OpenCodeRequestLogTests
{
    [Test]
    public async Task Format_IncludesAllSetFields_AndOmitsNulls()
    {
        var line = OpenCodeRequestLog.Format(new OpenCodeLogEntry(
            "attempt",
            RequestId: "abc123",
            Client: "chat",
            TargetId: "opencode-free",
            ModelId: "muse-spark-1.3",
            Style: EOpenCodeApiStyle.Responses,
            Profile: "Test Profile",
            Attempt: 2,
            HttpStatus: 500,
            State: EOpenCodeConnectivityState.ProviderError,
            Ok: false,
            LatencyMs: 1840,
            Detail: "Internal server error"));

        await line.Should().Contain("OpenCode.Req phase=attempt");
        await line.Should().Contain("rid=abc123");
        await line.Should().Contain("client=chat");
        await line.Should().Contain("target=opencode-free");
        await line.Should().Contain("model=muse-spark-1.3");
        await line.Should().Contain("style=Responses");
        await line.Should().Contain("profile=Test Profile");
        await line.Should().Contain("attempt=2");
        await line.Should().Contain("status=500");
        await line.Should().Contain("state=ProviderError");
        await line.Should().Contain("ok=false");
        await line.Should().Contain("latencyMs=1840");
        await line.Should().Contain("detail=\"Internal server error\"");

        var sparse = OpenCodeRequestLog.Format(new OpenCodeLogEntry("outcome"));
        await sparse.Should().BeEqualTo("OpenCode.Req phase=outcome");
    }

    [Test]
    public async Task Format_TruncatesLongDetail_AndNeutralizesQuotes()
    {
        var longDetail = new string('x', 5000) + "\"quoted\"";
        var line = OpenCodeRequestLog.Format(new OpenCodeLogEntry("outbound", Detail: longDetail));

        // 5008 chars in, first 2000 kept, quote characters fully outside the window.
        await line.Should().Contain("…(5008 chars)");
        await line.Count(c => c == '"').Should().BeEqualTo(2); // only the delimiters remain
    }

    [Test]
    public async Task Write_SwallowsSinkExceptions()
    {
        // Scope holds the log gate: swapping the static sink outside it would
        // drop lines from concurrently capturing tests.
        await using var logs = await OpenCodeLogTestScope.BeginAsync();
        var previous = OpenCodeRequestLog.TestSink;
        try
        {
            OpenCodeRequestLog.TestSink = _ => throw new InvalidOperationException("sink exploded");

            OpenCodeRequestLog.Write(new OpenCodeLogEntry("outcome", Ok: false));

            await true.Should().BeTrue(); // reaching this line means no exception escaped
        }
        finally
        {
            OpenCodeRequestLog.TestSink = previous;
        }
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public int CallCount;

        public StubHttpHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
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

    private static (RequestExecutor Executor, OpenCodeItem Settings) CreateExecutor(StubHttpHandler handler)
    {
        var provider = new StubProxyProvider();
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
        var settings = new OpenCodeItem
        {
            Enabled = true,
            GatewayEnabled = true,
            DefaultTarget = "opencode-free",
            DefaultModel = "big-pickle",
            GatewayHost = Global.Loopback,
            GatewayPort = OpenCodeConfigDefaults.DefaultGatewayPort,
            ConnectTimeoutSeconds = 5,
            RequestTimeoutSeconds = 30,
            MaxRetry = 0,
            MaxConcurrentRequests = 8,
            Targets = TargetCatalogDefaults.CreateDefaultTargets(),
            DebugLogRequests = false,
        };
        return (executor, settings);
    }

    private static NormalizedCompletionRequest Request() => new()
    {
        Model = "big-pickle",
        Messages = [new NormalizedMessage { Role = "user", Content = "Reply with OK." }],
        MaxTokens = 16,
    };

    [Test]
    public async Task Executor_LogsAttemptAndOutcome_EvenWhenDebugLogRequestsOff()
    {
        await using var logs = await OpenCodeLogTestScope.BeginAsync();
        var (executor, settings) = CreateExecutor(new StubHttpHandler(HttpStatusCode.OK, """
            {
              "id": "chatcmpl-1",
              "model": "big-pickle",
              "choices": [
                { "message": { "role": "assistant", "content": "OK" }, "finish_reason": "stop" }
              ]
            }
            """));

        var result = await executor.ExecuteAsync(Request(), settings);

        await result.Success.Should().BeTrue();
        await settings.DebugLogRequests.Should().BeFalse();
        // Filter by rid: parallel tests write lines through the same sink.
        await logs.Lines.Any(l =>
            l.Contains($"rid={result.RequestId}") && l.Contains("phase=attempt")
            && l.Contains("ok=true") && l.Contains("status=200")
            && l.Contains("style=ChatCompletions")).Should().BeTrue();
        await logs.Lines.Any(l =>
            l.Contains($"rid={result.RequestId}") && l.Contains("phase=outcome")
            && l.Contains("ok=true") && l.Contains("state=OpenCodeAccepted")).Should().BeTrue();
    }

    [Test]
    public async Task Executor_LogsOutcomeOnFailure_WithStatusAndState()
    {
        await using var logs = await OpenCodeLogTestScope.BeginAsync();
        var (executor, settings) = CreateExecutor(
            new StubHttpHandler(HttpStatusCode.InternalServerError, "boom"));

        var result = await executor.ExecuteAsync(Request(), settings);

        await result.Success.Should().BeFalse();
        // Preferred style's failure: logged once as outcome, plus per-attempt lines.
        await logs.Lines.Any(l =>
            l.Contains($"rid={result.RequestId}") && l.Contains("phase=outcome")
            && l.Contains("ok=false") && l.Contains("status=500")
            && l.Contains("state=ProviderError")).Should().BeTrue();
        await logs.Lines.Count(l =>
            l.Contains($"rid={result.RequestId}") && l.Contains("phase=attempt")).Should().BeEqualTo(2);
    }
}
