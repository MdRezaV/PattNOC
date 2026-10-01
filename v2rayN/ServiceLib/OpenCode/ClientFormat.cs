namespace ServiceLib.OpenCode;

public static class ClientFormat
{
    private static readonly JsonSerializerOptions _wireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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

            if (root.TryGetProperty("tool_choice", out var tc) && tc.ValueKind == JsonValueKind.String)
            {
                req = req with { ToolChoice = tc.GetString() };
            }
            else if (root.TryGetProperty("tool_choice", out var tcObj)
                && tcObj.ValueKind == JsonValueKind.Object
                && tcObj.TryGetProperty("type", out var tcType)
                && tcType.ValueKind == JsonValueKind.String)
            {
                req = req with { ToolChoice = tcType.GetString() };
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
                        ["content"] = response.Content,
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
