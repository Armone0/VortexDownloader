using System.Net;

namespace VortexDownloader.Core.Network;

public enum ProxyMode { System, None, Manual }

/// <summary>Builds the process-wide proxy (<c>HttpClient.DefaultProxy</c>) from the user's settings.</summary>
public static class ProxyFactory
{
    // WebProxy matches bypass regexes against "scheme://host[:port]". Loopback and dotless names are covered by BypassProxyOnLocal;
    // private LAN ranges stay direct so the browser bridge, Vortex Send and NAS downloads keep working.
    public const string PrivateNetworkBypass =
        @"^[a-z]+://(localhost|127(\.\d{1,3}){3}|10(\.\d{1,3}){3}|192\.168(\.\d{1,3}){2}|172\.(1[6-9]|2\d|3[01])(\.\d{1,3}){2}|169\.254(\.\d{1,3}){2}|\[(::1|f[cd][0-9a-f]*:[0-9a-f:]*|fe80:[0-9a-f:%]*)\])(:\d+)?$";

    /// <summary>
    /// Null for <see cref="ProxyMode.System"/> (keep the Windows proxy). None = direct connections.
    /// Manual throws <see cref="ArgumentException"/>/<see cref="UriFormatException"/> on a bad host or port.
    /// </summary>
    public static IWebProxy? Create(ProxyMode mode, string? host, int port, string? user = null, string? password = null)
    {
        switch (mode)
        {
            case ProxyMode.None:
                return new WebProxy(); // no address: every request goes direct
            case ProxyMode.Manual:
                ArgumentException.ThrowIfNullOrWhiteSpace(host);
                host = host.Trim();
                // A bare host name or IPv4 only: "http://proxy" or "proxy:3128" would silently become a proxy on the wrong host.
                if (Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
                    throw new ArgumentException("Proxy host must be a host name or IPv4 address.", nameof(host));
                ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
                return new WebProxy(host, port)
                {
                    BypassProxyOnLocal = true,
                    BypassList = [PrivateNetworkBypass],
                    Credentials = string.IsNullOrEmpty(user) ? null : new NetworkCredential(user, password)
                };
            default:
                return null;
        }
    }

    /// <summary>Proxy environment handed to child programs (yt-dlp); null leaves them on the inherited/Windows proxy. Set once at startup.</summary>
    public static IReadOnlyDictionary<string, string>? ChildProcessEnvironment { get; set; }

    /// <summary>
    /// Env vars that make child programs follow the same choice: None = direct (NO_PROXY=*), Manual = HTTP(S)_PROXY.
    /// Env, not a --proxy argument, so the proxy password never shows in a process command line. Call after <see cref="Create"/> validated the input.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? BuildChildEnvironment(ProxyMode mode, string? host, int port, string? user = null, string? password = null)
    {
        switch (mode)
        {
            case ProxyMode.None:
                return new Dictionary<string, string> { ["HTTP_PROXY"] = "", ["HTTPS_PROXY"] = "", ["ALL_PROXY"] = "", ["NO_PROXY"] = "*" };
            case ProxyMode.Manual:
                string auth = string.IsNullOrEmpty(user) ? "" :
                    Uri.EscapeDataString(user) + (string.IsNullOrEmpty(password) ? "" : ":" + Uri.EscapeDataString(password)) + "@";
                string url = $"http://{auth}{host!.Trim()}:{port}";
                return new Dictionary<string, string>
                {
                    ["HTTP_PROXY"] = url, ["HTTPS_PROXY"] = url, ["ALL_PROXY"] = "",
                    ["NO_PROXY"] = "localhost,127.0.0.1,::1,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16"
                };
            default:
                return null;
        }
    }
}
