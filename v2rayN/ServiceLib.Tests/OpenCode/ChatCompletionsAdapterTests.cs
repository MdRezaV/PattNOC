namespace ServiceLib.Tests.OpenCode;

public class ChatCompletionsAdapterTests
{
    private static OpenCodeTargetItem Target(string? baseUrl = "https://opencode.ai/zen/v1") => new()
    {
        Id = "opencode-free",
        Name = "OpenCode Free",
        BaseUrl = baseUrl!,
        KeyOptional = true,
        Enabled = true,
    };

    private static NormalizedCompletionRequest Request(
        string model = "big-pickle",
        bool stream = false,
        int? maxTokens = null,
        string? user = null) => new()
    {
        Model = model,
        Messages = [new NormalizedMessage { Role = "user", Content = "Reply with OK." }],
        Stream = stream,
        MaxTokens = maxTokens,
        User = user,
    };

    [Test]
    public async Task BuildRequest_PostsToChatCompletionsPath()
    {
        var adapter = new ChatCompletionsAdapter();
        using var msg = adapter.BuildRequest(Request(), Target(), null, "req1");

        await msg.Method.Should().BeEqualTo(HttpMethod.Post);
        await msg.RequestUri!.ToString().Should().BeEqualTo("https://opencode.ai/zen/v1/chat/completions");
    }

    [Test]
    public async Task BuildRequest_TrimsTrailingSlashOnBaseUrl()
    {
        var adapter = new ChatCompletionsAdapter();
        using var msg = adapter.BuildRequest(Request(), Target("https://opencode.ai/zen/v1/"), null, "req1");

        await msg.RequestUri!.ToString().Should().BeEqualTo("https://opencode.ai/zen/v1/chat/completions");
        await msg.RequestUri.ToString().Should().NotContain("/v1/v1/");
    }

    [Test]
    public async Task BuildRequest_WithApiKey_SetsBearerAuthorization()
    {
        var adapter = new ChatCompletionsAdapter();
        using var msg = adapter.BuildRequest(Request(), Target(), "sk-test-123", "req1");

        await msg.Headers.Authorization.Should().NotBeNull();
        await msg.Headers.Authorization!.Scheme.Should().BeEqualTo("Bearer");
        await msg.Headers.Authorization.Parameter.Should().BeEqualTo("sk-test-123");
    }

    [Test]
    public async Task BuildRequest_WithoutApiKey_SendsBearerPublicForFreeTier()
    {
        var adapter = new ChatCompletionsAdapter();
        using var msg = adapter.BuildRequest(Request(), Target(), null, "req1");

        await msg.Headers.Authorization.Should().NotBeNull();
        await msg.Headers.Authorization!.Scheme.Should().BeEqualTo("Bearer");
        await msg.Headers.Authorization.Parameter.Should().BeEqualTo("public");
    }

    [Test]
    public async Task BuildRequest_AddsRequestIdHeader()
    {
        var adapter = new ChatCompletionsAdapter();
        using var msg = adapter.BuildRequest(Request(), Target(), null, "req-abc");

        await msg.Headers.TryGetValues("x-request-id", out var values).Should().BeTrue();
        await values!.First().Should().BeEqualTo("req-abc");
    }

    [Test]
    public async Task BuildRequest_SetsFreeTierHeadersMatchingOfficialClient()
    {
        var adapter = new ChatCompletionsAdapter();
        using var msg = adapter.BuildRequest(Request(stream: true), Target(), null, "req1");

        await msg.Headers.TryGetValues("User-Agent", out var ua).Should().BeTrue();
        await ua!.First().Should().BeEqualTo("opencode/1.18.31");

        await msg.Headers.TryGetValues("x-opencode-client", out var client).Should().BeTrue();
        await client!.First().Should().BeEqualTo("desktop");

        await msg.Headers.TryGetValues("x-opencode-session", out var session).Should().BeTrue();
        var sid = session!.First();
        await sid.Should().StartWith("ses_");
        await sid.Length.Should().BeEqualTo(4 + 12 + 14);
        var sidBody = sid[4..];
        await System.Text.RegularExpressions.Regex.IsMatch(sidBody[..12], "^[0-9a-f]{12}$").Should().BeTrue();
        await System.Text.RegularExpressions.Regex.IsMatch(sidBody[12..], "^[0-9A-Za-z]{14}$").Should().BeTrue();

        await msg.Headers.TryGetValues("x-opencode-request", out var reqId).Should().BeTrue();
        var rid = reqId!.First();
        await rid.Should().StartWith("msg_");
        await rid.Length.Should().BeEqualTo(4 + 12 + 14);
        var ridBody = rid[4..];
        await System.Text.RegularExpressions.Regex.IsMatch(ridBody[..12], "^[0-9a-f]{12}$").Should().BeTrue();
        await System.Text.RegularExpressions.Regex.IsMatch(ridBody[12..], "^[0-9A-Za-z]{14}$").Should().BeTrue();

        await msg.Headers.TryGetValues("x-opencode-project", out var project).Should().BeTrue();
        await project!.First().Should().BeEqualTo("global");

        await msg.Headers.TryGetValues("Accept", out var accept).Should().BeTrue();
        await accept!.First().Should().BeEqualTo("text/event-stream");

        await msg.Headers.TryGetValues("accept-language", out var al).Should().BeTrue();
        await al!.First().Should().BeEqualTo("*");

        await msg.Headers.TryGetValues("sec-fetch-mode", out var sfm).Should().BeTrue();
        await sfm!.First().Should().BeEqualTo("cors");
    }

    [Test]
    public async Task BuildRequest_NonStreaming_StillUsesEventStreamAccept()
    {
        var adapter = new ChatCompletionsAdapter();
        using var msg = adapter.BuildRequest(Request(stream: false), Target(), null, "req1");

        await msg.Headers.TryGetValues("Accept", out var accept).Should().BeTrue();
        await accept!.First().Should().BeEqualTo("text/event-stream");
    }

    [Test]
    public async Task BuildBody_FreeTierMatchesOfficialClientShape()
    {
        var adapter = new ChatCompletionsAdapter();
        using var msg = adapter.BuildRequest(Request(stream: true), Target(), null, "req1");

        var json = await msg.Content!.ReadAsStringAsync();
        await json.Should().Contain("\"tool_choice\":\"none\"");
        await json.Should().NotContain("permissions");
        await json.Should().NotContain("stream_options");

        await json.Should().Contain("\"name\":\"bash\"");
        await json.Should().Contain("\"name\":\"glob\"");
        await json.Should().Contain("\"name\":\"grep\"");
        await json.Should().Contain("\"name\":\"read\"");
        await json.Should().NotContain("\"name\":\"edit\"");
        await json.Should().NotContain("\"name\":\"write\"");

        await json.Should().Contain("This tool is currently unavailable and must not be used.");
    }

    [Test]
    public async Task BuildBody_SerializesModelMessagesAndSampling()
    {
        var req = Request(model: "big-pickle", maxTokens: 16, user: "agent") with
        {
            Temperature = 0.5,
            TopP = 0.9,
        };

        var body = ChatCompletionsAdapter.BuildBody(req);
        var json = JsonSerializer.Serialize(body);

        await json.Should().Contain("\"model\":\"big-pickle\"");
        await json.Should().Contain("\"role\":\"user\"");
        await json.Should().Contain("\"content\":\"Reply with OK.\"");
        await json.Should().Contain("\"temperature\":0.5");
        await json.Should().Contain("\"top_p\":0.9");
        await json.Should().Contain("\"max_tokens\":16");
        await json.Should().Contain("\"user\":\"agent\"");
    }

    [Test]
    public async Task BuildBody_IncludesStreamFlagOnlyWhenStreaming()
    {
        var streaming = ChatCompletionsAdapter.BuildBody(Request(stream: true));
        var nonStreaming = ChatCompletionsAdapter.BuildBody(Request(stream: false));

        await streaming.ContainsKey("stream").Should().BeTrue();
        await nonStreaming.ContainsKey("stream").Should().BeFalse();
    }

    [Test]
    public async Task BuildBody_OmitsOptionalFieldsWhenNull()
    {
        var body = ChatCompletionsAdapter.BuildBody(Request());

        await body.ContainsKey("temperature").Should().BeFalse();
        await body.ContainsKey("top_p").Should().BeFalse();
        await body.ContainsKey("max_tokens").Should().BeFalse();
        await body.ContainsKey("user").Should().BeFalse();
        await body.ContainsKey("tools").Should().BeFalse();
        await body.ContainsKey("stream").Should().BeFalse();
    }

    [Test]
    public async Task BuildBody_IncludesToolsAndToolChoiceWhenPresent()
    {
        var req = Request() with
        {
            Tools = [new NormalizedTool
            {
                Type = "function",
                Function = new NormalizedFunctionDef { Name = "get_weather", Description = "Get weather" },
            }],
            ToolChoice = "auto",
        };

        var json = JsonSerializer.Serialize(ChatCompletionsAdapter.BuildBody(req));
        await json.Should().Contain("get_weather");
        await json.Should().Contain("\"tool_choice\":\"auto\"");
    }

    [Test]
    public async Task BuildBody_IncludesResponseFormatWhenNotText()
    {
        var req = Request() with
        {
            ResponseFormatType = "json_object",
        };

        var json = JsonSerializer.Serialize(ChatCompletionsAdapter.BuildBody(req));
        await json.Should().Contain("\"response_format\"");
        await json.Should().Contain("json_object");
    }

    [Test]
    public async Task MaterializeAsync_ParsesContentUsageAndFinishReason()
    {
        var payload = """
            {
              "id": "chatcmpl-1",
              "model": "big-pickle",
              "choices": [
                {
                  "message": { "role": "assistant", "content": "OK" },
                  "finish_reason": "stop"
                }
              ],
              "usage": { "prompt_tokens": 5, "completion_tokens": 1 }
            }
            """;
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        var adapter = new ChatCompletionsAdapter();

        var result = await adapter.MaterializeAsync(response, CancellationToken.None);

        await result.Content.Should().BeEqualTo("OK");
        await result.FinishReason.Should().BeEqualTo("stop");
        await result.PromptTokens.Should().BeEqualTo(5);
        await result.CompletionTokens.Should().BeEqualTo(1);
        await result.Model.Should().BeEqualTo("big-pickle");
    }

    [Test]
    public async Task MaterializeAsync_ParsesToolCalls()
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
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        var adapter = new ChatCompletionsAdapter();

        var result = await adapter.MaterializeAsync(response, CancellationToken.None);

        await result.ToolCalls.Should().NotBeNull();
        await result.ToolCalls!.Count.Should().BeEqualTo(1);
        await result.ToolCalls[0].Id.Should().BeEqualTo("call_1");
        await result.ToolCalls[0].Function!.Name.Should().BeEqualTo("get_weather");
        await result.FinishReason.Should().BeEqualTo("tool_calls");
    }

    [Test]
    public async Task TranslateStreamAsync_EmitsTextDeltasAndDone()
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
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        };
        var adapter = new ChatCompletionsAdapter();

        var events = new List<NormalizedStreamEvent>();
        await foreach (var evt in adapter.TranslateStreamAsync(response, CancellationToken.None))
        {
            events.Add(evt);
        }

        await events.Count(e => e.Type == NormalizedStreamEventType.TextDelta).Should().BeEqualTo(2);
        var texts = events.Where(e => e.Type == NormalizedStreamEventType.TextDelta)
            .Select(e => e.Text)
            .ToList();
        await texts.Should().BeEquivalentTo(["He", "llo"]);
        await events.Last().Type.Should().BeEqualTo(NormalizedStreamEventType.Done);
        await events.Last().FinishReason.Should().BeEqualTo("stop");
    }

    [Test]
    public async Task TranslateStreamAsync_EmitsToolCallDeltas()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"call_1\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\"\"}}]}}]}",
            "",
            "data: [DONE]",
            "");
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        };
        var adapter = new ChatCompletionsAdapter();

        var events = new List<NormalizedStreamEvent>();
        await foreach (var evt in adapter.TranslateStreamAsync(response, CancellationToken.None))
        {
            events.Add(evt);
        }

        var toolEvents = events.Where(e => e.Type == NormalizedStreamEventType.ToolCallDelta).ToList();
        await toolEvents.Should().HaveCount(1);
        await toolEvents[0].ToolCallId.Should().BeEqualTo("call_1");
        await toolEvents[0].ToolName.Should().BeEqualTo("get_weather");
    }

    [Test]
    public async Task TranslateStreamAsync_EmitsUsageWhenPresent()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1}}",
            "",
            "data: [DONE]",
            "");
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        };
        var adapter = new ChatCompletionsAdapter();

        var events = new List<NormalizedStreamEvent>();
        await foreach (var evt in adapter.TranslateStreamAsync(response, CancellationToken.None))
        {
            events.Add(evt);
        }

        var usage = events.FirstOrDefault(e => e.Type == NormalizedStreamEventType.Usage);
        await usage.Should().NotBeNull();
        await usage!.PromptTokens.Should().BeEqualTo(3);
        await usage.CompletionTokens.Should().BeEqualTo(1);
    }
}
