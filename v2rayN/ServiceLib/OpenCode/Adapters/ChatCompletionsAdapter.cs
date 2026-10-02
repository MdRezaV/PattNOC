namespace ServiceLib.OpenCode.Adapters;

public sealed class ChatCompletionsAdapter : IUpstreamAdapter
{
    private static readonly JsonSerializerOptions _wireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Boxed JsonElement null survives WhenWritingNull so assistant tool-call messages
    // serialize with explicit "content": null as the OpenAI spec shows.
    private static readonly JsonElement JsonNull = JsonSerializer.SerializeToElement<object?>(null);

    public EOpenCodeApiStyle Style => EOpenCodeApiStyle.ChatCompletions;

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

        var url = OpenCodeUrl.Combine(target.BaseUrl, "/chat/completions");
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
        string? finishReason = null;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (line.IsNullOrEmpty())
            {
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].TrimStart();
            if (payload.Equals("[DONE]", StringComparison.Ordinal))
            {
                yield return new NormalizedStreamEvent(
                    NormalizedStreamEventType.Done, FinishReason: finishReason);
                yield break;
            }

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                {
                    finishReason = fr.GetString();
                }

                if (!choice.TryGetProperty("delta", out var delta))
                {
                    continue;
                }

                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var text = content.GetString();
                    if (text.IsNotEmpty())
                    {
                        yield return new NormalizedStreamEvent(
                            NormalizedStreamEventType.TextDelta, Text: text);
                    }
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls)
                    && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        var id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString()
                            : null;
                        string? name = null;
                        string? argsDelta = null;
                        if (tc.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                            {
                                name = nameEl.GetString();
                            }

                            if (fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                            {
                                argsDelta = argsEl.GetString();
                            }
                        }

                        yield return new NormalizedStreamEvent(
                            NormalizedStreamEventType.ToolCallDelta,
                            ToolCallId: id,
                            ToolName: name,
                            ArgumentsDelta: argsDelta);
                    }
                }
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var pt = usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pi) ? pi : (int?)null;
                var ctok = usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var ci) ? ci : (int?)null;
                if (pt is not null || ctok is not null)
                {
                    yield return new NormalizedStreamEvent(
                        NormalizedStreamEventType.Usage, PromptTokens: pt, CompletionTokens: ctok);
                }
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

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            {
                result.FinishReason = fr.GetString();
            }

            if (choice.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var content))
                {
                    result.Content = content.ValueKind == JsonValueKind.String
                        ? content.GetString()
                        : null;
                }

                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<NormalizedToolCall>();
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        var call = new NormalizedToolCall
                        {
                            Id = tc.TryGetProperty("id", out var tcid) && tcid.ValueKind == JsonValueKind.String
                                ? tcid.GetString() ?? ""
                                : "",
                        };
                        if (tc.TryGetProperty("function", out var fn))
                        {
                            call = new NormalizedToolCall
                            {
                                Id = call.Id,
                                Type = "function",
                                Function = new NormalizedFunctionCall
                                {
                                    Name = fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                                        ? n.GetString() ?? ""
                                        : "",
                                    Arguments = fn.TryGetProperty("arguments", out var a)
                                        && a.ValueKind == JsonValueKind.String
                                        ? a.GetString()
                                        : null,
                                },
                            };
                        }

                        list.Add(call);
                    }

                    result.ToolCalls = list.Count > 0 ? list : null;
                }
            }
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pi))
            {
                result.PromptTokens = pi;
            }

            if (usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var ci))
            {
                result.CompletionTokens = ci;
            }
        }

        return result;
    }

    internal static Dictionary<string, object?> BuildBody(NormalizedCompletionRequest req)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = req.Model,
            ["messages"] = req.Messages.Select(MessageToWire).ToList(),
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
            body["max_tokens"] = req.MaxTokens;
        }

        if (req.Stop is { Count: > 0 })
        {
            body["stop"] = req.Stop;
        }

        if (req.User.IsNotEmpty())
        {
            body["user"] = req.User;
        }

        if (req.Tools is { Count: > 0 })
        {
            body["tools"] = req.Tools.Select(ToolToWire).ToList();
        }

        var toolChoice = ToolChoiceToWire(req.ToolChoice);
        if (toolChoice is not null)
        {
            body["tool_choice"] = toolChoice;
        }

        if (req.ResponseFormatType.IsNotEmpty() && !req.ResponseFormatType.Equals("text", StringComparison.OrdinalIgnoreCase))
        {
            var format = new Dictionary<string, object?> { ["type"] = req.ResponseFormatType };
            if (req.ResponseFormatSchema is { } schema
                && req.ResponseFormatType.Equals("json_schema", StringComparison.OrdinalIgnoreCase))
            {
                format["json_schema"] = schema;
            }

            body["response_format"] = format;
        }

        return body;
    }

    /// <summary>
    /// OpenAI Chat Completions tool_choice: auto/none/required stay strings;
    /// a named tool must be {"type":"function","function":{"name":"..."}}.
    /// </summary>
    internal static object? ToolChoiceToWire(string? toolChoice)
    {
        if (toolChoice.IsNullOrEmpty())
        {
            return null;
        }

        if (toolChoice.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || toolChoice.Equals("none", StringComparison.OrdinalIgnoreCase)
            || toolChoice.Equals("required", StringComparison.OrdinalIgnoreCase))
        {
            return toolChoice;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?> { ["name"] = toolChoice },
        };
    }

    private static Dictionary<string, object?> MessageToWire(NormalizedMessage m)
    {
        var wire = new Dictionary<string, object?>
        {
            ["role"] = m.Role,
        };

        var isTool = m.Role.Equals("tool", StringComparison.OrdinalIgnoreCase);
        var hasToolCalls = m.ToolCalls is { Count: > 0 };

        if (isTool)
        {
            // OpenAI requires tool_call_id and content on every tool message.
            wire["tool_call_id"] = m.ToolCallId ?? "";
            wire["content"] = m.Content ?? "";
        }
        else if (hasToolCalls && m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
        {
            wire["content"] = m.Content is not null ? m.Content : JsonNull;
        }
        else if (m.Content is not null)
        {
            wire["content"] = m.Content;
        }

        if (m.Name.IsNotEmpty())
        {
            wire["name"] = m.Name;
        }

        if (hasToolCalls)
        {
            wire["tool_calls"] = m.ToolCalls!.Select(tc => new Dictionary<string, object?>
            {
                ["id"] = tc.Id,
                ["type"] = tc.Type.IsNullOrEmpty() ? "function" : tc.Type,
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tc.Function?.Name ?? "",
                    ["arguments"] = tc.Function?.Arguments ?? "",
                },
            }).ToList();
        }

        return wire;
    }

    private static Dictionary<string, object?> ToolToWire(NormalizedTool t)
    {
        var fn = t.Function;
        return new Dictionary<string, object?>
        {
            ["type"] = t.Type,
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = fn?.Name ?? "",
                ["description"] = fn?.Description,
                ["parameters"] = fn?.Parameters,
            },
        };
    }

}
