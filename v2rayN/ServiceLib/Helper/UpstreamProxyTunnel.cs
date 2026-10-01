using System.Net.Security;
using System.Security.Authentication;

namespace ServiceLib.Helper;

/// <summary>
/// Opens a raw TCP tunnel to host:port through the configured upstream proxy
/// (SOCKS5 CONNECT or HTTP CONNECT, with optional TLS to an Https proxy).
/// </summary>
public static class UpstreamProxyTunnel
{
    public static async Task<TcpClient> ConnectAsync(UpstreamProxyItem item, string host, int port, CancellationToken ct = default)
    {
        if (item is null || !item.IsUsable())
        {
            throw new InvalidOperationException("Upstream proxy is not configured.");
        }
        return item.ProxyType switch
        {
            EUpstreamProxyType.Socks5 => await ConnectSocks5Async(item, host, port, ct),
            EUpstreamProxyType.Http => await ConnectHttpAsync(item, host, port, ct, useTls: false),
            EUpstreamProxyType.Https => await ConnectHttpAsync(item, host, port, ct, useTls: true),
            _ => throw new NotSupportedException($"Unsupported upstream proxy type: {item.ProxyType}")
        };
    }

    private static async Task<TcpClient> ConnectSocks5Async(UpstreamProxyItem item, string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            using var timeoutCts = new CancellationTokenSource(Global.ProxyDownloadConnect);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var token = linkedCts.Token;

            await tcp.ConnectAsync(item.Server, item.Port, token);
            var stream = tcp.GetStream();

            var hasAuth = !item.Username.IsNullOrEmpty();
            // Greeting: VER=5, NMETHODS, METHODS (no-auth and/or user/pass)
            if (hasAuth)
            {
                await stream.WriteAsync(new byte[] { 0x05, 0x02, 0x00, 0x02 }, token);
            }
            else
            {
                await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, token);
            }

            var choice = new byte[2];
            await ReadExactAsync(stream, choice, token);
            if (choice[0] != 0x05)
            {
                throw new IOException($"Invalid SOCKS5 version from upstream proxy: 0x{choice[0]:X2}");
            }

            if (choice[1] == 0x02)
            {
                // Username/password subnegotiation (RFC 1929)
                var user = Encoding.UTF8.GetBytes(item.Username ?? string.Empty);
                var pass = Encoding.UTF8.GetBytes(item.Password ?? string.Empty);
                if (user.Length > 255 || pass.Length > 255)
                {
                    throw new IOException("SOCKS5 username/password too long.");
                }
                var auth = new byte[3 + user.Length + pass.Length];
                auth[0] = 0x01;
                auth[1] = (byte)user.Length;
                user.CopyTo(auth, 2);
                auth[2 + user.Length] = (byte)pass.Length;
                pass.CopyTo(auth, 3 + user.Length);
                await stream.WriteAsync(auth, token);

                var authResp = new byte[2];
                await ReadExactAsync(stream, authResp, token);
                if (authResp[1] != 0x00)
                {
                    throw new IOException("SOCKS5 upstream proxy authentication failed.");
                }
            }
            else if (choice[1] != 0x00)
            {
                throw new IOException($"SOCKS5 upstream proxy rejected method: 0x{choice[1]:X2}");
            }

            // CONNECT request
            var hostBytes = Encoding.UTF8.GetBytes(host);
            var portBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short)port));
            var request = new byte[4 + 1 + hostBytes.Length + 2];
            request[0] = 0x05; // VER
            request[1] = 0x01; // CMD = CONNECT
            request[2] = 0x00; // RSV
            if (IPAddress.TryParse(host, out var ipAddr))
            {
                var ipBytes = ipAddr.GetAddressBytes();
                request[3] = ipBytes.Length == 4 ? (byte)0x01 : (byte)0x04; // ATYP
                ipBytes.CopyTo(request, 4);
                portBytes.CopyTo(request, 4 + ipBytes.Length);
                var total = 4 + ipBytes.Length + 2;
                await stream.WriteAsync(request.AsMemory(0, total), token);

                var replyHeader = new byte[4];
                await ReadExactAsync(stream, replyHeader, token);
                if (replyHeader[1] != 0x00)
                {
                    throw new IOException($"SOCKS5 CONNECT failed with code 0x{replyHeader[1]:X2}.");
                }
                var bindAddrLen = replyHeader[3] switch { 0x01 => 4, 0x04 => 16, 0x03 => -1, _ => 0 };
                if (bindAddrLen > 0)
                {
                    var bindRest = new byte[bindAddrLen + 2];
                    await ReadExactAsync(stream, bindRest, token);
                }
            }
            else
            {
                request[3] = 0x03; // ATYP = DOMAINNAME
                request[4] = (byte)hostBytes.Length;
                hostBytes.CopyTo(request, 5);
                portBytes.CopyTo(request, 5 + hostBytes.Length);
                var total = 5 + hostBytes.Length + 2;
                await stream.WriteAsync(request.AsMemory(0, total), token);

                var replyHeader = new byte[4];
                await ReadExactAsync(stream, replyHeader, token);
                if (replyHeader[1] != 0x00)
                {
                    throw new IOException($"SOCKS5 CONNECT failed with code 0x{replyHeader[1]:X2}.");
                }
                var bindAddrLen = replyHeader[3] switch { 0x01 => 4, 0x04 => 16, 0x03 => -1, _ => 0 };
                if (bindAddrLen > 0)
                {
                    var bindRest = new byte[bindAddrLen + 2];
                    await ReadExactAsync(stream, bindRest, token);
                }
            }

            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task<TcpClient> ConnectHttpAsync(UpstreamProxyItem item, string host, int port, CancellationToken ct, bool useTls)
    {
        var tcp = new TcpClient();
        try
        {
            using var timeoutCts = new CancellationTokenSource(Global.ProxyDownloadConnect);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var token = linkedCts.Token;

            await tcp.ConnectAsync(item.Server, item.Port, token);

            Stream stream = tcp.GetStream();
            if (useTls)
            {
                var ssl = new SslStream(stream, false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = item.Server
                }, token);
                stream = ssl;
            }

            var connectRequest = $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n";
            if (!item.Username.IsNullOrEmpty())
            {
                var cred = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{item.Username}:{item.Password}"));
                connectRequest += $"Proxy-Authorization: Basic {cred}\r\n";
            }
            connectRequest += "\r\n";
            var requestBytes = Encoding.ASCII.GetBytes(connectRequest);
            await stream.WriteAsync(requestBytes, token);

            // Read response headers
            var response = await ReadHttpResponseHeadersAsync(stream, token);
            if (!response.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase)
                && !response.StartsWith("HTTP/1.0 200", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"HTTP CONNECT through upstream proxy failed: {response.Split('\r', '\n').FirstOrDefault()}");
            }

            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
            {
                throw new IOException("Upstream proxy closed the connection.");
            }
            offset += read;
        }
    }

    private static async Task<string> ReadHttpResponseHeadersAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (sb.Length < 8192)
        {
            var read = await stream.ReadAsync(buf.AsMemory(0, 1), ct);
            if (read == 0)
            {
                break;
            }
            sb.Append((char)buf[0]);
            if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n")
            {
                break;
            }
        }
        return sb.ToString();
    }
}
