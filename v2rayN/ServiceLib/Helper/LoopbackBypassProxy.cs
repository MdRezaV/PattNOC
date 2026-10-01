namespace ServiceLib.Helper;

/// <summary>
/// Wraps an IWebProxy and never proxies loopback destinations (local core APIs, PAC, Kestrel gateway).
/// </summary>
public sealed class LoopbackBypassProxy : IWebProxy
{
    private readonly IWebProxy _inner;

    public LoopbackBypassProxy(IWebProxy inner)
    {
        _inner = inner;
    }

    public ICredentials? Credentials
    {
        get => _inner.Credentials;
        set => _inner.Credentials = value;
    }

    public Uri? GetProxy(Uri destination)
    {
        return IsLoopback(destination) ? null : _inner.GetProxy(destination);
    }

    public bool IsBypassed(Uri host)
    {
        return IsLoopback(host) || _inner.IsBypassed(host);
    }

    private static bool IsLoopback(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return true;
        }
        if (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip))
        {
            return true;
        }
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }
}
