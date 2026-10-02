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
        await body["tool_choice"]!.GetValue<string>().Should().BeEqualTo("none");
        await body["stream"]!.GetValue<bool>().Should().BeTrue();

        var tools = body["tools"]!.AsArray();
        await tools.Count.Should().BeEqualTo(4);
        var names = tools.Select(t => t!["function"]!["name"]!.GetValue<string>()).OrderBy(n => n);
        await string.Join(",", names).Should().BeEqualTo("bash,glob,grep,read");
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
}
