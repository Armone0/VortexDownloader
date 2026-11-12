using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using VortexDownloader.Core.Common;
using Xunit;

namespace VortexDownloader.Tests;

public class ZipEntryExtractorTests : IDisposable
{
    private const string Url = "https://vortex.test/archive.zip";
    private readonly string _targetDir = Path.Combine(Path.GetTempPath(), "vortex_zipx_" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _storedBytes = Encoding.UTF8.GetBytes(new string('S', 3000) + "stored-end");
    private readonly byte[] _deflatedBytes;

    public ZipEntryExtractorTests()
    {
        var rnd = new Random(42);
        var sb = new StringBuilder();
        for (int i = 0; i < 20000; i++) sb.Append((char)('a' + rnd.Next(6)));
        _deflatedBytes = Encoding.UTF8.GetBytes(sb.ToString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_targetDir, recursive: true); } catch { }
    }

    private byte[] CreateZip()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            void Add(string name, byte[] data, CompressionLevel level)
            {
                using var s = archive.CreateEntry(name, level).Open();
                s.Write(data);
            }
            Add("folder/stored.txt", _storedBytes, CompressionLevel.NoCompression);
            Add("folder/deflated.txt", _deflatedBytes, CompressionLevel.Optimal);
            Add("../../evil.txt", Encoding.UTF8.GetBytes("evil"), CompressionLevel.Optimal);
        }
        return ms.ToArray();
    }

    private sealed class RangeHandler(byte[] data) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range == null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });

            long from = range.From ?? 0;
            long to = Math.Min(range.To ?? data.Length - 1, data.Length - 1);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(data, (int)from, (int)(to - from + 1))
            };
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, data.Length);
            return Task.FromResult(response);
        }
    }

    private static async Task<(HttpClient Client, List<ZipEntryMetadata> Entries)> OpenAsync(byte[] zip)
    {
        var client = new HttpClient(new RangeHandler(zip));
        return (client, await ZipInspector.InspectRemoteZipAsync(client, Url));
    }

    [Fact]
    public async Task Extract_StoredAndDeflatedEntries_MatchOriginalBytes()
    {
        var (client, entries) = await OpenAsync(CreateZip());
        using var _ = client;

        var stored = entries.Single(e => e.FileName == "folder/stored.txt");
        var deflated = entries.Single(e => e.FileName == "folder/deflated.txt");
        Assert.Equal(0, stored.CompressionMethod);
        Assert.Equal(8, deflated.CompressionMethod);

        long lastProgress = 0;
        var progress = new SyncProgress(v => lastProgress = v);
        string storedPath = await ZipEntryExtractor.ExtractRemoteEntryAsync(client, Url, stored, _targetDir, progress);
        string deflatedPath = await ZipEntryExtractor.ExtractRemoteEntryAsync(client, Url, deflated, _targetDir);

        Assert.Equal(_storedBytes, File.ReadAllBytes(storedPath));
        Assert.Equal(_deflatedBytes, File.ReadAllBytes(deflatedPath));
        Assert.Equal(Path.Combine(_targetDir, "stored.txt"), storedPath);
        Assert.Equal(_storedBytes.Length, lastProgress);
    }

    [Fact]
    public async Task Extract_ZipSlipName_StaysInTargetFolder_AndNeverOverwrites()
    {
        var (client, entries) = await OpenAsync(CreateZip());
        using var _ = client;
        var evil = entries.Single(e => e.FileName == "../../evil.txt");

        string first = await ZipEntryExtractor.ExtractRemoteEntryAsync(client, Url, evil, _targetDir);
        string second = await ZipEntryExtractor.ExtractRemoteEntryAsync(client, Url, evil, _targetDir);

        Assert.Equal(Path.Combine(_targetDir, "evil.txt"), first);
        Assert.Equal(Path.Combine(_targetDir, "evil (1).txt"), second);
        Assert.Equal("evil", File.ReadAllText(first));
    }

    [Fact]
    public async Task Extract_CorruptedByte_ThrowsCrcError_AndLeavesNoFile()
    {
        byte[] zip = CreateZip();
        var stored = ZipInspector.InspectZipBytes(zip).Single(e => e.FileName == "folder/stored.txt");
        long off = stored.LocalHeaderOffset;
        long dataStart = off + 30 + BitConverter.ToUInt16(zip, (int)off + 26) + BitConverter.ToUInt16(zip, (int)off + 28);
        zip[dataStart + 100] ^= 0xFF;

        using var client = new HttpClient(new RangeHandler(zip));
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ZipEntryExtractor.ExtractRemoteEntryAsync(client, Url, stored, _targetDir));

        Assert.Contains("CRC", ex.Message);
        Assert.Empty(Directory.GetFiles(_targetDir));
    }

    [Theory]
    [InlineData(1, 8, "şifreli")]
    [InlineData(0, 14, "LZMA")]
    [InlineData(0, 9, "Deflate64")]
    public async Task Extract_EncryptedOrUnsupportedMethod_ThrowsClearError(int flag, int method, string expected)
    {
        using var client = new HttpClient(new RangeHandler(CreateZip()));
        var entry = new ZipEntryMetadata { FileName = "x.bin", GeneralPurposeFlag = flag, CompressionMethod = method, CompressedSize = 10, UncompressedSize = 10 };

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            ZipEntryExtractor.ExtractRemoteEntryAsync(client, Url, entry, _targetDir));
        Assert.Contains(expected, ex.Message);
        Assert.False(Directory.Exists(_targetDir) && Directory.GetFiles(_targetDir).Length > 0);
    }

    [Fact]
    public void GetSafeFileName_StripsPathsAndReservedNames()
    {
        Assert.Equal("evil.txt", ZipEntryExtractor.GetSafeFileName("../../evil.txt"));
        Assert.Equal("evil.txt", ZipEntryExtractor.GetSafeFileName(@"C:\Windows\evil.txt"));
        Assert.Equal("_CON.txt", ZipEntryExtractor.GetSafeFileName("a/CON.txt"));
        Assert.Equal("dosya", ZipEntryExtractor.GetSafeFileName(".."));
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
