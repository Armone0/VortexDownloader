using System.IO;
using VortexDownloader.Core.Models;

namespace VortexDownloader.Core.Common;

public class ChunksManager
{
    public const long DefaultMinSplitSize = 512 * 1024; // 512 KB

    public static List<DownloadChunk> InitializeChunks(
        Guid taskId,
        long totalBytes,
        int requestedChunkCount,
        string tempDirectory)
    {
        Directory.CreateDirectory(tempDirectory);
        List<DownloadChunk> chunks = [];

        if (totalBytes <= 0 || requestedChunkCount <= 1)
        {
            chunks.Add(new DownloadChunk
            {
                Id = 0,
                TaskId = taskId,
                StartPos = 0,
                CurrentPos = 0,
                EndPos = totalBytes > 0 ? totalBytes - 1 : -1,
                TempFilePath = Path.Combine(tempDirectory, $"{taskId}_part_0.bin"),
                IsCompleted = false
            });
            return chunks;
        }

        // Adjust chunk count if total size is smaller than chunkCount * minSplitSize
        int actualChunks = requestedChunkCount;
        while (actualChunks > 1 && (totalBytes / actualChunks) < 64 * 1024)
        {
            actualChunks--;
        }

        long baseChunkSize = totalBytes / actualChunks;
        for (int i = 0; i < actualChunks; i++)
        {
            long start = i * baseChunkSize;
            long end = (i == actualChunks - 1) ? totalBytes - 1 : (start + baseChunkSize - 1);

            chunks.Add(new DownloadChunk
            {
                Id = i,
                TaskId = taskId,
                StartPos = start,
                CurrentPos = start,
                EndPos = end,
                TempFilePath = Path.Combine(tempDirectory, $"{taskId}_part_{i}.bin"),
                IsCompleted = false
            });
        }

        return chunks;
    }

    /// <summary>
    /// Dynamic segmentation: Finds the incomplete chunk with the maximum remaining un-downloaded bytes.
    /// </summary>
    public static DownloadChunk? FindMaxFreeChunk(
        IEnumerable<DownloadChunk> chunks,
        long minSplitSize = DefaultMinSplitSize)
    {
        DownloadChunk? candidate = null;
        long maxRemaining = 0;

        foreach (var chunk in chunks)
        {
            if (chunk.IsCompleted || chunk.EndPos < 0)
            {
                continue;
            }

            long remaining = chunk.EndPos - chunk.CurrentPos + 1;
            if (remaining >= minSplitSize && remaining > maxRemaining)
            {
                maxRemaining = remaining;
                candidate = chunk;
            }
        }

        return candidate;
    }

    /// <summary>
    /// Dynamic chunk split: Splits an active chunk into two halves.
    /// The original chunk retains the first half, and the new chunk takes the second half.
    /// </summary>
    public static (DownloadChunk ModifiedOriginal, DownloadChunk NewChunk)? SplitChunk(
        DownloadChunk chunkToSplit,
        int newChunkId,
        string newTempFilePath,
        long minSplitSize = DefaultMinSplitSize)
    {
        if (chunkToSplit.IsCompleted || chunkToSplit.EndPos < 0)
        {
            return null;
        }

        long remaining = chunkToSplit.EndPos - chunkToSplit.CurrentPos + 1;
        if (remaining < minSplitSize)
        {
            return null;
        }

        long half = remaining / 2;
        long newOriginalEnd = chunkToSplit.CurrentPos + half - 1;
        long newChunkStart = newOriginalEnd + 1;
        long oldEnd = chunkToSplit.EndPos;

        chunkToSplit.EndPos = newOriginalEnd;

        var newChunk = new DownloadChunk
        {
            Id = newChunkId,
            TaskId = chunkToSplit.TaskId,
            StartPos = newChunkStart,
            CurrentPos = newChunkStart,
            EndPos = oldEnd,
            TempFilePath = newTempFilePath,
            IsCompleted = false
        };

        return (chunkToSplit, newChunk);
    }
}
