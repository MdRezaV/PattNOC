namespace ServiceLib.Tests.OpenCode;

public class GatewayServiceTests
{
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

    private sealed class MockUpstream : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<HttpListenerContext, Task> _handler;
        public int Port { get; }

        public MockUpstream(Func<HttpListenerContext, Task> handler)
        {
            _handler = handler;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener.Prefixes.Add($"http://{Global.Loopback}:{Port}/");
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = HandleAsync(ctx);
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                await _handler(ctx);
            }
            catch
            {
                // Upstream stub failures surface as gateway network errors.
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _listener.Close();
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static async Task WriteSseAsync(HttpListenerContext ctx, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static ActiveProxySnapshot DirectSnapshot() => new(null!, 0, "index-1", "Test Profile");

    private static OpenCodeItem Settings(string upstreamBaseUrl, int maxConcurrent = 8, int maxRetry = 0) => new()
    {
        Enabled = true,
        GatewayEnabled = true,
        GatewayHost = Global.Loopback,
        GatewayPort = 0,
        DefaultTarget = "opencode-free",
        DefaultModel = "big-pickle",
        ConnectTimeoutSeconds = 5,
        RequestTimeoutSeconds = 30,
        MaxRetry = maxRetry,
        MaxConcurrentRequests = maxConcurrent,
        Targets =
        [
            new OpenCodeTargetItem
            {
                Id = "opencode-free",
                Name = "OpenCode Free",
                BaseUrl = upstreamBaseUrl,
                CatalogUrl = null,
                KeyOptional = true,
                Enabled = true,
            },
        ],
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

    private static (GatewayService Gateway, OpenCodeItem Settings, HttpClient Client) CreateGateway(
        OpenCodeItem settings,
        StubProxyProvider provider,
        string? cachePath = null)
    {
        var catalog = new ModelCatalog(
            provider,
            cachePath ?? Path.Combine(Path.GetTempPath(), $"oc_gw_{Guid.NewGuid():N}.json"));
        var telemetry = new OpenCodeTelemetry();
        var executor = new RequestExecutor(provider, catalog, telemetry);
        var gateway = new GatewayService(executor, catalog, telemetry, () => settings);
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://{Global.Loopback}:0/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        return (gateway, settings, client);
    }

    private static async Task<Uri> StartGatewayAsync(GatewayService gateway)
    {
        var ok = await gateway.StartAsync();
        await ok.Should().BeTrue();
        await gateway.BoundPort.Should().BeGreaterThan(0);
        return new Uri($"http://{Global.Loopback}:{gateway.BoundPort}");
    }

    private static string ChatBody(string model = "big-pickle", bool stream = false, bool withTools = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new List<object?>
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = "Reply with OK." },
            },
            ["stream"] = stream,
            ["tools"] = withTools
                ? new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = "get_weather",
                            ["description"] = "Get weather",
                        },
                    },
                }
                : null,
        });

    [Test]
    public async Task Gateway_StartsOnEphemeralPort_ListsModels()
    {
        using var upstream = new MockUpstream(ctx => WriteJsonAsync(ctx, 200, """{"data":[{"id":"big-pickle"}]}"""));
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider);

        try
        {
            var baseUri = await StartGatewayAsync(gateway);
            client.BaseAddress = baseUri;

            var resp = await client.GetAsync("/v1/models");
            await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.OK);
            var body = await resp.Content.ReadAsStringAsync();
            await body.Should().Contain("big-pickle");
            await body.Should().Contain("\"object\":\"list\"");
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
        }
    }

    [Test]
    public async Task Gateway_ListModels_FreeOnly_ExposesOnlyFreeModels()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"oc_gw_freeonly_{Guid.NewGuid():N}.json");
        var cache = new OpenCodeCatalogCache
        {
            ModelsByTarget =
            {
                ["opencode-free"] =
                [
                    new OpenCodeModel("big-pickle", "Big Pickle", EOpenCodeApiStyle.ChatCompletions,
                        null, true, true, true, false, "default", IsFree: true),
                    new OpenCodeModel("remote-paid", "Remote Paid", EOpenCodeApiStyle.ChatCompletions,
                        null, true, true, true, false, "remote", IsFree: false),
                ],
            },
        };
        File.WriteAllText(cachePath, JsonUtils.Serialize(cache, true));

        using var upstream = new MockUpstream(_ => Task.CompletedTask);
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        settings.FreeOnly = true;
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider, cachePath);

        try
        {
            client.BaseAddress = await StartGatewayAsync(gateway);

            var resp = await client.GetAsync("/v1/models");
            await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.OK);
            var body = await resp.Content.ReadAsStringAsync();
            await body.Should().Contain("big-pickle");
            await body.Should().NotContain("remote-paid");

            var freeModel = await client.GetAsync("/v1/models/big-pickle");
            await freeModel.StatusCode.Should().BeEqualTo(HttpStatusCode.OK);

            var paidModel = await client.GetAsync("/v1/models/remote-paid");
            await paidModel.StatusCode.Should().BeEqualTo(HttpStatusCode.NotFound);
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
            try { File.Delete(cachePath); } catch { }
        }
    }

    [Test]
    public async Task Gateway_GetModel_Returns404_ForUnknownModel()
    {
        using var upstream = new MockUpstream(_ => Task.CompletedTask);
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider);

        try
        {
            client.BaseAddress = await StartGatewayAsync(gateway);

            var resp = await client.GetAsync("/v1/models/does-not-exist");
            await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.NotFound);
            var body = await resp.Content.ReadAsStringAsync();
            await body.Should().Contain("model_not_found");
            await body.Should().Contain("\"type\":\"not_found_error\"");
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
        }
    }

    [Test]
    public async Task Gateway_ClaudeAlias_ListModels_PrefixesIds()
    {
        using var upstream = new MockUpstream(ctx => WriteJsonAsync(ctx, 200, """{"data":[{"id":"big-pickle"}]}"""));
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider);

        try
        {
            client.BaseAddress = await StartGatewayAsync(gateway);

            var resp = await client.GetAsync("/v1/claude/models");
            await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.OK);
            var body = await resp.Content.ReadAsStringAsync();
            await body.Should().Contain("claude-big-pickle");
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
        }
    }

    [Test]
    public async Task Gateway_ClaudeAlias_GetModel_StripsPrefixForLookup_ReturnsPrefixedId()
    {
        using var upstream = new MockUpstream(_ => Task.CompletedTask);
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider);

        try
        {
            client.BaseAddress = await StartGatewayAsync(gateway);

            var resp = await client.GetAsync("/v1/claude/models/claude-big-pickle");
            await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.OK);
            var body = await resp.Content.ReadAsStringAsync();
            await body.Should().Contain("\"id\":\"claude-big-pickle\"");

            var missing = await client.GetAsync("/v1/claude/models/claude-does-not-exist");
            await missing.StatusCode.Should().BeEqualTo(HttpStatusCode.NotFound);
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
        }
    }

    [Test]
    public async Task Gateway_ClaudeAlias_ChatCompletions_StripsPrefixForUpstream_EchoesPrefixedModel()
    {
        string? upstreamModel = null;
        using var upstream = new MockUpstream(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var reqBody = await reader.ReadToEndAsync();
            try
            {
                using var doc = JsonDocument.Parse(reqBody);
                upstreamModel = doc.RootElement.GetProperty("model").GetString();
            }
            catch
            {
                // Best-effort capture; failure surfaces via assertions below.
            }

            await WriteJsonAsync(ctx, 200, ChatOkBody());
        });
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider);

        try
        {
            client.BaseAddress = await StartGatewayAsync(gateway);

            var resp = await client.PostAsync("/v1/claude/chat/completions",
                new StringContent(ChatBody("claude-big-pickle"), Encoding.UTF8, "application/json"));

            await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.OK);
            await upstreamModel.Should().BeEqualTo("big-pickle");
            var body = await resp.Content.ReadAsStringAsync();
            await body.Should().Contain("OK");
            await body.Should().Contain("\"model\":\"claude-big-pickle\"");
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
        }
    }

    [Test]
    public async Task Gateway_ChatCompletions_NonStream_ReturnsMaterializedResponse()
    {
        using var upstream = new MockUpstream(ctx => WriteJsonAsync(ctx, 200, ChatOkBody()));
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider);

        try
        {
            client.BaseAddress = await StartGatewayAsync(gateway);

            var resp = await client.PostAsync("/v1/chat/completions",
                new StringContent(ChatBody(), Encoding.UTF8, "application/json"));

            await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.OK);
            await resp.Headers.Contains("x-opencode-request-id").Should().BeTrue();
            var body = await resp.Content.ReadAsStringAsync();
            await body.Should().Contain("OK");
            await body.Should().Contain("big-pickle");
            await body.Should().Contain("\"object\":\"chat.completion\"");
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
        }
    }

    [Test]
    public async Task Gateway_ChatCompletions_Stream_EmitsSseFramesAndDone()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"He\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"llo\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}",
            "",
            "data: [DONE]",
            "");
        using var upstream = new MockUpstream(ctx => WriteSseAsync(ctx, sse));
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider);

        try
        {
            client.BaseAddress = await StartGatewayAsync(gateway);

            var resp = await client.PostAsync("/v1/chat/completions",
                new StringContent(ChatBody(stream: true), Encoding.UTF8, "application/json"));

            await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.OK);
            await resp.Content.Headers.ContentType!.MediaType.Should().BeEqualTo("text/event-stream");
            var body = await resp.Content.ReadAsStringAsync();
            await body.Should().Contain("data: ");
            await body.Should().Contain("chat.completion.chunk");
            await body.Should().Contain("data: [DONE]");
            await body.Should().Contain("He");
            await body.Should().Contain("llo");
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
        }
    }

    [Test]
    public async Task Gateway_ChatCompletions_Tools_RoundTripsToolCalls()
    {
        var payload = """
            {
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": null,
                    "tool_calls": [
                      {
                        "id": "call_1",
                        "type": "function",
                        "function": { "name": "get_weather", "arguments": "{\"city\":\"Tehran\"}" }
                      }
                    ]
                  },
                  "finish_reason": "tool_calls"
                }
              ]
            }
            """;
        using var upstream = new MockUpstream(ctx => WriteJsonAsync(ctx, 200, payload));
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider);

        try
        {
            client.BaseAddress = await StartGatewayAsync(gateway);

            var resp = await client.PostAsync("/v1/chat/completions",
                new StringContent(ChatBody(withTools: true), Encoding.UTF8, "application/json"));

            await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.OK);
            var body = await resp.Content.ReadAsStringAsync();
            await body.Should().Contain("get_weather");
            await body.Should().Contain("call_1");
            await body.Should().Contain("\"finish_reason\":\"tool_calls\"");
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
        }
    }

    [Test]
    public async Task Gateway_ToolsOnModelWithoutSupport_Returns400()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"oc_gw_tools_{Guid.NewGuid():N}.json");
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

        using var upstream = new MockUpstream(ctx => WriteJsonAsync(ctx, 200, ChatOkBody()));
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        settings.DefaultModel = "no-tools";
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider, cachePath);

        try
        {
            client.BaseAddress = await StartGatewayAsync(gateway);

            var resp = await client.PostAsync("/v1/chat/completions",
                new StringContent(ChatBody("no-tools", withTools: true), Encoding.UTF8, "application/json"));

            await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.BadRequest);
            var body = await resp.Content.ReadAsStringAsync();
            await body.Should().Contain("unsupported_request");
            await body.Should().Contain("\"param\":\"tools\"");
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
            try { File.Delete(cachePath); } catch { }
        }
    }

    [Test]
    public async Task Gateway_DisabledSettings_Returns503OnCompletion()
    {
        using var upstream = new MockUpstream(ctx => WriteJsonAsync(ctx, 200, ChatOkBody()));
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        settings.Enabled = false;
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider);

        try
        {
            client.BaseAddress = await StartGatewayAsync(gateway);

            var resp = await client.PostAsync("/v1/chat/completions",
                new StringContent(ChatBody(), Encoding.UTF8, "application/json"));

            await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.ServiceUnavailable);
            var body = await resp.Content.ReadAsStringAsync();
            await body.Should().Contain("gateway_disabled");
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
        }
    }

    [Test]
    public async Task Gateway_429_PassesThroughRetryAfter_AndDoesNotRetryUpstream()
    {
        var upstreamCalls = 0;
        using var upstream = new MockUpstream(async ctx =>
        {
            Interlocked.Increment(ref upstreamCalls);
            ctx.Response.StatusCode = 429;
            ctx.Response.Headers["Retry-After"] = "7";
            var bytes = Encoding.UTF8.GetBytes("""{"error":"rate limited"}""");
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1", maxRetry: 3);
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider);

        try
        {
            client.BaseAddress = await StartGatewayAsync(gateway);

            var resp = await client.PostAsync("/v1/chat/completions",
                new StringContent(ChatBody(), Encoding.UTF8, "application/json"));

            await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.TooManyRequests);
            await resp.Headers.TryGetValues("Retry-After", out var values).Should().BeTrue();
            await values!.First().Should().BeEqualTo("7");
            var body = await resp.Content.ReadAsStringAsync();
            await body.Should().Contain("rate_limit_error");
            await body.Should().Contain("rate_limit_exceeded");
            await upstreamCalls.Should().BeEqualTo(1);
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
        }
    }

    [Test]
    public async Task Gateway_PortInUse_StartFailsWithoutCrashing_AndKeepsExistingListener()
    {
        using var upstream = new MockUpstream(ctx => WriteJsonAsync(ctx, 200, ChatOkBody()));
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gatewayA, _, clientA) = CreateGateway(settings, provider);

        try
        {
            var baseUri = await StartGatewayAsync(gatewayA);
            clientA.BaseAddress = baseUri;

            var settingsB = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
            settingsB.GatewayPort = gatewayA.BoundPort;
            var providerB = new StubProxyProvider { Snapshot = DirectSnapshot() };
            var (gatewayB, _, clientB) = CreateGateway(settingsB, providerB);
            try
            {
                var okB = await gatewayB.StartAsync();
                await okB.Should().BeFalse();
                await gatewayB.LastError.Should().BeEqualTo("port_in_use");
                await gatewayB.LastErrorIsPortInUse.Should().BeTrue();
                await gatewayB.IsRunning.Should().BeFalse();

                var resp = await clientA.PostAsync("/v1/chat/completions",
                    new StringContent(ChatBody(), Encoding.UTF8, "application/json"));
                await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.OK);
            }
            finally
            {
                await gatewayB.StopAsync(TimeSpan.FromSeconds(1));
                clientB.Dispose();
            }
        }
        finally
        {
            await gatewayA.StopAsync(TimeSpan.FromSeconds(2));
            clientA.Dispose();
        }
    }

    [Test]
    public async Task Gateway_StopAsync_ReleasesPort()
    {
        using var upstream = new MockUpstream(_ => Task.CompletedTask);
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider);

        try
        {
            await StartGatewayAsync(gateway);
            var port = gateway.BoundPort;

            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            await gateway.IsRunning.Should().BeFalse();

            var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            probe.Stop();
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(1));
            client.Dispose();
        }
    }

    [Test]
    public async Task Gateway_ClientAbort_DuringStream_DoesNotHangGateway()
    {
        using var upstream = new MockUpstream(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.SendChunked = true;
            var first = Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n");
            await ctx.Response.OutputStream.WriteAsync(first);
            await ctx.Response.OutputStream.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(10));
            var last = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            await ctx.Response.OutputStream.WriteAsync(last);
            ctx.Response.Close();
        });
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider);

        try
        {
            client.BaseAddress = await StartGatewayAsync(gateway);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var aborted = false;
            try
            {
                using var resp = await client.PostAsync("/v1/chat/completions",
                    new StringContent(ChatBody(stream: true), Encoding.UTF8, "application/json"),
                    cts.Token);
                var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                var buffer = new byte[256];
                var read = await stream.ReadAsync(buffer, cts.Token);
                _ = read;
            }
            catch (OperationCanceledException)
            {
                aborted = true;
            }

            await aborted.Should().BeTrue();

            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ok = await gateway.StartAsync(cts2.Token);
            await ok.Should().BeTrue();
            var health = await client.GetAsync("/v1/models", cts2.Token);
            await health.StatusCode.Should().BeEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
        }
    }

    [Test]
    public async Task Gateway_MalformedBody_Returns400()
    {
        using var upstream = new MockUpstream(_ => Task.CompletedTask);
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider);

        try
        {
            client.BaseAddress = await StartGatewayAsync(gateway);

            var resp = await client.PostAsync("/v1/chat/completions",
                new StringContent("{not-json", Encoding.UTF8, "application/json"));

            await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.BadRequest);
            var body = await resp.Content.ReadAsStringAsync();
            await body.Should().Contain("invalid_request_error");
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
        }
    }

    [Test]
    public async Task Gateway_EmptyBody_Returns400()
    {
        using var upstream = new MockUpstream(_ => Task.CompletedTask);
        var settings = Settings($"http://{Global.Loopback}:{upstream.Port}/v1");
        var provider = new StubProxyProvider { Snapshot = DirectSnapshot() };
        var (gateway, _, client) = CreateGateway(settings, provider);

        try
        {
            client.BaseAddress = await StartGatewayAsync(gateway);

            var resp = await client.PostAsync("/v1/chat/completions",
                new StringContent("", Encoding.UTF8, "application/json"));

            await resp.StatusCode.Should().BeEqualTo(HttpStatusCode.BadRequest);
            var body = await resp.Content.ReadAsStringAsync();
            await body.Should().Contain("Request body is required");
        }
        finally
        {
            await gateway.StopAsync(TimeSpan.FromSeconds(2));
            client.Dispose();
        }
    }
}
