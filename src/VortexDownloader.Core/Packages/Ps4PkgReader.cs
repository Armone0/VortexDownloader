using System.Buffers.Binary;
using System.Text;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Models;

namespace VortexDownloader.Core.Packages;

/// <summary>Thrown for anything that is not a well-formed PS4 PKG (bad magic, truncated, out-of-range offsets).</summary>
public sealed class Ps4PkgFormatException(string message) : FormatException(message);

/// <summary>One file in the PKG entry table. Offsets/sizes are absolute positions in the .pkg.</summary>
public sealed record Ps4PkgEntry(uint Id, string Name, uint Flags1, uint Flags2, long Offset, long Size)
{
    /// <summary>Flags1 bit 31 marks an encrypted entry (LibOrbisPkg MetaEntry.Encrypted).</summary>
    public bool IsEncrypted => (Flags1 & 0x80000000) != 0;
}

/// <summary>One PARAM.SFO key. <see cref="Integer"/> is set for integer (0x0404) values.</summary>
public sealed record Ps4SfoValue(string Key, string Text, uint? Integer);

public sealed class Ps4ParamSfo
{
    public required IReadOnlyList<Ps4SfoValue> Values { get; init; }

    public string? this[string key] => Values.FirstOrDefault(v => v.Key == key)?.Text;

    public string? Title => this["TITLE"];
    public string? TitleId => this["TITLE_ID"];
    public string? ContentId => this["CONTENT_ID"];
    public string? Category => this["CATEGORY"];
    public string? AppVersion => this["APP_VER"];
    public string? Version => this["VERSION"];

    /// <summary>Required firmware, e.g. 0x05050000 → "5.05".</summary>
    public string? SystemVersion => Values.FirstOrDefault(v => v.Key == "SYSTEM_VER")?.Integer is uint v
        ? Ps4PkgReader.FormatFirmware(v) : null;
}

public sealed class Ps4PkgInfo
{
    public required uint Flags { get; init; }
    public required string ContentId { get; init; }
    public required uint DrmType { get; init; }
    public required uint ContentType { get; init; }
    public required uint ContentFlags { get; init; }
    public required long BodyOffset { get; init; }
    public required long BodySize { get; init; }
    public required long PfsImageOffset { get; init; }
    public required long PfsImageSize { get; init; }
    /// <summary>Size declared in the header (0x430); <see cref="FileLength"/> is the real stream length.</summary>
    public required long PackageSize { get; init; }
    public required long FileLength { get; init; }
    public required IReadOnlyList<Ps4PkgEntry> Entries { get; init; }
    public Ps4ParamSfo? ParamSfo { get; init; }
    public byte[]? Icon0 { get; init; }

    public bool IsFinalized => (Flags & 0x80000000) != 0;
}

/// <summary>
/// Read-only PS4 PKG viewer: header, entry table, PARAM.SFO and icon0 from any seekable stream (a local file or
/// <see cref="HttpRangeStream"/>). Never decrypts anything; encrypted entries are listed but cannot be extracted.
/// Layout follows OpenOrbis LibOrbisPkg (PkgReader.ReadHeader, MetaEntry, EntryNames, SFO.ParamSfo).
/// </summary>
public static class Ps4PkgReader
{
    public const int MaxEntries = 10_000;
    public const int MaxSfoSize = 64 * 1024;
    public const int MaxIconSize = 4 * 1024 * 1024;
    private const int HeaderSize = 0x5A0;
    private const int EntrySize = 0x20;
    private const int MaxNameTableSize = 1024 * 1024;

    public const uint EntryNamesId = 0x0200;
    public const uint ParamSfoId = 0x1000;
    public const uint Icon0Id = 0x1200;

    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = TimeSpan.FromSeconds(60) };

    // Names of entries that have no entry in the name table (LibOrbisPkg EntryNames.IdToName).
    private static readonly Dictionary<uint, string> KnownNames = new()
    {
        [0x0001] = ".digests", [0x0010] = ".entry_keys", [0x0020] = ".image_key", [0x0080] = ".general_digests",
        [0x0100] = ".metas", [0x0200] = ".entry_names", [0x0400] = "license.dat", [0x0401] = "license.info",
        [0x0402] = "nptitle.dat", [0x0403] = "npbind.dat", [0x0404] = "selfinfo.dat", [0x0406] = "imageinfo.dat",
        [0x0407] = "target-deltainfo.dat", [0x0408] = "origin-deltainfo.dat", [0x0409] = "psreserved.dat",
        [0x1000] = "param.sfo", [0x1001] = "playgo-chunk.dat", [0x1002] = "playgo-chunk.sha",
        [0x1003] = "playgo-manifest.xml", [0x1004] = "pronunciation.xml", [0x1005] = "pronunciation.sig",
        [0x1006] = "pic1.png", [0x1007] = "pubtoolinfo.dat", [0x1008] = "app/playgo-chunk.dat",
        [0x1009] = "app/playgo-chunk.sha", [0x100A] = "app/playgo-manifest.xml", [0x100B] = "shareparam.json",
        [0x100C] = "shareoverlayimage.png", [0x100D] = "save_data.png", [0x100E] = "shareprivacyguardimage.png",
        [0x1200] = "icon0.png", [0x1220] = "pic0.png", [0x1240] = "snd0.at9", [0x1260] = "changeinfo/changeinfo.xml",
        [0x1280] = "icon0.dds", [0x12A0] = "pic0.dds", [0x12C0] = "pic1.dds",
    };

    public static bool IsPkgName(string? nameOrUrl)
    {
        if (string.IsNullOrWhiteSpace(nameOrUrl)) return false;
        string path = Uri.TryCreate(nameOrUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : nameOrUrl;
        return path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens a remote .pkg as a seekable stream (Range requests, cookies/referrer forwarded). Caller disposes.</summary>
    public static async Task<HttpRangeStream> OpenRemoteAsync(string url, string? cookies = null, string? referrer = null,
        CancellationToken cancellationToken = default)
    {
        long length = await HttpRangeStream.GetRangeLengthAsync(SharedClient, url, cookies, referrer, cancellationToken).ConfigureAwait(false);
        return new HttpRangeStream(SharedClient, url, length, cookies, referrer, cancellationToken);
    }

    /// <summary>Parses header, entry table, PARAM.SFO and icon0. Blocks on I/O: call from a worker thread for remote streams.</summary>
    public static Ps4PkgInfo Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead) throw new ArgumentException("PKG akışı okunabilir ve konumlanabilir olmalı.", nameof(stream));
        long length = stream.Length;
        if (length < HeaderSize) throw new Ps4PkgFormatException("Dosya PS4 PKG başlığı için çok kısa (eksik ya da bozuk).");

        byte[] h = ReadAt(stream, 0, HeaderSize);
        if (h[0] != 0x7F || h[1] != (byte)'C' || h[2] != (byte)'N' || h[3] != (byte)'T')
            throw new Ps4PkgFormatException("Geçerli bir PS4 PKG dosyası değil (CNT imzası yok).");

        uint entryCount = U32(h, 0x10);
        uint tableOffset = U32(h, 0x18);
        if (entryCount > MaxEntries)
            throw new Ps4PkgFormatException($"PKG girdi sayısı makul değil ({entryCount}); dosya bozuk.");
        if (tableOffset + (long)entryCount * EntrySize > length)
            throw new Ps4PkgFormatException("PKG girdi tablosu dosyanın dışına taşıyor (eksik ya da bozuk).");

        byte[] table = ReadAt(stream, tableOffset, (int)entryCount * EntrySize);
        var raw = new List<(uint Id, uint NameOffset, uint Flags1, uint Flags2, uint Offset, uint Size)>((int)entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            int p = i * EntrySize;
            var e = (U32(table, p), U32(table, p + 4), U32(table, p + 8), U32(table, p + 12), U32(table, p + 16), U32(table, p + 20));
            if ((long)e.Item5 + e.Item6 > length)
                throw new Ps4PkgFormatException($"PKG girdisi 0x{e.Item1:X4} dosyanın dışını gösteriyor (eksik ya da bozuk).");
            raw.Add(e);
        }

        byte[]? names = null;
        var nameEntry = raw.FirstOrDefault(e => e.Id == EntryNamesId);
        if (nameEntry.Id == EntryNamesId && (nameEntry.Flags1 & 0x80000000) == 0 && nameEntry.Size is > 0 and <= MaxNameTableSize)
            names = ReadAt(stream, nameEntry.Offset, (int)nameEntry.Size);

        var entries = raw.Select(e => new Ps4PkgEntry(e.Id, ResolveName(e.Id, e.NameOffset, names), e.Flags1, e.Flags2, e.Offset, e.Size)).ToList();

        Ps4ParamSfo? sfo = null;
        if (entries.FirstOrDefault(e => e.Id == ParamSfoId) is { IsEncrypted: false } sfoEntry)
        {
            if (sfoEntry.Size > MaxSfoSize) throw new Ps4PkgFormatException("PARAM.SFO beklenenden büyük; dosya bozuk.");
            sfo = ParseParamSfo(ReadAt(stream, sfoEntry.Offset, (int)sfoEntry.Size));
        }

        byte[]? icon = null;
        if (entries.FirstOrDefault(e => e.Id == Icon0Id) is { IsEncrypted: false, Size: > 0 and <= MaxIconSize } iconEntry)
            icon = ReadAt(stream, iconEntry.Offset, (int)iconEntry.Size);

        return new Ps4PkgInfo
        {
            Flags = U32(h, 0x04),
            ContentId = Encoding.ASCII.GetString(h, 0x40, 36).TrimEnd('\0'),
            DrmType = U32(h, 0x70),
            ContentType = U32(h, 0x74),
            ContentFlags = U32(h, 0x78),
            BodyOffset = (long)Math.Min(U64(h, 0x20), long.MaxValue),
            BodySize = (long)Math.Min(U64(h, 0x28), long.MaxValue),
            PfsImageOffset = (long)Math.Min(U64(h, 0x410), long.MaxValue),
            PfsImageSize = (long)Math.Min(U64(h, 0x418), long.MaxValue),
            PackageSize = (long)Math.Min(U64(h, 0x430), long.MaxValue),
            FileLength = length,
            Entries = entries,
            ParamSfo = sfo,
            Icon0 = icon
        };
    }

    /// <summary>Little-endian PARAM.SFO ("\0PSF"); every offset is checked against the buffer.</summary>
    public static Ps4ParamSfo ParseParamSfo(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > MaxSfoSize) throw new Ps4PkgFormatException("PARAM.SFO beklenenden büyük; dosya bozuk.");
        if (data.Length < 0x14 || data[0] != 0 || data[1] != (byte)'P' || data[2] != (byte)'S' || data[3] != (byte)'F')
            throw new Ps4PkgFormatException("PARAM.SFO geçersiz (PSF imzası yok).");

        var span = data.AsSpan();
        uint keyTable = BinaryPrimitives.ReadUInt32LittleEndian(span[0x08..]);
        uint dataTable = BinaryPrimitives.ReadUInt32LittleEndian(span[0x0C..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(span[0x10..]);
        if (count > (data.Length - 0x14) / 0x10 || keyTable > data.Length || dataTable > data.Length)
            throw new Ps4PkgFormatException("PARAM.SFO tablo başlığı bozuk.");

        var values = new List<Ps4SfoValue>((int)count);
        for (int i = 0; i < count; i++)
        {
            var idx = span.Slice(0x14 + i * 0x10, 0x10);
            long keyAt = keyTable + (long)BinaryPrimitives.ReadUInt16LittleEndian(idx);
            ushort format = BinaryPrimitives.ReadUInt16LittleEndian(idx[2..]);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(idx[4..]);
            long dataAt = dataTable + (long)BinaryPrimitives.ReadUInt32LittleEndian(idx[12..]);
            if (keyAt >= data.Length || dataAt + len > data.Length)
                throw new Ps4PkgFormatException("PARAM.SFO değeri tablonun dışını gösteriyor.");

            int keyEnd = span[(int)keyAt..].IndexOf((byte)0);
            if (keyEnd < 0) throw new Ps4PkgFormatException("PARAM.SFO anahtar adı sonlanmamış.");
            string key = Encoding.ASCII.GetString(span.Slice((int)keyAt, keyEnd));
            var value = span.Slice((int)dataAt, (int)len);

            if (format == 0x0404)
            {
                if (len < 4) throw new Ps4PkgFormatException("PARAM.SFO tamsayı değeri kısa.");
                uint n = BinaryPrimitives.ReadUInt32LittleEndian(value);
                values.Add(new Ps4SfoValue(key, $"0x{n:X8}", n));
            }
            else
            {
                values.Add(new Ps4SfoValue(key, Encoding.UTF8.GetString(value).TrimEnd('\0'), null));
            }
        }
        return new Ps4ParamSfo { Values = values };
    }

    /// <summary>Firmware stored as BCD-like hex: 0x05050000 → "5.05", 0x10010000 → "10.01".</summary>
    public static string FormatFirmware(uint value) => value == 0 ? "-" : $"{value >> 24:X}.{(value >> 16) & 0xFF:X2}";

    /// <summary>SFO CATEGORY (or the header content type when the SFO is missing) as Turkish text.</summary>
    public static string DescribeCategory(string? category, uint contentType = 0) => category?.ToLowerInvariant() switch
    {
        "gd" => "Oyun",
        "gde" => "Uygulama",
        "gp" => "Yama (güncelleme)",
        "ac" => "Ek içerik (DLC)",
        "sd" => "Kayıt verisi",
        { Length: > 0 } c => c.ToUpperInvariant(),
        _ => contentType switch
        {
            0x1A => "Oyun / yama (GD)",
            0x1B => "Ek içerik (AC)",
            0x1C => "Ek içerik, verisiz (AL)",
            0x1E => "Delta yama (DP)",
            _ => "Bilinmiyor"
        }
    };

    /// <summary>
    /// Copies unencrypted entries to <paramref name="targetFolder"/> (flat, sanitized names, never overwrites) and
    /// returns the written paths. Progress reports total bytes written. A cancelled/failed entry leaves no partial file.
    /// </summary>
    public static List<string> ExtractEntries(Stream stream, IEnumerable<Ps4PkgEntry> entries, string targetFolder,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        var list = entries.ToList();
        if (list.FirstOrDefault(e => e.IsEncrypted) is { } locked)
            throw new NotSupportedException($"\"{locked.Name}\" şifreli; şifreli PKG girdileri çıkarılamaz.");
        long length = stream.Length;
        if (list.FirstOrDefault(e => e.Offset < 0 || e.Size < 0 || e.Offset + e.Size > length) is { } bad)
            throw new Ps4PkgFormatException($"\"{bad.Name}\" dosyanın dışını gösteriyor.");

        Directory.CreateDirectory(targetFolder);
        EnsureFreeSpace(targetFolder, list.Sum(e => e.Size));

        var written = new List<string>(list.Count);
        long total = 0;
        byte[] buffer = new byte[81920];
        foreach (var entry in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, output) = ZipEntryExtractor.CreateUniqueFile(targetFolder, ZipEntryExtractor.GetSafeFileName(entry.Name));
            bool success = false;
            try
            {
                using (output)
                {
                    stream.Position = entry.Offset;
                    long remaining = entry.Size;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0) throw new Ps4PkgFormatException($"\"{entry.Name}\" okunurken dosya erken bitti.");
                        output.Write(buffer, 0, read);
                        remaining -= read;
                        total += read;
                        progress?.Report(total);
                    }
                }
                success = true;
                written.Add(path);
            }
            finally
            {
                if (!success)
                {
                    try { File.Delete(path); } catch { /* best effort cleanup */ }
                }
            }
        }
        return written;
    }

    private static void EnsureFreeSpace(string folder, long needed)
    {
        long free;
        try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; }
        catch (ArgumentException) { return; } // UNC / unusual roots: DriveInfo cannot tell, let the write fail instead
        if (free < needed)
            throw new IOException($"Hedef diskte yeterli boş alan yok. Gerekli: {DownloadTask.FormatBytes(needed)}, boş: {DownloadTask.FormatBytes(free)}.");
    }

    private static string ResolveName(uint id, uint nameOffset, byte[]? names)
    {
        if (names != null && nameOffset > 0 && nameOffset < names.Length)
        {
            int end = Array.IndexOf(names, (byte)0, (int)nameOffset);
            int len = (end < 0 ? names.Length : end) - (int)nameOffset;
            if (len > 0 && len <= 255) return Encoding.UTF8.GetString(names, (int)nameOffset, len);
        }
        if (KnownNames.TryGetValue(id, out var known)) return known;
        if (id is >= 0x1201 and <= 0x121F) return $"icon0_{id - 0x1201:d2}.png";
        if (id is >= 0x1241 and <= 0x125F) return $"pic1_{id - 0x1241:d2}.png";
        return $"entry_0x{id:X4}.bin";
    }

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        if (offset < 0 || offset + count > stream.Length)
            throw new Ps4PkgFormatException("PKG verisi dosyanın dışını gösteriyor (eksik ya da bozuk).");
        var buffer = new byte[count];
        stream.Position = offset;
        try { stream.ReadExactly(buffer); }
        catch (EndOfStreamException) { throw new Ps4PkgFormatException("PKG dosyası beklenenden kısa (eksik indirilmiş olabilir)."); }
        return buffer;
    }

    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
    private static ulong U64(byte[] b, int at) => BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at));
}
