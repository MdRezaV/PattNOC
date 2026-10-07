namespace ServiceLib.OpenCode;

public static class OpenCodeColumnStatus
{
    public const string NotTestedBox = "⬜";
    public const string SuccessBox = "🟩";
    public const string RateLimitedBox = "🟨";
    public const string FailedBox = "🟥";

    /// <summary>Connection/timeout failures that abort the whole model sequence.</summary>
    public static bool IsCritical(EOpenCodeConnectivityState state)
    {
        return state is EOpenCodeConnectivityState.NetworkError
            or EOpenCodeConnectivityState.Timeout
            or EOpenCodeConnectivityState.Unknown
            or EOpenCodeConnectivityState.ClientRestricted;
    }

    public static string ToBox(EOpenCodeConnectivityState state)
    {
        return state switch
        {
            EOpenCodeConnectivityState.OpenCodeAccepted => SuccessBox,
            EOpenCodeConnectivityState.RateLimited or EOpenCodeConnectivityState.FreeUsageLimit => RateLimitedBox,
            _ => FailedBox,
        };
    }

    public static string AllNotTested(int modelCount)
    {
        return modelCount > 0 ? string.Concat(Enumerable.Repeat(NotTestedBox, modelCount)) : string.Empty;
    }
}
