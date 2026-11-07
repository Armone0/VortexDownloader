using System.IO;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Models;
using Xunit;

namespace VortexDownloader.Tests;

public class FileAssemblerTests
{
    [Fact]
    public async Task AssembleAsync_CombinesThreeMockChunks_WithExactByteMatch()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vortex_asm_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Chunk 0: 4096 bytes of 0x11
            string chunk0Path = Path.Combine(tempDir, "chunk_0.part");
            byte[] chunk0Bytes = new byte[4096];
            Array.Fill(chunk0Bytes, (byte)0x11);
            await File.WriteAllBytesAsync(chunk0Path, chunk0Bytes);

            // Chunk 1: 8192 bytes of 0x22
            string chunk1Path = Path.Combine(tempDir, "chunk_1.part");
            byte[] chunk1Bytes = new byte[8192];
            Array.Fill(chunk1Bytes, (byte)0x22);
            await File.WriteAllBytesAsync(chunk1Path, chunk1Bytes);

            // Chunk 2: 2048 bytes of 0x33
            string chunk2Path = Path.Combine(tempDir, "chunk_2.part");
            byte[] chunk2Bytes = new byte[2048];
            Array.Fill(chunk2Bytes, (byte)0x33);
            await File.WriteAllBytesAsync(chunk2Path, chunk2Bytes);

            long totalBytes = chunk0Bytes.Length + chunk1Bytes.Length + chunk2Bytes.Length; // 14336
            string destPath = Path.Combine(tempDir, "final_assembled.dat");

            var taskId = Guid.NewGuid();
            var task = new DownloadTask
            {
                Id = taskId,
                DestinationPath = destPath,
                TotalBytes = totalBytes,
                Chunks =
                [
                    new() { Id = 0, TaskId = taskId, StartPos = 0, CurrentPos = 4096, EndPos = 4095, TempFilePath = chunk0Path, IsCompleted = true },
                    new() { Id = 1, TaskId = taskId, StartPos = 4096, CurrentPos = 12288, EndPos = 12287, TempFilePath = chunk1Path, IsCompleted = true },
                    new() { Id = 2, TaskId = taskId, StartPos = 12288, CurrentPos = 14336, EndPos = 14335, TempFilePath = chunk2Path, IsCompleted = true }
                ]
            };

            await FileAssembler.AssembleAsync(task);

            Assert.True(File.Exists(destPath));
            byte[] assembledBytes = await File.ReadAllBytesAsync(destPath);
            Assert.Equal(totalBytes, assembledBytes.Length);

            // Verify byte precision
            for (int i = 0; i < 4096; i++)
            {
                Assert.Equal(0x11, assembledBytes[i]);
            }
            for (int i = 4096; i < 12288; i++)
            {
                Assert.Equal(0x22, assembledBytes[i]);
            }
            for (int i = 12288; i < 14336; i++)
            {
                Assert.Equal(0x33, assembledBytes[i]);
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

    [Fact]
    public async Task AssembleAsync_ThrowsFileNotFound_WhenChunkIsMissing()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vortex_asm_missing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var task = new DownloadTask
            {
                Id = Guid.NewGuid(),
                DestinationPath = Path.Combine(tempDir, "output.bin"),
                TotalBytes = 1000,
                Chunks =
                [
                    new() { Id = 0, StartPos = 0, CurrentPos = 1000, EndPos = 999, TempFilePath = Path.Combine(tempDir, "non_existent.part"), IsCompleted = true }
                ]
            };

            await Assert.ThrowsAsync<FileNotFoundException>(() => FileAssembler.AssembleAsync(task));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task AssembleAsync_ReportsProgressMonotonically_AndFinishesAt100()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vortex_asm_prog_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string chunk0Path = Path.Combine(tempDir, "chunk_0.part");
            string chunk1Path = Path.Combine(tempDir, "chunk_1.part");
            await File.WriteAllBytesAsync(chunk0Path, new byte[100_000]);
            await File.WriteAllBytesAsync(chunk1Path, new byte[100_000]);

            string destPath = Path.Combine(tempDir, "progress_output.bin");
            var taskId = Guid.NewGuid();
            var task = new DownloadTask
            {
                Id = taskId,
                DestinationPath = destPath,
                TotalBytes = 200_000,
                Chunks =
                [
                    new() { Id = 0, TaskId = taskId, StartPos = 0, CurrentPos = 100_000, EndPos = 99_999, TempFilePath = chunk0Path, IsCompleted = true },
                    new() { Id = 1, TaskId = taskId, StartPos = 100_000, CurrentPos = 200_000, EndPos = 199_999, TempFilePath = chunk1Path, IsCompleted = true }
                ]
            };

            var reports = new List<double>();
            var progress = new Progress<double>(p => reports.Add(p));

            await FileAssembler.AssembleAsync(task, progress);

            Assert.True(File.Exists(destPath));
            // Ensure 100.0 is the final progress reported
            Assert.NotEmpty(reports);
            Assert.Equal(100.0, reports[^1]);
            // Ensure monotonic progression
            for (int i = 1; i < reports.Count; i++)
            {
                Assert.True(reports[i] >= reports[i - 1], $"Progress went backwards: {reports[i]} < {reports[i-1]}");
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
