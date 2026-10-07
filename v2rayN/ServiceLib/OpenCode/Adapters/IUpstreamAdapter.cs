namespace ServiceLib.OpenCode.Adapters;

public interface IUpstreamAdapter
{
    EOpenCodeApiStyle Style { get; }

    HttpRequestMessage BuildRequest(
        NormalizedCompletionRequest req,
        OpenCodeTargetItem target,
        string? apiKey,
        string requestId);

    IAsyncEnumerable<NormalizedStreamEvent> TranslateStreamAsync(
        HttpResponseMessage response,
        CancellationToken ct);

    Task<NormalizedCompletionResponse> MaterializeAsync(
        HttpResponseMessage response,
        CancellationToken ct);
}

public sealed record RequestRoute(
    IWebProxy? WebProxy,
    string? ProfileIndexId,
    string? ProfileRemark,
    OpenCodeTargetItem Target,
    OpenCodeModel Model,
    IUpstreamAdapter Adapter,
    string RequestId);

public static class AdapterFactory
{
    /// <summary>
    /// Every upstream format this gateway can speak. A new style is added by
    /// extending the enum, this list, and Create — request negotiation picks
    /// formats up from here with no per-model changes.
    /// </summary>
    public static IReadOnlyList<EOpenCodeApiStyle> SupportedStyles() =>
        [EOpenCodeApiStyle.ChatCompletions, EOpenCodeApiStyle.Responses];

    public static IUpstreamAdapter Create(EOpenCodeApiStyle style)
    {
        return style switch
        {
            EOpenCodeApiStyle.Responses => new ResponsesAdapter(),
            _ => new ChatCompletionsAdapter(),
        };
    }
}
