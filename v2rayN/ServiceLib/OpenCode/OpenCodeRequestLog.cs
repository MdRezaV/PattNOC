namespace ServiceLib.OpenCode;

public sealed record OpenCodeLogEntry(
    string Phase,
    string? RequestId = null,
    string? Client = null,
    string? TargetId = null,
    string? ModelId = null,
    EOpenCodeApiStyle? Style = null,
    string? Profile = null,
    int? Attempt = null,
    int? HttpStatus = null,
    EOpenCodeConnectivityState? State = null,
    bool? Ok = null,
    long? LatencyMs = null,
    string? Detail = null);

/// <summary>
/// Single funnel for every OpenCode request/result log line. Executor attempts,
/// outcomes, stream summaries, gateway client traffic, and model tests all write
/// through here so no execution path can bypass logging. Failures in logging
/// itself are swallowed — logging must never fail a request.
/// </summary>
public static class OpenCodeRequestLog
{
    internal const string Tag = "OpenCode.Req";

    /// <summary>Test hook: receives every formatted line before it hits NLog.</summary>
    internal static Action<string>? TestSink;

    public static void Write(OpenCodeLogEntry entry)
    {
        try
        {
            var line = Format(entry);
            TestSink?.Invoke(line);
            Logging.SaveLog(line);
        }
        catch
        {
            // Swallow: a logging failure must never propagate into request handling.
        }
    }

    internal static string Format(OpenCodeLogEntry e)
    {
        var parts = new List<string> { Tag, $"phase={e.Phase}" };

        void Add(string name, string? value)
        {
            if (value.IsNotEmpty())
            {
                parts.Add($"{name}={value}");
            }
        }

        Add("rid", e.RequestId);
        Add("client", e.Client);
        Add("target", e.TargetId);
        Add("model", e.ModelId);
        if (e.Style is not null)
        {
            parts.Add($"style={e.Style}");
        }

        Add("profile", e.Profile);
        if (e.Attempt is not null)
        {
            parts.Add($"attempt={e.Attempt}");
        }

        if (e.HttpStatus is not null)
        {
            parts.Add($"status={e.HttpStatus}");
        }

        if (e.State is not null)
        {
            parts.Add($"state={e.State}");
        }

        if (e.Ok is not null)
        {
            parts.Add($"ok={e.Ok.Value.ToString().ToLowerInvariant()}");
        }

        if (e.LatencyMs is not null)
        {
            parts.Add($"latencyMs={e.LatencyMs}");
        }

        Add("detail", e.Detail is null ? null : $"\"{Truncate(e.Detail, 2000).Replace("\"", "'")}\"");
        return string.Join(' ', parts);
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value[..max] + $"…({value.Length} chars)";
    }
}
