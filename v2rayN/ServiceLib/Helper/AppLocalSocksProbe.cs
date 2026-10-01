namespace ServiceLib.Helper;

/// <summary>
/// Probes whether a local SOCKS5 inbound is ready by sending a SOCKS5 greeting.
/// Shared by DownloadService, AppProxyResolver and OpenCode ActiveProxyProvider.
/// </summary>
public static class AppLocalSocksProbe
{
    public static async Task<bool> IsAvailableAsync(int port, CancellationToken ct = default)
    {
        if (port <= 0)
        {
            return false;
        }
        try
        {
            using var rootTimeOutCts = new CancellationTokenSource(Global.LocalFetch);
            using var rootCts = CancellationTokenSource.CreateLinkedTokenSource(ct, rootTimeOutCts.Token);
            var rootToken = rootCts.Token;

            // SOCKS5 client greeting: VER=5, NMETHODS=1, METHOD=0x00 (no auth)
            ReadOnlyMemory<byte> greeting = new byte[] { 0x05, 0x01, 0x00 };
            var buf = new byte[2];

            while (!rootToken.IsCancellationRequested)
            {
                using var tcp = new TcpClient();
                using var attemptCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(rootToken, attemptCts.Token);
                var linkedToken = linkedCts.Token;
                try
                {
                    await tcp.ConnectAsync(Global.Loopback, port, linkedToken);
                    var stream = tcp.GetStream();

                    await stream.WriteAsync(greeting, linkedToken);

                    var read = await stream.ReadAsync(buf.AsMemory(0, 2), linkedToken);

                    // Server selection: VER=5 — proxy is ready enough for a client to dial
                    if (read == 2 && buf[0] == 0x05)
                    {
                        return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!rootToken.IsCancellationRequested)
                    {
                        continue;
                    }
                    Logging.SaveLog($"AppLocalSocksProbe: timeout waiting for socks port {port}.");
                    return false;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    try
                    {
                        await Task.Delay(50, rootToken);
                    }
                    catch (OperationCanceledException)
                    {
                        Logging.SaveLog($"AppLocalSocksProbe: timeout waiting for socks port {port}.");
                        return false;
                    }
                }
                catch
                {
                    // Ignore other exceptions and continue
                }
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AppLocalSocksProbe", ex);
            return false;
        }
    }
}
