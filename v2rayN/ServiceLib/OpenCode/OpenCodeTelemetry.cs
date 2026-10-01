namespace ServiceLib.OpenCode;

public sealed class OpenCodeTelemetry
{
    private long _total;
    private long _success;
    private long _failed;
    private long _retries;
    private long _streaming;
    private long _tools;
    private long _lastSuccessTicks;
    private long _lastFailureTicks;
    private string? _lastFailureSummary;

    public void RecordRequest(bool streaming, bool toolCalls)
    {
        Interlocked.Increment(ref _total);
        if (streaming)
        {
            Interlocked.Increment(ref _streaming);
        }

        if (toolCalls)
        {
            Interlocked.Increment(ref _tools);
        }
    }

    public void RecordSuccess()
    {
        Interlocked.Increment(ref _success);
        Interlocked.Exchange(ref _lastSuccessTicks, DateTime.UtcNow.Ticks);
    }

    public void RecordFailure(string summary)
    {
        Interlocked.Increment(ref _failed);
        Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref _lastFailureSummary, summary);
    }

    public void RecordRetry()
    {
        Interlocked.Increment(ref _retries);
    }

    public OpenCodeTelemetrySnapshot GetSnapshot()
    {
        var successTicks = Interlocked.Read(ref _lastSuccessTicks);
        var failureTicks = Interlocked.Read(ref _lastFailureTicks);
        return new OpenCodeTelemetrySnapshot(
            Interlocked.Read(ref _total),
            Interlocked.Read(ref _success),
            Interlocked.Read(ref _failed),
            Interlocked.Read(ref _retries),
            Interlocked.Read(ref _streaming),
            Interlocked.Read(ref _tools),
            successTicks > 0 ? new DateTime(successTicks, DateTimeKind.Utc) : null,
            failureTicks > 0 ? new DateTime(failureTicks, DateTimeKind.Utc) : null,
            Volatile.Read(ref _lastFailureSummary));
    }
}
