namespace ServiceLib.OpenCode;

public sealed record OpenCodeModel(
    string Id,
    string DisplayName,
    EOpenCodeApiStyle ApiStyle,
    int? ContextWindow,
    bool SupportsStreaming,
    bool SupportsTools,
    bool SupportsJsonMode,
    bool SupportsStructuredOutput,
    string Source,
    bool IsFree = false);

public sealed class OpenCodeCatalogCache
{
    public DateTime UpdatedAt { get; set; }
    public Dictionary<string, List<OpenCodeModel>> ModelsByTarget { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Request-negotiated upstream API style per "targetId/modelId". The remote
    /// catalog only lists ids, so the format a model actually answers on is
    /// discovered by trying it and recorded here to survive restarts.
    /// </summary>
    public Dictionary<string, EOpenCodeApiStyle> NegotiatedStyles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record NormalizedMessage
{
    public string Role { get; init; } = "";
    public string? Content { get; init; }
    public string? ToolCallId { get; init; }
    public string? Name { get; init; }
    public List<NormalizedToolCall>? ToolCalls { get; init; }
}

public sealed record NormalizedToolCall
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "function";
    public NormalizedFunctionCall? Function { get; init; }
}

public sealed class NormalizedFunctionCall
{
    public string Name { get; init; } = "";
    public string? Arguments { get; init; }
}

public sealed class NormalizedTool
{
    public string Type { get; init; } = "function";
    public NormalizedFunctionDef? Function { get; init; }
}

public sealed class NormalizedFunctionDef
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public JsonElement? Parameters { get; init; }
}

public sealed record NormalizedCompletionRequest
{
    public string Model { get; init; } = "";
    public List<NormalizedMessage> Messages { get; init; } = [];
    public List<NormalizedTool>? Tools { get; init; }
    public string? ToolChoice { get; init; }
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? MaxTokens { get; init; }
    public List<string>? Stop { get; init; }
    public bool Stream { get; init; }
    public string? ResponseFormatType { get; init; }
    public JsonElement? ResponseFormatSchema { get; init; }
    public string? User { get; init; }
}

public sealed class NormalizedCompletionResponse
{
    public string Id { get; set; } = "";
    public string Model { get; set; } = "";
    public string? Content { get; set; }
    public string? FinishReason { get; set; }
    public List<NormalizedToolCall>? ToolCalls { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
}

public enum NormalizedStreamEventType
{
    TextDelta = 0,
    ToolCallDelta = 1,
    Usage = 2,
    Done = 3,
    Error = 4,
}

public sealed record NormalizedStreamEvent(
    NormalizedStreamEventType Type,
    string? Text = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? ArgumentsDelta = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    string? ErrorType = null,
    string? ErrorMessage = null,
    string? FinishReason = null);

public sealed record OpenCodeError(string Type, string Message, int? Status = null, string? Param = null, string? Code = null);

public sealed record OpenCodeGatewayStatus(
    bool IsRunning,
    string Host,
    int Port,
    string Endpoint,
    string? Error);

public sealed record OpenCodeTelemetrySnapshot(
    long TotalRequests,
    long Success,
    long Failed,
    long Retries,
    long StreamingRequests,
    long ToolCallRequests,
    DateTime? LastSuccessAtUtc,
    DateTime? LastFailureAtUtc,
    string? LastFailureSummary);

/// <summary>
/// An upstream response body/SSE stream failed to fold into a completion —
/// the transport succeeded but the payload was an error or unparseable.
/// </summary>
public sealed class OpenCodeStreamException(string message) : Exception(message);

public sealed record ConnectivityTestResult(
    EOpenCodeConnectivityState State,
    string Detail,
    int? HttpStatus = null,
    string? ProfileRemark = null,
    string? ModelRef = null,
    TimeSpan? Elapsed = null);
