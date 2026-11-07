using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using VortexDownloader.Core.Common;
using Xunit;

namespace VortexDownloader.Tests;

public class ZipInspectorTests
{
    private static byte[] CreateSampleZip(Dictionary<string, string> files, string? comment = null)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            if (comment != null)
            {
                archive.Comment = comment;
            }

            foreach (var kvp in files)
            {
                var entry = archive.CreateEntry(kvp.Key);
                using var entryStream = entry.Open();
                byte[] bytes = Encoding.UTF8.GetBytes(kvp.Value);
                entryStream.Write(bytes, 0, bytes.Length);
            }
        }
        return ms.ToArray();
    }

    private class MockRangeZipHttpHandler : HttpMessageHandler
    {
        private readonly byte[] _zipData;
        private readonly bool _supportRange;

        public MockRangeZipHttpHandler(byte[] zipData, bool supportRange = true)
        {
            _zipData = zipData;
            _supportRange = supportRange;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                if (_supportRange)
                {
                    response.Headers.AcceptRanges.Add("bytes");
                }
                response.Content = new ByteArrayContent([]);
                response.Content.Headers.ContentLength = _zipData.Length;
                return Task.FromResult(response);
            }

            if (!_supportRange)
            {
                var fullResponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_zipData)
                };
                fullResponse.Content.Headers.ContentLength = _zipData.Length;
                return Task.FromResult(fullResponse);
            }

            if (request.Headers.Range != null && request.Headers.Range.Ranges.Count > 0)
            {
                var range = request.Headers.Range.Ranges.First();
                long from = range.From ?? 0;
                long to = range.To ?? (_zipData.Length - 1);

                to = Math.Min(to, _zipData.Length - 1);
                int length = (int)(to - from + 1);
                byte[] slice = new byte[length];
                Array.Copy(_zipData, from, slice, 0, length);

                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice)
                };
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, _zipData.Length);
                response.Content.Headers.ContentLength = length;
                return Task.FromResult(response);
            }

            var defaultResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_zipData)
            };
            defaultResponse.Headers.AcceptRanges.Add("bytes");
            defaultResponse.Content.Headers.ContentLength = _zipData.Length;
            return Task.FromResult(defaultResponse);
        }
    }

    [Fact]
    public void InspectZipBytes_ParsesValidArchive_ReturnsAccurateMetadata()
    {
        var files = new Dictionary<string, string>
        {
            { "readme.txt", "Vortex Downloader v1.2.0 is blazing fast!" },
            { "docs/manual.md", "# Manual\r\nDownload faster with multi-chunk range acceleration." },
            { "empty_folder/", "" }
        };

        byte[] zipBytes = CreateSampleZip(files);
        var entries = ZipInspector.InspectZipBytes(zipBytes);

        Assert.Equal(3, entries.Count);

        var readme = entries.FirstOrDefault(e => e.FileName == "readme.txt");
        Assert.NotNull(readme);
        Assert.False(readme.IsDirectory);
        Assert.Equal(Encoding.UTF8.GetByteCount(files["readme.txt"]), readme.UncompressedSize);
        Assert.True(readme.CompressedSize > 0);
        Assert.True(readme.LastModified > DateTime.MinValue);

        var docs = entries.FirstOrDefault(e => e.FileName == "docs/manual.md");
        Assert.NotNull(docs);
        Assert.False(docs.IsDirectory);
        Assert.Equal(Encoding.UTF8.GetByteCount(files["docs/manual.md"]), docs.UncompressedSize);

        var folder = entries.FirstOrDefault(e => e.FileName == "empty_folder/");
        Assert.NotNull(folder);
        Assert.True(folder.IsDirectory);
        Assert.Equal(0, folder.UncompressedSize);
    }

    [Fact]
    public void InspectZipBytes_WithArchiveComment_FindsEocdAndParsesCorrectly()
    {
        var files = new Dictionary<string, string>
        {
            { "data.json", "{\"version\": \"1.2.0\"}" }
        };

        string archiveComment = "This is a custom archive comment used to test EOCD scanning.";
        byte[] zipBytes = CreateSampleZip(files, archiveComment);

        var entries = ZipInspector.InspectZipBytes(zipBytes);
        Assert.Single(entries);
        Assert.Equal("data.json", entries[0].FileName);
        Assert.False(entries[0].IsDirectory);
    }

    [Fact]
    public async Task InspectRemoteZipAsync_UsingRangeRequests_PeeksWithoutFullDownload()
    {
        var files = new Dictionary<string, string>
        {
            { "file1.txt", new string('A', 5000) },
            { "folder/file2.bin", new string('B', 8000) }
        };

        byte[] zipBytes = CreateSampleZip(files);
        var handler = new MockRangeZipHttpHandler(zipBytes, supportRange: true);
        using var httpClient = new HttpClient(handler);

        var entries = await ZipInspector.InspectRemoteZipAsync(httpClient, "https://vortex.test/sample.zip");

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.FileName == "file1.txt" && e.UncompressedSize == 5000);
        Assert.Contains(entries, e => e.FileName == "folder/file2.bin" && e.UncompressedSize == 8000);
    }

    [Fact]
    public async Task InspectRemoteZipAsync_ServerDoesNotSupportRange_ThrowsNotSupportedException()
    {
        var files = new Dictionary<string, string> { { "file.txt", "content" } };
        byte[] zipBytes = CreateSampleZip(files);

        var handler = new MockRangeZipHttpHandler(zipBytes, supportRange: false);
        using var httpClient = new HttpClient(handler);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            ZipInspector.InspectRemoteZipAsync(httpClient, "https://vortex.test/no-range.zip"));
    }

    [Fact]
    public void InspectZipBytes_CorruptedOrInvalidData_ThrowsInvalidDataException()
    {
        byte[] randomBytes = new byte[256];
        new Random(123).NextBytes(randomBytes);

        Assert.Throws<InvalidDataException>(() => ZipInspector.InspectZipBytes(randomBytes));
        Assert.Throws<InvalidDataException>(() => ZipInspector.InspectZipBytes(new byte[10]));
    }
}
