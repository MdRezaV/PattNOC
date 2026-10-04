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
        var result = await TestCoreAsync(settings, targetId, modelRef, sw, ct);
        sw.Stop();

        // Every test — pass or fail — leaves one line behind.
        OpenCodeRequestLog.Write(new OpenCodeLogEntry(
            "test",
            TargetId: targetId ?? settings.DefaultTarget,
            ModelId: result.ModelRef ?? modelRef,
            Profile: result.ProfileRemark,
            HttpStatus: result.HttpStatus,
            State: result.State,
            Ok: result.State == EOpenCodeConnectivityState.OpenCodeAccepted,
            LatencyMs: result.Elapsed is { } elapsed ? (long)elapsed.TotalMilliseconds : sw.ElapsedMilliseconds,
            Detail: result.Detail));
        return result;
    }

    private async Task<ConnectivityTestResult> TestCoreAsync(
        OpenCodeItem settings,
        string? targetId,
        string? modelRef,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
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
                    var sawDone = false;
                    var sawToolCall = false;
                    string? streamError = null;

                    await foreach (var evt in result.Events.WithCancellation(ct))
                    {
                        if (evt.Type == NormalizedStreamEventType.TextDelta && evt.Text.IsNotEmpty())
                        {
                            sb.Append(evt.Text);
                        }
                        else if (evt.Type == NormalizedStreamEventType.ToolCallDelta)
                        {
                            sawToolCall = true;
                        }
                        else if (evt.Type == NormalizedStreamEventType.Done)
                        {
                            sawDone = true;
                        }
                        else if (evt.Type == NormalizedStreamEventType.Error)
                        {
                            streamError = evt.ErrorMessage ?? "Upstream stream error.";
                            break;
                        }
                    }

                    // The transport said 2xx, but the stream itself must show a
                    // completed reply — otherwise this would report success for a
                    // connection that produced nothing.
                    if (streamError is not null)
                    {
                        return new ConnectivityTestResult(
                            EOpenCodeConnectivityState.ProviderError,
                            streamError,
                            result.HttpStatus,
                            profileRemark,
                            resolvedModel,
                            sw.Elapsed);
                    }

                    if (!sawDone && sb.Length == 0 && !sawToolCall)
                    {
                        return new ConnectivityTestResult(
                            EOpenCodeConnectivityState.ProviderError,
                            "Stream ended without a completion event.",
                            result.HttpStatus,
                            profileRemark,
                            resolvedModel,
                            sw.Elapsed);
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
