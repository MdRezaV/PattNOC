namespace ServiceLib.Tests.OpenCode;

public class OpenCodeColumnStatusTests
{
    [Test]
    [Arguments(EOpenCodeConnectivityState.OpenCodeAccepted, OpenCodeColumnStatus.SuccessBox)]
    [Arguments(EOpenCodeConnectivityState.RateLimited, OpenCodeColumnStatus.RateLimitedBox)]
    [Arguments(EOpenCodeConnectivityState.FreeUsageLimit, OpenCodeColumnStatus.RateLimitedBox)]
    [Arguments(EOpenCodeConnectivityState.AuthenticationFailed, OpenCodeColumnStatus.FailedBox)]
    [Arguments(EOpenCodeConnectivityState.AuthorizationFailed, OpenCodeColumnStatus.FailedBox)]
    [Arguments(EOpenCodeConnectivityState.ModelNotFound, OpenCodeColumnStatus.FailedBox)]
    [Arguments(EOpenCodeConnectivityState.ProviderError, OpenCodeColumnStatus.FailedBox)]
    [Arguments(EOpenCodeConnectivityState.UnsupportedRequest, OpenCodeColumnStatus.FailedBox)]
    [Arguments(EOpenCodeConnectivityState.Timeout, OpenCodeColumnStatus.FailedBox)]
    [Arguments(EOpenCodeConnectivityState.NetworkError, OpenCodeColumnStatus.FailedBox)]
    [Arguments(EOpenCodeConnectivityState.ClientRestricted, OpenCodeColumnStatus.FailedBox)]
    [Arguments(EOpenCodeConnectivityState.Unknown, OpenCodeColumnStatus.FailedBox)]
    public async Task ToBox_MapsStateToSquare(EOpenCodeConnectivityState state, string expected)
    {
        await OpenCodeColumnStatus.ToBox(state).Should().BeEqualTo(expected);
    }

    [Test]
    [Arguments(EOpenCodeConnectivityState.NetworkError)]
    [Arguments(EOpenCodeConnectivityState.Timeout)]
    [Arguments(EOpenCodeConnectivityState.Unknown)]
    [Arguments(EOpenCodeConnectivityState.ClientRestricted)]
    public async Task IsCritical_ConnectionFailures_AbortSequence(EOpenCodeConnectivityState state)
    {
        await OpenCodeColumnStatus.IsCritical(state).Should().BeTrue();
    }

    [Test]
    [Arguments(EOpenCodeConnectivityState.OpenCodeAccepted)]
    [Arguments(EOpenCodeConnectivityState.RateLimited)]
    [Arguments(EOpenCodeConnectivityState.FreeUsageLimit)]
    [Arguments(EOpenCodeConnectivityState.AuthenticationFailed)]
    [Arguments(EOpenCodeConnectivityState.AuthorizationFailed)]
    [Arguments(EOpenCodeConnectivityState.ModelNotFound)]
    [Arguments(EOpenCodeConnectivityState.ProviderError)]
    [Arguments(EOpenCodeConnectivityState.UnsupportedRequest)]
    public async Task IsCritical_ModelSpecificFailures_ContinueSequence(EOpenCodeConnectivityState state)
    {
        await OpenCodeColumnStatus.IsCritical(state).Should().BeFalse();
    }

    [Test]
    public async Task AllNotTested_OneBoxPerSelectedModel()
    {
        await OpenCodeColumnStatus.AllNotTested(3).Should().BeEqualTo("⬜⬜⬜");
        await OpenCodeColumnStatus.AllNotTested(0).Should().BeEmpty();
    }

    [Test]
    public async Task Sequence_StopsAtFirstCriticalFailure_AndLeavesRestUntested()
    {
        // First model times out: everything after it must stay untested.
        var states = new[]
        {
            EOpenCodeConnectivityState.Timeout,
            EOpenCodeConnectivityState.OpenCodeAccepted,
            EOpenCodeConnectivityState.RateLimited,
        };

        var boxes = new List<string>(Enumerable.Repeat(OpenCodeColumnStatus.NotTestedBox, states.Length));
        for (var i = 0; i < states.Length; i++)
        {
            boxes[i] = OpenCodeColumnStatus.ToBox(states[i]);
            if (OpenCodeColumnStatus.IsCritical(states[i]))
            {
                break;
            }
        }

        await string.Concat(boxes).Should().BeEqualTo("🟥⬜⬜");
    }

    [Test]
    public async Task Sequence_RateLimitAndModelFailures_DoNotStopTheRun()
    {
        var states = new[]
        {
            EOpenCodeConnectivityState.OpenCodeAccepted,
            EOpenCodeConnectivityState.OpenCodeAccepted,
            EOpenCodeConnectivityState.RateLimited,
            EOpenCodeConnectivityState.ProviderError,
            EOpenCodeConnectivityState.OpenCodeAccepted,
        };

        var boxes = new List<string>(Enumerable.Repeat(OpenCodeColumnStatus.NotTestedBox, states.Length));
        for (var i = 0; i < states.Length; i++)
        {
            boxes[i] = OpenCodeColumnStatus.ToBox(states[i]);
            if (OpenCodeColumnStatus.IsCritical(states[i]))
            {
                break;
            }
        }

        await string.Concat(boxes).Should().BeEqualTo("🟩🟩🟨🟥🟩");
    }
}
