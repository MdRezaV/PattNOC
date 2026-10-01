namespace ServiceLib.OpenCode;

public sealed class ConnectivityTester
{
    private const string Tag = "OpenCode.Tester";

    private readonly RequestExecutor _executor;

    public ConnectivityTester(RequestExecutor executor)
    {
        _executor = executor;
    }

    public async Task<ConnectivityTestResult> TestAsync(
        OpenCodeItem settings,
        string? targetId = null,
        string? modelRef = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? profileRemark = null;

        try
        {
            var target = settings.Targets?.FirstOrDefault(t =>
                t.Id.Equals(targetId ?? settings.DefaultTarget, StringComparison.OrdinalIgnoreCase) && t.Enabled)
                ?? settings.Targets?.FirstOrDefault(t => t.Enabled);

            if (target is null)
            {
                return new ConnectivityTestResult(
                    EOpenCodeConnectivityState.Unknown,
                    "No enabled OpenCode target is configured.",
                    Elapsed: sw.Elapsed);
            }

            var resolvedModel = modelRef;
            if (resolvedModel.IsNullOrEmpty())
            {
                resolvedModel = target.Id.Equals(settings.DefaultTarget, StringComparison.OrdinalIgnoreCase)
                    ? settings.DefaultModel
                    : $"{target.Id}/{settings.DefaultModel}";
            }

            var request = new NormalizedCompletionRequest
            {
                Model = resolvedModel,
                Messages =
                [
                    new NormalizedMessage
                    {
                        Role = "user",
                        Content = "Reply with OK.",
                    },
                ],
                MaxTokens = 16,
                Stream = true,
            };

            var result = await _executor.ExecuteAsync(request, settings, ct);
            profileRemark = result.Route?.ProfileRemark;
            sw.Stop();

            if (result.Success)
            {
                string? content = null;
                if (result.Response is not null)
                {
                    content = result.Response.Content;
                }
                else if (result.Events is not null)
                {
                    var sb = new System.Text.StringBuilder();
                    await foreach (var evt in result.Events.WithCancellation(ct))
                    {
                        if (evt.Type == NormalizedStreamEventType.TextDelta && evt.Text.IsNotEmpty())
                        {
                            sb.Append(evt.Text);
                        }

                        if (evt.Type == NormalizedStreamEventType.Done)
                        {
                            break;
                        }
                    }

                    content = sb.ToString();
                }

                return new ConnectivityTestResult(
                    EOpenCodeConnectivityState.OpenCodeAccepted,
                    BuildSuccessDetail(content),
                    result.HttpStatus,
                    profileRemark,
                    resolvedModel,
                    sw.Elapsed);
            }

            var detail = result.Error?.Message ?? "Connection test failed.";
            if (result.Error?.Code is not null)
            {
                detail = $"{detail} ({result.Error.Code})";
            }

            return new ConnectivityTestResult(
                result.State,
                detail,
                result.HttpStatus,
                profileRemark,
                resolvedModel,
                sw.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            return new ConnectivityTestResult(
                EOpenCodeConnectivityState.Timeout,
                "Connection test was cancelled.",
                Elapsed: sw.Elapsed);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
            sw.Stop();
            return new ConnectivityTestResult(
                EOpenCodeConnectivityState.Unknown,
                ex.Message,
                Elapsed: sw.Elapsed);
        }
    }

    private static string BuildSuccessDetail(string? content)
    {
        if (content.IsNullOrEmpty())
        {
            return "OpenCode accepted the request.";
        }

        if (content!.Length > 80)
        {
            content = content[..80] + "…";
        }

        return content;
    }
}
