namespace ServiceLib.OpenCode.Adapters;

internal static class OpenCodeFreeTier
{
    private static readonly object _lock = new();

    internal const string UserAgent = "opencode/1.18.31";

    private static string GenerateId(string prefix)
    {
        var ms = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
        var hex = ms.ToString("x");
        hex = hex.Length >= 12 ? hex[^12..] : hex.PadLeft(12, '0');

        const string base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var bytes = RandomNumberGenerator.GetBytes(14);
        var suffix = new char[14];
        for (var i = 0; i < 14; i++)
        {
            suffix[i] = base62[bytes[i] % 62];
        }

        return $"{prefix}{hex}{new string(suffix)}";
    }

    public static string GenerateSessionId() => GenerateId("ses_");

    public static string GenerateRequestId() => GenerateId("msg_");

    internal static bool IsFreeTierTarget(OpenCodeTargetItem target)
    {
        return target.KeyOptional
               && target.BaseUrl.Contains("opencode.ai", StringComparison.OrdinalIgnoreCase);
    }

    internal static void ApplyHeaders(
        HttpRequestMessage msg,
        string? apiKey,
        string requestId,
        OpenCodeTargetItem target,
        bool stream)
    {
        var effectiveKey = apiKey;
        if (effectiveKey.IsNullOrEmpty() && target.KeyOptional)
        {
            effectiveKey = "public";
        }

        if (effectiveKey.IsNotEmpty())
        {
            msg.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", effectiveKey);
        }

        if (requestId.IsNotEmpty())
        {
            msg.Headers.TryAddWithoutValidation("x-request-id", requestId);
        }

        msg.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        msg.Headers.TryAddWithoutValidation("x-opencode-client", "desktop");
        msg.Headers.TryAddWithoutValidation("x-opencode-session", GenerateSessionId());
        msg.Headers.TryAddWithoutValidation("x-opencode-request", GenerateRequestId());
        msg.Headers.TryAddWithoutValidation("x-opencode-project", "global");
        // Free tier expects text/event-stream regardless of stream flag.
        msg.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        msg.Headers.TryAddWithoutValidation("accept-language", "*");
        msg.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
    }

    /// <summary>
    /// Fields third-party clients (Claude Code, SDKs, curl) inject but the official
    /// OpenCode desktop client never sends. The free tier fingerprints the whole
    /// envelope, not just the tools block, so any of these left in the body trips
    /// FreeTierError ("can only be used from within OpenCode"). Key names differ per
    /// API style — Chat Completions uses max_tokens, Responses uses max_output_tokens.
    /// </summary>
    private static readonly string[] ClientOnlyFields =
    [
        // sampling
        "temperature", "top_p", "top_k", "presence_penalty", "frequency_penalty",
        "seed", "logit_bias", "n", "logprobs", "top_logprobs",
        // length limits
        "max_tokens", "max_completion_tokens", "max_output_tokens",
        // identity / metadata
        "user", "metadata", "store",
        // tooling extras
        "parallel_tool_calls", "thinking", "response_format", "text",
        // permissions / stream plumbing
        "permissions", "stream_options",
        // stop sequences — parsed from OpenAI "stop" and Anthropic "stop_sequences",
        // but never sent by the official client, so they fingerprint as third-party.
        "stop",
    ];

    internal static IReadOnlyDictionary<string, string> ApplyBodyShape(
        Dictionary<string, object?> body, EOpenCodeApiStyle apiStyle)
    {
        // Preserve the caller's tools while satisfying the free-tier gate on the
        // lowercase file-search quartet. Returns the quartet rename map
        // (sent name → caller's name) so the response path can hand the agent
        // its own tool spellings back.
        var renamed = OpenCodeFingerprintTools.Apply(
            body, apiStyle == EOpenCodeApiStyle.Responses);

        // Zen rejects non-streaming requests on the free tier, so always stream
        // upstream even for non-streaming clients (answers are aggregated).
        body["stream"] = true;

        foreach (var key in ClientOnlyFields)
        {
            body.Remove(key);
        }

        return renamed;
    }
}
