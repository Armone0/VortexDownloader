using System.Net;
using System.Net.Http;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Models;
using Xunit;

namespace VortexDownloader.Tests;

public class SecurityAndIntegrityTests
{
    [Theory]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData("..\\..\\Windows\\System32\\cmd.exe", "cmd.exe")]
    [InlineData("report:draft*<test>.pdf", "report_draft__test_.pdf")]
    [InlineData("", "download.bin")]
    [InlineData(null, "download.bin")]
    [InlineData("   ", "download.bin")]
    [InlineData("normal_archive.zip", "normal_archive.zip")]
    public void CleanFileName_StripsPathTraversal_AndSanitizesInvalidChars(string? input, string expected)
    {
        string sanitized = UrlMetadataInspector.CleanFileName(input);
        Assert.Equal(expected, sanitized);
    }

    private class MockInspectorHandler : HttpMessageHandler
    {
        private readonly string? _etag;
        private readonly string? _lastModified;
        private readonly long _length;

        public MockInspectorHandler(string? etag, string? lastModified, long length)
        {
            _etag = etag;
            _lastModified = lastModified;
            _length = length;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.AcceptRanges.Add("bytes");
            if (!string.IsNullOrEmpty(_etag))
            {
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(_etag);
            }
            if (!string.IsNullOrEmpty(_lastModified))
            {
                response.Content.Headers.TryAddWithoutValidation("Last-Modified", _lastModified);
            }
            response.Content.Headers.ContentLength = _length;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task UrlMetadataInspector_ExtractsETagAndLastModifiedHeaders_Correctly()
    {
        string expectedETag = "\"vortex-etag-12345\"";
        string expectedLastModified = "Sat, 07 Nov 2026 12:00:00 GMT";
        var handler = new MockInspectorHandler(expectedETag, expectedLastModified, 1024 * 1024);
        using var client = new HttpClient(handler);

        var metadata = await UrlMetadataInspector.InspectAsync(client, "https://example.com/test.zip");

        Assert.Equal(expectedETag, metadata.ETag);
        Assert.NotNull(metadata.LastModified);
        Assert.Equal(expectedLastModified, metadata.LastModified);
        Assert.True(metadata.SupportsRange);
        Assert.Equal(1024 * 1024, metadata.ContentLength);
    }
}
