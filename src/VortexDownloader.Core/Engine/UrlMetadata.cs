using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using VortexDownloader.Core.Network;

namespace VortexDownloader.Core.Engine;

public record UrlMetadata(
    string Url,
    string SuggestedFileName,
    long ContentLength,
    bool SupportsRange,
    string? ETag,
    string? LastModified = null,
    bool AuthRequired = false);

public static class UrlMetadataInspector
{
    public static async Task<UrlMetadata> InspectAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken = default,
        string? cookies = null,
        string? referrer = null,
        string? userAgent = null,
        HttpCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        void ApplyHeaders(HttpRequestMessage req, string? c, string? r, string? ua)
        {
            req.Headers.Authorization = credential?.HeaderFor(url);
            req.Headers.Accept.ParseAdd("*/*");
            req.Headers.AcceptLanguage.ParseAdd("tr-TR,tr;q=0.9,en-US;q=0.8,en;q=0.7");
            if (!string.IsNullOrWhiteSpace(c)) req.Headers.TryAddWithoutValidation("Cookie", c);
            if (!string.IsNullOrWhiteSpace(r)) req.Headers.TryAddWithoutValidation("Referer", r);
            if (!string.IsNullOrWhiteSpace(ua))
            {
                req.Headers.UserAgent.Clear();
                req.Headers.UserAgent.ParseAdd(ua);
            }
        }

        string fileName = ExtractFileNameFromUrl(url);
        long contentLength = -1;
        bool supportsRange = false;
        string? etag = null;
        string? lastModified = null;
        bool authRequired = false;

        // Try HEAD first
        HttpResponseMessage? headResponse = null;
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            ApplyHeaders(headRequest, cookies, referrer, userAgent);
            headResponse = await httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            authRequired = headResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized;
            if (headResponse.IsSuccessStatusCode)
            {
                etag = headResponse.Headers.ETag?.Tag;
                if (headResponse.Content.Headers.LastModified.HasValue)
                {
                    lastModified = headResponse.Content.Headers.LastModified.Value.ToString("R");
                }
                if (headResponse.Content.Headers.ContentLength.HasValue)
                {
                    contentLength = headResponse.Content.Headers.ContentLength.Value;
                }

                if (headResponse.Headers.AcceptRanges.Contains("bytes"))
                {
                    supportsRange = true;
                }

                string? dispFileName = ExtractFileNameFromContentDisposition(headResponse.Content.Headers.ContentDisposition);
                if (!string.IsNullOrEmpty(dispFileName))
                {
                    fileName = dispFileName;
                }
            }
        }
        catch
        {
            // Fall back to GET with 0-0 range
        }
        finally
        {
            headResponse?.Dispose();
        }

        // If HEAD didn't confirm range or length, test with a 0-0 GET range request
        if (!supportsRange || contentLength <= 0)
        {
            try
            {
                using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyHeaders(getRequest, cookies, referrer, userAgent);
                getRequest.Headers.Range = new RangeHeaderValue(0, 0);

                using var getResponse = await httpClient.SendAsync(
                    getRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                authRequired = getResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized;
                if (getResponse.StatusCode == System.Net.HttpStatusCode.PartialContent)
                {
                    supportsRange = true;
                    if (getResponse.Content.Headers.ContentRange?.Length.HasValue == true)
                    {
                        contentLength = getResponse.Content.Headers.ContentRange.Length.Value;
                    }
                }
                else if (getResponse.IsSuccessStatusCode)
                {
                    // Server responded with 200 OK (ignored range)
                    supportsRange = false;
                    if (getResponse.Content.Headers.ContentLength.HasValue)
                    {
                        contentLength = getResponse.Content.Headers.ContentLength.Value;
                    }
                }

                if (string.IsNullOrEmpty(etag))
                {
                    etag = getResponse.Headers.ETag?.Tag;
                }

                string? dispFileName = ExtractFileNameFromContentDisposition(getResponse.Content.Headers.ContentDisposition);
                if (!string.IsNullOrEmpty(dispFileName))
                {
                    fileName = dispFileName;
                }
            }
            catch
            {
                // Accept what we have
            }
        }

        return new UrlMetadata(
            url,
            CleanFileName(fileName),
            contentLength,
            supportsRange,
            etag,
            lastModified,
            authRequired);
    }

    private static string ExtractFileNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            string segment = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(segment))
            {
                return Uri.UnescapeDataString(segment);
            }
        }
        catch
        {
            // Ignore
        }

        return "download.bin";
    }

    private static string? ExtractFileNameFromContentDisposition(ContentDispositionHeaderValue? disposition)
    {
        if (disposition == null) return null;

        if (!string.IsNullOrWhiteSpace(disposition.FileNameStar))
        {
            return disposition.FileNameStar.Trim('\"');
        }

        if (!string.IsNullOrWhiteSpace(disposition.FileName))
        {
            return disposition.FileName.Trim('\"');
        }

        return null;
    }

    public static string CleanFileName(string? rawFileName)
    {
        if (string.IsNullOrWhiteSpace(rawFileName)) return "download.bin";
        string safeName = Path.GetFileName(rawFileName.Trim());
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char c in invalid)
        {
            safeName = safeName.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(safeName) ? "download.bin" : safeName;
    }
}
