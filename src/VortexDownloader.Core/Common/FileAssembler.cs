using System.Buffers;
using System.IO;
using VortexDownloader.Core.Models;

namespace VortexDownloader.Core.Common;

public class FileAssembler
{
    private const int BufferSize = 256 * 1024; // 256 KB zero-alloc buffer

    public static async Task AssembleAsync(
        DownloadTask task,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.Chunks.Count == 0)
        {
            throw new InvalidOperationException("Birleştirilecek parça bulunamadı.");
        }

        // Sort chunks strictly by starting position
        List<DownloadChunk> sortedChunks = [.. task.Chunks.OrderBy(c => c.StartPos)];

        string destPath = task.DestinationPath;
        string? destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        var chunk0 = sortedChunks[0];
        if (!File.Exists(chunk0.TempFilePath))
        {
            throw new FileNotFoundException($"Parça dosyası bulunamadı: {chunk0.TempFilePath}");
        }

        long totalExpectedBytes = task.TotalBytes > 0 
            ? task.TotalBytes 
            : sortedChunks.Sum(c => c.TotalBytes > 0 ? c.TotalBytes : (File.Exists(c.TempFilePath) ? new FileInfo(c.TempFilePath).Length : 0));

        long accumulatedBytes = 0;

        // Fast IDM path: Move chunk 0 directly to destination file
        bool movedChunk0 = false;
        try
        {
            if (File.Exists(destPath))
            {
                File.Delete(destPath);
            }
            File.Move(chunk0.TempFilePath, destPath);
            movedChunk0 = true;
            accumulatedBytes += new FileInfo(destPath).Length;
            if (totalExpectedBytes > 0)
            {
                progress?.Report(Math.Min(100.0, (double)accumulatedBytes / totalExpectedBytes * 100.0));
            }
        }
        catch
        {
            // If cross-volume move fails, fall back to streaming copy
            movedChunk0 = false;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            if (!movedChunk0)
            {
                await using var destStream = new FileStream(
                    destPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    useAsync: true);

                await using var srcStream = new FileStream(
                    chunk0.TempFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    useAsync: true);

                int read;
                while ((read = await srcStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    accumulatedBytes += read;
                    if (totalExpectedBytes > 0)
                    {
                        progress?.Report(Math.Min(100.0, (double)accumulatedBytes / totalExpectedBytes * 100.0));
                    }
                }
            }

            // Append remaining chunks
            if (sortedChunks.Count > 1)
            {
                await using var appendStream = new FileStream(
                    destPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    useAsync: true);

                for (int i = 1; i < sortedChunks.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = sortedChunks[i];

                    if (!File.Exists(chunk.TempFilePath))
                    {
                        throw new FileNotFoundException($"Parça dosyası bulunamadı: {chunk.TempFilePath}");
                    }

                    await using (var chunkStream = new FileStream(
                        chunk.TempFilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        BufferSize,
                        useAsync: true))
                    {
                        int read;
                        while ((read = await chunkStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await appendStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            accumulatedBytes += read;
                            if (totalExpectedBytes > 0)
                            {
                                progress?.Report(Math.Min(100.0, (double)accumulatedBytes / totalExpectedBytes * 100.0));
                            }
                        }
                    }

                    // Clean up assembled temporary part
                    try
                    {
                        File.Delete(chunk.TempFilePath);
                    }
                    catch
                    {
                        // Best effort cleanup for temporary part file
                    }
                }

                await appendStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Cleanup chunk 0 temp file if it wasn't moved
            if (!movedChunk0 && File.Exists(chunk0.TempFilePath))
            {
                try
                {
                    File.Delete(chunk0.TempFilePath);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Verify final assembled file
        var finalInfo = new FileInfo(destPath);
        if (task.TotalBytes > 0 && finalInfo.Length != task.TotalBytes)
        {
            throw new InvalidDataException($"Birleştirilen dosya boyutu ({finalInfo.Length} B), beklenen boyut ile ({task.TotalBytes} B) uyuşmuyor.");
        }

        progress?.Report(100.0);
    }
}
