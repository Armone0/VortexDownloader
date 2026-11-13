namespace VortexDownloader.Core.Common;

/// <summary>Decides whether copied text is a direct file link worth offering as a download.</summary>
public static class ClipboardLinkFilter
{
    public const int MaxLength = 2048;

    /// <summary>Single-line absolute http(s) URL whose path ends in a known file extension; plain pages are rejected.</summary>
    public static bool TryGetDownloadUrl(string? text, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        if (t.Length > MaxLength || t.Any(char.IsWhiteSpace)) return false;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
        // Extension of the path only: "page?f=a.zip" and "https://foo.zip/" are web pages, not files.
        if (FileCategories.GetCategory(Uri.UnescapeDataString(uri.AbsolutePath)) == null) return false;
        url = uri.AbsoluteUri;
        return true;
    }
}
