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
    public async Task DownloadTask_ConcurrentChunkModifications_DoNotThrow()
    {
        var task = new DownloadTask { TotalBytes = 10000 };
        for (int i = 0; i < 5; i++)
        {
            task.Chunks.Add(new DownloadChunk { Id = i, StartPos = i * 2000, CurrentPos = i * 2000 + 1000, EndPos = (i + 1) * 2000 - 1, IsActive = true });
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var addRemoveTask = Task.Run(() =>
        {
            int counter = 10;
            while (!cts.IsCancellationRequested)
            {
                lock (task.ChunksLock)
                {
                    task.Chunks.Add(new DownloadChunk { Id = counter++, IsActive = true });
                    if (task.Chunks.Count > 15) task.Chunks.RemoveAt(task.Chunks.Count - 1);
                }
                Thread.Sleep(1);
            }
        });

        var readTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                int completed = task.CompletedChunkCount;
                int active = task.ActiveChunkCount;
                var clone = task.CloneChunks();
                Assert.True(clone.Count >= 0);
                task.RecalculateDownloadedBytes();
                Thread.Sleep(1);
            }
        });

        await Task.WhenAll(addRemoveTask, readTask);
    }

    [Fact]
    public async Task AssembleAsync_Throws_WhenChunksAreIncomplete()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "asm_incomplete_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string p0 = Path.Combine(tempDir, "p0.bin");
            string p1 = Path.Combine(tempDir, "p1.bin");
            await File.WriteAllBytesAsync(p0, new byte[100]);
            await File.WriteAllBytesAsync(p1, new byte[100]);

            var task = new DownloadTask
            {
                TotalBytes = 200,
                DestinationPath = Path.Combine(tempDir, "out.bin"),
                Chunks =
                [
                    new DownloadChunk { Id = 0, StartPos = 0, CurrentPos = 100, EndPos = 99, TempFilePath = p0, IsCompleted = true },
                    new DownloadChunk { Id = 1, StartPos = 100, CurrentPos = 150, EndPos = 199, TempFilePath = p1, IsCompleted = false } // incomplete!
                ]
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => FileAssembler.AssembleAsync(task));
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
