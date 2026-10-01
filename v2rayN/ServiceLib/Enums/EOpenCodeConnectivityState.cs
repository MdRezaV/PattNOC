namespace ServiceLib.Enums;

public enum EOpenCodeConnectivityState
{
    NotTested = 0,
    Testing = 1,
    OpenCodeAccepted = 2,
    RateLimited = 3,
    FreeUsageLimit = 4,
    AuthenticationFailed = 5,
    AuthorizationFailed = 6,
    ModelNotFound = 7,
    ProviderError = 8,
    NetworkError = 9,
    Timeout = 10,
    ClientRestricted = 11,
    UnsupportedRequest = 12,
    Unknown = 13,
}
