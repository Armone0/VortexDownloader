using System.Buffers;
using System.IO;
using VortexDownloader.Core.Models;

namespace VortexDownloader.Core.Common;

public class FileAssembler
{
    private const int BufferSize = 512 * 1024; // 512 KB high-throughput streaming buffer

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

        // Strict verification: all chunks must be completed and exist on disk before assembly
        foreach (var chunk in sortedChunks)
        {
            if (!chunk.IsCompleted)
            {
                throw new InvalidOperationException($"Parça {chunk.Id} henüz tamamlanmadı.");
            }
            if (!File.Exists(chunk.TempFilePath))
            {
                throw new FileNotFoundException($"Parça dosyası bulunamadı: {chunk.TempFilePath}");
            }
        }

        // Strict continuity verification: ensure no gaps between chunks
        for (int i = 0; i < sortedChunks.Count - 1; i++)
        {
            if (sortedChunks[i].EndPos >= 0 && sortedChunks[i].EndPos + 1 != sortedChunks[i + 1].StartPos)
            {
                throw new InvalidDataException($"Parça süreklilik hatası: Parça {sortedChunks[i].Id} bitişi {sortedChunks[i].EndPos}, sonraki başlangıç {sortedChunks[i + 1].StartPos}");
            }
        }

        string destPath = task.DestinationPath;
        string? destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        long totalExpectedBytes = task.TotalBytes > 0 
            ? task.TotalBytes 
            : sortedChunks.Sum(c => c.TotalBytes > 0 ? c.TotalBytes : (File.Exists(c.TempFilePath) ? new FileInfo(c.TempFilePath).Length : 0));

        long accumulatedBytes = 0;
        double lastReportedPct = 0.0;

        void ReportProgress(bool force = false)
        {
            if (totalExpectedBytes <= 0 || progress == null) return;
            double currentPct = (double)accumulatedBytes / totalExpectedBytes * 100.0;
            if (force || (currentPct - lastReportedPct >= 0.25) || accumulatedBytes >= totalExpectedBytes)
            {
                lastReportedPct = currentPct;
                progress.Report(Math.Min(100.0, currentPct));
            }
        }

        // Fast path for single chunk: atomic Move
        if (sortedChunks.Count == 1)
        {
            var singleChunk = sortedChunks[0];
            if (File.Exists(destPath))
            {
                File.Delete(destPath);
            }
            try
            {
                File.Move(singleChunk.TempFilePath, destPath);
                accumulatedBytes = new FileInfo(destPath).Length;
                ReportProgress(force: true);
            }
            catch
            {
                // Fall back to stream copy if cross-volume move fails
                await using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                await using var srcStream = new FileStream(singleChunk.TempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                await srcStream.CopyToAsync(destStream, cancellationToken).ConfigureAwait(false);
                accumulatedBytes = destStream.Length;
            }

            // Verify
            var singleFinalInfo = new FileInfo(destPath);
            if (task.TotalBytes > 0 && singleFinalInfo.Length != task.TotalBytes)
            {
                throw new InvalidDataException($"Birleştirilen dosya boyutu ({singleFinalInfo.Length} B), beklenen boyut ile ({task.TotalBytes} B) uyuşmuyor.");
            }

            // Clean up temp dir
            CleanupTempDir(task.TempDirectory);
            progress?.Report(100.0);
            return;
        }

        // Multi-chunk streaming assembly: keep all chunk files intact until verified
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using (var destStream = new FileStream(
                destPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                for (int i = 0; i < sortedChunks.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = sortedChunks[i];

                    await using (var chunkStream = new FileStream(
                        chunk.TempFilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 4096,
                        options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        long maxChunkBytes = chunk.EndPos >= chunk.StartPos ? (chunk.EndPos - chunk.StartPos + 1) : -1;
                        long chunkBytesRead = 0;

                        int read;
                        while ((read = await chunkStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            int toWrite = read;
                            if (maxChunkBytes > 0)
                            {
                                long allowed = maxChunkBytes - chunkBytesRead;
                                if (allowed <= 0) break;
                                toWrite = (int)Math.Min(read, allowed);
                            }

                            await destStream.WriteAsync(buffer.AsMemory(0, toWrite), cancellationToken).ConfigureAwait(false);
                            chunkBytesRead += toWrite;
                            accumulatedBytes += toWrite;
                            ReportProgress();

                            if (maxChunkBytes > 0 && chunkBytesRead >= maxChunkBytes)
                            {
                                break;
                            }
                        }
                    }
                }

                await destStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Verify final assembled file
            var finalInfo = new FileInfo(destPath);
            if (task.TotalBytes > 0 && finalInfo.Length != task.TotalBytes)
            {
                throw new InvalidDataException($"Birleştirilen dosya boyutu ({finalInfo.Length} B), beklenen boyut ile ({task.TotalBytes} B) uyuşmuyor.");
            }

            // Assembly verified! Now safely delete temporary chunk files and directory
            foreach (var chunk in sortedChunks)
            {
                try
                {
                    if (File.Exists(chunk.TempFilePath))
                    {
                        File.Delete(chunk.TempFilePath);
                    }
                }
                catch
                {
                    // Best effort cleanup
                }
            }

            CleanupTempDir(task.TempDirectory);
            progress?.Report(100.0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void CleanupTempDir(string? tempDir)
    {
        if (!string.IsNullOrWhiteSpace(tempDir) && Directory.Exists(tempDir))
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort directory removal
            }
        }
    }
}
