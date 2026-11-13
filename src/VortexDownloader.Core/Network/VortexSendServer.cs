using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VortexDownloader.Core.Network;

/// <summary>
/// "Vortex Send": a tiny HTTP/1.1 server on the LAN that lets a phone hand a link to the PC.
/// Lives only while its window is open. Every link still has to be confirmed on the PC.
/// Defenses: random 128-bit path token (constant-time compare, lockout after bad guesses),
/// RFC1918 clients only, strict size/time/connection caps, no keep-alive, escaped HTML, strict CSP.
/// </summary>
public sealed class VortexSendServer : IDisposable
{
    public const int MaxHeaderBytes = 8 * 1024;
    public const int MaxBodyBytes = 8 * 1024;
    public const int MaxUrlLength = 4 * 1024;
    public const int MaxConnections = 4;
    public const int MaxBadAttempts = 10;
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private const string Csp = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    private readonly TcpListener _listener;
    private readonly IPAddress _bindAddress;
    private readonly Action<string> _onUrl;
    private readonly Func<IPAddress, bool> _isAllowedClient;
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _tokenBytes;
    private string _expectedHost = "";
    private Task? _acceptLoop;
    private int _active;
    private int _badAttempts;
    private int _disposed;

    /// <summary>Raised once (from a worker thread) when too many wrong tokens arrived; the server then answers 404 to everything.</summary>
    public event Action? LockedOut;

    public string Token { get; }
    public int Port { get; private set; }
    public string Url => $"http://{_expectedHost}/s/{Token}";
    public int ActiveConnections => Volatile.Read(ref _active);
    public bool IsLockedOut => Volatile.Read(ref _badAttempts) >= MaxBadAttempts;

    /// <param name="isAllowedClient">Client address filter; defaults to <see cref="IsPrivateAddress"/>. Tests inject a loopback filter.</param>
    public VortexSendServer(IPAddress bindAddress, Action<string> onUrl, Func<IPAddress, bool>? isAllowedClient = null)
    {
        _bindAddress = bindAddress;
        _onUrl = onUrl;
        _isAllowedClient = isAllowedClient ?? IsPrivateAddress;
        Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _tokenBytes = Encoding.ASCII.GetBytes(Token);
        _listener = new TcpListener(bindAddress, 0) { ExclusiveAddressUse = true };
    }

    public void Start()
    {
        _listener.Start(MaxConnections);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _expectedHost = $"{_bindAddress}:{Port}";
        var ct = _cts.Token;
        // Task.Run: keep the loop off the UI SynchronizationContext so Dispose can wait for it without deadlocking.
        _acceptLoop = Task.Run(() => AcceptLoopAsync(ct));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _listener.Dispose();
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }

    // ---------------------------------------------------------------- helpers (public for tests)

    /// <summary>True for RFC1918 IPv4 addresses (10/8, 172.16/12, 192.168/16), including IPv4-mapped IPv6.</summary>
    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>The private IPv4 address of the first active interface that has a default gateway, or null.</summary>
    public static IPAddress? FindLanAddress()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = nic.GetIPProperties();
            if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
            var address = props.UnicastAddresses.Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivateAddress(a));
            if (address != null) return address;
        }
        return null;
    }

    /// <summary>Accepts only absolute http/https URLs up to <see cref="MaxUrlLength"/> chars, without user-info or control chars.</summary>
    public static bool TryValidateUrl(string? raw, out string url)
    {
        url = "";
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxUrlLength || raw.Any(char.IsControl)) return false;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        url = uri.AbsoluteUri;
        return url.Length <= MaxUrlLength;
    }

    public static string BuildFormPage() => Page("Vortex Send",
        "<h1>Vortex Send</h1><p>Bağlantıyı yapıştır ve gönder. İndirme bilgisayarda onaylanınca başlar.</p>" +
        "<form method=\"post\"><input name=\"url\" type=\"url\" inputmode=\"url\" required maxlength=\"" + MaxUrlLength +
        "\" placeholder=\"https://…\" autocomplete=\"off\" autofocus><button type=\"submit\">Gönder</button></form>");

    public static string BuildSentPage(string url, string token) => Page("Gönderildi",
        "<h1>Gönderildi ✓</h1><p>Bilgisayarda onayla.</p><p class=\"u\">" + WebUtility.HtmlEncode(url) + "</p>" +
        "<p><a href=\"" + WebUtility.HtmlEncode(token) + "\">Başka bağlantı gönder</a></p>");

    public static string BuildErrorPage(string message) => Page("Hata",
        "<h1>Gönderilemedi</h1><p>" + WebUtility.HtmlEncode(message) + "</p>");

    private static string Page(string title, string body) =>
        "<!doctype html><html lang=\"tr\"><head><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + WebUtility.HtmlEncode(title) + "</title>" +
        "<style>body{font-family:system-ui,sans-serif;background:#0f172a;color:#e2e8f0;margin:0;padding:24px}" +
        "main{max-width:480px;margin:0 auto}h1{font-size:22px}input,button{width:100%;box-sizing:border-box;font-size:17px;" +
        "padding:14px;border-radius:10px;border:1px solid #334155;margin-top:10px}input{background:#1e293b;color:#e2e8f0}" +
        "button{background:#6366f1;color:#fff;border:0;font-weight:600}.u{word-break:break-all;color:#94a3b8}a{color:#a5b4fc}</style>" +
        "</head><body><main>" + body + "</main></body></html>";

    // ---------------------------------------------------------------- connection handling

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (ct.IsCancellationRequested) break; continue; }

            if (Interlocked.Increment(ref _active) > MaxConnections)
            {
                Interlocked.Decrement(ref _active);
                client.Dispose();
                continue;
            }
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        try
        {
            using (client)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
            {
                deadline.CancelAfter(ReadTimeout);
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !_isAllowedClient(remote.Address)) return;

                var stream = client.GetStream();
                byte[]? response = await ProcessAsync(stream, deadline.Token);
                if (response == null) return;
                await stream.WriteAsync(response, deadline.Token);
                client.Client.Shutdown(SocketShutdown.Send);

                // Lingering close: swallow what the client still sends so the response is not lost to a TCP reset.
                deadline.CancelAfter(TimeSpan.FromSeconds(1));
                var sink = new byte[1024];
                for (int total = 0; total < 64 * 1024;)
                {
                    int n = await stream.ReadAsync(sink, deadline.Token);
                    if (n == 0) break;
                    total += n;
                }
            }
        }
        catch { /* client went away, timed out or server stopped: nothing to report */ }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private async Task<byte[]?> ProcessAsync(NetworkStream stream, CancellationToken ct)
    {
        // 1. Request line + headers, capped at MaxHeaderBytes.
        var buffer = new byte[MaxHeaderBytes];
        int length = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (length == buffer.Length) return Error(431, "Request Header Fields Too Large", "İstek çok büyük.");
            int n = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (n == 0) return null;
            int from = Math.Max(0, length - 3);
            length += n;
            int idx = buffer.AsSpan(from, length - from).IndexOf("\r\n\r\n"u8);
            if (idx >= 0) headerEnd = from + idx;
        }

        if (IsLockedOut) return NotFound();

        string[] lines = Encoding.Latin1.GetString(buffer, 0, headerEnd).Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || (requestLine[2] != "HTTP/1.1" && requestLine[2] != "HTTP/1.0"))
            return Error(400, "Bad Request", "Geçersiz istek.");
        string method = requestLine[0], target = requestLine[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) return Error(400, "Bad Request", "Geçersiz istek.");
            string name = lines[i][..colon].Trim();
            if (!headers.TryAdd(name, lines[i][(colon + 1)..].Trim())) return Error(400, "Bad Request", "Geçersiz istek.");
        }

        // DNS-rebinding guard: the browser must address us by the exact IP:port in the QR code.
        if (!headers.TryGetValue("Host", out var host) || host != _expectedHost) return Error(400, "Bad Request", "Geçersiz istek.");

        // 2. Path token: /s/{token}. Only /s/ guesses count toward lockout (browsers fetch /favicon.ico on their own).
        if (!target.StartsWith("/s/", StringComparison.Ordinal)) return NotFound();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(target[3..]), _tokenBytes))
        {
            if (Interlocked.Increment(ref _badAttempts) == MaxBadAttempts) LockedOut?.Invoke();
            return NotFound();
        }

        if (method == "GET") return Respond(200, "OK", "text/html; charset=utf-8", BuildFormPage());
        if (method != "POST") return Respond(405, "Method Not Allowed", "text/plain; charset=utf-8", "", "Allow: GET, POST\r\n");

        if (headers.TryGetValue("Origin", out var origin) && origin != "http://" + _expectedHost)
            return Error(403, "Forbidden", "İzin verilmeyen kaynak.");

        // 3. Body, capped at MaxBodyBytes. No chunked encoding.
        if (headers.ContainsKey("Transfer-Encoding")) return Error(400, "Bad Request", "Geçersiz istek.");
        if (!headers.TryGetValue("Content-Length", out var clText)) return Error(411, "Length Required", "Geçersiz istek.");
        if (!int.TryParse(clText, System.Globalization.NumberStyles.None, null, out int contentLength))
            return Error(400, "Bad Request", "Geçersiz istek.");
        if (contentLength > MaxBodyBytes) return Error(413, "Content Too Large", "Bağlantı çok uzun.");

        var body = new byte[contentLength];
        int have = Math.Min(length - (headerEnd + 4), contentLength);
        Array.Copy(buffer, headerEnd + 4, body, 0, have);
        await stream.ReadExactlyAsync(body.AsMemory(have), ct);

        string contentType = headers.GetValueOrDefault("Content-Type", "").Split(';')[0].Trim().ToLowerInvariant();
        bool json = contentType == "application/json";
        string? rawUrl;
        if (json)
        {
            try
            {
                using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
                rawUrl = doc.RootElement.ValueKind == JsonValueKind.Object &&
                         doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            }
            catch (JsonException) { rawUrl = null; }
        }
        else if (contentType == "application/x-www-form-urlencoded")
        {
            rawUrl = ReadFormField(Encoding.UTF8.GetString(body), "url");
        }
        else return Error(415, "Unsupported Media Type", "Geçersiz istek.");

        if (!TryValidateUrl(rawUrl, out var url))
        {
            return json
                ? Respond(400, "Bad Request", "application/json", "{\"status\":\"invalid_url\"}")
                : Error(400, "Bad Request", "Yalnızca http:// veya https:// bağlantıları gönderilebilir.");
        }

        if (Volatile.Read(ref _disposed) != 0) return null;
        _onUrl(url);
        return json
            ? Respond(200, "OK", "application/json", "{\"status\":\"sent\",\"message\":\"Gönderildi, bilgisayarda onayla\"}")
            : Respond(200, "OK", "text/html; charset=utf-8", BuildSentPage(url, Token));
    }

    private static string? ReadFormField(string form, string name)
    {
        foreach (var pair in form.Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0 || !pair.AsSpan(0, eq).SequenceEqual(name)) continue;
            try { return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')); }
            catch (UriFormatException) { return null; }
        }
        return null;
    }

    private static byte[] NotFound() => Error(404, "Not Found", "Bulunamadı.");

    private static byte[] Error(int status, string reason, string message) =>
        Respond(status, reason, "text/html; charset=utf-8", BuildErrorPage(message));

    private static byte[] Respond(int status, string reason, string contentType, string body, string extraHeaders = "")
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string head =
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            $"Content-Security-Policy: {Csp}\r\n" +
            extraHeaders + "\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        var result = new byte[headBytes.Length + bodyBytes.Length];
        headBytes.CopyTo(result, 0);
        bodyBytes.CopyTo(result, headBytes.Length);
        return result;
    }
}
