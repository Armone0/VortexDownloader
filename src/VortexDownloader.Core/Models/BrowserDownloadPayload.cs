using System.Text.Json.Serialization;

namespace VortexDownloader.Core.Models;

public record BrowserDownloadPayload(
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("fileName")] string? FileName,
    [property: JsonPropertyName("fileSize")] long? FileSize,
    [property: JsonPropertyName("cookies")] string? Cookies = null,
    [property: JsonPropertyName("referrer")] string? Referrer = null,
    [property: JsonPropertyName("referer")] string? Referer = null,
    [property: JsonPropertyName("userAgent")] string? UserAgent = null)
{
    public string? EffectiveReferrer => !string.IsNullOrEmpty(Referrer) ? Referrer : Referer;
}
