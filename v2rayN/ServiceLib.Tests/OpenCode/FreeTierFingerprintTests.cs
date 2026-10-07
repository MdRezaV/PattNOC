namespace ServiceLib.Tests.OpenCode;

/// <summary>
/// The in-app connection test only ever sends one plain user message, so it cannot
/// catch a field that Claude Code (or any SDK) injects and the free tier rejects.
/// These tests pin the gateway's outbound body to the shape the connection test
/// proves is accepted.
/// </summary>
public class FreeTierFingerprintTests
{
    private static OpenCodeTargetItem Target() => new()
    {
        Id = "opencode-free",
        Name = "OpenCode Free",
        BaseUrl = "https://opencode.ai/zen/v1",
        KeyOptional = true,
        Enabled = true,
    };

    // Claude Code's Anthropic Messages payload: system prompt, real tools, length
    // limit, sampling, identity metadata, and stop sequences — none of which the
    // official OpenCode desktop client sends.
    private const string ClaudeCodeBody = """
        {
          "model": "big-pickle",
          "max_tokens": 32000,
          "temperature": 0.7,
          "top_p": 0.95,
          "stream": true,
          "system": "You are terse. Always answer briefly.",
          "stop_sequences": ["STOP"],
          "metadata": { "user_id": "user_abc123" },
          "tools": [
            {
              "name": "Bash",
              "description": "Run a shell command",
              "input_schema": { "type": "object", "properties": { "command": { "type": "string" } } }
            },
            {
              "name": "Read",
              "description": "Read a file",
              "input_schema": { "type": "object", "properties": { "path": { "type": "string" } } }
            }
          ],
          "messages": [
            { "role": "user", "content": "Reply with OK." }
          ]
        }
        """;

    private static NormalizedCompletionRequest ConnectivityTestRequest() => new()
    {
        Model = "big-pickle",
        Messages = [new NormalizedMessage { Role = "user", Content = "Reply with OK." }],
        MaxTokens = 16,
        Stream = true,
    };

    private static async Task<JsonObject> BuildWireBody(NormalizedCompletionRequest request)
    {
        var adapter = new ChatCompletionsAdapter();
        using var msg = adapter.BuildRequest(request, Target(), null, "req1");
        var json = await msg.Content!.ReadAsStringAsync();
        return JsonNode.Parse(json)!.AsObject();
    }

    private static JsonObject WithoutVariableFields(JsonObject body)
    {
        var copy = (JsonObject)body.DeepClone();
        copy.Remove("model");
        copy.Remove("messages");
        // The tools block is caller-specific: the connectivity test sends none, an
        // agent sends its own (the quartet is canonicalised but descriptions and
        // extra tools survive). tool_choice follows from that, so both are pinned
        // by separate tests — the envelope itself must still match.
        copy.Remove("tools");
        copy.Remove("tool_choice");
        return copy;
    }

    [Test]
    public async Task ClaudeCodeShapedRequest_MatchesConnectivityTestBody()
    {
        var parsed = ClientFormat.ParseAnthropicRequest(ClaudeCodeBody);
        await parsed.Should().NotBeNull();

        var claudeBody = await BuildWireBody(parsed!);
        var testBody = await BuildWireBody(ConnectivityTestRequest());

        var claudeResidual = WithoutVariableFields(claudeBody);
        var testResidual = WithoutVariableFields(testBody);

        // Comparing serialized forms so a mismatch prints both envelopes.
        await claudeResidual.ToJsonString().Should().BeEqualTo(testResidual.ToJsonString());
    }

    [Test]
    public async Task ClaudeCodeShapedRequest_LeavesNoThirdPartyFields()
    {
        var parsed = ClientFormat.ParseAnthropicRequest(ClaudeCodeBody);
        await parsed.Should().NotBeNull();
        var body = await BuildWireBody(parsed!);

        // Fields the official client never sends must not survive into the wire body.
        var leaked = new[]
            {
                "temperature", "top_p", "max_tokens", "stop",
                "user", "metadata", "stop_sequences",
            }
            .Where(body.ContainsKey)
            .ToList();
        await string.Join(",", leaked).Should().BeEqualTo("");

        // "user" survives only as a message role, never as a top-level identity field.
        // The caller brought its own tools, so no tool_choice is forced.
        await body.ContainsKey("tool_choice").Should().BeFalse();
        await body["stream"]!.GetValue<bool>().Should().BeTrue();

        // Claude Code's Bash/Read are canonicalised to bash/read rather than
        // duplicated, glob/grep are appended, and the caller's descriptions —
        // not the unavailable decoys — survive.
        var tools = body["tools"]!.AsArray();
        await tools.Count.Should().BeEqualTo(4);
        var names = tools.Select(t => t!["function"]!["name"]!.GetValue<string>()).OrderBy(n => n);
        await string.Join(",", names).Should().BeEqualTo("bash,glob,grep,read");
        await body.ToJsonString().Should().Contain("Run a shell command");
    }

    [Test]
    public async Task ClaudeCodeShapedRequest_KeepsFingerprintHeaders()
    {
        var parsed = ClientFormat.ParseAnthropicRequest(ClaudeCodeBody);
        var adapter = new ChatCompletionsAdapter();
        using var msg = adapter.BuildRequest(parsed!, Target(), null, "req1");

        await msg.Headers.Authorization!.Parameter.Should().BeEqualTo("public");
        await msg.Headers.TryGetValues("x-opencode-client", out var client).Should().BeTrue();
        await client!.First().Should().BeEqualTo("desktop");
        await msg.Headers.TryGetValues("User-Agent", out var ua).Should().BeTrue();
        await ua!.First().Should().BeEqualTo("opencode/1.18.31");
    }

    [Test]
    public async Task FreeTier_QuartetCaseDuplicates_CollapseToSingleCanonicalName()
    {
        // `Bash` + `bash` is rejected upstream as a duplicate, so both collapse to
        // one canonical declaration instead of doubling the tools block.
        var req = new NormalizedCompletionRequest
        {
            Model = "big-pickle",
            Messages = [new NormalizedMessage { Role = "user", Content = "Reply with OK." }],
            Stream = true,
            Tools =
            [
                new NormalizedTool
                {
                    Type = "function",
                    Function = new NormalizedFunctionDef { Name = "Bash", Description = "caller shell" },
                },
                new NormalizedTool
                {
                    Type = "function",
                    Function = new NormalizedFunctionDef { Name = "bash", Description = "second shell" },
                },
            ],
        };

        var body = await BuildWireBody(req);
        var names = body["tools"]!.AsArray()
            .Select(t => t!["function"]!["name"]!.GetValue<string>())
            .OrderBy(n => n);
        await string.Join(",", names).Should().BeEqualTo("bash,glob,grep,read");
    }

    [Test]
    public async Task FreeTier_ExplicitToolChoiceOnRenamedQuartet_IsRetargeted()
    {
        // The caller forces its own `Bash`; the wire request must name the
        // canonical `bash` and leave every other explicit choice untouched.
        var req = new NormalizedCompletionRequest
        {
            Model = "big-pickle",
            Messages = [new NormalizedMessage { Role = "user", Content = "Reply with OK." }],
            Stream = true,
            ToolChoice = "Bash",
            Tools =
            [
                new NormalizedTool
                {
                    Type = "function",
                    Function = new NormalizedFunctionDef { Name = "Bash", Description = "caller shell" },
                },
                new NormalizedTool
                {
                    Type = "function",
                    Function = new NormalizedFunctionDef { Name = "get_weather", Description = "Get weather" },
                },
            ],
        };

        var body = await BuildWireBody(req);
        await body["tool_choice"]!["function"]!["name"]!.GetValue<string>().Should().BeEqualTo("bash");
    }

    [Test]
    public async Task FreeTier_ExplicitToolChoiceOnOwnTool_IsPreserved()
    {
        var req = new NormalizedCompletionRequest
        {
            Model = "big-pickle",
            Messages = [new NormalizedMessage { Role = "user", Content = "Reply with OK." }],
            Stream = true,
            ToolChoice = "get_weather",
            Tools =
            [
                new NormalizedTool
                {
                    Type = "function",
                    Function = new NormalizedFunctionDef { Name = "get_weather", Description = "Get weather" },
                },
            ],
        };

        var body = await BuildWireBody(req);
        await body["tool_choice"]!["function"]!["name"]!.GetValue<string>().Should().BeEqualTo("get_weather");
    }

    [Test]
    public async Task FreeTier_RestoresCallerToolSpelling_OnMaterializedToolCalls()
    {
        // The model answers with the canonical `bash`; the agent declared `Bash`
        // and must get its own spelling back so it recognises the call.
        var req = new NormalizedCompletionRequest
        {
            Model = "big-pickle",
            Messages = [new NormalizedMessage { Role = "user", Content = "Run ls." }],
            Stream = false,
            Tools =
            [
                new NormalizedTool
                {
                    Type = "function",
                    Function = new NormalizedFunctionDef { Name = "Bash", Description = "Run a shell command" },
                },
            ],
        };

        var adapter = new ChatCompletionsAdapter();
        using var wire = adapter.BuildRequest(req, Target(), null, "req1");

        var upstream = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"chatcmpl-1","model":"big-pickle","choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"bash","arguments":"{\"command\":\"ls\"}"}}]}}]}""",
                Encoding.UTF8,
                "application/json"),
        };

        var result = await adapter.MaterializeAsync(upstream, CancellationToken.None);
        await result.ToolCalls!.Count.Should().BeEqualTo(1);
        await result.ToolCalls[0].Function!.Name.Should().BeEqualTo("Bash");
        await result.ToolCalls[0].Function.Arguments.Should().BeEqualTo("{\"command\":\"ls\"}");
    }
}
