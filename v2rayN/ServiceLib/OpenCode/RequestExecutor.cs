namespace ServiceLib.OpenCode;

public sealed record ExecutorResult(
    bool Success,
    EOpenCodeConnectivityState State,
    OpenCodeError? Error,
    NormalizedCompletionResponse? Response,
    IAsyncEnumerable<NormalizedStreamEvent>? Events,
    RequestRoute? Route,
    int? HttpStatus,
    string? RetryAfter = null);

public sealed class RequestExecutor
{
    private const string Tag = "OpenCode.Executor";

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
        CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
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

        var adapter = Adapters.AdapterFactory.Create(model.ApiStyle);
        var route = new RequestRoute(
            snapshot.WebProxy,
            snapshot.ProfileIndexId,
            snapshot.ProfileRemark,
            target,
            model,
            adapter,
            requestId);

        var wireModel = model.Id;
        var wireRequest = request with { Model = wireModel };
        _telemetry.RecordRequest(request.Stream, request.Tools is { Count: > 0 });

        var maxAttempts = 1 + Math.Max(0, settings.MaxRetry);
        var lastResult = FailResult(
            EOpenCodeConnectivityState.Unknown,
            new OpenCodeError("server_error", "Request failed.", null, null, "unknown_error"),
            null, 500);

        var sessionRefreshed = false;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                _telemetry.RecordRetry();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.RequestTimeoutSeconds)));

            HttpClient? client = null;
            try
            {
                client = CreateClient(snapshot.WebProxy, settings.ConnectTimeoutSeconds);
                var upstreamRequest = adapter.BuildRequest(wireRequest, target, apiKey, requestId);
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

                    var retryAfter = response.Headers.TryGetValues("Retry-After", out var raValues)
                        ? raValues.FirstOrDefault()
                        : null;
                    var (state, error) = ErrorClassification.Classify(
                        (int)response.StatusCode, body, null, proxyUnavailable: false);
                    response.Dispose();

                    // 403 FreeTierError: force session regeneration and retry once.
                    if (!sessionRefreshed
                        && (int)response.StatusCode == 403
                        && body is not null
                        && body.Contains("FreeTierError", StringComparison.OrdinalIgnoreCase))
                    {
                        sessionRefreshed = true;
                        Adapters.OpenCodeFreeTier.RegenerateSession();
                        _telemetry.RecordRetry();
                        continue;
                    }

                    var status = MapHttpStatus(state, (int)response.StatusCode);
                    lastResult = FailResult(state, error, route, status, retryAfter);

                    if (IsTransient(state) && attempt < maxAttempts)
                    {
                        continue;
                    }

                    _telemetry.RecordFailure($"{(int)response.StatusCode} {state}");
                    return lastResult;
                }

                if (request.Stream)
                {
                    var events = adapter.TranslateStreamAsync(response, timeoutCts.Token);
                    _telemetry.RecordSuccess();
                    return new ExecutorResult(
                        true, EOpenCodeConnectivityState.OpenCodeAccepted, null, null, events, route,
                        (int)response.StatusCode);
                }

                var materialized = await adapter.MaterializeAsync(response, timeoutCts.Token);
                response.Dispose();
                _telemetry.RecordSuccess();
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
                var err = new OpenCodeError("server_error", "Request timed out.", null, null, "timeout");
                _telemetry.RecordFailure("timeout");
                return FailResult(EOpenCodeConnectivityState.Timeout, err, route, 504);
            }
            catch (Exception ex)
            {
                var (state, error) = ErrorClassification.Classify(null, null, ex, proxyUnavailable: false);
                Logging.SaveLog(Tag, ex);
                lastResult = FailResult(state, error, route, MapHttpStatus(state, null));

                if (IsTransient(state) && attempt < maxAttempts)
                {
                    continue;
                }

                _telemetry.RecordFailure($"{state}: {error.Message}");
                return lastResult;
            }
            finally
            {
                client?.Dispose();
            }
        }

        return lastResult;
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
