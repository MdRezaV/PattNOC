namespace ServiceLib.OpenCode.Adapters;

/// <summary>
/// Folds a normalized upstream SSE stream into a single completion response.
/// Both upstream adapters force body["stream"] = true, so non-streaming client
/// requests are answered from SSE rather than a JSON body.
/// </summary>
internal static class StreamAggregator
{
    public static bool IsEventStream(HttpResponseMessage response)
    {
        return string.Equals(
            response.Content.Headers.ContentType?.MediaType,
            "text/event-stream",
            StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<NormalizedCompletionResponse> AggregateAsync(
        IAsyncEnumerable<NormalizedStreamEvent> events,
        CancellationToken ct)
    {
        var text = new StringBuilder();
        var calls = new List<NormalizedToolCall>();
        var result = new NormalizedCompletionResponse();
        string? error = null;
        var sawDone = false;

        await foreach (var evt in events.WithCancellation(ct))
        {
            switch (evt.Type)
            {
                case NormalizedStreamEventType.TextDelta:
                    text.Append(evt.Text);
                    break;

                case NormalizedStreamEventType.ToolCallDelta:
                    AppendToolCall(calls, evt);
                    break;

                case NormalizedStreamEventType.Usage:
                    if (evt.PromptTokens is not null)
                    {
                        result.PromptTokens = evt.PromptTokens;
                    }
                    if (evt.CompletionTokens is not null)
                    {
                        result.CompletionTokens = evt.CompletionTokens;
                    }
                    break;

                case NormalizedStreamEventType.Done:
                    sawDone = true;
                    result.FinishReason = evt.FinishReason ?? result.FinishReason;
                    break;

                case NormalizedStreamEventType.Error:
                    error = evt.ErrorMessage ?? "Upstream error.";
                    break;
            }
        }

        if (error is not null)
        {
            throw new OpenCodeStreamException(error);
        }

        if (!sawDone && text.Length == 0 && calls.Count == 0)
        {
            throw new OpenCodeStreamException(
                "Upstream stream ended without a completion event.");
        }

        if (text.Length > 0)
        {
            result.Content = text.ToString();
        }
        if (calls.Count > 0)
        {
            result.ToolCalls = calls;
        }
        return result;
    }

    private static void AppendToolCall(List<NormalizedToolCall> calls, NormalizedStreamEvent evt)
    {
        var index = -1;
        if (evt.ToolCallId.IsNotEmpty())
        {
            index = calls.FindIndex(c => c.Id == evt.ToolCallId);
        }
        if (index < 0 && evt.ToolCallId.IsNullOrEmpty() && calls.Count > 0)
        {
            index = calls.Count - 1;
        }

        if (index < 0)
        {
            calls.Add(new NormalizedToolCall
            {
                Id = evt.ToolCallId ?? "",
                Function = new NormalizedFunctionCall
                {
                    Name = evt.ToolName ?? "",
                    Arguments = evt.ArgumentsDelta,
                },
            });
            return;
        }

        var call = calls[index];
        var name = evt.ToolName.IsNotEmpty() ? evt.ToolName! : call.Function?.Name ?? "";
        var args = (call.Function?.Arguments ?? "") + (evt.ArgumentsDelta ?? "");
        calls[index] = call with
        {
            Function = new NormalizedFunctionCall
            {
                Name = name,
                Arguments = args.Length > 0 ? args : null,
            },
        };
    }
}
