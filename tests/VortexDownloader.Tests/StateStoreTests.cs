using System.IO;
using VortexDownloader.Core.Models;
using VortexDownloader.Core.Storage;
using Xunit;

namespace VortexDownloader.Tests;

public class StateStoreTests
{
    [Fact]
    public async Task SaveAndLoadTasks_PreservesTaskDetails_AndSetsDownloadingToPaused()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "vortex_tasks_test_" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var store = new StateStore(tempFile);
            var taskId = Guid.NewGuid();

            var tasks = new List<DownloadTask>
            {
                new()
                {
                    Id = taskId,
                    Url = "https://example.com/file.zip",
                    FileName = "file.zip",
                    DestinationPath = @"C:\Downloads\file.zip",
                    TotalBytes = 50_000_000,
                    DownloadedBytes = 25_000_000,
                    Status = DownloadStatus.Downloading, // Should become Paused on load
                    SupportsRange = true,
                    MaxChunks = 8,
                    Chunks =
                    [
                        new()
                        {
                            Id = 0,
                            TaskId = taskId,
                            StartPos = 0,
                            CurrentPos = 25_000_000,
                            EndPos = 25_000_000,
                            TempFilePath = "part_0.bin",
                            IsCompleted = true
                        }
                    ]
                }
            };

            await store.SaveTasksAsync(tasks);
            Assert.True(File.Exists(tempFile));

            var loaded = await store.LoadTasksAsync();
            Assert.Single(loaded);

            var loadedTask = loaded[0];
            Assert.Equal(taskId, loadedTask.Id);
            Assert.Equal("file.zip", loadedTask.FileName);
            Assert.Equal(50_000_000, loadedTask.TotalBytes);
            // On recovery after power loss, downloading tasks are set to Paused
            Assert.Equal(DownloadStatus.Paused, loadedTask.Status);
            Assert.Single(loadedTask.Chunks);
            Assert.Equal(25_000_000, loadedTask.Chunks[0].CurrentPos);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }
}
