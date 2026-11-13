using System.Net.Http.Headers;
using System.Text;

namespace VortexDownloader.Core.Network;

/// <summary>
/// HTTP Basic credentials tied to one origin (scheme://host[:port]). <see cref="HeaderFor"/> returns a header only for
/// that origin, so passing the credential along to mirrors, redirect targets or refreshed links never leaks it.
/// </summary>
public sealed class HttpCredential(string origin, string user, string password)
{
    public string Origin { get; } = origin;
    public string User { get; } = user;
    private readonly string _password = password;

    /// <summary>Credential for the origin of <paramref name="url"/>, or null when there is no user name or the URL is not absolute.</summary>
    public static HttpCredential? For(string url, string? user, string? password) =>
        !string.IsNullOrEmpty(user) && OriginOf(url) is { } origin ? new(origin, user, password ?? "") : null;

    /// <summary>scheme://host[:port] without user info (default port omitted), or null.</summary>
    public static string? OriginOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) : null;

    public AuthenticationHeaderValue? HeaderFor(string url) =>
        string.Equals(OriginOf(url), Origin, StringComparison.OrdinalIgnoreCase)
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{_password}")))
            : null;

    /// <summary>Removes "user:pass@" from <paramref name="url"/> (so it never reaches cards or logs) and returns the unescaped parts.</summary>
    public static string StripUserInfo(string url, out string? user, out string? password)
    {
        user = password = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length == 0) return url;

        string info = uri.UserInfo;
        int colon = info.IndexOf(':'); // the user name cannot hold an unescaped ':', the password can
        user = Uri.UnescapeDataString(colon < 0 ? info : info[..colon]);
        password = colon < 0 ? null : Uri.UnescapeDataString(info[(colon + 1)..]);
        return uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped);
    }
}
