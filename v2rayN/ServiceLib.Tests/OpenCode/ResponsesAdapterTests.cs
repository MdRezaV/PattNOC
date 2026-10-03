namespace ServiceLib.Tests.OpenCode;

public class ResponsesAdapterTests
{
    [Test]
    public async Task MaterializeAsync_FunctionCallStream_EmitsSingleToolCallWithFullArguments()
    {
        // response.output_item.done repeats call_id, name, and the complete
        // arguments after added()/delta events already delivered the tool call.
        // Re-emitting them would duplicate the tool_use block downstream.
        var sse = string.Join("\n",
            "event: response.output_item.added",
            "data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"function_call\",\"call_id\":\"call_9\",\"name\":\"Bash\"}}",
            "",
            "event: response.function_call_arguments.delta",
            "data: {\"type\":\"response.function_call_arguments.delta\",\"call_id\":\"call_9\",\"delta\":\"{\\\"command\\\":\"}",
            "",
            "data: {\"type\":\"response.function_call_arguments.delta\",\"call_id\":\"call_9\",\"delta\":\"\\\"ls\\\"}\"}",
            "",
            "event: response.output_item.done",
            "data: {\"type\":\"response.output_item.done\",\"item\":{\"type\":\"function_call\",\"call_id\":\"call_9\",\"name\":\"Bash\",\"arguments\":\"{\\\"command\\\":\\\"ls\\\"}\"}}",
            "",
            "event: response.completed",
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":3,\"output_tokens\":5}}}",
            "");
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        };
        var adapter = new ResponsesAdapter();

        var result = await adapter.MaterializeAsync(response, CancellationToken.None);

        await result.ToolCalls.Should().NotBeNull();
        await result.ToolCalls!.Count.Should().BeEqualTo(1);
        await result.ToolCalls[0].Id.Should().BeEqualTo("call_9");
        await result.ToolCalls[0].Function!.Name.Should().BeEqualTo("Bash");
        await result.ToolCalls[0].Function!.Arguments.Should().BeEqualTo("{\"command\":\"ls\"}");
        await result.FinishReason.Should().BeEqualTo("completed");
        await result.PromptTokens.Should().BeEqualTo(3);
        await result.CompletionTokens.Should().BeEqualTo(5);
        await result.Content.Should().BeNull();
    }

    [Test]
    public async Task MaterializeAsync_TextStream_AggregatesDeltas()
    {
        var sse = string.Join("\n",
            "event: response.output_text.delta",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hi\"}",
            "",
            "event: response.output_text.delta",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\" there\"}",
            "",
            "event: response.completed",
            "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}",
            "");
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        };
        var adapter = new ResponsesAdapter();

        var result = await adapter.MaterializeAsync(response, CancellationToken.None);

        await result.Content.Should().BeEqualTo("Hi there");
        await result.FinishReason.Should().BeEqualTo("completed");
    }

    [Test]
    public async Task BuildRequest_WithApiKey_PreservesClientTools()
    {
        var req = new NormalizedCompletionRequest
        {
            Model = "muse-spark-1.3-contributor-free",
            Messages = [new NormalizedMessage { Role = "user", Content = "Reply with OK." }],
            Stream = true,
            Tools =
            [
                new NormalizedTool
                {
                    Function = new NormalizedFunctionDef { Name = "get_weather" },
                },
            ],
        };
        var target = new OpenCodeTargetItem
        {
            Id = "opencode-zen",
            Name = "OpenCode Zen",
            BaseUrl = "https://opencode.ai/zen/v1",
            KeyOptional = true,
            Enabled = true,
        };
        var adapter = new ResponsesAdapter();

        using var msg = adapter.BuildRequest(req, target, "sk-test-123", "req1");
        var json = await msg.Content!.ReadAsStringAsync();

        await json.Should().Contain("\"name\":\"get_weather\"");
        await json.Should().NotContain("\"name\":\"bash\"");
    }
}
