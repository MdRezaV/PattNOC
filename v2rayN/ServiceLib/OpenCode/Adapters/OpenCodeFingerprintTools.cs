namespace ServiceLib.OpenCode.Adapters;

/// <summary>
/// Free-tier client fingerprint for the tools block — C# port of 9Router's
/// open-sse/utils/opencodeFingerprint.js.
///
/// The upstream free-tier gate requires the lowercase file-search quartet
/// (bash/glob/grep/read). Agent clients declare the same tools in their own
/// casing (Claude Code sends Bash/Read/Glob/Grep) and a case-variant pair is
/// rejected upstream as a duplicate, so quartet names are canonicalised on the
/// way out and the caller's spelling is restored on the way back. Everything
/// outside the quartet passes through verbatim — the agent's own tools stay in
/// the request, otherwise it can never invoke them.
/// </summary>
internal static class OpenCodeFingerprintTools
{
    /// <summary>Canonical names required by the upstream free-tier gate.</summary>
    internal static readonly string[] Quartet = ["bash", "glob", "grep", "read"];

    internal const string UnavailableDescription =
        "This tool is currently unavailable and must not be used.";

    /// <summary>Canonical lowercase name when <paramref name="name"/> is a quartet member; null otherwise.</summary>
    internal static string? FingerprintToolKey(string? name)
    {
        var lower = name?.Trim().ToLowerInvariant();
        return lower is not null && Array.IndexOf(Quartet, lower) >= 0 ? lower : null;
    }

    /// <summary>Reads a tool name from either flat ({name}) or chat ({function:{name}}) shape.</summary>
    private static string ToolNameOf(Dictionary<string, object?> tool)
    {
        if (tool.TryGetValue("name", out var flat) && flat is string name && name.Trim().Length > 0)
        {
            return name.Trim();
        }

        if (tool.TryGetValue("function", out var fn) && fn is Dictionary<string, object?> fnDict
            && fnDict.TryGetValue("name", out var nested) && nested is string nestedName && nestedName.Trim().Length > 0)
        {
            return nestedName.Trim();
        }

        return "";
    }

    /// <summary>Copy-on-write rename so the caller's request body is never mutated.</summary>
    private static Dictionary<string, object?> WithToolName(Dictionary<string, object?> tool, string name)
    {
        var copy = new Dictionary<string, object?>(tool);

        if (copy.TryGetValue("name", out _))
        {
            copy["name"] = name;
        }

        if (copy.TryGetValue("function", out var fn) && fn is Dictionary<string, object?> fnDict)
        {
            var fnCopy = new Dictionary<string, object?>(fnDict) { ["name"] = name };
            copy["function"] = fnCopy;
        }

        return copy;
    }

    private static bool HasClientTools(object? toolsValue)
    {
        if (toolsValue is not System.Collections.IEnumerable enumerable || toolsValue is string)
        {
            return false;
        }

        foreach (var _ in enumerable)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Canonicalises only the fingerprint quartet and removes duplicate quartet
    /// variants. Non-fingerprint tools are preserved verbatim, including tools
    /// whose names differ only by case — they are outside OpenCode's
    /// fingerprint contract.
    /// </summary>
    /// <returns>map: sent name → original name</returns>
    internal static (List<object?> Tools, Dictionary<string, string> Map) Conceal(object? toolsValue)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var tools = new List<object?>();
        if (toolsValue is not System.Collections.IEnumerable enumerable || toolsValue is string)
        {
            return (tools, map);
        }

        var seenQuartet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in enumerable)
        {
            if (entry is not Dictionary<string, object?> tool)
            {
                tools.Add(entry);
                continue;
            }

            var current = ToolNameOf(tool);
            var key = FingerprintToolKey(current);
            if (key is null)
            {
                tools.Add(tool);
                continue;
            }

            // `Bash` + `bash` is rejected upstream as a duplicate. Keep exactly
            // one declaration for each quartet member.
            if (!seenQuartet.Add(key))
            {
                continue;
            }

            if (current == key)
            {
                tools.Add(tool);
                continue;
            }

            map[key] = current;
            tools.Add(WithToolName(tool, key));
        }

        return (tools, map);
    }

    private static Dictionary<string, object?> DecoyParameters() =>
        new()
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>(),
        };

    private static Dictionary<string, object?> FlatDecoy(string name) =>
        new()
        {
            ["type"] = "function",
            ["name"] = name,
            ["description"] = UnavailableDescription,
            ["parameters"] = DecoyParameters(),
        };

    private static Dictionary<string, object?> ChatDecoy(string name) =>
        new()
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["description"] = UnavailableDescription,
                ["parameters"] = DecoyParameters(),
            },
        };

    /// <summary>Appends only genuinely missing quartet declarations.</summary>
    internal static List<object?> AppendMissing(List<object?> tools, bool flat)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in tools)
        {
            if (entry is not Dictionary<string, object?> tool)
            {
                continue;
            }

            var key = FingerprintToolKey(ToolNameOf(tool));
            if (key is not null)
            {
                present.Add(key);
            }
        }

        foreach (var name in Quartet)
        {
            if (present.Contains(name))
            {
                continue;
            }

            tools.Add(flat ? FlatDecoy(name) : ChatDecoy(name));
        }

        return tools;
    }

    /// <summary>Points an explicit tool_choice at a quartet member after canonicalisation.</summary>
    private static void RetargetToolChoice(Dictionary<string, object?> body, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0
            || !body.TryGetValue("tool_choice", out var choice)
            || choice is not Dictionary<string, object?> choiceDict)
        {
            return;
        }

        // Responses shape: {type:"function", name}.
        if (choiceDict.TryGetValue("name", out var flatName) && flatName is string name
            && FingerprintToolKey(name) is string key && map.ContainsKey(key))
        {
            choiceDict["name"] = key;
            return;
        }

        // Chat shape: {type:"function", function:{name}}.
        if (choiceDict.TryGetValue("function", out var fn) && fn is Dictionary<string, object?> fnDict
            && fnDict.TryGetValue("name", out var nested) && nested is string nestedName
            && FingerprintToolKey(nestedName) is string nestedKey && map.ContainsKey(nestedKey))
        {
            fnDict["name"] = nestedKey;
        }
    }

    /// <summary>
    /// Full request-side pass: canonicalise quartet case variants, remove
    /// duplicate quartet declarations, append missing members and preserve the
    /// tool_choice defaults used by the OpenCode free tier.
    /// </summary>
    /// <returns>map: sent name → original name</returns>
    internal static IReadOnlyDictionary<string, string> Apply(Dictionary<string, object?> body, bool flat)
    {
        body.TryGetValue("tools", out var existing);
        var hadClientTools = HasClientTools(existing);

        var (tools, map) = Conceal(existing);
        body["tools"] = AppendMissing(tools, flat);
        RetargetToolChoice(body, map);

        // Responses uses auto when the fingerprint helper supplies tools; chat
        // requests with no caller tools use none so the injected decoys cannot be
        // selected. A caller that brought its own tools keeps the model's normal
        // default — forcing "none" would silently disable the agent's tools.
        if (!body.ContainsKey("tool_choice"))
        {
            if (flat)
            {
                body["tool_choice"] = "auto";
            }
            else if (!hadClientTools)
            {
                body["tool_choice"] = "none";
            }
        }

        return map;
    }

    /// <summary>Restores the caller's tool spelling; a no-op when nothing was renamed.</summary>
    internal static string? Restore(IReadOnlyDictionary<string, string>? map, string? name)
    {
        if (map is not { Count: > 0 } || name is null)
        {
            return name;
        }

        return map.TryGetValue(name, out var original) ? original : name;
    }

    /// <summary>
    /// Response side of the rename: every tool call the upstream answered with is
    /// handed back under the name the agent declared, so it still recognises its
    /// own call. Applying it twice is harmless — map keys are canonical, restored
    /// names are not.
    /// </summary>
    internal static void RestoreResponseToolNames(
        IReadOnlyDictionary<string, string>? map,
        NormalizedCompletionResponse response)
    {
        if (map is not { Count: > 0 } || response.ToolCalls is null)
        {
            return;
        }

        response.ToolCalls = response.ToolCalls
            .Select(tc => tc.Function is null
                ? tc
                : tc with
                {
                    Function = new NormalizedFunctionCall
                    {
                        Name = Restore(map, tc.Function.Name) ?? tc.Function.Name,
                        Arguments = tc.Function.Arguments,
                    },
                })
            .ToList();
    }
}
