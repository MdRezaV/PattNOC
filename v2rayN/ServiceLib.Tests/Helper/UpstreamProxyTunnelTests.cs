using System.Net.Sockets;

namespace ServiceLib.Tests.Helper;

public class UpstreamProxyTunnelTests
{
    private static UpstreamProxyItem Socks5Item(string server, int port, string? user = null, string? pass = null)
        => new()
        {
            Enabled = true,
            ProxyType = EUpstreamProxyType.Socks5,
            Server = server,
            Port = port,
            Username = user,
            Password = pass,
        };

    private static UpstreamProxyItem HttpItem(string server, int port, string? user = null, string? pass = null)
        => new()
        {
            Enabled = true,
            ProxyType = EUpstreamProxyType.Http,
            Server = server,
            Port = port,
            Username = user,
            Password = pass,
        };

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
            {
                throw new IOException("Unexpected EOF while reading from mock proxy.");
            }
            offset += read;
        }
    }

    private static async Task EchoUntilClosedAsync(NetworkStream stream)
    {
        var buf = new byte[1024];
        while (true)
        {
            var n = await stream.ReadAsync(buf);
            if (n == 0)
            {
                break;
            }
            await stream.WriteAsync(buf.AsMemory(0, n));
        }
    }

    private static async Task<string> ReadHeadersUntilCrlfCrlfAsync(Stream stream)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await stream.ReadAsync(buffer);
            if (n == 0)
            {
                break;
            }
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        return sb.ToString();
    }

    [Test]
    public async Task ConnectAsync_Disabled_Throws()
    {
        var item = new UpstreamProxyItem { Enabled = false };
        var threw = false;
        try
        {
            await UpstreamProxyTunnel.ConnectAsync(item, "example.com", 443);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        await threw.Should().BeTrue();
    }

    [Test]
    public async Task Socks5_NoAuth_GreetingAndConnect_EchoThroughTunnel()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[]? capturedGreeting = null;
        byte[]? capturedConnect = null;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();

            var header = new byte[2];
            await ReadExactAsync(stream, header, CancellationToken.None);
            var methods = new byte[header[1]];
            await ReadExactAsync(stream, methods, CancellationToken.None);
            capturedGreeting = [header[0], header[1], .. methods];

            await stream.WriteAsync(new byte[] { 0x05, 0x00 });

            var reqHeader = new byte[4];
            await ReadExactAsync(stream, reqHeader, CancellationToken.None);
            byte[] domain = [];
            byte[] portBytes = [];
            if (reqHeader[3] == 0x03)
            {
                var lenByte = new byte[1];
                await ReadExactAsync(stream, lenByte, CancellationToken.None);
                domain = new byte[lenByte[0]];
                await ReadExactAsync(stream, domain, CancellationToken.None);
                portBytes = new byte[2];
                await ReadExactAsync(stream, portBytes, CancellationToken.None);
                capturedConnect = [.. reqHeader, .. lenByte, .. domain, .. portBytes];
            }
            else
            {
                var rest = new byte[6];
                await ReadExactAsync(stream, rest, CancellationToken.None);
                capturedConnect = [.. reqHeader, .. rest];
            }

            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
            await EchoUntilClosedAsync(stream);
        });

        var item = Socks5Item("127.0.0.1", port);
        var tcp = await UpstreamProxyTunnel.ConnectAsync(item, "example.com", 443);

        await tcp.Connected.Should().BeTrue();

        var stream = tcp.GetStream();
        var payload = Encoding.UTF8.GetBytes("ping-through-tunnel");
        await stream.WriteAsync(payload);
        var echo = new byte[payload.Length];
        await ReadExactAsync(stream, echo, CancellationToken.None);
        await Encoding.UTF8.GetString(echo).Should().BeEqualTo("ping-through-tunnel");
        tcp.Dispose();

        await serverTask;
        listener.Stop();

        await capturedGreeting.Should().NotBeNull();
        await ((int)capturedGreeting![0]).Should().BeEqualTo(0x05);
        await ((int)capturedGreeting[1]).Should().BeEqualTo(0x01);
        await ((int)capturedGreeting[2]).Should().BeEqualTo(0x00);

        await capturedConnect.Should().NotBeNull();
        var connect = capturedConnect!;
        await ((int)connect[0]).Should().BeEqualTo(0x05);
        await ((int)connect[1]).Should().BeEqualTo(0x01);
        await ((int)connect[2]).Should().BeEqualTo(0x00);
        await ((int)connect[3]).Should().BeEqualTo(0x03);
        var domainLen = (int)connect[4];
        var domain = Encoding.UTF8.GetString(connect, 5, domainLen);
        await domain.Should().BeEqualTo("example.com");
        var parsedPort = (connect[5 + domainLen] << 8) | connect[6 + domainLen];
        await parsedPort.Should().BeEqualTo(443);
    }

    [Test]
    public async Task Socks5_WithAuth_Rfc1929_Subnegotiation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        byte[]? capturedAuth = null;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();

            var header = new byte[2];
            await ReadExactAsync(stream, header, CancellationToken.None);
            var methods = new byte[header[1]];
            await ReadExactAsync(stream, methods, CancellationToken.None);

            await stream.WriteAsync(new byte[] { 0x05, 0x02 });

            var authVer = new byte[2];
            await ReadExactAsync(stream, authVer, CancellationToken.None);
            var user = new byte[authVer[1]];
            await ReadExactAsync(stream, user, CancellationToken.None);
            var plenByte = new byte[1];
            await ReadExactAsync(stream, plenByte, CancellationToken.None);
            var pass = new byte[plenByte[0]];
            await ReadExactAsync(stream, pass, CancellationToken.None);
            capturedAuth = [.. user, .. pass];

            await stream.WriteAsync(new byte[] { 0x01, 0x00 });

            var reqHeader = new byte[4];
            await ReadExactAsync(stream, reqHeader, CancellationToken.None);
            var rest = new byte[6];
            await ReadExactAsync(stream, rest, CancellationToken.None);

            await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
            await EchoUntilClosedAsync(stream);
        });

        var item = Socks5Item("127.0.0.1", port, "alice", "s3cret");
        var tcp = await UpstreamProxyTunnel.ConnectAsync(item, "203.0.113.7", 8443);

        var stream = tcp.GetStream();
        var payload = Encoding.UTF8.GetBytes("authed");
        await stream.WriteAsync(payload);
        var echo = new byte[payload.Length];
        await ReadExactAsync(stream, echo, CancellationToken.None);
        await Encoding.UTF8.GetString(echo).Should().BeEqualTo("authed");
        tcp.Dispose();

        await serverTask;
        listener.Stop();

        await capturedAuth.Should().NotBeNull();
        await Encoding.UTF8.GetString(capturedAuth!).Should().BeEqualTo("alices3cret");
    }

    [Test]
    public async Task Socks5_WrongVersionFromServer_Throws()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var header = new byte[2];
            await ReadExactAsync(stream, header, CancellationToken.None);
            var methods = new byte[header[1]];
            await ReadExactAsync(stream, methods, CancellationToken.None);
            await stream.WriteAsync(new byte[] { 0x04, 0x00 });
        });

        var item = Socks5Item("127.0.0.1", port);
        var threw = false;
        try
        {
            await UpstreamProxyTunnel.ConnectAsync(item, "example.com", 443);
        }
        catch (IOException)
        {
            threw = true;
        }
        await threw.Should().BeTrue();

        await serverTask;
        listener.Stop();
    }

    [Test]
    public async Task Http_Connect_EchoThroughTunnel()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string? capturedRequest = null;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            capturedRequest = await ReadHeadersUntilCrlfCrlfAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));
            await EchoUntilClosedAsync(stream);
        });

        var item = HttpItem("127.0.0.1", port, "bob", "pw");
        var tcp = await UpstreamProxyTunnel.ConnectAsync(item, "target.example.com", 8443);

        var stream = tcp.GetStream();
        var payload = Encoding.UTF8.GetBytes("via-http-connect");
        await stream.WriteAsync(payload);
        var echo = new byte[payload.Length];
        await ReadExactAsync(stream, echo, CancellationToken.None);
        await Encoding.UTF8.GetString(echo).Should().BeEqualTo("via-http-connect");
        tcp.Dispose();

        await serverTask;
        listener.Stop();

        await capturedRequest.Should().NotBeNull();
        await capturedRequest!.Should().StartWith("CONNECT target.example.com:8443 HTTP/1.1\r\n");
        await capturedRequest!.Should().Contain("Host: target.example.com:8443");
        var expectedAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes("bob:pw"));
        await capturedRequest!.Should().Contain($"Proxy-Authorization: Basic {expectedAuth}");
    }

    [Test]
    public async Task Http_ErrorStatus_Throws()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            _ = await ReadHeadersUntilCrlfCrlfAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"));
        });

        var item = HttpItem("127.0.0.1", port);
        var threw = false;
        try
        {
            await UpstreamProxyTunnel.ConnectAsync(item, "example.com", 443);
        }
        catch (IOException)
        {
            threw = true;
        }
        await threw.Should().BeTrue();

        await serverTask;
        listener.Stop();
    }
}
