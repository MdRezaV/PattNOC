namespace ServiceLib.OpenCode.Adapters;

public sealed class ResponsesAdapter : IUpstreamAdapter
{
    private static readonly JsonSerializerOptions _wireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public EOpenCodeApiStyle Style => EOpenCodeApiStyle.Responses;

    public HttpRequestMessage BuildRequest(
        NormalizedCompletionRequest req,
        OpenCodeTargetItem target,
        string? apiKey,
        string requestId)
    {
        var body = BuildBody(req);
        if (OpenCodeFreeTier.IsFreeTierTarget(target))
        {
            OpenCodeFreeTier.ApplyBodyShape(body, Style);
        }

        var url = OpenCodeUrl.Combine(target.BaseUrl, "/responses");
        var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, _wireOptions), Encoding.UTF8, "application/json"),
        };
        OpenCodeFreeTier.ApplyHeaders(msg, apiKey, requestId, target, req.Stream);
        return msg;
    }

    public async IAsyncEnumerable<NormalizedStreamEvent> TranslateStreamAsync(
        HttpResponseMessage response,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        string? currentEvent = null;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line[6..].Trim();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].TrimStart();
            if (payload.IsNullOrEmpty())
            {
                continue;
            }

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : currentEvent;

            switch (type)
            {
                case "response.output_text.delta":
                    if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
                    {
                        var text = delta.GetString();
                        if (text.IsNotEmpty())
                        {
                            yield return new NormalizedStreamEvent(
                                NormalizedStreamEventType.TextDelta, Text: text);
                        }
                    }
                    break;

                case "response.output_item.added":
                    if (root.TryGetProperty("item", out var item)
                        && item.TryGetProperty("type", out var itemType)
                        && itemType.ValueKind == JsonValueKind.String
                        && itemType.GetString() == "function_call")
                    {
                        var callId = item.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String
                            ? cid.GetString()
                            : null;
                        var name = item.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                            ? nm.GetString()
                            : null;
                        yield return new NormalizedStreamEvent(
                            NormalizedStreamEventType.ToolCallDelta,
                            ToolCallId: callId,
                            ToolName: name,
                            ArgumentsDelta: null);
                    }
                    break;

                case "response.output_text.done":
                    break;

                case "response.function_call_arguments.delta":
                    if (root.TryGetProperty("delta", out var argDelta) && argDelta.ValueKind == JsonValueKind.String)
                    {
                        var callId = root.TryGetProperty("call_id", out var ac) && ac.ValueKind == JsonValueKind.String
                            ? ac.GetString()
                            : null;
                        yield return new NormalizedStreamEvent(
                            NormalizedStreamEventType.ToolCallDelta,
                            ToolCallId: callId,
                            ArgumentsDelta: argDelta.GetString());
                    }
                    break;

                case "response.output_item.done":
                    if (root.TryGetProperty("item", out var doneItem)
                        && doneItem.TryGetProperty("type", out var doneType)
                        && doneType.ValueKind == JsonValueKind.String
                        && doneType.GetString() == "function_call")
                    {
                        var callId = doneItem.TryGetProperty("call_id", out var dc) && dc.ValueKind == JsonValueKind.String
                            ? dc.GetString()
                            : null;
                        var name = doneItem.TryGetProperty("name", out var dn) && dn.ValueKind == JsonValueKind.String
                            ? dn.GetString()
                            : null;
                        var args = doneItem.TryGetProperty("arguments", out var da) && da.ValueKind == JsonValueKind.String
                            ? da.GetString()
                            : null;
                        yield return new NormalizedStreamEvent(
                            NormalizedStreamEventType.ToolCallDelta,
                            ToolCallId: callId,
                            ToolName: name,
                            ArgumentsDelta: args);
                    }
                    break;

                case "response.completed":
                case "response.incomplete":
                case "response.failed":
                    if (root.TryGetProperty("response", out var resp))
                    {
                        var pt = resp.TryGetProperty("usage", out var usage)
                            && usage.TryGetProperty("input_tokens", out var it)
                            && it.TryGetInt32(out var ii) ? ii : (int?)null;
                        var ctok = resp.TryGetProperty("usage", out var usage2)
                            && usage2.TryGetProperty("output_tokens", out var ot)
                            && ot.TryGetInt32(out var oi) ? oi : (int?)null;
                        var finish = resp.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                            ? st.GetString()
                            : null;

                        if (pt is not null || ctok is not null)
                        {
                            yield return new NormalizedStreamEvent(
                                NormalizedStreamEventType.Usage, PromptTokens: pt, CompletionTokens: ctok);
                        }

                        if (type == "response.failed" || type == "response.incomplete")
                        {
                            var err = "Upstream response failed.";
                            if (resp.TryGetProperty("error", out var errObj)
                                && errObj.TryGetProperty("message", out var msg)
                                && msg.ValueKind == JsonValueKind.String)
                            {
                                err = msg.GetString() ?? err;
                            }

                            yield return new NormalizedStreamEvent(
                                NormalizedStreamEventType.Error, ErrorType: "server_error", ErrorMessage: err);
                            yield break;
                        }

                        yield return new NormalizedStreamEvent(
                            NormalizedStreamEventType.Done, FinishReason: finish);
                        yield break;
                    }
                    break;

                case "error":
                    var errorType = root.TryGetProperty("type", out var et) && et.ValueKind == JsonValueKind.String
                        ? et.GetString() ?? "server_error"
                        : "server_error";
                    var errorMessage = root.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String
                        ? em.GetString() ?? "Upstream error."
                        : "Upstream error.";
                    yield return new NormalizedStreamEvent(
                        NormalizedStreamEventType.Error, ErrorType: errorType, ErrorMessage: errorMessage);
                    yield break;
            }
        }
    }

    public async Task<NormalizedCompletionResponse> MaterializeAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var result = new NormalizedCompletionResponse
        {
            Id = root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            Model = root.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "",
        };

        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
        {
            result.FinishReason = status.GetString();
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var ii))
            {
                result.PromptTokens = ii;
            }

            if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var oi))
            {
                result.CompletionTokens = oi;
            }
        }

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            var contentParts = new List<string>();
            var toolCalls = new List<NormalizedToolCall>();

            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var itemType) || itemType.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var kind = itemType.GetString();
                if (kind == "message")
                {
                    if (item.TryGetProperty("content", out var contents) && contents.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in contents.EnumerateArray())
                        {
                            if (part.TryGetProperty("type", out var pType)
                                && pType.ValueKind == JsonValueKind.String
                                && pType.GetString() is "output_text" or "text"
                                && part.TryGetProperty("text", out var text)
                                && text.ValueKind == JsonValueKind.String)
                            {
                                var t = text.GetString();
                                if (t.IsNotEmpty())
                                {
                                    contentParts.Add(t);
                                }
                            }
                        }
                    }
                }
                else if (kind == "function_call")
                {
                    toolCalls.Add(new NormalizedToolCall
                    {
                        Id = item.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String
                            ? cid.GetString() ?? ""
                            : "",
                        Function = new NormalizedFunctionCall
                        {
                            Name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                                ? n.GetString() ?? ""
                                : "",
                            Arguments = item.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String
                                ? a.GetString()
                                : null,
                        },
                    });
                }
            }

            if (contentParts.Count > 0)
            {
                result.Content = string.Concat(contentParts);
            }

            if (toolCalls.Count > 0)
            {
                result.ToolCalls = toolCalls;
            }
        }

        return result;
    }

    internal static Dictionary<string, object?> BuildBody(NormalizedCompletionRequest req)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = req.Model,
            ["input"] = BuildInput(req.Messages),
        };

        if (req.Stream)
        {
            body["stream"] = true;
        }

        if (req.Temperature is not null)
        {
            body["temperature"] = req.Temperature;
        }

        if (req.TopP is not null)
        {
            body["top_p"] = req.TopP;
        }

        if (req.MaxTokens is not null)
        {
            body["max_output_tokens"] = req.MaxTokens;
        }

        if (req.User.IsNotEmpty())
        {
            body["user"] = req.User;
        }

        if (req.Tools is { Count: > 0 })
        {
            body["tools"] = req.Tools.Select(ToolToWire).ToList();
        }

        if (req.ToolChoice.IsNotEmpty())
        {
            body["tool_choice"] = req.ToolChoice;
        }

        if (req.ResponseFormatType.IsNotEmpty() && !req.ResponseFormatType.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            var format = new Dictionary<string, object?> { ["type"] = req.ResponseFormatType };
            if (req.ResponseFormatSchema is { } schema
                && req.ResponseFormatType.Equals("json_schema", StringComparison.OrdinalIgnoreCase))
            {
                format["schema"] = schema;
            }

            body["text"] = new Dictionary<string, object?> { ["format"] = format };
        }

        return body;
    }

    private static List<object?> BuildInput(List<NormalizedMessage> messages)
    {
        var input = new List<object?>(messages.Count);
        foreach (var m in messages)
        {
            if (m.ToolCalls is { Count: > 0 } && m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            {
                if (m.Content.IsNotEmpty())
                {
                    input.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["content"] = m.Content,
                    });
                }

                foreach (var tc in m.ToolCalls)
                {
                    input.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "function_call",
                        ["call_id"] = tc.Id,
                        ["name"] = tc.Function?.Name ?? "",
                        ["arguments"] = tc.Function?.Arguments ?? "",
                    });
                }

                continue;
            }

            if (m.ToolCallId.IsNotEmpty() && m.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
            {
                input.Add(new Dictionary<string, object?>
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = m.ToolCallId,
                    ["output"] = m.Content ?? "",
                });
                continue;
            }

            input.Add(new Dictionary<string, object?>
            {
                ["role"] = m.Role,
                ["content"] = m.Content ?? "",
            });
        }

        return input;
    }

    private static Dictionary<string, object?> ToolToWire(NormalizedTool t)
    {
        var fn = t.Function;
        return new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["name"] = fn?.Name ?? "",
            ["description"] = fn?.Description,
            ["parameters"] = fn?.Parameters,
        };
    }
}
