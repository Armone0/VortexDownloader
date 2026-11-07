using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace VortexDownloader.Core.Engine;

public record UrlMetadata(
    string Url,
    string SuggestedFileName,
    long ContentLength,
    bool SupportsRange,
    string? ETag);

public static class UrlMetadataInspector
{
    public static async Task<UrlMetadata> InspectAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        string fileName = ExtractFileNameFromUrl(url);
        long contentLength = -1;
        bool supportsRange = false;
        string? etag = null;

        // Try HEAD first
        HttpResponseMessage? headResponse = null;
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            headResponse = await httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (headResponse.IsSuccessStatusCode)
            {
                etag = headResponse.Headers.ETag?.Tag;
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
                getRequest.Headers.Range = new RangeHeaderValue(0, 0);

                using var getResponse = await httpClient.SendAsync(
                    getRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

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
            etag);
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

    private static string CleanFileName(string rawFileName)
    {
        string invalid = new string(Path.GetInvalidFileNameChars());
        foreach (char c in invalid)
        {
            rawFileName = rawFileName.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(rawFileName) ? "download.bin" : rawFileName;
    }
}
