using System.Net;
using System.Text;
using VortexDownloader.Core.Common;

namespace VortexDownloader.Tests;

/// <summary>
/// Remote RAR listing / single-file extraction over HTTP Range, against archives made with WinRAR from:
/// readme.txt, docs/rapor.txt (repetitive text), docs/veri.bin (300 000 random bytes, seed 42).
/// </summary>
public class RemoteRarArchiveTests : IDisposable
{
    private const string Url = "https://vortex.test/sunumlar.rar";
    private readonly string _targetDir = Path.Combine(Path.GetTempPath(), "vortex_rarx_" + Guid.NewGuid().ToString("N"));

    private static readonly byte[] Readme = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Vortex RAR test\r\n", 40)));
    private static readonly byte[] Rapor = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Sunum satiri numara 12345\r\n", 8000)));
    private static readonly byte[] Veri = MakeRandom();

    private static byte[] MakeRandom()
    {
        var bytes = new byte[300000];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    public void Dispose()
    {
        try { Directory.Delete(_targetDir, recursive: true); } catch { }
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>Serves a byte array with Range support (or without it) and counts the bytes sent.</summary>
    private sealed class RangeHandler(byte[] data, bool supportsRange = true) : HttpMessageHandler
    {
        public long BytesServed;

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range == null || !supportsRange)
            {
                BytesServed += data.Length;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            }
            long from = range.From ?? 0;
            long to = Math.Min(range.To ?? data.Length - 1, data.Length - 1);
            BytesServed += to - from + 1;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(data, (int)from, (int)(to - from + 1))
            };
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, data.Length);
            return response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private static ZipEntryMetadata Find(RemoteRarListing listing, string fileName) =>
        listing.Entries.Single(e => !e.IsDirectory && e.FileName.Replace('\\', '/').EndsWith(fileName, StringComparison.Ordinal));

    [Theory]
    [InlineData("normal5.rar", false)]
    [InlineData("solid5.rar", true)]
    public async Task List_ReturnsFilesWithSizes_AndSolidFlag(string fixture, bool solid)
    {
        using var client = new HttpClient(new RangeHandler(Fixture(fixture)));
        var listing = await RemoteRarArchive.ListAsync(client, Url, null, null, CancellationToken.None);

        Assert.Equal(solid, listing.IsSolid);
        Assert.Equal(Readme.Length, Find(listing, "readme.txt").UncompressedSize);
        Assert.Equal(Rapor.Length, Find(listing, "rapor.txt").UncompressedSize);
        Assert.Equal(Veri.Length, Find(listing, "veri.bin").UncompressedSize);
        Assert.All(listing.Entries, e => Assert.False(e.IsEncrypted));
    }

    [Theory]
    [InlineData("normal5.rar")]
    [InlineData("solid5.rar")]
    public async Task Extract_EachFile_MatchesOriginalBytes(string fixture)
    {
        using var client = new HttpClient(new RangeHandler(Fixture(fixture)));
        var listing = await RemoteRarArchive.ListAsync(client, Url, null, null, CancellationToken.None);

        foreach (var (name, expected) in new[] { ("readme.txt", Readme), ("rapor.txt", Rapor), ("veri.bin", Veri) })
        {
            string path = await RemoteRarArchive.ExtractEntryAsync(client, Url, Find(listing, name), _targetDir, null, null, null, CancellationToken.None);
            Assert.Equal(name, Path.GetFileName(path));
            Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        }
    }

    [Fact]
    public async Task Extract_SmallFileFromNonSolid_DoesNotDownloadWholeArchive()
    {
        byte[] rar = Fixture("normal5.rar");
        var handler = new RangeHandler(rar);
        using var client = new HttpClient(handler);
        var listing = await RemoteRarArchive.ListAsync(client, Url, null, null, CancellationToken.None);
        handler.BytesServed = 0;

        await RemoteRarArchive.ExtractEntryAsync(client, Url, Find(listing, "readme.txt"), _targetDir, null, null, null, CancellationToken.None);

        Assert.True(handler.BytesServed < rar.Length / 2, $"served {handler.BytesServed} of {rar.Length} bytes");
    }

    [Fact]
    public async Task Extract_DoesNotOverwriteExistingFile()
    {
        Directory.CreateDirectory(_targetDir);
        await File.WriteAllTextAsync(Path.Combine(_targetDir, "readme.txt"), "mine");
        using var client = new HttpClient(new RangeHandler(Fixture("normal5.rar")));
        var listing = await RemoteRarArchive.ListAsync(client, Url, null, null, CancellationToken.None);

        string path = await RemoteRarArchive.ExtractEntryAsync(client, Url, Find(listing, "readme.txt"), _targetDir, null, null, null, CancellationToken.None);

        Assert.Equal("readme (1).txt", Path.GetFileName(path));
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(_targetDir, "readme.txt")));
    }

    [Fact]
    public async Task PasswordProtectedEntries_AreMarkedEncrypted_AndRefused()
    {
        using var client = new HttpClient(new RangeHandler(Fixture("encrypted5.rar")));
        var listing = await RemoteRarArchive.ListAsync(client, Url, null, null, CancellationToken.None);
        var entry = Find(listing, "readme.txt");

        Assert.True(entry.IsEncrypted);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            RemoteRarArchive.ExtractEntryAsync(client, Url, entry, _targetDir, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task ServerWithoutRange_IsReported()
    {
        using var client = new HttpClient(new RangeHandler(Fixture("normal5.rar"), supportsRange: false));
        await Assert.ThrowsAsync<NotSupportedException>(() => RemoteRarArchive.ListAsync(client, Url, null, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://site.edu/pluginfile.php/1/mod_resource/content/1/sunumlar.rar", true)]
    [InlineData("https://site.edu/file.RAR?forcedownload=1", true)]
    [InlineData("sunumlar.rar", true)]
    [InlineData("https://site.edu/archive.zip", false)]
    [InlineData(null, false)]
    public void IsRarName_ChecksPathExtension(string? input, bool expected) => Assert.Equal(expected, RemoteRarArchive.IsRarName(input));
}
