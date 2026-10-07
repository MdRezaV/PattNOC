namespace ServiceLib.Tests.OpenCode;

/// <summary>
/// Captures OpenCodeRequestLog lines for assertions. The logger sink is static,
/// so the scope holds a process-wide async gate for its lifetime — log-inspecting
/// tests never run interleaved with each other. Use with `await using` (tests
/// are async, so a Monitor-based lock would be released on the wrong thread).
/// </summary>
internal sealed class OpenCodeLogTestScope : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<string> _lines = new();

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_sync)
            {
                return _lines.ToArray();
            }
        }
    }

    private OpenCodeLogTestScope()
    {
    }

    public static async Task<OpenCodeLogTestScope> BeginAsync()
    {
        await Gate.WaitAsync();
        var scope = new OpenCodeLogTestScope();
        OpenCodeRequestLog.TestSink = line =>
        {
            lock (scope._sync)
            {
                scope._lines.Add(line);
            }
        };
        return scope;
    }

    public ValueTask DisposeAsync()
    {
        OpenCodeRequestLog.TestSink = null;
        Gate.Release();
        return ValueTask.CompletedTask;
    }
}
