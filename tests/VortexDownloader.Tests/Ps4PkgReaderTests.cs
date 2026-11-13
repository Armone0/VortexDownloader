using System.Buffers.Binary;
using System.Net;
using System.Text;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Packages;

namespace VortexDownloader.Tests;

/// <summary>PS4 PKG viewer against a synthetic PKG built in memory (no real PKG files in the repo).</summary>
public class Ps4PkgReaderTests : IDisposable
{
    private const string ContentId = "EP0001-CUSA00001_00-TESTGAME00000000";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] Evil = Encoding.ASCII.GetBytes("not a real file");
    private readonly string _targetDir = Path.Combine(Path.GetTempPath(), "vortex_pkg_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_targetDir, recursive: true); } catch { }
    }

    private static byte[] BuildSfo()
    {
        var items = new (string Key, ushort Fmt, byte[] Data)[]
        {
            ("APP_VER", 0x0204, Z("01.05")),
            ("CATEGORY", 0x0204, Z("gd")),
            ("CONTENT_ID", 0x0204, Z(ContentId)),
            ("SYSTEM_VER", 0x0404, BitConverter.GetBytes(0x05050000u)),
            ("TITLE", 0x0204, Z("Vortex Test Oyunu")),
            ("TITLE_ID", 0x0204, Z("CUSA00001")),
            ("VERSION", 0x0204, Z("01.00")),
        };
        static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

        int keyTable = 0x14 + items.Length * 0x10;
        var keys = new MemoryStream();
        var data = new MemoryStream();
        var index = new byte[items.Length * 0x10];
        for (int i = 0; i < items.Length; i++)
        {
            var span = index.AsSpan(i * 0x10);
            BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)keys.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(span[2..], items[i].Fmt);
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)items[i].Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)items[i].Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)data.Length);
            keys.Write(Encoding.ASCII.GetBytes(items[i].Key + "\0"));
            data.Write(items[i].Data);
        }
        while (keys.Length % 4 != 0) keys.WriteByte(0);
        int dataTable = keyTable + (int)keys.Length;

        var sfo = new byte[dataTable + data.Length];
        sfo[1] = (byte)'P'; sfo[2] = (byte)'S'; sfo[3] = (byte)'F';
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(4), 0x101);
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(8), (uint)keyTable);
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(12), (uint)dataTable);
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(16), (uint)items.Length);
        index.CopyTo(sfo, 0x14);
        keys.ToArray().CopyTo(sfo, keyTable);
        data.ToArray().CopyTo(sfo, dataTable);
        return sfo;
    }

    /// <summary>Header + entry table at 0x800 + data: names, param.sfo, icon0, an encrypted .entry_keys and "../evil.txt".</summary>
    private static byte[] BuildPkg(uint? entryCountOverride = null, uint? icon0OffsetOverride = null)
    {
        byte[] names = Encoding.ASCII.GetBytes("\0param.sfo\0icon0.png\0../evil.txt\0");
        byte[] sfo = BuildSfo();
        byte[] keys = new byte[0x40];
        var parts = new (uint Id, uint Name, uint Flags1, byte[] Data)[]
        {
            (0x0010, 0, 0x80000000, keys),
            (0x0200, 0, 0, names),
            (0x1000, 1, 0, sfo),
            (0x1200, 11, 0, Png),
            (0x9001, 21, 0, Evil),
        };

        const int tableOffset = 0x800;
        long dataStart = tableOffset + parts.Length * 0x20;
        var pkg = new MemoryStream();
        pkg.SetLength(dataStart);
        var buf = pkg.GetBuffer();
        buf[0] = 0x7F; buf[1] = (byte)'C'; buf[2] = (byte)'N'; buf[3] = (byte)'T';
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x04), 0x80000001);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x10), entryCountOverride ?? (uint)parts.Length);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x18), tableOffset);
        Encoding.ASCII.GetBytes(ContentId).CopyTo(buf, 0x40);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0x74), 0x1A);

        pkg.Position = dataStart;
        for (int i = 0; i < parts.Length; i++)
        {
            uint offset = (uint)pkg.Position;
            pkg.Write(parts[i].Data);
            buf = pkg.GetBuffer();
            var e = buf.AsSpan(tableOffset + i * 0x20);
            BinaryPrimitives.WriteUInt32BigEndian(e, parts[i].Id);
            BinaryPrimitives.WriteUInt32BigEndian(e[4..], parts[i].Name);
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], parts[i].Flags1);
            BinaryPrimitives.WriteUInt32BigEndian(e[16..], parts[i].Id == 0x1200 && icon0OffsetOverride is uint o ? o : offset);
            BinaryPrimitives.WriteUInt32BigEndian(e[20..], (uint)parts[i].Data.Length);
        }
        var result = pkg.ToArray();
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(0x430), (ulong)result.Length);
        return result;
    }

    private static Ps4PkgInfo Read(byte[] pkg) => Ps4PkgReader.Read(new MemoryStream(pkg));

    [Fact]
    public void Read_ParsesHeaderEntriesAndIcon()
    {
        var pkg = BuildPkg();
        var info = Read(pkg);

        Assert.Equal(ContentId, info.ContentId);
        Assert.Equal(0x1Au, info.ContentType);
        Assert.True(info.IsFinalized);
        Assert.Equal(pkg.Length, info.PackageSize);
        Assert.Equal(5, info.Entries.Count);
        Assert.Equal([".entry_keys", ".entry_names", "param.sfo", "icon0.png", "../evil.txt"], info.Entries.Select(e => e.Name));
        Assert.True(info.Entries[0].IsEncrypted);
        Assert.All(info.Entries.Skip(1), e => Assert.False(e.IsEncrypted));
        Assert.Equal(Png, info.Icon0);
    }

    [Fact]
    public void Read_ParsesParamSfo()
    {
        var sfo = Read(BuildPkg()).ParamSfo!;
        Assert.Equal("Vortex Test Oyunu", sfo.Title);
        Assert.Equal("CUSA00001", sfo.TitleId);
        Assert.Equal(ContentId, sfo.ContentId);
        Assert.Equal("gd", sfo.Category);
        Assert.Equal("01.05", sfo.AppVersion);
        Assert.Equal("01.00", sfo.Version);
        Assert.Equal("5.05", sfo.SystemVersion);
        Assert.Equal(7, sfo.Values.Count);
        Assert.Equal("Oyun", Ps4PkgReader.DescribeCategory(sfo.Category));
        Assert.Equal("10.01", Ps4PkgReader.FormatFirmware(0x10010000));
    }

    [Fact]
    public void Extract_EncryptedEntry_IsRefused()
    {
        var pkg = BuildPkg();
        var info = Read(pkg);
        Assert.Throws<NotSupportedException>(() =>
            Ps4PkgReader.ExtractEntries(new MemoryStream(pkg), [info.Entries[0]], _targetDir));
        Assert.False(Directory.Exists(_targetDir) && Directory.EnumerateFiles(_targetDir).Any());
    }

    [Fact]
    public void Read_TruncatedFile_Throws()
    {
        var pkg = BuildPkg();
        Assert.Throws<Ps4PkgFormatException>(() => Read(pkg[..0x100]));
        Assert.Throws<Ps4PkgFormatException>(() => Read(pkg[..(pkg.Length - 4)]));
    }

    [Fact]
    public void Read_BadMagic_Throws()
    {
        var pkg = BuildPkg();
        pkg[1] = (byte)'X';
        Assert.Throws<Ps4PkgFormatException>(() => Read(pkg));
    }

    [Fact]
    public void Read_AbsurdEntryCount_Throws()
    {
        Assert.Throws<Ps4PkgFormatException>(() => Read(BuildPkg(entryCountOverride: 0xFFFFFFFF)));
        Assert.Throws<Ps4PkgFormatException>(() => Read(BuildPkg(entryCountOverride: 9_000))); // under the cap but past EOF
    }

    [Fact]
    public void Read_OffsetBeyondLength_Throws()
    {
        Assert.Throws<Ps4PkgFormatException>(() => Read(BuildPkg(icon0OffsetOverride: 0xFFFFFF00)));
    }

    [Fact]
    public void ParseParamSfo_Malformed_Throws()
    {
        var sfo = BuildSfo();
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(16), 100_000);
        Assert.Throws<Ps4PkgFormatException>(() => Ps4PkgReader.ParseParamSfo(sfo));
        Assert.Throws<Ps4PkgFormatException>(() => Ps4PkgReader.ParseParamSfo(new byte[Ps4PkgReader.MaxSfoSize + 1]));
    }

    [Fact]
    public void Extract_TraversalName_StaysInFolder()
    {
        var pkg = BuildPkg();
        var evil = Read(pkg).Entries.Single(e => e.Id == 0x9001);
        var paths = Ps4PkgReader.ExtractEntries(new MemoryStream(pkg), [evil], _targetDir);

        string path = Assert.Single(paths);
        Assert.Equal(Path.Combine(Path.GetFullPath(_targetDir), "evil.txt"), path);
        Assert.Equal(Evil, File.ReadAllBytes(path));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_targetDir))!, "evil.txt")));
    }

    [Fact]
    public void Extract_NeverOverwrites()
    {
        var pkg = BuildPkg();
        var icon = Read(pkg).Entries.Single(e => e.Id == 0x1200);
        Directory.CreateDirectory(_targetDir);
        string existing = Path.Combine(_targetDir, "icon0.png");
        File.WriteAllText(existing, "keep me");
        long reported = 0;

        var paths = Ps4PkgReader.ExtractEntries(new MemoryStream(pkg), [icon, icon], _targetDir, new SyncProgress(v => reported = v));

        Assert.Equal("keep me", File.ReadAllText(existing));
        Assert.Equal([Path.Combine(Path.GetFullPath(_targetDir), "icon0 (1).png"), Path.Combine(Path.GetFullPath(_targetDir), "icon0 (2).png")], paths);
        Assert.All(paths, p => Assert.Equal(Png, File.ReadAllBytes(p)));
        Assert.Equal(Png.Length * 2, reported);
    }

    [Fact]
    public void Extract_Cancelled_LeavesNoFiles()
    {
        var pkg = BuildPkg();
        var sfo = Read(pkg).Entries.Single(e => e.Id == 0x1000);
        Assert.Throws<OperationCanceledException>(() =>
            Ps4PkgReader.ExtractEntries(new MemoryStream(pkg), [sfo], _targetDir, cancellationToken: new CancellationToken(true)));
        Assert.Empty(Directory.EnumerateFiles(_targetDir));
    }

    [Fact]
    public async Task Read_OverHttpRangeStream_MatchesLocal()
    {
        var pkg = BuildPkg();
        using var client = new HttpClient(new RangeHandler(pkg));
        const string url = "https://vortex.test/game.pkg";
        long length = await HttpRangeStream.GetRangeLengthAsync(client, url, "a=b", null, default);
        using var stream = new HttpRangeStream(client, url, length, "a=b");

        var info = await Task.Run(() => Ps4PkgReader.Read(stream));
        Assert.Equal(ContentId, info.ContentId);
        Assert.Equal("CUSA00001", info.ParamSfo!.TitleId);
        Assert.True(Ps4PkgReader.IsPkgName(url));
        Assert.False(Ps4PkgReader.IsPkgName("https://vortex.test/game.pkg.zip"));
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private sealed class RangeHandler(byte[] data) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        // HttpRangeStream reads synchronously through HttpClient.Send.
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("a=b", request.Headers.GetValues("Cookie").Single());
            var range = request.Headers.Range!.Ranges.First();
            long from = range.From ?? 0;
            long to = Math.Min(range.To ?? data.Length - 1, data.Length - 1);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(data, (int)from, (int)(to - from + 1))
            };
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, data.Length);
            return response;
        }
    }
}
