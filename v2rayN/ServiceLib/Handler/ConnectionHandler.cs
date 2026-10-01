namespace ServiceLib.Handler;

public static class ConnectionHandler
{
    private static readonly string _tag = "ConnectionHandler";

    /// <summary>
    /// Runs ping and IP checks against the active configuration egress.
    /// Uses the local core only — never the first-hop upstream — so the reported
    /// IP is the configuration exit IP, not the proxy's own address.
    /// </summary>
    public static async Task<AvailabilityCheckResult> RunAvailabilityCheck()
    {
        var webProxy = await GetWebProxy();
        if (webProxy is null)
        {
            // Core not running: no configuration egress to report
            return new AvailabilityCheckResult(-1, Global.None);
        }

        var time = -1;
        try
        {
            for (var i = 0; i < 2; i++)
            {
                time = await GetRealPingTime(webProxy);
                if (time > 0)
                {
                    break;
                }
                await Task.Delay(500);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return new AvailabilityCheckResult(-1, Global.None);
        }

        var ip = Global.None;
        if (time > 0)
        {
            var ipInfo = await GetIPInfo(webProxy);
            ip = ipInfo?.ToString() ?? Global.None;
        }
        return new AvailabilityCheckResult(time, ip);
    }

    /// <summary>
    /// Resolves the proxy for ping/IP checks: local core SOCKS only.
    /// Does not fall back to the first-hop upstream — that would report the proxy IP.
    /// </summary>
    private static Task<IWebProxy?> GetWebProxy()
    {
        return AppProxyResolver.Instance.ResolveLocalCoreOnlyAsync();
    }

    /// <summary>
    /// Measures response time by sending HTTP requests through proxy.
    /// </summary>
    public static async Task<int> GetRealPingTime(IWebProxy? webProxy, CancellationToken cancellationToken = default)
    {
        var url = AppManager.Instance.Config.SpeedTestItem.SpeedPingTestUrl;
        var responseTime = -1;
        try
        {
            using var timeoutCts = new CancellationTokenSource();
            timeoutCts.CancelAfter(Global.LocalFetch);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var linkedToken = linkedCts.Token;
            using var client = new HttpClient(new SocketsHttpHandler()
            {
                Proxy = webProxy,
                UseProxy = webProxy != null,
                ConnectTimeout = Global.LocalFetch,
            });

            List<int> oneTime = [];
            for (var i = 0; i < 2; i++)
            {
                var timer = Stopwatch.StartNew();
                await client.GetAsync(url, linkedToken).ConfigureAwait(false);
                timer.Stop();
                oneTime.Add((int)timer.Elapsed.TotalMilliseconds);
                await Task.Delay(100, linkedToken);
            }
            responseTime = oneTime.Where(x => x > 0).OrderBy(x => x).FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Ignore
        }
        return responseTime;
    }

    /// <summary>
    /// Gets IP and country information through specified proxy.
    /// </summary>
    public static async Task<IpInfoResult?> GetIPInfo(IWebProxy? webProxy, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = AppManager.Instance.Config.SpeedTestItem.IPAPIUrl;
            if (url.IsNullOrEmpty())
            {
                return null;
            }

            var downloadHandle = new DownloadService();
            var result = await downloadHandle.TryDownloadString(url, webProxy, "", cancellationToken);
            if (result == null)
            {
                return null;
            }

            var ipInfo = JsonUtils.Deserialize<IPAPIInfo>(result);
            if (ipInfo == null)
            {
                return null;
            }

            var ip = ipInfo.ip ?? ipInfo.clientIp ?? ipInfo.ip_addr ?? ipInfo.query;
            var country = ipInfo.country_code ?? ipInfo.country ?? ipInfo.countryCode ?? ipInfo.location?.country_code ?? "unknown";

            return new IpInfoResult(country, ip);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
