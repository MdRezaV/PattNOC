namespace ServiceLib.Tests.OpenCode;

public class OpenCodeTelemetryTests
{
    [Test]
    public async Task RecordRequest_CountsTotalStreamingAndTools()
    {
        var telemetry = new OpenCodeTelemetry();

        telemetry.RecordRequest(streaming: false, toolCalls: false);
        telemetry.RecordRequest(streaming: true, toolCalls: false);
        telemetry.RecordRequest(streaming: false, toolCalls: true);
        telemetry.RecordRequest(streaming: true, toolCalls: true);

        var snapshot = telemetry.GetSnapshot();
        await snapshot.TotalRequests.Should().BeEqualTo(4);
        await snapshot.StreamingRequests.Should().BeEqualTo(2);
        await snapshot.ToolCallRequests.Should().BeEqualTo(2);
    }

    [Test]
    public async Task RecordSuccessAndFailure_TrackCountsAndTimestamps()
    {
        var telemetry = new OpenCodeTelemetry();

        telemetry.RecordRequest(false, false);
        telemetry.RecordSuccess();
        telemetry.RecordFailure("429 RateLimited");

        var snapshot = telemetry.GetSnapshot();
        await snapshot.Success.Should().BeEqualTo(1);
        await snapshot.Failed.Should().BeEqualTo(1);
        await snapshot.LastSuccessAtUtc.HasValue.Should().BeTrue();
        await snapshot.LastFailureAtUtc.HasValue.Should().BeTrue();
        await snapshot.LastFailureSummary.Should().BeEqualTo("429 RateLimited");
    }

    [Test]
    public async Task RecordRetry_CountsRetries()
    {
        var telemetry = new OpenCodeTelemetry();

        telemetry.RecordRetry();
        telemetry.RecordRetry();

        await telemetry.GetSnapshot().Retries.Should().BeEqualTo(2);
    }

    [Test]
    public async Task GetSnapshot_EmptyTelemetry_HasNullTimestamps()
    {
        var snapshot = new OpenCodeTelemetry().GetSnapshot();

        await snapshot.TotalRequests.Should().BeEqualTo(0);
        await snapshot.LastSuccessAtUtc.HasValue.Should().BeFalse();
        await snapshot.LastFailureAtUtc.HasValue.Should().BeFalse();
        await snapshot.LastFailureSummary.Should().BeNull();
    }

    [Test]
    public async Task RecordFailure_OverwritesSummary()
    {
        var telemetry = new OpenCodeTelemetry();
        telemetry.RecordFailure("first");
        telemetry.RecordFailure("second");

        await telemetry.GetSnapshot().LastFailureSummary.Should().BeEqualTo("second");
    }
}
