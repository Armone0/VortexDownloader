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
                ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
                return new WebProxy(host.Trim(), port)
                {
                    BypassProxyOnLocal = true,
                    BypassList = [PrivateNetworkBypass],
                    Credentials = string.IsNullOrEmpty(user) ? null : new NetworkCredential(user, password)
                };
            default:
                return null;
        }
    }
}
