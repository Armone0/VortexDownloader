using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;

namespace VortexDownloader.Core.Common;

/// <summary>
/// Downloads a single entry out of a remote ZIP archive using HTTP Range requests,
/// without downloading the rest of the archive.
/// </summary>
public static class ZipEntryExtractor
{
    private const uint LocalFileHeaderSignature = 0x04034b50;
    private const int LocalFileHeaderSize = 30;

    /// <summary>
    /// Extracts <paramref name="entry"/> into <paramref name="targetFolder"/> and returns the written file path.
    /// Progress reports the number of uncompressed bytes written so far.
    /// </summary>
    public static async Task<string> ExtractRemoteEntryAsync(
        HttpClient httpClient,
        string url,
        ZipEntryMetadata entry,
        string targetFolder,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);

        if (entry.IsDirectory)
            throw new NotSupportedException("Klasörler tek dosya olarak indirilemez.");
        if (entry.IsEncrypted)
            throw new NotSupportedException("Bu dosya şifreli; şifreli ZIP girdileri desteklenmiyor.");
        if (entry.CompressionMethod != 0 && entry.CompressionMethod != 8)
            throw new NotSupportedException($"Desteklenmeyen sıkıştırma yöntemi ({DescribeMethod(entry.CompressionMethod)}). Yalnızca Stored ve Deflate desteklenir.");

        // 1. Local header: name/extra lengths may differ from the central directory, so read them from here.
        byte[] header = await ZipInspector.FetchRangeBytesAsync(httpClient, url, entry.LocalHeaderOffset,
            entry.LocalHeaderOffset + LocalFileHeaderSize - 1, cancellationToken).ConfigureAwait(false);
        if (header.Length < LocalFileHeaderSize || BitConverter.ToUInt32(header, 0) != LocalFileHeaderSignature)
            throw new InvalidDataException("ZIP yerel dosya başlığı geçersiz.");
        if ((BitConverter.ToUInt16(header, 6) & 0x0001) != 0)
            throw new NotSupportedException("Bu dosya şifreli; şifreli ZIP girdileri desteklenmiyor.");

        long dataStart = entry.LocalHeaderOffset + LocalFileHeaderSize
                         + BitConverter.ToUInt16(header, 26) + BitConverter.ToUInt16(header, 28);

        Directory.CreateDirectory(targetFolder);
        var (outputPath, output) = CreateUniqueFile(targetFolder, GetSafeFileName(entry.FileName));
        bool success = false;
        try
        {
            using (output)
            {
                var crc = new Crc32();
                long written = 0;

                if (entry.CompressedSize > 0)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(dataStart, dataStart + entry.CompressedSize - 1);
                    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.OK)
                        throw new NotSupportedException("Sunucu HTTP Range isteklerini desteklemiyor (HTTP 200 döndü).");
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                        throw new HttpRequestException($"Range isteği başarısız oldu: HTTP {response.StatusCode}");

                    using var raw = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using Stream data = entry.CompressionMethod == 8 ? new DeflateStream(raw, CompressionMode.Decompress) : raw;

                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = await data.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        // Stops servers that send extra bytes and decompression bombs alike.
                        if (written + read > entry.UncompressedSize)
                            throw new InvalidDataException("Çıkarılan veri beklenen boyutu aşıyor; arşiv bozuk olabilir.");
                        crc.Append(buffer.AsSpan(0, read));
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        written += read;
                        progress?.Report(written);
                    }
                }

                if (written != entry.UncompressedSize)
                    throw new InvalidDataException($"Dosya eksik indirildi. Beklenen: {entry.UncompressedSize}, Yazılan: {written} bayt.");
                if (crc.Value != entry.Crc32)
                    throw new InvalidDataException("CRC-32 doğrulaması başarısız; indirilen dosya bozuk.");
            }

            success = true;
            return outputPath;
        }
        finally
        {
            if (!success)
            {
                try { File.Delete(outputPath); } catch { /* best effort cleanup */ }
            }
        }
    }

    /// <summary>
    /// Returns only the file-name part of a ZIP entry name, stripped of path segments and invalid characters (zip slip guard).
    /// </summary>
    public static string GetSafeFileName(string entryName)
    {
        string name = (entryName ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');

        if (name.Length == 0)
            return "dosya";

        string stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        bool reserved = stem is "CON" or "PRN" or "AUX" or "NUL" ||
                        (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]));
        return reserved ? "_" + name : name;
    }

    internal static (string Path, FileStream Stream) CreateUniqueFile(string folder, string fileName)
    {
        string root = Path.GetFullPath(folder);
        string expectedDir = Path.GetDirectoryName(Path.Combine(root, "_"))!;
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);

        for (int i = 0; ; i++)
        {
            string candidate = Path.GetFullPath(Path.Combine(root, i == 0 ? fileName : $"{stem} ({i}){ext}"));
            if (!string.Equals(Path.GetDirectoryName(candidate), expectedDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Geçersiz dosya adı: hedef klasörün dışına yazılamaz.");

            try
            {
                // CreateNew never overwrites an existing file.
                return (candidate, new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true));
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && (File.Exists(candidate) || Directory.Exists(candidate)))
            {
                // Name taken; try the next suffix.
            }
        }
    }

    private static string DescribeMethod(int method) => method switch
    {
        9 => "Deflate64",
        12 => "BZip2",
        14 => "LZMA",
        93 => "Zstandard",
        95 => "XZ",
        99 => "AES şifreleme",
        _ => $"yöntem {method}"
    };

    /// <summary>Table-based CRC-32 (IEEE 802.3), as used by ZIP.</summary>
    internal sealed class Crc32
    {
        private static readonly uint[] Table = BuildTable();
        private uint _crc = 0xFFFFFFFF;

        public uint Value => ~_crc;

        public void Append(ReadOnlySpan<byte> data)
        {
            uint crc = _crc;
            foreach (byte b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            _crc = crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }
    }
}
