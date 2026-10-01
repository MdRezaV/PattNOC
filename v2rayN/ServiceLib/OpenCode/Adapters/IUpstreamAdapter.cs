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
    public static IUpstreamAdapter Create(EOpenCodeApiStyle style)
    {
        return style switch
        {
            EOpenCodeApiStyle.Responses => new ResponsesAdapter(),
            _ => new ChatCompletionsAdapter(),
        };
    }
}
