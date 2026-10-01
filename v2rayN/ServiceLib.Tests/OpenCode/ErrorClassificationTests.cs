namespace ServiceLib.Tests.OpenCode;

public class ErrorClassificationTests
{
    [Test]
    [Arguments(200, EOpenCodeConnectivityState.OpenCodeAccepted)]
    [Arguments(201, EOpenCodeConnectivityState.OpenCodeAccepted)]
    [Arguments(400, EOpenCodeConnectivityState.UnsupportedRequest)]
    [Arguments(401, EOpenCodeConnectivityState.AuthenticationFailed)]
    [Arguments(403, EOpenCodeConnectivityState.AuthorizationFailed)]
    [Arguments(404, EOpenCodeConnectivityState.ModelNotFound)]
    [Arguments(408, EOpenCodeConnectivityState.Timeout)]
    [Arguments(500, EOpenCodeConnectivityState.ProviderError)]
    [Arguments(502, EOpenCodeConnectivityState.ProviderError)]
    [Arguments(503, EOpenCodeConnectivityState.ProviderError)]
    [Arguments(504, EOpenCodeConnectivityState.ProviderError)]
    public async Task Classify_MapsHttpStatusToState(int status, EOpenCodeConnectivityState expected)
    {
        var (state, _) = ErrorClassification.Classify(status, null, null, proxyUnavailable: false);
        await state.Should().BeEqualTo(expected);
    }

    [Test]
    [Arguments("Free usage limit reached.", EOpenCodeConnectivityState.FreeUsageLimit)]
    [Arguments("free_usage_limit", EOpenCodeConnectivityState.FreeUsageLimit)]
    [Arguments("Too many requests", EOpenCodeConnectivityState.RateLimited)]
    public async Task Classify_429_DistinguishesFreeUsageLimitFromRateLimit(string body, EOpenCodeConnectivityState expected)
    {
        var (state, error) = ErrorClassification.Classify(429, body, null, proxyUnavailable: false);
        await state.Should().BeEqualTo(expected);
        await error.Status.Should().BeEqualTo(429);
        await error.Type.Should().BeEqualTo("rate_limit_error");
    }

    [Test]
    public async Task Classify_ProxyUnavailable_TakesPrecedence()
    {
        var (state, error) = ErrorClassification.Classify(401, "bad key", null, proxyUnavailable: true);
        await state.Should().BeEqualTo(EOpenCodeConnectivityState.ClientRestricted);
        await error.Code.Should().BeEqualTo("client_restricted");
        await error.Status.Should().BeEqualTo(503);
    }

    [Test]
    [Arguments(typeof(TimeoutException))]
    [Arguments(typeof(TaskCanceledException))]
    public async Task Classify_TimeoutExceptions_MapToTimeout(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        var (state, error) = ErrorClassification.Classify(null, null, ex, proxyUnavailable: false);
        await state.Should().BeEqualTo(EOpenCodeConnectivityState.Timeout);
        await error.Code.Should().BeEqualTo("timeout");
    }

    [Test]
    [Arguments(typeof(HttpRequestException))]
    [Arguments(typeof(IOException))]
    public async Task Classify_NetworkExceptions_MapToNetworkError(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType, "connect fail")!;
        var (state, error) = ErrorClassification.Classify(null, null, ex, proxyUnavailable: false);
        await state.Should().BeEqualTo(EOpenCodeConnectivityState.NetworkError);
        await error.Code.Should().BeEqualTo("network_error");
    }

    [Test]
    public async Task Classify_SocketException_MapsToNetworkError()
    {
        var ex = new SocketException(111);
        var (state, error) = ErrorClassification.Classify(null, null, ex, proxyUnavailable: false);
        await state.Should().BeEqualTo(EOpenCodeConnectivityState.NetworkError);
        await error.Code.Should().BeEqualTo("network_error");
    }

    [Test]
    public async Task Classify_NoStatusNoException_MapsToUnknown()
    {
        var (state, error) = ErrorClassification.Classify(null, null, null, proxyUnavailable: false);
        await state.Should().BeEqualTo(EOpenCodeConnectivityState.Unknown);
        await error.Code.Should().BeEqualTo("unknown_error");
    }

    [Test]
    public async Task Classify_404_ModelNotFound_HasCode()
    {
        var (_, error) = ErrorClassification.Classify(404, "model not found", null, proxyUnavailable: false);
        await error.Code.Should().BeEqualTo("model_not_found");
        await error.Type.Should().BeEqualTo("not_found_error");
    }

    [Test]
    public async Task ToOpenAiErrorJson_ProducesOpenAIErrorShape()
    {
        var error = new OpenCodeError("authentication_error", "Invalid key", 401, null, "authentication_error");
        var json = ErrorClassification.ToOpenAiErrorJson(error);

        await json.Should().Contain("\"error\"");
        await json.Should().Contain("\"message\":\"Invalid key\"");
        await json.Should().Contain("\"type\":\"authentication_error\"");
        await json.Should().Contain("\"code\":\"authentication_error\"");
    }

    [Test]
    public async Task ToOpenAiErrorJson_DefaultsEmptyTypeToServerError()
    {
        var error = new OpenCodeError("", "Something broke", 502, null, "unknown_error");
        var json = ErrorClassification.ToOpenAiErrorJson(error);
        await json.Should().Contain("\"type\":\"server_error\"");
    }
}
