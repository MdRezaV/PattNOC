namespace ServiceLib.OpenCode;

public static class OpenCodeColumnStatus
{
    public static string Format(ConnectivityTestResult result)
    {
        var message = result.State switch
        {
            EOpenCodeConnectivityState.OpenCodeAccepted => ResUI.OpenCodeColWorking,
            EOpenCodeConnectivityState.AuthorizationFailed => ResUI.OpenCodeColForbidden,
            EOpenCodeConnectivityState.AuthenticationFailed => ResUI.OpenCodeColUnauthenticated,
            EOpenCodeConnectivityState.FreeUsageLimit => ResUI.OpenCodeColIpLimit,
            EOpenCodeConnectivityState.RateLimited => ResUI.OpenCodeColRateLimited,
            EOpenCodeConnectivityState.NetworkError => ResUI.OpenCodeColConnectionFailed,
            EOpenCodeConnectivityState.Timeout => ResUI.OpenCodeColTimeout,
            EOpenCodeConnectivityState.ProviderError => ResUI.OpenCodeColProviderError,
            EOpenCodeConnectivityState.ModelNotFound => ResUI.OpenCodeColModelNotFound,
            EOpenCodeConnectivityState.UnsupportedRequest => ResUI.OpenCodeColUnsupported,
            EOpenCodeConnectivityState.ClientRestricted => ResUI.OpenCodeColNoProxy,
            EOpenCodeConnectivityState.Testing => ResUI.OpenCodeColTesting,
            _ => ResUI.OpenCodeColUnknown,
        };

        var code = result.HttpStatus ?? result.State switch
        {
            EOpenCodeConnectivityState.OpenCodeAccepted => 200,
            EOpenCodeConnectivityState.UnsupportedRequest => 400,
            EOpenCodeConnectivityState.AuthenticationFailed => 401,
            EOpenCodeConnectivityState.AuthorizationFailed => 403,
            EOpenCodeConnectivityState.ModelNotFound => 404,
            EOpenCodeConnectivityState.FreeUsageLimit or EOpenCodeConnectivityState.RateLimited => 429,
            EOpenCodeConnectivityState.ProviderError => 500,
            EOpenCodeConnectivityState.NetworkError => 502,
            EOpenCodeConnectivityState.ClientRestricted => 503,
            EOpenCodeConnectivityState.Timeout => 504,
            _ => 0,
        };

        return code > 0 ? $"{code} {message}" : message;
    }
}
