using SharpCompress.Archives.Rar;
using SharpCompress.Readers;

namespace VortexDownloader.Core.Common;

/// <summary>Entry list of a remote RAR plus whether it is solid (solid = an entry needs everything before it).</summary>
public sealed record RemoteRarListing(List<ZipEntryMetadata> Entries, bool IsSolid);

/// <summary>
/// Lists and extracts single files out of a remote RAR (RAR4/RAR5) through <see cref="HttpRangeStream"/>,
/// fetching only the header blocks and the picked files' data. Entries reuse <see cref="ZipEntryMetadata"/>
/// so the preview window and download cards work for both formats.
/// </summary>
public static class RemoteRarArchive
{
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = TimeSpan.FromSeconds(60) };

    public static bool IsRarName(string? nameOrUrl)
    {
        if (string.IsNullOrWhiteSpace(nameOrUrl)) return false;
        string path = Uri.TryCreate(nameOrUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : nameOrUrl;
        return path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase);
    }

    public static Task<RemoteRarListing> ListAsync(string url, string? cookies = null, string? referrer = null,
        CancellationToken cancellationToken = default) => ListAsync(SharedClient, url, cookies, referrer, cancellationToken);

    public static async Task<RemoteRarListing> ListAsync(HttpClient client, string url, string? cookies, string? referrer,
        CancellationToken cancellationToken)
    {
        long length = await HttpRangeStream.GetRangeLengthAsync(client, url, cookies, referrer, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var stream = new HttpRangeStream(client, url, length, cookies, referrer, cancellationToken);
            using var archive = OpenArchive(stream);
            var entries = archive.Entries.Select(e => new ZipEntryMetadata
            {
                FileName = e.Key ?? "",
                UncompressedSize = e.Size,
                CompressedSize = e.CompressedSize,
                IsDirectory = e.IsDirectory,
                LastModified = e.LastModifiedTime ?? DateTime.MinValue,
                Crc32 = SafeCrc(e),
                GeneralPurposeFlag = e.IsEncrypted ? 1 : 0 // IsEncrypted reads bit 0
            }).ToList();
            return new RemoteRarListing(entries, archive.IsSolid);
        }, cancellationToken).ConfigureAwait(false);
    }

    public static Task<string> ExtractEntryAsync(string url, ZipEntryMetadata entry, string targetFolder, string? cookies = null,
        string? referrer = null, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        ExtractEntryAsync(SharedClient, url, entry, targetFolder, cookies, referrer, progress, cancellationToken);

    /// <summary>Writes <paramref name="entry"/> into <paramref name="targetFolder"/> (never overwrites) and returns the path.</summary>
    public static async Task<string> ExtractEntryAsync(HttpClient client, string url, ZipEntryMetadata entry, string targetFolder,
        string? cookies, string? referrer, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        if (entry.IsDirectory) throw new NotSupportedException("Klasörler tek dosya olarak indirilemez.");
        if (entry.IsEncrypted) throw new NotSupportedException("Bu dosya şifreli; şifreli RAR girdileri desteklenmiyor.");

        long length = await HttpRangeStream.GetRangeLengthAsync(client, url, cookies, referrer, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var stream = new HttpRangeStream(client, url, length, cookies, referrer, cancellationToken);
            using var archive = OpenArchive(stream);

            Directory.CreateDirectory(targetFolder);
            var (outputPath, output) = ZipEntryExtractor.CreateUniqueFile(targetFolder, ZipEntryExtractor.GetSafeFileName(entry.FileName));
            bool success = false;
            try
            {
                using (output)
                {
                    Stream data;
                    IDisposable? owner = null;
                    if (archive.IsSolid)
                    {
                        // Solid: entries share one compression stream, so read forward from the start up to this entry.
                        stream.Position = 0;
                        var reader = archive.ExtractAllEntries();
                        owner = reader;
                        while (true)
                        {
                            if (!reader.MoveToNextEntry()) throw new InvalidDataException("Dosya arşivde bulunamadı.");
                            cancellationToken.ThrowIfCancellationRequested();
                            if (reader.Entry.Key == entry.FileName) break;
                        }
                        data = reader.OpenEntryStream();
                    }
                    else
                    {
                        var match = archive.Entries.FirstOrDefault(e => e.Key == entry.FileName && !e.IsDirectory)
                            ?? throw new InvalidDataException("Dosya arşivde bulunamadı.");
                        data = match.OpenEntryStream();
                    }

                    using (owner)
                    using (data)
                    {
                        var crc = new ZipEntryExtractor.Crc32();
                        byte[] buffer = new byte[81920];
                        long written = 0;
                        int read;
                        while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            // Guards against corrupt headers and decompression bombs alike.
                            if (written + read > entry.UncompressedSize)
                                throw new InvalidDataException("Çıkarılan veri beklenen boyutu aşıyor; arşiv bozuk olabilir.");
                            crc.Append(buffer.AsSpan(0, read));
                            output.Write(buffer, 0, read);
                            written += read;
                            progress?.Report(written);
                        }

                        if (written != entry.UncompressedSize)
                            throw new InvalidDataException($"Dosya eksik çıkarıldı. Beklenen: {entry.UncompressedSize}, Yazılan: {written} bayt.");
                        // RAR stores CRC-32 for every file (RAR5 may omit it: 0).
                        if (entry.Crc32 != 0 && crc.Value != entry.Crc32)
                            throw new InvalidDataException("CRC-32 doğrulaması başarısız; indirilen dosya bozuk.");
                    }
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
        }, cancellationToken).ConfigureAwait(false);
    }

    // Directories (and some RAR5 files) carry no CRC; SharpCompress throws instead of returning 0 there.
    private static uint SafeCrc(SharpCompress.Common.IEntry entry)
    {
        if (entry.IsDirectory) return 0;
        try { return unchecked((uint)entry.Crc); }
        catch (ArgumentNullException) { return 0; }
    }

    private static IRarArchive OpenArchive(Stream stream)
    {
        try
        {
            return RarArchive.OpenArchive(stream, new ReaderOptions { LeaveStreamOpen = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not HttpRequestException)
        {
            throw new InvalidDataException($"RAR arşivi okunamadı (bozuk, çok parçalı ya da başlıkları şifreli olabilir): {ex.Message}", ex);
        }
    }
}
