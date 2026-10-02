namespace ServiceLib.OpenCode;

public static class ClientFormat
{
    private static readonly JsonSerializerOptions _wireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Boxed JsonElement null survives WhenWritingNull so explicit "content": null is emitted.
    private static readonly JsonElement JsonNull = JsonSerializer.SerializeToElement<object?>(null);

    public static NormalizedCompletionRequest? ParseChatRequest(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var req = new NormalizedCompletionRequest
            {
                Model = root.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "",
                Stream = root.TryGetProperty("stream", out var stream)
                    && stream.ValueKind == JsonValueKind.True,
                Temperature = root.TryGetProperty("temperature", out var temp)
                    && temp.TryGetDouble(out var tv) ? tv : null,
                TopP = root.TryGetProperty("top_p", out var tp) && tp.TryGetDouble(out var tpv) ? tpv : null,
                MaxTokens = root.TryGetProperty("max_tokens", out var mt)
                    && mt.TryGetInt32(out var mtv) ? mtv : null,
                Stop = ParseStop(root),
                User = root.TryGetProperty("user", out var user) ? user.GetString() : null,
            };

            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                req = req with { Messages = ParseMessages(messages) };
            }

            if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                req = req with { Tools = ParseTools(tools) };
            }

            if (root.TryGetProperty("tool_choice", out var tc))
            {
                var choice = tc.ValueKind switch
                {
                    JsonValueKind.String => tc.GetString(),
                    JsonValueKind.Object when tc.TryGetProperty("type", out var tcType)
                        && tcType.ValueKind == JsonValueKind.String => ResolveToolChoiceObject(tc, tcType.GetString()),
                    _ => null,
                };
                if (choice.IsNotEmpty())
                {
                    req = req with { ToolChoice = choice };
                }
            }

            if (root.TryGetProperty("response_format", out var rf) && rf.ValueKind == JsonValueKind.Object)
            {
                var rfType = rf.TryGetProperty("type", out var rft) && rft.ValueKind == JsonValueKind.String
                    ? rft.GetString()
                    : null;
                JsonElement? schema = null;
                if (rf.TryGetProperty("json_schema", out var js) && js.ValueKind == JsonValueKind.Object)
                {
                    // OpenAI wraps schema in { name, schema, strict }; keep the whole object for upstream.
                    schema = js.Clone();
                }

                req = req with { ResponseFormatType = rfType, ResponseFormatSchema = schema };
            }

            return req;
        }
        catch
        {
            return null;
        }
    }

    public static NormalizedCompletionRequest? ParseResponsesRequest(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var req = new NormalizedCompletionRequest
            {
                Model = root.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "",
                Stream = root.TryGetProperty("stream", out var stream)
                    && stream.ValueKind == JsonValueKind.True,
                Temperature = root.TryGetProperty("temperature", out var temp)
                    && temp.TryGetDouble(out var tv) ? tv : null,
                TopP = root.TryGetProperty("top_p", out var tp) && tp.TryGetDouble(out var tpv) ? tpv : null,
                User = root.TryGetProperty("user", out var user) ? user.GetString() : null,
            };

            if (root.TryGetProperty("max_output_tokens", out var mot)
                && mot.TryGetInt32(out var motv))
            {
                req = req with { MaxTokens = motv };
            }

            if (root.TryGetProperty("input", out var input))
            {
                var messages = new List<NormalizedMessage>();
                if (input.ValueKind == JsonValueKind.String)
                {
                    messages.Add(new NormalizedMessage { Role = "user", Content = input.GetString() });
                }
                else if (input.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in input.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                            ? t.GetString()
                            : null;

                        if (type == "function_call")
                        {
                            messages.Add(new NormalizedMessage
                            {
                                Role = "assistant",
                                ToolCalls =
                                [
                                    new NormalizedToolCall
                                    {
                                        Id = item.TryGetProperty("call_id", out var cid)
                                            && cid.ValueKind == JsonValueKind.String
                                            ? cid.GetString() ?? ""
                                            : "",
                                        Function = new NormalizedFunctionCall
                                        {
                                            Name = item.TryGetProperty("name", out var n)
                                                && n.ValueKind == JsonValueKind.String
                                                ? n.GetString() ?? ""
                                                : "",
                                            Arguments = item.TryGetProperty("arguments", out var a)
                                                && a.ValueKind == JsonValueKind.String
                                                ? a.GetString()
                                                : null,
                                        },
                                    },
                                ],
                            });
                        }
                        else if (type == "function_call_output")
                        {
                            messages.Add(new NormalizedMessage
                            {
                                Role = "tool",
                                ToolCallId = item.TryGetProperty("call_id", out var oc)
                                    && oc.ValueKind == JsonValueKind.String
                                    ? oc.GetString()
                                    : null,
                                Content = item.TryGetProperty("output", out var outp)
                                    ? outp.ValueKind == JsonValueKind.String
                                        ? outp.GetString()
                                        : outp.GetRawText()
                                    : null,
                            });
                        }
                        else if (item.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String)
                        {
                            string? content = null;
                            if (item.TryGetProperty("content", out var c))
                            {
                                if (c.ValueKind == JsonValueKind.String)
                                {
                                    content = c.GetString();
                                }
                                else if (c.ValueKind == JsonValueKind.Array)
                                {
                                    var parts = new List<string>();
                                    foreach (var part in c.EnumerateArray())
                                    {
                                        if (part.TryGetProperty("text", out var pt) && pt.ValueKind == JsonValueKind.String)
                                        {
                                            var text = pt.GetString();
                                            if (text.IsNotEmpty())
                                            {
                                                parts.Add(text);
                                            }
                                        }
                                    }

                                    content = parts.Count > 0 ? string.Concat(parts) : null;
                                }
                            }

                            messages.Add(new NormalizedMessage
                            {
                                Role = role.GetString() ?? "user",
                                Content = content,
                            });
                        }
                    }
                }

                req = req with { Messages = messages };
            }

            if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                var list = new List<NormalizedTool>();
                foreach (var tool in tools.EnumerateArray())
                {
                    if (tool.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var name = tool.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? ""
                        : "";
                    JsonElement? parameters = null;
                    if (tool.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
                    {
                        parameters = p.Clone();
                    }

                    list.Add(new NormalizedTool
                    {
                        Function = new NormalizedFunctionDef
                        {
                            Name = name,
                            Description = tool.TryGetProperty("description", out var d)
                                && d.ValueKind == JsonValueKind.String
                                ? d.GetString()
                                : null,
                            Parameters = parameters,
                        },
                    });
                }

                if (list.Count > 0)
                {
                    req = req with { Tools = list };
                }
            }

            return req;
        }
        catch
        {
            return null;
        }
    }

    public static NormalizedCompletionRequest? ParseAnthropicRequest(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var req = new NormalizedCompletionRequest
            {
                Model = root.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "",
                Stream = root.TryGetProperty("stream", out var stream)
                    && stream.ValueKind == JsonValueKind.True,
                Temperature = root.TryGetProperty("temperature", out var temp)
                    && temp.TryGetDouble(out var tv) ? tv : null,
                TopP = root.TryGetProperty("top_p", out var tp) && tp.TryGetDouble(out var tpv) ? tpv : null,
                MaxTokens = root.TryGetProperty("max_tokens", out var mt)
                    && mt.TryGetInt32(out var mtv) ? mtv : null,
                Stop = ParseStop(root),
            };

            if (root.TryGetProperty("metadata", out var meta)
                && meta.TryGetProperty("user_id", out var uid)
                && uid.ValueKind == JsonValueKind.String)
            {
                req = req with { User = uid.GetString() };
            }

            var messages = new List<NormalizedMessage>();

            if (root.TryGetProperty("system", out var system))
            {
                var systemText = system.ValueKind switch
                {
                    JsonValueKind.String => system.GetString(),
                    JsonValueKind.Array => string.Concat(system.EnumerateArray()
                        .Where(b => b.TryGetProperty("type", out var bt) && bt.ValueKind == JsonValueKind.String
                            && bt.GetString() == "text"
                            && b.TryGetProperty("text", out var btx) && btx.ValueKind == JsonValueKind.String)
                        .Select(b => b.GetProperty("text").GetString())),
                    _ => null,
                };
                if (systemText.IsNotEmpty())
                {
                    messages.Add(new NormalizedMessage { Role = "system", Content = systemText });
                }
            }

            if (root.TryGetProperty("messages", out var anthMessages) && anthMessages.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in anthMessages.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var role = m.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                        ? r.GetString() ?? "user"
                        : "user";
                    if (!m.TryGetProperty("content", out var content))
                    {
                        continue;
                    }

                    if (content.ValueKind == JsonValueKind.String)
                    {
                        messages.Add(new NormalizedMessage { Role = role, Content = content.GetString() });
                        continue;
                    }

                    if (content.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    string? text = null;
                    var toolCalls = new List<NormalizedToolCall>();
                    var toolResults = new List<NormalizedMessage>();

                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.ValueKind != JsonValueKind.Object
                            || !block.TryGetProperty("type", out var bt)
                            || bt.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var blockType = bt.GetString();
                        switch (blockType)
                        {
                            case "text":
                                if (block.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String
                                    && tx.GetString().IsNotEmpty())
                                {
                                    text = text is null ? tx.GetString() : text + tx.GetString();
                                }
                                break;

                            case "tool_use":
                                toolCalls.Add(new NormalizedToolCall
                                {
                                    Id = block.TryGetProperty("id", out var tuid)
                                        && tuid.ValueKind == JsonValueKind.String
                                        ? tuid.GetString() ?? ""
                                        : "",
                                    Function = new NormalizedFunctionCall
                                    {
                                        Name = block.TryGetProperty("name", out var tun)
                                            && tun.ValueKind == JsonValueKind.String
                                            ? tun.GetString() ?? ""
                                            : "",
                                        Arguments = block.TryGetProperty("input", out var tui)
                                            ? AnthropicInputToArguments(tui)
                                            : "{}",
                                    },
                                });
                                break;

                            case "tool_result":
                                toolResults.Add(new NormalizedMessage
                                {
                                    Role = "tool",
                                    ToolCallId = block.TryGetProperty("tool_use_id", out var trid)
                                        && trid.ValueKind == JsonValueKind.String
                                        ? trid.GetString()
                                        : null,
                                    Content = block.TryGetProperty("content", out var trc)
                                        ? AnthropicContentToString(trc)
                                        : null,
                                });
                                break;
                        }
                    }

                    // OpenAI-style tool results precede any remaining user text.
                    messages.AddRange(toolResults);
                    if (text is not null || toolCalls.Count > 0 || role == "assistant")
                    {
                        messages.Add(new NormalizedMessage
                        {
                            Role = role,
                            Content = text,
                            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
                        });
                    }
                }

                req = req with { Messages = messages };
            }

            if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                var list = new List<NormalizedTool>();
                foreach (var tool in tools.EnumerateArray())
                {
                    if (tool.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var name = tool.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? ""
                        : "";
                    JsonElement? parameters = null;
                    if (tool.TryGetProperty("input_schema", out var schema) && schema.ValueKind == JsonValueKind.Object)
                    {
                        parameters = schema.Clone();
                    }

                    if (name.IsNullOrEmpty())
                    {
                        continue;
                    }

                    list.Add(new NormalizedTool
                    {
                        Function = new NormalizedFunctionDef
                        {
                            Name = name,
                            Description = tool.TryGetProperty("description", out var d)
                                && d.ValueKind == JsonValueKind.String
                                ? d.GetString()
                                : null,
                            Parameters = parameters,
                        },
                    });
                }

                if (list.Count > 0)
                {
                    req = req with { Tools = list };
                }
            }

            if (root.TryGetProperty("tool_choice", out var tc))
            {
                if (tc.ValueKind == JsonValueKind.String)
                {
                    req = req with { ToolChoice = tc.GetString() };
                }
                else if (tc.ValueKind == JsonValueKind.Object
                    && tc.TryGetProperty("type", out var tcType)
                    && tcType.ValueKind == JsonValueKind.String)
                {
                    var choice = tcType.GetString() switch
                    {
                        "auto" => "auto",
                        "any" => "required",
                        "none" => "none",
                        "tool" => tc.TryGetProperty("name", out var tn) && tn.ValueKind == JsonValueKind.String
                            ? tn.GetString()
                            : null,
                        _ => null,
                    };
                    if (choice.IsNotEmpty())
                    {
                        req = req with { ToolChoice = choice };
                    }
                }
            }

            return req;
        }
        catch
        {
            return null;
        }
    }

    private static string AnthropicInputToArguments(JsonElement input)
    {
        return input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : input.GetRawText();
    }

    /// <summary>
    /// Reads OpenAI "stop" or Anthropic "stop_sequences" (string or string[]) into a list.
    /// </summary>
    private static List<string>? ParseStop(JsonElement root)
    {
        if (!root.TryGetProperty("stop", out var stop)
            && !root.TryGetProperty("stop_sequences", out stop))
        {
            return null;
        }

        List<string>? list = stop.ValueKind switch
        {
            JsonValueKind.String when stop.GetString().IsNotEmpty() => [stop.GetString()!],
            JsonValueKind.Array => stop.EnumerateArray()
                .Where(s => s.ValueKind == JsonValueKind.String && s.GetString().IsNotEmpty())
                .Select(s => s.GetString()!)
                .ToList() is { Count: > 0 } items ? items : null,
            _ => null,
        };

        return list;
    }

    /// <summary>
    /// Resolves an OpenAI tool_choice object to the normalized bare form:
    /// {type:function, function:{name}} → name; otherwise the type string (auto/required/none).
    /// </summary>
    private static string? ResolveToolChoiceObject(JsonElement tc, string? typeName)
    {
        if (typeName.Equals("function", StringComparison.OrdinalIgnoreCase)
            && tc.TryGetProperty("function", out var fn)
            && fn.TryGetProperty("name", out var fnName)
            && fnName.ValueKind == JsonValueKind.String
            && fnName.GetString().IsNotEmpty())
        {
            return fnName.GetString();
        }

        return typeName;
    }

    private static string? AnthropicContentToString(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("type", out var bt) && bt.ValueKind == JsonValueKind.String
                    && bt.GetString() == "text"
                    && block.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String
                    && tx.GetString().IsNotEmpty())
                {
                    parts.Add(tx.GetString()!);
                }
            }

            return parts.Count > 0 ? string.Concat(parts) : content.GetRawText();
        }

        return content.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : content.GetRawText();
    }

    public static string WriteChatCompletion(NormalizedCompletionResponse response, string? modelRef)
    {
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var toolCalls = response.ToolCalls?.Select(tc => new Dictionary<string, object?>
        {
            ["id"] = tc.Id,
            ["type"] = tc.Type,
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = tc.Function?.Name ?? "",
                ["arguments"] = tc.Function?.Arguments ?? "",
            },
        }).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["id"] = response.Id.IsNullOrEmpty() ? $"chatcmpl-{Guid.NewGuid().ToString("N")[..12]}" : response.Id,
            ["object"] = "chat.completion",
            ["created"] = created,
            ["model"] = modelRef ?? response.Model,
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["message"] = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        // OpenAI serializes assistant tool-call turns with explicit null content.
                        ["content"] = response.Content is not null ? response.Content : JsonNull,
                        ["tool_calls"] = toolCalls,
                    },
                    ["finish_reason"] = response.FinishReason ?? (toolCalls is { Count: > 0 } ? "tool_calls" : "stop"),
                },
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["prompt_tokens"] = response.PromptTokens ?? 0,
                ["completion_tokens"] = response.CompletionTokens ?? 0,
                ["total_tokens"] = (response.PromptTokens ?? 0) + (response.CompletionTokens ?? 0),
            },
        };

        return JsonSerializer.Serialize(payload, _wireOptions);
    }

    public static string WriteResponses(NormalizedCompletionResponse response, string? modelRef)
    {
        var output = new List<object?>();
        var text = response.Content;
        if (text.IsNotEmpty())
        {
            output.Add(new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["role"] = "assistant",
                ["content"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "output_text",
                        ["text"] = text,
                    },
                },
            });
        }

        if (response.ToolCalls is { Count: > 0 })
        {
            foreach (var tc in response.ToolCalls)
            {
                output.Add(new Dictionary<string, object?>
                {
                    ["type"] = "function_call",
                    ["call_id"] = tc.Id,
                    ["name"] = tc.Function?.Name ?? "",
                    ["arguments"] = tc.Function?.Arguments ?? "",
                });
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["id"] = response.Id.IsNullOrEmpty() ? $"resp_{Guid.NewGuid().ToString("N")[..12]}" : response.Id,
            ["object"] = "response",
            ["model"] = modelRef ?? response.Model,
            ["status"] = response.FinishReason ?? "completed",
            ["output"] = (object?)output,
            ["usage"] = new Dictionary<string, object?>
            {
                ["input_tokens"] = response.PromptTokens ?? 0,
                ["output_tokens"] = response.CompletionTokens ?? 0,
            },
        };

        return JsonSerializer.Serialize(payload, _wireOptions);
    }

    public static string WriteAnthropicMessage(NormalizedCompletionResponse response, string? modelRef)
    {
        var content = new List<object?>();
        if (response.Content.IsNotEmpty())
        {
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = response.Content,
            });
        }

        if (response.ToolCalls is { Count: > 0 })
        {
            foreach (var tc in response.ToolCalls)
            {
                content.Add(new Dictionary<string, object?>
                {
                    ["type"] = "tool_use",
                    ["id"] = tc.Id.IsNullOrEmpty() ? $"toolu_{Guid.NewGuid():N}"[..28] : tc.Id,
                    ["name"] = tc.Function?.Name ?? "",
                    ["input"] = ParseArgumentsObject(tc.Function?.Arguments),
                });
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["id"] = response.Id.IsNullOrEmpty() || !response.Id.StartsWith("msg_", StringComparison.Ordinal)
                ? $"msg_{Guid.NewGuid().ToString("N")[..24]}"
                : response.Id,
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = modelRef ?? response.Model,
            ["content"] = content,
            ["stop_reason"] = MapAnthropicStopReason(response.FinishReason, response.ToolCalls is { Count: > 0 }),
            ["stop_sequence"] = null,
            ["usage"] = new Dictionary<string, object?>
            {
                ["input_tokens"] = response.PromptTokens ?? 0,
                ["output_tokens"] = response.CompletionTokens ?? 0,
            },
        };

        return Serialize(payload);
    }

    public static string WriteAnthropicError(OpenCodeError error)
    {
        return Serialize(new Dictionary<string, object?>
        {
            ["type"] = "error",
            ["error"] = new Dictionary<string, object?>
            {
                ["type"] = error.Type.IsNullOrEmpty() || error.Type == "server_error"
                    ? "api_error"
                    : error.Type,
                ["message"] = error.Message,
                ["param"] = error.Param,
                ["code"] = error.Code,
            },
        });
    }

    private static object ParseArgumentsObject(string? arguments)
    {
        if (arguments.IsNotEmpty())
        {
            try
            {
                using var doc = JsonDocument.Parse(arguments);
                return doc.RootElement.Clone();
            }
            catch
            {
                // Fall through to empty object.
            }
        }

        return new Dictionary<string, object?>();
    }

    private static string MapAnthropicStopReason(string? finishReason, bool hasToolCalls)
    {
        return finishReason switch
        {
            "tool_calls" or "tool_use" => "tool_use",
            "length" => "max_tokens",
            "stop" or "end_turn" => "end_turn",
            null or "" => hasToolCalls ? "tool_use" : "end_turn",
            _ => "end_turn",
        };
    }

    public static string WriteChatChunk(
        string? id,
        string? modelRef,
        NormalizedStreamEvent evt,
        long created)
    {
        var delta = new Dictionary<string, object?>();
        string? finishReason = null;

        switch (evt.Type)
        {
            case NormalizedStreamEventType.TextDelta:
                delta["role"] = "assistant";
                delta["content"] = evt.Text;
                break;
            case NormalizedStreamEventType.ToolCallDelta:
                var toolCalls = new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["index"] = 0,
                        ["id"] = evt.ToolCallId,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = evt.ToolName,
                            ["arguments"] = evt.ArgumentsDelta ?? "",
                        },
                    },
                };
                delta["tool_calls"] = toolCalls;
                break;
            case NormalizedStreamEventType.Done:
                finishReason = evt.FinishReason ?? "stop";
                break;
        }

        var payload = new Dictionary<string, object?>
        {
            ["id"] = id ?? $"chatcmpl-{Guid.NewGuid().ToString("N")[..12]}",
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = modelRef,
            ["choices"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = finishReason,
                },
            },
        };

        if (evt.Type == NormalizedStreamEventType.Usage)
        {
            payload["usage"] = new Dictionary<string, object?>
            {
                ["prompt_tokens"] = evt.PromptTokens ?? 0,
                ["completion_tokens"] = evt.CompletionTokens ?? 0,
                ["total_tokens"] = (evt.PromptTokens ?? 0) + (evt.CompletionTokens ?? 0),
            };
        }

        return JsonSerializer.Serialize(payload, _wireOptions);
    }

    public static (string EventName, string Json) WriteResponsesEvent(
        string? id,
        string? modelRef,
        NormalizedStreamEvent evt)
    {
        switch (evt.Type)
        {
            case NormalizedStreamEventType.TextDelta:
                return (
                    "response.output_text.delta",
                    Serialize(new Dictionary<string, object?>
                    {
                        ["type"] = "response.output_text.delta",
                        ["delta"] = evt.Text ?? "",
                    }));

            case NormalizedStreamEventType.ToolCallDelta:
                if (evt.ToolName.IsNotEmpty())
                {
                    return (
                        "response.output_item.added",
                        Serialize(new Dictionary<string, object?>
                        {
                            ["type"] = "response.output_item.added",
                            ["output_index"] = 0,
                            ["item"] = new Dictionary<string, object?>
                            {
                                ["type"] = "function_call",
                                ["call_id"] = evt.ToolCallId,
                                ["name"] = evt.ToolName,
                                ["arguments"] = "",
                            },
                        }));
                }

                return (
                    "response.function_call_arguments.delta",
                    Serialize(new Dictionary<string, object?>
                    {
                        ["type"] = "response.function_call_arguments.delta",
                        ["call_id"] = evt.ToolCallId,
                        ["delta"] = evt.ArgumentsDelta ?? "",
                    }));

            case NormalizedStreamEventType.Usage:
                return (
                    "response.completed",
                    Serialize(new Dictionary<string, object?>
                    {
                        ["type"] = "response.completed",
                        ["response"] = new Dictionary<string, object?>
                        {
                            ["id"] = id,
                            ["object"] = "response",
                            ["model"] = modelRef,
                            ["status"] = "completed",
                            ["usage"] = new Dictionary<string, object?>
                            {
                                ["input_tokens"] = evt.PromptTokens ?? 0,
                                ["output_tokens"] = evt.CompletionTokens ?? 0,
                            },
                        },
                    }));

            case NormalizedStreamEventType.Done:
                return (
                    "response.completed",
                    Serialize(new Dictionary<string, object?>
                    {
                        ["type"] = "response.completed",
                        ["response"] = new Dictionary<string, object?>
                        {
                            ["id"] = id,
                            ["object"] = "response",
                            ["model"] = modelRef,
                            ["status"] = "completed",
                        },
                    }));

            case NormalizedStreamEventType.Error:
                return (
                    "error",
                    Serialize(new Dictionary<string, object?>
                    {
                        ["type"] = evt.ErrorType ?? "server_error",
                        ["message"] = evt.ErrorMessage ?? "Upstream error.",
                    }));

            default:
                return ("response.completed", "{}");
        }
    }

    public static string WriteModels(IEnumerable<OpenCodeModel> models, string targetId)
    {
        var data = models.Select(m => new Dictionary<string, object?>
        {
            ["id"] = m.Id,
            ["object"] = "model",
            ["owned_by"] = targetId,
            ["created"] = 0,
            ["display_name"] = m.DisplayName,
            ["api_style"] = m.ApiStyle.ToString(),
        }).ToList();

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["object"] = "list",
            ["data"] = data,
        }, _wireOptions);
    }

    public static string WriteModel(OpenCodeModel model, string targetId)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = model.Id,
            ["object"] = "model",
            ["owned_by"] = targetId,
            ["display_name"] = model.DisplayName,
            ["api_style"] = model.ApiStyle.ToString(),
        }, _wireOptions);
    }

    public static string WriteError(OpenCodeError error) => ErrorClassification.ToOpenAiErrorJson(error);

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload, _wireOptions);

    private static List<NormalizedMessage> ParseMessages(JsonElement messages)
    {
        var list = new List<NormalizedMessage>();
        foreach (var m in messages.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var role = m.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? "user"
                : "user";
            string? content = null;
            if (m.TryGetProperty("content", out var c))
            {
                content = c.ValueKind switch
                {
                    JsonValueKind.String => c.GetString(),
                    JsonValueKind.Array => string.Concat(c.EnumerateArray()
                        .Where(p => p.TryGetProperty("text", out var pt) && pt.ValueKind == JsonValueKind.String)
                        .Select(p => p.GetProperty("text").GetString())),
                    _ => null,
                };
            }

            var msg = new NormalizedMessage
            {
                Role = role,
                Content = content,
                ToolCallId = m.TryGetProperty("tool_call_id", out var tcid) && tcid.ValueKind == JsonValueKind.String
                    ? tcid.GetString()
                    : null,
                Name = m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : null,
            };

            if (m.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                var calls = new List<NormalizedToolCall>();
                foreach (var tc in tcs.EnumerateArray())
                {
                    if (tc.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var call = new NormalizedToolCall
                    {
                        Id = tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                            ? id.GetString() ?? ""
                            : "",
                    };
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        call = call with
                        {
                            Function = new NormalizedFunctionCall
                            {
                                Name = fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                                    ? nm.GetString() ?? ""
                                    : "",
                                Arguments = fn.TryGetProperty("arguments", out var a)
                                    && a.ValueKind == JsonValueKind.String
                                    ? a.GetString()
                                    : null,
                            },
                        };
                    }

                    calls.Add(call);
                }

                msg = msg with { ToolCalls = calls.Count > 0 ? calls : null };
            }

            list.Add(msg);
        }

        return list;
    }

    private static List<NormalizedTool>? ParseTools(JsonElement tools)
    {
        var list = new List<NormalizedTool>();
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            JsonElement? parameters = null;
            string? description = null;
            var name = "";

            if (tool.TryGetProperty("function", out var fn))
            {
                name = fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? ""
                    : "";
                description = fn.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()
                    : null;
                if (fn.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    parameters = p.Clone();
                }
            }
            else
            {
                name = tool.TryGetProperty("name", out var n2) && n2.ValueKind == JsonValueKind.String
                    ? n2.GetString() ?? ""
                    : "";
                if (tool.TryGetProperty("parameters", out var p2) && p2.ValueKind == JsonValueKind.Object)
                {
                    parameters = p2.Clone();
                }
            }

            if (name.IsNullOrEmpty())
            {
                continue;
            }

            list.Add(new NormalizedTool
            {
                Function = new NormalizedFunctionDef
                {
                    Name = name,
                    Description = description,
                    Parameters = parameters,
                },
            });
        }

        return list.Count > 0 ? list : null;
    }
}

/// <summary>
/// Emits Anthropic Messages API SSE events from normalized stream events.
/// </summary>
public sealed class AnthropicStreamWriter
{
    private static readonly JsonSerializerOptions _wireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _modelRef;
    private readonly string _messageId = $"msg_{Guid.NewGuid().ToString("N")[..24]}";
    private bool _messageStarted;
    private int _blockIndex = -1;
    private string? _blockType;
    private int? _promptTokens;
    private int? _completionTokens;

    public AnthropicStreamWriter(string? modelRef)
    {
        _modelRef = modelRef ?? "";
    }

    public IReadOnlyList<(string EventName, string Json)> Write(NormalizedStreamEvent evt)
    {
        var events = new List<(string, string)>();
        switch (evt.Type)
        {
            case NormalizedStreamEventType.Usage:
                _promptTokens = evt.PromptTokens ?? _promptTokens;
                _completionTokens = evt.CompletionTokens ?? _completionTokens;
                break;

            case NormalizedStreamEventType.TextDelta:
                EnsureStarted(events);
                if (_blockType != "text")
                {
                    CloseBlock(events);
                    OpenTextBlock(events);
                }
                events.Add(("content_block_delta", Serialize(new Dictionary<string, object?>
                {
                    ["type"] = "content_block_delta",
                    ["index"] = _blockIndex,
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["type"] = "text_delta",
                        ["text"] = evt.Text ?? "",
                    },
                })));
                break;

            case NormalizedStreamEventType.ToolCallDelta:
                EnsureStarted(events);
                if (evt.ToolName.IsNotEmpty())
                {
                    CloseBlock(events);
                    OpenToolBlock(events, evt);
                }

                if (evt.ArgumentsDelta is not null)
                {
                    if (_blockType != "tool_use")
                    {
                        CloseBlock(events);
                        OpenToolBlock(events, evt);
                    }

                    events.Add(("content_block_delta", Serialize(new Dictionary<string, object?>
                    {
                        ["type"] = "content_block_delta",
                        ["index"] = _blockIndex,
                        ["delta"] = new Dictionary<string, object?>
                        {
                            ["type"] = "input_json_delta",
                            ["partial_json"] = evt.ArgumentsDelta,
                        },
                    })));
                }
                break;

            case NormalizedStreamEventType.Done:
                EnsureStarted(events);
                var hadToolBlock = _blockType == "tool_use";
                CloseBlock(events);
                var stopReason = MapStopReason(evt.FinishReason, hadToolBlock);
                events.Add(("message_delta", Serialize(new Dictionary<string, object?>
                {
                    ["type"] = "message_delta",
                    ["delta"] = new Dictionary<string, object?>
                    {
                        ["stop_reason"] = stopReason,
                        ["stop_sequence"] = null,
                    },
                    ["usage"] = new Dictionary<string, object?>
                    {
                        ["input_tokens"] = _promptTokens ?? 0,
                        ["output_tokens"] = _completionTokens ?? 0,
                    },
                })));
                events.Add(("message_stop", Serialize(new Dictionary<string, object?>
                {
                    ["type"] = "message_stop",
                })));
                break;

            case NormalizedStreamEventType.Error:
                events.Add(("error", ClientFormat.WriteAnthropicError(new OpenCodeError(
                    evt.ErrorType ?? "api_error",
                    evt.ErrorMessage ?? "Upstream error."))));
                break;
        }

        return events;
    }

    private void EnsureStarted(List<(string, string)> events)
    {
        if (_messageStarted)
        {
            return;
        }

        _messageStarted = true;
        events.Add(("message_start", Serialize(new Dictionary<string, object?>
        {
            ["type"] = "message_start",
            ["message"] = new Dictionary<string, object?>
            {
                ["id"] = _messageId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = _modelRef,
                ["content"] = new List<object?>(),
                ["stop_reason"] = null,
                ["stop_sequence"] = null,
                ["usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = _promptTokens ?? 0,
                    ["output_tokens"] = _completionTokens ?? 0,
                },
            },
        })));
    }

    private void CloseBlock(List<(string, string)> events)
    {
        if (_blockType is null)
        {
            return;
        }

        events.Add(("content_block_stop", Serialize(new Dictionary<string, object?>
        {
            ["type"] = "content_block_stop",
            ["index"] = _blockIndex,
        })));
        _blockType = null;
    }

    private void OpenTextBlock(List<(string, string)> events)
    {
        _blockType = "text";
        _blockIndex++;
        events.Add(("content_block_start", Serialize(new Dictionary<string, object?>
        {
            ["type"] = "content_block_start",
            ["index"] = _blockIndex,
            ["content_block"] = new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = "",
            },
        })));
    }

    private void OpenToolBlock(List<(string, string)> events, NormalizedStreamEvent evt)
    {
        _blockType = "tool_use";
        _blockIndex++;
        var toolId = evt.ToolCallId.IsNotEmpty()
            ? evt.ToolCallId!
            : $"toolu_{Guid.NewGuid().ToString("N")[..24]}";
        events.Add(("content_block_start", Serialize(new Dictionary<string, object?>
        {
            ["type"] = "content_block_start",
            ["index"] = _blockIndex,
            ["content_block"] = new Dictionary<string, object?>
            {
                ["type"] = "tool_use",
                ["id"] = toolId,
                ["name"] = evt.ToolName ?? "",
                ["input"] = new Dictionary<string, object?>(),
            },
        })));
    }

    private static string MapStopReason(string? finishReason, bool hadToolBlock)
    {
        return finishReason switch
        {
            "tool_calls" or "tool_use" => "tool_use",
            "length" => "max_tokens",
            "stop" or "end_turn" => "end_turn",
            null or "" => hadToolBlock ? "tool_use" : "end_turn",
            _ => "end_turn",
        };
    }

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload, _wireOptions);
}
