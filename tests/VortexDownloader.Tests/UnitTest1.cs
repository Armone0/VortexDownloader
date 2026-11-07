using VortexDownloader.Core.Common;
using VortexDownloader.Core.Models;
using Xunit;

namespace VortexDownloader.Tests;

public class ChunksManagerTests
{
    [Fact]
    public void FindMaxFreeChunk_ReturnsChunkWithLargestRemainingBytes()
    {
        var taskId = Guid.NewGuid();
        var chunks = new List<DownloadChunk>
        {
            new() { Id = 0, TaskId = taskId, StartPos = 0, CurrentPos = 9_000_000, EndPos = 10_000_000, IsCompleted = false }, // 1 MB remaining
            new() { Id = 1, TaskId = taskId, StartPos = 10_000_001, CurrentPos = 12_000_001, EndPos = 20_000_000, IsCompleted = false }, // ~8 MB remaining
            new() { Id = 2, TaskId = taskId, StartPos = 20_000_001, CurrentPos = 28_000_001, EndPos = 30_000_000, IsCompleted = false }, // ~2 MB remaining
            new() { Id = 3, TaskId = taskId, StartPos = 30_000_001, CurrentPos = 40_000_001, EndPos = 40_000_000, IsCompleted = true }   // Completed
        };

        var selected = ChunksManager.FindMaxFreeChunk(chunks, minSplitSize: 500_000);

        Assert.NotNull(selected);
        Assert.Equal(1, selected.Id);
    }

    [Fact]
    public void FindMaxFreeChunk_ReturnsNull_WhenAllChunksAreBelowThreshold()
    {
        var taskId = Guid.NewGuid();
        var chunks = new List<DownloadChunk>
        {
            new() { Id = 0, TaskId = taskId, StartPos = 0, CurrentPos = 900, EndPos = 1000, IsCompleted = false }, // 101 bytes
            new() { Id = 1, TaskId = taskId, StartPos = 1001, CurrentPos = 1001, EndPos = 2000, IsCompleted = true }
        };

        var selected = ChunksManager.FindMaxFreeChunk(chunks, minSplitSize: 500_000);

        Assert.Null(selected);
    }

    [Fact]
    public void SplitChunk_DividesChunkAccurately_WithoutOverlapsOrGaps()
    {
        var taskId = Guid.NewGuid();
        var originalChunk = new DownloadChunk
        {
            Id = 0,
            TaskId = taskId,
            StartPos = 1_000_000,
            CurrentPos = 2_000_000,
            EndPos = 10_000_000,
            TempFilePath = "chunk_0.part",
            IsCompleted = false
        };

        long originalRemaining = originalChunk.EndPos - originalChunk.CurrentPos + 1; // 8,000,001 bytes
        long oldEnd = originalChunk.EndPos;

        var result = ChunksManager.SplitChunk(originalChunk, newChunkId: 1, newTempFilePath: "chunk_1.part", minSplitSize: 100_000);

        Assert.NotNull(result);
        var (modified, newChunk) = result.Value;

        // Verify continuity: modified chunk end + 1 == new chunk start
        Assert.Equal(modified.EndPos + 1, newChunk.StartPos);
        Assert.Equal(oldEnd, newChunk.EndPos);
        Assert.Equal(newChunk.StartPos, newChunk.CurrentPos);
        Assert.Equal(1, newChunk.Id);
        Assert.Equal(taskId, newChunk.TaskId);
        Assert.Equal("chunk_1.part", newChunk.TempFilePath);

        // Sum of both chunks remaining must equal original remaining
        long newTotalRemaining = (modified.EndPos - modified.CurrentPos + 1) + (newChunk.EndPos - newChunk.CurrentPos + 1);
        Assert.Equal(originalRemaining, newTotalRemaining);
    }

    [Fact]
    public void SplitChunk_ReturnsNull_WhenRemainingBytesBelowThreshold()
    {
        var originalChunk = new DownloadChunk
        {
            Id = 0,
            StartPos = 0,
            CurrentPos = 800_000,
            EndPos = 1_000_000, // 200,001 bytes remaining
            IsCompleted = false
        };

        var result = ChunksManager.SplitChunk(originalChunk, 1, "chunk_1.part", minSplitSize: 500_000);

        Assert.Null(result);
    }

    [Fact]
    public void InitializeChunks_PartitionsTotalBytesWithoutGaps()
    {
        var taskId = Guid.NewGuid();
        long totalBytes = 10_000_000;
        int chunkCount = 4;
        string tempDir = Path.Combine(Path.GetTempPath(), "test_chunks_" + Guid.NewGuid().ToString("N"));

        try
        {
            var chunks = ChunksManager.InitializeChunks(taskId, totalBytes, chunkCount, tempDir);

            Assert.Equal(4, chunks.Count);
            Assert.Equal(0, chunks[0].StartPos);
            Assert.Equal(totalBytes - 1, chunks[^1].EndPos);

            for (int i = 0; i < chunks.Count - 1; i++)
            {
                Assert.Equal(chunks[i].EndPos + 1, chunks[i + 1].StartPos);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
