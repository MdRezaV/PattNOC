namespace ServiceLib.OpenCode;

public sealed record ExecutorResult(
    bool Success,
    EOpenCodeConnectivityState State,
    OpenCodeError? Error,
    NormalizedCompletionResponse? Response,
    IAsyncEnumerable<NormalizedStreamEvent>? Events,
    RequestRoute? Route,
    int? HttpStatus,
    string? RetryAfter = null)
{
    /// <summary>Correlates this result with its attempt/outcome log lines.</summary>
    public string RequestId { get; init; } = "";
}

public sealed class RequestExecutor
{
    private const string Tag = "OpenCode.Executor";

    private enum NextAction
    {
        Retry,
        SwitchStyle,
        Stop,
    }

    private readonly IActiveProxyProvider _proxyProvider;
    private readonly ModelCatalog _catalog;
    private readonly OpenCodeTelemetry _telemetry;
    private readonly Func<HttpMessageHandler>? _handlerFactory;

    public RequestExecutor(
        IActiveProxyProvider proxyProvider,
        ModelCatalog catalog,
        OpenCodeTelemetry telemetry,
        Func<HttpMessageHandler>? handlerFactory = null)
    {
        _proxyProvider = proxyProvider;
        _catalog = catalog;
        _telemetry = telemetry;
        _handlerFactory = handlerFactory;
    }

    public async Task<ExecutorResult> ExecuteAsync(
        NormalizedCompletionRequest request,
        OpenCodeItem settings,
        CancellationToken ct = default,
        string? requestId = null,
        bool allowSessionRefresh = true)
    {
        requestId ??= Guid.NewGuid().ToString("N")[..12];
        var sw = System.Diagnostics.Stopwatch.StartNew();

        ExecutorResult result;
        try
        {
            result = await ExecuteCoreAsync(request, settings, requestId, allowSessionRefresh, ct);
        }
        catch (Exception ex)
        {
            // Even an escaping exception (caller cancel, unexpected fault) must
            // leave one outcome line behind.
            sw.Stop();
            Logging.SaveLog(Tag, ex);
            OpenCodeRequestLog.Write(new OpenCodeLogEntry(
                "outcome",
                RequestId: requestId,
                ModelId: request.Model,
                Ok: false,
                LatencyMs: sw.ElapsedMilliseconds,
                Detail: ex.Message));
            throw;
        }

        sw.Stop();
        result = result with { RequestId = requestId };
        OpenCodeRequestLog.Write(new OpenCodeLogEntry(
            "outcome",
            RequestId: requestId,
            TargetId: result.Route?.Target.Id,
            ModelId: result.Route?.Model.Id ?? request.Model,
            Style: result.Route?.Adapter.Style,
            Profile: result.Route?.ProfileRemark,
            HttpStatus: result.HttpStatus,
            State: result.State,
            Ok: result.Success,
            LatencyMs: sw.ElapsedMilliseconds,
            Detail: result.Success
                ? null
                : result.Error?.Code is { Length: > 0 } code
                    ? $"{result.Error.Message} ({code})"
                    : result.Error?.Message));
        return result;
    }

    private async Task<ExecutorResult> ExecuteCoreAsync(
        NormalizedCompletionRequest request,
        OpenCodeItem settings,
        string requestId,
        bool allowSessionRefresh,
        CancellationToken ct)
    {
        var modelRef = request.Model.IsNullOrEmpty() ? settings.DefaultModel : request.Model;
        var (target, model) = _catalog.ResolveTargetModel(modelRef, settings);

        if (model is null || target is null)
        {
            var err = new OpenCodeError(
                "not_found_error",
                $"Model '{modelRef}' is not available in the catalog.",
                404, null, "model_not_found");
            return new ExecutorResult(
                false, EOpenCodeConnectivityState.ModelNotFound, err, null, null, null, 404);
        }

        var capabilityError = CheckCapabilities(request, model);
        if (capabilityError is not null)
        {
            return new ExecutorResult(
                false, EOpenCodeConnectivityState.UnsupportedRequest, capabilityError, null, null, null, 400);
        }

        var snapshot = await _proxyProvider.TryGetSnapshotAsync(ct);
        if (snapshot is null)
        {
            var err = new OpenCodeError(
                "server_error",
                "No active v2rayN proxy route is available. Start a profile and try again.",
                503, null, "client_restricted");
            return new ExecutorResult(
                false, EOpenCodeConnectivityState.ClientRestricted, err, null, null, null, 503);
        }

        var apiKey = target.ApiKey;
        if (apiKey.IsNullOrEmpty() && !target.KeyOptional)
        {
            var err = new OpenCodeError(
                "authentication_error",
                $"Target '{target.Name}' requires an API key.",
                401, null, "authentication_error");
            return new ExecutorResult(
                false, EOpenCodeConnectivityState.AuthenticationFailed, err, null, null, null, 401);
        }

        var wireModel = model.Id;
        var wireRequest = request with { Model = wireModel };
        _telemetry.RecordRequest(request.Stream, request.Tools is { Count: > 0 });

        // Models differ in which upstream format they answer on (e.g. Mimo speaks
        // Chat Completions, Muse Spark only the Responses API) and the remote
        // catalog doesn't say. Try the known style first; on a format-shaped
        // failure probe the other registered formats and remember what worked.
        var candidates = _catalog.GetStyleCandidates(target, model);
        var maxAttempts = 1 + Math.Max(0, settings.MaxRetry);
        var sessionRefreshed = false;
        ExecutorResult? preferredFailure = null;

        for (var ci = 0; ci < candidates.Count; ci++)
        {
            var adapter = Adapters.AdapterFactory.Create(candidates[ci]);
            var isLastCandidate = ci == candidates.Count - 1;
            var route = new RequestRoute(
                snapshot.WebProxy,
                snapshot.ProfileIndexId,
                snapshot.ProfileRemark,
                target,
                model,
                adapter,
                requestId);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    _telemetry.RecordRetry();
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.RequestTimeoutSeconds)));
                var attemptSw = System.Diagnostics.Stopwatch.StartNew();

                HttpClient? client = null;
                try
                {
                    client = CreateClient(snapshot.WebProxy, settings.ConnectTimeoutSeconds);
                    var upstreamRequest = adapter.BuildRequest(wireRequest, target, apiKey, requestId);
                    if (settings.DebugLogRequests)
                    {
                        await LogOutboundAsync(upstreamRequest, attempt, requestId, target, model, request, timeoutCts.Token);
                    }

                    using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
                    var response = await client.SendAsync(
                        upstreamRequest, HttpCompletionOption.ResponseHeadersRead, sendCts.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        string? body = null;
                        try
                        {
                            body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                        }
                        catch
                        {
                            // Body read is best-effort for classification.
                        }

                        if (settings.DebugLogRequests)
                        {
                            OpenCodeRequestLog.Write(new OpenCodeLogEntry(
                                "inbound",
                                RequestId: requestId,
                                TargetId: target.Id,
                                ModelId: model.Id,
                                Style: adapter.Style,
                                Attempt: attempt,
                                HttpStatus: (int)response.StatusCode,
                                Detail: TruncateForLog(body, 4000)));
                        }

                        var retryAfter = response.Headers.TryGetValues("Retry-After", out var raValues)
                            ? raValues.FirstOrDefault()
                            : null;
                        var (state, error) = ErrorClassification.Classify(
                            (int)response.StatusCode, body, null, proxyUnavailable: false);
                        response.Dispose();

                        // 403 FreeTierError: retry once on live paths to recover a stale
                        // free-tier session. Every attempt re-runs BuildRequest, so
                        // x-opencode-session / x-opencode-request already come out fresh.
                        // Test paths pass allowSessionRefresh=false so a 403 is reported
                        // as a valid result instead of being retried.
                        if (allowSessionRefresh
                            && !sessionRefreshed
                            && (int)response.StatusCode == 403
                            && body is not null
                            && body.Contains("FreeTierError", StringComparison.OrdinalIgnoreCase))
                        {
                            sessionRefreshed = true;
                            _telemetry.RecordRetry();
                            attempt--;
                            continue;
                        }

                        var failure = FailResult(state, error, route,
                            MapHttpStatus(state, (int)response.StatusCode), retryAfter);
                        LogAttempt(requestId, route, attempt, (int)response.StatusCode, state,
                            attemptSw.ElapsedMilliseconds, error.Message);

                        if (ci == 0)
                        {
                            preferredFailure = failure;
                        }

                        var action = DecideNext(isLastCandidate, state, attempt, maxAttempts);
                        if (action == NextAction.Retry)
                        {
                            continue;
                        }

                        if (action == NextAction.Stop)
                        {
                            _telemetry.RecordFailure($"{(int)response.StatusCode} {state}");
                            return preferredFailure ?? failure;
                        }

                        break; // probe the next request format for this model
                    }

                    if (request.Stream)
                    {
                        var events = ObserveStream(
                            adapter.TranslateStreamAsync(response, timeoutCts.Token),
                            requestId, route);
                        _telemetry.RecordSuccess();
                        LogAttempt(requestId, route, attempt, (int)response.StatusCode,
                            EOpenCodeConnectivityState.OpenCodeAccepted, attemptSw.ElapsedMilliseconds, null);
                        RememberNegotiatedStyle(target, model, candidates, adapter);
                        return new ExecutorResult(
                            true, EOpenCodeConnectivityState.OpenCodeAccepted, null, null, events, route,
                            (int)response.StatusCode);
                    }

                    var materialized = await adapter.MaterializeAsync(response, timeoutCts.Token);
                    response.Dispose();
                    _telemetry.RecordSuccess();
                    LogAttempt(requestId, route, attempt, (int)response.StatusCode,
                        EOpenCodeConnectivityState.OpenCodeAccepted, attemptSw.ElapsedMilliseconds, null);
                    RememberNegotiatedStyle(target, model, candidates, adapter);
                    return new ExecutorResult(
                        true, EOpenCodeConnectivityState.OpenCodeAccepted, null, materialized, null, route,
                        (int)response.StatusCode);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    attemptSw.Stop();
                    var err = new OpenCodeError("server_error", "Request timed out.", null, null, "timeout");
                    _telemetry.RecordFailure("timeout");
                    LogAttempt(requestId, route, attempt, null,
                        EOpenCodeConnectivityState.Timeout, attemptSw.ElapsedMilliseconds, err.Message);
                    return FailResult(EOpenCodeConnectivityState.Timeout, err, route, 504);
                }
                catch (Exception ex)
                {
                    attemptSw.Stop();
                    var (state, error) = ErrorClassification.Classify(null, null, ex, proxyUnavailable: false);
                    Logging.SaveLog(Tag, ex);
                    var failure = FailResult(state, error, route, MapHttpStatus(state, null));
                    LogAttempt(requestId, route, attempt, null, state,
                        attemptSw.ElapsedMilliseconds, ex.Message);

                    if (ci == 0)
                    {
                        preferredFailure = failure;
                    }

                    var action = DecideNext(isLastCandidate, state, attempt, maxAttempts);
                    if (action == NextAction.Retry)
                    {
                        continue;
                    }

                    if (action == NextAction.Stop)
                    {
                        _telemetry.RecordFailure($"{state}: {error.Message}");
                        return preferredFailure ?? failure;
                    }

                    break; // unusable payload: another format may work for this model
                }
                finally
                {
                    client?.Dispose();
                }
            }
        }

        // Defensive: the last candidate always exits the inner loop by returning.
        return preferredFailure ?? FailResult(
            EOpenCodeConnectivityState.Unknown,
            new OpenCodeError("server_error", "Request failed.", null, null, "unknown_error"),
            null, 500);
    }

    /// <summary>
    /// Format-shaped failures (bad request for this endpoint, unknown model on it,
    /// provider 5xx) are what a wrong request format looks like — probe the next
    /// registered format when one remains. Auth, rate limits, and network trouble
    /// are format-independent: they never switch, and only transient states retry.
    /// </summary>
    private static NextAction DecideNext(
        bool isLastCandidate,
        EOpenCodeConnectivityState state,
        int attempt,
        int maxAttempts)
    {
        var formatShaped = state is EOpenCodeConnectivityState.UnsupportedRequest
            or EOpenCodeConnectivityState.ModelNotFound
            or EOpenCodeConnectivityState.ProviderError;
        var transient = IsTransient(state);

        if (!formatShaped)
        {
            return transient && attempt < maxAttempts ? NextAction.Retry : NextAction.Stop;
        }

        if (!isLastCandidate)
        {
            return NextAction.SwitchStyle;
        }

        return transient && attempt < maxAttempts ? NextAction.Retry : NextAction.Stop;
    }

    private void RememberNegotiatedStyle(
        OpenCodeTargetItem target,
        OpenCodeModel model,
        IReadOnlyList<EOpenCodeApiStyle> candidates,
        IUpstreamAdapter adapter)
    {
        if (candidates.Count > 1 && adapter.Style != candidates[0])
        {
            _catalog.RecordNegotiatedStyle(target.Id, model.Id, adapter.Style);
            OpenCodeRequestLog.Write(new OpenCodeLogEntry(
                "negotiate",
                TargetId: target.Id,
                ModelId: model.Id,
                Style: adapter.Style,
                Detail: $"model answers on {adapter.Style} (preferred {candidates[0]})"));
        }
    }

    private static void LogAttempt(
        string requestId,
        RequestRoute route,
        int attempt,
        int? httpStatus,
        EOpenCodeConnectivityState state,
        long latencyMs,
        string? detail)
    {
        OpenCodeRequestLog.Write(new OpenCodeLogEntry(
            "attempt",
            RequestId: requestId,
            TargetId: route.Target.Id,
            ModelId: route.Model.Id,
            Style: route.Adapter.Style,
            Profile: route.ProfileRemark,
            Attempt: attempt,
            HttpStatus: httpStatus,
            State: state,
            Ok: state == EOpenCodeConnectivityState.OpenCodeAccepted,
            LatencyMs: latencyMs,
            Detail: detail));
    }

    /// <summary>
    /// Wraps a translated upstream stream so its end is always logged, and a
    /// mid-stream crash becomes an Error event (which clients and the tester
    /// surface) instead of an exception thrown at the consumer.
    /// </summary>
    private async IAsyncEnumerable<NormalizedStreamEvent> ObserveStream(
        IAsyncEnumerable<NormalizedStreamEvent> source,
        string requestId,
        RequestRoute route)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long textChars = 0;
        var toolDeltas = 0;
        var done = false;
        string? inStreamError = null;
        Exception? failure = null;
        var cancelled = false;

        var enumerator = source.GetAsyncEnumerator();
        try
        {
            while (true)
            {
                NormalizedStreamEvent evt;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    evt = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }

                switch (evt.Type)
                {
                    case NormalizedStreamEventType.TextDelta:
                        textChars += evt.Text?.Length ?? 0;
                        break;
                    case NormalizedStreamEventType.ToolCallDelta:
                        toolDeltas++;
                        break;
                    case NormalizedStreamEventType.Done:
                        done = true;
                        break;
                    case NormalizedStreamEventType.Error:
                        inStreamError ??= evt.ErrorMessage ?? "Upstream error.";
                        break;
                }

                yield return evt;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
            var ok = failure is null && inStreamError is null
                && (done || textChars > 0 || toolDeltas > 0);
            OpenCodeRequestLog.Write(new OpenCodeLogEntry(
                "stream",
                RequestId: requestId,
                TargetId: route.Target.Id,
                ModelId: route.Model.Id,
                Style: route.Adapter.Style,
                Profile: route.ProfileRemark,
                Ok: ok,
                LatencyMs: sw.ElapsedMilliseconds,
                Detail: failure?.Message
                    ?? inStreamError
                    ?? (done ? "completed" : "stream ended without a done event")));
        }

        if (cancelled)
        {
            throw new OperationCanceledException();
        }

        if (failure is not null)
        {
            var (_, error) = ErrorClassification.Classify(null, null, failure, proxyUnavailable: false);
            yield return new NormalizedStreamEvent(
                NormalizedStreamEventType.Error, ErrorType: error.Type, ErrorMessage: error.Message);
        }
    }

    private async Task LogOutboundAsync(
        HttpRequestMessage msg,
        int attempt,
        string requestId,
        OpenCodeTargetItem target,
        OpenCodeModel model,
        NormalizedCompletionRequest request,
        CancellationToken ct)
    {
        string? body = null;
        try
        {
            body = msg.Content is null ? null : await msg.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            // Logging must never fail the request.
        }

        var headers = new List<string>();
        foreach (var header in msg.Headers)
        {
            headers.Add($"{header.Key}: {string.Join(", ", header.Value)}");
        }

        if (msg.Content is not null)
        {
            foreach (var header in msg.Content.Headers)
            {
                headers.Add($"{header.Key}: {string.Join(", ", header.Value)}");
            }
        }

        // Free-tier bodies can embed a full system prompt; keep the log bounded but
        // large enough that two requests can still be diffed by eye.
        OpenCodeRequestLog.Write(new OpenCodeLogEntry(
            "outbound",
            RequestId: requestId,
            TargetId: target.Id,
            ModelId: model.Id,
            Attempt: attempt,
            Detail: $"source={(request.Stream ? "stream" : "non-stream")} " +
                $"baseUrl={target.BaseUrl} style={model.ApiStyle} " +
                $"freeTier={Adapters.OpenCodeFreeTier.IsFreeTierTarget(target)} " +
                $"url={msg.Method} {msg.RequestUri}\n" +
                $"headers: {string.Join("; ", headers)}\n" +
                $"body: {TruncateForLog(body, 32000)}"));
    }

    private static string? TruncateForLog(string? value, int max)
    {
        if (value.IsNullOrEmpty())
        {
            return value;
        }

        return value!.Length <= max ? value : value[..max] + $"…({value.Length} chars)";
    }

    private HttpClient CreateClient(IWebProxy? proxy, int connectTimeoutSeconds)
    {
        if (_handlerFactory is null)
        {
            return ProxyHttpClientFactory.Create(proxy, connectTimeoutSeconds);
        }

        var handler = _handlerFactory();
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static ExecutorResult FailResult(
        EOpenCodeConnectivityState state,
        OpenCodeError error,
        RequestRoute? route,
        int? httpStatus,
        string? retryAfter = null)
    {
        return new ExecutorResult(false, state, error, null, null, route, httpStatus, retryAfter);
    }

    private static OpenCodeError? CheckCapabilities(NormalizedCompletionRequest request, OpenCodeModel model)
    {
        if (request.Tools is { Count: > 0 } && !model.SupportsTools)
        {
            return new OpenCodeError(
                "invalid_request_error",
                $"Model '{model.Id}' does not support tool calls.",
                400, "tools", "unsupported_request");
        }

        if (request.Stream && !model.SupportsStreaming)
        {
            return new OpenCodeError(
                "invalid_request_error",
                $"Model '{model.Id}' does not support streaming.",
                400, "stream", "unsupported_request");
        }

        if (request.ResponseFormatType.IsNotEmpty()
            && request.ResponseFormatType.Equals("json_schema", StringComparison.OrdinalIgnoreCase)
            && !model.SupportsStructuredOutput)
        {
            return new OpenCodeError(
                "invalid_request_error",
                $"Model '{model.Id}' does not support json_schema response format.",
                400, "response_format", "unsupported_request");
        }

        if (request.ResponseFormatType.IsNotEmpty()
            && request.ResponseFormatType.Equals("json_object", StringComparison.OrdinalIgnoreCase)
            && !model.SupportsJsonMode)
        {
            return new OpenCodeError(
                "invalid_request_error",
                $"Model '{model.Id}' does not support json_object response format.",
                400, "response_format", "unsupported_request");
        }

        return null;
    }

    private static bool IsTransient(EOpenCodeConnectivityState state)
    {
        return state is EOpenCodeConnectivityState.NetworkError
            or EOpenCodeConnectivityState.ProviderError
            or EOpenCodeConnectivityState.Timeout;
    }

    private static int MapHttpStatus(EOpenCodeConnectivityState state, int? upstreamStatus)
    {
        return state switch
        {
            EOpenCodeConnectivityState.OpenCodeAccepted => 200,
            EOpenCodeConnectivityState.UnsupportedRequest => 400,
            EOpenCodeConnectivityState.AuthenticationFailed => 401,
            EOpenCodeConnectivityState.AuthorizationFailed => 403,
            EOpenCodeConnectivityState.ModelNotFound => 404,
            EOpenCodeConnectivityState.Timeout => 504,
            EOpenCodeConnectivityState.FreeUsageLimit or EOpenCodeConnectivityState.RateLimited => 429,
            EOpenCodeConnectivityState.ClientRestricted => 503,
            _ => upstreamStatus is >= 400 and < 600 ? upstreamStatus.Value : 502,
        };
    }
}
