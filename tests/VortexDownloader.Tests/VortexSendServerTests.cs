using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VortexDownloader.Core.Network;

namespace VortexDownloader.Tests;

public class VortexSendServerTests : IDisposable
{
    private readonly ConcurrentQueue<string> _received = new();
    private readonly VortexSendServer _server;

    public VortexSendServerTests()
    {
        // Loopback stands in for the LAN; the private-address check is injected.
        _server = new VortexSendServer(IPAddress.Loopback, _received.Enqueue, IPAddress.IsLoopback);
        _server.Start();
    }

    public void Dispose() => _server.Dispose();

    private string Host => $"127.0.0.1:{_server.Port}";
    private string TokenPath => $"/s/{_server.Token}";

    private async Task<string> SendRawAsync(string request, VortexSendServer? server = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, (server ?? _server).Port);
        var stream = client.GetStream();
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            // The server may drop us (cap, disallowed client) before or while we write.
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request), cts.Token);
            client.Client.Shutdown(SocketShutdown.Send);
            await stream.CopyToAsync(ms, cts.Token);
        }
        catch (Exception e) when (e is IOException or SocketException) { }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private Task<string> PostAsync(string path, string contentType, string body) => SendRawAsync(
        $"POST {path} HTTP/1.1\r\nHost: {Host}\r\nContent-Type: {contentType}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");

    private async Task WaitForIdleAsync()
    {
        for (int i = 0; i < 100 && _server.ActiveConnections != 0; i++) await Task.Delay(50);
        Assert.Equal(0, _server.ActiveConnections);
    }

    [Fact]
    public async Task FormPost_WithValidToken_InvokesCallback()
    {
        string resp = await PostAsync(TokenPath, "application/x-www-form-urlencoded", "url=" + Uri.EscapeDataString("https://example.com/file.zip"));
        Assert.StartsWith("HTTP/1.1 200", resp);
        Assert.Contains("Gönderildi", resp);
        Assert.Equal(new[] { "https://example.com/file.zip" }, _received);
    }

    [Fact]
    public async Task JsonPost_WithValidToken_InvokesCallback()
    {
        string resp = await PostAsync(TokenPath, "application/json; charset=utf-8", "{\"url\":\"http://example.com/a.iso\"}");
        Assert.StartsWith("HTTP/1.1 200", resp);
        Assert.Contains("\"sent\"", resp);
        Assert.Equal(new[] { "http://example.com/a.iso" }, _received);
    }

    [Fact]
    public async Task Get_ServesPageWithSecurityHeaders()
    {
        string resp = await SendRawAsync($"GET {TokenPath} HTTP/1.1\r\nHost: {Host}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", resp);
        Assert.Contains("Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; form-action 'self'", resp);
        Assert.Contains("X-Content-Type-Options: nosniff", resp);
        Assert.Contains("Cache-Control: no-store", resp);
        Assert.Contains("Connection: close", resp);
        Assert.Contains("<form method=\"post\">", resp);
    }

    [Fact]
    public async Task WrongToken_Returns404_AndDoesNotInvokeCallback()
    {
        string resp = await PostAsync("/s/" + new string('0', 32), "application/x-www-form-urlencoded", "url=https%3A%2F%2Fexample.com%2F");
        Assert.StartsWith("HTTP/1.1 404", resp);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task TooManyWrongTokens_LocksOutEvenTheValidToken()
    {
        int lockedOut = 0;
        _server.LockedOut += () => Interlocked.Increment(ref lockedOut);
        for (int i = 0; i < VortexSendServer.MaxBadAttempts; i++)
            await SendRawAsync($"GET /s/bad{i} HTTP/1.1\r\nHost: {Host}\r\n\r\n");

        Assert.True(_server.IsLockedOut);
        Assert.Equal(1, lockedOut);
        string resp = await PostAsync(TokenPath, "application/x-www-form-urlencoded", "url=https%3A%2F%2Fexample.com%2F");
        Assert.StartsWith("HTTP/1.1 404", resp);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task WrongHost_IsRejected()
    {
        string resp = await SendRawAsync($"GET {TokenPath} HTTP/1.1\r\nHost: evil.example:{_server.Port}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400", resp);
    }

    [Fact]
    public async Task ForeignOrigin_IsRejected()
    {
        string body = "url=https%3A%2F%2Fexample.com%2F";
        string resp = await SendRawAsync(
            $"POST {TokenPath} HTTP/1.1\r\nHost: {Host}\r\nOrigin: http://evil.example\r\nContent-Type: application/x-www-form-urlencoded\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        Assert.StartsWith("HTTP/1.1 403", resp);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task OversizedHeaders_AreRejected()
    {
        string resp = await SendRawAsync($"GET {TokenPath} HTTP/1.1\r\nHost: {Host}\r\nX-Pad: {new string('a', VortexSendServer.MaxHeaderBytes)}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 431", resp);
    }

    [Fact]
    public async Task OversizedBody_IsRejected()
    {
        string body = "url=https%3A%2F%2Fexample.com%2F" + new string('a', VortexSendServer.MaxBodyBytes);
        string resp = await PostAsync(TokenPath, "application/x-www-form-urlencoded", body);
        Assert.StartsWith("HTTP/1.1 413", resp);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task ChunkedBody_IsRejected()
    {
        string resp = await SendRawAsync($"POST {TokenPath} HTTP/1.1\r\nHost: {Host}\r\nTransfer-Encoding: chunked\r\nContent-Type: application/json\r\n\r\n0\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400", resp);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/a")]
    [InlineData("not a url")]
    public async Task NonHttpUrl_IsRejected(string url)
    {
        string resp = await PostAsync(TokenPath, "application/x-www-form-urlencoded", "url=" + Uri.EscapeDataString(url));
        Assert.StartsWith("HTTP/1.1 400", resp);
        Assert.Empty(_received);
    }

    [Theory]
    [InlineData("https://example.com/a.zip", true)]
    [InlineData("  http://example.com/a  ", true)]
    [InlineData("HTTPS://EXAMPLE.COM/", true)]
    [InlineData("https://user:pass@example.com/", false)]
    [InlineData("https://example.com/a\nb", false)]
    [InlineData("data:text/html,hi", false)]
    [InlineData("/relative/path", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryValidateUrl_AcceptsOnlyHttpAndHttps(string? raw, bool expected)
    {
        Assert.Equal(expected, VortexSendServer.TryValidateUrl(raw, out _));
    }

    [Fact]
    public void TryValidateUrl_RejectsOverlongUrl()
    {
        Assert.False(VortexSendServer.TryValidateUrl("https://example.com/" + new string('a', VortexSendServer.MaxUrlLength), out _));
    }

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("::ffff:192.168.1.20", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::1", false)]
    public void IsPrivateAddress_AllowsOnlyRfc1918(string address, bool expected)
    {
        Assert.Equal(expected, VortexSendServer.IsPrivateAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task DisallowedClient_GetsNoResponse()
    {
        var received = new ConcurrentQueue<string>();
        using var server = new VortexSendServer(IPAddress.Loopback, received.Enqueue, _ => false);
        server.Start();
        string body = "url=https%3A%2F%2Fexample.com%2F";
        string resp = await SendRawAsync(
            $"POST /s/{server.Token} HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nContent-Type: application/x-www-form-urlencoded\r\nContent-Length: {body.Length}\r\n\r\n{body}", server);
        Assert.Equal("", resp);
        Assert.Empty(received);
    }

    [Fact]
    public void Pages_EscapeHtml()
    {
        string sent = VortexSendServer.BuildSentPage("https://x/\"><script>alert(1)</script>", "tok\"en");
        Assert.DoesNotContain("<script>", sent);
        Assert.Contains("&lt;script&gt;", sent);
        Assert.DoesNotContain("tok\"en", sent);

        string error = VortexSendServer.BuildErrorPage("<img src=x onerror=alert(1)>");
        Assert.DoesNotContain("<img", error);
    }

    [Fact]
    public async Task Dispose_StopsServerAndReleasesPort()
    {
        var server = new VortexSendServer(IPAddress.Loopback, _ => { }, IPAddress.IsLoopback);
        server.Start();
        int port = server.Port;
        server.Dispose();
        server.Dispose(); // idempotent

        using (var client = new TcpClient())
            await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));

        var rebind = new TcpListener(IPAddress.Loopback, port);
        rebind.Start();
        rebind.Stop();
    }

    [Fact]
    public async Task ManyRequests_LeaveNoOpenConnections()
    {
        for (int i = 0; i < 40; i++)
            await PostAsync(TokenPath, "application/x-www-form-urlencoded", $"url=https%3A%2F%2Fexample.com%2F{i}");

        // Burst above the connection cap: extra connections are dropped, none leak.
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => SendRawAsync($"GET {TokenPath} HTTP/1.1\r\nHost: {Host}\r\n\r\n")));

        await WaitForIdleAsync();
        Assert.Equal(40, _received.Count);
    }

    [Fact]
    public async Task ConnectionCap_DropsExtraClients()
    {
        var idle = new List<TcpClient>();
        try
        {
            for (int i = 0; i < VortexSendServer.MaxConnections; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, _server.Port);
                idle.Add(c);
            }
            for (int i = 0; i < 100 && _server.ActiveConnections < VortexSendServer.MaxConnections; i++) await Task.Delay(20);
            Assert.Equal(VortexSendServer.MaxConnections, _server.ActiveConnections);

            string resp = await SendRawAsync($"GET {TokenPath} HTTP/1.1\r\nHost: {Host}\r\n\r\n");
            Assert.Equal("", resp);
        }
        finally
        {
            foreach (var c in idle) c.Dispose();
        }
        await WaitForIdleAsync();
    }
}
