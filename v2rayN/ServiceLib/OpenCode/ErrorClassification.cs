namespace ServiceLib.OpenCode;

public static class ErrorClassification
{
    private static readonly JsonSerializerOptions _errorJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static (EOpenCodeConnectivityState State, OpenCodeError Error) Classify(
        int? httpStatus,
        string? body,
        Exception? exception,
        bool proxyUnavailable)
    {
        if (proxyUnavailable)
        {
            return (EOpenCodeConnectivityState.ClientRestricted,
                new OpenCodeError("server_error", "No active v2rayN proxy route is available.", 503, null, "client_restricted"));
        }

        if (httpStatus is >= 200 and < 300)
        {
            return (EOpenCodeConnectivityState.OpenCodeAccepted,
                new OpenCodeError("", "Accepted", httpStatus));
        }

        var bodyLower = body?.ToLowerInvariant() ?? "";

        switch (httpStatus)
        {
            case 400:
                if (bodyLower.Contains("unsupported") || bodyLower.Contains("tool") || bodyLower.Contains("schema"))
                {
                    return (EOpenCodeConnectivityState.UnsupportedRequest,
                        new OpenCodeError("invalid_request_error", Truncate(body, 300) ?? "Unsupported request.", 400));
                }

                return (EOpenCodeConnectivityState.UnsupportedRequest,
                    new OpenCodeError("invalid_request_error", Truncate(body, 300) ?? "Invalid request.", 400));

            case 401:
                return (EOpenCodeConnectivityState.AuthenticationFailed,
                    new OpenCodeError("authentication_error", Truncate(body, 300) ?? "Authentication failed. An optional API key may be required upstream.", 401));

            case 403:
                return (EOpenCodeConnectivityState.AuthorizationFailed,
                    new OpenCodeError("permission_error", Truncate(body, 300) ?? "Not authorized for this model or resource.", 403));

            case 404:
                return (EOpenCodeConnectivityState.ModelNotFound,
                    new OpenCodeError("not_found_error", Truncate(body, 300) ?? "Model or endpoint not found.", 404, null, "model_not_found"));

            case 408:
                return (EOpenCodeConnectivityState.Timeout,
                    new OpenCodeError("server_error", "Upstream request timed out.", 408, null, "timeout"));

            case 429:
                if (bodyLower.Contains("free usage limit") || bodyLower.Contains("free_usage_limit")
                    || bodyLower.Contains("usage_limit") || bodyLower.Contains("free usage"))
                {
                    return (EOpenCodeConnectivityState.FreeUsageLimit,
                        new OpenCodeError("rate_limit_error", Truncate(body, 300) ?? "Free usage limit reached.", 429, null, "free_usage_limit"));
                }

                return (EOpenCodeConnectivityState.RateLimited,
                    new OpenCodeError("rate_limit_error", Truncate(body, 300) ?? "Rate limited.", 429, null, "rate_limit_exceeded"));

            case >= 500:
                return (EOpenCodeConnectivityState.ProviderError,
                    new OpenCodeError("server_error", Truncate(body, 300) ?? "Upstream provider error.", httpStatus, null, "upstream_error"));
        }

        if (exception is TimeoutException or TaskCanceledException
            || (exception is HttpRequestException hre && hre.InnerException is TimeoutException))
        {
            return (EOpenCodeConnectivityState.Timeout,
                new OpenCodeError("server_error", "Request timed out.", null, null, "timeout"));
        }

        if (exception is HttpRequestException or SocketException or IOException)
        {
            return (EOpenCodeConnectivityState.NetworkError,
                new OpenCodeError("server_error", "Network error reaching the upstream endpoint.", null, null, "network_error"));
        }

        // A parseable transport with an unusable payload: the endpoint answered but
        // not with a completion this adapter understands (in-band error, bad JSON,
        // wrong response shape). Treat as a provider-side failure so a different
        // request format can still be tried for this model.
        if (exception is OpenCodeStreamException or JsonException)
        {
            return (EOpenCodeConnectivityState.ProviderError,
                new OpenCodeError("server_error", Truncate(exception.Message, 300) ?? "Unreadable upstream response.", null, null, "invalid_upstream_response"));
        }

        if (httpStatus is null && exception is null)
        {
            return (EOpenCodeConnectivityState.Unknown,
                new OpenCodeError("server_error", "Unknown gateway failure.", null, null, "unknown_error"));
        }

        return (EOpenCodeConnectivityState.Unknown,
            new OpenCodeError("server_error", Truncate(body, 300) ?? exception?.Message ?? "Unknown error.", httpStatus, null, "unknown_error"));
    }

    public static string ToOpenAiErrorJson(OpenCodeError error)
    {
        var payload = new Dictionary<string, object?>
        {
            ["error"] = new Dictionary<string, object?>
            {
                ["message"] = error.Message,
                ["type"] = error.Type.IsNullOrEmpty() ? "server_error" : error.Type,
                ["param"] = error.Param,
                ["code"] = error.Code,
            },
        };

        return JsonSerializer.Serialize(payload, _errorJsonOptions);
    }

    public static string ToAnthropicErrorJson(OpenCodeError error)
    {
        var payload = new Dictionary<string, object?>
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
        };

        return JsonSerializer.Serialize(payload, _errorJsonOptions);
    }

    private static string? Truncate(string? value, int max)
    {
        if (value.IsNullOrEmpty())
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }
}
