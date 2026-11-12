using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace VortexDownloader.Core.Common;

/// <summary>
/// Metadata describing an entry (file or directory) inside a remote or local ZIP archive.
/// </summary>
public class ZipEntryMetadata
{
    public string FileName { get; set; } = string.Empty;
    public long UncompressedSize { get; set; }
    public long CompressedSize { get; set; }
    public bool IsDirectory { get; set; }
    public DateTime LastModified { get; set; }
    public int CompressionMethod { get; set; }
    public uint Crc32 { get; set; }
    public int GeneralPurposeFlag { get; set; }
    public long LocalHeaderOffset { get; set; }
    public bool IsEncrypted => (GeneralPurposeFlag & 0x0001) != 0;
}

/// <summary>
/// Provides capabilities to peek inside ZIP archives without downloading the entire file,
/// using HTTP Range requests to read the Central Directory at the end of the archive.
/// </summary>
public static class ZipInspector
{
    private const uint EocdSignature = 0x06054b50;
    private const uint CentralDirectoryHeaderSignature = 0x02014b50;
    private const int MaxEocdScanSize = 65536;

    /// <summary>
    /// Peeks into a remote ZIP archive at the specified URL using HTTP Range requests
    /// and parses its Central Directory without downloading the full archive.
    /// </summary>
    public static async Task<List<ZipEntryMetadata>> InspectRemoteZipAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        // 1. Determine Content-Length and verify Range support
        long totalLength = -1;
        bool supportsRange = false;

        try
        {
            using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResp = await httpClient.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (headResp.IsSuccessStatusCode)
            {
                if (headResp.Content.Headers.ContentLength.HasValue)
                {
                    totalLength = headResp.Content.Headers.ContentLength.Value;
                }
                if (headResp.Headers.AcceptRanges.Contains("bytes") ||
                    (headResp.Headers.TryGetValues("Accept-Ranges", out var values) && values.Any(v => v.Contains("bytes", StringComparison.OrdinalIgnoreCase))))
                {
                    supportsRange = true;
                }
            }
        }
        catch
        {
            // Fallback to GET range test if HEAD is not allowed
        }

        if (totalLength <= 0 || !supportsRange)
        {
            using var rangeReq = new HttpRequestMessage(HttpMethod.Get, url);
            rangeReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var rangeResp = await httpClient.SendAsync(rangeReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (rangeResp.StatusCode == HttpStatusCode.PartialContent)
            {
                supportsRange = true;
                if (rangeResp.Content.Headers.ContentRange?.Length.HasValue == true)
                {
                    totalLength = rangeResp.Content.Headers.ContentRange.Length.Value;
                }
            }
            else if (rangeResp.IsSuccessStatusCode)
            {
                supportsRange = false;
                if (rangeResp.Content.Headers.ContentLength.HasValue)
                {
                    totalLength = rangeResp.Content.Headers.ContentLength.Value;
                }
            }
        }

        if (!supportsRange)
        {
            throw new NotSupportedException("Sunucu HTTP Range isteklerini desteklemiyor.");
        }

        if (totalLength < 22)
        {
            throw new InvalidDataException("Belirtilen dosya geçerli bir ZIP arşivi olamayacak kadar küçük veya boş.");
        }

        // 2. Request the last 65,536 bytes (or file length if < 65KB)
        long fetchSize = Math.Min(totalLength, MaxEocdScanSize);
        long tailStart = totalLength - fetchSize;
        long tailEnd = totalLength - 1;

        byte[] tailBuffer = await FetchRangeBytesAsync(httpClient, url, tailStart, tailEnd, cancellationToken).ConfigureAwait(false);
        if (tailBuffer.Length == 0)
        {
            throw new InvalidDataException("Uzak sunucudan ZIP sonu verisi okunamadı.");
        }

        // 3. Scan backwards for End of Central Directory (EOCD) signature: 0x06054b50
        int eocdOffsetInTail = FindEocdSignature(tailBuffer);
        if (eocdOffsetInTail < 0)
        {
            throw new InvalidDataException("Geçerli bir ZIP End of Central Directory (EOCD) başlığı bulunamadı.");
        }

        // 4. Parse EOCD to get Central Directory offset and size
        uint cdSize = BitConverter.ToUInt32(tailBuffer, eocdOffsetInTail + 12);
        uint cdOffset = BitConverter.ToUInt32(tailBuffer, eocdOffsetInTail + 16);

        if (cdSize == 0)
        {
            return [];
        }

        if ((long)cdOffset + cdSize > totalLength || cdSize > 100 * 1024 * 1024)
        {
            throw new InvalidDataException("Central Directory verisi geçersiz veya dosya sınırlarını aşıyor.");
        }

        // 5. If Central Directory spans before the last 65KB, issue one more range request
        byte[] cdData;
        long cdStart = cdOffset;
        long cdEnd = cdOffset + cdSize - 1;

        long actualTailStart = totalLength - tailBuffer.Length;
        if (cdStart >= actualTailStart && cdEnd < totalLength && (cdEnd - actualTailStart) < tailBuffer.Length)
        {
            // Central Directory is already completely inside tailBuffer
            int cdStartInTail = (int)(cdStart - actualTailStart);
            cdData = new byte[cdSize];
            Array.Copy(tailBuffer, cdStartInTail, cdData, 0, (int)cdSize);
        }
        else
        {
            // Range request for full Central Directory
            cdData = await FetchRangeBytesAsync(httpClient, url, cdStart, cdEnd, cancellationToken).ConfigureAwait(false);
            if (cdData.Length < cdSize)
            {
                throw new InvalidDataException($"Central Directory verisi eksik indirildi. Beklenen: {cdSize}, Alınan: {cdData.Length}");
            }
        }

        // 6. Parse Central Directory file headers
        return ParseCentralDirectoryHeaders(cdData);
    }

    /// <summary>
    /// Overload using default HttpClient.
    /// </summary>
    public static async Task<List<ZipEntryMetadata>> InspectRemoteZipAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient();
        return await InspectRemoteZipAsync(client, url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the Central Directory of a ZIP file directly from a byte array.
    /// </summary>
    public static List<ZipEntryMetadata> InspectZipBytes(byte[] zipBytes)
    {
        ArgumentNullException.ThrowIfNull(zipBytes);
        if (zipBytes.Length < 22)
        {
            throw new InvalidDataException("Belirtilen veri geçerli bir ZIP arşivi olamayacak kadar küçük.");
        }

        int eocdPos = FindEocdSignature(zipBytes);
        if (eocdPos < 0)
        {
            throw new InvalidDataException("Geçerli bir ZIP End of Central Directory (EOCD) başlığı bulunamadı.");
        }

        uint cdSize = BitConverter.ToUInt32(zipBytes, eocdPos + 12);
        uint cdOffset = BitConverter.ToUInt32(zipBytes, eocdPos + 16);

        if ((long)cdOffset + cdSize > zipBytes.Length)
        {
            throw new InvalidDataException("Central Directory verisi dosya sınırlarını aşıyor.");
        }

        byte[] cdData = new byte[cdSize];
        Array.Copy(zipBytes, (int)cdOffset, cdData, 0, (int)cdSize);

        return ParseCentralDirectoryHeaders(cdData);
    }

    /// <summary>
    /// Parses the Central Directory of a ZIP file directly from a stream.
    /// </summary>
    public static List<ZipEntryMetadata> InspectZipStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return InspectZipBytes(ms.ToArray());
    }

    /// <summary>
    /// Scans backwards in buffer to locate the End of Central Directory (EOCD) signature 0x06054b50.
    /// </summary>
    public static int FindEocdSignature(byte[] buffer)
    {
        if (buffer.Length < 22) return -1;

        // Check for exact comment length match first
        for (int i = buffer.Length - 22; i >= 0; i--)
        {
            if (buffer[i] == 0x50 &&
                buffer[i + 1] == 0x4B &&
                buffer[i + 2] == 0x05 &&
                buffer[i + 3] == 0x06)
            {
                ushort commentLen = BitConverter.ToUInt16(buffer, i + 20);
                if (i + 22 + commentLen == buffer.Length)
                {
                    return i;
                }
            }
        }

        // Fallback check for trailing data or comment size within buffer
        for (int i = buffer.Length - 22; i >= 0; i--)
        {
            if (buffer[i] == 0x50 &&
                buffer[i + 1] == 0x4B &&
                buffer[i + 2] == 0x05 &&
                buffer[i + 3] == 0x06)
            {
                ushort commentLen = BitConverter.ToUInt16(buffer, i + 20);
                if (i + 22 + commentLen <= buffer.Length)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Parses individual Central Directory file headers (signature 0x02014b50).
    /// </summary>
    public static List<ZipEntryMetadata> ParseCentralDirectoryHeaders(byte[] cdData)
    {
        var entries = new List<ZipEntryMetadata>();
        int cursor = 0;

        while (cursor + 46 <= cdData.Length)
        {
            uint signature = BitConverter.ToUInt32(cdData, cursor);
            if (signature != CentralDirectoryHeaderSignature)
            {
                break;
            }

            ushort generalPurposeFlag = BitConverter.ToUInt16(cdData, cursor + 8);
            ushort compressionMethod = BitConverter.ToUInt16(cdData, cursor + 10);
            ushort dosTime = BitConverter.ToUInt16(cdData, cursor + 12);
            ushort dosDate = BitConverter.ToUInt16(cdData, cursor + 14);
            uint crc32 = BitConverter.ToUInt32(cdData, cursor + 16);
            uint compSize = BitConverter.ToUInt32(cdData, cursor + 20);
            uint uncompSize = BitConverter.ToUInt32(cdData, cursor + 24);
            ushort fileNameLength = BitConverter.ToUInt16(cdData, cursor + 28);
            ushort extraFieldLength = BitConverter.ToUInt16(cdData, cursor + 30);
            ushort fileCommentLength = BitConverter.ToUInt16(cdData, cursor + 32);
            uint externalAttributes = BitConverter.ToUInt32(cdData, cursor + 38);
            uint localHeaderOffset = BitConverter.ToUInt32(cdData, cursor + 42);

            int fileNameOffset = cursor + 46;
            if (fileNameOffset + fileNameLength > cdData.Length)
            {
                break;
            }

            bool isUtf8 = (generalPurposeFlag & 0x0800) != 0;
            string fileName;
            try
            {
                fileName = isUtf8
                    ? Encoding.UTF8.GetString(cdData, fileNameOffset, fileNameLength)
                    : Encoding.Latin1.GetString(cdData, fileNameOffset, fileNameLength);
            }
            catch
            {
                fileName = Encoding.UTF8.GetString(cdData, fileNameOffset, fileNameLength);
            }

            long finalUncompSize = uncompSize;
            long finalCompSize = compSize;
            long finalLocalHeaderOffset = localHeaderOffset;

            // Zip64 size/offset check if values exceed 4GB (0xFFFFFFFF)
            if (extraFieldLength > 0 && (uncompSize == 0xFFFFFFFF || compSize == 0xFFFFFFFF || localHeaderOffset == 0xFFFFFFFF))
            {
                int extraOffset = fileNameOffset + fileNameLength;
                int extraEnd = Math.Min(extraOffset + extraFieldLength, cdData.Length);
                int extraCursor = extraOffset;

                while (extraCursor + 4 <= extraEnd)
                {
                    ushort headerId = BitConverter.ToUInt16(cdData, extraCursor);
                    ushort dataSize = BitConverter.ToUInt16(cdData, extraCursor + 2);
                    int dataStart = extraCursor + 4;

                    if (headerId == 0x0001 && dataStart + dataSize <= extraEnd)
                    {
                        int recordEnd = dataStart + dataSize;
                        int currentPos = dataStart;
                        if (uncompSize == 0xFFFFFFFF && currentPos + 8 <= recordEnd)
                        {
                            finalUncompSize = BitConverter.ToInt64(cdData, currentPos);
                            currentPos += 8;
                        }
                        if (compSize == 0xFFFFFFFF && currentPos + 8 <= recordEnd)
                        {
                            finalCompSize = BitConverter.ToInt64(cdData, currentPos);
                            currentPos += 8;
                        }
                        if (localHeaderOffset == 0xFFFFFFFF && currentPos + 8 <= recordEnd)
                        {
                            finalLocalHeaderOffset = BitConverter.ToInt64(cdData, currentPos);
                            currentPos += 8;
                        }
                        break;
                    }

                    extraCursor += 4 + dataSize;
                }
            }

            bool isDirectory = fileName.EndsWith('/') || fileName.EndsWith('\\') ||
                               ((externalAttributes & 0x10) != 0 && finalUncompSize == 0);

            DateTime lastModified = ParseDosDateTime(dosDate, dosTime);

            entries.Add(new ZipEntryMetadata
            {
                FileName = fileName,
                UncompressedSize = finalUncompSize,
                CompressedSize = finalCompSize,
                IsDirectory = isDirectory,
                LastModified = lastModified,
                CompressionMethod = compressionMethod,
                Crc32 = crc32,
                GeneralPurposeFlag = generalPurposeFlag,
                LocalHeaderOffset = finalLocalHeaderOffset
            });

            cursor += 46 + fileNameLength + extraFieldLength + fileCommentLength;
        }

        return entries;
    }

    internal static async Task<byte[]> FetchRangeBytesAsync(
        HttpClient httpClient,
        string url,
        long from,
        long to,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            throw new NotSupportedException("Sunucu HTTP Range isteklerini desteklemiyor (HTTP 200 döndü).");
        }

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException($"Range isteği başarısız oldu: HTTP {response.StatusCode}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTime ParseDosDateTime(ushort dosDate, ushort dosTime)
    {
        if (dosDate == 0) return DateTime.MinValue;

        try
        {
            int year = ((dosDate >> 9) & 0x7F) + 1980;
            int month = (dosDate >> 5) & 0x0F;
            int day = dosDate & 0x1F;
            int hour = (dosTime >> 11) & 0x1F;
            int minute = (dosTime >> 5) & 0x3F;
            int second = Math.Min(59, (dosTime & 0x1F) * 2);

            if (month >= 1 && month <= 12 && day >= 1 && day <= 31 && hour <= 23 && minute <= 59)
            {
                return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
            }
        }
        catch
        {
            // Ignore format issues and return MinValue
        }

        return DateTime.MinValue;
    }
}
